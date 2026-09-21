using ECommons.DalamudServices;
using System;
using System.Security.Cryptography;
using System.Text;

namespace AutoTreasure.Sync.Relay;

/// <summary>
/// 同じパーティーの人だけが同じ値を出せる合言葉を作る。
///
/// これがあると、招待をコピーして貼り付ける手間が要らない。
/// 全員がこの値でサーバーに尋ね、同じ部屋へ入る。
/// </summary>
internal static class PartyKey
{
    /// <summary>
    /// 合言葉の頭。
    ///
    /// ⚠ MogColle は "mogcolle-party:" を使っている。
    ///   同じにすると、同じパーティーで両方のプラグインを使ったとき
    ///   部屋が混ざる。必ず別の語にすること。
    /// </summary>
    private const string Prefix = "autotreasure-party:";

    /// <summary>
    /// パーティーリーダーのキャラクターIDから作る。
    ///
    /// なぜ EntityId ではないか:
    ///   PartyRoleDetector は EntityId でリーダーを判定しているが、
    ///   <b>EntityId はログインのたびに変わる</b>ので合言葉には使えない。
    ///   ContentId は変わらない。
    ///
    ///   （役割の判定に EntityId を使うのは問題ない。
    ///     その場かぎりの比較で、覚えておく必要がないため。
    ///     PartyRoleDetector はそのまま残す。）
    ///
    /// なぜハッシュにするか:
    ///   ContentId は個人を特定できる値。
    ///   サーバーには「同じパーティーか」さえ分かればよく、
    ///   誰かを知る必要はない。
    /// </summary>
    internal static string Current()
    {
        var leader = LeaderContentId();

        if (leader == 0)
            return string.Empty;           // 分からないときは繋がない

        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Prefix}{leader}"));

        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// パーティーリーダーのキャラクターID。読めなければ 0。
    ///
    /// ソロのときも 0。判断できないので繋がない。
    ///
    /// ⚠ エリア移動中は ContentId が 0 になることがある。
    ///   0 を「繋がない」に倒しているのはそのため。
    ///   「読めない一瞬」を「パーティーがいない」と解釈すると、
    ///   ロード中に切断してしまう。
    ///
    /// Svc.Party を使う理由:
    ///   PartyRoleDetector のように GroupManager を直接読む手もあるが、
    ///   Svc.Party のほうが安全（unsafe が要らず、Dalamud 側が
    ///   null を面倒みてくれる）。
    /// </summary>
    private static ulong LeaderContentId()
    {
        try
        {
            var party = Svc.Party;

            if (party is null || party.Length < 2)
                return 0;                  // パーティーを組んでいない

            var index = (int)party.PartyLeaderIndex;

            if (index < 0 || index >= party.Length)
                return 0;

            return party[index]?.ContentId ?? 0;
        }
        catch
        {
            return 0;                      // 読めないときは繋がない
        }
    }
}
