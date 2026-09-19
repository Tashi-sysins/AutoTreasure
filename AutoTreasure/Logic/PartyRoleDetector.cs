using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using System;

namespace AutoTreasure.Logic;

/// <summary>今のパーティの状態。</summary>
internal enum PartyStatus
{
    /// <summary>まだ分からない（ログイン前など）。</summary>
    Unknown,

    /// <summary>パーティに入っていない。</summary>
    Solo,

    /// <summary>パーティのリーダー。</summary>
    Leader,

    /// <summary>パーティのメンバー。</summary>
    Member,
}

/// <summary>
/// パーティの中で自分がどの立場かを調べる。
///
/// 役割を手で設定させると、3台それぞれで設定を変える手間がかかるうえ、
/// 設定を間違えたまま動かす事故も起きる。
/// ゲームが持っている情報から自動で判断する。
///
/// リーダーは常に1人なので、取り違えようがない。
/// </summary>
internal static unsafe class PartyRoleDetector
{
    /// <summary>
    /// 今の立場を調べる。
    /// </summary>
    internal static PartyStatus Detect()
    {
        try
        {
            if (!Helpers.PlayerHelper.IsValid)
                return PartyStatus.Unknown;

            var manager = GroupManager.Instance();
            if (manager == null)
                return PartyStatus.Unknown;

            ref var group = ref manager->MainGroup;

            // 人数が2人未満なら、パーティを組んでいない。
            if (group.MemberCount < 2)
                return PartyStatus.Solo;

            // リーダーの位置にいるのが自分かどうかを見る。
            var leaderIndex = (int)group.PartyLeaderIndex;
            if (leaderIndex < 0 || leaderIndex >= group.MemberCount)
                return PartyStatus.Member;   // 読めないときは控えめに

            var leader = group.GetPartyMemberByIndex(leaderIndex);
            if (leader == null)
                return PartyStatus.Member;

            var myEntityId = Svc.Objects.LocalPlayer?.EntityId ?? 0;
            if (myEntityId == 0)
                return PartyStatus.Unknown;

            return leader->EntityId == myEntityId
                ? PartyStatus.Leader
                : PartyStatus.Member;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "パーティの状態を調べられませんでした。");
            return PartyStatus.Unknown;
        }
    }

    /// <summary>パーティの人数。組んでいなければ1。</summary>
    internal static int MemberCount
    {
        get
        {
            try
            {
                var manager = GroupManager.Instance();
                if (manager == null)
                    return 1;

                ref var group = ref manager->MainGroup;
                return Math.Max(group.MemberCount, (byte)1);
            }
            catch
            {
                return 1;
            }
        }
    }

    /// <summary>今の立場を、読める言葉で返す。</summary>
    internal static string Describe(PartyStatus status) => status switch
    {
        PartyStatus.Solo   => "パーティ未参加（ソロ）",
        PartyStatus.Leader => "パーティリーダー",
        PartyStatus.Member => "パーティメンバー",
        _                  => "確認中",
    };

    /// <summary>
    /// 待ち合わせる仲間の数（自分を除く）を決める。
    ///
    /// パーティの人数から引く。4人パーティなら 3、8人パーティなら 7。
    /// ゲームが答えを持っているのに手で設定させると、
    /// 人数が変わったときに直し忘れて、足りない人数で先へ進んでしまう。
    ///
    /// パーティを組んでいないときは、手で設定した数を使う。
    /// （パーティ外で複数台を動かす場合の逃げ道）
    /// </summary>
    internal static int ResolveExpectedMembers()
    {
        var cfg = Plugin.Config;

        var manual = Math.Clamp(cfg.ExpectedMembers, 0, Configuration.MaxMembers);

        if (!cfg.AutoExpectedMembers)
            return manual;

        // まずパーティを組んでいるかどうかを確かめる。人数を読むのはそのあと。
        //
        // 順番が逆だと取り違える。人数だけを見ると「1人」が
        //   ・本当にソロ
        //   ・まだログインしていない
        //   ・読み取りに失敗した
        // のどれなのか区別できない。読めなかっただけなのに
        // ソロとみなして少ない台数で進むと、仲間を置き去りにする。
        switch (Detect())
        {
            case PartyStatus.Solo:
                // 本当にパーティを組んでいない。待つ相手はいない。
                return 0;

            case PartyStatus.Unknown:
                // 読めなかった。ここで数を決めてはいけないので、
                // 手で決めた数を使う。
                return manual;
        }

        // ここまで来たらパーティにいる。人数を読んでよい。
        var count = MemberCount;

        // 組んでいるはずなのに人数が読めない。数を捏造せず手動値に退く。
        if (count < 2)
            return manual;

        return Math.Clamp(count - 1, 0, Configuration.MaxMembers);
    }

    /// <summary>
    /// 待ち合わせで実際に待つ台数を決める。
    ///
    /// <b>画面の表示にはこれを使わないこと。</b>
    /// つながっている台数まで下げるので、
    /// 表示に使うと「2/2」のように常に足りているように見えてしまい、
    /// 1台つながっていないことに気づけない。
    ///
    /// パーティの人数だけで決めてはいけない。
    /// 8人パーティでも、このプラグインを入れて動かしているのは
    /// 3台だけ——ということが普通にある。残りは生身の人。
    /// その場合、届くはずのない5人分の合図を待って毎回時間切れになる。
    ///
    /// そこで「パーティの人数」と「実際につながっている台数」の
    /// 小さい方を採る。つながっていない相手は待っても来ない。
    /// </summary>
    /// <param name="connectedClients">今つながっている台数。</param>
    internal static int ResolveBarrierMembers(int connectedClients)
    {
        var byParty = ResolveExpectedMembers();

        // まだ1台もつながっていないなら、判断材料が無い。
        // パーティの人数をそのまま使い、接続を待つ。
        if (connectedClients <= 0)
            return byParty;

        return Math.Min(byParty, connectedClients);
    }

    /// <summary>
    /// 立場から、このプラグインでの役割を決める。
    ///
    /// ソロのときは「単独」として動かす。
    /// 仲間との待ち合わせをせず、自分だけで進む。
    /// </summary>
    internal static ClientRole ToRole(PartyStatus status) => status switch
    {
        PartyStatus.Leader => ClientRole.Leader,
        PartyStatus.Member => ClientRole.Member,
        _                  => ClientRole.Solo,
    };
}
