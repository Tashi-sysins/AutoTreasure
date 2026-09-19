using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System.Numerics;

namespace AutoTreasure.Helpers;

/// <summary>
/// 自分の今の状態を調べる。
///
/// 自機の取得には Svc.ClientState.LocalPlayer ではなく Svc.Objects.LocalPlayer を使う。
/// 前者は今の Dalamud には無い。
///
/// <b>ゲーム内部の入口（Instance()）は null を返すことがある。</b>
/// ログイン前、エリア移動中、終了処理中など。
/// null のまま触るとゲームごと落ちる（try/catch では止められない種類の落ち方）ので、
/// 必ず確かめてから読む。
/// </summary>
internal static unsafe class PlayerHelper
{
    /// <summary>ログイン済みで、自機を触れる状態か。</summary>
    internal static bool IsValid => Svc.Objects.LocalPlayer != null;

    /// <summary>
    /// 自分のキャラクター名。読めないときは空。
    ///
    /// 地図を使う順番は名前で管理するので、そこで使う。
    /// パーティの一覧に出る名前と同じ綴りになる。
    /// </summary>
    internal static string Name => Svc.Objects.LocalPlayer?.Name.TextValue ?? "";

    /// <summary>自分の現在地。取得できないときは Vector3.Zero。</summary>
    internal static Vector3 Position => Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;

    /// <summary>
    /// 操作を受け付けられる状態か。
    ///
    /// エリア移動中・ムービー中・イベント中・ロード中などは false。
    /// この間に移動やアクションを指示しても通らないので、待つのが正しい。
    /// </summary>
    internal static bool IsReady
        => IsValid
        && !Svc.Condition[ConditionFlag.BetweenAreas]
        && !Svc.Condition[ConditionFlag.BetweenAreas51]
        && !Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]
        && !Svc.Condition[ConditionFlag.WatchingCutscene]
        && !Svc.Condition[ConditionFlag.WatchingCutscene78]
        && !Svc.Condition[ConditionFlag.Occupied]
        && !Svc.Condition[ConditionFlag.Occupied33]
        && !Svc.Condition[ConditionFlag.Occupied38]
        && !Svc.Condition[ConditionFlag.Occupied39]
        && !Svc.Condition[ConditionFlag.OccupiedInEvent]
        && !Svc.Condition[ConditionFlag.OccupiedInQuestEvent]
        && !Svc.Condition[ConditionFlag.OccupiedSummoningBell]
        && !Svc.Condition[ConditionFlag.Casting]
        && !Svc.Condition[ConditionFlag.Unconscious];

    /// <summary>エリアを移動している最中か。</summary>
    internal static bool IsBetweenAreas
        => Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51];

    /// <summary>ムービーを見ている最中か。</summary>
    internal static bool IsInCutscene
        => Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]
        || Svc.Condition[ConditionFlag.WatchingCutscene]
        || Svc.Condition[ConditionFlag.WatchingCutscene78];

    /// <summary>詠唱中か。詠唱を邪魔しないよう、この間はアクションを撃たない。</summary>
    internal static bool IsCasting => Svc.Condition[ConditionFlag.Casting];

    /// <summary>戦闘中か。</summary>
    internal static bool InCombat => Svc.Condition[ConditionFlag.InCombat];

    /// <summary>死んでいるか。</summary>
    internal static bool IsDead => Svc.Objects.LocalPlayer?.IsDead ?? false;

    /// <summary>マウントに乗っているか。</summary>
    internal static bool IsMounted
    {
        get
        {
            var conditions = Conditions.Instance();
            return conditions != null && conditions->Mounted;
        }
    }

    /// <summary>飛んでいるか。</summary>
    internal static bool IsFlying
    {
        get
        {
            var conditions = Conditions.Instance();
            return conditions != null && conditions->InFlight;
        }
    }

    /// <summary>
    /// 潜水中か。
    ///
    /// 潜水中は IsFlying が false になる。ここを見落とすと
    /// 「飛んでいないから離陸させよう」と判断して、水面へ出ては潜るを繰り返す。
    /// </summary>
    internal static bool IsDiving
    {
        get
        {
            var conditions = Conditions.Instance();
            return conditions != null && conditions->Diving;
        }
    }

    /// <summary>移動中か。</summary>
    internal static bool IsMoving
    {
        get
        {
            var agent = AgentMap.Instance();
            return agent != null && agent->IsPlayerMoving;
        }
    }

    /// <summary>今いるエリアの TerritoryType。</summary>
    internal static uint TerritoryType => Svc.ClientState.TerritoryType;
}
