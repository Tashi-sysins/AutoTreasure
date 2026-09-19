using AutoTreasure.Helpers;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoTreasure.Logic;

/// <summary>魔紋の中で、次に何をするか。</summary>
internal enum VaultAction
{
    /// <summary>待つ。今は動かない。</summary>
    Wait,

    /// <summary>敵がいる。倒し終わるまで宝箱には近づかない。</summary>
    Fight,

    /// <summary>宝箱がある。近づいて開ける。</summary>
    OpenChest,

    /// <summary>宝箱が無い。扉へ向かう。</summary>
    GoToDoor,

    /// <summary>
    /// 扉が開いている。ワープ床に乗って次の部屋へ進む。
    ///
    /// <b>今は使っていない。</b>
    /// <see cref="VaultRoutine.Decide"/> はこれを返さない。
    /// ワープ床は触れると手前へ戻されることがあり、
    /// 「見つけたら乗る」では詰まるため、
    /// 判断を <see cref="RunController"/> 側（一定時間待ってから乗る）に移した。
    /// 消さずに残してあるのは、記録や過去のログと突き合わせるときの手がかりのため。
    /// </summary>
    GoToWarp,

    /// <summary>
    /// 宝箱も扉もワープ床も無い。最後の部屋なので出る。
    ///
    /// <b>今は使っていない。</b>
    /// <see cref="VaultRoutine.Decide"/> はこれを返さない。
    /// 最下層かどうかは「脱出地点（2000139）が在るか」で判断するようになった
    /// （実測 2026-09-18）。見えるものが無いことを根拠にすると、
    /// ムービーの前後で一瞬すべて消える瞬間を最下層と取り違える。
    /// </summary>
    Leave,
}

/// <summary>
/// 魔紋（宝物庫）の中での動き。
///
/// 魔紋は入るたびに中の配置が変わる。決まった道順を覚えておく方法が使えないので、
/// そのつど周りを見て、次にすることを決める。
///
/// 決め方はこの順番。
///   1. 敵がいる間は宝箱に近づかない（先に倒す）
///   2. 敵が消えたら宝箱へ向かう
///   3. 宝箱が無くなったら扉へ向かう
///   4. 宝箱も扉も無ければ、最後の部屋なので出る
///
/// 扉にアクセスするのはリーダーだけ。全員がアクセスすると
/// ムービーが何度も始まってしまう。
/// </summary>
internal static class VaultRoutine
{
    // 魔紋の中に出てくるものの ID。
    // 2026-09-17 のセノーテ・ジャジャグラル（TerritoryType 1209）で実測した。
    //
    // 名前だけで見分けると、言語設定が変わったときに壊れる。
    // ID を主に使い、名前は保険として併用する。

    // 区画ごとに ID がずれる。第1〜第4区画まで実測して、規則性を確認した。
    //
    //   区画   宝箱      HIGH      LOW       扉(左)    扉(右)
    //   第1   2013860   2013872   2013873   2013864   2013865
    //   第2   2013861   2013874   2013875   2013866   2013867
    //   第3   2013862   2013876   2013877   2013868   2013869
    //   第4   2013863   2013878   2013879   2013870   2013871
    //
    // 宝箱は +1、扉と HIGH/LOW は +2 ずつ増える。
    // 1つの ID だけを見ていると第2区画以降で何も見つけられなくなるので、
    // 範囲で判定する。

    /// <summary>次の区画へ進む扉（左）。第1区画の値。</summary>
    internal const uint DoorLeftBaseId = 2013864;

    /// <summary>次の区画へ進む扉（右）。第1区画の値。</summary>
    internal const uint DoorRightBaseId = 2013865;

    /// <summary>扉の ID の範囲（第1〜第4区画）。</summary>
    internal const uint DoorFirstId = 2013864;
    internal const uint DoorLastId  = 2013871;

    /// <summary>宝箱の ID の範囲（第1〜第4区画）。</summary>
    internal const uint ChestFirstId = 2013860;
    internal const uint ChestLastId  = 2013863;

    /// <summary>
    /// 「HIGH」「LOW」の足場の ID の範囲。
    ///
    /// 第1〜第4区画が 2013872〜2013879、最下層が 2013880/2013881。
    /// 最下層にも強欲の罠がある（実測 2026-09-18 17:26）。
    /// </summary>
    internal const uint GambleFirstId = 2013872;
    internal const uint GambleLastId  = 2013881;

    /// <summary>
    /// 脱出地点（最下層にある出口）。
    ///
    /// 実測（2026-09-18 17:26）:
    ///   DataId 2000139 / 名前「脱出地点」/ 座標 (0, -9, -388)
    ///
    /// 以前は「扉でもワープでも宝箱でもないもの」という消去法で
    /// 探していた。ID が分かったので、直に指せるようになった。
    /// </summary>
    internal const uint ExitPortalBaseId = 2000139;

    /// <summary>
    /// 最下層の高さ。
    ///
    /// 実測（2026-09-18 17:26 / 2026-09-19 09:55）ともに -9。
    /// 第1〜第4区画は -400 / -400 / -290 / -169 なので、
    /// いちばん近い第4区画とも 160y 離れている。取り違えようがない。
    /// </summary>
    internal const float FinalRoomHeight = -9f;

    /// <summary>
    /// 最下層と認める高さの幅。
    ///
    /// 着地の直後は少し浮いていることがある
    /// （実測では -7.81 や -10.5 を通った）。
    /// 上下 30y まで認める。次に近い第4区画（-169）とは 160y 離れているので、
    /// これだけ広く取っても混じらない。
    /// </summary>
    internal const float FinalRoomHeightTolerance = 30f;

    /// <summary>
    /// 区画ごとの、決まった場所。
    ///
    /// <b>魔紋の中は、入るたびに同じ場所に同じものが出る。</b>
    /// 185本の記録（複数日・4人・多数の周回）を調べたところ、
    /// 宝箱・扉・HIGH/LOW の 20 個すべてが、常に同じ座標だった。
    /// 1つも動いていない。
    ///
    /// これが分かる前は「見えたものを探して向かう」しかなかった。
    /// そのため、仕掛けが見えないメンバーは何もできずに止まっていた。
    /// 座標が決まっているなら、見えなくても向かえる。
    ///
    /// 第1区画と第2区画は<b>高さが同じ</b>（どちらも -400）。
    /// 「階層ごとに高さが違う」とは限らないので、
    /// 区画の見分けには高さではなくこの表を使う。
    /// </summary>
    internal readonly record struct VaultRoom(
        int Index,
        uint ChestId,
        Vector3 Chest,
        uint LeftDoorId,
        Vector3 LeftDoor,
        uint RightDoorId,
        Vector3 RightDoor);

    /// <summary>
    /// 第1〜第4区画の決まった場所（実測 2026-09-18）。
    ///
    /// <b>最下層（第5区画）はここに入れない。</b>
    /// 最下層には次の部屋へ進む扉が無く、代わりに脱出地点がある。
    /// 作りが違うので、同じ表で扱うと「扉が無い＝おかしい」と
    /// 判断してしまう。最下層は <see cref="IsFinalRoom"/> で見分ける。
    ///
    /// 実測した最下層の様子（2026-09-18 17:26）:
    ///   高さ            -9
    ///   脱出地点        2000139  ( 0.0, -9, -388.0)
    ///   HIGH            2013880  (-1.0, -9, -370.3)
    ///   LOW             2013881  ( 1.0, -9, -370.3)
    ///   宝箱            Treasure 種別（DataId 792 / 0）。場所は一定しない
    ///   名前の無い仕掛け 2014392  ( 0.0, -9, -385.0)
    /// </summary>
    internal static readonly VaultRoom[] Rooms =
    [
        new(1, 2013860, new(   0f, -400f, 377f),
               2013864, new( -25f, -396f, 352f),
               2013865, new(  25f, -396f, 352f)),

        new(2, 2013861, new(   0f, -400f, 192f),
               2013866, new( -25f, -396f, 167f),
               2013867, new(  24f, -396f, 168f)),

        new(3, 2013862, new( 160f, -290f,  19f),
               2013868, new( 135f, -286f,  -6f),
               2013869, new( 185f, -286f,  -6f)),

        new(4, 2013863, new(-197f, -169f, -145f),
               2013870, new(-222f, -165f, -170f),
               2013871, new(-172f, -165f, -170f)),
    ];

    /// <summary>
    /// 今いる区画を、自分の座標から見分ける。
    ///
    /// 仕掛けが1つも見えていなくても分かる。
    /// 見つからなければ null（第5区画、または運ばれている最中）。
    /// </summary>
    internal static VaultRoom? CurrentRoom()
    {
        if (!IsInsideVault())
            return null;

        var me = PlayerHelper.Position;

        VaultRoom? best = null;
        var bestDistance = float.MaxValue;

        foreach (var room in Rooms)
        {
            // 区画の代表点は宝箱の場所。扉もその近くにある。
            var d = Vector3.Distance(me, room.Chest);

            if (d < bestDistance)
            {
                bestDistance = d;
                best = room;
            }
        }

        // 遠すぎるなら、表に無い区画にいる。
        return bestDistance <= RoomMatchRadius ? best : null;
    }

    /// <summary>
    /// その区画の中と認める広さ。
    ///
    /// 実測では、区画どうしは 185y 以上離れている
    /// （第1 Z=377 と第2 Z=192）。
    /// 部屋の端から端まで入り、隣の区画とは混じらない広さにする。
    /// </summary>
    private const float RoomMatchRadius = 120f;

    /// <summary>
    /// 左の扉か。
    ///
    /// 左右は座標で分かれる。実測では左が X≈-24.6、右が X≈+24.6 で、
    /// 高さも Z も同じ。ID でも偶数が左、奇数が右になっている。
    /// </summary>
    internal static bool IsLeftDoor(IGameObject? o)
        => IsDoor(o) && o!.BaseId % 2 == 0;

    /// <summary>
    /// フィールドに現れる、宝物庫への入り口（「転送魔紋」）。
    /// 宝箱を開け終わって敵を倒すと出る。
    /// </summary>
    internal const uint EntryPortalBaseId = 2007181;

    /// <summary>魔紋の中の宝箱。</summary>
    internal const uint VaultChestBaseId = 2013860;

    /// <summary>
    /// 「簡易移動」。
    ///
    /// <b>これは次の部屋へ進むためのものではない。</b>
    /// 部屋の入口へ戻すための移動床で、触れると部屋の手前
    /// （実測では Z=449 → Z=392）へ<b>引き戻される</b>。
    ///
    /// 実測で分かったこと（2026-09-17 セノーテ・ジャジャグラル）:
    ///   ・扉を開ける前（13:42:26）から、すでに置かれている
    ///   ・触れてもエリア（TerritoryType 1209）は変わらない
    ///   ・触れると Z が 445 → 392 に戻る
    ///
    /// つまり、これを「次へ進む道」と思って乗ると、
    /// 進んでは戻されるを延々と繰り返す。実際にそうなった。
    /// 近づくだけでも作動するため、<b>触れない・近づかない</b>のが正しい扱い。
    /// </summary>
    internal const uint WarpBaseId = 2000700;

    /// <summary>
    /// 周りを見る範囲。この外にあるものは今は考えない。
    ///
    /// 実測（2026-09-17 セノーテ・ジャジャグラル）:
    ///   魔紋に入った直後、宝箱まで 80.8m、扉まで 108.4m あった。
    ///   70m だと宝箱も扉も見つからず、少し歩いてから
    ///   先に見えた方へ向かってしまう。
    ///
    /// 部屋の端から端まで入る広さにしておく。
    /// 宝箱と扉のどちらを選ぶかは距離ではなく順番で決めているので、
    /// 広くしても「近い扉に釣られる」ことはない。
    /// </summary>
    private const float SearchRadius = 150f;

    /// <summary>
    /// 敵がいるかを見る範囲。
    ///
    /// 部屋の端で1体だけ生き残っていることがある。
    /// 狭く取ると「倒し終わった」と誤解して先へ進もうとし、
    /// 扉が開かずに止まる。部屋全体が入る広さにしておく。
    /// </summary>
    private const float EnemyRadius = 70f;

    /// <summary>
    /// 今の状況から、次にすることを決める。
    /// </summary>
    /// <param name="chest">向かうべき宝箱。無ければ null。</param>
    /// <param name="door">向かうべき扉。無ければ null。</param>
    internal static VaultAction Decide(out IGameObject? chest, out IGameObject? door, out IGameObject? warp,
                                      bool preferRightDoor = false)
    {
        chest = null;
        door = null;
        warp = null;

        if (!PlayerHelper.IsValid)
            return VaultAction.Wait;

        // ムービー中やエリア移動中は何もしない。
        if (!PlayerHelper.IsReady)
            return VaultAction.Wait;

        // 1. 敵がいる間は、ほかのことを一切しない。
        //
        //    魔紋の中では、これが最優先。
        //    敵は宝箱を開けたときにしか湧かないので、
        //    「敵がいない」状態を作ることが先へ進む条件になる。
        //
        //    戦闘中かどうかだけでは、遠くで生き残っている敵を見落とす。
        //    逆に、戦闘が終わっていても湧いたばかりの敵がいることもある。
        //    両方を見る。
        if (PlayerHelper.InCombat || ObjectHelper.HasLivingEnemyWithin(EnemyRadius))
            return VaultAction.Fight;

        // 2. 宝箱があれば、それが最優先。
        chest = FindChest();
        if (chest != null)
            return VaultAction.OpenChest;

        // 3. 宝箱が無ければ扉を探す。
        door = FindDoor(preferRightDoor);
        if (door != null)
            return VaultAction.GoToDoor;

        // 4. 「簡易移動」は次へ進む道ではない（触れると手前へ戻される）。
        //    見つけても乗らない。ここでは「動かずに待つ」を選ぶ。
        //
        //    扉が開いたあと、次の部屋へはワープ床ではなく
        //    扉の奥へ歩いて進む。その道はまだ実測できていないので、
        //    勝手に動いて戻され続けるより、止まって人に任せる方が安全。
        warp = FindWarp();
        if (warp != null)
            return VaultAction.Wait;

        // 5. どれも見えない。
        //
        // ただし、この瞬間だけ見えていないことがある。
        // ムービーが始まる直前、扉も宝箱も先に消える。
        // 実測（2026-09-17）では、仕掛けが 2 → 0 になった 0.035 秒前に
        // 「最後の部屋だ」と判断し、まだ第3区画なのに脱出しようとした。
        //
        // そこで、しばらく何も見えない状態が続いて初めて
        // 「最後の部屋」とみなす。すぐには決めない。
        return VaultAction.Wait;
    }

    /// <summary>
    /// 一番近い宝箱を探す。
    ///
    /// 開け終わった宝箱は触れなくなるので、自然に対象から外れる。
    /// </summary>
    private static IGameObject? FindChest()
    {
        // 魔紋の中の宝箱は Treasure ではなく EventObj として現れる。
        //
        // 実測（2026-09-17）:
        //   第1区画 2013860 / 第2 2013861 / 第3 2013862 / 第4 2013863
        //   いずれも ObjectKind は EventObj。
        //
        // Treasure だけを探していたので、魔紋の中の宝箱は
        // 1つも見つけられていなかった。フィールドの宝箱は Treasure なので、
        // 両方を見る。
        // 魔紋の中では、まず魔紋の宝箱を探す。
        //
        // 中には「革袋」のような、拾うだけのものも落ちている
        // （Treasure 種別・実測 DataId 791）。
        // 先に Treasure 種別を見ると、そちらを宝箱と思って
        // 近い革袋へ向かってしまう。
        if (IsInsideVault())
        {
            var vaultChest = ObjectHelper.GetEventObjects()
                .Where(IsVaultChest)
                .FirstOrDefault(o => ObjectHelper.DistanceToPlayer(o) <= SearchRadius);

            if (vaultChest != null)
                return vaultChest;
        }

        // Treasure 種別の宝箱。
        //
        // <b>「革袋」は除く。</b>
        // 革袋は拾うだけのもので、開けても先へ進まない。
        // 魔紋の中では EventObj の宝箱が優先されるので今まで表に出なかったが、
        // 最下層には EventObj の宝箱が無いため、革袋を宝箱と思い込んで
        // そこへ向かい続けることになる（実測 2026-09-18 17:27 に
        // 最下層で DataId 791 の革袋を確認）。
        var byKind = ObjectHelper.GetTreasures()
            .Where(o => !IsPouch(o))
            .FirstOrDefault(o => ObjectHelper.DistanceToPlayer(o) <= SearchRadius);

        if (byKind != null)
            return byKind;

        return ObjectHelper.GetEventObjects()
            .Where(IsVaultChest)
            .FirstOrDefault(o => ObjectHelper.DistanceToPlayer(o) <= SearchRadius);
    }

    /// <summary>
    /// 種類を問わず、近くにある宝箱を返す。
    ///
    /// フィールドの宝箱は Treasure、魔紋の中の宝箱は EventObj。
    /// 片方だけを見ると、もう片方を見落とす。
    /// </summary>
    internal static IGameObject? FindAnyChest() => FindChest();

    /// <summary>
    /// 今の区画にある宝箱の種類。無ければ 0。
    ///
    /// 区画ごとに ID が変わるので、これで区画の入れ替わりが分かる。
    /// </summary>
    internal static uint CurrentRoomChestId()
    {
        foreach (var o in ObjectHelper.GetEventObjects())
        {
            if (IsVaultChest(o))
                return o.BaseId;
        }

        return 0;
    }

    /// <summary>魔紋の中の宝箱か。</summary>
    internal static bool IsVaultChest(IGameObject? o)
        => o != null
        && o.BaseId >= ChestFirstId
        && o.BaseId <= ChestLastId;

    /// <summary>その仕掛けが「次の区画へ進む扉」か。</summary>
    internal static bool IsDoor(IGameObject? o)
    {
        if (o == null)
            return false;

        if (o.BaseId >= DoorFirstId && o.BaseId <= DoorLastId)
            return true;

        // ID が変わった場合の保険。名前でも見る。
        var name = o.Name.TextValue;
        return name.Contains("宝物庫の扉", StringComparison.Ordinal);
    }

    /// <summary>その仕掛けが「簡易移動」（ワープ地点）か。</summary>
    internal static bool IsWarp(IGameObject? o)
    {
        if (o == null)
            return false;

        if (o.BaseId == WarpBaseId)
            return true;

        return o.Name.TextValue.Contains("簡易移動", StringComparison.Ordinal);
    }

    /// <summary>
    /// フィールドにある「転送魔紋」（宝物庫の入り口）を探す。
    ///
    /// 宝箱を開け終わると、その場に出る。これに触れると宝物庫へ入る。
    /// </summary>
    internal static IGameObject? FindEntryPortal()
        => ObjectHelper.GetEventObjects()
            .Where(o => o.BaseId == EntryPortalBaseId
                     || o.Name.TextValue.Contains("転送魔紋", StringComparison.Ordinal))
            .OrderBy(ObjectHelper.DistanceToPlayer)
            .FirstOrDefault();

    /// <summary>
    /// 最下層（第5区画）にいるか。脱出地点2000139の存在で判断する。
    /// 扉が見えないだけでは、開扉後や読込中の区画と区別できない。
    /// </summary>
    internal static bool IsFinalRoom()
    {
        if (!IsInsideVault())
            return false;

        // <b>高さで先に切る。</b>
        //
        // 実測（2026-09-19 09:55:29）で分かったこと:
        //   まだ第4区画の扉のそば (-231, -168, -180) にいるのに、
        //   脱出地点(2000139)が「周囲」に載っていた。
        //   最下層は高さ -9 なので、160y も下にあるものが見えていたことになる。
        //
        // その結果、扉をくぐる前に「最下層だ」と判断して
        // 脱出へ移り、<b>最下層の敵をすべて素通りした</b>。
        // 敵を倒さないと宝箱が出ないので、取り分もゼロになる。
        //
        // ゲームは遠くの仕掛けも一覧に載せることがある。
        // 「見えている＝そこにいる」ではない。
        // 自分の高さが最下層のものでなければ、まだ着いていない。
        var height = CurrentHeight();

        if (height == null || MathF.Abs(height.Value - FinalRoomHeight) > FinalRoomHeightTolerance)
            return false;

        // 脱出地点があれば、そこが最下層。
        //
        // <b>これが一番確かな見分け方。</b>
        // 以前は「触れる扉が1枚も無いこと」で見分けていたが、
        // 扉は開けると触れなくなる（消えはしない）。
        // そのため、扉を開けた直後の区画も「最下層だ」と
        // 誤って判断しうる作りだった。
        //
        // 脱出地点（2000139）は最下層にしか無い。実測で確認済み。
        // 触れるかどうかは見ない（近づくまで触れないため）。
        if (ObjectHelper.GetEventObjectsIncludingUntargetable()
                .Any(o => o.BaseId == ExitPortalBaseId))
        {
            return true;
        }

        // 一時的な未読・開扉後・メンバー側では扉が見えなくなる。
        // 不在を最下層の根拠にしない。
        return false;
    }

    /// <summary>
    /// 「革袋」か。
    ///
    /// Treasure 種別だが、拾うだけのもので宝箱ではない。
    /// 実測（全ログで確認）:
    ///   DataId 791 = 「革袋」  ← これだけを除く
    ///   DataId 792 = 「宝箱」  ← <b>本物の宝箱。除いてはいけない</b>
    ///
    /// 番号が隣り合っているので、まとめて除きたくなるが間違い。
    /// 792 を除くと、最下層で宝箱を開けずに出ていくことになる。
    /// </summary>
    internal static bool IsPouch(IGameObject? o)
        => o != null
        && o.BaseId == 791;

    /// <summary>「HIGH」「LOW」の足場か。強欲の罠で使う。</summary>
    internal static bool IsGamblePad(IGameObject? o)
        => o != null
        && o.BaseId >= GambleFirstId
        && o.BaseId <= GambleLastId;

    /// <summary>近くにあるワープ地点を返す。無ければ null。</summary>
    internal static IGameObject? FindWarp()
        => ObjectHelper.GetEventObjects()
            .Where(IsWarp)
            .OrderBy(ObjectHelper.DistanceToPlayer)
            .FirstOrDefault();

    /// <summary>近くにある扉を返す。無ければ null。</summary>
    /// <summary>
    /// 向かうべき扉を返す。
    ///
    /// 左の扉を選ぶ。手順として「左上の扉へ向かう」と決まっているため。
    /// 近い方を選ぶと、立ち位置しだいで右を掴んでしまう。
    /// 左が見つからないときだけ、残っている扉を使う。
    /// </summary>
    internal static IGameObject? FindDoor(bool preferRight = false)
    {
        var doors = ObjectHelper.GetEventObjects().Where(IsDoor).ToList();

        var wanted = doors
            .Where(d => preferRight ? !IsLeftDoor(d) : IsLeftDoor(d))
            .OrderBy(ObjectHelper.DistanceToPlayer)
            .FirstOrDefault();

        return wanted ?? doors
            .OrderBy(ObjectHelper.DistanceToPlayer)
            .FirstOrDefault();
    }

    /// <summary>
    /// 脱出ポータルを探す。
    ///
    /// 最後の部屋にたどり着いたときだけ使う。
    /// 見つからなければ null を返し、呼び出し側は「出ない」を選ぶ。
    /// 勝手に出るより、その場で待つ方が安全なため。
    ///
    /// DataId 2000139だけを出口と認める。不明な仕掛けは推測で触らない。
    /// </summary>
    internal static IGameObject? FindExitPortal()
    {
        // 「脱出地点」が見えているなら、それが答え。
        //
        // 実測（2026-09-18 17:26）で DataId 2000139 と分かった。
        // 消去法で探す必要はもう無い。
        //
        // 触れるかどうかは見ない。近づくまで触れないことがあるため
        // （実測では 50y 離れた時点で 触れるか=False だった）。
        var known = ObjectHelper.GetEventObjectsIncludingUntargetable()
            .FirstOrDefault(o => o.BaseId == ExitPortalBaseId);

        if (known != null)
            return known;

        // 不明な仕掛けを出口と推測して触らない。
        return null;
    }

    /// <summary>
    /// 部屋のだいたいの中央を求める。
    ///
    /// 部屋の形はゲームからは分からないので、
    /// 今いる場所と、周りにある仕掛けの位置から見当をつける。
    /// 正確である必要はない。ポータルを探すために少し動く、その行き先として使う。
    /// </summary>
    internal static bool TryGetRoomCenter(out Vector3 center)
    {
        center = Vector3.Zero;

        if (!PlayerHelper.IsValid)
            return false;

        var points = new List<Vector3> { PlayerHelper.Position };
        points.AddRange(ObjectHelper.GetEventObjects().Select(o => o.Position));

        if (points.Count == 0)
            return false;

        var sum = Vector3.Zero;
        foreach (var p in points)
            sum += p;

        center = sum / points.Count;
        return true;
    }

    /// <summary>
    /// 今いる場所が魔紋の中か。
    ///
    /// エリアの種別を一覧で持つ方法は、拡張のたびに直す必要があって壊れやすい。
    /// 代わりに、そのエリアが「トレジャーハントのコンテンツ」かどうかを
    /// ゲームのデータに尋ねる。
    /// </summary>
    /// <summary>
    /// 今いる高さ。階層を見分けるのに使う。
    ///
    /// <b>魔紋は階層が変わってもエリア番号が変わらない（どこも 1209）。
    /// 変わるのは高さだけで、階層ごとに高さはすべて違う。</b>
    /// そのため「高さが変わった＝階層が変わった」と見てよい。
    ///
    /// 以前は「1フレームで 20y 以上動いたら搬送された」と当てていたが、
    /// それは起きたあとにしか分からず、見落とすこともあった。
    /// 高さなら、その場でいつでも確かめられる。
    ///
    /// 実測（2026-09-18・4台ぶん 15,620 件）:
    ///   立っている高さは -400 / -290 / -169 の3つに分かれ、
    ///   そのどれかにいた割合は 99.63%。
    ///   残り 0.37% は運ばれている最中のもので、そこに留まらない。
    ///
    ///   搬送の記録とも時刻が一致した:
    ///     07:49:08 ワープ → Y が -400 から -290 へ
    ///     07:51:31 ワープ → Y が -290 から -169 へ
    ///
    /// <b>高さの一覧は持たない。</b>
    /// 第4層・第5層の高さは実測できていないが、
    /// 「変わったかどうか」だけを見るので、知らない階層でも働く。
    /// </summary>
    /// <returns>今の高さ。魔紋の外なら null。</returns>
    internal static float? CurrentHeight()
        => IsInsideVault() ? PlayerHelper.Position.Y : null;

    /// <summary>
    /// 同じ階層とみなす高さの幅。
    ///
    /// 実測（2026-09-18・15,620 件）:
    ///   落ち着いて立っているときの振れは 1.2y ほど
    ///     （-400.0〜-398.8 / -290.0〜-288.8 / -169.0）。
    ///   ただし搬送の途中や段差では、同じ階層の帯の中でも
    ///   最大 32.5y 離れた高さが記録された。
    ///
    ///   一方、階層どうしの間隔は 110y 以上
    ///     （-400 → -290 が 110y、-290 → -169 が 121y）。
    ///
    /// 5y では、同じ階層の段差を「階層が変わった」と
    /// 取り違える。かといって広げすぎると本当の移動を見逃す。
    /// 実測の振れ（32.5y）より広く、階層の間隔（110y）より
    /// 十分せまい 50y を取る。
    /// </summary>
    internal const float SameFloorTolerance = 50f;

    internal static bool IsInsideVault()
    {
        try
        {
            var territoryId = PlayerHelper.TerritoryType;
            if (territoryId == 0)
                return false;

            var territory = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                                    .GetRowOrDefault(territoryId);
            if (territory == null)
                return false;

            var content = territory.Value.ContentFinderCondition.ValueNullable;
            if (content == null)
                return false;

            // ContentType 9 がトレジャーハント。
            return content.Value.ContentType.RowId == 9;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "今いるエリアの種類を調べられませんでした。");
            return false;
        }
    }
}
