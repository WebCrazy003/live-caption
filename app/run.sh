#!/usr/bin/env bash
# Build → bundle → sign (stable identity) → launch LocalCaption.
# Run scripts/setup-signing.sh ONCE first for a stable Screen-Recording grant.
set -euo pipefail
cd "$(dirname "$0")"

APP="LocalCaption.app"
IDENTITY="LocalCaption Dev"
KEYCHAIN="$HOME/Library/Keychains/localcaption-signing.keychain-db"

echo "▶ Building…"
swift build

echo "▶ Bundling…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp Info.plist "$APP/Contents/Info.plist"
cp .build/debug/LocalCaption "$APP/Contents/MacOS/LocalCaption"

echo "▶ Signing…"
if security find-certificate -c "$IDENTITY" "$KEYCHAIN" >/dev/null 2>&1; then
  if [ -f .signing/keychain-password ]; then
    security unlock-keychain -p "$(cat .signing/keychain-password)" "$KEYCHAIN" >/dev/null 2>&1 || true
  fi
  codesign --force --sign "$IDENTITY" --identifier com.livecaption.app --timestamp=none "$APP"
  echo "  signed as: $(codesign -dvv "$APP" 2>&1 | awk -F= '/Authority/{print $2; exit}')"
else
  echo "  ⚠ Stable identity '$IDENTITY' not found — signing ad-hoc."
  echo "    macOS will re-prompt for Screen Recording on every rebuild."
  echo "    Fix: run ./scripts/setup-signing.sh once."
  codesign --force --sign - --timestamp=none "$APP"
fi

echo "▶ Launching…"
pkill -f "$APP" 2>/dev/null || true
sleep 1
open "$APP"
echo "✅ Launched."
