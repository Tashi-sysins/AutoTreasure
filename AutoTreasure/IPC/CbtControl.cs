using ECommons.DalamudServices;
using System;
using System.IO;
using System.Linq;

namespace AutoTreasure.IPC;

/// <summary>CBT の左側のチェックを周回中だけ外す。Auto Leave の個別設定は維持する。</summary>
internal sealed class CbtControl
{
    private const string Tweak = "EnhancedDutyStartEnd";
    private TemporaryTweakSuppression? suppression;
    private long nextCheck;
    private long nextWarning;
    private bool requested;
    private ulong characterId;

    internal void Begin()
    {
        requested = true;
        Tick(force: true);
    }

    internal void End()
    {
        var wasRequested = requested;
        requested = false;
        Tick(force: wasRequested);
    }

    internal void Tick(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now < nextCheck) return;
        nextCheck = now + 1000;
        try
        {
            var cid = Svc.PlayerState.ContentId;
            if (cid != 0 && characterId != 0 && cid != characterId)
            {
                // キャラを切り替えた場合、前のキャラの記録へ上書きしない。
                suppression?.Update(false);
                suppression = null;
            }
            if (suppression == null)
            {
                // 共有設定ファイルを使う複数キャラの復元記録を混ぜない。
                if (cid == 0) return;
                characterId = cid;
                var directory = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "cbt-recovery");
                var path = Path.Combine(directory, $"{cid:X16}.txt");
                suppression = new TemporaryTweakSuppression(Read, Write,
                    () => File.Exists(path) ? bool.Parse(File.ReadAllText(path)) : null,
                    value =>
                    {
                        if (value == null) File.Delete(path);
                        else
                        {
                            Directory.CreateDirectory(directory);
                            File.WriteAllText(path, value.Value.ToString());
                        }
                    });
            }
            suppression.Update(requested);
        }
        catch (Exception ex)
        {
            if (now < nextWarning) return;
            nextWarning = now + 30000;
            Svc.Log.Warning(ex, "[AutoTreasure] CBT の脱出抑止・復元に失敗しました。再試行します。");
            Svc.Chat.PrintError("[AutoTreasure] CBT の切り替えに失敗しました。Enhanced Duty Start/End のチェックを確認してください。");
        }
    }

    private static bool? Read()
    {
        if (!Svc.PluginInterface.InstalledPlugins.Any(p => p.InternalName == "Automaton" && p.IsLoaded))
            return null;
        return Svc.PluginInterface.GetIpcSubscriber<string, bool>("Automaton.IsTweakEnabled").InvokeFunc(Tweak);
    }

    private static void Write(bool enabled)
    {
        Svc.PluginInterface.GetIpcSubscriber<string, bool, object>("Automaton.SetTweakState").InvokeAction(Tweak, enabled);
        Svc.Log.Information($"[AutoTreasure] CBT {Tweak}: {(enabled ? "ON（復元）" : "OFF（周回中の脱出抑止）")}");
    }
}
