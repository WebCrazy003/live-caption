#!/usr/bin/env bash
# One-time setup of a STABLE self-signed code-signing identity for local dev.
#
# Why: macOS TCC (Screen Recording, Microphone, …) keys a permission grant to the app's
# code-signing identity. Ad-hoc signing (`codesign -s -`) has no stable identity — its
# code hash changes every build — so macOS forgets the grant on each rebuild and re-prompts.
# Signing with a fixed cert gives a stable "designated requirement", so the grant sticks.
#
# This is a local, self-signed DEV cert (untrusted, not notarized). Real distribution uses
# a Developer ID cert + notarization (SPEC-09, Phase 6). Secrets live in .signing/ (gitignored).
#
# Idempotent: safe to re-run. Run once, then use ./run.sh.
set -euo pipefail
cd "$(dirname "$0")/.."

SIGN_DIR=".signing"
IDENTITY="LocalCaption Dev"
KEYCHAIN="$HOME/Library/Keychains/localcaption-signing.keychain-db"
mkdir -p "$SIGN_DIR"

# Stable keychain password (generated once, kept locally).
PW_FILE="$SIGN_DIR/keychain-password"
if [ ! -f "$PW_FILE" ]; then
  openssl rand -hex 12 > "$PW_FILE"; chmod 600 "$PW_FILE"
fi
KCPW="$(cat "$PW_FILE")"

# 1) Cert + key with the codeSigning EKU (only if missing).
if [ ! -f "$SIGN_DIR/cert.pem" ] || [ ! -f "$SIGN_DIR/key.pem" ]; then
  cat > "$SIGN_DIR/openssl.cnf" <<'EOF'
[req]
distinguished_name = dn
x509_extensions = v3
prompt = no
[dn]
CN = LocalCaption Dev
[v3]
basicConstraints = critical,CA:false
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
EOF
  openssl req -x509 -newkey rsa:2048 -nodes \
    -keyout "$SIGN_DIR/key.pem" -out "$SIGN_DIR/cert.pem" \
    -days 3650 -config "$SIGN_DIR/openssl.cnf" -extensions v3
fi

# 2) PKCS#12 bundle — MUST be -legacy (SHA1/3DES) so macOS `security` can import it.
openssl pkcs12 -export -legacy \
  -inkey "$SIGN_DIR/key.pem" -in "$SIGN_DIR/cert.pem" \
  -out "$SIGN_DIR/cert.p12" -passout "pass:$KCPW" -name "$IDENTITY"

# 3) Dedicated keychain (recreated cleanly), import identity, allow codesign non-interactively.
security delete-keychain "$KEYCHAIN" 2>/dev/null || true
security create-keychain -p "$KCPW" "$KEYCHAIN"
security set-keychain-settings "$KEYCHAIN"                 # no auto-lock timeout
security unlock-keychain -p "$KCPW" "$KEYCHAIN"
security import "$SIGN_DIR/cert.p12" -k "$KEYCHAIN" -P "$KCPW" \
  -T /usr/bin/codesign -T /usr/bin/security
security set-key-partition-list -S apple-tool:,apple: -s -k "$KCPW" "$KEYCHAIN" >/dev/null

# 4) Add to the user keychain search list (keep the login keychain).
security list-keychains -d user -s login.keychain-db "$KEYCHAIN"

echo "✅ Signing identity '$IDENTITY' ready in $KEYCHAIN"
echo "   Now run ./run.sh — the app signs with this identity every build."
echo "   First run after switching identities: re-grant Screen Recording once; it persists after that."
