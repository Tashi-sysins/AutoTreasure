using ECommons.DalamudServices;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTreasure.Sync;

internal enum SyncMode { Stopped, Leader, Member }

/// <summary>
/// 同一PCの名前付きパイプ。接続の公開と世代判定を同じロックで行い、
/// 送信・中継は同じ列を通す。ゲームのスレッドで通信の完了を待たない。
/// </summary>
internal sealed class PipeSync : IDisposable
{
    private readonly string _pipeName;
    private readonly Lock _stateLock = new();
    private readonly Lock _sendLock = new();
    private readonly ConcurrentQueue<SyncMessage> _received = new();
    private readonly List<StreamWriter> _clients = [];
    private StreamWriter? _toLeader;
    private CancellationTokenSource? _cts;
    private int _generation;
    private Task _sendChain = Task.CompletedTask;
    private int _queuedSends;
    private const int MaxQueuedSends = 8;
    private volatile SyncMode _mode;
    private volatile string _statusText = "停止中";

    internal SyncMode Mode => _mode;
    internal string StatusText => _statusText;
    internal int ConnectedClients { get { lock (_stateLock) return _clients.Count; } }
    internal bool IsConnected
    {
        get { lock (_stateLock) return _toLeader != null || _clients.Count > 0; }
    }

    internal PipeSync(string pipeName)
        => _pipeName = string.IsNullOrWhiteSpace(pipeName) ? "AutoTreasurePipe" : pipeName;

    internal void StartAsLeader() => Start(SyncMode.Leader);
    internal void StartAsMember() => Start(SyncMode.Member);

    private void Start(SyncMode mode)
    {
        Stop();
        lock (_stateLock)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            var generation = _generation;
            // 可変フィールド _cts を Task 内から読まない。
            var token = cts.Token;
            _mode = mode;
            _statusText = mode == SyncMode.Leader ? "待ち受け中" : "接続中";
            _ = Task.Run(async () =>
            {
                try
                {
                    if (mode == SyncMode.Leader)
                        await LeaderLoop(token, generation).ConfigureAwait(false);
                    else
                        await MemberLoop(token, generation).ConfigureAwait(false);
                }
                finally
                {
                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_cts, cts)) _cts = null;
                        cts.Dispose();
                    }
                }
            });
        }
    }

    internal void Stop()
    {
        List<StreamWriter> old;
        lock (_stateLock)
        {
            _generation++;
            _cts?.Cancel();
            _cts = null;
            old = [.. _clients];
            _clients.Clear();
            if (_toLeader != null) old.Add(_toLeader);
            _toLeader = null;
            _mode = SyncMode.Stopped;
            _statusText = "停止中";
            while (_received.TryDequeue(out _)) { }
        }
        // Writer.Dispose は未送信バッファを同期 Flush する。まずパイプを閉じて
        // 詰まった書き込み・読み込みを中断し、Writer の後始末は所有ループに任せる。
        foreach (var writer in old) ClosePipe(writer);
        // 旧送信は世代判定で捨て、件数は各送信の finally だけで減らす。
        // ここで0へ戻すと旧送信の完了により負数になる。
    }

    internal void Send(SyncMessage message)
    {
        int generation;
        lock (_stateLock)
        {
            if (_mode == SyncMode.Stopped) return;
            generation = _generation;
        }
        QueueSend(message.Serialize(), generation, null, message.Kind == SyncKind.Ping);
    }

    private void QueueSend(string line, int generation, StreamWriter? origin, bool droppable)
    {
        lock (_sendLock)
        {
            if (droppable && _queuedSends >= MaxQueuedSends) return;
            _queuedSends++;
            _sendChain = _sendChain.ContinueWith(async _ =>
            {
                try { await SendCore(line, generation, origin).ConfigureAwait(false); }
                finally { lock (_sendLock) _queuedSends--; }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task SendCore(string line, int generation, StreamWriter? origin)
    {
        List<StreamWriter> targets;
        lock (_stateLock)
        {
            if (generation != _generation) return;
            targets = _toLeader != null ? [_toLeader] : [.. _clients];
        }
        foreach (var writer in targets)
        {
            if (ReferenceEquals(writer, origin)) continue;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await writer.WriteLineAsync(line.AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                ClosePipe(writer);
                lock (_stateLock)
                {
                    if (generation != _generation) continue;
                    _clients.Remove(writer);
                    if (ReferenceEquals(_toLeader, writer)) _toLeader = null;
                    UpdateStatus();
                }
            }
        }
    }

    internal bool TryReceive(out SyncMessage message) => _received.TryDequeue(out message);

    private async Task LeaderLoop(CancellationToken token, int generation)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                var accepted = pipe;
                pipe = null;
                // HandleClient が必ず所有し、キャンセル済みでも閉じる。
                _ = HandleClient(accepted, token, generation);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Svc.Log.Debug(ex, "接続の待ち受けを再試行します。");
                await DelaySafe(token).ConfigureAwait(false);
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task HandleClient(NamedPipeServerStream pipe, CancellationToken token, int generation)
    {
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            lock (_stateLock)
            {
                if (generation != _generation) return;
                _clients.Add(writer);
                UpdateStatus();
            }
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line == null) break;
                if (!SyncMessage.TryParse(line, out var message)) continue;
                lock (_stateLock)
                {
                    if (generation != _generation) break;
                    _received.Enqueue(message);
                }
                // 直接 WriteLine しない。リーダー自身の送信と他の中継を直列化する。
                QueueSend(line, generation, writer, message.Kind == SyncKind.Ping);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        catch (Exception ex) { Svc.Log.Debug(ex, "メンバーとの接続が切れました。"); }
        finally
        {
            ClosePipe(writer);
            try { writer.Dispose(); } catch { }
            lock (_stateLock)
            {
                _clients.Remove(writer);
                if (generation == _generation) UpdateStatus();
            }
        }
    }

    private async Task MemberLoop(CancellationToken token, int generation)
    {
        while (!token.IsCancellationRequested)
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            StreamWriter? writer = null;
            try
            {
                await pipe.ConnectAsync(3000, token).ConfigureAwait(false);
                writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                lock (_stateLock)
                {
                    if (generation != _generation) break;
                    _toLeader = writer;
                    UpdateStatus();
                }
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    if (line == null) break;
                    if (!SyncMessage.TryParse(line, out var message)) continue;
                    lock (_stateLock)
                    {
                        if (generation != _generation) break;
                        _received.Enqueue(message);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Svc.Log.Debug(ex, "リーダーへの接続を再試行します。"); }
            finally
            {
                pipe.Dispose();
                try { writer?.Dispose(); } catch { }
                lock (_stateLock)
                {
                    // このループが開いた接続だけを片付ける。
                    if (ReferenceEquals(_toLeader, writer)) _toLeader = null;
                    if (generation == _generation) UpdateStatus();
                }
            }
            await DelaySafe(token).ConfigureAwait(false);
        }
    }

    // _stateLock 内から呼ぶ。
    private void UpdateStatus()
        => _statusText = _mode switch
        {
            SyncMode.Leader => _clients.Count > 0 ? $"接続中（{_clients.Count} 台）" : "待ち受け中",
            SyncMode.Member => _toLeader != null ? "接続済み" : "再接続を待っています",
            _ => "停止中",
        };

    private static void ClosePipe(StreamWriter writer)
    {
        try { writer.BaseStream.Dispose(); } catch { }
    }

    private static async Task DelaySafe(CancellationToken token)
    {
        try { await Task.Delay(500, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => Stop();
}
