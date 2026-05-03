using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NetDraw.Server;
using NetDraw.Server.Services;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Xunit;

namespace NetDraw.Server.Tests;

public class RoomServiceBroadcastTests
{
    [Fact(Timeout = 5000)]
    public async Task BroadcastToRoomAsync_StripsOriginatorSessionToken()
    {
        var roomService = new RoomService();
        const string roomId = "room-broadcast-1";

        // Two real ClientHandlers, each on its own loopback socket pair so we can
        // read what the broadcast actually wrote.
        var (alice, alicePeer, aliceListener) = await CreateConnectedHandlerAsync();
        var (bob,   bobPeer,   bobListener)   = await CreateConnectedHandlerAsync();
        try
        {
            roomService.AddUserToRoom(roomId, alice, new UserInfo { UserId = "alice", UserName = "Alice" });
            roomService.AddUserToRoom(roomId, bob,   new UserInfo { UserId = "bob",   UserName = "Bob"   });

            var msg = NetMessage<ChatPayload>.Create(MessageType.ChatMessage, "alice", "Alice", roomId,
                new ChatPayload { Message = "hi" });
            // Simulate what would happen if a future handler accidentally copied the inbound
            // session token onto the outbound NetMessage. The strip must scrub it.
            msg.SessionToken = "alice-secret-token-xyz";

            await roomService.BroadcastToRoomAsync(roomId, msg);

            var aliceLine = await ReadLineAsync(alicePeer);
            var bobLine   = await ReadLineAsync(bobPeer);

            var aliceEnv = MessageEnvelope.Parse(aliceLine);
            var bobEnv   = MessageEnvelope.Parse(bobLine);

            Assert.NotNull(aliceEnv);
            Assert.NotNull(bobEnv);
            Assert.Equal(string.Empty, aliceEnv!.SessionToken);
            Assert.Equal(string.Empty, bobEnv!.SessionToken);

            // Belt-and-suspenders: also confirm the field literally isn't carrying the token
            // text in the wire JSON.
            Assert.DoesNotContain("alice-secret-token-xyz", aliceLine);
            Assert.DoesNotContain("alice-secret-token-xyz", bobLine);
        }
        finally
        {
            try { alicePeer.Close(); } catch { }
            try { bobPeer.Close(); } catch { }
            try { aliceListener.Stop(); } catch { }
            try { bobListener.Stop(); } catch { }
        }
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
        var handler = new ClientHandler(server, server.GetStream(), NullLogger<ClientHandler>.Instance);
        return (handler, peer.GetStream(), listener);
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
}
