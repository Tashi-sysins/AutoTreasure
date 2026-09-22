using AutoTreasure.IPC;
using Xunit;

namespace AutoTreasure.SyncTests;

public class TemporaryTweakSuppressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartupWithoutRunRestoresCrashJournal(bool original)
    {
        bool? state = false, saved = original;
        var guard = new TemporaryTweakSuppression(() => state, x => state = x, () => saved, x => saved = x);
        guard.Update(false);
        Assert.Equal(original, state);
        Assert.Null(saved);
    }

    [Fact]
    public void IgnoredDisableIsReportedAndRetried()
    {
        bool? state = true, saved = null;
        var ignore = true;
        var guard = new TemporaryTweakSuppression(() => state, x => { if (!ignore) state = x; },
            () => saved, x => saved = x);
        Assert.Throws<InvalidOperationException>(() => guard.Update(true));
        Assert.True(saved);
        ignore = false;
        guard.Update(true);
        Assert.False(state);
        guard.Update(false);
        Assert.True(state);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedBeginAndManualEnablePreserveOriginal(bool original)
    {
        bool? state = original, saved = null;
        var guard = new TemporaryTweakSuppression(() => state, x => state = x, () => saved, x => saved = x);
        guard.Update(true);
        guard.Update(true);
        Assert.False(state);
        state = true; // 周回中に手動で有効化されても再度外す。
        guard.Update(true);
        Assert.False(state);
        guard.Update(false);
        Assert.Equal(original, state);
        Assert.Null(saved);
    }

    [Fact]
    public void UnavailablePluginAndFailedRestoreKeepRecovery()
    {
        bool? state = null, saved = null;
        var fail = false;
        var guard = new TemporaryTweakSuppression(() => state,
            x => { if (fail) throw new InvalidOperationException(); state = x; },
            () => saved, x => saved = x);
        guard.Update(true);
        Assert.Null(saved);
        state = true;
        guard.Update(true);
        state = null; // CBT の読み直し中に停止しても復元要求は残す。
        guard.Update(false);
        Assert.True(saved);
        state = false;
        fail = true;
        Assert.Throws<InvalidOperationException>(() => guard.Update(false));
        Assert.True(saved);
        fail = false;
        guard.Update(false);
        Assert.True(state);
        Assert.Null(saved);
    }

    [Fact]
    public void RestartRestoresJournalAndDoesNotOverwriteItOnResume()
    {
        bool? state = false, saved = true;
        var guard = new TemporaryTweakSuppression(() => state, x => state = x, () => saved, x => saved = x);
        guard.Update(true);
        Assert.True(saved);
        guard.Update(false);
        Assert.True(state);
        Assert.Null(saved);
    }

    [Fact]
    public void FailedJournalWriteMustNotDisablePlugin()
    {
        bool? state = true;
        var guard = new TemporaryTweakSuppression(() => state, x => state = x, () => null,
            _ => throw new IOException());
        Assert.Throws<IOException>(() => guard.Update(true));
        Assert.True(state);
    }
}
