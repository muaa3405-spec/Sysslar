#!/usr/bin/env bash
# Builds Sysslar.app and Sysslar.dmg for macOS. Run on a Mac with the .NET 10 SDK and
# the Xcode Command Line Tools (sips, iconutil, codesign, hdiutil).
#
#   ./scripts/package-macos.sh          # Apple silicon (arm64)
#   ./scripts/package-macos.sh x64      # Intel
#
# Without SIGN_IDENTITY the app is signed ad hoc, which is enough to run it on your own Mac.
# To share it, set SIGN_IDENTITY="Developer ID Application: … (TEAMID)", and APPLE_ID,
# APPLE_TEAM_ID and APPLE_APP_PASSWORD to notarize it too (see BUILD.md).
set -euo pipefail

ARCH="${1:-arm64}"
case "$ARCH" in arm64|x64) ;; *) echo "Usage: $0 [arm64|x64]" >&2; exit 1 ;; esac

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/Tasky/Tasky.csproj"
DIST="$ROOT/dist"
APP="$DIST/Sysslar.app"
PUBLISH="$DIST/publish-osx-$ARCH"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT")"
# Kept from when the app was called Tasky: the same bundle id makes macOS treat Sysslar as an
# update of Tasky (same permissions, same login item), and the data folders are unchanged too.
BUNDLE_ID="com.nordfin.tasky"

echo "▸ Publishing Sysslar $VERSION for osx-$ARCH"
rm -rf "$PUBLISH" "$APP"
dotnet publish "$PROJECT" -c Release -r "osx-$ARCH" --self-contained true -o "$PUBLISH"

echo "▸ Creating Sysslar.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"

ICONSET="$DIST/Sysslar.iconset"
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z $size $size           "$ROOT/Tasky/Assets/sysslar-logo.png" --out "$ICONSET/icon_${size}x${size}.png"    >/dev/null
  sips -z $((size*2)) $((size*2)) "$ROOT/Tasky/Assets/sysslar-logo.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Sysslar.icns"
rm -rf "$ICONSET"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>               <string>Sysslar</string>
    <key>CFBundleDisplayName</key>        <string>Sysslar</string>
    <key>CFBundleIdentifier</key>         <string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key>         <string>Tasky</string>
    <key>CFBundleIconFile</key>           <string>Sysslar.icns</string>
    <key>CFBundlePackageType</key>        <string>APPL</string>
    <key>CFBundleShortVersionString</key> <string>$VERSION</string>
    <key>CFBundleVersion</key>            <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>     <string>12.0</string>
    <key>LSApplicationCategoryType</key>  <string>public.app-category.productivity</string>
    <key>NSHighResolutionCapable</key>    <true/>
    <key>NSHumanReadableCopyright</key>   <string>© 2026 Muhammad Aadil</string>
</dict>
</plist>
PLIST

if [ -n "${SIGN_IDENTITY:-}" ]; then
  # Developer ID + hardened runtime: needed for notarization. .NET's JIT needs the entitlements.
  echo "▸ Signing with $SIGN_IDENTITY"
  codesign --force --deep --timestamp --options runtime \
           --entitlements "$ROOT/scripts/macos-entitlements.plist" \
           --sign "$SIGN_IDENTITY" "$APP"
else
  echo "▸ Signing (ad hoc)"
  codesign --force --deep --sign - "$APP"
fi

echo "▸ Building Sysslar.dmg"
DMG="$DIST/Sysslar-$VERSION-$ARCH.dmg"
rm -f "$DMG"
# hdiutil sometimes fails with "Resource busy" while macOS still indexes the new app; retry a few times.
for attempt in 1 2 3 4 5; do
  if hdiutil create -volname "Sysslar" -srcfolder "$APP" -ov -format UDZO "$DMG" >/dev/null; then break; fi
  if [ "$attempt" -eq 5 ]; then echo "hdiutil failed 5 times" >&2; exit 1; fi
  echo "  hdiutil failed (attempt $attempt), retrying in 5 s"
  sleep 5
done

if [ -n "${SIGN_IDENTITY:-}" ]; then
  codesign --force --timestamp --sign "$SIGN_IDENTITY" "$DMG"
  # Notarize when Apple credentials are given (an app-specific password from appleid.apple.com).
  if [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ]; then
    echo "▸ Notarizing (this can take a few minutes)"
    xcrun notarytool submit "$DMG" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" \
                                   --password "$APPLE_APP_PASSWORD" --wait
    xcrun stapler staple "$DMG"
  fi
fi

echo "✔ Done: $APP"
echo "✔ Done: $DMG"
