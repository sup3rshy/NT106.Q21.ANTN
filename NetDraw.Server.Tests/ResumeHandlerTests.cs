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

            // Drop the TCP. Server's Disconnected handler defers cleanup for the grace window.
            tcp.Close();

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
            return new Fixture { Server = server, ServerTask = task, Port = port };
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
