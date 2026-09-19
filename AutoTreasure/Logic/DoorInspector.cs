using AutoTreasure.Helpers;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AutoTreasure.Logic;

/// <summary>扉の位置関係。マップ上でどちら側にあるか。</summary>
internal enum DoorSide
{
    Unknown,
    Left,
    Right,
}

/// <summary>扉1つ分の見立て。</summary>
internal readonly record struct DoorInfo(
    IGameObject Object,
    DoorSide Side,
    float DistanceToPlayer,
    byte EventState,
    uint RenderFlags,
    float VfxScale)
{
    /// <summary>
    /// 光っている（＝正解の合図が出ている）とみられるか。
    ///
    /// 低い確率で、正解の扉に目立つ演出が付く。
    /// それを見分けられれば、当てずっぽうで選ばずに済む。
    ///
    /// <b>ただし、何を見れば分かるのかは、まだ確かめられていない。</b>
    /// 記録から判明したら、ここを正しくする。
    /// </summary>
    public bool LooksHighlighted => VfxScale > 1.01f;
}

/// <summary>
/// 魔紋の中の扉を調べる。
///
/// 部屋には扉が2つ（左上・右上）あり、片方が正解。
/// 外すと魔紋から追い出される。
///
/// 低い確率で、正解の扉に目立つ演出が付くことがある。
/// それを拾えれば確実に選べるので、まずは見分けられるかを調べる。
/// </summary>
internal static unsafe class DoorInspector
{
    /// <summary>
    /// 見えている扉を調べて返す。
    /// 左右の並びで整えるので、前から順に「左」「右」となる。
    /// </summary>
    internal static List<DoorInfo> Inspect()
    {
        var objects = ObjectHelper.GetEventObjects();
        if (objects.Count == 0)
            return [];

        var result = new List<DoorInfo>(objects.Count);

        foreach (var obj in objects)
        {
            result.Add(new DoorInfo(
                obj,
                DoorSide.Unknown,
                ObjectHelper.DistanceToPlayer(obj),
                ReadEventState(obj),
                ReadRenderFlags(obj),
                ReadVfxScale(obj)));
        }

        // 左右を決める。扉が2つのときだけ意味がある。
        if (result.Count == 2)
        {
            var a = result[0];
            var b = result[1];

            // X が小さい方を左とする。
            // マップの向きと一致するかは、記録で確かめる。
            var aIsLeft = a.Object.Position.X <= b.Object.Position.X;

            result[0] = a with { Side = aIsLeft ? DoorSide.Left : DoorSide.Right };
            result[1] = b with { Side = aIsLeft ? DoorSide.Right : DoorSide.Left };

            // 左が先に来るよう並べ替える。
            result.Sort((x, y) => x.Side.CompareTo(y.Side));
        }

        return result;
    }

    /// <summary>
    /// 光っているとみられる扉を返す。無ければ null。
    ///
    /// 見分けがつかないうちは、いつも null を返す。
    /// 当てずっぽうで「これが光っている」と決めつけるより、
    /// 分からないと答える方が安全。
    /// </summary>
    internal static DoorInfo? FindHighlighted(List<DoorInfo> doors)
    {
        var highlighted = doors.Where(d => d.LooksHighlighted).ToList();

        // 1つだけ光っているなら、それが正解とみてよい。
        // 全部光っている・全部光っていない場合は、見分けになっていない。
        return highlighted.Count == 1 ? highlighted[0] : null;
    }

    // ---- ゲーム内部の値を読む -----------------------------------------------

    /// <summary>
    /// オブジェクトの状態を表す値。
    /// 扉が「開いた」「光っている」などで変わる可能性がある。
    /// </summary>
    private static byte ReadEventState(IGameObject obj)
    {
        try
        {
            var ptr = (CSGameObject*)obj.Address;
            return ptr == null ? (byte)0 : ptr->EventState;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 描画に関する値。見た目の変化がここに出ることがある。
    /// </summary>
    private static uint ReadRenderFlags(IGameObject obj)
    {
        try
        {
            var ptr = (CSGameObject*)obj.Address;
            return ptr == null ? 0u : (uint)ptr->RenderFlags;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 光の演出が付いているか。
    ///
    /// 演出は VFX として付くことが多い。
    /// ただし、この見方で本当に拾えるかは確かめられていない。
    /// </summary>
    private static float ReadVfxScale(IGameObject obj)
    {
        try
        {
            var ptr = (CSGameObject*)obj.Address;
            return ptr == null ? 0f : ptr->VfxScale;
        }
        catch
        {
            return 0f;
        }
    }

    // ---- 記録用 -------------------------------------------------------------

    /// <summary>
    /// 扉の様子を、記録に残す形の文字列にする。
    ///
    /// 何を見れば正解が分かるのかを突き止めるための材料。
    /// 分かりそうな値を、まとめて残しておく。
    /// </summary>
    internal static List<string> Describe(List<DoorInfo> doors)
    {
        var lines = new List<string>();

        if (doors.Count == 0)
        {
            lines.Add("  （扉は見えません）");
            return lines;
        }

        foreach (var d in doors)
        {
            var side = d.Side switch
            {
                DoorSide.Left  => "左",
                DoorSide.Right => "右",
                _              => "？",
            };

            lines.Add($"  [{side}] 「{d.Object.Name.TextValue}」 DataId {d.Object.BaseId}");
            lines.Add($"       位置     : {RunLog.Format(d.Object.Position)}");
            lines.Add($"       距離     : {d.DistanceToPlayer:F1} m");
            lines.Add($"       触れるか : {d.Object.IsTargetable}");
            lines.Add($"       EventState  : {d.EventState}");
            lines.Add($"       RenderFlags : 0x{d.RenderFlags:X}");
            lines.Add($"       VfxScale    : {d.VfxScale:F3}");
        }

        var highlighted = FindHighlighted(doors);
        lines.Add(highlighted != null
            ? $"  → 光っているとみられるのは {(highlighted.Value.Side == DoorSide.Left ? "左" : "右")} の扉"
            : "  → 光っている扉は見分けられませんでした");

        return lines;
    }

    /// <summary>
    /// 扉の様子が前回から変わったかを見分けるための文字列。
    /// これが変われば「何かが起きた」ことになる。
    /// </summary>
    internal static string Fingerprint(List<DoorInfo> doors)
        => string.Join(";", doors.Select(d =>
               $"{d.Object.BaseId}/{d.Object.IsTargetable}/{d.EventState}/{d.RenderFlags:X}/{d.VfxScale:F2}"));
}
