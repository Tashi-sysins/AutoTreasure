using ECommons.DalamudServices;

namespace AutoTreasure.Sync;

/// <summary>
/// 設定に合わせて、連携の口を1つ作る。
///
/// ここだけが「どちらの経路か」を知っている。
/// RunController は <see cref="ISyncTransport"/> しか見ない。
/// </summary>
internal static class SyncTransportFactory
{
    /// <summary>
    /// いまの設定に合った口を作る。
    ///
    /// <b>URL が空のときはパイプに退く。</b>
    ///
    /// なぜそうするか:
    ///   既定は「インターネット」だが、配布物に URL は埋めていない
    ///   （他人の利用が自分の VPS に来てしまうため）。
    ///
    ///   URL を知らない人が更新しただけで連携できなくなると、
    ///   「更新したら壊れた」という壊れ方になる。
    ///   繋ぎ先が無いなら、これまでどおり同じPCの中で繋ぐ。
    /// </summary>
    internal static ISyncTransport Create()
    {
        var cfg = Plugin.Config;

        if (cfg.SyncTransport == SyncTransportKind.Internet)
        {
            var url = (cfg.RelayUrl ?? string.Empty).Trim();

            if (url.Length > 0)
            {
                Svc.Log.Information(
                    "[AutoTreasure] 連携: インターネット（中継サーバー）を使います。");

                return new RelaySync(url);
            }

            Svc.Log.Information(
                "[AutoTreasure] 連携: 中継サーバーのURLが未設定のため、"
                + "このPCの中だけで連携します。");
        }

        return new PipeSync(cfg.PipeName);
    }

    /// <summary>
    /// いまの設定で、実際に使われる経路。
    ///
    /// 画面の表示に使う。設定の値をそのまま出すと、
    /// URL 未設定でパイプに退いているのに
    /// 「インターネット」と表示されてしまう。
    /// </summary>
    internal static SyncTransportKind Effective()
    {
        var cfg = Plugin.Config;

        if (cfg.SyncTransport != SyncTransportKind.Internet)
            return SyncTransportKind.LocalPipe;

        return string.IsNullOrWhiteSpace(cfg.RelayUrl)
            ? SyncTransportKind.LocalPipe
            : SyncTransportKind.Internet;
    }
}
