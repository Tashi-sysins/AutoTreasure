using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace AutoTreasure.Windows;

/// <summary>
/// ソロで始めようとしたときの確認。
///
/// 魔紋（宝物庫）は1人では攻略が難しい。
/// パーティを組み忘れたまま地図を使ってしまうと、地図を1枚無駄にする。
/// そこで、始める前に一度確かめる。
/// </summary>
internal sealed class SoloConfirmWindow : Window
{
    private Action? _onYes;

    internal SoloConfirmWindow() : base("確認###AutoTreasureSoloConfirm",
        ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        IsOpen = false;

        // 画面の真ん中に出す。見落とされては意味がない。
        PositionCondition = ImGuiCond.Appearing;
        Position = null;
    }

    /// <summary>
    /// 確認を出す。
    /// 「はい」が選ばれたときだけ、渡された処理を実行する。
    /// </summary>
    internal void Ask(Action onYes)
    {
        _onYes = onYes;
        IsOpen = true;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted("ソロですが「古ぼけた地図S5」を解読しますか？");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "※8人パーティー推奨");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("はい", new Vector2(120, 32)))
        {
            IsOpen = false;

            var action = _onYes;
            _onYes = null;
            action?.Invoke();
        }

        ImGui.SameLine();

        if (ImGui.Button("いいえ", new Vector2(120, 32)))
        {
            IsOpen = false;
            _onYes = null;
        }
    }

    public override void OnClose()
    {
        // 窓を閉じただけのときは、何もしない。
        _onYes = null;
    }
}
