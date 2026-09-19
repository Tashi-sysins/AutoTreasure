using AutoTreasure.Helpers;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AutoTreasure.Logic;

/// <summary>
/// 手で遊んでいる間、何が起きたかを見て記録する。
///
/// 自動化を組むのに要るのは、実際のゲームで
/// 「宝箱がどういうオブジェクトとして現れるか」「扉と脱出ポータルをどう見分けるか」
/// といった、実物の姿。これは実機でしか分からない。
///
/// 毎フレーム全部を書くと読めない量になるので、
/// <b>変わったときだけ</b>書く。同じ状態が続いている間は黙っている。
/// </summary>
internal sealed class RunObserver
{
    private readonly RunLog _log;

    // 前回見たときの様子。変化に気づくために覚えておく。
    private uint _lastTerritory;
    private bool _lastInCombat;
    private bool _lastMounted;
    private bool _lastFlying;
    private bool _lastInCutscene;
    private int _lastTreasureCount = -1;
    private int _lastEventObjCount = -1;
    private int _lastEnemyCount = -1;
    private bool _lastHasLoot;
    private uint _lastFlagTerritory;
    private float _lastFlagX = float.NaN;

    // 持ち物と表示中のUI。自動化の判断材料として要になる。
    private int _lastMapCount = int.MinValue;
    private int _lastDecodedCount = int.MinValue;
    private string _lastAddons = "";

    // 一度書いたオブジェクトは繰り返さない。同じものが毎回並ぶと読めなくなる。
    private readonly HashSet<uint> _seenTreasureIds = [];
    private readonly HashSet<uint> _seenEventObjIds = [];

    // 種類を問わず、一度でも見かけたもの。「種類:DataId」で覚える。
    private readonly HashSet<string> _seenAnyIds = [];

    // 前に見たときの位置。大きく飛んだら記録するために使う。
    private Vector3 _lastTeleportCheck = Vector3.Zero;

    // 地形の様子。変わったときだけ残すために覚えておく。
    private bool _lastMeshReady;
    private int _lastMeshStep = -1;
    private string _lastPathState = "";

    /// <summary>これ以上動いていたら、走ったのではなく飛ばされたとみなす。</summary>
    private const float TeleportThreshold = 25f;

    // 中身まで書き出したウィンドウ。二度は書かない。
    private readonly HashSet<string> _dumpedAddons = [];

    /// <summary>
    /// 中身が変わるたびに書き出し直すウィンドウ。
    ///
    /// 同じ名前のまま文面が変わるものは、一度きりにすると取り逃がす。
    /// </summary>
    private static readonly HashSet<string> RedumpOnChange =
    [
        "SelectYesno",
        "SelectString",
        "SelectIconString",
    ];

    /// <summary>文字の並びから、短い目印を作る。</summary>
    private static string Fingerprint(IEnumerable<string> texts)
    {
        var joined = string.Join("/", texts);
        return joined.Length <= 60 ? joined : joined[..60];
    }

    // 扉の様子。変化したら詳しく残す。
    private string _lastDoorFingerprint = "";

    // ウィンドウごとの、前回の表示内容。
    // ミニゲームは同じウィンドウのまま数字が変わるので、その変化を追う。
    private readonly Dictionary<string, string> _lastAddonTexts = [];

    // 位置は動き続けるので、一定の間隔でだけ残す。
    private Vector3 _lastLoggedPosition;

    internal RunObserver(RunLog log)
    {
        _log = log;
    }

    /// <summary>覚えていることを捨てる。記録を始め直すときに呼ぶ。</summary>
    internal void Reset()
    {
        _lastTerritory = 0;
        _lastTreasureCount = -1;
        _lastEventObjCount = -1;
        _lastEnemyCount = -1;
        _lastFlagTerritory = 0;
        _lastFlagX = float.NaN;
        _lastMapCount = int.MinValue;
        _lastDecodedCount = int.MinValue;
        _lastAddons = "";
        _seenTreasureIds.Clear();
        _seenEventObjIds.Clear();
        _seenAnyIds.Clear();
        _lastTeleportCheck = Vector3.Zero;
        _lastMeshReady = false;
        _lastMeshStep = -1;
        _lastPathState = "";
        _dumpedAddons.Clear();
        _lastAddonTexts.Clear();
        _lastDoorFingerprint = "";
        _lastLoggedPosition = Vector3.Zero;
    }

    /// <summary>毎フレーム呼ぶ。変わったところだけ書く。</summary>
    internal void Tick()
    {
        if (!_log.IsRecording || !PlayerHelper.IsValid)
            return;

        try
        {
            ObserveTerritory();
            ObserveCondition();
            ObserveFlag();

            // オブジェクトの走査は少し重い。毎フレームではなく間隔をあける。
            if (EzThrottler.Throttle("AutoTreasure.Observe", 500))
            {
                ObserveTreasures();
                ObserveEventObjects();
                ObserveEverythingNearby();
                ObserveTeleport();
                ObserveNavmesh();
                ObserveEnemies();
                ObserveLoot();
                ObserveInventory();
                ObserveAddons();
                ObserveDoors();
                ObservePosition();
                WriteSnapshot();
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "記録中に問題が起きました。");
        }
    }

    // ---- エリア -------------------------------------------------------------

    private void ObserveTerritory()
    {
        var territory = PlayerHelper.TerritoryType;
        if (territory == _lastTerritory)
            return;

        _lastTerritory = territory;

        // エリアが変わったら、そこがどういう場所かを書いておく。
        var name = "";
        var contentName = "";
        uint contentType = 0;

        try
        {
            var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
                              .GetRowOrDefault(territory);
            if (row != null)
            {
                name = row.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
                var cfc = row.Value.ContentFinderCondition.ValueNullable;
                if (cfc != null)
                {
                    contentName = cfc.Value.Name.ExtractText();
                    contentType = cfc.Value.ContentType.RowId;
                }
            }
        }
        catch
        {
            // 読めなくても記録は続ける。
        }

        var info = GameSnapshot.GetTerritoryInfo();

        _log.WriteSection($"エリア移動 → {name}（TerritoryType {territory}）");
        _log.Write($"  コンテンツ名 : {(string.IsNullOrEmpty(contentName) ? "（フィールド）" : contentName)}");
        _log.Write($"  ContentType  : {contentType}{(contentType == 9 ? "  ← トレジャーハント" : "")}");
        _log.Write($"  ContentFinderCondition : {info.ContentFinderConditionId}");
        _log.Write($"  MapId        : {info.MapId}");
        _log.Write($"  現在地       : {RunLog.Format(PlayerHelper.Position)}");
        _log.Write($"  パーティ人数 : {GameSnapshot.PartyCount}");
        _log.Write($"  表示中のUI   : {string.Join(", ", GameSnapshot.GetVisibleAddons())}");

        // エリアが変わると中身も変わる。覚えていたものを捨てる。
        _seenTreasureIds.Clear();
        _seenEventObjIds.Clear();
        _seenAnyIds.Clear();
        _lastTeleportCheck = Vector3.Zero;
        _lastTreasureCount = -1;
        _lastEventObjCount = -1;
        _lastEnemyCount = -1;
    }

    // ---- 自分の状態 ---------------------------------------------------------

    private void ObserveCondition()
    {
        var inCombat = PlayerHelper.InCombat;
        if (inCombat != _lastInCombat)
        {
            _lastInCombat = inCombat;
            _log.Write(inCombat
                ? $"戦闘開始  敵 {ObjectHelper.CountLivingEnemies(50f)} 体  位置 {RunLog.Format(PlayerHelper.Position)}"
                : $"戦闘終了  位置 {RunLog.Format(PlayerHelper.Position)}");
        }

        var mounted = PlayerHelper.IsMounted;
        if (mounted != _lastMounted)
        {
            _lastMounted = mounted;
            _log.Write(mounted ? "マウントに騎乗" : "マウントから降りた");
        }

        var flying = PlayerHelper.IsFlying;
        if (flying != _lastFlying)
        {
            _lastFlying = flying;
            _log.Write(flying ? "飛行を開始" : "飛行を終了");
        }

        var cutscene = PlayerHelper.IsInCutscene;
        if (cutscene != _lastInCutscene)
        {
            _lastInCutscene = cutscene;
            _log.Write(cutscene ? "ムービー開始" : "ムービー終了");
        }
    }

    // ---- マップのフラグ -----------------------------------------------------

    private void ObserveFlag()
    {
        var flag = MapFlagReader.Read();

        if (flag == null)
        {
            if (_lastFlagTerritory != 0)
            {
                _lastFlagTerritory = 0;
                _lastFlagX = float.NaN;
                _log.Write("マップのフラグが消えた");
            }
            return;
        }

        // 同じフラグが立ち続けている間は書かない。
        if (flag.Value.TerritoryType == _lastFlagTerritory
            && Math.Abs(flag.Value.X - _lastFlagX) < 0.01f)
            return;

        _lastFlagTerritory = flag.Value.TerritoryType;
        _lastFlagX = flag.Value.X;

        _log.WriteSection("マップにフラグが立った（地図を解読した合図）");
        _log.Write($"  エリア   : {flag.Value.TerritoryType}");
        _log.Write($"  マップID : {flag.Value.MapId}");
        _log.Write($"  位置     : X {flag.Value.X:F2}  Z {flag.Value.Z:F2}");

        // 候補地と結びつくかも見ておく。ここが自動化の要。
        var target = TreasureLocator.Locate();
        if (target != null)
        {
            _log.Write($"  → 候補地と一致: {target.Value.PlaceName}");
            _log.Write($"     ワールド座標: {RunLog.Format(target.Value.World)}");
            _log.Write($"     地図の種類  : Rank {target.Value.Rank} / {target.Value.SubRow} 番目");
        }
        else
        {
            _log.Write("  → 候補地と結びつかなかった（要調査）");
        }
    }

    // ---- 宝箱 ---------------------------------------------------------------

    private void ObserveTreasures()
    {
        var treasures = ObjectHelper.GetTreasures();

        if (treasures.Count != _lastTreasureCount)
        {
            var before = _lastTreasureCount;
            _lastTreasureCount = treasures.Count;

            if (before >= 0)
                _log.Write($"宝箱の数が変化: {before} → {treasures.Count}");
        }

        // 初めて見る宝箱だけ、詳しく書く。
        foreach (var t in treasures)
        {
            if (!_seenTreasureIds.Add(t.BaseId))
                continue;

            _log.WriteSection($"宝箱を発見  DataId {t.BaseId}");
            WriteObjectDetail(t);
        }
    }

    // ---- 仕掛け（扉・ポータルなど） -----------------------------------------

    private void ObserveEventObjects()
    {
        var objects = ObjectHelper.GetEventObjects();

        if (objects.Count != _lastEventObjCount)
        {
            var before = _lastEventObjCount;
            _lastEventObjCount = objects.Count;

            if (before >= 0)
                _log.Write($"仕掛けの数が変化: {before} → {objects.Count}");
        }

        // 初めて見る仕掛けだけ、詳しく書く。
        // 扉と脱出ポータルを見分ける手がかりがここに出る。
        foreach (var o in objects)
        {
            if (!_seenEventObjIds.Add(o.BaseId))
                continue;

            _log.WriteSection($"仕掛けを発見  DataId {o.BaseId}  名前「{o.Name.TextValue}」");
            WriteObjectDetail(o);
        }
    }

    /// <summary>
    /// 近くにあるものを、種類も触れるかも問わず全部記録する。
    ///
    /// 普段の記録は「EventObj かつ触れるもの」だけを見ている。
    /// ところが床に乗って作動するような仕掛けは、触れない（IsTargetable=false）
    /// ことがあり、その場合これまでの記録には一切残らなかった。
    /// 実際、扉の奥にあるはずのワープ床は1行も記録できていない。
    ///
    /// 見落としを無くすため、ここでは絞り込みをしない。
    /// 新しく見かけたものは、何であれ1回だけ書き出す。
    /// </summary>
    private void ObserveEverythingNearby()
    {
        const float Radius = 60f;

        foreach (var o in Svc.Objects)
        {
            try
            {
                if (o == null)
                    continue;

                // 人やペットは除く。仕掛けになりうるものだけを見る。
                //
                // 除外する側を並べると、知らない種類が漏れる。
                // 残したい側を並べる方が取りこぼさない。
                if (o.ObjectKind is not (ObjectKind.EventObj or ObjectKind.Treasure
                                      or ObjectKind.Aetheryte or ObjectKind.GatheringPoint
                                      or ObjectKind.EventNpc or ObjectKind.Cutscene
                                      or ObjectKind.CardStand or ObjectKind.Ornament))
                    continue;

                if (ObjectHelper.DistanceToPlayer(o) > Radius)
                    continue;

                // 同じものを何度も書かない。
                // 触れるかどうかは途中で変わるので、鍵には入れない。
                var key = $"{o.ObjectKind}:{o.BaseId}";
                if (!_seenAnyIds.Add(key))
                    continue;

                _log.WriteSection($"【周囲】{o.ObjectKind}  DataId {o.BaseId}  名前「{o.Name.TextValue}」");
                WriteObjectDetail(o);
            }
            catch
            {
                // 1つ読めなくても、残りは記録したい。
            }
        }
    }

    /// <summary>
    /// 場所が大きく変わったら、その前後を残す。
    ///
    /// ワープ床は「乗った瞬間」に作動する。触れた記録が残らないため、
    /// どこからどこへ飛んだかを座標で押さえる。
    /// これが分かれば、床の位置と行き先を突き止められる。
    /// </summary>
    /// <summary>
    /// 地形（navmesh）の様子を残す。
    ///
    /// 地形が無いと、移動の指示は静かに失敗する。
    /// 画面には何も出ず、その場で棒立ちになるだけなので、
    /// あとから記録を見ても理由が分からなかった。
    ///
    /// 状態が変わったときと、作っている最中の進み具合を残す。
    /// </summary>
    private void ObserveNavmesh()
    {
        var ready = IPC.VNavmesh.NavIsReady;
        var progress = IPC.VNavmesh.NavBuildProgress;
        var pathfinding = IPC.VNavmesh.PathfindInProgress;
        var running = IPC.VNavmesh.PathIsRunning;

        // 出来た・出来ていないが変わったら残す。
        if (ready != _lastMeshReady)
        {
            _lastMeshReady = ready;
            _log.Write(ready
                ? "《地形》 準備できました"
                : "《地形》 準備できていません（この間、移動の指示は通りません）");
        }

        // 作っている最中は、進み具合を10%きざみで残す。
        if (!ready && progress >= 0f)
        {
            var step = (int)(progress * 10);
            if (step != _lastMeshStep)
            {
                _lastMeshStep = step;
                _log.Write($"《地形》 作成中 {progress * 100:F0}%");
            }
        }
        else
        {
            _lastMeshStep = -1;
        }

        // 経路の状態が変わったら残す。
        var pathState = $"{pathfinding}/{running}";
        if (pathState != _lastPathState)
        {
            _lastPathState = pathState;
            _log.Write($"《経路》 探索中={pathfinding} 移動中={running} "
                     + $"経由点={IPC.VNavmesh.PathNumWaypoints}");
        }
    }

    private void ObserveTeleport()
    {
        var now = PlayerHelper.Position;

        if (_lastTeleportCheck != Vector3.Zero)
        {
            var jumped = Vector3.Distance(now, _lastTeleportCheck);

            // 走って動ける距離を超えていたら、飛ばされたとみなす。
            if (jumped > TeleportThreshold)
            {
                _log.WriteSection($"【移動を検知】{jumped:F1} m 飛びました");
                _log.Write($"  飛ぶ前: {RunLog.Format(_lastTeleportCheck)}");
                _log.Write($"  飛んだ先: {RunLog.Format(now)}");
                _log.Write($"  エリア  : {PlayerHelper.TerritoryType}");

                // そのとき近くにあったものも一緒に残す。
                foreach (var o in ObjectHelper.GetEventObjects())
                {
                    _log.Write($"  近くの仕掛け: 「{o.Name.TextValue}」 DataId {o.BaseId} "
                             + $"距離 {ObjectHelper.DistanceToPlayer(o):F1} m");
                }
            }
        }

        _lastTeleportCheck = now;
    }

    private void WriteObjectDetail(IGameObject obj)
    {
        _log.Write($"  名前        : 「{obj.Name.TextValue}」");
        _log.Write($"  DataId      : {obj.BaseId}");
        _log.Write($"  EntityId    : {obj.EntityId:X}");
        _log.Write($"  ObjectKind  : {obj.ObjectKind}");
        _log.Write($"  位置        : {RunLog.Format(obj.Position)}");
        _log.Write($"  自分との距離: {ObjectHelper.DistanceToPlayer(obj):F1} m");
        _log.Write($"  触れるか    : {obj.IsTargetable}");
        _log.Write($"  自分の位置  : {RunLog.Format(PlayerHelper.Position)}");
    }

    // ---- 敵 -----------------------------------------------------------------

    private void ObserveEnemies()
    {
        var count = ObjectHelper.CountLivingEnemies(50f);
        if (count == _lastEnemyCount)
            return;

        var before = _lastEnemyCount;
        _lastEnemyCount = count;

        if (before < 0)
            return;

        if (count > before)
        {
            _log.Write($"敵が出現: {before} → {count} 体");

            // 出てきた敵の顔ぶれを書いておく。
            foreach (var e in Svc.Objects
                                 .Where(o => o.ObjectKind == ObjectKind.BattleNpc
                                          && o is IBattleChara { IsDead: false }
                                          && ObjectHelper.DistanceToPlayer(o) <= 50f)
                                 .Take(8))
            {
                _log.Write($"    「{e.Name.TextValue}」 DataId {e.BaseId}  距離 {ObjectHelper.DistanceToPlayer(e):F1} m");
            }
        }
        else
        {
            _log.Write($"敵が減った: {before} → {count} 体");
        }
    }

    // ---- ロット -------------------------------------------------------------

    private void ObserveLoot()
    {
        var has = LootHelper.HasPendingLoot();
        if (has == _lastHasLoot)
            return;

        _lastHasLoot = has;

        if (!has)
        {
            _log.Write("ロットが片付いた");
            return;
        }

        _log.WriteSection("ロットが出た");
        WriteLootDetail();
    }

    private unsafe void WriteLootDetail()
    {
        try
        {
            var loot = Loot.Instance();
            if (loot == null)
                return;

            var span = loot->Items;
            for (var i = 0; i < span.Length; i++)
            {
                var item = span[i];
                if (item.ItemId == 0 || item.ChestObjectId is 0 or 0xE0000000)
                    continue;

                var id = item.ItemId >= 1000000 ? item.ItemId - 1000000 : item.ItemId;
                var name = "";
                try
                {
                    name = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()
                                   .GetRowOrDefault(id)?.Name.ExtractText() ?? "";
                }
                catch { }

                _log.Write($"  [{i}] 「{name}」 ItemId {id}");
                _log.Write($"       RollState {item.RollState} / RollResult {item.RollResult} / LootMode {item.LootMode}");
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "ロットの内容を記録できませんでした。");
        }
    }

    // ---- 持ち物 -------------------------------------------------------------

    /// <summary>
    /// 地図の枚数を見張る。
    ///
    /// <b>解読した瞬間を捉えるのが目的。</b>
    /// 未解読の地図が1枚減り、代わりに解読済みのものが増える。
    /// この変化が「解読できた」の合図になる。自動化では、
    /// 地図を使ったあとに何を待てばよいかの判断に使う。
    /// </summary>
    private void ObserveInventory()
    {
        var maps = GameSnapshot.CountMapS5();
        var decoded = GameSnapshot.CountDecodedMap();

        var mapChanged = maps != _lastMapCount;
        var decodedChanged = decoded != _lastDecodedCount;

        if (!mapChanged && !decodedChanged)
            return;

        var beforeMaps = _lastMapCount;
        var beforeDecoded = _lastDecodedCount;

        _lastMapCount = maps;
        _lastDecodedCount = decoded;

        // 初回は「変化」ではないので、今の枚数を書くだけ。
        if (beforeMaps == int.MinValue)
        {
            _log.Write($"持ち物: 未解読の地図S5 {maps} 枚 / 解読済み {decoded} 個");
            return;
        }

        _log.WriteSection("持ち物が変わった");

        if (mapChanged)
            _log.Write($"  未解読の地図S5: {beforeMaps} → {maps} 枚");

        if (decodedChanged)
            _log.Write($"  解読済み      : {beforeDecoded} → {decoded} 個");

        // 解読の瞬間らしいなら、そう書いておく。
        if (beforeMaps > maps && decoded > beforeDecoded)
            _log.Write("  → 地図を解読したとみられる");

        _log.Write($"  表示中のUI    : {string.Join(", ", GameSnapshot.GetVisibleAddons())}");
    }

    // ---- 表示中のUI ---------------------------------------------------------

    /// <summary>
    /// 画面に出ているウィンドウを見張る。
    ///
    /// 自動化では「知らないウィンドウが出たら手を出さない」のが安全。
    /// そのために、どの場面でどのウィンドウが出るのかを先に知っておく。
    ///
    /// 宝箱を開けたとき、魔紋に入るとき、扉を選ぶとき——
    /// それぞれで何が出るかが分かれば、確実に見分けられる。
    /// </summary>
    private void ObserveAddons()
    {
        var addons = GameSnapshot.GetVisibleAddons();

        // 常に出ているもの（HUDなど）は省く。変化だけを見たい。
        var notable = addons.Where(IsNotable).ToList();
        var joined = string.Join(", ", notable);

        if (joined == _lastAddons)
            return;

        _lastAddons = joined;

        _log.Write(string.IsNullOrEmpty(joined)
            ? "UI: （注目すべきものは無し）"
            : $"UI: {joined}");

        // 初めて見るウィンドウは、中身まで書き出しておく。
        //
        // 「強欲の罠」のようなミニゲームを自動で操作するには、
        // 数字がどこに出るか、ボタンがどれかを知る必要がある。
        // 外から名前を眺めているだけでは分からないので、
        // 出会えたときに中を全部残しておく。
        foreach (var name in notable)
        {
            // 「はい／いいえ」は、同じウィンドウのまま中身だけが変わる。
            //
            // 一度きりにすると、最初に出た問いかけ（地図の解読）で枠を使い切り、
            // あとから出る肝心な問いかけ（扉をくぐるか）の文面が残らない。
            // 実際、扉の確認は1文字も記録できていなかった。
            // 中身が変わったら、その都度あらためて書き出す。
            var key = RedumpOnChange.Contains(name)
                ? $"{name}|{Fingerprint(AddonDump.ExtractTexts(name))}"
                : name;

            if (!_dumpedAddons.Add(key))
                continue;

            _log.WriteSection($"ウィンドウ「{name}」の中身");

            // まず文字だけ。数字を探すならここが手がかりになる。
            var texts = AddonDump.ExtractTexts(name);
            if (texts.Count > 0)
            {
                _log.Write("  ── 文字 ──");
                foreach (var t in texts)
                    _log.Write($"    {t}");
            }

            // 続けて全体の作り。ボタンの位置などはこちらに出る。
            _log.Write("  ── 作り ──");
            foreach (var line in AddonDump.Describe(name).Split('\n'))
            {
                var trimmed = line.TrimEnd();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    _log.Write("  " + trimmed);
            }
        }

        // 一度出たウィンドウでも、中の文字が変わったら残す。
        // ミニゲームは同じウィンドウのまま数字だけが変わるため、
        // これが無いと「何が起きたか」を追えない。
        foreach (var name in notable)
        {
            if (!IsLikelyMiniGame(name))
                continue;

            var texts = string.Join(" / ", AddonDump.ExtractTexts(name));
            if (_lastAddonTexts.TryGetValue(name, out var before) && before == texts)
                continue;

            _lastAddonTexts[name] = texts;
            _log.Write($"「{name}」の表示が変わった: {texts}");
        }
    }

    /// <summary>
    /// ミニゲームらしいウィンドウか。
    ///
    /// 名前で見分けるのは確実ではないが、
    /// 中身の変化を追うべきものを絞るために使う。
    /// 実物の名前が分かったら、ここを正確にする。
    /// </summary>
    private static bool IsLikelyMiniGame(string name)
    {
        // 確認や会話のウィンドウは、中身が変わっても追う必要がない。
        if (name.StartsWith("SelectYesno", StringComparison.Ordinal)
            || name.StartsWith("Talk", StringComparison.Ordinal)
            || name.StartsWith("SelectString", StringComparison.Ordinal))
            return false;

        // それ以外は、いちおう中身の変化を見ておく。
        // 余計に残る分には困らない。取り逃す方が痛い。
        return true;
    }

    /// <summary>
    /// 記録する価値のあるウィンドウか。
    ///
    /// HUDや常時出ているものを省く。全部書くと量が多すぎて読めない。
    /// 迷ったら残す方針。あとで減らす方が、取り逃すより良い。
    /// </summary>
    private static bool IsNotable(string name)
    {
        // 常に出ていて、判断に関係しないもの。
        string[] ignore =
        [
            "_ActionBar", "_ActionCross", "_ActionDoubleCrossL", "_ActionDoubleCrossR",
            "_BagWidget", "_CastBar", "_ChatLog", "_ChatLogPanel",
            "_DTR", "_Exp", "_FocusTargetInfo", "_Help",
            "_LimitBreak", "_MainCommand", "_Minimap", "_Money",
            "_NaviMap", "_PartyList", "_ScreenText", "_ScreenFrontText",
            "_TargetInfo", "_TargetInfoCastBar", "_TargetInfoMainTarget",
            "_TargetInfoBuffDebuff", "_ToDoList", "_TargetCursor",
            "_StatusCustom", "_Status", "_ParameterWidget", "_TargetCursorGround",
            "JobHud", "Hud", "AreaMap",
        ];

        foreach (var prefix in ignore)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    // ---- 位置 ---------------------------------------------------------------

    private void ObservePosition()
    {
        var position = PlayerHelper.Position;

        // ある程度動いたときだけ残す。細かく書くと量が膨れる。
        if (_lastLoggedPosition != Vector3.Zero
            && Vector3.Distance(position, _lastLoggedPosition) < 25f)
            return;

        _lastLoggedPosition = position;
        _log.Write($"移動中  {RunLog.Format(position)}");
    }

    // ---- 扉 -----------------------------------------------------------------

    /// <summary>
    /// 扉の様子を見張る。
    ///
    /// 魔紋の部屋には扉が2つあり、片方が正解。外すと追い出される。
    /// 低い確率で、正解の扉に目立つ演出が付く。
    ///
    /// 何を見れば「光っている」と分かるのかは、まだ突き止められていない。
    /// そこで、手がかりになりそうな値をまとめて残しておく。
    /// 実際に演出が出たときの記録と、出なかったときの記録を見比べれば、
    /// どの値が変わるのかが分かる。
    /// </summary>
    private void ObserveDoors()
    {
        // 魔紋の中でだけ見る。フィールドの仕掛けは対象外。
        if (!VaultRoutine.IsInsideVault())
            return;

        var doors = DoorInspector.Inspect();
        if (doors.Count == 0)
        {
            if (_lastDoorFingerprint != "")
            {
                _lastDoorFingerprint = "";
                _log.Write("扉が見えなくなった");
            }
            return;
        }

        var fingerprint = DoorInspector.Fingerprint(doors);
        if (fingerprint == _lastDoorFingerprint)
            return;

        _lastDoorFingerprint = fingerprint;

        _log.WriteSection($"扉の様子が変わった（{doors.Count} 個）");
        foreach (var line in DoorInspector.Describe(doors))
            _log.Write(line);
    }

    // ---- 機械で読む用の控え -------------------------------------------------

    /// <summary>
    /// 今の様子をまとめて1行のJSONで残す。
    ///
    /// 人が読む方の記録は流れを追うためのもの。こちらは、
    /// あとから「この場面をどう見分けるか」を洗い出すために使う。
    /// 一定の間隔で、そのときの全体像を残しておく。
    /// </summary>
    private void WriteSnapshot()
    {
        if (!EzThrottler.Throttle("AutoTreasure.Snapshot", 2000))
            return;

        try
        {
            var info = GameSnapshot.GetTerritoryInfo();
            var position = PlayerHelper.Position;
            var addons = GameSnapshot.GetVisibleAddons().Where(IsNotable);
            var flag = MapFlagReader.Read();

            var sb = new System.Text.StringBuilder(512);
            sb.Append('{');
            sb.Append($"\"t\":\"{DateTime.Now:O}\"");
            // 誰の記録かを必ず入れる。3台分を突き合わせるときに要る。
            sb.Append($",\"who\":\"{RunLog.Escape(RunLog.CharacterName)}\"");
            sb.Append($",\"role\":\"{Plugin.Config.Role}\"");
            sb.Append($",\"who\":\"{RunLog.Escape(RunLog.CharacterName)}\"");
            sb.Append($",\"territory\":{info.TerritoryType}");
            sb.Append($",\"place\":\"{RunLog.Escape(info.PlaceName)}\"");
            sb.Append($",\"contentType\":{info.ContentType}");
            sb.Append($",\"cfc\":{info.ContentFinderConditionId}");
            sb.Append($",\"map\":{info.MapId}");
            sb.Append($",\"pos\":{{\"x\":{position.X:F2},\"y\":{position.Y:F2},\"z\":{position.Z:F2}}}");
            sb.Append($",\"inCombat\":{Lower(PlayerHelper.InCombat)}");
            sb.Append($",\"mounted\":{Lower(PlayerHelper.IsMounted)}");
            sb.Append($",\"flying\":{Lower(PlayerHelper.IsFlying)}");
            sb.Append($",\"betweenAreas\":{Lower(PlayerHelper.IsBetweenAreas)}");
            sb.Append($",\"cutscene\":{Lower(PlayerHelper.IsInCutscene)}");
            sb.Append($",\"ready\":{Lower(PlayerHelper.IsReady)}");
            sb.Append($",\"party\":{GameSnapshot.PartyCount}");
            sb.Append($",\"mapS5\":{GameSnapshot.CountMapS5()}");
            sb.Append($",\"decodedMap\":{GameSnapshot.CountDecodedMap()}");
            sb.Append($",\"treasures\":{ObjectHelper.GetTreasures().Count}");
            sb.Append($",\"eventObjs\":{ObjectHelper.GetEventObjects().Count}");

            // 触れないものも含めた数。
            //
            // eventObjs だけでは、0 件だったときに
            // 「湧いていない」のか「触れないだけ」なのか区別できない。
            // 2026-09-18 にメンバー3台が25分止まった件で、
            // どちらなのかが分からず原因を絞り込めなかった。
            sb.Append($",\"eventObjsAll\":{ObjectHelper.GetEventObjectsIncludingUntargetable().Count}");
            sb.Append($",\"hostiles\":{ObjectHelper.CountLivingEnemies(50f)}");
            sb.Append($",\"hasLoot\":{Lower(LootHelper.HasPendingLoot())}");
            sb.Append($",\"target\":\"{RunLog.Escape(GameSnapshot.DescribeTarget())}\"");

            if (flag != null)
            {
                sb.Append($",\"flag\":{{\"territory\":{flag.Value.TerritoryType}");
                sb.Append($",\"x\":{flag.Value.X:F2},\"z\":{flag.Value.Z:F2}}}");
            }

            sb.Append(",\"addons\":[");
            var first = true;
            foreach (var a in addons)
            {
                if (!first) sb.Append(',');
                sb.Append($"\"{RunLog.Escape(a)}\"");
                first = false;
            }
            sb.Append(']');

            sb.Append('}');

            _log.WriteJson(sb.ToString());
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "様子をJSONで残せませんでした。");
        }
    }

    private static string Lower(bool value) => value ? "true" : "false";

    // ---- 外から呼ぶ記録 -----------------------------------------------------

    /// <summary>アクションを使ったことを記録する。</summary>
    internal void RecordAction(uint actionId, string label)
    {
        _log.Write($"アクション使用: {label}（ID {actionId}）  位置 {RunLog.Format(PlayerHelper.Position)}");
    }

    /// <summary>何かに触れたことを記録する。</summary>
    internal void RecordInteract(IGameObject obj)
    {
        _log.Write($"触れた: 「{obj.Name.TextValue}」 DataId {obj.BaseId}  距離 {ObjectHelper.DistanceToPlayer(obj):F1} m");
    }
}
