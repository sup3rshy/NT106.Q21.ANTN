#!/usr/bin/env bash
# tools/gen-cert.sh — dev cert generator for the in-house TLS layer (Phase 1).
#
# Generates a self-signed dev CA (RSA-4096, 10y), a leaf cert signed by it
# (RSA-2048, 1y) with SAN covering localhost, 127.0.0.1, and *.netdraw.local,
# and writes a SHA-256 thumbprint of the leaf for the client to pin.
#
# Outputs (under $OUT, default ./dev):
#   ca.crt              — dev root, preserved across runs once created
#   ca.key              — dev root key
#   server.crt          — leaf cert (regenerated every run)
#   server.key          — leaf private key
#   server.pfx          — PKCS#12 bundle the server loads (no password — dev only)
#   server.pin.sha256   — SHA-256 thumbprint of the leaf, uppercase hex, no separators.
#                         Format matches X509Certificate2.GetCertHashString(SHA256).
#
# Env vars:
#   OUT     — output directory (default: dev)
#
# Usage:
#   tools/gen-cert.sh           # refuses to overwrite existing server.pfx
#   tools/gen-cert.sh --force   # reissues leaf + pfx + pin (CA preserved)
#
# Demo flow:
#   bash tools/gen-cert.sh
#   export TLS_CERT_PATH=$PWD/dev/server.pfx
#   export NETDRAW_PIN=$(cat dev/server.pin.sha256)
#
# The pfx has no password because this is a dev-only artifact. Anyone with
# read access to the file impersonates the server; production cert handling
# is out of scope for Phase 1.

set -euo pipefail

OUT="${OUT:-dev}"
DAYS_CA=3650
DAYS_LEAF=365
SUBJ_CA="/CN=NetDraw Dev CA"
SUBJ_LEAF="/CN=netdraw-dev"

mkdir -p "$OUT"

if [[ ! -f "$OUT/ca.key" ]]; then
  openssl genrsa -out "$OUT/ca.key" 4096
  openssl req -x509 -new -nodes -key "$OUT/ca.key" -sha256 -days "$DAYS_CA" \
    -subj "$SUBJ_CA" -out "$OUT/ca.crt"
fi

if [[ -f "$OUT/server.pfx" && "${1:-}" != "--force" ]]; then
  echo "$OUT/server.pfx exists; pass --force to reissue" >&2
  exit 1
fi

# Leaf SANs: localhost (DNS demo), 127.0.0.1 (loopback), *.netdraw.local
# (room-scoped subdomains for any future LB/multi-host work).
SAN_CONF=$(mktemp)
trap 'rm -f "$SAN_CONF"' EXIT
cat > "$SAN_CONF" <<'EOF'
[req]
distinguished_name = req_distinguished_name
req_extensions = v3_req
prompt = no

[req_distinguished_name]
CN = netdraw-dev

[v3_req]
subjectAltName = @alt_names
keyUsage = digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth

[alt_names]
DNS.1 = localhost
DNS.2 = *.netdraw.local
IP.1  = 127.0.0.1
EOF

openssl genrsa -out "$OUT/server.key" 2048
openssl req -new -key "$OUT/server.key" -subj "$SUBJ_LEAF" \
  -config "$SAN_CONF" -out "$OUT/server.csr"
openssl x509 -req -in "$OUT/server.csr" -CA "$OUT/ca.crt" -CAkey "$OUT/ca.key" \
  -CAcreateserial -days "$DAYS_LEAF" -sha256 \
  -extfile "$SAN_CONF" -extensions v3_req \
  -out "$OUT/server.crt"

openssl pkcs12 -export -out "$OUT/server.pfx" -inkey "$OUT/server.key" \
  -in "$OUT/server.crt" -certfile "$OUT/ca.crt" -passout "pass:"

# Uppercase hex with no separators, matching .NET's
# X509Certificate2.GetCertHashString(HashAlgorithmName.SHA256).
openssl x509 -in "$OUT/server.crt" -noout -fingerprint -sha256 \
  | sed 's/^.*=//; s/://g' | tr 'a-f' 'A-F' > "$OUT/server.pin.sha256"

echo "wrote $OUT/server.pfx (no password — dev only)"
echo "pin (SHA-256, hex): $(cat "$OUT/server.pin.sha256")"
