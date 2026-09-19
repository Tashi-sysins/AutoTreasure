using ECommons.DalamudServices;
using ECommons.Reflection;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoTreasure.IPC;

/// <summary>
/// vnavmesh への入口。
///
/// 移動はすべてこのプラグインに任せる。自前で座標を書き換えることはしない。
///
/// 呼び出しは必ず try/catch で包む。vnavmesh が入っていない・読み込み前・
/// エリア切り替え中などに例外が飛ぶことがあり、そのたびに落ちては困るため。
/// 失敗したときは「今は使えない」を意味する値（false や null）を返す。
/// </summary>
internal static class VNavmesh
{
    private const string Name = "vnavmesh";

    /// <summary>
    /// 取り込んだ経路探索。動いていればこちらを使う。
    ///
    /// 外部プラグインの vnavmesh への呼び出しは、
    /// 取り込みが動かなかったときの逃げ道として残してある。
    /// </summary>
    private static EmbeddedNavmesh? Embedded => EmbeddedNavmesh.Instance?.IsAvailable == true
        ? EmbeddedNavmesh.Instance
        : null;

    /// <summary>取り込んだ経路探索を使っているか。画面に出して確かめる。</summary>
    internal static bool UsingEmbedded => Embedded != null;

    /// <summary>vnavmesh が導入され、読み込まれているか。</summary>
    internal static bool IsEnabled
        => Embedded != null || DalamudReflector.TryGetDalamudPlugin(Name, out _, false, true);

    // ---- ナビメッシュの状態 -------------------------------------------------

    /// <summary>
    /// 現在のエリアのナビメッシュが使える状態か。
    /// エリア移動の直後は false。生成が終わるまで移動指示を出しても無駄になる。
    /// </summary>
    internal static bool NavIsReady
        => Embedded?.NavIsReady ?? Get<bool>("Nav.IsReady");

    /// <summary>ナビメッシュ生成の進み具合（0〜1）。生成中でなければ -1。</summary>
    internal static float NavBuildProgress
        => Embedded?.BuildProgress ?? Get<float>("Nav.BuildProgress");

    /// <summary>
    /// エリアが変わったときに、地形を自動で作る設定か。
    ///
    /// これが切られていると、いつまでも地形が作られない。
    /// 読めなければ null。
    /// </summary>
    internal static bool? IsAutoLoad
    {
        get
        {
            try { return Svc.PluginInterface.GetIpcSubscriber<bool>($"{Name}.Nav.IsAutoLoad").InvokeFunc(); }
            catch { return null; }
        }
    }

    /// <summary>自動で作る設定を変える。</summary>
    internal static void SetAutoLoad(bool value)
    {
        // 取り込み版では、こちらから作り直しを頼む作りにしてある。
        // 自動読み込みの設定は使わない。
        if (Embedded != null)
            return;

        Invoke("Nav.SetAutoLoad", value);
    }

    /// <summary>
    /// 地形を作り直す。
    ///
    /// 作られていない・作り始めてもいないときに頼む。
    /// 出来合いのものがあればそれを使うので、毎回1から作るわけではない。
    /// </summary>
    internal static void Reload()
    {
        if (Embedded != null)
        {
            Embedded.Reload();
            return;
        }

        // 外部の vnavmesh を使っているときの道。
        //
        // <b>Nav.Reload は「bool を1つ受け取る」呼び出し。</b>
        // 以前は Get<bool>（引数なしで bool が返る）として呼んでいたため、
        // 形が合わず、呼ぶたびに必ず失敗していた。
        // 失敗は握りつぶしていたので気づきにくいが、
        // 外部版に頼った環境では地形の作り直しが一度も通らず、
        // 「地形ができないまま棒立ち」になる。
        //
        // 引数は「出来合いのものがあれば使ってよいか」。
        // 作り直しの狙いは読み込ませることなので true でよい。
        Invoke("Nav.Reload", true);
    }

    // ---- 経路探索と移動 -----------------------------------------------------

    /// <summary>
    /// 目的地まで経路を引いて移動を始める。
    ///
    /// fly を true にすると飛行経路を引くが、<b>騎乗していないと無言で止まる</b>。
    /// vnavmesh は「騎乗中なら自分でジャンプして離陸する」が、
    /// 「マウントを呼び出す」ことはしない。騎乗は呼び出し側の責任。
    ///
    /// 戻り値が false のときは、前の経路探索がまだ終わっていないので何も始まっていない。
    /// 少し待って呼び直すこと。
    /// </summary>
    internal static bool PathfindAndMoveTo(Vector3 destination, bool fly)
        => Embedded != null
            ? Embedded.PathfindAndMoveTo(destination, fly)
            : Get<Vector3, bool, bool>("SimpleMove.PathfindAndMoveTo", destination, fly);

    /// <summary>
    /// 目的地の手前 range ヤードまで近づく。
    /// 宝箱や扉のように「そこまで行けば用が足りる」相手にはこちらを使う。
    /// 戻り値の意味は <see cref="PathfindAndMoveTo"/> と同じ。
    /// </summary>
    internal static bool PathfindAndMoveCloseTo(Vector3 destination, bool fly, float range)
        => Embedded != null
            ? Embedded.PathfindAndMoveCloseTo(destination, fly, range)
            : Get<Vector3, bool, float, bool>("SimpleMove.PathfindAndMoveCloseTo", destination, fly, range);

    /// <summary>
    /// 経路探索が進行中か。
    ///
    /// 到着judgeに使うのはこちら。名前のよく似た "Nav.PathfindInProgress" は別物で、
    /// そちらを見ると「呼んだ直後に一瞬到着したと誤判定する」問題を避けられない。
    /// </summary>
    internal static bool PathfindInProgress
        => Embedded?.PathfindInProgress ?? Get<bool>("SimpleMove.PathfindInProgress");

    /// <summary>
    /// 経路に沿って移動中か（＝残りの経由地があるか）。
    ///
    /// これだけで到着を判定してはいけない。経路探索が始まる前も false になるため、
    /// 「呼んだ直後 ＝ 到着」と誤解する。<see cref="ArrivalCheck"/> を使うこと。
    /// </summary>
    internal static bool PathIsRunning
        => Embedded?.PathIsRunning ?? Get<bool>("Path.IsRunning");

    /// <summary>残りの経由地の数。</summary>
    internal static int PathNumWaypoints
        => Embedded?.NumWaypoints ?? Get<int>("Path.NumWaypoints");

    /// <summary>移動を止める。</summary>
    internal static void PathStop()
    {
        if (Embedded != null)
        {
            Embedded.Stop();
            return;
        }

        Invoke("Path.Stop");
    }

    /// <summary>到着とみなす距離。</summary>
    internal static void PathSetTolerance(float tolerance)
    {
        // 取り込み版では、目的地ごとに range を渡す作りにしてある。
        if (Embedded != null)
            return;

        Invoke("Path.SetTolerance", tolerance);
    }

    // ---- 地形の問い合わせ ---------------------------------------------------

    /// <summary>
    /// その座標の真下にある地面を返す。
    /// 宝の座標は地表とずれていることがあるので、移動前にこれで落とし込むと安全。
    /// 見つからなければ null。
    /// </summary>
    internal static Vector3? PointOnFloor(Vector3 point, bool allowUnlandable, float halfExtentXZ)
        => Embedded != null
            ? Embedded.PointOnFloor(point, allowUnlandable, halfExtentXZ)
            : Get<Vector3, bool, float, Vector3?>("Query.Mesh.PointOnFloor", point, allowUnlandable, halfExtentXZ);

    /// <summary>その座標がナビメッシュ上にあるか（＝たどり着けるか）。</summary>
    internal static bool IsPointOnMesh(Vector3 point, float halfExtentY, bool allowUnreachable)
        => Embedded != null
            ? Embedded.IsPointOnMesh(point, halfExtentY, allowUnreachable)
            : Get<Vector3, float, bool, bool>("Query.Mesh.IsPointOnMesh", point, halfExtentY, allowUnreachable);

    /// <summary>
    /// ゲーム内のマップに立っているフラグを、3D座標に直して返す。
    ///
    /// ただし<b>フラグがどのエリアのものかは見ていない</b>。別エリアのフラグでも
    /// 「今いるエリアの同じ平面座標」を黙って返すため、エリアを自分で確かめてから使うこと。
    /// </summary>
    internal static Vector3? FlagToPoint()
        => Embedded != null
            ? Embedded.FlagToPoint()
            : Get<Vector3?>("Query.Mesh.FlagToPoint");

    // ---- 呼び出しの土台 -----------------------------------------------------

    private static void Invoke(string name)
    {
        try { Svc.PluginInterface.GetIpcSubscriber<object>($"{Name}.{name}").InvokeAction(); }
        catch (Exception ex) { Log(name, ex); }
    }

    private static void Invoke<T1>(string name, T1 a1)
    {
        try { Svc.PluginInterface.GetIpcSubscriber<T1, object>($"{Name}.{name}").InvokeAction(a1); }
        catch (Exception ex) { Log(name, ex); }
    }

    private static TRet? Get<TRet>(string name)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<TRet>($"{Name}.{name}").InvokeFunc(); }
        catch (Exception ex) { Log(name, ex); return default; }
    }

    private static TRet? Get<T1, TRet>(string name, T1 a1)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<T1, TRet>($"{Name}.{name}").InvokeFunc(a1); }
        catch (Exception ex) { Log(name, ex); return default; }
    }

    private static TRet? Get<T1, T2, TRet>(string name, T1 a1, T2 a2)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<T1, T2, TRet>($"{Name}.{name}").InvokeFunc(a1, a2); }
        catch (Exception ex) { Log(name, ex); return default; }
    }

    private static TRet? Get<T1, T2, T3, TRet>(string name, T1 a1, T2 a2, T3 a3)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<T1, T2, T3, TRet>($"{Name}.{name}").InvokeFunc(a1, a2, a3); }
        catch (Exception ex) { Log(name, ex); return default; }
    }

    // 例外は出るたびに記録すると毎フレーム流れて読めなくなるので、
    // 同じ呼び出しについては一度だけ残す。
    private static readonly HashSet<string> _logged = [];

    private static void Log(string name, Exception ex)
    {
        if (_logged.Add(name))
            Svc.Log.Warning(ex, $"vnavmesh.{name} の呼び出しに失敗しました。vnavmesh が無効か、まだ読み込まれていない可能性があります。");
    }
}
