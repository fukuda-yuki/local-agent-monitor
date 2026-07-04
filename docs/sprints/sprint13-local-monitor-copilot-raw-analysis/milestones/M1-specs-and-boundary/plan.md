# Sprint13 M1 - Specs And Boundary Plan

## Goal

Promote the raw-analysis behavior into current source-of-truth documents before
treating sprint-local notes as implementation authority.

## Steps

1. Update product requirements to define Local Monitor Copilot raw analysis.
2. Update technical spec public interfaces with the analysis routes and .NET SDK
   service.
3. Update security/data-boundary specification for local raw analysis and
   repository-safe summary export.
4. Update telemetry ingestion and raw-store specs for route and persistence
   contracts.
5. Add decision D035 and roadmap entry.

## Acceptance

The docs distinguish local raw analysis, process-internal .NET SDK raw tools,
and repository-safe summary output. `/api/monitor/*`, SSE, and the existing Canvas
adapter stay sanitized-only.
