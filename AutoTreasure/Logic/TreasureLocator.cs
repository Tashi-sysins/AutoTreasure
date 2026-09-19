using AutoTreasure.Helpers;
using AutoTreasure.IPC;
using ECommons.DalamudServices;
using System;
using System.Numerics;

namespace AutoTreasure.Logic;

/// <summary>確定した宝の場所。</summary>
internal readonly record struct TreasureTarget(
    uint TerritoryType,
    Vector3 World,
    string PlaceName,
    uint Rank,
    ushort SubRow)
{
    public override string ToString()
        => $"{PlaceName} ({World.X:F1}, {World.Y:F1}, {World.Z:F1})";
}

/// <summary>
/// 宝がどこにあるかを突き止める。
///
/// 手がかりはマップに立つフラグだが、それをそのまま目的地にはしない。
/// フラグの位置で「候補地のうちどれか」を絞り込み、実際の座標は
/// ゲームのデータから取る。こうする理由は3つ。
///
///   ・フラグには高さの情報が無い
///   ・フラグは1つしか置けず、他のプラグインが立てたものと区別できない
///   ・候補地は数が限られていて十分に離れているので、
///     おおよその位置さえ分かれば取り違えない
///
/// 結果として、フラグが多少ずれていても正確な座標へ向かえる。
/// </summary>
internal static class TreasureLocator
{
    /// <summary>
    /// 今わかっている手がかりから、宝の場所を突き止める。
    /// </summary>
    /// <param name="rank">
    /// 地図の種類。0 を渡すと、エリアと位置だけで候補地を探す
    /// （どの地図かが分からなくても、候補地の一覧から当てられる）。
    /// </param>
    /// <returns>突き止められたら位置。無理なら null。</returns>
    internal static TreasureTarget? Locate(uint rank = 0)
    {
        var flag = MapFlagReader.Read();
        if (flag == null)
            return null;

        // 地図の種類が分かっているなら、その候補地から探す。
        if (rank != 0)
        {
            var spot = TreasureSpotTable.FindNearest(
                rank,
                flag.Value.ApproximatePosition,
                flag.Value.TerritoryType);

            if (spot != null)
                return ToTarget(spot.Value);

            // 種類を指定したのに見つからない＝その種類の旗ではない。
            //
            // ここで総当たりに落とすと、前の周回で残っていた別の地図の旗を
            // 目的地として読んでしまう。実際に Rank 30 の旗を拾ったことがある。
            // 指定があったなら、見つからないことを答えとして返す。
            return null;
        }

        // 種類が分からないときは、手元の候補地をひと通り当たる。
        // 古ぼけた地図の種類はそう多くないので、総当たりでも負担にならない。
        for (uint r = 1; r <= 40; r++)
        {
            var spots = TreasureSpotTable.GetSpots(r);
            if (spots.Count == 0)
                continue;

            var spot = TreasureSpotTable.FindNearest(
                r,
                flag.Value.ApproximatePosition,
                flag.Value.TerritoryType,
                maxDistance: 30f);   // 総当たりのときは厳しめに見る

            if (spot != null)
                return ToTarget(spot.Value);
        }

        Svc.Log.Information(
            $"フラグは見つかりましたが、候補地と結びつきませんでした。" +
            $"（エリア {flag.Value.TerritoryType} / 位置 {flag.Value.X:F1}, {flag.Value.Z:F1}）");

        return null;
    }

    private static TreasureTarget ToTarget(TreasureSpotInfo spot)
        => new(spot.TerritoryType, spot.World, spot.PlaceName, spot.Rank, spot.SubRow);

    /// <summary>
    /// 向かう先として使える座標に整える。
    ///
    /// データに入っている高さは地表とずれていることがある。
    /// そのままだと空中や地面の下を指してしまい、経路が引けない。
    /// 実際の地面に落とし込んでから使う。
    ///
    /// 今いるエリアが違う場合は落とし込めない（そのエリアの地形が分からないため）ので、
    /// テレポで移動したあとに呼び直すこと。
    /// </summary>
    internal static Vector3 SnapToGround(Vector3 world)
    {
        if (!VNavmesh.NavIsReady)
            return world;

        try
        {
            // 少し上から下に向かって地面を探す。
            var above = world with { Y = world.Y + 20f };
            var floor = VNavmesh.PointOnFloor(above, false, 10f);
            return floor ?? world;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "地面の高さを求められませんでした。データの高さをそのまま使います。");
            return world;
        }
    }

    /// <summary>
    /// その座標までたどり着けそうか、あらかじめ調べる。
    /// たどり着けない場所へ何度も経路を引こうとして時間を無駄にしないために使う。
    /// </summary>
    internal static bool IsReachable(Vector3 world)
    {
        if (!VNavmesh.NavIsReady)
            return false;

        try
        {
            return VNavmesh.IsPointOnMesh(world, 20f, true);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// たどり着ける場所を、目的地のまわりから探す。
    ///
    /// 宝の座標が崖の中や水中を指していると、そこへは行けない。
    /// 同じ場所へ何度も経路を引いても結果は変わらないので、
    /// 少しずつ範囲を広げて、行ける場所を探す。
    ///
    /// 進めなくなった回数を渡すと、それに応じて探す範囲が広がる。
    /// </summary>
    /// <param name="world">本来の目的地。</param>
    /// <param name="attempt">何度目の試みか（0 から数える）。</param>
    /// <returns>行けそうな場所。見つからなければ元の座標。</returns>
    internal static Vector3 FindReachableNear(Vector3 world, int attempt)
    {
        if (attempt <= 0 || !VNavmesh.NavIsReady)
            return world;

        // 回数に応じて広げる。いきなり遠くを探すと、
        // まったく別の場所へ向かってしまう。
        var radius = attempt switch
        {
            1 => 5f,
            2 => 8f,
            3 => 12f,
            _ => Math.Min(8f + attempt * 3f, 25f),
        };

        // 8方向を順に当たる。
        for (var i = 0; i < 8; i++)
        {
            var angle = MathF.PI * 2f * i / 8f;
            var candidate = world with
            {
                X = world.X + MathF.Cos(angle) * radius,
                Z = world.Z + MathF.Sin(angle) * radius,
            };

            var grounded = SnapToGround(candidate);
            if (IsReachable(grounded))
                return grounded;
        }

        return world;
    }
}
