using System;

namespace AutoTreasure.Sync;

/// <summary>
/// 連携の口。
///
/// 同一PC（名前付きパイプ）と、インターネット（中継サーバー）の
/// 両方がこの形になる。
///
/// <b>RunController はこの形しか知らない。</b>
/// どちらで繋いでいるかを気にしなくてよいので、
/// 経路を足しても RunController 側の約30箇所は一切触らずに済む。
///
/// ここに並ぶ8つは、PipeSync が元々公開していたものと同じ。
/// 新しく考えた形ではなく、**既にある使われ方をそのまま写した**もの。
/// </summary>
internal interface ISyncTransport : IDisposable
{
    /// <summary>いまリーダーとして動いているか、メンバーか、止まっているか。</summary>
    SyncMode Mode { get; }

    /// <summary>画面に出す短い状態。</summary>
    string StatusText { get; }

    /// <summary>
    /// いま繋がっている台数（自分を除く）。
    ///
    /// <b>待ち合わせの人数決めに使う。</b>
    /// PartyRoleDetector.ResolveBarrierMembers がこの値を見て、
    /// 「繋がっていない相手は待っても来ない」と判断する。
    /// </summary>
    int ConnectedClients { get; }

    /// <summary>1台でも繋がっているか。</summary>
    bool IsConnected { get; }

    /// <summary>リーダーとして始める。</summary>
    void StartAsLeader();

    /// <summary>メンバーとして始める。</summary>
    void StartAsMember();

    /// <summary>止める。</summary>
    void Stop();

    /// <summary>1通送る。送れなくても例外は投げない。</summary>
    void Send(SyncMessage message);

    /// <summary>
    /// 受け取ったものを1つ取り出す。
    ///
    /// <b>受信スレッドからゲームを触らないための要。</b>
    /// 受信は裏で行い、積むだけにする。
    /// ゲームのスレッドがここから取り出して処理する。
    /// </summary>
    bool TryReceive(out SyncMessage message);
}
