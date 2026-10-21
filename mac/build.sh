#!/bin/bash
# Builds mac/build/Aevalsistant.app, one binary for Apple silicon and Intel, and zips it as
# mac/build/Aevalsistant-mac.zip. Needs Xcode or the Command Line Tools on macOS 13 or later.
# The version comes from <Version> in Aevalsistant.csproj, so both apps ship under one number.
set -euo pipefail
cd "$(dirname "$0")/.."

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Aevalsistant.csproj | head -1)
out=mac/build
app="$out/Aevalsistant.app"
rm -rf "$out"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

sources=(mac/Sources/Shared/*.swift mac/Sources/App/*.swift mac/Sources/Main/*.swift)
for arch in arm64 x86_64; do
  swiftc -O -swift-version 5 -target "$arch-apple-macos13.0" -module-name Aevalsistant "${sources[@]}" -o "$out/Aevalsistant-$arch"
done
lipo -create -output "$app/Contents/MacOS/Aevalsistant" "$out/Aevalsistant-arm64" "$out/Aevalsistant-x86_64"
rm "$out/Aevalsistant-arm64" "$out/Aevalsistant-x86_64"

sed "s/__VERSION__/$version/g" mac/Info.plist > "$app/Contents/Info.plist"
cp assets/avatar.png assets/sounds/done.wav assets/sounds/needs-you.wav "$app/Contents/Resources/"

iconset="$out/AppIcon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" assets/mac-icon.png --out "$iconset/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) assets/mac-icon.png --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
rm -rf "$iconset"

# Ad-hoc signed: Apple silicon will not run an unsigned binary, and the updater checks that the
# signature still matches the files. There is no Developer ID yet, so Gatekeeper asks once.
codesign --force --sign - --identifier io.github.aevalmere.aevalsistant "$app"
codesign --verify --deep --strict "$app"

ditto -c -k --keepParent "$app" "$out/Aevalsistant-mac.zip"
echo "Built $app ($version) and $out/Aevalsistant-mac.zip"
