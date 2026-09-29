# Arcadia — Backup & Recovery

Everything here is local. Arcadia has no account and no cloud; a backup is a file you keep wherever you like.

## Where your data lives

| What | Where | In a backup |
|---|---|---|
| Library database (games, projects, documents, travel, calendar, tasks, notes, finance, automations) | `%LocalAppData%\Arcadia\Library\arcadia.db` | yes |
| Arcadia's media (covers, artwork, imported screenshots) | `…\Library\Media` | yes |
| Oracle history | `…\Library\Oracle\oracle-history.json` | yes |
| Settings | `%AppData%\Arcadia\settings.json` | yes (the data-folder choice of the machine you restore on is kept) |
| Saved API keys | `%AppData%\Arcadia\secrets.dat` | **no** — encrypted for this Windows user (DPAPI); re-enter keys after a restore on another PC |
| Caches (thumbnails, map tiles, downloaded artwork) | `…\Library\Cache` | no — rebuilt on demand |
| Logs | `…\Library\Logs` | no |
| Safety copies | `…\Library\Backups` | no |
| Files you added to projects/documents | where you keep them | only with "Include referenced files" |

The data folder can be moved during the first-run welcome. Uninstalling Arcadia never deletes it unless you answer
**Yes** to the uninstaller's explicit question (default: No).

## The `.arcadia-backup` format

A zip archive with a versioned `manifest.json`:

```json
{ "format": "arcadia-backup", "formatVersion": 1, "arcadiaVersion": "1.0.0", "createdUtc": "…",
  "databaseVersion": "20260928211215_Automation", "components": ["database","settings","media","oracle"],
  "includesReferencedFiles": false, "entries": [{ "path": "arcadia.db", "size": 794624, "sha256": "…" }], "references": [] }
```

Entries: `arcadia.db` (a consistent copy made with SQLite's online backup API — safe while Arcadia runs), `settings/settings.json`,
`media/…`, `oracle/…`, and with the option on, `references/<drive>/<path>` (e.g. `references/C/Users/me/invoice.pdf`).

## Creating a backup (Settings → Backup & recovery)

* **Back up now** writes `Arcadia backup <date time>.arcadia-backup` into the backup folder (default `Documents\Arcadia Backups`,
  changeable). Progress and stage are shown; **Cancel** stops it and removes the partial file.
* The archive is written as `*.partial`, then **re-opened and fully validated** (every checksum, the database's integrity
  check) before it is renamed and reported as saved. A backup that could not be restored is never reported as a backup.
* **Include referenced files** copies the documents and project files the library points to. The size it adds is shown
  before you back up ("Adds 312 files, about 1.4 GB"); missing files are skipped and counted.

## Automatic backups

Off by default. Choose **Daily**, **Weekly** or **After each Arcadia update**, and how many to keep (3/5/10/20).
Checked every 30 minutes; never while Gaming Mode is on or memory is under pressure. Only automatic archives
(`Arcadia auto …`) are pruned; backups you made by hand are never removed.

## Restoring

1. **Restore from backup…** → choose a file.
2. Arcadia validates it: it must be a zip with a readable manifest in a format this version understands; every entry must
   be listed, have the listed size and SHA-256; no unknown extra files; the database must pass `PRAGMA integrity_check`
   and must not come from a newer Arcadia (unknown migrations are refused).
3. You see when it was made, with which version, its size and what it contains, and a warning that it replaces the current
   library and settings.
4. **Restore and restart** (press twice). Arcadia first writes a **safety backup** of the current state
   (`Library\Backups\arcadia-before-restore-<time>.arcadia-backup`, newest three kept), extracts the archive to
   `Library\.restore\staged` (re-checking every checksum while extracting), and restarts.
5. On the next start — before settings or the database are opened — the current files are moved aside, the restored ones
   moved in, and the restored database is checked. On success the old files are removed (the safety backup remains).
   On any failure, or if the previous attempt was interrupted (power loss), **everything is moved back** and Arcadia starts
   with your previous data and says so.
6. Restored referenced files go to `Library\Restored files\<date>\…` and **never overwrite** your originals.

## Safety copies Arcadia makes on its own

* Before a database schema migration: `Backups\arcadia-before-<Migration>-<time>.db` (newest five).
* On the first start of a new Arcadia version: `Backups\arcadia-before-update-<version>-<time>.db` (newest five).
* Before a restore: `Backups\arcadia-before-restore-<time>.arcadia-backup` (newest three).

## Archive safety (for archives from elsewhere)

Only relative forward-slash entry names are accepted (no rooted paths, drive letters, `..`, `.` or trailing dots — Zip Slip);
at most 500,000 entries; the manifest is limited to 16 MB; entries over 64 MB with a compression ratio above 1000:1 are
refused (decompression bombs); every entry is read with a hard limit of its declared size; free disk space is checked before
extraction; nothing inside an archive is ever executed or opened.

## Check database / Clear cache / Reset settings (Settings → Diagnostics & maintenance)

* **Check database** runs SQLite's `integrity_check` and `foreign_key_check` on a read-only connection. It never repairs
  or rewrites anything; if it finds a problem, restore a backup (or keep a copy of the data folder) before continuing.
* **Clear cache** deletes thumbnails, downloaded artwork and map tiles only — they come back when needed.
* **Reset settings** restores default preferences and restarts. Library content, media, backups, saved API keys, the
  language and the data-folder choice are kept; the old file is saved as `settings.before-reset.json`.
