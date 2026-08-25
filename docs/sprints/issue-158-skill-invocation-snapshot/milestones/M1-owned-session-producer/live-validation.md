# Issue #158 M1 owned-session producer live-validation evidence

This is historical candidate evidence, not a product specification. It records
sanitized platform and deterministic validation observed on 2026-08-25. Final
full repository validation and merge remained pending at document creation.

## Evidence boundary

| Field | Record |
| --- | --- |
| Live execution anchor | `527c7b5f299296afefe13def783c08be121684b9` |
| Windows attempt | One accepted execution; exit `0` |
| Linux attempt | One accepted wrapper execution; exit `0`; approximately 36.46 seconds |
| Inputs | Synthetic Windows and Linux inputs only |
| Data boundary | No actual Skill text, prompt/response, tool arguments/results, credentials, user data, local usernames, sensitive absolute paths, database content, or raw installer output |
| Release state | Evidence-document commit SHA, full pinned validation, fresh final review, merge, and Issues #173/#158 closure pending |

## Implementation and candidate anchors

The concise implementation progression included `c2d6f39d...` (runtime
reservation), `08e67299...` (test alignment), and `527c7b5f...` (D089 and the
current live execution anchor). Intermediate commits are implementation
anchors, not separate final platform-evidence candidates.

## Windows signed-in owned-session lane

The immutable candidate
`527c7b5f299296afefe13def783c08be121684b9` passed in one attempt with source
application `1.0.75` and protocol `3`.

| Observation | Count |
| --- | ---: |
| Retained roots | 1 |
| Retained Skills | 1 |
| Probe Sessions | 1 |
| Execution Sessions | 1 |
| User-invoked | 1 |
| Agent-invoked | 1 |
| Task complete | 1 |
| v2 imported | 2 |
| v1 imported | 2 |
| Snapshot rows | 2 |

The accepted result reported true for `operator_gate`, `cli_override_absent`,
`retained_only_inventory`, `exact_tool_union`, `native_reproof`,
`current_generation`, `metadata_route`, `historical_route`,
`current_file_route`, `shutdown_drain`, and `cleanup_complete`. The wrapper
certified cleanup and exited `0`. The detached and main candidates remained
exact and clean, with no attributable temporary residual.

## Linux WSL Ubuntu native-ext4 lane

The immutable candidate
`527c7b5f299296afefe13def783c08be121684b9` passed in one wrapper attempt on
native `ext4`. The accepted result used schema `issue-158-live-validation.v1`,
exited `0`, and completed in approximately 36.46 seconds.

| Observation | Count |
| --- | ---: |
| Retained roots | 1 |
| Retained Skills | 1 |
| Matrix cases | 6 |

The accepted result reported true for `operator_gate`,
`detached_clean_candidate`, `kernel_supported`, `native_ext4`,
`retained_root_reproof`, `strict_utf8_read`, `unsafe_path_rejected`,
`missing_rejected`, `oversized_rejected`, `binary_rejected`, `metadata_route`,
`historical_route`, `current_file_route`, and `cleanup_complete`.

After the lane, SDK, installer, and lane roots were safely absent. Two earlier
provisioning-only attempts did not invoke the Linux wrapper, were not accepted
as evidence, and no task roots remained afterward.

## Sanitized cleanup and data boundary

Both lanes used synthetic inputs. Accepted evidence contains only the bounded
candidate, version, protocol, filesystem, count, boolean, and outcome facts
recorded here. Cleanup evidence records absence and counts only; it does not
retain or disclose machine paths, owner markers, database content, installer
output, or runtime artifacts.

## Live commands

These repository-relative commands identify the exact authorized candidate for
each observed live lane:

```powershell
pwsh scripts\validation\issue-158\run-windows-owned-session.ps1 -CandidateSha 527c7b5f299296afefe13def783c08be121684b9 -OperatorAuthorized
pwsh scripts\validation\issue-158\run-linux-current-file-matrix.ps1 -CandidateSha 527c7b5f299296afefe13def783c08be121684b9 -OperatorAuthorized
```

## Final validation gate pending

The evidence-document commit SHA was not yet frozen. Full pinned validation,
fresh final review, merge, and Issues #173/#158 closure remained pending. No
result is claimed for these commands; their required order is:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Collector configuration validation is not applicable because no
`infra/otel-collector` path changed.
