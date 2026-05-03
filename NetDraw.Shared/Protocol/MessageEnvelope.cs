using System.Buffers.Binary;
using System.Text;
using NetDraw.Shared.Models;
using NetDraw.Shared.Protocol.Payloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetDraw.Shared.Protocol;

/// <summary>
/// Utilities for parsing protocol messages in two phases:
/// (1) envelope-only parsing (no payload deserialization),
/// (2) typed payload deserialization on demand.
///
/// This allows the server to route messages by <see cref="MessageType"/>
/// without knowing the concrete payload type upfront.
/// </summary>
public static class MessageEnvelope
{
    private static readonly JsonSerializerSettings DeserializerSettings = new()
    {
        Converters = { new DrawActionConverter() }
    };

    /// <summary>Magic byte that identifies a binary frame on the wire (per docs/design/binary-frame.md).</summary>
    public const byte BinaryMagic = 0xFE;

    /// <summary>Wire-format version emitted by Phase 1 binary writers and accepted by Phase 1 parsers.</summary>
    public const byte BinaryVersion = 1;

    /// <summary>
    /// Receiver-side cap on a single binary frame's payload-length field. The u24 ceiling
    /// is 0xFF_FFFF (~16.78 MiB); 16_000_000 is the deployment-chosen threshold the design
    /// doc fixes so the cap matches a round number rather than the wire ceiling.
    /// </summary>
    public const int MaxBinaryPayloadLength = 16_000_000;

    private const int BinaryHeaderLength = 6;
    private const int BinaryEnvelopeLength = 48;

    /// <summary>
    /// Parsed envelope fields without a deserialized payload.
    /// </summary>
    /// <remarks>
    /// Binary-only fields default to null/zero on the JSON path so the existing positional
    /// constructor stays source-compatible; binary callers use named arguments to populate them.
    /// </remarks>
    public record Envelope(
        MessageType Type,
        string SenderId,
        string SenderName,
        string RoomId,
        long Timestamp,
        JObject? RawPayload,
        int Version,
        string SessionToken,
        ReadOnlyMemory<byte> RawBinary = default,
        byte[]? SessionTokenBytes = null,
        uint SenderUint = 0,
        uint RoomHash = 0);

    /// <summary>
    /// Parse only the envelope fields from a JSON message string.
    /// The payload is kept as a raw <see cref="JObject"/> for deferred deserialization.
    /// Returns <c>null</c> if the JSON is malformed or missing required fields.
    /// </summary>
    public static Envelope? Parse(string json)
    {
        try
        {
            var jObject = JObject.Parse(json.Trim());

            var typeToken = jObject["type"];
            if (typeToken is null)
                return null;

            if (!Enum.TryParse<MessageType>(typeToken.Value<string>(), ignoreCase: false, out var type)
                && !Enum.TryParse<MessageType>(typeToken.Value<int>().ToString(), out type))
            {
                return null;
            }

            var senderId     = jObject["senderId"]?.Value<string>()     ?? string.Empty;
            var senderName   = jObject["senderName"]?.Value<string>()   ?? string.Empty;
            var roomId       = jObject["roomId"]?.Value<string>()       ?? string.Empty;
            var timestamp    = jObject["timestamp"]?.Value<long>()      ?? 0L;
            var version      = jObject["version"]?.Value<int>()         ?? 0;
            var sessionToken = jObject["sessionToken"]?.Value<string>() ?? string.Empty;
            var rawPayload   = jObject["payload"] as JObject;

            return new Envelope(type, senderId, senderName, roomId, timestamp, rawPayload, version, sessionToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deserialize a raw <see cref="JObject"/> payload into the specified typed payload.
    /// Uses <see cref="DrawActionConverter"/> so that draw payloads resolve correctly.
    /// Returns <c>default</c> if <paramref name="rawPayload"/> is <c>null</c>.
    /// </summary>
    public static T? DeserializePayload<T>(JObject? rawPayload) where T : IPayload
    {
        if (rawPayload is null)
            return default;

        var serializer = JsonSerializer.Create(DeserializerSettings);
        return rawPayload.ToObject<T>(serializer);
    }

    /// <summary>
    /// Full deserialization of a JSON message string into a strongly-typed <see cref="NetMessage{T}"/>.
    /// Returns <c>null</c> if parsing fails.
    /// </summary>
    public static NetMessage<T>? Deserialize<T>(string json) where T : IPayload
    {
        try
        {
            return JsonConvert.DeserializeObject<NetMessage<T>>(json.Trim(), DeserializerSettings);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a complete binary frame: 6-byte header + 48-byte envelope + per-type body.
    /// Returns <c>null</c> on bad magic, unsupported version, oversize payload-length,
    /// or a slice that doesn't match the declared length. The caller (the framer) has
    /// already consumed the bytes from the stream; this function only validates and
    /// extracts envelope fields, leaving body bytes in <see cref="Envelope.RawBinary"/>.
    /// </summary>
    public static Envelope? ParseBinary(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < BinaryHeaderLength + BinaryEnvelopeLength) return null;
        if (frame[0] != BinaryMagic) return null;
        if (frame[1] != BinaryVersion) return null;

        int payloadLength = (frame[3] << 16) | (frame[4] << 8) | frame[5];
        if (payloadLength > MaxBinaryPayloadLength) return null;
        if (frame.Length != BinaryHeaderLength + payloadLength) return null;
        if (payloadLength < BinaryEnvelopeLength) return null;

        var typeId = frame[2];
        if (!Enum.IsDefined(typeof(MessageType), (int)typeId)) return null;

        var envelopeSpan = frame.Slice(BinaryHeaderLength, BinaryEnvelopeLength);
        long timestamp  = BinaryPrimitives.ReadInt64BigEndian (envelopeSpan.Slice(0,  8));
        uint senderUint = BinaryPrimitives.ReadUInt32BigEndian(envelopeSpan.Slice(8,  4));
        uint roomHash   = BinaryPrimitives.ReadUInt32BigEndian(envelopeSpan.Slice(12, 4));
        var tokenBytes  = envelopeSpan.Slice(16, 32).ToArray();

        var bodySpan = frame.Slice(BinaryHeaderLength + BinaryEnvelopeLength);
        var bodyCopy = bodySpan.ToArray();

        return new Envelope(
            Type: (MessageType)typeId,
            SenderId: senderUint.ToString("x8"),
            SenderName: string.Empty,
            RoomId: string.Empty,
            Timestamp: timestamp,
            RawPayload: null,
            Version: frame[1],
            SessionToken: string.Empty,
            RawBinary: bodyCopy,
            SessionTokenBytes: tokenBytes,
            SenderUint: senderUint,
            RoomHash: roomHash);
    }

    /// <summary>
    /// Server-side emit path for the Phase 1 Error stub. Packs an <see cref="ErrorPayload"/>
    /// into the binary wire shape so that a binary-speaking client (none in Phase 1, only the
    /// framer's round-trip tests) can decode it through <see cref="ParseBinary"/>.
    /// The remaining message types stay JSON in Phase 1 — this overload exists solely to
    /// answer a well-formed binary frame whose type-id has no decoder yet.
    /// </summary>
    /// <remarks>
    /// Body layout (Error is not specified in the design doc because Error was not originally
    /// in the binary set; the layout is fixed here so Phase 1's stub round-trips):
    ///   [u16 BE codeLen][code UTF-8][u16 BE messageLen][message UTF-8]
    /// </remarks>
    public static byte[] SerializeBinary(NetMessage<ErrorPayload> msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        var payload = msg.Payload ?? new ErrorPayload();

        var codeBytes    = Encoding.UTF8.GetBytes(payload.Code ?? string.Empty);
        var messageBytes = Encoding.UTF8.GetBytes(payload.Message ?? string.Empty);
        if (codeBytes.Length    > ushort.MaxValue) throw new ArgumentException("Error code too long for u16 length prefix", nameof(msg));
        if (messageBytes.Length > ushort.MaxValue) throw new ArgumentException("Error message too long for u16 length prefix", nameof(msg));

        int bodyLength = 2 + codeBytes.Length + 2 + messageBytes.Length;
        int payloadLength = BinaryEnvelopeLength + bodyLength;
        if (payloadLength > MaxBinaryPayloadLength)
            throw new ArgumentException($"Encoded frame exceeds {MaxBinaryPayloadLength}-byte cap", nameof(msg));

        byte[] frame = new byte[BinaryHeaderLength + payloadLength];

        // Header.
        frame[0] = BinaryMagic;
        frame[1] = BinaryVersion;
        frame[2] = (byte)msg.Type;
        frame[3] = (byte)((payloadLength >> 16) & 0xFF);
        frame[4] = (byte)((payloadLength >> 8) & 0xFF);
        frame[5] = (byte)(payloadLength & 0xFF);

        var envelopeSpan = frame.AsSpan(BinaryHeaderLength, BinaryEnvelopeLength);
        BinaryPrimitives.WriteInt64BigEndian (envelopeSpan.Slice(0, 8),  msg.Timestamp);
        // SenderId is the 8-hex-char UserId form; uint conversion mirrors the JSON path's senderUint.
        // Falls through to 0 if SenderId isn't 8 hex chars (the server identity for "server"-origin
        // frames doesn't fit; the binary stub only needs the slot filled, not round-trip identity).
        uint senderUint = TryParseSenderUint(msg.SenderId);
        BinaryPrimitives.WriteUInt32BigEndian(envelopeSpan.Slice(8,  4), senderUint);
        // roomHash = 0 in the Phase 1 stub: the canonical xxhash32(NFC(roomId)) helper is a Phase 2
        // deliverable (Shared/Util/RoomKey.cs in the design doc); the field's slot is reserved here.
        BinaryPrimitives.WriteUInt32BigEndian(envelopeSpan.Slice(12, 4), 0u);
        // sessionToken slot: 32 zero bytes on the broadcast/server path per the design doc
        // (broadcasts MUST strip the originator's token); the slot stays present so the wire
        // shape is uniform across encodings.
        envelopeSpan.Slice(16, 32).Clear();

        var bodySpan = frame.AsSpan(BinaryHeaderLength + BinaryEnvelopeLength);
        BinaryPrimitives.WriteUInt16BigEndian(bodySpan.Slice(0, 2), (ushort)codeBytes.Length);
        codeBytes.CopyTo(bodySpan.Slice(2));
        int afterCode = 2 + codeBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(bodySpan.Slice(afterCode, 2), (ushort)messageBytes.Length);
        messageBytes.CopyTo(bodySpan.Slice(afterCode + 2));

        return frame;
    }

    /// <summary>
    /// Decode the Phase 1 Error stub body back into an <see cref="ErrorPayload"/>.
    /// Mirrors <see cref="SerializeBinary"/>'s body layout. Returns <c>null</c> on underrun.
    /// </summary>
    public static ErrorPayload? DecodeErrorBody(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) return null;
        int codeLen = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(0, 2));
        if (body.Length < 2 + codeLen + 2) return null;
        var codeStr = Encoding.UTF8.GetString(body.Slice(2, codeLen));
        int after = 2 + codeLen;
        int msgLen = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(after, 2));
        if (body.Length < after + 2 + msgLen) return null;
        var msgStr = Encoding.UTF8.GetString(body.Slice(after + 2, msgLen));
        return new ErrorPayload { Code = codeStr, Message = msgStr };
    }

    private static uint TryParseSenderUint(string senderId)
    {
        if (string.IsNullOrEmpty(senderId)) return 0;
        return uint.TryParse(senderId, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0u;
    }
}
