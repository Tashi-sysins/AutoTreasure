using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Runtime.InteropServices;

namespace AutoTreasure.Helpers;

/// <summary>
/// ロット（Need / Greed / Pass）を自動で選ぶ。
///
/// リーダーは Need、メンバーは Pass。取り分をリーダーにまとめるための決まりごと。
///
/// ロット画面のボタンを押すのではなく、ゲーム内部の処理を直接呼ぶため、
/// 画面が出ていなくても、複数まとめてでも確実に処理できる。
///
/// <para>
/// 出どころについて:
/// この手口は LazyLoot（PunishXIV・GPL-3.0）で知られているものだが、
/// コードは流用していない。借りているのは次の2つの「事実」だけ:
///   ・ゲーム内部の処理の引数の並び（ロット一覧・選んだ内容・何番目か）
///   ・その処理を見つけるためのバイト列
/// どちらもゲーム側の仕様であって、著作物ではない。
/// 判断の仕方（誰が Need で誰が Pass か、いつ諦めるか、重複をどう防ぐか）は
/// すべてこちらで決めている。
///
/// このプラグインは非公開で使うものなので、GPL のコードをそのまま
/// 取り込むわけにはいかない。書き写さないことを守る。
/// </para>
/// </summary>
internal static unsafe class LootHelper
{
    /// <summary>
    /// ロットを確定させるゲーム内部の処理。
    ///
    /// 第1引数がロット一覧、第2引数が選んだ内容、第3引数が何番目のアイテムか。
    /// </summary>
    private delegate bool RollItemRawDelegate(Loot* loot, RollResult option, uint index);

    private static RollItemRawDelegate? _rollItemRaw;

    /// <summary>
    /// ロット処理を探すための目印（バイト列）。
    ///
    /// この並びは LazyLoot v5.3.3.3（実際に動作しているもの）と
    /// 公開ソース（2026-04-30）の両方で一致することを確認済み。
    ///
    /// ゲームの更新でずれることがある。そのときはロットだけが効かなくなるので、
    /// LazyLoot の更新を見て新しい並びに直す。
    /// </summary>
    private const string RollItemRawSignature = "41 83 F8 ?? 0F 83 ?? ?? ?? ?? 48 89 5C 24 08";

    private static bool _signatureFailed;

    // 直前に処理したもの。同じものがまた出てきたら「効いていない」と判断する。
    private static uint _lastItemId;
    private static uint _lastIndex = uint.MaxValue;

    /// <summary>ロット処理を使えるか。</summary>
    internal static bool IsAvailable => Resolve() != null;

    private static RollItemRawDelegate? Resolve()
    {
        if (_rollItemRaw != null)
            return _rollItemRaw;

        // 一度失敗したら諦める。毎フレーム探し直すと重いうえ、ログが埋まる。
        if (_signatureFailed)
            return null;

        try
        {
            var address = Svc.SigScanner.ScanText(RollItemRawSignature);

            // 目印が複数の場所に一致することがある。
            //
            // 2026-09-17 のゲーム更新後、この並びは 2 箇所に一致した
            // （0xC3C460 と 0xC3C7B0。先頭 32 バイトまで同じ）。
            // ScanText は最初に見つけた方を返すので、
            // 別の処理を掴んでいても気づけない。
            //
            // ロットが効かない場合は、まずここを疑う。
            Svc.Log.Information($"[AutoTreasure] ロット処理の場所: 0x{address.ToInt64():X}");

            _rollItemRaw = Marshal.GetDelegateForFunctionPointer<RollItemRawDelegate>(address);
            return _rollItemRaw;
        }
        catch (Exception ex)
        {
            _signatureFailed = true;
            Svc.Log.Error(ex, "ロット処理が見つかりませんでした。ゲームの更新で目印がずれた可能性があります。自動ロットは行いません。");
            return null;
        }
    }

    /// <summary>今ロットできるアイテムが1つ以上あるか。</summary>
    internal static bool HasPendingLoot()
    {
        var loot = Loot.Instance();
        if (loot == null)
            return false;

        var span = loot->Items;
        for (var i = 0; i < span.Length; i++)
        {
            if (IsRollable(span[i]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 出ているロットを1件だけ処理する。
    ///
    /// <b>まとめて処理しないのが要点。</b>
    /// ロットを確定させても、一覧に反映されるまで数フレームかかる。
    /// 同じ一覧を見たまま次々に処理すると、すでに無いものを指してしまう。
    /// 借りてきた実装（LazyLoot）も1件ずつ処理している。
    ///
    /// 何度も呼ばれる前提で、呼ばれるたびに1件ずつ片付ける。
    /// </summary>
    /// <param name="preferred">
    /// 望む内容。リーダーは Needed、メンバーは Passed。
    /// ただしアイテムによっては Need を選べないので、その場合は自動的に控えめな方へ落とす。
    /// </param>
    /// <returns>1件処理したら true。処理するものが無ければ false。</returns>
    internal static bool RollPending(RollResult preferred)
    {
        // LazyLoot が入っているなら、何があっても押さない。
        //
        // <b>最後の歯止め。</b>
        // 呼ぶ側（RunController）でも止めているが、
        // 呼び忘れ・新しい呼び出しの追加があっても、
        // ここを通る限り二重にロットすることはない。
        //
        // 両方が押すと、こちらが Need を押す前に LazyLoot が Pass を押す、
        // といった取り合いになる。手口まで同じ（RollItemRaw を直接呼ぶ）ので、
        // どちらが先に通るかで結果が変わり、安定しない。
        if (AutoTreasure.IPC.LazyLootControl.ShouldYield)
            return false;

        if (!EzThrottler.Throttle("AutoTreasure.Roll", 600))
            return false;

        var fn = Resolve();
        if (fn == null)
            return false;

        var loot = Loot.Instance();
        if (loot == null)
            return false;

        var span = loot->Items;

        for (var i = 0; i < span.Length; i++)
        {
            var item = span[i];
            if (!IsRollable(item))
                continue;

            var index = (uint)i;
            var itemId = NormalizeItemId(item.ItemId);
            var option = ClampToAllowed(preferred, item.RollState);

            // 直前とまったく同じものが残っている＝前回の指示が通っていない。
            // 同じことを繰り返しても結果は変わらないので、Pass に切り替えて先へ進める。
            // （Need が選べない品なのに Need を投げ続ける、といった状態を抜けるため）
            if (_lastItemId == itemId && _lastIndex == index)
            {
                Svc.Log.Debug($"ロットが通らなかったため、Pass に切り替えます（アイテム {itemId}）。");
                option = RollResult.Passed;
            }

            try
            {
                fn(loot, option, index);
                _lastItemId = itemId;
                _lastIndex = index;
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, $"ロットに失敗しました（{i} 番目）。");
            }

            // 1件処理したら抜ける。残りは次に呼ばれたときに片付ける。
            return true;
        }

        // 残っていない。次にロットが出たときのために覚えていたものを捨てる。
        _lastItemId = 0;
        _lastIndex = uint.MaxValue;
        return false;
    }

    /// <summary>
    /// そのアイテムに対して実際に選べる内容へ落とす。
    ///
    /// アイテムによっては Need を選べない（装備できない、すでに持っている等）。
    /// そこへ Need を投げても何も起きず、ロット画面が残り続ける。
    ///
    /// RollResult は値が大きいほど控えめ（UnAwarded 0 → Needed 1 → Greeded 2 → Passed 5）。
    /// 望んだ内容と、そのアイテムの上限を比べて、控えめな方を採る。
    /// </summary>
    private static RollResult ClampToAllowed(RollResult preferred, RollState state)
    {
        var allowed = state switch
        {
            RollState.UpToNeed  => RollResult.Needed,
            RollState.UpToGreed => RollResult.Greeded,
            _                   => RollResult.Passed,
        };

        return (int)preferred >= (int)allowed ? preferred : allowed;
    }

    /// <summary>高品質のアイテムは ID に 1000000 が足されている。元に戻す。</summary>
    private static uint NormalizeItemId(uint itemId)
        => itemId >= 1000000 ? itemId - 1000000 : itemId;

    /// <summary>
    /// そのアイテムが「今ロットできる」ものか。
    /// 判定の内容は LazyLoot に倣っている。
    /// </summary>
    private static bool IsRollable(LootItem item)
    {
        if (NormalizeItemId(item.ItemId) == 0)
            return false;

        // どの宝箱から出たか分からないものは対象外。
        if (item.ChestObjectId is 0 or 0xE0000000)
            return false;

        // すでにロット済み。
        if (item.RollResult != RollResult.UnAwarded)
            return false;

        // ロットできない状態。
        if (item.RollState is RollState.Rolled or RollState.Unavailable or RollState.Unknown)
            return false;

        // ロットマスターが管理していて自分では選べない場合など。
        if (item.LootMode is LootMode.LootMasterGreedOnly or LootMode.Unavailable)
            return false;

        return true;
    }

    /// <summary>役割に応じて、望む内容を決める。</summary>
    /// <summary>
    /// ロットで何を選ぶか。
    ///
    /// 以前は「リーダーは Need、メンバーは Pass」と決め打ちだった。
    /// 全員で分け合う必要がなくなったので、機ごとの設定に従う。
    /// </summary>
    internal static RollResult OptionFor(ClientRole role)
        => Plugin.Config.RollOption switch
        {
            RollChoice.Need  => RollResult.Needed,
            RollChoice.Greed => RollResult.Greeded,
            _                => RollResult.Passed,
        };

    /// <summary>覚えている内容を捨てる。周回の区切りで呼ぶ。</summary>
    internal static void Reset()
    {
        _lastItemId = 0;
        _lastIndex = uint.MaxValue;
    }
}
