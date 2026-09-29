# Arcadia 1.0.0 — Release notes (2026-09-29)

`Arcadia-Setup-1.0.0-x64.exe` — SHA-256 `17c03a8847f2f4a0423a22b817a4bf811a9df9fb09e06c535a136d3d52f7a5b0`
Self-contained (no .NET install needed), Windows 10 1809+ x64, per-user install, not code-signed.

**Modules:** Home, Games (play tracking, screenshots, journal), IT Tools, Projects, Documents, Travel, Calendar, Tasks,
Notes, Finance, Oracle (deterministic, offline, EN/DE/AR commands), Automation. UI in English and Arabic (RTL).

**New in 1.0:** first-run welcome; Backup & recovery (verified `.arcadia-backup` archives, safe restore with rollback,
automatic backups); Diagnostics & maintenance (check database, clear cache, reset settings); safety copy on every update;
remembered window position (never off-screen); faster start (warm first frame ~1.2 s) and large-library performance
(search index rebuild 145 s → 3 s; Travel first visit 2.5 s → 0.4 s); bounded logs.

**Known limitations:** German UI not yet translated; SmartScreen warns once (unsigned); referenced-file backups copy files,
not folders. Upgrading from 0.1.0 keeps all data (tested).
