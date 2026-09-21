using System.Text.Json.Serialization;

namespace AutoTreasure.Sync.Relay;

/// <summary>
/// 中継サーバーとやり取りする1通の外側。
///
/// なぜ既存の <see cref="AutoTreasure.Sync.SyncMessage"/> をそのまま流さないか:
///   既存のものは「同じPCの中で合図を送り合う」ための形で、
///   ルームの作成・参加・退出という概念を持っていない。
///
///   そこで外側にこの封筒をかぶせ、中身（Payload）へ
///   既存の SyncMessage を**文字列のまま**入れて運ぶ。
///   こうすると**既存の同期処理を一切変えずに**経路だけ差し替えられる。
///
/// ⚠ MogColle は Payload に JSON オブジェクトを入れていたが、
///   AutoTreasure は「Kind|引数|引数」の文字列を1本入れる。
///   サーバーが中身を見分ける方法もそれに合わせてある
///   （RoomManager.ReadKind を参照）。
/// </summary>
public sealed class RelayEnvelope
{
    /// <summary>取り決めの版。合わないものは断る。</summary>
    [JsonPropertyName("relayVersion")]
    public int RelayVersion { get; set; } = CurrentVersion;

    /// <summary>この版。互換性が壊れる変更をしたら上げる。</summary>
    public const int CurrentVersion = 1;

    /// <summary>種類。<see cref="RelayMessageType"/> のいずれか。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>返事を突き合わせるための番号。</summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    /// <summary>ルームの合言葉（表示用の短いコード）。</summary>
    [JsonPropertyName("roomCode")]
    public string? RoomCode { get; set; }

    /// <summary>
    /// 参加するときに示す合鍵。
    ///
    /// 表示用のコードとは別にする。コードだけで入れると、
    /// 総当たりで他人のルームへ入られうる。
    /// </summary>
    [JsonPropertyName("joinToken")]
    public string? JoinToken { get; set; }

    /// <summary>
    /// 切れた後に同じ人として戻るための合鍵。
    ///
    /// サーバーは**ハッシュだけ**を持ち、本体は持たない。
    /// </summary>
    [JsonPropertyName("resumeToken")]
    public string? ResumeToken { get; set; }

    /// <summary>ルームの中での自分の番号。</summary>
    [JsonPropertyName("memberId")]
    public string? MemberId { get; set; }

    /// <summary>取りまとめ役か参加側か。サーバーが決めた値を正とする。</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>中身。SyncMessage の文字列か、ルーム制御の情報。</summary>
    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement? Payload { get; set; }

    /// <summary>断った理由。<see cref="RelayError"/> のいずれか。</summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    /// <summary>人が読む説明。</summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>封筒の種類。</summary>
public static class RelayMessageType
{
    // ---- クライアント → サーバー ----

    /// <summary>取りまとめ役がルームを作る。</summary>
    public const string CreateRoom = "create_room";

    /// <summary>参加側がルームへ入る。</summary>
    public const string JoinRoom = "join_room";

    /// <summary>切れた後に同じ人として戻る。</summary>
    public const string ResumeRoom = "resume_room";

    /// <summary>自分から抜ける。</summary>
    public const string LeaveRoom = "leave_room";

    /// <summary>生存応答。</summary>
    public const string Pong = "pong";

    // ---- サーバー → クライアント ----

    /// <summary>作れた。合言葉と合鍵を返す。</summary>
    public const string RoomCreated = "room_created";

    /// <summary>入れた。</summary>
    public const string RoomJoined = "room_joined";

    /// <summary>いま誰が居るか。</summary>
    public const string MemberList = "member_list";

    /// <summary>生存確認。</summary>
    public const string Ping = "ping";

    /// <summary>断った。</summary>
    public const string Error = "error";

    /// <summary>ルームが終わった。</summary>
    public const string RoomClosed = "room_closed";

    // ---- 両方向 ----

    /// <summary>既存の SyncMessage（文字列）を運ぶ。</summary>
    public const string Relay = "relay";

    // ---- 招待を自動で渡す ----
    //
    // なぜ要るか:
    //   これまでは取りまとめ役が招待をコピーし、チャット等で伝え、
    //   参加側が貼り付けて押す、という手順だった。
    //
    //   同じパーティーに居るなら、その事実だけで十分なはず。
    //   サーバーへ「このパーティーの招待はこれ」と預け、
    //   同じパーティーの人だけが受け取れるようにする。

    /// <summary>取りまとめ役が、自分のパーティー用の招待を預ける。</summary>
    public const string PublishInvite = "publish_invite";

    /// <summary>参加側が、自分のパーティーの招待を尋ねる。</summary>
    public const string RequestInvite = "request_invite";

    /// <summary>尋ねへの返事。見つからなければ招待は空。</summary>
    public const string InviteAnswer = "invite_answer";

    /// <summary>
    /// 取りまとめ役が、ルームから誰かを外す。
    ///
    /// なぜ要るか（実機で起きた）:
    ///   パーティーを抜けた人が「再接続中」のまま居座り、
    ///   新しく入れた人の席が埋まらなかった。
    ///
    ///   中継では接続をサーバーが握っているので、
    ///   取りまとめ役から頼まないと外せない。
    /// </summary>
    public const string KickMember = "kick_member";

    /// <summary>
    /// 外した相手の締め出しを解く。
    ///
    /// 外された側は招待を持ったままなので、サーバーは
    /// しばらく入れないようにしている（でないと戻ってくる）。
    ///
    /// パーティーへ入れ直したときは、その締め出しを解かないと
    /// 待ち時間のあいだ参加できない。
    /// </summary>
    public const string AllowMember = "allow_member";
}

/// <summary>役割。サーバーが決めた値を正とする。</summary>
public static class RelayRole
{
    public const string Leader = "leader";
    public const string Worker = "worker";
}

/// <summary>断った理由。画面では日本語に直して出す。</summary>
public static class RelayError
{
    /// <summary>そのルームが無い、または期限切れ。</summary>
    public const string RoomNotFound = "ROOM_NOT_FOUND";

    /// <summary>合鍵が違う。</summary>
    public const string InviteInvalid = "INVITE_INVALID";

    /// <summary>もう満員（8人）。</summary>
    public const string RoomFull = "ROOM_FULL";

    /// <summary>取り決めの版が合わない。</summary>
    public const string VersionMismatch = "VERSION_MISMATCH";

    /// <summary>その役割では送れない（参加側が命令を送った等）。</summary>
    public const string RoleForbidden = "ROLE_FORBIDDEN";

    /// <summary>1通が大きすぎる。</summary>
    public const string MessageTooLarge = "MESSAGE_TOO_LARGE";

    /// <summary>送りすぎ。</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>戻るための合鍵が違う。</summary>
    public const string ResumeInvalid = "RESUME_INVALID";

    /// <summary>取りまとめ役が戻ってこなかった。</summary>
    public const string LeaderGone = "LEADER_GONE";

    /// <summary>知らない種類。</summary>
    public const string UnknownType = "UNKNOWN_TYPE";

    /// <summary>まだルームに入っていない。</summary>
    public const string NotInRoom = "NOT_IN_ROOM";

    /// <summary>そのパーティーの招待がまだ預けられていない。</summary>
    public const string InviteNotReady = "INVITE_NOT_READY";

    /// <summary>人が読む説明にする。</summary>
    public static string Describe(string? code) => code switch
    {
        RoomNotFound => "そのルームは見つかりません（終了したか、期限が切れています）",
        InviteInvalid => "招待情報が違います",
        RoomFull => "そのルームは既に満員です（最大8人）",
        VersionMismatch => "プラグインの版が合いません。全員を同じ版にしてください",
        RoleForbidden => "その操作は取りまとめ役だけができます",
        MessageTooLarge => "送ろうとした内容が大きすぎます",
        RateLimited => "送信が多すぎます。少し待ってください",
        ResumeInvalid => "復帰できませんでした。入り直してください",
        LeaderGone => "取りまとめ役との接続が切れたため、ルームを終了しました",
        UnknownType => "知らない種類の通信です",
        NotInRoom => "まだルームに入っていません",
        InviteNotReady => "取りまとめ役がまだ準備できていません",
        _ => "接続できませんでした",
    };
}

/// <summary>ルームに居る1人分（画面表示用）。</summary>
public sealed class RelayMember
{
    [JsonPropertyName("memberId")]
    public string MemberId { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("character")]
    public string Character { get; set; } = string.Empty;

    [JsonPropertyName("contentId")]
    public ulong ContentId { get; set; }

    /// <summary>connected / reconnecting のいずれか。</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = MemberStatus.Connected;
}

/// <summary>参加者の様子。</summary>
public static class MemberStatus
{
    public const string Connected = "connected";

    /// <summary>切れたが、まだ戻れる猶予の内。</summary>
    public const string Reconnecting = "reconnecting";
}

/// <summary>
/// 招待を預ける・受け取るときの中身。
///
/// パーティーの識別には、**リーダーのキャラクターIDのハッシュ**を使う。
/// 生のIDを送らないのは、個人を特定しうる値だから。
///
/// ハッシュなら、同じパーティーの人だけが同じ値を作れる。
/// サーバーは誰のパーティーかを知らないまま、取り次げる。
/// </summary>
public sealed class InvitePayload
{
    /// <summary>パーティーリーダーのIDから作った合言葉。</summary>
    [JsonPropertyName("partyKey")]
    public string PartyKey { get; set; } = string.Empty;

    /// <summary>招待文字列。尋ねるときは空。</summary>
    [JsonPropertyName("invite")]
    public string Invite { get; set; } = string.Empty;
}

/// <summary>誰を外すかを伝える。</summary>
public sealed class KickPayload
{
    /// <summary>外す相手のキャラクターID。</summary>
    [JsonPropertyName("contentId")]
    public ulong ContentId { get; set; }
}

/// <summary>ルームを作る・入るときに名乗る内容。</summary>
public sealed class RelayIdentity
{
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("character")]
    public string Character { get; set; } = string.Empty;

    [JsonPropertyName("contentId")]
    public ulong ContentId { get; set; }
}

/// <summary>ルームの中身（member_list の payload）。</summary>
public sealed class RelayRoomSnapshot
{
    [JsonPropertyName("roomCode")]
    public string RoomCode { get; set; } = string.Empty;

    [JsonPropertyName("members")]
    public RelayMember[] Members { get; set; } = [];
}
