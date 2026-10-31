#!/usr/bin/env bash
# Builds "Computer Use Time Tracker.app": a self-contained, menu-bar-only bundle for macOS.
#
#   ./installer/build-app.sh [version] [runtime]
#
#   version  defaults to 1.0.0
#   runtime  defaults to osx-arm64 (Apple silicon); use osx-x64 for Intel Macs
#
# The bundle is written to Mac/artifacts. It is ad-hoc signed so it runs on this Mac. Distributing it to other
# Macs needs a Developer ID certificate and notarization; see README.md.
set -euo pipefail

version="${1:-1.0.0}"
runtime="${2:-osx-arm64}"

installer_dir="$(cd "$(dirname "$0")" && pwd)"
mac_root="$(cd "$installer_dir/.." && pwd)"
project="$mac_root/src/Mac/ComputerUse.TimeTracking.Mac.csproj"
artifacts="$mac_root/artifacts"
publish_dir="$artifacts/publish-$runtime"
app="$artifacts/Computer Use Time Tracker.app"
icon_source="$mac_root/src/Mac/Assets/AppIcon.png"
iconset="$artifacts/AppIcon.iconset"

echo "Publishing $runtime build $version..."
dotnet publish "$project" -c Release -r "$runtime" --self-contained true \
  -p:Version="$version" -p:PublishSingleFile=false -o "$publish_dir"

echo "Assembling $(basename "$app")..."
rm -rf "$app" "$iconset"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$iconset"
cp -R "$publish_dir/." "$app/Contents/MacOS/"

# The Windows .ico has no 1024px frame, so the largest sizes are upscaled from the 128px source. Supply a 1024px
# master PNG at src/Mac/Assets/AppIcon.png to get a sharp icon.
for size in 16 32 128 256; do
  sips -z "$size" "$size" "$icon_source" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  sips -z "$((size * 2))" "$((size * 2))" "$icon_source" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"
rm -rf "$iconset"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>Computer Use Time Tracker</string>
  <key>CFBundleDisplayName</key>
  <string>Computer Use Time Tracker</string>
  <key>CFBundleIdentifier</key>
  <string>com.computeruse.timetracking</string>
  <key>CFBundleVersion</key>
  <string>$version</string>
  <key>CFBundleShortVersionString</key>
  <string>$version</string>
  <key>CFBundleExecutable</key>
  <string>ComputerUse.TimeTracking</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>LSUIElement</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
PLIST

echo "Signing (ad hoc)..."
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

echo "Built: $app"
