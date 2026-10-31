# Computer Use Time Tracking

Records active time by application and writes daily PDF reports. Activity stays on the computer; nothing is sent to a cloud service.

The repository has one platform-neutral core and two desktop apps:

- [Windows/](Windows/): notification-area app for Windows 10/11 (WPF, MSIX installer). See its [README](Windows/README.md).
- [Mac/](Mac/): menu bar app for macOS (Avalonia). See its [README](Mac/README.md).
- [Shared/](Shared/): tracking, SQLite storage, settings, retention, and PDF report code used by both apps, with its tests in `Shared/Core.Tests`.

Each app builds on its own (`Windows/ComputerUse.TimeTracking.slnx` and `Mac/ComputerUse.TimeTracking.Mac.slnx`). Platform code never goes into `Shared/`.

Licensed under Apache License 2.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE). Security reports: [SECURITY.md](SECURITY.md).
