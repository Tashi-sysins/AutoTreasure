using ECommons.DalamudServices;
using System;
using System.Linq;
using System.Reflection;

namespace AutoTreasure.IPC;

/// <summary>
/// vnavmesh の設定を、こちらから一時的に変える。
///
/// 変えたいのは「地形を作るのに何コア使うか」の1つだけ。
/// 初期値は 1 で、これだと4台同時のときに地形が間に合わず、
/// 移動の指示が静かに失敗し続ける（Mesh: Not Ready のまま棒立ちになる）。
///
/// vnavmesh には、この値を外から変える窓口が無い。
/// そのため、動いているプラグインの中身を直接見て書き換える。
///
/// <para>
/// 借り物の設定なので、必ず元に戻す。
/// 周回を始めるときに上げ、止めるときに戻す。
/// 戻し忘れると、ゲームを閉じたときにその値が保存されてしまう。
/// </para>
/// </summary>
internal static class VNavmeshConfig
{
    private static FieldInfo? _field;
    private static object? _config;
    private static bool _searched;

    /// <summary>元の値。戻すときに使う。まだ変えていなければ null。</summary>
    private static int? _original;

    /// <summary>今この値に変えているか。</summary>
    internal static bool IsOverridden => _original != null;

    /// <summary>今の値。読めなければ null。</summary>
    internal static int? Current
    {
        get
        {
            // 取り込んだ経路探索を使っているなら、そちらの値を返す。
            if (VNavmesh.UsingEmbedded)
                return Navmesh.Service.Config.BuildMaxCores;

            var target = Resolve();
            if (target == null || _config == null)
                return null;

            try { return (int?)target.GetValue(_config); }
            catch { return null; }
        }
    }

    /// <summary>
    /// 地形づくりに使うコア数を、一時的に変える。
    /// </summary>
    internal static bool Apply(int cores)
    {
        // 取り込んだ経路探索を使っているなら、その設定を直に変える。
        // 反射で外部プラグインの中を探る必要はない。
        if (VNavmesh.UsingEmbedded)
        {
            var now = Navmesh.Service.Config.BuildMaxCores;

            _original ??= now;

            if (now == cores)
                return true;

            Navmesh.Service.Config.BuildMaxCores = cores;
            Navmesh.Service.Config.NotifyModified();

            Svc.Log.Information(
                $"[AutoTreasure] 地形づくりを {now} → {cores} コアにしました（取り込み版）。");
            return true;
        }

        var field = Resolve();
        if (field == null || _config == null)
            return false;

        try
        {
            var now = (int)(field.GetValue(_config) ?? 1);

            // 最初に変えるときだけ、元の値を控える。
            _original ??= now;

            if (now == cores)
                return true;

            field.SetValue(_config, cores);
            NotifyModified();

            Svc.Log.Information(
                $"[AutoTreasure] vnavmesh の地形づくりを {now} → {cores} コアにしました。");
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "vnavmesh の設定を変えられませんでした。");
            return false;
        }
    }

    /// <summary>元の値に戻す。</summary>
    internal static void Restore()
    {
        if (_original == null)
            return;

        // 取り込んだ経路探索を使っているなら、そちらを直に戻す。
        //
        // <b>ここを忘れると、上げた値が使う人の設定ファイルに残る。</b>
        // Current と Apply には取り込み版の分岐があるのに、
        // ここにだけ無かった。
        //
        // 下の Resolve() はアセンブリ名 "vnavmesh" を探すが、
        // 取り込み版ではその型は AutoTreasure 自身の中にあるので見つからない。
        // つまり「戻せませんでした」ではなく、何もせずに帰っていた。
        //
        // そのうえ EmbeddedNavmesh は Config.Modified に保存処理を繋いである。
        // 上げたままどこかで NotifyModified() が走ると、
        // その値が AutoTreasure.navmesh.json に書き込まれて残る。
        if (VNavmesh.UsingEmbedded)
        {
            try
            {
                Navmesh.Service.Config.BuildMaxCores = _original.Value;
                Navmesh.Service.Config.NotifyModified();

                Svc.Log.Information(
                    $"[AutoTreasure] 地形づくりを {_original.Value} コアに戻しました（取り込み版）。");
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "地形づくりの設定を戻せませんでした（取り込み版）。");
            }
            finally
            {
                _original = null;
            }

            return;
        }

        var field = Resolve();
        if (field == null || _config == null)
        {
            _original = null;
            return;
        }

        try
        {
            field.SetValue(_config, _original.Value);
            NotifyModified();
            Svc.Log.Information(
                $"[AutoTreasure] vnavmesh の地形づくりを {_original.Value} コアに戻しました。");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "vnavmesh の設定を戻せませんでした。");
        }
        finally
        {
            _original = null;
        }
    }

    /// <summary>
    /// vnavmesh に「設定が変わった」と伝える。
    /// これを呼ばないと、保存されないまま元に戻ることがある。
    /// </summary>
    private static void NotifyModified()
    {
        try
        {
            _config?.GetType()
                    .GetMethod("NotifyModified", BindingFlags.Public | BindingFlags.Instance)
                    ?.Invoke(_config, null);
        }
        catch
        {
            // 伝えられなくても、値自体は変わっている。
        }
    }

    /// <summary>
    /// 動いている vnavmesh の設定を探す。
    ///
    /// 読み込まれているアセンブリから Navmesh.Service を見つけ、
    /// その中の Config を取り出す。
    /// </summary>
    private static FieldInfo? Resolve()
    {
        if (_field != null)
            return _field;

        if (_searched)
            return null;

        _searched = true;

        try
        {
            var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "vnavmesh")
                .Select(a => a.GetType("Navmesh.Service"))
                .FirstOrDefault(t => t != null);

            if (serviceType == null)
            {
                Svc.Log.Information("[AutoTreasure] vnavmesh が見つかりません。設定はそのままにします。");
                return null;
            }

            var configField = serviceType.GetField("Config",
                BindingFlags.Public | BindingFlags.Static);

            _config = configField?.GetValue(null);
            if (_config == null)
                return null;

            _field = _config.GetType().GetField("BuildMaxCores",
                BindingFlags.Public | BindingFlags.Instance);

            if (_field == null)
                Svc.Log.Information("[AutoTreasure] vnavmesh の設定の形が変わったようです。");

            return _field;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "vnavmesh の設定を探せませんでした。");
            return null;
        }
    }
}
