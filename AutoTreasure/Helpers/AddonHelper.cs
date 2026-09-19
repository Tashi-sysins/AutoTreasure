using ECommons;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace AutoTreasure.Helpers;

/// <summary>
/// 画面に出てくる確認ウィンドウを片付ける。
///
/// 宝箱を開ける、魔紋に入る、扉をくぐる——どれも「よろしいですか？」が挟まる。
/// これを自動で進めないと、そこで止まってしまう。
///
/// ウィンドウのボタンを押す処理は ECommons に用意があるのでそれを使う。
/// 自前で組むと、ボタンの並びが変わるたびに壊れる。
/// </summary>
internal static unsafe class AddonHelper
{
    /// <summary>そのウィンドウが開いていて、操作できる状態か。</summary>
    internal static bool IsAddonReady(string name)
    {
        try
        {
            return GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)
                && GenericHelpers.IsAddonReady(addon);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 「はい／いいえ」の確認に答える。
    ///
    /// 連打すると意図しない選択まで押してしまうので、間隔をあける。
    /// </summary>
    /// <returns>実際に押したら true。</returns>
    internal static bool ClickSelectYesno(bool yes = true)
    {
        if (!EzThrottler.Throttle("AutoTreasure.SelectYesno", 500))
            return false;

        try
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)
                || !GenericHelpers.IsAddonReady(addon))
                return false;

            var master = new AddonMaster.SelectYesno(addon);
            if (yes)
                master.Yes();
            else
                master.No();

            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "確認ウィンドウの操作に失敗しました。");
            return false;
        }
    }

    /// <summary>
    /// 選択肢のウィンドウで、指定した番号を選ぶ。
    /// </summary>
    internal static bool ClickSelectString(int index)
    {
        if (!EzThrottler.Throttle("AutoTreasure.SelectString", 500))
            return false;

        try
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon)
                || !GenericHelpers.IsAddonReady(addon))
                return false;

            var master = new AddonMaster.SelectString(addon);
            if (index < 0 || index >= master.Entries.Length)
                return false;

            master.Entries[index].Select();
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "選択ウィンドウの操作に失敗しました。");
            return false;
        }
    }

    /// <summary>
    /// 右クリックの一覧から、指定した文字を含む項目を選ぶ。
    ///
    /// 地図の解読は「使う」ではなく、この一覧から選ぶ形。
    /// 位置は持ち物の状態で変わるので、番号ではなく文字で探す。
    /// </summary>
    /// <returns>選べたら true。</returns>
    internal static bool ClickContextMenu(string contains)
    {
        if (!EzThrottler.Throttle("AutoTreasure.ContextMenu", 500))
            return false;

        try
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon)
                || !GenericHelpers.IsAddonReady(addon))
                return false;

            var master = new AddonMaster.ContextMenu(addon);

            foreach (var entry in master.Entries)
            {
                if (!entry.Text.Contains(contains, StringComparison.Ordinal))
                    continue;

                entry.Select();
                Svc.Log.Information($"[AutoTreasure] 一覧から「{entry.Text}」を選びました。");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "一覧の操作に失敗しました。");
            return false;
        }
    }

    /// <summary>
    /// 会話ウィンドウを進める。
    /// </summary>
    internal static bool ClickTalk()
    {
        if (!EzThrottler.Throttle("AutoTreasure.Talk", 200))
            return false;

        try
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var addon)
                || !GenericHelpers.IsAddonReady(addon))
                return false;

            new AddonMaster.Talk(addon).Click();
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "会話ウィンドウの操作に失敗しました。");
            return false;
        }
    }

    /// <summary>
    /// 出ている確認・会話ウィンドウをまとめて片付ける。
    ///
    /// 毎フレーム呼んでおけば、何が出ても先へ進む。
    /// どれが出るか事前に分からない場面で使う。
    /// </summary>
    /// <returns>何か1つでも処理したら true。</returns>
    internal static bool DismissCommonDialogs()
    {
        if (ClickSelectYesno())
            return true;
        if (ClickTalk())
            return true;
        return false;
    }
}
