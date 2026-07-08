# Canvas VCS repository metadata bug cards

Scope reviewed: Local Monitor projection to Canvas helper / bounded action DTOs
for repository labels.

Source of truth checked:

- `docs/requirements.md`
- `docs/spec.md`
- `docs/specifications/layers/telemetry-ingestion.md`
- `docs/specifications/layers/raw-store-normalization.md`
- `docs/specifications/security-data-boundaries.md`
- `docs/decisions.md` D040
- OpenTelemetry semantic conventions 1.43.0 VCS attributes:
  `vcs.repository.name` and `vcs.repository.url.full`

User decision captured:

- Existing compatibility for the repository-name source is not required.
- Do not keep a dual `repo.name` / `vcs.repository.name` read path when fixing
  this bug.

Validation attempted while filing:

- Not run. This file records the issue only; no product code changed.

## Fix-unit index

| Card | Severity | Fix unit | Status |
| --- | --- | --- | --- |
| CM-1 | Medium | Canvas repository label source attribute | Fixed |

---

<a id="CM-1"></a>

## CM-1 — Canvas repository labels ignore OpenTelemetry VCS repository attributes — Medium

Status: Fixed. Local Monitor now derives `repository_name` from
`vcs.repository.name` only; `repo.name` is intentionally ignored for repository
labels.

### Problem

Canvas helper / bounded action DTOs can display `unknown repository` even when
the incoming OpenTelemetry resource carries standard VCS repository metadata.
The current projection reads only the repository-specific custom attribute
`repo.name`, while OpenTelemetry defines the VCS repository display attribute as
`vcs.repository.name`.

### Source of truth conflict

Current repository specifications and D040 say Local Monitor derives
`repository_name` from `repo.name`. OpenTelemetry semantic conventions define:

- `vcs.repository.name` as the human-readable repository name.
- `vcs.repository.url.full` as the canonical repository URL.

The implementation follows the current repository specification, so the bug fix
must update the current specs first instead of silently changing only code.

### Observed implementation

- `MonitorProjectionBuilder` reads `repo.name` from OTLP resource attributes and
  stores the sanitized value as `repository_name`.
- Canvas helper code reads only the already-projected `repository_name`; it does
  not inspect raw OTLP resource attributes.
- Tests currently seed `repo.name` and assert repository metadata appears in
  Local Monitor and Canvas surfaces.

### Impact

Users who rely on OpenTelemetry VCS semantic attributes cannot see the
repository name in Canvas. The display falls back to `unknown repository`, which
defeats the Sprint16 cross-repo metadata purpose for standard OTel payloads.

### Reproduction

Ingest a synthetic OTLP trace whose resource attributes include:

```json
[
  { "key": "vcs.repository.name", "value": { "stringValue": "copilot-agent-observability" } }
]
```

and do not include `repo.name`.

Expected: Local Monitor projection stores `repository_name` as
`copilot-agent-observability`, and Canvas helper surfaces display that label.

Actual: `repository_name` remains null, and Canvas helper surfaces display
`unknown repository`.

### Fix constraints

- Replace the repository-name source with `vcs.repository.name`.
- Do not preserve `repo.name` as a compatibility fallback.
- Do not expose `vcs.repository.url.full` in Canvas helper / bounded action DTOs
  unless a separate product/security decision explicitly allows repository URLs
  in that surface.
- Keep the existing sanitization guard before storing or emitting
  `repository_name`.
- Do not backfill existing projected rows.
- Keep the bug scope to the repository label unless the specs are explicitly
  updated for workspace or snapshot fields.

### Suggested fix path

1. Update the current source of truth:
   - `docs/requirements.md`
   - `docs/spec.md`
   - `docs/specifications/layers/telemetry-ingestion.md`
   - `docs/specifications/layers/raw-store-normalization.md`
   - `docs/specifications/security-data-boundaries.md`
   - `docs/decisions.md` D040 or a new superseding decision
2. Change `MonitorProjectionBuilder` to read `vcs.repository.name` for
   `repository_name`.
3. Update Local Monitor projection tests and Canvas helper contract tests to use
   `vcs.repository.name`.
4. Remove assertions or fixtures that depend on `repo.name` as the repository
   label source.

### Tests to add/update

- Projection builder test: `vcs.repository.name` populates `RepositoryName`.
- Projection builder test: `repo.name` alone no longer populates
  `RepositoryName`.
- Sanitization regression: unsafe `vcs.repository.name` values are dropped.
- API/summary/Canvas contract tests: emitted field remains
  `repository_name`, but the source fixture uses `vcs.repository.name`.

### Validation

Run after the fix:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```
