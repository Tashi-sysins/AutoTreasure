using AutoTreasure.Helpers;
using AutoTreasure.IPC;
using AutoTreasure.Logic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Linq;
using System.Numerics;

namespace AutoTreasure.Windows;

/// <summary>
/// 操作画面。
///
/// 見れば今どうなっているか分かり、押せば動く——それだけの画面にする。
/// 細かい調整は下の方にまとめ、普段は触らなくて済むようにしてある。
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly RunController _controller;
    private readonly RunLog _log;
    private readonly RunObserver _observer;

    /// <summary>開始のときに呼ぶ処理。ソロなら確認を挟む。</summary>
    private readonly Action _startAction;

    internal MainWindow(RunController controller, RunLog log, RunObserver observer, Action startAction)
        : base("魔紋自動周回###AutoTreasureMain")
    {
        _controller = controller;
        _log = log;
        _observer = observer;
        _startAction = startAction;
        Size = new Vector2(460, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>
    /// 毎フレーム、描く前に呼ばれる。
    ///
    /// ここで見出しの文字を差し替える。
    /// 畳んだときは見出しのバーしか見えないので、
    /// そこに今のロット設定を出しておくと、開かずに確かめられる。
    ///
    /// <b>「###」より後ろは変えない。</b>
    /// ImGui はここで窓を見分けているので、変えると
    /// 位置や大きさの記憶が別物として扱われる。
    /// </summary>
    public override void PreDraw()
    {
        // 見出しは常に同じ。
        //
        // 以前は畳んだときのバーにロット設定を出していたが、やめた。
        // 画面右上（サーバー情報バー）に出しているので、二重に要らない。
        WindowName = "魔紋自動周回###AutoTreasureMain";
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Spacing();
        DrawControls();
        ImGui.Spacing();
        DrawRole();
        ImGui.Spacing();
        DrawSettingsBar();
        ImGui.Spacing();
        DrawRecording();
        ImGui.Spacing();
        DrawDebugSettings(Plugin.Config);
    }

    /// <summary>今どうなっているか。</summary>
    private void DrawStatus()
    {
        // 「状態」の文字は、デバッグを開く鍵にもなっている。
        //
        // <b>見た目は何も変えない。</b>
        // 印を付けると「押せそう」に見えてしまい、隠す意味が無くなる。
        // ふつうの見出しにしか見えないが、5回続けて押すと開く。
        //
        // Selectable ではなく「文字を描いてから、その範囲が押されたか見る」
        // という形にしてあるのは、Selectable だと触れたときに
        // 背景が光ってしまい、押せることが分かってしまうため。
        ImGui.TextUnformatted("状態");
        HandleDebugUnlockClick();

        ImGui.Separator();

        var state = _controller.State;
        var color = state switch
        {
            RunState.Failed    => new Vector4(1f, 0.4f, 0.4f, 1f),
            RunState.Completed => new Vector4(0.5f, 1f, 0.5f, 1f),
            RunState.Idle      => new Vector4(0.7f, 0.7f, 0.7f, 1f),
            _                  => new Vector4(0.5f, 0.8f, 1f, 1f),
        };

        ImGui.TextColored(color, state.ToJapanese());

        if (!string.IsNullOrEmpty(_controller.Note))
            ImGui.TextWrapped(_controller.Note);

        var target = _controller.Target;
        if (target != null)
            ImGui.TextUnformatted($"目的地: {target.Value.PlaceName}");

        // 今回の地図役を出す。
        //
        // 地図を使った人だけが宝箱・魔紋・扉に触れるので、
        // 「今回は自分の番か」が分からないと、
        // 止まっているのが正常なのか不具合なのか判断できない。
        var mapUser = _controller.MapUser;

        if (!string.IsNullOrEmpty(mapUser) && _controller.IsRunning)
        {
            var mine = mapUser == PlayerHelper.Name;

            ImGui.TextColored(
                mine ? new Vector4(1f, 0.85f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f),
                mine ? "地図役: 自分（宝箱と扉を担当します）" : $"地図役: {mapUser}");
        }
    }

    /// <summary>
    /// 直前に描いた「状態」の文字が押されたかを見る。
    ///
    /// 5回続けて押すとデバッグが出る。
    ///
    /// <b>文字には何も手を加えない。</b>
    /// ImGui.IsItemHovered / IsMouseClicked を使えば、
    /// 見た目をふつうの文字のままにして押下だけ拾える。
    /// ボタンにすると枠が付き、Selectable にすると触れたとき光るので、
    /// どちらも「ここに何かある」と分かってしまう。
    ///
    /// 残り回数も出さない。出すと気づかれるため。
    /// </summary>
    private void HandleDebugUnlockClick()
    {
        var cfg = Plugin.Config;

        if (cfg.ShowDebugSettings)
            return;

        if (!ImGui.IsItemHovered() || !ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return;

        // 間が空いたら数え直す。
        // 何日も前に1回押したものを覚えていても仕方がない。
        if ((DateTime.UtcNow - _lastDebugTap).TotalSeconds > 3)
            _debugTaps = 0;

        _lastDebugTap = DateTime.UtcNow;
        _debugTaps++;

        if (_debugTaps < DebugTapsToOpen)
            return;

        cfg.ShowDebugSettings = true;
        _debugTaps = 0;
        cfg.Save();
    }

    /// <summary>開始と停止。</summary>
    private void DrawControls()
    {
        var running = _controller.IsRunning;

        // 次の周回を待っている間も「止める」を出す。
        //
        // 「完了」は動いていない扱いなので、以前はここで開始ボタンが出た。
        // カウントダウン中に止められず、押しても次の周回が始まってしまう。
        // メンバーは自分では次を始めないので、待っている状態にはならない。
        // リーダー（とソロ）のときだけ、カウントダウン中とみなす。
        var waitingNext = _controller.State == RunState.Completed
                       && Plugin.Config.ContinuousRuns
                       && Plugin.Config.Role != ClientRole.Member;

        var canStop = running || waitingNext;

        if (Plugin.Config.Role == ClientRole.Member)
        {
            // <b>メンバーは停止ボタンだけ。</b>
            // 開始はリーダーの合図で始まるので、自分で押す機会がない。
            // 一方、止めるのは自分の機だけの操作なので、いつでも要る。
            //
            // 開始ボタンは置かない。
            // メンバーが押すと、リーダーが決めた地図役より先に動き出し、
            // 自分の地図を使ってしまうことがある。
            // どうしても1台だけ動かしたいときは /amt start で押せる。
            DrawStopButton(canStop, new Vector2(120, 32));
        }
        else if (canStop)
        {
            DrawStopButton(true, new Vector2(120, 32));
        }
        else
        {
            if (ImGui.Button("開始", new Vector2(120, 32)))
                _startAction();
        }

        ImGui.TextDisabled("/amt start  /amt stop");

        // ---- ロットの設定 ---------------------------------------------------
        //
        // <b>ここに置く理由。</b>
        // 以前は「設定」を開かないと見えなかった。
        // LazyLoot を入れていない人にとっては、ここが唯一の設定場所なので、
        // 開かないと分からない場所にあると見つけられない。
        ImGui.Spacing();
        DrawLootSetting(Plugin.Config);

        ImGui.Spacing();
        DrawMapTurn(Plugin.Config);

        // 動かすのに必要なものが揃っているか。
        if (!VNavmesh.IsEnabled)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f),
                "vnavmesh が見つかりません。移動できません。");
        }
    }

    /// <summary>
    /// 停止ボタン。
    ///
    /// 動いていないときは灰色にして押せなくする。
    /// 消してしまうと、その場所に開始ボタンが来たりして位置が動き、
    /// 「さっき止めたところ」を探し直すことになる。
    /// 押せないだけで、常に同じ場所に在る方が扱いやすい。
    /// </summary>
    private void DrawStopButton(bool enabled, Vector2 size)
    {
        if (!enabled)
            ImGui.BeginDisabled();

        if (ImGui.Button("停止", size) && enabled)
        {
            // <b>リーダーが押したら全員が止まる。メンバーは自分だけ。</b>
            //
            // リーダーは全体の進行を決める側なので、
            // 止めるときも全体を止めるのが自然。
            // 4台を1台ずつ止めて回るのは手間がかかるうえ、
            // 止め忘れた機が独りで動き続けることになる。
            //
            // メンバーは自分の機だけを止める。
            // 1台の調子が悪いとき、その機だけ外して残りは続けたいため。
            var all = Plugin.Config.Role == ClientRole.Leader;

            _controller.Stop(
                all ? "リーダーが停止しました" : "手動で停止しました",
                tellOthers: all);
        }

        if (!enabled)
            ImGui.EndDisabled();

        // 押すとどうなるかを、押す前に見せる。
        // 「自分だけ止まると思ったら全部止まった」を防ぐ。
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Plugin.Config.Role == ClientRole.Leader
                ? "仲間もまとめて止めます"
                : "この機だけ止めます");
        }
    }

    /// <summary>
    /// 役割の表示。
    ///
    /// 選ばせない。パーティリーダーは常に1人なので、
    /// ゲームに聞けば取り違えようがない。
    /// 手で設定させると、3台のどれかを間違えたまま動かす事故が起きる。
    /// </summary>
    private void DrawRole()
    {
        var cfg = Plugin.Config;

        ImGui.TextUnformatted("役割");
        ImGui.Separator();

        var status = PartyRoleDetector.Detect();

        var color = cfg.Role switch
        {
            ClientRole.Leader => new Vector4(1f, 0.85f, 0.4f, 1f),
            ClientRole.Member => new Vector4(0.6f, 0.85f, 1f, 1f),
            _                 => new Vector4(0.8f, 0.8f, 0.8f, 1f),
        };

        ImGui.TextColored(color, PartyRoleDetector.Describe(status));

        if (PartyRoleDetector.MemberCount > 1)
        {
            ImGui.SameLine(0, 8);
            ImGui.TextDisabled($"（{PartyRoleDetector.MemberCount} 人）");
        }

        // 役割の説明は、ふだん出さない。
        //
        // 一度読めば分かる内容で、毎回見る必要がない。
        // 役割そのものは上の1行で分かる。
        // 調べもののときだけ出せるよう、デバッグ側の設定にしてある（既定は切）。
        if (cfg.ShowRoleDescription)
        {
            switch (cfg.Role)
            {
                case ClientRole.Solo:
                    ImGui.TextWrapped("1台だけで動きます。地図の解読から宝箱まで自分で行います。");
                    break;
                case ClientRole.Leader:
                    ImGui.TextWrapped("地図を解読して宝の場所を仲間に伝えます。魔紋では扉を開ける役。");
                    break;
                case ClientRole.Member:
                    ImGui.TextWrapped("場所を受け取って自分で向かいます。扉は開けません。");
                    break;
            }
        }

        if (cfg.Role != ClientRole.Solo)
        {
            // 何台つながっているかを出す。
            // パーティの人数から「つながるべき台数」を出すので、
            // 足りていないことに気づける。
            var expected = PartyRoleDetector.ResolveExpectedMembers();

            ImGui.TextDisabled($"連携: {_controller.SyncStatus}");

            if (cfg.Role == ClientRole.Leader)
            {
                var connected = _controller.ConnectedClients;
                var ok = connected >= expected;

                ImGui.SameLine(0, 8);
                ImGui.TextColored(
                    ok ? new Vector4(0.5f, 1f, 0.5f, 1f) : new Vector4(1f, 0.8f, 0.3f, 1f),
                    $"{connected}/{expected} 台");

                // つながっていない人がいるなら、<b>名前を出す</b>。
                //
                // 「2/3 台」とだけ出ても、誰を探せばよいか分からない。
                // パーティにいるのに合図が届かない人を挙げる。
                if (!ok)
                {
                    var missing = _controller.MissingPartners();

                    if (missing.Count > 0)
                    {
                        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                            $"未接続: {string.Join(" / ", missing)}");
                    }

                    ImGui.TextDisabled("その機で /amt を開き、役割が「パーティメンバー」か確認してください。");
                }
            }
        }
    }

    /// <summary>
    /// 記録。
    ///
    /// 手で1周する様子を残すためのもの。
    /// 何がどう見えているかが分かれば、自動化を詰められる。
    /// </summary>
    /// <summary>
    /// 記録の様子。
    ///
    /// ふだんは何も出さない。記録を取っているときだけ、
    /// 今どうなっているかが分かるように1行出す。
    /// 記録を始める操作は「デバッグ」の中にまとめてある。
    /// </summary>
    private void DrawRecording()
    {
        if (!_log.IsRecording)
            return;

        ImGui.TextUnformatted("記録");
        ImGui.Separator();

        if (ImGui.Button("記録を終える", new Vector2(140, 28)))
            _log.Stop();

        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), $"記録中（{_log.LineCount} 行）");
    }

    /// <summary>
    /// ふだん使う設定をまとめたバー。
    ///
    /// <b>リーダーにだけ出す。</b>
    /// ここに並ぶのは、どれも<b>リーダーが決めればよいもの</b>。
    ///   ・画面右上の表示 … 見た目の好みだが、揃っている方が分かりやすい
    ///   ・連続周回        … 次の周回を始めるのはリーダーだけ
    ///                      （メンバーは合図を待つので、切っても入れても動きが変わらない）
    ///   ・待つ秒数        … 同上
    ///
    /// メンバーに出しても触る意味がなく、迷いのもとにしかならないので隠す。
    ///
    /// 以前は「デバッグ」の中に入れていたが、
    /// ふだん使う設定をデバッグの奥に置くのは筋が悪い。表に出した。
    /// </summary>
    private void DrawSettingsBar()
    {
        var cfg = Plugin.Config;

        // メンバーには出さない。
        if (cfg.Role == ClientRole.Member)
            return;

        if (!ImGui.CollapsingHeader("設定"))
            return;

        ImGui.Indent();
        DrawCommonSettings();
        ImGui.Unindent();
    }

    /// <summary>設定バーの中身。</summary>
    private void DrawCommonSettings()
    {
        var cfg = Plugin.Config;

        // 画面右上の表示。
        //
        // メンバーは操作画面を開かないので、ここが唯一の確かめる場所になる。
        // 既定で出しておき、邪魔なら切れるようにする。
        var statusBar = cfg.ShowStatusBar;
        if (ImGui.Checkbox("画面右上に状態を出す", ref statusBar))
        {
            cfg.ShowStatusBar = statusBar;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "サーバー情報バーに、役割とロット設定を出す。\n"
                + "この画面を開かなくても確かめられる。\n"
                + "押すとこの画面が開く。");
        }

        ImGui.Spacing();

        // 1周終わったら続けるか。
        //
        // このバー自体をリーダーにしか出していないので、
        // ここで役割を確かめ直す必要はない。
        var continuous = cfg.ContinuousRuns;
        if (ImGui.Checkbox("1周終わったら続けて次を始める", ref continuous))
        {
            cfg.ContinuousRuns = continuous;
            cfg.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "地図がある限り回り続ける。\n"
                + "地図が無くなったら自動で止まる。");
        }

        if (continuous)
        {
            var delay = cfg.ContinuousRunDelaySeconds;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.InputFloat("次の周回まで待つ秒数", ref delay, 1f, 5f, "%.0f"))
            {
                cfg.ContinuousRunDelaySeconds = Math.Clamp(delay, 0f, 120f);
                cfg.Save();
            }
        }
    }

    /// <summary>
    /// ロットの選び方。
    ///
    /// 何を選んでいるかが一目で分かるよう、選んだものだけ色を付ける。
    /// 「気づかないうちに Pass のまま回していた」を防ぐため。
    /// </summary>
    private static void DrawLootSetting(Configuration cfg)
    {
        // LazyLoot が入っているなら、設定そのものを出さない。
        //
        // <b>出して触らせない、ではなく、出さない。</b>
        // 使われない設定が見えていると、それで動くと思われる。
        // 代わりに、誰が担当しているかだけを伝える。
        if (IPC.LazyLootControl.ShouldYield)
        {
            ImGui.TextColored(new Vector4(0.45f, 0.8f, 1f, 1f),
                "ロットはLazyLootで制御中");
            return;
        }

        var autoRoll = cfg.AutoRoll;
        if (ImGui.Checkbox("ロットを自動で行う", ref autoRoll))
        {
            cfg.AutoRoll = autoRoll;
            cfg.Save();
        }

        if (!autoRoll)
            return;

        ImGui.SameLine(0, 16);

        DrawRollChoice(cfg, RollChoice.Need,  "Need",  ColorFor(RollChoice.Need));
        ImGui.SameLine(0, 8);
        DrawRollChoice(cfg, RollChoice.Greed, "Greed", ColorFor(RollChoice.Greed));
        ImGui.SameLine(0, 8);
        DrawRollChoice(cfg, RollChoice.Pass,  "Pass",  ColorFor(RollChoice.Pass));

        // ロット処理が使えないときだけ知らせる。
        if (!LootHelper.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                "ロット処理を用意できませんでした。ゲームの更新で目印がずれた可能性があります。");
        }
    }

    /// <summary>
    /// 「古ぼけた地図S5の使用順番」。
    ///
    /// <b>地図は誰でも使える。</b>
    /// 使った人が、その周回で宝箱・魔紋・魔紋内の扉に触る役になる
    /// （ゲーム側の決まりで、使っていない人は触れない）。
    ///
    /// 設定できるのはリーダーだけ。
    /// メンバーにも見せるが、触れないようにする。
    /// 3台それぞれで別の順番を設定すると、誰が使うのか食い違うため。
    /// </summary>
    private void DrawMapTurn(Configuration cfg)
    {
        ImGui.TextUnformatted("古ぼけた地図S5の使用順番");

        var readOnly = cfg.Role == ClientRole.Member;

        if (readOnly)
        {
            ImGui.SameLine(0, 8);
            ImGui.TextDisabled("（リーダーが設定します）");
            ImGui.BeginDisabled();
        }

        DrawMapTurnModeButtons(cfg);
        ImGui.Spacing();
        DrawMapTurnSlots(cfg);

        if (readOnly)
            ImGui.EndDisabled();
    }

    /// <summary>順番の決め方を選ぶ3つのボタン。選んだものだけ色が付く。</summary>
    private static void DrawMapTurnModeButtons(Configuration cfg)
    {
        DrawMapTurnModeButton(cfg, MapTurnMode.FixedCharacter, "指定キャラのみ使用");
        ImGui.SameLine(0, 6);
        DrawMapTurnModeButton(cfg, MapTurnMode.RoundRobin, "1個ずつ順繰り交代");
        ImGui.SameLine(0, 6);
        DrawMapTurnModeButton(cfg, MapTurnMode.AfterCount, "設定消費数後に交代");
    }

    /// <summary>
    /// 順番の決め方のボタンを1つ描く。
    ///
    /// 選ばれているものは色を変える。
    /// どれが効いているのかを、押さずに見分けられるようにするため。
    /// </summary>
    private static void DrawMapTurnModeButton(Configuration cfg, MapTurnMode mode, string label)
    {
        var selected = cfg.MapTurn == mode;

        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.20f, 0.45f, 0.75f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.55f, 0.85f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.30f, 0.60f, 0.90f, 1f));
        }

        if (ImGui.Button(label, new Vector2(146, 26)) && !selected)
        {
            cfg.MapTurn = mode;

            // 「指定キャラのみ」に切り替えたら、先頭以外は要らない。
            // 残しておくと、戻したときに古い並びが復活して驚かせる。
            if (mode == MapTurnMode.FixedCharacter && cfg.MapTurnOrder.Count > 1)
                cfg.MapTurnOrder.RemoveRange(1, cfg.MapTurnOrder.Count - 1);

            cfg.Save();
        }

        if (selected)
            ImGui.PopStyleColor(3);
    }

    /// <summary>
    /// 順番の枠（プルダウン）を並べる。
    ///
    /// 「指定キャラのみ」は1枠だけ。
    /// あとの2つは、＋ボタンでパーティの人数まで増やせる。
    /// </summary>
    private static void DrawMapTurnSlots(Configuration cfg)
    {
        var party = MapTurnTable.PartyNames();
        var single = cfg.MapTurn == MapTurnMode.FixedCharacter;

        // 枠が1つも無ければ、ひとつ用意しておく。
        // 空のまま置くと、何を設定する場所なのか分からない。
        if (cfg.MapTurnOrder.Count == 0)
        {
            cfg.MapTurnOrder.Add(new MapTurnSlot());
            cfg.Save();
        }

        var slotCount = single ? 1 : cfg.MapTurnOrder.Count;

        for (var i = 0; i < slotCount && i < cfg.MapTurnOrder.Count; i++)
        {
            var slot = cfg.MapTurnOrder[i];

            ImGui.PushID(i);

            // 「指定キャラのみ」のときは番号を出さない。1人しかいないため。
            if (!single)
            {
                ImGui.TextUnformatted(CircledNumber(i));
                ImGui.SameLine(0, 4);
            }

            // すでに他の枠で選ばれている人は、選択肢に出さない。
            // 同じ人が二度出てくる並びに意味がないため。
            var taken = cfg.MapTurnOrder
                .Where((s, index) => index != i && !string.IsNullOrEmpty(s.Name))
                .Select(s => s.Name)
                .ToHashSet();

            var choices = party.Where(n => !taken.Contains(n)).ToList();

            // 今選んでいる人が、パーティから抜けていることがある。
            // 選択肢に無いと表示できないので、先頭に足しておく。
            if (!string.IsNullOrEmpty(slot.Name) && !choices.Contains(slot.Name))
                choices.Insert(0, slot.Name);

            ImGui.SetNextItemWidth(190f);

            var shown = string.IsNullOrEmpty(slot.Name) ? "（選んでください）" : slot.Name;

            if (ImGui.BeginCombo("##name", shown))
            {
                foreach (var name in choices)
                {
                    if (ImGui.Selectable(name, name == slot.Name) && name != slot.Name)
                    {
                        slot.Name = name;
                        cfg.Save();
                    }
                }

                ImGui.EndCombo();
            }

            // 「設定消費数後に交代」だけ、枚数を入れる欄を出す。
            if (cfg.MapTurn == MapTurnMode.AfterCount)
            {
                ImGui.SameLine(0, 6);
                ImGui.TextUnformatted("×");
                ImGui.SameLine(0, 6);

                var count = slot.Count;
                ImGui.SetNextItemWidth(90f);

                if (ImGui.InputInt("消費数", ref count, 1, 1))
                {
                    slot.Count = Math.Clamp(count, 1, 999);
                    cfg.Save();
                }
            }

            // 2枠目からは、消せるようにする。
            if (!single && cfg.MapTurnOrder.Count > 1)
            {
                ImGui.SameLine(0, 6);

                if (ImGui.Button("−", new Vector2(24, 22)))
                {
                    cfg.MapTurnOrder.RemoveAt(i);
                    cfg.Save();
                    ImGui.PopID();
                    break;
                }
            }

            ImGui.PopID();
        }

        if (single)
        {
            ImGui.TextDisabled(
                "この人の地図が尽きたら周回を終わります。");
            return;
        }

        // ＋ボタン。パーティの人数までしか増やせない。
        var limit = Math.Min(party.Count, MapTurnTable.MaxSlots);

        if (cfg.MapTurnOrder.Count < limit)
        {
            if (ImGui.Button("＋", new Vector2(28, 22)))
            {
                cfg.MapTurnOrder.Add(new MapTurnSlot());
                cfg.Save();
            }
        }

        ImGui.TextDisabled("①番目へループします");
        ImGui.TextDisabled(
            "並べた全員の地図が尽きたら停止します。\n"
            + "順番が来た人が持っていないだけなら、その人を飛ばして次に進みます。");
    }

    /// <summary>丸数字。9を超えたら「(10)」のように書く。</summary>
    private static string CircledNumber(int index) => index switch
    {
        0 => "①", 1 => "②", 2 => "③", 3 => "④",
        4 => "⑤", 5 => "⑥", 6 => "⑦", 7 => "⑧",
        _ => $"({index + 1})",
    };

    /// <summary>
    /// ロットの色。
    ///
    /// 設定欄と、畳んだときのバーで同じ色を使う。
    /// 別々に書くと、片方だけ直したときに食い違う。
    /// </summary>
    internal static Vector4 ColorFor(RollChoice choice) => choice switch
    {
        RollChoice.Need  => new Vector4(1f, 0.45f, 0.45f, 1f),
        RollChoice.Greed => new Vector4(1f, 0.85f, 0.35f, 1f),
        _                => new Vector4(0.55f, 0.75f, 1f, 1f),
    };

    /// <summary>ロットの名前。</summary>
    internal static string NameFor(RollChoice choice) => choice switch
    {
        RollChoice.Need  => "Need",
        RollChoice.Greed => "Greed",
        _                => "Pass",
    };

    /// <summary>ロットの選択肢を1つ描く。選ばれていれば色を付ける。</summary>
    private static void DrawRollChoice(Configuration cfg, RollChoice choice, string label, Vector4 color)
    {
        var selected = cfg.RollOption == choice;

        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.CheckMark, color);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
        }

        if (ImGui.RadioButton(label, selected) && !selected)
        {
            cfg.RollOption = choice;
            cfg.Save();
        }

        if (selected)
            ImGui.PopStyleColor(2);
    }

    /// <summary>
    /// 調べもの用の設定。
    ///
    /// ふだんは閉じておく。初めて使う人や、調べものをしない人には要らない。
    /// 開いたかどうかは覚えておく（更新のたび閉じられると煩わしいため）。
    /// </summary>
    /// <summary>「デバッグ」のバーを何回押したか。</summary>
    private int _debugTaps;

    /// <summary>最後に押した時刻。間が空いたら数え直す。</summary>
    private DateTime _lastDebugTap = DateTime.MinValue;

    /// <summary>これだけ押すと開く。</summary>
    private const int DebugTapsToOpen = 5;

    /// <summary>
    /// 調べもの用の設定。
    ///
    /// <b>ふだんは隠してある。</b>
    /// 初めて使う人や、調べものをしない人には要らない。
    /// 押し間違いで開かないよう、「デバッグ」のバーを
    /// 続けて5回押したときだけ出す。
    ///
    /// 一度開けば、閉じるまで開いたまま。
    /// 開いたかどうかは覚えておく（更新のたび閉じられると煩わしいため）。
    /// </summary>
    private void DrawDebugSettings(Configuration cfg)
    {
        // <b>開くまでは、何も出さない。</b>
        // 以前は「デバッグ」のバーを出していたが、
        // 「設定」の折りたたみを無くしたことで、そのバーだけが目立ち、
        // かえって「ここに何かある」と分かるようになってしまった。
        //
        // 今は「状態」の文字を5回押すと開く（HandleDebugUnlockClick）。
        if (!cfg.ShowDebugSettings)
            return;

        ImGui.Separator();

        if (ImGui.CollapsingHeader("デバッグ"))
        {
            ImGui.Indent();

            // ふだん使う設定は「設定」のバーへ移した（DrawSettingsBar）。
            // ここには調べもの用だけを置く。
            DrawDebugBody(cfg);

            ImGui.Unindent();
        }

        if (ImGui.Button("デバッグを隠す", new Vector2(160, 24)))
        {
            cfg.ShowDebugSettings = false;
            _debugTaps = 0;
            cfg.Save();
        }
    }

    /// <summary>デバッグの中身。</summary>
    private void DrawDebugBody(Configuration cfg)
    {
        ImGui.TextDisabled("※ 記録を取ると動作が重くなることがあります");

        // ---- 記録 -----------------------------------------------------------
        ImGui.Spacing();
        ImGui.TextUnformatted("記録");

        if (_log.IsRecording)
        {
            if (ImGui.Button("記録を終える", new Vector2(160, 24)))
                _log.Stop();
        }
        else
        {
            if (ImGui.Button("記録を始める", new Vector2(160, 24)))
            {
                _observer.Reset();
                _log.Start();
            }
        }

        var autoStart = cfg.AutoStartLog;
        if (ImGui.Checkbox("ログインしたら自動で記録を始める", ref autoStart))
        {
            cfg.AutoStartLog = autoStart;
            cfg.Save();

            // 今ログイン中なら、その場で始める（次のログインを待たない）。
            if (autoStart && !_log.IsRecording)
            {
                _observer.Reset();
                _log.Start();
            }
        }

        ImGui.TextDisabled($"このキャラクター: {RunLog.CharacterName}");
        ImGui.TextWrapped($"保存先: {RunLog.LogDirectory}");

        // ---- 調べもの -------------------------------------------------------
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("調べもの");

        var dumpUi = cfg.DumpGreedTrapUi;
        if (ImGui.Checkbox("強欲の罠で画面と世界の中身を書き出す", ref dumpUi))
        {
            cfg.DumpGreedTrapUi = dumpUi;
            cfg.Save();
        }

        var study = cfg.GreedTrapStudyMode;
        if (ImGui.Checkbox("強欲の罠で札が開いたら止まる", ref study))
        {
            cfg.GreedTrapStudyMode = study;
            cfg.Save();
        }

        var finalStop = cfg.StopAtFinalRoom;
        if (ImGui.Checkbox("最下層で止まる", ref finalStop))
        {
            cfg.StopAtFinalRoom = finalStop;
            cfg.Save();
        }

        var roleDesc = cfg.ShowRoleDescription;
        if (ImGui.Checkbox("役割の説明文を出す", ref roleDesc))
        {
            cfg.ShowRoleDescription = roleDesc;
            cfg.Save();
        }

        ImGui.Spacing();

        if (ImGui.Button("今の画面の中身を書き出す", new Vector2(220, 24)))
        {
            var folder = System.IO.Path.Combine(
                ECommons.DalamudServices.Svc.PluginInterface.ConfigDirectory.FullName, "uidump");

            var path = UiDump.Capture(folder, "手動", "TreasureHighLow");

            if (path != null)
                ECommons.DalamudServices.Svc.Chat.Print($"[AutoTreasure] 書き出しました: {path}");
        }

        if (ImGui.Button("今の世界の様子を書き出す", new Vector2(220, 24)))
        {
            var folder = System.IO.Path.Combine(
                ECommons.DalamudServices.Svc.PluginInterface.ConfigDirectory.FullName, "uidump");

            var path = UiDump.CaptureWorld(folder, "手動");

            if (path != null)
                ECommons.DalamudServices.Svc.Chat.Print($"[AutoTreasure] 書き出しました: {path}");
        }

        // ---- 今の状態 -------------------------------------------------------
        ImGui.Spacing();
        ImGui.Separator();
        DrawDiagnosticsBody();
    }

    /// <summary>
    /// 今どうなっているかを並べる。
    ///
    /// 「開発用」という独立した欄はやめ、デバッグの中に入れた。
    /// 調べるときにしか見ないので、一か所にまとまっている方がよい。
    /// </summary>
    private void DrawDiagnosticsBody()
    {
        ImGui.TextUnformatted("今わかっていること");

        ImGui.TextUnformatted($"  経路探索: {(VNavmesh.UsingEmbedded ? "取り込み版（このプラグインの中）" : VNavmesh.IsEnabled ? "外部の vnavmesh" : "なし")}");
        var meshReady = VNavmesh.NavIsReady;
        var meshProgress = VNavmesh.NavBuildProgress;

        ImGui.TextUnformatted(meshReady
            ? "  地形の準備: できている"
            : meshProgress >= 0f
                ? $"  地形の準備: 読み込み中（{meshProgress * 100:F0}%）"
                : "  地形の準備: まだ");
        ImGui.TextUnformatted($"  エリア: {PlayerHelper.TerritoryType}");
        ImGui.TextUnformatted($"  魔紋の中: {(VaultRoutine.IsInsideVault() ? "はい" : "いいえ")}");

        if (VaultRoutine.IsInsideVault())
        {
            ImGui.TextUnformatted($"  今の高さ: {PlayerHelper.Position.Y:F1}（20y以上変わったら階層移動）");
        }
        ImGui.TextUnformatted($"  マウント: {(PlayerHelper.IsMounted ? "乗っている" : "降りている")}");
        ImGui.TextUnformatted($"  飛行: {(PlayerHelper.IsFlying ? "飛んでいる" : "飛んでいない")}");
        ImGui.TextUnformatted($"  戦闘: {(PlayerHelper.InCombat ? "戦闘中" : "非戦闘")}");

        // 解読済み地図の直接読み取り。旗に頼らない経路の確認用。
        var decoded = DecodedMapReader.Read();
        ImGui.TextUnformatted(decoded == null
            ? "  解読済みの地図: 持っていない（または読めない）"
            : $"  解読済みの地図: Rank {decoded.Value.Rank} / {decoded.Value.SubRow} 番目");

        var located = DecodedMapReader.Locate();
        if (located != null)
            ImGui.TextUnformatted($"    → {located.Value.PlaceName} {RunLog.Format(located.Value.World)}");

        var flag = MapFlagReader.Read();
        ImGui.TextUnformatted(flag == null
            ? "  マップのフラグ: なし"
            : $"  マップのフラグ: エリア {flag.Value.TerritoryType} ({flag.Value.X:F1}, {flag.Value.Z:F1})");

        ImGui.TextUnformatted($"  近くの宝箱: {ObjectHelper.GetTreasures().Count} 個");
        ImGui.TextUnformatted($"  近くの仕掛け: {ObjectHelper.GetEventObjects().Count} 個");
        ImGui.TextUnformatted($"  近くの敵: {ObjectHelper.CountLivingEnemies(50f)} 体");
        ImGui.TextUnformatted($"  ディグ: {(ActionHelper.CanDig() ? "使える" : "使えない")}");
    }
}
