using AutoTreasure.IPC;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Numerics;

namespace AutoTreasure.Helpers;

/// <summary>
/// 目的地まで運ぶ。
///
/// 経路探索と実際の移動は vnavmesh に任せる。ここが受け持つのは
///   ・マウントに乗せること（vnavmesh はマウントを呼び出さない）
///   ・到着したかどうかの判断
///   ・進まなくなったときに気づくこと
/// の3つ。
/// </summary>
internal static unsafe class MovementHelper
{
    /// <summary>
    /// 足元に地形があるとみなす高さの幅。
    ///
    /// 段差や坂を考えて少し広めに取る。
    /// 広すぎると、運ばれている最中を見逃す。
    /// </summary>
    private const float MeshFootTolerance = 10f;

    /// <summary>
    /// 足元に地形が無い状態を、これだけ見送る。
    ///
    /// ワープ床の運搬は実測2.7秒で終わる。
    /// それを十分に越えても外れないなら、運搬ではなく
    /// 地形の外に立っているだけなので、見送るのをやめる。
    /// </summary>
    private const double OffMeshGiveUpSeconds = 6.0;

    /// <summary>足元に地形が無くなった時刻。戻ったら消す。</summary>
    private static DateTime _offMeshSince = DateTime.MinValue;

    /// <summary>マウントを呼び出す／降りる（マウントルーレット）。</summary>
    private const uint GeneralActionMountRoulette = 9;

    /// <summary>ジャンプ。マウントに乗っている間に使うと飛び立つ。</summary>
    private const uint GeneralActionJump = 2;

    /// <summary>スプリント。</summary>
    private const uint GeneralActionSprint = 4;

    /// <summary>降りる。</summary>
    private const uint GeneralActionDismount = 23;

    // 乗る・飛ぶ・降りるで、待ち時間を別々に持つ。
    //
    // 1つにまとめると、降りた直後に「乗る」が塞がれる（またはその逆）。
    // 到着して降りる → 掘れない → 近づき直す、のような場面で
    // 実際に噛み合わなくなる。
    private static DateTime _nextMount = DateTime.MinValue;
    private static DateTime _nextTakeoff = DateTime.MinValue;
    private static DateTime _nextDismount = DateTime.MinValue;

    /// <summary>「飛べない」と読めた最初の時刻。少し待って読み直すために使う。</summary>
    private static DateTime _groundedSince = DateTime.MinValue;

    /// <summary>これだけ待っても飛べないなら、本当に飛べない場所とみなす。</summary>
    private const double FlightGiveUpSeconds = 3.0;

    /// <summary>
    /// 今いる場所で飛べるか。
    ///
    /// エリアの種別を一覧で判定する方法は使わない。拡張のたびに値が増え、
    /// 抜けがあると「飛べるのに飛ばない」「飛べないのに飛ぼうとする」が起きるため。
    /// ゲームが持っている「飛べるか」の判定をそのまま使う。
    /// </summary>
    internal static bool CanFly
    {
        get
        {
            try { return PlayerHelper.IsValid && Control.CanFly; }
            catch { return false; }
        }
    }

    /// <summary>
    /// このエリアで飛べるか（今マウントに乗っているかは問わない）。
    ///
    /// <see cref="CanFly"/> は「今すぐ飛べるか」を答える。
    /// 徒歩のときは NotMounted（乗っていない）を返すため、false になる。
    /// これを「飛べない場所だ」と受け取ると、乗らずに歩き出してしまう。
    /// 実際、テレポ直後に徒歩で向かってしまった。
    ///
    /// 行き先を決める前に見るのはこちら。
    /// 「乗っていないから飛べない」と「この場所では飛べない」を区別する。
    /// </summary>
    internal static bool CanFlyHere
    {
        get
        {
            try
            {
                if (!PlayerHelper.IsValid)
                    return false;

                var status = Control.GetFlightAllowedStatus();

                return status switch
                {
                    // 今すぐ飛べる。
                    Control.FlightAllowedStatus.CanFly => true,

                    // 乗っていないだけ。乗れば飛べる。
                    Control.FlightAllowedStatus.NotMounted => true,

                    // それ以外（この場所では飛べない、解放していない等）。
                    _ => false,
                };
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// マウントに乗せる。必要なら飛び立たせる。
    ///
    /// vnavmesh は「騎乗中なら自分でジャンプして離陸する」が、
    /// 「マウントを呼び出す」ことはしない。そこをここで埋める。
    ///
    /// <b>未騎乗のまま飛行経路を渡すと、vnavmesh は無言でその場に止まる。</b>
    /// エラーもログも出ず、移動中の扱いのままになるため、必ず先に乗せる。
    /// </summary>
    /// <param name="needFlight">飛び立たせるところまで面倒を見るか。</param>
    /// <returns>準備が整っていれば true。まだなら false（次のフレームで呼び直す）。</returns>
    internal static bool EnsureMounted(bool needFlight)
    {
        if (!PlayerHelper.IsValid)
            return false;

        // 潜水中は「飛んでいない」扱いになる。ここで離陸させようとすると
        // 水面に出ては潜るを繰り返すので、そのまま進んでよいことにする。
        if (PlayerHelper.IsDiving)
            return true;

        // 詠唱中に割り込むと詠唱が消える。終わるのを待つ。
        if (PlayerHelper.IsCasting)
            return false;

        if (!PlayerHelper.IsMounted)
        {
            if (DateTime.UtcNow >= _nextMount)
            {
                VNavmesh.PathStop();
                UseGeneralAction(GeneralActionMountRoulette);
                // 騎乗の詠唱が終わるまで待つ。連打すると乗り降りを繰り返す。
                _nextMount = DateTime.UtcNow.AddSeconds(2.5);
            }
            return false;
        }

        if (needFlight && !PlayerHelper.IsFlying)
        {
            // 乗った直後は、まだ「飛べるか」を正しく答えられないことがある。
            //
            // ここで CanFly を一度読んだだけで「飛べない場所だ」と決めてしまうと、
            // 本当は飛べるのに、その周回はずっと地上を走ることになる。
            // 3台で同時に動かすと、たまたま読めた機だけが飛ぶ——
            // という食い違いが起きる。実際、地上を走る機と飛ぶ機に分かれた。
            //
            // そこで、乗ってからしばらくは答えを保留して読み直す。
            if (!CanFly)
            {
                if (_groundedSince == DateTime.MinValue)
                    _groundedSince = DateTime.UtcNow;

                // それでも飛べないままなら、本当に飛べない場所。地上を走る。
                if (DateTime.UtcNow - _groundedSince >= TimeSpan.FromSeconds(FlightGiveUpSeconds))
                {
                    // 数え直せるように戻しておく。
                    //
                    // 戻さないと、そのあと飛べる場所へ移っても
                    // 「もう十分待った」と即座に判断され、
                    // エリアが変わる（ResetMountCooldown）まで飛べないままになる。
                    _groundedSince = DateTime.MinValue;
                    return true;
                }

                return false;   // まだ判断しない。次のフレームで読み直す
            }

            _groundedSince = DateTime.MinValue;

            if (DateTime.UtcNow >= _nextTakeoff)
            {
                UseGeneralAction(GeneralActionJump);
                _nextTakeoff = DateTime.UtcNow.AddSeconds(1.5);
            }
            return false;
        }

        _groundedSince = DateTime.MinValue;
        return true;
    }

    /// <summary>
    /// 移動の途中で飛行が切れていたら、飛び直す。
    ///
    /// 経路に沿って動いている最中は EnsureMounted を通らないので、
    /// ここで面倒を見る。経路そのものは止めない——止めると探索からやり直しになる。
    /// </summary>
    private static void RecoverFlightIfNeeded()
    {
        if (!PlayerHelper.IsValid || PlayerHelper.IsFlying)
            return;

        // 乗っていないなら、ここでは何もしない。
        // 経路を止めずにマウントは呼べないため、次の機会に任せる。
        if (!PlayerHelper.IsMounted)
            return;

        if (PlayerHelper.IsDiving || PlayerHelper.IsCasting)
            return;

        if (!CanFly)
            return;

        if (DateTime.UtcNow < _nextTakeoff)
            return;

        UseGeneralAction(GeneralActionJump);
        _nextTakeoff = DateTime.UtcNow.AddSeconds(1.5);
    }

    /// <summary>マウントから降りる。</summary>
    internal static void Dismount()
    {
        if (!PlayerHelper.IsMounted)
            return;

        if (DateTime.UtcNow >= _nextDismount)
        {
            UseGeneralAction(GeneralActionDismount);
            _nextDismount = DateTime.UtcNow.AddSeconds(1.5);
        }
    }

    /// <summary>
    /// 走っている間、スプリントを使う。
    /// リキャスト中や戦闘中は何もしない。
    /// </summary>
    internal static void UseSprintIfIdle()
    {
        if (!PlayerHelper.IsValid || PlayerHelper.InCombat || PlayerHelper.IsCasting)
            return;
        if (PlayerHelper.IsMounted || !PlayerHelper.IsMoving)
            return;

        var am = ActionManager.Instance();
        if (am == null)
            return;

        if (am->GetActionStatus(ActionType.GeneralAction, GeneralActionSprint) == 0
            && am->QueuedActionId != GeneralActionSprint)
        {
            UseGeneralAction(GeneralActionSprint);
        }
    }

    /// <summary>
    /// 目的地へ向かわせる。
    ///
    /// 毎フレーム呼ぶ。内部で「もう向かっているか」を見て、
    /// 二重に指示を出さないようにしている。
    /// </summary>
    /// <param name="destination">目的地。</param>
    /// <param name="range">ここまで近づけば良いという距離。</param>
    /// <param name="fly">飛んで向かうか。</param>
    /// <returns>指示を出せた（または既に向かっている）なら true。</returns>
    /// <summary>
    /// 今は動いてよいか。
    ///
    /// <b>移動の入口をここ一本にまとめる。</b>
    /// 区画を移っている最中は、どこからの指示でも動いてはいけない。
    /// これまでは「気づいた側」で止めていたが、
    /// <see cref="RunController"/> の別の処理から
    /// すぐに次の <see cref="MoveTo"/> が呼ばれ、動き出せてしまった。
    ///
    /// 止める場所を一か所にすれば、どこから指示が来ても防げる。
    /// </summary>
    internal static bool MovementAllowed { get; set; } = true;

    /// <summary>移動を禁じている理由。記録に残す。</summary>
    internal static string BlockReason { get; private set; } = "";

    /// <summary>移動を禁じた時刻。閉じたままになっていないか見るために使う。</summary>
    private static DateTime _blockedAt = DateTime.MinValue;

    /// <summary>
    /// これだけ禁じたままなら、自動で解く。
    ///
    /// 解く処理に届かない道筋が残っていると、
    /// 一歩も動けないまま周回が終わる。
    /// 搬送は実測で長くても10秒程度なので、余裕を見てこの値にする。
    /// </summary>
    private const double BlockGiveUpSeconds = 30.0;

    /// <summary>移動を禁じる。</summary>
    internal static void Block(string reason)
    {
        // 時刻を入れ直すのは「新しく禁じたとき」だけ。
        //
        // <b>理由が同じなら、数え直さない。</b>
        // 禁じる処理（区画の移動中など）は、条件が続くあいだ毎フレーム呼ばれる。
        // 呼ばれるたびに時刻を入れ直すと、30秒の見切りが永久に来ない。
        //
        // 自動で解いた直後も同じことが起きる。
        // 解く → 同じフレームで禁じ直す → また30秒、を繰り返し、
        // 1フレームだけ動いては止まる、という状態になる。
        if (MovementAllowed || BlockReason != reason)
            _blockedAt = DateTime.UtcNow;

        MovementAllowed = false;
        BlockReason = reason;
    }

    /// <summary>
    /// 禁じたまま長すぎないか見る。長ければ解く。
    ///
    /// <b>毎フレーム、必ず呼ぶこと。</b>
    /// 以前はこの判定が <see cref="MoveTo"/> の中にあった。
    /// ところが「地形を作り直しています」「地形が入れ替わるのを待っています」の
    /// 段階は <see cref="Stop"/> を呼んで戻るだけで、<see cref="MoveTo"/> を通らない。
    /// vnavmesh が地形を作れないまま諦めると（NavmeshWatcher は3回で諦める）、
    /// その段階から一生出られず、禁止も解けないまま周回が止まっていた。
    ///
    /// 動こうとしたときではなく、時間が過ぎたかどうかで解く。
    /// </summary>
    internal static void ReleaseIfStuck()
    {
        if (MovementAllowed)
            return;

        if (DateTime.UtcNow - _blockedAt <= TimeSpan.FromSeconds(BlockGiveUpSeconds))
            return;

        Svc.Log.Warning(
            $"[AutoTreasure] 移動の禁止が {BlockGiveUpSeconds:F0} 秒続いたので解きます（{BlockReason}）。");
        Allow();
    }

    /// <summary>移動を許す。</summary>
    internal static void Allow()
    {
        MovementAllowed = true;
        BlockReason = "";
    }

    /// <summary>
    /// 直前に MoveTo が断った理由。空なら受理している。
    ///
    /// <b>黙って false を返す道筋が多いため。</b>
    /// 実測（2026-09-22）では、宝箱まで12.5mの位置で移動が止まったまま
    /// 4分過ぎたが、何が起きたのか記録から追えなかった。
    /// 断った理由をここに残し、呼んだ側が記録できるようにする。
    /// </summary>
    internal static string LastMoveRefusal { get; private set; } = "";

    private static bool Refuse(string reason)
    {
        LastMoveRefusal = reason;
        return false;
    }

    internal static bool MoveTo(Vector3 destination, float range, bool fly)
    {
        LastMoveRefusal = "";

        if (!PlayerHelper.IsReady)
            return Refuse("まだ操作できません（ロード中・ムービー中など）");

        // 区画を移っている最中は、どこからの指示でも動かない。
        //
        // 長すぎる禁止を解くのは ReleaseIfStuck の役目。
        // ここで解いてはいけない。
        // 解いたそのフレームに禁止し直されると、時刻が入り直って
        // 「1フレームだけ動いては30秒止まる」を繰り返すことになる。
        if (!MovementAllowed)
            return Refuse($"移動を禁じています（{BlockReason}）");

        // 地形が無いと、何を指示しても静かに失敗する。
        // 待つだけでなく、作り始めていなければこちらから促す。
        if (!VNavmesh.NavIsReady)
        {
            NavmeshWatcher.EnsureBuilding();
            return Refuse("地形がまだ読めていません");
        }

        // 自分が地形の上にいないなら、経路は引けない。
        //
        // <b>運ばれている最中がこれにあたる。</b>
        // ワープ床に乗ると宙を移動するので、足元に地形が無い。
        // その状態で経路を頼むと、毎フレーム
        // 「failed to find polygon on a mesh」が出続ける。
        //
        // 実測（2026-09-18 14:19）では、運搬中の2.7秒間に
        // 76回この失敗を繰り返していた（35ミリ秒ごと）。
        // 記録が埋まるうえ、成功する見込みもない。
        //
        // 着いてから引けばよいので、ここでは黙って見送る。
        //
        // <b>飛んでいるときは見ない。</b>
        // 飛行中は地形から高く離れるので、ここで弾くと
        // フィールドの移動そのものができなくなる。
        // 見たいのは「ワープ床で運ばれている最中」だけ。
        //
        // ⚠ ただし<b>見送り続けてはいけない</b>（2026-09-22 実測）。
        //   地図役が岩の上など地形の無い場所で掘ると、
        //   宝箱まで13.5mの位置から<b>一歩も動けないまま</b>になった。
        //   他の3人は同じ場所から普通に歩けており、
        //   この機だけが足元を地形の外と判定されていた。
        //
        //   運搬は実測2.7秒で終わる。数秒待っても外れないなら
        //   それは運搬ではなく、ただ地形の外に立っているだけ。
        //   その場合は<b>断らずに経路を頼む</b>。
        //   vnavmesh は近くの地形へ寄せてくれるので、動き出せる。
        if (!PlayerHelper.IsFlying
            && !VNavmesh.IsPointOnMesh(PlayerHelper.Position, MeshFootTolerance, true))
        {
            if (_offMeshSince == DateTime.MinValue)
                _offMeshSince = DateTime.UtcNow;

            // 運搬が終わるまでの猶予だけ見送る。
            if ((DateTime.UtcNow - _offMeshSince).TotalSeconds < OffMeshGiveUpSeconds)
                return Refuse("自分の足元に地形がありません（運ばれている最中など）");

            // 猶予を過ぎた。運搬ではないので、頼んでみる。
        }
        else
        {
            _offMeshSince = DateTime.MinValue;
        }

        // 経路探索中、または経路に沿って移動中なら、そのまま任せる。
        if (VNavmesh.PathfindInProgress || VNavmesh.PathIsRunning)
        {
            // ただし、飛んでいるはずが落ちていないかだけは見ておく。
            //
            // 移動を始めたあとに飛行が切れることがある。実測では、
            // 離陸から6秒後に落ちて、そこから目的地まで地上を走り続けた。
            // ここを素通りさせると、一度落ちたら二度と飛び直さない。
            if (fly)
                RecoverFlightIfNeeded();

            return true;
        }

        // 飛ぶなら先に乗せる。乗るまではここで止まる。
        if (fly && !EnsureMounted(true))
            return false;

        // 戻り値が false のときは、前の経路探索がまだ終わっていない。
        // 何も始まっていないので、次のフレームで呼び直せばよい。
        return range > 0f
            ? VNavmesh.PathfindAndMoveCloseTo(destination, fly, range)
            : VNavmesh.PathfindAndMoveTo(destination, fly);
    }

    /// <summary>
    /// 目的地に着いたか。
    ///
    /// vnavmesh の「移動中か」だけで判断してはいけない。
    /// 経路探索が始まる前も「移動中でない」ため、呼んだ直後に
    /// 「もう着いた」と誤解してしまう。
    ///
    /// そこで3つそろって初めて到着とみなす。
    ///   1. 経路に沿って移動していない
    ///   2. 経路探索もしていない
    ///   3. 実際に目的地の近くにいる
    ///
    /// 3つ目があることで、途中で止まってしまった場合を「到着」と取り違えずに済む。
    /// </summary>
    internal static bool ArrivalCheck(Vector3 destination, float range)
    {
        if (!PlayerHelper.IsValid)
            return false;

        if (VNavmesh.PathIsRunning || VNavmesh.PathfindInProgress)
            return false;

        return ObjectHelper.DistanceToPlayer(destination) <= Math.Max(range, 0.5f);
    }

    /// <summary>移動を止める。</summary>
    internal static void Stop() => VNavmesh.PathStop();

    private static void UseGeneralAction(uint actionId)
    {
        try
        {
            var am = ActionManager.Instance();
            if (am == null)
                return;

            am->UseAction(ActionType.GeneralAction, actionId);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"ジェネラルアクション {actionId} を実行できませんでした。");
        }
    }

    /// <summary>
    /// 騎乗・離陸・降車の待ち時間をなかったことにする。
    /// エリアが変わったときなど、状況が大きく変わったときだけ呼ぶ。
    /// </summary>
    internal static void ResetMountCooldown()
    {
        _nextMount = DateTime.MinValue;
        _nextTakeoff = DateTime.MinValue;
        _groundedSince = DateTime.MinValue;
        _nextDismount = DateTime.MinValue;
    }
}
