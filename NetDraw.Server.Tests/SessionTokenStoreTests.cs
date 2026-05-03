using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NetDraw.Server;
using NetDraw.Server.Services;
using NetDraw.Shared.Protocol;
using Xunit;

namespace NetDraw.Server.Tests;

public class SessionTokenStoreTests
{
    [Fact]
    public void TryClaim_succeeds_within_grace()
    {
        var (handler, peer, listener) = MakeHandler();
        try
        {
            var store = new SessionTokenStore(TimeSpan.FromSeconds(30));
            var (token, bytes) = NewToken();
            store.Issue(token, bytes, handler, "alice");
            store.RecordRoom(handler, "room-1");
            Assert.True(store.MarkOrphaned(handler));

            var (newHandler, peer2, listener2) = MakeHandler();
            try
            {
                bool ok = store.TryClaim(token, newHandler, out var userId, out var lastRoomId);
                Assert.True(ok);
                Assert.Equal("alice", userId);
                Assert.Equal("room-1", lastRoomId);
                Assert.Equal(token, newHandler.SessionToken);
                Assert.NotNull(newHandler.SessionTokenBytes);
                Assert.Equal(bytes, newHandler.SessionTokenBytes!);
            }
            finally { Cleanup(newHandler, peer2, listener2); }
        }
        finally { Cleanup(handler, peer, listener); }
    }

    [Fact]
    public void TryClaim_fails_after_expiry()
    {
        var (handler, peer, listener) = MakeHandler();
        try
        {
            // 1ms grace so we can wait it out without slowing the suite.
            var store = new SessionTokenStore(TimeSpan.FromMilliseconds(1));
            var (token, bytes) = NewToken();
            store.Issue(token, bytes, handler, "alice");
            store.MarkOrphaned(handler);
            // Give the wall clock past the 1ms expiry. Sleep is unavoidable here —
            // expiry is read off DateTimeOffset.UtcNow inside TryClaim.
            Thread.Sleep(20);

            var (newHandler, peer2, listener2) = MakeHandler();
            try
            {
                bool ok = store.TryClaim(token, newHandler, out _, out _);
                Assert.False(ok);
                Assert.Null(newHandler.SessionTokenBytes);
            }
            finally { Cleanup(newHandler, peer2, listener2); }
        }
        finally { Cleanup(handler, peer, listener); }
    }

    [Fact]
    public void TryClaim_fails_when_not_orphaned()
    {
        var (handler, peer, listener) = MakeHandler();
        try
        {
            var store = new SessionTokenStore(TimeSpan.FromSeconds(30));
            var (token, bytes) = NewToken();
            // Issued but never marked orphaned — original handler still bound.
            store.Issue(token, bytes, handler, "alice");

            var (newHandler, peer2, listener2) = MakeHandler();
            try
            {
                bool ok = store.TryClaim(token, newHandler, out _, out _);
                Assert.False(ok);
                Assert.Null(newHandler.SessionTokenBytes);
            }
            finally { Cleanup(newHandler, peer2, listener2); }
        }
        finally { Cleanup(handler, peer, listener); }
    }

    [Fact]
    public async Task TryClaim_race_first_wins()
    {
        // Two concurrent reconnects on the same orphaned token: exactly one must
        // win the rebind, the other gets false. Repeat the experiment to amortize
        // schedule luck — single-shot wouldn't reliably exercise the race.
        const int iterations = 50;
        for (int i = 0; i < iterations; i++)
        {
            var (handler, peer, listener) = MakeHandler();
            try
            {
                var store = new SessionTokenStore(TimeSpan.FromSeconds(30));
                var (token, bytes) = NewToken();
                store.Issue(token, bytes, handler, "alice");
                store.MarkOrphaned(handler);

                var (h1, p1, l1) = MakeHandler();
                var (h2, p2, l2) = MakeHandler();
                try
                {
                    using var gate = new ManualResetEventSlim(false);
                    bool r1 = false, r2 = false;
                    var t1 = Task.Run(() => { gate.Wait(); r1 = store.TryClaim(token, h1, out _, out _); });
                    var t2 = Task.Run(() => { gate.Wait(); r2 = store.TryClaim(token, h2, out _, out _); });
                    gate.Set();
                    await Task.WhenAll(t1, t2);

                    Assert.True(r1 ^ r2, $"Iteration {i}: expected exactly one winner, got r1={r1} r2={r2}");
                }
                finally { Cleanup(h1, p1, l1); Cleanup(h2, p2, l2); }
            }
            finally { Cleanup(handler, peer, listener); }
        }
    }

    private static (string token, byte[] bytes) NewToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return (Base64UrlEncoding.Encode(bytes), bytes);
    }

#pragma warning disable xUnit1031 // synchronous helper used by sync test scaffolding only
    private static (ClientHandler, NetworkStream, TcpListener) MakeHandler()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();
        var peer = new TcpClient();
        peer.Connect(IPAddress.Loopback, port);
        var server = acceptTask.GetAwaiter().GetResult();
        var handler = new ClientHandler(server, server.GetStream(), NullLogger<ClientHandler>.Instance);
        return (handler, peer.GetStream(), listener);
    }
#pragma warning restore xUnit1031

    private static void Cleanup(ClientHandler _, NetworkStream peerStream, TcpListener listener)
    {
        try { peerStream.Close(); } catch { }
        try { listener.Stop(); } catch { }
    }
}
