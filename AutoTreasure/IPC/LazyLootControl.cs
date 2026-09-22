using ECommons.DalamudServices;
using System;
using System.Linq;
using System.Reflection;

namespace AutoTreasure.IPC;

/// <summary>
/// LazyLoot が入っているかを見て、ロットの担当を譲る。
///
/// <b>考え方を変えた（2026-09-19）。</b>
///
/// 以前は「周回中だけ LazyLoot の自動ロット（FULF）を切る」ことをしていた。
/// これをやめて、<b>LazyLoot が入っているなら、ロットは丸ごと任せる</b>ことにした。
///
/// やめた理由は2つ。
///
/// <b>1. 元に戻せなかった。</b>
/// 設定の値そのものは反射で戻せる。実際ファイルにも戻っていた。
/// ところが LazyLoot の画面表示（右上の「FULF Disabled」）は、
/// LazyLoot 自身が設定を変えたときにしか書き換わらない。
/// こちらが横から値だけ書き換えても、表示は「切れている」ままになる。
/// LazyLoot には IPC が無く、「表示を更新して」と頼む手段もない。
/// 利用者から見れば「オートトレジャーを止めたのに LazyLoot が壊れたまま」になる。
///
/// <b>2. そもそも横取りする必要が無い。</b>
/// LazyLoot はロット専用のプラグインで、こちらより作り込まれている。
/// 入っているなら、そちらに任せる方が利用者の設定どおりに動く。
///
/// <b>今の振る舞い。</b>
///   LazyLoot が入っている   → こちらのロットは動かさない（<see cref="ShouldYield"/> が true）
///   LazyLoot が入っていない → こちらがロットする
///
/// 相手の設定には<b>一切触らない</b>。触らなければ、戻す必要も生じない。
/// </summary>
internal static class LazyLootControl
{
    /// <summary>共有ファイルでは分からない各クライアントの実際の状態を読む。変更はしない。</summary>
    internal static string Diagnostic()
    {
        if (!ShouldYield) return "未ロード";
        try
        {
            var states = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "LazyLoot")
                .Select(a => a.GetType("LazyLoot.LazyLoot"))
                .Where(t => t != null)
                .Select((t, index) =>
                {
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    var config = t!.GetField("Config", flags)?.GetValue(null);
                    if (config == null) return "Config 未取得";
                    var fields = config.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Where(f => f.Name.StartsWith("Fulf") || f.Name.StartsWith("Restriction")
                            || f.Name.StartsWith("WeeklyLockout") || f.Name == "NoPassEmergency")
                        .Where(f => f.FieldType.IsPrimitive)
                        .Select(f => $"{f.Name}={f.GetValue(config)}");
                    // 無効化済みのアセンブリも残るため、番号を付けて混同を防ぐ。
                    var restrictions = config.GetType().GetProperty("Restrictions")?.GetValue(config)
                        ?? config.GetType().GetField("Restrictions")?.GetValue(config);
                    var rules = System.Text.Json.JsonSerializer.Serialize(restrictions,
                        new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
                    return $"実体{index + 1}: " + string.Join(", ", fields)
                        + $", rules={rules}, rollOption={t.GetField("_rollOption", flags)?.GetValue(null)}";
                });
            return string.Join(" | ", states);
        }
        catch (Exception ex) { return $"読取失敗: {ex.Message}"; }
    }

    private static object? _config;
    private static FieldInfo? _field;
    private static MethodInfo? _save;


    /// <summary>
    /// LazyLoot が入っていて、ロットを任せられるか。
    ///
    /// これが true のあいだ、こちらのロット処理は動かない。
    /// </summary>
    internal static bool ShouldYield
    {
        get
        {
            // 1秒だけ答えを覚えておく。
            //
            // <b>覚えっぱなしにはしない。</b>
            // 途中で LazyLoot を入れたり外したりされても気づけるように、
            // 短い間隔で確かめ直す。
            //
            // ただし毎フレーム調べる必要はない。
            // ここは画面と情報バーから毎フレーム呼ばれ、
            // 1回あたり導入済みプラグイン全件（実機で65件）を
            // 名前で突き合わせる。1秒に1回で十分。
            var now = Environment.TickCount64;

            // <b>引き算で比べてはいけない。</b>
            // 「まだ一度も調べていない」を long.MinValue で表すと、
            // now - long.MinValue が桁あふれして大きな負の数になる。
            // 負の数は CacheMilliseconds より小さいので、
            // 「さっき調べたばかり」と誤判定し、
            // <b>一度も Detect() を呼ばないまま false を返し続ける</b>。
            //
            // 実際にそうなった（2026-09-19）。
            // LazyLoot を入れているのに、ロットの設定欄が出たままだった。
            //
            // 「まだ調べていない」は専用の目印で表し、
            // 時刻の比較は足し算の向きで行う。
            if (_checked && now < _nextCheckAt)
                return _cached;

            _checked = true;
            _nextCheckAt = now + CacheMilliseconds;

            var found = Detect();

            // 答えが変わったときだけ記録する。
            // 毎秒書くとログが埋まるが、変わり目は残したい。
            // 「入れているのに任せてくれない」を調べるときの手がかりになる。
            if (found != _cached || !_logged)
            {
                _logged = true;
                Svc.Log.Information(found
                    ? "[AutoTreasure] LazyLoot を見つけました。ロットは LazyLoot に任せます。"
                    : "[AutoTreasure] LazyLoot は見つかりません。ロットはこちらで行います。");
            }

            _cached = found;

            return _cached;
        }
    }

    /// <summary>一度でも記録したか。最初の1回は必ず残す。</summary>
    private static bool _logged;

    /// <summary>
    /// LazyLoot が今、動いているかを調べる。
    ///
    /// <b>Dalamud の一覧だけを見る。</b>
    /// 一覧に載っていて <c>IsLoaded</c> が true なら動いている。
    /// それ以外（無効化・削除・一覧に無い）は動いていない。
    ///
    /// <b>アセンブリの有無で判断してはいけない。</b>
    /// 以前はこれを保険にしていたが、間違いだった。
    /// Dalamud はプラグインを無効にしても、
    /// 読み込んだアセンブリをすぐには捨てない。
    /// そのため「無効にしたのに、動いていると判断し続ける」ことになり、
    /// ロットの設定欄が戻らなかった（実測 2026-09-19）。
    ///
    /// 名前は <c>InternalName</c> と <c>Name</c> の両方を見る。
    /// 配布元によって、どちらに正式名が入るかが違うため。
    /// </summary>
    private static bool Detect()
    {
        try
        {
            foreach (var plugin in Svc.PluginInterface.InstalledPlugins)
            {
                if (!IsLazyLootName(plugin.InternalName) && !IsLazyLootName(plugin.Name))
                    continue;

                // 名前が一致した。今読み込まれているかで決める。
                return plugin.IsLoaded;
            }
        }
        catch
        {
            // 一覧を読めないときは「入っていない」とみなす。
            // ここで true にすると、こちらのロットまで止まってしまう。
        }

        return false;
    }

    private static bool IsLazyLootName(string? name)
        => string.Equals(name, "LazyLoot", StringComparison.OrdinalIgnoreCase);

    /// <summary>前回調べた答え。</summary>
    private static bool _cached;

    /// <summary>一度でも調べたか。初回は必ず調べさせるために持つ。</summary>
    private static bool _checked;

    /// <summary>次に調べてよい時刻。</summary>
    private static long _nextCheckAt;

    /// <summary>答えを覚えておく長さ（ミリ秒）。</summary>
    private const long CacheMilliseconds = 1000;

    /// <summary>LazyLoot が入っているか。</summary>
    internal static bool IsAvailable => ShouldYield;

    /// <summary>
    /// 以前の作りで切ったまま戻っていない設定を、元に戻す。
    ///
    /// <b>後始末のための処理。</b>
    /// 古い作りでは、周回中に FULF を切って、終わったら戻していた。
    /// 途中でゲームが落ちると切られたまま残る。
    /// また、表示が更新されないために「切れたまま」に見える不具合もあった。
    ///
    /// ここで一度だけ戻し、記録も消す。
    /// 新しい作りでは二度と切らないので、この処理は
    /// 「昔の設定が残っている人」のためだけに残してある。
    /// </summary>
    internal static void RestoreIfLeftSuppressed()
    {
        if (!Plugin.Config.LazyLootSuppressed)
            return;

        // 相手がまだ起動していない場合、復元要求を消さずに次回へ残す。
        var wanted = Plugin.Config.LazyLootOriginal;

        var field = Resolve();
        if (field == null || _config == null)
            return;

        try
        {
            var now = (bool)(field.GetValue(_config) ?? false);

            if (now != wanted)
            {
                field.SetValue(_config, wanted);
                if (_save == null) return;
                _save.Invoke(_config, null);
            }
            Plugin.Config.LazyLootSuppressed = false;
            Plugin.Config.Save();

            Svc.Log.Information(
                $"[AutoTreasure] 以前切ったままだった LazyLoot の自動ロットを戻しました（{(wanted ? "有効" : "無効")}）。"
                + "画面右上の表示は、LazyLoot の設定画面を開くか、ゲームを入れ直すと直ります。");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "LazyLoot の自動ロットを戻せませんでした。");
        }
    }

    /// <summary>
    /// 動いている LazyLoot の設定を探す。
    ///
    /// 読み込まれているアセンブリから LazyLoot の本体を見つけ、
    /// その中の Config を取り出す。
    ///
    /// IPC が用意されていないので、この方法しかない。
    /// 形が変われば読めなくなるが、そのときは黙って諦める
    /// （こちらの都合で相手のプラグインを壊さないため）。
    ///
    /// <b>見つかること自体が「入っている」の合図になる。</b>
    /// 今はもう設定を書き換えないので、探す目的は在否の確認だけ。
    /// </summary>
    private static FieldInfo? Resolve()
    {
        // 復元時だけ現在の実体を探す。未導入判定や古い実体を固定しない。
        _field = null;
        _config = null;
        _save = null;
        if (!ShouldYield) return null;

        try
        {
            var pluginType = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "LazyLoot")
                .Select(a => a.GetType("LazyLoot.LazyLoot"))
                .FirstOrDefault(t => t != null);

            if (pluginType == null)
            {
                Svc.Log.Information(
                    "[AutoTreasure] LazyLoot は入っていません。ロットはこちらで行います。");
                return null;
            }

            var configField = pluginType.GetField("Config",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            _config = configField?.GetValue(null);

            if (_config == null)
            {
                Svc.Log.Information("[AutoTreasure] LazyLoot の設定を読めませんでした。");
                return null;
            }

            // 自動ロットの入り切り。LazyLoot では FULF と呼ばれている。
            _field = _config.GetType().GetField("FulfEnabled",
                BindingFlags.Public | BindingFlags.Instance);

            _save = _config.GetType().GetMethod("Save",
                BindingFlags.Public | BindingFlags.Instance,
                Type.EmptyTypes);

            if (_field == null)
                Svc.Log.Information("[AutoTreasure] LazyLoot の設定の形が変わったようです。");
            else
                Svc.Log.Information(
                    "[AutoTreasure] LazyLoot を見つけました。ロットは LazyLoot に任せます。");

            return _field;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "LazyLoot の設定を探せませんでした。");
            return null;
        }
    }
}
