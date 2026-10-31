#!/usr/bin/env bash
# Builds an internal QA installer: "Computer Use Time Tracker <version>.pkg".
#
#   ./installer/build-pkg.sh [version] [runtime]
#
# Builds the app with build-app.sh first, then wraps it in a .pkg that installs to /Applications.
# The package is not signed with a Developer ID Installer certificate, so macOS Gatekeeper blocks the first
# double-click. Testers right-click the .pkg and choose Open. Do not distribute it outside the QA team.
set -euo pipefail

version="${1:-1.0.0}"
runtime="${2:-osx-arm64}"

installer_dir="$(cd "$(dirname "$0")" && pwd)"
mac_root="$(cd "$installer_dir/.." && pwd)"
artifacts="$mac_root/artifacts"
app="$artifacts/Computer Use Time Tracker.app"
staging="$artifacts/pkg-root"
component="$artifacts/component.plist"
pkg="$artifacts/Computer-Use-Time-Tracker-$version-$runtime-QA.pkg"

"$installer_dir/build-app.sh" "$version" "$runtime"

rm -rf "$staging" "$component" "$pkg"
mkdir -p "$staging"
cp -R "$app" "$staging/"

# Stop the installer from relocating the app to wherever a same-named bundle already lives.
pkgbuild --analyze --root "$staging" "$component" >/dev/null
/usr/libexec/PlistBuddy -c "Add :0:BundleIsRelocatable bool false" "$component"

pkgbuild \
  --root "$staging" \
  --component-plist "$component" \
  --install-location /Applications \
  --identifier com.computeruse.timetracking \
  --version "$version" \
  --scripts "$installer_dir/pkg-scripts" \
  "$pkg"

rm -rf "$staging" "$component"
echo "Built: $pkg"
pkgutil --payload-files "$pkg" | head -3
