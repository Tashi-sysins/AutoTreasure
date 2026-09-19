using AutoTreasure.IPC;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using System;

namespace AutoTreasure.Helpers;

/// <summary>
/// 地形（navmesh）が使える状態かを見張り、足りなければ促す。
///
/// 地形が無いと、移動の指示は静かに失敗する。
/// 画面には何も出ず、その場で棒立ちになるだけ。
/// 実際、これで何度も止まった（vnavmesh の表示は Mesh: Not Ready）。
///
/// <para>
/// vnavmesh はエリアが変わると自分で作り始めるが、
///   ・自動読み込みが切られている
///   ・ムービーの間は待たされる
///   ・何かの拍子に作り始めそこねる
/// といった理由で、始まらないことがある。
/// その場合はこちらから作り直しを頼む。
/// </para>
/// </summary>
internal static class NavmeshWatcher
{
    /// <summary>作り直しを頼んだ回数。何度も頼まないように数える。</summary>
    private static int _rebuildRequests;

    /// <summary>この回数を超えたら、もう頼まない（人に任せる）。</summary>
    private const int MaxRebuildRequests = 3;

    /// <summary>最後に見たエリア。変わったら数え直す。</summary>
    private static uint _lastTerritory;

    /// <summary>地形が無い状態が始まった時刻。</summary>
    private static DateTime? _notReadySince;

    /// <summary>
    /// 地形が作られているか確かめ、始まっていなければ促す。
    ///
    /// 何度呼んでもよい。頼むのは間隔をあける。
    /// </summary>
    internal static void EnsureBuilding()
    {
        // エリアが変わったら、数え直す。
        var territory = PlayerHelper.TerritoryType;
        if (territory != _lastTerritory)
        {
            _lastTerritory = territory;
            _rebuildRequests = 0;
            _notReadySince = null;
        }

        if (VNavmesh.NavIsReady)
        {
            _notReadySince = null;
            _rebuildRequests = 0;
            return;
        }

        _notReadySince ??= DateTime.UtcNow;

        // 作っている最中なら、待つだけでよい。
        if (VNavmesh.NavBuildProgress >= 0f)
            return;

        // ムービーの間は vnavmesh 自身が待つ。邪魔をしない。
        if (PlayerHelper.IsInCutscene || PlayerHelper.IsBetweenAreas)
            return;

        // 作り始めてもいない状態が続いている。
        var stuck = (DateTime.UtcNow - _notReadySince.Value).TotalSeconds;
        if (stuck < StartGraceSeconds)
            return;

        if (_rebuildRequests >= MaxRebuildRequests)
            return;

        if (!EzThrottler.Throttle("AutoTreasure.NavRebuild", 15000))
            return;

        _rebuildRequests++;

        // 自動読み込みが切られていたら、戻す。
        // これが切られていると、エリアが変わっても永久に作られない。
        if (VNavmesh.IsAutoLoad == false)
        {
            VNavmesh.SetAutoLoad(true);
            Svc.Log.Information("[AutoTreasure] vnavmesh の自動読み込みが切れていたので入れ直しました。");
        }

        VNavmesh.Reload();
        Svc.Log.Information(
            $"[AutoTreasure] 地形が {stuck:F0} 秒たっても作られないので、"
            + $"作り直しを頼みました（{_rebuildRequests} 回目）。");
    }

    /// <summary>作り始めるまで待つ時間。すぐに頼むと、始まりかけを邪魔する。</summary>
    private const double StartGraceSeconds = 5.0;
}
