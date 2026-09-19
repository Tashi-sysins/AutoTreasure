using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Text;

namespace AutoTreasure.Logic;

/// <summary>
/// ウィンドウの中身を丸ごと書き出す。
///
/// 「強欲の罠」のようなミニゲームを自動で操作するには、
///   ・今いくつの数字が出ているか
///   ・ボタンがどこにあるか
///   ・どれを押せば何が起きるか
/// を知る必要がある。これは外から見ているだけでは分からない。
///
/// そこで、ウィンドウの中にある文字や部品を全部並べて残す。
/// あとから読めば、どこを見れば数字が分かるかを特定できる。
///
/// 一度分かってしまえば二度と要らない処理だが、
/// 分かるまでは、これが無いと手が出せない。
/// </summary>
internal static unsafe class AddonDump
{
    /// <summary>
    /// ウィンドウの中身を、読める形の文字列にする。
    ///
    /// 部品は入れ子になっているので、たどれるだけたどる。
    /// 深すぎると量が増えるので、ある程度で切る。
    /// </summary>
    /// <summary>
    /// 書き出すノードの上限。
    ///
    /// 200 だと「強欲の罠」の画面が途中で切れ、
    /// 肝心の数字があるかもしれない部分まで届かなかった。
    /// 多少長くてもファイルに書くだけなので、上限を上げる。
    /// </summary>
    internal static string Describe(string addonName, int maxNodes = 1000)
    {
        var sb = new StringBuilder(1024);

        try
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(addonName, out var addon)
                || addon == null)
            {
                return $"（{addonName} は見つかりませんでした）";
            }

            sb.AppendLine($"ウィンドウ「{addonName}」");
            sb.AppendLine($"  表示中   : {addon->IsVisible}");
            sb.AppendLine($"  位置     : X {addon->X}  Y {addon->Y}");
            sb.AppendLine($"  部品の数 : {addon->UldManager.NodeListCount}");
            sb.AppendLine("  ── 中身 ──");

            var count = 0;
            for (var i = 0; i < addon->UldManager.NodeListCount && count < maxNodes; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node == null)
                    continue;

                DescribeNode(sb, node, depth: 1, ref count, maxNodes);
            }

            if (count >= maxNodes)
                sb.AppendLine($"  （{maxNodes} 個まで。これ以上は省略）");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  （読み取りに失敗: {ex.Message}）");
        }

        return sb.ToString();
    }

    private static void DescribeNode(StringBuilder sb, AtkResNode* node, int depth, ref int count, int maxNodes)
    {
        if (node == null || count >= maxNodes)
            return;

        count++;

        var indent = new string(' ', depth * 2 + 2);

        // 文字が入っている部品なら、その中身が一番の手がかりになる。
        if (node->Type == NodeType.Text)
        {
            var textNode = (AtkTextNode*)node;
            var text = textNode->NodeText.ToString();

            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine($"{indent}[文字] Id {node->NodeId}  「{text}」"
                            + $"  表示 {node->IsVisible()}");
            }
            return;
        }

        // 部品のまとまり。中をたどる。
        if ((int)node->Type >= 1000)
        {
            var component = (AtkComponentNode*)node;
            var info = component->Component;
            if (info == null)
                return;

            sb.AppendLine($"{indent}[部品] Id {node->NodeId}  種類 {node->Type}"
                        + $"  表示 {node->IsVisible()}");

            var uld = info->UldManager;
            for (var i = 0; i < uld.NodeListCount && count < maxNodes; i++)
            {
                var child = uld.NodeList[i];
                if (child != null)
                    DescribeNode(sb, child, depth + 1, ref count, maxNodes);
            }
            return;
        }

        // 画像などは、あることだけ分かれば十分。
        if (node->IsVisible())
        {
            sb.AppendLine($"{indent}[その他] Id {node->NodeId}  種類 {node->Type}");
        }
    }

    /// <summary>
    /// ウィンドウの中にある文字だけを、順番に取り出す。
    ///
    /// 数字を読みたいときは、まずこれで見当をつける。
    /// </summary>
    internal static List<string> ExtractTexts(string addonName)
    {
        var result = new List<string>();

        try
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(addonName, out var addon)
                || addon == null)
                return result;

            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node == null)
                    continue;

                CollectTexts(node, result, depth: 0);
            }
        }
        catch
        {
            // 読めなくても困らない。空で返す。
        }

        return result;
    }

    private static void CollectTexts(AtkResNode* node, List<string> result, int depth)
    {
        if (node == null || depth > 8 || result.Count > 100)
            return;

        if (node->Type == NodeType.Text)
        {
            var text = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                result.Add($"[{node->NodeId}] {text}");
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
                    CollectTexts(child, result, depth + 1);
            }
        }
    }
}
