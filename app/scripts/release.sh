#!/usr/bin/env bash
# Build a RELEASE LocalCaption.app you can copy to another Mac, plus a zip of it.
#
#   ./scripts/release.sh                       # ad-hoc signed (free; other Mac must "Open Anyway")
#   VERSION=0.2 ./scripts/release.sh           # also stamp the version into Info.plist
#
#   # Proper distribution (Apple Developer Program): Developer ID + hardened runtime,
#   # then notarize + staple so it opens with no warning on any Mac.
#   SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
#   NOTARY_PROFILE=localcaption ./scripts/release.sh
#   # (create the profile once: xcrun notarytool store-credentials localcaption \
#   #    --apple-id you@example.com --team-id TEAMID --password <app-specific-password>)
#
# Env:
#   VERSION         CFBundleShortVersionString (default: whatever Info.plist says)
#   BUILD           CFBundleVersion (default: git commit count)
#   SIGN_IDENTITY   codesign identity; unset = ad-hoc ("-")
#   NOTARY_PROFILE  notarytool keychain profile; set = notarize + staple (needs SIGN_IDENTITY)
#   ARCHS           CPU architectures (default: "arm64 x86_64" = universal, runs on Apple Silicon
#                   and Intel; "arm64" or "x86_64" for a single-arch build)
#
# Output: dist/LocalCaption.app and dist/LocalCaption-<version>.zip
# Target Mac: macOS 14+, Apple Silicon or Intel (universal by default). Whisper models download
# on first launch; Interview mode needs Codex installed there.
set -euo pipefail
cd "$(dirname "$0")/.."

NAME="LocalCaption"
BUNDLE_ID="com.livecaption.app"
DIST="dist"
APP="$DIST/$NAME.app"
PLIST="$APP/Contents/Info.plist"
SIGN_IDENTITY="${SIGN_IDENTITY:-}"
NOTARY_PROFILE="${NOTARY_PROFILE:-}"
ARCHS="${ARCHS:-arm64 x86_64}"

if [ -n "$NOTARY_PROFILE" ] && [ -z "$SIGN_IDENTITY" ]; then
  echo "✗ NOTARY_PROFILE needs SIGN_IDENTITY (a Developer ID Application cert)." >&2
  exit 1
fi

# One native SwiftPM build per arch, then lipo. (A multi-`--arch` build switches SwiftPM to the
# Xcode build system, which rejects swift-collections' language-version setting and exits 1.)
echo "▶ Building (release, $ARCHS)…"
SLICES=()
for a in $ARCHS; do
  swift build -c release --arch "$a" --product "$NAME"
  BIN_DIR="$(swift build -c release --arch "$a" --show-bin-path)"
  SLICES+=("$BIN_DIR/$NAME")
done

echo "▶ Bundling…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp Info.plist "$PLIST"
lipo -create "${SLICES[@]}" -output "$APP/Contents/MacOS/$NAME"
# SwiftPM resource bundles from dependencies (GRDB/swift-crypto privacy manifests, swift-transformers
# fallback tokenizer configs). Contents/Resources is the only place codesign allows them.
for b in "$BIN_DIR"/*.bundle; do
  [ -e "$b" ] && cp -R "$b" "$APP/Contents/Resources/"
done

if [ -n "${VERSION:-}" ]; then
  /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$PLIST"
fi
BUILD="${BUILD:-$(git rev-list --count HEAD 2>/dev/null || echo 1)}"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUILD" "$PLIST"
VERSION="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$PLIST")"

echo "▶ Signing…"
if [ -n "$SIGN_IDENTITY" ]; then
  # Hardened runtime + secure timestamp are required for notarization.
  # audio-input: the hardened runtime blocks the microphone without it (Record audio ▸ My microphone).
  codesign --force --options runtime --timestamp \
    --entitlements LocalCaption.entitlements \
    --identifier "$BUNDLE_ID" --sign "$SIGN_IDENTITY" "$APP"
else
  echo "  (ad-hoc — set SIGN_IDENTITY for a Developer ID signature)"
  codesign --force --identifier "$BUNDLE_ID" --sign - --timestamp=none "$APP"
fi
codesign --verify --deep --strict "$APP"

ZIP="$DIST/$NAME-$VERSION.zip"
rm -f "$ZIP"
if [ -n "$NOTARY_PROFILE" ]; then
  echo "▶ Notarizing (takes a few minutes)…"
  ditto -c -k --keepParent "$APP" "$ZIP"
  xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  rm -f "$ZIP"   # re-zip so the shipped zip carries the stapled ticket
fi

echo "▶ Zipping…"
ditto -c -k --keepParent "$APP" "$ZIP"

echo
echo "✅ $APP  (v$VERSION build $BUILD, $(lipo -archs "$APP/Contents/MacOS/$NAME"))"
echo "   $ZIP  ($(du -h "$ZIP" | cut -f1))"
if [ -n "$NOTARY_PROFILE" ]; then
  spctl --assess --type execute -v "$APP" || true
else
  echo "   Not notarized: on the other Mac, open it once, then System Settings → Privacy & Security"
  echo "   → Open Anyway (or: xattr -dr com.apple.quarantine /Applications/$NAME.app)."
fi
