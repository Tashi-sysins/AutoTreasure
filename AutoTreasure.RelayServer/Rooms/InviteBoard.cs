using System.Collections.Concurrent;

namespace AutoTreasure.RelayServer.Rooms;

/// <summary>
/// パーティーごとの招待を、短い間だけ預かる掲示板。
///
/// なぜ要るか:
///   これまでは取りまとめ役が招待をコピーし、チャット等で伝え、
///   参加側が貼り付けて押す、という手順が要った。
///
///   同じパーティーに居るなら、その事実だけで十分なはず。
///   ここへ預けておけば、同じパーティーの人が自分で取りに来られる。
///
/// サーバーは「誰のパーティーか」を知らない:
///   鍵はキャラクターIDそのものではなく、そこから作ったハッシュ。
///   同じパーティーの人だけが同じ鍵を作れる。
///   サーバーから見ると意味のない文字列でしかない。
///
/// 預かりは短時間だけ:
///   ルームは一時的なもの。古い招待が残っていると、
///   終わったルームへ繋ごうとして失敗する。
/// </summary>
public sealed class InviteBoard
{
    /// <summary>これを過ぎたら捨てる。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Entry> board = new();

    /// <summary>
    /// 預かっている1件。
    ///
    /// どのルームの物かを覚えておく。
    ///
    /// なぜ要るか（実機で起きた）:
    ///   同じパーティーで作り直すと、鍵は同じまま
    ///   ルームだけが新しくなる。
    ///
    ///   古いルームが猶予切れで終わるとき、鍵だけを見て消すと
    ///   **新しいルームの招待まで消えてしまう**。
    ///   参加側は「まだ準備できていません」のまま繋がらなくなる。
    /// </summary>
    private readonly record struct Entry(string Invite, string RoomCode, DateTime AtUtc);

    /// <summary>いま預かっている数（監視用）。</summary>
    public int Count => this.board.Count;

    /// <summary>取りまとめ役が預ける。</summary>
    public void Publish(string partyKey, string invite, string roomCode)
    {
        if (string.IsNullOrWhiteSpace(partyKey) || string.IsNullOrWhiteSpace(invite))
            return;

        this.board[partyKey] = new Entry(invite, roomCode, DateTime.UtcNow);
    }

    /// <summary>
    /// 参加側が受け取る。
    ///
    /// 無ければ空。古ければ捨てて空を返す。
    /// </summary>
    public string? Take(string partyKey)
    {
        if (string.IsNullOrWhiteSpace(partyKey))
            return null;

        if (!this.board.TryGetValue(partyKey, out var e))
            return null;

        if (DateTime.UtcNow - e.AtUtc > Ttl)
        {
            this.board.TryRemove(partyKey, out _);
            return null;
        }

        return e.Invite;
    }

    /// <summary>
    /// そのルームの預かりを消す。
    ///
    /// **同じ鍵でも、別のルームの物なら消さない。**
    /// 作り直した直後は、古いルームの片付けと
    /// 新しいルームの預かりが同時に起きるため。
    /// </summary>
    public void Withdraw(string partyKey, string roomCode)
    {
        if (string.IsNullOrWhiteSpace(partyKey))
            return;

        if (!this.board.TryGetValue(partyKey, out var e))
            return;

        // いま預かっているのが別のルームの物なら、触らない。
        if (e.RoomCode != roomCode)
            return;

        this.board.TryRemove(partyKey, out _);
    }

    /// <summary>古いものを片付ける。定期的に呼ばれる。</summary>
    public void Sweep(DateTime nowUtc)
    {
        foreach (var (key, e) in this.board.ToArray())
        {
            if (nowUtc - e.AtUtc > Ttl)
                this.board.TryRemove(key, out _);
        }
    }
}
