using System;

namespace AutoTreasure.IPC;

/// <summary>借りる前の状態を保存し、IPC が一時的に失敗しても復元要求を失わない。</summary>
internal sealed class TemporaryTweakSuppression(
    Func<bool?> read, Action<bool> write, Func<bool?> load, Action<bool?> save)
{
    private bool? original;
    private bool loaded;

    internal void Update(bool suppress)
    {
        if (!loaded)
        {
            original = load();
            loaded = true;
        }
        var current = read();
        if (current == null) return;
        if (suppress)
        {
            if (original == null)
            {
                // CBT は設定変更を永続化するため、先に復元用の記録を残す。
                save(current);
                original = current;
            }
            if (current.Value)
            {
                write(false);
                if (read() != false) throw new InvalidOperationException("CBT の無効化を確認できませんでした。");
            }
        }
        else if (original != null)
        {
            if (current != original) write(original.Value);
            if (read() != original) return;
            save(null);
            original = null;
        }
    }
}
