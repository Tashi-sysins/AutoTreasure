using System.Security.Cryptography;

namespace AutoTreasure.RelayServer.Rooms;

/// <summary>
/// ルームの合言葉と合鍵を作る。
///
/// なぜ単純な6桁数字にしないか:
///   100万通りしかなく、総当たりで他人のルームへ入られる。
///   人が読み上げられる長さを保ちつつ、桁数を増やす。
///
/// 読み間違えやすい文字（O/0、I/1、L、U）を外した30文字を使う。
/// 10桁で約 5.9×10^14 通り。読み上げられる長さのまま、総当たりには耐える。
/// </summary>
public static class RoomCode
{
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>表示用の合言葉。例 7K3M-P9TW-XQ</summary>
    public static string NewCode()
    {
        var raw = Pick(10);
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..]}";
    }

    /// <summary>
    /// 参加に要る合鍵。表示用の合言葉とは別にする。
    ///
    /// 合言葉だけで入れると、総当たりされたとき防げない。
    /// </summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    /// <summary>見た目の違いを吸収して比べる（大小・区切りを無視）。</summary>
    public static string Normalize(string? code)
        => (code ?? string.Empty).Replace("-", string.Empty).Trim().ToUpperInvariant();

    private static string Pick(int count)
    {
        var chars = new char[count];

        for (var i = 0; i < count; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];

        return new string(chars);
    }

    /// <summary>
    /// 合鍵を照合する。
    ///
    /// 長さや内容で処理時間が変わらない比べ方をする。
    /// 時間の差から中身を当てられるのを防ぐため。
    /// </summary>
    public static bool TokenMatches(string? given, byte[] expectedHash)
    {
        if (string.IsNullOrEmpty(given))
            return false;

        var actual = Hash(given);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    /// <summary>合鍵はそのまま持たず、ハッシュにして持つ。</summary>
    public static byte[] Hash(string token)
        => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
}
