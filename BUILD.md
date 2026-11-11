# Sysslar – Build & Install Guide

Sysslar is one app for **Windows and macOS**. The UI is built with
[Avalonia](https://avaloniaui.net) (XAML, like WPF), so the same code and the same
design run on both systems. Features that need the operating system (tray icon,
global shortcut, idle detection, app usage, clipboard images, sounds) have a
Windows and a macOS implementation under `Tasky/Platform/`.

## Prerequisites

| Tool | Needed for | Download |
|------|------------|----------|
| .NET 10 SDK | building and running (Windows or macOS) | https://dotnet.microsoft.com/download/dotnet/10.0 |
| Visual Studio 2022, Rider or VS Code with C# Dev Kit | editing (optional) | |
| Inno Setup 6 | the Windows installer only (Windows) | https://jrsoftware.org/isdl.php |
| Xcode Command Line Tools | the macOS app bundle only (`xcode-select --install`) | |

Supported systems: Windows 10 (version 2004) or later, 64-bit, and macOS 12 or later
(Apple silicon and Intel).

---

## Project Structure

```
Tasky/
├── Tasky.sln
├── Tasky/
│   ├── Tasky.csproj            ← .NET 10 + Avalonia, one project for Windows and macOS
│   ├── Program.cs              ← entry point, fonts
│   ├── App.axaml / .cs         ← startup, single-instance, theme, services
│   ├── MainWindow.axaml / .cs  ← main UI + tray + event handlers
│   ├── Assets/                 ← icon.ico (Windows), sysslar-logo.png
│   ├── Models/                 ← TaskItem, ReminderItem, Note… (plain C# classes)
│   ├── Services/               ← database, reminders, idle monitor, app usage, migration…
│   ├── Platform/               ← one interface per OS feature
│   │   ├── PlatformServices.cs ← picks the Windows or macOS implementation at start-up
│   │   ├── Windows/            ← Win32: tray icon, Win+Shift+T, idle, clipboard, sounds…
│   │   └── MacOS/              ← AppKit / Carbon: menu-bar icon, Ctrl+Option+T, idle, clipboard…
│   ├── ViewModels/             ← MainViewModel, TaskItemViewModel, RelayCommand
│   ├── Behaviors/              ← drag-to-reorder for the task list
│   ├── Controls/ Dialogs/ Windows/   ← the other screens
│   ├── Helpers/                ← shared UI helpers (animations, file pickers, images…)
│   └── Resources/
│       ├── DarkTheme.axaml / LightTheme.axaml  ← colour palettes
│       └── Styles.axaml        ← all control styles
├── installer/setup.iss         ← Inno Setup 6 script → SysslarSetup.exe (Windows)
└── scripts/package-macos.sh    ← builds Sysslar.app and Sysslar.dmg (macOS)
```

---

## Build & Run (Windows or macOS)

```bash
dotnet run --project Tasky/Tasky.csproj
```

Or open `Tasky.sln` in Visual Studio / Rider and press **F5**.

### Tests

```bash
dotnet test Tasky.Tests
```

The tests cover sync: the merge rules (add, update, newest wins, delete markers, tracked time, twins,
task numbers, attachments), the encryption and key codes, and two simulated computers syncing through a
real folder, including a key change. CI runs them on Windows; they also pass on macOS.

Sandboxed runs (`TASKY_DATA_ROOT`) keep the sync key in a plain file in the sandbox instead of the
keychain, and offer only the folder in `TASKY_SYNC_TEST_FOLDER` (never your real cloud folders).
The smoke test uses this to turn sync on, sync and turn it off again.

### First-run notes
- The database is created automatically (see [Data Location](#data-location)).
- A tray icon appears (Windows notification area / macOS menu bar).
- The Quick Add shortcut opens a small input window from anywhere:
  **Win + Shift + T** on Windows, **Control + Option + T** on macOS
  (Cmd + Shift + T is avoided because browsers use it to reopen tabs).

---

## Publish (self-contained, no .NET install needed on the target)

```bash
# Windows (64-bit)
dotnet publish Tasky/Tasky.csproj -c Release -r win-x64     -o publish/win-x64

# macOS — Apple silicon / Intel
dotnet publish Tasky/Tasky.csproj -c Release -r osx-arm64   -o publish/osx-arm64
dotnet publish Tasky/Tasky.csproj -c Release -r osx-x64     -o publish/osx-x64
```

The Windows folder contains `Tasky.exe` plus its DLLs; copy it anywhere and run it.

---

## Windows Installer (Inno Setup)

1. Publish for Windows into the folder the script expects:

   ```powershell
   dotnet publish Tasky/Tasky.csproj -c Release -r win-x64 -o Tasky/bin/Release/net10.0/win-x64/publish
   ```

2. Compile the installer:

   ```cmd
   "C:\Program Files (x86)\Inno Setup 6\iscc.exe" installer\setup.iss
   ```

3. The installer appears at `installer\Output\SysslarSetup.exe`. It installs to
   `%ProgramFiles%\Sysslar\` (an update from Tasky stays in its folder) and optionally adds a desktop shortcut and a startup entry.

---

## macOS App (`Sysslar.app` and `Sysslar.dmg`)

Run on a Mac:

```bash
./scripts/package-macos.sh          # Apple silicon (arm64)
./scripts/package-macos.sh x64      # Intel
```

The script publishes Sysslar, wraps it in `dist/Sysslar.app` (with its icon and
`Info.plist`), signs it ad hoc so it runs on your own Mac, and builds `dist/Sysslar.dmg`.

To give the app to other people without the "unidentified developer" warning, sign it
with an Apple Developer ID and notarize it (needs a paid Apple Developer account). The
script does both when you give it the identity and your Apple credentials:

```bash
SIGN_IDENTITY="Developer ID Application: <Your Name> (<TEAMID>)" \
APPLE_ID="you@example.com" APPLE_TEAM_ID="<TEAMID>" APPLE_APP_PASSWORD="<app-specific password>" \
./scripts/package-macos.sh
```

---

## Code signing in CI

Unsigned installers work, but Windows SmartScreen and macOS Gatekeeper warn people before
they open them. The **Build installers** workflow signs by itself once the settings below
exist (GitHub → Settings → Secrets and variables → Actions). Without them it keeps building
unsigned installers.

### Windows: SignPath Foundation (free for open source)

Sysslar's Windows installer is signed for free by [SignPath Foundation](https://signpath.org)
(see the README's *Code signing policy*). Only `v*` tag builds are signed; the job waits up
to 2 hours for you to approve the signing request in SignPath, then attaches the signed
installer to the release.

1. Apply at signpath.org/apply. After approval, SignPath creates an organization for you.
2. In SignPath, create the project `tasky` with a signing policy `release-signing` (or tell
   CI other names with the variables below), and paste
   [installer/signpath-artifact-configuration.xml](installer/signpath-artifact-configuration.xml)
   as its artifact configuration. Link the project to this GitHub repository.
3. Create an API token for a user who may submit signing requests, then add to GitHub:

| Name | Kind | Value |
|------|------|-------|
| `SIGNPATH_API_TOKEN` | Secret | The SignPath API token |
| `SIGNPATH_ORGANIZATION_ID` | Variable | Your SignPath organization ID |
| `SIGNPATH_PROJECT_SLUG` | Variable (optional) | Defaults to `tasky` |
| `SIGNPATH_SIGNING_POLICY_SLUG` | Variable (optional) | Defaults to `release-signing` |

SignPath requires multi-factor sign-in on both GitHub and SignPath for everyone on the team.

### macOS: Apple Developer ID (paid)

Apple has no free signing for distribution. With an Apple Developer Program membership:

| Secret | What to put in it |
|--------|-------------------|
| `MACOS_CERTIFICATE` | Your "Developer ID Application" certificate exported from Keychain as `.p12`, base64: `base64 -i cert.p12` |
| `MACOS_CERTIFICATE_PASSWORD` | The `.p12` password |
| `MACOS_SIGN_IDENTITY` | e.g. `Developer ID Application: Muhammad Aadil (ABCDE12345)` |
| `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` | For notarization: your Apple ID, team ID, and an app-specific password from appleid.apple.com |

---

## Smoke test

Every CI build starts the app against sample tasks and notes, opens every page and fails
if Sysslar crashes or logs an error, so a broken build can't become an installer. Run it
yourself after publishing (it never touches your real data):

```powershell
./scripts/smoke-test.ps1 -Exe Tasky/bin/Release/net10.0/win-x64/publish/Tasky.exe
```

```bash
./scripts/smoke-test.sh dist/Sysslar.app/Contents/MacOS/Tasky
```

Sysslar needs **no special macOS permissions**: idle time, the frontmost app name, the
clipboard and the global shortcut all work without Accessibility or Screen Recording access.
To start Sysslar at login, add it under **System Settings → General → Login Items**.

---

## Windows and macOS differences

The screens, layout, colours and behaviour are the same on both systems. Where an
OS works differently:

| Feature | Windows | macOS |
|---------|---------|-------|
| Tray icon | Notification area, double-click to open, balloon tips | Menu bar icon with the same menu; no balloon tips (macOS has none) |
| Quick Add shortcut | Win + Shift + T | Control + Option + T |
| Paste shortcut | Ctrl + V | ⌘ V |
| Reminder sounds | Windows Media sounds | Built-in macOS alert sounds (Glass, Ping…) |
| Meeting hours | Detected from microphone / camera use | Not available, so the Meeting cards are hidden |
| App usage | Foreground window, including browser web apps | Frontmost application |
| Emoji icons | Single-colour outlines from Segoe UI Emoji (as always) | Single-colour outlines from the bundled Noto Emoji (same colours; slightly different drawings) |
| Close button | Hides to the tray | Hides; click the Dock or menu-bar icon to bring it back |

---

## Replacing the Icons

- **Windows:** replace `Tasky/Assets/icon.ico` (used for `Tasky.exe`, the window and the tray).
- **macOS:** replace `Tasky/Assets/sysslar-logo.png` (512×512 or larger); the packaging
  script turns it into the app icon.

---

## Data Location

| System | Data (database, settings, attachments) | Logs, daily backups and safety copies |
|--------|----------------------------------------|---------------------------------------|
| Windows | `%APPDATA%\Tasky\` | `%LOCALAPPDATA%\Tasky\` |
| macOS | `~/Library/Application Support/Tasky/` | `~/Library/Application Support/Tasky Local/` |

The database is `tasky.db` (SQLite). To move everything to another computer —
including from Windows to macOS and back — use **Settings → Migrate to another computer**
(see the README).

### Testing with sandbox data

Set the `TASKY_DATA_ROOT` environment variable to an empty folder to run Sysslar
against throw-away data instead of your own. The sandboxed copy uses its own
single-instance lock, so it can run next to your normal Sysslar:

```powershell
$env:TASKY_DATA_ROOT = "$env:TEMP\tasky-sandbox"
dotnet run --project Tasky/Tasky.csproj
```

```bash
TASKY_DATA_ROOT="$TMPDIR/tasky-sandbox" dotnet run --project Tasky/Tasky.csproj
```

---

## Feature Reference

| Feature | How it works |
|---------|-------------|
| Minimize to tray | `Window_Closing` cancels the close and hides the window; the tray icon stays |
| Restore from tray | Double-click the tray icon (Windows), the "Open Sysslar" menu item, or the Dock icon (macOS) |
| Quick Add shortcut | `IGlobalHotkey`: Win32 `RegisterHotKey` on Windows, Carbon `RegisterEventHotKey` on macOS |
| Quick-Add window | `QuickAddWindow` — borderless, transparent, topmost, closes on Escape or when it loses focus |
| Drag-to-reorder | `DragDropBehavior` attached property on the task `ItemsControl` (pointer capture, ghost card, drop line) |
| Active task green highlight | `ActiveToBackgroundConverter` bound to `IsActive` |
| Timer | `DispatcherTimer` in `TaskItemViewModel` refreshes the display every second; time comes from the clock |
| Reminders | `ReminderDialog` stores them in SQLite; `ReminderSchedulerService` checks every minute |
| Reminder pop-ups | `ReminderToastWindow`, Sysslar's own themed window (bottom-right of the screen) |
| Idle / lock / sleep pause | `IdleMonitorService` + `IUserActivity` (Windows session events / macOS polling) |
| SQLite persistence | `DatabaseService`, WAL mode |
| Daily backups | `BackupService`: `VACUUM INTO` snapshot + settings, once a day, last 14 kept; restore is staged and applied at the next start |
| Safe mode | If start-up throws, `App` shows `SafeModeWindow` (restore latest backup, open data folder / log) instead of closing |
| Undo | `UndoService` keeps the last complete / delete / postpone; the notification's Undo button or Ctrl+Z / ⌘Z runs it |
| Keyboard shortcuts | `MainWindow.Keyboard.cs`; the list shown to users is `Helpers/KeyboardShortcuts.cs` |
| Export / import / print | `Services/DataExchange` (CSV both ways, Markdown, printable HTML opened in the browser) |
| Update check | `UpdateCheckService`, off by default; one anonymous request to the GitHub releases API |
| Single instance | Named mutex; a second launch signals the first (named event on Windows, local pipe on macOS) and exits |
