### Downloads

| File | For |
|---|---|
| **Sysslar-…-Windows-Setup.exe** | Windows 10 (version 2004) or later, 64-bit |
| **Sysslar-…-macOS-AppleSilicon.dmg** | Macs with Apple silicon (M1 and later), macOS 12 or later |
| **Sysslar-…-macOS-Intel.dmg** | Intel Macs, macOS 12 or later |

### Installing

**Windows:** run the setup file. Windows SmartScreen may say it "protected your PC" because the installer isn't code-signed. Click **More info → Run anyway**.

**macOS:** open the `.dmg` and drag **Sysslar** to **Applications**. The app isn't signed with an Apple Developer ID yet, so on first launch macOS says it can't check it for malicious software. To open it, **right-click Sysslar → Open → Open**, or go to **System Settings → Privacy & Security → Open Anyway**. You only need to do this once.

Install it over your current Sysslar: your tasks, notes and attachments are kept.

[Code signing policy](https://github.com/muaa3405-spec/tasky#code-signing-policy) · [Privacy policy](https://github.com/muaa3405-spec/tasky#privacy-policy)

### New in 1.4.3
- **Switch workspaces instantly.** Sysslar no longer restarts: a short "Switching to …" screen covers the change while your tasks, notes, settings and reminders for the other workspace load. A running timer is paused and saved first, as before.

### New in 1.4.2
- **Cleaner task cards.** Attachment and reminder counts are small chips next to the date (📎 1, 🔔 1), so the title and description keep their full width on narrow windows.
- **Smoother scrolling** in the task list.
- **Popups follow your zoom** (Settings → Appearance → Zoom), just like the main window.
- **Descriptions** that are cut off always end in "…".

### New in 1.4.1
- **Fits small screens.** The window now always fits the screen it opens on, so its title bar can't end up off-screen on a small laptop display.
- **Zoom.** Settings → Appearance → **Zoom** makes everything smaller or larger (80–130 %), also with **Ctrl/⌘ −**, **+** and **0**. On Windows, Sysslar now starts a little smaller (90 %).
- **Remembers your choices.** Window size and position, zoom, All / Today / This Week, the group filter, the Completed sort order and the Stats filters are kept between launches.
- **Shorter task cards.** The attachment and reminder counts sit under the action buttons instead of on a line of their own.
- **Narrow windows.** All / Today / This Week stays next to **New Task** and shrinks to fit.
- **Tidier Settings.** The Data section is one clean card: Recycle Bin, Backups, Move to another computer, and Export & import.

### A fresh new look
- **Modern design.** A new navy dark theme and a clean light theme, with one design language across every page and dialog: rounded cards, pill buttons and a single set of line icons in place of emoji.
- **Calmer task cards.** Action buttons are grey until you point at them, then show their colour. Cards brighten slightly on hover.
- **Edit tasks on a page.** Clicking a task opens a scrollable **Edit task** page inside the window instead of a popup. **Esc** goes back, **Ctrl+S / ⌘S** saves.
- **Notes open on their own page** too, with the same Back, Copy and Save, and a prompt if you leave with unsaved changes.
- **Fits any window size.** On a narrow window the card buttons move into a **⋯** menu, the All / Today / This Week switch gets its own row, and Stats, Completed and Settings rearrange so nothing is cut off.
- **Smooth animations.** The view switch slides, cards rise in, a completed task folds away, a running timer pulses, and pages and dialogs ease in. They switch off when your system's **Reduce motion** setting is on.
- **No shortcuts popup at first start.** Press **?** any time to see them; in Settings they sit in a panel you can open.

### Also new since 1.2.3: Tasky is now Sysslar
- **New name, new icon.** *Sysslar* is Swedish for everyday tasks and chores.
- **Updates in place.** Install it over your current Tasky: it is the same app, so your tasks, notes, attachments, settings and backups are all kept.
  - **Windows:** the setup replaces Tasky (same folder, one entry in Installed apps) and renames its shortcuts.
  - **macOS:** drag **Sysslar** to Applications. The first time it opens, it moves the old Tasky app to the Trash.

### New
- **Workspaces.** Keep separate sets of tasks, for example **Personal** and **Office**. Each workspace has its own tasks, notes, groups, reminders, time history, reports, settings and backups, and nothing mixes between them.
  - Click the workspace name **next to the Sysslar logo** to switch. Sysslar restarts in the other workspace in a second or two; a running timer is paused and saved first.
  - **New workspace…** starts one empty, or fills it straight from a Sysslar migration file, so you can bring tasks from another computer in without touching what you already have.
  - **Manage workspaces…** renames, recolours or deletes them.
- Your existing tasks become the first workspace, **Personal** (rename it any time). Nothing about them changes or moves.

### Changed
- **Click anywhere on a task to edit it.** The card presses in when you click. Its buttons (done, timer, postpone, delete…) still do their own thing, and **Enter** on a selected task shows its read-only details.
