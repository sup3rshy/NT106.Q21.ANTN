using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NetDraw.Server;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Xunit;

namespace NetDraw.Server.Tests;

public class ClientHandlerFramerTests
{
    [Fact(Timeout = 5000)]
    public async Task Routes_Json_To_JsonPath()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            var seen = new List<MessageEnvelope.Envelope>();
            var firstMsg = new TaskCompletionSource<MessageEnvelope.Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.MessageReceived += (_, env) =>
            {
                seen.Add(env);
                firstMsg.TrySetResult(env);
                return Task.CompletedTask;
            };
            var listenTask = Task.Run(handler.ListenAsync);

            var json = NetMessage<ChatPayload>.Create(MessageType.ChatMessage, "u1", "Alice", "r1",
                new ChatPayload { Message = "hi" }).Serialize();
            await peerStream.WriteAsync(Encoding.UTF8.GetBytes(json));
            await peerStream.FlushAsync();

            var env = await firstMsg.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MessageType.ChatMessage, env.Type);
            Assert.Equal("u1", env.SenderId);
            Assert.True(env.RawBinary.IsEmpty); // JSON path leaves the binary buffer untouched.
        }
        finally { Cleanup(peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task Routes_Binary_To_BinaryHandler()
    {
        // The 0xFE prefix routes through ParseBinary; Phase 1's dispatch responds with a
        // BINARY_NOT_IMPLEMENTED stub frame on the wire. We assert the reply rather than
        // the MessageReceived event because per-design the binary path does NOT raise the
        // higher-level event yet (no per-type decoder exists).
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            var listenTask = Task.Run(handler.ListenAsync);

            var frame = BuildBinaryFrame((byte)MessageType.Draw, body: new byte[] { 0x01, 0x02 });
            await peerStream.WriteAsync(frame);
            await peerStream.FlushAsync();

            var reply = await ReadFrameAsync(peerStream);
            Assert.Equal(MessageEnvelope.BinaryMagic, reply[0]);
            var env = MessageEnvelope.ParseBinary(reply);
            Assert.NotNull(env);
            Assert.Equal(MessageType.Error, env!.Type);

            var body = MessageEnvelope.DecodeErrorBody(env.RawBinary.Span);
            Assert.NotNull(body);
            Assert.Equal(ErrorCodes.BinaryNotImplemented, body!.Code);
        }
        finally { Cleanup(peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task Closes_On_Unknown_Magic()
    {
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            var listenTask = Task.Run(handler.ListenAsync);

            // 0x42 ('B') is neither '{', '[', '\n', nor 0xFE — unrecognised framing.
            // The framer sends a JSON Error and tears down. We confirm by waiting for
            // the read loop to terminate (peer sees EOF after the JSON Error line).
            await peerStream.WriteAsync(new byte[] { 0x42, 0x42, 0x42 });
            await peerStream.FlushAsync();

            // Drain whatever JSON Error the server emits before closing.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            var sb = new StringBuilder();
            var buf = new byte[4096];
            while (DateTime.UtcNow < deadline)
            {
                int n;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    n = await peerStream.ReadAsync(buf, cts.Token);
                }
                catch (OperationCanceledException) { break; }
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                if (sb.ToString().Contains('\n')) break;
            }
            var text = sb.ToString();
            Assert.Contains("\"code\":\"" + ErrorCodes.BinaryBadMagic + "\"", text);

            // After the Error reply the server closes the connection; ListenAsync should exit.
            var done = await Task.WhenAny(listenTask, Task.Delay(2000));
            Assert.Same(listenTask, done);
        }
        finally { Cleanup(peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task Closes_On_Length_Cap_Overrun()
    {
        // The framer reads payload-length out of the buffer directly (before ParseBinary
        // ever sees the body) so the cap-enforcement path is distinct from ParseBinary's
        // null-on-cap branch. Send only the 6-byte header with an oversize length and
        // assert the JSON Error + EOF.
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            var listenTask = Task.Run(handler.ListenAsync);

            const int oversize = 16_000_001;
            var header = new byte[6];
            header[0] = MessageEnvelope.BinaryMagic;
            header[1] = MessageEnvelope.BinaryVersion;
            header[2] = (byte)MessageType.Draw;
            header[3] = (byte)((oversize >> 16) & 0xFF);
            header[4] = (byte)((oversize >> 8)  & 0xFF);
            header[5] = (byte)( oversize        & 0xFF);
            await peerStream.WriteAsync(header);
            await peerStream.FlushAsync();

            var sb = new StringBuilder();
            var buf = new byte[4096];
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                while (!cts.IsCancellationRequested)
                {
                    int n;
                    try { n = await peerStream.ReadAsync(buf, cts.Token); }
                    catch (OperationCanceledException) { break; }
                    if (n == 0) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                    if (sb.ToString().Contains('\n')) break;
                }
            }
            Assert.Contains("\"code\":\"" + ErrorCodes.BinaryBodyUnderrun + "\"", sb.ToString());

            var done = await Task.WhenAny(listenTask, Task.Delay(2000));
            Assert.Same(listenTask, done);
        }
        finally { Cleanup(peerStream, listener); }
    }

    [Fact(Timeout = 5000)]
    public async Task Buffer_Survives_Mixed_Stream()
    {
        // One TCP segment carries (JSON line)+(binary frame)+(JSON line) concatenated.
        // The framer must dispatch each in order without UTF-8 state contamination.
        var (handler, peerStream, listener) = await CreateConnectedHandlerAsync();
        try
        {
            var seenJson = new List<MessageEnvelope.Envelope>();
            var twoJsonSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.MessageReceived += (_, env) =>
            {
                seenJson.Add(env);
                if (seenJson.Count >= 2) twoJsonSeen.TrySetResult(true);
                return Task.CompletedTask;
            };
            var listenTask = Task.Run(handler.ListenAsync);

            var json1 = NetMessage<ChatPayload>.Create(MessageType.ChatMessage, "u1", "Alice", "r1",
                new ChatPayload { Message = "first" }).Serialize();
            var binFrame = BuildBinaryFrame((byte)MessageType.Draw, body: new byte[] { 0xAA });
            var json2 = NetMessage<ChatPayload>.Create(MessageType.ChatMessage, "u1", "Alice", "r1",
                new ChatPayload { Message = "second" }).Serialize();

            using var ms = new MemoryStream();
            ms.Write(Encoding.UTF8.GetBytes(json1));
            ms.Write(binFrame);
            ms.Write(Encoding.UTF8.GetBytes(json2));
            await peerStream.WriteAsync(ms.ToArray());
            await peerStream.FlushAsync();

            await twoJsonSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, seenJson.Count);
            Assert.Equal(MessageType.ChatMessage, seenJson[0].Type);
            Assert.Equal(MessageType.ChatMessage, seenJson[1].Type);

            // The binary frame in the middle should have produced a binary Error reply on the wire.
            var reply = await ReadFrameAsync(peerStream);
            Assert.Equal(MessageEnvelope.BinaryMagic, reply[0]);
        }
        finally { Cleanup(peerStream, listener); }
    }

    private static byte[] BuildBinaryFrame(byte typeId, byte[] body)
    {
        const int envelopeLen = 48;
        int payloadLen = envelopeLen + body.Length;
        var frame = new byte[6 + payloadLen];
        frame[0] = MessageEnvelope.BinaryMagic;
        frame[1] = MessageEnvelope.BinaryVersion;
        frame[2] = typeId;
        frame[3] = (byte)((payloadLen >> 16) & 0xFF);
        frame[4] = (byte)((payloadLen >> 8)  & 0xFF);
        frame[5] = (byte)( payloadLen        & 0xFF);
        // Envelope = 48 zero bytes (timestamp 0, senderUint 0, roomHash 0, token zeroed).
        if (body.Length > 0) body.CopyTo(frame.AsSpan(6 + envelopeLen));
        return frame;
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
    {
        // Read at least 6 bytes for the header, then the declared length.
        var header = new byte[6];
        await ReadExactlyAsync(stream, header, 6);
        Assert.Equal(MessageEnvelope.BinaryMagic, header[0]);
        int payloadLen = (header[3] << 16) | (header[4] << 8) | header[5];
        var rest = new byte[payloadLen];
        await ReadExactlyAsync(stream, rest, payloadLen);
        var full = new byte[6 + payloadLen];
        header.CopyTo(full, 0);
        rest.CopyTo(full, 6);
        return full;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buf, int count)
    {
        int read = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (read < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, count - read), cts.Token);
            if (n == 0) throw new IOException("peer closed before frame complete");
            read += n;
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

        var handler = new ClientHandler(server, NullLogger<ClientHandler>.Instance);
        return (handler, peer.GetStream(), listener);
    }

    private static void Cleanup(NetworkStream peerStream, TcpListener listener)
    {
        try { peerStream.Close(); } catch { }
        try { listener.Stop(); } catch { }
    }
}
