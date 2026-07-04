# Dashboard Input Staging

This directory is the agreed staging path for Sprint5 static dashboard inputs used by the GitHub Actions workflow and local M5 validation.

Allowed local input file names:

- `measurements.json`
- `raw-otlp.json`
- `diagnosis-candidates.json`
- `improvement-candidates.json`
- `auto-decisions.json`

The JSON files are intentionally ignored by git. Do not commit raw prompt, response, system prompt, tool arguments, tool results, source snippets, credentials, secrets, authorization headers, sensitive bundle content, or local sensitive bundle paths here.

`measurements.json` may contain real-data-derived aggregate values, reference IDs, classification attributes, `user.id`, and `user.email` when the data handling decision for the run allows it.
