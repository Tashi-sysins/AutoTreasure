using Dalamud.Configuration;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;

namespace AutoTreasure;

/// <summary>
/// このクライアントの役割。
///
/// 3台以上で動かすとき、1台だけを「リーダー」にする。
/// リーダーだけが行うのは次の2つ。
///   ・古ぼけた地図を解読し、宝の座標を他の機へ配る
///   ・魔紋の中で、扉にアクセスしてムービーを進める
///     （全機がアクセスすると多重に発火する恐れがあるため1台に限る）
///
/// ロットの扱いも役割で分かれる。リーダーは Need、メンバーは Pass。
/// </summary>
public enum ClientRole
{
    /// <summary>1台だけで動かす。座標の配信も受信もしない。</summary>
    Solo,

    /// <summary>親機。地図を解読し、座標を配り、扉を開ける。ロットは Need。</summary>
    Leader,

    /// <summary>子機。座標を受け取って自分で向かう。ロットは Pass。</summary>
    Member,
}

/// <summary>
/// 連携の経路。
///
/// どちらも「やり取りする中身」は同じ（SyncMessage の文字列）。
/// 違うのは運び方だけなので、片方で動けばもう片方でも動く。
/// </summary>
public enum SyncTransportKind
{
    /// <summary>このPCだけ。名前付きパイプ。速いが同じPCの中でしか通じない。</summary>
    LocalPipe,

    /// <summary>インターネット。中継サーバー経由。別々の家からでも繋がる。</summary>
    Internet,
}

/// <summary>
/// ロットで何を選ぶか。
/// </summary>
public enum RollChoice
{
    /// <summary>Need（必要）。</summary>
    Need,

    /// <summary>Greed（欲しい）。</summary>
    Greed,

    /// <summary>Pass（見送る）。</summary>
    Pass,
}

/// <summary>
/// 「古ぼけた地図S5」を誰が使うか、の決め方。
///
/// <b>地図は誰でも使える。</b>
/// これまでは分かりやすさのためリーダーだけが使う作りにしていたが、
/// ゲームの仕組み上、パーティの誰が使っても構わない。
/// 使った人が、その周回の「地図役」になる。
/// </summary>
public enum MapTurnMode
{
    /// <summary>決めた1人だけが使う。その人の地図が尽きたら止まる。</summary>
    FixedCharacter,

    /// <summary>並べた順に、1周ごとに次の人へ回す。</summary>
    RoundRobin,

    /// <summary>並べた順に、決めた枚数を使ってから次の人へ回す。</summary>
    AfterCount,
}

/// <summary>
/// 地図を使う順番の1枠。
///
/// キャラクター名と、その人が続けて使う枚数を持つ。
/// 枚数は <see cref="MapTurnMode.AfterCount"/> のときだけ使う。
/// </summary>
public sealed class MapTurnSlot
{
    /// <summary>キャラクター名。空なら未設定。</summary>
    public string Name { get; set; } = "";

    /// <summary>続けて使う枚数。1以上。</summary>
    public int Count { get; set; } = 1;
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>
    /// 地図を使う順番の決め方。
    ///
    /// リーダーだけが設定できる。メンバーは見るだけ。
    /// 設定はリーダーから全機に配られる。
    /// </summary>
    public MapTurnMode MapTurn { get; set; } = MapTurnMode.FixedCharacter;

    /// <summary>
    /// 地図を使う順番。
    ///
    /// <see cref="MapTurnMode.FixedCharacter"/> のときは先頭の1枠だけを使う。
    /// 空のときは、開始を押した人が使う。
    ///
    /// <b>名前は保存するが、実行時は今のパーティと突き合わせる。</b>
    /// パーティにいない人の枠は飛ばす。抜けた人で止まらないようにするため
    /// （再び合流すれば、また順番に入る）。
    /// </summary>
    public List<MapTurnSlot> MapTurnOrder { get; set; } = [];

    /// <summary>
    /// これまでに終えた周回の数。順番のどこまで進んだかを表す。
    ///
    /// <b>保存する必要がある。</b>
    /// 覚えておかないと、プラグインを更新するたびに 0 に戻り、
    /// 順番が常に①番目からやり直しになる。
    ///
    /// 実測（2026-09-19 11:17）:
    ///   ①Aさん → ②Bさん の順に設定してあったのに、
    ///   魔紋の中で更新して開始したら、また①が地図役に選ばれた。
    ///   本来は②の番だったため、地図を使っていない機が
    ///   宝箱を開けようとして失敗した。
    /// </summary>
    public int MapLapsDone { get; set; }

    /// <summary>
    /// 役割をパーティの状態から自動で決めるか。
    ///
    /// パーティリーダーは常に1人なので、ゲームに聞けば確実に分かる。
    /// 3台それぞれで手で設定すると、間違えたまま動かす事故が起きる。
    /// 既定では自動にしておく。
    /// </summary>
    public bool AutoDetectRole { get; set; } = true;

    /// <summary>
    /// このクライアントの役割。
    ///
    /// 自動判定が有効なときは、パーティの状態に合わせて勝手に変わる。
    /// 手で決めたいときは <see cref="AutoDetectRole"/> を切る。
    /// </summary>
    public ClientRole Role { get; set; } = ClientRole.Solo;

    /// <summary>
    /// 自動でロットするか。
    /// </summary>
    public bool AutoRoll { get; set; } = true;

    /// <summary>
    /// ロットで何を選ぶか。
    ///
    /// 以前はリーダーが Need、メンバーが Pass と決め打ちだった。
    /// 全員で分け合う必要がなくなったので、機ごとに選べるようにした。
    /// </summary>
    public RollChoice RollOption { get; set; } = RollChoice.Need;

    /// <summary>
    /// 周回を始めるとき、戦闘プラグインの設定も合わせるか。
    ///
    /// 手で切り替えると、3台のうち1台だけ設定し忘れる事故が起きる。
    /// </summary>
    public bool ManageCombatPlugins { get; set; } = true;

    /// <summary>
    /// 地形づくりに使うコア数を上げるか。
    ///
    /// <b>画面には出さない。常に有効でよい。</b>
    /// 初期値の 1 では、地形づくりに1エリアあたり1分以上かかる。
    /// 実測（2026-09-18）では 5 コアで約22秒だった。
    /// 切る理由が無いので、設定として見せる意味がない。
    /// </summary>
    public bool BoostNavmeshCores { get; set; } = true;

    /// <summary>
    /// 地形づくりに使うコア数（1台あたり）。
    ///
    /// <b>画面には出さない。</b>
    /// 24 コアの環境で4台動かす場合、5 なら合計 20 で余裕が残る。
    /// 台数や環境を変えるときだけ、設定ファイルを直に書き換える。
    /// </summary>
    public int NavmeshCores { get; set; } = 5;

    /// <summary>
    /// 周回中に使う BMR のプリセット名。
    ///
    /// 無ければプラグインが作る（<see cref="IPC.CombatPlugins.EnsureRunPreset"/>）。
    /// AutoDuty のプリセットには頼らない。
    /// </summary>
    public string BmrPreset { get; set; } = "Treasure Field";

    /// <summary>
    /// 戦えなくなったときに切り替える、最後の手段のプリセット。
    ///
    /// 「AutoDuty」は周りの敵を片端から狙いに行く。
    /// ふだんは地図の敵だけ相手にしたいので使わないが、
    /// まったく攻撃しない状態から抜け出すには確実。
    /// </summary>
    public string BmrFallbackPreset { get; set; } = "AutoDuty";

    /// <summary>複数クライアントの連携に使う名前付きパイプの名前。全機で同じにする。</summary>
    public string PipeName { get; set; } = "AutoTreasurePipe";

    /// <summary>
    /// 連携の経路。
    ///
    /// <b>既定はインターネット。</b>
    /// 別々の家・別々の回線からでも足並みを揃えられる。
    ///
    /// 同じPCで複数クライアントを動かす場合は
    /// <see cref="SyncTransportKind.LocalPipe"/> のほうが速く、
    /// 中継サーバーに無駄な負荷もかからない。
    ///
    /// ⚠ <see cref="RelayUrl"/> が空のときは、既定でもパイプで動く。
    ///   URL を知らない人が更新しただけで連携できなくなるのを防ぐため。
    /// </summary>
    public SyncTransportKind SyncTransport { get; set; } = SyncTransportKind.Internet;

    /// <summary>
    /// 中継サーバーの URL（wss://...）。
    ///
    /// <b>既定で入れてある。</b> 利用者に入力させない。
    /// MogColle と同じで、「インターネット」を選んだら
    /// そのまま繋がるのが当たり前の振る舞い。
    ///
    /// 別のサーバーを使いたいときだけ、ここを書き換える。
    /// 空にすると「このPCだけ」で動く。
    /// </summary>
    public string RelayUrl { get; set; } = DefaultRelayUrl;

    /// <summary>
    /// 既定の中継サーバー。
    ///
    /// ⚠ 設定ファイルには保存された値が残るので、ここを変えても
    ///   既に使っている人には届かない。移すときは Migrate で読み替える。
    /// </summary>
    public const string DefaultRelayUrl = "wss://estelldprereleaserepo.net/treasure/ws";

    /// <summary>
    /// 参加するときの招待（ATR1:...）。
    ///
    /// <b>ふつうは空のままでよい。</b>
    /// 同じパーティーなら、リーダーが預けた招待を
    /// サーバーから自動で受け取る（PartyKey が合言葉になる）。
    ///
    /// パーティーを組まずに繋ぐときだけ、手で貼り付ける。
    /// </summary>
    public string RelayInviteCode { get; set; } = "";

    /// <summary>
    /// 連携できる最大の台数（自分を除く）。
    ///
    /// パーティは最大8人なので、リーダーから見た仲間は最大7人。
    /// </summary>
    public const int MaxMembers = 7;

    /// <summary>
    /// 待つ仲間の数を、パーティの人数から自動で決めるか。
    ///
    /// 手で数を設定させると、4人パーティなのに3台のままだった、
    /// といった取り違えが起きる。ゲームに聞けば間違えようがない。
    /// </summary>
    public bool AutoExpectedMembers { get; set; } = true;

    /// <summary>
    /// リーダーが待つ仲間の数（自分を除く）。
    ///
    /// <see cref="AutoExpectedMembers"/> が有効なときは使わない。
    /// パーティを組まずに複数台を動かすときなど、手で決めたいときの逃げ道。
    ///
    /// 「今つながっている数」で判断してはいけない。まだ接続できていない機が
    /// あると、少ない人数のまま先へ進んでしまう。
    /// </summary>
    public int ExpectedMembers { get; set; } = 2;

    /// <summary>
    /// ログインしたら、記録を自動で始めるか。
    ///
    /// 手で遊びながら様子を残したいときに使う。
    /// 毎回ボタンを押すのは手間なので、一度ONにすれば以後は勝手に始まる。
    /// </summary>
    public bool AutoStartLog { get; set; } = false;

    /// <summary>
    /// 1周終わったら、続けて次の周回を始めるか。
    ///
    /// 切ると、1周ごとに手で開始を押すことになる。
    /// 地図が尽きたときは、この設定に関わらず止まる。
    /// </summary>
    public bool ContinuousRuns { get; set; } = true;

    /// <summary>
    /// 1周終わってから、次を始めるまでの秒数。
    ///
    /// すぐ始めると、戦利品の受け取りや魔紋から出た直後の処理と重なる。
    /// 実測では脱出から落ち着くまで数秒かかるので、少し余裕を見る。
    /// </summary>
    public float ContinuousRunDelaySeconds { get; set; } = 10f;

    /// <summary>
    /// 強欲の罠で、解除に挑むか。
    ///
    /// <b>常に false。</b>
    /// 札の数字が読めないため、挑むと期待値が下がる。
    ///   挑む   … 成功率 55.6% × 15,000 ＝ 8,333
    ///   降りる … 10,000 が確定
    /// 読めるようになったら、また選べるようにする。
    /// </summary>
    public bool ChallengeGreedTrap { get; set; } = false;

    /// <summary>
    /// 強欲の罠で、札の絵を集めるために止まるか。
    ///
    /// 1〜9 の札がどの絵かを突き止めるための調べもの用。
    /// 入れると、罠が出たら必ず「解除に挑む」を選び、
    /// 札が開いて HIGH／LOW を選ぶ画面になったところで止まる。
    /// そこで画面を撮り、記録に残った絵の番号と突き合わせる。
    ///
    /// 対応表がそろったら切る。切り忘れると周回が進まない。
    /// </summary>
    public bool GreedTrapStudyMode { get; set; } = false;

    /// <summary>
    /// 最下層（第5区画）で止まるか。
    ///
    /// <b>既定は切。</b>
    /// 最下層の様子は調べ終わったので、ふだん止める必要はない。
    /// 入れたままだと、周回のたびに最下層で止まって先へ進まない。
    ///
    /// 入れておくと、最下層で「宝箱 → 戦闘 → 宝箱」まで進んだところで
    /// 止まり、そのときの周囲の様子を記録に残す。
    /// また調べたくなったときだけ入れる。
    /// </summary>
    public bool StopAtFinalRoom { get; set; } = false;

    /// <summary>
    /// 強欲の罠のとき、画面の中身をファイルに書き出すか。
    ///
    /// 数字は絵で描かれていて、文字としては出てこない。
    /// そこで、画面に渡された値（AtkValues）や
    /// 画面を作る元のデータ（ArrayData）まで丸ごと書き出し、
    /// どこに数字があるのかを後から探せるようにする。
    ///
    /// 罠1回につき2つのファイルができる（札が伏せられた状態と、開いた状態）。
    /// 見比べれば「開いたときに何が変わったか」が分かる。
    ///
    /// 数字の在処が分かったら切ってよい。
    /// </summary>
    public bool DumpGreedTrapUi { get; set; } = false;

    /// <summary>
    /// 調べもの用の設定を画面に出すか。
    ///
    /// ふだんは隠しておく。初めて使う人や、調べものをしない人には要らない。
    /// 開くと、記録を取るためのチェックが並ぶ。
    ///
    /// <b>開いたかどうかは覚えておく。</b>
    /// 調べている最中にプラグインが更新されるたび閉じられると、
    /// そのつど開き直すことになって煩わしい。
    /// </summary>
    public bool ShowDebugSettings { get; set; } = false;

    /// <summary>
    /// 役割の説明文を画面に出すか。
    ///
    /// 「場所を受け取って自分で向かいます。扉は開けません。」といった説明。
    /// 一度読めば分かる内容で、毎回見る必要がない。
    /// 役割そのものは、その上の1行で分かる。
    ///
    /// 既定は切。調べもののときだけ出せるようにしてある。
    /// </summary>
    public bool ShowRoleDescription { get; set; } = false;

    /// <summary>
    /// 画面右上（サーバー情報バー）に状態を出すか。
    ///
    /// メンバーは操作画面を開かなくても動いてしまうので、
    /// ロットの設定を確かめる機会が無い。
    /// ここなら開かなくても常に見える。
    /// </summary>
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>
    /// LazyLoot の自動ロットを止めたまま終わったか。
    ///
    /// 止めるときに記録し、戻したら消す。
    /// 次の起動時にこれが残っていれば、前回は戻し損ねている
    /// （落ちた・強制終了した等）。起動時に戻す。
    ///
    /// <b>利用者の設定を借りている以上、返し忘れは許されない。</b>
    /// 「LazyLoot が壊れた」と思われてしまう。
    /// </summary>
    public bool LazyLootSuppressed { get; set; }

    /// <summary>止める前の値。戻すときに使う。</summary>
    public bool LazyLootOriginal { get; set; }

    public static Configuration Load()
    {
        try
        {
            var cfg = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

            cfg.Migrate();
            return cfg;
        }
        catch (Exception ex)
        {
            // 設定が壊れていても起動は止めない。既定値で立ち上げて、ログに残す。
            Svc.Log.Warning(ex, "設定を読み込めませんでした。既定の設定で起動します。");
            return new Configuration();
        }
    }

    /// <summary>
    /// 設定を保存する。
    ///
    /// <b>失敗しても例外を投げない。</b>
    ///
    /// 設定ファイルは4台で1つを共有している。
    /// リーダーが地図の順番を配ると、メンバー3台が同じ瞬間に書き込もうとし、
    /// Dalamud の保存先（SQLite）が「database is locked」で弾く。
    ///
    /// 実測（2026-09-19 15:18:59）:
    ///   例外がそのまま上がり、毎フレームの処理で捕まえられて
    ///   「問題が起きました。いったん停止します」となり<b>周回が止まった</b>。
    ///
    /// 設定の保存は、失敗しても周回を止めるほどのことではない。
    /// 次に保存する機会で書ければよい。
    /// 記録には残すが、呼んだ側へは投げ返さない。
    /// </summary>
    public void Save()
    {
        try
        {
            Svc.PluginInterface.SavePluginConfig(this);
        }
        catch (Exception ex)
        {
            // 同じ内容を何度も出さない。毎フレーム呼ばれる場所があるため。
            if (!_saveWarned)
            {
                _saveWarned = true;
                Svc.Log.Warning(ex,
                    "[AutoTreasure] 設定を保存できませんでした。"
                    + "ほかのクライアントが同時に書き込んでいる可能性があります。"
                    + "周回はそのまま続けます。");
            }
        }
    }

    /// <summary>保存の失敗を一度だけ記録するための目印。</summary>
    private static bool _saveWarned;

    /// <summary>
    /// 古い設定を今の形に直す。
    ///
    /// 設定は保存されているので、既定値を変えても既に使っている人には届かない。
    /// 名前が変わったものは、ここで読み替える。
    /// </summary>
    private void Migrate()
    {
        var changed = false;

        // 古い設定ファイルには、地図の順番の項目が無い。
        //
        // 初期値（= []）を書いてあっても、読み込みで null が入ることがある。
        // 項目が無いファイルを読むと、初期化子の結果を上書きして null にする
        // 読み手があるため。null のまま使うと、画面を開いた瞬間に落ちる。
        if (MapTurnOrder == null)
        {
            MapTurnOrder = [];
            changed = true;
        }

        // 中身に null が混ざっている場合も取り除く。
        // 手で設定ファイルを編集したときなどに起こりうる。
        if (MapTurnOrder.RemoveAll(s => s == null) > 0)
            changed = true;

        // BMR のプリセットを差し替えた（2026-09-18）。
        //
        // 「AutoDuty Passive LB」は AutoDuty が配っているものではなく、
        // 手で作られたものだった。他の環境では存在しないので、
        // 自前で作る「Treasure Passive」に移す。
        // 「Treasure Passive」は狙いを横取りされる設定だった（2026-09-19）。
        //
        // AutoTarget の Retarget が Always になっていたため、
        // こちらが狙った相手を BMR が毎フレーム付け替えてしまい、
        // 最下層で攻撃が始まらなかった。
        // 中身を直したものを別名で作り直すので、そちらへ移す。
        if (BmrPreset is "AutoDuty Passive LB" or "AutoDuty Passive" or "Treasure Passive" or "Treasure Passive 2" or "Treasure Passive 3")
        {
            BmrPreset = IPC.CombatPlugins.RunPresetName;
            changed = true;
        }

        // 中継サーバーのURLが空なら、既定を入れる。
        //
        // <b>既定値を変えても、保存済みの設定には届かない。</b>
        // 0.2.0.0 の最初の版は空を既定にしていたので、
        // そのとき起動した人の設定ファイルには "" が残っている。
        // 空のままだと「インターネット」を選んでも繋がらない。
        //
        // 自分で消して「このPCだけ」にしている人は、
        // そもそも繋ぎ方が LocalPipe になっているので上書きしない。
        if (string.IsNullOrWhiteSpace(RelayUrl) && SyncTransport == SyncTransportKind.Internet)
        {
            RelayUrl = DefaultRelayUrl;
            changed = true;
        }

        if (changed)
        {
            Svc.Log.Information(
                $"[AutoTreasure] 設定を今の形に直しました（BMR のプリセット: {BmrPreset}）。");
            Save();
        }
    }
}
