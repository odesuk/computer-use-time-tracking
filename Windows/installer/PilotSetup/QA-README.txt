Computer Use Time Tracking QA package

Contents
- ComputerUse.TimeTracking_1.0.0.1_Setup.exe: the one-file installer.
- ComputerUse.TimeTracking_LocalPublisher.cer: public QA publisher certificate only.

Prerequisites
- Windows 10 or Windows 11, with the user signed in.
- An administrator must approve the UAC prompt. If a different administrator signs in to approve it, the app installs for that administrator rather than the intended user.

Install
1. Sign in as the person who will use the tracker.
2. Right-click Setup.exe and choose Run as administrator, then approve the UAC prompt.
3. Setup verifies the bundled MSIX and public certificate, adds the certificate to LocalMachine\TrustedPeople, and installs the app for the current user.
4. Open Computer Use Time Tracking from the Start menu and complete the local setup.

The publisher is a self-signed QA certificate. Its SHA-1 thumbprint is:
CB1673110A83486A0F1380DDC40D845D797E82CE

Windows may show a SmartScreen or unknown-publisher warning because this is a pilot certificate. The package contains no PFX or private key. Do not distribute it as a production installer.

For direct MSIX testing only, an administrator must import the included .cer into LocalMachine\TrustedPeople before installing the MSIX. The Setup.exe does this automatically.

Data and uninstall
- Local tracking data, logs, and CSV/JSON report sidecars are under %LOCALAPPDATA%\ComputerUse-TimeTracking. PDF reports default to Documents\Computer Use Time Tracking Reports.
- Uninstall from Settings > Apps > Installed apps > Computer Use Time Tracking. The pilot certificate remains trusted until an administrator removes thumbprint CB1673110A83486A0F1380DDC40D845D797E82CE from LocalMachine\TrustedPeople.
