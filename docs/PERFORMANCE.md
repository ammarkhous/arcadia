# Arcadia — Performance (1.0.0)

Machine: development laptop, 4 logical cores, 6 GB RAM (often < 2 GB free), Windows 11. Release build, framework-dependent,
demo library (`p9d`, 20 games) unless noted. Scripts in the session scratchpad: `baseline.ps1`, `startup.ps1`, `stalls.ps1`.
Working set includes shared WPF/.NET pages; private bytes are Arcadia's own. No forced GC for the numbers.

## Before → after (Phase 10)

| Measure | Baseline | 1.0.0 |
|---|---|---|
| Cold start, first frame | 3,146 ms | 1,937 ms |
| Warm start, first frame | 2,036 ms | 1,191 ms |
| Window shown → first frame | ~940 ms | ~100–130 ms |
| Home (after start) | 211 MB WS / 82 MB private | 210 / 82 |
| After visiting all 12 modules | 351 MB / — | 319 / 149 |
| Navigation loops (10×10 modules) | plateau ~418 MB | plateau ~381 MB, handles fall |
| Idle, Home visible | 0.13 % CPU | 0.17 % (30 s sample; noise level) |
| Minimized | 16 ms CPU / 60 s | 0 ms / 60 s |
| Minimized + game (Gaming Mode) | 31 ms / 60 s | 47 ms / 60 s (process scan every few s) |
| GPU while minimized | counter empty (no rendering) | same |

Startup stages (warm): app entry 227 · restore check 305 · host 379 · settings+language 432 · window constructed 620 ·
shown 1,064 · first frame 1,191 ms. Home's data appears ~0.4 s after the first frame.

## Large library (1,000 games · 10k tasks · 5k notes · 5k transactions · 1k documents · 300 projects · 200 trips · 500 places · 5 years of events)

| Measure | Before | After |
|---|---|---|
| Search-index rebuild (first open of such a library) | 145 s (blocked "database ready") | 3.2 s |
| Global search (palette), typical inputs | — | 0.7–13 ms |
| Oracle commands | notes "about …" 380 ms | 1–115 ms |
| Travel first visit (UI thread) | 2.5 s, 17.7k visuals | 0.4 s, 2.3k visuals |
| Projects first visit | 1.5 s (300 rows) | ~0.5 s (40 rows + "Show all") |
| Calendar first visit | 0.36–1.1 s | 0.24 s |
| Documents first visit | 0.8–1.0 s | ~0.45 s |
| Memory after a full tour | — | ~300 MB WS / ~150 MB private |

Remaining first-visit render passes of 0.1–0.5 s are WPF layout of rich pages on this slow machine; revisits are faster
(pages are cached).

## What changed

See `DEVELOPMENT.md` → Phase 10: background services after the first frame; Home Today queries off the UI thread; keyed
FTS rowids + single-transaction rebuild + one-time re-key; ids-only FTS matches; search providers on a worker; lazy page
sections; paged project list; map brush caching; bounded logs.

## How to measure

* `ARCADIA_QA_STARTUP=1` — logs UI-thread work until the first frame.
* `ARCADIA_QA_STALLS=1` — logs every UI-thread operation ≥ 50 ms and, for long renders, the heaviest lists by visual count.
* `baseline.ps1 [-Loops N]` — startup, memory per module, idle/minimized/Gaming Mode CPU.
* `StressDataGenerator` (tests, `ARCADIA_STRESS_DB=<new file>`) — the large library above.
