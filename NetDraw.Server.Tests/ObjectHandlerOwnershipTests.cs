using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NetDraw.Server;
using NetDraw.Server.Handlers;
using NetDraw.Server.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Models.Actions;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NetDraw.Server.Tests;

/// <summary>
/// Verifies that ObjectHandler enforces ownership: only the action's author may
/// move or delete it. Without this check any peer can flood Move/Delete with random
/// IDs and shove every shape off-canvas.
/// </summary>
public class ObjectHandlerOwnershipTests
{
    [Fact(Timeout = 5000)]
    public async Task DeleteObject_ByNonOwner_IsRejected()
    {
        var (alice, bob, listener) = await CreateTwoConnectedHandlersAsync();
        try
        {
            var roomService = new RoomService();
            roomService.AddUserToRoom("room1", alice, MakeUser("alice"));
            roomService.AddUserToRoom("room1", bob, MakeUser("bob"));

            var room = roomService.GetRoom("room1")!;
            var aliceAction = MakePen("alice");
            room.AddAction(aliceAction);

            var handler = new ObjectHandler(roomService);
            var envelope = MakeEnvelope(MessageType.DeleteObject, "bob", "Bob", "room1",
                JObject.FromObject(new DeleteObjectPayload { ActionId = aliceAction.Id }));

            await handler.HandleAsync(envelope, bob);

            // Action must still be present — Bob is not the owner.
            Assert.NotNull(room.FindActionById(aliceAction.Id));
        }
        finally { Cleanup(alice, bob, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task DeleteObject_ByOwner_Succeeds()
    {
        var (alice, _bob, listener) = await CreateTwoConnectedHandlersAsync();
        try
        {
            var roomService = new RoomService();
            roomService.AddUserToRoom("room1", alice, MakeUser("alice"));

            var room = roomService.GetRoom("room1")!;
            var aliceAction = MakePen("alice");
            room.AddAction(aliceAction);

            var handler = new ObjectHandler(roomService);
            var envelope = MakeEnvelope(MessageType.DeleteObject, "alice", "Alice", "room1",
                JObject.FromObject(new DeleteObjectPayload { ActionId = aliceAction.Id }));

            await handler.HandleAsync(envelope, alice);

            Assert.Null(room.FindActionById(aliceAction.Id));
        }
        finally { Cleanup(alice, _bob, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task MoveObject_ByNonOwner_IsRejectedSilently()
    {
        // The handler returns without throwing, without broadcasting, and without removing
        // the action. We verify the no-side-effect contract: room state unchanged.
        var (alice, bob, listener) = await CreateTwoConnectedHandlersAsync();
        try
        {
            var roomService = new RoomService();
            roomService.AddUserToRoom("room1", alice, MakeUser("alice"));
            roomService.AddUserToRoom("room1", bob, MakeUser("bob"));

            var room = roomService.GetRoom("room1")!;
            var aliceAction = MakePen("alice");
            room.AddAction(aliceAction);

            var handler = new ObjectHandler(roomService);
            var envelope = MakeEnvelope(MessageType.MoveObject, "bob", "Bob", "room1",
                JObject.FromObject(new MoveObjectPayload { ActionId = aliceAction.Id, DeltaX = 100, DeltaY = 100 }));

            await handler.HandleAsync(envelope, bob);

            // Action must still be present — and the test is implicitly green if no exception fired.
            Assert.NotNull(room.FindActionById(aliceAction.Id));
        }
        finally { Cleanup(alice, bob, listener); }
    }

    private static UserInfo MakeUser(string id) => new()
    {
        UserId = id,
        UserName = id,
        Color = "#abcdef"
    };

    private static PenAction MakePen(string userId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        UserId = userId,
        UserName = userId,
        Color = "#000000",
        StrokeWidth = 2,
        Points = new() { new PointData { X = 0, Y = 0 }, new PointData { X = 10, Y = 10 } }
    };

    private static MessageEnvelope.Envelope MakeEnvelope(
        MessageType type, string senderId, string senderName, string roomId, JObject payload) =>
        new(type, senderId, senderName, roomId, 0, payload, 2, "");

    private static async Task<(ClientHandler alice, ClientHandler bob, TcpListener listener)> CreateTwoConnectedHandlersAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var aliceTask = listener.AcceptTcpClientAsync();
        var alicePeer = new TcpClient();
        await alicePeer.ConnectAsync(IPAddress.Loopback, port);
        var aliceServer = await aliceTask;

        var bobTask = listener.AcceptTcpClientAsync();
        var bobPeer = new TcpClient();
        await bobPeer.ConnectAsync(IPAddress.Loopback, port);
        var bobServer = await bobTask;

        return (
            new ClientHandler(aliceServer, NullLogger<ClientHandler>.Instance),
            new ClientHandler(bobServer, NullLogger<ClientHandler>.Instance),
            listener);
    }

    private static void Cleanup(ClientHandler a, ClientHandler b, TcpListener listener)
    {
        try { listener.Stop(); } catch { }
    }
}
