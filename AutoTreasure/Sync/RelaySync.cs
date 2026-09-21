using AutoTreasure.Sync.Relay;
using ECommons.DalamudServices;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace AutoTreasure.Sync;

/// <summary>
/// インターネット越しの連携。中継サーバーを経由する。
///
/// <see cref="PipeSync"/> と同じ形（<see cref="ISyncTransport"/>）にしてあるので、
/// RunController から見ると違いが無い。
///
/// <b>やり取りする中身はパイプとまったく同じ。</b>
/// SyncMessage.Serialize() の文字列をそのまま封筒へ入れて運ぶ。
/// 形を変えないので「パイプでは動くのに中継では動かない」が起きにくい。
/// </summary>
internal sealed class RelaySync : ISyncTransport
{
    private readonly string _url;

    /// <summary>このクライアント1つを表す。同じPCで8つ起動しても衝突しない。</summary>
    private readonly string _clientId;

    /// <summary>起動ごとに変える。繋ぎ直しを見分けるため。</summary>
    private readonly string _sessionId;

    private readonly ConcurrentQueue<SyncMessage> _received = new();
    private readonly Lock _stateLock = new();

    private RelayCoordinator? _relay;
    private volatile SyncMode _mode;

    internal RelaySync(string url)
    {
        _url = (url ?? string.Empty).Trim();

        var machine = SafeMachineName();

        _clientId = $"{machine}-{Environment.ProcessId}";
        _sessionId = Guid.NewGuid().ToString("N")[..8];
    }

    public SyncMode Mode => _mode;

    public string StatusText
    {
        get
        {
            if (_mode == SyncMode.Stopped)
                return "停止中";

            if (string.IsNullOrWhiteSpace(_url))
                return "中継サーバーのURLが設定されていません";

            RelayCoordinator? relay;
            lock (_stateLock) relay = _relay;

            if (relay is null)
                return "停止中";

            // 何人そろったかを添える。待っている側が状況を判断できるようにする。
            var room = relay.Room;

            if (room is null)
                return relay.Status;

            var connected = room.Members.Count(m => m.Status == MemberStatus.Connected);

            return $"{relay.Status}（{connected} 人）";
        }
    }

    /// <summary>
    /// いま繋がっている台数（自分を除く）。
    ///
    /// <b>「再接続中」は数えない。</b>
    /// 席は残っていても合図は届かないので、
    /// 数えると届かない相手を待ち続けることになる。
    /// </summary>
    public int ConnectedClients
    {
        get
        {
            RelayCoordinator? relay;
            lock (_stateLock) relay = _relay;

            var room = relay?.Room;

            if (room is null)
                return 0;

            return room.Members.Count(m =>
                m.Status == MemberStatus.Connected && m.MemberId != room.MemberId);
        }
    }

    public bool IsConnected
    {
        get
        {
            RelayCoordinator? relay;
            lock (_stateLock) relay = _relay;

            return relay?.Connected == true;
        }
    }

    public void StartAsLeader() => Start(SyncMode.Leader);

    public void StartAsMember() => Start(SyncMode.Member);

    private void Start(SyncMode mode)
    {
        Stop();

        // URL が無いまま繋ごうとしても無駄に再試行を繰り返すだけ。
        // 状態を出して止めておく（StatusText が理由を伝える）。
        if (string.IsNullOrWhiteSpace(_url))
        {
            _mode = mode;
            return;
        }

        lock (_stateLock)
        {
            _mode = mode;

            _relay = new RelayCoordinator(
                _url,
                asLeader: mode == SyncMode.Leader,
                clientId: _clientId,
                sessionId: _sessionId,
                describe: Describe,
                partyKey: PartyKey.Current);
        }
    }

    public void Stop()
    {
        RelayCoordinator? old;

        lock (_stateLock)
        {
            old = _relay;
            _relay = null;
            _mode = SyncMode.Stopped;

            while (_received.TryDequeue(out _)) { }
        }

        old?.Dispose();
    }

    public void Send(SyncMessage message)
    {
        RelayCoordinator? relay;
        lock (_stateLock) relay = _relay;

        // ⚠ SyncMessage.Serialize() は書き換えない。
        //   既存の形をそのまま封筒に入れるだけにする。
        relay?.Send(message.Serialize());
    }

    public bool TryReceive(out SyncMessage message)
    {
        // 先に、中継から届いた文字列をいつもの形へ戻しておく。
        Drain();

        return _received.TryDequeue(out message);
    }

    /// <summary>
    /// 中継から届いた文字列を SyncMessage へ戻す。
    ///
    /// 読み取り（TryParse）はパイプと同じものを使う。
    /// 別に書くと、片方だけ直したときに食い違う。
    /// </summary>
    private void Drain()
    {
        RelayCoordinator? relay;
        lock (_stateLock) relay = _relay;

        if (relay is null)
            return;

        while (relay.TryReceive(out var line))
        {
            if (SyncMessage.TryParse(line, out var message))
                _received.Enqueue(message);
        }
    }

    /// <summary>
    /// 自分のキャラクターIDと名前。名乗りに載せる。
    ///
    /// ログイン前は (0, "") でよい。
    /// エリア移動中に 0 になることがあるが、それで困らない作りにしてある
    /// （サーバーは身元をこの接続のものとして扱う）。
    /// </summary>
    private static (ulong, string) Describe()
    {
        try
        {
            return (Svc.PlayerState.ContentId, Helpers.PlayerHelper.Name);
        }
        catch
        {
            return (0UL, string.Empty);
        }
    }

    private static string SafeMachineName()
    {
        try
        {
            var name = Environment.MachineName;
            return string.IsNullOrWhiteSpace(name) ? "PC" : name;
        }
        catch
        {
            return "PC";
        }
    }

    public void Dispose() => Stop();
}
