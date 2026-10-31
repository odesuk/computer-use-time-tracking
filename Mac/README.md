# Computer Use Time Tracking for macOS

A menu bar app that records active time by application and writes daily PDF reports. It is the macOS counterpart of the Windows app in `../Windows`. Both use the same tracking, storage, and report code from `../Shared/Core`. Data and reports stay on the Mac.

## Requirements

- macOS 12 or later
- .NET 10 SDK

## Build and test

```sh
dotnet test tests/Mac.Tests/ComputerUse.TimeTracking.Mac.Tests.csproj
./installer/build-app.sh 1.0.0            # Apple silicon; pass osx-x64 as a second argument for Intel
open "artifacts/Computer Use Time Tracker.app"
```

`build-app.sh` publishes a self-contained build and wraps it in a menu-bar-only `.app` bundle, ad-hoc signed for this Mac.

## Permissions

- **Accessibility** (System Settings → Privacy & Security → Accessibility) is needed to record window titles. Without it, reports show app names only. Settings has a button that opens this pane.
- Tracking pauses while the screen is locked and during idle time, as on Windows.

## Where things are stored

- Data, settings, and logs: `~/Library/Application Support/ComputerUse-TimeTracking`
- PDF reports: `~/Documents/Computer Use Time Tracking Reports` (changeable in Settings)
- "Start when I log in" writes `~/Library/LaunchAgents/com.computeruse.timetracking.plist`. It works only from the packaged `.app`.

## Limitations

- Mail is tracked by window title. Outlook-specific subject and conversation matching from the Windows app is not ported yet.
- Sleep is detected by the shared polling-gap check, not by a system notification.
- The app icon is upscaled from the 128px Windows icon. A 1024px PNG at `Assets/AppIcon.png` would give a sharper icon.
- Distributing to other Macs needs a Developer ID certificate and notarization. The ad-hoc build runs only on the Mac that built it.

## Layout

- Root `*.cs`, `Native/`, `Providers/`, `UI/`, `Assets/`: the app (Avalonia menu bar UI, macOS sampler, native interop, host)
- `tests/Mac.Tests/`: unit tests for argument parsing, the LaunchAgent plist, and the sampler
- `installer/build-app.sh`: publish and bundle script (output in `artifacts/`)

## Internal QA installer

Testers: see [QA-INSTALL.md](QA-INSTALL.md) for step-by-step installation and what to report.

```sh
./installer/build-pkg.sh 1.0.0            # writes artifacts/Computer-Use-Time-Tracker-1.0.0-osx-arm64-QA.pkg
cd artifacts
zip -j Computer-Use-Time-Tracker-1.0.0-osx-arm64-QA.zip Computer-Use-Time-Tracker-1.0.0-osx-arm64-QA.pkg ../QA-INSTALL.md
```

The zip holds the `.pkg` and `QA-INSTALL.md`, the two files QA needs. Send that zip to testers.

The `.pkg` installs the app to `/Applications` and clears the download quarantine flag after install. It is not signed with a Developer ID Installer certificate, so the first open is blocked by Gatekeeper: testers right-click the `.pkg` and choose Open. Do not distribute it outside the team. Public distribution needs signing and notarization.
