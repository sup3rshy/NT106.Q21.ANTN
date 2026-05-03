# LAN auto-discovery and persistent server cache

## Elevator

Right now you have to know a server's IP and port and type them into the connect panel. After this change the panel shows two lists above the IP/port boxes: servers actively beaconing on the local LAN (live, with current room/client counts) and servers you've connected to before (cached, with a "last seen N days ago" indicator). Clicking either fills the host/port for you. The manual fields stay — nothing is removed, the picker is purely additive.

## Beacon packet schema

Wire format is JSON (Newtonsoft, matches the rest of the codebase) on UDP. One datagram per beacon, no fragmentation, no framing. JSON instead of binary because the budget is comfortable, the existing `RoomInfo` shape is JSON, and the server already pays the Newtonsoft cost.

```json
{
  "v": 1,
  "id": "f3a2c1b0",
  "name": "hoang-laptop",
  "port": 5000,
  "appVersion": "0.3.0",
  "rooms": 2,
  "clients": 5,
  "maxClients": 50,
  "ts": 1746230400
}
```

Field notes:

- `v` (byte): schema version. Bump only on breaking change. Listeners ignore unknown top-level keys, so additive fields don't bump it.
- `id` (string, 8 hex): per-process random ID, regenerated on every server start. Used as the dedup key when the same host has multiple network interfaces broadcasting the same beacon (we'd otherwise see it twice). Not stable across restarts — that's intentional, see open questions.
- `name` (string, ≤ 64 chars after trim): human label. Defaults to `Environment.MachineName`. Override via `BEACON_NAME` env var.
- `port` (uint16): TCP port the DrawServer is listening on. Independent of the source UDP port the beacon comes from.
- `appVersion` (string, ≤ 16 chars): from the assembly version. Surfaced in the picker so you can tell stale servers apart.
- `rooms`, `clients` (uint16): live counts at beacon-emission time. Pulled from `IRoomService.GetAllRoomInfos()` the same way `HttpHealthServer` already does it. Field names match the `/health` endpoint (`rooms`, `clients`) rather than `RoomInfo.userCount`/`RoomInfo.maxUsers` — the beacon plays the same observability role as the health endpoint, so they should look the same.
- `maxClients` (uint16): `MaxRooms * MaxUsersPerRoom`. Lets the client show "5 / 50" capacity.
- `ts` (int64): Unix seconds, server clock. Used by the listener to drop very-old packets that arrived late, and for jitter detection. Not authoritative — clients tolerate clock skew.

Multicast group + port: **`239.255.77.12:5099`**. Reasoning: `239.255.0.0/16` is the IPv4 admin-scoped block (RFC 2365), reserved for site-local multicast and never routed to the wider internet — exactly what we want. `239.255.250-255.x` is the SSDP/UPnP neighbourhood, so we sit a couple of /24s away. `5099` is unassigned by IANA and adjacent to our existing TCP `5000`/MCP `5001`/health `5050`, which keeps the port story memorable. Both are overridable via `BEACON_GROUP` and `BEACON_PORT` env vars for environments where 5099 is firewalled.

Datagram size budget: ~512 bytes total payload, well under the 1500-byte MTU and even under the 576-byte conservative IPv4 minimum. Measured: the example above serializes to 132 bytes with Newtonsoft's default (no whitespace); allow the name/version to grow, plus headroom for several additive fields per future revision. If we ever blow past ~1KB, switch the wire format before fragmenting.

## Discovery service (server side)

`DiscoveryBroadcaster` runs alongside `DrawServer`, started from `Program.cs` the same way `HttpHealthServer` is in the feature branch — `Task.Run(() => broadcaster.RunAsync(cts.Token))` with a shared cancellation token. One thread, one socket, one timer. No locking — it just reads counts off `IRoomService` (already concurrent) and serializes.

```csharp
public class DiscoveryBroadcaster
{
    public DiscoveryBroadcaster(BeaconConfig cfg, int tcpPort, IRoomService rooms,
                                ILogger<DiscoveryBroadcaster> log) { ... }

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        var endpoint = new IPEndPoint(IPAddress.Parse(_cfg.Group), _cfg.Port);
        var serverId = Guid.NewGuid().ToString("N")[..8];

        while (!ct.IsCancellationRequested)
        {
            var beacon = BuildBeacon(serverId);
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(beacon));
            try { await udp.SendAsync(bytes, bytes.Length, endpoint); }
            catch (Exception ex) { _log.LogWarning(ex, "Beacon send failed"); }
            await Task.Delay(_cfg.IntervalMs, ct);
        }
    }
}
```

Threading model: a single async loop on the thread pool. No per-NIC fanout in phase 1 — `MulticastTimeToLive=1` plus the OS routing table picks a default interface. Multi-NIC behaviour is in open questions.

Lifecycle: started after `server.StartAsync()` is kicked off, cancelled by the same token that ends the process. If the socket throws on bind (port in use), log a warning and exit the loop — the rest of the server keeps running. Discovery is best-effort, never fatal.

Config surface (env vars, mirroring the `LOG_LEVEL` pattern):

- `BEACON_ENABLED` (default `true`)
- `BEACON_GROUP` (default `239.255.77.12`)
- `BEACON_PORT` (default `5099`)
- `BEACON_INTERVAL_MS` (default `2000`)
- `BEACON_NAME` (default `Environment.MachineName`)

## Listener service (client side)

`LanDiscoveryService` is a singleton owned by `App.OnStartup`, injected into `MainViewModel` alongside `INetworkService`. It owns one UdpClient joined to the multicast group and a background read loop.

The discovered set is exposed as a single `ObservableCollection<DiscoveredServer> LiveServers` plus a `DateTimeOffset LastSeen` per row. Updates come off the UDP read loop — but `ObservableCollection` mutations have to happen on the WPF UI thread, otherwise the binding throws on the next CollectionChanged event. The service does not depend on WPF; instead it raises a plain `event Action<DiscoveredServer> BeaconReceived` and the ViewModel's constructor wires that to `Application.Current.Dispatcher.BeginInvoke(...)` to merge into the collection. This keeps `LanDiscoveryService` testable from a console host (phase 1) and keeps the dispatcher footgun isolated to one place.

```csharp
public class LanDiscoveryService : IDisposable
{
    public event Action<DiscoveredServer>? BeaconReceived;
    public event Action<DiscoveredServer>? ServerExpired; // no beacon for 3*interval

    public void Start(BeaconConfig cfg) { ... }   // joins group, spawns read loop
    public void Stop() { ... }
}

public record DiscoveredServer(
    string Id, string Name, IPAddress Host, int Port,
    string AppVersion, int Rooms, int Clients, int MaxClients,
    DateTimeOffset LastSeen);
```

Read loop:

```
while (!ct.IsCancellationRequested)
{
    var result = await udp.ReceiveAsync(ct);
    if (TryParseBeacon(result.Buffer, out var b))
    {
        var server = new DiscoveredServer(b.Id, b.Name, result.RemoteEndPoint.Address,
                                          b.Port, b.AppVersion, b.Rooms, b.Clients,
                                          b.MaxClients, DateTimeOffset.UtcNow);
        BeaconReceived?.Invoke(server);
    }
}
```

Source-address handling: the beacon's `port` is the TCP port; the host comes from the UDP packet's source address (`result.RemoteEndPoint.Address`), not from the JSON payload. Servers don't know their own externally-visible IP and shouldn't try to advertise it — the client picks up whichever interface the packet actually arrived on, which is the one it can reach.

Expiry: a single `Timer` running every 1s walks the live set and fires `ServerExpired` for any entry whose `LastSeen` is older than `3 * IntervalMs` (default 6s). The ViewModel removes the row.

Cap the live set at 64 entries to prevent a hostile or buggy beacon flood from growing the list unbounded; drop the oldest unseen entry when full.

## Cache file

Location:

- Windows: `%APPDATA%/NetDraw/known_servers.json` → expands to `C:\Users\<user>\AppData\Roaming\NetDraw\known_servers.json`
- Linux: `$XDG_CONFIG_HOME/NetDraw/known_servers.json` (fallback `~/.config/NetDraw/known_servers.json`)
- macOS: `~/Library/Application Support/NetDraw/known_servers.json`

All three resolved via `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` + `"NetDraw"` + filename, which gives the right answer on every platform without per-OS branching.

Schema:

```json
{
  "version": 1,
  "servers": [
    {
      "host": "192.168.1.42",
      "port": 5000,
      "displayName": "Hoang's laptop",
      "advertisedName": "hoang-laptop",
      "lastConnectedUtc": "2026-05-03T08:14:22Z",
      "lastRooms": 2,
      "lastClients": 5,
      "appVersion": "0.3.0",
      "notes": null
    }
  ]
}
```

Loader behaviour: `JsonConvert.DeserializeObject<KnownServersFile>` with `MissingMemberHandling.Ignore` (the Newtonsoft default), so unknown additive fields from a future client version are silently dropped. If the file is missing or unparseable, treat as an empty list and overwrite on next save — never crash the connect picker because the cache is corrupt. Log the parse error and rename the bad file to `known_servers.corrupt-{timestamp}.json` so we don't trash the user's history if the bug is in our code.

Write triggers:

- **Successful connect:** create-or-update entry, set `lastConnectedUtc` to now, refresh `advertisedName`, `appVersion`, `lastRooms`, `lastClients` from whatever the server told us during the handshake (currently it tells us nothing at the protocol level — see open questions; phase 2 falls back to whatever the matching beacon last said).
- **User edits the display name or notes** in the picker: write immediately.
- **Prune sweep:** runs once at app startup, removes entries with `lastConnectedUtc` older than `CacheRetentionDays` (default 30, configurable in the same file under a `settings` block, additive in phase 3).

Not a write trigger: receiving a beacon. Beacons mutate the in-memory live list only. Otherwise a beaconing server you've never connected to would grow the cache unbounded.

Locking story for two clients on the same machine: read-write the file with `FileShare.Read` and an atomic-rename pattern (`write to known_servers.json.tmp`, `File.Replace` onto the real file). No advisory lock — last writer wins. Two clients editing notes simultaneously is a real-life never-gonna-happen scenario, and `FileShare.Read` plus atomic replace is enough that you never see a half-written file. The picker reloads from disk on open, so the second client picks up the first client's changes within one panel-open cycle.

## Merge + prune rules

Dedup key: `(host, port)` lowercase-host. The beacon's `id` is informational only — if the user has the same server cached as `192.168.1.42:5000` and a beacon arrives from the same `(host, port)` with a different `id` (server restarted), they're the same row.

Picker layout:

```
LIVE ON LAN (3)
  ● Hoang's laptop          192.168.1.42:5000    2 rooms · 5/50    ← cached
  ● desktop-vm              192.168.1.50:5000    0 rooms · 0/50
  ● raspberry-pi            192.168.1.99:5000    1 room  · 1/10

KNOWN SERVERS (4)
  Office server             10.0.0.5:5000        last seen 3h ago
  Friend's PC               203.0.113.7:5000     last seen 2 days ago
  ...

[ Manual: IP ____  Port ____  Connect ]
```

When a cached entry is also currently broadcasting, it shows up **once** — in the LIVE section, with a small "cached" badge so the user knows their custom display name applies. The KNOWN section only shows entries with no live match. This is the dedup-by-`(host,port)`. The alternative (showing the same server in both lists) was rejected because it confuses the picker — users would wonder which row to click.

When a live beacon's `advertisedName` differs from the cached `advertisedName`, the in-memory display row uses cached `displayName` (user override) but the underlying `advertisedName` field gets refreshed on next successful connect. We don't auto-rename the user's label.

Prune: at app startup, drop any cache entry whose `lastConnectedUtc` is older than `CacheRetentionDays` (default 30). Also prune at picker-open, in case the app stays running for weeks. Configurable via `settings.cacheRetentionDays` in the same JSON file (phase 3). The pruned-out servers might still appear in the LIVE list if they're broadcasting — they just don't carry historical metadata anymore.

Stale-beacon visual: rows stay in LIVE for `3 * IntervalMs` after the last beacon, then get evicted. No "beacon recency" indicator beyond that — the row's mere presence means "we heard from it in the last six seconds".

## Phases

**Phase 1 — Beacon + console listener (S).** New `NetDraw.Server/Services/DiscoveryBroadcaster.cs`, wired into `Program.cs` behind `BEACON_ENABLED`. New tiny console tool `NetDraw.DiscoveryProbe` (or a `--probe` flag on the existing client binary) that joins the multicast group and prints every received beacon. No WPF, no cache, no merge logic. Goal: prove on two laptops on the same Wi-Fi that the protocol works and that one OS firewall isn't silently dropping multicast. Demoable in a single `dotnet run` per machine.

Conventional commits planned:
- `feat(server): add UDP multicast discovery beacon`
- `feat(client): add discovery probe console tool`
- `docs(server): document BEACON_* env vars`

**Phase 2 — Live LAN list in the picker (M).** `LanDiscoveryService` in the WPF client, `DiscoveredServer` ObservableCollection, picker UI section above the existing IP/port fields. Click-to-fill the host/port boxes. Still no cache — this is just the live half of the merged list. Goal: a user opening the side panel on the right LAN sees servers without typing anything.

Commits:
- `feat(client): add LanDiscoveryService for multicast beacon listener`
- `feat(client): show live LAN servers above connect form`
- `feat(client): wire LAN-row click to populate host/port fields`

**Phase 3 — Persistent cache + merge + prune (L).** `KnownServersStore` reading and writing the JSON file, write-on-successful-connect hook in `MainViewModel.ToggleConnectAsync`, cached section in the picker, dedup with the live list, edit-display-name flow, startup + on-open prune, configurable retention. Goal: the connect form is the last thing you ever look at — pick from a list instead.

Commits:
- `feat(client): add KnownServersStore with JSON cache file`
- `feat(client): record successful connects to known-servers cache`
- `feat(client): merge cached and live servers in connect picker`
- `feat(client): prune stale known servers at startup and on picker open`
- `feat(client): allow editing display name and notes per known server`

## Open questions

1. **Server identity on the wire.** The TCP protocol currently doesn't tell the connecting client any of `name`, `appVersion`, `rooms`, `clients`. Phase 3's "refresh metadata on successful connect" only works if we also extend the join-flow — easiest is adding a `serverInfo` block to `RoomJoinedPayload`. Worth confirming that's acceptable before phase 3 starts; otherwise the cache only ever refreshes from beacons, which is fine on LAN but stale for servers reached over typed-in WAN IPs.

2. **Should beacon `id` survive restarts?** A persistent server-id-on-disk would let the cache key on `id` instead of `(host, port)`, surviving DHCP lease changes ("my home server's IP rotated, but it's the same server"). The downside is one more file to manage and a new identity-stability bug class. Punted to phase 3 if at all.

3. **Multi-NIC servers.** A server with both a Wi-Fi and a wired NIC will, with default routing, beacon out only one of them — clients on the other subnet won't see it. Do we enumerate `NetworkInterface.GetAllNetworkInterfaces()` and `JoinMulticastGroup(group, nic)` per interface, or is single-NIC behaviour fine for the dorm-room/classroom use case the project actually has?

4. **Windows Firewall on first server run.** The first time `dotnet run --project NetDraw.Server` opens a UDP socket, Windows pops the firewall consent dialog. For multicast broadcasts (no inbound UDP) it may not — needs testing. If it does, we should document the workaround in the README rather than try to script `netsh`.

5. **Beacon trust.** Anyone on the LAN can broadcast a beacon claiming to be `name: "Office server"` on a host they control. The picker would show it. Mitigation options: show the IP prominently, never auto-connect, only fill the field on explicit click. All of these are already in the design, but we should explicitly call out that the LAN list is advisory — the user is responsible for trusting the server they pick.

## Out of scope

- TLS or any cryptographic auth on the beacon. It's plaintext UDP. Spoofing a beacon to mislead a user is possible (open question 5).
- WAN / internet discovery. The 239/8 multicast block doesn't route past the local segment by design. Discovering remote servers needs a rendezvous service (registry, DHT, mDNS-over-Tailscale, etc.) — that's a separate feature.
- NAT traversal of any kind. The server announces a TCP port; if the client can't reach that port directly, the connect fails the same way it does today.
- Server-name uniqueness or central registration. Two servers can advertise the same `name`. The picker disambiguates by IP.
- IPv6 multicast. We bind `AddressFamily.InterNetwork` only. IPv6-only LANs are not a target environment for this project.
- Reverse direction: clients announcing themselves to servers. Servers don't need to know who's looking; they ACK nothing.
- Authentication of cache contents. The cache file is a plain JSON file in the user's appdata; anyone with shell on the box can edit it.
- Migration of cache schema beyond additive fields. If we ever need a breaking change, write a new file (`known_servers.v2.json`) and read both during a transition window.
