# Installing Computer Use Time Tracker for QA (macOS)

This guide is for the internal QA team. It covers installing the test build on a Mac, granting the permissions the app needs, and where to find its data. Please do not share the installer outside the team.

## What you need

- A Mac running macOS 12 (Monterey) or later, with an Apple silicon chip (M1 or later)
- The installer file: `Computer-Use-Time-Tracker-<version>-osx-arm64-QA.pkg`
- An administrator account on the Mac (the installer asks for your password)

## 1. Install

1. Double-clicking the `.pkg` will show a warning that it can't be opened. This is expected for internal builds. Instead, **right-click** (or Control-click) the file and choose **Open**, then click **Open** in the dialog.
2. Follow the installer prompts and enter your Mac password when asked.
3. When it finishes, the app is in **Applications** as **Computer Use Time Tracker**.

If the installer still won't open, go to **System Settings → Privacy & Security**, scroll to the message about the installer, and click **Open Anyway**.

## 2. First launch and setup

1. Open **Computer Use Time Tracker** from Applications or Spotlight (⌘ Space, then type its name).
2. The app has no Dock icon. It runs in the **menu bar** at the top right of the screen. Look for its icon there.
3. The first time, a **setup window** opens. Choose a folder for daily PDF reports, then click **Save and start**.

## 3. Grant Accessibility access (needed for window titles)

Without this permission, reports show only app names, not window titles. Tracking still works.

1. Click the menu bar icon, then **Settings…**
2. Click **Open Accessibility settings**. (Or go to **System Settings → Privacy & Security → Accessibility**.)
3. Turn the switch on for **Computer Use Time Tracker**. If it's not listed, click **+**, choose the app from Applications, and turn it on.
4. Quit the app from its menu and open it again so the permission takes effect.

## 4. Optional: start at login

In **Settings…**, tick **Start when I log in**. This takes effect at your next login.

## Using the app

Click the menu bar icon to:

- See today's tracked time
- **Generate today's report now** and open the PDF
- **Open today's report** (double-clicking the icon does the same)
- **Pause tracking** for 15 minutes, 1 hour, or until you resume
- Open the **reports folder** or the **logs** folder
- **Reset local data…** (deletes all tracked data and settings; use with care)
- **Quit**

Tracking pauses automatically when the screen is locked and when you're idle.

## Where things are stored

| What | Location |
|---|---|
| Daily PDF reports | `~/Documents/Computer Use Time Tracking Reports` (or the folder you chose) |
| Tracking database, settings, and logs | `~/Library/Application Support/ComputerUse-TimeTracking` |

To open either folder quickly, use the menu bar icon.

## Updating to a new build

1. Quit the app from its menu.
2. Run the new `.pkg` the same way as in step 1. It replaces the old app.
3. Open the app again. Accessibility access may need to be turned on again for the new build, because the app's identity changes between builds.

## Uninstalling

1. Quit the app from its menu.
2. Drag **Computer Use Time Tracker** from Applications to the Trash.
3. Optional: delete `~/Library/Application Support/ComputerUse-TimeTracking` and the reports folder to remove all data.
4. Optional: remove the app under Accessibility and in **Start when I log in** before deleting it.

## Reporting problems

Please include:

- The macOS version (Apple menu → About This Mac) and the build version from the `.pkg` file name
- What you did, what you expected, and what happened
- The log file from `~/Library/Application Support/ComputerUse-TimeTracking/logs` (the newest `tracker-*.log`)
- A screenshot, if it helps

Logs don't record window titles, email subjects, or senders. Please still check them before attaching, and remove anything you consider private.

## Known limitations in this build

- Apple silicon Macs only. Intel Macs need a separate build.
- Mail is tracked by window title; Outlook-specific subject and conversation grouping from the Windows app is not included yet.
- Sleep is detected by a periodic check, so the time before sleep may be a few seconds off.
- The app icon is a temporary, upscaled icon.
