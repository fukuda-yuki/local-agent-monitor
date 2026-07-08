# Dashboard Publishing Specification

## Scope

This layer defines dashboard dataset generation and static HTML generation.

## Generation Flow

```text
measurements + optional raw/candidate outputs
  -> generate-dashboard-dataset
  -> dashboard dataset JSON / CSV tables
  -> generate-static-dashboard
  -> index.html + dashboard-data.json
```

## Commands

```text
config-cli generate-dashboard-dataset <measurements.csv|measurements.json> [--raw <raw-store.db|raw-otlp.json>] [--diagnosis-candidates <input.csv|input.json>] [--improvement-candidates <input.csv|input.json>] [--auto-decisions <input.csv|input.json>] [--time-bucket <day|hour|week>] [--csv-dir <output-dir>] [--json <output.json>]
config-cli generate-static-dashboard <dashboard-dataset.json> --out-dir <output-dir> [--snapshot-date <YYYY-MM-DD>] [--title <title>]
```

## Static Artifact

`generate-static-dashboard` writes:

```text
index.html
dashboard-data.json
```

Rules:

- `index.html` is static and requires no server-side API.
- `dashboard-data.json` is a sanitized copy of the input dashboard dataset.
- `--out-dir` is required.
- `--snapshot-date` defaults to UTC date.
- `--title` defaults to `Agent Workflow Observability`.

## Client-side Interaction

Initial controls:

- filter by date。
- filter by user。
- filter by client。
- filter by experiment。
- filter by variant。
- filter by status。
- free text search。
- table sort。

Initial sections:

- Run Overview。
- Agent / Tool Behavior。
- Prompt / Skill / Instructions。
- Baseline vs Variant。
- Diagnosis / Improvement Loop。
- Collection Health。
- Outcome Linkage Candidate placeholder。

## Safety

Dashboard artifacts must not contain raw prompt / response, tool arguments / results, source fragments, credentials, Base64 authorization headers, sensitive bundle content, or sensitive bundle local paths.
