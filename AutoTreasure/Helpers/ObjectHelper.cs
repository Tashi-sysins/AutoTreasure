using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AutoTreasure.Helpers;

/// <summary>
/// 周りにあるものを探して、触る。
///
/// 宝箱・扉・敵は毎回ちがう場所に湧くため、座標を覚えておく方法が使えない。
/// そのつど探して、一番近いものへ向かう。
///
/// <b>見つけたオブジェクトを次のフレームまで持ち越さないこと。</b>
/// ゲーム側がいつ作り直すか分からず、古い参照を触ると落ちる。
/// 毎フレーム探し直すのが正しい。
/// </summary>
internal static unsafe class ObjectHelper
{
    /// <summary>自分からその座標までの距離。</summary>
    internal static float DistanceToPlayer(Vector3 position)
        => Vector3.Distance(position, PlayerHelper.Position);

    /// <summary>自分からそのオブジェクトまでの距離。</summary>
    internal static float DistanceToPlayer(IGameObject gameObject)
        => DistanceToPlayer(gameObject.Position);

    /// <summary>
    /// 自分からその相手の<b>体の表面</b>までの距離。
    ///
    /// <b>ゲームの「◯◯m」と同じ測り方。</b>
    /// ゲームは相手の当たり判定（体の大きさ）を差し引いた距離を出す。
    /// こちらの <see cref="DistanceToPlayer(IGameObject)"/> は
    /// 相手の<b>中心</b>までを測るので、体が大きい相手ほど食い違う。
    ///
    /// 実測（2026-09-19 最下層のゴールデン・モルター）:
    ///   ゲームの表示    0.00m  ← ぴったり密着している
    ///   中心までの距離  4.1y   ← こちらの計算
    ///
    /// この 4.1y を「まだ遠い」と判断したため、
    /// 近づく処理から先へ一度も進めず、攻撃に届かなかった。
    ///
    /// 相手の大きさを差し引いて、ゲームと同じ土俵で測る。
    /// 自分の大きさ（0.5y 前後）も引く。
    /// </summary>
    internal static float SurfaceDistanceToPlayer(IGameObject gameObject)
    {
        var raw = DistanceToPlayer(gameObject.Position);
        var theirs = gameObject.HitboxRadius;
        var mine = Svc.Objects.LocalPlayer?.HitboxRadius ?? 0.5f;

        return MathF.Max(0f, raw - theirs - mine);
    }

    /// <summary>
    /// 水平の距離と高さの差を別々に見る。
    ///
    /// 真上や真下にあるものを「近い」と誤解しないために使う。
    /// 宝箱の真上を飛んでいるときなどに効く。
    /// </summary>
    internal static bool IsNear(Vector3 target, Vector3 origin, float maxDistance, float maxHeightDistance)
        => Vector3.Distance(target, origin) < maxDistance
        && MathF.Abs(target.Y - origin.Y) < maxHeightDistance;

    // ---- 探す ---------------------------------------------------------------

    /// <summary>指定した種類のものを、近い順に全部返す。</summary>
    internal static List<IGameObject> GetByKind(ObjectKind kind)
        => [.. Svc.Objects.Where(o => o.ObjectKind == kind).OrderBy(DistanceToPlayer)];

    /// <summary>指定した種類のうち、一番近いものを返す。無ければ null。</summary>
    internal static IGameObject? GetNearestByKind(ObjectKind kind)
        => Svc.Objects.Where(o => o.ObjectKind == kind).OrderBy(DistanceToPlayer).FirstOrDefault();

    /// <summary>
    /// 一番近い宝箱を返す。
    ///
    /// 開け終わった宝箱は触れなくなる（IsTargetable が false）ので、
    /// それを除くことで「まだ開けていないもの」だけが残る。
    /// </summary>
    internal static IGameObject? GetNearestTreasure()
        => Svc.Objects
              .Where(o => o.ObjectKind == ObjectKind.Treasure && o.IsTargetable)
              .OrderBy(DistanceToPlayer)
              .FirstOrDefault();

    /// <summary>触れる状態の宝箱を、近い順に全部返す。</summary>
    internal static List<IGameObject> GetTreasures()
        => [.. Svc.Objects
                 .Where(o => o.ObjectKind == ObjectKind.Treasure && o.IsTargetable)
                 .OrderBy(DistanceToPlayer)];

    /// <summary>
    /// 一番近い仕掛け（扉など）を返す。
    /// 魔紋の中では、これが次の部屋へ進む扉になる。
    /// </summary>
    internal static IGameObject? GetNearestEventObject()
        => Svc.Objects
              .Where(o => o.ObjectKind == ObjectKind.EventObj && o.IsTargetable)
              .OrderBy(DistanceToPlayer)
              .FirstOrDefault();

    /// <summary>触れる状態の仕掛けを、近い順に全部返す。</summary>
    internal static List<IGameObject> GetEventObjects()
        => [.. Svc.Objects
                 .Where(o => o.ObjectKind == ObjectKind.EventObj && o.IsTargetable)
                 .OrderBy(DistanceToPlayer)];

    /// <summary>
    /// 仕掛けを、触れるかどうかを問わずに全部返す。
    ///
    /// <see cref="GetEventObjects"/> は「触れるもの」しか返さない。
    /// そのため、返ってきた数が 0 でも
    ///   (1) オブジェクトがまだ読み込まれていない
    ///   (2) 読み込まれてはいるが、まだ触れる状態になっていない
    /// のどちらなのか区別できない。
    ///
    /// 実測（2026-09-18 06:03 メンバー3台）:
    ///   扉のムービー明けに仕掛けが 0 件になり、25分・738回の観測で
    ///   一度も戻らなかった。このとき上の(1)と(2)のどちらなのかが
    ///   分からず、対処の当てがつけられなかった。
    ///
    /// 両方を数えて初めて「湧いていないのか、触れないだけなのか」が分かる。
    /// 判断には使わず、記録と作り直しの要否を決めるために使う。
    /// </summary>
    internal static List<IGameObject> GetEventObjectsIncludingUntargetable()
        => [.. Svc.Objects
                 .Where(o => o.ObjectKind == ObjectKind.EventObj)
                 .OrderBy(DistanceToPlayer)];

    /// <summary>
    /// 今そこにある仕掛けの数を「触れるもの／すべて」の2つで返す。
    ///
    /// ムービー明けに何も見えなくなったとき、
    /// どちらが起きているのかを記録に残すために使う。
    /// </summary>
    internal static (int Targetable, int Total) CountEventObjects()
    {
        var targetable = 0;
        var total = 0;

        foreach (var o in Svc.Objects)
        {
            if (o.ObjectKind != ObjectKind.EventObj)
                continue;

            total++;

            if (o.IsTargetable)
                targetable++;
        }

        return (targetable, total);
    }

    /// <summary>
    /// 生きている敵が周りにいるか。
    ///
    /// 戦闘中かどうかだけでは、敵が遠くで生き残っている場合を拾えない。
    /// 宝箱に近づいてよいかの判断に使う。
    /// </summary>
    internal static bool HasLivingEnemyWithin(float radius)
        => Svc.Objects.Any(o => IsLivingEnemy(o, radius));

    /// <summary>
    /// まだ戦っていない敵も含めて、倒すべき相手がいるか。
    ///
    /// <b><see cref="HasLivingEnemyWithin"/> との違い。</b>
    /// あちらは「こちらと戦っている敵」しか数えない。
    /// そこにいるだけの敵を数えると、宝箱のそばを通りかかった敵のせいで
    /// 「まだ戦闘中だ」と判断し、宝箱に触れなくなるため。
    ///
    /// ただし<b>最下層だけは事情が違う</b>。
    /// 最下層の敵は最初から置かれていて、<b>こちらから攻撃しないと戦闘が始まらない</b>
    /// （利用者の説明・2026-09-19）。
    /// つまり「戦っている敵」で数えると 0 件になり、
    /// 敵がいるのに素通りして脱出してしまう（実測 09:55 でそうなった）。
    ///
    /// そこで最下層では、こちらを向いていない敵も数える。
    /// 部屋は封鎖されていて他に何も無いので、
    /// 「そこにいる敵＝倒すべき敵」と見てよい。
    /// </summary>
    internal static bool HasAnyEnemyWithin(float radius)
        => Svc.Objects.Any(o => IsEnemyPresent(o, radius));

    /// <summary>まだ戦っていない敵も含めた数。</summary>
    internal static int CountAnyEnemies(float radius)
        => Svc.Objects.Count(o => IsEnemyPresent(o, radius));

    /// <summary>
    /// 一番近い、倒すべき相手。まだ戦っていない敵も含む。
    ///
    /// 最下層で「自分から殴りに行く」ときの目標に使う。
    /// </summary>
    internal static IGameObject? GetNearestAnyEnemy(float radius)
        => Svc.Objects
            .Where(o => IsEnemyPresent(o, radius))
            .OrderBy(DistanceToPlayer)
            .FirstOrDefault();

    /// <summary>
    /// そこにいる敵か（戦闘中かどうかは問わない）。
    ///
    /// <see cref="IsLivingEnemy"/> から「こちらと戦っているか」だけを外したもの。
    /// ほかの条件（生きている・触れる・戦う相手）は同じ。
    /// </summary>
    /// <summary>
    /// 敵を選ぶ条件が、どれで弾いているかを書き出す。
    ///
    /// 「触れる敵がいるのに見つからない」を調べるためのもの。
    /// 条件を1つずつ確かめ、どこで落ちたかを残す。
    /// </summary>
    internal static string DescribeEnemyFilter(float radius)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  敵の判定（範囲 {radius:F0}y・自分 {PlayerHelper.Position:F1}）");

        var found = false;

        foreach (var o in Svc.Objects)
        {
            if (o.ObjectKind != ObjectKind.BattleNpc)
                continue;

            found = true;

            var isChara = o is IBattleChara;
            var chara = o as IBattleChara;
            var dist = DistanceToPlayer(o);

            var verdict =
                !isChara                              ? "× IBattleChara ではない"
                : chara!.IsDead                       ? "× 死んでいる"
                : chara.CurrentHp == 0                ? "× HP が 0"
                : !o.IsTargetable                     ? "× 触れない"
                : dist > radius                       ? $"× 遠い（{dist:F1}y）"
                : IsFriendly(o)                       ? "× 味方（ペット等）"
                                                      : "○ 敵とみなす";

            sb.AppendLine($"    DataId {o.BaseId,7}  種別 {chara?.SubKind,3}"
                        + $"  HP {chara?.CurrentHp,10}  触れる={o.IsTargetable,-5}"
                        + $"  距離 {dist,6:F1}  {verdict}  「{o.Name}」");
        }

        if (!found)
            sb.AppendLine("    （BattleNpc が1体もいません）");

        return sb.ToString();
    }

    /// <summary>
    /// 殴ってはいけない相手か（ペット・味方）。
    ///
    /// フェアリー、カーバンクル、召喚獣、他の人のチョコボなど。
    /// これらは BattleNpc で、触れるうえに生きているため、
    /// 触れるかどうかだけでは敵と区別できない。
    /// </summary>
    private static bool IsFriendly(IGameObject o)
    {
        if (o is not IBattleNpc npc)
            return false;

        // ペット（フェアリー・カーバンクル・召喚獣）は種別で分かる。
        //
        // チョコボ（バディ）に当たる種別は Dalamud の列挙に無いので、
        // 下の「持ち主がいるか」で拾う。
        if (npc.SubKind == (byte)BattleNpcSubKind.Pet)
            return true;

        // 誰かの持ち物なら味方。自分のペットも、仲間のペットも外れる。
        //
        // ⚠ 敵の「本体の一部」（実測の種別 5）は持ち主を持たないので、
        //   ここでは外れない。ボスは今までどおり見つかる。
        return npc.OwnerId != 0 && npc.OwnerId != 0xE0000000;
    }

    private static bool IsEnemyPresent(IGameObject o, float radius)
    {
        if (o.ObjectKind != ObjectKind.BattleNpc)
            return false;

        if (o is not IBattleChara { IsDead: false, CurrentHp: > 0 })
            return false;

        // <b>種別（SubKind）では絞らない。</b>
        //
        // 実測（2026-09-19 10:14 最下層）:
        //   本物のゴールデン・モルターは
        //     DataId 17158 / 種別 5 / 触れる=True / HP 8,981,910
        //   だった。種別 5 は「本体の一部」を表す値で、
        //   通常の敵（種別 1）ではない。
        //
        //   同じ場所に 種別 11 / HP 44 / 触れない ものが7体重なっていたが、
        //   こちらは演出用で、殴る相手ではない。
        //
        // 「種別 1 だけ」で絞ると、<b>本物のボスが除外される</b>。
        // 実際そうなり、敵がいるのに「いない」と判断して
        // そのまま脱出しようとした。
        //
        // 代わりに<b>触れるかどうか</b>で選ぶ。
        // 触れない相手は、そもそも殴れない。
        if (!o.IsTargetable)
            return false;

        // ⚠ ただし<b>味方は外す</b>（2026-09-22 の報告）。
        //
        //   フェアリーやカーバンクル、他の人のチョコボは
        //   BattleNpc で、触れるうえに生きている。
        //   種別で絞るのをやめた結果、これらまで「倒す相手」に
        //   数えるようになっていた。
        //
        //   数えるだけなら害は小さいが、<b>狙いを付けて近づく</b>
        //   処理がこれを使っている。味方を狙ってしまうと、
        //   動き回るフェアリーを追いかけ続けることになり、
        //   周回がそこで途切れる。
        //
        //   ペットと味方だけを外す。ボスは種別が 1 でなくても
        //   ペットではないので、これまでどおり見つかる。
        if (IsFriendly(o))
            return false;

        return DistanceToPlayer(o) <= radius;
    }

    /// <summary>周りにいる生きた敵の数。</summary>
    internal static int CountLivingEnemies(float radius)
        => Svc.Objects.Count(o => IsLivingEnemy(o, radius));

    /// <summary>
    /// 倒すべき敵か。
    ///
    /// 「戦える相手」であることまで見る。
    /// BattleNpc には、こちらに敵対しないものも含まれるため、
    /// それを数えると「敵がいる」と思い込んで先へ進めなくなる。
    /// </summary>
    private static bool IsLivingEnemy(IGameObject o, float radius)
    {
        if (o.ObjectKind != ObjectKind.BattleNpc)
            return false;

        if (o is not IBattleChara { IsDead: false, CurrentHp: > 0 } chara)
            return false;

        if (!o.IsTargetable)
            return false;

        // 戦う相手だけを数える。ペットや的は除く。
        if (chara.SubKind != (byte)BattleNpcSubKind.Combatant)
            return false;

        if (DistanceToPlayer(o) > radius)
            return false;

        // こちらと戦っている相手だけを数える。
        //
        // ただそこにいるだけの野良の敵は数えない。
        // 数えてしまうと、宝箱のそばに敵が歩いていただけで
        // 「まだ戦闘中だ」と判断し、宝箱に触れなくなる。
        //
        // 実際、掘った直後に野良の敵が5体近くにいて、
        // 宝箱が目の前（6m）にあるのに開けられなかった。
        return IsEngaged(chara);
    }

    /// <summary>
    /// その敵が戦闘状態か（誰かと戦っているか）。
    ///
    /// 読めないときは true にしておく。
    /// 「まだ敵がいるかもしれない」側に倒す方が安全なため。
    /// </summary>
    private static unsafe bool IsEngaged(IBattleChara chara)
    {
        try
        {
            var raw = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)chara.Address;
            if (raw == null)
                return true;

            return raw->Character.InCombat;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 周りにいる敵の、残り体力の合計。
    ///
    /// これが減っていれば戦えている、という判断に使う。
    /// 「戦闘中か」だけでは、殴られているだけの状態と区別できない。
    /// </summary>
    internal static uint TotalEnemyHp(float radius)
    {
        uint total = 0;

        foreach (var o in Svc.Objects)
        {
            if (!IsLivingEnemy(o, radius))
                continue;

            if (o is IBattleChara chara)
                total += chara.CurrentHp;
        }

        return total;
    }

    /// <summary>一番近い生きた敵。無ければ null。</summary>
    internal static IGameObject? GetNearestEnemy(float radius)
        => Svc.Objects.Where(o => IsLivingEnemy(o, radius))
                      .OrderBy(DistanceToPlayer)
                      .FirstOrDefault();

    // ---- 触る ---------------------------------------------------------------

    /// <summary>
    /// オブジェクトに触る（決定キーを押すのと同じ）。
    ///
    /// UIのボタンを押すのではなく、ゲーム内部の処理を直接呼ぶ。
    /// 触れない状態のものには何もしない。
    /// </summary>
    internal static void Interact(IGameObject? gameObject)
    {
        if (gameObject is not { IsTargetable: true })
            return;

        // 触れる状態かどうかを、先に確かめる。
        //
        // 「触ったのに何も起きない」の多くは、こちらの都合で弾かれている。
        // 騎乗したまま・運ばれている最中・ウィンドウが開いている最中などは、
        // ゲームが黙って無視する。
        //
        // AutoDuty の触り方（Managers/ActionsManager.cs の Interactable）が
        // ここを丁寧に見ていたので、同じ考え方を取り入れた。
        // 実測でも「何も起きません」が画面に並ぶことがあった。
        if (!CanInteractNow())
            return;

        try
        {
            // null を確かめてから使う。
            // null のまま -> で辿るとアクセス違反になり、try/catch では拾えない。
            // ゲームが落ちるので、必ず先に確かめる。
            var system = TargetSystem.Instance();
            if (system == null)
                return;

            var ptr = (CSGameObject*)gameObject.Address;
            system->InteractWithObject(ptr, false);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "オブジェクトに触れませんでした。");
        }
    }

    /// <summary>
    /// 今、ものに触れる状態か。
    ///
    /// 触っても弾かれる場面を、先に除く。
    /// 弾かれると「何も起きません」がチャットに並ぶだけで、
    /// こちらは触ったつもりのまま次へ進んでしまう。
    ///
    /// 見る項目は AutoDuty の Interactable に倣った（実績のある一覧）。
    /// </summary>
    internal static bool CanInteractNow()
    {
        try
        {
            // 乗ったままでは触れない。
            if (Svc.Condition[ConditionFlag.Mounted]
                || Svc.Condition[ConditionFlag.RidingPillion])
                return false;

            // 運ばれている・飛んでいる最中。
            if (Svc.Condition[ConditionFlag.BetweenAreas]
                || Svc.Condition[ConditionFlag.BetweenAreas51]
                || Svc.Condition[ConditionFlag.BeingMoved]
                || Svc.Condition[ConditionFlag.Jumping]
                || Svc.Condition[ConditionFlag.Jumping61])
                return false;

            // 何かを運んでいる最中。
            if (Svc.Condition[ConditionFlag.CarryingObject])
                return false;

            // ほかの用事の最中。
            //
            // Occupied は種類が多く、どれも「今は手が離せない」を表す。
            // ひとつでも立っていれば触らない。
            if (Svc.Condition[ConditionFlag.Occupied]
                || Svc.Condition[ConditionFlag.Occupied30]
                || Svc.Condition[ConditionFlag.Occupied33]
                || Svc.Condition[ConditionFlag.Occupied38]
                || Svc.Condition[ConditionFlag.Occupied39]
                || Svc.Condition[ConditionFlag.OccupiedInEvent]
                || Svc.Condition[ConditionFlag.OccupiedInQuestEvent]
                || Svc.Condition[ConditionFlag.OccupiedSummoningBell])
                return false;

            // 詠唱中。動くと中断される。
            if (PlayerHelper.IsCasting)
                return false;

            return true;
        }
        catch
        {
            // 読めないときは触らない。
            // 触って弾かれるより、次のフレームで確かめ直す方がよい。
            return false;
        }
    }

    /// <summary>
    /// 触れなくなるまで触り続ける。
    ///
    /// 宝箱は「1回触れば開く」とは限らず、反応しないことがある。
    /// 開ききると触れなくなるので、それを終わりの合図にする。
    ///
    /// 連打にならないよう間隔をあける。
    /// </summary>
    /// <param name="throttleKey">
    /// 間隔をあけるための名前。同時に複数のものを扱うときは別々の名前にする。
    /// </param>
    /// <returns>触れなくなった（＝終わった）なら true。</returns>
    internal static bool InteractUntilNotTargetable(IGameObject? gameObject, string throttleKey = "Interact", int intervalMs = 500)
    {
        if (gameObject is not { IsTargetable: true })
            return true;

        if (EzThrottler.Throttle(throttleKey, intervalMs))
            Interact(gameObject);

        return false;
    }

    // ---- ターゲット ---------------------------------------------------------

    /// <summary>そのオブジェクトをターゲットにする。</summary>
    internal static void Target(IGameObject? gameObject)
    {
        if (gameObject == null)
            return;

        Svc.Targets.Target = gameObject;
    }

    /// <summary>自分が設定したターゲットを外す。</summary>
    internal static void ClearTarget() => Svc.Targets.Target = null;
}
