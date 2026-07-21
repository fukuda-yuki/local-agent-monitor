# Issue #58 Repository Metadata Live Validation

Status: repository-safe implementation and synthetic live HTTP validation
complete; real GitHub Copilot producer payload validation remains blocked by
external authorization and producer availability.

This record contains attribute keys, counts, scopes, fixed status tokens,
tool versions, booleans, commands, and test totals only. It contains no raw
attribute value, repository label, repository URL, owner, credential, identity,
prompt/response, tool body, or machine-specific path.

## Execution basis

- Implementation and live-execution SHA:
  `6c92070ef28b27916784cfa906f0a13afde295b8`
- Wave 1 kickoff SHA: `1f624175d2de293756181f86c0365cff1ab080f7`
- Execution date: 2026-07-21
- Environment: Windows, .NET SDK `10.0.300-preview.0.26177.108`
- Read-only local version inventory: VS Code `1.129.1`; GitHub Copilot CLI
  `1.0.73`; the queried VS Code CLI profile did not expose an official GitHub
  Copilot extension.
- Capture policy: structural repository-safe metadata only. Synthetic raw
  records and their temporary SQLite database were removed after validation.

Tool/version inventory is provenance only. It does not prove that those tools
emitted the synthetic payloads below and is not a compatibility allowlist.

## Evidence status by surface

| Surface | Evidence state | Version basis | Result / blocker |
| --- | --- | --- | --- |
| Local Monitor repository metadata diagnostics | `live` | execution SHA above | Three repository-safe synthetic OTLP records were accepted over loopback under `--sanitized-only`; diagnostics and sanitized trace projection assertions passed. |
| VS Code GitHub Copilot Chat-shaped fixture | `live` | VS Code version observed out of band | Key inventory, `metadata_present`, label-present true, fallback false, and value-marker absence passed. This is synthetic shape evidence, not a real producer payload. |
| GitHub Copilot CLI-shaped fixtures | `live` | CLI version observed out of band | Unsafe URL shape rejection and canonical GitHub HTTPS fallback were both observed; the allowlisted record reported `url_fallback_used`, label-present true, and fallback true. This is synthetic shape evidence, not a real producer payload. |
| Issue #58 automated fixture corpus | `reused` | execution SHA above | Focused status, URL rejection, duplicate-key, bounds, retention, and UI fixtures passed and support the live assertions below. |
| Sprint16 Canvas validation | `reused` | historical evidence only | Reused only for the existing bounded Canvas/`unknown repository` boundary. It is not accepted as Issue #58 producer inventory or URL-fallback evidence. |
| Real VS Code GitHub Copilot Chat payload | `blocked_external` | VS Code executable observed; official extension unavailable in queried CLI profile | No authorized editor environment mutation or external Copilot interaction was in scope, so no real producer payload was captured. |
| Real GitHub Copilot CLI payload | `blocked_external` | CLI executable observed | A real authenticated external Copilot execution was not authorized; no producer payload was captured. |
| GitHub Copilot App / SDK producer payload | `not_attempted` | unavailable | Optional Issue #58 surface; no supported runtime or authorized external execution was provided. |
| Multiple repositories open concurrently | `not_attempted` | unavailable | Optional Issue #58 scenario; no authorized live editor session was provided. |

`blocked_external` and `not_attempted` rows are not converted into passes by
the synthetic or historical evidence.

## Repository-safe live inventory

The live monitor aggregated these key-only rows. Counts below are per synthetic
record; no value was retained in this document.

### VS Code-shaped record

| Key | Count | Scope | Classification |
| --- | ---: | --- | --- |
| `client.kind` | 1 | `resource` | `other` |
| `ide.version` | 1 | `resource` | `other` |
| `vcs.repository.name` | 1 | `resource` | `repository` |
| `vcs.provider.name` | 1 | `resource` | `vcs` |
| `workspace.name` | 1 | `resource` | `workspace` |
| `vcs.ref.head.revision` | 1 | `span` | `vcs` |
| `repository.validation.event` | 1 | `event` | `repository` |

Status: `metadata_present`; repository label obtained: yes; fallback used: no.

### Copilot CLI-shaped records

Each of the two CLI-shaped records contained the following resource keys:

| Key | Count per record | Scope | Classification |
| --- | ---: | --- | --- |
| `client.kind` | 1 | `resource` | `other` |
| `cli.wrapper.version` | 1 | `resource` | `other` |
| `vcs.repository.url.full` | 1 | `resource` | `repository` |
| `vcs.owner.name` | 1 | `resource` | `vcs` |

The first record intentionally used a non-canonical owner token and produced
`unsafe_value_rejected`; repository label obtained: no; fallback used: no. The
corrected canonical GitHub HTTPS record produced `url_fallback_used`;
repository label obtained: yes; fallback used: yes. The correction confirms
the allowlist boundary rather than weakening it.

## Live assertions

The server was started from the execution SHA on a loopback-only URL with
`--sanitized-only`. The HTTP sequence returned `200` for ingestion,
`/diagnostics`, and `/api/monitor/traces`.

Observed assertions:

- status rows included `metadata_present`, `url_fallback_used`, and
  `unsafe_value_rejected`;
- inventory included all ten expected key/scope pairs across `resource`,
  `span`, and `event`;
- all synthetic attribute value markers were absent from `/diagnostics`;
- two of three trace projections obtained a sanitized repository label;
- the raw URL and owner were absent from the trace response;
- no `repository_url`, `repository_owner`, `vcs_repository_url`, or
  `vcs_owner_name` field appeared in the existing trace response;
- no prompt, CWD, path, time, proximity, or identity inference was used.

## Automated validation

| Command | Result |
| --- | --- |
| `pwsh scripts\agent\sync-claude-skills.ps1 -Check` | PASS; 5 shared skills current |
| <code>dotnet test tests\CopilotAgentObservability.LocalMonitor.Tests\CopilotAgentObservability.LocalMonitor.Tests.csproj --filter "FullyQualifiedName~RepositoryMetadata&#124;FullyQualifiedName~MonitorProjectionBuilderTests" --no-restore --verbosity minimal</code> | PASS; 73 passed, 0 failed, 0 skipped |
| `dotnet build CopilotAgentObservability.slnx` | PASS; 0 warnings, 0 errors |
| `pwsh scripts\test\install-playwright-chromium.ps1` | PASS; Chromium/headless-shell bootstrap exit 0 |
| `dotnet test CopilotAgentObservability.slnx` | PASS; 6,642 passed, 0 failed, 0 skipped (`Doctor` 266 + `ConfigCli` 4,282 + `LocalMonitor` 2,094) |

## Validation deviations retained for review

- The initial RED compile proved the repository metadata diagnostic types were
  absent before implementation.
- The bounded loader/UI RED compile proved the candidate-query and diagnostics
  loader seams were absent before implementation.
- The unsupported authoritative value-type RED expected
  `unsupported_candidate_present` and initially observed
  `unsafe_value_rejected`; the implementation was corrected and the focused
  suite passed.
- One newly added public xUnit theory initially used an internal enum parameter
  and failed compilation. The test was corrected to assert the public wire
  token; this was a test-signature defect, not a product failure.
- A broad Local Monitor test run was mistakenly started before the required
  Playwright bootstrap. Browser tests failed only because the pinned executable
  was absent; the exact Issue #58 test processes were stopped. The specified
  bootstrap then succeeded and the exact full solution run passed 6,642/6,642.
- The first synthetic CLI live assertion expected fallback from a deliberately
  non-canonical owner token. The implementation correctly reported
  `unsafe_value_rejected`; the corrected canonical fixture then passed the
  fallback and non-leakage assertions.

## Consumer handoff

Issues #72 and #85 may consume only the existing nullable
`repository_name`, `workspace_label`, and `repo_snapshot` projection fields.
They must preserve null for `metadata_not_present`,
`unsupported_candidate_present`, and `unsafe_value_rejected`, and must not
infer repository identity from Session relations, prompt/tool content, CWD,
path, time, or proximity. The five metadata status tokens are diagnostic reason
codes, not Session identity or a new trace DTO field.
