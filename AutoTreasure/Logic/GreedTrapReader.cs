using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Text;

namespace AutoTreasure.Logic;

/// <summary>
/// 「強欲の罠」の画面から、今出ている数字を読む。
///
/// <b>数字は文字ではなく、絵で描かれている。</b>
/// トランプの絵札のように、1〜9 がそれぞれ別の絵になっている。
/// そのため、ウィンドウの文字を全部集めても数字は出てこない。
/// 実際、90秒ぶんの文字を書き出しても報酬額と残り秒数しか無かった
/// （2026-09-18 実測。1〜9 はどこにも現れなかった）。
///
/// 絵は <see cref="NodeType.Image"/> のノードに描かれ、
/// 「どの絵を出すか」は部品番号（PartId）で決まる。
/// つまり PartId を読めば、絵を見なくても数字が分かる。
///
/// ただし「PartId のいくつが数字のいくつに当たるか」は
/// 実測しないと分からない。そこで:
///   1. まず画面じゅうの Image ノードの PartId を書き出す
///   2. 人が見た数字と突き合わせて対応表を作る
///   3. 対応表ができたら、そこから数字を返す
/// という順で進める。ここは 1 と 3 を受け持つ。
/// </summary>
internal static unsafe class GreedTrapReader
{
    /// <summary>「強欲の罠」の画面の名前。</summary>
    internal const string AddonName = "TreasureHighLow";

    /// <summary>
    /// 画面に出ている数字（1〜9）。読めなければ null。
    ///
    /// 対応表が未確定のうちは null を返す。
    /// 当てずっぽうで数字を返すと、その数字を信じて賭けてしまう。
    /// 読めないことが分かっている方が、まだ安全。
    /// </summary>
    internal static int? ReadNumber()
    {
        var addon = GetAddon();
        if (addon == null)
            return null;

        foreach (var (partId, textureId) in CollectImageParts(addon))
        {
            if (CardNumbers.TryGetValue((textureId, partId), out var number))
                return number;
        }

        return null;
    }

    /// <summary>
    /// 画面の Image ノードを全部書き出す。
    ///
    /// どの番号が数字のいくつに当たるかを突き止めるために使う。
    /// 人が画面で見た数字と、この一覧を突き合わせる。
    /// </summary>
    internal static string DescribeImageParts()
    {
        var addon = GetAddon();
        if (addon == null)
            return "「強欲の罠」の画面は出ていません";

        var sb = new StringBuilder();
        sb.AppendLine("──────────────────────────────────────────");
        sb.AppendLine("  「強欲の罠」の絵（数字はここに描かれている）");
        sb.AppendLine("──────────────────────────────────────────");

        var found = 0;

        foreach (var (partId, textureId) in CollectImageParts(addon))
        {
            found++;
            var known = CardNumbers.TryGetValue((textureId, partId), out var number)
                ? $"  → 数字 {number}"
                : string.Empty;

            sb.AppendLine($"    絵 {found,2}: PartId {partId,3}  TextureId {textureId,6}{known}");
        }

        if (found == 0)
            sb.AppendLine("    絵が1つも見つかりませんでした");

        return sb.ToString();
    }

    /// <summary>
    /// 表に出ている（描画されている）Image ノードの番号を集める。
    ///
    /// 隠れているものまで拾うと、裏向きの手札や
    /// 使われていない絵まで混ざって数字を取り違える。
    /// </summary>
    private static List<(uint PartId, uint TextureId)> CollectImageParts(AtkUnitBase* addon)
    {
        var result = new List<(uint, uint)>();

        try
        {
            var uld = addon->UldManager;

            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var node = uld.NodeList[i];
                if (node != null)
                    Collect(node, result, depth: 0);
            }
        }
        catch
        {
            // 読めなくても困らない。集まった分だけ返す。
        }

        return result;
    }

    private static void Collect(AtkResNode* node, List<(uint, uint)> result, int depth)
    {
        if (node == null || depth > 8 || result.Count > 200)
            return;

        // 見えていないものは数えない。
        if (!node->IsVisible())
            return;

        if (node->Type == NodeType.Image)
        {
            var image = (AtkImageNode*)node;
            var partsList = image->PartsList;

            if (partsList != null && image->PartId < partsList->PartCount)
            {
                var part = partsList->Parts[image->PartId];
                var asset = part.UldAsset;

                uint textureId = 0;

                if (asset != null && asset->AtkTexture.TextureType == TextureType.Resource)
                {
                    var resource = asset->AtkTexture.Resource;
                    if (resource != null)
                        textureId = resource->IconId;
                }

                result.Add((image->PartId, textureId));
            }

            return;
        }

        if ((int)node->Type >= 1000)
        {
            var component = (AtkComponentNode*)node;
            var info = component->Component;
            if (info == null)
                return;

            var uld = info->UldManager;
            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var child = uld.NodeList[i];
                if (child != null)
                    Collect(child, result, depth + 1);
            }
        }
    }

    private static AtkUnitBase* GetAddon()
    {
        try
        {
            return GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var addon)
                && GenericHelpers.IsAddonReady(addon)
                ? addon
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 絵の番号と、それが表す数字の対応表。
    ///
    /// <b>まだ実測できていないので空にしてある。</b>
    /// 埋まるまで <see cref="ReadNumber"/> は null を返し、
    /// 賭けには進まない（確定させる方を選ぶ）。
    ///
    /// 埋め方:
    ///   罠が出たら記録に「絵 n: PartId x TextureId y」が並ぶ。
    ///   画面に見えている数字と突き合わせて、ここへ書き足す。
    ///   9種類そろえば、どの数字が出ても読めるようになる。
    /// </summary>
    private static readonly Dictionary<(uint TextureId, uint PartId), int> CardNumbers = new();
}
