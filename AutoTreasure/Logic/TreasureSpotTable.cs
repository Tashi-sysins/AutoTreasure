using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoTreasure.Logic;

/// <summary>宝の候補地1件。</summary>
internal readonly record struct TreasureSpotInfo(
    uint Rank,
    ushort SubRow,
    uint TerritoryType,
    uint MapId,
    string PlaceName,
    Vector3 World)
{
    public override string ToString()
        => $"{PlaceName} ({World.X:F1}, {World.Y:F1}, {World.Z:F1})";
}

/// <summary>
/// 宝の候補地の一覧。ゲームのデータから読み出す。
///
/// 古ぼけた地図は、地図の種類ごとに決まった候補地の中から1つが選ばれる。
/// 候補地そのものはゲーム内のデータに入っているので、こちらで持つ必要はない。
/// 拡張で候補地が増えても、データを読み直せばそのまま追随できる。
///
/// たとえば「古ぼけた地図S5」は 5つのエリアに 8か所ずつ、計 40か所。
/// 数が限られているため、おおよその位置さえ分かれば、どの候補地かを言い当てられる。
/// </summary>
internal static class TreasureSpotTable
{
    /// <summary>
    /// 古ぼけた地図S5 の種類。実測で確認した（TreasureHuntRank の 29 番）。
    ///
    /// これを指定せずに候補地を探すと、別の地図の旗まで拾ってしまう。
    /// 実際、前の周回で残っていた Rank 30（リビング・メモリー）の旗を
    /// S5 の目的地として読んでしまったことがある。
    /// </summary>
    internal const uint S5Rank = 29;

    // 読み込みは一度きり。ゲームのデータは実行中に変わらない。
    private static readonly Dictionary<uint, List<TreasureSpotInfo>> _byRank = [];

    // 解読済みのキーアイテムから、地図の種類を逆に引くための対応表。
    private static Dictionary<uint, uint>? _keyItemToRank;

    /// <summary>
    /// 解読済みキーアイテムの ID から、地図の種類（TreasureHuntRank）を引く。
    /// </summary>
    internal static bool TryGetRankByKeyItem(uint keyItemId, out uint rank)
    {
        _keyItemToRank ??= BuildKeyItemMap();
        return _keyItemToRank.TryGetValue(keyItemId, out rank);
    }

    private static Dictionary<uint, uint> BuildKeyItemMap()
    {
        var map = new Dictionary<uint, uint>();
        try
        {
            var sheet = Svc.Data.GetExcelSheet<TreasureHuntRank>();
            foreach (var row in sheet)
            {
                // 未解読の地図が無い行は、地図の種類として使われていない。
                if (row.ItemName.RowId == 0)
                    continue;

                var keyItemId = row.KeyItemName.RowId;
                if (keyItemId == 0)
                    continue;

                // 同じキーアイテムが複数の行から指されることがある。先に見つけた方を採る。
                map.TryAdd(keyItemId, row.RowId);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "地図の種類を読み込めませんでした。");
        }
        return map;
    }

    /// <summary>
    /// 指定した地図の種類の候補地を全部返す。
    /// </summary>
    internal static IReadOnlyList<TreasureSpotInfo> GetSpots(uint rank)
    {
        if (_byRank.TryGetValue(rank, out var cached))
            return cached;

        var list = new List<TreasureSpotInfo>();
        try
        {
            var sheet = Svc.Data.GetSubrowExcelSheet<TreasureSpot>();

            // 何件あるかは種類によって違う。取れなくなるまで順に読む。
            for (ushort sub = 0; sub < 256; sub++)
            {
                var spot = sheet.GetSubrowOrDefault(rank, sub);
                if (spot == null)
                    break;

                var level = spot.Value.Location.ValueNullable;
                if (level == null)
                    continue;

                var map = level.Value.Map.ValueNullable;
                if (map == null)
                    continue;

                var territory = map.Value.TerritoryType.ValueNullable;
                if (territory == null)
                    continue;

                var place = territory.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? "";

                list.Add(new TreasureSpotInfo(
                    rank,
                    sub,
                    territory.Value.RowId,
                    map.Value.RowId,
                    place,
                    new Vector3(level.Value.X, level.Value.Y, level.Value.Z)));
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"候補地を読み込めませんでした（地図の種類 {rank}）。");
        }

        _byRank[rank] = list;
        return list;
    }

    /// <summary>
    /// おおよその位置から、どの候補地かを言い当てる。
    ///
    /// 同じエリアの候補地どうしは十分に離れているため（S5 では最も近いもので約109ヤード）、
    /// 多少ずれた位置からでも取り違えない。
    ///
    /// エリアで絞ってから一番近いものを選ぶ。エリアを指定しない場合は全候補から選ぶ。
    /// </summary>
    /// <param name="rank">地図の種類。</param>
    /// <param name="approximate">おおよその位置（平面座標で見る）。</param>
    /// <param name="territoryType">エリア。0 なら絞り込まない。</param>
    /// <param name="maxDistance">これより遠ければ「該当なし」とする。</param>
    /// <summary>
    /// 種類と何番目かを指定して、候補地をそのまま引く。
    ///
    /// 旗の位置から一番近い候補地を探す方法と違い、取り違えようがない。
    /// ゲームが「今どの地図を持っているか」を答えてくれるので、
    /// それをそのまま使う。
    /// </summary>
    internal static TreasureSpotInfo? GetExact(uint rank, ushort subRow)
    {
        foreach (var spot in GetSpots(rank))
        {
            if (spot.SubRow == subRow)
                return spot;
        }

        return null;
    }

    internal static TreasureSpotInfo? FindNearest(
        uint rank,
        Vector3 approximate,
        uint territoryType = 0,
        float maxDistance = 60f)
    {
        var spots = GetSpots(rank);
        if (spots.Count == 0)
            return null;

        var candidates = territoryType != 0
            ? spots.Where(s => s.TerritoryType == territoryType)
            : spots;

        TreasureSpotInfo? best = null;
        var bestDistance = float.MaxValue;

        foreach (var spot in candidates)
        {
            // 高さは見ない。地図から得られる位置は平面のものだけで、
            // 高さは当てにならないため。
            var dx = spot.World.X - approximate.X;
            var dz = spot.World.Z - approximate.Z;
            var distance = MathF.Sqrt(dx * dx + dz * dz);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = spot;
            }
        }

        return bestDistance <= maxDistance ? best : null;
    }

    /// <summary>
    /// 読み込んだ内容を捨てる。ゲームの更新をまたいだときなどに使う。
    /// </summary>
    internal static void Clear()
    {
        _byRank.Clear();
        _keyItemToRank = null;
    }
}
