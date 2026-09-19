namespace AutoTreasure.Logic;

/// <summary>
/// 今どの段階にいるか。
///
/// 上から順に進んでいく。魔紋の中は部屋ごとに繰り返す。
/// </summary>
internal enum RunState
{
    /// <summary>止まっている。</summary>
    Idle,

    /// <summary>宝の場所を突き止めるところ。</summary>
    Locating,

    /// <summary>目的のエリアへテレポするところ。</summary>
    Teleporting,

    /// <summary>宝の場所へ向かっているところ。</summary>
    Travelling,

    /// <summary>全員がそろうのを待っているところ。</summary>
    WaitingForParty,

    /// <summary>ディグを使うところ。</summary>
    Digging,

    /// <summary>宝箱を探して開けるところ。</summary>
    OpeningChest,

    /// <summary>敵を倒すのを待っているところ。</summary>
    Fighting,

    /// <summary>ロットを処理しているところ。</summary>
    Rolling,

    /// <summary>魔紋に入るところ。</summary>
    EnteringVault,

    /// <summary>魔紋の中で、宝箱や扉を探しているところ。</summary>
    VaultExploring,

    /// <summary>扉にアクセスしてムービーを待っているところ。</summary>
    VaultDoor,

    /// <summary>次の部屋へ移るところ。</summary>
    VaultTransition,

    /// <summary>魔紋から出るところ。</summary>
    LeavingVault,

    /// <summary>一区切りついた。</summary>
    Completed,

    /// <summary>続けられなくなった。</summary>
    Failed,
}

/// <summary>段階の名前を日本語で返す。画面に出す用。</summary>
internal static class RunStateText
{
    internal static string ToJapanese(this RunState state) => state switch
    {
        RunState.Idle             => "待機",
        RunState.Locating         => "宝の場所を調べています",
        RunState.Teleporting      => "エリアへ移動しています",
        RunState.Travelling       => "宝の場所へ向かっています",
        RunState.WaitingForParty  => "仲間がそろうのを待っています",
        RunState.Digging          => "掘っています",
        RunState.OpeningChest     => "宝箱を開けています",
        RunState.Fighting         => "戦っています",
        RunState.Rolling          => "ロットを処理しています",
        RunState.EnteringVault    => "魔紋に入っています",
        RunState.VaultExploring   => "魔紋の中を探しています",
        RunState.VaultDoor        => "扉を開けています",
        RunState.VaultTransition  => "次の部屋へ移っています",
        RunState.LeavingVault     => "魔紋から出ています",
        RunState.Completed        => "完了",
        RunState.Failed           => "中断",
        _                         => state.ToString(),
    };
}
