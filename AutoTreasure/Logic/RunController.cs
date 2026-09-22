using AutoTreasure.Helpers;
using AutoTreasure.IPC;
using AutoTreasure.Sync;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoTreasure.Logic;

/// <summary>
/// 全体の進行を受け持つ。
///
/// 毎フレーム呼ばれて、「今どの段階か」に応じて次の一手を出す。
/// 重い判断はせず、その場で見えているものだけで決める。
/// 前のフレームの結果を覚えておいて判断すると、1フレーム古い情報で動くことになり、
/// 見落としが起きる。
/// </summary>
internal sealed class RunController : IDisposable
{
    private readonly CbtControl _cbt = new();
    private long _nextLootDiagnostic;
    private string? _lastLootDiagnostic;
    private string? _lastRelayDiagnostic;
    /// <summary>
    /// 仲間を待つ上限。
    ///
    /// 誰かが落ちたり、来られなくなったりしても、いつまでも止まらないようにする。
    /// 取り残されるより、先に進んだ方がまだ立て直しやすい。
    /// </summary>
    private const double WaitForPartyTimeoutSeconds = 120.0;

    /// <summary>
    /// 戦闘を待つ上限。
    ///
    /// 勝てない相手や、遠くで戦闘状態が続いている場合に、
    /// いつまでも待ち続けないようにする。
    /// </summary>
    private const double FightTimeoutSeconds = 300.0;

    /// <summary>
    /// 目的地までの移動を待つ上限。
    ///
    /// たどり着けない場所だと、詰まり判定にも引っかからないまま
    /// 遠回りを続けることがある。時間でも区切る。
    /// </summary>
    private const double TravelTimeoutSeconds = 300.0;

    /// <summary>
    /// 1つの宝箱を開けるのにかける上限。
    ///
    /// 近づききれていない、他の人が開けている最中——
    /// 触っても開かないまま時間が過ぎることがある。
    /// </summary>
    private const double ChestTimeoutSeconds = 90.0;

    /// <summary>
    /// 経路が引けないとき、まっすぐ歩いてよい距離。
    ///
    /// これより遠いと、障害物を無視して突っ込むことになる。
    /// 掘ったあとの宝箱は目の前にあるので、この程度で足りる。
    /// </summary>
    private const float DirectWalkRange = 25f;

    /// <summary>まっすぐ歩くまでに、経路で試す時間。</summary>
    private const double DirectWalkAfterSeconds = 8.0;

    /// <summary>ロットの処理にかける上限。</summary>
    private const double RollTimeoutSeconds = 60.0;

    /// <summary>
    /// 脱出ポータルを探す上限。
    /// これを過ぎても見つからなければ、こちらからは出ずに人に任せる。
    /// </summary>
    private const double ExitPortalSearchSeconds = 45.0;

    /// <summary>
    /// 扉を1回試すのにかける時間。
    ///
    /// 光っていない側の扉は選べない（2026-09 の仕様変更）。
    /// 選べない扉は叩いても何も起きないので、長く待つ意味がない。
    /// 早めに反対側へ切り替える。
    /// </summary>
    private const double DoorAttemptSeconds = 10.0;

    /// <summary>
    /// 仕掛けに触れられる距離。
    ///
    /// これより遠いと反応しないので、まず近づく。
    /// </summary>
    private const float InteractRange = 4f;

    /// <summary>テレポを待つ上限。これを過ぎたら手に任せる。</summary>
    private const double TeleportTimeoutSeconds = 45.0;

    /// <summary>転送魔紋に入るのを待つ上限。</summary>
    private const double PortalWaitSeconds = 30.0;

    /// <summary>
    /// 扉を通り過ぎて進む距離。
    ///
    /// 最初は実測（扉から搬送の起点まで 1〜7y）に合わせて 12y にしたが、
    /// 実機では届かなかった。扉のかなり手前で止まってから
    /// 歩き始めるため、扉からの距離とは別に余裕が要る。
    ///
    /// 実測 2026-09-17: 扉(-23.0, 354.1) から 11y 進んだところで止まり、
    /// ワープ床に届かなかった。
    ///
    /// 行き過ぎても、ワープ床を踏んだ時点で搬送が始まって止まるので害はない。
    /// 届かないより長い方がよい。
    /// </summary>
    private const float WalkPastDoorDistance = 35f;

    /// <summary>これだけ動いたら、搬送が始まったとみなす。</summary>
    private const float TransportDetectDistance = 20f;

    /// <summary>宝箱の前で仲間が集まるのを待つ時間。最下層では待たない。</summary>
    private const double ChestGatherSeconds = 1.0;

    /// <summary>
    /// 魔紋の中で、宝箱より討伐を先にする敵の範囲。
    ///
    /// 区画は狭いので、フィールドの 70y ほど広く見る必要はない。
    /// 広すぎると、隣の部屋の敵で手が止まる。
    /// </summary>
    private const float VaultEnemyRange = 30f;

    /// <summary>
    /// LazyLoot に任せて待つ時間。過ぎたら自分で押す。
    ///
    /// LazyLoot は既定で 1.5〜3.0 秒おいてから押す。
    /// その倍以上を見ておけば、正常に働いているときに
    /// 横から取り合うことはない。
    /// </summary>
    private const double LazyLootGraceSeconds = 8.0;

    /// <summary>扉の先へ歩くのにかける上限。</summary>
    private const double WalkPastDoorSeconds = 25.0;

    /// <summary>
    /// 敵を探す範囲。
    ///
    /// 場所ごとに違う値を使うと、ある処理は「まだ敵がいる」、
    /// 別の処理は「もういない」と判断してしまい、状態が行き来する。
    /// 1か所で決めておく。
    /// </summary>
    private const float EnemySearchRange = 70f;

    /// <summary>
    /// 敵が0体になってから、次の波を待つ時間。
    ///
    /// 魔紋の敵は何波かに分かれて湧く。一瞬0体になる時があるため、
    /// そこで終わりと決めつけない。
    /// </summary>
    private const double EnemySettleSeconds = 1.0;

    /// <summary>
    /// これだけの間ダメージが入らなければ、戦えていないとみなす。
    ///
    /// 短すぎると、詠唱の長い技や移動中に誤検知する。
    /// </summary>
    private const double CombatStallSeconds = 15.0;

    /// <summary>解読済みの地図から旗が立つのを待つ上限。</summary>
    private const double ShowMapTimeoutSeconds = 15.0;

    /// <summary>部屋の中央とみなす範囲。この中にいれば、それ以上動かさない。</summary>
    private const float RoomCenterRange = 8f;

    /// <summary>
    /// 扉を試す回数の上限。
    ///
    /// 何度やっても開かないなら、条件が足りていない
    /// （全員そろっていない、倒していない敵がいる、など）。
    /// 繰り返しても結果は変わらないので、止めて人に任せる。
    /// </summary>
    private const int MaxDoorAttempts = 4;

    /// <summary>
    /// ロットが出そろうのを待つ時間。
    ///
    /// 宝箱を開けた直後は、まだ一覧に載っていないことがある。
    /// 「無いから終わり」と早合点しないよう、少し待つ。
    /// </summary>
    private const double RollSettleSeconds = 8.0;

    /// <summary>
    /// 連携の口。
    ///
    /// 同一PC（パイプ）とインターネット（中継）のどちらかが入る。
    /// ここから下は、どちらで繋いでいるかを気にしない。
    /// </summary>
    private readonly ISyncTransport _sync;
    private readonly StuckDetector _stuck = new(12.0);

    private RunState _state = RunState.Idle;
    private TreasureTarget? _target;
    private Vector3 _destination;
    private DateTime _stateEnteredAt = DateTime.UtcNow;
    private string _note = "";

    /// <summary>
    /// 進めなくなった回数。
    /// 増えるほど、目的地を探す範囲を広げる。
    /// </summary>
    private int _stuckCount;
    private DateTime? _navmeshWaitSince;

    /// <summary>
    /// 始める前から立っていた旗を、すでに片付けたか。
    ///
    /// 他のプラグイン（マップリンクの案内など）が立てた旗を
    /// 宝の位置と取り違えないようにするため、一度だけ消す。
    /// </summary>
    private bool _clearedStaleFlag;

    /// <summary>扉を試した回数。</summary>
    private int _doorAttempts;

    /// <summary>
    /// 魔紋に入る前にいたエリア。
    ///
    /// ここに戻ってきたら「魔紋から出た」と判断する。
    /// 追い出されたのか、自分で出たのかを区別せずに済む。
    /// </summary>
    private uint _territoryBeforeVault;

    /// <summary>
    /// この区画の宝箱があった場所。
    ///
    /// 宝箱は開けると消えるので、消える前に覚えておく。
    /// 扉が開いたあと、「宝箱 → 扉」の向きへ進むために使う。
    /// </summary>
    private Vector3? _chestPosition;

    /// <summary>扉にアクセスした場所。ここから先へ進む起点になる。</summary>
    private Vector3? _doorAccessPosition;

    /// <summary>
    /// 右の扉を狙っているか。
    ///
    /// ふだんは左の扉へ向かう。左が開かなかったときだけ右に切り替える。
    /// 光った扉がある区画では、光っていない側の扉は選べないため、
    /// 左が当たりでないときは右へ移らないと先へ進めない。
    /// </summary>
    private bool _useRightDoor;

    /// <summary>敵が0体になった時刻。次の波を待つために使う。</summary>
    private DateTime? _enemiesClearedAt;

    /// <summary>前に見たときの、敵の体力の合計。減っていれば戦えている。</summary>
    private uint? _lastEnemyHp;

    /// <summary>体力が減らなくなった時刻。</summary>
    private DateTime? _combatStalledSince;

    /// <summary>
    /// 立て直しをどこまで試したか。
    /// 0 = まだ / 1 = 設定を入れ直した / 2 = プリセットを上げた
    /// </summary>
    private int _combatRecoveryStage;

    /// <summary>メンバーが扉のそばでリーダーを待つ上限。</summary>
    private const double MemberDoorWaitSeconds = 90.0;

    /// <summary>魔紋の中で、何も進まないまま待てる上限。</summary>
    private const double VaultIdleSeconds = 180.0;

    /// <summary>
    /// 目的地に着いたとみなす距離。
    ///
    /// 以前は設定で変えられるようにしていたが、
    /// 3y でも 3.1y でも動きは変わらなかったため、固定にした。
    /// </summary>
    private const float ArrivalRange = 3f;

    /// <summary>搬送中に見た、直前の位置。着地したかを見るために使う。</summary>
    private Vector3? _transportLastPosition;

    /// <summary>動きが小さくなった時刻。</summary>
    private DateTime? _transportStillSince;

    /// <summary>この距離より小さい動きなら、止まっているとみなす。</summary>
    private const float TransportSettleDistance = 3f;

    /// <summary>これだけ動きが小さいままなら、着地したとみなす。</summary>
    private const double TransportSettleSeconds = 1.5;

    /// <summary>地形がこれだけ待っても読めないなら、記録に残す。</summary>
    private const double MeshWaitWarnSeconds = 20.0;

    /// <summary>何も見えなくなった時刻。最後の部屋かどうかを見極めるために使う。</summary>
    private DateTime? _emptyRoomSince;

    /// <summary>
    /// 最下層で戦闘が終わった時刻。
    ///
    /// 宝箱は敵を倒した 0.5 秒ほどあとに出る。
    /// 終わった瞬間に「宝箱は無い」と決めると取りこぼす。
    /// </summary>
    private DateTime? _combatEndedAt;

    /// <summary>
    /// 戦闘が終わってから、宝箱が出そろうまで待つ時間（秒）。
    ///
    /// 実測は 0.5 秒（2026-09-18・4台一致）。
    /// 倍の余裕をみて 1.5 秒にしてある。
    /// 待ちすぎても、周回1回につき1度しか通らないので損は小さい。
    /// </summary>
    private const double ChestSpawnSettleSeconds = 1.5;

    /// <summary>
    /// 最下層で、敵に仕掛けられる距離（y）。
    /// </summary>
    private const float FinalRoomEngageRange = 3f;

    /// <summary>
    /// 最下層で狙いを付けてから、様子を書き出すまでの時間（秒）。
    ///
    /// 狙っているのに倒せないなら、RSR が撃っていない可能性が高い。
    /// そのときの状態を残しておけば、次の機会を待たずに原因を追える。
    /// </summary>
    private const double FinalFightWarnSeconds = 20.0;

    /// <summary>最下層で敵に狙いを付けた時刻。</summary>
    private DateTime? _finalFightSince;



    /// <summary>
    /// ロットが片付くのを待つ上限（秒）。
    ///
    /// <b>待ちきりにしないための歯止め。</b>
    /// LazyLoot に任せている場合、向こうの設定によっては
    /// 特定の品を押さないことがある（装備できないものを飛ばす等）。
    /// その品は一覧に残り続けるので、無いものを待ち続けることになる。
    ///
    /// LazyLoot は既定で 1.5〜3.0 秒待ってから押す。
    /// 数点まとめて出ても片付く長さとして 30 秒にしてある。
    /// </summary>
    private const double LootWaitGiveUpSeconds = 30.0;

    /// <summary>
    /// これだけの間ずっと何も見えなければ、最後の部屋とみなす。
    ///
    /// ムービーの前後では、扉も宝箱も一時的に消える。
    /// 短くすると、まだ先があるのに脱出しようとしてしまう。
    /// </summary>
    private const double EmptyRoomSeconds = 20.0;

    /// <summary>
    /// 何も見えないとき、動いて確かめた回数。
    ///
    /// 動くたびに向きを変えるので、何回目かを覚えておく必要がある。
    /// </summary>
    private int _shakeCount;

    /// <summary>
    /// 何も見えなくなってから、動き出すまでの秒数。
    ///
    /// ムービー直後はオブジェクトの出入りが落ち着いていない。
    /// すぐ動くと、湧きかけたものを取りこぼす。
    /// </summary>
    private const double ShakeFirstDelaySeconds = 3.0;

    /// <summary>動いて確かめる間隔。</summary>
    private const double ShakeIntervalSeconds = 4.0;

    /// <summary>1回に動く距離。その場から離れすぎない程度。</summary>
    private const float ShakeDistance = 6.0f;

    /// <summary>
    /// 動いて確かめる回数の上限。
    ///
    /// 仕掛けがそこに在るのに触れない状態は、動かないと解けない。
    /// 20秒では足りないことがあるので、詰まり判定（180秒）まで
    /// 試し続けられるだけの回数を持たせる。
    /// </summary>
    private const int ShakeMaxAttempts = 30;

    /// <summary>1周終わった時刻。次の周回まで少し空けるために使う。</summary>
    private DateTime? _completedAt;

    /// <summary>ワープ床だけが見えている状態が始まった時刻。</summary>
    private DateTime? _warpOnlySince;

    /// <summary>
    /// 今の階層の高さ。null は「まだ分からない・魔紋の外」。
    ///
    /// 魔紋はエリア番号が変わらず、変わるのは高さだけ。
    /// これが階層移動に気づく唯一の手がかりになる。
    /// </summary>
    private float? _floorHeight;

    /// <summary>この階層で高さを測った回数。平均を出すのに使う。</summary>
    private int _floorSamples;

    /// <summary>この階層で見た高さの振れ幅。記録用。</summary>
    private float _floorSpread;

    /// <summary>
    /// これだけ高さが変われば「階層を離れた」とみなす。
    ///
    /// 実測（2026-09-18・15,620 件）:
    ///   同じ階層の中の振れは 1.2y ほど。段差を入れても数y。
    ///   一方、搬送が始まると 1サンプルで 17.6y → 30.6y と
    ///   一気に上がっていった（08:30:37 リーダー）。
    ///
    /// 20y なら、階層の中の起伏では絶対に届かず、
    /// 搬送は上がり始めた時点で捕まえられる。
    /// </summary>
    private const float FloorLeaveThreshold = 20f;

    /// <summary>
    /// 階層を離れた（運ばれている最中）か。
    ///
    /// この間は経路を止め続け、地形の作り直しは頼まない。
    /// 着地して高さが落ち着いたら false に戻す。
    /// </summary>
    private bool _leavingFloor;

    /// <summary>直前に見た高さ。着地したかを見るために使う。</summary>
    private float? _lastHeightSeen;

    /// <summary>
    /// 直前に立っていた場所。水平の瞬間移動に気づくために使う。
    ///
    /// 高さが変わらないまま区画を移ることがある
    /// （第1→第2、第3→第4。どちらも高さは同じ）。
    /// </summary>
    private Vector3? _lastGroundPosition;

    /// <summary>
    /// 直前にいた区画の番号。0 は不明（表に無い区画・運搬中）。
    ///
    /// 区画は座標から分かる。跳び方に関わらず
    /// 「別の区画に来た」ことに気づけるので、これが一番確実。
    /// </summary>
    private int _lastRoomIndex;

    /// <summary>
    /// 着地待ちの起点。
    ///
    /// 「止まり始めた場所」を固定して覚える。
    /// 直前のフレームと比べると、ゆっくり動き続けていても
    /// 「静止している」と誤判定するため。
    /// </summary>
    private Vector3? _landingAnchor;

    /// <summary>
    /// これだけ水平に跳んだら「区画を移った」とみなす。
    ///
    /// 実測では、走っていて1サンプル（約2秒）に進むのは15y程度。
    /// ワープは 413y 跳んだ例がある。30y なら取り違えない。
    /// </summary>
    private const float WarpJumpDistance = 30f;

    /// <summary>高さが動かなくなった時刻。</summary>
    private DateTime? _landingSince;

    /// <summary>これ以内の変化なら「止まっている」とみなす。</summary>
    private const float LandingStillTolerance = 0.5f;

    /// <summary>
    /// 着地したと認めるまで、高さが止まっている秒数。
    ///
    /// 短すぎると、上がる途中の一瞬の停滞を着地と取り違える。
    /// 長すぎると、動き出しが遅れる。
    /// </summary>
    private const double LandingStillSeconds = 1.0;

    /// <summary>
    /// 地形が落ち着くまで動かない、その期限。
    ///
    /// 階層が変わったあとに使う。vnavmesh の「準備できています」は
    /// 前の階層の地形についての答えなので、当てにできない。
    /// </summary>
    private DateTime? _meshSettleUntil;

    /// <summary>
    /// 階層が変わったあと、動き出すまで待つ秒数。
    ///
    /// 作り直しを頼んだ直後の一瞬だけ、前の階層の地形が残っている。
    /// そこを越えれば正しく動くので、長く待つ必要はない。
    /// </summary>
    private const double MeshSettleSeconds = 2.0;

    /// <summary>
    /// ワープ床だけの状態がこれだけ続いたら、乗って先へ進む。
    ///
    /// すぐ乗らないのは、扉や宝箱が湧く前の一瞬を
    /// 「ワープ床しかない」と取り違えないため。
    /// </summary>
    private const double WarpOnlySeconds = 5.0;

    /// <summary>
    /// 魔紋の中で「何もできない」状態が始まった時刻。
    ///
    /// <see cref="SecondsInState"/> の代わりに使う。
    /// メンバーは魔紋に入ってからずっと VaultExploring のままなので、
    /// 状態の滞在時間では「進んでいる」と「詰まっている」を区別できない。
    /// 何かできたときに捨てることで、詰まった時間だけを測る。
    /// </summary>
    private DateTime? _vaultStuckSince;

    /// <summary>魔紋の中で戦い始めた時刻。終わらない戦闘を見つけるために使う。</summary>
    private DateTime? _vaultFightSince;

    /// <summary>メンバーが扉のそばで待ち始めた時刻。</summary>
    private DateTime? _memberDoorWaitSince;

    /// <summary>覚えている宝箱の種類。区画が変わったことに気づくために使う。</summary>
    private uint _chestBaseId;

    /// <summary>
    /// 仲間に位置を伝えた宝箱の種類。
    ///
    /// 同じ宝箱を何度も送らないために覚えておく。
    /// 区画が変われば ID も変わるので、そのとき改めて送られる。
    /// </summary>
    private uint _sharedChestId;

    /// <summary>
    /// この区画の宝箱を開け終わったか。
    ///
    /// 仕掛けが見えないときに「宝箱へ向かうか、扉へ向かうか」を
    /// 決めるために使う。区画が変われば捨てる。
    /// </summary>
    private bool _roomChestDone;

    /// <summary>
    /// 仲間に位置を伝えた扉の種類。
    /// 送る側は「同じ扉を何度も送らない」ために、
    /// 受け取る側は「同じ扉を何度も記録しない」ために使う。
    /// </summary>
    private uint _sharedDoorId;

    /// <summary>
    /// リーダーから届いた扉の位置。
    ///
    /// 自分で扉を見つけられないときだけ使う。
    /// 階層が変われば捨てる（ForgetRoom）。
    /// </summary>
    private Vector3? _sharedDoorPosition;

    /// <summary>今の扉を試し始めた時刻。左右の切り替えで測り直す。</summary>
    private DateTime? _doorTryingSince;

    /// <summary>宝箱のそばに着いた時刻。仲間を待つために使う。</summary>
    private DateTime? _chestArrivedAt;

    /// <summary>ワープ床へ向かって歩き出したときの場所。搬送の検知に使う。</summary>
    private Vector3? _walkStartPosition;

    /// <summary>始める前に使っていた BMR のプリセット。終わったら戻す。</summary>
    private string? _previousBmrPreset;

    /// <summary>前回見たときのパーティの状態。変化に気づくために覚えておく。</summary>
    private PartyStatus _lastPartyStatus = PartyStatus.Unknown;

    // 待ち合わせのために使う。リーダーが「誰が終えたか」を数える。
    private readonly HashSet<string> _stepDoneFrom = [];
    private int _currentStep;
    /// <summary>リーダーから届いた「次の段階の番号」。まだ反映していないもの。</summary>
    private int? _pendingStep;

    /// <summary>
    /// これまでに終えた周回の数。
    ///
    /// 地図を使う順番を決めるのに使う（<see cref="MapTurnTable"/>）。
    /// リーダーが数えて、全機に配る。
    /// 各機が勝手に数えると、合流や再接続でずれる。
    ///
    /// <b>設定ファイルに保存する。</b>
    /// プラグインを更新すると、覚えていた値は消える。
    /// 0 に戻ると順番が①番目からやり直しになり、
    /// <b>本来の番でない機が地図役に選ばれる</b>。
    ///
    /// 実測（2026-09-19 11:17）:
    ///   ①Aさん → ②Bさん と設定してあるのに、
    ///   魔紋の中で更新して開始したら、また① が選ばれた。
    ///   地図を使っていない機が宝箱を開けようとして失敗した。
    /// </summary>
    private int _lapsDone
    {
        get => Plugin.Config.MapLapsDone;
        set
        {
            if (Plugin.Config.MapLapsDone == value)
                return;

            Plugin.Config.MapLapsDone = value;

            // <b>ファイルに書くのはリーダーだけ。</b>
            //
            // 設定ファイルは4キャラで1つを共有している。
            // メンバーが受け取った値を書き込むと、
            // リーダーが数えた値を上書きして取り合いになる。
            //
            // メンバーは覚えておくだけでよい（次の合図で上書きされる）。
            if (Plugin.Config.Role == ClientRole.Leader)
                Plugin.Config.Save();
        }
    }

    /// <summary>
    /// この周回で地図を使う人の名前。
    ///
    /// <b>この人だけが、宝箱・魔紋・魔紋内の扉と宝箱に触れる。</b>
    /// ゲーム側の決まりで、地図を使っていない人は
    /// そもそもアクセスできない（触ろうとしても何も起きない）。
    ///
    /// 空のあいだは「まだ決まっていない」。
    /// 決めるのはリーダーで、決まったら全機に配る。
    /// </summary>
    private string _mapUser = "";
    // リーダーの再読込で周回数が0に戻っても、古い周回として捨てない。
    private readonly string _mapSession = Guid.NewGuid().ToString("N");
    private string _receivedMapSession = "";
    // 現在の選出で地図なしと確認できた人。次の周回・手動開始でクリアする。
    private readonly HashSet<string> _unavailableMapUsers = [];


    /// <summary>
    /// 自分が今の周回の地図役か。
    ///
    /// <b>これが「触れるかどうか」の唯一の判断基準。</b>
    /// 以前はパーティリーダーかどうかで分けていたが、
    /// 地図は誰でも使えるので、リーダーとは切り離した。
    ///
    /// まだ決まっていないときは false。
    /// 決まる前に触りに行くと、触れないまま待ち続けることになる。
    /// </summary>
    private bool IsMapUser
        => !string.IsNullOrEmpty(_mapUser)
        && _mapUser == PlayerHelper.Name;

    /// <summary>今の周回で地図を使う人。画面に出す用。</summary>
    internal string MapUser => _mapUser;

    /// <summary>今の段階。</summary>
    internal RunState State => _state;

    /// <summary>画面に出す補足。</summary>
    internal string Note => _note;

    /// <summary>突き止めた宝の場所。</summary>
    internal TreasureTarget? Target => _target;

    /// <summary>動いているか。</summary>
    internal bool IsRunning => _state is not (RunState.Idle or RunState.Completed or RunState.Failed);

    /// <summary>仲間とのやり取りの状態。</summary>
    internal string SyncStatus => _sync.StatusText;

    /// <summary>リーダーとして、今つながっている台数。</summary>
    internal int ConnectedClients => _sync.ConnectedClients;

    /// <summary>
    /// 記録の書き出し先。
    ///
    /// ここに渡しておかないと、ログには「ゲームで何が起きたか」しか残らず、
    /// 「プラグインが何を判断したか」が一切残らない。
    /// 実際、最初の記録では状態の移り変わりが1行も残っておらず、
    /// どこで詰まったのかをコードから推測するしかなかった。
    /// </summary>
    private readonly RunLog? _log;

    internal RunController(RunLog? log = null)
    {
        _log = log;
        _sync = SyncTransportFactory.Create();
    }

    /// <summary>誰からいつ合図が届いたか。連携の実態を見るために使う。</summary>
    private readonly Dictionary<string, DateTime> _lastSeen = [];

    /// <summary>
    /// 生きている合図を送る。
    ///
    /// 送るだけで、書き込みに失敗した相手は一覧から外れる。
    /// 「つながっているつもりで実は切れている」状態を放置しないための仕組み。
    /// </summary>
    private void SendHeartbeat()
    {
        if (Plugin.Config.Role == ClientRole.Solo)
            return;

        if (!EzThrottler.Throttle("AutoTreasure.Heartbeat", 5000))
            return;

        var selfId = SelfId;
        if (!string.IsNullOrEmpty(selfId))
            _sync.Send(SyncMessage.Ping(selfId));

        // 地図役も繰り返し伝える。
        //
        // <b>1回だけでは足りない。</b>
        // 開始のときに1度送っているが、届かないことがある。
        // 届かなかった機は「自分は地図役ではない」と思ったまま待ち続ける
        // （自分を地図役だと決めつけると、自分の地図を1枚使ってしまうので、
        //   待つ方を選んでいる）。
        //
        // 座標は3秒ごとに送り直しているのに、地図役だけ送り直していなかった。
        // 同じように繰り返せば、取りこぼしても次で追いつく。
        if (Plugin.Config.Role == ClientRole.Leader
            && IsRunning
            && !string.IsNullOrEmpty(_mapUser))
        {
            _sync.Send(SyncMessage.MapUser(_mapUser, _lapsDone, _mapSession));
        }

        // 使用順番の設定も配る。
        //
        // <b>動きのためではなく、画面を合わせるため。</b>
        // 順番を決めるのはリーダーだけなので、配らなくても周回は回る。
        // だが配らないと、メンバーの画面には自分の機の古い設定が出たままになり、
        // 「リーダーで直したのに反映されない」と見える（実際にそうなった）。
        //
        // 止まっている間も配る。設定は周回前に触るものなので、
        // 動いているときだけ配ったのでは遅い。
        if (Plugin.Config.Role == ClientRole.Leader)
            SendMapTurnSetting();
    }

    /// <summary>
    /// 地図の使用順番の設定を仲間に配る。リーダーだけが呼ぶ。
    ///
    /// 中身が変わったときは、すぐに配る。
    /// 変わっていなくても、たまに配り直す
    /// （あとからつないだ機や、取りこぼした機に追いつかせるため）。
    /// </summary>
    private void SendMapTurnSetting(bool force = false)
    {
        if (Plugin.Config.Role != ClientRole.Leader)
            return;

        var cfg = Plugin.Config;

        var slots = cfg.MapTurnOrder
            .Where(s => !string.IsNullOrEmpty(s.Name))
            .Select(s => (s.Name, Math.Max(1, s.Count)))
            .ToList();

        // 前に送ったものと同じかを見るための目印。
        var signature = $"{(int)cfg.MapTurn}|"
                      + string.Join(",", slots.Select(s => $"{s.Name}*{s.Item2}"));

        if (!force && signature == _sentMapTurnSignature)
            return;

        _sentMapTurnSignature = signature;
        _sync.Send(SyncMessage.MapTurnSetting((int)cfg.MapTurn, slots));
    }

    /// <summary>前に配った使用順番の設定。同じものを何度も送らないために持つ。</summary>
    private string _sentMapTurnSignature = "";

    /// <summary>
    /// リーダーから届いた使用順番を、自分の設定に写す。
    ///
    /// <b>メンバーの画面を、リーダーと同じにするための処理。</b>
    /// 順番を決めるのはリーダーだけなので、
    /// ここで写した値をメンバーが判断に使うことはない。
    /// 見えているものを一致させるのが目的。
    ///
    /// 中身が同じなら何もしない。
    /// 毎回書き込むと、設定ファイルへの保存が繰り返される。
    /// </summary>
    private void ApplyMapTurnSetting(int mode, List<(string Name, int Count)> slots)
    {
        // 知らない値なら受け取らない。
        // 壊れた行で「指定キャラのみ」に化けると、
        // 動きは変わらないものの、画面には嘘が出ることになる。
        if (!Enum.IsDefined(typeof(MapTurnMode), mode))
            return;

        var cfg = Plugin.Config;

        var sameMode = (int)cfg.MapTurn == mode;
        var sameCount = cfg.MapTurnOrder.Count == slots.Count;

        if (sameMode && sameCount)
        {
            var same = true;

            for (var i = 0; i < slots.Count; i++)
            {
                if (cfg.MapTurnOrder[i].Name == slots[i].Name
                    && cfg.MapTurnOrder[i].Count == slots[i].Count)
                    continue;

                same = false;
                break;
            }

            if (same)
                return;
        }

        cfg.MapTurn = (MapTurnMode)mode;
        cfg.MapTurnOrder.Clear();

        foreach (var (name, count) in slots)
            cfg.MapTurnOrder.Add(new MapTurnSlot { Name = name, Count = count });

        // <b>ファイルには書かない。</b>
        //
        // 設定ファイルは4台で1つを共有している。
        // ここはリーダーが配った内容を受け取る処理なので、
        // メンバー3台が同じ瞬間に書き込もうとして衝突する
        // （実測 2026-09-19 15:18:59「database is locked」で周回が止まった）。
        //
        // <b>書かなくても困らない。</b>
        // 順番を決めるのはリーダーだけで、メンバーは画面に出すために
        // 覚えているにすぎない。5秒ごとに配り直されるので、
        // 読み込み直しても、すぐ元の値に揃う。
        //
        // 書くのはリーダーだけ（画面で触ったときに保存される）。

        Record("リーダーの地図の使用順番に合わせました");
    }

    /// <summary>最近合図が届いた相手の数。</summary>
    internal int AlivePartners
    {
        get
        {
            var now = DateTime.UtcNow;
            var count = 0;

            foreach (var seen in _lastSeen.Values)
            {
                if ((now - seen).TotalSeconds <= 20)
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// 合図が届いていないパーティメンバーの名前。
    ///
    /// 「3台つながっているはずが2台」と分かっても、
    /// <b>誰が来ていないか</b>が分からないと探しようがない。
    /// パーティにいるのに合図が届かない人を挙げる。
    /// </summary>
    internal List<string> MissingPartners()
    {
        var missing = new List<string>();

        try
        {
            var party = Svc.Party;
            if (party == null || party.Length == 0)
                return missing;

            var now = DateTime.UtcNow;
            var me = SelfId;

            foreach (var member in party)
            {
                var name = member?.Name.TextValue;
                if (string.IsNullOrEmpty(name))
                    continue;

                // 20秒以内に合図が届いていれば「つながっている」。
                var id = member!.ContentId != 0 ? member.ContentId.ToString() : name;
                if (id == me || name == PlayerHelper.Name)
                    continue;
                var alive = _lastSeen.TryGetValue(id, out var seen)
                         && (now - seen).TotalSeconds <= 20;

                if (!alive)
                    missing.Add(name);
            }
        }
        catch
        {
            // 読めないときは何も挙げない。
        }

        return missing;
    }

    /// <summary>一度だけ記録する。同じ行がログを埋めるのを防ぐ。</summary>
    private readonly HashSet<string> _recordedOnce = [];

    private void RecordOnce(string key, string text)
    {
        if (_recordedOnce.Add(key))
            Record(text);
    }

    /// <summary>判断したことを記録に残す。</summary>
    private void Record(string text)
    {
        Svc.Log.Debug($"[AutoTreasure] {text}");
        _log?.Write($"《動作》 {text}");
    }

    /// <summary>
    /// 同じ内容は間を空けて記録する。
    ///
    /// <see cref="RecordOnce"/> は一度きりなので、
    /// 「止まったまま」を追うのに使えない（最初の1回で終わる）。
    /// こちらは同じ内容でも、間隔が空けばまた残す。
    ///
    /// 実際、宝箱の手前で止まったとき、記録がまったく残らず
    /// 何が起きているのか追えなかった（2026-09-22）。
    /// </summary>
    private readonly Dictionary<string, DateTime> _recordedEvery = [];

    private void RecordEvery(string key, double seconds, string text)
    {
        var now = DateTime.UtcNow;

        if (_recordedEvery.TryGetValue(key, out var last)
            && (now - last).TotalSeconds < seconds)
        {
            return;
        }

        _recordedEvery[key] = now;
        Record(text);
    }

    /// <summary>
    /// いま何をしているかを、定期的に記録する。
    ///
    /// <b>「動いていない」ことを記録に残すためのもの。</b>
    ///
    /// これまでは段階が変わったときしか記録していなかった。
    /// そのため、段階の中で止まってしまうと
    /// <b>記録が一行も増えないまま何分も過ぎ</b>、
    /// 何が起きていたのかまったく追えなかった。
    ///
    /// 実測（2026-09-22 09:19:28〜09:23:32）:
    ///   宝箱まで 12.5m の位置で止まり、座標が4分間まったく動かなかった。
    ///   その間の記録は 0 行。例外も出ていない。
    ///
    /// 動いているときは黙っていてよいので、
    /// <b>場所が変わっていないときだけ</b>残す。
    /// </summary>
    private Vector3 _lastWatchPos;
    private DateTime _lastWatchMoved = DateTime.UtcNow;

    private void WatchStall()
    {
        if (!IsRunning)
            return;

        var now = DateTime.UtcNow;
        var pos = PlayerHelper.Position;

        // 1m以上動いていれば、詰まってはいない。
        if (Vector3.Distance(pos, _lastWatchPos) > 1f)
        {
            _lastWatchPos = pos;
            _lastWatchMoved = now;
            return;
        }

        // 止まってから10秒は黙っておく。待ち合わせなど、
        // 止まっているのが正しい場面もある。
        var still = (now - _lastWatchMoved).TotalSeconds;

        if (still < 10)
            return;

        // 10秒ごとに、いま何をしているつもりかを残す。
        RecordEvery("stall", 10,
            $"止まっています（{still:F0}秒）"
            + $" 段階={_state.ToJapanese()}"
            + $" 表示=「{_note}」"
            + $" 地図役={(IsMapUser ? "自分" : _mapUser)}"
            + $" 戦闘={PlayerHelper.InCombat}"
            + $" 移動可={MovementHelper.MovementAllowed}"
            + (MovementHelper.MovementAllowed ? "" : $"（理由: {MovementHelper.BlockReason}）")
            + $" 経路探索中={IPC.VNavmesh.PathfindInProgress}"
            + $" 経路移動中={IPC.VNavmesh.PathIsRunning}"
            + $" 地形={IPC.VNavmesh.NavIsReady}");
    }

    /// <summary>やり取りを始める。役割に応じて受け口か接続かが決まる。</summary>
    internal void StartSync()
    {
        switch (Plugin.Config.Role)
        {
            case ClientRole.Leader:
                _sync.StartAsLeader();
                break;
            case ClientRole.Member:
                _sync.StartAsMember();
                break;
            default:
                _sync.Stop();
                break;
        }
    }

    /// <summary>やり取りをやめる。</summary>
    internal void StopSync() => _sync.Stop();

    /// <summary>
    /// 周回を始める。
    ///
    /// リーダーが始めると、仲間にも合図を送って一緒に始めさせる。
    /// 3台それぞれで押させると、押し忘れや押す順の違いで足並みが乱れる。
    /// </summary>
    /// <param name="tellOthers">
    /// 仲間にも伝えるか。合図を受け取って始めるときは false
    /// （受け取った合図をそのまま投げ返さないため）。
    /// </param>
    internal void Start(bool tellOthers = true)
    {
        // <b>始める前に、役割を今のパーティに合わせる。</b>
        //
        // 設定に残っている役割は、前回の終了時のもの。
        // プラグインを更新した直後は、それがそのまま読み込まれている。
        //
        // 毎フレームの点検（UpdateRoleFromParty）が直してくれるが、
        // <b>更新直後に開始を押すと、直る前にここへ来る</b>。
        // 古い役割のまま判断すると、リーダーでない機が
        // 「自分がリーダーだ」と思って地図役を決めてしまう。
        UpdateRoleFromParty(force: true);

        _unavailableMapUsers.Clear();
        _navmeshWaitSince = null;
        _finalFightSince = null;
        // 前周回の座標は同じ場所の再抽選でも必ず読み直す。
        _target = null;
        _destination = Vector3.Zero;
        _chestBaseId = 0;
        _sharedDoorId = 0;
        _roomChestDone = false;
        _vaultFightSince = null;
        _memberDoorWaitSince = null;
        _transportLastPosition = null;
        _transportStillSince = null;

        if (tellOthers && Plugin.Config.Role == ClientRole.Leader)
        {
            // 誰が地図を使うかを決めて、先に配る。
            //
            // <b>開始の合図より前に送る。</b>
            // 受け取った側は、これが無いと自分が地図役かどうか分からない。
            // 分からないまま動き出すと、触れない宝箱へ向かって待ち続ける。
            //
            // 連続周回では TickCompleted がすでに決めている。
            // まだ決まっていないとき（手で開始を押したとき）だけ決める。
            if (string.IsNullOrEmpty(_mapUser))
                DecideMapUser();

            if (string.IsNullOrEmpty(_mapUser))
            {
                StopForMissingMaps();
                return;
            }

            _sync.Send(SyncMessage.MapUser(_mapUser, _lapsDone, _mapSession));

            _sync.Send(SyncMessage.Begin());
            Record($"仲間にも開始を伝えました（今回の地図役: {_mapUser}）");
        }
        else if (Plugin.Config.Role == ClientRole.Solo)
        {
            // 1台だけのときは、自分が使うしかない。
            _mapUser = PlayerHelper.Name;
        }

        // 戦闘プラグインを、周回に合った設定にしておく。
        // 手で切り替えさせると、3台のうち1台だけ忘れる事故が起きる。
        _cbt.Begin();
        ApplyCombatSettings();

        // LazyLoot が入っているなら、ロットはそちらに任せる。
        //
        // 以前はここで LazyLoot の自動ロットを切っていたが、やめた。
        // 値は戻せても、LazyLoot 側の表示（右上の「FULF Disabled」）が
        // 戻らず、利用者から見て「壊れたまま」になったため。
        // 詳しくは LazyLootControl の説明を参照。
        //
        // 触らなければ、戻す必要も生じない。

        // 地形づくりを速くする。止めるときに元へ戻す。
        // 読み込み時に上げてあるが、途中で手で戻された場合に備えて確かめる。
        if (Plugin.Config.BoostNavmeshCores)
        {
            var cores = Math.Clamp(Plugin.Config.NavmeshCores, 1, 64);
            if (VNavmeshConfig.Current != cores && VNavmeshConfig.Apply(cores))
                Record($"地形づくりを {cores} コアにしました");
        }

        _recordedOnce.Clear();
        _stuck.Reset();
        _stuckCount = 0;
        _clearedStaleFlag = false;
        _doorAttempts = 0;
        LootHelper.Reset();
        _stepDoneFrom.Clear();
        _currentStep = 0;
        _pendingStep = null;
        _territoryBeforeVault = 0;
        _chestPosition = null;
        _doorAccessPosition = null;
        _walkStartPosition = null;
        _chestArrivedAt = null;
        _useRightDoor = false;
        _doorTryingSince = null;
        _lastEnemyHp = null;
        _combatStalledSince = null;
        _combatRecoveryStage = 0;
        _enemiesClearedAt = null;
        _completedAt = null;
        _warpOnlySince = null;
        _combatEndedAt = null;
        _meshSettleUntil = null;
        _vaultStuckSince = null;
        _shakeCount = 0;
        _floorHeight = null;
        _floorSamples = 0;
        _floorSpread = 0f;
        _leavingFloor = false;
        _lastHeightSeen = null;
        _landingSince = null;
        _lastGroundPosition = null;
        _landingAnchor = null;
        _lastRoomIndex = 0;
        _sharedChestId = 0;

        // 移動の禁止は持ち越さない。
        // 前回の周回で閉じたまま終わっていると、一歩も動けない。
        MovementHelper.Allow();
        // 新しい開始では前周回の扉IDも座標も持ち越さない。
        _sharedDoorPosition = null;
        GreedTrap.Reset();

        // 今の状況から、どこから始めるかを決める。
        //
        // いつも地図の解読から始めるとは限らない。
        // 途中で止めた・手で進めたあとに始めることがある。
        // 目の前にあるものを見て、続きから入る。

        // 魔紋の途中から始めた場合、そこまでの経過を推し量る。
        // 覚えていたことは今すべて捨てたので、周りを見て入れ直す。
        RecoverVaultProgress();

        // 地図役がまだ決まっていなければ、自分にする。
        //
        // <b>ただし、メンバーは自分を入れてはいけない。</b>
        //
        // 地図役を決めるのはリーダーで、5秒ごとに送り直している。
        // メンバーがここで自分を入れると、本当の地図役と2人になり、
        // <b>地図を1枚余計に使ってしまう</b>。
        //
        // これは次の2つの場面で起きていた:
        //   ・合図で始まったが、地図役の合図だけ届いていない
        //   ・メンバーが手で停止 → 開始を押した
        //
        // どちらも「待つ」方が安全。待っていれば送り直しで届く。
        // 届かなければ WaitingForParty のまま止まるが、
        // 地図を失うよりはよい。
        //
        // <b>入れてよいのは「本当に1人のとき」だけ。</b>
        //
        // 以前は「メンバーでなければ入れる」としていたが、これが不具合になった。
        //
        // プラグインを更新すると、覚えていた地図役が消える。
        // そのうえ役割（Role）は設定ファイルに残った古い値のままで、
        // パーティを見て直されるのは次のフレーム以降になる。
        //
        // その隙に開始を押すと、<b>地図役でない機が自分を地図役だと思い込む</b>。
        // 実際そうなり、魔紋の中で触れない宝箱を開けようとして失敗した
        // （実測 2026-09-19）。
        //
        // 設定の役割ではなく、<b>今のパーティを直に見て</b>決める。
        // 1人なら自分しかいない。2人以上なら、誰かから届くのを待つ。
        var alone = PartyRoleDetector.Detect() == PartyStatus.Solo;

        if (string.IsNullOrEmpty(_mapUser) && alone)
            _mapUser = PlayerHelper.Name;

        SetState(DecideStartState());
    }

    /// <summary>
    /// 今回の周回で地図を使う人を決める。リーダーだけが呼ぶ。
    ///
    /// 決め方は設定による（<see cref="MapTurnTable"/>）。
    /// 決めた結果は仲間にも配る。配らないと、
    /// メンバーは自分が地図役かどうか分からない。
    ///
    /// <b>持っていない人は飛ばす。</b>
    /// 順番が回ってきても地図を持っていないことがある。
    /// そこで止まると、1人が切らしただけで全部止まる。
    /// 持っている人が見つかるまで順に送る。
    /// 誰も持っていなければ、そこで初めて止める。
    /// </summary>
    private void DecideMapUser()
    {
        _mapUser = "";
        var tries = Math.Max(1, MapTurnTable.PartyNames().Count);
        for (var i = 0; i < tries; i++)
        {
            var candidate = MapTurnTable.Decide(_lapsDone);
            if (string.IsNullOrEmpty(candidate))
                return;
            if (!_unavailableMapUsers.Contains(candidate))
            {
                // 持ち物は本人が解読済み地図も含めて確認する。
                // 未読(-1)を欠品として飛ばしてはいけない。
                _mapUser = candidate;
                return;
            }
            if (Plugin.Config.MapTurn == MapTurnMode.FixedCharacter)
                return;
            _lapsDone = MapTurnTable.SkipToNext(_lapsDone);
        }
    }

    private void StopForMissingMaps()
    {
        // 地図が尽きたら、全機とも止める。
        // 1台だけ止めても、残りは地図役が決まらず待ち続けるだけ。
        const string reason = "使用対象の地図が無いか、指定キャラがパーティにいません";
        Stop(reason, tellOthers: true);
    }

    private void HandleMapUnavailable(string name, int lap)
    {
        // 再送や前の担当から遅れて届いた報告で順番を進めない。
        if (!IsRunning || name != _mapUser || lap != _lapsDone)
            return;
        _unavailableMapUsers.Add(name);
        Record($"{name} は地図を持っていません。次の担当を確認します");
        DecideMapUser();
        if (string.IsNullOrEmpty(_mapUser))
        {
            StopForMissingMaps();
            return;
        }
        _sync.Send(SyncMessage.MapUser(_mapUser, _lapsDone, _mapSession));
        _target = null;
        _destination = Vector3.Zero;
        _clearedStaleFlag = false;
        SetState(IsMapUser ? RunState.Locating : RunState.WaitingForParty);
    }

    /// <summary>
    /// 止める。
    ///
    /// <paramref name="tellOthers"/> に true を渡すと、仲間にも止まるよう伝える。
    ///
    /// <b>既定では伝えない。</b>
    /// 伝えるのは「人が停止ボタンを押したとき」だけにしたい。
    /// 内部の失敗（時間切れ・地形が作れない等）まで伝えると、
    /// 1台の不調で4台とも止まることになる。
    ///
    /// また、<b>合図を受けて止まるときに伝え返してはいけない</b>。
    /// 互いに送り合って永久に止め合うことになる。
    /// </summary>
    /// <param name="reason">画面に出す理由。</param>
    /// <param name="tellOthers">仲間にも伝えるか。</param>
    internal void Stop(string reason = "", bool tellOthers = false)
    {
        _cbt.End();
        // 先に伝える。
        // 自分の後片付けで時間がかかっても、合図だけは出ているようにする。
        if (tellOthers)
        {
            _sync.Send(SyncMessage.Abort(reason));
            Record("仲間にも停止を伝えました");
        }

        RestoreCombatSettings();

        // LazyLoot には何もしない。
        // 始めるときに触っていないので、戻すものも無い。

        // あてていたプリセットの覚えを捨てる。
        // 次に始めるとき、今いる場所に合わせて選び直させる。
        _currentPreset = "";

        // <b>魔紋の中にいるときは、地図役を捨てない。</b>
        //
        // 地図を使えるのは1周につき1人だけで、途中で替われない。
        // 止めて再開しても、地図役は<b>同じ人のまま</b>でなければならない。
        //
        // 捨ててしまうと、再開時に順番の表から決め直すことになる。
        // 表が指すのは「次に誰の番か」であって、
        // 「今回、誰が使ったか」ではない。別人が選ばれる。
        //
        // 実測（2026-09-19 11:19）:
        //   停止ボタンで全機を止めたあと再開したところ、
        //   表に従って別の人が地図役に選ばれ、
        //   宝箱に触れて「宝箱は固く閉ざされている……」と出た。
        //
        // <b>この不具合は、停止を全機へ伝えるようにしてから出た。</b>
        // 以前は押した機だけが忘れ、他の3台が覚えていたので、
        // 再開時に正しい地図役が配り直されて事なきを得ていた。
        // 全機が一斉に忘れるようになり、誰も覚えていない状態になった。
        //
        // 外にいるときは捨てる。1周終わっているので、次は別の人でよい。
        if (!VaultRoutine.IsInsideVault())
            _mapUser = "";

        // 地形づくりの設定は、ここでは戻さない。
        //
        // 止めている間もエリアは変わる。戻してしまうと、
        // 次に始めるときには遅い設定で作られた地形しか無く、
        // また待たされることになる。
        //
        // 戻すのはプラグインを止める・外すときだけ（Dispose）。
        MovementHelper.Stop();
        MovementHelper.Allow();
        _note = reason;
        SetState(RunState.Idle);
    }

    /// <summary>
    /// 戦闘プラグインを、周回に合った設定にする。
    ///
    /// 役割分担:
    ///   BMR = 移動と範囲攻撃（AoE）の回避、狙う相手の優先順位
    ///   RSR = 攻撃と回復のスキル回し
    ///
    /// ヒーラーが何もしなかったのは、BMR の「AutoDuty Passive LB」に
    /// ヒーラー用の中身が一つも入っていないため。移動系しか残らず、
    /// 結果として回避だけする状態になっていた。
    /// 攻撃と回復は RSR に任せるのが正しい。
    /// </summary>
    private void ApplyCombatSettings()
    {
        if (!Plugin.Config.ManageCombatPlugins)
            return;

        if (CombatPlugins.BmrAvailable)
        {
            // 元の設定を覚えておき、終わったら戻す。
            _previousBmrPreset = CombatPlugins.GetActivePreset();

            // 今いる場所に合わせて選ぶ。
            //
            // 魔紋の途中から始めることがある。
            // そのときフィールド用をあてると、最下層で攻撃が始まらない。
            var preset = VaultRoutine.IsInsideVault()
                ? CombatPlugins.VaultPresetName
                : Plugin.Config.BmrPreset;
            if (!string.IsNullOrWhiteSpace(preset))
            {
                // 使うプリセットが無ければ、こちらで作る。
                //
                // 「AutoDuty Passive LB」は AutoDuty には入っていない
                // （配られるのは「AutoDuty」と「AutoDuty Passive」の2つだけ）。
                // 手で作ったものを前提にすると、他の人の環境では動かない。
                CombatPlugins.EnsureRunPreset();

                if (CombatPlugins.SetActivePreset(preset))
                {
                    _currentPreset = preset;
                    Record($"BMR のプリセットを「{preset}」にしました");
                }
                else
                    Record($"BMR のプリセット「{preset}」が見つかりませんでした");
            }
        }
        else
        {
            RecordOnce("no-bmr", "BMR が見つかりません。範囲攻撃の回避は働きません");
        }

        if (CombatPlugins.RsrAvailable)
        {
            // TargetOnly は「狙った相手だけを攻撃する」モード。
            //
            // <b>Manual では攻撃しない。</b>
            // 以前は Manual にしていた。「自分から敵を探しに行かない」
            // という意味だと考えていたが、実際には
            // <b>こちらが指示しない限り一切撃たない</b>モードだった。
            //
            // 実測（2026-09-19 09:55）:
            //   最下層でゴールデン・モルターが名札に出ているのに、
            //   4台とも一度も攻撃せず、そのまま脱出地点へ歩いた。
            //
            // 最下層の敵はこちらから仕掛けないと動かないので、
            // 「狙いを付ける → RSR が撃つ」という流れが要る。
            // TargetOnly なら、狙った相手だけを撃ち、
            // 勝手に他の敵を探しに行くこともない。
            if (CombatPlugins.SetRsrMode(CombatPlugins.RsrMode.TargetOnly))
                Record("RSR を TargetOnly にしました");
        }
        else
        {
            RecordOnce("no-rsr", "RSR が見つかりません。攻撃と回復は行われません");
        }
    }

    /// <summary>
    /// 魔紋の中の戦い方に切り替える。
    ///
    /// フィールド用との違いは「Everything」の有無だけ。
    /// これが入っていると、周りの敵を片端から狙う。
    /// 魔紋は封鎖された部屋で、いるのは倒すべき敵だけなので構わない。
    ///
    /// <b>最下層はこれでないと攻撃が始まらない。</b>
    /// 同じ名前・同じ座標の相手が10体いて、9体は触れない偽物。
    /// 狙いを自分で選ばせないと、本物に手が出せない（実測 2026-09-19）。
    /// </summary>
    private void ApplyVaultPreset() => ApplyPreset(CombatPlugins.VaultPresetName, "魔紋用");

    /// <summary>
    /// フィールドの戦い方に戻す。
    ///
    /// <b>戻し忘れないこと。</b>
    /// 魔紋用のまま外へ出ると、道中の雑魚を片端から狙いに行き、
    /// 宝の場所へ向かう途中で関係ない戦闘に巻き込まれる。
    /// </summary>
    private void ApplyFieldPreset() => ApplyPreset(CombatPlugins.FieldPresetName, "フィールド用");

    /// <summary>
    /// BMR のプリセットを切り替える。
    ///
    /// 同じものを何度も送らない。すでにそれなら何もしない。
    /// </summary>
    private void ApplyPreset(string name, string label)
    {
        if (!Plugin.Config.ManageCombatPlugins || !CombatPlugins.BmrAvailable)
            return;

        if (_currentPreset == name)
            return;

        // 無ければ作る。初回や、手で消されたときのため。
        CombatPlugins.EnsureRunPreset();

        if (CombatPlugins.SetActivePreset(name))
        {
            _currentPreset = name;
            Record($"戦い方を{label}（{name}）に切り替えました");
        }
        else
        {
            Record($"BMR のプリセット「{name}」に切り替えられませんでした");
        }
    }

    /// <summary>今あてているプリセット。同じものを送り直さないために持つ。</summary>
    private string _currentPreset = "";

    /// <summary>戦闘プラグインの設定を元に戻す。</summary>
    private void RestoreCombatSettings()
    {
        if (!Plugin.Config.ManageCombatPlugins)
            return;

        if (CombatPlugins.RsrAvailable)
            CombatPlugins.SetRsrMode(CombatPlugins.RsrMode.Off);

        // 始める前に使っていたプリセットに戻す。
        if (CombatPlugins.BmrAvailable && _previousBmrPreset != null)
        {
            CombatPlugins.SetActivePreset(_previousBmrPreset);
            _previousBmrPreset = null;
        }
    }

    /// <summary>毎フレーム呼ぶ。</summary>
    internal void Tick()
    {
        _cbt.Tick();
        ReceiveMessages();

        if (!PlayerHelper.IsValid)
            return;

        if (Environment.TickCount64 >= _nextLootDiagnostic)
        {
            _nextLootDiagnostic = Environment.TickCount64 + 1000;
            if (_sync is RelaySync relayDiagnostic)
            {
                var connection = relayDiagnostic.Diagnostic;
                if (connection != _lastRelayDiagnostic)
                {
                    _lastRelayDiagnostic = connection;
                    Record($"インターネット接続診断: {connection}");
                }
            }
            var diagnostic = LazyLootControl.Diagnostic() + "; " + LootHelper.Diagnostic();
            if (diagnostic != _lastLootDiagnostic)
            {
                _lastLootDiagnostic = diagnostic;
                Record($"LazyLoot 診断 [{PlayerHelper.Name}]: {diagnostic}");
                Svc.Log.Information($"[AutoTreasure] LazyLoot 診断 [{PlayerHelper.Name}]: {diagnostic}");
            }
        }

        // パーティの状態を見て、役割を合わせる。
        // 動いていない間も見ておく。始める前に正しくしておきたいため。
        UpdateRoleFromParty();

        // 生きている合図を定期的に送る。
        // 送り先が切れていれば、その場で一覧から外れる。
        SendHeartbeat();
        if (VNavmesh.NavIsReady) _navmeshWaitSince = null;

        // 移動の禁止が長すぎないか見る。
        //
        // <b>毎フレーム・無条件に見ること。</b>
        // 以前はこの判定が MovementHelper.MoveTo の中にあった。
        // ところが「地形を作り直しています」の段階は Stop() して戻るだけで、
        // MoveTo を通らない。vnavmesh が地形を作れないまま諦めると
        // （NavmeshWatcher は3回で諦める）、その段階から抜けられず、
        // 禁止も解けないまま周回が永久に止まっていた。
        //
        // 動こうとしたときではなく、時間が過ぎたかどうかで解く。
        MovementHelper.ReleaseIfStuck();

        // 地形が作られているか、常に見張る。
        //
        // 移動しようとしたときに気づくのでは遅い。
        // エリアに入った時点で作り始めていなければ、その場で促す。
        // 動き出す頃には出来上がっている、という状態を目指す。
        if (IsRunning)
            NavmeshWatcher.EnsureBuilding();

        // 魔紋に入ったかどうかは、止まっている間も見ておく。
        //
        // 宝箱を開け終わると、いったん「完了」になる。そこから魔紋へ入るのが
        // 本来の流れなので、ここで気づけないと中で何もしなくなる。
        //
        // エリア移動の通知だけに頼らないのは、通知が来る前後で
        // まだ中の情報が読めないことがあるため。毎フレーム確かめる方が確実。
        CheckVaultEntry();

        // 階層が変わっていないかを、何よりも先に確かめる。
        //
        // <b>ここに置くことが大事。</b>
        // 以前は魔紋の中の処理（TickVaultExploring）の中にあったが、
        // 搬送が起きるのは「次の部屋へ移っています」の最中なので、
        // その間はこの確認が一度も走らなかった。
        //
        // 実測（2026-09-18 08:29:16）では、搬送されてから
        // 「ワープ床に乗りました」が出るまでの5秒間、
        // 前の階層の経路をたどって元の方向へ走り続けていた。
        //
        // 毎フレーム・どの段階でも見る。魔紋の外では何もしない。
        CheckFloorChange();

        // 1周終わったら、次の周回へ。
        //
        // ここは IsRunning の手前に置く。
        // 「完了」は IsRunning が false なので、下の早期 return より後ろに
        // 置くと一度も通らない。実際、状態機械にも case が無いため、
        // 1周終わるとそのまま止まっていた。
        TickCompleted();

        if (!IsRunning)
            return;

        // 出ている確認ウィンドウは、何よりも先に片付ける。
        //
        // ここを IsReady の後ろに置いてはいけない。
        // 「はい／いいえ」が開くと、ゲームは自分を OccupiedInEvent 扱いにする。
        // つまり IsReady は false になり、下の早期 return で抜けてしまう。
        // 結果、ウィンドウが出ている間だけ片付ける処理に届かない——
        // 一番必要な場面で動かない、という状態になっていた。
        //
        // 扉の「くぐりますか？」で止まったのはこれが原因。
        AddonHelper.DismissCommonDialogs();

        // ムービー中やエリア移動中は手を出さない。
        // ここで動かそうとしても通らず、かえって詰まる元になる。
        if (!PlayerHelper.IsReady)
        {
            // 移動中に待たされている場合もあるので、詰まり判定は止めておく。
            _stuck.Reset();
            return;
        }

        // 止まったままになっていないかを見張る。
        //
        // ここに置くのは、段階ごとの処理より<b>前</b>。
        // 処理の中で早期に return していても、記録だけは残る。
        WatchStall();

        switch (_state)
        {
            case RunState.Locating:        TickLocating();       break;
            case RunState.Teleporting:     TickTeleporting();    break;
            case RunState.Travelling:      TickTravelling();     break;
            case RunState.WaitingForParty: TickWaitingForParty();break;
            case RunState.Digging:         TickDigging();        break;
            case RunState.OpeningChest:    TickOpeningChest();   break;
            case RunState.Fighting:        TickFighting();       break;
            case RunState.Rolling:         TickRolling();        break;
            case RunState.VaultExploring:  TickVaultExploring(); break;
            case RunState.VaultDoor:       TickVaultDoor();      break;
            case RunState.VaultTransition: TickVaultTransition();break;
            case RunState.EnteringVault:   TickEnteringVault();  break;
            case RunState.LeavingVault:    TickLeavingVault();   break;
        }
    }

    /// <summary>
    /// パーティの状態から、自分の役割を決める。
    ///
    /// パーティリーダーは常に1人なので、ゲームに聞けば取り違えようがない。
    /// 手で設定していると、3台のどれかを間違えたまま動かす事故が起きる。
    ///
    /// 状態が変わったときだけ切り替える。毎フレーム触ると通信が落ち着かない。
    /// </summary>
    private void UpdateRoleFromParty(bool force = false)
    {
        if (!Plugin.Config.AutoDetectRole)
            return;

        // 毎フレーム調べる必要はないので、ふだんは間引く。
        //
        // ただし <paramref name="force"/> のときは必ず調べる。
        // 開始を押した瞬間は、古い役割のまま判断させてはいけない。
        // 間引きに当たって飛ばされると、直前の値がそのまま使われる。
        if (!force && !EzThrottler.Throttle("AutoTreasure.RoleCheck", 2000))
            return;

        var status = PartyRoleDetector.Detect();

        // まだ読めないうちは何もしない。
        if (status == PartyStatus.Unknown)
            return;

        // 状態が変わったときだけ記録する。2秒ごとに同じ行が並ぶと読みづらい。
        var changed = status != _lastPartyStatus;
        _lastPartyStatus = status;

        var role = PartyRoleDetector.ToRole(status);

        if (role != Plugin.Config.Role)
        {
            Record($"役割を「{role}」にします（{PartyRoleDetector.Describe(status)}）");

            Plugin.Config.Role = role;
            Plugin.Config.Save();
        }
        else if (changed)
        {
            Svc.Log.Debug($"パーティの状態: {PartyRoleDetector.Describe(status)}");
        }

        // 役割が「変わったとき」だけでなく、毎回この先へ進む。
        //
        // 以前はここで「役割が同じなら何もしない」と抜けていた。
        // すると、前回の設定がすでに正しい役割だった場合（再起動後など）に
        // 一度も通信を始めないまま、役割の表示だけ正しくなる。
        // 画面には「パーティリーダー」と出ているのに誰ともつながらない、
        // という分かりにくい状態になっていた。
        //
        // 見るべきは「役割が変わったか」ではなく「通信が今あるべき姿か」。
        EnsureSyncMatchesRole();
    }

    /// <summary>
    /// 通信が、今の役割に合った向きで動いているかを確かめる。
    /// 合っていなければ開き直す。
    ///
    /// 目指す状態と実際の状態を突き合わせるだけなので、何度呼んでもよい。
    /// </summary>
    private void EnsureSyncMatchesRole()
    {
        var want = Plugin.Config.Role switch
        {
            ClientRole.Leader => SyncMode.Leader,
            ClientRole.Member => SyncMode.Member,
            _                 => SyncMode.Stopped,
        };

        if (_sync.Mode == want)
            return;

        Record($"連携: {_sync.Mode} → {want} に切り替えます");

        if (want == SyncMode.Stopped)
        {
            StopSync();
            return;
        }

        StartSync();
    }

    /// <summary>
    /// 魔紋の中にいるかを見て、必要なら段階を切り替える。
    ///
    /// エリアの種類を調べるのは少し重いので、毎フレームではなく間隔をあける。
    /// </summary>
    private void CheckVaultEntry()
    {
        if (!EzThrottler.Throttle("AutoTreasure.VaultCheck", 1000))
            return;

        var inside = VaultRoutine.IsInsideVault();

        // 魔紋の中で動いている段階。ここに VaultTransition を入れ忘れると、
        // ワープ床へ歩いている最中に「外に出た」と誤解して区切ってしまう。
        var inVaultState = _state is RunState.VaultExploring or RunState.VaultDoor
                                  or RunState.VaultTransition or RunState.LeavingVault;

        // 自分で止めたとき（Idle）と、続けられないと判断したとき（Failed）は、
        // 勝手に動き出さない。人が止めた・人に委ねた状態を尊重する。
        if (inside && !inVaultState
            && _state != RunState.Idle && _state != RunState.Failed)
        {
            // 魔紋に入った。中での動きに切り替える。
            // 自分で止めている（Idle）ときは、勝手に動き出さない。
            //
            // <b>戦い方も魔紋用に切り替える。</b>
            // フィールド用は「自分から敵を探しに行かない」設定で、
            // 関係ない雑魚に絡まれないようにしてある。
            // だが魔紋の中——特に最下層——では、それだと攻撃が始まらない。
            ApplyVaultPreset();

            SetState(RunState.VaultExploring);
        }
        else if (!inside && inVaultState)
        {
            // 魔紋から出た。
            //
            // 入る前のエリアに戻っていれば、追い出されたか自分で出たかを
            // 区別せずに「1周終わった」と判断できる。
            if (_territoryBeforeVault != 0
                && PlayerHelper.TerritoryType == _territoryBeforeVault)
            {
                Record($"魔紋から出ました（{_territoryBeforeVault} に戻りました）");
            }
            else
            {
                Record("魔紋から出ました");
            }

            // <b>フィールド用の戦い方に戻す。</b>
            // 戻し忘れると、次の周回で道中の雑魚を片端から狙いに行く。
            ApplyFieldPreset();

            // 次の地図に備えて、覚えていたことを捨てる。
            _target = null;
            _destination = Vector3.Zero;
            _chestPosition = null;
            _doorAccessPosition = null;
            _walkStartPosition = null;
            _chestArrivedAt = null;
            _useRightDoor = false;
            _doorTryingSince = null;

            // 次の周回に備えて、覚えていたエリアも捨てる。
            // 残すと、次の地図が別のエリアでも前のエリアと比べてしまう。
            _territoryBeforeVault = 0;

            SetState(RunState.Completed);
        }
    }

    // ---- 各段階 -------------------------------------------------------------

    /// <summary>宝の場所を突き止める。</summary>
    /// <summary>
    /// 自分がテレポしてよい順番か。
    ///
    /// <b>常に true。</b>
    /// 以前は機ごとに時間をずらしていたが、実測（2026-09-17・4台同時）で
    /// 全員が1秒以内に同じエリアへ入っても地形の待ちは 0.9 秒だった。
    /// ずらす意味がなく、待たせる害だけがあったのでやめた。
    /// </summary>
    private static bool IsMyTeleportTurn() => true;

    /// <summary>
    /// 今の状況から、どの段階で始めるかを決める。
    ///
    /// 「開始」は必ずしも周回の最初とは限らない。
    /// 手で途中まで進めてから押すこともあれば、
    /// 止めたあとに押し直すこともある。
    /// そのとき地図の解読から始めると、目の前の宝箱を放置してしまう。
    /// </summary>
    private RunState DecideStartState()
    {
        // 魔紋の中にいる。中での動きに入る。
        if (VaultRoutine.IsInsideVault())
            return RunState.VaultExploring;

        // フィールドに転送魔紋が出ている。入るところから。
        if (VaultRoutine.FindEntryPortal() != null)
            return RunState.EnteringVault;

        // 目の前に宝箱がある。開けるところから。
        //
        // 掘ったあとや、手で一度開けたあとがこれにあたる。
        if (ObjectHelper.GetNearestTreasure() != null)
        {
            Record("宝箱が目の前にあるので、開けるところから始めます");
            return RunState.OpeningChest;
        }

        // 地図役でなければ、自分では場所を調べない。届くのを待つ。
        //
        // <b>パーティリーダーかどうかでは決めない。</b>
        // 地図は誰でも使えるので、メンバーが地図役になることもある。
        // 役割で分けると、地図を持っているメンバーが
        // 何もせずに待ち続けることになる。
        if (!IsMapUser)
            return RunState.WaitingForParty;

        return RunState.Locating;
    }

    /// <summary>
    /// 必要なら、古ぼけた地図S5 を自分で解読する。
    ///
    /// 解読すると「宝の地図S5」がイベントアイテム欄に入る。
    /// これを持っている間は、その地図を追っている最中なので使ってはいけない。
    /// 持っていなければ使ってよい——というのが安全の根拠。
    ///
    /// 地図はリーダーだけが使う。全員が使うと枚数を無駄にする。
    /// </summary>
    private void TryDecodeMap()
    {
        // 地図役でなければ使わない。座標が届くのを待つ。
        //
        // <b>地図を使った人しか、その先へ進めない。</b>
        // 宝箱も魔紋も、使った人以外は触れない（ゲーム側の決まり）。
        // だから「誰が使うか」を先に決めておく必要がある。
        if (!IsMapUser)
        {
            _note = string.IsNullOrEmpty(_mapUser)
                ? "地図役が決まるのを待っています"
                : $"{_mapUser} が地図を解読するのを待っています";
            return;
        }

        // 解読済みの地図を持っているか。
        //
        // 読めなかったときは null が返る。そのときは使わない。
        // 「分からないから使ってみる」は、地図を1枚失う可能性がある。
        // ゲームに直接聞く方を先に見る。持ち物を数える方法より確実。
        // 地図を1枚無駄にしないための二重の確認。
        var held = DecodedMapReader.Read();
        if (held != null)
        {
            // S5 以外の地図を持っている。
            //
            // 解読済みの地図は1枚しか持てないので、これを片付けない限り
            // S5 は解読できない。黙って止まると理由が分からないので伝える。
            if (held.Value.Rank != TreasureSpotTable.S5Rank)
            {
                _note = $"S5 以外の地図（Rank {held.Value.Rank}）を持っています。先に片付けてください";
                RecordOnce("other-map", $"S5 以外の解読済み地図を所持（Rank {held.Value.Rank}）");
                return;
            }

            _note = "解読済みの地図があります";
            return;
        }

        var hasDecoded = GameSnapshot.HasDecodedMap();

        if (hasDecoded == null)
        {
            _note = "持ち物を確認しています";
            return;
        }

        if (hasDecoded == true)
        {
            // すでに解読済みの地図を持っている。
            //
            // 前回の周回の途中から再開した場合や、
            // 手で解読したあとに開始した場合がこれにあたる。
            //
            // このとき、旗は立っていないことがある。
            // 解読済みの地図をもう一度使うと地図が開き、旗が立つ。
            // 地図は消費されないので、何度使っても損はしない。
            // 解読済みの地図を持っている。場所はゲームに聞けば分かるので、
            // このまま次のフレームで読み取られる（旗も Globetrotter も要らない）。
            _note = "解読済みの地図から宝の位置を読んでいます";

            // 万一ゲームから読めない場合に備えて、旗も立てておく。
            // Globetrotter が入っていれば、これで旗が立つ。
            if (ActionHelper.ShowDecodedMap())
                RecordOnce("show-map", "念のため /tmap も呼びました");

            if (SecondsInState > ShowMapTimeoutSeconds)
            {
                _note = "宝の位置を読み取れません。地図にカーソルを合わせてください";
                RecordOnce("show-map-failed", "解読済みの地図から場所を読み取れませんでした");
            }

            return;
        }

        // 未解読の地図を持っているか。
        var stock = GameSnapshot.CountMapS5();

        if (stock < 0)
        {
            _note = "持ち物を確認しています";
            return;
        }

        if (stock == 0)
        {
            _note = "古ぼけた地図S5 を持っていません";
            if (Plugin.Config.Role == ClientRole.Member)
            {
                if (EzThrottler.Throttle("AutoTreasure.MapUnavailable", 2000))
                    _sync.Send(SyncMessage.MapUnavailable(PlayerHelper.Name, _lapsDone));
            }
            else
            {
                HandleMapUnavailable(PlayerHelper.Name, _lapsDone);
            }
            return;
        }

        // ここまで来たら使ってよい。
        _note = "古ぼけた地図S5 を解読しています";

        // 右クリックの一覧が開いていたら、「解読する」を選ぶ。
        //
        // 地図は「使う」では通らず、この一覧から選ぶ形だった。
        // 一覧が出ているあいだは、そちらを片付けるのが先。
        if (AddonHelper.ClickContextMenu("解読"))
        {
            RecordOnce("decipher", "一覧から「解読する」を選びました");
            return;
        }

        if (ActionHelper.UseMapS5())
            RecordOnce("use-map", $"古ぼけた地図S5 を使います（残り {stock} 枚）");
    }

    private void TickLocating()
    {
        if (!IsMapUser)
        {
            SetState(RunState.WaitingForParty);
            return;
        }

        // 始めた時点で立っていた旗は、他のプラグインが以前に立てたものかもしれない。
        // それを宝の位置と取り違えると、まったく違う場所へ向かってしまう。
        //
        // 最初の一度だけ消しておき、そのあとに立った旗＝今回の地図のもの、として扱う。
        // 地図の解読はこれから行うので、消しても困らない。
        if (!_clearedStaleFlag)
        {
            MapFlagReader.Clear();
            _clearedStaleFlag = true;
            _note = "地図を解読してください（宝の位置が分かると自動で動き出します）";
            return;
        }

        // まずゲームに直接聞く。
        //
        // 持っている解読済み地図が「40候補地のうち何番目か」を
        // ゲーム自身が持っている。これを読めば、旗に頼る必要がない。
        //
        // 旗を見る方法には次の弱点がある:
        //   ・前の周回の旗が残っていると、それを読んでしまう
        //   ・他のプラグインや、地図をクリックした旗と区別できない
        //   ・高さの情報が無く、一番近い候補地を推測するしかない
        //
        // ゲームに聞けば、どれも起きない。
        var target = DecodedMapReader.Locate();

        // 読めなかったときだけ、旗から探す。
        // ゲームの更新で読み方が変わっても、こちらで動き続けられるようにする。
        if (target == null)
        {
            target = TreasureLocator.Locate(TreasureSpotTable.S5Rank);

            if (target != null)
                RecordOnce("locate-by-flag", "地図を直接読めないので、旗から場所を割り出しました");
        }
        if (target == null)
        {
            // まだ旗が立っていない。地図を解読すれば立つ。
            TryDecodeMap();
            return;
        }

        _target = target;
        _note = $"目的地: {target.Value.PlaceName}";

        // 仲間にも同じ場所を伝える。追いかけさせるのではなく、
        // それぞれが自分で向かえるようにする。
        // 座標を配るのは、地図を使った人。
        // その人しか宝の場所を知らないため。
        if (IsMapUser)
            _sync.Send(SyncMessage.Treasure(target.Value.TerritoryType, target.Value.World));

        SetState(PlayerHelper.TerritoryType == target.Value.TerritoryType
            ? RunState.Travelling
            : RunState.Teleporting);
    }

    /// <summary>目的のエリアへ移動する。</summary>
    private void TickTeleporting()
    {
        // 順番にテレポする。
        //
        // 4台が同時に同じエリアへ入ると、同じ瞬間に地形の読み込みが重なる。
        // 読み込み自体は速いが、エリア移動直後は描画も通信も立て込むため、
        // 重なると全体が遅れ、そのあいだ移動の指示が通らない。
        //
        // 自分の番が来るまで待つ。ずらすのは数秒で足りる。
        if (!IsMyTeleportTurn())
        {
            _note = "順番にテレポしています";
            return;
        }

        if (_target == null)
        {
            SetState(RunState.Locating);
            return;
        }

        if (PlayerHelper.TerritoryType == _target.Value.TerritoryType)
        {
            SetState(RunState.Travelling);
            return;
        }

        // 戦闘中は飛べない。終わるのを待つ。
        if (PlayerHelper.InCombat)
        {
            _note = "戦闘が終わるのを待っています";
            return;
        }

        // 詠唱中（テレポの詠唱）なら、そのまま待つ。
        if (PlayerHelper.IsCasting)
        {
            _note = $"{_target.Value.PlaceName} へ飛んでいます";
            return;
        }

        // 宝に一番近いエーテライトを選ぶ。
        // 同じエリアに複数あることがあり、遠い方に降りると飛行が長くなる。
        var destination = TeleportHelper.FindNearest(
            _target.Value.TerritoryType, _target.Value.World);

        if (destination == null)
        {
            // 登録していないエーテライトには飛べない。
            _note = $"{_target.Value.PlaceName} へ手で移動してください（飛べるエーテライトがありません）";
            return;
        }

        _note = $"{destination.Value.Name} へ飛んでいます";

        if (TeleportHelper.Teleport(destination.Value.AetheryteId))
        {
            RecordOnce($"tp-{destination.Value.AetheryteId}",
                $"テレポ: {destination.Value.Name}（宝まで {destination.Value.Distance:F0}y）");
        }

        // いつまでも飛べないなら、手に任せる。
        if (SecondsInState > TeleportTimeoutSeconds)
        {
            _note = $"{_target.Value.PlaceName} へ手で移動してください";
        }
    }

    /// <summary>地形構築が失敗したまま待ち続けることを防ぐ。</summary>
    private bool CheckNavmeshTimeout()
    {
        _navmeshWaitSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _navmeshWaitSince.Value).TotalSeconds <= TravelTimeoutSeconds)
            return false;
        MovementHelper.Stop();
        MovementHelper.Allow();
        _note = "地形を読み込めませんでした。地形の状態を確認して再開してください";
        Record(_note);
        SetState(RunState.Failed);
        return true;
    }

    private void TickTravelling()
    {
        if (_target == null)
        {
            SetState(RunState.Locating);
            return;
        }

        // 別のエリアにいるなら、歩いても着かない。テレポへ戻す。
        //
        // ここへは色々な経路から入ってくるので、
        // 途中で取りこぼしても、最後にここで拾えるようにしておく。
        if (PlayerHelper.TerritoryType != _target.Value.TerritoryType)
        {
            RecordOnce($"travel-wrong-area-{_target.Value.TerritoryType}",
                $"別のエリアにいるので、テレポし直します（{_target.Value.PlaceName} へ）");

            SetState(RunState.Teleporting);
            return;
        }

        // 目的地はエリアに入ってから決める。
        // 地形が読み込まれていないと、地面の高さを求められない。
        // 地形が読めていないと、何を指示しても静かに失敗する。
        //
        // vnavmesh は地形が無いと経路探索そのものを断る。
        // こちらはその失敗を握りつぶしていたため、
        // 見た目には「指示は出ているのに動かない」状態になっていた。
        // 実際、エーテライトから一歩も動かない機があり、
        // vnavmesh の表示は「Mesh: Not Ready」だった。
        if (!VNavmesh.NavIsReady)
        {
            if (CheckNavmeshTimeout()) return;
            var progress = VNavmesh.NavBuildProgress;

            _note = progress >= 0f
                ? $"地形を読み込んでいます（{progress * 100:F0}%）"
                : "地形の読み込みを待っています";

            // 待ち始めと、長引いたときに残す。
            RecordOnce("mesh-wait", "地形がまだ読めていないので待ちます");

            // 待っているあいだに、乗るところまで済ませておく。
            // 地形ができた瞬間に飛び出せるようにしておく。
            if (MovementHelper.CanFlyHere)
                MovementHelper.EnsureMounted(true);

            if (SecondsInState > MeshWaitWarnSeconds
                && EzThrottler.Throttle("AutoTreasure.MeshWarn", 15000))
            {
                Record($"地形が {SecondsInState:F0} 秒たっても読めません"
                     + $"（進み具合 {(progress >= 0f ? $"{progress * 100:F0}%" : "不明")}）");
            }

            return;
        }

        if (_destination == Vector3.Zero)
        {

            _destination = TreasureLocator.SnapToGround(_target.Value.World);
            _stuck.Reset();
            _stuckCount = 0;
        }

        var range = ArrivalRange;

        if (MovementHelper.ArrivalCheck(_destination, range))
        {
            MovementHelper.Stop();
            MovementHelper.Dismount();
            SetState(RunState.WaitingForParty);
            return;
        }

        // 詰まったら、同じ場所へ引き直すのではなく、行ける場所を探す。
        //
        // 宝の座標が崖の中や水中を指していると、何度引き直しても結果は変わらない。
        // 進めなかった回数に応じて、まわりを少しずつ広く探す。
        if (_stuck.Check())
        {
            _stuckCount++;
            MovementHelper.Stop();
            MovementHelper.ResetMountCooldown();

            var alternative = TreasureLocator.FindReachableNear(_target.Value.World, _stuckCount);
            if (alternative != _destination)
            {
                Svc.Log.Information($"進めないので、少し離れた場所を目指します（{_stuckCount} 回目）。");
                _destination = alternative;
            }
            else
            {
                Svc.Log.Information($"進めないので、経路を引き直します（{_stuckCount} 回目）。");
            }
            return;
        }

        // 飛べる場所なら、マウントに乗って飛んで向かう。
        //
        // ここで CanFly（今すぐ飛べるか）を見てはいけない。
        // 徒歩のあいだは「乗っていない」ため false になり、
        // 「飛べない場所だ」と誤解してそのまま歩き出してしまう。
        // 実際、テレポ直後に徒歩で向かってしまった。
        //
        // 見るべきは「このエリアで飛べるか」。
        // 乗る・飛び立つところまでは MoveTo が面倒を見る。
        // 移動しながらも、仲間に場所を伝え続ける。
        // 仲間がまだ場所を知らないまま立ち止まっていることがある。
        if (IsMapUser && _target != null
            && EzThrottler.Throttle("AutoTreasure.ResendTreasure", 3000))
        {
            _sync.Send(SyncMessage.Treasure(
                _target.Value.TerritoryType, _target.Value.World));
        }

        var fly = MovementHelper.CanFlyHere;

        // 地形ができるまでは、乗るだけ先に済ませておく。
        //
        // 待っている時間で騎乗と離陸を終わらせておけば、
        // 地形ができた瞬間に飛び出せる。
        // 何もせず突っ立って待つより、その分だけ早く着く。
        if (fly && !PlayerHelper.IsFlying)
            MovementHelper.EnsureMounted(true);

        MovementHelper.MoveTo(_destination, range, fly);
        MovementHelper.UseSprintIfIdle();

        var how = !fly ? "徒歩"
                : PlayerHelper.IsFlying ? "飛行"
                : PlayerHelper.IsMounted ? "騎乗（離陸中）"
                : "騎乗の準備中";

        _note = $"移動中 {how}（残り {ObjectHelper.DistanceToPlayer(_destination):F0} m）";

        RecordOnce($"travel-{how}", $"移動のしかた: {how}");

        // たどり着けない場所だった場合、ここで止まり続けてしまう。
        // 詰まり判定は「同じ場所にいること」を見るので、
        // 遠回りし続けている場合は引っかからない。時間でも区切る。
        if (SecondsInState > TravelTimeoutSeconds)
        {
            Svc.Log.Warning($"目的地にたどり着けませんでした（残り {ObjectHelper.DistanceToPlayer(_destination):F0} m）。");
            _note = "目的地にたどり着けませんでした";
            MovementHelper.Stop();
            SetState(RunState.Failed);
        }
    }

    /// <summary>仲間がそろうのを待つ。</summary>
    private void TickWaitingForParty()
    {
        // 1台だけで動かしているなら待つ必要がない。
        if (Plugin.Config.Role == ClientRole.Solo)
        {
            SetState(RunState.Digging);
            return;
        }

        if (Plugin.Config.Role == ClientRole.Member)
        {
            TickWaitingAsMember();
            return;
        }

        TickWaitingAsLeader();
    }

    /// <summary>メンバーとして、現地に着いてから合図を待つ。</summary>
    private void TickWaitingAsMember()
    {
        // 場所を受け取るまでは動きようがない。
        if (_target == null)
        {
            _note = "リーダーからの連絡を待っています";

            // こちらからも催促する。
            // リーダーの合図を取りこぼしていた場合、待っているだけでは
            // 永久に届かない。生きていることを伝えれば、
            // リーダーは繰り返し場所を送ってくれる。
            var id = SelfId;
            if (!string.IsNullOrEmpty(id)
                && EzThrottler.Throttle("AutoTreasure.AskTreasure", 3000))
            {
                _sync.Send(SyncMessage.Ping(id));
            }

            return;
        }

        // 宝が別のエリアにあるなら、まずテレポする。
        //
        // <b>ここを飛ばしてはいけない。</b>
        // 以前はいきなり Travelling に入れていたため、
        // 別のエリアにいるメンバーは歩いて向かおうとして、
        // たどり着けずに止まっていた。
        // リーダーと同じエリアにいるとは限らない。
        if (PlayerHelper.TerritoryType != _target.Value.TerritoryType)
        {
            RecordOnce($"member-tp-{_target.Value.TerritoryType}",
                $"宝は別のエリア（{_target.Value.PlaceName}）なので、テレポで向かいます");

            SetState(RunState.Teleporting);
            return;
        }

        // まだ着いていないなら、先に向かう。
        // 着いていないのに「着きました」と報告すると、全員が先へ進んでしまう。
        if (_destination == Vector3.Zero)
        {
            SetState(RunState.Travelling);
            return;
        }

        var range = ArrivalRange;
        if (!MovementHelper.ArrivalCheck(_destination, range))
        {
            SetState(RunState.Travelling);
            return;
        }

        // 着いたので報告する。届いたかどうかは分からないので、繰り返し送る。
        // 受け取る側は同じ相手からの報告を1回として数えるため、重複しても害はない。
        // 自分を見分ける名前が読めないうちは報告しない。
        //
        // 読めないときに別の値（プロセス番号など）で代用すると、
        // ログインの前後で名前が変わり、リーダー側で同じ機が2人分に数えられてしまう。
        var selfId = SelfId;
        if (!string.IsNullOrEmpty(selfId)
            && EzThrottler.Throttle("AutoTreasure.StepDone", 2000))
        {
            _sync.Send(SyncMessage.StepDone(selfId, _currentStep));
        }

        if (_pendingStep != null)
        {
            // リーダーの番号に合わせる（1つ進めるのではなく、同じ値にする）。
            _currentStep = _pendingStep.Value;
            _pendingStep = null;
            SetState(RunState.Digging);
            return;
        }

        _note = "合図を待っています";

        // リーダーが落ちているかもしれない。いつまでも待たず、
        // 一定時間で自分だけでも進む。取り残されるよりはよい。
        if (SecondsInState > WaitForPartyTimeoutSeconds)
        {
            Record("時間切れ: 合図が来ないので待たずに進みます");
            SetState(RunState.Digging);
        }
    }

    /// <summary>リーダーとして、仲間がそろうのを待つ。</summary>
    private void TickWaitingAsLeader()
    {
        if (_target == null)
        {
            _note = $"{_mapUser} から宝の場所が届くのを待っています";
            if (SecondsInState > WaitForPartyTimeoutSeconds)
            {
                // 座標が届かないなら、全機とも進めない。
                // 1台だけ止めても、残りは同じ場所で待ち続ける。
                Stop("宝の場所が届きませんでした", tellOthers: true);
            }
            return;
        }

        // 待っているあいだ、宝の場所を繰り返し伝える。
        //
        // 一度きりだと、そのとき繋がっていなかった機や、
        // 合図を取りこぼした機が、場所を知らないまま待ち続ける。
        // 実際、メンバー3台ともエーテライトから動かなかった。
        //
        // 受け取る側は同じ場所を何度受け取っても害がない。
        if (_target != null && EzThrottler.Throttle("AutoTreasure.ResendTreasure", 3000))
        {
            _sync.Send(SyncMessage.Treasure(
                _target.Value.TerritoryType, _target.Value.World));
        }

        // 待つ人数はパーティの人数から決める。4人パーティなら3人。
        //
        // ただしパーティの人数だけでは決めない。
        // 8人パーティでもプラグインを動かしているのは3台、ということがある。
        // 届かない合図を待ち続けても時間切れになるだけ。
        var expected = PartyRoleDetector.ResolveBarrierMembers(_sync.ConnectedClients);

        if (expected == 0)
        {
            // 待つ相手がいない設定。一人で進む。
            AdvanceFromBarrier();
            return;
        }

        if (_stepDoneFrom.Count >= expected)
        {
            AdvanceFromBarrier();
            return;
        }

        _note = $"仲間を待っています（{_stepDoneFrom.Count}/{expected}・接続 {_sync.ConnectedClients}）";

        // 誰かが来られなくなっている可能性もある。いつまでも止まらないよう、
        // 一定時間で先へ進む。
        if (SecondsInState > WaitForPartyTimeoutSeconds)
        {
            Record($"時間切れ: 仲間がそろいませんが進みます（{_stepDoneFrom.Count}/{expected}）");
            AdvanceFromBarrier();
        }
    }

    /// <summary>
    /// 待ち合わせを抜けて、次へ進む合図を出す。
    ///
    /// 段階の番号を1つ進めるのが要点。番号が変わらないと、
    /// 次の待ち合わせで「前回の合図」をそのまま食べてしまい、待たずに通過する。
    /// </summary>
    private void AdvanceFromBarrier()
    {
        _currentStep++;
        _sync.Send(SyncMessage.StepGo(_currentStep));
        _stepDoneFrom.Clear();
        SetState(RunState.Digging);
    }

    /// <summary>ディグを使う。</summary>
    private void TickDigging()
    {
        // 宝箱が出ていれば、もう掘る必要はない。
        if (ObjectHelper.GetNearestTreasure() != null)
        {
            SetState(RunState.OpeningChest);
            return;
        }

        // 掘るのは地図を使った本人だけ（ゲーム側の決まり）。
        if (!IsMapUser)
        {
            _note = "宝箱が出るのを待っています";

            // いつまでも出ないなら、その場にいても仕方がない。
            if (SecondsInState > 60)
                SetState(RunState.Failed);
            return;
        }

        MovementHelper.Dismount();

        if (!ActionHelper.CanDig())
        {
            _note = "掘れる位置まで近づいています";

            // 位置が合っていないのかもしれない。少し近づき直す。
            if (_destination != Vector3.Zero && SecondsInState > 5)
                MovementHelper.MoveTo(_destination, 1f, false);

            if (SecondsInState > 45)
            {
                _note = "掘れませんでした。位置が違う可能性があります";
                SetState(RunState.Failed);
            }
            return;
        }

        if (ActionHelper.Dig())
            _note = "掘っています";
    }

    /// <summary>宝箱を開ける。</summary>
    private void TickOpeningChest()
    {
        // 敵が出たら、先に倒す。
        if (PlayerHelper.InCombat || ObjectHelper.HasLivingEnemyWithin(EnemySearchRange))
        {
            MovementHelper.Stop();
            SetState(RunState.Fighting);
            return;
        }

        var chest = ObjectHelper.GetNearestTreasure();
        if (chest == null)
        {
            // 宝箱が無くなった。
            //
            // ここで「ロットが出ているか」を見て分岐してはいけない。
            // 開けた直後はまだ一覧に載っておらず、必ず「出ていない」と判定され、
            // ロットを飛ばしてしまう。
            //
            // 必ずロットの段階へ進み、そこで出そろうのを待つ。
            // 何も出なければ、その段階が時間で自然に終わる。
            SetState(RunState.Rolling);
            return;
        }

        var distance = ObjectHelper.DistanceToPlayer(chest);
        if (distance > 3f)
        {
            var moving = MovementHelper.MoveTo(chest.Position, 2f, false);
            _note = $"宝箱へ向かっています（{distance:F0} m）";

            // <b>近づけているのかどうかを残す。</b>
            // 実測では 12.5m の位置で止まったまま4分過ぎたが、
            // 記録が一行も出ず、何を試したのか分からなかった。
            RecordEvery("chest-approach", 5,
                $"宝箱へ近づいています（残り {distance:F1}m"
                + $" 指示={(moving ? "受理" : "拒否: " + MovementHelper.LastMoveRefusal)}"
                + $" 経路探索中={IPC.VNavmesh.PathfindInProgress}"
                + $" 経路移動中={IPC.VNavmesh.PathIsRunning}"
                + $" 移動可={MovementHelper.MovementAllowed}"
                + $" 宝箱={chest.Position}）");

            if (_stuck.Check())
            {
                MovementHelper.Stop();
                Record($"宝箱に近づけないので、経路を引き直します（残り {distance:F1}m）");
            }

            // それでも近づけないなら、最後の手段としてまっすぐ歩く。
            //
            // なぜ要るか（2026-09-22 実測）:
            //   地図役が地形の外（岩の上など）で掘ると、経路を引けず
            //   13.5m の位置から一歩も動けなかった。
            //   他の3人は宝箱の前で待っており、地図役だけが取り残される。
            //
            //   宝箱はすぐそこに見えている。経路が引けなくても、
            //   向きを合わせて歩けば届く距離。
            //
            // ⚠ 遠いうちはやらない。障害物を無視して突っ込むことになる。
            //   経路で近づけなかった近距離のときだけの逃げ道。
            if (!moving && distance <= DirectWalkRange && SecondsInState > DirectWalkAfterSeconds)
            {
                RecordEvery("chest-direct", 5,
                    $"経路の判定を飛ばして、宝箱へ直接向かわせます（残り {distance:F1}m）");

                IPC.VNavmesh.PathfindAndMoveCloseTo(chest.Position, false, 2f);
            }

            // 近づけないまま時間が過ぎたら、そのことを残して次へ。
            //
            // ここに時間切れが無かったため、3m以内に入れないと
            // <b>この枝から永久に出られなかった</b>。
            if (SecondsInState > ChestTimeoutSeconds)
            {
                Record($"宝箱へ近づけませんでした（残り {distance:F1}m）。次へ進みます");
                SetState(RunState.Rolling);
            }

            return;
        }

        MovementHelper.Stop();

        // 乗ったままでは触れない。開けると敵が湧くので、
        // ここで降りておけば戦闘にもそのまま入れる。
        MovementHelper.Dismount();

        // 宝箱を開けられるのは、地図を使った人だけ。
        //
        // <b>これは取り決めではなく、ゲーム側の制約。</b>
        // 地図を使っていない人が触れても「何も起きません」と出るだけで、
        // 画面がエラーで埋まる。
        // そばで降りて待っていれば、戦闘にはそのまま入れる。
        if (!IsMapUser)
        {
            _note = $"宝箱のそばで {_mapUser} を待っています";

            // <b>待ちきりにしない。</b>
            // この枝は下のタイムアウトより手前にあるので、
            // ここで return し続けると時間切れの判定に一生届かない。
            // 地図役が落ちた・地図役が届いていない等で、
            // 誰も開けないまま永久に立ち尽くすことになる。
            if (SecondsInState > ChestTimeoutSeconds)
            {
                Record("地図役が宝箱を開けないまま時間が過ぎました");
                SetState(RunState.Failed);
            }

            return;
        }

        ObjectHelper.InteractUntilNotTargetable(chest, "AutoTreasure.Chest");
        _note = "宝箱を開けています";

        // 触っても開かないまま時間が過ぎることがある。
        // （近づききれていない、他の人が開けている最中、など）
        if (SecondsInState > ChestTimeoutSeconds)
        {
            Svc.Log.Warning("宝箱を開けられませんでした。次へ進みます。");
            SetState(RunState.Rolling);
        }
    }

    /// <summary>敵を倒すのを待つ。</summary>
    private void TickFighting()
    {
        // 戦い方はこのプラグインでは決めない。
        // RotationSolver や BossMod といった、そのために作られたものに任せる。
        MovementHelper.Stop();

        // まずマウントから降りる。
        //
        // 乗ったままでは攻撃できない。RSR も BMR も何もできず、
        // 敵に近づくだけで棒立ちになる。実際そうなった。
        MovementHelper.Dismount();

        if (!PlayerHelper.InCombat && !ObjectHelper.HasLivingEnemyWithin(EnemySearchRange))
        {
            // 敵が0になった。ただし、すぐには終わりとみなさない。
            //
            // 魔紋の敵は何波かに分かれて湧く。前の波を倒しきってから
            // 次の波が出るまで、一瞬だけ0体になる時がある。
            // そこで打ち切ると、まだ敵が残っているのに宝箱へ戻ってしまう。
            //
            // 少し待って、それでも湧かなければ本当に終わり。
            _enemiesClearedAt ??= DateTime.UtcNow;

            if ((DateTime.UtcNow - _enemiesClearedAt.Value).TotalSeconds < EnemySettleSeconds)
            {
                _note = "次の敵が湧かないか確かめています";
                return;
            }

            // 倒し終えた。宝箱に戻る。
            _enemiesClearedAt = null;

            // 「AutoDuty」に上げていたなら、ふだんの設定に戻す。
            //
            // 上げたままだと、次の区画でも周りの敵を片端から狙い続ける。
            // 地図の敵だけ相手にしたいので、戦闘が終わったら必ず戻す。
            if (_combatRecoveryStage >= 2)
            {
                var preset = Plugin.Config.BmrPreset;
                if (!string.IsNullOrWhiteSpace(preset) && CombatPlugins.BmrAvailable)
                {
                    CombatPlugins.SetActivePreset(preset);
                    Record($"戦闘が終わったので、BMR を「{preset}」に戻します");
                }
            }

            _combatRecoveryStage = 0;
            _lastEnemyHp = null;
            _combatStalledSince = null;

            SetState(RunState.OpeningChest);
            return;
        }

        // まだ敵がいる。数え直しの起点を戻す。
        _enemiesClearedAt = null;

        // 死んでいたら戦いようがない。
        if (PlayerHelper.IsDead)
        {
            _note = "倒れました。蘇生を待っています";
            return;
        }

        var enemies = ObjectHelper.CountLivingEnemies(EnemySearchRange);
        _note = $"戦っています（敵 {enemies} 体）";

        // 狙う相手を決める。
        //
        // <b>RSR は TargetOnly なので、狙いが無いと撃たない。</b>
        // 以前は Manual にしていて、RSR が自分で相手を選ぶものと
        // 思い込んでいたが、実際には指示しない限り一切撃たなかった。
        //
        // 何も狙っていないときだけ入れる。
        // 毎フレーム入れ直すと、撃とうとしている最中に横から変えることになる。
        // 倒し終われば相手が消えるので、そこで次の敵に移る。
        EnsureEnemyTarget();

        // 戦えているかを見張る。
        WatchCombatProgress(enemies);

        // 勝てない相手だったり、遠くの敵が戦闘状態のままだったりすると、
        // いつまでも終わらない。一定時間で切り上げて、次にできることを探す。
        if (SecondsInState > FightTimeoutSeconds)
        {
            // 宝箱へ戻しても、同じ条件でまたここへ来るだけ。
            // 行き来を繰り返して見かけ上は動いている、という状態になる。
            Record("戦闘が終わりません。手で操作してください");
            _note = "戦闘が終わりません。手で操作してください";
            SetState(RunState.Failed);
        }
    }

    /// <summary>
    /// 戦闘が進んでいるかを見張り、止まっていれば立て直す。
    ///
    /// 見るのは「敵の残り体力の合計」。これが減っていれば戦えている。
    /// 敵がいるのに、一定時間まったく減らないなら、何かが働いていない。
    ///
    /// 体力を見るのは、次の理由による:
    ///   ・「戦闘中か」だけでは、殴られているだけの状態と区別できない
    ///   ・「攻撃したか」を見ると、詠唱の長い職や回復役で誤検知する
    ///   ・体力が減っていれば、誰が倒していようと前には進んでいる
    ///
    /// 立て直しは、設定し直すだけにとどめる。
    /// プリセットを頻繁に切り替えると、かえって動きが乱れるため、
    /// 一度だけ試して、それでも駄目なら人に任せる。
    /// </summary>
    private void WatchCombatProgress(int enemies)
    {
        if (!Plugin.Config.ManageCombatPlugins)
            return;

        if (enemies <= 0)
        {
            _combatStalledSince = null;
            return;
        }

        var totalHp = ObjectHelper.TotalEnemyHp(EnemySearchRange);

        // 体力が変わった＝何かが起きている。
        //
        // 「減った」だけを見てはいけない。敵は何波かに分かれて湧くので、
        // 新しい波が出ると合計は<b>増える</b>。増えたときに覚え直さないと、
        // 以後ずっと「減っていない」と判断され続ける。
        //
        // 実際、敵を全滅させたあとも「戦闘が進みません」と誤判定した。
        if (_lastEnemyHp == null || totalHp != _lastEnemyHp.Value)
        {
            _lastEnemyHp = totalHp;
            _combatStalledSince = null;
            return;
        }

        _combatStalledSince ??= DateTime.UtcNow;

        var stalled = (DateTime.UtcNow - _combatStalledSince.Value).TotalSeconds;
        if (stalled < CombatStallSeconds)
            return;

        // ここから先は「敵がいるのに、体力がまったく減っていない」状態。
        //
        // 二段構えで立て直す。
        //   1回目: 同じ設定を入れ直す（何かの拍子に外れた場合に戻る）
        //   2回目: プリセットを「AutoDuty」に上げる
        //
        // 「AutoDuty」は周りの敵を片端から狙いに行く。
        // ふだんは地図の敵だけ相手にしたいので使わないが、
        // まったく攻撃しない状態から抜け出すには確実。
        // 止まったままでいるより、多少よけいな敵を巻き込む方がまし。
        switch (_combatRecoveryStage)
        {
            case 0:
                _combatRecoveryStage = 1;
                Record($"{stalled:F0} 秒間ダメージが入っていません。戦闘プラグインを設定し直します");
                ApplyCombatSettings();
                break;

            case 1:
                _combatRecoveryStage = 2;

                var fallback = Plugin.Config.BmrFallbackPreset;
                if (!string.IsNullOrWhiteSpace(fallback) && CombatPlugins.BmrAvailable)
                {
                    if (CombatPlugins.SetActivePreset(fallback))
                        Record($"それでも進まないので、BMR を「{fallback}」に切り替えます");
                    else
                        Record($"BMR のプリセット「{fallback}」が見つかりませんでした");
                }

                // RSR も念のため入れ直す。
                if (CombatPlugins.RsrAvailable)
                    CombatPlugins.SetRsrMode(CombatPlugins.RsrMode.TargetOnly);
                break;

            default:
                RecordOnce("combat-stall-give-up",
                    "戦闘が進みません。RSR と BMR の設定を確認してください");
                _note = "戦闘が進みません。手で確認してください";
                return;
        }

        _combatStalledSince = null;
    }

    /// <summary>ロットを処理する。</summary>
    private void TickRolling()
    {
        if (!Plugin.Config.AutoRoll && !IPC.LazyLootControl.ShouldYield)
        {
            // 自動ロットOFFでも、魔紋の出現を待って入口判定へ進む。
            if (SecondsInState > RollSettleSeconds)
                FinishFieldPhase();
            return;
        }

        var option = LootHelper.OptionFor(Plugin.Config.Role);
        var isNeed = option == FFXIVClientStructs.FFXIV.Client.Game.UI.RollResult.Needed;

        if (LootHelper.HasPendingLoot())
        {
            // LazyLoot が入っているなら、押すのはそちらに任せて待つだけにする。
            //
            // LazyLoot はわざと間を置いてから押す（既定で 1.5〜3.0 秒）。
            // こちらが先に押してしまうと取り合いになるので、手を出さない。
            // ⚠ 譲ったまま誰も押さないことがある（2026-09-22 実測）。
            //   魔紋の中と同じで、待っても押されないならこちらが押す。
            var yieldToLazy = IPC.LazyLootControl.ShouldYield
                           && SecondsInState < LazyLootGraceSeconds;

            if (yieldToLazy)
            {
                _note = "ロット中（LazyLoot に任せています）";

                // 任せているあいだ、実際のロットの中身を残す。
                //
                // <b>1台だけ自動ロットされない</b>という報告があり、
                // 設定は4台とも同一だと分かっている（診断で確認ずみ）。
                // 残るのは「その台にロットが見えているか」「どの状態か」。
                // それはここでしか分からない。
                RecordEvery("roll-yield", 5,
                    $"ロット待ち（LazyLoot に任せて {SecondsInState:F0}秒）"
                    + $" 中身:{Helpers.LootHelper.Diagnostic()}");
            }
            else
            {
                // LazyLoot に譲ったが押されなかった場合は、そのことを残す。
                if (IPC.LazyLootControl.ShouldYield)
                {
                    RecordEvery("roll-takeover-field", 10,
                        $"LazyLoot が {SecondsInState:F0}秒 押さないので、こちらでロットします");
                }

                // 1回につき1件ずつ処理される。毎フレーム呼ばれるので順に片付く。
                LootHelper.RollPending(option);
                _note = isNeed ? "ロット中（Need）" : "ロット中（Pass）";
            }

            // 何かの理由で処理しきれない場合に備えて、上限を設ける。
            // 受け皿が働くので、譲ったまま長く待つ必要はない。
            var timeout = yieldToLazy ? LootWaitGiveUpSeconds : RollTimeoutSeconds;
            if (SecondsInState > timeout)
            {
                // 何が残ったまま諦めたのかを残す。
                Record($"ロットが終わりませんでした（{SecondsInState:F0}秒）。次へ進みます"
                    + $" 任せ先={(IPC.LazyLootControl.ShouldYield ? "LazyLoot" : "自分")}"
                    + $" 中身:{Helpers.LootHelper.Diagnostic()}");

                FinishFieldPhase();
            }
            return;
        }

        // 出そろうまで少し待つ。宝箱を開けた直後は、まだ一覧に載っていないことがある。
        if (SecondsInState > RollSettleSeconds)
            FinishFieldPhase();
    }

    /// <summary>
    /// フィールドでの用事が済んだあと、次にどうするか決める。
    ///
    /// 宝箱を開け終わると「転送魔紋」が出る。これに入ると宝物庫へ進む。
    /// 以前はここで「完了」にしてしまい、目の前に魔紋があっても
    /// 何もせずに止まっていた。実際、1.3m 先にあるのに 13 秒間動かなかった。
    /// </summary>
    private void FinishFieldPhase()
    {
        if (VaultRoutine.FindEntryPortal() != null)
        {
            SetState(RunState.EnteringVault);
            return;
        }

        SetState(RunState.Completed);
    }

    /// <summary>
    /// フィールドの「転送魔紋」に入って、宝物庫へ進む。
    /// </summary>
    private void TickEnteringVault()
    {
        // 入る前のエリアを覚えておく（手順⓪）。
        // 出たときに、ここへ戻っていれば「魔紋から出た」と判断できる。
        if (_territoryBeforeVault == 0 && !VaultRoutine.IsInsideVault())
        {
            _territoryBeforeVault = PlayerHelper.TerritoryType;
            Record($"魔紋に入る前のエリア: {_territoryBeforeVault}");
        }

        // 入れたかどうかは CheckVaultEntry が見ている。
        // ここではエリアが変わるまで、魔紋に近づいて触れ続ける。
        var portal = VaultRoutine.FindEntryPortal();

        if (portal == null)
        {
            // 魔紋が消えた。時間切れで閉じたか、すでに入ったあと。
            if (SecondsInState > PortalWaitSeconds)
            {
                _note = "転送魔紋が見つかりませんでした";
                SetState(RunState.Completed);
            }
            return;
        }

        var distance = ObjectHelper.DistanceToPlayer(portal);

        if (distance > InteractRange)
        {
            _note = $"転送魔紋へ向かっています（あと {distance:F0}y）";
            MovementHelper.MoveTo(portal.Position, InteractRange * 0.5f, fly: false);
            return;
        }

        MovementHelper.Stop();

        // 転送魔紋に触れるのも、地図を使った人だけ。
        //
        // 触れるのは1人でよく、その人が入れば仲間も一緒に運ばれる。
        // 地図を使っていない人が触れると「何も起きません」と出るだけで、
        // 画面にエラーが並ぶ（実測 2026-09-18）。
        // そばまで来て待っていれば、一緒に入れる。
        if (!IsMapUser)
        {
            _note = $"転送魔紋のそばで {_mapUser} を待っています";

            // ここも待ちきりにしない（上のタイムアウトより後ろにあるため）。
            if (SecondsInState > PortalWaitSeconds)
            {
                Record("地図役が魔紋に入らないまま時間が過ぎました");
                SetState(RunState.Failed);
            }

            return;
        }

        // 乗ったままでは触れない。
        //
        // 触る前に必ず降りる。降りないと、こちらは触ったつもりでも
        // ゲームに弾かれ、「何も起きません」が出るだけで先へ進まない。
        MovementHelper.Dismount();

        _note = "転送魔紋に入っています";
        ObjectHelper.InteractUntilNotTargetable(portal, "AutoTreasure.EntryPortal", 1000);

        if (SecondsInState > PortalWaitSeconds)
        {
            Svc.Log.Warning("転送魔紋に入れませんでした。手で操作してください。");
            _note = "転送魔紋に入れません。手で操作してください";
            SetState(RunState.Failed);
        }
    }

    /// <summary>魔紋の中を探す。</summary>
    /// <summary>
    /// 1周終わったあと、続けて次の周回を始める。
    ///
    /// 「完了」は <see cref="IsRunning"/> が false なので、
    /// 状態機械には流れてこない。ここで面倒を見る。
    ///
    /// 続けて回すかどうかは利用者の設定による（既定は続ける）。
    /// 地図が尽きたら止める。持っていないのに回し続けても意味がない。
    /// </summary>
    private void TickCompleted()
    {
        if (_state != RunState.Completed)
        {
            // 「完了」から離れたら、数えた時刻を捨てる。
            _completedAt = null;
            return;
        }

        if (!Plugin.Config.ContinuousRuns)
        {
            if (!VaultRoutine.IsInsideVault()) _cbt.End();
            _note = "1周終わりました";
            return;
        }

        // リーダーだけが次の周回を始める。
        //
        // メンバーが勝手に始めると、合図より先に走り出して足並みが乱れる。
        // メンバーはリーダーの合図（Begin）で始まる。
        if (Plugin.Config.Role == ClientRole.Member)
        {
            _note = "リーダーの合図を待っています";
            return;
        }

        _completedAt ??= DateTime.UtcNow;

        var waited = (DateTime.UtcNow - _completedAt.Value).TotalSeconds;

        if (waited < Plugin.Config.ContinuousRunDelaySeconds)
        {
            _note = $"次の周回まで {Plugin.Config.ContinuousRunDelaySeconds - waited:F0} 秒";
            return;
        }

        // 落ち着くまでは始めない。
        // ムービーやエリア移動の途中で始めると、最初の手順が通らない。
        if (!PlayerHelper.IsReady)
        {
            _note = "次の周回に備えています";
            return;
        }

        // ロットが残っていれば、先に片付ける。
        // 取り残したまま次の地図を使うと、戦利品を捨てることになる。
        //
        // ただし、いつまでも待たない。
        // LazyLoot に任せている場合、向こうの設定によっては
        // 特定の品を「押さない」ことがある（装備不可のものを飛ばす等）。
        // その品は一覧に残り続けるので、待ち続けると次の周回に入れない。
        RollIfPending();
        if (LootHelper.HasPendingLoot() && SecondsInState <= LootWaitGiveUpSeconds)
        {
            _note = "戦利品を片付けています";
            return;
        }

        // 在庫は選ばれた本人が解読済み地図とともに確認する。
        // リーダーだけ先に未解読枚数で判断すると、再開時の地図を見落とす。
        _lapsDone++;
        _mapUser = "";
        _completedAt = null;
        Start();
    }

    /// <summary>
    /// 高さを見て、階層が変わっていないか確かめる。
    ///
    /// <b>これが「階層が変わった」の確かな合図になる。</b>
    /// これまでは「1フレームで 20y 以上動いたか」で当てていたが、
    /// それは搬送が終わったあとにしか分からず、見落とすこともあった。
    /// 高さなら、その場でいつでも確かめられる。
    ///
    /// 変わっていたら、地形を作り直させて、
    /// 入れ替わるまで動かないようにする。
    /// </summary>
    private void CheckFloorChange()
    {
        var height = VaultRoutine.CurrentHeight();

        if (height == null)
        {
            // 魔紋の外。覚えていた高さは捨てる。
            _floorHeight = null;
            _floorSamples = 0;
            _leavingFloor = false;
            _landingSince = null;
            _lastHeightSeen = null;
            _lastGroundPosition = null;
            _landingAnchor = null;
            _lastRoomIndex = 0;

            // 魔紋の外では、移動を禁じたままにしない。
            // 閉じたまま残すと、フィールドで一歩も動けなくなる。
            MovementHelper.Allow();
            return;
        }

        var y = height.Value;
        var here = PlayerHelper.Position;

        // 水平方向の瞬間移動も「区画を移った」とみなす。
        //
        // <b>高さだけでは足りない。</b>
        // 実測（2026-09-18 14:10 リーダー）:
        //   第3区画から第4区画へワープしたとき、
        //   水平に 413y 動いたのに高さは -167.8 のまま変わらなかった。
        //   高さの見張りは反応せず、前の区画の地形のまま走り出し、
        //   壁へ向かって約20秒走り続けた。
        //
        // 第1区画→第2区画も同じで、どちらも高さ -400 のまま
        // Z だけが 377 → 192 へ動く。
        //
        // 走っていて 1フレームに 30y も進むことはない。
        // 瞬間移動と見分けられる。
        //
        // <b>ただし、これだけでは取り逃がす。</b>
        // 実測（2026-09-18 14:21）では、360y のワープが
        // 細かく分けて伝わり、1フレームあたりの差が小さかった。
        // そのため「跳んだ」と気づけないことがある。
        //
        // そこで、区画そのものが変わっていないかも見る。
        // 区画は座標から分かる（決まった場所に決まったものがある）ので、
        // 跳び方に関わらず「別の区画に来た」ことに気づける。
        var roomNow = VaultRoutine.CurrentRoom()?.Index ?? 0;

        if (roomNow != 0 && _lastRoomIndex != 0 && roomNow != _lastRoomIndex && !_leavingFloor)
        {
            Record($"区画が変わりました（第{_lastRoomIndex}区画 → 第{roomNow}区画）");
            Record("経路と移動を止めます（地形の作り直しは落ち着いてから）");

            _lastRoomIndex = roomNow;
            _leavingFloor = true;
            _landingSince = null;
            _lastHeightSeen = y;
            _lastGroundPosition = here;
            _landingAnchor = here;

            // どこからの指示でも動かないようにする。
            // 止めた直後に別の処理が MoveTo を呼べてしまうため、
            // 入口そのものを閉じる。
            MovementHelper.Block("区画を移っています");

            VNavmesh.PathStop();
            MovementHelper.Stop();
            ForgetRoom();

            if (_state == RunState.VaultTransition)
                SetState(RunState.VaultExploring);

            return;
        }

        if (roomNow != 0)
            _lastRoomIndex = roomNow;

        if (_lastGroundPosition != null)
        {
            var moved = Vector2.Distance(
                new Vector2(here.X, here.Z),
                new Vector2(_lastGroundPosition.Value.X, _lastGroundPosition.Value.Z));

            if (moved >= WarpJumpDistance && !_leavingFloor)
            {
                Record($"区画を移りました（水平に {moved:F0}y 跳びました）");
                Record("経路と移動を止めます（地形の作り直しは落ち着いてから）");

                _leavingFloor = true;
                _landingSince = null;
                _lastHeightSeen = y;
                _lastGroundPosition = here;
                _landingAnchor = here;

                MovementHelper.Block("区画を移っています");

                VNavmesh.PathStop();
                MovementHelper.Stop();
                ForgetRoom();

                if (_state == RunState.VaultTransition)
                    SetState(RunState.VaultExploring);

                return;
            }
        }

        _lastGroundPosition = here;

        if (_floorHeight == null)
        {
            // 初めて高さが分かった。ここを基準にする。
            _floorHeight = y;
            _floorSamples = 1;
            _floorSpread = 0f;
            return;
        }

        var diff = MathF.Abs(y - _floorHeight.Value);

        // <b>この階層の高さを測り続ける。</b>
        //
        // 宝箱を開ける・戦う・扉へ向かう——そのあいだ、
        // 自分がいる高さを平均で持っておく。
        // 実測では、同じ階層の中の振れは 1.2y ほどしかない。
        //
        // 平均を持つのは、床のわずかな起伏で基準がぶれないようにするため。
        // 1点だけを基準にすると、たまたま段の上で測った値が
        // 基準になってしまう。
        // ---- 段階1: まだこの階層にいる ----------------------------------
        if (diff < FloorLeaveThreshold && !_leavingFloor)
        {
            _floorSamples++;

            // 移動平均。新しい値を少しずつ混ぜる。
            _floorHeight = _floorHeight.Value + (y - _floorHeight.Value) / MathF.Min(_floorSamples, 60);

            // 振れ幅も覚えておく。記録に出して、あとで確かめられるようにする。
            if (diff > _floorSpread)
                _floorSpread = diff;

            return;
        }

        // ---- 段階2: 階層を離れた（運ばれている最中）----------------------
        //
        // <b>ここで地形を作り直させてはいけない。</b>
        //
        // vnavmesh の Reload は「今そこにある地形」から作る
        // （NavmeshManager.cs: scene.FillFromActiveLayout()）。
        // 運ばれている最中はまだ前の階層の上にいるので、
        // 頼んでも前の階層の地形をもう一度読み込むだけになる。
        // しかも「作り直したばかり」なので正常に見えてしまい、
        // かえって質が悪い。
        //
        // 加えて、Reload は中でムービーが終わるまで待つ
        // （while (InCutscene)）。搬送はムービーを伴うので、
        // 早く頼んでも実際の読み込みは着地後まで始まらない。
        //
        // <b>いま止めるべきは地形ではなく「経路」。</b>
        // 走り出す原因は、古い地形の上にすでに引かれた経路なので、
        // それを止めれば暴走は止まる。
        if (!_leavingFloor)
        {
            _leavingFloor = true;
            MovementHelper.Block("区画を移っています");

            Record($"階層を離れました（この階層の高さ {_floorHeight.Value:F1}"
                 + $"・振れ {_floorSpread:F1}y → 今 {y:F1}・差 {diff:F0}y）");
            Record("経路と移動を止めます（地形の作り直しは着地してから）");

            // 経路を止める。これが暴走を止める本体。
            VNavmesh.PathStop();
            MovementHelper.Stop();

            // 覚えていた位置も、ここで捨てる。
            // 前の階層の宝箱・扉の位置は、着いた先では誤りになる。
            ForgetRoom();

            // 「扉の先へ歩いている」途中なら、そこから抜ける。
            if (_state == RunState.VaultTransition)
                SetState(RunState.VaultExploring);

            _landingSince = null;
            _lastHeightSeen = y;
            return;
        }

        // ---- 段階3: 着地を待つ ------------------------------------------
        //
        // 高さが動かなくなったら着いたとみなす。
        // 着いてから地形を作り直させる。ここで初めて Reload が意味を持つ。
        _note = "次の階層へ運ばれています";

        // 動いているあいだは、止め続ける。
        // 前の階層の経路が生き返らないようにする。
        VNavmesh.PathStop();
        MovementHelper.Stop();

        // 着地したかは、<b>止まり始めた地点からどれだけ離れたか</b>で見る。
        //
        // 直前のフレームと比べてはいけない。
        // 60fps で毎秒 6y 進んでいても、1フレームでは 0.1y しか動かない。
        // 「1フレームの差が小さい」を静止とみなすと、
        // <b>1秒で 6y 動いているのに「1秒静止した」と判定してしまう。</b>
        // （ChatGPT の指摘で気づいた。計算でも確かめた）
        //
        // そこで、止まり始めた地点を固定して覚え、
        // そこから一定より離れたら測り直す。
        // これなら、ゆっくり動き続けている間は決して成立しない。
        var anchor = _landingAnchor;

        var movedFromAnchor = anchor == null
            || MathF.Abs(y - anchor.Value.Y) > LandingStillTolerance
            || Vector2.Distance(
                   new Vector2(here.X, here.Z),
                   new Vector2(anchor.Value.X, anchor.Value.Z))
               > LandingStillTolerance;

        if (movedFromAnchor)
        {
            // まだ動いている。ここを新しい起点にして測り直す。
            _landingAnchor = here;
            _landingSince = null;
            return;
        }

        _landingSince ??= DateTime.UtcNow;

        if ((DateTime.UtcNow - _landingSince.Value).TotalSeconds < LandingStillSeconds)
            return;

        // 落ち着いた。ここが新しい階層。
        _leavingFloor = false;
        _landingSince = null;
        _landingAnchor = null;
        _floorHeight = y;
        _floorSamples = 1;
        _floorSpread = 0f;

        _landingAnchor = null;

        Record($"次の区画に着きました（高さ {y:F1}）。地形を作り直します");

        // ここで初めて地形を捨てて作り直す。
        DiscardFloorKnowledge();

        // 地形ができるまでは、まだ動かさない。
        // 許すのは「地形が入れ替わりました」を出すところ（TickVaultExploring）。
    }

    /// <summary>
    /// 階層が変わったときに、前の階層のことを捨てて作り直す。
    ///
    /// 前の階層の地形・経路・覚えた位置は、新しい階層では
    /// すべて誤りになる。使うとまったく違う方向へ歩き出すので、
    /// 残さず捨てる。
    ///
    /// 捨てたあとは、今いる高さの周りを見直して情報を入れ直す。
    /// </summary>
    private void DiscardFloorKnowledge()
    {
        // 1. 動きと経路を止める。
        //    前の階層の地形で引かれた経路が残っていると、
        //    着いた瞬間に戻ろうとして走り出す。
        VNavmesh.PathStop();
        MovementHelper.Stop();

        // 2. 地形を作り直させる。
        //    エリア番号が変わらないので、頼まなければ作り直されない。
        VNavmesh.Reload();

        // 3. 覚えていた位置を捨てる。
        //    宝箱・扉・歩き出しの起点は、どれも前の階層のもの。
        ForgetRoom();

        // 4. 地形ができるまで動かない。
        _meshSettleUntil = DateTime.UtcNow.AddSeconds(MeshSettleSeconds);

        // 5. 周りを見直す。
        //    ここで数えておくと、記録を見たときに
        //    「新しい階層で何が見えているか」がすぐ分かる。
        var (targetable, total) = ObjectHelper.CountEventObjects();
        var chest = VaultRoutine.FindAnyChest();
        var door = VaultRoutine.FindDoor();

        Record($"この階層を見直します（仕掛け {targetable}/{total} 件"
             + $"・宝箱 {(chest != null ? "あり" : "なし")}"
             + $"・扉 {(door != null ? "あり" : "なし")}）");

        // 6. 部屋の判断もやり直す。
        //    前の階層で「何も見えない」と数えていた時間は、
        //    新しい階層では意味がない。
        _emptyRoomSince = null;
        _shakeCount = 0;
        _vaultStuckSince = null;
        _warpOnlySince = null;
        _memberDoorWaitSince = null;
        _doorAttempts = 0;
        _combatEndedAt = null;

        // 7. 「1回だけ書く」印を消す。
        //
        //    これを消さないと、2階層目以降で同じことが起きても
        //    記録に残らない。実際、階層ごとの様子を追えなくなる。
        //    階層ごとに書き直したいものだけを消す。
        _recordedOnce.Remove("shared-door-go");
        _recordedOnce.Remove("shared-door-arrived");
        _recordedOnce.Remove("warp-wait");
        _recordedOnce.Remove("warp-ride");
        _recordedOnce.Remove("empty-but-present");
        _recordedOnce.Remove("transition-center");
        _recordedOnce.Remove("mesh-wait");

        // 8. 「扉の先へ歩いている」途中なら、そこから抜ける。
        //
        //    その歩きは前の階層の向きへのものなので、
        //    続けると新しい階層で見当違いの方向へ進む。
        //    新しい階層を一から見直すところへ戻す。
        if (_state == RunState.VaultTransition)
            SetState(RunState.VaultExploring);
    }

    /// <summary>
    /// 仕掛けが見えなくても、決まった場所へ向かう。向かったら true。
    ///
    /// <b>魔紋の中は座標が決まっている。</b>
    /// 185本の記録を調べたところ、宝箱・扉・HIGH/LOW の 20 個すべてが
    /// 常に同じ座標だった。だから「見えないから動けない」は要らない。
    ///
    /// 向かう順番は、見えているときと同じ考え方にする。
    ///   1. まだ宝箱があるなら宝箱へ
    ///   2. 宝箱が終わっていれば扉へ
    ///
    /// 近づけば仕掛けが見えるようになり、通常の流れに戻る。
    /// </summary>
    private bool TryGoToKnownSpot()
    {
        if (!VNavmesh.NavIsReady)
            return false;

        var room = VaultRoutine.CurrentRoom();
        if (room == null)
            return false;

        var r = room.Value;

        // 宝箱がまだ開いていないなら、そこへ。
        //
        // 「開いたか」は、その場所に近づいたかで決める。
        // 触れる宝箱が見えていればそちらが優先されるので、
        // ここへ来るのは「見えていない」ときだけ。
        var target = r.Chest;
        var what = "宝箱";

        if (_chestBaseId == r.ChestId || _roomChestDone)
        {
            // この区画の宝箱はもう済んでいる。扉へ。
            target = _useRightDoor ? r.RightDoor : r.LeftDoor;
            what = _useRightDoor ? "右の扉" : "左の扉";
        }

        var distance = ObjectHelper.DistanceToPlayer(target);

        // 着いたのに何も見えないなら、ここでは何もできない。
        // 動いて確かめる方に任せる。
        if (distance <= 5f)
            return false;

        RecordOnce($"known-spot-{r.Index}-{what}",
            $"第{r.Index}区画の{what}へ向かいます（決まった場所・{distance:F0} m）");

        MovementHelper.MoveTo(target, 3f, false);
        _note = $"第{r.Index}区画の{what}へ向かっています（{distance:F0} m）";

        if (_stuck.Check())
            MovementHelper.Stop();

        return true;
    }

    /// <summary>
    /// 魔紋の途中から始めたとき、そこまでの経過を周りから推し量る。
    ///
    /// <b>開始を押すと、覚えていたことは全部捨てられる。</b>
    /// 新しい周回の始まりとして扱うためで、普段はそれでよい。
    ///
    /// ただし魔紋の途中で押した場合、
    ///   ・この区画の宝箱を開け終わったか
    ///   ・どちらの扉が開いたか
    /// が分からなくなり、宝箱を探しに戻ったり、
    /// 開いていない側の扉へ向かったりする。
    ///
    /// これらは目の前を見れば分かる。推し量るのではなく、事実から決める。
    ///   ・その区画の宝箱が見当たらない → 開け終わっている
    ///   ・扉が片方しか無い → 残っている方が「まだ開いていない扉」で、
    ///     消えた方が開いた扉
    /// </summary>
    private void RecoverVaultProgress()
    {
        if (!VaultRoutine.IsInsideVault())
            return;

        var room = VaultRoutine.CurrentRoom();
        if (room == null)
            return;

        var r = room.Value;

        Record($"魔紋の途中から始めました（第{r.Index}区画）。周りを見て続きを決めます");

        // 宝箱が残っているか。
        //
        // 開けた宝箱は、オブジェクトごと消える（利用者に確認済み）。
        // 見当たらなければ開け終わっている。
        var chest = ObjectHelper.GetEventObjects()
            .FirstOrDefault(o => o.BaseId == r.ChestId);

        if (chest == null)
        {
            _roomChestDone = true;
            _chestBaseId = r.ChestId;
            _chestPosition = r.Chest;
            Record($"第{r.Index}区画の宝箱は開け終わっているとみなします");
        }
        else
        {
            Record($"第{r.Index}区画の宝箱がまだあります");
        }

        // どちらの扉が開いたか。
        //
        // 開いた扉は「触れなくなる」。
        //
        // 触れるものだけを数える（GetEventObjects）。
        // 触れないものまで含めると、開いた扉も「まだある」と数えてしまい、
        // 「両方とも残っている」と誤って判断する。
        //
        // 両方あるならまだどちらも開いていない。
        // 片方だけ触れなくなっていれば、そちらが開いた扉。
        var doors = ObjectHelper.GetEventObjects()
            .Where(VaultRoutine.IsDoor)
            .Select(o => o.BaseId)
            .ToHashSet();

        var leftGone  = !doors.Contains(r.LeftDoorId);
        var rightGone = !doors.Contains(r.RightDoorId);

        if (leftGone && !rightGone)
        {
            _useRightDoor = false;
            _doorAccessPosition = r.LeftDoor;
            Record($"左の扉（{r.LeftDoorId}）が開いています。その先へ進みます");
        }
        else if (rightGone && !leftGone)
        {
            _useRightDoor = true;
            _doorAccessPosition = r.RightDoor;
            Record($"右の扉（{r.RightDoorId}）が開いています。その先へ進みます");
        }
        else if (leftGone && rightGone)
        {
            // 両方とも触れない。
            //
            // <b>これは扉を開けたあとの普通の状態。</b>
            // 開いた扉は触れなくなるが、開いていない側も
            // 「もう選べない」ので同じく触れなくなる（利用者に確認）。
            // つまり、触れるかどうかでは左右を見分けられない。
            //
            // そこで<b>自分が今どちらの扉のそばに立っているか</b>で決める。
            // 扉を開けた人はその扉の前にいるはずで、
            // 左右の扉は約 50y 離れているため、取り違えようがない。
            //
            // ワープ床が見えていれば、そちらを優先して信じる。
            // 開いた扉の先にしか現れないため、これが一番確かな手がかり。
            var warp = VaultRoutine.FindWarp();

            if (warp != null)
            {
                var warpToLeft  = Vector3.Distance(warp.Position, r.LeftDoor);
                var warpToRight = Vector3.Distance(warp.Position, r.RightDoor);

                _useRightDoor = warpToRight < warpToLeft;
                _doorAccessPosition = _useRightDoor ? r.RightDoor : r.LeftDoor;

                Record($"ワープ床が見えるので、{(_useRightDoor ? "右" : "左")}の扉が開いたとみなします");
            }
            else
            {
                var toLeft  = ObjectHelper.DistanceToPlayer(r.LeftDoor);
                var toRight = ObjectHelper.DistanceToPlayer(r.RightDoor);

                _useRightDoor = toRight < toLeft;
                _doorAccessPosition = _useRightDoor ? r.RightDoor : r.LeftDoor;

                Record($"扉は両方とも触れません。自分が近い{(_useRightDoor ? "右" : "左")}の扉が"
                     + $"開いたとみなします（左まで {toLeft:F0}m / 右まで {toRight:F0}m）");
            }
        }
        else
        {
            Record("扉は両方とも残っています。まだ開いていないとみなします");
        }
    }

    /// <summary>
    /// 扉のそばまで来たのに何も見えないとき、その先へ進む。進んだら true。
    ///
    /// <b>扉が開いたあとは、扉そのものが消えている。</b>
    /// リーダーが先に通ると、メンバーが着いたときには扉が無い。
    /// そのまま扉を探し続けると、開いた扉の前で止まったままになる。
    /// 実測（2026-09-18 13:52）では、左右の扉を行き来して30秒を失った。
    ///
    /// 扉の座標は分かっているので、そこから先へ進めばワープ床に乗れる。
    /// 進む向きは「宝箱 → 扉」。どちらも決まった場所なので計算できる。
    /// </summary>
    private bool TryWalkPastKnownDoor()
    {
        if (!VNavmesh.NavIsReady)
            return false;

        var room = VaultRoutine.CurrentRoom();
        if (room == null)
            return false;

        var r = room.Value;

        // 宝箱がまだ残っているなら、先に進んではいけない。
        if (!_roomChestDone && _chestBaseId != r.ChestId)
            return false;

        // どちらの扉を通るかは、リーダーが実際に使った扉を優先する。
        //
        // <b>_useRightDoor だけを見てはいけない。</b>
        // 光った扉が右だった場合、リーダーは最初から右へ向かうため、
        // 「左が開かないので右を試します」という切り替えが起きない。
        // その切り替えでしか仲間に左右を伝えていなかったので、
        // メンバーは左のままになり、開いていない左の扉へ走っていた。
        // 実測（2026-09-18・利用者の報告）でそうなった。
        //
        // 扉の位置は DataId つきで届いている。ID の偶数・奇数で
        // 左右が分かるので、そちらを信じる。
        var door = _useRightDoor ? r.RightDoor : r.LeftDoor;

        if (_sharedDoorId >= VaultRoutine.DoorFirstId
            && _sharedDoorId <= VaultRoutine.DoorLastId)
        {
            var sharedIsRight = _sharedDoorId % 2 != 0;

            if (sharedIsRight != _useRightDoor)
            {
                _useRightDoor = sharedIsRight;
                Record($"リーダーが使ったのは{(sharedIsRight ? "右" : "左")}の扉"
                     + $"（{_sharedDoorId}）なので、そちらへ進みます");
            }

            door = sharedIsRight ? r.RightDoor : r.LeftDoor;
        }

        // 扉のそばに来ていないなら、まずそちらへ。
        if (ObjectHelper.DistanceToPlayer(door) > 8f)
            return false;

        // 「宝箱 → 扉」の向きへ、扉からさらに進む。
        var direction = door - r.Chest;
        direction.Y = 0f;

        if (direction.LengthSquared() < 0.01f)
            return false;

        direction = Vector3.Normalize(direction);

        var goal = door + direction * WalkPastDoorDistance;

        // 床の上へ寄せる。地形の外を指すと、指示が静かに失敗する。
        var onFloor = VNavmesh.PointOnFloor(goal, false, 5f);
        if (onFloor != null)
            goal = onFloor.Value;

        RecordOnce($"past-door-{r.Index}",
            $"第{r.Index}区画: 扉が開いているので、その先へ進みます（{WalkPastDoorDistance:F0}y）");

        // 歩き出しの起点を覚える。搬送されたかを距離で見分けるため。
        _walkStartPosition ??= PlayerHelper.Position;

        if (VNavmesh.PathIsRunning || VNavmesh.PathfindInProgress)
        {
            _note = "扉の先へ進んでいます";
            return true;
        }

        if (EzThrottler.Throttle("AutoTreasure.PastDoor", 1000))
            MovementHelper.MoveTo(goal, 1f, false);

        _note = "扉の先へ進んでいます";
        return true;
    }

    /// <summary>
    /// リーダーから届いた扉の位置へ向かう。向かったら true。
    ///
    /// メンバーが扉を見つけられないときの逃げ道。
    /// 扉が見えなくても、位置が分かっていれば近づける。
    /// 近づけば見えるようになり、通常の流れに戻れる。
    /// </summary>
    private bool TryGoToSharedDoor()
    {
        if (IsMapUser)
            return false;

        if (_sharedDoorPosition == null)
            return false;

        // 自分でも扉が見えているなら、こちらは使わない。
        // 見えているものを優先する方が確かなため。
        if (VaultRoutine.FindDoor() != null)
            return false;

        if (!VNavmesh.NavIsReady)
            return false;

        var distance = ObjectHelper.DistanceToPlayer(_sharedDoorPosition.Value);

        // 着いても見えないなら、位置が古い。捨てて普通の探し方に戻す。
        if (distance <= 5f)
        {
            RecordOnce("shared-door-arrived",
                "扉の位置まで来ましたが、扉が見当たりません。位置を捨てます");
            _sharedDoorPosition = null;
            return false;
        }

        RecordOnce("shared-door-go",
            $"扉が見えないので、リーダーから届いた位置へ向かいます（{distance:F0} m）");

        MovementHelper.MoveTo(_sharedDoorPosition.Value, 3f, false);
        _note = $"扉の位置へ向かっています（{distance:F0} m）";

        if (_stuck.Check())
        {
            MovementHelper.Stop();
            _sharedDoorPosition = null;
            Record("扉の位置へ近づけません。位置を捨てます");
        }

        return true;
    }

    /// <summary>
    /// 何も見えないときに、少し動いて読み込みを促す。
    ///
    /// <b>まず「本当に湧いていないのか」を確かめる。</b>
    /// 触れる仕掛けの数だけを見ていると、
    ///   (1) オブジェクトがまだ湧いていない
    ///   (2) 湧いてはいるが、まだ触れる状態になっていない
    /// の区別がつかない。対処が変わるので、両方を数えて記録に残す。
    ///
    /// (2) なら、そのうち触れるようになるので待てばよい。
    /// (1) なら、待っても湧かない。動いて読み込ませるしかない。
    ///
    /// 実測（2026-09-18）では、同じ位置にいた4台のうち
    /// 動いた1台だけが 2秒後に見えるようになった。
    /// 動く距離は、その場から離れすぎない程度でよい。
    /// </summary>
    /// <param name="quiet">何も見えなくなってからの秒数。</param>
    private void TryShakeLoose(double quiet)
    {
        // ムービー直後はオブジェクトの出入りが落ち着いていない。
        // すぐ動くと、湧きかけたものを取りこぼす。少しだけ待つ。
        if (quiet < ShakeFirstDelaySeconds)
            return;

        // 動かせる状態でなければ、何もしない。
        if (!VNavmesh.NavIsReady || !PlayerHelper.IsReady)
            return;

        // 動いている最中なら、着くまで任せる。
        if (VNavmesh.PathIsRunning || VNavmesh.PathfindInProgress)
            return;

        if (!EzThrottler.Throttle("AutoTreasure.ShakeLoose", (int)(ShakeIntervalSeconds * 1000)))
            return;

        // 何が起きているのかを、最初の1回だけ書き残す。
        // 「湧いていない」のか「触れないだけ」なのかで、次の手が変わる。
        var (targetable, total) = ObjectHelper.CountEventObjects();

        if (_shakeCount == 0)
        {
            Record(total > 0
                ? $"仕掛けは {total} 件ありますが、触れるものが {targetable} 件です。"
                  + "その場から動いて、触れるようになるか確かめます"
                : "仕掛けが 1 件も見当たりません。読み込みを促すため動いてみます");
        }

        // 「触れないだけなら待てばよい」は誤りだった。
        //
        // 実測（2026-09-18 07:07）:
        //   扉のムービー明け、4台とも仕掛けは 15 件そこに在ったが、
        //   触れるものは 0 件だった（eventObjsAll=15 / eventObjs=0）。
        //   同じ扉の前にいたリーダーは 07:07:26 に動いた瞬間、
        //   2秒後に 3 件が触れるようになった。
        //   動かなかったメンバー3台は 0 件のまま戻らなかった。
        //
        // つまり「そこに在るが触れない」状態も、動くことで解ける。
        // 待っていても解けない。ここで返してはいけない。

        if (_shakeCount >= ShakeMaxAttempts)
            return;

        _shakeCount++;

        // 今いる場所から、少しだけ離れた場所へ動く。
        //
        // 向きは毎回変える。同じ向きに戻され続ける場所だと、
        // 何度やっても同じところに戻ってしまうため。
        var angle = _shakeCount * 2.39996f;   // 黄金角。回るたび違う向きになる
        var offset = new Vector3(
            MathF.Cos(angle) * ShakeDistance,
            0f,
            MathF.Sin(angle) * ShakeDistance);

        var destination = PlayerHelper.Position + offset;

        // 地形の上に無い座標を渡すと、静かに失敗して動かない。
        // 床の上へ寄せてから渡す。
        var onFloor = VNavmesh.PointOnFloor(destination, false, ShakeDistance);
        if (onFloor != null)
            destination = onFloor.Value;

        MovementHelper.MoveTo(destination, 1.5f, false);
        _note = $"見えないので動いて確かめています（{_shakeCount} 回目）";
    }

    private void TickVaultExploring()
    {
        // 地形ができるまでは動かない。
        //
        // 階層が変わった直後は、前の階層の地形が残っていることがある。
        // その地形には今いない場所の通路が載っているため、
        // 「戻れる」と誤解して壁に向かって走り続ける。
        // 作り直しを頼んであるので、出来上がるまで待つ。
        if (!VNavmesh.NavIsReady)
        {
            if (CheckNavmeshTimeout()) return;
            MovementHelper.Stop();

            var progress = VNavmesh.NavBuildProgress;
            _note = progress >= 0f
                ? $"地形を作り直しています（{progress * 100:F0}%）"
                : "地形を作り直しています";

            NavmeshWatcher.EnsureBuilding();
            return;
        }


        // 階層が変わった直後は、動かずに待つ。
        //
        // vnavmesh は「準備できています」と答えるが、その中身は
        // 前の階層の地形で、今いない場所の通路が載っている。
        // ここで動くと、戻れない道へ向かって走り続ける。
        if (_meshSettleUntil != null)
        {
            var remain = (_meshSettleUntil.Value - DateTime.UtcNow).TotalSeconds;

            if (remain > 0)
            {
                MovementHelper.Stop();
                _note = $"地形が入れ替わるのを待っています（{remain:F0} 秒）";
                return;
            }

            _meshSettleUntil = null;

            // ここまで来て初めて動いてよい。
            // 区画を移り、地形を作り直し、落ち着くのを待った後。
            MovementHelper.Allow();

            Record("地形が入れ替わりました。動き出します");
        }

        // ロットは場面を選ばず片付ける。
        //
        // 宝箱から離れたあとに出てくることもあるし、
        // 戦っている最中に前の宝箱の分が残っていることもある。
        // どの場面でも見ておけば取りこぼさない。
        RollIfPending();

        // 強欲の罠は、何よりも先に片付ける。
        //
        // 罠が出ている間は扉にも宝箱にも触れない。
        // ここを飛ばして扉へ向かうと、扉に触れないまま
        // その場で止まり続ける。実測（2026-09-18）でそうなった。
        //
        // 「解除に挑む」を押したあとも、床を選ぶまでは罠が続いている。
        // ウィンドウの有無だけで終わったと判断してはいけない。
        if (GreedTrap.IsActive)
        {
            _vaultStuckSince = null;
                _warpOnlySince = null;
            _emptyRoomSince = null;
            _shakeCount = 0;

            MovementHelper.Stop();

            if (!GreedTrap.Tick(Plugin.Config.ChallengeGreedTrap, out var trapNote, Record))
            {
                // 調べもの用。札が開いたところで止める。
                // ここで画面を撮ってもらう。
                if (GreedTrap.StudyStopRequested)
                {
                    Record("強欲の罠: 札が開きました。画面を撮ってください（調べもののため停止します）");
                    _note = "強欲の罠: 札が開きました。画面を撮ってください";
                    Stop("強欲の罠の札を調べています");
                    return;
                }

                _note = trapNote;
                return;
            }

            GreedTrap.Reset();
            Record("強欲の罠が片付きました");
            return;
        }

        // リーダーが右へ切り替えたら、メンバーも右の扉へ向かう。
        // 別々の扉に集まると、置き去りになる者が出る。
        var decision = VaultRoutine.Decide(out var chest, out var door, out var warp, _useRightDoor);

        // 最下層かどうかは、宝箱や扉より先に見る。
        //
        // <b>最下層の進み方（実測 2026-09-18 17:26〜17:28）。</b>
        //   17:26:41  脱出地点(2000139) はこの時点で既に在る（触れない）
        //   17:27:26  「最終区画が封鎖された！」敵が現れる。宝箱は0個
        //   17:27:36  アルパカを倒す → 革袋(791)が出現
        //   17:27:49  ウォロンを倒す → 宝箱(792)が出現
        //   17:28:50  戦闘終了 → 仕掛け 0→1（脱出地点が触れるようになる）
        //             さらに宝箱(DataId 0)が出現
        //
        // つまり最下層は「入る → 敵 → 倒すたびに宝箱が出る」。
        // 入った時点では宝箱が1つも無い。
        // そして<b>何個出るかは決まっていない</b>。
        // 敵が落とすかどうかは、そのときの状況による。
        //
        // <b>なぜ Decide の結果より前に置くのか。</b>
        // 宝箱があると Decide は OpenChest を返すので、
        // 「宝箱がある間は最下層だと気づけない」ことになる。
        // それでも脱出側（TickLeavingVault）は宝箱が残っていれば
        // ここへ戻してくるので、次のような堂々巡りが起きる:
        //
        //   VaultExploring → 宝箱を開ける → まだ宝箱がある
        //     → LeavingVault は「宝箱がある」と言って戻す → …
        //
        // 倒すたびに宝箱が増える以上、この行き来は毎回起こりうる。
        // 最下層だと先に分かっていれば、脱出側が宝箱を片付けてから
        // 出るので、往復しない。
        //
        // 脱出地点が見えていることだけを根拠にする。数は数えない。
        //
        // <b>戦闘中は移らない。</b>
        // 脱出地点は敵を倒す前から在るので、これを見ただけで脱出へ
        // 移ると、敵を残したまま出口へ歩き出す。
        // 倒さないと宝箱が出ないので、取りこぼしにもなる。
        if (VaultRoutine.IsFinalRoom() && decision != VaultAction.Fight)
        {
            // 着いた瞬間の様子を、1度だけ記録に残す。
            //
            // <b>最下層は滅多に来られない。</b>
            // 来たときに取れるだけ取っておかないと、
            // 次の機会まで調べものが進まない。
            //
            // 触る前の状態を残すのが大事。
            // 敵が何体いて、どれが触れて、戦闘状態はどうなっているか——
            // ここが分かれば、攻撃が始まらない理由を追える。
            RecordOnce("final-room-arrive",
                "最下層に着きました。触る前の様子を記録します\n" + DescribeSurroundings());

            // <b>最下層の敵は、こちらから殴らないと始まらない。</b>
            //
            // 最下層の敵は最初から置かれている。
            // ほかの区画のように「宝箱を開けたら湧く」のではなく、
            // <b>攻撃するまで何もしてこない</b>（利用者の説明・2026-09-19）。
            //
            // そのため「こちらと戦っている敵」を数えると 0 件になり、
            // 敵がいるのに素通りして脱出してしまう。
            // 実測（2026-09-19 09:55）では、ゴールデン・モルターが
            // 名札に出ているのに一度も戦わず、そのまま周回が終わった。
            //
            // 戦闘中かどうかを問わず敵を探し、見つけたら自分から仕掛ける。
            //
            // <b>通った／通らなかったを必ず残す。</b>
            // これまで「ログに出ていないこと」から中の動きを推し量っていたが、
            // 推測を重ねて何度も外した。出す・出さないを条件にせず、
            // 毎回かならず1行残して、事実で判断できるようにする。
            var engaged = TryStartFinalRoomFight();

            if (EzThrottler.Throttle("AutoTreasure.FinalTrace", 2000))
            {
                var t = Svc.Targets.Target;
            var tc = t as Dalamud.Game.ClientState.Objects.Types.IBattleChara;

            Record($"最下層: 敵の処理 = {(engaged ? "担当した" : "何もしなかった")}"
                     + $"・自分の戦闘中 = {PlayerHelper.InCombat}"
                     + $"・狙い = {t?.Name.TextValue ?? "なし"}"
                     + $"（DataId {t?.DataId}・HP {tc?.CurrentHp}"
                     + $"・触れる {t?.IsTargetable}）");
            }

            if (engaged)
                return;

            RecordOnce("final-room", "脱出地点が見えました。最下層です");
            _emptyRoomSince = null;
            _shakeCount = 0;
            _vaultStuckSince = null;
            _warpOnlySince = null;
            SetState(RunState.LeavingVault);
            return;
        }

        switch (decision)
        {
            case VaultAction.Wait:
                _vaultFightSince = null;
                // 「簡易移動」しか無い場面もここに来る。
                // 乗ると手前へ戻されるので、あえて動かない。
                if (warp != null)
                {
                    MovementHelper.Stop();
                    _note = "扉の奥へ進んでいます";

                    // 扉が開いたあとなら、向きが分かるので自分で進める。
                    if (_chestPosition != null && _doorAccessPosition != null)
                    {
                        SetState(RunState.VaultTransition);
                        return;
                    }

                    // 向きが分からない場合でも、ここで止まったままにはしない。
                    //
                    // 覚えているはずの「宝箱の位置」と「扉に触れた位置」は、
                    // 途中で開始し直すと両方とも空になる。
                    // すると上の逃げ道に入れず、永久に待ち続ける。
                    //
                    // 実測（2026-09-18 06:52）:
                    //   魔紋の中で開始を押した4台すべてが、
                    //   扉を開けたあとのワープ床の前でこの状態になり、
                    //   「簡易移動のみ検知」を出したまま一歩も動かなかった。
                    //
                    // 宝箱も扉も無く、残っているのがワープ床だけなら、
                    // 進む先はそこしかない。乗って先へ進む。
                    //
                    // 「乗ると手前へ戻される」のは扉を開ける前の話。
                    // 扉が開いていなければ、そもそも扉が見えているので
                    // ここには来ない（扉があれば GoToDoor に入る）。
                    _warpOnlySince ??= DateTime.UtcNow;

                    var warpWait = (DateTime.UtcNow - _warpOnlySince.Value).TotalSeconds;

                    if (warpWait >= WarpOnlySeconds)
                    {
                        RecordOnce("warp-ride",
                            "扉も宝箱も無く、ワープ床だけが残っています。乗って先へ進みます");

                        var distance = ObjectHelper.DistanceToPlayer(warp);

                        if (distance > 3f)
                        {
                            MovementHelper.MoveTo(warp.Position, 2f, false);
                            _note = $"ワープ床へ向かっています（{distance:F0} m）";
                        }
                        else
                        {
                            // 乗ったあとは搬送が始まる。
                            // 階層が変わるので、地形を作り直させる。
                            _note = "ワープ床に乗っています";
                            MovementHelper.Dismount();
                            ObjectHelper.Interact(warp);
                        }

                        return;
                    }

                    RecordOnce("warp-wait", "簡易移動のみ検知。進む向きが分からないため待機します");
                    _note = $"ワープ床を確かめています（{WarpOnlySeconds - warpWait:F0} 秒）";
                }
                else
                {
                    // 宝箱も扉も見えない。
                    //
                    // 最後の部屋かもしれないし、ムービーの前後で
                    // 一時的に消えているだけかもしれない。
                    // しばらく様子を見てから決める。
                    _emptyRoomSince ??= DateTime.UtcNow;

                    var quiet = (DateTime.UtcNow - _emptyRoomSince.Value).TotalSeconds;

                    // 何も見えないまま動かないでいると、そのまま永久に見えない。
                    //
                    // 実測（2026-09-18 06:03 メンバー3台）:
                    //   扉のムービー明けに仕掛けが 0 件になり、
                    //   その場に立ち続けた3台は 25分・738回の観測で
                    //   一度も 0 件から戻らなかった。
                    //   同じ位置にいたリーダーだけは、ムービー直後に
                    //   2.7y 動いた結果 2秒後に 3 件見えるようになった。
                    //
                    // 「見えないから動かない」と「動かないから見えない」で
                    // 堂々巡りになっている。

                    // 仕掛けが見えなくても、決まった場所へ向かう。
                    //
                    // 魔紋の中は入るたびに同じ場所に同じものが出る
                    // （185本の記録で 20 個すべて座標が一致）。
                    // 見えるかどうかに関わらず、行くべき場所は決まっている。
                    //
                    // 実測（2026-09-18 第2層以降）では、メンバーは
                    // 触れる仕掛けが 0 件のまま何も見つけられず、
                    // 「部屋を確かめています」のまま止まっていた。
                    // 近づけば見えるようになるので、まず向かう。
                    // 扉が開いたあとなら、その先へ進む。
                    // 扉そのものは消えているので、探しても見つからない。
                    if (TryWalkPastKnownDoor())
                        break;

                    if (TryGoToKnownSpot())
                        break;

                    // 最下層なら、待たずに出る。
                    //
                    // 脱出地点（2000139）が見えていれば、そこが最下層。
                    // 以前は「20秒間なにも見えない」ことで判断していたが、
                    // それでは毎回20秒を無駄にするうえ、
                    // 途中の区画を最下層と取り違える恐れもあった。
                    if (VaultRoutine.IsFinalRoom())
                    {
                        Record("脱出地点が見えました。最下層です");
                        _emptyRoomSince = null;
                        _shakeCount = 0;
                        _vaultStuckSince = null;
                        _warpOnlySince = null;
                        SetState(RunState.LeavingVault);
                        return;
                    }

                    // 表に無い区画（第5区画など）では、
                    // リーダーから届いた位置を頼りにする。
                    if (TryGoToSharedDoor())
                        break;

                    // 位置も届いていないなら、動いて読み込みを促す。
                    TryShakeLoose(quiet);

                    // 最後の部屋かどうかは、触れるものだけで決めてはいけない。
                    //
                    // 実測（2026-09-18 07:07）:
                    //   仕掛けが 15 件そこに在るのに、触れるものが 0 件という
                    //   状態が続き、20秒で「最後の部屋」と誤って判断して
                    //   魔紋から出ようとした。まだ第2区画だった。
                    //
                    // そこに在るなら、まだ先がある。触れるようになるのを待つ
                    // （待つだけでは解けないので、上の TryShakeLoose で動く）。
                    var (_, present) = ObjectHelper.CountEventObjects();

                    if (quiet >= EmptyRoomSeconds)
                    {
                        RecordOnce("empty-but-present",
                            $"仕掛けが {present} 件あるので、まだ先の部屋があるとみなします");
                    }

                    _note = $"部屋を確かめています（{EmptyRoomSeconds - quiet:F0} 秒）";
                }

                // 何も起きないまま時間が過ぎたら、人に任せる。
                //
                // ここは各段階の失敗が戻ってくる先なので、
                // 出口が無いと、どこで詰まっても永久に止まったままになる。
                //
                // <b>SecondsInState では測れない。</b>
                // メンバーは魔紋に入ってからずっと VaultExploring のままで、
                // 部屋を進むたびに時間が積もる一方、状態は変わらない。
                // 逆に、本当に詰まっていても状態が変わらないので気づけない。
                //
                // 実測（2026-09-18）:
                //   メンバー3台が扉の前で25分動かなかったが、
                //   この判定は一度も働かなかった。
                //
                // 「何も見えない・何もできない」が続いた時間そのもので測る。
                _vaultStuckSince ??= DateTime.UtcNow;

                if ((DateTime.UtcNow - _vaultStuckSince.Value).TotalSeconds > VaultIdleSeconds)
                {
                    Record("魔紋の中で進めなくなりました");
                    _note = "進めなくなりました。手で操作してください";
                    _vaultStuckSince = null;
                    _warpOnlySince = null;
                    SetState(RunState.Failed);
                }
                break;

            case VaultAction.Fight:
            {
                _combatEndedAt = null;
                _emptyRoomSince = null;
                _shakeCount = 0;
                _vaultStuckSince = null;
                _warpOnlySince = null;

                // 乗ったままでは攻撃できない。先に降りる。
                MovementHelper.Dismount();

                MovementHelper.Stop();

                var count = ObjectHelper.CountLivingEnemies(EnemySearchRange);
                _note = $"戦っています（敵 {count} 体）";

                // 狙う相手を決める。RSR は TargetOnly なので、狙いが無いと撃たない。
                EnsureEnemyTarget();

                // 倒しきれない相手や、部屋の外の敵に絡まれると終わらない。
                // 状態は VaultExploring のままなので、滞在時間では測れない。
                // 戦い始めからの時間で測る。
                _vaultFightSince ??= DateTime.UtcNow;

                if ((DateTime.UtcNow - _vaultFightSince.Value).TotalSeconds > FightTimeoutSeconds)
                {
                    Record($"戦闘が終わりません（敵 {count} 体）");
                    _note = "戦闘が終わりません。手で操作してください";
                    SetState(RunState.Failed);
                    return;
                }

                // 魔紋の中でも戦えているかを見張る。
                //
                // ここを入れないと、見張りはフィールドの戦闘でしか働かない。
                // 魔紋の中で攻撃しなくなると、敵が減らないまま
                // 永久に止まってしまう——一番困る場面で効かなかった。
                WatchCombatProgress(count);
                break;
            }

            case VaultAction.OpenChest:
                _emptyRoomSince = null;
                _shakeCount = 0;
                _vaultStuckSince = null;
                _warpOnlySince = null;
                _vaultFightSince = null;
                HandleVaultChest(chest!);
                break;

            case VaultAction.GoToDoor:
                _emptyRoomSince = null;
                _shakeCount = 0;
                _vaultStuckSince = null;
                _warpOnlySince = null;
                _vaultFightSince = null;

                // 扉へ向かう段階に来た＝この区画の宝箱は終わっている。
                // 仕掛けが見えなくなったときの行き先を決めるのに使う。
                _roomChestDone = true;

                HandleVaultDoor(door!);
                break;

            case VaultAction.Leave:
                SetState(RunState.LeavingVault);
                break;
        }
    }

    /// <summary>
    /// 狙う相手がいなければ、近くの敵を狙う。
    ///
    /// <b>RSR は TargetOnly なので、狙いが無いと撃たない。</b>
    /// 以前 Manual にしていたときは、こちらが何も指示しないため
    /// 一切攻撃しなかった（実測 2026-09-19）。
    ///
    /// すでに生きた敵を狙っているなら、そのままにする。
    /// 毎フレーム狙い直すと、撃とうとしている最中に横から変えることになる。
    /// </summary>
    private static void EnsureEnemyTarget()
    {
        var current = Svc.Targets.Target;

        // 今の狙いが生きた敵なら、そのまま。
        if (current is Dalamud.Game.ClientState.Objects.Types.IBattleChara
            { IsDead: false, CurrentHp: > 0, IsTargetable: true })
            return;

        var enemy = ObjectHelper.GetNearestAnyEnemy(EnemySearchRange);

        if (enemy != null)
            ObjectHelper.Target(enemy);
    }

    /// <summary>
    /// 最下層の敵に、こちらから仕掛ける。
    ///
    /// <b>最下層の敵は放っておいても襲ってこない。</b>
    /// ほかの区画は「宝箱を開けたら湧く」が、
    /// 最下層は最初から置かれていて、攻撃するまで何もしてこない。
    ///
    /// そのため、いつもの「戦っている敵がいるか」では見つけられない。
    /// 戦闘中かどうかを問わずに探し、近づいて、ターゲットにする。
    /// あとは RSR が撃ち始める。
    ///
    /// 倒すべき相手がいなければ false。呼んだ側は脱出へ進んでよい。
    /// </summary>
    /// <returns>まだ敵がいて、こちらで面倒を見たなら true。</returns>
    private bool TryStartFinalRoomFight()
    {
        var enemy = ObjectHelper.GetNearestAnyEnemy(EnemySearchRange);

        if (enemy == null)
        {
            // <b>なぜ見つからないのかを残す。</b>
            // 記録には「触れる=True の敵がいる」と出ているのに、
            // ここで見つからない状態が続いている（実測 2026-09-19 10:17）。
            // 条件のどれで弾いているのかが分からないと、推測で直すことになる。
            RecordOnce("final-no-enemy",
                "最下層: 倒す相手が見つかりません\n" + ObjectHelper.DescribeEnemyFilter(EnemySearchRange));
            return false;
        }

        // <b>体の表面までを測る。</b>
        //
        // 中心までを測ると、体の大きい相手では届かない。
        // 実測（2026-09-19）: 画面の表示は 0.00m なのに、
        // 中心までは 4.1y あり、「まだ 4m 遠い」と判断して
        // 近づく処理から先へ一歩も進めなかった。
        //
        // ゲームと同じ測り方にすれば、密着している状態は 0m になる。
        var distance = ObjectHelper.SurfaceDistanceToPlayer(enemy);

        // 遠ければ近づく。
        //
        // <b>乗ったままでは攻撃できない。</b>
        // 最下層へは飛んで着くことがあるので、まず降りる。
        if (distance > FinalRoomEngageRange)
        {
            MovementHelper.Dismount();

            // 止まる位置は、相手の大きさを足したところ。
            //
            // 中心から 3y の地点は<b>相手の体の中</b>で、そこへは行けない。
            // 体の表面から 3y のところで止まるようにする。
            MovementHelper.MoveTo(
                enemy.Position,
                FinalRoomEngageRange + enemy.HitboxRadius,
                false);

            _note = $"最下層: 敵に向かっています（{distance:F0} m）";

            if (_stuck.Check())
                MovementHelper.Stop();

            return true;
        }

        MovementHelper.Stop();
        MovementHelper.Dismount();

        // ターゲットにするだけでよい。撃つのは RSR の仕事。
        //
        // 何度も入れ直さない。狙いを毎フレーム付け直すと、
        // RSR が撃とうとしている最中に横から変えることになる。
        // <b>毎回かならず狙い直す。</b>
        //
        // 以前は「違う相手を狙っていたら入れ替える」形にしていた。
        // だが最下層には同じ名前・同じ座標の相手が10体いて、
        // そのうち9体は HP 44 の触れないもの。
        // 前の周回の狙いが残っていると、それを掴んだまま
        // 「もう狙っている」と判断して入れ替えなかった。
        //
        // 狙いを入れ直すのは軽い操作なので、毎回やってよい。
        if (Svc.Targets.Target?.EntityId != enemy.EntityId)
        {
            ObjectHelper.Target(enemy);

            Record($"最下層: 狙いを {enemy.Name} に変えました"
                 + $"（DataId {enemy.DataId}・距離 {distance:F1}・HP "
                 + $"{(enemy as Dalamud.Game.ClientState.Objects.Types.IBattleChara)?.CurrentHp}）");
        }

        // <b>1発目は、こちらから殴る。</b>
        //
        // 狙いを付けただけでは戦闘が始まらない。
        // 最下層の敵は手を出すまで何もしてこないので、
        // こちらが戦闘状態に入らず、RSR も撃ち始めない。
        //
        // 実測（2026-09-19 10:11）:
        //   距離 2.9y で狙いを付けたのに、26秒間まったく攻撃しなかった。
        //   利用者の報告「ターゲットはしてくれますが、攻撃を行ってくれません」も同じ。
        //
        // オートアタックを撃てば相手が反撃し、戦闘状態に入る。
        // あとは RSR が続けてくれる。
        //
        // 戦闘に入るまでの間だけ撃つ。入ったあとは RSR に任せる。
        if (!PlayerHelper.InCombat)
            ActionHelper.StartAutoAttack();

        // 狙いを付けたのに撃ち始めないなら、RSR 側の問題。
        // それが分かるように、しばらく様子を残す。
        _finalFightSince ??= DateTime.UtcNow;

        var fighting = (DateTime.UtcNow - _finalFightSince.Value).TotalSeconds;

        if (fighting > FinalFightWarnSeconds)
        {
            RecordOnce("final-fight-stuck",
                $"最下層: 狙いを付けて {FinalFightWarnSeconds:F0} 秒たっても倒せません"
                + $"（自分の戦闘中={PlayerHelper.InCombat}・"
                + $"狙い={Svc.Targets.Target?.Name.TextValue ?? "なし"}）\n"
                + DescribeSurroundings());
        }

        _note = "最下層: 敵と戦っています";
        return true;
    }

    /// <summary>ロットが出ていれば処理する。出ていなければ何もしない。</summary>
    /// <summary>ロットが出たままになっている時刻。押されたら消す。</summary>
    private DateTime? _rollPendingSince;

    private void RollIfPending()
    {
        if (!Plugin.Config.AutoRoll)
            return;

        if (!LootHelper.HasPendingLoot())
        {
            _rollPendingSince = null;
            return;
        }

        // LazyLoot が入っているなら、まずそちらに任せる。
        //
        // 両方が動くと、こちらが Need を押す前に LazyLoot が Pass を押す、
        // といった取り合いになる。手口まで同じ（RollItemRaw を直接呼ぶ）なので、
        // どちらが先に通るかで結果が変わり、安定しない。
        //
        // LazyLoot はロット専用のプラグインで、こちらより作り込まれている。
        // 入っているなら、利用者の設定どおりに動く方へ譲る。
        if (IPC.LazyLootControl.ShouldYield)
        {
            _rollPendingSince ??= DateTime.UtcNow;

            var waited = (DateTime.UtcNow - _rollPendingSince.Value).TotalSeconds;

            // ⚠ 譲ったまま<b>誰も押さない</b>ことがある（2026-09-22 実測）。
            //   魔紋の中で地図役の画面にロット窓が開き、
            //   残り時間だけが減って周回が止まった。
            //   窓が出るのは宝箱を開けた本人だけなので、
            //   地図役だけロットされないように見えていた。
            //
            //   LazyLoot の設定は問題なく（制限は全部OFF・FulfEnabled=True）、
            //   停止の知らせも出ていない。なぜ押さないかは未解明。
            //
            //   原因が相手側にあっても周回は止めたくないので、
            //   待っても押されないときだけ、こちらが押す。
            //   LazyLoot が正しく働く場面では、その前に窓が消えるので
            //   ここへは来ない（取り合いにならない）。
            if (waited < LazyLootGraceSeconds)
                return;

            RecordEvery("roll-takeover", 10,
                $"LazyLoot が {waited:F0}秒 押さないので、こちらでロットします");
        }

        LootHelper.RollPending(LootHelper.OptionFor(Plugin.Config.Role));
    }

    private void HandleVaultChest(IGameObject chest)
    {
        // 敵が出ているあいだは、宝箱に触らない。
        //
        // <b>触っても開かない。</b>
        // 宝箱を開けると敵が湧く仕掛けなので、湧いている最中は
        // アクセスが通らない。それでも毎フレーム触り続けていた。
        //
        // 無駄なだけでなく、触る操作が戦闘の動きと噛み合わない。
        // 先に倒してから開ける。戦闘は BMR に任せてあるので、
        // ここでは手を出さずに待つだけでよい。
        //
        // ⚠ 位置を覚える処理より先に置かない。
        //   宝箱の位置は、扉へ進む向きを決めるのに要る。
        //   戦闘中に返してしまうと、覚える機会を逃す。

        // 宝箱は開けると消える。消える前に位置を覚えておく。
        //
        // 扉が開いたあと、「宝箱 → 扉」の向きへ進んでワープ床を踏む。
        // ここで覚えないと向きが決まらず、扉の先へ進めない。
        _chestPosition = chest.Position;
        _chestBaseId = chest.BaseId;

        // 仲間にも位置を伝える。
        //
        // メンバーは宝箱に近づけないことがある。
        // 実測（2026-09-18 第2層）では、着いた瞬間から戦闘に入り、
        // 倒し終えたときには宝箱がもう開けられていた。
        // 自分で覚える機会が無いので、リーダーが配る。
        //
        // 同じ宝箱を何度も送らない。区画が変わったときだけ送る。
        // 魔紋の宝箱（2013860〜2013863）だけを伝える。
        //
        // 実測（2026-09-18）では、DataId 0 と 792 まで送っていた。
        // 792 は「革袋」で、拾い物であって宝箱ではない。
        // 0 は宝箱が消えた瞬間などに入る、意味のない値。
        // これを受け取った側が向きの基準にすると、
        // 見当違いの方向へ歩き出す。
        // 位置を配るのは、実際に触れる人（地図役）。
        if (IsMapUser
            && VaultRoutine.IsVaultChest(chest)
            && _sharedChestId != chest.BaseId)
        {
            _sharedChestId = chest.BaseId;
            _sync.Send(SyncMessage.VaultChest(chest.BaseId, chest.Position));
            Record($"宝箱の位置を仲間に伝えました（{chest.BaseId}）");
        }

        var distance = ObjectHelper.DistanceToPlayer(chest);
        if (distance > 3f)
        {
            MovementHelper.MoveTo(chest.Position, 2f, false);
            _note = $"宝箱へ向かっています（{distance:F0} m）";
            _chestArrivedAt = null;

            if (_stuck.Check())
                MovementHelper.Stop();
            return;
        }

        MovementHelper.Stop();

        // 仲間が集まるのを少し待つ。
        //
        // 開けるとすぐ敵が湧き、扉へ進む流れになる。
        // 離れている仲間がいると置いていくことになるため、一呼吸おく。
        //
        // 最下層では待たない。次の区画へ進まないので、散らばっていても困らない。
        // 待つのは<b>魔紋の宝箱だけ</b>にする。
        //
        // 開けると敵が湧き、扉へ進む流れになるので、
        // 離れている仲間を置いていかないよう一呼吸おく。
        //
        // それ以外の宝箱（区画に散らばっているもの）は、
        // 開けても隊列が動かない。待つ意味がないので、
        // 見つけた順にどんどん開ける。
        //
        // 最下層でも待たない。次の区画へ進まないので、
        // 散らばっていても困らない。
        if (!VaultRoutine.IsFinalRoom() && VaultRoutine.IsVaultChest(chest))
        {
            _chestArrivedAt ??= DateTime.UtcNow;

            if ((DateTime.UtcNow - _chestArrivedAt.Value).TotalSeconds < ChestGatherSeconds)
            {
                _note = "仲間が集まるのを待っています";
                return;
            }
        }

        // 開けるのはリーダーだけ。
        //
        // メンバーは宝箱のそばに集まるところまで。
        // 全員が触ると、二度目のアクセスが噛み合わずに進まないことがある。
        // 触れるのは地図を使った人だけ。
        // 他の人が触ろうとしても、ゲーム側が受け付けない。
        if (!IsMapUser)
        {
            _note = $"宝箱のそばで {_mapUser} を待っています";

            // ここは Failed にしない。
            // 魔紋の中で止めると、そこから出る手段が無くなる。
            // 代わりに、探索へ戻して別のことを試させる
            // （扉が見えていれば、そちらへ向かう）。
            if (SecondsInState > ChestTimeoutSeconds)
            {
                RecordOnce("vault-chest-wait",
                    "地図役が宝箱を開けないので、ほかを探します");
                SetState(RunState.VaultExploring);
            }

            return;
        }

        MovementHelper.Dismount();

        // 敵が出ているなら、触らずに倒すのを待つ。
        //
        // 位置はもう覚えてあるので、ここで見送っても支障はない。
        // 倒し終われば、次のフレームでそのまま開けに入る。
        if (PlayerHelper.InCombat || ObjectHelper.HasLivingEnemyWithin(VaultEnemyRange))
        {
            RecordEvery("vault-chest-combat", 5,
                "敵が出ているので、宝箱より討伐を先にします");

            _note = "敵を倒しています（宝箱はそのあと）";
            return;
        }

        ObjectHelper.InteractUntilNotTargetable(chest, "AutoTreasure.VaultChest");
        _note = "宝箱を開けています";

        // 開けたあとのロットは、この段階のはじめ（RollIfPending）で片付けている。

        // 最下層の様子を調べるために止める。
        //
        // 第5区画の宝箱・脱出ポータルの座標がまだ分かっていない。
        // 第1〜第4区画は表にしてあるが、最下層だけ記録が無い。
        //
        // 「宝箱 → 戦闘 → 宝箱」まで進んだところで止めれば、
        // そのときの周囲の様子が記録に残る。
        if (Plugin.Config.StopAtFinalRoom
            && VaultRoutine.CurrentRoom() == null
            && VaultRoutine.IsInsideVault()
            && _roomChestDone)
        {
            Record("最下層で宝箱を開け終わりました。調べもののため停止します");
            Record(DescribeSurroundings());
            _note = "最下層です。画面と記録を確認してください";
            Stop("最下層の様子を調べています");
        }
    }

    /// <summary>
    /// 今そこにあるものを、座標つきで書き出す。
    ///
    /// 最下層のように、まだ表に無い区画を調べるために使う。
    /// </summary>
    private static string DescribeSurroundings()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("──────────────────────────────────────────");
        sb.AppendLine($"  今いる場所の様子（自分 {PlayerHelper.Position:F1}）");
        sb.AppendLine("──────────────────────────────────────────");

        sb.AppendLine("  【仕掛け】");

        foreach (var o in ObjectHelper.GetEventObjectsIncludingUntargetable())
        {
            sb.AppendLine($"    DataId {o.BaseId,8}  {o.Position:F1}"
                        + $"  触れる={o.IsTargetable}  「{o.Name}」");
        }

        // 敵も書き出す。
        //
        // <b>最下層の調べものでは、ここが一番大事。</b>
        // 最下層の敵はこちらから殴るまで戦闘に入らないため、
        // 「戦っている敵」を数えると 0 件になる。
        // 実際に何が置かれているのかを、戦闘状態ごと残しておく。
        sb.AppendLine("  【敵】");

        var foundEnemy = false;

        foreach (var o in Svc.Objects)
        {
            if (o.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
                continue;

            if (o is not Dalamud.Game.ClientState.Objects.Types.IBattleChara chara)
                continue;

            foundEnemy = true;

            sb.AppendLine($"    DataId {o.BaseId,8}  {o.Position:F1}"
                        + $"  距離={ObjectHelper.DistanceToPlayer(o):F1}"
                        + $"  HP={chara.CurrentHp}"
                        + $"  触れる={o.IsTargetable}"
                        + $"  種別={chara.SubKind}"
                        + $"  「{o.Name}」");
        }

        if (!foundEnemy)
            sb.AppendLine("    （いません）");

        sb.AppendLine($"  今狙っている相手: {Svc.Targets.Target?.Name.TextValue ?? "なし"}");
        sb.AppendLine($"  自分は戦闘中か: {PlayerHelper.InCombat}");

        return sb.ToString();
    }

    private void HandleVaultDoor(IGameObject door)
    {
        // 扉には全員が近づく。
        //
        // 扉が開くと、その先のワープで次の部屋へ移る。
        // 近くにいない者は置き去りになるため、待たせてはいけない。
        //
        // 「触る」のだけをリーダーに限る。全員が触ると
        // ムービーが何度も始まってしまうため。

        // 扉の位置を仲間にも伝える。
        //
        // メンバーは扉が見えないことがある（触れる仕掛けが 0 件）。
        // 見えないと扉へ向かう処理に入れず、その場で止まる。
        // 実測（2026-09-18 第2層以降）でそうなった。
        // 本物の扉（2013864〜2013871）だけを伝える。
        // 宝箱のときに DataId 0 や革袋まで送ってしまった例があるので、
        // ここでも種類を確かめてから送る。
        if (IsMapUser
            && VaultRoutine.IsDoor(door)
            && _sharedDoorId != door.BaseId)
        {
            _sharedDoorId = door.BaseId;
            _sync.Send(SyncMessage.VaultDoor(door.BaseId, door.Position));
            Record($"扉の位置を仲間に伝えました（{door.BaseId}）");
        }

        var distance = ObjectHelper.DistanceToPlayer(door);
        if (distance > 4f)
        {
            MovementHelper.MoveTo(door.Position, 3f, false);
            _note = $"扉へ向かっています（{distance:F0} m）";

            if (_stuck.Check())
            {
                MovementHelper.Stop();
                Svc.Log.Information("扉に近づけないので、経路を引き直します。");
            }
            return;
        }

        MovementHelper.Stop();

        // 扉のそばに着いた。ここが先へ進む起点になるので覚えておく。
        // メンバーも自分でワープ床まで歩くため、役割を問わず記録する。
        _doorAccessPosition = PlayerHelper.Position;

        if (!IsMapUser)
        {
            // 触れるのは地図を使った人だけ（ゲーム側の制約）。
            //
            // ただし、ここでただ待っていると置き去りになる。
            // 扉が開いたら（扉が消えたら）自分でワープ床へ歩く。
            //
            // <b>宝箱の位置を条件にしてはいけない。</b>
            //
            // 実測（2026-09-18 07:50 第2層→第3層）:
            //   メンバーは触れる仕掛けが 0 件の状態で扉まで来ていた。
            //   宝箱に一度も触れていないので _chestPosition は空のまま。
            //   そのため扉が開いても、この条件に入れず
            //   「次の部屋へ移っています」に一度も移らなかった。
            //   リーダーだけがワープし、メンバーは置き去りになった。
            //
            //   第1層→第2層で失敗しないのは、入口で宝箱に触れていて
            //   位置を覚えているため。層が進むほど覚えていないことが増える。
            //
            // 宝箱を覚えていなくても、扉のそばに立った位置は分かっている。
            // 向きは VaultTransition 側で決められるので、ここでは
            // 「扉が消えた」ことだけを条件にする。
            if (VaultRoutine.FindDoor() == null && _doorAccessPosition != null)
            {
                _memberDoorWaitSince = null;
                SetState(RunState.VaultTransition);
                return;
            }

            _note = "扉のそばでリーダーを待っています";

            // いつまでも待たない。
            //
            // 待ち時間は「扉のそばに着いてから」で測る。
            // 状態の滞在時間で測ってはいけない。メンバーは魔紋に入ってから
            // ずっと VaultExploring のままなので、部屋を進むほど時間が積もり、
            // 2部屋目には着いた瞬間に時間切れになってしまう。
            _memberDoorWaitSince ??= DateTime.UtcNow;

            if ((DateTime.UtcNow - _memberDoorWaitSince.Value).TotalSeconds > MemberDoorWaitSeconds)
            {
                Record("リーダーの合図が来ないので、扉を探し直します");
                _memberDoorWaitSince = null;
                _doorAttempts++;

                // 何度待っても開かないなら、人に任せる。
                if (_doorAttempts >= MaxDoorAttempts)
                {
                    _note = "扉が開きません。手で操作してください";
                    SetState(RunState.Failed);
                }
            }
            return;
        }

        SetState(RunState.VaultDoor);
    }

    /// <summary>扉にアクセスして、結果を待つ。</summary>
    /// <summary>
    /// 扉が開いたあと、ワープ床へ向かって歩く。
    ///
    /// ワープ床は目に見えるオブジェクトとして現れない（記録に一度も出なかった）。
    /// そこで座標を狙わず、「宝箱 → 扉」の向きへ、扉を通り過ぎるように歩かせる。
    ///
    /// この方法が使える根拠（2026-09-17 実測・3区画とも一致）:
    ///   ・宝箱・扉・搬送の起点が、ほぼ一直線に並ぶ（直線からのズレ 3〜4y）
    ///   ・搬送の起点は、扉より 1〜7y 先
    ///   ・ムービーの間、座標は1ミリも動かない
    ///     （第2区画では29秒間 (-23.51, -398.81, 168.31) のまま）
    ///
    /// 止めどきは「座標が大きく動いた」こと。搬送が始まった合図になる。
    /// 距離を決め打ちしないので、区画ごとの差（1y と 7y）を気にしなくてよい。
    /// </summary>
    /// <summary>
    /// 搬送が終わって落ち着いたか。
    ///
    /// 運ばれている最中は座標が大きく動き続ける。
    /// しばらく動きが小さくなったら、着地したとみなす。
    /// </summary>
    private bool IsTransportSettled()
    {
        var now = PlayerHelper.Position;

        if (_transportLastPosition != null)
        {
            var moved = Vector3.Distance(now, _transportLastPosition.Value);

            // まだ大きく動いている＝運ばれている最中。
            if (moved > TransportSettleDistance)
            {
                _transportLastPosition = now;
                _transportStillSince = null;
                return false;
            }

            _transportStillSince ??= DateTime.UtcNow;

            if ((DateTime.UtcNow - _transportStillSince.Value).TotalSeconds < TransportSettleSeconds)
                return false;

            _transportLastPosition = null;
            _transportStillSince = null;
            return true;
        }

        _transportLastPosition = now;
        _transportStillSince = null;
        return false;
    }

    /// <summary>
    /// 今の区画について覚えていたことを、すべて捨てる。
    ///
    /// 区画が変われば、宝箱も扉も座標もまったく別のものになる。
    /// 一つでも残すと、前の区画の向きへ歩き出すといった事故が起きる。
    /// 「進めなかった」場合も含め、この段階を離れるときは必ず呼ぶ。
    /// </summary>
    private void ForgetRoom()
    {
        _vaultFightSince = null;
        _lastEnemyHp = null;
        _combatStalledSince = null;
        _chestBaseId = 0;
        _sharedChestId = 0;
        _roomChestDone = false;
        // 扉の位置は捨てる（次の区画では別の場所になる）。
        //
        // ただし <b>_sharedDoorId は残す</b>。
        // ここを消すと「リーダーがどちらの扉を使ったか」が分からなくなり、
        // 扉の先へ進むときに左右を取り違える。
        // 次の区画でリーダーが扉を伝えてくれば、そのとき上書きされる。
        _sharedDoorPosition = null;
        _transportLastPosition = null;
        _transportStillSince = null;

        // 区画が変われば、罠も新しいものになる。
        // 前の区画で「賭けない」と決めたことを引きずらない。
        GreedTrap.Reset();
        // 左へ戻すことを仲間にも伝える。
        // 伝えないと、前の区画で右へ切り替えた仲間が右のまま残る。
        if (_useRightDoor && IsMapUser)
            _sync.Send(SyncMessage.DoorSide(false));

        _chestPosition = null;
        _doorAccessPosition = null;
        _walkStartPosition = null;
        _chestArrivedAt = null;
        _useRightDoor = false;
        _doorTryingSince = null;
        _memberDoorWaitSince = null;
        _doorAttempts = 0;
    }

    private void TickVaultTransition()
    {
        // ムービー中は動かさない。ただし歩き出しの起点は捨てない。
        //
        // ここへ来るのは扉のムービーが終わったあとなので、
        // この段階で始まるムービーは「ワープ床に乗った」ものしかない。
        // 起点を捨てると、まさに見つけたい合図を消してしまう。
        // 終わったあとに座標を比べれば、飛んだことが分かる。
        if (PlayerHelper.IsInCutscene)
        {
            _note = "次の部屋へ運ばれています";
            return;
        }

        // 搬送が始まったか。始まっていれば、あとはゲームに任せる。
        if (_walkStartPosition != null)
        {
            var moved = Vector3.Distance(PlayerHelper.Position, _walkStartPosition.Value);
            if (moved > TransportDetectDistance)
            {
                // 搬送はまだ続いている。着地するまで待つ。
                //
                // 運ばれている最中に次の段階へ進むと、
                // 前の部屋で引いた経路がそのまま残り、
                // 着地した瞬間に「元の場所へ戻ろう」と走り出す。
                // 実測では、着地後に逆方向へ 18y ほど戻っていた。
                //
                // 落ち着いてから片付ける。
                if (!IsTransportSettled())
                {
                    _note = "次の部屋へ運ばれています";
                    return;
                }

                Record($"ワープ床に乗りました（{moved:F0}y 移動）");

                // <b>ここでは地形を作り直さない。</b>
                //
                // 階層の移り変わりは、高さの見張り（CheckFloorChange）が
                // 受け持つ。あちらは上がり始めた時点で経路を止め、
                // 着地してから地形を作り直す、という順で動く。
                //
                // ここでも作り直させると、運ばれている最中に
                // 前の階層の地形を読み込み直してしまう。
                // vnavmesh の Reload は「今そこにある地形」から作るため、
                // 着く前に頼んでも前の階層のものしか取れない。
                //
                // この見張りは、高さがほとんど変わらない移動
                // （同じ階層の中でのワープ）のときだけ意味を持つ。
                // その場合は高さの見張りが反応しないので、
                // ここで部屋の記憶だけを捨てておく。
                if (!_leavingFloor)
                {
                    VNavmesh.PathStop();
                    MovementHelper.Stop();
                    ForgetRoom();
                }

                SetState(RunState.VaultExploring);
                return;
            }
        }

        // 扉に触れた位置は、向きを決める起点になる。これが無いと進めない。
        if (_doorAccessPosition == null)
        {
            _note = "進む向きが分かりません";
            ForgetRoom();
            SetState(RunState.VaultExploring);
            return;
        }

        // 宝箱の位置を覚えていない場合の備え。
        //
        // 本来は「宝箱 → 扉」の向きへ進む。
        // ただしメンバーは、触れる仕掛けが無いまま扉まで来ることがあり、
        // そのときは宝箱の位置を一度も覚えられない（実測 2026-09-18 第2層以降）。
        //
        // 覚えていないなら、部屋の中心を宝箱の代わりに使う。
        // 扉は部屋の端にあるので、「中心 → 扉」は「宝箱 → 扉」とほぼ同じ向きになる。
        var origin = _chestPosition;

        if (origin == null)
        {
            if (VaultRoutine.TryGetRoomCenter(out var center))
            {
                origin = center;
                RecordOnce("transition-center",
                    "宝箱を覚えていないので、部屋の中心を起点にして進みます");
            }
            else
            {
                _note = "進む向きが分かりません";
                ForgetRoom();
                SetState(RunState.VaultExploring);
                return;
            }
        }

        // 覚えている宝箱が、この区画のものか確かめる。
        //
        // 区画ごとに宝箱の ID が変わる（2013860〜2013863）。
        // 前の区画の宝箱を覚えたまま向きを出すと、
        // まったく違う方向へ歩き出す。
        if (_chestBaseId != 0
            && VaultRoutine.CurrentRoomChestId() is var nowId
            && nowId != 0 && nowId != _chestBaseId)
        {
            Record($"別の区画の宝箱を覚えていました（{_chestBaseId} → {nowId}）");
            ForgetRoom();
            SetState(RunState.VaultExploring);
            return;
        }

        var direction = _doorAccessPosition.Value - origin.Value;
        direction.Y = 0f;   // 高さは無視する。床の上を歩くだけなので

        if (direction.LengthSquared() < 0.01f)
        {
            _note = "進む向きが分かりません";
            ForgetRoom();
            SetState(RunState.VaultExploring);
            return;
        }

        direction = Vector3.Normalize(direction);

        // 扉の位置から、その向きへさらに進んだ先を目指す。
        // 実測では 1〜7y 先だが、行き過ぎても
        // ワープ床を踏んだ時点で搬送が始まるので害はない。
        var goal = _doorAccessPosition.Value + direction * WalkPastDoorDistance;

        if (_walkStartPosition == null)
        {
            _walkStartPosition = PlayerHelper.Position;
            Record($"扉の先へ進みます（{WalkPastDoorDistance:F0}y）");
        }

        _note = "ワープ床へ向かっています";

        // 経路が途中で終わってしまうことがある。
        //
        // 目的地が壁の向こうや、まだ読み込めていない床の上だと、
        // vnavmesh は行けるところまでで経路を終える。
        // そのまま放っておくと、届かないまま立ち止まる。
        //
        // 止まっていたら引き直す。
        // MoveTo は経路が動いている間は何もしないので、
        // 止まったことを見てから呼び直す。
        if (VNavmesh.PathIsRunning || VNavmesh.PathfindInProgress)
            return;

        // 壁に向かって進み続けていないか。
        //
        // 起点や向きが実際と合っていないと、たどり着けない場所を
        // 目指し続けることになる。進めていないなら向きが違うので、
        // 25秒待たずに切り上げて、部屋を見直す。
        if (_stuck.Check())
        {
            MovementHelper.Stop();
            Record("扉の先へ進めません。向きが違うようなので、部屋を見直します");
            ForgetRoom();
            SetState(RunState.VaultExploring);
            return;
        }

        if (EzThrottler.Throttle("AutoTreasure.WarpNudge", 1000))
            MovementHelper.MoveTo(goal, 1f, fly: false);

        // いつまでも搬送が始まらないなら、探し直す。
        if (SecondsInState > WalkPastDoorSeconds)
        {
            MovementHelper.Stop();
            Record("扉の先へ進みましたが、ワープ床に届きませんでした");
            ForgetRoom();
            SetState(RunState.VaultExploring);
        }
    }

    private void TickVaultDoor()
    {
        // ムービーが始まった＝扉が開いた。
        //
        // ここで気づかないと、ムービー中もアクセスを試み続けることになる。
        if (PlayerHelper.IsInCutscene)
        {
            _note = "扉が開きました";
            return;
        }

        // 狙う扉。ふだんは左、切り替えたあとは右。
        var door = VaultRoutine.FindDoor(_useRightDoor);
        if (door == null)
        {
            // 扉が消えた＝開いた、または追い出された。
            _doorAttempts = 0;

            // 向きが分かるなら、扉の先へ進んでワープ床を踏みに行く。
            if (_chestPosition != null && _doorAccessPosition != null)
            {
                SetState(RunState.VaultTransition);
                return;
            }

            SetState(RunState.VaultExploring);
            return;
        }

        // 遠ければまず近づく。触れる距離まで寄らないと反応しない。
        // 左右を切り替えた後も、実際に触る扉のIDと座標を配る。
        if (IsMapUser && (_sharedDoorId != door.BaseId
            || EzThrottler.Throttle("AutoTreasure.ResendVaultDoor", 3000)))
        {
            _sharedDoorId = door.BaseId;
            _sync.Send(SyncMessage.VaultDoor(door.BaseId, door.Position));
        }

        var distance = ObjectHelper.DistanceToPlayer(door);
        if (distance > InteractRange)
        {
            _note = $"{(_useRightDoor ? "右" : "左")}の扉へ向かっています（あと {distance:F0}y）";
            MovementHelper.MoveTo(door.Position, InteractRange * 0.6f, fly: false);

            if (_stuck.Check())
                MovementHelper.Stop();
            return;
        }

        MovementHelper.Stop();

        // 扉に触れた場所を覚えておく。ここが先へ進む起点になる。
        //
        // 最後にアクセスした扉の位置で上書きする。
        // 右へ切り替えたときは、右の扉の位置が起点になる。
        _doorAccessPosition = PlayerHelper.Position;

        // 乗ったままでは触れない。先に降りる。
        MovementHelper.Dismount();

        ObjectHelper.InteractUntilNotTargetable(door, "AutoTreasure.VaultDoor", 1000);
        _note = $"{(_useRightDoor ? "右" : "左")}の扉を開けています";

        // 待ち時間は、この扉を試し始めてからで測る。
        //
        // 状態の滞在時間で測ってはいけない。左から右へ切り替えても
        // 同じ状態のままなので、SetState は何もせず時間が戻らない。
        // その結果、右の扉を1フレームも試さないまま失敗扱いになる。
        _doorTryingSince ??= DateTime.UtcNow;

        if ((DateTime.UtcNow - _doorTryingSince.Value).TotalSeconds <= DoorAttemptSeconds)
            return;

        // ここから先は、決めた時間だけ待っても何も起きなかった場合。

        // まだ左しか試していないなら、右に切り替える。
        //
        // 光った扉がある区画では、光っていない側の扉は選べない
        // （2026-09 の仕様変更）。左が当たりでないとき、
        // 左を叩き続けても永久に開かないため、右へ移る。
        if (!_useRightDoor)
        {
            _useRightDoor = true;
            Record("左の扉が開かないので、右の扉を試します");

            // 仲間にも伝える。別々の扉に分かれないようにする。
            if (IsMapUser)
                _sync.Send(SyncMessage.DoorSide(true));
            // 右の扉を、あらためて最初から試す。
            _doorTryingSince = DateTime.UtcNow;
            _note = "右の扉へ向かっています";
            return;
        }

        // 左右とも試した。条件が足りていない可能性が高い
        // （全員そろっていない、まだ倒していない敵がいる、など）。
        _doorAttempts++;

        if (_doorAttempts >= MaxDoorAttempts)
        {
            Record("左右どちらの扉も開きませんでした");
            _note = "扉が開きません。手で操作してください";
            SetState(RunState.Failed);
            return;
        }

        // もう一度、左から試し直す。
        //
        // 右へ切り替えたことは仲間に伝えてあるので、
        // 左へ戻すことも伝える。伝えないと、仲間だけ右に残る。
        _useRightDoor = false;
        _doorTryingSince = null;

        if (IsMapUser)
            _sync.Send(SyncMessage.DoorSide(false));

        SetState(RunState.VaultExploring);
    }

    /// <summary>魔紋から出る。</summary>
    /// <summary>
    /// 最後の部屋で、脱出ポータルから出る。
    ///
    /// 自分から魔紋を出るのは、最後の部屋にたどり着いたときだけ。
    /// それ以外の部屋では、扉の結果しだいで勝手に追い出されるので、
    /// こちらから出ようとしてはいけない。
    ///
    /// 最後の部屋では次の順で進む。
    ///   1. 宝箱を開ける（敵が出たら倒してから、もう一度開ける）
    ///   2. 宝箱が無くなる
    ///   3. 脱出ポータルに触れて出る
    ///
    /// ポータルが見つからないうちは出ない。宝箱を取り残して
    /// 出てしまうより、その場で待つ方がまだよい。
    /// </summary>
    private void TickLeavingVault()
    {
        // すでに外に出ている。
        if (!VaultRoutine.IsInsideVault())
        {
            SetState(RunState.Completed);
            return;
        }

        // 念のためもう一度、宝箱と敵を確かめる。
        // 宝箱が湧く前の一瞬を「最後の部屋」と取り違えている恐れがあるため。
        //
        // <b>ここでは「戦っていない敵」も数える。</b>
        // 最下層の敵はこちらから殴るまで何もしてこないので、
        // 戦闘中かどうかで数えると 0 件になり、
        // 敵を残したまま脱出してしまう（実測 2026-09-19 09:55）。
        if (PlayerHelper.InCombat || ObjectHelper.HasAnyEnemyWithin(EnemySearchRange))
        {
            MovementHelper.Stop();
            _note = "最後の部屋: 敵を倒しています";
            _combatEndedAt = null;
            SetState(RunState.VaultExploring);
            return;
        }

        // 戦闘が終わった直後は、少し待つ。
        //
        // <b>宝箱は敵を倒した「あと」に出る。同時ではない。</b>
        //
        // 実測（2026-09-18・4台すべて一致）:
        //   17:28:50.414  戦闘終了
        //   17:28:50.979  宝箱を発見 DataId 0     ← 0.5秒あと
        //
        // 戦闘が終わった瞬間に宝箱を探すと、まだ出ていないので
        // 「宝箱は無い」と判断して脱出地点へ歩き出す。
        // 0.5秒後に出てくる宝箱を置き去りにすることになる。
        //
        // 上の「敵がいるか」だけでは防げない。敵はもう消えているため。
        // 時間で待つしかない。
        _combatEndedAt ??= DateTime.UtcNow;

        var settled = (DateTime.UtcNow - _combatEndedAt.Value).TotalSeconds;

        if (settled < ChestSpawnSettleSeconds)
        {
            MovementHelper.Stop();
            _note = $"最後の部屋: 宝箱が出るのを待っています（{ChestSpawnSettleSeconds - settled:F1} 秒）";
            return;
        }

        // まだ宝箱が残っているなら、開けるのが先。
        //
        // 魔紋の中の宝箱は EventObj なので、Treasure だけを見てはいけない。
        // ここを間違えると、宝箱が残っているのに脱出しようとする。
        var remaining = VaultRoutine.FindAnyChest();
        if (remaining != null)
        {
            // <b>ここで VaultExploring に戻してはいけない。</b>
            //
            // 最下層の宝箱は数が決まっていない。
            // 敵を倒したときに落ちたり落ちなかったりするため、
            // 「あと何個で終わり」を当てにできない。
            //
            // 戻すと、探索側が「最下層だ」と判断して再びここへ送り、
            // ここが「宝箱がある」と言って戻す——という行き来になる。
            // 状態が毎フレーム入れ替わると、移動の指示も毎フレーム
            // 出し直しになり、その場で足踏みしたまま進まなくなる。
            //
            // 最下層に居ることは分かっているので、ここで開けきる。
            // 開け終われば触れなくなり、自然に下の脱出処理へ進む。
            //
            // 開ける手順そのものは HandleVaultChest に任せる。
            // 「リーダーだけが触る」「メンバーはそばで待つ」という
            // 決め事がそちらに入っているため、ここで書き直すと
            // 全員が触りに行って噛み合わなくなる。
            //
            // 待ち時間を測り直す。
            // 宝箱を開けると敵が湧くことがあり、その敵を倒すと
            // また宝箱が出る。1度だけ待って終わりにはできない。
            HandleVaultChest(remaining);
            return;
        }

        // 扉があるなら、ここは最後の部屋ではない。
        //
        // 最後の部屋以外で自分から出ることはしない、という決め事のための歯止め。
        //
        // 見分けに扉を使う理由:
        //   第1〜第4区画には必ず左右2枚の扉があり、最下層には無い。
        //   「簡易移動」は最下層にも置かれている可能性があるため、
        //   これを根拠にすると最下層で脱出できなくなる。
        if (VaultRoutine.FindDoor() != null)
        {
            _note = "まだ先の部屋があります";
            SetState(RunState.VaultExploring);
            return;
        }

        // ロットが残っていれば先に片付ける。
        //
        // ここも待ちきりにしない。
        // LazyLoot が押さない品が残ると、脱出できなくなる。
        RollIfPending();
        if (LootHelper.HasPendingLoot() && SecondsInState <= LootWaitGiveUpSeconds)
        {
            _note = "最後の部屋: ロットを処理しています";
            return;
        }

        // 脱出ポータルを探す。
        var portal = VaultRoutine.FindExitPortal();
        if (portal == null)
        {
            _note = "脱出ポータルを探しています";

            // 部屋の真ん中あたりへ寄ると見つかることがある。
            // ポータルは中央付近に出るため。
            //
            // すでに中央付近にいるなら動かない。
            // 着いているのに指示を出し続けると、毎フレーム経路を引き直すことになり、
            // その場で固まったまま軌跡だけが高速に点滅する。実際そうなった。
            if (SecondsInState > 5 && VaultRoutine.TryGetRoomCenter(out var center))
            {
                if (ObjectHelper.DistanceToPlayer(center) > RoomCenterRange)
                {
                    MovementHelper.MoveTo(center, RoomCenterRange * 0.6f, false);
                    _note = "部屋の中央へ向かっています（脱出ポータルを探しています）";
                }
                else
                {
                    MovementHelper.Stop();
                    _note = "脱出ポータルを探しています（部屋の中央付近）";
                }
            }

            // それでも見つからないなら、こちらからは出ない。
            // 勝手に出るより、待って人に任せる方が安全。
            if (SecondsInState > ExitPortalSearchSeconds)
            {
                _note = "脱出ポータルが見つかりません。手で出てください";
            }
            return;
        }

        // ポータルが見つかった。近づいて触れる。
        var distance = ObjectHelper.DistanceToPlayer(portal);
        if (distance > 4f)
        {
            MovementHelper.MoveTo(portal.Position, 3f, false);
            _note = $"脱出ポータルへ向かっています（{distance:F0} m）";

            if (_stuck.Check())
                MovementHelper.Stop();
            return;
        }

        MovementHelper.Stop();

        // 乗ったままでは触れない。先に降りる。
        MovementHelper.Dismount();

        ObjectHelper.InteractUntilNotTargetable(portal, "AutoTreasure.ExitPortal", 1000);
        _note = "脱出しています";
    }

    // ---- 仲間とのやり取り ---------------------------------------------------

    private void ReceiveMessages()
    {
        // 中継サーバーに断られていたら、まず記録に残す。
        //
        // <b>これが無いと、断られたことに気づけない。</b>
        // 実際、地図役がメンバーのとき座標が弾かれていたのに、
        // 「なぜか全員がエーテライトで動かない」としか見えなかった。
        if (_sync is RelaySync relay)
        {
            while (relay.TryTakeProblem(out var problem))
                Record(problem);
        }

        while (_sync.TryReceive(out var message))
        {
            switch (message.Kind)
            {
                case SyncKind.Treasure:
                    if (message.TryGetTreasure(out var territory, out var world))
                    {
                        // 同じ場所をもう一度受け取っただけなら、何もしない。
                        //
                        // リーダーは取りこぼしに備えて3秒ごとに送り続ける。
                        // 受け取るたびに目的地を捨てていると、
                        // 着いたそばから「まだ着いていない」に戻され、
                        // その場で行ったり来たりを繰り返す。実際そうなった。
                        var same = _target != null
                                && _target.Value.TerritoryType == territory
                                && Vector3.Distance(_target.Value.World, world) < 1f;

                        if (same)
                            break;

                        _target = new TreasureTarget(territory, world, "仲間から受信", 0, 0);
                        _destination = Vector3.Zero;   // エリアに入ってから地面に落とし込む
                        _note = "宝の場所を受け取りました";

                        // 場所を受け取ったら、テレポか移動に入る。
                        //
                        // <b>受け取れる状態を広げてある。</b>
                        // 以前は Idle と WaitingForParty のときしか動かなかった。
                        // そのため、1周終わった直後（Completed）に座標が届くと
                        // 何もせず、メンバーだけがテレポせずに取り残されていた。
                        //
                        // 逆に、魔紋の中や戦闘中に割り込まれては困る。
                        // 「まだ宝へ向かっていない」状態のときだけ受け入れる。
                        var canStart = _state is RunState.Idle
                                               or RunState.WaitingForParty
                                               or RunState.Completed
                                               or RunState.Failed
                                               or RunState.Locating;

                        if (canStart)
                        {
                            var sameArea = PlayerHelper.TerritoryType == territory;

                            Record(sameArea
                                ? "宝の場所を受け取りました。同じエリアなので歩いて向かいます"
                                : "宝の場所を受け取りました。テレポで向かいます");

                            SetState(sameArea
                                ? RunState.Travelling
                                : RunState.Teleporting);
                        }
                        else
                        {
                            RecordOnce($"treasure-busy-{territory}",
                                $"宝の場所を受け取りましたが、今は動けません（{_state.ToJapanese()}）");
                        }
                    }
                    break;

                case SyncKind.StepDone:
                    if (Plugin.Config.Role == ClientRole.Leader && message.TryGetStep(out var doneStep))
                    {
                        if (doneStep == _currentStep && !string.IsNullOrEmpty(message.Sender))
                            _stepDoneFrom.Add(message.Sender);
                    }
                    break;

                case SyncKind.StepGo:
                    // リーダーは「次の段階の番号」を送ってくる。
                    // 自分がまだそこへ進んでいなければ受け取る。
                    //
                    // 古い合図（すでに通過した番号）は捨てる。
                    // そうしないと、次の待ち合わせで前回の合図を食べて素通りする。
                    //
                    // 受け取った番号をそのまま覚える。
                    // 「1つ進める」ではいけない。リーダーは時間切れなどで
                    // 2つ以上進むことがあり、そのぶん番号がずれ続ける。
                    // 一度ずれると、以後の報告がすべて弾かれ、
                    // 毎回の待ち合わせが時間切れになるまで止まる。
                    if (message.TryGetStep(out var goStep) && goStep > _currentStep)
                        _pendingStep = goStep;
                    break;

                case SyncKind.VaultDoor:
                    // リーダーから扉の位置が届いた。
                    //
                    // メンバーは扉が見えないことがある。
                    // 見えないと扉へ向かえず、その場で止まってしまう。
                    // 届いた位置があれば、見えなくても向かえる。
                    //
                    // 前の階層の扉に向かわないよう、DataId で区画を確かめる。
                    // 扉の ID は区画ごとに変わる（2013864〜2013871）。
                    // 地図役でない人が受け取る。
                    // 自分が触る側なら、自分で見つけた位置の方が正しい。
                    if (!IsMapUser
                        && message.TryGetVaultDoor(out var doorId, out var doorPos))
                    {
                        // 本物の扉以外は受け取らない。
                        if (doorId < VaultRoutine.DoorFirstId || doorId > VaultRoutine.DoorLastId)
                        {
                            Record($"扉ではない位置が届いたので使いません（{doorId}）");
                            break;
                        }

                        var currentRoom = VaultRoutine.CurrentRoom();
                        if (currentRoom != null && doorId != currentRoom.Value.LeftDoorId
                            && doorId != currentRoom.Value.RightDoorId)
                            break;

                        if (_sharedDoorId != doorId || _sharedDoorPosition == null)
                        {
                            _sharedDoorId = doorId;
                            _sharedDoorPosition = doorPos;

                            // どちら側の扉かも、ここで合わせる。
                            //
                            // 扉の ID は左が偶数、右が奇数（実測）。
                            // リーダーが使った扉の ID が分かれば、左右も分かる。
                            //
                            // これをしないと、リーダーが右を開けたのに
                            // メンバーは左へ向かい、開いていない扉の前で止まる。
                            // 実測（2026-09-18 13:52）でそうなった。
                            var right = doorId % 2 != 0;

                            if (right != _useRightDoor)
                            {
                                _useRightDoor = right;
                                Record($"リーダーが使った扉に合わせて{(right ? "右" : "左")}へ向かいます");
                            }

                            Record($"リーダーから扉の位置を受け取りました（{doorId}）");
                        }
                    }
                    break;

                case SyncKind.VaultChest:
                    // リーダーから宝箱の位置が届いた。
                    //
                    // メンバーは戦闘に追われて宝箱に近づけないことがあり、
                    // 自分では位置を覚えられない。届いたものを使う。
                    //
                    // <b>ただし、今いる区画のものでなければ使わない。</b>
                    // 前の階層の位置で向きを出すと、まったく違う方向へ歩き出す。
                    // 宝箱の DataId は区画ごとに変わる（2013860〜2013863）ので、
                    // 今その宝箱がある区画にいるのかを確かめてから受け入れる。
                    if (!IsMapUser
                        && message.TryGetVaultChest(out var chestId, out var chestPos))
                    {
                        // 魔紋の宝箱以外は受け取らない。
                        // 古い版から DataId 0 や革袋（792）が届くことがある。
                        if (chestId < VaultRoutine.ChestFirstId
                            || chestId > VaultRoutine.ChestLastId)
                        {
                            Record($"宝箱ではない位置が届いたので使いません（{chestId}）");
                            break;
                        }

                        var here = VaultRoutine.CurrentRoomChestId();

                        // 今の区画の宝箱 ID が分かるなら、一致しなければ捨てる。
                        // 分からない（もう開けられて消えた）ときは、
                        // 届いたものを信じる。ほかに手がかりが無い。
                        if (here != 0 && here != chestId)
                        {
                            Record($"別の区画の宝箱の位置が届いたので使いません（{chestId} / 今は {here}）");
                            break;
                        }

                        if (_chestBaseId != chestId)
                        {
                            _chestPosition = chestPos;
                            _chestBaseId = chestId;
                            Record($"リーダーから宝箱の位置を受け取りました（{chestId}）");
                        }
                    }
                    break;

                case SyncKind.DoorSide:
                    // リーダーが向かう扉が変わった。自分も合わせる。
                    // 別々の扉に分かれると、置き去りになる者が出る。
                    if (!IsMapUser && message.Args.Length > 0)
                    {
                        // "R" と "L" 以外は読み捨てる。
                        // 壊れた行を「左」と解釈すると、仲間だけ違う扉へ行く。
                        if (message.Args[0] is not ("R" or "L"))
                            break;

                        var right = message.Args[0] == "R";
                        if (right != _useRightDoor)
                        {
                            _useRightDoor = right;
                            Record($"リーダーに合わせて{(right ? "右" : "左")}の扉へ向かいます");
                        }
                    }
                    break;

                case SyncKind.MapUser:
                    // 今回の地図役が届いた。
                    //
                    // <b>開始の合図より先に届く。</b>
                    // これを受け取ってから始めないと、
                    // 自分が地図役かどうか分からないまま動き出すことになる。
                    //
                    // リーダーは自分で決めているので、受け取らない
                    // （自分が送ったものが中継で返ってくることがある）。
                    if (Plugin.Config.Role != ClientRole.Leader
                        && message.TryGetMapUser(out var mapUser, out var laps))
                    {
                        var session = message.Args.Length > 2 ? message.Args[2] : "";
                        if (session.Length > 0 && session == _receivedMapSession && laps < _lapsDone)
                            break;
                        _receivedMapSession = session;
                        var changed = _mapUser != mapUser;
                        _lapsDone = laps;
                        if (changed)
                        {
                            _mapUser = mapUser;
                            if (_target == null && _state == RunState.Locating && !IsMapUser)
                                SetState(RunState.WaitingForParty);

                            Record(mapUser == PlayerHelper.Name
                                ? "今回は自分が地図を使います"
                                : $"今回の地図役は {mapUser} です");

                            // 自分の番だと遅れて分かった場合、その場から動き出す。
                            //
                            // 開始の合図が先に届いていると、
                            // 「場所が届くのを待つ」状態で止まっている。
                            // だが自分が地図役なら、待っていても誰も送ってこない
                            // （送るのは自分）。気づいた時点で調べに行く。
                            if (mapUser == PlayerHelper.Name
                                && _state == RunState.WaitingForParty
                                && _target == null)
                            {
                                Record("自分が地図役なので、宝の場所を調べます");
                                SetState(RunState.Locating);
                            }
                        }
                    }
                    break;

                case SyncKind.MapUnavailable:
                    if (Plugin.Config.Role == ClientRole.Leader
                        && message.TryGetMapUnavailable(out var missingUser, out var missingLap))
                        HandleMapUnavailable(missingUser, missingLap);
                    break;

                case SyncKind.MapTurnSetting:
                    // リーダーが決めた使用順番を受け取る。
                    //
                    // リーダー自身は受け取らない。
                    // 自分が送ったものが中継で返ってくることがあり、
                    // それで自分の設定を上書きすると、
                    // 触った直後に元へ戻されたように見える。
                    if (Plugin.Config.Role != ClientRole.Leader
                        && message.TryGetMapTurnSetting(out var turnMode, out var turnSlots))
                    {
                        ApplyMapTurnSetting(turnMode, turnSlots);
                    }
                    break;

                case SyncKind.Begin:
                    // リーダーが始めた。自分も始める。
                    //
                    // すでに動いているなら何もしない。
                    // 途中で受け取った合図で最初からやり直すと、かえって乱れる。
                    if (Plugin.Config.Role == ClientRole.Member && !IsRunning)
                    {
                        Record("リーダーの合図で開始します");
                        Start(tellOthers: false);
                    }
                    break;

                case SyncKind.Ping:
                    // 生きている合図。誰から届いたかを控えておく。
                    // つながっているつもりで実は切れている、を見つけるため。
                    if (message.Args.Length > 0)
                        _lastSeen[message.Args[0]] = DateTime.UtcNow;
                    break;

                case SyncKind.Abort:
                    Stop(message.Args.Length > 0 ? message.Args[0] : "仲間が中断しました");
                    break;
            }
        }
    }

    // ---- 細かい道具 ---------------------------------------------------------

    /// <summary>
    /// 自分を見分けるための名前。
    ///
    /// 同じパソコンで複数のキャラクターを動かすため、
    /// 「誰が報告してきたか」を区別する必要がある。
    /// キャラクターごとに固有の ID を使う。
    /// 読めないときは、せめて重複しない値を返す。
    /// </summary>
    private static string SelfId
    {
        get
        {
            try
            {
                var cid = Svc.PlayerState.ContentId;
                if (cid != 0)
                    return cid.ToString();
            }
            catch
            {
                // ログイン前やエリア移動中は読めない。
            }

            // ID が読めないときは名前で代用する。
            //
            // 空のまま返すと、その機は「終わった」と報告できず、
            // リーダーは毎回の待ち合わせで時間切れまで止まる。
            // 名前でも、3台を見分けるには十分。
            try
            {
                var name = Svc.Objects.LocalPlayer?.Name.TextValue;
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
            catch
            {
                // ここも読めないなら、報告は次の機会に回す。
            }

            return "";
        }
    }

    private double SecondsInState => (DateTime.UtcNow - _stateEnteredAt).TotalSeconds;

    private void SetState(RunState next)
    {
        if (_state == next)
            return;

        Record($"段階: {_state.ToJapanese()} → {next.ToJapanese()}");
        _state = next;
        if (next is RunState.Idle or RunState.Failed) _cbt.End();
        else if (IsRunning) _cbt.Begin();
        _stateEnteredAt = DateTime.UtcNow;
        _stuck.Reset();
    }

    /// <summary>エリアが変わったときに呼ぶ。</summary>
    internal void OnTerritoryChanged()
    {
        _navmeshWaitSince = null;
        _destination = Vector3.Zero;
        _stuck.Reset();
        MovementHelper.ResetMountCooldown();

        // 魔紋に入った・出たの判定は Tick 側で毎秒行っている（CheckVaultEntry）。
        // ここでは、エリアが変わった直後に持ち越してはいけないものを捨てるだけにする。
        //
        // エリア移動の通知が来た時点では、まだ中の情報を読めないことがあるため、
        // ここで種類を調べても正しい答えが得られない。
    }

    public void Dispose()
    {
        _cbt.End();
        // プラグインを止める・外すときも、借りた設定は返す。
        //
        // 停止ボタンを押さずにプラグインを無効にすることもある。
        // ここで戻さないと、上げたままの値がゲーム終了時に保存され、
        // 次に立ち上げたときも重いままになる。
        VNavmeshConfig.Restore();
        RestoreCombatSettings();

        // LazyLoot には何もしない（触っていないため）。

        // 移動の禁止も解いておく。
        //
        // 区画を移っている最中（Block したまま）にプラグインを
        // 無効にすると、禁止したまま終わる。
        // MovementAllowed は static なので、同じプロセスで読み込み直したとき
        // 値が残っていると一歩も動けない。
        //
        // 通常 Dalamud は読み込み直しで型ごと作り直すため実害は出にくいが、
        // 借りたものを返すのと同じで、閉めたものは開けて終わる。
        MovementHelper.Allow();

        _sync.Dispose();
    }
}
