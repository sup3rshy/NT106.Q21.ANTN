using System.Buffers.Binary;
using System.Text;
using NetDraw.Shared.Protocol;
using NetDraw.Shared.Protocol.Payloads;
using Xunit;

namespace NetDraw.Shared.Tests;

public class BinaryFrameTests
{
    [Fact]
    public void Roundtrip_ErrorStub()
    {
        var msg = NetMessage<ErrorPayload>.Create(
            MessageType.Error, senderId: "server", senderName: "Server", roomId: "room42",
            new ErrorPayload { Code = ErrorCodes.BinaryNotImplemented, Message = "type-id 6 has no binary decoder yet" });
        msg.Timestamp = 1746230400000L;

        var frame = MessageEnvelope.SerializeBinary(msg);
        Assert.Equal(MessageEnvelope.BinaryMagic, frame[0]);
        Assert.Equal(MessageEnvelope.BinaryVersion, frame[1]);
        Assert.Equal((byte)MessageType.Error, frame[2]);

        var env = MessageEnvelope.ParseBinary(frame);
        Assert.NotNull(env);
        Assert.Equal(MessageType.Error, env!.Type);
        Assert.Equal(1746230400000L, env.Timestamp);

        var decoded = MessageEnvelope.DecodeErrorBody(env.RawBinary.Span);
        Assert.NotNull(decoded);
        Assert.Equal(ErrorCodes.BinaryNotImplemented, decoded!.Code);
        Assert.Equal("type-id 6 has no binary decoder yet", decoded.Message);
    }

    [Fact]
    public void Rejects_Bad_Magic()
    {
        var frame = BuildMinimalFrame(typeId: (byte)MessageType.Error, payloadLength: 48, body: Array.Empty<byte>());
        frame[0] = 0xAB;
        Assert.Null(MessageEnvelope.ParseBinary(frame));
    }

    [Fact]
    public void Rejects_Length_Cap()
    {
        // Construct a header alone whose payload-length exceeds the receiver cap.
        // ParseBinary doesn't need the full body bytes to reject — the length field is in the header.
        var frame = new byte[6 + 48];
        frame[0] = MessageEnvelope.BinaryMagic;
        frame[1] = MessageEnvelope.BinaryVersion;
        frame[2] = (byte)MessageType.Error;
        // payload-length = 16_000_001 > MaxBinaryPayloadLength
        const int oversize = 16_000_001;
        frame[3] = (byte)((oversize >> 16) & 0xFF);
        frame[4] = (byte)((oversize >> 8)  & 0xFF);
        frame[5] = (byte)( oversize        & 0xFF);
        Assert.Null(MessageEnvelope.ParseBinary(frame));
    }

    [Fact]
    public void Rejects_Underrun()
    {
        // Header declares payload-length = 100 but the slice only contains 6 + 48 bytes.
        var frame = new byte[6 + 48];
        frame[0] = MessageEnvelope.BinaryMagic;
        frame[1] = MessageEnvelope.BinaryVersion;
        frame[2] = (byte)MessageType.Error;
        const int declared = 100;
        frame[3] = (byte)((declared >> 16) & 0xFF);
        frame[4] = (byte)((declared >> 8)  & 0xFF);
        frame[5] = (byte)( declared        & 0xFF);
        Assert.Null(MessageEnvelope.ParseBinary(frame));
    }

    [Fact]
    public void Roundtrip_Envelope_Fields()
    {
        // Hand-pack an envelope so we can assert each binary-only field round-trips exactly.
        const long ts = 0x0102030405060708L;
        const uint senderUint = 0xCAFEBABEu;
        const uint roomHash = 0xDEADBEEFu;
        var token = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var body = new byte[] { 0xAA, 0xBB, 0xCC };

        int payloadLength = 48 + body.Length;
        var frame = new byte[6 + payloadLength];
        frame[0] = MessageEnvelope.BinaryMagic;
        frame[1] = MessageEnvelope.BinaryVersion;
        frame[2] = (byte)MessageType.CursorMove;
        frame[3] = (byte)((payloadLength >> 16) & 0xFF);
        frame[4] = (byte)((payloadLength >> 8)  & 0xFF);
        frame[5] = (byte)( payloadLength        & 0xFF);

        BinaryPrimitives.WriteInt64BigEndian (frame.AsSpan(6, 8),   ts);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(14, 4),  senderUint);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(18, 4),  roomHash);
        token.CopyTo(frame.AsSpan(22, 32));
        body.CopyTo (frame.AsSpan(54));

        var env = MessageEnvelope.ParseBinary(frame);
        Assert.NotNull(env);
        Assert.Equal(MessageType.CursorMove, env!.Type);
        Assert.Equal(ts, env.Timestamp);
        Assert.Equal(senderUint, env.SenderUint);
        Assert.Equal(roomHash, env.RoomHash);
        Assert.Equal("cafebabe", env.SenderId);
        Assert.NotNull(env.SessionTokenBytes);
        Assert.Equal(token, env.SessionTokenBytes!);
        Assert.Equal(body, env.RawBinary.ToArray());
    }

    [Fact]
    public void ParseBinary_RejectsUnknownVersion()
    {
        var frame = BuildMinimalFrame(typeId: (byte)MessageType.Error, payloadLength: 48, body: Array.Empty<byte>());
        frame[1] = 99;
        Assert.Null(MessageEnvelope.ParseBinary(frame));
    }

    [Fact]
    public void ParseBinary_RejectsUnknownTypeId()
    {
        // type-id 200 is not a defined MessageType.
        var frame = BuildMinimalFrame(typeId: 200, payloadLength: 48, body: Array.Empty<byte>());
        Assert.Null(MessageEnvelope.ParseBinary(frame));
    }

    [Fact]
    public void DecodeErrorBody_RejectsUnderrun()
    {
        // codeLen = 5 but only 4 bytes follow the length prefix.
        var body = new byte[] { 0x00, 0x05, (byte)'a', (byte)'b', (byte)'c', (byte)'d' };
        Assert.Null(MessageEnvelope.DecodeErrorBody(body));
    }

    private static byte[] BuildMinimalFrame(byte typeId, int payloadLength, byte[] body)
    {
        var frame = new byte[6 + payloadLength];
        frame[0] = MessageEnvelope.BinaryMagic;
        frame[1] = MessageEnvelope.BinaryVersion;
        frame[2] = typeId;
        frame[3] = (byte)((payloadLength >> 16) & 0xFF);
        frame[4] = (byte)((payloadLength >> 8)  & 0xFF);
        frame[5] = (byte)( payloadLength        & 0xFF);
        // 48 envelope bytes left as zero
        if (body.Length > 0) body.CopyTo(frame.AsSpan(6 + 48));
        return frame;
    }
}
