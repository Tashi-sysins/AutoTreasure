using AutoTreasure.RelayServer.Rooms;
using AutoTreasure.RelayServer.Services;

// AutoTreasure の中継サーバー。
//
// 役目は3つだけ。
//   ・誰がどのルームに居るか覚える
//   ・受け取った合図を**送り主以外の全員**へ流す
//   ・切れた人に、戻ってくる猶予を与える
//
// ⚠ 「送り主以外の全員」が MogColle との決定的な違い。
//   AutoTreasure はメンバー同士でも合図をやり取りする（StepDone など）。
//   詳しくは RoomManager.Route を見ること。
//
// ゲームの状態は読まない。周回の判断もしない。
// それらはクライアント側にあるので、ゲームの仕様が変わっても
// ここを直さずに済む。

// Docker の生存確認から呼ばれる。
//
// image に wget も curl も入っていないので、自分で自分を叩く。
// 応答があれば 0、無ければ 1 を返す。
if (args.Contains("--healthcheck"))
{
    var probePort = Environment.GetEnvironmentVariable("TREASURE_RELAY_PORT") ?? "8080";

    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var reply = await http.GetAsync($"http://127.0.0.1:{probePort}/treasure/health");

        return reply.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<InviteBoard>();
builder.Services.AddSingleton<ConnectionHandler>();
builder.Services.AddHostedService<CleanupService>();

// 待ち受けは環境変数で変えられるようにする（既定 8080）。
// VPS では Caddy が前に立つので、ここは平文でよい。
var port = Environment.GetEnvironmentVariable("TREASURE_RELAY_PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    // 相手が黙っていないか確かめる。
    KeepAliveInterval = TimeSpan.FromSeconds(15),
});

// 生存確認。Caddy と監視から叩く。
app.MapGet("/treasure/health", (RoomManager rooms) => Results.Json(new
{
    status = "ok",
    rooms = rooms.RoomCount,
}));

// ここへ繋ぐ。
app.Map("/treasure/ws", async (HttpContext ctx, ConnectionHandler handler) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("WebSocket でつないでください。");
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await handler.HandleAsync(socket, ctx.RequestAborted);
});

app.Run();

return 0;
