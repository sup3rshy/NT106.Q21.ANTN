using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using NetDraw.Server;
using NetDraw.Server.Handlers;
using NetDraw.Server.Pipeline;
using NetDraw.Server.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Xunit;

namespace NetDraw.Server.Tests;

public class DrawServerTlsTests
{
    // Phase 1 acceptance: with no cert passed, the accept loop must hand a
    // raw NetworkStream to ClientHandler and a plaintext JoinRoom round-trips
    // unchanged. Guards against a regression where the SslStream wrap path
    // gets accidentally wired into the no-cert branch.
    //
    // The actual TLS handshake path is not exercised here — that requires
    // generating a cert at test time, which adds CI complexity (openssl
    // dependency, cert teardown, env-var hygiene across parallel tests).
    // Tracked for a later phase.
    [Fact(Timeout = 5000)]
    public async Task Server_starts_in_insecure_mode_without_cert()
    {
        var clientRegistry = new ClientRegistry();
        var roomService = new RoomService();
        var rateLimiter = new TokenBucketRateLimiter(capacity: 200, refillPerSec: 50);
        var sessionTokenStore = new SessionTokenStore();
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
        var dispatcher = new MessageDispatcher(rateLimiter, loggerFactory.CreateLogger<MessageDispatcher>());
        dispatcher.Register(new RoomHandler(roomService, clientRegistry, sessionTokenStore));

        // serverCert is null — Phase 1 default-off.
        var server = new DrawServer(0, dispatcher, clientRegistry, roomService, rateLimiter, sessionTokenStore, loggerFactory, serverCert: null);
        server.Bind();
        int port = server.BoundPort;
        var serverTask = Task.Run(server.StartAsync);

        try
        {
            using var tcp = new TcpClient();
            await ConnectWithRetryAsync(tcp, port, TimeSpan.FromSeconds(2));
            using var stream = tcp.GetStream();

            const string roomId = "tls-insecure-room";
            const string userId = "user-tls-1";
            const string userName = "InsecureAlice";

            var joinMsg = NetMessage<UserPayload>.Create(
                MessageType.JoinRoom, userId, userName, roomId,
                new UserPayload { User = new UserInfo { UserId = userId, UserName = userName } });

            byte[] outBytes = Encoding.UTF8.GetBytes(joinMsg.Serialize());
            await stream.WriteAsync(outBytes);
            await stream.FlushAsync();

            string responseLine = await ReadLineAsync(stream, TimeSpan.FromSeconds(3));
            var envelope = MessageEnvelope.Parse(responseLine);
            Assert.NotNull(envelope);
            Assert.Equal(MessageType.RoomJoined, envelope!.Type);
            Assert.Equal(roomId, envelope.RoomId);

            tcp.Close();
        }
        finally
        {
            await server.StopAsync();
            var done = await Task.WhenAny(serverTask, Task.Delay(500));
            if (done == serverTask && serverTask.IsFaulted)
                throw new Xunit.Sdk.XunitException($"Server task faulted: {serverTask.Exception}");
        }
    }

    private static async Task ConnectWithRetryAsync(TcpClient client, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException ex)
            {
                last = ex;
                await Task.Delay(25);
            }
        }
        throw new TimeoutException($"Could not connect to 127.0.0.1:{port} within {timeout.TotalSeconds}s", last);
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, TimeSpan timeout)
    {
        var buffer = new byte[4096];
        var charBuf = new char[4096];
        var sb = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"No newline-delimited response received within {timeout.TotalSeconds}s");
            }
            if (n == 0) break;
            int decoded = decoder.GetChars(buffer, 0, n, charBuf, 0);
            sb.Append(charBuf, 0, decoded);
            int idx = sb.ToString().IndexOf('\n');
            if (idx >= 0) return sb.ToString()[..idx];
        }
        throw new TimeoutException($"No newline-delimited response received within {timeout.TotalSeconds}s");
    }
}
