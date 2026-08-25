# Issue #158 M1 owned-session producer live-validation evidence

This is historical candidate evidence, not a product specification. It records
sanitized platform/live validation observed on 2026-08-25. Pinned full
validation, fresh final reviews, merge, release, and Issues #173/#158 closure
remained pending at document creation.

## Evidence boundary

| Field | Record |
| --- | --- |
| Final live anchor | `711b3e16283796d7d8dcebe8733fdb63dbc86df6` |
| Prior accepted anchor | `527c7b5f299296afefe13def783c08be121684b9`; historical and superseded after later production changes |
| Date | 2026-08-25 |
| Windows final attempt | One authorized execution; exit `0`; approximately 34.0 seconds |
| Linux final attempt | One wrapper execution; exit `0`; approximately 41.69 seconds |
| Inputs | Synthetic Windows and Linux inputs only |
| Data boundary | No Skill content, prompt/response, tool arguments/results, credentials, user data, usernames, identifiers, database content, owner markers, sensitive absolute paths, or raw installer/SDK output |
| Release state | Evidence commit/frozen SHA is established by Git history; pinned full validation, fresh final reviews, merge, release, and Issues #173/#158 closure pending |

Both accepted platform rows ran on the exact same final candidate. The prior
accepted anchor is not final evidence, and no cross-candidate inference is
used.

## Implementation and candidate anchors

The concise implementation progression includes `c2d6f39d...` (runtime
reservation), `08e67299...` (test alignment),
`527c7b5f299296afefe13def783c08be121684b9` (historical accepted live anchor),
and `711b3e16283796d7d8dcebe8733fdb63dbc86df6` (exact final platform/live
anchor). Intermediate commits are implementation or harness anchors, not
accepted final platform evidence unless explicitly identified below.

## Blocked Windows progression

Each row was exactly one authorized attempt. Each remains a blocked validation
outcome and was never reinterpreted or retried on the same SHA. The outcomes
led only to reviewed harness corrections; they are not accepted live evidence.
No hidden counts or private output are inferred or disclosed.

| Candidate | Closed outcome | Duration |
| --- | --- | ---: |
| `f7da5ad655cc62f04e22bac671ae6808be5a1780` | `succeeded_post_success_execution_evidence_prepared_invocation_count` | Not recorded here |
| `dc155cc87690956bd02b33a981125ceddadeff49` | `succeeded_post_success_execution_evidence_prepared_invocation_excess` | Approximately 54.0 seconds |
| `655da557af6615262a50c44cbd5c2f613ea5e25f` | `wrapper_test_output` | Approximately 24.1 seconds |
| `967d55f8e4e2ced2dd00b540f14e48825f891e8a` | `wrapper_test_output_pass_summary` | Approximately 60.8 seconds |

## Final Windows signed-in owned-session lane

The exact final candidate
`711b3e16283796d7d8dcebe8733fdb63dbc86df6` passed in one authorized attempt.
The wrapper exited `0` in approximately 34.0 seconds. The accepted result used
schema `issue-158-live-validation.v1`, source application `1.0.75`, and
protocol `3`.

| Observation | Count |
| --- | ---: |
| `retained_roots` | 1 |
| `retained_skills` | 1 |
| `probe_sessions` | 1 |
| `execution_sessions` | 1 |
| `user_invoked` | 1 |
| `agent_invoked` | 1 |
| `task_complete` | 1 |
| `v2_imported` | 2 |
| `v1_imported` | 2 |
| `snapshot_rows` | 2 |

The accepted result reported true for `operator_gate`, `cli_override_absent`,
`retained_only_inventory`, `exact_tool_union`, `native_reproof`,
`current_generation`, `metadata_route`, `historical_route`,
`current_file_route`, `shutdown_drain`, and `cleanup_complete`. Main and target
were exact, clean, and detached as applicable. The temporary baseline remained
zero before and after. There was no retry or private inspection.

## Final Linux WSL Ubuntu native-ext4 lane

The same exact final candidate
`711b3e16283796d7d8dcebe8733fdb63dbc86df6` passed in one wrapper attempt on
WSL Ubuntu native `ext4`. The wrapper exited `0` in approximately 41.69 seconds.

| Observation | Count |
| --- | ---: |
| `retained_roots` | 1 |
| `retained_skills` | 1 |
| `matrix_cases` | 6 |

The accepted result reported true for `operator_gate`,
`detached_clean_candidate`, `kernel_supported`, `native_ext4`,
`retained_root_reproof`, `strict_utf8_read`, `unsafe_path_rejected`,
`missing_rejected`, `oversized_rejected`, `binary_rejected`, `metadata_route`,
`historical_route`, `current_file_route`, and `cleanup_complete`.

SDK `10.0.203` was provisioned only to a disposable native-ext4 target. Its
exact-version, type, non-symlink, and current-owner checks reported true. The
installer was guarded-removed, the SDK was absent afterward, and both
`installer_roots` and `lane_roots` were `0`.

## Sanitized cleanup and privacy boundary

Accepted evidence contains only candidate SHAs, repository-relative commands,
version/protocol/filesystem facts, counts, closed tokens, durations, booleans,
and sanitized outcomes. It contains no raw or private runtime artifact and no
content or path evidence. Blocked attempts remain blocked and are not used to
support the final pass.

## Live commands

These repository-relative commands identify the exact authorized candidate for
each accepted final lane:

```powershell
pwsh scripts\validation\issue-158\run-windows-owned-session.ps1 -CandidateSha 711b3e16283796d7d8dcebe8733fdb63dbc86df6 -OperatorAuthorized
pwsh scripts\validation\issue-158\run-linux-current-file-matrix.ps1 -CandidateSha 711b3e16283796d7d8dcebe8733fdb63dbc86df6 -OperatorAuthorized
```

## Final validation gate pending

The evidence-document commit and frozen SHA are established by Git history and
cannot be self-recorded here. Pinned full validation, fresh final reviews,
merge, release, and Issues #173/#158 closure remained pending. No result is
claimed for these commands; their required order is:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Collector configuration validation is not applicable because no
`infra/otel-collector` path changed.
