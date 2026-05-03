# Custom binary frame (alongside newline-JSON)

## Elevator

Today every NetDraw message is a `NetMessage<T>` serialized to JSON with
Newtonsoft and terminated by `\n`. Both the server's `ClientHandler` and the
client's `NetworkService` split their inbound stream on `\n` and feed each line
into `MessageEnvelope.Parse(string)`. This is fine — until you watch a Pen
stroke. A 50-point freehand pen frame on the wire today is roughly 600 bytes of
JSON describing 100 doubles, two GUIDs, a hex colour, and a half-dozen styling
fields. At 60 Hz pen sampling that's ~36 KB/s per active stroker per room; at
the 10-user room cap it's ~360 KB/s. JSON earns its keep on `JoinRoom`,
`ChatMessage`, `AiCommand` (Vietnamese text, complex shapes, low rate). It does
not earn its keep on Pen.

After this change, the same TCP stream carries two wire formats. The first byte
of every line tells the receiver which it is: `{` (`0x7B`) → JSON, `0xFE` →
binary frame. JSON frames stay newline-delimited and pass through the existing
parser unchanged. Binary frames are length-prefixed, contain no newline framing
(payload bytes can include `0x0A` freely), and decode through a parallel
`MessageEnvelope.ParseBinary(span)` path that ends up populating the same
`NetMessage<T>` shape the rest of the server already consumes. The set of
binary-encoded message types is small — Pen, Cursor, DrawPreview, Shape, Line,
Text, Erase — and chosen specifically because the JSON overhead dominates the
useful payload for those types.

For the prof: a Wireshark capture of port 5000 now shows newline-delimited
ASCII JSON and length-prefixed binary frames mixed on the same TCP connection,
each with its own `0xFE`-magic + version + type-id + length header. The
existing `tools/wireshark/netdraw.lua` dissector grows a per-line magic-byte
branch and a second decoder.

## Wire format

A binary frame is a 6-byte header, an envelope-binary fixed prefix, then a
type-specific payload. All multi-byte integers are **big-endian** (network byte
order), matching the UDP-cursor frame in `docs/design/udp-cursor-channel.md`
and the LB's xxhash32 prefix in `docs/design/load-balancer.md`.

### Header (6 bytes)

```
 byte | 0     | 1       | 2       | 3 4 5
------+-------+---------+---------+---------------
field | magic | version | type-id | payload-length
size  | 1 B   | 1 B     | 1 B     | 3 B (uint24 BE)
```

- `magic` — `0xFE`. Sentinel that distinguishes a binary frame from a JSON
  frame at the per-line dispatch site. JSON envelopes always start with `{`
  (`0x7B`) and JSON arrays with `[` (`0x5B`); the codebase never emits
  whitespace before a frame, so the first byte is unambiguous. `0xFE` was
  picked over `0xFF` because `0xFF` is a common "all-bits-set" sentinel that
  shows up in random uninitialised buffers; `0xFE` is the next-cleanest
  high-bit value that clearly cannot be UTF-8 (`0xFE` and `0xFF` are
  forbidden lead bytes in UTF-8). The UDP cursor frame uses the two-byte
  `'N','D'` magic; over UDP the datagram boundary is the framing and the
  receiver sees the magic instantly. Over TCP we have to decide what to peek
  *before* we even know how much to read, and a one-byte test is cleaner.

- `version` — `1`. Bumped per breaking format change. A receiver that gets
  `version > MaxSupported` reads and discards exactly `payload-length` bytes
  from the stream, sends a JSON `Error` envelope explaining the version
  miss, and keeps the TCP connection up. (Closing on unknown version would
  punish the next 3000 in-flight JSON frames for a single stale binary one.)

- `type-id` — one byte, mirroring the integer ordinal of `MessageType` in
  `NetDraw.Shared/Protocol/MessageType.cs`. The dissector and the binary
  parser both use this for the per-type decoder branch. Reserves 0–255 — far
  more than the 19 message types today.

- `payload-length` — `uint24 BE`. Byte count of everything after the header
  (envelope + type-specific body), not including the header itself. 24 bits
  caps a single frame at 16 MiB, which is the receiver-side guard the brief
  asks for. A 2-byte length would cap at 64 KiB which `CanvasSnapshot` would
  trip the moment images live in room history; a 4-byte length is louder
  than the cap we actually want to enforce. 3 bytes is the lens that matches
  the cap.

The header is **always** 6 bytes. There is no continuation, no extended
length, no optional fields. A receiver that reads the 6 header bytes
immediately knows how many more bytes to consume to finish this frame.

### Envelope prefix (48 bytes)

After the header come 48 bytes of envelope, common to every binary frame
regardless of type-id:

```
 byte | 0..7      | 8..11      | 12..15    | 16..47
------+-----------+------------+-----------+------------------
field | timestamp | senderUint | roomHash  | sessionToken
size  | 8 B (BE)  | 4 B (BE)   | 4 B (BE)  | 32 B (raw bytes)
```

- `timestamp` — `int64 BE` ms since Unix epoch. Same as the JSON
  `NetMessage<T>.Timestamp` field.

- `senderUint` — `uint32 BE`, the same value the UDP cursor frame carries:
  `Convert.ToUInt32(userId, 16)` against the existing 8-hex-char `UserId`
  produced by `Guid.NewGuid().ToString("N")[..8]` in
  `NetworkService.cs:38`. The full 8-hex `UserId` string is recoverable as
  `senderUint.ToString("x8")`, so the binary path round-trips losslessly to
  the same `NetMessage<T>.SenderId` populated by the JSON path. The display
  name (`SenderName`) is **not** in the binary envelope; receivers look it
  up in their local `Dictionary<uint, UserInfo>` populated from the existing
  JSON-path `RoomJoinedPayload.users` and `UserJoined`/`UserLeft` messages.
  An unknown `senderUint` (race between a `UserJoined` and the first binary
  frame from that user) renders with a placeholder name and gets corrected
  by the next presence update — same fail-soft as a bare cursor seen before
  presence catches up. `UserId` is the 8-hex truncation of a fresh GUID, so
  birthday collisions land at ~1 in 10⁸ for 10 users in a room — non-zero
  but far below the rate at which two strangers pick the same display name.
  The client map is last-write-wins on collision: the second `UserJoined`
  with the same `senderUint` overwrites the first entry's display name and
  colour, same fail-soft as the cursor presence map.

- `roomHash` — `uint32 BE`, computed as
  `XxHash32.Hash(UTF8(NFC(roomId)))` using the same
  `NetDraw.Shared/Util/XxHash32.cs` the LB design adds. The NFC pass plus
  the hash live behind a single helper at `Shared/Util/RoomKey.cs` (to be
  created); TCP-binary, UDP-cursor, and load-balancer paths all call into
  it so the four-byte value on every wire is bit-identical for the same
  human roomId. Server compares against the room the connection's
  `ClientHandler` is bound to and drops on mismatch. The binary path never
  carries the human-readable `roomId` string — `JoinRoom` (the only frame
  that needs the string) stays JSON.

- `sessionToken` — 32 raw bytes. The same secret the JSON envelope's
  `sessionToken` field carries (per `docs/design/session-token.md`),
  base64url-decoded into its raw 32-byte form. The dispatcher's existing
  `CryptographicOperations.FixedTimeEquals(presented, sender.SessionTokenBytes)`
  check works against these 32 bytes directly — no base64 round-trip, one
  fewer allocation per frame. **Why include it at all** when the
  connection's TCP socket already pins identity: keeping the wire contract
  uniform across encodings means the auth model never reads
  "depends on which encoding the message used"; the dissector shows the
  field on both paths; and 32 bytes per frame is 11% of a Pen frame's size
  and disappears completely in the bandwidth math vs JSON. The alternative
  (token bound to connection only, omitted in binary) is documented in
  Open Questions for a future bandwidth-pressed revision.

### Type-specific body

Everything after byte 53 of the frame is the per-type payload. The format
varies by type-id; the next section gives byte-by-byte layouts for the three
types that ship in Phase 2/3, with sketches for the rest in Phase 4.

### Three example frames

#### Example 1 — `Draw` carrying a 50-point `PenAction`

```
+------+------+------+------+------+------+
| 0xFE | 0x01 | 0x06 | 0x00 0x01 0x26    |    header (6 B)
| magic| ver  | Draw |   length = 294    |
+------+------+------+------+------+------+
| 8 B BE timestamp                       |
| 4 B BE senderUint  | 4 B BE roomHash   |    envelope (48 B)
| 32 B sessionToken                      |
+----------------------------------------+
| 0x01                                   |    actionTag = pen
| 16 B Id (raw GUID bytes)               |
| 0x01 + 16 B GroupId                    |    presence-flag + body
| 0x00 0x00 0x00                         |    color RGB (3 B)
| 4 B BE float strokeWidth               |
| 0xFF                                   |    opacity = 1.0 (255)
| 0x00                                   |    dashStyle = Solid
| 0x00                                   |    penStyle = Normal
| 0x00 0x32                              |    pointCount = 50
| 50 × (int16 BE x, int16 BE y) = 200 B  |    points
+----------------------------------------+
```

Body = 1 + 16 + 1 + 16 + 3 + 4 + 1 + 1 + 1 + 2 + 200 = 246 B.
`payload-length` = envelope(48) + body(246) = **294** (`0x000126`).
Total = header(6) + payload-length(294) = **300 bytes**.

#### Example 2 — `CursorMove`

```
+------+------+------+------+------+------+
| 0xFE | 0x01 | 0x0E | 0x00 0x00 0x37    |    header (6 B)
| magic| ver  | Curs |   length = 55     |
+------+------+------+------+------+------+
| 48 B envelope                          |
+----------------------------------------+
| 0x00 0x00 0x00 0x00                    |    int16 BE x, int16 BE y
| 0x00 0x00 0x00                         |    color RGB (3 B)
+----------------------------------------+
```

Body = 7 B. `payload-length` = envelope(48) + body(7) = **55** (`0x000037`).
Total = header(6) + payload-length(55) = **61 bytes**.

This is the TCP-fallback path for cursor presence; the hot path is UDP
(see `docs/design/udp-cursor-channel.md`). When UDP is blocked or absent,
clients still send `CursorMove` over TCP, and the binary form here
replaces the ~180 B JSON envelope they would otherwise emit.

#### Example 3 — `Draw` carrying a `ShapeAction` (Rect)

```
+------+------+------+------+------+------+
| 0xFE | 0x01 | 0x06 | 0x00 0x00 0x5D    |    header (6 B)
| magic| ver  | Draw |   length = 93     |
+------+------+------+------+------+------+
| 48 B envelope                          |
+----------------------------------------+
| 0x02                                   |    actionTag = shape
| 16 B Id                                |
| 0x00                                   |    no GroupId
| 0x33 0x66 0x99                         |    color RGB
| 4 B BE float strokeWidth               |
| 0xFF                                   |    opacity = 1.0
| 0x00                                   |    dashStyle = Solid
| 0x00                                   |    shapeType = Rect
| 4 B BE float X | 4 B BE float Y        |
| 4 B BE float W | 4 B BE float H        |
| 0x00                                   |    no FillColor (presence flag)
+----------------------------------------+
```

Body = 1 + 16 + 1 + 3 + 4 + 1 + 1 + 1 + 16 + 1 = 45 B.
`payload-length` = envelope(48) + body(45) = **93** (`0x00005D`).
Total = header(6) + payload-length(93) = **99 bytes**.

For comparison the JSON `Draw{Shape}` runs ~330 B at the same shape: the
two GUID strings, the type discriminators, every styling field as JSON,
the envelope around it. ~3.3× ratio.

## Coexistence with JSON

The framing rule is per-line, decided by the first byte after the previous
frame ends:

| First byte    | Path                                                  |
|---------------|--------------------------------------------------------|
| `0x7B` (`{`)  | JSON envelope — read up to next `\n`, parse as today.  |
| `0xFE`        | Binary frame — read 6-byte header, then `length` bytes.|
| `0x5B` (`[`)  | Reserved. `MessageEnvelope.Parse` calls `JObject.Parse`, which rejects a top-level array; if a future revision wants a batch envelope, the parser switches to `JToken.Parse`. Until then this byte is treated as Unknown framing and rejected. |
| `0x0D` `0x0A` `0x09` `0x20` | Whitespace between frames; skip and re-peek. |
| anything else | Unknown framing. Send `Error` envelope, close connection. |

The receiver's read loop becomes a small state machine instead of a single
`IndexOf('\n')` scan. The relevant change is in `ClientHandler.ProcessBufferAsync`
and the symmetric block in `NetworkService.ListenAsync`; both are sketched
in "Server wiring" / "Client wiring" below.

The byte buffer is **bytes**, not chars. Today both the server and client
maintain a `StringBuilder` of decoded UTF-8 chars; that has to change — a
binary frame's payload bytes are not UTF-8 and trying to decode them through
`Encoding.UTF8.GetDecoder()` corrupts both the binary payload and the
decoder state. The parser switches to a `byte[]` buffer (or
`ArrayBufferWriter<byte>`) and only invokes the UTF-8 decoder on the slice
of bytes between a `0x7B` and the corresponding `\n`. Symmetric on both
sides.

This is the one piece of plumbing that has to ship in Phase 1 even though
no message types are converted to binary yet — the framer has to be
bytewise-correct before the first binary frame ever flies, otherwise the
first one corrupts the JSON parser's UTF-8 state and the demo dies in a
strange place.

## Per-type encoding

Binary set: `Draw`, `DrawPreview`, `CursorMove`. Everything else
(`JoinRoom`, `RoomJoined`, presence, `ChatMessage`, `AiCommand`,
`AiResult`, `CanvasSnapshot`, `Error`, `ClearCanvas`, `Undo`/`Redo`,
`MoveObject`, `DeleteObject`) stays JSON.

The split is: high-rate, point-heavy types where JSON's overhead exceeds
the useful payload go binary; everything else — control, presence,
Vietnamese chat, AI prompts, snapshots, debug — stays JSON, where the
debuggability and string-friendliness pay off and the per-frame rate is
already low.

### `Draw` / `DrawPreview` body

After the 48-byte envelope, the body starts with a 1-byte action tag that
mirrors the JSON discriminator string in `DrawActionConverter.cs`:

| action tag | JSON `"type"` | C# class       |
|------------|---------------|----------------|
| `0x01`     | `"pen"`       | `PenAction`    |
| `0x02`     | `"shape"`     | `ShapeAction`  |
| `0x03`     | `"line"`      | `LineAction`   |
| `0x04`     | `"text"`      | `TextAction`   |
| `0x05`     | `"image"`     | `ImageAction`  |
| `0x06`     | `"erase"`     | `EraseAction`  |

Then the `DrawActionBase` common header:

```
field      | size | encoding
-----------+------+------------------------------------------------------
Id         | 16 B | raw GUID bytes (Guid.ToByteArray())
groupFlag  | 1 B  | 0x00 = no GroupId, 0x01 = GroupId present
GroupId    | 16 B | only if groupFlag == 0x01
color      | 3 B  | parsed from "#RRGGBB" → R,G,B bytes
strokeWidth| 4 B  | float32 BE
opacity    | 1 B  | uint8: round(opacity * 255). lossy below 1/255 of a step,
           |      | which is invisible at any rendering this app does
dashStyle  | 1 B  | 0=Solid, 1=Dashed, 2=Dotted (matches enum ordinal)
```

`UserId` is **not** repeated here — the envelope's `senderUint` already
carries it. `UserName` is not on the wire — receivers look it up in the
presence map. This shaves ~50 B off every Pen frame and is the primary
reason the binary form is dramatically smaller than the JSON form even
before the points are encoded.

#### `PenAction` body (action tag `0x01`)

```
field      | size  | encoding
-----------+-------+------------------------------------------------------
penStyle   | 1 B   | 0=Normal, 1=Calligraphy, 2=Highlighter, 3=Spray
pointCount | 2 B   | uint16 BE; receiver caps at 16384 (1.7× the largest
           |       | stroke seen in stress testing) and drops the frame
           |       | with an Error if it exceeds, since the only legitimate
           |       | path to this many points is a runaway client
points     | n × 4 | n × (int16 BE x, int16 BE y), canvas pixel coords.
                     Sender clamps to [0, 16384) on each axis (the canvas
                     is 3000 × 2000 today; int16 has 5× headroom). int16
                     was chosen to match UDP cursor's choice for the same
                     reason: sub-pixel precision is invisible at the
                     stroke widths the renderer uses, and halving the
                     per-point cost saves ~200 B on every 50-point stroke.
```

#### `ShapeAction` body (action tag `0x02`)

```
field      | size  | encoding
-----------+-------+------------------------------------------------------
shapeType  | 1 B   | 0=Rect, 1=Ellipse, 2=Circle, 3=Triangle, 4=Star
X          | 4 B   | float32 BE (kept as float; shapes are ≤ a few of them
                     per frame, the bandwidth saving over float would be
                     noise, and X/Y/W/H come from non-clamped WPF doubles
                     that we'd prefer not to round)
Y          | 4 B   | float32 BE
Width      | 4 B   | float32 BE
Height     | 4 B   | float32 BE
fillFlag   | 1 B   | 0x00 = no FillColor, 0x01 = present
FillColor  | 3 B   | only if fillFlag == 0x01
```

#### `CursorMove` body (type-id `0x0E`)

No action-tag byte (it's a top-level message type, not a `DrawAction`):

```
field | size  | encoding
------+-------+--------------------------------------------------------
x     | 2 B   | int16 BE, canvas pixel coords (clamped, see PenAction)
y     | 2 B   | int16 BE
color | 3 B   | R, G, B
```

Total cursor body = 7 bytes. With envelope + header that's a 61-byte
frame on the TCP fallback path.

### Phase 4 sketches

Remaining DrawAction subtypes follow the same pattern; layouts freeze
when they ship.

- `LineAction` — `[4B startX][4B startY][4B endX][4B endY][1B hasArrow]` = 17 B.
- `TextAction` — float fontSize, X, Y; 1B style-bits (bold/italic/underline/strike); `[2B textLen][textLen UTF-8]`; same for optional `fontFamily`. UTF-8 stays UTF-8.
- `EraseAction` — `[4B eraserSize][2B count][count × (int16 x, int16 y)]`. Mirrors PenAction.
- `ImageAction` — stays JSON in v1; base64 inside binary doesn't pay off, and image storage is being rethought separately.

## Length cap, version, AAD, and corruption

- **Length cap.** 16 MiB receiver-side. A frame with `payload-length >
  0xFF_FFFF` (the largest u24) is impossible — the wire encoding caps it.
  The 16 MiB *check* is therefore against the largest representable u24 at
  exactly `0xFF_FFFF`, not strictly greater than it; receiver-side guard is
  `payload-length > 16_000_000` (or any threshold the deployment picks
  below the u24 ceiling) and on hit we close the connection because the
  framer can't reliably re-sync inside what we hoped was binary. JSON path
  has no equivalent cap today; this brings binary up to a defensive parity.
- **Version.** Header byte. v1 ships with this design. A v2 receiver MUST
  also speak v1. Unknown future version → `Error` envelope, frame
  discarded by length, connection stays up.
- **Body underrun.** A per-type decoder that asks for more bytes than
  remain in the body slice (e.g. `pointCount` says 50 points but only
  120 bytes are left after the common header) drops the frame, sends an
  `Error` envelope with code `BINARY_BODY_UNDERRUN`, and keeps the TCP
  connection up. Mirrors the version-mismatch handling: the framer
  already consumed the declared `payload-length` bytes correctly, so the
  stream is still aligned on the next frame boundary; only the malformed
  body is lost.
- **Endianness.** All multi-byte ints big-endian; floats are the standard
  IEEE 754 32-bit binary representation, written big-endian
  (`BinaryPrimitives.WriteSingleBigEndian`).
- **AAD / authenticated framing.** Not applicable. The TLS layer
  (`docs/design/tls-in-house.md`) wraps the entire TCP stream after the
  4-byte LB prefix; it provides confidentiality, integrity, and
  authenticity for everything inside, JSON or binary, as a single record
  stream. Per-frame MACs would duplicate that. The header carries no
  HMAC, no checksum.
- **Corruption inside a TLS record.** No per-frame CRC because TLS —
  *once `--insecure` defaults to false* (TLS Phase 2). Until then a
  corrupt high byte in a body is silently rendered. TLS terminates the
  connection on MAC failure; we never see corrupt application bytes when
  TLS is on. When TLS is off (`--insecure` dev mode) a corrupt header
  byte causes the framer to drop the connection with `Error`; the resync
  alternative (scan ahead for the next `{` or `0xFE`) is a worse
  experience than a reconnect.

## Server wiring

### `MessageEnvelope.Parse` becomes byte-aware

Today's `MessageEnvelope.Parse(string json)` becomes a router. The
existing JSON-string path is preserved as the slow path; the new entry
point reads bytes:

```csharp
public static class MessageEnvelope
{
    // Existing record, enriched with binary-only fields when relevant.
    public record Envelope(
        MessageType Type,
        string SenderId,           // JSON: from envelope; binary: senderUint.ToString("x8")
        string SenderName,         // JSON: from envelope; binary: empty (filled by caller)
        string RoomId,             // JSON: from envelope; binary: empty (server compares hash)
        long Timestamp,
        JObject? RawPayload,           // populated on JSON path
        ReadOnlyMemory<byte> RawBinary,// populated on binary path
        byte[]? SessionTokenBytes,     // populated on binary path
        uint SenderUint,           // populated on binary path; 0 on JSON path
        uint RoomHash,             // populated on binary path; 0 on JSON path
        int Version);

    public static Envelope? Parse(string json) => /* existing JSON path, unchanged */;

    public static Envelope? ParseFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0) return null;
        if (frame[0] == (byte)'{') return Parse(Encoding.UTF8.GetString(frame));
        if (frame[0] == 0xFE)      return ParseBinary(frame);
        return null;
    }

    private static Envelope? ParseBinary(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 + 48) return null;
        var version = frame[1];
        var typeId  = frame[2];
        var len     = (frame[3] << 16) | (frame[4] << 8) | frame[5];
        if (frame.Length != 6 + len) return null;

        var ts          = BinaryPrimitives.ReadInt64BigEndian (frame.Slice(6, 8));
        var senderUint  = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(14, 4));
        var roomHash    = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(18, 4));
        var tokenBytes  = frame.Slice(22, 32).ToArray();
        var rawBody     = frame.Slice(6 + 48).ToArray();

        return new Envelope(
            (MessageType)typeId, senderUint.ToString("x8"), SenderName: "", RoomId: "",
            ts, RawPayload: null, RawBinary: rawBody,
            SessionTokenBytes: tokenBytes, senderUint, roomHash, version);
    }
}
```

`ParseBinary` does **not** look anything up — it returns the raw
`senderUint` and `roomHash`. Resolution is the caller's job, and the
caller's needs differ by side:

- **Server.** `MessageDispatcher` validates
  `senderUint == Convert.ToUInt32(handler.UserId, 16)` and
  `roomHash == XxHash32.Hash(UTF8(NFC(handler.RoomId)))` against the
  values already pinned on the `ClientHandler`. Mismatch → `Error`,
  same shape as the session-token mismatch reply. The server never
  needs a hash → name dictionary because it already knows the
  per-connection identity from JoinRoom; the binary-envelope fields are
  cross-checks against state the server holds, not lookups into state
  the server has to build.
- **Client.** `NetworkService` populates a small
  `Dictionary<uint, UserInfo>` keyed by `Convert.ToUInt32(userId, 16)`,
  filled from the JSON-path `RoomJoinedPayload.users` and the
  `UserJoined`/`UserLeft` presence stream. When a binary frame's
  `senderUint` arrives, the client looks it up to render colour and
  display name. An unknown senderUint (presence message hasn't arrived
  yet) renders with a placeholder until the next presence update.

The dispatcher then branches on `Envelope.RawPayload != null` (JSON
path) vs `Envelope.RawBinary.Length > 0` (binary path) and calls the
matching deserializer. The binary deserializer for `Draw` reads the
action tag byte, instantiates the matching `DrawActionBase` subclass,
populates it, and the rest of the handler chain sees the same
`NetMessage<T>` it always has.

### `ClientHandler.ProcessBufferAsync` becomes a framer

Today's handler decodes UTF-8, splits on `\n`, parses each line. The new
version reads raw bytes into a `byte[]` ring/grow buffer and walks it:

```csharp
private async Task ProcessBufferAsync(byte[] data, int dataLen)
{
    _buffer.Append(data, 0, dataLen);
    int pos = 0;
    while (pos < _buffer.Length)
    {
        byte b = _buffer[pos];

        if (b == 0xFE)
        {
            if (_buffer.Length - pos < 6) break; // need more bytes for the header
            int payloadLen = (_buffer[pos+3] << 16) | (_buffer[pos+4] << 8) | _buffer[pos+5];
            if (payloadLen > MaxFrameBytes)
            {
                await SendErrorAndClose($"binary frame length {payloadLen} > {MaxFrameBytes}");
                return;
            }
            int totalLen = 6 + payloadLen;
            if (_buffer.Length - pos < totalLen) break; // need more bytes for body

            var frame = _buffer.AsSpan(pos, totalLen);
            var envelope = MessageEnvelope.ParseFrame(frame);
            if (envelope != null) await EmitMessageReceived(envelope);
            pos += totalLen;
            continue;
        }

        if (b == (byte)'{' || b == (byte)'[')
        {
            int nl = _buffer.IndexOf((byte)'\n', pos);
            if (nl < 0) break; // need more bytes to find newline
            var jsonBytes = _buffer.AsSpan(pos, nl - pos);
            var envelope = MessageEnvelope.ParseFrame(jsonBytes);
            if (envelope != null) await EmitMessageReceived(envelope);
            pos = nl + 1;
            continue;
        }

        if (b == 0x0D || b == 0x0A || b == 0x09 || b == 0x20) { pos++; continue; }

        // Unknown framing.
        await SendErrorAndClose($"unrecognised framing byte 0x{b:x2}");
        return;
    }
    _buffer.Consume(pos);
}
```

`_buffer` becomes a small wrapper around `byte[]` with `Append`,
`Consume`, `AsSpan`, `IndexOf` — about 60 lines, mirrors what
`StringBuilder` did but bytewise. The existing UTF-8 `Decoder` field is
gone; the only place a byte slice gets decoded as UTF-8 is inside
`MessageEnvelope.Parse(string)` for a known-`{`-bounded JSON line.

The UTF-8-split-across-reads case the comment in `ClientHandler.cs:43`
worried about is now trivially handled: bytes never get decoded across a
read boundary because they only get decoded inside `Parse` once the
whole `{ ... }` line is in the buffer.

### `NetMessage<T>.Serialize`

Today returns `string`. The new shape returns `ReadOnlyMemory<byte>` and
chooses encoding per type:

```csharp
public ReadOnlyMemory<byte> SerializeBytes()
{
    if (BinaryEncoder.SupportsBinary(Type, Payload, out var encoder))
        return encoder.Encode(this);   // binary frame, no trailing \n
    var json = JsonConvert.SerializeObject(this, Formatting.None, SerializerSettings) + "\n";
    return Encoding.UTF8.GetBytes(json);
}
```

`BinaryEncoder.SupportsBinary` is a small lookup table:

- `Draw` / `DrawPreview` → yes, dispatch on the `DrawActionBase` subclass.
- `CursorMove` → yes.
- everything else → no, fall through to JSON.

If a `Draw`'s nested action is `ImageAction` (binary opt-out), the
encoder returns false for that one call and JSON is emitted instead.
This is per-frame, not per-connection — the wire happily carries
heterogeneous frames, which is the whole point.

The transmit side is symmetric on client (`NetworkService.SendAsync`)
and server (`ClientHandler.SendAsync`) — both already serialize through
`NetMessage<T>` and write the result to the stream.

## Client wiring

`NetworkService.ListenAsync` mirrors the server change byte-for-byte: a
`byte[]` buffer, the same framer state machine, the same
`MessageEnvelope.ParseFrame` call. The client also maintains a
`BinaryUserMap` populated as it sees presence messages, and reads the
payload type-id to pick the right deserializer.

`MainViewModel` and the rest of the WPF UI never see this. They consume
`NetMessage<T>` records that come out of the dispatcher exactly the
shape they always have. The only behavioural change a UI dev might
notice: a Pen received over the binary path has a fully populated
`SenderName` only if the sender's `UserJoined` was already processed,
otherwise a placeholder. Same fail-soft policy as the cursor presence
map already uses.

## Wireshark dissector extension

`tools/wireshark/netdraw.lua` already dispatches per `\n`-line in
`netdraw_proto.dissector` (the per-line loop at lines 393–420 of the
current file). The change is in two spots:

1. The per-line loop peeks the first byte and branches.
2. A new `dissect_binary_message(line_tvb, ...)` function decodes the
   header + envelope + per-type body.

Sketch:

```lua
-- New ProtoFields (added to the existing fields list at top of file)
local f_bin_magic   = ProtoField.uint8 ("netdraw.bin.magic",   "Magic", base.HEX)
local f_bin_version = ProtoField.uint8 ("netdraw.bin.version", "Version", base.DEC)
local f_bin_type    = ProtoField.uint8 ("netdraw.bin.type",    "Type id", base.DEC)
local f_bin_length  = ProtoField.uint32("netdraw.bin.length",  "Payload length", base.DEC)
local f_bin_ts      = ProtoField.uint64("netdraw.bin.ts",      "Timestamp (ms)", base.DEC)
local f_bin_sender  = ProtoField.uint32("netdraw.bin.sender",  "Sender uint32", base.HEX)
local f_bin_room    = ProtoField.uint32("netdraw.bin.room",    "Room hash", base.HEX)
local f_bin_token   = ProtoField.bytes ("netdraw.bin.token",   "Session token (32B)")
local f_bin_summary = ProtoField.string("netdraw.bin.summary", "Body summary")

-- In the existing dissector function, the per-line loop becomes:
function netdraw_proto.dissector(tvb, pinfo, tree)
    local len = tvb:len()
    if len == 0 then return 0 end
    pinfo.cols.protocol = netdraw_proto.name
    pinfo.cols.info = ""

    local raw = tvb:raw()
    local offset = 0
    while offset < len do
        local first = raw:byte(offset + 1)

        if first == 0xFE then
            -- Binary frame: need 6-byte header to know length.
            if len - offset < 6 then
                pinfo.desegment_offset = offset
                pinfo.desegment_len = DESEGMENT_ONE_MORE_SEGMENT
                return len
            end
            local payload_len = raw:byte(offset + 4) * 0x10000
                              + raw:byte(offset + 5) * 0x100
                              + raw:byte(offset + 6)
            local total = 6 + payload_len
            if len - offset < total then
                pinfo.desegment_offset = offset
                pinfo.desegment_len = total - (len - offset)
                return len
            end
            local frame_tvb = tvb(offset, total)
            dissect_binary_message(frame_tvb, pinfo, tree)
            offset = offset + total

        elseif first == 0x7B or first == 0x5B then
            -- JSON line: existing path, find next '\n' from absolute offset.
            local nl = raw:find("\n", offset + 1, true)
            if nl == nil then
                pinfo.desegment_offset = offset
                pinfo.desegment_len = DESEGMENT_ONE_MORE_SEGMENT
                return len
            end
            local line_tvb = tvb(offset, nl - offset)
            dissect_message(line_tvb, pinfo, tree)
            offset = nl

        elseif first == 0x0D or first == 0x0A or first == 0x09 or first == 0x20 then
            offset = offset + 1
        else
            -- Bad framing byte; consume one and keep going so dissection
            -- doesn't get stuck on a single bad byte in a corrupt capture.
            offset = offset + 1
        end
    end
    return offset
end
```

`dissect_binary_message` adds the header fields, then the envelope
fields, then dispatches on type-id to a per-type body summarizer that
mirrors the existing `summarize_draw` / `summarize_cursor` shape. The
field dictionary `MESSAGE_TYPE_NAMES` already exists; reuse it for the
type-id label.

Note: the unknown-leading-byte branch in the dissector consumes one byte
and continues — deliberately *not* the same as `ClientHandler`, which
closes the connection. The dissector reads from packet captures we can't
re-request; bailing out of dissection on the first bad byte would hide
every well-formed frame later in the same capture. The server has a live
peer that can reconnect, so dropping a desynced connection is the safer
option there. Phase-5 dissector implementer: do not copy the server's
close-the-connection branch.

The dissector lands as a single Phase-5 PR after the wire format ships
and a sample capture exists to validate against.

## Interactions

**LB prefix (`docs/design/load-balancer.md`).** The 4-byte
xxhash32(roomId) prefix sits *before* TLS and *before* any framing, on
the raw TCP stream as the first thing the client writes. Binary frames
are per-message and live entirely after that prefix. No conflict — the
LB doesn't peek the framing layer at all, it byte-pumps everything after
the prefix in both directions.

**TLS (`docs/design/tls-in-house.md`).** TLS wraps the entire post-LB
TCP stream as one record sequence. The framer reads from `SslStream`
instead of `NetworkStream` and gets cleartext bytes back; it doesn't
care whether the underlying transport is plain TCP or TLS. Binary frames
are not record-aligned and don't try to be — TLS records are
opportunistic and may straddle frame boundaries freely. The framer's
"need more bytes" loop already handles partial reads.

**Session token (`docs/design/session-token.md`).** The token's wire
form changes from "base64url string in JSON envelope" to "32 raw bytes
in binary envelope" but the dispatcher's check is identical: compare
against `sender.SessionTokenBytes` with `FixedTimeEquals`. The
dispatcher stops needing to call `TryBase64UrlDecode` on the binary
path. Broadcast-side rule from the session-token doc (server MUST strip
the originator's token before fanning the message out) applies to both
encodings — the binary-encoder writes 32 zero bytes into the token slot
on the broadcast path. JSON path keeps doing what it does today (omit
the field on outbound).

**UDP cursor (`docs/design/udp-cursor-channel.md`).** The UDP cursor
frame and the binary `CursorMove` frame are different beasts: UDP is the
hot path (lossy-OK, 24-byte format optimised for datagram framing); TCP
binary `CursorMove` is the fallback path that already exists in the
protocol and just gets ~3× cheaper. A client whose UDP path is healthy
should never emit a TCP `CursorMove`; a client whose UDP path is dead
emits TCP `CursorMove` and the binary form here is the bandwidth win on
that path. The `senderUint` and `roomHash` fields in the envelope match
the UDP cursor frame's fields by design — same 32-bit-from-8-hex-GUID
trick, same xxhash32(NFC(roomId)) via the shared `Shared/Util/RoomKey.cs`
helper. An attentive grader sees the consistency.

Follow-up: udp-cursor-channel.md prose says NFC but its current code
samples skip the normalize step before hashing. That doc needs the same
RoomKey.cs centralization edit so both paths land on the helper together
— mismatched roomHash between TCP-binary and UDP-cursor would route a
client's pen to one shard and their cursor to another.

**LAN discovery (`docs/design/lan-discovery-and-server-cache.md`).**
UDP multicast on a different port (5099). No interaction.

## Phases

**Phase 1 (M) — Framer plumbing, no types converted.** Switch
`ClientHandler` and `NetworkService` from the `StringBuilder` + UTF-8
decoder model to the `byte[]` + per-line peek state machine. Add
`MessageEnvelope.ParseFrame(ReadOnlySpan<byte>)`. Wire up the
client-side `Dictionary<uint, UserInfo>` populated from JSON presence
messages. Add `0xFE` magic handling that *recognises* a binary frame
and emits `Error` "binary type not yet supported" so the parser is
exercised before any encoder ships. Introduce `MaxFrameBytes` (16 MiB).
All existing JSON traffic continues exactly as today; round-trip tests
in `NetDraw.Server.Tests` and `NetDraw.Shared.Tests` confirm zero
regression. Estimate: 4 days for a junior dev. The risky bit is the
bytes-not-chars buffer rewrite — the rest is mechanical, and the
UTF-8-split-across-reads case the existing comments worry about goes
away once decoding only happens inside `{...}` boundaries.

The Phase 1 client-side binary `Error` path is exercised by test frames
only — no client emits binary in Phase 1, so implementers should not
speculatively wire client-side fallback against it. Server emits the
"binary type not yet supported" `Error` for hand-crafted test frames;
production clients never see it.

**Phase 2 (M) — `Draw{Pen}` binary encoder + decoder.** Add
`PenBinaryEncoder` and `PenBinaryDecoder`. `NetMessage<T>.SerializeBytes()`
table-dispatches to the encoder for `Draw` whose action is `PenAction`.
Round-trip test: 50-point `PenAction`, serialize, deserialize,
field-by-field assert. Manual demo: two clients, freehand stroke,
`tcpdump` shows `0xFE` followed by ~300 B vs ~600 B JSON. Estimate: 3
days.

**Phase 3 (S) — `CursorMove` and `DrawPreview` binary.**
`CursorBinaryEncoder` (61 B frame). Reuse `PenBinaryEncoder` for
`DrawPreview`. Estimate: 1 day.

**Phase 4 (S) — `Shape`, `Line`, `Text`, `Erase`.** Per-subtype
encoders. `ImageAction` returns false from the encoder and falls back
to JSON. Estimate: 2 days.

**Phase 5 (S) — Wireshark dissector extension.** Update
`tools/wireshark/netdraw.lua` per the sketch above. Capture a mixed
JSON + binary session, verify both decode. Update
`tools/wireshark/README.md` and `tools/wireshark/test-capture.md`.
Estimate: 2 days.

Total: ~12 days for one junior C# dev; the bytes-not-chars rewrite
eats about a third. An experienced dev lands Phase 1 in two days and
the rest in another four.

## Bandwidth math

For the canonical "50-point Pen stroke at default styling" benchmark:

**JSON frame (today):**

```json
{"version":1,"type":6,"senderId":"f3a2c1b0","senderName":"Alice",
 "roomId":"demo","timestamp":1746230400000,"sessionToken":"k4_8r2VwQ1E…",
 "payload":{"action":{"id":"7c3e1d2a-...-...","type":"pen","userId":"f3a2c1b0",
            "userName":"Alice","color":"#000000","strokeWidth":2,"opacity":1,
            "dashStyle":0,"penStyle":0,
            "points":[{"x":123.456,"y":78.901},{...}, ...50 entries...]}}}
```

Roughly:
- Envelope (`version`, `type`, `senderId`, `senderName`, `roomId`,
  `timestamp`, `sessionToken`): ~155 B
- DrawPayload + DrawAction common: ~135 B (two GUID strings dominate)
- 50 points as JSON objects: ~285 B (avg ~5.7 B per coord with quotes,
  comma, colon, key)
- Trailing `\n`: 1 B

Total: **~580 B** (the brief said ~600; a stroke with no `groupId` and
short coord values lands closer to 540).

**Binary frame:**
- Header: 6 B
- Envelope: 48 B
- Action tag + GUID + groupFlag(0): 18 B
- Color + strokeWidth + opacity + dashStyle + penStyle: 10 B
- Point count + 50 × (int16, int16): 202 B

Total: **284 B** for a Pen with no GroupId; **300 B** with a GroupId
(284 + 16 = 300, matching Example 1).

**Ratio: ~2× reduction** at 50 points. Crossover is around 5 points;
real strokes are 30–500 points, so binary wins on every realistic Pen.

`CursorMove`: JSON ~180 B → binary 61 B = **3× reduction**. This is
the TCP fallback path; UDP cursor remains the 24 B-payload hot path.

10-user room, every user pen-drawing at 60 Hz, fanout to 9 peers each:
JSON 60 × 10 × 580 × 9 = **3.13 MB/s TCP egress** per server vs binary
**1.57 MB/s**. Real, but not the headline. The headline is the
multi-format-on-one-stream demo: Wireshark shows two protocols
side-by-side on the same TCP connection, distinguished by a single
peek-byte at every frame boundary.

## Open questions

1. **Connection-bound token vs per-frame token.** Including the 32-byte
   session token in every binary frame is paperwork: the TCP connection
   already pins identity, and the dispatcher's compare is trivially
   against `sender.SessionTokenBytes` cached on the handler. Omitting
   it would shave 32 B per frame (~10% on Pen, ~50% on CursorMove). The
   conservative call is to keep it for protocol uniformity and
   dissector symmetry; a future revision could drop it with a version
   bump if we accept "binary path authenticates by connection identity,
   JSON path by token field".

2. **`Color` parsing fall-through.** The 3-byte RGB rejects anything
   that isn't strict `#RRGGBB`. A few WPF brushes serialize as
   `#AARRGGBB`. The encoder falls back to JSON for that one frame; an
   alternative is an alpha byte in the common header. Default: fall
   back; revisit if `#AARRGGBB` shows up in real captures.

## Out of scope

- Compression (LZ4, deflate) of either JSON or binary frames. Adds a
  dependency, complicates the dissector, and the JSON win we have left
  on the table is `ChatMessage` / `AiCommand` text where compression
  helps least anyway.
- `ImageAction` binary encoding. The base64-vs-binary win on a
  multi-megabyte image is real, but image storage is being rethought
  separately and the binary frame's 16 MiB cap interacts with that
  rework.
- A separate "binary control frames" subprotocol for keepalive / ping.
  TCP keepalive plus the existing `Error` envelope cover what we need.
- Schema versioning beyond the 1-byte version field. No schema registry,
  no IDL. The C# class hierarchy is the schema; the encoder/decoder
  pair is hand-written per type.
- Replacing the JSON path. The two formats are designed to coexist
  forever; JSON stays the debug-friendly default for control,
  presence, chat, AI, and one-shot setup messages.
- DTLS, FEC, or any reliability/encryption layer below the framer.
  TLS handles confidentiality and integrity for everything; reliability
  is TCP's job.
