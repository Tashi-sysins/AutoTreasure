using AutoTreasure.Helpers;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;

namespace AutoTreasure.Logic;

/// <summary>
/// 「強欲の罠」の面倒を見る。
///
/// 罠が出ている間は、扉も宝箱も触れない。
/// 罠を片付けるまで先へ進めないので、ほかの何よりも先に扱う。
///
/// <b>放っておくと扉へ走って詰まる。</b>
/// 実測（2026-09-18）:
///   「解除に挑む」を押すと動けるようになるが、罠は続いている。
///   その状態で扉へ向かうと、扉に触れず、その場で止まったままになった。
///
/// 進み方:
///   1. 罠のウィンドウが出る（この時点で HIGH/LOW は触れない）
///   2. 「解除に挑む」か「解除に挑まない」を選ぶ
///   3. 挑むなら、HIGH/LOW が触れるようになる
///   4. 床の HIGH か LOW に触れて賭ける
///   5. 当たれば報酬が上がり、1 に戻る（最大3回）
/// </summary>
internal static unsafe class GreedTrap
{
    /// <summary>罠のウィンドウが出ているか。</summary>
    internal static bool IsOpen => AddonHelper.IsAddonReady(GreedTrapReader.AddonName);

    /// <summary>
    /// 罠に関わっている最中か。
    ///
    /// ウィンドウが閉じても、HIGH/LOW に触れるうちは終わっていない。
    /// 「挑む」を押した直後はウィンドウが消えて床を選ぶ番になるため、
    /// ウィンドウの有無だけで判断すると、その間に扉へ走り出す。
    /// </summary>
    internal static bool IsActive => !_declined && (IsOpen || FindPad() != null);

    /// <summary>
    /// 賭けないと決めたか。
    ///
    /// 床（HIGH/LOW）は触れるまで消えない。
    /// 「賭けない」と決めたあとも <see cref="FindPad"/> は床を見つけるので、
    /// これが無いと毎フレーム罠の処理へ戻り、先へ進めなくなる。
    /// 次の区画に入ったら <see cref="Reset"/> で戻す。
    /// </summary>
    private static bool _declined;

    /// <summary>
    /// この罠で賭けた回数。
    ///
    /// 数字が読めないうちは1回目だけ賭ける。
    /// 2回目以降は、積み上がった分を失う方が痛いので挑まない。
    /// </summary>
    private static int _attempts;

    /// <summary>
    /// 床に触れて、勝負の成立を待っている最中か。
    ///
    /// 触った回数ではなく<b>成立した勝負の回数</b>を数えるために使う。
    /// 触っても弾かれることがあり、同じ勝負で何度も触れるため、
    /// 接触のたびに数えると実際の回数と合わなくなる。
    /// </summary>
    private static bool _betPending;

    /// <summary>
    /// 挑める回数の上限。
    ///
    /// 10回続けて勝つと、ゲーム側で罠が強制終了する（利用者の説明）。
    /// それ以上は挑めないので、こちらからも挑まない。
    /// </summary>
    private const int MaxBets = 10;

    /// <summary>
    /// 調べもののため、周回を止めてほしいか。
    ///
    /// 札が開いた場面で立てる。呼ぶ側がこれを見て止める。
    /// </summary>
    internal static bool StudyStopRequested { get; private set; }

    /// <summary>
    /// 罠を片付ける。片付いたら true。
    ///
    /// まだ途中なら false を返す。呼ぶ側は、ほかのことをせずに待つ。
    /// </summary>
    /// <param name="challenge">
    /// 解除に挑むか。false なら、その場で報酬を確定させる。
    /// </param>
    /// <param name="note">今していることの説明。画面に出す。</param>
    /// <param name="record">記録に残したいことがあれば呼ばれる。</param>
    internal static bool Tick(bool challenge, out string note, Action<string>? record = null)
    {
        note = string.Empty;

        // 罠に関わっている間、画面の中身を丸ごと書き出す。
        //
        // 数字は絵で描かれていて、文字としては出てこない。
        // だが「文字に無い」のと「どこにも無い」のは別。
        // 画面に渡された値（AtkValues）や、画面を作る元のデータ
        // （ArrayData）に残っている可能性がある。
        //
        // 写真を撮って見比べる前に、まず中身を全部取っておく。
        // 1回でも罠が出れば、あとから落ち着いて調べられる。
        CaptureIfNeeded(record);

        // 賭ける床が出ているなら、そちらが先。
        // ウィンドウより床の方が後の段階なので、先に片付ける。
        var pad = FindPad();
        if (pad != null)
        {
            // カードが出るのは、この段階になってから。
            //
            // 「解除に挑む」を押す前のウィンドウには、報酬の数字しか無い。
            // 絵の番号を書き出すのはここでないと意味がない
            // （実測 2026-09-18 07:12。押す前の画面を書き出していたため、
            //   カードの絵が一度も記録に残らなかった）。
            if (record != null)
                RecordOnce(record, $"greed-cards-{_attempts}", GreedTrapReader.DescribeImageParts());

            // 調べもの用。札が開いたところで手を止める。
            //
            // ここで画面を撮ってもらい、見えている札の数字と
            // 記録に残った絵の番号を突き合わせて対応表を作る。
            if (Plugin.Config.GreedTrapStudyMode)
            {
                StudyStopRequested = true;
                MovementHelper.Stop();
                note = "強欲の罠: 札が開きました（調べもののため停止）";
                return false;
            }

            note = "強欲の罠: どちらに賭けるか選んでいます";
            return TickPad(pad, note: ref note, record);
        }

        if (!IsOpen)
            return true;

        // ウィンドウが出ている。挑むかどうかを決める。
        if (!EzThrottler.Throttle("AutoTreasure.GreedTrap", 1000))
        {
            note = "強欲の罠: 判断しています";
            return false;
        }

        // 数字が読めるなら、それを見て決める。
        // 読めないうちは、確定させる方を選ぶ（外すと全部失うため）。
        var number = GreedTrapReader.ReadNumber();

        // 調べもの用のときは、数字が読めなくても必ず挑む。
        // 札を開かせないと、絵を記録できないため。
        // 1回目は、数字が読めなくても挑む。
        // 数字は絵で描かれていて UI からは読めないため、
        // 待っていても読めるようにはならない。
        // 10回続けて勝つと、ゲーム側で罠が強制終了する（利用者の説明）。
        // それ以上は挑めないので、上限に達したら降りる。
        if (_attempts >= MaxBets)
        {
            RecordOnce(record, $"greed-maxed-{_attempts}",
                $"強欲の罠: {MaxBets} 回に達したので、ここで確定させます");

            if (ClickButton("解除に挑まない"))
            {
                note = "強欲の罠: 上限に達したので確定させました";
                return true;
            }

            // <b>押せなくても、ここで打ち切る。</b>
            // 下へ落ちると、調べもの用の設定が入っているときに
            // willChallenge が無条件で true になり、
            // 上限に達しているのに「解除に挑む」を押してしまう。
            //
            // 押せないのは、ウィンドウが一瞬まだ整っていないときなど。
            // 次のフレームでまたここへ来るので、待てばよい。
            note = "強欲の罠: 上限に達しました（確定させています）";
            return false;
        }

        var willChallenge = Plugin.Config.GreedTrapStudyMode
                         || (challenge
                             && (number == null
                                 ? _attempts == 0
                                 : GreedTrapPolicy.Decide(number.Value, isFirstBet: _attempts == 0)
                                   != GreedChoice.Stop));

        if (record != null)
        {
            // 絵の番号を書き出しておく。
            // どの絵が数字のいくつかを突き止めるのに使う。
            RecordOnce(record, $"greed-parts-{_attempts}", GreedTrapReader.DescribeImageParts());

            RecordOnce(record, $"greed-decide-{_attempts}", number != null
                ? $"強欲の罠: {GreedTrapPolicy.Explain(number.Value, _attempts == 0)}"
                : "強欲の罠: 数字を読めないので挑みません"
                + "（挑むと期待値 8,333 / 降りれば 10,000 確定）");
        }

        var label = willChallenge ? "解除に挑む" : "解除に挑まない";

        if (ClickButton(label))
        {
            record?.Invoke($"強欲の罠: 「{label}」を選びました");
            note = $"強欲の罠: {label}";

            // 「挑まない」なら、これで終わり。
            // 「挑む」なら、このあと床を選ぶ番になる。
            return !willChallenge;
        }

        note = "強欲の罠: ボタンを探しています";
        return false;
    }

    /// <summary>
    /// 罠の画面の中身を書き出す。
    ///
    /// 2回だけ取る。
    ///   1回目 … 罠のウィンドウが出た直後（まだ札は伏せられている）
    ///   2回目 … 札が開いて HIGH/LOW を選ぶ番になったとき
    ///
    /// 2つを見比べると、「札が開いたときに何が変わったか」が分かる。
    /// 変わったところに数字がある。
    /// </summary>
    private static void CaptureIfNeeded(Action<string>? record)
    {
        if (!Plugin.Config.DumpGreedTrapUi)
            return;

        // 札が開いているかどうかで、取るタイミングを分ける。
        // 同じ罠でも、勝負ごとに記録する。
        //
        // 以前はキーが固定だったため、2回目以降の勝負が
        // 一度も記録されなかった。勝てば札は入れ替わるので、
        // そのたびに取らないと対応表が作れない。
        var padsOut = FindPad() != null;
        var key = (padsOut ? "dump-open-" : "dump-closed-") + _attempts;

        if (!_recorded.Add(key))
            return;

        var label = (padsOut ? "強欲の罠_札あり" : "強欲の罠_札なし") + $"_{_attempts + 1}回目";

        var path = UiDump.Capture(DumpFolder, label, GreedTrapReader.AddonName);

        if (path != null)
            record?.Invoke($"強欲の罠: 画面の中身を書き出しました → {path}");

        // 札が伏せられているときも、世界の様子を書き出しておく。
        //
        // 開いたときのものと見比べれば、「札が現れたときに
        // 何が増えたか」が分かる。増えたものが札である可能性が高い。
        if (!padsOut)
        {
            var beforePath = UiDump.CaptureWorld(DumpFolder, $"強欲の罠_札なし_世界_{_attempts + 1}回目");

            if (beforePath != null)
                record?.Invoke($"強欲の罠: 世界の様子を書き出しました → {beforePath}");
        }

        // 札が開いているなら、世界にあるものも書き出す。
        //
        // <b>札は画面ではなく世界に浮かんでいる。</b>
        // 実測（2026-09-18 13:54）で、札が開いた瞬間には
        // 罠のウィンドウが消えていた。つまり札はUIの部品ではない。
        //
        // だとすれば Svc.Objects に出ているはずで、
        // その DataId が数字に対応している可能性がある。
        // 種類で絞らず全部書き出して、あとから突き合わせる。
        if (padsOut)
        {
            var worldPath = UiDump.CaptureWorld(DumpFolder, $"強欲の罠_札あり_世界_{_attempts + 1}回目");

            if (worldPath != null)
                record?.Invoke($"強欲の罠: 世界の様子を書き出しました → {worldPath}");
        }
    }

    /// <summary>画面の中身を書き出す先。記録と同じ場所にまとめる。</summary>
    private static string DumpFolder
        => System.IO.Path.Combine(
            Svc.PluginInterface.ConfigDirectory.FullName, "uidump");

    /// <summary>床の HIGH / LOW を選ぶ。</summary>
    private static bool TickPad(IGameObject pad, ref string note, Action<string>? record)
    {
        // 確認ウィンドウが出ていれば、それが最優先。
        // 「「HIGH」を選択しますか？」に答えないと先へ進まない。
        if (AddonHelper.IsAddonReady("SelectYesno"))
        {
            if (AddonHelper.ClickSelectYesno())
            {
                // ここで初めて勝負が成立したとみなす。
                //
                // 床に触れただけでは成立しない（弾かれることがある）。
                // 「はい」を押せた＝ゲームが賭けを受け付けた、と考える。
                if (_betPending)
                {
                    _betPending = false;
                    _attempts++;
                    record?.Invoke($"強欲の罠: {_attempts} 回目の勝負が成立しました");
                }

                note = "強欲の罠: 賭けました";
            }

            return false;
        }

        var number = GreedTrapReader.ReadNumber();

        // <b>数字が読めないなら、賭けない。</b>
        //
        // 以前は「1回目は失うものが無い」と考えて決め打ちで賭けていたが、
        // それは誤りだった。実測（利用者に確認・2026-09-18）:
        //   1回目に失敗すると報酬は 0 になる。確定させれば 10000 残る。
        //
        // 期待値を計算すると、挑まない方が得だった。
        //   HIGH 決め打ち … 成功率 55.6%（同数は成功扱い）
        //                    期待値 0.556 × 15000 ＝ 8,333
        //   降りる         … 10,000 が確定
        //
        // 数字が読めれば話は変わる。
        //   1〜5 HIGH / 6〜9 LOW … 成功率 80.2%
        //                          期待値 0.802 × 15000 ＝ 12,037
        //
        // つまり<b>札を読めるようにして初めて、挑む価値が生まれる</b>。
        // 読めないうちは黙って確定させる方が、周回あたりの取り分が多い。
        var choice = number != null
            ? GreedTrapPolicy.Decide(number.Value, isFirstBet: _attempts == 0)
            : GreedChoice.Stop;

        // 4・5・6、または数字が読めないときは賭けない。
        //
        // ここで「やめる」を選べるのは、ウィンドウの
        // 「解除に挑まない」だけ。床には HIGH と LOW しかない。
        // 以前はここで High に倒していたので、
        // 「確定させる」と判断したのに賭けてしまっていた。
        //
        // ウィンドウが残っていれば、そちらで降りる。
        if (choice == GreedChoice.Stop)
        {
            RecordOnce(record, $"greed-stop-{_attempts}", number != null
                ? $"強欲の罠: {number} は分が悪いので、報酬を確定させます"
                : "強欲の罠: 数字を読めないので、報酬を確定させます");

            if (IsOpen && ClickButton("解除に挑まない"))
            {
                note = "強欲の罠: 報酬を確定させました";
                return true;
            }

            // ウィンドウが無く、床しか残っていない場合。
            //
            // 降りる手立てが無い。ここで待ち続けると、
            // 制限時間（300秒）まで周回が止まってしまう。
            // 床に触らなければ賭けは成立せず、確保した分は残るので、
            // 罠は片付いたものとして扉へ進む。
            RecordOnce(record, $"greed-skip-{_attempts}",
                "強欲の罠: 降りるボタンが無いので、賭けずに先へ進みます");

            _declined = true;
            note = "強欲の罠: 賭けずに先へ進みます";
            return true;
        }

        var wantHigh = choice == GreedChoice.High;

        // 希望する側の床だけを狙う。反対側で代用しない。
        var target = FindPad(wantHigh);

        if (target == null)
        {
            RecordOnce(record, $"greed-nopad-{_attempts}",
                $"強欲の罠: {(wantHigh ? "HIGH" : "LOW")} の床が見つかりません。賭けずに待ちます");

            MovementHelper.Stop();
            note = $"強欲の罠: {(wantHigh ? "HIGH" : "LOW")} が見つかりません";
            return false;
        }

        var distance = ObjectHelper.DistanceToPlayer(target);

        if (distance > 4f)
        {
            MovementHelper.MoveTo(target.Position, 3f, false);
            note = $"強欲の罠: {(wantHigh ? "HIGH" : "LOW")} へ向かっています（{distance:F0} m）";
            return false;
        }

        MovementHelper.Stop();

        if (EzThrottler.Throttle("AutoTreasure.GreedPad", 1000))
        {
            RecordOnce(record, $"greed-pad-{_attempts}",
                $"強欲の罠: {(wantHigh ? "HIGH" : "LOW")} を選びます");

            // 乗ったままでは触れない。先に降りる。
            MovementHelper.Dismount();
            ObjectHelper.Interact(target);

            // <b>ここでは数えない。</b>
            // 触れても弾かれることがあり、同じ勝負で何度も触れる。
            // 触った回数と勝負の回数は別物なので、
            // 「勝負が成立した」と確かめられたところで数える。
            _betPending = true;
        }

        note = $"強欲の罠: {(wantHigh ? "HIGH" : "LOW")} を選んでいます";
        return false;
    }

    /// <summary>触れる状態の HIGH / LOW を探す。</summary>
    private static IGameObject? FindPad(bool? wantHigh = null)
    {
        var pads = ObjectHelper.GetEventObjects()
            .Where(VaultRoutine.IsGamblePad)
            .ToList();

        if (pads.Count == 0)
            return null;

        if (wantHigh == null)
            return pads.OrderBy(ObjectHelper.DistanceToPlayer).FirstOrDefault();

        // HIGH は偶数、LOW は奇数。
        //
        // 実測（2026-09-17／09-18）:
        //   第1区画 HIGH 2013872 / LOW 2013873
        //   第2区画 HIGH 2013874 / LOW 2013875
        //   第3区画 HIGH 2013876 / LOW 2013877
        //   第4区画 HIGH 2013878 / LOW 2013879
        var want = wantHigh.Value;

        // <b>希望する側が無ければ null を返す。反対側で代用しない。</b>
        //
        // 以前はここで「無ければ近い方」としていた。
        // その結果、HIGH に賭けるつもりで LOW に触れることが起きうる。
        // 数字を読めるようになったとき、正しく判断したのに
        // 逆に賭けるという最悪の事故になる。
        //
        // 見つからないなら賭けない方がよい。触らなければ勝負は成立せず、
        // 確保した分は残る。
        return pads.FirstOrDefault(o => (o.BaseId % 2 == 0) == want);
    }

    /// <summary>
    /// 罠のウィンドウのボタンを押す。
    ///
    /// ボタンは文字を持つ部品なので、文字で探して番号を割り出す。
    /// 番号を決め打ちにすると、更新で並びが変わったときに
    /// 「挑まない」のつもりで「挑む」を押してしまう。
    /// </summary>
    private static bool ClickButton(string label)
    {
        try
        {
            if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(
                    GreedTrapReader.AddonName, out var addon)
                || !GenericHelpers.IsAddonReady(addon))
                return false;

            // ボタンを押す。
            //
            // ReceiveEvent でノード番号を渡す方法は通らなかった
            // （実測 2026-09-18 07:12。ウィンドウが閉じず、
            //   1秒ごとに押し直しを繰り返した）。
            // このウィンドウは Callback で受け取る作りなので、そちらを使う。
            //
            // 番号の対応（実測のボタン並び）:
            //   0 = 解除に挑む / 1 = 解除に挑まない
            var index = label switch
            {
                "解除に挑む"     => 0,
                "解除に挑まない" => 1,
                _                => -1,
            };

            if (index < 0)
                return false;

            Callback.Fire(addon, true, index);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "強欲の罠のボタン操作に失敗しました。");
            return false;
        }
    }

    // 以前はここに、画面の部品をたどって文字からボタンを探す処理があった
    // （FindButton / HasText）。
    //
    // <b>消した理由。</b>
    //   1. どこからも呼ばれていなかった。
    //      ボタンを押すのは Callback.Fire（上の ClickButton）に変わっている。
    //   2. 危なかった。
    //      NodeList の中身が用意されているかを確かめずにたどっていた。
    //      罠のウィンドウが閉じる瞬間に走ると、
    //      ゲームごと落ちる種類の失敗になる（C# の catch では受け止められない）。
    //
    // 使っていないうえに落ちる危険だけがある、という状態だったので消した。
    // 番号の実測は残しておく: Id 46「解除に挑む」/ 47「解除に挑まない」/ 48「閉じる」。

    private static void RecordOnce(Action<string>? record, string key, string text)
    {
        if (record == null || !_recorded.Add(key))
            return;

        record(text);
    }

    /// <summary>罠が終わったら、また書き出せるようにする。</summary>
    internal static void Reset()
    {
        _recorded.Clear();
        _declined = false;
        _attempts = 0;
        _betPending = false;
        StudyStopRequested = false;
    }

    private static readonly System.Collections.Generic.HashSet<string> _recorded = [];
}
