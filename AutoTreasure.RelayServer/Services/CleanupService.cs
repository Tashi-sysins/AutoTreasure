using AutoTreasure.RelayServer.Rooms;

namespace AutoTreasure.RelayServer.Services;

/// <summary>
/// 期限の切れたものを定期的に片付ける。
///
/// なぜ要るか:
///   切れた人はすぐ消さず、戻ってくる猶予を与えている。
///   誰かが戻ってこなかったことに気づく仕組みが無いと、
///   いつまでも「再接続中」のまま居座ってしまう。
/// </summary>
public sealed class CleanupService(
    RoomManager rooms, InviteBoard invites, ILogger<CleanupService> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("片付けを開始しました（{Seconds} 秒ごと）", Interval.TotalSeconds);

        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var now = DateTime.UtcNow;

                await rooms.SweepAsync(now).ConfigureAwait(false);
                invites.Sweep(now);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 片付けに失敗しても、サーバーは止めない。
                log.LogWarning(ex, "片付けで問題が起きました");
            }
        }
    }
}
