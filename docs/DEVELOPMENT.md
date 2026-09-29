# Arcadia — Development Log

## Installer (2026-09-27)

`installer\build-installer.ps1` publishes a self-contained Release build (no .NET install needed on the target PC) and wraps it with Inno Setup 6
(`installer\Arcadia.iss`; install the compiler with `winget install JRSoftware.InnoSetup`). Output: `dist\Arcadia-Setup-<version>-x64.exe` plus a `.sha256` file. Release steps and checklist: `docs/RELEASE.md`.
Per-user install without admin rights (all-users optional), Windows 10 1809+ x64, Start menu + optional desktop shortcut, English/Arabic/German setup.
Uninstall keeps the user's data unless they choose to delete it (`%LocalAppData%\Arcadia\Library` and `%AppData%\Arcadia`); referenced documents are never touched.
The installer is not code-signed, so Windows SmartScreen warns on first run. Verified: silent install, launch, silent uninstall.

## Phase 10 — Final polish, performance, reliability, backup, packaging & Arcadia 1.0 (2026-09-29)

No new modules. The phase measured first (`PERFORMANCE.md`), fixed what the measurements showed, added data safety
(`BACKUP.md`) and packaging (`RELEASE.md`), and wrote the user guide (`USER-GUIDE.md`).

### Performance and responsiveness (numbers in PERFORMANCE.md)

* **Startup.** Background services (tracking, watchers, reminders, automation) used to start inline when the database was
  already ready — on a warm start that ran on the UI thread before the first frame. They now start after the first frame
  (`StartBackgroundServicesAsync(…, firstFrame)`). Home's Today strip ran its five queries on the UI thread (Microsoft.Data.Sqlite
  "async" methods complete synchronously); they now run in one `Task.Run`. Warm first frame 2.0 s → 1.2 s.
* **Search index.** FTS5 rows now have deterministic rowids `(kindCode << 32) | id`, so upserts/deletes use the rowid B-tree
  instead of scanning the UNINDEXED `kind/entity_id` columns (O(n) per save, O(n²) per rebuild). Rebuild of 25k entries:
  145 s → 3.2 s, in one transaction. Indexes from earlier versions are re-keyed once at startup (`RekeyLegacyRowsAsync`,
  keeps extracted document text). `MatchIdsAsync` returns ids without snippets for list filters (notes "about …" 380 ms → fast).
* **Global search** runs its providers on a worker thread (was the caller's/UI thread).
* **Lazy sections.** `LazySection` creates a page section the first time it is shown. Travel built all nine sections
  (Collapsed still generated ~12,000 visuals for 200 trips + timeline): first visit 2.5 s → 0.4 s. Calendar's Today/Month/Agenda too.
* Projects list shows 40 rows + "Show all N projects"; Documents' rail lists only projects that hold documents;
  the world map reuses frozen brushes/pens and cached cluster labels.
* QA hooks: `ARCADIA_QA_STARTUP=1` (UI work until the first frame), `ARCADIA_QA_STALLS=1` (every UI-thread operation ≥ 50 ms
  for the session, with the heaviest lists by visual count). Scripts: `baseline.ps1`, `startup.ps1`, `stalls.ps1`.
* Large-data library: `tests/StressDataGenerator.cs` (gated by `ARCADIA_STRESS_DB`): 1,000 games, 10,000 tasks, 5,000 notes,
  5,000 transactions, 1,000 documents, 300 projects, 200 trips, 500 places, ~2,500 events over five years + 40 series.

### Reliability

* Navigation leak test (25 loops over ten modules): working set plateaus (~418 MB), handles fall — pages are cached, no growth.
* `async void` audit: every handler either catches or goes through `UiSafe.Run` (log + quiet notification).
  Fire-and-forget faults are logged by the global `UnobservedTaskException` handler.
* Logs bounded: 14 days, 50 MB total, 20 MB per day (`FileLoggerProvider.Prune`).
* Window placement is remembered (`WindowsSettings.Placement`) and only restored when the title bar lands on a connected monitor.

### Data safety

* `.arcadia-backup` archives (`ArcadiaBackupService`), validation, staged restore applied before startup with rollback
  (`BackupRestorer`), automatic backups (`AutomaticBackupService`, never during Gaming Mode), Settings → Backup & recovery,
  Diagnostics & maintenance (Check database, Clear cache, Copy diagnostics, Reset settings). See BACKUP.md.
* First start of a new version copies the database (`BackupBeforeUpdateAsync`, `AppSettings.LastRunVersion`).
* The old folder snapshot (`LocalBackupService`) and its panel were removed.

### First run

`Shell/WelcomeWindow` (Welcome → Language → Data location → Performance → Start with Windows → Enter). Shown before the data
folder is created; an existing library skips it (`FirstRunCompleted` set silently). German UI is not available yet — the
option is shown disabled with a note (the Oracle already understands German).

### Release

Version 1.0.0 (`VersionPrefix`, `AppInfo.Version`); release builds never seed sample data; the "anonymous diagnostics (future)"
toggle was removed (there is no telemetry). Installer: `build-installer.ps1 [-Suffix rc.1]`, checksum file, version info.

### Oracle polish

Years after a time word are periods ("trips in 2024", "during 2025", "im Jahr 2024", "في 2024"); "what's next" is a question
(→ agenda). Per-command timing is logged only in QA runs.

### Tests

518 (was 472): backup archive format/guards/restore/rollback/interrupted restore/references/pruning, log retention, search
index keys and re-key, release defaults, automatic backup schedule, command-line hardening, update backup, Oracle years.
End-to-end `e2e-p10.ps1`: first run in an empty folder → backup through the UI → tampered archive refused → restore → restart
→ data back, safety backup present, log clean. P7/P8/P9 end-to-end scripts still pass.

### Known limitations

* Not code-signed (SmartScreen warns once). German UI not translated. The Projects list is paged, not virtualized.
* Referenced-file backups copy files, not folders; restored copies go to `Restored files`, never to the original paths.
* Clean-machine, laptop and desktop tests must be run by the owner (no Windows Sandbox on the development PC) — see RELEASE.md.

## Phase 9 — Automation, Workflows, Rules & Deep Windows Integration (2026-09-29)

Arcadia now reacts to local events: **WHEN** something happens, **IF** optional conditions hold, **THEN** it performs Arcadia
actions. Deterministic and local: no AI, no cloud, no IFTTT/Zapier, no scripts, no command lines. A new **Automation** area
(the "control room"), automatic playtime with executable association, Gaming Mode / Power Saver, tray, Windows startup,
notifications, shortcuts and workspace restore. All Phase 1–8 data is kept (one additive migration; 472 tests green).

### Architecture
- **`Arcadia.Core/Automation`** (UI-free, tested):
  - `AutomationModel.cs`: `AutomationEntity`, `AutomationRunEntity` (activity log), `WorkSession`, `AutomationDefinition`
    (entity ⇄ definition; steps are string-only JSON), `AutomationStep` (type + string settings), `AutomationEvent`,
    `RunDetails`, `IAutomationStore`, `IWorkSessionStore`.
  - `AutomationRegistry.cs`: `FieldSpec` (the editor schema, incl. `ShowWhen`), `TriggerDescriptor` / `ConditionDescriptor` /
    `ActionDescriptor` (id, category, icon, fields, validation, match/evaluate/execute), `IAutomationHost`, the registry.
  - `AutomationCatalog.cs`: every built-in trigger, condition and action (no switch statements).
  - `AutomationEngine.cs`: the engine and `ScheduleMath` (on the Phase 6 `Recurrence` rules — no second scheduler).
  - `AutomationTransfer.cs`: export/import (`.arcadia-automation`), `IAutomationService` (for Oracle and other modules).
- **`Arcadia.Data`**: `AutomationStore`, `WorkSessionStore`; migration **`Automation`** (additive: `Automations`,
  `AutomationRuns`, `WorkSessions` tables; `Games.Executables`, `Games.AutoGamingMode`).
- **`Arcadia.App/Services`**: `AutomationServices.cs` (`AutomationService`: engine host, trigger sources, work sessions),
  `PerformanceServices.cs` (Gaming Mode, Power Saver, memory pressure), `WindowsIntegration.cs` (`TrayService`,
  `ArcadiaNotifier`, `StartupRegistration`, `ShortcutManager`), `DesktopIntegration.cs` (tray menu/status, minimize/start to
  tray, global hotkey); `SessionTrackingService` extended (executables, sleep, game events).
- **UI**: `ViewModels/Automation`, `Views/Automation/AutomationView.xaml` (tabs: My automations + editor, Templates,
  Activity, Triggers, Sessions, Engine), game editor (executables, per-game Gaming Mode), Settings (Windows & performance,
  Shortcuts & notifications), palette commands, Oracle commands, new icon **Automation**.

### Triggers (registry ids)
`process.started`, `process.stopped` (executable) · `game.started`, `game.stopped` (a game or any; context: game, start, end,
minutes) · `schedule` (daily / weekly on days / monthly on a day / once, at HH:mm) · `app.started`, `app.closing` ·
`file.created`, `file.changed` (a user-chosen folder, optional extensions and subfolders) · `task.completed` (optional project) ·
`project.status` (optional project, new status) · `trip.started`, `trip.ended` (date reached — no location tracking) ·
`event.upcoming` (N minutes before a calendar event) · `budget.threshold` (percent), `bill.due` (N days before),
`goal.completed`. Finance triggers only notify or create tasks/notes; nothing ever creates a transaction.

### Conditions
`time.between` (after/before, overnight windows work) · `weekday` · `gaming` (on/off) · `process.running` (or not) ·
`file.extension` · `game.is` · `project.is` · `power` (plugged in / battery) · `network` (online/offline).
One level of grouping: **ALL** or **ANY**.

### Actions and safety
| Action | Safety |
|---|---|
| notify, open.section, open.today, open.entity (arcadia:// link or the event's record), quickcapture | Safe |
| task.create, note.create (linked to the event's record) | Safe (allowed while closing) |
| gaming.on / gaming.off, game.playing, game.session.start / stop | Safe |
| work.start / work.stop (project work session) | Safe |
| ping (diagnostic → notification), it.open (IT tool) | Safe |
| file.open, folder.open, document.add (a reference in Documents; the file is never modified) | Safe |
| project.status | **Confirm** — asks with a dialog when Arcadia is in front and no game runs; otherwise it is not done (Blocked + a notification) |

**Restricted actions cannot be registered** (`AutomationRegistry.Add` throws). There is no action or host member that deletes
files, kills processes, edits the registry, changes services, runs PowerShell/CMD/scripts or anything remote. `file.open`
refuses programs and scripts (.exe, .bat, .ps1, .lnk, .msi…). Automation never creates financial transactions.

### Execution engine
`RaiseAsync(event)` → for each enabled automation of that trigger type: trigger `Matches` → **duplicate suppression** (same
automation + same event data within 3 s) → **cooldown** (optional, per automation) → **once-per-occurrence keys** (a bill's due
date, a trip's start, a schedule slot never fire twice — checked in the log, kept when the log is pruned) → **loop
prevention** → conditions → actions in order. A run is written as *Running* before the first action, so a crash leaves an
**Incomplete** record that is never re-run (startup marks them). An automation stops at its first failing action; other
automations are unaffected. **TEST** runs the automation against a sample event from the trigger's descriptor and records it
as a *simulation* (badge in the log; not counted in run statistics).

**Loop prevention**: actions run inside an execution chain (`AsyncLocal`). Events raised while an action runs carry the chain;
an automation already in the chain, or a chain deeper than 4, is **Blocked** and logged (`Automation loop prevented: A → B → A`).

### Trigger sources (zero-work idle)
Sources exist only while an enabled automation needs them (`Engine.ActiveTriggerTypes()`; paused/disabled → nothing runs):
- **Processes**: a Toolhelp name snapshot every 4 s (8 s in Gaming Mode/Power Saver), only for the executables automations
  name; processes already running when watching starts are the baseline (they did not "start" now).
- **Files**: one `FileSystemWatcher` per folder in use; created/renamed/changed events settle for 1.5 s and are reported once
  per file (temporary downloads `.crdownload/.part/.tmp` ignored; a rename into place counts as created); watcher errors
  recreate the watcher; a missing folder is logged and retried on reload.
- **Clock**: one one-shot timer to the next moment (next schedule slot, next event lead time, next midnight for date checks),
  capped at 6 h; re-planned on resume, clock/time-zone change (`SystemEvents.TimeChanged`) and data changes. A slot missed by
  more than 15 minutes (asleep, closed, clock jumped) is **skipped and logged**, never run late; DST gaps move to the first
  valid time (tested with W. Europe time).
- **Data**: task completion and project status from Arcadia's change messages (no polling); trips/bills/budgets/goals once a
  day and when that data changes.
- **Games**: `SessionTrackingService` events.

### Game detection and automatic playtime
- A game may have **executables** (game editor: *Add program…*, *Detect running process*, remove, *Use full path*): a name
  matches in any folder (Steam, GOG, Epic, standalone), a full path matches one program (emulators). Without executables the
  Phase 3 install-folder detection applies. Per game: *Turn on Gaming Mode while this game runs*.
- Start time = the game process's own start (correct when Arcadia opens after the game started). Multiple processes of one
  game are one session; launcher hand-offs and a **relaunch within 30 s** continue the same session.
- **Crash / close**: the session ends at the last time the process was seen. **Arcadia restart**: the heartbeat
  (`tracking.json`, every minute) resumes the session; if it is older than 10 minutes the session is closed at the heartbeat
  and a still-running game starts a new one — unknown time is never counted twice or guessed.
- **Sleep**: on suspend the session is recorded up to that moment; after resume a still-running game starts a new session from
  the resume time. Sleep time is never counted (`PlaySessionTracker.Suspend/Resumed`, tested).
- Game start/stop drive Gaming Mode, the `game.started/stopped` triggers and an optional *session note* offer (a notification
  that opens the game's journal; never forced, never interrupts a full-screen game).

### Project work sessions & Session Center
Manual (Automation → Sessions, palette) or by automation (e.g. WHEN devenv.exe starts THEN start work on project X — configured,
never inferred). One open session; a 5-minute heartbeat bounds crash recovery (older than 10 min → closed at the heartbeat);
sleep splits the session. The Session Center shows the running game, the work session (stop with an optional note), recent
work and the performance state. Diagnostic sessions (IT Tools) stay as they are.

### Gaming Mode, Power Saver, resources
- **Gaming Mode**: manual (tray, palette `Ctrl+Shift+G`, Automation header, Oracle), per game, for every detected game
  (setting), or by automation. A game (or automation) turns it off after the grace period (default 60 s) unless another game
  runs; switched on by hand it stays until switched off. While on: document folder re-scans and thumbnail generation pause,
  monitoring slows (session scans 5 s, process triggers 8 s), and Windows notifications are quiet (only reminders and warnings).
- **Power Saver**: Normal / Power Saver / Automatic (on battery, from `SystemEvents.PowerModeChanged`): pauses indexing, slows
  monitoring, fewer animations.
- **Memory pressure**: one `GlobalMemoryStatusEx` call every 2 minutes; at ≥ 90 % in use the decoded-image cache is trimmed and
  thumbnails pause. No forced garbage collection.
- The header clock only ticks while the window is visible; the "Now playing" pulse only animates while a game plays and the
  window is shown (it previously animated invisibly — the main cause of idle CPU when minimized).

### Windows integration
- **Tray** (Shell_NotifyIcon on a message-only window, no WinForms): click opens Arcadia; menu (Arcadia-styled): Open, Today,
  Quick Capture, Oracle, Gaming Mode ✓, Power Saver ✓, Pause automations ✓, Exit. The icon carries a small static mark: cyan
  dot = Gaming Mode, amber bars = automations paused. Explorer restarts re-add it.
- **Start with Windows** (default off): the per-user `Run` entry, written/removed only by that switch; starts with
  `--minimized` (to the tray, or minimized when the tray icon is off). **Minimize to tray** optional.
- **Notifications** (`ArcadiaNotifier`): one service with categories Info / Reminder / Automation / Gaming / Warning, each
  *In Arcadia*, *Windows notification when Arcadia is in the background*, or *Off*; the same message within a minute is merged;
  at most 4 Windows notifications per minute. (Reminders keep their Phase 6 presenter.)
- **Shortcuts** (`ShortcutManager`): palette, Quick Capture, Gaming Mode and Oracle (Oracle panel) are configurable; conflicts
  with each other or with fixed shortcuts (Ctrl+N/S/F, Ctrl+Shift+H) are refused in Settings. The only system-wide hotkey is the
  optional **Show Arcadia** (off by default); if another program owns it, Arcadia says so.
- **Deep links**: `arcadia://game|project|trip|place|task|note|event|document|journal|transaction|account|budget|recurring|goal|automation/{id}`
  route inside Arcadia (automation links open the editor). No OS protocol handler is registered.
- **Restore last workspace** (default off) and **recent workspaces** (12, shown when the palette opens empty).
- **Explorer integration — extension point only**: a future shell verb would launch Arcadia with a path; `DocumentActions.AddPathsAsync`
  and the `document.add` action are the entry points. No shell extension is installed.

### Oracle integration
Commands: *show automations*, *show failed automations*, *what automation ran last*, *how many automations*, *open
automations*, *pause/resume automations*, *is gaming mode on?*, *enable/disable gaming mode* (EN/DE/AR vocabulary).
Creation from text is a deliberately small grammar — `when <game or program.exe> starts|closes <enable/disable gaming mode |
show notification … | open today | mark as playing>` (+ German *wenn … startet …*, Arabic *عندما يبدأ …*). It always produces a
**preview** (WHEN / THEN, CONFIRM / EDIT / CANCEL); EDIT opens the automation editor with the draft; creation goes through the
same validation (Oracle cannot bypass automation safety). "disable" switches only modes and automations; for anything else it
is still refused, as are delete/kill/format…

### Export / import
`.arcadia-automation` = JSON `{ format: "arcadia-automation", version: 1, exportedAt, automations: [{ name, description,
trigger: { type, settings }, conditionMode, conditions: [...], actions: [...], cooldownSeconds, tags }] }`. Import parses into
string-only DTOs (never type names), rejects unknown formats/versions, malformed files, files over 512 KB, unknown or
unregistered trigger/condition/action types and invalid settings; accepted automations are saved **disabled** for review and
never run on import.

### Backup
Automations and work sessions live in the database, so every local backup (Phase 1) includes them; the activity log is in the
database too but bounded (default 30 days / 2,000 runs; runs that guard once-per-occurrence triggers are kept) — it is not
meant as a permanent record.

### Performance (Debug build, p9 library, 60 s windows after a 15 s settle)
| State | Working set | Private | CPU | GPU |
|---|---|---|---|---|
| Idle, window visible (4 automations) | 195 MB | 70 MB | 141 ms/min (0.06 %) | 0 % |
| Idle, minimized | 187 MB | 59 MB | 62 ms/min (0.03 %) | 0 % |
| Minimized + game running (Gaming Mode) | 171 MB | 62 MB | 16 ms/min (0.01 %) | 0 % |
| Minimized + 3 process-trigger automations | 191 MB | 62 MB | 47 ms/min (0.02 %) | 0 % |
Before the halo fix, minimized idle was 2.9 s/min. Handles stay flat. See the long-run test below.

### Tests
- `AutomationEngineTests` (12): matching and action order, disabled/paused, ALL/ANY, cooldown + duplicates, once-per-occurrence,
  loop prevention (A → B → A blocked), failure isolation, Confirm actions and the closing rule, labelled simulation, crash →
  Incomplete, cancellation, registry safety (restricted refused, no dangerous ids, validation), import (disabled, unknown
  actions rejected, malformed/foreign/version), schedules (daily/weekly/monthly/last-day/once/DST gap).
- `TrackerPhase9Tests` (2): executable association by name/path, Arcadia opened after the game, relaunch grace, crash end,
  duplicate names in other folders; sleep not counted, stale restore not double-counted.
- `AutomationStoreTests` (3), `AutomationMigrationTests` (Phase 7 library → Automation migration keeps games/finance, backup
  taken), `OracleAutomationTests` (3: list/pause/resume/last run; Gaming Mode status and switches, "disable windows defender"
  still refused; text → preview → confirm, unknown actions not guessed, Arabic).
- UI automation `e2e-p9.ps1` (§92–95): morning automation evaluated on start; an automation built in the editor, TESTed
  (simulation, condition detail) with Open Today executed; PDF watcher: one run for a chunked write, `.txt` skipped, finished
  download (rename) once; stand-in game (renamed process) recognized by executable → automation marks Playing, Gaming Mode on,
  Session Center shows it; after 65 s the process ends → session recorded (1 min, automatic), note offer, Gaming Mode off after
  the grace; Oracle: list, pause, status, "when Elden Ring starts enable gaming mode" preview → confirm; restart with
  `--minimized` → tray, global hotkey shows the window, automations and the session persisted (no double count). **PASS**.
- Long-run churn test (`longrun-p9.ps1`: a chunked PDF every 30 s, a watched process started/stopped every minute, Gaming Mode
  toggled by automations): stopped after ~7.5 of 20 minutes because the development PC (6 GB RAM) ran low on memory — not
  because of Arcadia. Up to that point: 15 PDFs → exactly 15 runs, 8 process starts → 8 "on" / 7 "off" runs (the 8th still
  running), no errors, no loops; the per-minute memory/handle samples were not captured. A full-length run is still to be done.

### Known limitations
- Process triggers see programs by executable name (no command-line arguments, no per-user filtering); detection latency is
  up to 4 s (8 s while gaming).
- Sleep and restart handling is exact for sessions Arcadia observes; time while Arcadia was not running is not counted once
  the last heartbeat is older than 10 minutes.
- Schedules only run while Arcadia runs (no Windows service or scheduled task is installed); missed slots are skipped.
- Confirm-level actions are not performed while Arcadia is in the background or a game runs (they are logged as Blocked).
- The tray menu and Windows notifications use the system notification area; on systems where it is disabled, notifications stay
  in Arcadia.
- No Explorer context-menu integration yet (extension point documented).

### How to add a trigger, condition or action
1. Add a descriptor in `AutomationCatalog` (or call `registry.Add` from a module): id, category, icon, `FieldSpec`s, and
   `Matches`/`Sample` (trigger), `Evaluate` (condition) or `Execute` with a `Safety` (action; Restricted is refused).
2. A new trigger also needs a source that raises `AutomationEvent`s — add it to `AutomationService.ConfigureSources` and keep
   it dormant unless `Engine.ActiveTriggerTypes()` contains its type.
3. Actions reach the app only through `IAutomationHost`; add a member there if a new capability is needed (never a destructive one).
4. Strings: `Auto_Trigger_*` / `Auto_Condition_*` / `Auto_Action_*` / `Auto_Field_*` / `Auto_Choice_*` (EN + AR), and a test.

### Recommended Phase 10
Not started. Suggested: a unified backup/restore & sync-free portability pass (encrypted export of the whole library,
restore wizard, integrity checks), plus optional Explorer "Add to Arcadia" integration using the documented extension point.

## Phase 8 — Oracle: deterministic command system (2026-09-28)

The sidebar's old "AI Assistant" placeholder is now **Oracle** (section key `oracle`, new *Oracle* icon: a diamond lens
with an eye). Oracle understands short commands in plain English, German and Arabic and answers with structured, clickable
results. **It is not an LLM and not "AI"**: no model, no embeddings, no ML, no network, no API keys, no tokens, no downloads.
A command is matched by a data-driven vocabulary and rules, fully offline and in well under a millisecond. Phase 1–7 data
is unchanged (no migration); 449 tests green, Phase 7 e2e green.

### Where it lives
- **`Arcadia.Core/Oracle`** — the whole engine, UI-free and testable:
  `OracleModel.cs` (request, intents, entities, context, actions, items, previews, results, trace),
  `OracleText.cs` (normaliser, tokenizer, folding, conservative fuzzy name matching),
  `Lexicon/en.txt · de.txt · ar.txt` (embedded vocabulary, one line per meaning), `OracleLexicon.cs` (phrase matcher,
  language detection), `OracleParser.cs` (concepts → structured `OracleParse`: verb, entities, filters, period, money,
  host/port, subject/target/topic), `OracleEngine.cs` (command base, registry, scoring, confidence, follow-ups, change
  confirmation, name index), `OracleServices.cs` (the service interfaces handlers may use),
  `Commands/*.cs` (44 commands: common, games, organizer, documents & travel, finance, IT tools) and `OracleRegistry.cs`.
- **`Arcadia.App`** — `Services/OracleServices.cs` (`AppOracleEnvironment`: localized templates, money/date formats,
  confirmation level; `OracleHost`: lazy engine, shared context, index invalidation, history file),
  `ViewModels/Oracle/OracleConsoleViewModel.cs` (shared by page and overlay; action executor),
  `Views/Oracle/OracleView.xaml` (the page) and `OracleResultView.xaml` (the rich result renderer used by both),
  the overlay in `Shell/MainWindow.xaml`, the Oracle panel in Settings.

### Pipeline (each stage is a pure function or a small class with tests)
1. **Normalise** (`OracleText.Clean`): NFKC, quotes, Arabic punctuation, tatweel/zero-width, spacing — for matching only.
   The original text is kept: quoted text or everything after `word:` becomes an exact **payload**
   (`add note to Cyberpunk: Finish Panam missions`); paths, URLs, money, dates and names are never altered.
2. **Tokenize**: words, numbers, money (`€12.50`, `35 EUR`), IPs, hosts (not file names like `invoice.pdf`), dates
   (`2026-10-12`, `12.10.`, `03/04`), times (`15:00`, `3pm`, `at 10`, `um 18 Uhr`, `الساعة 9`), percentages. `Fold()` gives
   the comparison form (case, accents/umlauts, `ß→ss`, Arabic hamza/alef/ta-marbuta/harakat).
3. **Language detection** (deterministic): Arabic script → Arabic; more German-only than English-only known words (or
   umlauts) → German; else English. Settings → Oracle → *Command language* can force one.
4. **Vocabulary** (`OracleLexicon`): longest phrase first in the detected language, then English. Arabic prefixes
   (وال، بال، لل، ال، و، ب، ل) and a possessive ي are tried. Long single words (≥ 6 letters) tolerate one typo — flagged,
   which lowers the confidence. Ambiguous words have several meanings and position decides:
   `open games` (verb) vs `show open tasks` (filter); `favorite games` (filter) vs `favorite Hades` (verb); `next Friday`.
5. **Parse** (`OracleParser`): verb, question, entities in order, filters, module, tool, metric, **period**, time, amount,
   currency, host/port, numbers; free words split into *subject* (`open Cyberpunk`), *target* (after for/to/in/on:
   `tasks for Home Server`, with the entity word that came with it) and *topic* (after about/tagged), plus each run of free
   words as a *segment*. Filler words inside a name are kept (`call the bank`); vocabulary inside a name after a
   preposition stays part of the name (`Home Server` is not the Home module).
6. **Dates**: today / tomorrow / yesterday, this/next/last week · month · year, weekend, weekdays (`Friday` = this coming
   one, `next Friday` = Friday of next week, `last Friday`), `in 3 days`, `within 7 days`, ISO and German dates. A slash
   date that reads two ways (`03/04`) is **never guessed**: Oracle asks with both readings as one-click commands.
7. **Context** (`OracleContext`): the module and the record the current page shows (project, trip, game), the *focus*
   (the record the last result was about), and the last refinable query. `its` / `this` resolve to the focus, else the page.
   Context expires after 10 minutes, when the module changes, or on `reset`.
8. **Match & confidence**: every registered command scores the parse (0–1). Free words a command does not use and typo-
   corrected vocabulary lower the score. **High ≥ 0.75, Medium ≥ 0.5, Low** otherwise; a result can lower it further
   (fuzzy name → Medium). Below 0.35 → *"I couldn't match that to an Arcadia command."* with suggestions for the page.
9. **Follow-ups**: a command without its own verb/entity but with filters, a period or `only …` refines the last list
   (`show open tasks` → `only high priority` → `this week`); a new status filter replaces the old one.
10. **Resolve names** (`OracleEntityIndex`): one strong match (≥ 0.85, or an exact name that beats all others) is acted on;
    several → "Which one?" with each choice as an exact command; only plausible matches (≥ 0.6) → "Did you mean…", never
    acted on. Games, projects, trips, places, accounts, goals and bills are cached as small name lists (lazy, dropped when
    Arcadia reports a change, 2-minute TTL); tasks, notes, documents and events are searched on demand.
11. **Plan → confirm → execute** through existing services only; **12. Result** (structured, templated);
    **13. Render** (`OracleResultView`) and the **trace** (developer diagnostics).

### Intents, entities, commands
Intents: Navigate, Show, Search, Count, Calculate, Open, Create, Update, Diagnose, Help, Reset, Refuse.
Entities: game, project, task, note, document, trip, place, event, account, transaction, budget, subscription, bill,
savings goal, diagnostic session, IT tool, module (+ screenshot, journal, bucket list, activity, category as tabs/targets).
Commands are small classes (`OracleCommand`: id, module, intent, entities, safety, examples per language, `Match`,
`ExecuteAsync`) — no giant switch. Help and suggestions are generated from the registry.

| Module | Examples |
|---|---|
| Navigation | `open finance`, `go to calendar`, `open subscriptions`, `open IT tools`, `öffne Finanzen`, `افتح التقويم` |
| Any record | `open Cyberpunk`, `open project Home Server`, `Cyberpunk journal`, `find router`, `everything related to Home Server` |
| Games | `show unfinished games`, `which games am I playing`, `how many games have I completed`, `games not played recently`, `favorite games`, `gaming stats`, `mark Cyberpunk as completed`, `favorite Hades` |
| Projects | `show active projects`, `projects due this month`, `create project Garden Shed`, `set project Home Server to on hold`, `pin project Home Server`, `show activity for Home Server` |
| Tasks | `show overdue tasks`, `tasks for today`, `show tasks for Home Server`, `tasks due within 7 days`, `add task buy cables tomorrow`, `remind me to call the bank Friday at 10`, `complete task label ports`, `move task label ports to Friday` |
| Notes | `show notes about router`, `notes tagged network`, `add note to Cyberpunk: …` (a game journal entry), `new note: …` |
| Calendar | `what's on tomorrow`, `show events this week`, `create event dentist tomorrow at 10`, `when is the dentist`, `when is my Italy trip` |
| Documents | `show recent documents`, `missing files`, `show documents for Home Server`, `show its documents`, `open document …`, `open folder of …`, `copy path of …`, `link … to Home Server` |
| Travel | `show upcoming trips`, `places I want to visit`, `how many countries have I visited`, `plan trip to Japan next month`, `mark Rome as visited`, `add Rome to Italy trip`, `add Northern Lights to bucket list`, `add journal entry to Italy trip: …`, `travel stats` |
| Finance | `how much did I spend this month`, `spending on groceries last month`, `income this year`, `net this month`, `largest expenses this month`, `show transactions this week`, `expenses for Italy trip`, `show my subscriptions`, `upcoming bills`, `budget status`, `show savings goals`, `account balances`, `I spent 35 euro on groceries` |
| IT tools (read-only) | `ping 192.168.1.1`, `dns lookup google.com`, `test port 443 on google.com`, `traceroute google.com` (opens the tool), `system info`, `show my ip address`, `disk space`, `top processes` |
| Oracle | `help`, `help finance`, `reset` |

### Safety
- **Nothing destructive exists in Oracle.** Delete/remove/kill/terminate/format/wipe/uninstall/disable (and the German and
  Arabic words) are recognised only to be **refused** with an explanation; the registry has no command with a destructive
  safety level (test: `Registry_has_no_destructive_command`, `Destructive_commands_are_refused`).
- IT tools are read-only: Oracle receives a *process listing function*, not `IProcessService`, so it cannot end a process;
  no services, registry, event-log clearing or file deletion.
- **Confirmation** (Settings → Oracle): *Smart* (default) previews every create and every change to a record that was not
  named exactly; an exactly named, reversible change (status, pin, favorite, complete) runs directly. *Always* previews
  everything; *Minimal* runs creates directly — except **finance**, which always previews.
- **Finance entries** always show a preview (amount, description, category, account, date). The account must be named
  (`from Visa`), be the default account, or be the only account — **never guessed**; otherwise *Confirm* is disabled and
  *Edit* opens the normal transaction editor with the draft. Different currencies are shown per currency, never converted.
- Weak fuzzy matches are never acted on; changes only happen on a single strong match.
- Results use Finance privacy mode: amounts are masked while it is on.

### Responses and results
Responses are templates (`Orc_*` resource keys, English and Arabic, in the interface language; German input is answered in
the interface language). A result is typed (List, Metric, Preview, Clarify, Unknown, Help, Tool, Refused, Error, Navigated,
Done) and carries items with icon/accent/value/progress and compact actions (Open, Journal, Complete, Open file, Show in
folder, Copy path, Ping…), metric tiles, a preview card with **CONFIRM / EDIT / CANCEL**, suggestions and a confidence badge.

### UI
- **Overlay**: Ctrl+Space anywhere in the Arcadia window (alternatives: Ctrl+Shift+Space, Ctrl+Shift+O, Ctrl+Alt+Space).
  Prompt, latest result, key hints; Esc closes; navigation results close it and open the page/record.
- **Oracle page**: terminal-style transcript (last 40 results), prompt at the bottom, rail with context (module · page ·
  focus, Reset), suggestions for the current page, recent commands (click to rerun), optional unmatched-command list.
- **Keyboard**: Enter runs; ↑/↓ walk the history; Tab jumps into the newest result (rows are focusable, Enter runs the
  row's primary action); Esc clears (page) / closes (overlay).
- **Arabic**: full RTL layout; command lines (console text) stay left-to-right so mixed English/Arabic commands read correctly.
- The input is designed for any front end (`OracleRequest`); a speech front end could call the same engine later.

### Settings, persistence, privacy
Settings → Oracle: Enable, Shortcut, Command language, Confirmation, History limit, Keep history, Show suggestions,
Developer diagnostics (shows the parse trace — input, normalised text, language, concepts, intent, entity, parameters,
context, confidence/score, handler, parse and total time — under each result), Collect unmatched commands (off by default),
Clear history. History and unmatched commands are a small bounded JSON file, `Library\Oracle\oracle-history.json`
(atomic write); **no database change, no migration**. Nothing is sent anywhere.

### Performance
- Nothing at startup: the engine, vocabulary (≈1,170 phrases in three languages) and name index are created on first use.
- Latency (in-memory library, 227 example commands, warm): parse avg 0.04 ms; whole command avg 0.6 ms, p95 1.7 ms,
  max 4.3 ms. Real library: instant.
- Idle (p7 library, 30 s after 20 s settle): Home without Oracle 47–78 ms CPU, Oracle page after 20 commands 62 ms,
  overlay left open 109 ms; working set +50 MB with a full 20-result transcript (bounded at 40).
- A focused text box keeps WPF's caret animation — and the render loop — running (measured: ~600 ms / 30 s on the page).
  The Oracle prompt therefore hands focus to its page after 15 s without typing; the next key goes straight back into the
  prompt. The stock `ProgressBar` (endless glow animation) is not used in results.

### Tests
- `OracleParserTests` (30): normaliser/payload preservation, verb/entity/filter in EN/DE/AR, ambiguous words, language
  detection and override, periods (today … next month, weekdays, next Friday, within N days, ISO/German dates), ambiguous
  slash dates, money/host/port/time, subject/target/topic, follow-up detection, destructive words, typo tolerance,
  conservative fuzzy names.
- `OracleEngineTests` (29, real stores on in-memory SQLite): game/task queries, follow-ups, focus + `its` + expiry,
  conservative name resolution (typo → Medium, ambiguity → choices, tabs), exact vs fuzzy updates and confirmation levels,
  refusals leave data untouched, registry safety, creates preview first then apply (task, reminder, game journal),
  ambiguous dates, agenda/when/navigation/travel/related, finance answers and the never-guess-the-account rule, IT tools,
  the same engine in three languages, unknown/help, **every help example runs**, parse speed.
- UI automation `e2e-p8.ps1` (integration flow): Ctrl+Space → query → rich results → follow-up → open by name (overlay
  closes, page opens) → create preview → CONFIRM → verified in the database → expense preview with named account → no
  account: confirm disabled → refusal (nothing deleted) → unknown → German and Arabic → Esc → page help → ↑ history →
  restart: history and data persisted. **PASS**; Phase 7 e2e still PASS.

### Limitations
- Vocabulary-based: phrasings outside the lexicon are not understood (they get suggestions; with *Collect unmatched
  commands* on they are kept locally so words can be added). No free-form questions, no reasoning.
- The shortcut works while Arcadia's window has focus (no system-wide hotkey — deliberate: nothing to collide with games).
- German is understood but answered in the interface language (English/Arabic); German UI strings are not part of Arcadia.
- Oracle creates and updates; it never deletes. Transfers, splits, budgets and goals are created in Finance itself.
- Traceroute opens the tool (it can take minutes); ping/DNS/port run inline.
- Name matching is per word with small edit distances; very short or very different spellings are not matched.

### How to extend
- **Words**: add a line to `Lexicon/<lang>.txt` (`concept = phrase | phrase`); no code changes. A new language is a new file
  plus one line in `OracleLexicon.Load`.
- **A command**: a class deriving from `OracleCommandBase` (`Match` returns a score from the parse; `ExecuteAsync` uses
  `call.S` services and returns an `OracleResult`; changes go through `call.ChangeAsync(preview, exact)`), plus one line in
  `OracleRegistry`, examples in each language, `Orc_*` strings, and a test. Help and suggestions pick it up automatically.

### Next phase
Phase 9 (Automation) followed; see above.

## Phase 7 — Personal Finance, Budgets, Transactions & Financial Dashboard (2026-09-28)

The **Finance** sidebar entry is now a working module: a private financial terminal with an overview, a transaction
register, accounts, budgets, bills & subscriptions, savings goals, reports and a month/year history. It is local-only:
no bank logins, no Open Banking, no cloud, no telemetry, no AI. Nothing in Finance ever leaves the PC unless the user
exports a file. All earlier modules are unchanged (regression-captured; 390 tests green).

### Architecture
- **`Arcadia.Core/Finance`** — entities (`FinanceAccount`, `FinanceCategory`, `FinanceTransaction`, `TransactionDocument`,
  `Budget`, `RecurringItem`, `RecurringOccurrence`, `SavingsGoal`); pure logic: `Money.cs` (`Currency`, `AmountParser`,
  `MoneyFormatter`), `FinanceRules.cs` (`FinanceMath` balances, `BudgetMath` with rollover, `RecurringMath`, `GoalProgress`,
  `FinancePeriods`, `FinanceDefaults` categories with EN/DE/AR names), `FinanceCsv.cs` (CSV reader/writer, import parser,
  duplicate rules, export), `QuickExpenseParser`; contracts: `IFinanceStore`, **`IFinanceQueryService`** (the deterministic
  read side a future Oracle calls: `GetMonthlyExpensesAsync`, `GetMonthlyIncomeAsync`, `GetCategorySpendingAsync`,
  `GetUpcomingBillsAsync`, `GetSubscriptionsAsync`, `GetBudgetStatusAsync`, `GetTripExpensesAsync`, `GetProjectExpensesAsync`,
  `GetSavingsGoalsAsync`, `GetMonthlySummaryAsync`, `GetLargestExpensesAsync`, `GetBalanceHistoryAsync`, `GetAccountBalancesAsync`),
  `IQuickExpenseService`, `IFinanceExporter`, and `FinanceException` (a resource key, shown in the user's language).
- **`Arcadia.Data/Finance`** — `FinanceStore` (accounts/categories, transactions & import, budgets/bills/goals, queries —
  four partial files), `QuickExpenseService`, `FinanceJsonExporter`.
- **`Arcadia.App`** — `Services/FinanceServices.cs` (`FinanceContext`, `FinancePrivacy`, `FinanceFormats`),
  `ViewModels/Finance` (page, eight sections, `FinanceActions` = every editor opened the same way from any entry point,
  `ExpensePanelViewModel` for trips and projects), `Views/Finance` (views, `MoneyText`, `MeterBar`, `FinanceChart`,
  `FinanceStyles.xaml`), `Dialogs/FinanceDialogs.*` and `Dialogs/FinanceImportExport.cs` (import wizard, export).

### Money representation
- `decimal` everywhere in code; never `float`/`double` for money (charts convert to `double` only for drawing).
- Stored as **exact INTEGER ten-thousandths** (`MoneyConverter`, like SQL Server's `money`): exact sums, correct ordering in
  SQL, four decimals kept. The stores reject more than four decimals and amounts above one trillion; totals are summed in
  `decimal` (no overflow at that size; an `OverflowException` is caught and explained, never a crash).
- Display rounding happens only in `MoneyFormatter` (currency minor unit, half away from zero; JPY 0, KWD 3 digits…).
- **Currencies**: every account has one ISO 4217 code; any code Windows knows is accepted. Totals are always lists per
  currency (`MoneyTotal`); amounts in different currencies are never added and never converted.
- **Number style**: Settings → Finance → *Number format* (default: the Windows regional format with the user's own
  separators, e.g. `1.234,56 €` in Germany; or 1,234.56 / 1.234,56 / 1 234,56 / 1'234.56). Typed amounts are read the same
  way; anything unambiguous is accepted either way ("12,50" and "12.50").

### Accounting model (sign convention)
- `Amount` is **always positive**; `Type` (Expense / Income / Transfer) gives the direction. There are no negative expenses.
- A **transfer is one row**: `Amount` leaves `AccountId`, `ToAmount` (or `Amount`) arrives in `ToAccountId`; it is neither
  income nor expense and never has a category. Across currencies the received amount must be entered (no conversion).
- **Balance** = opening balance + income − expenses − transfers out + transfers in. It is never stored: computed from a
  narrow projection in one pass and cached until the next write (`IFinanceStore.Version`). Deleting a transfer restores both
  balances; editing a transaction recalculates everything (tests: `FinanceMathTests`, `FinanceStoreTests`).
- Refunds are recorded as income (optionally in the spending category); category spending reports count expenses only.

### Categories
21 built-in categories (seeded by the migration, names in English/German/Arabic until renamed), including Transportation →
Fuel / Public transport / Parking / Repairs. One level of subcategories, same kind as the parent. Deleting a category moves
its transactions (and budgets) to another one or to *Uncategorized*; archiving hides it from pickers.

### Budgets
Monthly budgets per category (subcategories included) and currency, or a custom date range; optional explicit **rollover**
(only unspent money carries to the next month; an overspent month starts the next at the plain amount; computed from the
budget's start, at most 20 years back). States: *Within budget*, *Approaching limit* (≥ 85 %), *Over budget* — factual wording
only. A **trip budget** is the same `Budget` with a `TripId` (covers every expense recorded for that trip).

### Recurring items, bills and subscriptions
`RecurringItem` = bill, subscription or income with an amount, optional account and category, and a rule
(daily/weekly/monthly/yearly × interval, first due date, optional end). Due dates come from the **Phase 6 recurrence engine**
(`Recurrence.Dates(RecurrenceRule…)` — the calendar and finance share one engine; month-end clamping included). Nothing is
pre-generated. *Mark paid* creates a real transaction and a `RecurringOccurrence` (deleting that transaction reopens the date);
*Skip* records a skipped date. Due dates before the day an item is created are history, not "unpaid". Annual cost = amount ×
nominal times per year (365 / 52 / 12 / 1 ÷ interval); monthly equivalent = annual / 12.
**Calendar**: due dates are a projection (`CalendarItemKind.Bill`, source *Bills & income*, switchable in the calendar and in
Settings); no calendar events are created, and only names (no amounts) appear there.

### Savings goals
Target, optional date, and a saved amount that is either typed by hand (*Add to goal*) or follows an account's balance.
Progress shows saved / remaining / % and, with a date, what per month would reach it — arithmetic only, no advice.

### Reports and history
Periods: this month, last month, last 3 / 6 months (current month included), this year, last year, custom. Income vs expenses,
spending by category (click → register filtered), net and saved over time, month-end account balances, budget performance
(budgeted vs spent per budget across the months), subscription costs. *Month & year*: the month as a factual statement (income,
expenses, net, saved, largest category, count, budgets over limit) and a 12-month table with a chart and the year's major
categories. "Saved" = transfers into savings/investment accounts minus transfers back out, plus expenses in the Savings category.
Charts (`FinanceChart`) are drawn in one render pass: sunken well, thin axes, faint grid at round values, grouped bars and
lines, a hover readout; values are hidden in privacy mode.

### Receipts and documents
A transaction links Documents-library files (Phase 4) with a role (receipt, invoice, contract, statement, other) —
referenced, never copied. *Attach receipt…* in the register and the editor; *Open* shows the document in Documents (a missing
file is handled there as before). No OCR, no reading of receipts.

### Travel and projects
Trip dossier tab **09 Expenses** and project workspace tab **Expenses**: totals per currency, spending by category, the list,
*Add expense* (pre-linked) and *Open in Finance* (register scoped to the trip/project); trips can have a budget. Deleting a trip
keeps its transactions (unlinked) and removes its budget. Projects stay projects: a read-out, not accounting.

### CSV import and export
- **Import wizard** (four steps): file (UTF-8/UTF-16 by BOM, otherwise strict UTF-8, otherwise Windows-1252; separator
  detected: comma, semicolon, tab, pipe) → column mapping (headers recognised in English, German, Arabic and Arcadia's own
  export; date, amount or debit/credit, description, payee, category, type, time, notes, tags, account, to-account, to-amount,
  currency) → review → import. Number style and date format are **detected or asked, never guessed**: "1,234" or 01/02/2026
  that can be read both ways makes the user choose; the format that reads most rows wins, and every row that does not fit is
  listed as invalid with the reason. Duplicates (same account, type, amount; same day and similar text = likely; ±1 day or
  different text, or repeated inside the file = possible) are only marked — skipping likely ones is the default, anything
  imported anyway is flagged *needs review*. One database transaction per import; **Undo import** removes the batch.
- **Export**: CSV (UTF-8 with BOM for spreadsheets, ISO dates, invariant amounts, cells starting with = + - @ are neutralised
  and restored on import) by account, category and period; **JSON** of all finance data (`FinanceJsonExporter`, schema
  `arcadia-finance` v1). The local backup snapshot can also include `finance.json` (off by default; the database copy contains
  the data anyway). Nothing is exported unless the user asks and chooses where.

### Privacy
All finance data lives in the local SQLite database; no network code touches it; log messages never include amounts,
descriptions or account names. **Privacy mode** (rail toggle, Ctrl+Shift+H, palette, Settings): every amount on screen —
figures, chart axes and readouts, palette results, Quick Capture hints and toasts — is shown as `••••••`; the data is
untouched. The state is remembered (and flushed on exit), so a hidden screen stays hidden after a restart.

### Search, palette, Quick Capture
- Ctrl+K finds transactions (description, payee, notes, tags, category names in every language, account), accounts,
  budgets, bills/subscriptions and goals; rows describe themselves ("E2E SSD — -120,00 € · Today · Checking",
  "39,99 € monthly · Subscription"), masked in privacy mode.
- Palette: Open Finance, New transaction, Add expense, Add income, Open accounts, Open budgets, Open subscriptions, Upcoming bills,
  Monthly summary, Savings goals, Hide/show amounts.
- **Quick Capture → Expense** (Ctrl+Shift+N, Tab to *Expense*): "35 groceries", "€35 groceries", "groceries 12,90 €",
  "12 EUR Kino gestern". Saved at once only when the pattern, the account (default account, or the only account in that
  currency) and at most one matching category are clear; otherwise the transaction editor opens with what was understood.
  Attached to the current page's project or trip. `QuickDateParser` now also knows *yesterday / gestern / أمس*.
- Keys: Ctrl+N new transaction; register Enter edit, Delete remove, Ctrl+D duplicate; editor Enter saves, *Save & new*.

### Database
Migration **`Finance`** (purely additive): `FinanceAccounts`, `FinanceCategories` (21 seeded), `FinanceTransactions`,
`TransactionDocuments`, `Budgets`, `RecurringItems`, `RecurringOccurrences`, `SavingsGoals`; indexes on account+date, date,
category, project, trip, recurring item, import batch; FTS kinds `txn`, `account`, `budget`, `recurring`, `goal`. Foreign keys
never cascade into user data (accounts with transactions cannot be deleted; deleting a project/trip only unlinks). The
automatic pre-upgrade copy is written (`Backups\arcadia-before-Finance-*.db`). `FinanceMigrationTests` upgrades a Phase 6
library with games, tasks and events; the real library was upgraded the same way (see the final report).

### Performance
Queries project only the columns they need; balances are cached by data version; reports compute on demand for the visible
period; lists are virtualised; no timers or polling in Finance. Measured on the demo library (5 accounts, ~120 transactions):
Home idle 181 MB working set / 68 MB private, 62–78 ms CPU per 30 s; Finance overview/reports open 187–190 MB / 69–70 MB,
60–160 ms CPU per 30 s.

### Localization
~490 new keys in English and Arabic (German UI resources do not exist yet; built-in category names include German for search
and quick capture). Amounts use the chosen number style and keep Western digits in Arabic (tabular figures, no digit
substitution); sentences around amounts follow the page direction.

### Tests
`FinanceTests.cs` (82 tests): amount parsing (styles, ambiguity, signs, symbols, precision, overflow), formatting per currency,
currency detection, decimal exactness; balances and transfer invariants (wealth unchanged, delete restores both, edit
recalculates, cross-currency); budget levels and rollover; recurring costs and open due dates; goal progress; periods; CSV
parsing (quotes, line breaks, delimiters, encodings, date/number detection, headers in three languages, sign conventions,
debit/credit, type column, transfers, currency mismatch, formula guard); duplicate grading; quick expense parsing; stores
(categories, balances, accounting rules, currency separation, category roll-up, budgets incl. rollover and custom ranges,
bills paid/skipped/reopened, subscription statistics, goals, monthly summary, trip & project expenses and trip budget, import
with duplicates and undo, search in three languages, calendar projection, quick expense service, receipts, 4-decimal storage,
JSON export); **the spec's integration scenario** on a database file (checking 2,000 → salary 3,000 → rent 900 → groceries
100 → transfer 500 to savings → grocery budget → internet subscription → receipt → project and trip expenses → reopen →
verify → CSV export → import into a second library → identical values); migration from Phase 6. Total: **390 tests, all
passing**.
UI end to end (`e2e-p7.ps1`, UI Automation on a copy of the Phase 6 library upgraded at start): accounts from the welcome
screen, income/expenses/transfer/project expense in the editor, balances (3.380,00 € / 500,00 €), grocery budget, subscription
(479,88 € per year), register chip, privacy mode, restart (privacy still on, data persisted), Ctrl+K opens the transaction,
clean log — **PASS** (twice in fresh sessions). Visual QA: all sections at 1600×900, Arabic RTL at 1366×768, privacy mode,
dialogs, import review of a Windows-1252 German bank file, calendar bills, trip and project expense tabs, Home.

### Known limitations
- No currency conversion (by design in this phase); multi-currency totals are shown side by side.
- Import creates transfers only from Arcadia-format files (a *Type*/*To account* column); bank files become income/expenses in
  one chosen account. Project/trip links are not imported.
- Bills have no reminders of their own (their due dates are on the calendar); snooze/notifications are Phase 6 events only.
- Goals follow one account or are manual; no split of one account across several goals.
- Budget rollover carries surpluses only; no fiscal years or envelope budgeting.
- The "largest expenses" query orders by amount across currencies (each row keeps its currency).
- No receipt OCR, no automatic categorisation (by design).

### Next recommended phase
Phase 8 is not started. Candidates: the deterministic Oracle command engine over the organizer and finance query services,
optional offline exchange-rate tables entered by the user, recurring reminders, bank-file import presets.
## Phase 6 — Calendar, Tasks, Notes & Personal Organizer (2026-09-28)

The **Calendar**, **Tasks** and **Notes** sidebar entries are now working modules (they replaced the placeholder pages), with a
**Today** agenda, local **reminders**, **Quick Capture** (Ctrl+Shift+N), cross-module dates, Phase 6 search and palette commands,
and a compact Today strip on Home. Games, IT Tools, Projects, Documents and Travel are unchanged (regression-captured; full suite green).

### Architecture
- **`Arcadia.Core/Organizer`** — entities `TaskItem`, `Note`, `NoteRevision`, `CalendarEvent`, `CalendarEventException`, `CalendarCategory`;
  `EntityRef`/`EntityKind` (what a task, note or event is about, and the internal `arcadia://kind/id` link format);
  pure logic: `Recurrence` (expansion, split), `QuickDateParser`, `TaskRules`, `ReminderPlanner`; contracts `ITaskStore`, `INoteStore`,
  `ICalendarStore` and the read-side services a future deterministic command engine (Oracle) will call: `ICalendarQueryService`,
  `ITaskQueryService`, `INoteQueryService`, `IQuickCaptureService`, `IEntityDirectory`.
- **`Arcadia.Data/Organizer`** — `TaskStore`, `NoteStore`, `CalendarStore`, `CalendarQueryService` (projection), `EntityDirectory`, `QuickCaptureService`.
  Every write updates the FTS index in the same transaction.
- **`Arcadia.App`** — `ViewModels/Organizer` (`CalendarViewModel` with Today/Month/Week/Day/Agenda, `TasksViewModel` + `TaskDetailsViewModel`,
  `NotesViewModel`, `QuickCaptureViewModel`, `OrganizerContext`), `ViewModels/Home/HomeTodayViewModel`, `Services/OrganizerServices`
  (`ReminderService`, `NotificationCenter`, `OrganizerChangedMessage`), `Views/Organizer` (views, `TimelinePanel`, `RailItemsControl`,
  `ReminderCard` + `ReminderPresenter`), dialogs (event editor, recurrence scope, entity picker, category editor).
- `INavigationService.Open(EntityRef)` routes any record (arcadia:// links in notes, context chips, reminders, calendar items) to its module.

### Unified tasks and notes (Phase 4 consolidation)
Phase 4's `ProjectTasks` and `ProjectNotes` became Arcadia-wide `Tasks` and `Notes` — **the same rows, same ids**, not a parallel system.
`ProjectId` is now optional: a task or note belongs to a project (FK, cascade, project progress and activity unchanged), to another record
through `ContextKind`/`ContextId` (trip, place, game, document, diagnostic session…), or to nothing. The Projects workspace keeps editing
its tasks and notes through `ProjectStore`; the Tasks and Notes modules edit the same rows. Subtasks stay one level deep and share their
parent's context. Links to records deleted later are detached at startup (one set-based statement per table and kind).

### Calendar and recurrence
- Times are local wall-clock values ("floating"): an event at 09:00 stays at 09:00; created/updated stamps are UTC. All-day events store dates.
- A recurring event is **one row** with its rule (daily/weekly/monthly/yearly, interval, weekdays, optional last date). Occurrences are
  expanded per visible range and never stored; the expansion jumps to the range, so a daily event from 2010 costs the same as a new one.
  Monthly rules on the 31st use the month's last day; yearly 29 February becomes 28 February in common years.
- Single occurrences changed or cancelled are `CalendarEventException` rows keyed by their original date.
- Editing an occurrence asks **This event / This and following / All events**: an exception, a split of the series at that date
  (the old part ends the day before; later exceptions move to the new series), or the series itself (every occurrence moves by the same
  number of days; per-occurrence times are kept when only times change).
- **Projection** (`CalendarQueryService`): the calendar shows event occurrences plus task due dates, project target dates, trips (their whole
  span) and itinerary items with an exact time — read from their owners, never copied. Sources (Events, Tasks, Projects, Travel, Gaming =
  Gaming-category events and game-linked tasks/events) can be switched off without touching data; choices and the last view are remembered.
- Categories are user-editable (name, icon, one of Arcadia's eight accent tones — no free colours); seven defaults are seeded.

### Reminders
`ReminderService` never polls: after each check it plans the next reminder (`ReminderPlanner`, per occurrence) and arms **one timer** for that
moment, capped at six hours; data changes, Windows resume and clock changes re-plan immediately. Idle cost measured: ~60 ms CPU per 30 s idle
(the whole app). What has been shown is persisted (`Organizer.RemindersShownUntil`), so restarts never repeat a reminder; reminders missed while
Arcadia was closed (up to a day) are shown once, marked "missed". Presentation is local: an Arcadia card in the screen corner (never steals
focus; Open / Snooze 10 min / Dismiss), the header bell list with an unread badge, an optional Windows sound and a flashing taskbar button.
Failures are logged once and never retried in a loop; the reminder stays in the bell list. Reminders only work while Arcadia runs (it can stay
minimised); a Windows service or toast registration was not added — see limitations.

### Tasks
Views Today (due today or overdue), Upcoming (14 days, grouped by day), Overdue, Pinned, All (with subtasks nested), By project, Completed;
filters (text via FTS, priority), sort (smart, due, priority, title, updated, created). Quick add parses dates without guessing
(`QuickDateParser`, EN/DE/AR: "tomorrow", "Friday", "next Monday", "in 3 days", "12 Oct", "2026-10-12", "18:00", "at 9", "6:30pm",
"um 18 Uhr", "غداً"…; slash dates like 10/12 are ambiguous and left in the text with a warning). Priority marks only High (amber) and Critical
(red). Details panel with explicit save (Ctrl+S) and automatic save of valid edits when another task is selected or Arcadia closes.
Context chips ("PROJECT · Name") open their record.

### Notes
Library views (All, Recent, Pinned, Favourites, by module, by tag), FTS search with relevance, sort. The editor is a calm writing surface with a
Markdown toolbar (heading, bold, italic, lists, checklists, code, web and internal links; Ctrl+B/I; list continuation on Enter), Write /
Both / Read modes (remembered) using the Phase 4 Markdown renderer, `arcadia://` links that navigate inside Arcadia, accent, pin, favourite,
tags and a context link. Autosave after 800 ms of calm (never per keystroke), states Editing… / Saving… / Saved / Save failed (+ Try again);
flushed on note switch, page change and application exit (on a worker thread, so exit never deadlocks). **History foundation**: before an
overwrite the previous text is kept as a revision if the last one is older than 10 minutes; at most 10 per note; restorable from the history drawer.

### Today, Home, Quick Capture, search and palette
- **Today** (Calendar's first view): date, next event with countdown, the day's timeline (past items dimmed) and all-day items, active trip,
  tasks due/overdue with checkboxes, what's ahead (deadlines and trips, 14 days), pinned and recent notes.
- **Home**: a slim Today strip between the featured game and the game panels (today + next event, tasks due, next deadline or trip, pinned
  note). Each cell can be hidden in Settings → Home; all hidden restores the Phase 1 layout exactly.
- **Quick Capture** (Ctrl+Shift+N or palette): Task / Note / Event from one line, date preview before saving, attached to the current page's
  project, trip or game by default (one click to detach). Enter saves, Shift+Enter new line, Tab switches kind, Esc closes.
- **Search** (Ctrl+K): events join tasks and notes; event rows say when ("Dentist — Tomorrow · 9:00 AM · Hauptstraße 3"), task rows their due
  date and project; snippets no longer show Markdown markers. Commands: Open Calendar/Tasks/Notes, New event/task/note, Quick capture,
  Today's agenda, Upcoming tasks, Overdue tasks, Pinned notes.
- Keyboard: calendar T/M/W/D/A views, N new event, ←/→ and PageUp/PageDown move, Alt+Home today; tasks Space done, Enter edit, Delete remove;
  agenda ↑/↓, Enter, Space, Delete; Ctrl+N creates the page's own kind (event, task, note) on organizer pages.

### Database
Migration **`Organizer`**: new `Tasks` and `Notes` tables receive every Phase 4 row **with its id** (parents first), then `ProjectTasks` and
`ProjectNotes` are dropped; new `NoteRevisions`, `CalendarCategories` (7 seeded), `CalendarEvents`, `CalendarEventExceptions`; FTS kind `event`.
Down migration copies project-owned rows back. Indexes on due date, status, reminder, context, event start/end/last date.
The automatic pre-upgrade copy (Phase 5) was written each time (`Backups\arcadia-before-Organizer-*.db`). Verified by
`OrganizerMigrationTests` (ids, subtasks, statuses, Unicode note text, search, project progress) and on a fresh snapshot of the real library
(21 games, 137 sessions unchanged, integrity OK). The EF compiled model was regenerated.

### Performance
Only the visible range is queried; recurrence expansion is range-bounded; task counts use one small projection; subtasks for a page come in one
query; lists are virtualised; searches are debounced (FTS for text in tasks and notes). Idle: the reminder timer sleeps until the next reminder.
Measured on the demo library: Home idle 180 MB working set / 64 MB private; after opening every organizer view 249 / 112 MB (pages stay cached
like the other modules); ~60 ms CPU in 30 s of idle.

### Localization and accessibility
≈335 new keys in English and Arabic; dates/times use the UI culture (week start from the culture), stored values are never formatted strings.
RTL verified for the month grid, week grid, tasks and notes (titles trim instead of clipping). Rails use `RailItemsControl` so their items stay
visible to screen readers (WPF's item peers could stay empty after a combo box was opened through UI Automation; Travel's rail uses it too);
palette rows, journal and task rows, capture kind chips and dossier tabs work with UI Automation Select.

### Tests
`OrganizerTests.cs`: recurrence (weekly multi-day/interval, weekdays, month-end clamp, leap day, custom interval + end, long-running series,
exceptions cancel/move/rename, moved in from outside the range, split, multi-day all-day), date parser (15 phrases EN/DE/AR, no false dates,
ambiguous slash dates), overdue rules, reminder planning (per occurrence, all-day at 09:00, task reminders, done tasks ignored), the reminder
service's sleep rule, arcadia:// links and stored contexts, stores (event CRUD/search/delete, the three edit scopes and delete scopes, tasks across
contexts with project progress and activity, views/counts/overdue, notes with revisions/views/tags/FTS/clean snippets, calendar projection with
sources, quick capture, detaching links to deleted records) and the Phase 4 → 6 migration. Total suite: **308 tests, all passing**.
UI end to end (UI Automation, `e2e-p6.ps1`, on a copy of the Phase 5 library upgraded at start): create two events (one with a reminder two
minutes ahead) → quick-add a task and link it to an existing project → write a note (autosaved) and link it → trip dates visible on Today and in
the month → the reminder card appears on time → restart → everything persisted → complete the task → Today's summary and the project's
progress update → Ctrl+K finds and opens the note → Quick Capture a task → restart → it persisted. **PASS**, no warnings in the log.
Palette probe: event/task rows and organizer commands. Visual QA: every view at 1600×900 and 1366×768, Arabic RTL, dialogs, Home, Settings.

### Known limitations
- Reminders fire only while Arcadia is running (minimised is fine); no Windows toast/Action Center integration and no background service yet.
  Snoozes are in memory (a restart forgets a snooze; the reminder was already shown).
- Recurrence covers daily/weekly/monthly(by day)/yearly with interval, weekdays and an end date; no "second Tuesday", count-based ends,
  time zones or iCalendar import/export.
- Per-occurrence changes cover time, title, location and description (not category or reminder). Moving a series by whole days clears its exceptions.
- Week/day views show timed items side by side but do not drag-to-move or resize yet.
- Deleting a day, trip or game leaves linked tasks/notes/events free-standing (not deleted).
- Note history keeps up to 10 revisions; no diff view.

### Next recommended phase
Phase 7 is not started. Candidates: Finance, a deterministic Oracle command engine on top of the query services and `QuickDateParser`,
drag-and-drop in the week view, Windows toast notifications via an app identity, calendar import (.ics).
## Phase 5 — Travel, Trips, World Explorer & Travel Journal (2026-09-28)

The **Travel** sidebar entry is now a working module: an explorer's desk, an interactive world map, places, trips with a day-by-day dossier,
a travel journal, photos, memories, a bucket list, countries ("world explored") and a personal timeline. Games, IT Tools, Projects and Documents
are unchanged (regression-captured, full test suite green).

### Architecture
- **`Arcadia.Core/Travel`** — entities `TravelPlace` (type, status WantToVisit/Planned/Visited, favourite, rating, source), `PlaceVisit`, `Trip`
  (Idea/Planning/Upcoming/Active/Completed/Cancelled, optional linked project, artwork), `TripPlace`, `TripDay` (the date is derived from the trip start),
  `ItineraryItem` (exact time or part of day, category, place, done), `TripDocument`, `TravelJournalEntry` (Markdown, mood, highlight), `TravelPhoto`,
  `BucketListItem`, `TravelCountry` (per-country flags: favourite, want, marked visited). Pure rules: `GeoMath` (validation, Web Mercator, distances,
  coordinate parsing incl. DMS), `TravelRules` (inclusive durations, date validation, phase from dates, progress, day dates, status suggestion,
  photo-to-place matching radius per place type). Contracts: `ITravelStore`, `ICountryCatalog`, `IGeocodingService`, `IMapTileService`, `IPhotoMetadataReader`.
- **`Arcadia.Infrastructure/Geo`** — `CountryCatalog` (242 territories, 195 UN member/observer states, names EN/DE/AR, continents, outlines at two levels of detail,
  point-in-country incl. coastal tolerance), `CityIndex` (34 k cities, accent/ß/Arabic folding, 1° grid for nearest lookups), `GeocodingService`,
  `MapTileService`, `PhotoMetadataReader` (MetadataExtractor: date taken, camera, orientation, GPS).
- **`Arcadia.Data/Travel/TravelStore`** (+ Places, Trips, Journal, Insights partials) — CRUD, itinerary ordering, visits, stats (days on the road as a union of dates),
  memories grouped by trip or month, timeline; every change updates the FTS index in the same transaction.
- **`Arcadia.App`** — `ViewModels/Travel` (`TravelViewModel` with sections, `TripWorkspaceViewModel` with Overview/Itinerary/Map/Places/Notes/Photos/Documents/Memories tabs,
  `PlaceDetailsViewModel`, `TravelJournalViewModel` with 800 ms autosave flushed on exit, photo grid + viewer), custom WPF controls `WorldMap` (pan/zoom/wrap,
  clusters, labels with collision placement, routes coloured per day), `PhotoThumb` (lazy, cached, EXIF-oriented), `TopoLines`; dialogs for place, trip, itinerary item,
  visit, bucket item and pickers. `tools/Arcadia.GeoForge` regenerates the bundled geodata.
- Deep links for QA: `--page travel --tab Desk|Map|Trips|Places|Journal|Memories|Bucket|Countries|Timeline`, `--trip <id> [--tab <TripTab>]`, `--place <id>`.

### Map provider, geocoding and external requests
- The world map is **offline first**: country outlines come from bundled Natural Earth data and are drawn as vectors (coarse below zoom 3.4, detailed above).
  A dark "night chart" style is applied locally.
- **Detail tiles** (optional, on by default, switch in Settings): OpenStreetMap standard tiles fade in from zoom 3–4.5 and are darkened locally. Policy compliance:
  identifying User-Agent, at most 2 parallel requests, only tiles in view, no prefetch or bulk download, disk cache in `Cache\Tiles` honouring Cache-Control
  (default 7 days) with ETag revalidation, 300 MB cap, 30 s back-off after a network failure, cached tiles reused offline, attribution always visible.
  A custom XYZ server (`{z}/{x}/{y}`, optional `{key}`) can be set; its key is stored only in the DPAPI secret store (`maptiles.apiKey`). CARTO dark tiles were
  evaluated and rejected (they now require a key).
- **Place search**: the built-in index (GeoNames cities + countries, EN/AR/DE names, coordinates) answers as you type, offline. **Nominatim** is queried only when the
  user presses Enter / "Online" (switch in Settings): ≥ 1.1 s between requests, identifying User-Agent, results cached for 30 days in `Cache\Geocoding`.
- **What leaves the PC**: tile coordinates (z/x/y) and the search words the user typed. Never places, trips, notes, photos or their positions. No API key is required.
- Credits are shown in Settings and listed in `THIRD-PARTY-NOTICES.md` (installed next to the app).

### Photos
Photos are Phase 4 library documents (referenced, never copied or moved) plus a `TravelPhoto` row (trip/place/journal links, date taken, GPS, orientation).
EXIF is read locally. Photos with GPS but no place get a suggestion "Place them": each is linked to the nearest saved place within a radius that depends on its type
(city 15 km, town 6 km, landmark 1.5 km, restaurant/hotel 400 m; countries never). Thumbnails are generated lazily into the shared thumbnail cache; the grid is virtualised by rows.
Removing a photo from Travel removes the library reference only if nothing else uses it; the file itself is never touched.

### Database
Migration **`Travel`** (additive): `TravelPlaces`, `PlaceVisits`, `Trips`, `TripPlaces`, `TripDays`, `ItineraryItems`, `TripDocuments`, `TravelJournalEntries`,
`TravelPhotos`, `BucketListItems`, `TravelCountries`; cascades inside a trip, SET NULL for optional links (project, place, cover photo). FTS kinds `trip`, `place`,
`journal`; country names in every language and a trip's places are indexed in the tags column (matched, never shown as a snippet). The EF compiled model was regenerated.
**New: before any migration of an existing library a consistent copy is written to `Backups\arcadia-before-<migration>-<time>.db`** next to the database
(newest five kept; a failed copy is logged and does not block the upgrade). Verified on two fresh snapshots of the real library: 21 games and 137 sessions unchanged,
integrity and foreign-key checks OK, backup contains the pre-upgrade schema.

### Privacy and offline behaviour
Everything is stored locally. Without a network the atlas, search, trips, journal and photos all work; tiles fall back to the cache or the vector map with a quiet notice.
There is no account, sync, sharing or upload of any kind.

### Localization, accessibility, visual identity
≈470 new EN/AR keys (German still has no infrastructure). Coordinates, file names and the map stay left-to-right inside RTL layouts. Accessible names on all map controls,
tabs, list rows and palette rows (palette rows previously announced their type name — fixed for the whole app). Dossier tabs switch via keyboard, mouse and UI Automation.
Identity: topographic line textures, monospaced coordinates, a compass rose, route lines per day, 100–300 ms transitions (map flights, marker pulse and photo fades follow Settings → Interface animations).

### Tests
`TravelTests.cs`: GeoMath (parsing, Mercator, distances), TravelRules (durations, phases, progress, statuses, photo matching), bundled geodata (countries, cities,
names in three languages, point-in-country, flags), geocoding and tiles with fake HTTP (cache, 1.1 s spacing, errors, offline, ETag revalidation, custom key),
EXIF parsing, store (places, trips, itinerary moves, visits, journal excerpts, photos, stats, memories, search) and the migration of an existing library incl. the backup.
Total suite: **267 tests, all passing**.
UI end to end (UI Automation, fresh data folder, `e2e-p5.ps1`): search "Kyoto" offline → save as Want to visit → New trip here (10–13 Oct) → 4 days from dates, Add day
(trip extends to 14 Oct) → 2 itinerary items (06:30 at Kyoto; evening) → trip note in Markdown → document via file picker → EXIF/GPS photo via file picker, "Place them"
→ journal entry linked to trip and place → restart → everything present → trip Active → Kyoto marked visited → trip Completed → desk "1 of 195 countries visited",
timeline, memories chapter (photo, journal, visit), map. Palette: place/country/journal/trip search hits and all seven travel commands, Enter opens place/trip.
Visual QA: every section populated and empty, 1600×900, 1366×768, 150 % scaling, Arabic RTL, Settings panel.

### Known limitations
- Map labels come from the tiles when zoomed in; the offline vector map shows country names only (no roads, no city labels beyond saved places).
- Online tiles use the OSM community servers (fair-use); heavy use should switch to a custom tile provider in Settings.
- No route planning, travel times or distances along roads; routes are straight lines between itinerary places.
- Photos are matched to places by distance only; no import of whole folders by date range yet; HEIC needs the Windows HEIF extension for thumbnails.
- "Add day" extends a dated trip; deleting a day does not shorten the dates.
- Out of scope by request: booking, payments, live tracking, passport/card storage, sync, sharing, AI features, weather, currency.

### Next recommended phase
Phase 6 is not started. Candidates: Calendar/Tasks module across projects and trips, restore-from-backup, folder import of trip photos by date range.
## Phase 4 — Projects, Workspaces & Document Library (2026-09-27)

The **Projects** and **Documents** sidebar entries are now working modules (they replaced the placeholder pages). A global search foundation and a
Ctrl+K command palette cover the whole app. Games and IT Tools are unchanged (regression-tested end to end).

### Architecture
- **`Arcadia.Core/Projects`** — entities `Project`, `ProjectTask` (one level of subtasks via `ParentTaskId`), `ProjectNote`, `ProjectLink`, `ProjectActivity`;
  enums for status (Planning/Active/OnHold/Completed/Archived), priority (Low/Normal/High/Critical), progress mode (Manual/TaskBased — new projects follow their tasks)
  and task status; pure rules in `ProjectRules` (progress ignores cancelled tasks, days remaining, overdue, status → completion date, restore) and `LinkRules`
  (http/https only, adds `https://`, never fetched); contracts `IProjectStore`, drafts and summaries.
- **`Arcadia.Core/Documents`** — `Document`, `Tag`, `DocumentTag`, `DocumentProject` (many-to-many), `WatchedFolder`; `FileKinds` (kind + safe preview kind per extension,
  temporary-file filter), `PathRules` (normalisation, network paths, "is under"), contracts `IDocumentStore`, `IFileFactsService`.
- **`Arcadia.Core/Search`** — `ISearchProvider` / `IGlobalSearchService` (fan-out over providers), `FtsQuery.Build` (quoted prefix terms, max 8), `CommandMatcher` (palette ranking).
- **`Arcadia.Core/Text/MarkdownLite`** — small block/inline Markdown parser (headings, lists, checkboxes, code, quotes, bold/italic/code, http(s) links only).
- **`Arcadia.Core/Platform/BackupContracts`** — `DataLocations`, `IBackupService`.
- **`Arcadia.Data`** — `ProjectStore`, `DocumentStore`, `Search/SearchIndex` (FTS5, kept in the same transaction as every change, rebuilt at startup if empty),
  `IndexSearchProvider` + `GameSearchProvider`, `LocalBackupService` (SQLite online backup + settings + managed media).
- **`Arcadia.Infrastructure/Files/FileFactsService`** — file metadata with 3 s timeouts (network paths), folder scans, batched availability checks (one probe per drive/share).
- **`Arcadia.App`** —
  `ViewModels/Projects` (`ProjectsViewModel` home: stats, pinned, filters, deadlines, recently opened/updated, search; `ProjectWorkspaceViewModel` with Overview, Tasks,
  Notes, Files, Links and Activity tabs, autosave of description and notes — Saving… / Saved / Failed, flushed on exit),
  `ViewModels/Documents/DocumentsViewModel` (library rail: views, kinds, tags, projects, watched folders; paged virtualised grid; details panel with safe preview),
  `CommandPaletteViewModel`, services `DocumentActions` (add/open/open with/reveal/copy path/locate/remove), `DocumentAvailabilityService` (throttled background check),
  `DocumentWatcherService` (FileSystemWatcher for watched folders, coalesced, retried), `ShellFileInfo` (Windows file-type icons and names, cached),
  `ThumbnailService` (disk JPEG cache in `Cache\Thumbnails`, keyed by path + size + mtime). Dialogs: project editor, task, link, add folder, project picker.
- Deep links for QA: `--page projects --project <id> [--tab Overview|Tasks|Notes|Files|Links|Activity]`, `--page documents --doc <id>`.

### Files are references
Arcadia never moves, copies, renames or deletes user files. Project files are library documents associated with the project (`DocumentProject`):
"Remove from project" deletes only the association, "Remove from Arcadia" deletes only the reference. Missing files are detected (background check, "Check files",
watcher) and shown with a **Missing** badge; **Locate file…** reconnects the reference ("File reconnected" appears in each project's activity; a path already in the library is refused).
Adding a folder offers three modes: reference the folder only, index its documents once, or index and watch. Previews are limited to text/CSV (first 64 K characters,
read-only shared access), Markdown (rendered natively, no WebView) and images (downscaled thumbnails). Links are stored as addresses only and never visited.

### Database
Migration **`ProjectsDocuments`** (additive): `Projects`, `ProjectTasks`, `ProjectNotes`, `ProjectLinks`, `ProjectActivity`, `Documents` (unique NOCASE path),
`Tags`, `DocumentTags`, `DocumentProjects`, `WatchedFolders` and the FTS5 table
`SearchIndex(kind, entity_id, project_id, title, body, tags)` with `unicode61 remove_diacritics 2` tokenisation (Arabic and German match without accents/diacritics).
Verified on a snapshot of the real library (21 games and the diagnostic session unchanged; new tables and index created) and by `ProjectsDocumentsMigrationTests`.
The EF compiled model was regenerated.

### Search, palette and shortcuts
Ctrl+K opens the command palette (commands + search across projects, tasks, notes, links, documents and games; ↑/↓, Enter, Escape). Ctrl+N creates in context
(project, task, note), Ctrl+S saves now, Ctrl+F focuses the page search, Delete removes the selection (with confirmation), Escape closes palette/dialogs.

### Data locations and backup
Settings → **Data & backup** lists every location (database, settings, encrypted keys, media, thumbnail cache, logs), opens the data folder and writes a snapshot
folder (database via the SQLite backup API, settings, managed media). Referenced files are not copied; `secrets.dat` is DPAPI-bound to this Windows user and excluded.
Nothing is uploaded; there is no cloud sync.

### Localization and accessibility
≈375 new keys in English and Arabic (German still has no infrastructure). Paths and file names keep left-to-right order inside right-to-left layouts and trim
instead of overflowing. Workspace tabs, list items and pickers have screen-reader names.

### Tests
`ProjectsDocumentsTests.cs`: project rules and progress, link validation, path/kind/preview rules, FTS query building, command matching, Markdown parsing,
store tests on in-memory SQLite with FTS5 (CRUD, subtasks, archive/restore, stats, search in Arabic and German, shared documents, tags, relocate, watched folders,
project deletion keeps documents), migration of an existing database, and backup snapshot contents. Total suite: **211 tests, all passing**.
UI end to end (UI Automation, fresh data folder): create project → edit description → quick-add 3 tasks → complete one (progress 1 of 3) → note with German/Arabic text
autosaved → add a file with the file picker → add a link → add a library document and associate it → restart → everything persisted → move the file on disk →
"Check files" reports it missing → Locate → reconnected, user files untouched. Also: OLE drag and drop of a file onto the Files tab (real `DoDragDrop` source; the file is referenced, not moved), command palette (commands, FTS, Arabic, Escape, open project), dialogs smoke test,
IT Tools/Games regression flow. Visual QA at 1600×900, 1920×1080, 1366×768, 150 % scaling, Arabic RTL and empty states.

### Known limitations
- No PDF/Office preview (by design: only safe native previews); open such files in their program.
- Watched folders need the drive to be connected; offline network shares are retried every minute and reported, not scanned.
- Search indexes file contents only for text, Markdown, log and CSV files (first 64 K characters); other documents are found by name, tags, path and project.
- Backup is a manual snapshot folder; there is no restore wizard or schedule yet.
- One level of subtasks; no recurring tasks, reminders or calendar integration.

### Next recommended phase
Phase 5 is not started. Candidates: Notes/Journal module on top of the project note editor, Tasks module across projects with a calendar view, restore-from-backup.
## Phase 3 — IT Toolbox (2026-09-27)

The **IT Tools** sidebar entry is now a working Windows toolbox (it replaced the placeholder page).

### Architecture
- **`Arcadia.Core/ItTools`** — contracts and pure logic, no Windows APIs:
  `ToolException`/`ToolFailure` (explainable errors), `Network/` (`NetworkTarget` parsing + validation, `Ipv4Subnet`, ping/trace/DNS/port/device contracts, `PingStatistics`),
  `SystemInfo/` (system snapshot, performance samples, processes + `ProcessSafety`, services, event log), `Files/` (drives, large files, hashing, `HashText`, `ByteSize`),
  `Reports/` (`ReportSection`/`ReportDocument` + `IReportExporter` with `TextReportExporter` and `JsonReportExporter`), `Diagnostics/` (session entities, `IDiagnosticSessionStore`, `DiagnosticReport`), `QuickCommands` (fixed catalogue).
- **`Arcadia.ItTools`** (new project, net10.0-windows) — Windows implementations: WMI + registry (`SystemInformationService`), `GetSystemTimes`/performance counters (`PerformanceMonitor`),
  `System.Net.NetworkInformation` (adapters, ping, traceroute via TTL), `DnsQuery_W` (DNS incl. MX/TXT/NS/SOA/PTR, UDP retries, TCP fallback), sockets (port test),
  `GetIpNetTable` + gentle ping sweep (local devices), `ServiceController`, `EventLogReader`, `DriveInfo`, `FileSystemEnumerable` (large files), `IncrementalHash`, `ProcessStartInfo` (quick commands, UAC `runas`).
  Registered with `AddArcadiaItTools()`.
- **`Arcadia.Data`** — `DiagnosticSessionStore` (EF Core).
- **`Arcadia.App`** — `ItToolsViewModel` (page: status strip, rail, overview; tool view models are created lazily on first open), one `ToolViewModel` subclass per tool
  (`ViewModels/ItTools/*`), views in `Views/ItTools/Tools/*`, shared `ToolHeader` (copy all / export / add to session, busy scanner, friendly error panel),
  `ItToolsStyles.xaml` (DataGrid, rail, chips, checkbox, console well, property sheets). Services: `ClipboardService`, `ExplorerLauncher`, `ReportExportService`, `DiagnosticSessionContext` (active session, id kept in settings).
- **Arcadia.UI** — new `ArcadiaLed` (status lamp), `ArcadiaLineGraph` (live graph), `ArcadiaChoice` (option chip that runs its command when selected — also via UI Automation),
  `Bidi.AutoDirection` for text boxes; `ArcadiaPanel` is now exposed to screen readers as a named group. 25 new icons from IconForge.
- Pages can implement `IDeactivatable`; live timers (status strip 2 s, performance 1 s, processes 3 s) run only while visible and skip ticks while the window is minimised.

### Tools
System Information · Live Performance · Network Adapters · Ping · Traceroute · DNS Lookup · Port Tester · Local Devices · Storage · Large File Finder · Processes ·
Windows Services · Event Log · Quick Commands · Diagnostic Session (+ notes) · Hash Calculator · Subnet Calculator. Every reporting tool has Copy all, Export (TXT/JSON) and,
with an active session, Add to session; tables add Copy selected via context menu. QA deep link: `--page ittools --tab <tool>` (tool keys: sysinfo, performance, network, ping, trace, dns, port, devices, storage, largefiles, processes, services, eventlog, commands, session, hash, subnet).

### Database
Migration **`ItToolsDiagnostics`** (additive): `DiagnosticSessions`, `DiagnosticResults` (tool, title, summary, outcome, `SectionJson`), `DiagnosticNotes` (text stored exactly as typed, ≤ 8000 chars); cascade delete from session.
Verified on a copy of a real library (all game rows identical before/after) and by `ItToolsMigrationTests`. The EF compiled model was regenerated.

### Permissions & safety
Arcadia still runs as a normal user (no manifest elevation). Elevation is requested per operation and only after an explanation + confirmation:
ending a process Windows refuses (`taskkill` via UAC), starting/stopping/restarting a service (`Start/Stop/Restart-Service` via UAC; only names from the service list),
and "PowerShell (Administrator)". Declined UAC prompts are reported, never retried or bypassed. The Security event log needs admin rights and shows "Access denied".
Ending system-critical processes shows an explicit warning. Nothing deletes files, edits the registry, installs drivers or scans ports; the device sweep pings only the
local subnet (≤ /22, 24 in parallel, one ping per address). No IT data leaves the computer.

### Localization
All strings in English and Arabic (≈520 new keys). German has no infrastructure yet, so it is not included; notes accept any language and use per-text direction.

### Tests
`ItToolsTests.cs`: subnet maths (incl. /0, /31, /32, masks), target parsing (URLs, IDN, IPv6, invalid), reverse names, ports, hash normalisation/comparison + known vectors,
ping statistics, formatting, reports (text alignment, JSON with Arabic/German), session store (exact notes, cascade), migration of an existing database, and local
integration checks (loopback ping, port open/refused, large-file scan, hashing, quick status). Total: 165 tests.
UI end-to-end (UI Automation): navigation, session start, ping + record, validation, DNS failure, port refused, traceroute, DNS, subnet, processes, services, event log, Security access denied, Arabic/German note, back to Games/Home.

### Known limitations
- DNS results show the TTL the resolver returns (the Hyper-V DNS proxy returns 0). Some DNS proxies answer one type malformed now and then; the tool retries and reports "Not answered: …" instead of failing.
- Disk activity needs the Windows performance counters; if they are disabled the Disk graph shows "Not available".
- Local Devices sweeps only networks up to /22; larger networks list the neighbour cache only. Devices with ICMP blocked appear only if Windows resolved their MAC.
- Traceroute uses ICMP; routers that do not answer show "No reply" (like tracert).
- Service control and process termination of protected items require accepting UAC; there is no remote-computer support.
- PDF export is not implemented (add an `IReportExporter`).

### Next recommended phase
Notes/Journal module (reusing the Bidi note editor and report exporters), then Tasks. For IT Tools: saved hosts/favourites, a scheduled "health snapshot", and PDF export.
## Speed pass (2026-09-27)

Stopped early by product decision ("it is ok if the app is a bit slow"): the changes below are kept, further tuning is not planned.

- **Startup marks** in the log (`Perf: startup ...`, `Perf: database init ...`, `Perf: home query ...`).
- **Database**: the migration step is skipped when every migration is already applied (it also diffed the whole model); EF **compiled model** in `src/Arcadia.Data/CompiledModels` — regenerate after any model change:
  `dotnet tool run dotnet-ef dbcontext optimize --project src/Arcadia.Data --startup-project src/Arcadia.Data --output-dir CompiledModels --namespace Arcadia.Data.CompiledModels`
- **Shared theme dictionaries** (`SharedResourceDictionary`): Palette/Typography/etc. are parsed once instead of once per including file.
- **ReadyToRun** release build (how the app should be run day to day):
  `dotnet publish src/Arcadia.App -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o <folder>`
- **Library virtualization**: grid (rows of cards) and list both use a recycling `VirtualizingStackPanel`; the list header stays put while scrolling. `GamesViewModel.Items` is replaced as a whole instead of item by item.
- **Artwork**: decoded bitmaps are cached against a 256 MB budget (LRU) and shown synchronously when already decoded; image headers are read once; the procedural scene is only painted when a game has no real artwork.
- **QA**: `--perf-scroll grid|list` opens the library, scrolls it end to end and logs timings.

Measured (warm start, this machine; bare WPF window = ~0.38 s):

| | Before | After (ReadyToRun) |
|---|---|---|
| Home visible with data | ~1.45 s | ~0.82–0.87 s |
| Database ready | ~1.25 s | before first frame |
| Home query | ~190 ms | ~90 ms |
| Library, 500 games, ready | all cards realized | ~350 ms, only visible rows exist |
## Phase 4 — Online details & artwork: IGDB + SteamGridDB (2026-09-27)

### Status
Builds with **0 warnings / 0 errors**; **99/99 tests pass** (+3 live API tests that run only when
`ARCADIA_IGDB_ID`, `ARCADIA_IGDB_SECRET`, `ARCADIA_SGDB_KEY` are set). Live end-to-end run through the real UI:
Save & Test (both services connected) → Find Details & Art (IGDB match, details, SteamGridDB art) → Apply (≈1–2 s).

### Implemented
- **Secrets**: `DpapiSecretStore` — keys encrypted for the current Windows user in `%AppData%\Arcadia\secrets.dat`;
  never in settings.json, logs or source (verified in the end-to-end run). Settings → **Online Services**: Client ID,
  Client Secret and API key (password boxes), **Save & Test**, **Remove keys**.
- **IGDB** (`IgdbClient`): Twitch client-credentials token cached until expiry, requests spaced for IGDB's 4 req/s limit,
  one retry on 401/429. Search filters bundles/DLC/packs/updates/editions (`game_type`, `version_parent`) and ranks exact
  title, then popularity. Details: summary, release date, genres + themes, developers/publishers, platforms, rating,
  cover, screenshots, Steam app id (`external_games`, supports both `category` and the newer `external_game_source`).
- **SteamGridDB** (`SteamGridDbClient`): game by Steam app id (exact) or title search; box art (600×900),
  capsule/cover (920×430, 460×215), heroes, logos — static, SFW only.
- **Find Details & Art** dialog: search, results with IGDB covers, details preview, per-kind art picker (Box art ·
  Cover · Banner · Logo; best candidate pre-selected, official logos preferred; click again = keep current).
  Opens automatically after adding a new game when keys are set; also in the game page, library card menu and Home ▾.
- **Apply**: description, developer, publisher, release date, genres merged into tags, IGDB/SteamGridDB ids, Steam
  launch link filled in when empty; chosen art downloaded in parallel into Media (logos kept as transparent PNG),
  replaced art deleted, everything rolled back if saving fails.
- **Display**: transparent logo over the game banner and on Home's featured art; portrait box art on the game page;
  Developer / Publisher / Released; library cards fall back cover → banner → box art.
- **Web images**: disk cache (`Library\Cache\web`), async downloads limited to 6 in parallel and de-duplicated per URL
  (fixes thread-pool starvation that made art slow to appear).
- **Single instance per library**: a second launch brings the running window to the front (210 ms) instead of
  starting a second tracker/watcher on the same database. Isolated `--data-dir` runs can coexist.
- Migrations `Phase4Metadata`, `Phase4MetadataLengths`.

### Known limitations (Phase 4)
- No bulk "match my whole library" yet (one game at a time).
- IGDB screenshots are fetched but not yet offered for import.
- Only English metadata (IGDB localisations not requested).

### Next recommended step
**Speed pass**: ReadyToRun publish, cache procedural artwork (BitmapCache), virtualize the library grid; target first frame < 0.6 s.
Then an optional "Match all games" bulk action with review.

---
## Phase 3 — Automatic sessions & captures (2026-09-27)

### Status
Builds with **0 warnings / 0 errors**; **96/96 tests pass**. Live end-to-end run (real process, real folder watcher):
launch → detected in 11 ms → header "Now playing" → capture imported into the playing game 1.5 s after it was written →
process exit → automatic session recorded → plate cleared. No warnings in the log.
Startup unchanged: first frame ≈ 1.0 s, Home populated ≈ 1.45 s (Release, warm) — background services start only after the database is ready.

### Implemented
- **Automatic play sessions** (`PlaySessionTracker` in Core — pure, fully unit-tested; `SessionTrackingService` in App):
  a session is "a game is running" = any process whose image lives in the game's install folder.
  - Folder resolution: Steam (`libraryfolders.vdf` + `appmanifest_{id}.acf`), Epic (`*.item` manifests), `.exe` targets,
    or **learned** from the first new windowed process after an Arcadia launch; editable in the game editor.
  - Launchers, redistributables, anti-cheat installers and anything under Windows are ignored; drive roots / Program Files are never learned.
  - Start time = the game process's own start time (correct even when detected late); end = last time seen; two missed scans
    tolerate launcher→game hand-offs. Sessions below the minimum (default 2 min) are discarded.
  - Scans every 3 s while playing or after a launch, every 10 s otherwise; `QueryFullProcessImageName` with limited rights (no elevation),
    paths cached per (pid, start time).
  - The active session is persisted (`Library\tracking.json`, heartbeat each minute) and resumed after an Arcadia restart.
  - Header **Now playing** plate (live timer, click → game page, × → end now); sessions list marks **Auto** sessions.
  - Optional detection of library games started outside Arcadia (Settings).
- **Capture folders** (`CaptureWatcherService`): FileSystemWatcher on configured folders (+ "Add detected folders": Steam `userdata\*\760\remote`,
  Xbox Game Bar `Videos\Captures`). New images are imported once fully written, never twice (`Screenshot.SourcePath`), Steam thumbnails ignored.
  Matching (`ScreenshotMatcher`): game being played (or ended < 15 min ago) → Steam app id in the path → title in Game Bar / NVIDIA file or folder names.
  Bursts are announced as one toast per game.
- **Drag-and-drop**: drop images onto a game's page (overlay) or onto Home's Screenshots panel (→ featured game).
- Migration `Phase3Tracking` (`Games.InstallDirectory`, `Screenshots.SourcePath` + index, `GameSessions.IsAutomatic`).
- Durations under an hour now read in minutes ("15 min" instead of "0.3 h").
- Perf instrumentation in the log (`Perf:` lines: first frame, database ready, Home visible).

### Known limitations (Phase 3)
- Games that run from a shared folder with other programs (rare) could be over-counted; set the game folder precisely in Edit.
- Titles launched through a store with no manifests we read (Ubisoft, EA, Battle.net) learn their folder on the first Arcadia launch.
- Unmatched captures are skipped (logged), not collected in an inbox.

### Next recommended step
1. **Speed pass** — ReadyToRun publish, cached procedural artwork (BitmapCache), virtualized library grid; target first frame < 0.6 s.
2. **IGDB + SteamGridDB** metadata/art (needs Twitch Client ID/Secret and a SteamGridDB API key — stored with DPAPI).

---
## Phase 2 — Games module (2026-09-27)

### Status
Builds with **0 warnings / 0 errors**; **69/69 tests pass**; UI Automation end-to-end run passes
(navigate → add game with validation → save → log session → live refresh → library).

### Implemented
- **Game Library** (sidebar *Games*): search (title/tags/platform), status filter chips with live counts,
  platform filter, sort (recently played, title, progress, playtime, recently added), grid ⇄ list view
  (remembered in settings), responsive card columns, right-click menu per game, empty/no-match states.
- **Game detail page**: hero banner (banner → cover → procedural art), status/platform/featured, actions
  (Continue Playing, Log Session, Edit, Feature on Home, Import Screenshots, Delete), readout strip
  (progress, playtime, sessions, achievements, last played, time to beat) and tabs:
  Overview · Sessions (12-week activity chart + session list with delete) · Journal (add / pin / delete) ·
  Screenshots (import, edit details, delete, full-size viewer with previous/next).
- **Dialogs** in Arcadia chrome (`DialogWindow` + DataTemplated view models): Add/Edit Game with inline
  validation, Log Session (day, duration, note, optional progress/objective), Screenshot details, Confirm.
- **Sessions**: new `GameSessions` table. Logging a session adds playtime, updates last played, moves
  Backlog → Playing and 100 % → Completed. Deleting a session refunds its playtime.
- **Media**: `MediaStore` copies imported images to `%LocalAppData%\Arcadia\Media\{covers,heroes,screenshots}`
  (paths stored relative to that root), validates by decoding, downsizes very large covers, writes 480 px
  JPEG thumbnails off the UI thread, and only ever deletes files inside its own folder.
- **Launching**: `Game.LaunchTarget` — store protocols (steam, epic, ubisoft, EA, battle.net, GOG, Xbox, heroic)
  or a full path to an .exe/.lnk/.url. Anything else (http links, scripts, relative paths) is rejected in the
  editor, in the catalog and again in the launcher.
- **Home** now uses real data paths: Continue Playing launches, ▾ menu has Game details / Log Session /
  Add screenshot, the featured title opens the detail page, the "Currently Playing" meter shows hours
  played per month from sessions. All views refresh through `LibraryChangedMessage` (WeakReferenceMessenger).
- **Upgrade path**: migration `Phase2Games`; existing sample libraries get deterministic sample sessions.
- **Accessibility fix**: sidebar items now navigate when selected by screen readers/UI Automation, not only on click.
- QA switches: `--game <id> [--tab sessions|journal|screenshots]`, `--dialog edit|log|shot|confirm`.

### Architecture additions
- Core: `GameSession`, `IGameCatalog` + `SessionLog`, `IMediaStore`, `LaunchTargets`, `Platforms`, `GamesSettings`.
- Data: `GameCatalog` (all library writes, with validation), sessions in the dashboard query.
- App: `GameActions` (shared workflows for Home and Games), `DialogService`, `MediaStore`,
  `GamesViewModel` / `GameDetailViewModel`, dialog view models.

### Data location change (Phase 2)
`%LocalAppData%\Arcadia` turned out to contain another, earlier "Arcadia" program's files (its own `arcadia.db`,
`settings.json`, `logs`). Arcadia now keeps all machine-local data in **`%LocalAppData%\Arcadia\Library`**
(database, Media, Logs, Cache) and never touches the parent folder. `DatabaseInitializer` also refuses to migrate any
database it did not create (`IncompatibleDatabaseException`, shown as a clear message on Home) — covered by tests.
Settings remain in `%AppData%\Arcadia\settings.json`.

### Known limitations (Phase 2)
- Playtime is logged manually; Arcadia does not yet watch the launched process to record sessions automatically.
- The library grid is not virtualized (fine for hundreds of games; revisit for thousands).
- No drag-and-drop import yet (file picker only); no bulk edit.
- Game metadata lookup (descriptions, covers) still needs a provider decision — see `IGameMetadataProvider`.

### Next recommended step — Phase 3
1. Automatic session tracking: when Arcadia launches a game, watch the process and log the session on exit.
2. Drag-and-drop screenshot import and a watched screenshots folder (Steam/NVIDIA/Xbox Game Bar captures).
3. Metadata provider (requires choosing a provider and an API key — user decision).
4. Then the next module (Notes/Journal as a standalone module, or Tasks).

---
## Phase 1 — Foundation (2026-09-27)

### Status
Shell, Home dashboard, localization (EN/AR with RTL), SQLite foundation, custom controls and visual QA tooling are in place.
Solution builds with **0 warnings / 0 errors**; **35/35 tests pass**.

### Run
```powershell
dotnet run --project src/Arcadia.App
```
or launch `src/Arcadia.App/bin/Debug/net10.0-windows/win-x64/Arcadia.exe`.

Useful switches:

| Switch | Purpose |
|---|---|
| `--page <key>` | Start on a section (`home`, `games`, `settings`, …) |
| `--lang ar` | Override the language for one run |
| `--data-dir <path>` | Use an isolated data folder (nothing touches the real profile) |
| `--capture out.png --size 1920x1080 --scale 1.5` | Render the window to PNG at a given size / display scale, then exit |
| `--art-sheet out.png` | Render every procedural artwork theme as a contact sheet |

### Architecture

```
Arcadia.sln
src/
  Arcadia.Core            Domain models, service contracts, formatting. No UI, no EF.
  Arcadia.Data            EF Core + SQLite: DbContext, migrations, library queries, sample seeder.
  Arcadia.Infrastructure  App paths, JSON settings, file logging, placeholder weather, DI wiring.
  Arcadia.UI              WPF control library: theme dictionaries, icon set, custom controls, procedural artwork.
  Arcadia.App             WPF executable: shell, views, view models, localization, startup, error handling.
tests/
  Arcadia.Tests           xUnit: domain, data (real migrations on in-memory SQLite), settings, resources.
tools/
  Arcadia.IconForge       Generates the icon set (Icons.xaml) and arcadia.ico from geometric primitives.
```

Dependencies point inward: `App → UI, Data, Infrastructure → Core`. `UI` does not know about `Core`.

#### Decisions
- **.NET 10 / WPF / MVVM** with CommunityToolkit.Mvvm (source-generated properties/commands) —
  removes boilerplate without adding a framework.
- **Generic Host** (`Microsoft.Extensions.Hosting`) for DI, logging and future hosted background services.
- **View-model-first navigation**: `ShellViewModel.Navigate(key)` sets `CurrentPage`; `App.xaml` DataTemplates map VM → View.
  Pages implementing `IActivatable` load lazily on first show.
- **Database**: SQLite at `%LocalAppData%\Arcadia\arcadia.db`, created via **EF migrations** (`MigrateAsync`) so the
  schema can evolve. Initialization runs on a background thread; pages await `StartupState.DatabaseReady`.
  Only `Games`, `JournalEntries`, `Screenshots` exist — no speculative schema.
- **Sample data** is flagged `IsSample` on every row; *Settings → Sample library → Remove* deletes only those rows.
  Seeding happens only into an empty database and can be disabled (`development.seedSampleData`).
- **Settings**: `%AppData%\Arcadia\settings.json` (roaming), written atomically; a corrupt file falls back to defaults
  and is preserved as `settings.json.corrupt`. No secrets are stored there (enforced by a test); API keys will go to
  DPAPI-protected storage when the first provider arrives.
- **Logging**: `Microsoft.Extensions.Logging` → Debug + a small non-blocking daily file logger
  (`%LocalAppData%\Arcadia\Logs`).
- **Errors**: dispatcher, task-scheduler and AppDomain handlers log everything; users see an Arcadia-styled dialog
  with a friendly message and "Open log folder". Stack traces are shown only in Debug builds.
- **Localization**: `.resx` (`Strings.resx`, `Strings.ar.resx`) + `LocalizationManager` (`ILocalizer`) +
  `{l:Loc Key}` markup extension (live bindings — language switches without restart). Arabic flips `FlowDirection`
  of the window (true RTL mirroring, including caption buttons). User-entered text uses `ui:Bidi.Text` so Latin
  titles stay intact in Arabic (WPF ignores Unicode embedding marks, so direction is set per TextBlock).
  Object icons never mirror; only directional glyphs do.
- **Artwork**: no third-party images ship. `ScenePainter` paints deterministic original landscapes (themes + seed:
  castles, mesas, neon city, voxel blocks, forests, planets, the header's lone wanderer). `ArtworkPresenter` shows a
  real image instead when a path is set (decoded off the UI thread at display size, cached, faded in) — the hook
  for user images and a future metadata provider. Header artwork is a presenter too, ready for a user image.
- **Icons**: 40 duotone vector icons generated by `tools/Arcadia.IconForge` from unions/exclusions/strokes on a
  24px grid (reproducible; edit `Program.cs`, re-run). Also produces `Assets/arcadia.ico`.
- **Fonts**: Windows-shipped only — Bahnschrift (display/stats), Segoe UI (body, Arabic), Segoe Script (accents).

#### Visual system
- Palette/typography/icons are centralized in `Arcadia.UI/Themes` (`Palette.xaml`, `Typography.xaml`, `Icons.xaml`,
  `StandardControls.xaml`, `Generic.xaml`). Each dictionary merges what it depends on.
- Distinct surface treatments rather than one card: `ArcadiaPanel` (bevelled console panel), `ArcadiaStatDisplay`
  (tinted slab + level meter), `ModuleTile` (lamp-lit desk illustration), artwork wells, list rows, status plates.
- Motion is 100–300 ms, centralized in `Motion` and switchable in Settings: nav key illumination, hover light,
  pressed dips, panel reveal stagger, page transitions, progress fill, level-meter rise, image fade.

### Implemented in Phase 1
- Custom chrome (drag, double-click maximize, resize, snap, min/max/restore/close, maximize overhang compensation,
  Win11 rounded corners + dark frame via DWM, per-monitor-V2 DPI manifest).
- Header banner: wordmark, emblem, tagline, category links (navigate), hand-lettered motto, sample weather, clock,
  search (Ctrl+F), notifications, settings, profile.
- Sidebar console with 12 sections, illuminated selected key, hover/pressed states, compact icon rail below 1280 DIP,
  scenic footer (hidden on short windows).
- Home: Featured Game (artwork carousel, journal plate, facts, split Continue button + menu, external search links),
  Games Overview (5 stat slabs, trends, playtime/achievements), Recent Games (click → feature), Screenshots & Notes,
  Upcoming/Backlog, 5 module tiles. Wide 3-column layout ≥ 1040 DIP content width, 2-column below, vertical scroll when short.
- Elegant placeholder screens for all unimplemented modules (localized roadmap).
- Settings: language (EN/AR, live), animations, start maximized, data/settings/log folders, privacy toggles
  (stored for later), sample-data removal with two-step confirmation.
- Toast notifications for actions that belong to later phases (launching games, search, importing screenshots).

### Verified
- Rendered and inspected: 1920×1080 @100%, 1366×768, 1920×1080 @150%, 3840×2160 @200%, Arabic RTL,
  Settings, placeholder; live window on a 2560×1440 @150% display (hover, maximize, clean close). Cold start to
  visible window ≈ 0.9 s.

### Known limitations
- Hovering the maximize button does not show the Windows 11 Snap Layouts flyout (requires custom `WM_NCHITTEST`
  handling; normal snapping via drag and Win+Arrow works).
- Weather is sample data (tooltip says so); search, notifications, profile and "Continue Playing" show a
  "later phase" notice.
- Game sparkline trends are cumulative library size by month, derived from `AddedAtUtc` — real session history
  arrives with GameSessions.
- Only one theme ("Midnight"); templates use StaticResource brushes, so a live theme switch will need
  DynamicResource in the relevant templates.
- German localization not started (infrastructure ready: add `Strings.de.resx` and a `LanguageOption`).
- Header/cover artwork paths are not yet user-editable in the UI (the data model and presenters support them).

### Next recommended step — Phase 2: Games module
1. Games library view (grid/list, filters by status/platform, sort, search) using the existing `Game` model.
2. Add/edit game dialog, including choosing a local cover/hero image (copied into `%LocalAppData%\Arcadia\Media`
   with thumbnail generation).
3. `GameSessions` table + manual "log session", feeding playtime and real overview trends.
4. Journal and screenshot import (file picker / folder watch), tags and locations.
5. Then evaluate an authorized metadata provider behind `IGameMetadataProvider` (needs an API-key decision).
