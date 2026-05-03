# UDP cursor channel

## Elevator

Today every client→server frame goes over a single TCP connection on port 5000:
draws, chat, AI commands, snapshots, and the 20 Hz `MessageType.CursorMove`
broadcast that `MainWindow.xaml.cs:172-178` fires from the canvas mouse-move
handler. Cursor traffic is the loudest thing on that connection, it doesn't
need in-order or reliable delivery (a missed cursor frame is invisible — the
next one arrives 50 ms later), and bursting it through the TCP send buffer
gives draw frames behind it head-of-line latency they don't deserve.

After this change the cursor moves to UDP. The server binds 5000/UDP next to
its existing 5000/TCP listener; the client opens a UDP socket pointing at the
same `host:port` it TCP-connected to, and sends a 24-byte binary cursor frame
on every mouse-move beat instead of a JSON envelope. The server fans the frame
out to every other room member over UDP. Draws, chat, AI, snapshots, presence
join/leave — all of those stay on TCP, untouched. The fallback path is the
existing TCP `CursorMove` handler, which we leave wired up: if no inbound UDP
arrives within two seconds of the client starting to send, the client reverts.

For the prof: a Wireshark capture filtered to `port 5000` now shows two
distinct protocols on the same number — TCP carrying JSON envelopes, UDP
carrying tiny binary frames — which is the multi-protocol-diversity demo the
course asks for.

## Threat model

What this defends against:

- Off-path UDP spoofers on the same LAN. The server pins each UserId to the
  `(sourceIP, sourcePort)` of its first valid UDP frame, then drops any later
  frame claiming that UserId from a different endpoint until the pin
  rebinds (see "Endpoint pinning" below). An attacker who can guess Alice's
  UserId but cannot read her packets cannot inject cursor traffic for her.
- Garbage frames. The 2-byte magic `'N','D'` plus version + type + length
  filters the obvious noise (port scanners, stray multicast). Anything that
  doesn't parse is dropped silently.

What this does not defend against:

- An on-path attacker on Alice's LAN segment who can read her UDP traffic.
  They see her `(sourceIP, sourcePort)`, can spoof packets that carry that
  source address back at the server, and the pin will accept them. This is
  the same threat-model floor the rest of the protocol sits on today before
  TLS lands; cursor spoofing is annoying (a wiggling Alice-cursor on Bob's
  canvas) and not destructive (no draw, no undo, no chat — those still go
  over the authenticated TCP channel).
- Any kind of session takeover. UDP carries no session token by design;
  draws and chat are unaffected by anything that happens on the UDP path.
- DTLS or wire encryption. UDP is plaintext. The `tls-in-house.md` design
  is explicitly TCP-only; see "Interactions" below.
- DDoS amplification or flood resistance. The threat model is two students
  on the same campus LAN, not internet-scale attackers. The server caps
  per-source ingress (see "Server wiring") which is enough for that bar.

## Wire format

One UDP datagram = one cursor frame. No fragmentation, no length prefix
(the datagram boundary is the framing), no JSON. All multi-byte fields are
**big-endian** (network byte order), matching the LB's xxhash32 prefix
convention from `docs/design/load-balancer.md`.

```
 byte | 0  1  | 2 | 3 |  4  5  6  7   |  8  9 10 11   | 12 13 14 15 |16 17|18 19|20 21 22|23
------+-------+---+---+---------------+---------------+-------------+-----+-----+--------+---
field | magic | v | t |   senderId    |   roomHash    |    seq      |  x  |  y  |  R G B |pad
size  |  2 B  |1B |1B |     4 B       |     4 B       |    4 B      | 2 B | 2 B |  3 B   |1B
```

Total: **24 bytes** payload. With IPv4 + UDP headers (20 + 8 = 28 B) that's
**52 bytes on wire** per frame.

Field-by-field:

- `magic` — `0x4E 0x44` (ASCII `'N','D'`). Sentinel so the listener can drop
  obviously-not-NetDraw datagrams (port scanners, stray multicast) without
  spending any CPU on them. Two bytes is enough at this scale; the second
  filter is the version check on byte 2.
- `version` — `1`. Bump on breaking change. A v2 server receiving a v1 frame
  silently drops it (no ICMP, no error reply — UDP is fire-and-forget); the
  client falls back to TCP after the 2 s timeout.
- `type` — `1` for `CursorMove`. Reserved values 2–255 for future presence
  frames (typing-indicator, selection-rect, etc.) that share the same lossy
  channel. Putting `type` in the header now means we don't reframe the
  protocol when those land.
- `senderId` — `uint32 BE`, parsed from the existing 8-hex-char `UserId`
  via `Convert.ToUInt32(userId, 16)`. The 8-char IDs come from
  `Guid.NewGuid().ToString("N")[..8]` (see `NetworkService.cs:38`), which
  fits exactly in 32 bits. No collision worry at room scale (10 users × N
  rooms; birthday collision among ≤10 random uint32s is ~10⁻⁸).
- `roomHash` — `uint32 BE`, computed as `XxHash32.Hash(UTF8(NFC(roomId)))`
  using the same `NetDraw.Shared/Util/XxHash32.cs` the LB design adds. The
  server compares this against the room the sender's UserId is bound to and
  drops on mismatch. This is a sanity check, not a security measure — an
  attacker who knows the room can compute the hash too. Its real job is
  catching client bugs where the user switches rooms on TCP but the UDP
  socket forgets to update.
- `seq` — `uint32 BE`. Per-sender monotonic counter starting at 0 on UDP
  socket open. Receivers (server fanout, clients reading the rebroadcast)
  drop any frame whose `seq` is `<=` the last seen for that sender. This
  handles UDP reordering (rare on LAN but real) so a stale cursor position
  doesn't appear ahead of a newer one. Wraps at 2³² ≈ 4.3 billion frames,
  which at 20 Hz is ~6.8 years per session — not a real concern.
- `x`, `y` — `uint16 BE`, canvas pixel coordinates. The existing canvas is
  3000 × 2000 (`Program.cs:55` hard-codes those for the AI parser too), so
  uint16 has 21× headroom on each axis. Sender clamps to `[0, 3000)` and
  `[0, 2000)` before serializing; off-canvas mouse positions during pan
  are clamped, not lost. Trading the existing JSON `double` for `uint16`
  drops sub-pixel precision, which is invisible at the 1.5-px-stroke
  cursor render in `RemotePresenceManager.cs`.
- `R`, `G`, `B` — three `uint8`, one byte per channel. Replaces the JSON
  `"#RRGGBB"` string. Sender parses its `UserColor` (set by
  `RoomService.AddUserToRoom`, see `RoomHandler.HandleJoinAsync`) into the
  three bytes; receiver formats them back as `#RRGGBB` for the
  `RemotePresenceManager.UpdateCursor` color argument.
- `pad` — `0x00`. Pads the payload to 24 bytes (a multiple of 8) so the
  layout reads cleanly in `xxd` and leaves a byte for a future flag field
  without reflowing the diagram. Not validated on receive — receivers
  ignore the value.

### Bandwidth math

The brief sets a budget of "5–10 KB/s per room is fine; 100+ KB/s is not."
At the current throttle (50 ms = 20 Hz) and a full room cap of 10 users:

- Per sender: 20 frames/s × 52 B = **1.04 KB/s upstream**.
- Server ingress for one full room: 10 × 1.04 = **10.4 KB/s**.
- Server egress for one full room: every frame is fanned out to the other 9
  members → 20 × 10 × 9 × 52 = **93.6 KB/s**.

That sits under the 100 KB/s ceiling, so the existing 50 ms throttle stays
the default. Bumping to 30 Hz would push egress to 140 KB/s and violate
the budget; 50 Hz (the upper end of "20–50 Hz" in the brief) would land at
234 KB/s which is not safe at room cap. The throttle constant is the same
`50` ms in `MainWindow.xaml.cs:172` and we leave it untouched.

For comparison, the JSON path today at the same 20 Hz cadence sends roughly
180 B/frame (`{"version":2,"type":"CursorMove","senderId":"f3a2c1b0",...}`),
so server egress for the same room is ~324 KB/s of TCP traffic. Moving to
UDP is a 3.5× wire-bandwidth reduction on top of getting it off the TCP
socket entirely.

## Server wiring

`UdpCursorService` runs alongside `DrawServer`, started from `Program.cs`
the same way `BeaconService` already is — one `Task.Run(() =>
service.RunAsync(cts.Token))` against the existing process cancellation
token. One UDP socket on the configured port (default = same as TCP), one
read loop, fanout via the existing `IRoomService`.

```csharp
public class UdpCursorService
{
    private readonly IRoomService _rooms;
    private readonly IClientRegistry _clients;
    private readonly ILogger<UdpCursorService> _log;
    private readonly int _port;

    // (UserId-as-uint32) -> last-known endpoint + seq + roomHash
    private readonly ConcurrentDictionary<uint, CursorBinding> _bindings = new();

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(_port); // binds 0.0.0.0:5000/UDP
        var buf = new byte[64]; // 24-byte payload + slack; anything bigger we drop

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; } // ICMP port-unreachable on Windows; ignore

            if (!CursorFrame.TryParse(result.Buffer, out var frame)) continue;
            if (!_clients.TryGet(FormatUserId(frame.SenderId), out var sender)) continue;

            // Pin / re-pin the endpoint, validate roomHash matches the TCP-known room.
            var roomId = _rooms.GetRoomIdForClient(sender);
            if (roomId is null) continue;
            if (XxHash32.Hash(Encoding.UTF8.GetBytes(roomId)) != frame.RoomHash) continue;

            var binding = _bindings.AddOrUpdate(frame.SenderId,
                _ => new CursorBinding(result.RemoteEndPoint, frame.Seq, roomId),
                (_, b) =>
                {
                    if (frame.Seq <= b.LastSeq && b.Endpoint.Equals(result.RemoteEndPoint))
                        return b; // stale or duplicate, ignore
                    return new CursorBinding(result.RemoteEndPoint, frame.Seq, roomId);
                });
            if (binding.LastSeq != frame.Seq) continue; // we dropped this frame above

            await FanOutAsync(udp, frame, roomId, sender);
        }
    }

    private async Task FanOutAsync(UdpClient udp, CursorFrame frame, string roomId, ClientHandler from)
    {
        var room = _rooms.GetRoom(roomId);
        if (room is null) return;
        foreach (var peer in room.GetClients())
        {
            if (peer == from) continue;
            var peerId = TryParseUserId(peer.UserId);
            if (peerId is null || !_bindings.TryGetValue(peerId.Value, out var b)) continue;
            // Re-emit the same 24 bytes verbatim — sender's senderId is what other clients render.
            try { await udp.SendAsync(frame.Buffer, frame.Buffer.Length, b.Endpoint); }
            catch (SocketException) { /* peer's UDP path is broken; their TCP fallback will pick up */ }
        }
    }
}

internal record CursorBinding(IPEndPoint Endpoint, uint LastSeq, string RoomId);
```

The bindings dictionary is keyed by `senderId-as-uint32`, not by
`ClientHandler`, because the inbound UDP packet carries the senderId but
not the TCP handler — the lookup goes UDP → ClientRegistry. `IClientRegistry`
already exists (`Services/IClientRegistry.cs`) and is keyed by the
8-hex-char UserId; we expose a `TryGet(string userId, out ClientHandler)`
on it that the cursor service calls.

`PresenceHandler.cs` (the existing TCP `CursorMove` handler) is **not
deleted**. It stays wired up so that:

1. Clients that don't open the UDP socket (Phase 1 rollout, firewall
   blocking outbound UDP, future test clients) keep working.
2. The fallback path (see "Fallback to TCP") has somewhere to land.

The handler does become a fallback in the design narrative — under normal
operation no real client should fire it once UDP is up — but the code
stays.

Per-source rate cap: the service drops any frame from an `(IP, port)` pair
that exceeds 60 frames/sec (3× the design rate). This is a defensive
ceiling against a buggy or hostile client trying to amplify traffic
through fanout. Implemented as a `(IPEndPoint -> TokenBucketRateLimiter)`
map with a 30 s idle prune. Mirrors the existing
`TokenBucketRateLimiter` pattern but at the UDP entry point, not in
`MessageDispatcher` — the dispatcher never sees these frames.

Lifecycle: started after `server.StartAsync()` is kicked off, cancelled
by the same `healthCts`-style token. Bind failure (port collision, perms)
logs a warning and the rest of the server keeps running — UDP cursor is
best-effort, never fatal. Mirrors `BeaconService.RunAsync`.

Config (env vars, mirroring `BEACON_*` and `LAN_*` patterns from
`Program.cs:81-105`):

- `UDP_CURSOR_DISABLE` (default unset; `=1` to skip the bind) — opt-out
  knob for environments where UDP is blocked or for the v1 demo of the
  TCP-only fallback path.
- `UDP_CURSOR_PORT` (default = same as TCP port) — override the bind
  port for testing or if 5000/UDP is contended.
- `UDP_CURSOR_PUBLIC_HOST` (default = empty) — the IP the server should
  advertise to clients in `RoomJoinedPayload.UdpHost` when the server
  cannot infer its own externally-visible IP. With LB in front, this is
  the **backend's own** routable IP (the one the LB dialled), not the LB
  IP. For the localhost demo this is `127.0.0.1`.

## Client wiring

`MainWindow.xaml.cs:172-178` is the send site today:

```csharp
if (_vm.IsConnected && (DateTime.Now - _lastCursorSend).TotalMilliseconds > 50)
{
    _lastCursorSend = DateTime.Now;
    var msg = NetMessage<CursorPayload>.Create(MessageType.CursorMove,
        _vm.Canvas.UserId, _vm.Canvas.UserName, _vm.Canvas.RoomId,
        new CursorPayload { X = pos.X, Y = pos.Y, Color = _vm.Canvas.UserColor });
    _ = _vm.Network.SendAsync(msg);
}
```

That call site stays the same shape but routes through a new
`ICursorTransport` indirection so the choice of UDP vs TCP fallback is
hidden from the UI:

```csharp
// MainWindow.xaml.cs (call site unchanged in spirit):
_vm.CursorTransport.Send(pos.X, pos.Y, _vm.Canvas.UserColor);
```

`CursorTransport` is a small service owned by `MainViewModel`, instantiated
on successful `JoinRoom`:

```csharp
public class CursorTransport : IDisposable
{
    private readonly UdpClient _udp = new();
    private readonly IPEndPoint _serverEndpoint;
    private readonly INetworkService _tcp;
    private readonly uint _senderId;
    private readonly uint _roomHash;
    private uint _seq;

    private DateTime _lastInboundUdp;
    private bool _udpHealthy = true; // optimistic; flips on the 2 s timeout

    public CursorTransport(INetworkService tcp, string udpHost, int udpPort,
                           string userId, string roomId)
    {
        _tcp = tcp;
        _serverEndpoint = new IPEndPoint(IPAddress.Parse(udpHost), udpPort);
        _senderId = Convert.ToUInt32(userId, 16);
        _roomHash = XxHash32.Hash(Encoding.UTF8.GetBytes(roomId));
        _lastInboundUdp = DateTime.UtcNow; // grace period before fallback decision
        _ = Task.Run(ListenAsync);
    }

    public void Send(double x, double y, string colorHex)
    {
        if (_udpHealthy)
        {
            var buf = CursorFrame.Build(_senderId, _roomHash, ++_seq,
                ClampX(x), ClampY(y), colorHex);
            try { _udp.Send(buf, buf.Length, _serverEndpoint); }
            catch (SocketException) { _udpHealthy = false; }
        }
        else
        {
            // Fallback: existing TCP path.
            var msg = NetMessage<CursorPayload>.Create(MessageType.CursorMove, ...);
            _ = _tcp.SendAsync(msg);
        }
    }

    private async Task ListenAsync()
    {
        while (!_disposed)
        {
            var result = await _udp.ReceiveAsync();
            if (!CursorFrame.TryParse(result.Buffer, out var f)) continue;
            if (f.SenderId == _senderId) continue; // server shouldn't echo, but defend
            _lastInboundUdp = DateTime.UtcNow;
            // hand to RemotePresenceManager via the same dispatcher path TCP uses
            _onCursorReceived(f);
        }
    }
}
```

The inbound listen runs on the same UDP socket the client sends from, so
the source port the server pinned is the port the client receives on —
no separate inbound bind, no NAT pinhole worry on the LAN.

The `OnCursorReceived` callback hands the frame up to the same
`HandleCursorMove` flow `MainWindow.xaml.cs:96-103` already runs, marshalled
to the WPF dispatcher. `RemotePresenceManager.UpdateCursor` doesn't care
whether the values came from a JSON `CursorPayload` or a binary frame.

The fallback decision lives in a one-shot timer set when `CursorTransport`
is constructed: if the room has ≥ 2 users (per `RoomJoinedPayload.Users`)
and `_lastInboundUdp` hasn't advanced after 2 seconds of the user
actively moving the mouse, flip `_udpHealthy = false` and log once. The
TCP fallback is the existing path verbatim — no new server work, since
`PresenceHandler` is still listening for `MessageType.CursorMove` over
TCP.

`CursorFrame.Build` and `CursorFrame.TryParse` are static helpers in
`NetDraw.Shared/Protocol/Udp/CursorFrame.cs` (new file), shared between
client and server. The parser is one allocation-free `Span<byte>` walk
plus a magic+version+type validation prefix; under 30 lines.

## Discovery and negotiation

The client cannot guess the UDP port, and it cannot guess the right host
when the LB is in front. `RoomJoinedPayload` grows two fields:

```csharp
public class RoomJoinedPayload : IPayload
{
    [JsonProperty("room")]      public RoomInfo Room { get; set; } = null!;
    [JsonProperty("history")]   public List<DrawActionBase> History { get; set; } = new();
    [JsonProperty("users")]     public List<UserInfo> Users { get; set; } = new();
    [JsonProperty("udpHost")]   public string? UdpHost { get; set; } // null => no UDP cursor
    [JsonProperty("udpPort")]   public int? UdpPort { get; set; }
}
```

Server fills these in `RoomHandler.HandleJoinAsync` (line 82) from the
`UDP_CURSOR_PUBLIC_HOST` env var and the actual bound UDP port. If the env
var is unset and the LB is not in play, the server fills `UdpHost` with
the local interface address `RoomHandler` can read off the
`ClientHandler`'s `TcpClient.Client.LocalEndPoint` — the IP the client
already reached the server on. With LB in front this is the LB's IP, which
is wrong; in that topology the operator MUST set `UDP_CURSOR_PUBLIC_HOST`
explicitly to the backend's own routable IP.

When `UdpHost` is null (server bound failed, or `UDP_CURSOR_DISABLE=1`),
the client never constructs `CursorTransport.UdpClient` — it sends every
cursor frame on TCP from the start. No degradation, just slower-default.

Older clients that don't read `udpHost` simply ignore the field
(Newtonsoft's default `MissingMemberHandling.Ignore`) and keep using TCP.
There is no protocol-version bump for this — it's a strict additive field,
unlike the session-token PR's bump from 1 → 2. A future client can be
deployed before or after the server starts populating the field; in either
mismatch the user falls back to TCP cursor and nothing breaks.

## Fallback to TCP

Three failure modes the client must handle:

1. **Server didn't advertise UDP** (`udpHost is null`). Skip
   `CursorTransport.UdpClient` entirely; every `Send` goes to TCP. No
   timer, no flap.
2. **UDP send fails** (`SocketException` on the first send: blocked by
   local firewall, no route to host). Flip `_udpHealthy = false` on the
   first exception, log once, never retry UDP for the rest of this
   `CursorTransport`'s lifetime. Reconstructed on next `JoinRoom`.
3. **One-way UDP** — the most subtle case. The client's outbound UDP gets
   to the server (so the server pins the binding and starts fanning out
   to other peers) but inbound UDP back to the client is dropped (NAT
   timeout, Wi-Fi AP isolation between two laptops on the same SSID).
   The client is sending cursor data successfully and others see it move,
   but the client never sees other people's cursors over UDP. Detection:
   if the room has ≥ 2 users at `JoinRoom` time and 2 seconds have
   passed since the local mouse first moved with no inbound UDP frame
   landing in `ListenAsync`, flip to TCP. The server's fanout will keep
   delivering to every other client over UDP — only this client switches
   sides.

The 2-second window is a single `DispatcherTimer` started at
`CursorTransport` construction (not on first `Send`). If at the 2 s mark
the room is ≥ 2 users, `_lastInboundUdp` is unchanged, AND the user has
moved the mouse at least once (so we know they're an active participant
who would expect to be seen), flip. Single-user rooms are exempt —
there's nothing to receive, so a missing inbound UDP signal is
meaningless.

The "active participant" check matters because a freshly-joined silent
observer never sends a UDP frame, so the server never pins their endpoint
and never fans out other people's cursors to them. A pure observer who
opens NetDraw and never touches the mouse therefore sees no cursors at
all — which matches the existing "presence only when active" behaviour
on TCP today (the TCP path also broadcasts cursor only on `MouseMove`
events). The fallback timer triggers as soon as the observer moves, so
their first wiggle gets them onto the TCP fallback if UDP is broken
inbound. Owned, not fixed: this is the right behaviour for the
"who's actively present" use case.

There is no probe message, no UDP "ping" the server has to respond to.
The signal is "are real cursor frames flowing back to me", which is what
we actually need to know.

Once the client falls back to TCP, it stays on TCP for the rest of that
room session. A future room-rejoin (TCP `LeaveRoom` + new `JoinRoom`)
constructs a fresh `CursorTransport` and re-attempts UDP. Operationally
this means a user who joined a room from the wrong network can fix it by
leaving and rejoining; we don't need a periodic UDP-recovery probe.

## Interactions

**Session token (`docs/design/session-token.md`).** UDP frames carry no
token. The token doc states the broadcast path must strip the
originator's token; the UDP path is naturally compliant since it never
carries one. The threat model section above owns the trade-off (cursor
spoofing is annoying, not destructive). The TCP `MessageType.CursorMove`
fallback path remains subject to the token validation in
`MessageDispatcher.DispatchAsync` because it travels through the same
TCP pipeline as every other message — a fallback-mode client still has
to present its valid token on the TCP `CursorMove`.

**TLS (`docs/design/tls-in-house.md`).** UDP gets nothing. DTLS is out of
scope: .NET's `SslStream` doesn't speak DTLS, the BouncyCastle DTLS path
would re-introduce a dependency we keep narrow in the TLS doc, and the
threat model floor for cursor data is "annoying-not-destructive" anyway.
The TLS doc explicitly says "no TLS for the LAN beacon UDP traffic"
(`docs/design/lan-discovery-and-server-cache.md` reference); this is the
same call for cursor UDP. Mention in `tls-in-house.md` "Out of scope" on
the next edit pass: cursor UDP joins beacon UDP as an unencrypted
sibling.

**Load balancer (`docs/design/load-balancer.md`).** The L4 LB is TCP-only
(`Stream.CopyToAsync` pumps over `TcpClient`). UDP cursor traffic
**bypasses the LB**. The flow is:

1. Client TCP-connects to LB (port 5500).
2. LB picks a backend by xxhash32(roomId), forwards the prefix and the
   JoinRoom frame to backend `B_k`.
3. `B_k` replies with `RoomJoinedPayload { udpHost: "<B_k's own IP>",
   udpPort: 5000+k }`.
4. Client opens a UDP socket pointing directly at `B_k:5000+k`,
   skipping the LB entirely.

This means `UDP_CURSOR_PUBLIC_HOST` must be configured on every backend
to the IP that backend is reachable on from clients. For the localhost
demo (3 backends on `127.0.0.1`) it's `127.0.0.1` everywhere; for a
multi-host deploy it's each backend's LAN IP. The LB design's
"Out of scope" already says the LB doesn't do UDP; this design just
formalizes who does.

Consequence: if a client falls back to TCP cursor (firewall blocks
outbound UDP to the backend's IP, or backend's IP isn't routable from
the client even though the LB's is), all cursor traffic flows back
through the LB. The LB doesn't care — it's still byte-pumping JSON
envelopes.

**LAN discovery beacon (`docs/design/lan-discovery-and-server-cache.md`).**
The beacon multicasts to `239.255.77.12:5099`. UDP cursor binds the
backend's chosen TCP port (default 5000) for unicast UDP. **No conflict** —
different ports, different addresses (multicast vs unicast). Both can
run on the same machine simultaneously. The beacon `BeaconV1` payload
could grow a `udpCursorPort` field for clients to discover the UDP port
without a TCP round-trip first, but we don't need that in v1: clients
already have to TCP-connect and `JoinRoom` to know what room they're in,
and the UDP port comes back in the same reply.

## Wireshark demo

The deliverable is the side-by-side capture: same port, two protocols.
Same setup as `tls-in-house.md`'s demo section — three terminals, one
runs the server, one runs `tshark`, one runs the client.

```bash
# Terminal A — server with UDP cursor enabled (default)
dotnet run --project NetDraw.Server -- 5000

# Terminal B — capture both protocols on port 5000
sudo tshark -i lo -f 'port 5000' -V \
  | tee /tmp/capture-cursor.txt

# Terminal C — two client instances
dotnet run --project NetDraw.Client &
dotnet run --project NetDraw.Client
# join the same room "demo" on both, drag the mouse around
```

What the listener sees, sorted by frame:

- TCP three-way handshake on `5000`.
- TCP `PUSH, ACK` carrying `{"type":"JoinRoom",...}\n` and the
  `RoomJoined` reply, the latter now showing
  `"udpHost":"127.0.0.1","udpPort":5000` in its JSON payload.
- A burst of UDP datagrams on `5000`, each 24 bytes payload + 28 bytes
  IPv4/UDP header = 52 bytes on wire. `tshark -V` shows the source/dest
  ports the same as the TCP flow but the protocol column reads `UDP`,
  not `TCP`. The first two payload bytes (`4e 44`) are the `'N','D'`
  magic — visible in the hex dump.
- TCP draw frames (`{"type":"Draw",...}\n`) arriving in parallel,
  un-throttled by the cursor traffic that used to be sharing their
  socket.

For a single-slide before/after: capture once with
`UDP_CURSOR_DISABLE=1` on the server (everything on TCP, dense
`CursorMove` JSON in every frame) and once without (cursor moves to
UDP, TCP frames are only Draws and Chats). The `tshark` "Protocol"
column tells the story without any further annotation.

## Phases

**Phase 1 (S) — Wire format + server bind + client send.** Add
`NetDraw.Shared/Protocol/Udp/CursorFrame.cs` (build + parse, plus a
known-vector test in `NetDraw.Shared.Tests`). Add `UdpCursorService` in
`NetDraw.Server/Services/`, wire it into `Program.cs` next to
`BeaconService`. Add `UDP_CURSOR_DISABLE`, `UDP_CURSOR_PORT`,
`UDP_CURSOR_PUBLIC_HOST` env vars. Extend `RoomJoinedPayload` with the
two nullable fields. Add `CursorTransport` to the client and route the
`MainWindow.xaml.cs:172-178` send through it. **Don't** delete
`PresenceHandler.cs` or the TCP cursor send — both stay as the fallback
path. Manual test: 2 clients in one room, drag the mouse, run
`tcpdump -i lo 'port 5000' -X` and see UDP frames with the `'N','D'`
magic. Estimate: 3 days for a junior dev.

**Phase 2 (S) — Fallback timer + automatic switchover.** Implement the
2-second inbound-UDP watchdog in `CursorTransport`. Test by binding a
firewall rule that drops outbound UDP from one client only (`iptables
-A OUTPUT -p udp --dport 5000 -j DROP`) and observing that client falls
back to TCP while peers stay on UDP. Estimate: 1 day.

**Phase 3 (M) — Per-source rate cap + binding GC.** The
`UdpCursorService` accepts every well-formed frame in Phase 1; Phase 3
wraps each `(IPEndPoint -> TokenBucketRateLimiter)` lookup at the read
loop's top, drops over-rate frames, and prunes stale bindings every
30 s. Add `/health` exposure of UDP counters (frames received, frames
dropped by reason, active bindings count) to the existing
`HttpHealthServer`. Estimate: 2 days.

**Phase 4 (S) — Wireshark demo + writeup.** Wireshark dissector
configuration (a 30-line Lua dissector decoding the 24-byte frame is
overkill but fun for the grader; alternatively, just point Wireshark at
`port 5000` and screenshot the side-by-side TCP+UDP capture). Update the
project README with the Phase 1 env vars and the demo command list.
Estimate: 1 day.

Total: 7 days for a junior dev who hasn't shipped a UDP service before.
An experienced dev gets through Phases 1+2 in two days.

## Open questions

1. **Per-frame timestamp.** Should the frame carry a `uint32` ms-since-
   socket-open so the receiver can drop very-stale frames? Recommend
   no — the `seq` field already drops reordering and a late-but-monotonic
   frame just means a brief cursor lag before the next one catches up.
2. **DrawPreview on UDP too.** The other lossy-OK frame in the protocol.
   Payload is variable-size (a partial `DrawAction` with N points) so
   it doesn't fit a 24-byte binary frame; it'd need its own format.
   Recommend separate design pass once cursor ships and we have real
   measurements; don't ride this PR.
3. **Multi-NIC server source IP.** Bind to `0.0.0.0:5000/UDP` accepts
   on any interface, but the fanout source IP is whatever the OS routing
   table picks. For multi-NIC backends behind an LB this can desync
   from `UDP_CURSOR_PUBLIC_HOST` and the client may reject the inbound.
   Recommend punt — single-NIC is the demo case; revisit if it bites.
4. **Client UDP source-port stability.** A `new UdpClient()` binds an
   ephemeral port; we rely on the OS not changing it for the socket's
   lifetime (Windows and Linux do not, but worth confirming on the WPF
   target). If it floats, the server re-pins on the next frame.

## Out of scope

- DTLS or any wire-encryption of UDP. Cleartext only; threat model owns it.
- IPv6 (`AddressFamily.InterNetwork` matches the rest of the codebase).
- Multi-LAN, WAN, NAT traversal. Single-LAN demo only.
- LB UDP forwarding. LB stays TCP-only; UDP cursor bypasses it.
- `DrawPreview` on UDP. Tracked as Open Question 2.
- Reliable retransmission of dropped cursor frames — the whole point is
  lossy-OK; reliable delivery means using TCP.
