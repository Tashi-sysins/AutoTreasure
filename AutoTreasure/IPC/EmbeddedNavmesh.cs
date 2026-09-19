using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using Navmesh;
using Navmesh.Movement;
using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

namespace AutoTreasure.IPC;

/// <summary>
/// 取り込んだ vnavmesh を動かす。
///
/// これまでは外部プラグインの vnavmesh に IPC で頼っていた。
/// それをやめて、経路探索の部分をこのプラグインの中に入れた。
///
/// 中に入れた理由は2つ。
///
///   1. 地形の作り直しに不具合があり、直した版が要る。
///      魔紋は階層が変わってもエリア番号が 1209 のまま変わらないので、
///      公式版は「エリアが変わっていない＝作り直す必要が無い」と判断し、
///      前の階層の地形を持ち続ける。その地形には今いない場所の通路が
///      載っているため、戻れない道へ向かって走り続けることになる。
///
///   2. IPC 越しでは中の様子が分からない。
///      「準備できている」としか答えが返らず、それが今の階層のものか
///      前の階層のものかを見分けられなかった。
///      中に入れれば、地形がいつ作り直されたかを直接見られる。
///
/// <b>外部プラグインの vnavmesh とは同時に使えない。</b>
/// どちらも同じゲーム内の移動を操るので、両方が指示を出すと奪い合う。
/// こちらを使うときは、外部の vnavmesh を無効にしてもらう。
/// </summary>
internal sealed class EmbeddedNavmesh : IDisposable
{
    private NavmeshManager? _manager;
    private FollowPath? _follow;
    private AsyncMoveRequest? _move;

    private bool _ready;

    /// <summary>今つながっている入れ物。無ければ null。</summary>
    internal static EmbeddedNavmesh? Instance { get; private set; }

    /// <summary>使える状態か。</summary>
    internal bool IsAvailable => _ready && _manager != null;

    /// <summary>
    /// 立ち上げる。
    ///
    /// vnavmesh 本体は、自分が Dalamud プラグインである前提で書かれている。
    /// ここでは同じ手順を、このプラグインの中で踏む。
    /// </summary>
    internal EmbeddedNavmesh(IDalamudPluginInterface dalamud)
    {
        try
        {
            if (!dalamud.ConfigDirectory.Exists)
                dalamud.ConfigDirectory.Create();

            // vnavmesh 側の Dalamud サービスを埋める。
            // 本体は Service.Log などを直接使うので、ここを通さないと落ちる。
            dalamud.Create<Service>();

            var configFile = new FileInfo(
                Path.Combine(dalamud.ConfigDirectory.FullName, "AutoTreasure.navmesh.json"));

            Service.Config.Load(configFile);

            // 設定が変わったら保存する、という繋ぎ込み。
            //
            // <b>外せるように、変数に入れて持っておく。</b>
            // Service.Config は static なので、この繋ぎ込みは
            // プラグインを外しても残る。
            // 入れ直すたびに1本ずつ増えていき、
            // 古い（もう無いアセンブリの）処理が呼ばれるようになる。
            _saveConfig = () => Service.Config.Save(configFile);
            Service.Config.Modified += _saveConfig;

            // 地形の置き場。外部の vnavmesh と分けておく。
            //
            // 同じ場所を使うと、向こうが作った地形をこちらが読み、
            // こちらが作った地形を向こうが読むことになる。
            // 別々にしておけば、片方を消しても もう片方は無事。
            var cacheDir = Path.Combine(dalamud.ConfigDirectory.FullName, "meshcache");

            _manager = new NavmeshManager(new DirectoryInfo(cacheDir));
            _follow = new FollowPath(dalamud, _manager);
            _move = new AsyncMoveRequest(_manager, _follow);

            Svc.Framework.Update += OnUpdate;

            _ready = true;
            Instance = this;

            Svc.Log.Information(
                $"[AutoTreasure] 取り込んだ地形づくりを始めました（置き場: {cacheDir}）");
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[AutoTreasure] 取り込んだ地形づくりを始められませんでした。");
            _ready = false;
        }
    }

    /// <summary>
    /// 毎フレーム進める。
    ///
    /// vnavmesh は「毎フレーム呼ばれること」を前提に書かれている。
    /// ここを呼ばないと、地形づくりも移動も進まない。
    /// </summary>
    private void OnUpdate(IFramework framework)
    {
        if (!_ready)
            return;

        try
        {
            _manager?.Update();
            _follow?.Update(framework);
            _move?.Update();
        }
        catch (Exception ex)
        {
            // ここで落とすと、毎フレーム例外が飛び続けてゲームが重くなる。
            // 一度記録して、以後は動かさない。
            Svc.Log.Error(ex, "[AutoTreasure] 地形づくりの更新で問題が起きました。止めます。");
            _ready = false;
        }
    }

    // ---- 外から使う口 -------------------------------------------------------

    /// <summary>地形ができているか。</summary>
    internal bool NavIsReady => _manager?.Navmesh != null;

    /// <summary>地形づくりの進み具合（0〜1）。分からなければ -1。</summary>
    internal float BuildProgress => _manager?.LoadTaskProgress ?? -1f;

    /// <summary>経路をたどっている最中か。</summary>
    internal bool PathIsRunning => (_follow?.Waypoints.Count ?? 0) > 0;

    /// <summary>経路を探している最中か。</summary>
    internal bool PathfindInProgress => _move?.TaskInProgress ?? false;

    /// <summary>残りの経由点の数。</summary>
    internal int NumWaypoints => _follow?.Waypoints.Count ?? 0;

    /// <summary>
    /// 移動をやめる。
    ///
    /// <b>計算中の経路も無効にする。</b>
    /// 経由点を消すだけでは足りない。止めた時点で走っていた経路探索は
    /// そのまま続き、終わると結果がそのまま移動に渡される。
    /// つまり「止めたはずなのに、少し後で勝手に歩き出す」ことが起きる。
    ///
    ///   前の区画への経路を計算し始める
    ///     → 区画移動に気づいて止める
    ///     → 前の計算が終わる
    ///     → その結果が使われ、前の区画へ戻り始める
    ///
    /// 取り消しだけでは防げない。取り消す直前に計算が終わっていれば、
    /// 結果は届いてしまう。そこで「いつの要求か」を番号で持たせ、
    /// <b>結果を使う直前に</b>古い番号なら捨てる。
    /// </summary>
    internal void Stop()
    {
        _move?.InvalidatePending();
        _follow?.Stop();
    }

    /// <summary>
    /// 地形を作り直す。
    ///
    /// <b>魔紋ではこれが要る。</b>
    /// 階層が変わってもエリア番号が同じなので、頼まないと作り直されない。
    /// </summary>
    internal void Reload() => _manager?.Reload(true);

    /// <summary>その座標へ向かう。</summary>
    internal bool PathfindAndMoveTo(Vector3 destination, bool fly)
    {
        if (_move == null)
            return false;

        return _move.MoveTo(destination, fly);
    }

    /// <summary>
    /// その座標の手前 range ヤードまで近づく。
    ///
    /// 宝箱や扉のように「そこまで行けば用が足りる」相手に使う。
    /// </summary>
    internal bool PathfindAndMoveCloseTo(Vector3 destination, bool fly, float range)
    {
        if (_move == null)
            return false;

        return _move.MoveTo(destination, fly, range);
    }

    /// <summary>床の上の座標に寄せる。地形の外を指すと移動が静かに失敗するため。</summary>
    internal Vector3? PointOnFloor(Vector3 point, bool allowUnlandable, float halfExtentXZ)
        => _manager?.Query?.FindPointOnFloor(point, halfExtentXZ, allowUnlandable);

    /// <summary>その座標が地形の上にあるか（＝たどり着けるか）。</summary>
    internal bool IsPointOnMesh(Vector3 point, float halfExtentY, bool allowUnreachable)
        => _manager?.Query?.IsPointOnMesh(point, halfExtentY, allowUnreachable) ?? false;

    /// <summary>地図の旗の座標。無ければ null。</summary>
    internal Vector3? FlagToPoint()
    {
        var query = _manager?.Query;
        if (query == null)
            return null;

        var flag = Navmesh.MapUtils.FlagToPoint(query);
        return flag;
    }

    /// <summary>
    /// 設定の保存処理。外すときに使うので覚えておく。
    /// <see cref="Navmesh.Service.Config"/> は static なので、
    /// 外さないとプラグインを入れ直すたびに積み上がる。
    /// </summary>
    private Action? _saveConfig;

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;

        // 静的な繋ぎ込みを外す。
        // ここを忘れると、入れ直すたびに保存処理が増えていく。
        if (_saveConfig != null)
        {
            try { Navmesh.Service.Config.Modified -= _saveConfig; }
            catch { /* 外せなくても、これ以上できることはない */ }
            _saveConfig = null;
        }

        // 探索の途中なら、終わるまで待って片付ける。
        //
        // ここを省くと、経路を探している最中にプラグインを外したとき、
        // その処理が「もう捨てた地形」を掴んだまま裏で走り続ける。
        _move?.Dispose();
        _move = null;

        _follow?.Dispose();
        _follow = null;
        _manager?.Dispose();
        _manager = null;

        _ready = false;

        if (Instance == this)
            Instance = null;
    }
}
