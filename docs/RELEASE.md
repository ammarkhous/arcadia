# Arcadia — Release process

## Packaging decision: self-contained

The installer ships a **self-contained** win-x64 build (the .NET 10 runtime and WPF inside `Program Files\Arcadia`,
ReadyToRun-compiled):

* nothing to install first, and no dependency on whichever .NET the machine has (or gains/loses with Windows Update);
* ReadyToRun shortens cold start;
* cost: a larger download (~60 MB setup, ~150 MB installed) — acceptable for a desktop app that is installed once.

Framework-dependent publishes remain for development (`scratchpad` run copies). Settings → Diagnostics shows which one runs
("self-contained" / "uses installed .NET").

## Versions

`Directory.Build.props` holds `<VersionPrefix>1.0.0</VersionPrefix>`. It flows into the assemblies, the About/Diagnostics
panel, the log header ("Arcadia 1.0.0 starting"), every backup manifest (`arcadiaVersion`) and the installer
(`AppVersion`, file version 1.0.0.0). Release candidates add a suffix at build time only:

```powershell
installer\build-installer.ps1 -Suffix rc.1   # → dist\Arcadia-Setup-1.0.0-rc.1-x64.exe (+ .sha256)
installer\build-installer.ps1                # → dist\Arcadia-Setup-1.0.0-x64.exe      (+ .sha256)
```

The script publishes Release (self-contained, R2R, no PDBs), refuses output that contains `*.pdb`, `settings.json`,
`secrets.dat` or `*.db`, runs Inno Setup 6 and writes a SHA-256 file next to the setup.

## Update strategy

Manual: download a newer setup and run it. Inno Setup upgrades in place (same AppId; running Arcadia is closed first).
User data is never in the install folder. On the first start of the new version Arcadia copies the database to
`Library\Backups\arcadia-before-update-<version>-*.db`; schema migrations additionally write `arcadia-before-<Migration>-*.db`.
Optional automatic backup "After each Arcadia update" writes a full `.arcadia-backup`.

## Release configuration (verified for 1.0.0)

* Release build: no debug overlays; sample data is never seeded (compile-time guard; the setting defaults to off).
* Logging: Information level, EF Core at Warning; file logs bounded (14 days, 50 MB total, 20 MB per day); no note text,
  amounts, keys or typed Oracle text are logged.
* No secrets in source or settings; API keys only in `%AppData%\Arcadia\secrets.dat` (DPAPI, current user).
* QA switches remain for support (`--capture`, `--art-sheet`, `--data-dir`) — capture outputs are restricted to `.png` files.
* Icon, product name, version and copyright set in the executable and the installer.
* No telemetry, no cloud, no AI/paid APIs. Online services (weather, map tiles, game artwork, place search) are optional.

## Release checklist

1. `dotnet build Arcadia.sln -c Release` — 0 warnings, 0 errors.
2. `dotnet test tests/Arcadia.Tests` — all pass.
3. End-to-end scripts (development machine): P7 finance, P8 Oracle, P9 automation, P10 first run + backup/restore.
4. Large-library check (`StressDataGenerator`, `stalls.ps1`): no UI-thread operation over ~0.5 s on first visit.
5. Performance baseline (`baseline.ps1`) — compare with `PERFORMANCE.md`.
6. Build the installer with the RC suffix; install over the previous version; confirm the library, settings and keys
   are intact, the update backup exists, and Diagnostics shows the new version.
7. Uninstall (answer **No**): data folders remain; reinstall: everything is back.
8. Clean-machine test (below). 9. Laptop and desktop tests (below).
10. No blocking issue → build without suffix → `1.0.0`; record the SHA-256; write release notes; tag `v1.0.0` if the
    repository is used. Nothing is published anywhere.

## Tests that need other machines (run by the owner)

Windows Sandbox is not available on the development PC, so these are manual.

**Clean machine** (a fresh Windows 10 1809+ / 11 user or VM, no .NET installed):
1. Copy `Arcadia-Setup-1.0.0-x64.exe` and its `.sha256`; check `Get-FileHash -Algorithm SHA256`.
2. Install per-user (no admin prompt expected). SmartScreen may warn (unsigned build) → More info → Run anyway.
3. Arcadia starts with the welcome; choose language, keep the default folder, finish. Expect an empty library.
4. Add a game, a project, a task, a note, an account with one transaction. Settings → Back up now. Close.
5. Start Menu → Arcadia starts in under ~3 s. Settings → Restore the backup → restart → data intact.
6. Uninstall → answer No → `%LocalAppData%\Arcadia\Library` still exists.

**Laptop (16 GB, RTX 3060, battery):** install over any existing build; run a real game for 30+ minutes with Arcadia
minimized — Gaming Mode turns on/off with the game, the session is recorded once, no notification pops up mid-game;
unplug power in "Automatic" mode → Power Saver; check 125–150 % scaling and an external monitor unplugged while
Arcadia is closed (it must reopen on the laptop screen).

**Desktop (32 GB):** 2560×1440 or 4K at 150–200 %, two monitors; Arcadia remembers its window on the second monitor;
open every module; Settings → Check database; leave it running overnight (memory in Diagnostics should stay flat).
