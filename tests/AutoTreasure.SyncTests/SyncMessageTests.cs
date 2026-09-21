using System.Globalization;
using System.Numerics;
using AutoTreasure.Sync;
using Xunit;

namespace AutoTreasure.SyncTests;

/// <summary>
/// 段1: 合図の往復（Serialize → TryParse）。
///
/// ここが壊れていると、経路をどう変えても直らない。
/// 中継でもパイプでも、運ぶ中身はこの文字列だけ。
/// </summary>
public sealed class SyncMessageTests
{
    /// <summary>
    /// 宝の場所は、往復しても値が変わってはいけない。
    ///
    /// 座標がわずかでもずれると、着く場所がずれる。
    /// ToString("R") は往復して値が変わらない形式。
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(-123.456f, 12.5f, 987.654f)]
    [InlineData(0.1f, -0.2f, 0.3f)]
    [InlineData(float.MaxValue, float.MinValue, 1e-20f)]
    public void 宝の場所は往復しても値が変わらない(float x, float y, float z)
    {
        var original = new Vector3(x, y, z);
        var message = SyncMessage.Treasure(1191, original);

        Assert.True(SyncMessage.TryParse(message.Serialize(), out var parsed));
        Assert.True(parsed.TryGetTreasure(out var territory, out var world));

        Assert.Equal(1191u, territory);
        Assert.Equal(original.X, world.X);
        Assert.Equal(original.Y, world.Y);
        Assert.Equal(original.Z, world.Z);
    }

    /// <summary>
    /// 段階の報告は、名前と番号がそろって初めて意味がある。
    ///
    /// どちらかが欠けると待ち合わせが成立しない。
    /// </summary>
    [Fact]
    public void 段階の報告は名前と番号を保つ()
    {
        var message = SyncMessage.StepDone("Tashi Sysins", 3);

        Assert.True(SyncMessage.TryParse(message.Serialize(), out var parsed));
        Assert.Equal(SyncKind.StepDone, parsed.Kind);
        Assert.Equal("Tashi Sysins", parsed.Sender);
        Assert.True(parsed.TryGetStep(out var step));
        Assert.Equal(3, step);
    }

    /// <summary>
    /// 名前に区切り文字が混ざっても、引数の数がずれてはいけない。
    ///
    /// ずれると段階の番号を読み損ね、待ち合わせが永久に成立しない。
    /// </summary>
    [Fact]
    public void 名前に区切り文字が混ざっても引数がずれない()
    {
        var message = SyncMessage.StepDone("あ|い|う", 5);

        Assert.True(SyncMessage.TryParse(message.Serialize(), out var parsed));
        Assert.True(parsed.TryGetStep(out var step));
        Assert.Equal(5, step);
        Assert.DoesNotContain('|', parsed.Sender);
    }

    /// <summary>改行が混ざっても行が割れない（中継は1通＝1行が前提）。</summary>
    [Fact]
    public void 改行が混ざっても行が割れない()
    {
        var message = SyncMessage.Abort("落ちました\r\n次の行");
        var line = message.Serialize();

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.True(SyncMessage.TryParse(line, out _));
    }

    /// <summary>
    /// 魔紋の中の宝箱・扉は、DataId と位置がそろって意味を持つ。
    ///
    /// DataId が違えば「前の階層のもの」と分かり、
    /// まったく違う方向へ歩き出すのを防げる。
    /// </summary>
    [Fact]
    public void 魔紋の宝箱と扉はDataIdと位置を保つ()
    {
        var pos = new Vector3(12.25f, -3.5f, 44.125f);

        var chest = SyncMessage.VaultChest(2013860, pos);
        Assert.True(SyncMessage.TryParse(chest.Serialize(), out var pc));
        Assert.True(pc.TryGetVaultChest(out var chestId, out var chestPos));
        Assert.Equal(2013860u, chestId);
        Assert.Equal(pos, chestPos);

        var door = SyncMessage.VaultDoor(2013864, pos);
        Assert.True(SyncMessage.TryParse(door.Serialize(), out var pd));
        Assert.True(pd.TryGetVaultDoor(out var doorId, out var doorPos));
        Assert.Equal(2013864u, doorId);
        Assert.Equal(pos, doorPos);
    }

    /// <summary>地図役の合図は、名前と周回数を保つ。</summary>
    [Fact]
    public void 地図役の合図は名前と周回数を保つ()
    {
        var message = SyncMessage.MapUser("ヌル", 7, "sess");

        Assert.True(SyncMessage.TryParse(message.Serialize(), out var parsed));
        Assert.True(parsed.TryGetMapUser(out var name, out var laps));
        Assert.Equal("ヌル", name);
        Assert.Equal(7, laps);
    }

    /// <summary>地図の順番の設定は、枠の数と枚数を保つ。</summary>
    [Fact]
    public void 地図の順番の設定は枠と枚数を保つ()
    {
        var slots = new[] { ("Aさん", 2), ("Bさん", 1), ("Cさん", 3) };
        var message = SyncMessage.MapTurnSetting(2, slots);

        Assert.True(SyncMessage.TryParse(message.Serialize(), out var parsed));
        Assert.True(parsed.TryGetMapTurnSetting(out var mode, out var parsedSlots));

        Assert.Equal(2, mode);
        Assert.Equal(3, parsedSlots.Count);
        Assert.Equal("Aさん", parsedSlots[0].Name);
        Assert.Equal(2, parsedSlots[0].Count);
        Assert.Equal("Cさん", parsedSlots[2].Name);
        Assert.Equal(3, parsedSlots[2].Count);
    }

    /// <summary>
    /// 知らない種類は通さない。
    ///
    /// 他のプラグインと名前が重なったときの誤作動を防ぐ。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Unknown|1")]
    [InlineData("0|1")]          // 数字は enum として通ってしまうため
    [InlineData("99")]
    [InlineData("stepdone|x|1")] // 大文字小文字は区別する
    public void 知らない合図は通さない(string line)
    {
        Assert.False(SyncMessage.TryParse(line, out _));
    }

    /// <summary>引数の無い合図も往復できる。</summary>
    [Fact]
    public void 引数の無い合図も往復できる()
    {
        var message = SyncMessage.Begin();
        var line = message.Serialize();

        Assert.Equal("Begin", line);
        Assert.True(SyncMessage.TryParse(line, out var parsed));
        Assert.Equal(SyncKind.Begin, parsed.Kind);
    }

    /// <summary>
    /// すべての種類が往復できることを、総当たりで確かめる。
    ///
    /// 種類を足したとき、ここが落ちれば気づける。
    /// </summary>
    [Fact]
    public void すべての種類が往復できる()
    {
        foreach (var kind in Enum.GetValues<SyncKind>())
        {
            var message = new SyncMessage(kind, ["a", "1"]);
            var line = message.Serialize();

            Assert.True(SyncMessage.TryParse(line, out var parsed),
                $"{kind} が往復できませんでした（{line}）");

            Assert.Equal(kind, parsed.Kind);
        }
    }
}
