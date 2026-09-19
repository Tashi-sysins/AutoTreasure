using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Linq;
using System.Numerics;

namespace AutoTreasure.Logic;

/// <summary>
/// エーテライトへ飛ぶ。
///
/// 宝の位置が分かったら、そのエリアのエーテライトのうち
/// 宝に一番近いものを選んで飛ぶ。遠いところに降りると、
/// そのぶん飛行の時間が延びるため。
/// </summary>
internal static unsafe class TeleportHelper
{
    /// <summary>
    /// 行き先のエーテライト。
    /// </summary>
    internal readonly record struct Destination(uint AetheryteId, string Name, float Distance);

    /// <summary>
    /// 指定したエリアで、宝に一番近いエーテライトを探す。
    ///
    /// 登録済み（解放済み）のエーテライトだけが対象。
    /// 行ったことのない場所には飛べないため。
    /// </summary>
    internal static Destination? FindNearest(uint territoryType, Vector3 treasure)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            if (sheet == null)
                return null;

            Destination? best = null;

            foreach (var entry in Svc.AetheryteList)
            {
                if (entry == null)
                    continue;

                // 宿舎や個人宅は対象外。
                if (entry.IsApartment || entry.IsSharedHouse)
                    continue;

                if (entry.TerritoryId != territoryType)
                    continue;

                var row = sheet.GetRowOrDefault(entry.AetheryteId);
                if (row == null)
                    continue;

                var name = row.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? "";

                // エーテライトの座標は Map の座標系なので、
                // ここでは距離の比較にだけ使う。
                var pos = GetPosition(row.Value);

                // 座標が読めないエーテライトは、選びようがないので飛ばす。
                //
                // 以前は「とても遠い」とみなして候補に残していた。
                // その結果、どれも座標が読めないと、
                // 一番最初に見つけたものが選ばれてしまい、
                // まったく見当違いの場所へ飛んだ。
                if (pos == null)
                {
                    Svc.Log.Debug($"[AutoTreasure] {name} の座標が読めません。候補から外します。");
                    continue;
                }

                var distance = Vector3.Distance(pos.Value, treasure);

                if (best == null || distance < best.Value.Distance)
                    best = new Destination(entry.AetheryteId, name, distance);
            }

            return best;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "エーテライトを探せませんでした。");
            return null;
        }
    }

    /// <summary>
    /// エーテライトの、ワールド座標での位置。
    ///
    /// Aetheryte.Level[0] にワールド座標がそのまま入っている。
    /// ECommons もここを見ている（GameHelpers/Map.cs）。
    /// 自前でマップ座標から換算すると、係数を間違えて
    /// 見当違いのエーテライトを選ぶ危険がある。
    /// </summary>
    private static Vector3? GetPosition(Lumina.Excel.Sheets.Aetheryte row)
    {
        try
        {
            // まず Level から。ここにワールド座標がそのまま入っている。
            var level = row.Level[0].ValueNullable;
            if (level != null && (level.Value.X != 0 || level.Value.Z != 0))
                return new Vector3(level.Value.X, level.Value.Y, level.Value.Z);

            // Level が空のエーテライトもある。その場合は地図の印から拾う。
            // ECommons も同じ順で見ている（GameHelpers/Map.cs）。
            var markers = Svc.Data.GetSubrowExcelSheet<Lumina.Excel.Sheets.MapMarker>();
            if (markers == null)
                return null;

            foreach (var group in markers)
            {
                foreach (var marker in group)
                {
                    // 3 = エーテライトの印。
                    if (marker.DataType != 3 || marker.DataKey.RowId != row.RowId)
                        continue;

                    var map = row.Territory.ValueNullable?.Map.ValueNullable;
                    if (map == null)
                        return null;

                    // 地図の座標をワールド座標に直す。
                    var scale = map.Value.SizeFactor / 100f;
                    var x = ToWorld(marker.X, scale);
                    var z = ToWorld(marker.Y, scale);

                    return new Vector3(x, 0f, z);
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>地図の印の座標を、ワールド座標に直す。</summary>
    private static float ToWorld(int pixel, float scale)
        => (pixel - 1024f) / scale;

    /// <summary>
    /// 飛ぶ。
    ///
    /// 詠唱が始まったかどうかは呼ぶ側がエリアの変化で判断する。
    /// ここは指示を出すだけ。
    /// </summary>
    internal static bool Teleport(uint aetheryteId)
    {
        if (aetheryteId == 0)
            return false;

        // 詠唱中に重ねて撃つと詠唱が消える。
        if (Helpers.PlayerHelper.IsCasting)
            return true;

        if (!EzThrottler.Throttle("AutoTreasure.Teleport", 3000))
            return false;

        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null)
                return false;

            return telepo->Teleport(aetheryteId, 0);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "テレポできませんでした。");
            return false;
        }
    }
}
