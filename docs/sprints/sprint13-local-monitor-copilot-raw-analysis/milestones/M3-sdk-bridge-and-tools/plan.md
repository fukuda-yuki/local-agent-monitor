# Sprint13 M3 - .NET SDK Service And Tools Plan

## Goal

Add an in-process .NET GitHub Copilot SDK analysis service that exposes raw
analysis tools for run-scoped Local Monitor analysis.

## Steps

1. Create the Local Monitor .NET SDK runner seam.
2. Register process-internal raw-returning tools:
   - `get_raw_trace`
   - `get_raw_record`
   - `get_raw_span_context`
3. Register bounded summary tools:
   - `get_trace_summary`
   - `get_trace_span_tree`
   - `get_cache_summary`
4. Keep repository-safe summary generation separate from raw-returning tools.
5. Do not add Node extension files, `package.json`, lockfiles, or `node_modules`.

## Acceptance

The .NET runner has a process-internal tool set for all six tools. Live SDK
invocation remains a validation item when the GA package/runtime is available.
