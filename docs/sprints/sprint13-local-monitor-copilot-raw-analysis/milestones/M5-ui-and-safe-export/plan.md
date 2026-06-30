# Sprint13 M5 - UI And Safe Export Plan

## Goal

Expose raw analysis from TraceDetail and provide repository-safe summary export.

## Steps

1. Add `Analyze raw trace with Copilot` panel under raw-default TraceDetail.
2. Add focus values: `latency`, `tokens`, `cache`, `errors`, `tool-usage`,
   `agent-flow`.
3. Use a CSRF header on the browser start request.
4. Show queued/running run feedback and local .NET SDK result polling.
5. Generate repository-safe summary from run metadata and evidence refs only.

## Acceptance

The UI is absent under `--sanitized-only`; safe summary output excludes synthetic
raw markers in automated tests.
