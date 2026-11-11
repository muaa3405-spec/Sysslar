# Encrypted folder sync (design sketch)

Status: built (not released yet). Updated: 2026-11-11. Code: `Tasky/Services/Sync/`, `Tasky/Dialogs/SyncDialog.*`,
tests in `Tasky.Tests/`.

### As built: what differs from the sketch below

- **No changes to save code.** Instead of stamping clocks in every `DatabaseService` write, each sync compares the
  database with the copy kept from the last sync (`SyncRecords`, inside the workspace database) and stamps what
  changed. Because that copy lives in the database, a backup restore or a migration import swaps it together
  with the data and can never look like "everything was deleted".
- **Emptying the recycle bin syncs** (it writes delete markers), instead of being local only.
- **No Undo for a merge.** With true sync another computer would put the change straight back; the daily backup
  is still taken before the first sync of the day.
- **Settings don't sync**, only data. Workspace name and colour travel in each computer's device file so the
  other computers can list the workspace.
- **Folder layout:** `key.sysslar`, `keychange-<old key id>.sysslar`, `devices/<device>.sysslar`,
  `ws-<sync id>/<device>.sysslarsync`, `ws-<sync id>/files/<name>.blob`.
- **Key code:** 54 characters in 9 groups of 6 (52 base32 characters for 256 bits, plus a 2-character checksum).
- **One key for all workspaces** on a computer, with a key per workspace derived from it (HKDF).
- **Inactive workspaces sync too**, without opening them on screen. The open workspace refreshes in place, so open
  dialogs and a running timer are not disturbed (a running timer's time is merged once it stops or saves).

## Goal

Keep chosen workspaces the same on several computers, Mac and Windows mixed, **without an
account, a server or Sysslar ever going online**. The user picks a folder and turns on the
workspaces they want. After that, there is nothing to do.

Four promises drive everything below:

1. **Nothing is ever replaced.** Sync only **adds** what's missing and **updates** what changed.
   A computer's data is never swapped for another computer's copy.
2. **Something is removed only if it was on both computers and was deleted on one.** Even then it
   goes to the recycle bin (30 days), not away for good.
3. **Each workspace syncs only if the user switches it on**, separately on each computer.
4. **Stolen sync files are useless.** They are encrypted before they leave Sysslar. The key exists
   only on the computers where sync is on, and the user can change it at any time.

Not goals: syncing between *different people*, or syncing the app-usage and meeting logs (those
stay on each computer).

---

## 1. Merge rules: add, update, remove-if-both

Every record (task, completed task, note, group, reminder, attachment) gets a permanent
`SyncId`, so the same task is recognised on every computer. On each merge, for each record:

| This computer | Other computer | Result |
|---|---|---|
| doesn't have it | has it | **Add** it here |
| has it | doesn't have it, and never had it | **Keep** it, and the other computer adds it next time |
| has it | changed it | **Update** the changed fields here |
| has it | had it and **deleted** it | **Move it to the recycle bin** here |
| doesn't have it | deleted it | Nothing (it never reached this computer) |
| deleted it | changed it while apart | **Stays deleted** (in the recycle bin, *with* the other computer's change applied, so restoring it brings back the newest version) |
| restored it from the recycle bin | deleted it earlier | **Restored everywhere** (restoring is newer than the delete) |

"Never had it" vs "had it and deleted it" is the key difference. A deletion is written down as a
small **delete marker** (the record's `SyncId` + when it was deleted). Only a marker can remove
something. A record that is simply *missing* from another computer is **never** removed, so a new
computer, a half-synced folder or a computer that was off for a month can't wipe anything.

### Updates: true sync, newest change wins

Every change is applied everywhere, so after a sync **every computer shows exactly the same thing**.
Updates go **field by field**: changing the title on one computer and completing the task on another
keeps both changes. When the **same** field was changed on both computers while they were apart, the
**newest change wins** on every computer. That holds for every field, including note bodies and task
descriptions. Sysslar never adds extra copies or "conflict" text.

### Special cases

| Case | Rule |
|---|---|
| Completed tasks | Completing and un-completing are ordinary field updates. Completed tasks sync like any other task, including `CompletedAt`, so reports match on every computer |
| Tracked time | Each computer counts its own seconds per task, and the total is the sum. Time is only ever added, never overwritten |
| Running timer | Not synced. When the timer stops, its time goes into this computer's count |
| Task numbers (`#012`) | If two computers made the same number while apart, the task created **later** gets the next free number. The sync note says `#012 from MacBook is now #019` |
| Recurring tasks | The next copy's `SyncId` is derived from the parent's `SyncId` and its due date, so completing the same task on two computers makes **one** next copy, not two |
| Emptying the recycle bin | Local only. Other computers keep their bin for the normal 30 days |
| First sync of a workspace that already has the same tasks on both computers (for example from an earlier migration file) | Records without a shared `SyncId` are matched by title + created time, so they aren't doubled. Anything that doesn't match is added |

### Stable ids and clocks (schema change)

Today every table uses `INTEGER AUTOINCREMENT` ids, so task 37 on one computer isn't task 37 on
another. Add to `Groups`, `Tasks`, `Notes`, `Reminders`, `Attachments` and `NoteAttachments`:

| Column | Purpose |
|---|---|
| `SyncId TEXT UNIQUE` | Random GUID, backfilled for existing rows. The identity across computers |
| `Clocks TEXT` | Small JSON: the clock of the last change to each field |
| `DeletedAt` / `DeleteClock` | Already on `Tasks` (`DeletedAt`). Added to the other tables, which today delete rows outright |

Plus one new table, `SyncDeletes (SyncId, TableName, Clock)`, for records removed permanently
(for example from an emptied recycle bin), so the delete marker survives.

**Clock** = hybrid logical clock: UTC milliseconds + counter + device id. It always moves forward
even if one computer's clock is minutes off, and every computer reaches the same result from the
same data. Local integer ids stay, so the rest of the app doesn't change.

---

## 2. Folders: Google Drive, iCloud, OneDrive, Dropbox, anything

Sysslar never talks to Google, Apple, Microsoft or Dropbox. It writes files into a folder, and the
user's own Drive/iCloud/OneDrive/Dropbox app moves them. That keeps the README's promise that
Sysslar never goes online, and nothing needs signing in.

The folder picker shows the services it finds on this computer, then *Choose another folder…*
(NAS share, Syncthing, USB stick):

| Service | macOS | Windows |
|---|---|---|
| **Google Drive** (Drive for desktop) | `~/Library/CloudStorage/GoogleDrive-*/My Drive` | the drive whose volume label is `Google Drive` (usually `G:\My Drive`); older mirror installs: `%USERPROFILE%\Google Drive` |
| iCloud Drive | `~/Library/Mobile Documents/com~apple~CloudDocs` | `%USERPROFILE%\iCloudDrive` |
| OneDrive | `~/Library/CloudStorage/OneDrive-*` | `%OneDrive%` (and `%OneDriveCommercial%`) |
| Dropbox | `~/Library/CloudStorage/Dropbox`, `~/Dropbox` | path from `%LOCALAPPDATA%\Dropbox\info.json`, else `%USERPROFILE%\Dropbox` |

If none is found, the picker shows one line: *Install Google Drive, iCloud, OneDrive or Dropbox on
each computer, or pick any shared folder.*

Things the cloud folders need:

- **"Online-only" files** (Google Drive streaming, OneDrive Files On-Demand, iCloud "optimise
  storage") download when they're opened, so reading may take a few seconds or fail while offline.
  A file that can't be read yet is skipped and retried. It is never treated as missing.
- Sysslar writes `name.tmp` and then **renames** it into place, so other computers never read half
  a file.
- Two computers may pick the same service but see a different local path (`G:\My Drive` vs
  `~/Library/CloudStorage/…`). Each computer stores its own path, and only the **folder contents**
  are shared.

### Folder layout

```
<chosen folder>/Sysslar Sync/
  space.sysslar                  encrypted: key fingerprint, list of synced workspaces (ids + names)
  <workspace sync id>/
    <device id>.sysslarsync      this computer's latest copy of that workspace
    files/<blob name>.blob       encrypted attachments, written once, shared
  key-change-<n>.sysslar         written only when the key is changed (see section 4)
```

Each computer writes **only its own** `<device id>.sysslarsync` file and **reads everyone else's**.
Because no two computers ever write the same file, cloud apps never make "conflicted copy" files.
Overwriting your own file on each save is safe because the data is merged, not replaced (section 1).

Ids are random lowercase base32, and attachment blob names are an HMAC of their content hash. No
workspace, computer or user names appear in file names.

---

## 3. Works the same on Mac and Windows

The sync files are **byte-for-byte the same format** on both systems. Only how the key is kept and
where folders are differs, and both sit behind small interfaces.

| Concern | Rule |
|---|---|
| Format | UTF-8 JSON, gzip, then encrypted. Times always UTC ISO-8601. No platform types |
| Paths inside files | None. Attachments are referenced by blob name and original file name, and saved into the local `Attachments` folder on arrival (this already works across OSes today) |
| File names | Lowercase `a–z 2–7` only: safe on case-insensitive drives and free of characters Windows rejects |
| Attachment names | Original name kept for display, cleaned when written to disk (`<>:"/\|?*`, trailing dots and spaces, reserved names like `CON`) |
| Key storage | `ISecretStore`: **Windows DPAPI** (current user) and **macOS Keychain**. Same API, chosen at start |
| Watching the folder | .NET `FileSystemWatcher` (FSEvents on macOS, ReadDirectoryChanges on Windows), **plus** a check every 5 minutes, because watchers miss changes on some cloud and network drives |
| Crypto | .NET `AesGcm`, `HKDF`, `HMACSHA256`, the same on both |

The test matrix runs the merge tests against files written by both OSes, and the CI smoke test
writes a sync file on `windows-latest` and reads it on `macos-15`.

---

## 4. Encryption and the key

### What the user is told (shown on the Sync page, plain words)

> 🔒 **Your synced data is locked before it leaves this computer.**
> Sysslar encrypts every sync file (AES-256) before saving it to your folder. If someone gets the
> files, including Google, Apple, Microsoft or Dropbox, they can't read your tasks or notes.
>
> The **encryption key** is kept only on the computers where you turn on sync, protected by your
> computer account. Keep a copy somewhere safe, such as a password manager. Without it a new
> computer can't join, and nobody, including us, can recover it.
>
> You can **change the key** at any time. Your other computers pause syncing until you type the new
> key there. Nothing is lost while they wait.

The text is honest about the limit: the files are safe if they are stolen, but anyone who can sign
in to a computer where sync is on can see the data, just as they can see the app.

### How it works

- **One key for everything** this user syncs, so there is only one thing to keep. A 256-bit random
  secret, shown as **`K7QF-29XM-H4TD-8RWA-NC3P-6YVB-…`** (base32 with a checksum, so typos are
  caught). It's random, not a chosen password, so it can't be guessed.
- Separate keys per workspace and per purpose are derived with HKDF (`file`, `blob`, `blob-name`,
  `fingerprint`). One workspace's files can't be read with another workspace's derived key.
- **Key fingerprint** = 8 bytes from HKDF. It is stored in clear in each file header, so Sysslar
  can tell "wrong or old key" apart from "damaged file" without trying to decrypt.
- Files reuse `MigrationCrypto`'s chunked AES-256-GCM with a new header:
  `"SYSSYNC1" | version | key fingerprint | workspace sync id | device id | save number | nonce prefix`.
  The header is authenticated, so a file can't be renamed onto another computer, moved to another
  workspace, edited or cut off. Each computer remembers every other computer's highest save number
  and **ignores an older file put back** in its place.
- Stored with `ISecretStore` (DPAPI/Keychain), never in `settings.json` or the database.

### Changing the key

On computer A: **Change key…** → confirm → Sysslar:

1. Makes a new secret and shows it once, with **Copy**.
2. Writes `key-change-<n>.sysslar`. It is **encrypted with the old key** and contains only the
   new key's fingerprint and `n`. It does **not** contain the new key.
3. Re-encrypts `space.sysslar`, its own workspace files and the shared blobs with the new key.

Computer B, next time it looks:

1. Finds `key-change-<n>`, opens it with the old key, and so knows the change is genuine. Someone
   with only folder access can't fake it, because they don't have the old key.
2. **Stops syncing** (stops reading *and* writing) and shows the attention banner:
   *Sync paused: the encryption key was changed on Aadil's MacBook. [Enter new key]*
3. Keeps working normally offline. When the new key is entered and its fingerprint matches,
   B merges everything both ways. Nothing done in the meantime is lost.

Changing the key is also the way to **lock out a lost or old computer**: it can't read anything
written after the change. Once every active computer has the new key, Sysslar deletes files still
encrypted with the old one.

---

## 5. Per-workspace choice

- Sync is turned on **once per computer** (folder + key). Then **each workspace has its own switch**,
  separately on each computer. *Office* can sync on the work laptop and home Mac while *Personal*
  stays only on the home Mac.
- The list also shows **workspaces that exist only on other computers** (read from
  `space.sysslar`). Switching one on creates it here and fills it.
- Switching on a local workspace whose **name matches** one on another computer asks once:
  *"Office" is also on Aadil's MacBook. [Merge them] [Keep separate]*. *Merge* uses the rules in
  section 1, so both sides keep everything.
- **Switching a workspace off never deletes anything**, on any computer. This computer stops reading
  and writing it, and its file is removed from the folder. Other computers keep all their data.
- **Turn off sync on this computer** forgets the key and folder here. Local data stays as it is.

---

## 6. UI: one Settings page, one dialog, one banner

### Settings → Sync (off)

```
┌ Sync ─────────────────────────────────────────────────────────┐
│ Keep your workspaces the same on your other computers,       │
│ through Google Drive, iCloud, OneDrive, Dropbox or any        │
│ folder. Encrypted, no account needed.                         │
│                                                               │
│                                        [ Turn on sync… ]      │
└───────────────────────────────────────────────────────────────┘
```

### Turn on sync… (a single small dialog)

```
┌ Turn on sync ─────────────────────────────────────────────────┐
│ Where should Sysslar keep the sync files?                     │
│   ◉ Google Drive                                              │
│   ○ iCloud Drive                                              │
│   ○ OneDrive                                                  │
│   ○ Choose another folder…                                    │
│                                                               │
│                                   [ Cancel ]  [ Continue ]    │
└───────────────────────────────────────────────────────────────┘
```

Then Sysslar checks the folder and shows **one** of two follow-ups. The user is never asked "is this
your first computer?":

```
 No sync files found (first computer)      Sync files found (another computer)
 ─────────────────────────────────────     ─────────────────────────────────────
 Your encryption key:                      Enter your encryption key:
  K7QF-29XM-H4TD-8RWA-NC3P-6YVB-…  [Copy]   [______________________________]
 Save it in a password manager.            Find it on any synced computer in
 You need it on your other computers.      Settings → Sync → Show key.
                        [ Done ]                                [ Continue ]
```

### Settings → Sync (on)

```
┌ Sync ─────────────────────────────────────────────────────────┐
│ Folder      Google Drive                         [ Change ]   │
│                                                               │
│ Workspaces                                                    │
│   ● Personal     [■ On ]   Synced 2 min ago                   │
│   ● Office       [  Off]                                      │
│   ● Side work    [  Off]   On Aadil's MacBook only            │
│                                                               │
│ Encryption key            [ Show key ]  [ Change key… ]       │
│ 🔒 Your synced data is locked before it leaves this computer. │
│    Stolen sync files can't be read without the key, which is  │
│    kept only on computers where you turn on sync.  Learn more │
│                                                               │
│ [ Sync now ]                    Turn off sync on this computer│
└───────────────────────────────────────────────────────────────┘
```

*Learn more* opens the full text from section 4.

### Everyday signals (main window)

- Under the workspace name next to the logo: a small sync dot. Hover shows `Synced 2 min ago`.
  The dot isn't shown for workspaces that don't sync.
- After a merge that changed something, the existing in-app note: `8 changes from Aadil's MacBook`
  with **Undo** (restores the backup taken just before that merge).
- **Only when something needs the user**, a banner across the top:
  - `Sync paused: the encryption key was changed on Aadil's MacBook.  [Enter new key]`
  - `Can't reach the sync folder (Google Drive). Your changes are saved here and will sync later.`
    This shows only after an hour, not for brief offline gaps.

That's the whole UI: one page, one dialog and one banner, plus the existing note system.

---

## 7. Timing and safety nets

| When | What |
|---|---|
| 20 s after the last change, on workspace switch, on quit | Write this computer's file (`.tmp` + rename) |
| On start, when the folder changes (5 s debounce), every 5 min | Read the other computers' files whose save number is new, merge in one SQLite transaction |
| Before the first merge each day | `BackupService` snapshot, which backs the *Undo* on the sync note |
| File can't be decrypted | Same key fingerprint: probably still downloading, retry, and report only after an hour. Other fingerprint: key-change flow |
| Delete markers and old blobs | Cleaned up once every computer's file shows it has read past them. A computer that hasn't written for 90 days is listed as *not seen recently*, with **Remove** |

## Threats and what covers them

| Threat | Covered by |
|---|---|
| Files stolen from the cloud or folder | AES-256-GCM; random file names; workspace and computer names are encrypted |
| A file is edited, cut off or swapped | GCM tags over header + every chunk; ids in the authenticated header |
| An old file is put back to undo changes | Per-computer save number check |
| Fake "key changed" file to pause sync | Key-change file must open with the current key |
| Lost or stolen laptop | Key protected by DPAPI/Keychain (as safe as the user account); **Change key** locks it out of everything newer |
| Missing or half-synced files | A missing record is never treated as a delete; only delete markers remove anything |
| Merge bug | Daily snapshot, *Undo* on every merge, recycle bin |

## Build plan

1. **Schema:** `SyncId`, `Clocks`, soft delete on all tables, `SyncDeletes`, clock service.
   No UI, so it can ship first.
2. **Merge engine:** a pure function `(local, incoming) → changes` with thorough unit tests: add,
   update, same-field clash, delete vs edit, restore, missing ≠ deleted, clock skew,
   recurring tasks, number clashes, first-sync matching.
3. **Sync files + crypto:** reuse `MigrationCrypto` chunks; `ISecretStore` for DPAPI and Keychain;
   cross-OS round-trip test in CI.
4. **Folder detection** (Google Drive, iCloud, OneDrive, Dropbox) and the watcher with a polling
   fallback.
5. **UI:** Sync page, turn-on dialog, per-workspace switches, sync dot, banner, merge note.
6. **Key change** flow, attachments as blobs, clean-up.

## Open questions

- Should the README's privacy section say "Sysslar never goes online; your own cloud app moves the
  encrypted sync files"? It should, once this ships.
