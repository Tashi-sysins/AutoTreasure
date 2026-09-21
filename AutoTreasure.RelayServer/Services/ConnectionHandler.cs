using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AutoTreasure.Sync.Relay;
using AutoTreasure.RelayServer.Rooms;

namespace AutoTreasure.RelayServer.Services;

/// <summary>
/// 1本の接続を最後まで面倒みる。
///
/// 流れ:
///   1. 最初の1通で、作る／入る／戻る のどれかを済ませる
///   2. ルームに入れたら、以降は中継だけ
///   3. 切れたら、戻れる猶予を与えて知らせる
/// </summary>
public sealed class ConnectionHandler(
    RoomManager rooms, InviteBoard invites, ILogger<ConnectionHandler> log)
{
    /// <summary>最初の1通を待つ時間。名乗らない相手を居座らせない。</summary>
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    /// <summary>短い間にこれを超えて送ってきたら断る。</summary>
    private const int MaxMessagesPerSecond = 40;

    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        RoomState? room = null;
        MemberSession? me = null;

        try
        {
            // --- 最初の1通 ---
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloCts.CancelAfter(HelloTimeout);

            var first = await ReadAsync(socket, helloCts.Token).ConfigureAwait(false);
            if (first is null)
                return;

            if (first.RelayVersion != RelayEnvelope.CurrentVersion)
            {
                await SendErrorAsync(socket, RelayError.VersionMismatch, ct).ConfigureAwait(false);
                return;
            }

            // ルームへ入る前に、招待だけ尋ねてくる場合がある。
            //
            // 参加側は「自分のパーティーの招待」を知らないと入れない。
            // ここで答えて、いったん切る。相手はすぐ繋ぎ直して入ってくる。
            if (first.Type == RelayMessageType.RequestInvite)
            {
                await AnswerInviteAsync(socket, first, invites, ct).ConfigureAwait(false);
                return;
            }

            var entered = await this.EnterAsync(socket, first, ct).ConfigureAwait(false);
            if (entered is null)
                return;

            (room, me) = entered.Value;

            // 全員へ、いま誰が居るかを配る。
            await room.Gate.WaitAsync(ct).ConfigureAwait(false);
            try { RoomManager.BroadcastMembers(room); }
            finally { room.Gate.Release(); }

            // --- 以降は中継だけ ---
            var windowStart = DateTime.UtcNow;
            var inWindow = 0;

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var msg = await ReadAsync(socket, ct).ConfigureAwait(false);
                if (msg is null)
                    break;

                me.LastSeenUtc = DateTime.UtcNow;

                // 送りすぎを断る。
                if (DateTime.UtcNow - windowStart > TimeSpan.FromSeconds(1))
                {
                    windowStart = DateTime.UtcNow;
                    inWindow = 0;
                }

                if (++inWindow > MaxMessagesPerSecond)
                {
                    await SendErrorAsync(socket, RelayError.RateLimited, ct).ConfigureAwait(false);
                    break;
                }

                switch (msg.Type)
                {
                    case RelayMessageType.Relay:
                        // 参加側は命令を送れない。ここが権限の要。
                        if (me.Role == RelayRole.Worker
                            && !RoomManager.IsAllowedFromWorker(msg))
                        {
                            me.Sender?.TrySend(new RelayEnvelope
                            {
                                Type = RelayMessageType.Error,
                                ErrorCode = RelayError.RoleForbidden,
                                ErrorMessage = RelayError.Describe(RelayError.RoleForbidden),
                            });

                            continue;
                        }

                        await room.Gate.WaitAsync(ct).ConfigureAwait(false);
                        try { rooms.Route(room, me, msg); }
                        finally { room.Gate.Release(); }
                        break;

                    case RelayMessageType.LeaveRoom:
                        await rooms.LeaveAsync(room, me).ConfigureAwait(false);

                        await room.Gate.WaitAsync(ct).ConfigureAwait(false);
                        try { RoomManager.BroadcastMembers(room); }
                        finally { room.Gate.Release(); }

                        return;

                    case RelayMessageType.PublishInvite:
                        // 取りまとめ役だけが預けられる。
                        // 参加側が預けられると、偽の招待を置かれてしまう。
                        if (me.Role != RelayRole.Leader)
                        {
                            me.Sender?.TrySend(new RelayEnvelope
                            {
                                Type = RelayMessageType.Error,
                                ErrorCode = RelayError.RoleForbidden,
                                ErrorMessage = RelayError.Describe(RelayError.RoleForbidden),
                            });

                            continue;
                        }

                        PublishInvite(msg, invites, room);
                        break;

                    case RelayMessageType.KickMember:
                        // 外せるのは取りまとめ役だけ。
                        // 参加側が押せると、互いに追い出し合える。
                        if (me.Role != RelayRole.Leader)
                        {
                            me.Sender?.TrySend(new RelayEnvelope
                            {
                                Type = RelayMessageType.Error,
                                ErrorCode = RelayError.RoleForbidden,
                                ErrorMessage = RelayError.Describe(RelayError.RoleForbidden),
                            });

                            continue;
                        }

                        if (ReadKick(msg) is { ContentId: not 0 } kick
                            && await rooms.KickAsync(room, kick.ContentId).ConfigureAwait(false))
                        {
                            await room.Gate.WaitAsync(ct).ConfigureAwait(false);
                            try { RoomManager.BroadcastMembers(room); }
                            finally { room.Gate.Release(); }
                        }

                        break;

                    case RelayMessageType.AllowMember:
                        // 解けるのも取りまとめ役だけ。
                        // 参加側が押せると、外された人が自分で戻れてしまう。
                        if (me.Role != RelayRole.Leader)
                        {
                            me.Sender?.TrySend(new RelayEnvelope
                            {
                                Type = RelayMessageType.Error,
                                ErrorCode = RelayError.RoleForbidden,
                                ErrorMessage = RelayError.Describe(RelayError.RoleForbidden),
                            });

                            continue;
                        }

                        if (ReadKick(msg) is { ContentId: not 0 } allow)
                            await rooms.AllowAsync(room, allow.ContentId).ConfigureAwait(false);

                        break;

                    case RelayMessageType.Pong:
                        // 生きている印。LastSeen は既に更新済み。
                        break;

                    default:
                        me.Sender?.TrySend(new RelayEnvelope
                        {
                            Type = RelayMessageType.Error,
                            ErrorCode = RelayError.UnknownType,
                            ErrorMessage = RelayError.Describe(RelayError.UnknownType),
                        });
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 止めたときは異常ではない。
        }
        catch (WebSocketException)
        {
            // 切断は異常ではない。
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "接続の処理で問題が起きました");
        }
        finally
        {
            // 切れた。すぐ消さず、戻れる猶予を与える。
            if (room is not null && me is not null)
            {
                await rooms.MarkDisconnectedAsync(room, me).ConfigureAwait(false);

                await room.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { RoomManager.BroadcastMembers(room); }
                finally { room.Gate.Release(); }
            }
        }
    }

    /// <summary>最初の1通を処理して、ルームへ入れる。</summary>
    private async Task<(RoomState, MemberSession)?> EnterAsync(
        WebSocket socket, RelayEnvelope first, CancellationToken ct)
    {
        var who = ReadIdentity(first);

        RoomResult result = first.Type switch
        {
            RelayMessageType.CreateRoom
                => await rooms.CreateRoomAsync(
                        who, socket, ReadInvite(first)?.PartyKey)
                    .ConfigureAwait(false),

            RelayMessageType.JoinRoom
                => await rooms.JoinRoomAsync(first.RoomCode, first.JoinToken, who, socket)
                    .ConfigureAwait(false),

            RelayMessageType.ResumeRoom
                => await rooms.ResumeRoomAsync(
                        first.RoomCode, first.MemberId, first.ResumeToken, socket)
                    .ConfigureAwait(false),

            _ => new RoomResult(false, RelayError.NotInRoom),
        };

        if (!result.Ok || result.Room is null || result.Member is null)
        {
            await SendErrorAsync(socket, result.ErrorCode, ct).ConfigureAwait(false);
            return null;
        }

        var reply = new RelayEnvelope
        {
            Type = first.Type == RelayMessageType.CreateRoom
                ? RelayMessageType.RoomCreated
                : RelayMessageType.RoomJoined,
            RequestId = first.RequestId,
            RoomCode = result.Room.RoomCode,
            MemberId = result.Member.MemberId,
            Role = result.Member.Role,
            JoinToken = result.JoinToken,
            ResumeToken = result.ResumeToken,
        };

        result.Member.Sender?.TrySend(reply);

        return (result.Room, result.Member);
    }

    /// <summary>
    /// 取りまとめ役から預かった招待を、掲示板へ置く。
    ///
    /// 鍵はパーティーリーダーのIDから作ったハッシュ。
    /// サーバーは誰のパーティーかを知らないまま取り次ぐ。
    /// </summary>
    private static void PublishInvite(
        RelayEnvelope msg, InviteBoard invites, RoomState room)
    {
        var payload = ReadInvite(msg);

        if (payload is null || string.IsNullOrWhiteSpace(payload.PartyKey))
            return;

        invites.Publish(payload.PartyKey, payload.Invite, room.RoomCode);

        // どのルームの招待かを覚えておく。取りまとめ役が抜けたら消すため。
        room.PartyKey = payload.PartyKey;
    }

    /// <summary>
    /// 招待の尋ねに答える。
    ///
    /// ルームへ入る前に呼ばれる。答えたら、この接続は役目を終える。
    /// </summary>
    private static async Task AnswerInviteAsync(
        WebSocket socket, RelayEnvelope msg, InviteBoard invites, CancellationToken ct)
    {
        var payload = ReadInvite(msg);
        var invite = payload is null ? null : invites.Take(payload.PartyKey);

        var reply = new RelayEnvelope
        {
            Type = RelayMessageType.InviteAnswer,
            RequestId = msg.RequestId,
            Payload = JsonSerializer.SerializeToElement(new InvitePayload
            {
                PartyKey = payload?.PartyKey ?? string.Empty,
                Invite = invite ?? string.Empty,
            }),
        };

        if (invite is null)
        {
            reply.ErrorCode = RelayError.InviteNotReady;
            reply.ErrorMessage = RelayError.Describe(RelayError.InviteNotReady);
        }

        try
        {
            var json = JsonSerializer.Serialize(reply);

            await socket.SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct).ConfigureAwait(false);
        }
        catch
        {
            // 答えられなくても、相手はまた聞きに来る。
        }
    }

    private static KickPayload? ReadKick(RelayEnvelope envelope)
    {
        if (envelope.Payload is not { } p || p.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            return p.Deserialize<KickPayload>();
        }
        catch
        {
            return null;
        }
    }

    private static InvitePayload? ReadInvite(RelayEnvelope envelope)
    {
        if (envelope.Payload is not { } p || p.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            return p.Deserialize<InvitePayload>();
        }
        catch
        {
            return null;
        }
    }

    private static RelayIdentity ReadIdentity(RelayEnvelope envelope)
    {
        if (envelope.Payload is { } p && p.ValueKind == JsonValueKind.Object)
        {
            try
            {
                return p.Deserialize<RelayIdentity>() ?? new RelayIdentity();
            }
            catch
            {
                // 読めなければ空で進む。身元はサーバーが振り直す。
            }
        }

        return new RelayIdentity();
    }

    /// <summary>
    /// 1通を読む。
    ///
    /// 分割されて届くので、全部そろってから JSON として読む。
    /// 大きすぎるものは読み捨てる（溜め込んで落ちないため）。
    /// </summary>
    private static async Task<RelayEnvelope?> ReadAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result;

            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            // 文字だけ受ける。
            if (result.MessageType != WebSocketMessageType.Text)
                return null;

            if (ms.Length + result.Count > MemberSender.MaxMessageBytes)
            {
                // 大きすぎる。残りを読み飛ばしてから捨てる。
                while (!result.EndOfMessage)
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct)
                        .ConfigureAwait(false);
                }

                return new RelayEnvelope { Type = string.Empty };
            }

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
            // 壊れた JSON は捨てる。接続は切らない。
            return new RelayEnvelope { Type = string.Empty };
        }
    }

    private static async Task SendErrorAsync(
        WebSocket socket, string? code, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(new RelayEnvelope
            {
                Type = RelayMessageType.Error,
                ErrorCode = code,
                ErrorMessage = RelayError.Describe(code),
            });

            await socket.SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct).ConfigureAwait(false);
        }
        catch
        {
            // 断りを伝えられなくても、こちらは進む。
        }
    }
}
