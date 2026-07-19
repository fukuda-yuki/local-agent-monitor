---
name: seed-demo
description: Use when the user wants to see the Local Ingestion Monitor UI with synthetic demo data — seeding demo traces, visually verifying UI changes, or preparing a demo/screenshot session
---

# Seed Demo Data Into the Local Monitor

Start a monitor on a fresh throwaway database, post the synthetic demo
payloads, and point the user at the UI. Every payload under
`scripts\demo\payloads\` is fully synthetic (demo-prefixed trace ids, fake
identity), so seeding is safe against any local monitor instance.

## Critical rules

- **Never seed the real capture DB.** `scripts\local-monitor\start.ps1`
  defaults `-DbPath` to the runtime `raw-store.db` (the user's real
  captured data). Always pass a fresh path under `tmp\`.
- **Seed each database once.** Re-seeding the same DB re-ingests the same
  trace ids and duplicates spans in the projection. For a clean rerun:
  stop the monitor, delete the tmp DB (or pick a new path), start again.
- Loopback URLs only (the scripts enforce this).

## Steps

1. If a monitor is already running (`pwsh scripts\local-monitor\status.ps1`),
   stop it first so seeding cannot hit the wrong database:
   `pwsh scripts\local-monitor\stop.ps1`
2. Start on a fresh demo DB and wait for ready:

   ```powershell
   pwsh scripts\local-monitor\start.ps1 -DbPath tmp\monitor-demo\monitor.db
   ```

   Expected output: `started ready` (or `started degraded` right after boot).
3. Seed the demo payloads:

   ```powershell
   pwsh scripts\demo\seed-monitor-mock-data.ps1
   ```

   Expected: one `ok <payload>` line per file, then `Seeded 9 demo traces.`
4. Tell the user to open `http://127.0.0.1:4320/` — overview KPIs, trace
   list, drawer, and span detail should all show demo data.
5. When done: `pwsh scripts\local-monitor\stop.ps1`, then remove
   `tmp\monitor-demo\` if a clean slate is wanted.

## Troubleshooting

- `already_running` from start.ps1 — an instance is already up at that
  URL; stop it before starting the demo instance (step 1).
- `port_already_in_use` — something else owns 4320; pass the same
  alternate loopback URL to both scripts (`-Url` / `-MonitorUrl`).
- Seed script reports "not reachable" — the monitor is not started at
  that URL yet; check step 2 output.
