using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTreasure.Sync.Relay;

/// <summary>
/// ルームの様子（画面表示用）。
///
/// 受信は裏のタスクで動くので、画面が触る値を直接書き換えない。
/// 作り直した塊をまるごと差し替える。
/// </summary>
internal sealed record RelayRoomInfo(
    string RoomCode,
    string Invite,
    string MemberId,
    string Role,
    IReadOnlyList<RelayMember> Members);

/// <summary>
/// 中継サーバー経由でつなぐ。
///
/// なぜ要るか:
///   いまの名前付きパイプは、同じPCの中でしか通じない。
///   別々の家・別々の回線からでは繋がらない。
///
///   全員が**同じサーバーへ外向きに**繋げば、
///   家庭のルーターは何もしなくてよい。
///
/// ここが持つのは「繋ぐ・戻る・招待を尋ねる」まで。
/// 合図（SyncMessage）の意味は解釈しない。
/// 文字列のまま封筒に入れて運ぶだけ。
///
/// MogColle の同名クラスからの移植。
/// TCP 版と共通だった部分（CoordinatorBase）は持ち込まず、
/// 中継に要るものだけを写してある。
/// </summary>
internal sealed class RelayCoordinator : IDisposable
{
    /// <summary>1通の上限。壊れた相手に大量に送られても耐えるため。</summary>
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>溜めておく上限。読み出しが止まっても無限に太らせない。</summary>
    private const int MaxQueued = 500;

    private readonly string url;
    private readonly bool asLeader;
    private readonly string clientId;
    private readonly string sessionId;
    private readonly Func<(ulong ContentId, string Character)>? describe;
    private readonly Func<string>? partyKey;

    private readonly ConcurrentQueue<string> inbox = new();
    private readonly CancellationTokenSource cts = new();

    /// <summary>
    /// サーバーに断られた内容。記録に残すために溜める。
    ///
    /// <b>これが無いと、断られたことに気づけない。</b>
    /// 実際、地図役がメンバーのとき座標が ROLE_FORBIDDEN で
    /// 弾かれていたが、画面の状態がすぐ上書きされるため
    /// 「なぜか全員が動かない」としか見えなかった（2026-09-22）。
    /// </summary>
    private readonly ConcurrentQueue<string> problems = new();

    /// <summary>送る担当。同じ口への同時送信はフレームが壊れるので1本にまとめる。</summary>
    private readonly SemaphoreSlim sendGate = new(1, 1);

    /// <summary>
    /// 参加側が入るときに使う招待。取りまとめ役なら空。
    ///
    /// 設定で渡されたものを起点にし、
    /// サーバーから受け取れたらそちらへ差し替える。
    /// </summary>
    private string currentInvite;

    private ClientWebSocket? socket;
    private volatile string status = "未接続";

    /// <summary>戻るための合鍵。切れたとき同じ人として戻るのに使う。</summary>
    private string? resumeToken;
    private string? memberId;
    private string? roomCode;
    private string? joinToken;
    private long nextInviteRefresh;

    internal RelayCoordinator(
        string url,
        bool asLeader,
        string clientId,
        string sessionId,
        string invite = "",
        Func<(ulong, string)>? describe = null,
        Func<string>? partyKey = null)
    {
        this.url = url;
        this.asLeader = asLeader;
        this.clientId = clientId;
        this.sessionId = sessionId;
        this.currentInvite = invite;
        this.describe = describe;
        this.partyKey = partyKey;

        _ = Task.Run(() => this.RunAsync(this.cts.Token));
    }

    internal bool Connected => this.socket is { State: WebSocketState.Open }
                               && this.memberId is not null;

    internal string Status => this.status;

    /// <summary>いまのルーム。入っていなければ null。</summary>
    internal RelayRoomInfo? Room { get; private set; }

    /// <summary>招待文字列（取りまとめ役が配る）。</summary>
    internal string Invite
        => this.roomCode is null || this.joinToken is null
            ? string.Empty
            : RelayInvite.Build(this.roomCode, this.joinToken);

    /// <summary>
    /// 合図を1通送る。
    ///
    /// SyncMessage は「Kind|引数|引数」の文字列。
    /// そのまま封筒へ入れる（作り替えない）。
    /// </summary>
    internal void Send(string line)
    {
        if (!this.Connected || string.IsNullOrEmpty(line))
            return;

        // AutoTreasure は停止中も定期的に生存通知を送る。その送信で招待の期限も延長する。
        if (this.asLeader && Environment.TickCount64 >= this.nextInviteRefresh)
            this.PublishInvite();

        _ = this.SendEnvelopeAsync(new RelayEnvelope
        {
            Type = RelayMessageType.Relay,
            RoomCode = this.roomCode,
            MemberId = this.memberId,
            Payload = JsonSerializer.SerializeToElement(line),
        });
    }

    /// <summary>受け取った合図を1つ取り出す。</summary>
    internal bool TryReceive(out string line) => this.inbox.TryDequeue(out line!);

    /// <summary>
    /// サーバーに断られた内容を1つ取り出す。
    ///
    /// 記録に残すためのもの。動きには使わない。
    /// </summary>
    internal bool TryTakeProblem(out string problem)
        => this.problems.TryDequeue(out problem!);

    /// <summary>
    /// ルームから誰かを外すよう、サーバーへ頼む。取りまとめ役だけができる。
    ///
    /// なぜ要るか（MogColle の実機で起きた）:
    ///   パーティーを抜けた人が「再接続中」のまま居座り、
    ///   新しく入れた人の席が埋まらなかった。
    ///
    ///   中継では接続をサーバーが握っているので、
    ///   取りまとめ役から頼まないと外せない。
    /// </summary>
    internal void KickByContentId(ulong contentId)
    {
        if (!this.asLeader || contentId == 0 || !this.Connected)
            return;

        _ = this.SendEnvelopeAsync(new RelayEnvelope
        {
            Type = RelayMessageType.KickMember,
            RoomCode = this.roomCode,
            MemberId = this.memberId,
            Payload = JsonSerializer.SerializeToElement(new KickPayload { ContentId = contentId }),
        });
    }

    /// <summary>
    /// 外した相手の締め出しを解くよう、サーバーへ頼む。
    ///
    /// パーティーへ入れ直したときに呼ぶ。
    /// これを呼ばないと、締め出しが切れるまで参加できない。
    /// </summary>
    internal void AllowByContentId(ulong contentId)
    {
        if (!this.asLeader || contentId == 0 || !this.Connected)
            return;

        _ = this.SendEnvelopeAsync(new RelayEnvelope
        {
            Type = RelayMessageType.AllowMember,
            RoomCode = this.roomCode,
            MemberId = this.memberId,
            Payload = JsonSerializer.SerializeToElement(new KickPayload { ContentId = contentId }),
        });
    }

    // ------------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await this.ConnectOnceAsync(ct).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                this.status = $"接続できません: {Short(ex.Message)}";
            }

            // 間を空けて繋ぎ直す。総当たりにしない。
            var wait = attempt switch
            {
                0 => 1,
                1 => 2,
                2 => 5,
                _ => 10,
            };

            attempt++;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        // 参加側で招待を持っていないなら、まず尋ねる。
        //
        // 同じパーティーに居れば、取りまとめ役が預けたものを受け取れる。
        // これで、招待をコピーして渡してもらう必要が無くなる。
        if (!this.asLeader && string.IsNullOrWhiteSpace(this.currentInvite))
        {
            await this.AskInviteAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(this.currentInvite))
            {
                return;
            }
        }

        using var ws = new ClientWebSocket();
        this.socket = ws;
        this.status = "接続中…";

        await ws.ConnectAsync(new Uri(this.url), ct).ConfigureAwait(false);

        // 最初の1通で、作る／入る／戻る を済ませる。
        await this.SendRawAsync(ws, this.BuildEntry(), ct).ConfigureAwait(false);

        this.status = "ルームへの参加を待っています";

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var msg = await ReadAsync(ws, ct).ConfigureAwait(false);

            if (msg is null)
                break;

            this.Handle(msg);
        }

        this.status = "切断されました";
        this.Room = null;
    }

    private RelayEnvelope BuildEntry()
    {
        var who = this.describe?.Invoke() ?? (0UL, string.Empty);

        var identity = new RelayIdentity
        {
            ClientId = this.clientId,
            SessionId = this.sessionId,
            ContentId = who.Item1,
            Character = who.Item2,
        };

        // 一度入ったことがあるなら、同じ人として戻る。
        if (this.memberId is not null && this.resumeToken is not null)
        {
            return new RelayEnvelope
            {
                Type = RelayMessageType.ResumeRoom,
                RoomCode = this.roomCode,
                MemberId = this.memberId,
                ResumeToken = this.resumeToken,
                Payload = JsonSerializer.SerializeToElement(identity),
            };
        }

        if (this.asLeader)
        {
            // どのパーティーのルームかを伝える。
            //
            // サーバーは、同じパーティーで作り直したときに
            // 前のルームを畳む。残っていると参加側が迷う。
            var key = this.partyKey?.Invoke();

            if (!string.IsNullOrEmpty(key))
            {
                return new RelayEnvelope
                {
                    Type = RelayMessageType.CreateRoom,
                    Payload = JsonSerializer.SerializeToElement(new
                    {
                        clientId = identity.ClientId,
                        sessionId = identity.SessionId,
                        character = identity.Character,
                        contentId = identity.ContentId,
                        partyKey = key,
                    }),
                };
            }

            return new RelayEnvelope
            {
                Type = RelayMessageType.CreateRoom,
                Payload = JsonSerializer.SerializeToElement(identity),
            };
        }

        RelayInvite.TryParse(this.currentInvite, out var code, out var token);

        return new RelayEnvelope
        {
            Type = RelayMessageType.JoinRoom,
            RoomCode = code,
            JoinToken = token,
            Payload = JsonSerializer.SerializeToElement(identity),
        };
    }

    private void Handle(RelayEnvelope msg)
    {
        switch (msg.Type)
        {
            case RelayMessageType.RoomCreated:
            case RelayMessageType.RoomJoined:
                this.memberId = msg.MemberId;
                this.roomCode = msg.RoomCode;
                this.resumeToken = msg.ResumeToken ?? this.resumeToken;
                this.joinToken = msg.JoinToken ?? this.joinToken;

                this.status = msg.Type == RelayMessageType.RoomCreated
                    ? $"ルームを作りました（{msg.RoomCode}）"
                    : $"ルームに参加しました（{msg.RoomCode}）";

                // 取りまとめ役は、招待をサーバーへ預ける。
                // 同じパーティーの人が自分で取りに来られるようにするため。
                if (this.asLeader)
                    this.PublishInvite();

                break;

            case RelayMessageType.MemberList:
                this.UpdateRoom(msg);
                break;

            case RelayMessageType.Relay:
                this.Forward(msg);
                break;

            case RelayMessageType.Ping:
                _ = this.SendEnvelopeAsync(new RelayEnvelope { Type = RelayMessageType.Pong });
                break;

            case RelayMessageType.RoomClosed:
                this.status = RelayError.Describe(msg.ErrorCode);
                this.Room = null;
                break;

            case RelayMessageType.Error:
                this.status = RelayError.Describe(msg.ErrorCode);

                // 断られたことを記録に残す。
                //
                // 画面の状態はすぐ次の表示で上書きされるので、
                // それだけでは原因を追えない。
                if (this.problems.Count < 50)
                {
                    this.problems.Enqueue(
                        $"中継サーバーに断られました（{msg.ErrorCode}）"
                        + RelayError.Describe(msg.ErrorCode));
                }

                // 戻れなかったら、覚えていることを捨てて入り直す。
                //
                // なぜ要るか（MogColle の実機で起きた）:
                //   取りまとめ役が作り直すと、前のルームは消える。
                //   そこへ「同じ人として戻ります」と言っても通らない。
                //
                //   捨てずにいると、同じ失敗を延々と繰り返す。
                //   実際、参加側が「再接続中」のまま戻らなかった。
                if (msg.ErrorCode is RelayError.ResumeInvalid or RelayError.RoomNotFound or RelayError.InviteInvalid)
                {
                    this.memberId = null;
                    this.resumeToken = null;
                    this.roomCode = null;

                    // 招待も古いかもしれない。尋ね直す。
                    if (!this.asLeader)
                        this.currentInvite = string.Empty;

                    this.status = "入り直します";
                }

                break;
        }
    }

    /// <summary>
    /// 自分のパーティーの招待を、サーバーへ尋ねる。
    ///
    /// 短い接続を1本だけ使い、答えをもらったら閉じる。
    /// 受け取れたら、次の接続でそれを使って入る。
    /// </summary>
    private async Task AskInviteAsync(CancellationToken ct)
    {
        var key = this.partyKey?.Invoke();

        if (string.IsNullOrEmpty(key))
        {
            this.status = "パーティーを確認できません";
            return;
        }

        try
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(this.url), ct).ConfigureAwait(false);

            await this.SendRawAsync(ws, new RelayEnvelope
            {
                Type = RelayMessageType.RequestInvite,
                Payload = JsonSerializer.SerializeToElement(new InvitePayload { PartyKey = key }),
            }, ct).ConfigureAwait(false);

            var reply = await ReadAsync(ws, ct).ConfigureAwait(false);

            if (reply?.Type == RelayMessageType.InviteAnswer && reply.Payload is { } p)
            {
                var payload = p.Deserialize<InvitePayload>();

                if (!string.IsNullOrWhiteSpace(payload?.Invite))
                {
                    this.currentInvite = payload!.Invite;
                    this.status = "招待を受け取りました";
                }
                else this.status = "リーダーの招待情報を待っています";
            }
            else this.status = "招待情報の応答を確認できません";

            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 閉じられなくても支障はない。
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.status = $"招待情報を取得できません: {Short(ex.Message)}";
        }
    }

    /// <summary>自分のパーティー用の招待を、サーバーへ預ける。取りまとめ役だけ。</summary>
    private void PublishInvite()
    {
        var key = this.partyKey?.Invoke();

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(this.Invite))
            return;

        this.nextInviteRefresh = Environment.TickCount64 + 60000;

        _ = this.SendEnvelopeAsync(new RelayEnvelope
        {
            Type = RelayMessageType.PublishInvite,
            RoomCode = this.roomCode,
            MemberId = this.memberId,
            Payload = JsonSerializer.SerializeToElement(new InvitePayload
            {
                PartyKey = key,
                Invite = this.Invite,
            }),
        });
    }

    private void UpdateRoom(RelayEnvelope msg)
    {
        if (msg.Payload is not { } p)
            return;

        RelayRoomSnapshot? snapshot;

        try
        {
            snapshot = p.Deserialize<RelayRoomSnapshot>();
        }
        catch
        {
            return;
        }

        if (snapshot is null)
            return;

        this.Room = new RelayRoomInfo(
            snapshot.RoomCode,
            this.Invite,
            this.memberId ?? string.Empty,
            this.asLeader ? RelayRole.Leader : RelayRole.Worker,
            snapshot.Members);
    }

    /// <summary>
    /// 届いた合図を積む。
    ///
    /// 封筒から文字列を取り出すだけ。中身は解釈しない。
    /// 読み取り（TryParse）は受け取る側（RelaySync）が行う。
    /// </summary>
    private void Forward(RelayEnvelope msg)
    {
        if (msg.Payload is not { } p || p.ValueKind != JsonValueKind.String)
            return;

        var line = p.GetString();

        if (string.IsNullOrEmpty(line))
            return;

        if (this.inbox.Count >= MaxQueued)
            this.inbox.TryDequeue(out _);

        this.inbox.Enqueue(line);
    }

    // ------------------------------------------------------------------

    private async Task SendEnvelopeAsync(RelayEnvelope envelope)
    {
        if (this.socket is not { State: WebSocketState.Open } ws)
            return;

        try
        {
            await this.SendRawAsync(ws, envelope, this.cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // 送れないときは、読み側が切断に気づく。
        }
    }

    private async Task SendRawAsync(ClientWebSocket ws, RelayEnvelope envelope, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));

        // 同じ口へ同時に送るとフレームが壊れる。1本にまとめる。
        await this.sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct).ConfigureAwait(false);
        }
        finally
        {
            this.sendGate.Release();
        }
    }

    private static async Task<RelayEnvelope?> ReadAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result;

            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (ms.Length + result.Count > MaxMessageBytes)
                return new RelayEnvelope { Type = string.Empty };

            ms.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                break;
        }

        try
        {
            return JsonSerializer.Deserialize<RelayEnvelope>(ms.ToArray());
        }
        catch
        {
            return new RelayEnvelope { Type = string.Empty };
        }
    }

    private static string Short(string s) => s.Length <= 60 ? s : s[..60] + "…";

    public void Dispose()
    {
        try { this.cts.Cancel(); } catch { }
        try { this.socket?.Abort(); } catch { }
        try { this.socket?.Dispose(); } catch { }

        this.sendGate.Dispose();
        this.cts.Dispose();
    }
}

/// <summary>
/// 招待文字列の組み立てと読み取り。
///
/// 形: ATR1:ルーム合言葉:合鍵
///
/// ⚠ 頭を MogColle（MCEB1）と変えてある。
///   取り違えて貼り付けたときに、その場で弾けるようにするため。
///
/// 人が貼り付けるものなので、前後の空白や改行は捨てる。
/// </summary>
internal static class RelayInvite
{
    private const string Prefix = "ATR1";

    internal static string Build(string roomCode, string joinToken)
        => $"{Prefix}:{roomCode}:{joinToken}";

    internal static bool TryParse(string? invite, out string roomCode, out string joinToken)
    {
        roomCode = string.Empty;
        joinToken = string.Empty;

        if (string.IsNullOrWhiteSpace(invite))
            return false;

        var parts = invite.Trim().Split(':');

        if (parts.Length != 3 || parts[0] != Prefix)
            return false;

        roomCode = parts[1].Trim();
        joinToken = parts[2].Trim();

        return roomCode.Length > 0 && joinToken.Length > 0;
    }
}
