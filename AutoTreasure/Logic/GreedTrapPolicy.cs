namespace AutoTreasure.Logic;

/// <summary>強欲の罠で、次にどうするか。</summary>
internal enum GreedChoice
{
    /// <summary>次が大きい方に賭ける。</summary>
    High,

    /// <summary>次が小さい方に賭ける。</summary>
    Low,

    /// <summary>ここでやめて、今までの分を確定させる。</summary>
    Stop,
}

/// <summary>
/// 「強欲の罠」の賭け方を決める。
///
/// 1〜9 の数字が出て、次の数字がそれより大きいか小さいかを当てる。
/// 当たれば報酬が増え、外すとそれまでの分を失う。
///
/// <b>同じ数字が出たら「解除継続」で、負けにはならない。</b>
/// 実測（2026-09-18 の画面）:
///   8 → 8 で「解除継続…」、4 → 4 でも「解除継続…」
///   4 → 3 で「解除失敗…」
/// つまり外れるのは、賭けた向きと逆に振れたときだけ。
/// 同じ数字は仕切り直しになるので、勝率は下の数え方より実際は高い。
///
/// 賭けるかどうかは、勝てる見込みで決める。
/// 同じ数字は出ない前提で、残り8通りのうち何通りが自分の側かを数える。
///
///   出た数字 1 → 上は8通り（100%）
///   出た数字 2 → 上は7通り（88%）
///   出た数字 3 → 上は6通り（75%）
///   出た数字 4 → 上は5通り（63%）／下は3通り（38%）
///   出た数字 5 → 上下とも4通り（50%）
///   出た数字 6 → 下は5通り（63%）／上は3通り（38%）
///   出た数字 7 → 下は6通り（75%）
///   出た数字 8 → 下は7通り（88%）
///   出た数字 9 → 下は8通り（100%）
///
/// 4・5・6 は、どちらに賭けても分が悪い。
/// 6 なら 63% で勝てるが、外せば積み上げた分が全部消える。
/// 続けるより、そこで確定させる方が結果は安定する。
///
/// この考え方は利用者の指定によるもの（2026-09-17）。
/// </summary>
internal static class GreedTrapPolicy
{
    /// <summary>これ以下なら上に賭ける。</summary>
    private const int HighThreshold = 3;

    /// <summary>これ以上なら下に賭ける。</summary>
    private const int LowThreshold = 7;

    /// <summary>
    /// 出ている数字から、次にどうするかを決める。
    ///
    /// <b>1回目と2回目以降で基準が違う。</b>
    ///
    /// 1回目はまだ何も積み上がっていないので、失うものが無い。
    /// 五分に近くても賭ける価値がある。
    ///   1〜5 → HIGH ／ 6〜9 → LOW
    ///
    /// 2回目以降は、外すと積み上げた分を全部失う。
    /// 分が悪いなら降りる方がよい。
    ///   1〜3 → HIGH ／ 7〜9 → LOW ／ 4〜6 → 確定させる
    ///
    /// この分け方は利用者の指定による（2026-09-18）。
    /// </summary>
    /// <param name="current">今出ている数字（1〜9）。</param>
    /// <param name="isFirstBet">この罠で1回目の勝負か。</param>
    internal static GreedChoice Decide(int current, bool isFirstBet = false)
    {
        // 想定外の数字なら、危ない橋は渡らない。
        if (current < 1 || current > 9)
            return GreedChoice.Stop;

        if (isFirstBet)
        {
            // 1回目は失うものが無いので、必ずどちらかに賭ける。
            //
            // 5 は上下が同じ4通りずつだが、同数が負けにならないため
            // 上に賭けても損はしない（実測で 8→8、4→4 とも「解除継続」）。
            return current <= FirstBetHighThreshold
                ? GreedChoice.High
                : GreedChoice.Low;
        }

        if (current <= HighThreshold)
            return GreedChoice.High;

        if (current >= LowThreshold)
            return GreedChoice.Low;

        // 4・5・6。どちらも分が悪いので確定させる。
        return GreedChoice.Stop;
    }

    /// <summary>1回目は、これ以下なら上に賭ける（＝6以上なら下）。</summary>
    private const int FirstBetHighThreshold = 5;

    /// <summary>その判断の理由を、人に読める形で返す。記録や画面表示に使う。</summary>
    internal static string Explain(int current, bool isFirstBet = false)
    {
        if (current < 1 || current > 9)
            return $"数字を読めませんでした（{current}）。念のため確定させます。";

        var choice = Decide(current, isFirstBet);
        return choice switch
        {
            GreedChoice.High => $"{current} なので上に賭けます（勝てる見込み {WinRateHigh(current)}%）",
            GreedChoice.Low  => $"{current} なので下に賭けます（勝てる見込み {WinRateLow(current)}%）",
            _                => $"{current} は分が悪いので、ここで確定させます"
                              + $"（上 {WinRateHigh(current)}% / 下 {WinRateLow(current)}%）",
        };
    }

    /// <summary>上に賭けたときに勝てる見込み（％）。</summary>
    internal static int WinRateHigh(int current) => (9 - current) * 100 / 8;

    /// <summary>下に賭けたときに勝てる見込み（％）。</summary>
    internal static int WinRateLow(int current) => (current - 1) * 100 / 8;
}
