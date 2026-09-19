using Dalamud.Memory;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AutoTreasure.Logic;

/// <summary>
/// 画面の中身を、そのまま writing out する。
///
/// <b>なぜ要るのか。</b>
/// 「強欲の罠」の数字は、文字ではなく絵で描かれている。
/// そのため文字だけを集めても数字は出てこなかった
/// （2026-09-18・90秒ぶん集めても報酬額と残り秒数しか無かった）。
///
/// しかし「文字に無い」ことは「どこにも無い」ことを意味しない。
/// ゲームは絵を出す前に、どこかで数字を持っているはずで、
/// それは次のどれかにある可能性がある。
///
///   1. 部品（Node）… 絵の番号（PartId）として
///   2. AtkValues   … 画面に渡された値として
///   3. ArrayData   … 画面を作る元のデータとして
///
/// ここでは、その3つを丸ごとファイルに書き出す。
/// 1回でも罠が出れば、あとから落ち着いて調べられる。
///
/// <b>推測で決め打ちしない。</b>
/// 「この番号が数字だろう」と当たりを付けて実装すると、
/// 外れたときに間違った数字を信じて賭けることになる。
/// まず全部書き出し、人の目で突き合わせてから決める。
/// </summary>
internal static unsafe class UiDump
{
    /// <summary>
    /// 今の画面の中身を、ファイルに書き出す。
    /// </summary>
    /// <param name="folder">書き出し先のフォルダ。</param>
    /// <param name="label">ファイル名に付ける名前（「罠あり」など）。</param>
    /// <param name="addonNames">中身まで詳しく見る画面の名前。空なら出ている全部。</param>
    /// <returns>書き出したファイルの場所。失敗したら null。</returns>
    internal static string? Capture(string folder, string label, params string[] addonNames)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var safeLabel = string.Concat(label.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(folder, $"UI_{stamp}_{safeLabel}.txt");

            var sb = new StringBuilder();

            WriteHeader(sb, label);
            WriteVisibleAddons(sb);

            foreach (var name in addonNames)
                WriteAddonDetail(sb, name);

            WriteArrayData(sb);

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[AutoTreasure] 画面の中身を書き出せませんでした。");
            return null;
        }
    }

    private static void WriteHeader(StringBuilder sb, string label)
    {
        sb.AppendLine("══════════════════════════════════════════");
        sb.AppendLine($"  画面の中身（{label}）");
        sb.AppendLine($"  日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("══════════════════════════════════════════");
        sb.AppendLine();

        try
        {
            sb.AppendLine($"エリア      : {Helpers.PlayerHelper.TerritoryType}");
            sb.AppendLine($"自分の位置  : {Helpers.PlayerHelper.Position:F2}");
            sb.AppendLine($"戦闘中      : {Helpers.PlayerHelper.InCombat}");
        }
        catch
        {
            sb.AppendLine("（今いる場所を読めませんでした）");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// 今出ている画面の名前を、全部並べる。
    ///
    /// 罠が出る前と出た後で見比べると、増えた画面が分かる。
    /// それが罠の画面である可能性が高い。
    /// </summary>
    private static void WriteVisibleAddons(StringBuilder sb)
    {
        sb.AppendLine("──────────────────────────────────────────");
        sb.AppendLine("  今出ている画面");
        sb.AppendLine("──────────────────────────────────────────");

        try
        {
            var stage = AtkStage.Instance();
            if (stage == null)
            {
                sb.AppendLine("  （読めませんでした）");
                sb.AppendLine();
                return;
            }

            var list = stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;

            for (var i = 0; i < list.Count; i++)
            {
                var unit = list.Entries[i].Value;
                if (unit == null)
                    continue;

                var name = unit->NameString;
                if (string.IsNullOrEmpty(name))
                    continue;

                sb.AppendLine(
                    $"  {name,-32} 表示={unit->IsVisible,-5} "
                  + $"位置=({unit->X},{unit->Y}) 値の数={unit->AtkValuesCount}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  （読めませんでした: {ex.Message}）");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// ひとつの画面を、中身まで詳しく書き出す。
    ///
    /// 部品の木（Node Tree）と、画面に渡された値（AtkValues）の両方を出す。
    /// 数字がどちらにあるか分からないので、両方見る。
    /// </summary>
    private static void WriteAddonDetail(StringBuilder sb, string addonName)
    {
        sb.AppendLine("──────────────────────────────────────────");
        sb.AppendLine($"  画面「{addonName}」の中身");
        sb.AppendLine("──────────────────────────────────────────");

        try
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(addonName, out var addon)
                || addon == null)
            {
                sb.AppendLine("  （出ていません）");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"  表示     : {addon->IsVisible}");
            sb.AppendLine($"  位置     : ({addon->X}, {addon->Y})");
            sb.AppendLine($"  大きさ   : {addon->GetScaledWidth(true)} x {addon->GetScaledHeight(true)}");
            sb.AppendLine();

            WriteAtkValues(sb, addon);
            WriteNodes(sb, addon);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  （読めませんでした: {ex.Message}）");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// 画面に渡された値を並べる。
    ///
    /// ここに数字がそのまま入っていることがある。
    /// 絵で描かれていても、元の値は数のまま渡されている可能性がある。
    /// </summary>
    private static void WriteAtkValues(StringBuilder sb, AtkUnitBase* addon)
    {
        sb.AppendLine("  ── 画面に渡された値（AtkValues）──");

        var count = addon->AtkValuesCount;
        if (count <= 0 || addon->AtkValues == null)
        {
            sb.AppendLine("    （ありません）");
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var v = addon->AtkValues[i];

            var text = v.Type switch
            {
                AtkValueType.Int           => v.Int.ToString(CultureInfo.InvariantCulture),
                AtkValueType.UInt          => v.UInt.ToString(CultureInfo.InvariantCulture),
                AtkValueType.Bool          => v.Byte != 0 ? "true" : "false",
                AtkValueType.Float         => v.Float.ToString("F2", CultureInfo.InvariantCulture),
                AtkValueType.String
                or AtkValueType.ManagedString
                or AtkValueType.String8    => ReadString(v.String),
                _                          => "",
            };

            // 中身があるものだけを出す。空ばかり並ぶと読みにくい。
            if (v.Type == AtkValueType.Undefined)
                continue;

            sb.AppendLine($"    [{i,3}] {v.Type,-14} {text}");
        }
    }

    /// <summary>
    /// 部品の木を、絵の番号つきで書き出す。
    ///
    /// 文字（Text）だけでなく、絵（Image）の PartId も出す。
    /// 数字が絵で描かれている場合、PartId が数字に対応しているはず。
    /// </summary>
    private static void WriteNodes(StringBuilder sb, AtkUnitBase* addon)
    {
        sb.AppendLine();
        sb.AppendLine("  ── 部品の木（Node）──");
        sb.AppendLine("     ※ 数字が絵の場合、PartId が手がかりになる");

        var uld = addon->UldManager;

        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node != null)
                WriteNode(sb, node, depth: 2);
        }
    }

    private static void WriteNode(StringBuilder sb, AtkResNode* node, int depth)
    {
        if (node == null || depth > 12)
            return;

        var indent = new string(' ', depth * 2);
        var visible = node->IsVisible();

        if (node->Type == NodeType.Text)
        {
            var text = ((AtkTextNode*)node)->NodeText.ToString();
            sb.AppendLine($"{indent}[文字] Id {node->NodeId,3}  表示={visible,-5} 「{text}」");
            return;
        }

        if (node->Type == NodeType.Image)
        {
            var image = (AtkImageNode*)node;
            uint textureId = 0;

            var parts = image->PartsList;
            if (parts != null && image->PartId < parts->PartCount)
            {
                var asset = parts->Parts[image->PartId].UldAsset;
                if (asset != null && asset->AtkTexture.TextureType == TextureType.Resource)
                {
                    var res = asset->AtkTexture.Resource;
                    if (res != null)
                        textureId = res->IconId;
                }
            }

            sb.AppendLine(
                $"{indent}[絵]   Id {node->NodeId,3}  表示={visible,-5} "
              + $"PartId={image->PartId,3} TextureId={textureId}");
            return;
        }

        if ((int)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null)
                return;

            sb.AppendLine($"{indent}[部品] Id {node->NodeId,3}  表示={visible,-5} 種類={(int)node->Type}");

            var uld = component->UldManager;
            for (var i = 0; i < uld.NodeListCount; i++)
            {
                var child = uld.NodeList[i];
                if (child != null)
                    WriteNode(sb, child, depth + 1);
            }

            return;
        }

        // それ以外は種類だけ。数が多いので中身は出さない。
        sb.AppendLine($"{indent}[他]   Id {node->NodeId,3}  表示={visible,-5} 種類={node->Type}");
    }

    /// <summary>
    /// 画面を作る元のデータを書き出す。
    ///
    /// 部品にも値にも数字が無い場合、ここに残っている可能性がある。
    /// 数が多いので、中身が入っているものだけを出す。
    /// </summary>
    private static void WriteArrayData(StringBuilder sb)
    {
        sb.AppendLine("──────────────────────────────────────────");
        sb.AppendLine("  画面の元データ（ArrayData）");
        sb.AppendLine("  ※ 中身が入っているものだけ");
        sb.AppendLine("──────────────────────────────────────────");

        try
        {
            var module = RaptureAtkModule.Instance();
            if (module == null)
            {
                sb.AppendLine("  （読めませんでした）");
                return;
            }

            var holder = &module->AtkModule.AtkArrayDataHolder;

            sb.AppendLine($"  数の配列 : {holder->NumberArrayCount} 本");
            sb.AppendLine($"  文字の配列: {holder->StringArrayCount} 本");
            sb.AppendLine();

            WriteNumberArrays(sb, holder);
            WriteStringArrays(sb, holder);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  （読めませんでした: {ex.Message}）");
        }
    }

    private static void WriteNumberArrays(StringBuilder sb, AtkArrayDataHolder* holder)
    {
        sb.AppendLine("  ── 数の配列 ──");

        for (var i = 0; i < holder->NumberArrayCount; i++)
        {
            var arr = holder->NumberArrays[i];
            if (arr == null || arr->Size <= 0)
                continue;

            // 0 以外が入っている場所だけを集める。
            // 全部書くと膨大になり、肝心のところが埋もれる。
            var values = new List<string>();

            for (var j = 0; j < arr->Size && values.Count < MaxPerArray; j++)
            {
                var v = arr->IntArray[j];
                if (v != 0)
                    values.Add($"[{j}]={v}");
            }

            if (values.Count > 0)
                sb.AppendLine($"    番号 {i,3}（{arr->Size} 個）: {string.Join(" ", values)}");
        }
    }

    private static void WriteStringArrays(StringBuilder sb, AtkArrayDataHolder* holder)
    {
        sb.AppendLine();
        sb.AppendLine("  ── 文字の配列 ──");

        for (var i = 0; i < holder->StringArrayCount; i++)
        {
            var arr = holder->StringArrays[i];
            if (arr == null || arr->Size <= 0)
                continue;

            var values = new List<string>();

            for (var j = 0; j < arr->Size && values.Count < MaxPerArray; j++)
            {
                var p = arr->StringArray[j];
                if (p.Value == null)
                    continue;

                var s = ReadString(p.Value);
                if (!string.IsNullOrWhiteSpace(s))
                    values.Add($"[{j}]=「{s}」");
            }

            if (values.Count > 0)
                sb.AppendLine($"    番号 {i,3}（{arr->Size} 個）: {string.Join(" ", values)}");
        }
    }

    /// <summary>1本の配列から書き出す数の上限。多すぎると読めなくなる。</summary>
    private const int MaxPerArray = 40;

    /// <summary>
    /// 世界にあるものを、種類を問わず全部書き出す。
    ///
    /// <b>なぜ要るのか。</b>
    /// 「強欲の罠」の札は、画面（UI）ではなく<b>世界に浮かんでいる</b>。
    /// 実測（2026-09-18 13:54）で、札が開いた瞬間には
    /// 罠のウィンドウ（TreasureHighLow）が消えていた。
    /// つまり札はUIの部品ではなく、その場に置かれたオブジェクト。
    ///
    /// だとすれば <see cref="Svc.Objects"/> に出ているはずで、
    /// その DataId が数字に対応している可能性がある。
    ///
    /// ここでは種類で絞らず、近くにあるものを全部書き出す。
    /// 何が札なのか分かっていないので、絞ると取り逃がす。
    /// </summary>
    /// <param name="radius">この距離までのものを書き出す。</param>
    internal static string DescribeWorldObjects(float radius = 60f)
    {
        var sb = new StringBuilder();

        sb.AppendLine("──────────────────────────────────────────");
        sb.AppendLine("  世界にあるもの（種類を問わず・近い順）");
        sb.AppendLine($"  自分の位置: {Helpers.PlayerHelper.Position:F2}");
        sb.AppendLine("  ※ 強欲の罠の札は世界に浮かんでいる。ここに出ているはず");
        sb.AppendLine("──────────────────────────────────────────");

        try
        {
            var found = 0;

            var near = new List<(float Distance, string Line)>();

            foreach (var o in Svc.Objects)
            {
                if (o == null)
                    continue;

                var d = Helpers.ObjectHelper.DistanceToPlayer(o);
                if (d > radius)
                    continue;

                // 同じ個体を追えるように EntityId と並び位置も出す。
                // 「増えた」だけでなく「同じものが変化した」場合も見分けたい。
                near.Add((d,
                    $"    {d,6:F1}m  {o.ObjectKind,-12} DataId {o.BaseId,8}  "
                  + $"EntityId {o.EntityId:X8}  Index {o.ObjectIndex,4}  "
                  + $"触れる={o.IsTargetable,-5} {o.Position:F1}  「{o.Name}」"));
            }

            near.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            foreach (var (_, line) in near)
            {
                sb.AppendLine(line);
                found++;
            }

            if (found == 0)
                sb.AppendLine("    （何も見つかりませんでした）");
            else
                sb.AppendLine($"    — 合計 {found} 件 —");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  （読めませんでした: {ex.Message}）");
        }

        return sb.ToString();
    }

    /// <summary>
    /// コンテンツの演出（MapEffect）の状態を書き出す。
    ///
    /// <b>札がここで表示されている可能性がある。</b>
    /// 3Dに浮かぶものが、必ずしも <see cref="Svc.Objects"/> に
    /// 独立したオブジェクトとして出るとは限らない。
    /// コンテンツ内の演出は ContentDirector が持っており、
    /// 「演出番号（LayoutId）＋状態（State）」で切り替わる。
    ///
    /// 札がこの仕組みなら、DataId ではなく State から数字が分かるかもしれない。
    /// <b>ただし強欲の罠が実際にこれを使っているかは未確認。</b>
    ///
    /// 読み方は BossmodReborn の DebugMapEffect.cs に倣った
    /// （実績のある読み方をそのまま使い、offset は推測しない）。
    /// </summary>
    internal static string DescribeMapEffects()
    {
        var sb = new StringBuilder();

        sb.AppendLine("──────────────────────────────────────────");
        sb.AppendLine("  コンテンツの演出（MapEffect）");
        sb.AppendLine("  ※ 札がここで表示されている可能性がある（未確認）");
        sb.AppendLine("──────────────────────────────────────────");

        try
        {
            var framework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
            if (framework == null)
            {
                sb.AppendLine("  （EventFramework が読めませんでした）");
                return sb.ToString();
            }

            var director = framework->GetContentDirector();
            if (director == null)
            {
                sb.AppendLine("  （コンテンツ内ではありません）");
                return sb.ToString();
            }

            var effects = director->MapEffects;
            if (effects == null)
            {
                sb.AppendLine("  （演出の一覧が読めませんでした）");
                return sb.ToString();
            }

            var count = effects->ItemCount;
            sb.AppendLine($"  演出の数: {count}");
            sb.AppendLine();

            for (var i = 0; i < count; i++)
            {
                var item = effects->Items[i];

                sb.AppendLine(
                    $"    [{i,3}] LayoutId = {item.LayoutId:X8}  "
                  + $"State = {item.State:X4}  Flags = {item.Flags:X4}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  （読めませんでした: {ex.Message}）");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 世界にあるものを、ファイルに書き出す。
    /// </summary>
    internal static string? CaptureWorld(string folder, string label, float radius = 60f)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var safeLabel = string.Concat(label.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(folder, $"World_{stamp}_{safeLabel}.txt");

            var sb = new StringBuilder();
            WriteHeader(sb, label);
            sb.Append(DescribeWorldObjects(radius));
            sb.AppendLine();
            sb.Append(DescribeMapEffects());

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[AutoTreasure] 世界の様子を書き出せませんでした。");
            return null;
        }
    }

    private static string ReadString(byte* p)
    {
        if (p == null)
            return "";

        try
        {
            return MemoryHelper.ReadStringNullTerminated((nint)p).Replace("\n", " ");
        }
        catch
        {
            return "";
        }
    }
}
