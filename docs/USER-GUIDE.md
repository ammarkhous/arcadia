# Arcadia 1.0 — User Guide

Arcadia is one quiet, fast place on your own PC for games, IT tools, projects, documents, travel, calendar, tasks, notes,
finances and automations. There is no account and no cloud: your library stays on this computer.

## First start

A short welcome asks five things: language (English or العربية — German UI is planned; the Oracle already understands
German), where to keep your library, how Arcadia should behave (Automatic, Normal, Power Saver) and whether to start with
Windows. Everything can be changed later in **Settings**. Arcadia starts with an empty library — add a game, a project or a
note and it becomes yours.

## Getting around

| Keys | What |
|---|---|
| Ctrl+K | Command palette: search everything, jump anywhere |
| Ctrl+Space | Oracle: type what you want in plain words ("tasks due today", "spent on groceries in 2025", "new note …") |
| Ctrl+Shift+N | Quick capture (a task, note or event from anywhere in Arcadia) |
| Ctrl+Shift+G | Gaming Mode on/off |
| Ctrl+Shift+H | Hide/show money amounts (finance privacy) |
| Ctrl+F | Focus the search box of the current page |
| Ctrl+N / Ctrl+S | New item / save on the current page |
| T M W D A | Calendar: Today, Month, Week, Day, Agenda |

Shortcuts can be changed in **Settings → Shortcuts & notifications**; a global "show Arcadia" hotkey is optional.

## The modules

* **Home** — the featured game, today's agenda, tasks due, next deadline, pinned note and your modules.
* **Games** — your library, play sessions (tracked automatically when a known game runs), progress, screenshots and journal.
  Online artwork and details are optional and off until you allow them.
* **IT Tools** — system information, performance, network, ping, trace, DNS, ports, storage, processes, services, event log,
  hashes, subnets, diagnostic sessions and reports. Local only.
* **Projects** — worlds of tasks, notes, links and files. Files stay where they are; Arcadia only references them.
* **Documents** — one library of referenced files with tags, projects, favourites and watched folders. Arcadia never moves,
  changes or deletes your files; removing a document removes only the reference.
* **Travel** — places, trips with day-by-day itineraries, journal, memories, bucket list, countries and a world map.
* **Calendar, Tasks, Notes** — events with reminders and recurrence, tasks with due dates and priorities, Markdown notes
  saved as you type (with revisions).
* **Finance** — accounts, transactions, transfers, budgets, bills, subscriptions, savings goals and reports. Exact decimal
  arithmetic; CSV import shows a preview and never changes amounts silently.
* **Oracle** — a deterministic assistant: it understands commands in English, German and Arabic, shows what it will do,
  and asks before changing anything. No AI service, no network.
* **Automation** — "when this happens, if these conditions, do that": files arriving, a game starting, a time, a task due…
  Automations never delete files, end processes, change the registry or run scripts.

## Gaming Mode and performance

When a game runs, Gaming Mode quiets Arcadia: background indexing and thumbnails pause, notifications wait, monitoring
slows down. Power Saver (automatic on battery in "Automatic" mode) reduces animations and pauses indexing.

## Backups

**Settings → Backup & recovery**: **Back up now** writes one verified `.arcadia-backup` file to a folder you choose;
automatic daily/weekly backups are available. **Restore from backup…** checks the file, shows what it contains, saves a
safety copy of your current data and restarts; if anything goes wrong, your previous data is put back. See `BACKUP.md`.

## When something is wrong

**Settings → Diagnostics & maintenance** shows versions, sizes and modes, and offers **Open logs**, **Open data folder**,
**Check database**, **Clear cache**, **Copy diagnostics** (versions and sizes only — no content) and **Reset settings**
(preferences only; your library is kept). Logs stay on this PC, are kept for 14 days (50 MB at most) and never contain note
text, amounts or keys.

## Updating and uninstalling

Install a newer `Arcadia-Setup-<version>-x64.exe` over the old one: it upgrades in place and your library is copied to
`Library\Backups` on the first start of the new version. Uninstalling keeps your data unless you explicitly choose to delete
it; your own files and documents are never touched.
