using AutoTreasure.Logic;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using System;

namespace AutoTreasure.Windows;

/// <summary>
/// 画面右上（サーバー情報バー）に、今の状態を出す。
///
/// <b>なぜ要るのか。</b>
/// メンバーは操作画面を開かなくても動いてしまう。
/// そのため、ロットの設定が Need なのか Pass なのかを
/// 確かめる機会が無い。気づかないまま回し続けることになる。
///
/// ここなら、画面を開かなくても常に見える。
/// クリックすれば操作画面が開く。
/// </summary>
internal sealed class StatusBarEntry : IDisposable
{
    private readonly RunController _controller;
    private readonly IDtrBarEntry? _entry;

    internal StatusBarEntry(RunController controller, Action openWindow)
    {
        _controller = controller;

        try
        {
            _entry = Svc.DtrBar.Get("AutoTreasure");
            _entry.OnClick = _ => openWindow();
        }
        catch (Exception ex)
        {
            // 出せなくても周回は動く。無ければ諦める。
            Svc.Log.Warning(ex, "[AutoTreasure] 情報バーに出せませんでした。");
            _entry = null;
        }
    }

    /// <summary>毎フレーム呼ぶ。中身が変わったときだけ書き換える。</summary>
    internal void Update()
    {
        if (_entry == null)
            return;

        try
        {
            // 周回している間だけ出す。
            //
            // <b>止まっている間は消す。</b>
            // 常に出していると、画面右上は他のプラグインと場所を分け合っているので、
            // 使っていないときまで居座ることになる。
            //
            // ここで見たいのは「今どのロットで回っているか」なので、
            // 回っていないときに出す意味がない。
            if (!Plugin.Config.ShowStatusBar || !_controller.IsRunning)
            {
                _entry.Shown = false;
                return;
            }

            _entry.Shown = true;

            var text = Build();

            // 同じ文字なら書き換えない。
            // 毎フレーム作り直すと、そのぶん無駄に働くことになる。
            if (text == _lastText)
                return;

            _lastText = text;
            _entry.Text = Colorize(text);
        }
        catch
        {
            // 書けなくても周回には関わらない。
        }
    }

    private string _lastText = "";

    /// <summary>
    /// 出す文字を組み立てる。
    ///
    /// 短くする。情報バーは他のプラグインと場所を分け合っているので、
    /// 長いと他を押しのけてしまう。
    /// </summary>
    private string Build()
    {
        var cfg = Plugin.Config;

        // 役割は1文字で。「リーダー」「メンバー」では長すぎる。
        var role = cfg.Role switch
        {
            ClientRole.Leader => "L",
            ClientRole.Member => "M",
            _                 => "S",
        };

        // ロットを誰が担当しているかを出す。
        //
        // LazyLoot が入っているときは、こちらのロット設定は使われない。
        // ここに「Need」などと出すと、その設定で回っていると誤解させる。
        var roll = !cfg.AutoRoll                    ? "ロット手動"
                 : IPC.LazyLootControl.ShouldYield  ? "LazyLoot"
                 : MainWindow.NameFor(cfg.RollOption);

        // 自分が地図役なら、その印を付ける。
        //
        // 地図役だけが宝箱・魔紋・扉に触れるので、
        // 「自分が触る番か」は動いている最中に一番知りたいこと。
        var map = _controller.MapUser == Helpers.PlayerHelper.Name ? " [地図]" : "";

        return $"AT {role}:{roll}{map} 周回中";
    }

    /// <summary>
    /// ロットの色を付ける。
    ///
    /// 何を選んでいるかが一目で分かるようにする。
    /// 「気づかないうちに Pass のまま回していた」を防ぐのが目的なので、
    /// 色が付いていないと意味が薄い。
    /// </summary>
    private static SeString Colorize(string text)
    {
        var cfg = Plugin.Config;

        // こちらがロットしないときは、色を付けない。
        // 色はロットの選択を示すためのものなので、
        // 選択が使われていない場面で点けると紛らわしい。
        if (!cfg.AutoRoll || IPC.LazyLootControl.ShouldYield)
            return new SeString(new TextPayload(text));

        // 情報バーは装飾が限られるので、色番号で指定する。
        var color = cfg.RollOption switch
        {
            RollChoice.Need  => (ushort)17,   // 赤
            RollChoice.Greed => (ushort)25,   // 黄
            _                => (ushort)37,   // 青
        };

        return new SeString(
            new UIForegroundPayload(color),
            new TextPayload(text),
            new UIForegroundPayload(0));
    }

    public void Dispose()
    {
        try
        {
            _entry?.Remove();
        }
        catch
        {
            // 消せなくても困らない。
        }
    }
}
