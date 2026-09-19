using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Numerics;

namespace AutoTreasure.Logic;

/// <summary>ゲーム内のマップに立っているフラグ。</summary>
internal readonly record struct MapFlagInfo(
    uint TerritoryType,
    uint MapId,
    float X,
    float Z)
{
    /// <summary>平面の位置。高さは分からないので 0 を入れてある。</summary>
    public Vector3 ApproximatePosition => new(X, 0f, Z);
}

/// <summary>
/// マップに立っているフラグを読む。
///
/// 古ぼけた地図を解読すると、Globetrotter のようなプラグインが
/// 宝の場所にフラグを立てる。そのフラグの位置を、候補地を絞り込む手がかりに使う。
///
/// フラグの位置をそのまま目的地にはしない。理由は2つ。
///   ・他のプラグインが立てたフラグと区別できない（フラグは1つしか置けない）
///   ・フラグの位置は平面だけで、高さが分からない
///
/// 代わりに「どの候補地に一番近いか」を調べる手がかりとして使い、
/// 実際に向かう座標はゲームのデータから取る。こうすれば、
/// フラグが多少ずれていても、候補地の正確な座標へ向かえる。
/// </summary>
internal static unsafe class MapFlagReader
{
    /// <summary>
    /// 今立っているフラグを読む。無ければ null。
    ///
    /// エリアの情報も一緒に返すのが要点。
    /// vnavmesh の同種の機能はエリアを見ておらず、別エリアのフラグでも
    /// 「今いるエリアの同じ平面座標」を黙って返してしまう。
    /// </summary>
    internal static MapFlagInfo? Read()
    {
        try
        {
            var agent = AgentMap.Instance();
            if (agent == null || agent->FlagMarkerCount == 0)
                return null;

            var marker = agent->FlagMapMarkers[0];

            return new MapFlagInfo(
                marker.TerritoryId,
                marker.MapId,
                marker.XFloat,
                marker.YFloat);   // 平面の縦軸。3Dでいう Z にあたる
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "マップのフラグを読めませんでした。");
            return null;
        }
    }

    /// <summary>フラグが立っているか。</summary>
    internal static bool HasFlag()
    {
        try
        {
            var agent = AgentMap.Instance();
            return agent != null && agent->FlagMarkerCount > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// フラグを消す。
    ///
    /// 読む前に消しておくと、「今から立つフラグ」だけを拾える。
    /// 他のプラグインが以前に立てた古いフラグを宝の位置と取り違えずに済む。
    /// </summary>
    internal static void Clear()
    {
        try
        {
            var agent = AgentMap.Instance();
            if (agent != null)
                agent->FlagMarkerCount = 0;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "マップのフラグを消せませんでした。");
        }
    }
}
