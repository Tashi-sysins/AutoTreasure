using AutoTreasure.Logic;
using AutoTreasure.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using System;

namespace AutoTreasure;

/// <summary>
/// 魔紋自動周回（AutoTreasure）の入口。
///
/// 古ぼけた地図の宝探しを通しで自動化する。
///   ・地図を解読したら宝の場所を突き止める
///   ・そこまで移動して、ディグ → 宝箱 → 討伐 → ロット
///   ・魔紋（宝物庫）に入ったら、宝箱と扉を探しながら奥へ進む
///   ・複数のクライアントで動かすときは、節目ごとに足並みを揃える
///
/// 移動そのものは vnavmesh に、戦い方は RotationSolver や BossMod に任せる。
/// このプラグインは「次に何をするか」を決めて、その指示を出す役に徹する。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public const string PluginName = "AutoTreasure";

    private const string CmdMain = "/amt";

    private readonly WindowSystem _windows = new(PluginName);
    private readonly MainWindow _mainWindow;
    private readonly SoloConfirmWindow _soloConfirm = new();
    private readonly RunController _controller;

    /// <summary>手で遊んでいる間の様子を残すための記録。</summary>
    private readonly RunLog _log = new();
    private readonly IPC.LazyLootDiagnostics _lootDiagnostics;

    /// <summary>
    /// 取り込んだ経路探索。
    ///
    /// これが動いていないと、移動の指示は何も通らない。
    /// </summary>
    private readonly IPC.EmbeddedNavmesh? _navmesh;
    private readonly RunObserver _observer;

    /// <summary>
    /// 画面右上に出す状態表示。
    ///
    /// メンバーは操作画面を開かないので、ここでしか設定を確かめられない。
    /// </summary>
    private readonly Windows.StatusBarEntry? _statusBar;

    /// <summary>
    /// 記録を自動で始める処理を、すでに済ませたか。
    /// ログインするたびに一度だけ始めたいので、済んだかを覚えておく。
    /// </summary>
    private bool _autoStartDone;

    public static Configuration Config { get; private set; } = null!;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        // ECommons を初期化すると Svc.〜 でゲームの各機能に触れるようになる。
        ECommonsMain.Init(pluginInterface, this);

        Config = Configuration.Load();

        // 経路探索を立ち上げる。
        //
        // vnavmesh をこのプラグインの中に取り込んである。
        // 外部プラグインとしての vnavmesh には頼らない。
        //
        // 取り込んだ理由は、階層が変わったときに地形を作り直させる必要が
        // あるため。魔紋はエリア番号が 1209 のまま変わらないので、
        // 外からでは「作り直すべきか」を判断できなかった。
        _navmesh = new IPC.EmbeddedNavmesh(pluginInterface);

        // 前回、LazyLoot の自動ロットを戻せていなかったら戻す。
        //
        // 落ちた・ゲームが強制終了した場合、Stop も Dispose も通らない。
        // 借りたままにしておくと「LazyLoot が壊れた」と思われる。
        IPC.LazyLootControl.RestoreIfLeftSuppressed();

        Logic.ActionHelper.Log = _log;
        _controller = new RunController(_log);
        _observer = new RunObserver(_log);
        _lootDiagnostics = new IPC.LazyLootDiagnostics(message => _log.Write(message));
        _mainWindow = new MainWindow(_controller, _log, _observer, StartWithConfirm);
        _statusBar = new Windows.StatusBarEntry(_controller, OpenMain);
        _windows.AddWindow(_mainWindow);
        _windows.AddWindow(_soloConfirm);

        Svc.PluginInterface.UiBuilder.Draw += _windows.Draw;
        Svc.PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += OpenMain;

        Svc.Framework.Update += OnFrameworkUpdate;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;

        Svc.Commands.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "魔紋自動周回の画面を開く。/amt start 開始、/amt stop 停止、/amt log 記録の開始と終了。",
        });

        // 地形づくりを速くしておく。
        //
        // 開始ボタンを押したときではなく、ここで上げる。
        // 地形はエリアに入った瞬間から作られ始めるので、
        // 押してからでは間に合わない。
        // 押す前にテレポした場合、遅い設定のまま作られてしまう。
        //
        // プラグインを止めるときに元へ戻す。
        if (Config.BoostNavmeshCores)
        {
            var cores = Math.Clamp(Config.NavmeshCores, 1, 64);
            IPC.VNavmeshConfig.Apply(cores);
        }

        // ここでは通信を始めない。
        //
        // この時点ではまだログイン前のことがあり、パーティの状態を読めない。
        // 保存されていた古い役割を見て判断すると、実際とずれたまま始めてしまう。
        // 毎フレームの点検（UpdateRoleFromParty）が、
        // 読めるようになった時点で正しい向きに開く。
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try
        {
            // 設定してあれば、ログインした時点で記録を始める。
            // 毎回ボタンを押す手間を省くため。
            TryAutoStartLog();

            // 記録が有効なら、まず様子を見る。
            // 自動化が動いていなくても記録は続ける（手で遊ぶときのため）。
            _observer.Tick();

            _controller.Tick();

            // 画面右上の表示を更新する。
            _statusBar?.Update();
        }
        catch (Exception ex)
        {
            // 毎フレーム呼ばれるので、ここで例外を外に出すと
            // ログが埋まるうえ、他のプラグインにも迷惑がかかる。
            Svc.Log.Error(ex, "処理中に問題が起きました。いったん停止します。");
            _controller.Stop("問題が起きたため停止しました");
        }
    }

    /// <summary>
    /// 周回を始める。ソロのときは一度確かめる。
    ///
    /// 魔紋は1人では攻略が難しい。パーティを組み忘れたまま地図を使うと、
    /// 1枚を無駄にしてしまう。始める前に確認する。
    /// </summary>
    internal void StartWithConfirm()
    {
        var status = Logic.PartyRoleDetector.Detect();

        if (status == Logic.PartyStatus.Solo)
        {
            _soloConfirm.Ask(() => _controller.Start());
            return;
        }

        _controller.Start();
    }

    /// <summary>
    /// 設定してあれば、記録を自動で始める。
    ///
    /// ログインが済んで、キャラクターの名前が読めるようになってから始める。
    /// 名前が読めないうちに始めると、ファイル名が「不明」になってしまう。
    /// </summary>
    private void TryAutoStartLog()
    {
        if (_autoStartDone || !Config.AutoStartLog || _log.IsRecording)
            return;

        // キャラクターの名前が読めるまで待つ。
        if (!Helpers.PlayerHelper.IsValid)
            return;

        _autoStartDone = true;
        _observer.Reset();
        _log.Start();

        Svc.Chat.Print($"[AutoTreasure] 記録を始めました: {_log.Path}");
    }

    private void OnTerritoryChanged(uint territoryType)
    {
        try
        {
            _controller.OnTerritoryChanged();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "エリア移動の処理で問題が起きました。");
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "start":
                StartWithConfirm();
                break;

            case "stop":
            {
                // ボタンと同じ扱いにする。
                // リーダーなら全員、メンバーなら自分だけ。
                // 押す場所で動きが変わると、覚えることが増える。
                var all = Config.Role == ClientRole.Leader;

                _controller.Stop(
                    all ? "リーダーが停止しました" : "コマンドで停止しました",
                    tellOthers: all);
                break;
            }

            case "log":
                if (_log.IsRecording)
                {
                    _log.Stop();
                    Svc.Chat.Print($"[AutoTreasure] 記録を終えました: {_log.Path}");
                }
                else
                {
                    _observer.Reset();
                    _log.Start();
                    Svc.Chat.Print($"[AutoTreasure] 記録を始めました: {_log.Path}");
                }
                break;

            default:
                OpenMain();
                break;
        }
    }

    private void OpenMain() => _mainWindow.IsOpen = true;

    public void Dispose()
    {
        _lootDiagnostics.Dispose();
        Svc.Commands.RemoveHandler(CmdMain);

        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;

        Svc.PluginInterface.UiBuilder.Draw -= _windows.Draw;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;

        _statusBar?.Dispose();
        _windows.RemoveAllWindows();

        // <b>経路探索より先に片付ける。</b>
        // RunController.Dispose() は借りた設定（地形づくりのコア数）を
        // 元に戻すが、その戻し先は取り込んだ経路探索の中にある。
        // 先に _navmesh を片付けると、戻す相手がもう居らず、
        // 上げたままの値が使う人の設定ファイルに残る。
        _controller.Dispose();

        _navmesh?.Dispose();

        _log.Dispose();

        ECommonsMain.Dispose();
    }
}
