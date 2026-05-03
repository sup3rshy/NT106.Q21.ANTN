using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NetDraw.Server;
using NetDraw.Server.Handlers;
using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Xunit;

namespace NetDraw.Server.Tests;

public class ResumeHandlerTests
{
    [Fact(Timeout = 5000)]
    public async Task Resume_with_valid_token_restores_room()
    {
        var fixture = await Fixture.StartAsync(graceSeconds: 10);
        try
        {
            using var tcp = new TcpClient();
            await fixture.ConnectAsync(tcp);
            using var stream = tcp.GetStream();

            const string roomId = "resume-room";
            const string userId = "u-resume";

            await SendAsync(stream, NetMessage<UserPayload>.Create(
                MessageType.JoinRoom, userId, "Alice", roomId,
                new UserPayload { User = new UserInfo { UserId = userId, UserName = "Alice" } }));

            var joinedLine = await ReadLineAsync(stream);
            var joinedEnv = MessageEnvelope.Parse(joinedLine)!;
            var joined = MessageEnvelope.DeserializePayload<RoomJoinedPayload>(joinedEnv.RawPayload)!;
            var token = joined.SessionToken;
            Assert.False(string.IsNullOrEmpty(token));

            // Drop the TCP. Server's Disconnected handler defers cleanup for the grace
            // window, but MarkOrphaned has to actually run before TryClaim can succeed —
            // the disconnect callback is async. Poll the store with a hard ceiling.
            tcp.Close();
            var orphanDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < orphanDeadline)
            {
                var entry = fixture.Store.Get(token);
                if (entry?.Handler is null) break;
                await Task.Delay(20);
            }

            // Reconnect and Resume.
            using var tcp2 = new TcpClient();
            await fixture.ConnectAsync(tcp2);
            using var stream2 = tcp2.GetStream();

            await SendAsync(stream2, NetMessage<ResumePayload>.Create(
                MessageType.Resume, userId, "Alice", roomId,
                new ResumePayload { Token = token }));

            var resumeLine = await ReadLineAsync(stream2);
            var resumeEnv = MessageEnvelope.Parse(resumeLine)!;
            Assert.Equal(MessageType.ResumeAccepted, resumeEnv.Type);
            var resumePayload = MessageEnvelope.DeserializePayload<RoomJoinedPayload>(resumeEnv.RawPayload)!;
            Assert.Equal(roomId, resumePayload.Room.RoomId);
            Assert.Equal(token, resumePayload.SessionToken);
            Assert.Contains(resumePayload.Users, u => u.UserId == userId);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact(Timeout = 8000)]
    public async Task DeferredCleanup_skips_UserLeft_when_user_already_rejoined()
    {
        // Resume fails (e.g. server reload, token unknown) → client falls back to
        // fresh JoinRoom on a NEW handler with the same UserId. The deferred cleanup
        // for the original dead handler must NOT fire UserLeft after the grace window,
        // otherwise peers see the user vanish 30s after a successful re-join.
        var fixture = await Fixture.StartAsync(graceSeconds: 1);
        try
        {
            using var tcp = new TcpClient();
            await fixture.ConnectAsync(tcp);
            using var stream = tcp.GetStream();

            const string roomId = "deferred-room";
            const string userId = "u-rejoin";

            await SendAsync(stream, NetMessage<UserPayload>.Create(
                MessageType.JoinRoom, userId, "Alice", roomId,
                new UserPayload { User = new UserInfo { UserId = userId, UserName = "Alice" } }));
            await ReadLineAsync(stream); // RoomJoined

            // Bob joins to receive the UserLeft we want to NOT fire.
            using var bob = new TcpClient();
            await fixture.ConnectAsync(bob);
            using var bobStream = bob.GetStream();
            await SendAsync(bobStream, NetMessage<UserPayload>.Create(
                MessageType.JoinRoom, "bob", "Bob", roomId,
                new UserPayload { User = new UserInfo { UserId = "bob", UserName = "Bob" } }));
            await ReadLineAsync(bobStream); // Bob's RoomJoined
            await ReadLineAsync(stream);    // Alice receives Bob's UserJoined

            tcp.Close();
            // Alice fresh-rejoins on a new TCP, NOT via Resume.
            using var tcp2 = new TcpClient();
            await fixture.ConnectAsync(tcp2);
            using var stream2 = tcp2.GetStream();
            await SendAsync(stream2, NetMessage<UserPayload>.Create(
                MessageType.JoinRoom, userId, "Alice", roomId,
                new UserPayload { User = new UserInfo { UserId = userId, UserName = "Alice" } }));
            await ReadLineAsync(stream2); // RoomJoined (fresh)

            // Drain Bob until grace window has elapsed. We assert no UserLeft for Alice arrives.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            bool sawAliceLeft = false;
            while (DateTime.UtcNow < deadline)
            {
                var line = await TryReadLineAsync(bobStream, TimeSpan.FromMilliseconds(500));
                if (line is null) continue;
                var env = MessageEnvelope.Parse(line);
                if (env?.Type == MessageType.UserLeft && env.SenderId == userId) { sawAliceLeft = true; break; }
            }
            Assert.False(sawAliceLeft, "Deferred cleanup leaked UserLeft after fresh rejoin");
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact(Timeout = 8000)]
    public async Task Grace_expiry_eventually_emits_UserLeft()
    {
        var fixture = await Fixture.StartAsync(graceSeconds: 1);
        try
        {
            using var alice = new TcpClient(); await fixture.ConnectAsync(alice);
            using var aliceStream = alice.GetStream();
            const string roomId = "grace-room";
            await SendAsync(aliceStream, NetMessage<UserPayload>.Create(
                MessageType.JoinRoom, "alice", "Alice", roomId,
                new UserPayload { User = new UserInfo { UserId = "alice", UserName = "Alice" } }));
            await ReadLineAsync(aliceStream); // Alice RoomJoined

            using var bob = new TcpClient(); await fixture.ConnectAsync(bob);
            using var bobStream = bob.GetStream();
            await SendAsync(bobStream, NetMessage<UserPayload>.Create(
                MessageType.JoinRoom, "bob", "Bob", roomId,
                new UserPayload { User = new UserInfo { UserId = "bob", UserName = "Bob" } }));
            await ReadLineAsync(bobStream); // Bob RoomJoined
            await ReadLineAsync(aliceStream); // Alice receives UserJoined for Bob

            alice.Close();

            string? aliceLeftLine = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var line = await TryReadLineAsync(bobStream, TimeSpan.FromMilliseconds(500));
                if (line is null) continue;
                var env = MessageEnvelope.Parse(line);
                if (env?.Type == MessageType.UserLeft && env.SenderId == "alice")
                {
                    aliceLeftLine = line; break;
                }
            }
            Assert.NotNull(aliceLeftLine);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact(Timeout = 5000)]
    public async Task Resume_with_unknown_token_emits_AuthResumeFailed()
    {
        var fixture = await Fixture.StartAsync(graceSeconds: 10);
        try
        {
            using var tcp = new TcpClient();
            await fixture.ConnectAsync(tcp);
            using var stream = tcp.GetStream();

            await SendAsync(stream, NetMessage<ResumePayload>.Create(
                MessageType.Resume, "nobody", "Nobody", "any-room",
                new ResumePayload { Token = "unknown-token-bytes" }));

            var line = await ReadLineAsync(stream);
            var env = MessageEnvelope.Parse(line)!;
            Assert.Equal(MessageType.Error, env.Type);
            var payload = MessageEnvelope.DeserializePayload<ErrorPayload>(env.RawPayload)!;
            Assert.Equal(ErrorCodes.AuthResumeFailed, payload.Code);
        }
        finally { await fixture.DisposeAsync(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required DrawServer Server { get; init; }
        public required Task ServerTask { get; init; }
        public required int Port { get; init; }
        public required SessionTokenStore Store { get; init; }

        public static async Task<Fixture> StartAsync(int graceSeconds)
        {
            var clientRegistry = new ClientRegistry();
            var roomService = new RoomService();
            var rateLimiter = new TokenBucketRateLimiter(capacity: 200, refillPerSec: 50);
            var sessionTokenStore = new SessionTokenStore(TimeSpan.FromSeconds(graceSeconds));
            var loggerFactory = NullLoggerFactory.Instance;
            var dispatcher = new MessageDispatcher(rateLimiter, NullLogger<MessageDispatcher>.Instance);
            dispatcher.Register(new RoomHandler(roomService, clientRegistry, sessionTokenStore));

            var server = new DrawServer(0, dispatcher, clientRegistry, roomService, rateLimiter, sessionTokenStore, loggerFactory);
            server.Bind();
            int port = server.BoundPort;
            var task = Task.Run(server.StartAsync);
            await Task.Yield();
            return new Fixture { Server = server, ServerTask = task, Port = port, Store = sessionTokenStore };
        }

        public async Task ConnectAsync(TcpClient client)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            Exception? last = null;
            while (DateTime.UtcNow < deadline)
            {
                try { await client.ConnectAsync(IPAddress.Loopback, Port); return; }
                catch (SocketException ex) { last = ex; await Task.Delay(25); }
            }
            throw new TimeoutException($"Could not connect to 127.0.0.1:{Port}", last);
        }

        public async ValueTask DisposeAsync()
        {
            await Server.StopAsync();
            await Task.WhenAny(ServerTask, Task.Delay(500));
        }
    }

    private static async Task SendAsync<T>(NetworkStream stream, NetMessage<T> message) where T : IPayload
    {
        var bytes = Encoding.UTF8.GetBytes(message.Serialize());
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static async Task<string?> TryReadLineAsync(NetworkStream stream, TimeSpan timeout)
    {
        var buffer = new byte[8192];
        var charBuf = new char[8192];
        var sb = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            int n;
            try { n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token); }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
            if (n == 0) return null;
            int decoded = decoder.GetChars(buffer, 0, n, charBuf, 0);
            sb.Append(charBuf, 0, decoded);
            int idx = sb.ToString().IndexOf('\n');
            if (idx >= 0) return sb.ToString()[..idx];
        }
        return null;
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var charBuf = new char[8192];
        var sb = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!cts.IsCancellationRequested)
        {
            int n;
            try { n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("No newline-delimited response"); }
            if (n == 0) break;
            int decoded = decoder.GetChars(buffer, 0, n, charBuf, 0);
            sb.Append(charBuf, 0, decoded);
            int idx = sb.ToString().IndexOf('\n');
            if (idx >= 0) return sb.ToString()[..idx];
        }
        throw new TimeoutException("No newline-delimited response");
    }
}
