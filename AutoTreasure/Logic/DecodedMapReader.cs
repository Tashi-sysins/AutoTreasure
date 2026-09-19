using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;

namespace AutoTreasure.Logic;

/// <summary>
/// 今持っている、解読済みの地図がどこを指しているかを読む。
///
/// ゲーム自身が答えを持っている。
/// 「宝の地図S5」にカーソルを合わせると場所が出るのは、
/// この値を読んで文字に組み立てているため。
///
/// 通信を横取りする必要はない。いつでも聞ける。
/// Globetrotter が再起動後に場所を忘れるのは、
/// 通信を覚えておく作りだからで、ゲームの制約ではなかった。
///
/// 出典: FFXIVClientStructs/FFXIV/Client/Game/EventItemManager.cs
/// 導入済みの FFXIVClientStructs.dll にも入っていることを確認済み。
/// </summary>
internal static unsafe class DecodedMapReader
{
    /// <summary>解読済みの地図の場所。持っていなければ null。</summary>
    internal readonly record struct DecodedMap(uint Rank, ushort SubRow);

    /// <summary>
    /// 今持っている解読済み地図を読む。
    ///
    /// 持っていないときは null。
    /// 読めなかったときも null（区別しない。どちらも「使えない」ため）。
    /// </summary>
    internal static DecodedMap? Read()
    {
        try
        {
            var manager = EventItemManager.Instance();
            if (manager == null)
                return null;

            var subRow = manager->GetTreasureSpotSubKey();
            var rank = manager->GetTreasureHuntRank();

            // 地図を持っていないときは、どちらも 0 になる。
            if (rank == 0)
                return null;

            return new DecodedMap(rank, subRow);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "解読済みの地図を読めませんでした。");
            return null;
        }
    }

    /// <summary>
    /// 今持っている解読済み地図から、目的地を組み立てる。
    ///
    /// 旗を見る方法と違い、別の地図の旗や、他のプラグインが立てた旗と
    /// 取り違える心配がない。
    /// </summary>
    internal static TreasureTarget? Locate()
    {
        var map = Read();
        if (map == null)
            return null;

        // S5 以外の地図を持っているなら、こちらの仕事ではない。
        if (map.Value.Rank != TreasureSpotTable.S5Rank)
        {
            Svc.Log.Debug($"[AutoTreasure] 持っている地図は Rank {map.Value.Rank}。S5 ではありません。");
            return null;
        }

        var spot = TreasureSpotTable.GetExact(map.Value.Rank, map.Value.SubRow);
        if (spot == null)
            return null;

        return new TreasureTarget(
            spot.Value.TerritoryType,
            spot.Value.World,
            spot.Value.PlaceName,
            spot.Value.Rank,
            spot.Value.SubRow);
    }
}
