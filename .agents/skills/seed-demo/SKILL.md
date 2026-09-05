---
name: seed-demo
description: Prepare synthetic-data Local Monitor demos and UI screenshots on explicit request.
---

# Seed Demo Data Into the Local Monitor

Use only for an explicit demo-data, visual-verification, or screenshot request. Synthetic OTLP still mutates its destination: never seed a real capture DB or stop the ordinary capture instance to prepare a demo.

## Isolated procedure

Run from the repository root. Use the existing lifecycle isolation in `scripts/local-monitor/README.md`: one new absolute runtime root under ignored `tmp`, an unused DB, and an available loopback HTTP URL. Keep these variables for the entire task. The example port is a candidate, not guaranteed free; a startup conflict means choose another URL and fresh root, never stop its owner or fall back to the default runtime.

```powershell
$runtimeRoot = Join-Path (Get-Location).Path ('tmp\monitor-demo-' + [guid]::NewGuid().ToString('N'))
$dbPath = Join-Path $runtimeRoot 'monitor.db'
$monitorUrl = 'http://127.0.0.1:4321'
if (Test-Path -LiteralPath $runtimeRoot) { throw 'Choose a new unused demo runtime root.' }

$started = @(pwsh scripts\local-monitor\start.ps1 -Mode DotnetRun -RuntimeRoot $runtimeRoot -DbPath $dbPath -Url $monitorUrl -NoBrowser -WaitReady)
$startExit = $LASTEXITCODE
$started
if ($startExit -ne 0 -or -not ($started -cmatch '^started (ready|degraded)$')) {
    throw 'Demo startup was not verified; do not seed.'
}

$status = @(pwsh scripts\local-monitor\status.ps1 -RuntimeRoot $runtimeRoot)
$statusExit = $LASTEXITCODE
$status
$expected = @('running: yes', "URL: $monitorUrl", "DB path: $dbPath", 'mode: dotnet-run')
$missing = @($expected | Where-Object { $status -cnotcontains $_ })
if ($statusExit -notin @(0, 3) -or $missing.Count -ne 0 -or -not ($status -cmatch '^readiness status: (ready|degraded)$')) {
    throw 'Demo instance identity/readiness was not verified; do not seed.'
}

pwsh scripts\demo\seed-monitor-mock-data.ps1 -MonitorUrl $monitorUrl
if ($LASTEXITCODE -ne 0) { throw 'Seeding failed; do not reseed this DB or report success.' }
```

The explicit runtime root makes lifecycle commands validate state/PID/process ownership. Status code `3` reports a disabled startup task, not demo failure; accept it only with all identity/readiness fields above. A `runtime_state_mismatch`, `already_running`, failed start, or mismatched status is not permission to reuse a DB or seed an arbitrary reachable endpoint.

Seed each DB once, including after partial failure. For another attempt choose a new unused root/DB; never automatically reuse or delete an existing DB. Do not change capture routing or register a startup task for this demo.

## Evidence and handoff

Distinguish POST acceptance, completed projection, and requested UI observations. `ok` output and the script's fixed sleep establish neither projection completion nor UI acceptance. Inspect relevant projection/readiness evidence and the actual requested surface under the current `docs/spec.md` / owning UI specification. `/` is Repository selection, not the retired Overview/trace-list/drawer promise. OTLP fixtures alone do not prove Session lifecycle, Skill invocation, Sub-agent coverage, or whole-product acceptance; report unavailable relationships without fabricating them.

For a browsable demo, leave this task-owned instance running and report its loopback URL, runtime identity, observed surface, and unverified scope. When cleanup is requested or the task requires a temporary-only run, stop only this verified task-owned instance:

```powershell
pwsh scripts\local-monitor\stop.ps1 -RuntimeRoot $runtimeRoot
if ($LASTEXITCODE -ne 0) { throw 'Demo stop failed; report the remaining runtime state.' }
```

Keep the DB for explicit user-managed disposal; do not delete it automatically. This procedure bounds the instructed target; the seed script does not itself certify that a caller-supplied destination is disposable.
