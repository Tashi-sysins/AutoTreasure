using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace AutoTreasure.Sync;

/// <summary>やり取りする合図の種類。</summary>
internal enum SyncKind
{
    /// <summary>宝の場所を配る。リーダーだけが送る。</summary>
    Treasure,

    /// <summary>「この段階を終えた」の報告。メンバーが送る。</summary>
    StepDone,

    /// <summary>「全員そろったので次へ」の合図。リーダーが送る。</summary>
    StepGo,

    /// <summary>止める。誰でも送れる。</summary>
    Abort,

    /// <summary>
    /// どちらの扉へ向かうか。
    ///
    /// 光った扉がある区画では、光っていない側は選べない。
    /// リーダーが右へ切り替えたとき、メンバーにも伝えないと
    /// 別々の扉に分かれて置き去りが出る。
    /// </summary>
    DoorSide,

    /// <summary>
    /// 周回を始める合図。リーダーだけが送る。
    ///
    /// 3台それぞれで開始を押させると、押し忘れや押す順の違いで
    /// 足並みが揃わない。リーダーが押したら全員が始まるようにする。
    /// </summary>
    Begin,

    /// <summary>
    /// 魔紋の中の宝箱の位置。リーダーだけが送る。
    ///
    /// メンバーは、魔紋の中で宝箱に近づけないことがある。
    /// 実測（2026-09-18 第2層）では、着いた瞬間から戦闘が始まり、
    /// 敵を倒し終えた時点で宝箱はもう開けられていた。
    /// そのため宝箱の位置を覚えられず、扉の先へ進む向きが決まらない。
    ///
    /// 位置だけでなく宝箱の DataId も一緒に送る。
    /// 区画ごとに ID が変わる（2013860〜2013863）ので、
    /// 受け取った側は「今の区画のものか」を確かめられる。
    /// 前の階層の位置を使って、まったく違う方向へ歩き出すのを防ぐ。
    /// </summary>
    VaultChest,

    /// <summary>
    /// 魔紋の中の扉の位置。リーダーだけが送る。
    ///
    /// メンバーは、扉が見えないことがある。
    /// 実測（2026-09-18 第2層以降）では、触れる仕掛けが 0 件で、
    /// 扉を見つけられないまま立ち尽くしていた。
    /// 扉が見つからないと「扉へ向かう」処理に入れず、
    /// その先の「扉の奥へ進む」にも進めない。
    ///
    /// 扉の DataId も一緒に送る。区画ごとに変わる
    /// （2013864〜2013871）ので、受け取った側は
    /// 今の区画のものかを確かめられる。
    /// </summary>
    VaultDoor,

    /// <summary>
    /// この周回で地図を使う人。リーダーだけが送る。
    ///
    /// <b>これが「誰が触れるか」を決める。</b>
    /// ゲーム側の決まりで、古ぼけた地図S5 を使った人しか
    /// 宝箱・魔紋・魔紋内の扉と宝箱に触れない。
    /// 触る人を1台に絞るための取り決めではなく、
    /// そもそも他の人には触れないという制約。
    ///
    /// 名前と、何周目かを一緒に送る。
    /// 周回数は、受け取った側が古い合図を捨てるのに使う。
    /// </summary>
    MapUser,

    /// <summary>生きているかの確認。</summary>
    Ping,

    /// <summary>選ばれた地図役本人から、未解読・解読済みとも所持なしを報告。</summary>
    MapUnavailable,

    /// <summary>
    /// 地図の使用順番の設定。リーダーだけが送る。
    ///
    /// <b>なぜ配るのか。</b>
    /// 決めるのはリーダーだけなので、動きの上では配らなくても回る。
    /// だが配らないと、メンバーの画面には自分の機の古い設定が出たままになり、
    /// 「リーダーで直したのに反映されない」と見える。
    ///
    /// 中身は「決め方｜名前×枚数｜名前×枚数…」。
    /// </summary>
    MapTurnSetting,
}

/// <summary>
/// クライアント同士でやり取りする合図。
///
/// 中身は「種類｜値｜値…」という単純な文字列。
/// 同じパソコンの中だけで使うので、暗号化や認証は省いている。
/// </summary>
internal readonly record struct SyncMessage(SyncKind Kind, string[] Args)
{
    private const char Separator = '|';

    /// <summary>宝の場所を伝える合図を作る。</summary>
    internal static SyncMessage Treasure(uint territoryType, Vector3 world)
        => new(SyncKind.Treasure,
        [
            territoryType.ToString(CultureInfo.InvariantCulture),
            world.X.ToString("R", CultureInfo.InvariantCulture),
            world.Y.ToString("R", CultureInfo.InvariantCulture),
            world.Z.ToString("R", CultureInfo.InvariantCulture),
        ]);

    /// <summary>段階を終えたことを伝える合図を作る。</summary>
    internal static SyncMessage StepDone(string sender, int step)
        // 名前も整えてから送る。
        // 区切り文字が混ざると引数の数がずれ、
        // 段階の番号を読み損ねて待ち合わせが永久に成立しなくなる。
        => new(SyncKind.StepDone, [Sanitize(sender), step.ToString(CultureInfo.InvariantCulture)]);

    /// <summary>次へ進んでよいことを伝える合図を作る。</summary>
    /// <summary>生きているかを確かめる合図。</summary>
    internal static SyncMessage Ping(string sender)
        => new(SyncKind.Ping, [sender]);

    /// <summary>周回を始める合図。</summary>
    internal static SyncMessage Begin() => new(SyncKind.Begin, []);

    /// <summary>向かう扉を伝える。true なら右。</summary>
    internal static SyncMessage DoorSide(bool right)
        => new(SyncKind.DoorSide, [right ? "R" : "L"]);

    /// <summary>
    /// 魔紋の中の扉の位置を伝える。
    ///
    /// 扉の DataId を添えるのは、受け取った側が
    /// 「今いる区画のものか」を確かめられるようにするため。
    /// </summary>
    internal static SyncMessage VaultDoor(uint doorDataId, Vector3 world)
        => new(SyncKind.VaultDoor,
        [
            doorDataId.ToString(CultureInfo.InvariantCulture),
            world.X.ToString("R", CultureInfo.InvariantCulture),
            world.Y.ToString("R", CultureInfo.InvariantCulture),
            world.Z.ToString("R", CultureInfo.InvariantCulture),
        ]);

    /// <summary>魔紋の中の宝箱の位置を伝える。</summary>
    internal static SyncMessage VaultChest(uint chestDataId, Vector3 world)
        => new(SyncKind.VaultChest,
        [
            chestDataId.ToString(CultureInfo.InvariantCulture),
            world.X.ToString("R", CultureInfo.InvariantCulture),
            world.Y.ToString("R", CultureInfo.InvariantCulture),
            world.Z.ToString("R", CultureInfo.InvariantCulture),
        ]);

    internal static SyncMessage StepGo(int step)
        => new(SyncKind.StepGo, [step.ToString(CultureInfo.InvariantCulture)]);

    /// <summary>止めることを伝える合図を作る。</summary>
    internal static SyncMessage Abort(string reason)
        => new(SyncKind.Abort, [Sanitize(reason)]);

    /// <summary>
    /// この周回で地図を使う人を伝える。
    ///
    /// 名前は必ず整えてから送る。
    /// キャラクター名に区切り文字は入らないはずだが、
    /// 混ざると受け取る側で引数がずれ、
    /// まったく別の人が地図役だと解釈されてしまう。
    /// </summary>
    internal static SyncMessage MapUser(string name, int lapsDone, string session = "")
        => new(SyncKind.MapUser, [Sanitize(name), lapsDone.ToString(CultureInfo.InvariantCulture), Sanitize(session)]);

    /// <summary>
    /// 地図役の合図を読み取る。
    /// </summary>
    internal bool TryGetMapUser(out string name, out int lapsDone)
    {
        name = "";
        lapsDone = 0;

        if (Kind != SyncKind.MapUser || Args.Length < 2)
            return false;

        name = Args[0];

        if (string.IsNullOrEmpty(name))
            return false;

        return int.TryParse(Args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out lapsDone) && lapsDone >= 0;
    }

    internal static SyncMessage MapUnavailable(string name, int lap)
        => new(SyncKind.MapUnavailable, [Sanitize(name), lap.ToString(CultureInfo.InvariantCulture)]);

    internal bool TryGetMapUnavailable(out string name, out int lap)
    {
        name = Args.Length > 0 ? Args[0] : "";
        lap = 0;
        return Kind == SyncKind.MapUnavailable && Args.Length == 2 && name.Length > 0
            && int.TryParse(Args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out lap)
            && lap >= 0;
    }

    /// <summary>
    /// 地図の使用順番の設定を伝える。
    ///
    /// 「決め方｜名前×枚数｜名前×枚数…」の形で送る。
    /// 名前に区切り文字が混ざると引数がずれるので、必ず整えてから入れる。
    /// 枚数は名前と分けず、1つの引数にまとめる
    /// （引数の数で枠の数が分かるようにするため）。
    /// </summary>
    internal static SyncMessage MapTurnSetting(int mode, IEnumerable<(string Name, int Count)> slots)
    {
        var args = new List<string> { mode.ToString(CultureInfo.InvariantCulture) };

        foreach (var (name, count) in slots)
        {
            if (string.IsNullOrEmpty(name))
                continue;

            // 名前の中の「*」も潰しておく。区切りに使っているため。
            var safe = Sanitize(name).Replace('*', '_');
            args.Add($"{safe}*{count.ToString(CultureInfo.InvariantCulture)}");
        }

        return new SyncMessage(SyncKind.MapTurnSetting, [.. args]);
    }

    /// <summary>地図の使用順番の設定を読み取る。</summary>
    internal bool TryGetMapTurnSetting(out int mode, out List<(string Name, int Count)> slots)
    {
        mode = 0;
        slots = [];

        if (Kind != SyncKind.MapTurnSetting || Args.Length < 1)
            return false;

        if (!int.TryParse(Args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out mode))
            return false;

        for (var i = 1; i < Args.Length; i++)
        {
            var parts = Args[i].Split('*');

            if (parts.Length != 2 || parts[0].Length == 0)
                continue;

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                count = 1;

            slots.Add((parts[0], Math.Max(1, count)));
        }

        return true;
    }

    /// <summary>
    /// 1行に収まる形へ整える。
    /// 区切り文字と改行が混ざると、受け取る側で行が割れて壊れる。
    /// </summary>
    private static string Sanitize(string text)
    {
        // 区切り文字と改行を取り除く。混ざると受け取る側で行が割れて壊れる。
        var cleaned = text.Replace(Separator, '/');
        return string.Concat(cleaned.Select(c => char.IsControl(c) ? ' ' : c));
    }

    internal string Serialize()
        => Args.Length == 0
            ? Kind.ToString()
            : Kind + Separator.ToString() + string.Join(Separator, Args);

    internal static bool TryParse(string line, out SyncMessage message)
    {
        message = default;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var parts = line.Split(Separator);

        var head = parts[0];

        // 数字の文字列は受け付けない。
        //
        // ⚠ Enum.IsDefined だけでは足りない（2026-09-22 実測）。
        //   Enum.TryParse("0") は Treasure として通り、
        //   IsDefined も true を返す。範囲内の数字はすべて素通りしていた。
        //   弾けていたのは "99" のような範囲外の数字だけ。
        //
        //   中継サーバー経由では、届く文字列が同じPCの中とは限らない。
        //   壊れた相手や版違いが投げたものを「宝の場所」と解釈すると、
        //   まったく違う座標へ歩き出す。頭が数字なら、その場で捨てる。
        if (head.Length == 0 || char.IsAsciiDigit(head[0]) || head[0] is '-' or '+')
            return false;

        // 定義されている名前かどうかまで確かめる。
        // 他のプラグインと名前が重なったときの誤作動を防ぐ。
        if (!Enum.TryParse<SyncKind>(head, ignoreCase: false, out var kind)
            || !Enum.IsDefined(kind))
            return false;

        message = new SyncMessage(kind, parts[1..]);
        return true;
    }

    /// <summary>宝の場所の合図から中身を取り出す。</summary>
    internal bool TryGetTreasure(out uint territoryType, out Vector3 world)
    {
        territoryType = 0;
        world = default;

        if (Kind != SyncKind.Treasure || Args.Length < 4)
            return false;

        return uint.TryParse(Args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out territoryType)
            && TryParseFloat(Args[1], out world.X)
            && TryParseFloat(Args[2], out world.Y)
            && TryParseFloat(Args[3], out world.Z);
    }

    /// <summary>魔紋の扉の位置と、その扉の DataId を取り出す。</summary>
    internal bool TryGetVaultDoor(out uint doorDataId, out Vector3 world)
    {
        doorDataId = 0;
        world = default;

        if (Kind != SyncKind.VaultDoor || Args.Length < 4)
            return false;

        return uint.TryParse(Args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out doorDataId)
            && TryParseFloat(Args[1], out world.X)
            && TryParseFloat(Args[2], out world.Y)
            && TryParseFloat(Args[3], out world.Z);
    }

    /// <summary>魔紋の宝箱の位置と、その宝箱の DataId を取り出す。</summary>
    internal bool TryGetVaultChest(out uint chestDataId, out Vector3 world)
    {
        chestDataId = 0;
        world = default;

        if (Kind != SyncKind.VaultChest || Args.Length < 4)
            return false;

        return uint.TryParse(Args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out chestDataId)
            && TryParseFloat(Args[1], out world.X)
            && TryParseFloat(Args[2], out world.Y)
            && TryParseFloat(Args[3], out world.Z);
    }

    /// <summary>段階の番号を取り出す。</summary>
    internal bool TryGetStep(out int step)
    {
        step = 0;
        var index = Kind == SyncKind.StepDone ? 1 : 0;
        return Args.Length > index
            && int.TryParse(Args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out step);
    }

    /// <summary>送り主の名前を取り出す（StepDone のみ）。</summary>
    internal string Sender => Kind == SyncKind.StepDone && Args.Length > 0 ? Args[0] : "";

    private static bool TryParseFloat(string text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
