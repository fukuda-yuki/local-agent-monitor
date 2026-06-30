# Sprint13 M2 - Persistence And Routes Plan

## Goal

Add local analysis run persistence and raw-bearing Local Monitor routes outside
`/api/monitor/*`.

## Steps

1. Add additive SQLite tables for analysis runs, events, and safe summaries.
2. Add run lifecycle states: `queued`, `running`, `succeeded`, `failed`,
   `canceled`, `timed_out`.
3. Add CSRF-protected browser start route.
4. Dispatch accepted runs to the .NET SDK runner without embedding raw in the browser request.
5. Add `--sanitized-only` negative coverage.

## Acceptance

Raw-returning tools are process-internal C# tools for the .NET SDK runner, not
public HTTP routes.
