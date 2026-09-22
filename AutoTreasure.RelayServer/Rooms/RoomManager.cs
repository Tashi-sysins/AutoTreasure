using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using AutoTreasure.Sync.Relay;

namespace AutoTreasure.RelayServer.Rooms;

/// <summary>作成・参加の結果。</summary>
public sealed record RoomResult(
    bool Ok,
    string? ErrorCode = null,
    RoomState? Room = null,
    MemberSession? Member = null,
    string? JoinToken = null,
    string? ResumeToken = null);

/// <summary>
/// ルームの管理。
///
/// ここが担うのは次だけ。
///   ・誰がどのルームに居るか
///   ・取りまとめ役は誰か
///   ・メッセージをどちらへ流すか
///
/// ゲームの状態は解釈しない。周回の判断もしない。
/// それらはクライアント側にある（薄い中継に徹する）。
/// </summary>
public sealed class RoomManager(InviteBoard invites, ILogger<RoomManager> log)
{
    private readonly ConcurrentDictionary<string, RoomState> rooms = new();

    /// <summary>いまある部屋の数（監視用）。</summary>
    public int RoomCount => this.rooms.Count;

    // ------------------------------------------------------------------
    // 作る
    // ------------------------------------------------------------------

    public async Task<RoomResult> CreateRoomAsync(
        RelayIdentity who, WebSocket socket, string? partyKey = null)
    {
        // 同じパーティーで、取りまとめ役がまだ生きているルームがあるなら、
        // 作り直さずにそちらを返す。
        //
        // なぜ要るか（実機で起きた）:
        //   取りまとめ役が繋ぎ直すたびに新しいルームができ、
        //   そのたび参加側が全員切られて入り直していた。
        //   「4/4 になった直後に再接続中へ落ちる」の原因。
        //
        //   作り直す必要があるのは、前の取りまとめ役が
        //   もう居ないときだけ。
        if (!string.IsNullOrEmpty(partyKey))
        {
            var reused = await this.TryReuseRoomAsync(partyKey, who, socket)
                .ConfigureAwait(false);

            if (reused is not null)
                return reused;

            await this.CloseRoomsOfPartyAsync(partyKey).ConfigureAwait(false);
        }

        var code = RoomCode.NewCode();
        var joinToken = RoomCode.NewToken();
        var resumeToken = RoomCode.NewToken();
        var memberId = Guid.NewGuid().ToString("N");

        var room = new RoomState
        {
            RoomCode = code,
            JoinTokenHash = RoomCode.Hash(joinToken),
            LeaderMemberId = memberId,
            PartyKey = partyKey,
        };

        var leader = new MemberSession
        {
            MemberId = memberId,
            Role = RelayRole.Leader,
            ResumeTokenHash = RoomCode.Hash(resumeToken),
            ClientId = who.ClientId,
            SessionId = who.SessionId,
            Character = who.Character,
            ContentId = who.ContentId,
            Socket = socket,
            Sender = new MemberSender(socket),
        };

        room.Members[memberId] = leader;

        // 合言葉は見た目を無視して引けるようにする。
        if (!this.rooms.TryAdd(RoomCode.Normalize(code), room))
            return new RoomResult(false, RelayError.RoomNotFound);

        log.LogInformation("ルームを作りました {Code}（{Character}）", code, Safe(who.Character));

        await Task.CompletedTask;
        return new RoomResult(true, Room: room, Member: leader,
            JoinToken: joinToken, ResumeToken: resumeToken);
    }

    /// <summary>
    /// 同じパーティーのルームが生きていれば、取りまとめ役だけ差し替えて返す。
    ///
    /// 参加側はそのまま残るので、切れずに済む。
    ///
    /// 差し替えるのは、前の取りまとめ役が切れている場合だけ。
    /// 繋がったままなら、そちらが正。
    /// </summary>
    private async Task<RoomResult?> TryReuseRoomAsync(
        string partyKey, RelayIdentity who, WebSocket socket)
    {
        foreach (var room in this.rooms.Values.ToArray())
        {
            if (room.PartyKey != partyKey)
                continue;

            await room.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!room.Members.TryGetValue(room.LeaderMemberId, out var leader))
                    continue;

                // まだ繋がっているなら、こちらが後から来た方。触らない。
                if (leader.IsConnected)
                    continue;

                // 切れていた取りまとめ役の席へ、新しい接続を入れる。
                if (leader.Sender is { } old)
                    await old.DisposeAsync().ConfigureAwait(false);

                leader.Socket = socket;
                leader.Sender = new MemberSender(socket);
                leader.DisconnectedAtUtc = null;
                leader.LastSeenUtc = DateTime.UtcNow;
                leader.ClientId = who.ClientId;
                leader.SessionId = who.SessionId;
                leader.Character = who.Character;
                leader.ContentId = who.ContentId;

                // プラグイン再読み込みではクライアントに鍵が残っていない。
                // ハッシュから元の鍵は復元できないので、新しい鍵を渡す。
                var joinToken = RoomCode.NewToken();
                var resumeToken = RoomCode.NewToken();
                room.JoinTokenHash = RoomCode.Hash(joinToken);
                leader.ResumeTokenHash = RoomCode.Hash(resumeToken);

                log.LogInformation(
                    "取りまとめ役が戻りました {Code}（{Character}）{Count}/{Max}",
                    room.RoomCode, Safe(who.Character),
                    room.Members.Count, RoomState.MaxMembers);

                return new RoomResult(true, Room: room, Member: leader,
                    JoinToken: joinToken, ResumeToken: resumeToken);
            }
            finally
            {
                room.Gate.Release();
            }
        }

        return null;
    }

    /// <summary>
    /// 同じパーティーの古いルームを畳む。
    ///
    /// 作り直したときに呼ぶ。前のものが残っていると、
    /// 参加側がどちらへ入ればよいか分からなくなる。
    /// </summary>
    private async Task CloseRoomsOfPartyAsync(string partyKey)
    {
        foreach (var (key, room) in this.rooms.ToArray())
        {
            if (room.PartyKey != partyKey)
                continue;

            await room.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var m in room.Members.Values.ToArray())
                {
                    if (m.Sender is { } s)
                        await s.DisposeAsync().ConfigureAwait(false);

                    TryClose(m.Socket);
                }

                room.Members.Clear();
            }
            finally
            {
                room.Gate.Release();
            }

            this.rooms.TryRemove(key, out _);

            log.LogInformation("作り直しのため、前のルームを畳みました {Code}", room.RoomCode);
        }
    }

    // ------------------------------------------------------------------
    // 入る
    // ------------------------------------------------------------------

    public async Task<RoomResult> JoinRoomAsync(
        string? code, string? joinToken, RelayIdentity who, WebSocket socket)
    {
        if (!this.rooms.TryGetValue(RoomCode.Normalize(code), out var room))
            return new RoomResult(false, RelayError.RoomNotFound);

        // 合鍵が違えば断る。合言葉だけでは入れない。
        if (!RoomCode.TokenMatches(joinToken, room.JoinTokenHash))
            return new RoomResult(false, RelayError.InviteInvalid);

        await room.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 外されたばかりなら、入れない。
            //
            // パーティーに居ないから外された。招待はまだ手元にあるので、
            // 止めないとそのまま戻ってきてしまう。
            if (room.IsKicked(who.ContentId, DateTime.UtcNow))
            {
                log.LogInformation(
                    "外した直後なので断りました {Code}（{Character}）",
                    room.RoomCode, Safe(who.Character));

                return new RoomResult(false, RelayError.NotInRoom);
            }

            // 同じキャラクターが既に居るなら、その席を空ける。
            //
            // なぜ要るか（実機で起きた）:
            //   繋ぎ直しで入り直すと、古い席が残ったまま
            //   もう1つ席を取ってしまう。
            //   1人で2席を占め、4人居るのに「4/4 だが誰か足りない」
            //   という状態になっていた。
            //
            //   同じ人が2つ居ることはないので、古いほうを外す。
            if (who.ContentId != 0)
            {
                foreach (var old in room.Members.Values
                             .Where(m => m.ContentId == who.ContentId)
                             .ToArray())
                {
                    if (old.Sender is { } s)
                        await s.DisposeAsync().ConfigureAwait(false);

                    TryClose(old.Socket);
                    room.Members.Remove(old.MemberId);

                    log.LogInformation(
                        "入り直したので、古い席を外しました {Code}（{Character}）",
                        room.RoomCode, Safe(old.Character));
                }
            }

            if (room.Members.Count >= RoomState.MaxMembers)
                return new RoomResult(false, RelayError.RoomFull);

            var resumeToken = RoomCode.NewToken();
            var memberId = Guid.NewGuid().ToString("N");

            var worker = new MemberSession
            {
                MemberId = memberId,
                Role = RelayRole.Worker,
                ResumeTokenHash = RoomCode.Hash(resumeToken),
                ClientId = who.ClientId,
                SessionId = who.SessionId,
                Character = who.Character,
                ContentId = who.ContentId,
                Socket = socket,
                Sender = new MemberSender(socket),
            };

            room.Members[memberId] = worker;
            room.EmptySinceUtc = null;

            log.LogInformation("参加 {Code}（{Character}）{Count}/{Max}",
                room.RoomCode, Safe(who.Character), room.Members.Count, RoomState.MaxMembers);

            return new RoomResult(true, Room: room, Member: worker, ResumeToken: resumeToken);
        }
        finally
        {
            room.Gate.Release();
        }
    }

    // ------------------------------------------------------------------
    // 戻る
    // ------------------------------------------------------------------

    public async Task<RoomResult> ResumeRoomAsync(
        string? code, string? memberId, string? resumeToken, WebSocket socket)
    {
        if (!this.rooms.TryGetValue(RoomCode.Normalize(code), out var room))
            return new RoomResult(false, RelayError.RoomNotFound);

        if (string.IsNullOrEmpty(memberId))
            return new RoomResult(false, RelayError.ResumeInvalid);

        await room.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!room.Members.TryGetValue(memberId, out var member))
                return new RoomResult(false, RelayError.ResumeInvalid);

            if (!RoomCode.TokenMatches(resumeToken, member.ResumeTokenHash))
                return new RoomResult(false, RelayError.ResumeInvalid);

            // 外された相手は、戻るほうの口からも入れない。
            //
            // いまは外すときに席ごと消しているので、ここには来ない。
            // それでも塞いでおく。片方だけ塞いだ作りは、
            // あとで消し方を変えたときに穴になる。
            if (room.IsKicked(member.ContentId, DateTime.UtcNow))
                return new RoomResult(false, RelayError.NotInRoom);

            // 古い口は閉じる。同じ人が2本繋いだままにしない。
            if (member.Sender is { } old)
                await old.DisposeAsync().ConfigureAwait(false);

            TryClose(member.Socket);

            member.Socket = socket;
            member.Sender = new MemberSender(socket);
            member.DisconnectedAtUtc = null;
            member.LastSeenUtc = DateTime.UtcNow;

            room.EmptySinceUtc = null;

            log.LogInformation("復帰 {Code}（{Character}）", room.RoomCode, Safe(member.Character));

            return new RoomResult(true, Room: room, Member: member);
        }
        finally
        {
            room.Gate.Release();
        }
    }

    // ------------------------------------------------------------------
    // 流す
    // ------------------------------------------------------------------

    /// <summary>
    /// 既存の SyncMessage を相手へ流す。
    ///
    /// 向きの決まり:
    ///   **送り主以外の全員**（取りまとめ役・参加側を問わない）
    ///
    /// なぜ MogColle と違うか（転用時に一番効く違い）:
    ///   MogColle は参加側が取りまとめ役とだけ話す形だった。
    ///   AutoTreasure は PipeSync の時点で、リーダーが受けた合図を
    ///   他のメンバーへも中継している（PipeSync.cs:211 の
    ///   QueueSend(line, generation, writer, ...) の第3引数 writer が
    ///   「送り主を除く」の意味）。
    ///
    ///   同じ振る舞いにしないと、メンバー同士の合図が消える。
    ///   StepDone（この段階を終えた）が他のメンバーに見えなくなり、
    ///   足並みが崩れる。
    ///
    /// 中身は読まない。種類と向きだけ見る。
    /// </summary>
    public void Route(RoomState room, MemberSession sender, RelayEnvelope envelope)
    {
        // 送り主を偽れないよう、接続で確定した値で上書きする。
        envelope.MemberId = sender.MemberId;
        envelope.Role = sender.Role;

        // 詰まったときに捨ててよい合図か。
        var droppable = IsStateLike(envelope);

        // ⚠ room.Members.Values の列挙は Gate の中でしか行わない。
        //   ConnectionHandler は既に Route を Gate で囲んでいる。
        foreach (var m in room.Members.Values)
        {
            if (m.MemberId == sender.MemberId)
                continue;                       // 送り主には返さない

            m.Sender?.TrySend(envelope, droppable);
        }
    }

    /// <summary>
    /// 参加側が送ってよい中身か。
    ///
    /// <b>地図役はパーティリーダーとは限らない。</b>
    /// ゲームの仕組み上、古ぼけた地図S5 は誰でも使える。
    /// 使った人がその周回の「地図役」になり、
    /// <b>その人しか宝の場所・魔紋の中の宝箱と扉を知らない</b>。
    ///
    /// ⚠ ここを「リーダーだけ」にすると、地図役がメンバーだったとき
    ///   座標が誰にも届かず、メンバー全員がエーテライトで棒立ちになる。
    ///   実際にそうなった（2026-09-22）。
    ///
    /// そのため、地図役が配る種類（Treasure / VaultChest / VaultDoor /
    /// DoorSide）はメンバーからも通す。
    ///
    /// 逆に<b>全体を動かす号令</b>は通さない。
    ///   Begin        … 開始。押すのはリーダー
    ///   StepGo       … 次へ進め。待ち合わせを抜ける判断はリーダー
    ///   MapTurnSetting … 順番の決め。決めるのはリーダー
    /// ここを緩めると、誰でも他人の周回を動かせてしまう。
    ///
    /// ⚠ 送ってよい種類の一覧は、SyncKind を足すたびに見直す。
    ///   足し忘れると、そのメッセージだけメンバーから送れず、
    ///   原因が分かりにくい。
    /// </summary>
    public static bool IsAllowedFromWorker(RelayEnvelope envelope)
    {
        // ⚠ MogColle は「読めないもの（null）を許可」していたが、ここでは拒否する。
        //   MogColle は封筒の中が JSON で、読めない＝古い版という想定だった。
        //   AutoTreasure は文字列と決まっているので、読めない時点でおかしい。
        var kind = ReadKind(envelope);

        return kind is
            // 誰でも送るもの
            "StepDone" or "Ping" or "MapUser" or "MapUnavailable" or "Abort"
            // 地図役が配るもの（地図役はメンバーのことがある）
            or "Treasure" or "VaultChest" or "VaultDoor" or "DoorSide";
    }

    /// <summary>
    /// 詰まったときに捨ててよい合図か。
    ///
    /// Ping は生存確認なので、詰まったら捨ててよい（PipeSync も同じ扱い）。
    /// それ以外（StepDone / StepGo / Treasure / Begin / Abort など）は
    /// 1回きりの合図なので捨てない。落とすと待ち合わせが永久に成立しない。
    /// </summary>
    private static bool IsStateLike(RelayEnvelope envelope)
        => ReadKind(envelope) == "Ping";

    /// <summary>
    /// 合図の種類（先頭の Kind）だけを取り出す。
    ///
    /// AutoTreasure の合図は「Kind|引数|引数」の1行の文字列。
    /// MogColle のように Payload の中の type プロパティは覗けない。
    ///
    /// ⚠ StartsWith("Ping") にすると PingXxx のような種類を足したとき
    ///   誤爆する。区切りで割って先頭だけを厳密に見る。
    /// </summary>
    private static string? ReadKind(RelayEnvelope envelope)
    {
        if (envelope.Payload is not { } p || p.ValueKind != JsonValueKind.String)
            return null;

        var line = p.GetString();

        if (string.IsNullOrEmpty(line))
            return null;

        var bar = line.IndexOf('|');

        return bar < 0 ? line : line[..bar];
    }

    // ------------------------------------------------------------------
    // 出る・片付ける
    // ------------------------------------------------------------------

    /// <summary>
    /// 取りまとめ役の指示で、ルームから外す。
    ///
    /// パーティーを抜けた人が「再接続中」のまま居座ると、
    /// 新しく入れた人の席が埋まらない。
    ///
    /// 誰を外すかの判断は取りまとめ役が持つ（パーティーを見ている）。
    /// サーバーはゲームの状態を知らないので、言われたとおり外す。
    /// </summary>
    public async Task<bool> KickAsync(RoomState room, ulong contentId)
    {
        if (contentId == 0)
            return false;

        await room.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var targets = room.Members.Values
                .Where(m => m.ContentId == contentId)
                .Where(m => m.MemberId != room.LeaderMemberId)  // 取りまとめ役は外さない
                .ToArray();

            if (targets.Length == 0)
                return false;

            // 戻ってこられないようにする。
            //
            // 外すだけでは足りない。招待をまだ持っているので、
            // 切られた側は数秒後に入り直してしまう。
            room.KickedUntilUtc[contentId] = DateTime.UtcNow + RoomState.KickGrace;

            foreach (var m in targets)
            {
                m.Sender?.TrySend(new RelayEnvelope
                {
                    Type = RelayMessageType.RoomClosed,
                    RoomCode = room.RoomCode,
                    ErrorCode = RelayError.NotInRoom,
                    ErrorMessage = "パーティーから外れたため、ルームを出ました",
                });

                if (m.Sender is { } s)
                    await s.DisposeAsync().ConfigureAwait(false);

                TryClose(m.Socket);
                room.Members.Remove(m.MemberId);

                log.LogInformation("外しました {Code}（{Character}）",
                    room.RoomCode, Safe(m.Character));
            }

            return true;
        }
        finally
        {
            room.Gate.Release();
        }
    }

    /// <summary>
    /// 締め出しを解く。パーティーへ入れ直したときに呼ばれる。
    ///
    /// 外した相手は招待を持ったままなので、しばらく入れないようにしてある。
    /// 入れ直したのなら、その必要はもうない。
    /// </summary>
    public async Task AllowAsync(RoomState room, ulong contentId)
    {
        await room.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (room.Allow(contentId))
                log.LogInformation("締め出しを解きました {Code}", room.RoomCode);
        }
        finally
        {
            room.Gate.Release();
        }
    }

    /// <summary>切れた。すぐには消さず、戻れる猶予を与える。</summary>
    public async Task MarkDisconnectedAsync(RoomState room, MemberSession member)
    {
        await room.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (member.Sender is { } s)
                await s.DisposeAsync().ConfigureAwait(false);

            member.Sender = null;
            member.Socket = null;
            member.DisconnectedAtUtc = DateTime.UtcNow;

            log.LogInformation("切断 {Code}（{Character}）",
                room.RoomCode, Safe(member.Character));
        }
        finally
        {
            room.Gate.Release();
        }
    }

    /// <summary>自分から抜けた。戻る猶予は与えない。</summary>
    public async Task LeaveAsync(RoomState room, MemberSession member)
    {
        await room.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (member.Sender is { } s)
                await s.DisposeAsync().ConfigureAwait(false);

            TryClose(member.Socket);
            room.Members.Remove(member.MemberId);

            if (room.Members.Count == 0)
                room.EmptySinceUtc = DateTime.UtcNow;

            log.LogInformation("退出 {Code}（{Character}）",
                room.RoomCode, Safe(member.Character));
        }
        finally
        {
            room.Gate.Release();
        }
    }

    /// <summary>期限を過ぎたものを片付ける。定期的に呼ばれる。</summary>
    public async Task SweepAsync(DateTime nowUtc)
    {
        foreach (var (key, room) in this.rooms.ToArray())
        {
            await room.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // 戻ってこなかった人を外す。
                foreach (var m in room.Members.Values.ToArray())
                {
                    if (m.DisconnectedAtUtc is not { } since)
                        continue;

                    var grace = m.Role == RelayRole.Leader
                        ? RoomState.LeaderGrace
                        : RoomState.WorkerGrace;

                    if (nowUtc - since < grace)
                        continue;

                    room.Members.Remove(m.MemberId);

                    log.LogInformation("猶予切れ {Code}（{Character}）",
                        room.RoomCode, Safe(m.Character));
                }

                // 取りまとめ役が居なくなったら、この部屋は終わり。
                // 参加側だけ残しても、誰も号令をかけられない。
                if (!room.Members.ContainsKey(room.LeaderMemberId))
                {
                    await this.CloseRoomAsync(key, room, RelayError.LeaderGone)
                        .ConfigureAwait(false);
                    continue;
                }

                if (room.Members.Count == 0)
                {
                    room.EmptySinceUtc ??= nowUtc;

                    if (nowUtc - room.EmptySinceUtc.Value > RoomState.EmptyGrace)
                        this.rooms.TryRemove(key, out _);
                }
            }
            finally
            {
                room.Gate.Release();
            }
        }
    }

    private async Task CloseRoomAsync(string key, RoomState room, string reason)
    {
        foreach (var m in room.Members.Values.ToArray())
        {
            m.Sender?.TrySend(new RelayEnvelope
            {
                Type = RelayMessageType.RoomClosed,
                RoomCode = room.RoomCode,
                ErrorCode = reason,
                ErrorMessage = RelayError.Describe(reason),
            });

            if (m.Sender is { } s)
                await s.DisposeAsync().ConfigureAwait(false);

            TryClose(m.Socket);
        }

        room.Members.Clear();
        this.rooms.TryRemove(key, out _);

        // 預けてあった招待も消す。
        // 残っていると、終わったルームへ繋ごうとして失敗する。
        if (room.PartyKey is { } pk)
            invites.Withdraw(pk, room.RoomCode);

        log.LogInformation("ルームを終了しました {Code}（{Reason}）", room.RoomCode, reason);
    }

    /// <summary>ルームの全員へ、いま誰が居るかを配る。</summary>
    public static void BroadcastMembers(RoomState room)
    {
        var snapshot = room.ToSnapshot();

        var envelope = new RelayEnvelope
        {
            Type = RelayMessageType.MemberList,
            RoomCode = room.RoomCode,
            Payload = JsonSerializer.SerializeToElement(snapshot),
        };

        foreach (var m in room.Members.Values)
            m.Sender?.TrySend(envelope);
    }

    private static void TryClose(WebSocket? socket)
    {
        if (socket is null)
            return;

        try { socket.Abort(); } catch { }
        try { socket.Dispose(); } catch { }
    }

    /// <summary>ログに個人が特定できる値をそのまま出さない。</summary>
    private static string Safe(string? name)
        => string.IsNullOrEmpty(name) ? "(不明)" : name;
}
