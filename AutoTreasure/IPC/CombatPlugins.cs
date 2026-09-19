using ECommons.DalamudServices;
using System;
using System.Collections.Generic;

namespace AutoTreasure.IPC;

/// <summary>
/// 戦闘を受け持つプラグイン（RSR / BMR）を、こちらから操作する。
///
/// 戦い方そのものは作らない。そのために作られたものに任せる。
/// ここでやるのは「周回の間だけ、正しい設定で動かす」こと。
///
/// 手で切り替えていると、3台のうち1台だけ設定し忘れる事故が起きる。
/// 実際、ヒーラーだけ何もしない、という状態が起きていた。
///
/// IPC の名前は、それぞれのソースで確認した:
///   RSR: RotationSolverReborn/RotationSolver/IPC/IPCProvider.cs
///   BMR: BossmodReborn/BossMod/Framework/IPCProvider.cs
/// </summary>
internal static class CombatPlugins
{
    // ---- RotationSolverReborn ----------------------------------------------

    private const string RsrPrefix = "RotationSolverReborn.";

    /// <summary>RSR の動作モード。RSCommandType.cs の StateCommandType と同じ並び。</summary>
    internal enum RsrMode : byte
    {
        Off = 0,
        Auto = 1,
        TargetOnly = 2,
        Manual = 3,
        AutoDuty = 4,
        Henched = 5,
    }

    /// <summary>RSR が入っているか。</summary>
    internal static bool RsrAvailable => HasPlugin("RotationSolver");

    /// <summary>
    /// RSR の動作モードを変える。
    /// 周回中は Manual を使う（自分で撃たず、こちらの指示で動く形）。
    /// </summary>
    internal static bool SetRsrMode(RsrMode mode)
        => Invoke<byte>(RsrPrefix + "ChangeOperatingMode", (byte)mode);

    /// <summary>
    /// 優先して倒す相手を登録する。
    ///
    /// トレジャーハントでは、特定の敵を先に倒すと追加の宝箱が出る。
    /// どれを先に倒すかで結果が変わるため、ここで指定する。
    /// </summary>
    internal static bool AddPriorityTarget(uint nameId)
        => Invoke<uint>(RsrPrefix + "AddPriorityNameID", nameId);

    /// <summary>優先指定を外す。</summary>
    internal static bool RemovePriorityTarget(uint nameId)
        => Invoke<uint>(RsrPrefix + "RemovePriorityNameID", nameId);

    /// <summary>
    /// 攻撃してはいけない相手を登録する。
    ///
    /// 周回中は、地図で湧いた敵だけを相手にする。
    /// そのへんのフィールドの敵に手を出すと、余計な戦闘で時間を失う。
    /// </summary>
    internal static bool AddBlacklist(uint nameId)
        => Invoke<uint>(RsrPrefix + "AddBlacklistNameID", nameId);

    /// <summary>除外指定を外す。</summary>
    internal static bool RemoveBlacklist(uint nameId)
        => Invoke<uint>(RsrPrefix + "RemoveBlacklistNameID", nameId);

    /// <summary>RSR が今動いているか。</summary>
    internal static bool? RsrActive()
        => InvokeFunc<bool>(RsrPrefix + "AutorotationActive");

    // ---- BossmodReborn ------------------------------------------------------

    private const string BmrPrefix = "BossMod.";

    /// <summary>BMR が入っているか。</summary>
    internal static bool BmrAvailable => HasPlugin("BossMod");

    /// <summary>今有効なプリセットの名前。無ければ null。</summary>
    internal static string? GetActivePreset()
        => InvokeFunc<string?>(BmrPrefix + "Presets.GetActive");

    /// <summary>
    /// プリセットを有効にする。
    ///
    /// 「AutoDuty Passive LB」を想定している。
    /// 移動と範囲攻撃の回避を受け持たせ、攻撃と回復は RSR に任せる。
    /// </summary>
    internal static bool SetActivePreset(string name)
        => InvokeFunc<string, bool>(BmrPrefix + "Presets.SetActive", name) is true;

    /// <summary>プリセットを解除する。</summary>
    internal static bool ClearPreset()
        => Invoke(BmrPrefix + "Presets.ClearActive");

    /// <summary>そのプリセットが BMR にあるか。</summary>
    internal static bool HasPreset(string name)
        => InvokeFunc<string, string?>(BmrPrefix + "Presets.Get", name) != null;

    /// <summary>
    /// プリセットを作る。
    ///
    /// <b>「AutoDuty Passive LB」は AutoDuty には入っていない。</b>
    /// AutoDuty が配るのは「AutoDuty」と「AutoDuty Passive」の2つだけで、
    /// LB 版は手で作られたものだった（2026-09-18 に実機で確認）。
    ///
    /// そのため、これを前提にすると他の人の環境では動かない。
    /// 無ければこちらで作る。
    /// </summary>
    internal static bool CreatePreset(string json, bool overwrite = false)
        => InvokeFunc<string, bool, bool>(BmrPrefix + "Presets.Create", json, overwrite) is true;

    /// <summary>
    /// 周回で使うプリセットが無ければ作る。
    ///
    /// 中身は「移動と立ち位置は BMR、攻撃は RSR」という分担に、
    /// リミットブレイクを撃つ設定を足したもの。
    /// タンクの防御は切ってある（魔紋では要らないうえ、
    /// 使うと敵を集めてしまう）。
    /// </summary>
    internal static bool EnsureRunPreset()
    {
        if (!BmrAvailable)
            return false;

        // フィールド用と魔紋用の2つを用意する。
        // 場面で使い分けないと、どちらかで不都合が出る。
        var ok = true;

        foreach (var (name, json) in new[]
        {
            (FieldPresetName, FieldPresetJson),
            (VaultPresetName, VaultPresetJson),
        })
        {
            if (HasPreset(name))
                continue;

            var made = CreatePreset(json, overwrite: false);
            ok &= made;

            Svc.Log.Information(made
                ? $"[AutoTreasure] BMR にプリセット「{name}」を作りました。"
                : $"[AutoTreasure] BMR のプリセット「{name}」を作れませんでした。");
        }

        return ok;
    }

    /// <summary>周回で使うプリセットの名前。</summary>
    /// <summary>
    /// フィールドで使うプリセット。
    ///
    /// 攻撃はするが、<b>自分から敵を探しに行かない</b>。
    /// AutoDuty から「Everything（周りの敵を片端から狙う）」を外してある。
    /// これが入っていると、地図と関係ない雑魚に絡まれて時間を失う。
    /// </summary>
    internal const string FieldPresetName = "Treasure Field";

    /// <summary>
    /// 魔紋の中で使うプリセット。
    ///
    /// AutoDuty と同じ中身（Everything を入れたまま）。
    /// 魔紋は封鎖された部屋で、いるのは倒すべき敵だけなので、
    /// 片端から狙って構わない。
    ///
    /// <b>最下層はこれでないと攻撃が始まらない。</b>
    /// 最下層には同じ名前・同じ座標の相手が10体いて、
    /// そのうち9体は触れない偽物。
    /// Everything が無いと、本物を狙えずに立ち尽くす（実測 2026-09-19）。
    /// </summary>
    internal const string VaultPresetName = "Treasure Vault";

    /// <summary>既定で使う方（フィールド）。</summary>
    internal const string RunPresetName = FieldPresetName;

    /// <summary>
    /// 周回で使うプリセットの中身。
    ///
    /// 「移動と立ち位置は BMR、攻撃は RSR」という分担。
    ///
    /// 実機で使われていた「AutoDuty Passive LB」を土台に、
    /// 宝の周回に合わせて2つ変えてある（利用者の指定・2026-09-18）。
    ///
    ///   ・<b>リミットブレイクは撃たない</b>
    ///     各ジョブの LB 設定を全部落とした。
    ///     雑魚相手に使っても意味がなく、溜めておく方がよい。
    ///
    ///   ・<b>狙いを横取りさせない</b>（2026-09-19 に追加）
    ///     AutoTarget の Retarget を Always から <b>Never</b> にした。
    ///
    ///     Always は「BMR が常に狙いを選び直す」設定。
    ///     最下層には同じ名前・同じ座標の相手が10体いて、
    ///     そのうち9体は HP 44 の<b>触れない</b>もの。
    ///     こちらが本物（DataId 17158）を狙っても、
    ///     次のフレームで BMR が偽物へ付け替えてしまい、
    ///     いつまでも攻撃が始まらなかった。
    ///
    ///     利用者が BMR のプリセットを AutoDuty に替えたら
    ///     攻撃するようになったことで判明した（実測 2026-09-19）。
    ///
    ///   ・<b>タンクは防御を使う</b>
    ///     元は全部 Disabled だったが、Enabled にした。
    ///     スタンスを入れて敵を集めてもらう。
    ///     集まらないと、仲間が個別に狙われて散らばる。
    ///
    /// あわせて、自己回復と蘇生も入れてある（AutoDuty と同じ設定）。
    /// タンクが敵を抱える以上、耐える手立ては要る。
    /// </summary>
    private const string FieldPresetJson = @"{""Name"":""Treasure Field"",""Modules"":{""BossMod.Autorotation.MiscAI.AutoTarget"":[{""Track"":""Retarget"",""Option"":""Always""}],""BossMod.Autorotation.xan.BLU"":[{""Track"":""Targeting"",""Option"":""Auto""}],""BossMod.Autorotation.xan.BLM"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.PCT"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.RDM"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.SMN"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.AST"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.SCH"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.SGE"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.WHM"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.DRG"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.MNK"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.NIN"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.RPR"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.SAM"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.VPR"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""},{""Track"":""WrithingSnap"",""Option"":""Ranged""}],""BossMod.Autorotation.xan.BRD"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.DNC"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.MCH"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.DRK"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.GNB"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.PLD"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.VeynWAR"":[{""Track"":""AOE"",""Option"":""AutoFinishCombo""},{""Track"":""Burst"",""Option"":""Spend""},{""Track"":""Potion"",""Option"":""Manual""},{""Track"":""Infuriate"",""Option"":""ForceIfNoNC""},{""Track"":""IR"",""Option"":""Automatic""},{""Track"":""Upheaval"",""Option"":""Automatic""},{""Track"":""PR"",""Option"":""Automatic""},{""Track"":""Onslaught"",""Option"":""Force""},{""Track"":""Tomahawk"",""Option"":""Opener""},{""Track"":""Wrath"",""Option"":""Automatic""}],""BossMod.Autorotation.xan.HealerAI"":[{""Track"":""Heal"",""Option"":""Enabled""},{""Track"":""Esuna2"",""Option"":""Enabled""},{""Track"":""Raise"",""Option"":""Slowcast""}],""BossMod.Autorotation.xan.MeleeAI"":[{""Track"":""Second Wind"",""Option"":""Enabled""},{""Track"":""Bloodbath"",""Option"":""Enabled""},{""Track"":""Stun"",""Option"":""Enabled""}],""BossMod.Autorotation.xan.RangedAI"":[{""Track"":""Head Graze"",""Option"":""Enabled""},{""Track"":""Second Wind"",""Option"":""Enabled""}],""BossMod.Autorotation.xan.TankAI"":[{""Track"":""Stance"",""Option"":""Enabled""},{""Track"":""Ranged GCD"",""Option"":""Enabled""},{""Track"":""Low Blow"",""Option"":""Enabled""},{""Track"":""Arms' Length"",""Option"":""Enabled""},{""Track"":""Personal mits"",""Option"":""Enabled""},{""Track"":""Invuln"",""Option"":""Enabled""},{""Track"":""Protect party members"",""Option"":""Enabled""},{""Track"":""Interject2"",""Option"":""Enabled""}],""BossMod.Autorotation.xan.VariantAI"":[],""BossMod.Autorotation.xan.Caster"":[{""Track"":""Raise"",""Option"":""Swiftcast""}],""BossMod.Autorotation.MiscAI.StayCloseToTarget"":[],""BossMod.Autorotation.MiscAI.StayWithinLeylines"":[{""Track"":""Use Retrace"",""Option"":""Yes""},{""Track"":""Use Between The Lines"",""Option"":""Yes""}],""BossMod.Autorotation.MiscAI.StayCloseToPartyRole"":[{""Track"":""range"",""Option"":""20""}],""BossMod.Autorotation.MiscAI.NormalMovement"":[{""Track"":""Destination"",""Option"":""Pathfind""}]}}";

    private const string VaultPresetJson = @"{""Name"":""Treasure Vault"",""Modules"":{""BossMod.Autorotation.MiscAI.AutoTarget"":[{""Track"":""Retarget"",""Option"":""Always""},{""Track"":""Everything"",""Option"":""Enabled""}],""BossMod.Autorotation.xan.BLU"":[{""Track"":""Targeting"",""Option"":""Auto""}],""BossMod.Autorotation.xan.BLM"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.PCT"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.RDM"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.SMN"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.AST"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.SCH"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.SGE"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.WHM"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.DRG"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.MNK"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.NIN"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.RPR"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.SAM"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.VPR"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""},{""Track"":""WrithingSnap"",""Option"":""Ranged""}],""BossMod.Autorotation.xan.BRD"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.DNC"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.MCH"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.DRK"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.GNB"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.xan.PLD"":[{""Track"":""Targeting"",""Option"":""Auto""},{""Track"":""AOE"",""Option"":""AOE""}],""BossMod.Autorotation.VeynWAR"":[{""Track"":""AOE"",""Option"":""AutoFinishCombo""},{""Track"":""Burst"",""Option"":""Spend""},{""Track"":""Potion"",""Option"":""Manual""},{""Track"":""Infuriate"",""Option"":""ForceIfNoNC""},{""Track"":""IR"",""Option"":""Automatic""},{""Track"":""Upheaval"",""Option"":""Automatic""},{""Track"":""PR"",""Option"":""Automatic""},{""Track"":""Onslaught"",""Option"":""Force""},{""Track"":""Tomahawk"",""Option"":""Opener""},{""Track"":""Wrath"",""Option"":""Automatic""}],""BossMod.Autorotation.xan.HealerAI"":[{""Track"":""Heal"",""Option"":""Enabled""},{""Track"":""Esuna2"",""Option"":""Enabled""},{""Track"":""Raise"",""Option"":""Slowcast""}],""BossMod.Autorotation.xan.MeleeAI"":[{""Track"":""Second Wind"",""Option"":""Enabled""},{""Track"":""Bloodbath"",""Option"":""Enabled""},{""Track"":""Stun"",""Option"":""Enabled""}],""BossMod.Autorotation.xan.RangedAI"":[{""Track"":""Head Graze"",""Option"":""Enabled""},{""Track"":""Second Wind"",""Option"":""Enabled""}],""BossMod.Autorotation.xan.TankAI"":[{""Track"":""Stance"",""Option"":""Enabled""},{""Track"":""Ranged GCD"",""Option"":""Enabled""},{""Track"":""Low Blow"",""Option"":""Enabled""},{""Track"":""Arms' Length"",""Option"":""Enabled""},{""Track"":""Personal mits"",""Option"":""Enabled""},{""Track"":""Invuln"",""Option"":""Enabled""},{""Track"":""Protect party members"",""Option"":""Enabled""},{""Track"":""Interject2"",""Option"":""Enabled""}],""BossMod.Autorotation.xan.VariantAI"":[],""BossMod.Autorotation.xan.Caster"":[{""Track"":""Raise"",""Option"":""Swiftcast""}],""BossMod.Autorotation.MiscAI.StayCloseToTarget"":[],""BossMod.Autorotation.MiscAI.StayWithinLeylines"":[{""Track"":""Use Retrace"",""Option"":""Yes""},{""Track"":""Use Between The Lines"",""Option"":""Yes""}],""BossMod.Autorotation.MiscAI.StayCloseToPartyRole"":[{""Track"":""range"",""Option"":""20""}],""BossMod.Autorotation.MiscAI.NormalMovement"":[{""Track"":""Destination"",""Option"":""Pathfind""}]}}";

    // ---- 共通 ---------------------------------------------------------------

    /// <summary>そのプラグインが入っていて、動いているか。</summary>
    private static bool HasPlugin(string internalName)
    {
        try
        {
            foreach (var plugin in Svc.PluginInterface.InstalledPlugins)
            {
                if (!plugin.IsLoaded)
                    continue;

                if (plugin.InternalName.Contains(internalName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    // 同じ失敗を何度も記録しない。
    private static readonly HashSet<string> Warned = [];

    private static bool Invoke(string name)
    {
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<object>(name).InvokeAction();
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce(name, ex);
            return false;
        }
    }

    private static bool Invoke<T1>(string name, T1 arg)
    {
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<T1, object>(name).InvokeAction(arg);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce(name, ex);
            return false;
        }
    }

    private static TRet? InvokeFunc<TRet>(string name)
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<TRet>(name).InvokeFunc();
        }
        catch (Exception ex)
        {
            WarnOnce(name, ex);
            return default;
        }
    }

    private static TRet? InvokeFunc<T1, TRet>(string name, T1 arg)
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<T1, TRet>(name).InvokeFunc(arg);
        }
        catch (Exception ex)
        {
            WarnOnce(name, ex);
            return default;
        }
    }

    private static TRet? InvokeFunc<T1, T2, TRet>(string name, T1 a1, T2 a2)
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<T1, T2, TRet>(name).InvokeFunc(a1, a2);
        }
        catch (Exception ex)
        {
            WarnOnce(name, ex);
            return default;
        }
    }

    private static void WarnOnce(string name, Exception ex)
    {
        if (Warned.Add(name))
            Svc.Log.Warning(ex, $"{name} を呼べませんでした。");
    }
}
