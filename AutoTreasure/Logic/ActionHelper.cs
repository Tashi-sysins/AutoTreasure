using AutoTreasure.Helpers;
using ECommons.DalamudServices;
using ECommons.Automation;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;

namespace AutoTreasure.Logic;

/// <summary>
/// アクションを使う。
/// </summary>
internal static unsafe class ActionHelper
{
    /// <summary>
    /// 記録の書き出し先。
    ///
    /// 診断の手がかりは /xllog ではなくファイルにも残す。
    /// ファイルなら、そのまま見せてもらえる。
    /// </summary>
    internal static RunLog? Log { get; set; }

    /// <summary>ログと記録の両方に残す。</summary>
    private static void Note(string text)
    {
        Svc.Log.Information($"[AutoTreasure] {text}");
        Log?.Write($"《動作》 {text}");
    }

    /// <summary>
    /// ディグ。古ぼけた地図の場所で使うと宝箱が出てくる。
    ///
    /// 「ディグニティ」（占星術師のスキル）や「掘る」（別の場面で使うもの）と
    /// 名前が紛らわしい。ゲームのデータで確かめたところ、
    /// トレジャーハントで使うのは 1695（種別はシステム、再使用10秒）。
    ///
    /// もし違うものが出るようなら、実際に手で使ったときのログと突き合わせて直す。
    /// </summary>
    internal const uint DigActionId = 1695;

    /// <summary>
    /// 持ち物を使う。
    ///
    /// AutoDuty が使っている呼び方に合わせた。
    /// extraParam に 65535 を渡すのは「HQ でない方を使う」という意味。
    /// </summary>
    internal static void UseItem(uint itemId)
    {
        try
        {
            // 持ち物のどこにあるかまで指定して使う。
            //
            // 番号だけ渡す呼び方もあるが、それでは通らなかった
            // （命令は出ているのに確認窓すら開かなかった）。
            // 鞄の何番目にあるかを探してから渡す。
            // ICE など、実際に動いているプラグインもこの形。
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return;

            var agent = AgentInventoryContext.Instance();
            if (agent == null)
                return;

            foreach (var type in PlayerInventories)
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId != itemId)
                        continue;

                    // まず普通に使ってみる。薬や食事はこれで通る。
                    var result = agent->UseItem(itemId, type, (uint)i, 0);

                    if (result == 0)
                    {
                        // 通らなかった。
                        //
                        // 地図の解読は「使う」ではなく、右クリックの一覧から
                        // 「解読する」を選ぶ形。手で解読したときの記録でも、
                        // ContextMenu →「解読する」→「はい」の順だった。
                        //
                        // そこで右クリックの一覧を開く。
                        // 選ぶのは ContextMenuHandler が受け持つ。
                        agent->OpenForItemSlot(type, i, 0, 0);
                        Note($"アイテム {itemId} の一覧を開きました（{type} の {i} 番目）");
                        return;
                    }

                    Note($"アイテム {itemId} を使いました（{type} の {i} 番目 / 結果 {result}）");
                    return;
                }
            }

            Note($"アイテム {itemId} が持ち物に見つかりません");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "持ち物を使えませんでした。");
        }
    }

    /// <summary>普通の鞄。ここを順に探す。</summary>
    private static readonly InventoryType[] PlayerInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    /// <summary>
    /// 解読済みの地図の場所を、もう一度地図に出す。
    ///
    /// 旗を立てているのは Globetrotter（導入済み）。
    /// 解読した瞬間に自動で地図を開いて旗を立て、
    /// そのあとイベントアイテムにカーソルを合わせると再び出してくれる。
    ///
    /// こちらからは、Globetrotter の「/tmap」を呼ぶ。
    /// カーソルを合わせる操作は外から起こせないが、
    /// このコマンドは同じ処理（OpenMapLocation）を直接呼ぶため、
    /// 同じ結果が得られる。
    ///
    /// <b>ただし Globetrotter が地図を覚えていないと何も起きない。</b>
    /// 覚えるのは「解読した瞬間」なので、ゲームを再起動したあとなど、
    /// 解読を見ていない場合は手でカーソルを合わせてもらう必要がある。
    /// </summary>
    internal static bool ShowDecodedMap()
    {
        if (!EzThrottler.Throttle("AutoTreasure.ShowMap", 5000))
            return false;

        try
        {
            Chat.ExecuteCommand("/tmap");
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "/tmap を呼べませんでした。");
            return false;
        }
    }

    /// <summary>
    /// 古ぼけた地図S5 を解読する。
    ///
    /// 呼ぶ前に、解読済みの地図を持っていないことを必ず確かめること。
    /// 持っているのに使うと、地図を1枚無駄にする。
    /// </summary>
    internal static bool UseMapS5()
    {
        if (!EzThrottler.Throttle("AutoTreasure.UseMap", 3000))
            return false;

        UseItem(GameSnapshot.MapS5ItemId);
        return true;
    }

    /// <summary>
    /// ディグが今使えるか。
    ///
    /// 宝の場所から離れていると使えない。逆に言えば、
    /// 使えるようになったことが「正しい場所に立っている」合図になる。
    /// </summary>
    internal static bool CanDig()
    {
        if (!PlayerHelper.IsValid || PlayerHelper.IsMounted || PlayerHelper.IsCasting)
            return false;

        try
        {
            // null のまま -> で辿るとアクセス違反になり、try/catch では拾えない。
            var manager = ActionManager.Instance();
            if (manager == null)
                return false;

            return manager->GetActionStatus(ActionType.Action, DigActionId) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ディグを使う。
    ///
    /// 再使用まで10秒あるので、連打しても意味がない。間隔をあける。
    /// </summary>
    /// <returns>実際に使ったら true。</returns>
    internal static bool Dig()
    {
        if (!EzThrottler.Throttle("AutoTreasure.Dig", 1500))
            return false;

        if (!CanDig())
            return false;

        try
        {
            var manager = ActionManager.Instance();
            if (manager == null)
                return false;

            return manager->UseAction(ActionType.Action, DigActionId);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "ディグを使えませんでした。");
            return false;
        }
    }

    /// <summary>
    /// オートアタックを始める。
    ///
    /// <b>なぜ要るのか。</b>
    /// 最下層の敵は、こちらから手を出すまで何もしてこない。
    /// 狙いを付けただけでは戦闘が始まらないため、RSR も撃ち始めない
    /// （実測 2026-09-19 10:11: 距離 2.9y で狙いを付けたのに、
    ///   26秒間まったく攻撃しなかった）。
    ///
    /// そこで<b>1発目だけ、こちらから殴る</b>。
    /// 殴れば相手が反撃してきて戦闘状態に入り、あとは RSR が続ける。
    ///
    /// ID 7 は「オートアタック」。ジョブを問わず同じ。
    /// 武器を構えていない状態でも、これを撃てば構えて殴りはじめる。
    /// </summary>
    internal static bool StartAutoAttack()
    {
        // 連打しない。1秒に1度で十分。
        if (!EzThrottler.Throttle("AutoTreasure.AutoAttack", 1000))
            return false;

        try
        {
            var manager = ActionManager.Instance();
            if (manager == null)
                return false;

            var target = Svc.Targets.Target;

            if (target == null)
                return false;

            // <b>アクション 7 では始まらなかった。</b>
            //
            // 実測（2026-09-19 10:34）:
            //   UseAction は毎回「通った」を返すのに、
            //   「自分の戦闘中 = False」のまま1秒ごとに繰り返すだけで、
            //   一度も攻撃が発生しなかった。
            //
            // オートアタックは「撃つアクション」ではなく、
            // <b>相手に戦いを挑むと始まる</b>もの。
            // FFXIVClientStructs にも「始める」関数は無く、
            // 今その状態かを見る IsAutoAttacking しか無い。
            //
            // ゲームの /attack は、狙っている相手に戦いを挑む命令。
            // 人が手で殴りに行くのと同じ経路を通る。
            Chat.ExecuteCommand("/attack");

            Log?.Write($"オートアタック: /attack を送りました（相手 {target.Name}）");

            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "オートアタックを始められませんでした。");
            return false;
        }
    }

    /// <summary>オートアタックのアクションID。ジョブを問わず 7。</summary>
    private const uint AutoAttackActionId = 7;
}
