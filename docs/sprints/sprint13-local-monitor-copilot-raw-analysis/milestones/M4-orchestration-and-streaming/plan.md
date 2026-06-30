# Sprint13 M4 - Orchestration And Status Plan

## Goal

Let Local Monitor create local analysis runs and let the .NET SDK runner move them
through the lifecycle without embedding raw payloads in the start request.

## Steps

1. TraceDetail starts a queued run with trace id, optional span/raw record refs,
   focus, and no raw body.
2. .NET SDK tools fetch raw later through process-internal C# tool calls.
3. The runner records progress and terminal status.
4. Result route shows local raw-derived analysis output with no-store headers.

## Acceptance

Run state and raw result persistence are local-only. Repository-safe export uses
a separate route and never copies the raw result markdown.
