# Computer Use Time Tracking

Computer Use Time Tracking is an open-source Windows tray app that records active time by application and creates daily PDF reports. Tracking data, settings, logs, and reports stay on the user's computer. The application contains no cloud synchronization, account authentication, or MCP implementation.

The app records foreground applications while the user is active, trims idle and suspended time, supports classic and new Outlook activity, and keeps pending blocks on disk when SQLite writes temporarily fail. Users can pause tracking, exclude processes, omit window titles from PDFs, and reset local data.

## Requirements

- Windows 10 version 2004 or later, or Windows 11
- .NET 10 SDK for development
- 64-bit Windows for the provided publish profile

## Build and test

```powershell
dotnet build ComputerUse.TimeTracking.slnx
dotnet test tests/Core.Tests/ComputerUse.TimeTracking.Core.Tests.csproj
dotnet test tests/Windows.Tests/ComputerUse.TimeTracking.Windows.Tests.csproj
```

The public app uses separate product identities for local storage, reports, startup registration, and single-instance synchronization:

- data: `%LOCALAPPDATA%\ComputerUse-TimeTracking`
- reports: `%USERPROFILE%\Documents\Computer Use Time Tracking Reports`
- process coordination: `ComputerUse.TimeTracking`

See [docs/architecture.md](docs/architecture.md) for repository ownership and downstream customization.

## Packaging

`installer/build-msix.ps1` publishes a self-contained `win-x64` app and packages it as MSIX. It needs the Windows SDK tools `makeappx.exe` and `signtool.exe`. Signing credentials belong in a CI secret store and must never be committed.

```powershell
.\installer\build-msix.ps1 -Version 1.0.0.0 -Publisher "CN=Your Publisher" -SkipSigning
```

An unsigned MSIX can be inspected but Windows will not install it as a trusted application. Public releases still need a trusted signing identity and clean-machine testing.

## Privacy

The application itself makes no network requests. Windows may contact an update feed when a package is installed through App Installer. Foreground window titles and email subjects can contain sensitive data; users can omit them from PDFs or exclude applications entirely.

Raw activity is retained for 60 days, generated JSON sidecars for 7 days, and PDFs remain until the user deletes or resets them.

## License

Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
