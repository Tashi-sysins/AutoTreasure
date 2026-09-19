using AutoTreasure.Helpers;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTreasure.Logic;

/// <summary>
/// ゲームの今の様子を、必要なところだけ切り取って読む。
///
/// 自動化を組むには「何をどう見分けるか」を決める必要がある。
/// そのために、手で遊んでいる間の様子を残しておく。
/// ここは、その材料を集める場所。
/// </summary>
internal static unsafe class GameSnapshot
{
    /// <summary>古ぼけた地図S5（未解読）のアイテムID。ゲームのデータから実測した値。</summary>
    internal const uint MapS5ItemId = 44349;

    /// <summary>古ぼけた地図S5を解読したときに手に入るキーアイテムのID。</summary>
    internal const uint MapS5KeyItemId = 2003704;

    // ---- 持ち物 -------------------------------------------------------------

    /// <summary>
    /// 持っている数を数える。
    /// 読めないときは -1 を返す（0 と区別するため）。
    /// </summary>
    internal static int CountItem(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return -1;

            return inventory->GetInventoryItemCount(itemId);
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>未解読の古ぼけた地図S5を何枚持っているか。</summary>
    internal static int CountMapS5() => CountItem(MapS5ItemId);

    /// <summary>
    /// 解読済みの地図を持っているか。
    ///
    /// 解読すると、未解読の地図が消えて代わりにキーアイテムが入る。
    /// これが「今どの地図を追っているか」の目印になる。
    /// </summary>
    internal static int CountDecodedMap() => CountKeyItem(MapS5KeyItemId);

    /// <summary>
    /// キーアイテム（イベントアイテム欄）の数を数える。
    ///
    /// 「宝の地図S5」は通常の鞄ではなくイベントアイテム欄に入る。
    /// GetInventoryItemCount は普通の鞄を見るため、ここを別に読む必要がある。
    /// 読めないときは -1（0 と区別する）。
    /// </summary>
    internal static int CountKeyItem(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return -1;

            var container = inventory->GetInventoryContainer(InventoryType.KeyItems);
            if (container == null || !container->IsLoaded)
                return -1;

            var count = 0;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId)
                    count += (int)slot->Quantity;
            }

            return count;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 解読済みの地図を持っているか。
    ///
    /// 持っているなら、その地図を追っている最中。新しく使ってはいけない。
    /// 読めなかった場合は false ではなく null を返し、
    /// 呼ぶ側が「分からないなら使わない」を選べるようにする。
    /// </summary>
    internal static bool? HasDecodedMap()
    {
        var count = CountDecodedMap();
        return count < 0 ? null : count > 0;
    }

    // ---- 表示中のUI ---------------------------------------------------------

    /// <summary>
    /// 今画面に出ているウィンドウの名前を全部返す。
    ///
    /// 自動化では「知らないウィンドウが出たら何もしない」のが安全。
    /// そのためには、どんなウィンドウが出るのかを先に知っておく必要がある。
    ///
    /// 宝箱を開けたとき、魔紋に入るとき、扉を選ぶとき——
    /// それぞれで何が出るかを記録しておけば、後で見分けられる。
    /// </summary>
    internal static List<string> GetVisibleAddons()
    {
        var result = new List<string>();

        try
        {
            var module = RaptureAtkModule.Instance();
            if (module == null)
                return result;

            var units = module->RaptureAtkUnitManager.AtkUnitManager.AllLoadedUnitsList;

            for (var i = 0; i < units.Count; i++)
            {
                var unit = units.Entries[i].Value;
                if (unit == null || !unit->IsVisible)
                    continue;

                var name = unit->NameString;
                if (!string.IsNullOrEmpty(name))
                    result.Add(name);
            }
        }
        catch
        {
            // 読めない場面（ログイン前など）は空で返す。
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    // ---- 今いる場所 ---------------------------------------------------------

    /// <summary>今いるエリアの詳しい情報。</summary>
    internal readonly record struct TerritoryInfo(
        uint TerritoryType,
        string PlaceName,
        uint ContentFinderConditionId,
        string ContentName,
        uint ContentType,
        uint MapId);

    /// <summary>今いるエリアについて、分かることをまとめて返す。</summary>
    internal static TerritoryInfo GetTerritoryInfo()
    {
        var territoryId = PlayerHelper.TerritoryType;

        try
        {
            var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                              .GetRowOrDefault(territoryId);
            if (row == null)
                return new TerritoryInfo(territoryId, "", 0, "", 0, 0);

            var place = row.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            var mapId = row.Value.Map.RowId;

            var cfc = row.Value.ContentFinderCondition.ValueNullable;
            if (cfc == null)
                return new TerritoryInfo(territoryId, place, 0, "", 0, mapId);

            return new TerritoryInfo(
                territoryId,
                place,
                cfc.Value.RowId,
                cfc.Value.Name.ExtractText(),
                cfc.Value.ContentType.RowId,
                mapId);
        }
        catch
        {
            return new TerritoryInfo(territoryId, "", 0, "", 0, 0);
        }
    }

    // ---- ターゲット ---------------------------------------------------------

    /// <summary>
    /// 今ターゲットしているものの説明。
    /// 手で操作しているときに「何を狙って触ったか」が分かる。
    /// </summary>
    internal static string DescribeTarget()
    {
        try
        {
            var target = Svc.Targets.Target;
            if (target == null)
                return "（なし）";

            return $"「{target.Name.TextValue}」 DataId {target.BaseId} "
                 + $"Kind {target.ObjectKind} 距離 {ObjectHelper.DistanceToPlayer(target):F1} m";
        }
        catch
        {
            return "（読めません）";
        }
    }

    // ---- パーティ -----------------------------------------------------------

    /// <summary>パーティの人数。</summary>
    internal static int PartyCount
    {
        get
        {
            try { return Svc.Party.Length; }
            catch { return 0; }
        }
    }
}
