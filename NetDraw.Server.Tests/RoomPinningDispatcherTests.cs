using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NetDraw.Server;
using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NetDraw.Server.Tests;

/// <summary>
/// Verifies that the dispatcher rejects per-room messages whose envelope.roomId does
/// not match the room the connection joined. Without this guard a member of room X
/// could spoof Draw{roomId=Y} and the handlers would broadcast into Y. The token
/// match alone does not catch this — the token is connection-bound, not room-bound.
/// </summary>
public class RoomPinningDispatcherTests
{
    [Fact(Timeout = 5000)]
    public async Task SpoofedRoomId_IsRejectedWithAuthError()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            handler.SessionTokenBytes = MakeToken();
            handler.SessionToken = Base64UrlEncoding.Encode(handler.SessionTokenBytes!);
            handler.UserId = "alice";

            // Pin alice into roomA.
            var roomService = new RoomService();
            var added = roomService.AddUserToRoom("roomA", handler, new UserInfo
            {
                UserId = "alice",
                UserName = "Alice",
                Color = "#abcdef"
            });
            Assert.Equal(JoinResult.Ok, added);

            var dispatcher = new MessageDispatcher(
                new TokenBucketRateLimiter(capacity: 200, refillPerSec: 50),
                roomService,
                NullLogger<MessageDispatcher>.Instance);

            var probe = new ProbeHandler();
            dispatcher.Register(probe);

            // Spoof: pretend we are sending into roomB even though we joined roomA.
            var envelope = new MessageEnvelope.Envelope(
                MessageType.ChatMessage, "alice", "Alice", "roomB", 0, new JObject(), 2,
                handler.SessionToken);

            await dispatcher.DispatchAsync(envelope, handler);

            // Handler must NOT have been invoked.
            Assert.False(probe.Invoked);

            // Server should have written an error reply with AuthTokenMismatch.
            var line = await ReadLineAsync(peerStream);
            var reply = MessageEnvelope.Parse(line);
            Assert.NotNull(reply);
            Assert.Equal(MessageType.Error, reply!.Type);
            var payload = MessageEnvelope.DeserializePayload<ErrorPayload>(reply.RawPayload);
            Assert.Equal(ErrorCodes.AuthTokenMismatch, payload!.Code);
        }
        finally { Cleanup(handler, peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task MatchingRoomId_DispatchesToHandler()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            handler.SessionTokenBytes = MakeToken();
            handler.SessionToken = Base64UrlEncoding.Encode(handler.SessionTokenBytes!);
            handler.UserId = "alice";

            var roomService = new RoomService();
            roomService.AddUserToRoom("roomA", handler, new UserInfo
            {
                UserId = "alice",
                UserName = "Alice",
                Color = "#abcdef"
            });

            var dispatcher = new MessageDispatcher(
                new TokenBucketRateLimiter(capacity: 200, refillPerSec: 50),
                roomService,
                NullLogger<MessageDispatcher>.Instance);

            var probe = new ProbeHandler();
            dispatcher.Register(probe);

            var envelope = new MessageEnvelope.Envelope(
                MessageType.ChatMessage, "alice", "Alice", "roomA", 0, new JObject(), 2,
                handler.SessionToken);

            await dispatcher.DispatchAsync(envelope, handler);

            Assert.True(probe.Invoked);
        }
        finally { Cleanup(handler, peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task JoinRoom_IsExemptFromRoomPinning()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            // No prior pinning — JoinRoom must work even though GetRoomIdForClient is null.
            var roomService = new RoomService();
            var dispatcher = new MessageDispatcher(
                new TokenBucketRateLimiter(capacity: 200, refillPerSec: 50),
                roomService,
                NullLogger<MessageDispatcher>.Instance);

            var probe = new ProbeHandler { TypesHandled = MessageType.JoinRoom };
            dispatcher.Register(probe);

            var envelope = new MessageEnvelope.Envelope(
                MessageType.JoinRoom, "alice", "Alice", "roomA", 0, new JObject(), 2, "");

            await dispatcher.DispatchAsync(envelope, handler);

            Assert.True(probe.Invoked);
        }
        finally { Cleanup(handler, peerStream, listener); }
    }

    private static byte[] MakeToken()
    {
        var b = new byte[32];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static async Task<(ClientHandler, NetworkStream, TcpListener)> CreateConnectedHandlerAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();
        var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, port);
        var server = await acceptTask;

        var handler = new ClientHandler(server, NullLogger<ClientHandler>.Instance);
        return (handler, peer.GetStream(), listener);
    }

    private static void Cleanup(ClientHandler handler, NetworkStream peerStream, TcpListener listener)
    {
        try { peerStream.Close(); } catch { }
        try { listener.Stop(); } catch { }
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!cts.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(buf, cts.Token);
            if (n == 0) break;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            int idx = sb.ToString().IndexOf('\n');
            if (idx >= 0) return sb.ToString()[..idx];
        }
        throw new TimeoutException("No reply received");
    }

    private sealed class ProbeHandler : IMessageHandler
    {
        public MessageType TypesHandled { get; set; } = MessageType.ChatMessage;
        public bool Invoked { get; private set; }
        public bool CanHandle(MessageType type) => type == TypesHandled;
        public Task HandleAsync(MessageEnvelope.Envelope envelope, ClientHandler sender)
        {
            Invoked = true;
            return Task.CompletedTask;
        }
    }
}
