using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AutoTreasure.Sync;
using AutoTreasure.Sync.Relay;

namespace AutoTreasure.RelayHarness;

/// <summary>
/// 中継サーバーを、ゲーム無しで試すための疑似ノード。
///
/// 本番の封筒（RelayEnvelope）と合図（SyncMessage）をそのまま使う。
/// 作り直すと「偽物が通った」だけになる。
///
/// ⚠ プロセスを分けて動かすこと。
///   同じプロセス内で試すと、切断まわりの不具合が見つからない。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var url = Arg(args, "--url") ?? "ws://127.0.0.1:8080/treasure/ws";
        var role = Arg(args, "--role") ?? "member";
        var name = Arg(args, "--name") ?? "node";
        var partyKey = Arg(args, "--party") ?? "harness-party";
        var seconds = int.TryParse(Arg(args, "--seconds"), out var s) ? s : 20;

        // 送る合図。指定が無ければ StepDone を送る。
        var send = Arg(args, "--send");

        var asLeader = role.Equals("leader", StringComparison.OrdinalIgnoreCase);

        var node = new HarnessNode(url, asLeader, name, partyKey);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        try
        {
            await node.RunAsync(send, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 時間切れは正常な終わり方。
        }

        node.Report();

        return 0;
    }

    private static string? Arg(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// <summary>疑似ノード1つ。</summary>
internal sealed class HarnessNode(string url, bool asLeader, string name, string partyKey)
{
    private readonly List<string> received = [];
    private readonly SemaphoreSlim sendGate = new(1, 1);

    private ClientWebSocket? socket;
    private string? memberId;
    private string? roomCode;
    private string? joinToken;

    /// <summary>この節点が受け取った合図。</summary>
    internal IReadOnlyList<string> Received => this.received;

    internal async Task RunAsync(string? sendLine, CancellationToken ct)
    {
        // 参加側は、まず自分のパーティーの招待を尋ねる。
        var invite = string.Empty;

        if (!asLeader)
        {
            invite = await this.AskInviteAsync(ct);

            if (string.IsNullOrEmpty(invite))
            {
                Log($"{name}: 招待を受け取れませんでした");
                return;
            }

            Log($"{name}: 招待を受け取りました");
        }

        using var ws = new ClientWebSocket();
        this.socket = ws;

        await ws.ConnectAsync(new Uri(url), ct);

        await this.SendAsync(ws, this.BuildEntry(invite), ct);

        var sent = false;

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var msg = await ReadAsync(ws, ct);

            if (msg is null)
                break;

            switch (msg.Type)
            {
                case RelayMessageType.RoomCreated:
                case RelayMessageType.RoomJoined:
                    this.memberId = msg.MemberId;
                    this.roomCode = msg.RoomCode;
                    this.joinToken = msg.JoinToken ?? this.joinToken;

                    Log($"{name}: 入りました（{msg.RoomCode} / {msg.Role}）");

                    // 取りまとめ役は、招待を預ける。
                    if (asLeader)
                    {
                        await this.SendAsync(ws, new RelayEnvelope
                        {
                            Type = RelayMessageType.PublishInvite,
                            RoomCode = this.roomCode,
                            MemberId = this.memberId,
                            Payload = JsonSerializer.SerializeToElement(new InvitePayload
                            {
                                PartyKey = partyKey,
                                Invite = RelayInvite.Build(this.roomCode!, this.joinToken!),
                            }),
                        }, ct);

                        Log($"{name}: 招待を預けました");
                    }

                    break;

                case RelayMessageType.MemberList:
                    var snap = msg.Payload?.Deserialize<RelayRoomSnapshot>();

                    if (snap is not null)
                    {
                        var n = snap.Members.Count(m => m.Status == MemberStatus.Connected);
                        Log($"{name}: いま {n} 人");

                        // 全員そろってから送る。
                        // 先に送ると、まだ入っていない相手には届かない。
                        if (!sent && sendLine is not null && n >= 2)
                        {
                            sent = true;

                            await this.SendAsync(ws, new RelayEnvelope
                            {
                                Type = RelayMessageType.Relay,
                                RoomCode = this.roomCode,
                                MemberId = this.memberId,
                                Payload = JsonSerializer.SerializeToElement(sendLine),
                            }, ct);

                            Log($"{name}: 送りました → {sendLine}");
                        }
                    }

                    break;

                case RelayMessageType.Relay:
                    if (msg.Payload is { } p && p.ValueKind == JsonValueKind.String)
                    {
                        var line = p.GetString() ?? string.Empty;
                        this.received.Add(line);

                        var ok = SyncMessage.TryParse(line, out var parsed);
                        Log($"{name}: 受け取り ← {line}（読み取り {(ok ? "成功" : "失敗")}{(ok ? $" / {parsed.Kind}" : "")}）");
                    }

                    break;

                case RelayMessageType.Ping:
                    await this.SendAsync(ws, new RelayEnvelope { Type = RelayMessageType.Pong }, ct);
                    break;

                case RelayMessageType.Error:
                    Log($"{name}: 断られました（{msg.ErrorCode}）{RelayError.Describe(msg.ErrorCode)}");
                    break;

                case RelayMessageType.RoomClosed:
                    Log($"{name}: ルームが終わりました（{msg.ErrorCode}）");
                    return;
            }
        }
    }

    private RelayEnvelope BuildEntry(string invite)
    {
        var identity = new
        {
            clientId = $"{name}-{Environment.ProcessId}",
            sessionId = Guid.NewGuid().ToString("N")[..8],
            character = name,
            contentId = (ulong)Math.Abs(name.GetHashCode()) + 1,
            partyKey,
        };

        if (asLeader)
        {
            return new RelayEnvelope
            {
                Type = RelayMessageType.CreateRoom,
                Payload = JsonSerializer.SerializeToElement(identity),
            };
        }

        RelayInvite.TryParse(invite, out var code, out var token);

        return new RelayEnvelope
        {
            Type = RelayMessageType.JoinRoom,
            RoomCode = code,
            JoinToken = token,
            Payload = JsonSerializer.SerializeToElement(identity),
        };
    }

    /// <summary>自分のパーティーの招待を尋ねる。短い接続を1本だけ使う。</summary>
    private async Task<string> AskInviteAsync(CancellationToken ct)
    {
        // 取りまとめ役が預けるまで少し待つ。
        for (var attempt = 0; attempt < 20 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(url), ct);

                await this.SendAsync(ws, new RelayEnvelope
                {
                    Type = RelayMessageType.RequestInvite,
                    Payload = JsonSerializer.SerializeToElement(
                        new InvitePayload { PartyKey = partyKey }),
                }, ct);

                var reply = await ReadAsync(ws, ct);

                if (reply?.Type == RelayMessageType.InviteAnswer
                    && reply.Payload?.Deserialize<InvitePayload>() is { } payload
                    && !string.IsNullOrWhiteSpace(payload.Invite))
                {
                    return payload.Invite;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // まだ立ち上がっていない。待って試す。
            }

            await Task.Delay(500, ct);
        }

        return string.Empty;
    }

    private async Task SendAsync(ClientWebSocket ws, RelayEnvelope envelope, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));

        await this.sendGate.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
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
                result = await ws.ReceiveAsync(buffer, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

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
            return null;
        }
    }

    /// <summary>受け取ったものを、後から突き合わせられる形で出す。</summary>
    internal void Report()
    {
        Console.WriteLine($"### {name} が受け取った合図 {this.received.Count} 件");

        foreach (var line in this.received)
            Console.WriteLine($"### {name} <= {line}");
    }

    private static void Log(string text)
        => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
}
