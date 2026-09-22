using System.Net.WebSockets;
using AutoTreasure.Sync.Relay;

namespace AutoTreasure.RelayServer.Rooms;

/// <summary>ルームに居る1人。</summary>
public sealed class MemberSession
{
    public required string MemberId { get; init; }

    /// <summary>取りまとめ役か参加側か。**サーバーが決めた値が正**。</summary>
    public required string Role { get; init; }

    /// <summary>
    /// 戻るための合鍵のハッシュ。
    ///
    /// 合鍵そのものは持たない。持つと、サーバーが覗かれたとき
    /// 全員の合鍵が漏れる。
    /// </summary>
    public required byte[] ResumeTokenHash { get; set; }

    public string ClientId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Character { get; set; } = string.Empty;
    public ulong ContentId { get; set; }

    /// <summary>いま繋がっている口。切れていれば null。</summary>
    public WebSocket? Socket { get; set; }

    /// <summary>この口へ送る担当。1本だけにして、同時送信による壊れを防ぐ。</summary>
    public MemberSender? Sender { get; set; }

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>切れた時刻。戻ってきたら null に戻す。</summary>
    public DateTime? DisconnectedAtUtc { get; set; }

    public bool IsConnected => this.Socket is { State: WebSocketState.Open };

    public string Status => this.IsConnected
        ? MemberStatus.Connected
        : MemberStatus.Reconnecting;

    public RelayMember ToDto() => new()
    {
        MemberId = this.MemberId,
        Role = this.Role,
        Character = this.Character,
        ContentId = this.ContentId,
        Status = this.Status,
    };
}

/// <summary>
/// ひと部屋。
///
/// AEAssist の自鯖では RoomId が一致するセッションの集合として
/// 都度算出していた（実体テーブルを持たない）。
/// こちらは**権限・人数制限・期限・再接続**が要るので、
/// 明示的な入れ物を持つ。
/// </summary>
public sealed class RoomState
{
    /// <summary>
    /// パーティは最大8人。
    ///
    /// MogColle はアルテマ周回が4人固定だったので 4 だった。
    /// AutoTreasure は待つ人数を**パーティの実人数から自動で決める**
    /// （PartyRoleDetector.ResolveExpectedMembers）ので、
    /// サーバーは「8人まで入れる箱」を用意するだけでよい。
    ///
    /// ⚠ 8人で実際に足並みが揃うかは未検証。
    ///   MogColle は4人でしか試していない。
    /// </summary>
    public const int MaxMembers = 8;

    /// <summary>取りまとめ役が戻ってくるのを待つ時間。</summary>
    public static readonly TimeSpan LeaderGrace = TimeSpan.FromSeconds(30);

    /// <summary>参加側が戻ってくるのを待つ時間。</summary>
    public static readonly TimeSpan WorkerGrace = TimeSpan.FromSeconds(30);

    /// <summary>誰も居なくなってから片付けるまで。</summary>
    public static readonly TimeSpan EmptyGrace = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 外した相手を締め出しておく時間。
    ///
    /// 外しただけでは戻ってくる。招待（合言葉と合鍵）を
    /// まだ持っているので、切られた側は繋ぎ直して入り直す。
    /// 数秒で席に戻り、外した意味がなくなる。
    ///
    /// かといって永久に締め出すと、パーティーへ入れ直したときに
    /// 二度と参加できない。
    ///
    /// 繋ぎ直しの間隔（数秒〜十数秒）より十分長く、
    /// 人が入れ直す操作より十分短い長さにする。
    /// </summary>
    public static readonly TimeSpan KickGrace = TimeSpan.FromSeconds(60);

    public required string RoomCode { get; init; }

    /// <summary>参加に要る合鍵のハッシュ。</summary>
    public required byte[] JoinTokenHash { get; set; }

    public required string LeaderMemberId { get; init; }

    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>
    /// このルームへの出入りと送信を直列化する。
    ///
    /// ConcurrentDictionary だけでは「人数を数えて、空いていれば入れる」
    /// のような**複数段の操作**が原子的にならない。
    /// </summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public Dictionary<string, MemberSession> Members { get; } = [];

    public DateTime? EmptySinceUtc { get; set; }

    /// <summary>
    /// 外した相手と、外した時刻。
    ///
    /// キーはキャラクターID。KickGrace の間は入れない。
    /// 取りまとめ役がもう一度パーティーへ入れたら、
    /// そちらが Allow を呼んで解除する。
    /// </summary>
    public Dictionary<ulong, DateTime> KickedUntilUtc { get; } = [];

    /// <summary>締め出しを解く。パーティーへ入れ直したときに呼ばれる。</summary>
    public bool Allow(ulong contentId)
        => contentId != 0 && this.KickedUntilUtc.Remove(contentId);

    /// <summary>いま締め出し中か。期限切れはその場で消す。</summary>
    public bool IsKicked(ulong contentId, DateTime nowUtc)
    {
        if (contentId == 0)
            return false;

        if (!this.KickedUntilUtc.TryGetValue(contentId, out var until))
            return false;

        if (nowUtc < until)
            return true;

        this.KickedUntilUtc.Remove(contentId);
        return false;
    }

    /// <summary>
    /// このルームの招待を預けた鍵。
    ///
    /// ルームが終わったら、預かりも消すために覚えておく。
    /// 残っていると、終わったルームへ繋ごうとして失敗する。
    /// </summary>
    public string? PartyKey { get; set; }

    /// <summary>作ったときに分かっていれば、そのまま持つ。</summary>

    public RelayRoomSnapshot ToSnapshot() => new()
    {
        RoomCode = this.RoomCode,
        Members = [.. this.Members.Values.Select(m => m.ToDto())],
    };

    public MemberSession? Leader
        => this.Members.TryGetValue(this.LeaderMemberId, out var m) ? m : null;

    public IEnumerable<MemberSession> Workers
        => this.Members.Values.Where(m => m.Role == RelayRole.Worker);
}
