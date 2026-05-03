using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NetDraw.Server;
using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json.Linq;
using Xunit;

namespace NetDraw.Server.Tests;

public class MessageDispatcherTokenTests
{
    [Fact(Timeout = 5000)]
    public async Task NonJoinRoom_WithoutToken_RejectsWithAuthTokenMismatch()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            handler.SessionTokenBytes = MakeToken();
            handler.SessionToken = Base64UrlEncoding.Encode(handler.SessionTokenBytes!);
            var dispatcher = MakeDispatcher();

            var envelope = new MessageEnvelope.Envelope(
                MessageType.ChatMessage, "alice", "Alice", "room1", 0, new JObject(), 2, "");

            await dispatcher.DispatchAsync(envelope, handler);

            var line = await ReadLineAsync(peerStream);
            var reply = MessageEnvelope.Parse(line);
            Assert.NotNull(reply);
            Assert.Equal(MessageType.Error, reply!.Type);
            var payload = MessageEnvelope.DeserializePayload<ErrorPayload>(reply.RawPayload);
            Assert.NotNull(payload);
            Assert.Equal(ErrorCodes.AuthTokenMismatch, payload!.Code);
        }
        finally { Cleanup(handler, peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task NonJoinRoom_WithLengthMismatchToken_Rejects()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            handler.SessionTokenBytes = MakeToken();
            handler.SessionToken = Base64UrlEncoding.Encode(handler.SessionTokenBytes!);
            var dispatcher = MakeDispatcher();

            // 16-byte token presented against 32-byte expected.
            var shortBytes = new byte[16];
            RandomNumberGenerator.Fill(shortBytes);
            var envelope = new MessageEnvelope.Envelope(
                MessageType.ChatMessage, "alice", "Alice", "room1", 0, new JObject(), 2,
                Base64UrlEncoding.Encode(shortBytes));

            await dispatcher.DispatchAsync(envelope, handler);

            var line = await ReadLineAsync(peerStream);
            var reply = MessageEnvelope.Parse(line);
            Assert.NotNull(reply);
            var payload = MessageEnvelope.DeserializePayload<ErrorPayload>(reply!.RawPayload);
            Assert.Equal(ErrorCodes.AuthTokenMismatch, payload!.Code);
        }
        finally { Cleanup(handler, peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task NonJoinRoom_WithDifferentTokenContent_Rejects()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            handler.SessionTokenBytes = MakeToken();
            handler.SessionToken = Base64UrlEncoding.Encode(handler.SessionTokenBytes!);
            var dispatcher = MakeDispatcher();

            var wrong = MakeToken();
            var envelope = new MessageEnvelope.Envelope(
                MessageType.ChatMessage, "alice", "Alice", "room1", 0, new JObject(), 2,
                Base64UrlEncoding.Encode(wrong));

            await dispatcher.DispatchAsync(envelope, handler);

            var line = await ReadLineAsync(peerStream);
            var reply = MessageEnvelope.Parse(line);
            Assert.NotNull(reply);
            var payload = MessageEnvelope.DeserializePayload<ErrorPayload>(reply!.RawPayload);
            Assert.Equal(ErrorCodes.AuthTokenMismatch, payload!.Code);
        }
        finally { Cleanup(handler, peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task NonJoinRoom_WithMatchingToken_DispatchesToHandler()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            handler.SessionTokenBytes = MakeToken();
            handler.SessionToken = Base64UrlEncoding.Encode(handler.SessionTokenBytes!);
            handler.UserId = "alice";
            var dispatcher = MakeDispatcher();

            var probe = new ProbeHandler();
            dispatcher.Register(probe);

            var envelope = new MessageEnvelope.Envelope(
                MessageType.ChatMessage, "alice", "Alice", "room1", 0, new JObject(), 2,
                handler.SessionToken);

            await dispatcher.DispatchAsync(envelope, handler);

            Assert.True(probe.Invoked);
        }
        finally { Cleanup(handler, peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task NonJoinRoom_WithMatchingToken_ButHijackedSenderId_IsRejected()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            handler.SessionTokenBytes = MakeToken();
            handler.SessionToken = Base64UrlEncoding.Encode(handler.SessionTokenBytes!);
            handler.UserId = "alice";

            var dispatcher = MakeDispatcher();
            var probe = new ProbeHandler();
            dispatcher.Register(probe);

            var envelope = new MessageEnvelope.Envelope(
                MessageType.ChatMessage, "bob", "Bob", "room1", 0, new JObject(), 2,
                handler.SessionToken);

            await dispatcher.DispatchAsync(envelope, handler);

            Assert.False(probe.Invoked);
        }
        finally { Cleanup(handler, peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task JoinRoom_WithoutToken_BypassesValidation()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            // SessionTokenBytes deliberately null — pre-issuance state.
            var dispatcher = MakeDispatcher();
            var probe = new ProbeHandler { TypesHandled = MessageType.JoinRoom };
            dispatcher.Register(probe);

            var envelope = new MessageEnvelope.Envelope(
                MessageType.JoinRoom, "alice", "Alice", "room1", 0, new JObject(), 2, "");

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

    private static MessageDispatcher MakeDispatcher() =>
        new(new TokenBucketRateLimiter(capacity: 200, refillPerSec: 50), NullLogger<MessageDispatcher>.Instance);

    // Builds a real ClientHandler attached to one end of a loopback TCP pair.
    // The peer end is returned so tests can read back error replies the dispatcher writes.
    private static async Task<(ClientHandler, NetworkStream, TcpListener)> CreateConnectedHandlerAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();
        var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, port);
        var server = await acceptTask;

        var handler = new ClientHandler(server, server.GetStream(), NullLogger<ClientHandler>.Instance);
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
