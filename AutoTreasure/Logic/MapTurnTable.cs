using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoTreasure.Logic;

/// <summary>
/// 「古ぼけた地図S5」を誰が使うかを決める。
///
/// <b>地図は誰でも使える。</b>
/// これまでは分かりやすさのため、パーティリーダーだけが使う作りにしていた。
/// だがゲームの仕組み上、誰が使っても構わない。
/// 使った人がその周回の「地図役」になり、
/// 座標を配り、現地と魔紋の中で宝箱と扉に触る。
///
/// <b>パーティリーダーと地図役は別のもの。</b>
///   パーティリーダー … ゲームが決める。設定を配る係。
///   地図役           … この表が決める。周回ごとに変わる。
///
/// 順番の決め方は3通り（<see cref="MapTurnMode"/>）。
/// 決めるのはリーダーだけで、結果は全機に配られる。
/// </summary>
internal static class MapTurnTable
{
    /// <summary>プルダウンに並べられる上限。パーティは8人まで。</summary>
    internal const int MaxSlots = 8;

    /// <summary>
    /// 今パーティにいる人の名前を、並び順のまま返す。
    ///
    /// プルダウンの選択肢に使う。
    /// 1人のとき（ソロ）は自分だけを返す。
    /// </summary>
    internal static List<string> PartyNames()
    {
        var names = new List<string>();

        try
        {
            var party = Svc.Party;

            if (party != null && party.Length > 0)
            {
                foreach (var member in party)
                {
                    var name = member?.Name.TextValue;
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                        names.Add(name);
                }
            }

            // パーティを組んでいなければ自分だけ。
            if (names.Count == 0)
            {
                var me = Helpers.PlayerHelper.Name;
                if (!string.IsNullOrEmpty(me))
                    names.Add(me);
            }
        }
        catch
        {
            // 読めないときは空のまま返す。
        }

        return names;
    }

    /// <summary>
    /// 今の周回で地図を使う人の名前。決められなければ空。
    /// </summary>
    /// <param name="lapsDone">これまでに終えた周回の数。0 から数える。</param>
    internal static string Decide(int lapsDone)
    {
        var cfg = Plugin.Config;
        var party = PartyNames();

        if (party.Count == 0)
            return "";

        // 指定キャラのみ使用では、不在の指定を別キャラで代用しない。
        if (cfg.MapTurn == MapTurnMode.FixedCharacter)
        {
            var specified = cfg.MapTurnOrder.FirstOrDefault(s => !string.IsNullOrEmpty(s.Name));
            if (specified != null)
                return party.Contains(specified.Name) ? specified.Name : "";
        }

        // 設定された順番のうち、<b>今パーティにいる人だけ</b>を取り出す。
        //
        // 抜けた人の枠は飛ばす。
        // 抜けただけで周回が止まると、3台で回している最中に
        // 1人が落ちただけで全部止まることになる。
        // 再び合流すれば、また順番に入る。
        var order = cfg.MapTurnOrder
            .Where(s => !string.IsNullOrEmpty(s.Name) && party.Contains(s.Name))
            .ToList();

        // 誰も設定されていない（または全員いない）なら、自分が使う。
        //
        // 設定し忘れても動くようにしておく。
        // 止まってしまうより、押した人が使う方が分かりやすい。
        if (order.Count == 0)
            return cfg.MapTurnOrder.Any(s => !string.IsNullOrEmpty(s.Name))
                ? "" : Helpers.PlayerHelper.Name ?? "";

        switch (cfg.MapTurn)
        {
            case MapTurnMode.FixedCharacter:
                // 決めた1人だけ。先頭の枠を使う。
                return order[0].Name;

            case MapTurnMode.RoundRobin:
                // 1周ごとに次の人へ。最後まで行ったら先頭に戻る。
                return order[lapsDone % order.Count].Name;

            case MapTurnMode.AfterCount:
                return DecideByCount(order, lapsDone);

            default:
                return order[0].Name;
        }
    }

    /// <summary>
    /// 枚数で区切って順番を決める。
    ///
    /// 例: A×3枚 → B×2枚 なら、
    ///   1〜3周目 A / 4〜5周目 B / 6周目からまた A。
    ///
    /// <b>持っていない分は繰り越さない。</b>
    /// 「3枚と決めたのに2枚しか無かった」場合、
    /// その2枚を使った時点で次の人へ移る（不足分を次の人に足さない）。
    /// 帳尻を合わせようとすると、誰が何周目に使うのかが読めなくなる。
    /// その判断は <see cref="RunController"/> 側で、
    /// 実際に地図が無いことを見てから行う。
    /// </summary>
    private static string DecideByCount(List<MapTurnSlot> order, int lapsDone)
    {
        var total = order.Sum(s => Math.Max(1, s.Count));

        if (total <= 0)
            return order[0].Name;

        // 一巡ぶんの中での位置を出す。
        var position = lapsDone % total;

        foreach (var slot in order)
        {
            var count = Math.Max(1, slot.Count);

            if (position < count)
                return slot.Name;

            position -= count;
        }

        return order[^1].Name;
    }

    /// <summary>
    /// 今の番の人を飛ばして、次の人の番になるまで進めた周回数を返す。
    ///
    /// <b>1ずつ進めてはいけない。</b>
    /// 「設定消費数後に交代」では、1人が続けて何枚も使う。
    /// 1ずつ進めても同じ人の番が続くだけで、次の人に届かない。
    ///
    /// 実例（A×5 → B×5 → C×5 で A が地図を切らしている場合）:
    ///   1ずつ進めると A → A → A … と3回試して人数ぶんの試行を使い切り、
    ///   B の5枚に到達しないまま「A が地図役」と決まってしまう。
    ///
    /// そこで、今と違う人が出るまで一気に進める。
    /// </summary>
    internal static int SkipToNext(int lapsDone)
    {
        var current = Decide(lapsDone);

        if (string.IsNullOrEmpty(current))
            return lapsDone + 1;

        var cfg = Plugin.Config;
        var party = PartyNames();

        // 一巡ぶんより多く進めない。
        // 全員が同じ人しか返さない設定でも、ここで必ず止まる。
        var limit = cfg.MapTurnOrder
            .Where(s => !string.IsNullOrEmpty(s.Name) && party.Contains(s.Name))
            .Sum(s => Math.Max(1, s.Count));

        limit = Math.Max(1, limit);

        for (var i = 1; i <= limit; i++)
        {
            if (Decide(lapsDone + i) != current)
                return lapsDone + i;
        }

        // 1人しかいない（＝飛ばす先が無い）。
        return lapsDone + limit;
    }

    /// <summary>
    /// 自分が今の周回の地図役か。
    /// </summary>
    internal static bool IsMyTurn(int lapsDone)
    {
        var wanted = Decide(lapsDone);

        if (string.IsNullOrEmpty(wanted))
            return false;

        return wanted == Helpers.PlayerHelper.Name;
    }

    /// <summary>
    /// 順番の設定を、人に読める1行にする。画面と記録に使う。
    /// </summary>
    internal static string Describe()
    {
        var cfg = Plugin.Config;

        var slots = cfg.MapTurnOrder
            .Where(s => !string.IsNullOrEmpty(s.Name))
            .ToList();

        if (slots.Count == 0)
            return "未設定（開始した人が使います）";

        return cfg.MapTurn switch
        {
            MapTurnMode.FixedCharacter => $"{slots[0].Name} だけが使います",
            MapTurnMode.RoundRobin     => string.Join(" → ", slots.Select(s => s.Name)) + " の順に1枚ずつ",
            MapTurnMode.AfterCount     => string.Join(" → ", slots.Select(s => $"{s.Name}×{Math.Max(1, s.Count)}")),
            _                          => "未設定",
        };
    }
}
