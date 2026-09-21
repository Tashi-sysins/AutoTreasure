using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AutoTreasure.Sync.Relay;

namespace AutoTreasure.RelayServer.Rooms;

/// <summary>
/// 1つの口へ送る担当。
///
/// なぜ要るか:
///   同じ WebSocket へ複数の処理から同時に送ると、
///   フレームが混ざって壊れる（例外にもなる）。
///
///   一覧の配信と取りまとめ役の命令は同時に起きうるので、
///   **必ず1本の流れにまとめてから**送る。
///
/// 溜まりすぎたときの捨て方:
///   状態(state)は最新だけ意味があるので、古いものは捨ててよい。
///   命令(command)と返事(ack)、ルームの知らせは捨てない。
///   捨てられない物まで詰まったら、その相手は見放す。
/// </summary>
public sealed class MemberSender : IAsyncDisposable
{
    /// <summary>溜めておく上限。</summary>
    private const int Capacity = 200;

    /// <summary>1通の上限。</summary>
    public const int MaxMessageBytes = 64 * 1024;

    private readonly WebSocket socket;
    private readonly Channel<Outgoing> queue;
    private readonly CancellationTokenSource cts = new();
    private readonly Task pump;

    /// <summary>送れなくなったか。上位はこれを見て見放す。</summary>
    public bool Faulted { get; private set; }

    private readonly record struct Outgoing(string Json, bool Droppable);

    public MemberSender(WebSocket socket)
    {
        this.socket = socket;

        this.queue = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        this.pump = Task.Run(this.PumpAsync);
    }

    /// <summary>
    /// 送る。
    ///
    /// <param name="droppable">
    /// 詰まったときに捨ててよいか。状態は true、命令や知らせは false。
    /// </param>
    /// </summary>
    public bool TrySend(RelayEnvelope envelope, bool droppable = false)
    {
        if (this.Faulted)
            return false;

        var json = JsonSerializer.Serialize(envelope);

        if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
            return false;

        return this.queue.Writer.TryWrite(new Outgoing(json, droppable));
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var item in this.queue.Reader.ReadAllAsync(this.cts.Token))
            {
                if (this.socket.State != WebSocketState.Open)
                    break;

                var bytes = Encoding.UTF8.GetBytes(item.Json);

                await this.socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    this.cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 止めたときは異常ではない。
        }
        catch
        {
            // 送れなくなった。相手が切れたのだと扱う。
            this.Faulted = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { this.cts.Cancel(); } catch { }
        this.queue.Writer.TryComplete();

        try { await this.pump.ConfigureAwait(false); } catch { }

        this.cts.Dispose();
    }
}
