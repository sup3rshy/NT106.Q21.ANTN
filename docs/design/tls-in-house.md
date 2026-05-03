# In-house TLS for the DrawServer connection

## Elevator

Today, anyone running `tcpdump -i any -A 'tcp port 5000'` on a host that sees the LAN segment between a NetDraw client and the DrawServer reads every JSON envelope in the clear: `senderId`, chat messages, AI prompts, draw coordinates, the works. After this change the same capture shows TLS 1.3 records — `Application Data` blobs of opaque bytes after a handshake — and the only metadata still legible is the SNI hostname in the `ClientHello`, the leaf cert presented by the server (which the listener can re-fetch by connecting themselves), and record sizes/timing. Cert pinning on the client means a hostile cert (active MITM) is rejected even though the cert itself looks valid to OpenSSL: the client compares a SHA-256 thumbprint of the leaf against a value baked in via env var, and a mismatch terminates the handshake before any application bytes flow.

The protocol floor is **TLS 1.3 only**, no fallback to 1.2. Phase 4 layers an application-level ML-KEM-768 key exchange on top of the TLS stream as defense in depth against capture-now-decrypt-later — see the Phase 4 honesty disclaimer for what that buys and what it does not.

## Threat model

What TLS-with-pinning defends against:

- Passive eavesdropping on the same LAN (the demo case). A sniffer sees TLS records but not message contents.
- Active MITM with a hostile cert (rogue AP, ARP spoofer, malicious LAN device that intercepts and re-presents traffic). The pinned thumbprint won't match the attacker's freshly-minted leaf, the validation callback returns `false`, the handshake aborts.
- The session token (P4.T5, see `docs/design/session-token.md`) being sniffed off the wire and replayed from a separate TCP connection. With TLS the token never appears in plaintext on the network.
- Protocol-downgrade attacks. Both sides advertise only `SslProtocols.Tls13`; a `ClientHello` that omits the TLS 1.3 supported_versions extension, or a `ServerHello` that tries to negotiate 1.2, terminates the handshake. The cost of locking out 1.2 is rejecting any client built against a pre-2018 .NET runtime, which is irrelevant here — every NetDraw component targets .NET 8+, and TLS 1.3 has been universally available in the .NET runtime since .NET 5 (Linux) / .NET 6 (Windows). There is no production reason to keep 1.2 enabled and a real reason to drop it: the entire 1.2-vs-1.3 negotiation surface, including the historical downgrade-prevention scheme baked into the `ServerHello.Random` sentinel, is gone if 1.2 is never on the table.

What it does not defend against:

- A compromised endpoint. If the attacker has code execution on the client or server box, they read the plaintext after `SslStream` decrypts. The token map and the cert private key both live in memory and on disk.
- The cert private key leaking. `dev/server.pfx` is committed nowhere (see `.gitignore`) but it lives in plaintext on the operator's filesystem under PKCS#12 with a known dev password. Anyone who reads the file impersonates the server. Production rotation is out of scope for this PR.
- Side channels. TLS does not hide record sizes or timing. An attacker who knows the JSON envelope shape can guess at message types from record-size patterns (a `Draw` is ~80–200 bytes, a `CanvasSnapshot` is kilobytes, a `Chat` varies by typing speed). Not a concern for the demo threat model.
- Metadata in the handshake. SNI is sent in the clear (`localhost` in dev). The server's cert is encrypted in transit under TLS 1.3, but the cert subject is still recoverable by anyone who can themselves connect to the server. ECH would hide SNI; we are not deploying it.
- The 4-byte LB prefix that precedes the handshake (see "LB-prefix ordering"). It is a hash of the room-id, sent in cleartext, and is observable to any network-layer attacker.
- Pre-existing application-layer flaws. Authn and authz of users is the session token's job, not TLS's. TLS authenticates the *server*; the token authenticates the *user*.

Post-quantum considerations. The TLS 1.3 key exchange used by .NET 8 is classical X25519 (and possibly secp256r1, depending on the platform's OpenSSL/SChannel build). A cryptographically-relevant quantum computer running Shor's algorithm breaks both, which means a passive adversary who records today's TLS records can decrypt them once such a machine exists — the "harvest now, decrypt later" threat. The IETF answer is the hybrid `X25519MLKEM768` group (codepoint `0x11EC`), supported by OpenSSL 3.5+ since 2025, but .NET 8 / 10's `SslStream` does not expose a `SupportedGroups` knob, so the application has no guarantee that a PQ group was negotiated and no way to log whether it was. Phase 4 addresses this at the application layer by running a separate ML-KEM-768 KEM *inside* the TLS stream and AES-256-GCM-wrapping every NetMessage with the resulting key. The wrapping key is derived from a fresh KEM exchange per connection, so a recorded session is safe for as long as ML-KEM-768 itself holds, independent of whether the underlying TLS X25519 ever falls.

## Cert tooling

`tools/gen-cert.sh` generates a self-signed root CA and a server leaf signed by that CA, packages the leaf plus its private key into a PKCS#12 file, and writes a thumbprint file the client can pin. The script is the canonical source — `dev/` is gitignored, and anyone (CI, a teammate, the demo host) can regenerate the artifacts in one command. The CA is preserved across runs; only the leaf is reissued, so existing pinned thumbprints stay valid as long as you don't delete the CA. The script refuses to overwrite an existing `dev/server.pfx` unless `--force` is passed, so a stray run during the demo doesn't quietly invalidate the client's pin.

```bash
#!/usr/bin/env bash
# tools/gen-cert.sh — dev cert generator for the in-house TLS layer.
# Generates a CA (once) and a server leaf (every run unless --force omitted and pfx exists).
# All passwords are the dev placeholder "netdraw-dev"; do not reuse outside the demo.
set -euo pipefail

OUT="${OUT:-dev}"
PASS="netdraw-dev"
DAYS_CA=3650
DAYS_LEAF=825
SUBJ_CA="/CN=NetDraw Dev CA"
SUBJ_LEAF="/CN=netdraw-dev"
SAN="subjectAltName=DNS:localhost,DNS:netdraw-dev,IP:127.0.0.1,IP:::1"

mkdir -p "$OUT"

if [[ ! -f "$OUT/ca.key" ]]; then
  openssl genrsa -out "$OUT/ca.key" 2048
  openssl req -x509 -new -nodes -key "$OUT/ca.key" -sha256 -days "$DAYS_CA" \
    -subj "$SUBJ_CA" -out "$OUT/ca.crt"
fi

if [[ -f "$OUT/server.pfx" && "${1:-}" != "--force" ]]; then
  echo "$OUT/server.pfx exists; pass --force to reissue" >&2
  exit 1
fi

openssl genrsa -out "$OUT/server.key" 2048
openssl req -new -key "$OUT/server.key" -subj "$SUBJ_LEAF" -out "$OUT/server.csr"
openssl x509 -req -in "$OUT/server.csr" -CA "$OUT/ca.crt" -CAkey "$OUT/ca.key" \
  -CAcreateserial -days "$DAYS_LEAF" -sha256 \
  -extfile <(printf "%s\n" "$SAN") -out "$OUT/server.crt"
openssl pkcs12 -export -out "$OUT/server.pfx" -inkey "$OUT/server.key" \
  -in "$OUT/server.crt" -certfile "$OUT/ca.crt" -passout "pass:$PASS"

# SHA-256 thumbprint of the leaf — this is what the client pins. Uppercase hex, no separators,
# matching X509Certificate2.GetCertHashString(SHA256) on the .NET side.
openssl x509 -in "$OUT/server.crt" -noout -fingerprint -sha256 \
  | sed 's/^.*=//; s/://g' | tr 'a-f' 'A-F' > "$OUT/pin.txt"

echo "wrote $OUT/server.pfx (pass: $PASS)"
echo "pin (SHA-256, hex): $(cat "$OUT/pin.txt")"
```

What gets generated and what consumes it:

- `dev/ca.crt`, `dev/ca.key` — the dev root. Stays put across runs. Not consumed by the running services in pinned-mode (the client ignores the chain) but useful if anyone wants to flip to chain-validation later, and it's the issuer the leaf chains up to.
- `dev/server.crt`, `dev/server.key` — leaf, regenerated every run. Not consumed directly; bundled into the pfx.
- `dev/server.pfx` — what the server loads via `TLS_CERT_PATH`. PKCS#12 with cert + key + CA cert in the bag. Password: `netdraw-dev` (passed via `TLS_CERT_PASSWORD`).
- `dev/pin.txt` — SHA-256 thumbprint of the leaf, uppercase hex, no separators. Format chosen to match what `X509Certificate2.GetCertHashString(HashAlgorithmName.SHA256)` returns in .NET, so the client's compare is a single string equality. The demo flow is `export NETDRAW_PIN=$(cat dev/pin.txt)`.

Validity: leaf is **825 days**, CA is 3650. 825 is the historical Apple ceiling and well within every other platform's tolerance — long enough to cover the entire course semester even if regenerated only once, short enough that the doc-writer remembers it isn't forever.

Key type: **RSA 2048**. ECDSA P-256 is technically a better fit for TLS 1.3 but `X509Certificate2` + PKCS#12 + Windows + .NET has had rough edges with EC keys historically; RSA 2048 is the boring-and-works choice and the handshake-cost difference is invisible at LAN/demo scale.

`.gitignore` already has the catch-all rules for build output and tooling state; add a single line: `dev/`. Document at the top of `tools/gen-cert.sh` that the script is the canonical source of these files. CI never runs the script today (no test suite exists per project CLAUDE.md); a future job could call it before any TLS-touching integration test.

## Server wiring

The accept loop in `DrawServer.StartAsync` currently does `tcpClient = await _listener.AcceptTcpClientAsync(...)` and immediately hands the `TcpClient` to a new `ClientHandler`. The TLS change inserts a wrap step between accept and handler-construction: read the 4-byte LB prefix off the raw socket, then `AuthenticateAsServerAsync` on a fresh `SslStream` over `tcpClient.GetStream()`, then construct the handler with the `SslStream` as its read/write surface. If `--insecure` is set, skip the wrap and pass the raw `NetworkStream`. `ClientHandler` does not know which case it got.

The cert is loaded once at server startup from `TLS_CERT_PATH` + `TLS_CERT_PASSWORD` and held in a field on `DrawServer`. `SslServerAuthenticationOptions` is rebuilt per-connection (cheap; just the options object, not the cert) so per-connection state stays per-connection.

```csharp
// In DrawServer field-init (loaded once at startup; null when --insecure):
private readonly X509Certificate2? _serverCert;

// Inside the accept loop, replacing the direct `new ClientHandler(tcpClient, ...)`:
tcpClient = await _listener.AcceptTcpClientAsync(_cts.Token);

Stream stream;
if (_serverCert is null)
{
    stream = tcpClient.GetStream();
}
else
{
    var raw = tcpClient.GetStream();
    await ReadLbPrefixAsync(raw, _cts.Token); // 4 bytes, discarded after read; see LB-prefix section
    var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
    try
    {
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _serverCert,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, _cts.Token);
    }
    catch (Exception ex) when (ex is AuthenticationException or IOException)
    {
        _logger.LogWarning("TLS handshake failed from {Endpoint}: {Reason}",
            tcpClient.Client.RemoteEndPoint, ex.Message);
        ssl.Dispose(); tcpClient.Dispose();
        continue;
    }
    stream = ssl;
}

var handler = new ClientHandler(tcpClient, stream, _loggerFactory.CreateLogger<ClientHandler>());
// rest of the wireup unchanged
```

`ClientHandler` change: ctor becomes `(TcpClient, Stream, ILogger)`, the field type changes from `NetworkStream` to `Stream`. The `TcpClient` reference is kept only for `RemoteEndPoint` logging and for the `Close()` call in `TearDownAsync` — every read/write goes through `_stream`, which works the same way for `NetworkStream` and `SslStream`. The existing read loop in `ListenAsync` is byte-stream-shaped (`_stream.ReadAsync(buffer, ...)` with a stateful UTF-8 `Decoder`) and needs no further change: `SslStream` exposes the same `Stream` surface and the existing newline framing happens above the TLS layer.

What `SslStream` actually does on the rejection paths the threat-model section mentions: with `EnabledSslProtocols = SslProtocols.Tls13`, a TLS 1.2-only ClientHello fails handshake; .NET sends a `protocol_version` alert (TLS alert code 70) before closing the socket, so the client gets a diagnosable error rather than a bare RST. A raw plaintext connection that arrives after the LB prefix has been read fails parsing of the byte stream as a ClientHello and `AuthenticateAsServerAsync` throws `AuthenticationException`, which the catch above logs and disposes. Either way the LogWarning line is the only signal in the demo; that's intentional (no plaintext dump of the rejected bytes).

`Program.cs` reads `TLS_CERT_PATH` and `TLS_CERT_PASSWORD` at startup and constructs the cert with `X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet`. If `--insecure` is on the command line, the cert isn't loaded and `_serverCert` stays null. If `--insecure` is off and the env vars are missing or the cert file unreadable, the server logs the error and exits non-zero — a TLS-on server that can't load its cert is a misconfiguration, not a fall-back-to-plaintext condition.

`ReadLbPrefixAsync` is four bytes off the raw stream into a small buffer that's then discarded. The server doesn't act on the prefix in this PR (the LB design owns prefix-routing; the server is the terminus and the prefix is informational at the TCP layer only). When the LB design lands, the server may keep the bytes for room-id sanity checking; the wire shape doesn't change.

## Client wiring

`NetworkService.ConnectAsync` currently does `_client.ConnectAsync(host, port)` then `_client.GetStream()` and stores the result as `NetworkStream? _stream`. The TLS change makes `_stream` a `Stream?`, sends the 4-byte LB prefix on the raw socket first, then wraps in `SslStream` and calls `AuthenticateAsClientAsync`. The cert-pinning callback is the load-bearing piece: it ignores the certificate chain entirely and accepts only if the leaf's SHA-256 thumbprint matches the configured pin.

The pin is read from the env var `NETDRAW_PIN`. There is no `appsettings.json` plumbing on the client today (no `IConfiguration` is referenced anywhere in `NetDraw.Client/`), and standing one up is adjacent work; env var keeps the surface minimal. If the env var is empty and `--insecure` is not set, the client refuses to connect rather than silently accepting any cert. If the env var is empty and `--insecure` is set, no `SslStream` is created.

```csharp
public async Task<bool> ConnectAsync(string host, int port)
{
    try
    {
        _client = new TcpClient();
        await _client.ConnectAsync(host, port);
        Stream stream = _client.GetStream();

        if (!_insecure)
        {
            await WriteLbPrefixAsync(stream, _roomIdHash); // 4 bytes; see LB-prefix section
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = ValidatePinned
            });
            stream = ssl;
        }

        _stream = stream;
        _isConnected = true;
        ClientId = Guid.NewGuid().ToString("N")[..8];
        _ = Task.Run(ListenAsync);
        return true;
    }
    catch (Exception ex)
    {
        Disconnected?.Invoke($"Không thể kết nối: {ex.Message}");
        return false;
    }
}

private bool ValidatePinned(object _, X509Certificate? cert, X509Chain? __, SslPolicyErrors ___)
{
    // Pinning the leaf, not the chain — name-mismatch and untrusted-root errors are expected
    // and ignored. The only thing that matters is whether *this exact cert* is the one we expect.
    if (cert is not X509Certificate2 leaf) leaf = new X509Certificate2(cert!);
    var actual = leaf.GetCertHashString(HashAlgorithmName.SHA256);
    return string.Equals(actual, _pin, StringComparison.OrdinalIgnoreCase);
}
```

`TargetHost` is passed because `AuthenticateAsClientAsync` requires it for SNI; it has no effect on validation since the callback short-circuits on the pin. The value is whatever the user typed into the host field, which means a packet capture shows real SNI (e.g. `localhost`, or a LAN IP rendered as a literal). That's the demo-honest behaviour and matches what a real deployment would do.

`CertificateRevocationCheckMode = NoCheck` is intentional: the dev CA has no CRL/OCSP infrastructure and revocation checks would fail or hang. This mode skips the network round-trip entirely.

`_pin` is read from `NETDRAW_PIN` in the `NetworkService` constructor (or wherever the service is instantiated; `App.xaml.cs` is the natural place if it grows DI later). `_insecure` mirrors the server's `--insecure` and is read from a CLI arg or env var.

The existing `ListenAsync` loop reads via `_stream!.ReadAsync(buffer, ...)` and works unchanged. Disposal: `_stream?.Close()` calls through to `SslStream.Close()` which does the right thing (sends close-notify if the connection is still alive, then closes the underlying `NetworkStream` because `leaveInnerStreamOpen: false`).

One latent client bug, noted in Open Questions and out of scope here: the listener uses `Encoding.UTF8.GetString(buffer, 0, bytesRead)` per chunk, with no stateful `Decoder`. A multi-byte UTF-8 character split across two `ReadAsync` reads will produce replacement characters. The server-side handler does this correctly with a `Decoder`. Touching the client read loop for TLS is a fine moment to align them, but the bug exists today regardless of TLS and shouldn't ride this PR's scope.

## LB-prefix ordering

The custom L4 load balancer (Path A, designed in parallel) routes connections by reading 4 raw bytes from the very start of the TCP stream — a hash of the room-id — and then byte-pumps the rest of the connection blindly. TLS termination at the LB is impossible under that design (the LB doesn't speak TLS and gains nothing by terminating, since it doesn't parse JSON). So TLS must be end-to-end client ↔ backend, and the prefix must travel *before* the handshake on the raw TCP socket.

```mermaid
sequenceDiagram
    participant C as Client
    participant L as Load Balancer (L4)
    participant S as DrawServer

    C->>L: TCP SYN
    L->>S: TCP SYN (after picking backend by next 4 bytes)
    Note over C,L: TCP handshake completes
    C->>L: 4-byte room-id hash (cleartext)
    L->>L: route by hash → backend S
    L->>S: 4-byte room-id hash (forwarded)
    Note over C,S: prefix consumed; both sides now in TLS-handshake mode
    C->>S: TLS ClientHello (via L, opaque)
    S->>C: TLS ServerHello + Cert (via L, opaque)
    Note over C,S: TLS handshake completes
    C->>S: Application Data (TLS-wrapped JSON+\n)
    S->>C: Application Data (TLS-wrapped JSON+\n)
```

Why the prefix in the clear is safe: the prefix is a 32-bit hash of the room-id, not a secret. Room IDs are user-chosen short strings ("demo", "team-meeting") with low entropy. An attacker who sees the hash learns at most that this connection is for *some* room with that hash; they can pre-image-search the small room-id namespace offline and recover the room name. That's a confidentiality loss for the room name — which travels in cleartext JoinRoom messages today and is presumed non-secret — and not a loss for any actual secret. The session token, the user's chat content, the AI prompts, the draw data — all of those are sent *after* the TLS handshake and are protected.

What the LB sees on the wire:

- 4 bytes (the prefix), used to route.
- Then opaque TLS records for the lifetime of the connection. The LB never inspects them.
- Connection close (TCP FIN/RST), used to remove the connection from its tracking table.

What the LB does *not* see: SNI (the LB doesn't parse TLS), cert content, application data, session token, user-id claims, anything in the protocol.

The 4 bytes are sent by the client before constructing `SslStream`, and consumed by the server before constructing `SslStream`. The framing is exactly 4 bytes — fixed-size, no length prefix needed — so there's no chance of the TLS layer accidentally slurping a byte that belonged to the prefix or vice versa. Both sides do `stream.WriteAsync` / `stream.ReadAsync` of exactly 4 bytes on the raw `NetworkStream`, then construct `SslStream` over the same stream. Order of operations in the sequence above is the only correct order.

Failure mode: if the client sends fewer than 4 bytes and then closes, the server's `ReadAsync` returns short and the connection is dropped before the TLS handshake. If the bytes are present but the LB decides to route to a different backend (e.g. backend rebalancing), the new backend reads the same 4 bytes and proceeds identically. Reordering is impossible because TCP is ordered.

## Wireshark demo

Setup. Two terminals, one with the server, one with the client; a third with `tshark`/Wireshark capturing the loopback (or a span port for a real-LAN demo). The demo sequence is identical except for the `--insecure` flag.

Before TLS (everything on the wire as JSON):

```bash
# Terminal A
dotnet run --project NetDraw.Server -- --insecure 5000

# Terminal B (capture)
sudo tshark -i lo -f 'tcp port 5000' -Y tcp -V -O tcp \
  | tee /tmp/capture-plain.txt

# Terminal C
NETDRAW_INSECURE=1 dotnet run --project NetDraw.Client
# join room "demo", draw a line, send a chat
```

What the listener sees: TCP three-way handshake, then a stream of `PUSH, ACK` segments whose payload is human-readable JSON: `{"type":"JoinRoom","senderId":"...",...}\n`, `{"type":"Draw",...}\n`, `{"type":"Chat","payload":{"text":"hello"},...}\n`. Grep the capture for `Chat` or `senderId` and the matches are real plaintext bytes from the wire.

After TLS:

```bash
# Terminal A — generate dev cert once, then run with TLS
bash tools/gen-cert.sh
export TLS_CERT_PATH=$PWD/dev/server.pfx
export TLS_CERT_PASSWORD=netdraw-dev
dotnet run --project NetDraw.Server -- 5000

# Terminal B (same capture command)
sudo tshark -i lo -f 'tcp port 5000' -Y tcp -V -O tcp,tls \
  | tee /tmp/capture-tls.txt

# Terminal C
export NETDRAW_PIN=$(cat dev/pin.txt)
dotnet run --project NetDraw.Client
# same actions: join "demo", draw, chat
```

What the listener sees: TCP handshake, then four bytes (the LB prefix), then `TLSv1.3 Client Hello` with `Server Name: localhost` (or whatever the user typed), then `Server Hello, Certificate, ...`, then `Application Data` records. Grepping the capture for `Chat` or `senderId` returns nothing — those strings live inside the encrypted records. The cert subject is visible because the cert itself is on the wire (even in TLS 1.3 the cert is encrypted in transit, but the listener can connect to the server itself and pull the cert out — pinning protects against a hostile cert, not against curiosity about the legitimate one).

For a single-slide before/after: side-by-side screenshots of the Wireshark "Follow TCP Stream" view. Plaintext side: legible JSON. TLS side: `Application Data` and bytewise gibberish.

## Phases

Four phases, sized small/small/medium/medium. Phases 1–3 are the TLS work proper; Phase 4 is an optional, application-layer post-quantum overlay (educational defense-in-depth, not a production substitute for TLS-layer PQ). The split keeps Phase 1 demo-ready in isolation and lets the default-flip in Phase 2 happen as its own commit/PR for ease of review.

**Phase 1 (S) — Cert script + server SslStream + client cert pin, default off, opt-in.**
Ship `tools/gen-cert.sh`, the `SslStream` wrap on both sides, and the pinning callback. The single CLI flag is `--insecure` on both server and client. In Phase 1 it defaults to `true` (plaintext is the unchanged default; TLS is opted into by passing `--insecure=false`). CI and local dev keep working without the cert. The demo is run with `--insecure=false` on both sides; passing the demo is the acceptance criterion.

> **Status:** implemented. Wire-up is opt-in via `--insecure=false` on the server and `NETDRAW_INSECURE=0` on the client. The 4-byte LB prefix is intentionally not read in Phase 1 — no LB exists yet, so that step is deferred to the LB rollout. The cert script writes `dev/server.pin.sha256` (matching the task-side filename) instead of `dev/pin.txt`; everything else matches the design above.

**Phase 2 (S) — Flip the default.**
`--insecure` flips its default to `false`. Plaintext becomes the explicit opt-out via `--insecure` (or `--insecure=true`). README and the project's run instructions update to mention `gen-cert.sh` as a prerequisite. Anyone who runs `dotnet run --project NetDraw.Server` without the env vars and without `--insecure` gets a clear startup error: "TLS_CERT_PATH not set; run tools/gen-cert.sh or pass --insecure". The server's exit-non-zero behaviour from Phase 1's misconfig path catches this for free.

**Phase 3 (M) — Cert rotation tooling.**
Add the ability to re-issue the leaf and reload it on the running server fleet without dropping existing connections. Mechanics: the script grows a `--reissue` mode that produces a new pfx and pin without touching the CA (so old pins issued by previous reissues against the same CA still chain, even if pinning ignores the chain — this matters if a future PR adopts chain validation). The server gets a SIGHUP-equivalent (file watcher on `TLS_CERT_PATH`, or an admin endpoint) that swaps `SslServerAuthenticationOptions.ServerCertificate` for new connections only; in-flight `SslStream` instances keep using the cert they handshook with. Clients that have the old pin keep working until they reconnect; clients with the new pin work too if the server is presenting the new cert. The actual cut-over uses a brief two-pin window: clients accept either of two pins for a short period. Concrete plan deferred until the LB-fronted multi-backend deploy lands.

**Phase 4 (M, optional) — Application-layer ML-KEM-768 overlay.**

> This is application-layer crypto on top of TLS. The IETF-standard answer is `X25519MLKEM768` at the TLS layer, which we cannot enable until .NET exposes a `SupportedGroups` API. Phase 4 gives the team a demoable PQ story for the prof and real defense-in-depth against captured-now-decrypted-later attacks; it is NOT a production substitute for proper TLS-layer PQ. Drop Phase 4 immediately once .NET exposes the configuration knob.

Depends on Phases 1–3 (needs the TLS 1.3 stream as carrier). Independent of session-token (PR #14) and load-balancer (PR #15): the LB prefix is *before* TLS, the session token is *inside* the PQ wrapping. Approximate size: one week.

**Dependency.** NuGet package `BouncyCastle.Cryptography` (the namespace is `Org.BouncyCastle`; the package id is not), the bcgit/bc-csharp fork. Pinned to whatever the latest stable release is at implementation time — `2.6.2` (2025-07-31) was current at design time and exposes the ML-KEM API used here; ML-KEM has been stable in this package since `2.5.0`. The implementer should not pin to an older version "for stability"; pin to current stable. This is a new third-party dependency, not stdlib, and not currently referenced anywhere in the solution.

**Protocol.** After `AuthenticateAsServerAsync` / `AuthenticateAsClientAsync` returns and before any NetMessage flows, both sides run a single ML-KEM-768 exchange over the (now TLS-protected) stream:

1. Server generates a fresh ML-KEM-768 keypair on connection accept. Per-connection — not rotated mid-connection, not shared across connections. Cheap (sub-millisecond on commodity hardware) and means a key compromise burns one session, not the fleet.
2. Server writes its 1184-byte ML-KEM-768 public key to the stream.
3. Client encapsulates against that public key with `MLKemEncapsulator` (instantiated against the received `MLKemPublicKeyParameters`), producing a 32-byte shared secret and a 1088-byte ciphertext. Client writes the ciphertext to the stream.
4. Server decapsulates the ciphertext with `MLKemDecapsulator` (holding the secret-key parameters from `MLKemKeyPairGenerator` initialized with `MLKemKeyGenerationParameters` against `MLKemParameters.ml_kem_768`) and recovers the same 32-byte shared secret.
5. Both sides feed the shared secret into HKDF-SHA256 twice with empty salt and direction-bound info strings, deriving two independent 44-byte streams:
   - `info = "netdraw-pq-v1 c2s"` → 32-byte key `K_c2s` + 12-byte base nonce `N_c2s` (used to encrypt client→server, decrypt server-side)
   - `info = "netdraw-pq-v1 s2c"` → 32-byte key `K_s2c` + 12-byte base nonce `N_s2c` (used to encrypt server→client, decrypt client-side)

   Per-direction keys are required, not optional. With a single shared key, the symmetric counter scheme below would cause C→S message 0 and S→C message 0 to be encrypted under the same (key, nonce) — the AES-GCM forbidden attack (Joux 2006), which both reveals the XOR of plaintexts and recovers the GHASH authentication subkey, letting an attacker forge arbitrary ciphertexts under that key for the rest of the session. Domain-separated HKDF info strings are the standard fix (TLS 1.3, QUIC, Noise all do equivalent).

6. From here on, every NetMessage envelope is wrapped: `[u32 BE length][AES-GCM ciphertext][16-byte tag]` on the wire. The nonce is NOT on the wire — both sides derive it deterministically from their own per-direction counter (see next paragraph). The inner JSON+`\n` framing is preserved inside the ciphertext.

Nonce policy: `nonce_i = base_nonce XOR i_be` where `i_be` is a 64-bit big-endian counter placed in the low 8 bytes of a 12-byte field (high 4 bytes are the same as `base_nonce`'s high 4 bytes, so the XOR only mutates the low 8). Each direction uses its own counter starting at 0 and its own `base_nonce` from the HKDF stream above. Because the two directions have independent (key, base_nonce) pairs, their nonce spaces are independent by construction; even if the counters happen to coincide, the keys do not. Counter overflow ends the connection — at 2^64 messages per direction it will not happen in any realistic session, but the encoder asserts on increment past `0xFFFFFFFFFFFFFFFE`.

AAD: every `AesGcm.Encrypt`/`Decrypt` call passes the 4-byte big-endian length prefix as additional authenticated data. Without this, an on-path attacker (or a corrupted middlebox) can rewrite the length prefix to truncate or extend a frame and the GCM tag check still passes against the ciphertext bytes the receiver actually consumes — the receiver short-reads or over-reads against a different boundary than the sender intended. AAD-binding the length prefix closes that gap. The receiver computes the AAD from the bytes it has just read off the wire (so a tampered length is the AAD it gets, and the tag check fails as expected).

Key rotation policy: per-connection only. A new TCP connect → new ML-KEM keypair → new shared secret → new HKDF output. There is no in-connection rekey. If long-running connections become a concern (hours of continuous draw activity), a future revision can add a rekey trigger on either a message-count or wall-clock threshold; not in 4.0.

**Wire format.** Raw binary frame (length prefix + ciphertext + tag), not JSON+base64. `length = |ciphertext| + 16` (the GCM tag); the 4-byte length field itself is not counted in the value. The receiver caps `length` at 16 MiB and tears the connection down on anything larger so a malicious or buggy peer cannot drive a 4 GiB allocation off a single u32. The existing newline-delimited JSON model lives *inside* the encrypted payload and is unchanged from the application's point of view. Two reasons to pick raw over JSON+base64:

1. AES-GCM ciphertext and the GCM tag are pure binary; base64-encoding them costs roughly 33% on the wire for no gain (the receiver immediately base64-decodes back to the same bytes the sender had). The newline-delimited JSON convention exists because plaintext JSON is text; the wrapped envelope is not.
2. The PQ wrapper is a single layer of framing — length prefix in, length-prefixed payload out — and a binary frame keeps the parser to one `ReadExactlyAsync(4)` + one `ReadExactlyAsync(length)`. Mixing JSON at this layer would require a streaming JSON parser on what is morally a packet boundary.

The cost is that the `ListenAsync` read loop on both sides changes shape: when PQ is on, it reads length-prefixed frames, AES-GCM-decrypts each (passing the just-read length prefix as AAD and using its own per-direction key + counter-derived nonce), then feeds the plaintext into the same UTF-8 + newline-splitter that exists today. When PQ is off, the loop is unchanged.

**Failure modes.**

- BouncyCastle throws on encapsulate/decapsulate (malformed public key, malformed ciphertext, internal state error). Both sides log at `LogError` with the exception type and tear the connection down — no fallback to TLS-only mid-handshake, because a tampered KEM exchange is exactly the threat we are trying to detect.
- AES-GCM tag mismatch on any wrapped message. Same response: log and tear down. A single tag failure means either bit-flipping in transit (TCP would normally have caught this; if it didn't, something is up), the AAD-bound length prefix was tampered, or a key desync — all of which mean the rest of the connection is uninterpretable.
- Client lacks BouncyCastle, or the user passes `--no-pq`. The client skips the ML-KEM handshake and the server, if its own `--require-pq` is off (default in 4.0), falls back to TLS-only for that connection. Once the default flips in a later phase, `--require-pq` defaults to on and the server hangs up on no-PQ clients. In every case the negotiated mode is logged at `LogInformation` on both sides — `mode=tls+pq` or `mode=tls-only` — so a demo viewer can tell at a glance which connections actually exercised the ML-KEM path.
- `--insecure` and `--pq` are mutually exclusive at startup. The PQ overlay's MITM resistance depends entirely on the underlying TLS authenticating the server (the ML-KEM public key is sent unauthenticated inside the TLS stream); over a plaintext socket an on-path attacker trivially substitutes a rogue public key and the resulting "PQ" channel is theirs. The server and client both refuse to start when both flags are passed, and exit non-zero with a clear message: `--insecure cannot be combined with --pq; pick one.`
- Connection close: both sides call `CryptographicOperations.ZeroMemory` on the four byte arrays (`K_c2s`, `K_s2c`, `N_c2s`, `N_s2c`) in the disposer. Belt-and-suspenders against a future bug that holds the wrapper instance past EndOfStream.

**Demo angle.** Both sides log at `LogInformation` once the shared secret is derived: `[PQ] session key established (first 8 bytes: ab cd ef 12 34 56 78 9a)`. The first-8-bytes preview lets a demo viewer see that client and server agreed on the same key without exposing the whole thing. Optional companion script `tools/pq-decoy.py` opens a TCP+TLS connection, completes the TLS 1.3 handshake, then refuses to do the ML-KEM exchange — when the server is launched with `--require-pq` the script prints the server's tear-down log line for the slide.

**Sequence diagram.**

```mermaid
sequenceDiagram
    participant C as Client
    participant L as Load Balancer (L4)
    participant S as DrawServer

    C->>L: TCP SYN
    L->>S: TCP SYN
    Note over C,S: TCP handshake completes
    C->>L: 4-byte room-id hash (cleartext)
    L->>S: 4-byte room-id hash (forwarded)
    Note over C,S: prefix consumed
    C->>S: TLS 1.3 ClientHello (via L, opaque)
    S->>C: TLS 1.3 ServerHello + EncryptedExtensions + Cert + Finished
    C->>S: TLS Finished
    Note over C,S: TLS 1.3 handshake completes (X25519 inside)
    S->>C: ML-KEM-768 public key (1184 bytes, inside TLS)
    C->>S: ML-KEM-768 ciphertext (1088 bytes, inside TLS)
    Note over C,S: HKDF-SHA256 → per-direction (K, base_nonce)
    C->>S: [len][AES-GCM(JoinRoom JSON, K_c2s, nonce_0)][tag]  AAD=len
    S->>C: [len][AES-GCM(CanvasSnapshot JSON, K_s2c, nonce_0)][tag]  AAD=len
```

## Open questions

1. Pin storage on the client: env var only (current plan) vs introducing `appsettings.json` plumbing (`Microsoft.Extensions.Configuration` is not currently referenced from `NetDraw.Client/`; standing it up would touch DI in `App.xaml.cs`). Env var wins for simplicity now; if other client config grows, revisit.
2. Cert rotation graceful path: in-process file-watch + hot-swap of `ServerCertificate` (one process, one cert file) vs blue-green at the LB layer (rolling restart of backends, LB drains connections). Phase 3 will pick one based on how the LB deploy looks at that point.
3. Demo ergonomics: should `tools/gen-cert.sh` also write a `dev/run-server.env` and a `dev/run-client.env` file with the right `export` lines, so reviewers can `source` them instead of copy-pasting? Trivial to add, but introduces a small "is this for the demo or for prod?" branding concern in `dev/`.
4. SNI value: pass the configured `host` (current plan, demo-honest) vs always pass a fixed sentinel like `netdraw-dev` so packet captures look uniform across runs. The first is more realistic; the second makes the demo more reproducible across operators.
5. Pre-existing client UTF-8 decoder bug (`NetworkService.ListenAsync` uses a stateless `Encoding.UTF8.GetString` per chunk while the server uses a stateful `Decoder`). Orthogonal to TLS and present today; mention here so it's tracked, but not part of this PR's scope.
6. When does .NET expose TLS PQ groups (`SupportedGroups` configuration on `SslClientAuthenticationOptions` / `SslServerAuthenticationOptions`, allowing `X25519MLKEM768` to be required at the TLS layer)? Most likely target is .NET 11 in late 2026, tracking the dotnet/runtime issue for hybrid PQ KEMs. Once that lands and is available on every platform we deploy to, drop Phase 4 entirely — the application-layer overlay is strictly worse than the standardized TLS construction (no formal analysis of the composition, no negotiation, no algorithm-agility framework, more code to maintain).

## Out of scope

- mTLS (client certificates). The session token, not a client cert, is the user-identity story.
- A real CA, Let's Encrypt, ACME. The dev CA is the only CA. Production cert provisioning is not addressed.
- Cert auto-rotation, OCSP stapling, CT-log monitoring, HPKP-style backup pin enforcement at the protocol level.
- Anything beyond a single self-signed leaf cert per server. No SAN-based multi-host certs, no per-room certs, no per-tenant CAs.
- TLS for the McpServer ↔ DrawServer hop (port 5001). Localhost-only by default; if the McpServer is deployed remotely it gets its own design pass.
- TLS for the LAN beacon UDP traffic (`docs/design/lan-discovery-and-server-cache.md`). The beacon is broadcast metadata by design.
- Encryption of data at rest (room snapshots, AI prompt logs, etc.). TLS protects in-transit only.
- TLS-layer post-quantum KEX via the `SupportedGroups` API (`X25519MLKEM768`, codepoint `0x11EC`). Waiting on the .NET runtime to expose the configuration knob; tracked in Open Questions. The Phase 4 overlay is the application-layer stand-in until then.
