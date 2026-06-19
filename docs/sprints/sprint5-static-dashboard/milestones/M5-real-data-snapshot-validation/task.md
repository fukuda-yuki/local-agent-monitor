# M5: Real-data Snapshot Validation

## Goal

Validate the Sprint5 static dashboard path for real-data-derived aggregate input without committing raw trace content or generated snapshots to `main`.

## Scope

- Treat `artifacts/dashboard-input/` as the local staging path for M5 input files.
- Keep staged JSON files ignored by git to avoid accidental commits of real-data-derived input.
- Allow only aggregate metrics, reference IDs, classification attributes, `user.id`, and `user.email` into the dashboard dataset and static dashboard.
- Preserve `/latest/` and `/YYYY-MM-DD/` snapshot layout in the generated Pages artifact.
- Validate that raw prompt / response / system prompt / tool arguments / tool results, source fragments, credentials, authorization headers, sensitive bundle content, and local sensitive bundle paths are removed from static dashboard output.

## Implemented Scope

- Added `artifacts/dashboard-input/README.md` as the staging contract while keeping JSON inputs ignored.
- Tightened `generate-static-dashboard` sanitization for real-data-shaped risky keys and credential-like string values.
- Added automated coverage for a real-data-shaped dashboard snapshot containing user identity fields, repo snapshot metadata, raw content-like fields, authorization material, and sensitive bundle local paths.

## Validation

- `dotnet build CopilotAgentObservability.slnx` succeeded.
- `dotnet test CopilotAgentObservability.slnx` succeeded.
- `git check-ignore -v artifacts/dashboard-input/measurements.json` confirmed dashboard input JSON remains ignored.
- `git check-ignore -v artifacts/dashboard-input/README.md` confirmed the staging README is not ignored.
- For live M5 validation, place sanitized input files under `artifacts/dashboard-input/`, then run the same generation commands used by `.github/workflows/static-dashboard-pages.yml`.

## Completion Criteria

- Static dashboard output keeps `user_id`, `user_email`, and `repo_snapshot`.
- Static dashboard output removes raw content, credential-like values, and sensitive local paths.
- Generated snapshots remain under ignored local `tmp/` or the Pages branch/artifact path, not `main`.
