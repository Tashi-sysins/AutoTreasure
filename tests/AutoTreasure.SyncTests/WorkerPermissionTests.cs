using System.Linq;
using System.Text.Json;
using AutoTreasure.RelayServer.Rooms;
using AutoTreasure.Sync;
using AutoTreasure.Sync.Relay;
using Xunit;

namespace AutoTreasure.SyncTests;

/// <summary>
/// メンバーが送ってよい合図の判定。
///
/// <b>なぜこの試験が要るか（2026-09-22 の実機で起きた）。</b>
///
/// 地図役をメンバーにして周回したところ、
/// <b>地図役だけが宝箱へ向かい、他の全員がエーテライトで棒立ち</b>になった。
///
/// 原因は、サーバーが Treasure を「リーダーだけ」に限っていたこと。
/// 地図役がメンバーだと座標が誰にも届かず、
/// 全員が「場所が届くのを待つ」まま止まっていた。
///
/// <b>地図役はパーティリーダーとは限らない。</b>
/// 古ぼけた地図S5 は誰でも使え、使った人がその周回の地図役になる。
/// そしてその人しか宝の場所を知らない。
/// </summary>
public sealed class WorkerPermissionTests
{
    /// <summary>
    /// 地図役が配るものは、メンバーからでも通さなければならない。
    ///
    /// ここが false に戻ると、地図役がメンバーの周回が
    /// まるごと動かなくなる。
    /// </summary>
    [Theory]
    [InlineData("Treasure")]     // 宝の場所
    [InlineData("VaultChest")]   // 魔紋の中の宝箱
    [InlineData("VaultDoor")]    // 魔紋の中の扉
    [InlineData("DoorSide")]     // どちらの扉へ向かうか
    public void 地図役が配るものはメンバーからでも通す(string kind)
    {
        var envelope = Wrap($"{kind}|1191|1.0|2.0|3.0");

        Assert.True(RoomManager.IsAllowedFromWorker(envelope),
            $"{kind} がメンバーから送れません。"
            + "地図役がメンバーだと、この合図が誰にも届かなくなります。");
    }

    /// <summary>誰でも送るものは、当然メンバーからも通す。</summary>
    [Theory]
    [InlineData("StepDone")]
    [InlineData("Ping")]
    [InlineData("MapUser")]
    [InlineData("MapUnavailable")]
    [InlineData("Abort")]        // 異常に気づいた人が全体を止められる
    public void 誰でも送るものはメンバーからも通す(string kind)
    {
        var envelope = Wrap($"{kind}|なまえ|1");

        Assert.True(RoomManager.IsAllowedFromWorker(envelope), $"{kind} が送れません。");
    }

    /// <summary>
    /// 全体を動かす号令は、メンバーからは通さない。
    ///
    /// ここを緩めると、誰でも他人の周回を始めたり
    /// 待ち合わせを勝手に抜けさせたりできてしまう。
    /// </summary>
    [Theory]
    [InlineData("Begin")]           // 開始
    [InlineData("StepGo")]          // 次へ進め
    [InlineData("MapTurnSetting")]  // 順番の決め
    public void 全体を動かす号令はメンバーから通さない(string kind)
    {
        var envelope = Wrap($"{kind}|1");

        Assert.False(RoomManager.IsAllowedFromWorker(envelope),
            $"{kind} がメンバーから送れてしまいます。");
    }

    /// <summary>
    /// 読めないものは通さない。
    ///
    /// AutoTreasure の合図は文字列と決まっている。
    /// 中身が読めない時点でおかしい。
    /// </summary>
    [Fact]
    public void 読めないものは通さない()
    {
        // 中身が無い
        Assert.False(RoomManager.IsAllowedFromWorker(new RelayEnvelope()));

        // 文字列ではなくオブジェクト（MogColle の形）
        Assert.False(RoomManager.IsAllowedFromWorker(new RelayEnvelope
        {
            Payload = JsonSerializer.SerializeToElement(new { type = "state" }),
        }));

        // 知らない種類
        Assert.False(RoomManager.IsAllowedFromWorker(Wrap("Unknown|1")));
    }

    /// <summary>
    /// <b>SyncKind を足したら、ここで気づけるようにする。</b>
    ///
    /// 足したのに一覧へ入れ忘れると、そのメッセージだけ
    /// メンバーから送れず、原因が分かりにくい不具合になる。
    ///
    /// この試験が落ちたら、新しい種類を
    /// 「地図役が配るもの」か「リーダーだけの号令」か決めて、
    /// 下の一覧とサーバーの両方へ足すこと。
    /// </summary>
    [Fact]
    public void 種類を足したら仕分けを見直す()
    {
        // リーダーだけが送ると決めたもの。
        var leaderOnly = new[] { "Begin", "StepGo", "MapTurnSetting" };

        foreach (var kind in Enum.GetNames(typeof(SyncMessageKindProbe)))
        {
            var allowed = RoomManager.IsAllowedFromWorker(Wrap($"{kind}|1"));

            var shouldAllow = !leaderOnly.Contains(kind);

            Assert.True(allowed == shouldAllow,
                $"{kind} の扱いが決まっていません。"
                + $"（いまは {(allowed ? "メンバーも送れる" : "リーダーだけ")}）"
                + "新しく足した種類なら、仕分けを決めて"
                + "RoomManager.IsAllowedFromWorker とこの試験の両方へ反映してください。");
        }
    }

    /// <summary>本番と同じ形で封筒に入れる。</summary>
    /// <summary>
    /// 控えの種類が、本番と食い違っていないか。
    ///
    /// 本番に足して控えに足し忘れると、上の総当たりが
    /// 新しい種類を素通りしてしまう。
    /// </summary>
    [Fact]
    public void 控えの種類が本番と一致する()
    {
        var real = Enum.GetNames<SyncKind>().OrderBy(x => x).ToArray();
        var probe = Enum.GetNames<SyncMessageKindProbe>().OrderBy(x => x).ToArray();

        Assert.True(real.SequenceEqual(probe),
            "SyncKind と控え（SyncMessageKindProbe）が食い違っています。"
            + $"本番: {string.Join(" / ", real.Except(probe))} が控えにありません。"
            + $"控え: {string.Join(" / ", probe.Except(real))} が本番にありません。");
    }

    private static RelayEnvelope Wrap(string line) => new()
    {
        Type = RelayMessageType.Relay,
        Payload = JsonSerializer.SerializeToElement(line),
    };
}

/// <summary>
/// 試験から名前で回すための控え。
///
/// 本番の SyncKind は internal なので、公開が必要な xUnit の
/// [InlineData] へ直接渡せない。名前だけを写してある。
///
/// ⚠ 本番に種類を足したら、ここにも足すこと。
///   忘れても「種類の数が合いません」で気づけるようにしてある。
/// </summary>
internal enum SyncMessageKindProbe
{
    Treasure,
    StepDone,
    StepGo,
    Abort,
    DoorSide,
    Begin,
    VaultChest,
    VaultDoor,
    MapUser,
    Ping,
    MapUnavailable,
    MapTurnSetting,
}
