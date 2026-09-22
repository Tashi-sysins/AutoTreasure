using System.Net.WebSockets;
using AutoTreasure.RelayServer.Rooms;
using AutoTreasure.Sync.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoTreasure.SyncTests;

public class RelayReconnectTests
{
    [Fact]
    public async Task ReloadedLeaderGetsWorkingKeysWithoutRemovingWorkers()
    {
        var manager = new RoomManager(new InviteBoard(), NullLogger<RoomManager>.Instance);
        using var firstSocket = new ClientWebSocket();
        using var workerSocket = new ClientWebSocket();
        using var nextSocket = new ClientWebSocket();
        using var joinSocket = new ClientWebSocket();
        using var resumeSocket = new ClientWebSocket();
        var identity = new RelayIdentity { ClientId = "leader", Character = "leader", ContentId = 1 };
        var first = await manager.CreateRoomAsync(identity, firstSocket, "test-party");
        var worker = await manager.JoinRoomAsync(first.Room!.RoomCode, first.JoinToken,
            new RelayIdentity { ClientId = "worker", ContentId = 2 }, workerSocket);
        Assert.True(worker.Ok);
        await manager.MarkDisconnectedAsync(first.Room, first.Member!);

        var next = await manager.CreateRoomAsync(identity, nextSocket, "test-party");
        Assert.Same(first.Room, next.Room);
        Assert.True(next.Room!.Members.ContainsKey(worker.Member!.MemberId));
        Assert.False(string.IsNullOrWhiteSpace(next.JoinToken));
        Assert.False(string.IsNullOrWhiteSpace(next.ResumeToken));
        Assert.True(RoomCode.TokenMatches(next.JoinToken, next.Room.JoinTokenHash));
        Assert.True(RoomCode.TokenMatches(next.ResumeToken, next.Member!.ResumeTokenHash));

        var stale = await manager.JoinRoomAsync(next.Room.RoomCode, first.JoinToken,
            new RelayIdentity { ClientId = "new-worker", ContentId = 3 }, joinSocket);
        Assert.Equal(RelayError.InviteInvalid, stale.ErrorCode);
        var joined = await manager.JoinRoomAsync(next.Room.RoomCode, next.JoinToken,
            new RelayIdentity { ClientId = "new-worker", ContentId = 3 }, joinSocket);
        Assert.True(joined.Ok);
        await manager.MarkDisconnectedAsync(next.Room, next.Member);
        var resumed = await manager.ResumeRoomAsync(next.Room.RoomCode, next.Member.MemberId,
            next.ResumeToken, resumeSocket);
        Assert.True(resumed.Ok);

        foreach (var member in next.Room.Members.Values)
            if (member.Sender is { } sender) await sender.DisposeAsync();
    }
}
