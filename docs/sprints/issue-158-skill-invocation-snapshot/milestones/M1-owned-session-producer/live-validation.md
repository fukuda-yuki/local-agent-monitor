# Issue #158 M1 owned-session producer live-validation evidence

This is historical candidate evidence, not a product specification. It records
sanitized platform and deterministic validation observed on 2026-08-25. Final
full repository validation and merge remained pending at document creation.

## Evidence boundary

| Field | Record |
| --- | --- |
| Windows candidate | `bb698a6aad0f2b178d2d5a24596a8ce07fb64d63` |
| Linux candidate | `9d5fbfb8798fe277bffda6d2d54f95815319d9a5` |
| Inputs | Synthetic Windows and Linux inputs only |
| Data boundary | No actual Skill text, prompt/response, tool arguments/results, credentials, user data, local usernames, sensitive absolute paths, database content, or raw installer output |
| Release state | Platform lanes passed; final full validation and merge pending |

## Implementation and candidate anchors

The concise immutable progression was `fc5a0e...` (S3), `0078261...` (#173
identity), `1ddc7914...` (importer), `bb698a6...` (Windows evidence sync),
`c3878f5...` (Linux `openat2` authority), and `9d5fbfb...` (Linux diagnostic and
evidence candidate). Only the two complete lane candidate hashes above are
used as execution anchors; no omitted hash characters are inferred.

## Windows signed-in owned-session lane

The immutable candidate
`bb698a6aad0f2b178d2d5a24596a8ce07fb64d63` passed with source application
`1.0.75` and protocol `3`.

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

The operator gate, absence of a CLI override, retained-only enabled inventory,
exact tool union, native reproof, current generation, metadata route,
historical route, current-file route, shutdown drain, and complete cleanup all
reported true.

This lane was not rerun at
`9d5fbfb8798fe277bffda6d2d54f95815319d9a5`. The exact name-status diff
`bb698a6aad0f2b178d2d5a24596a8ce07fb64d63..9d5fbfb8798fe277bffda6d2d54f95815319d9a5`
changed only the Linux wrapper/helper/runbook/self-test, Linux native opener and
APIs, Linux live matrix tests, and native classifier tests. It is therefore a
deliberate inference that the signed-in Windows code path was unchanged, not
evidence that Windows execution occurred at `9d5fbfb...`.

## Linux WSL Ubuntu native-ext4 lane

The immutable candidate
`9d5fbfb8798fe277bffda6d2d54f95815319d9a5` passed using disposable SDK
`10.0.203` on native `ext4`.

| Observation | Count |
| --- | ---: |
| Retained roots | 1 |
| Retained Skills | 1 |
| Matrix cases | 6 |

The operator gate, detached clean candidate, supported kernel, native ext4,
retained-root reproof, strict UTF-8, unsafe-path rejection,
missing/oversized/binary rejection, metadata/historical/current-file routes,
and complete cleanup all reported true.

After the lane, the main and live candidates remained exact and clean, the
live candidate remained detached, WSL lane roots were zero, and installer
temporary roots were zero. The exact disposable SDK root was guarded-deleted
and its absence was proved.

## Sanitized cleanup and data boundary

Both lanes used synthetic inputs. Accepted evidence contains only the bounded
candidate, version, protocol, filesystem, count, boolean, and outcome facts
recorded here. Cleanup evidence records absence and counts only; it does not
retain or disclose machine paths, owner markers, database content, installer
output, or runtime artifacts.

## Deterministic and review gate

On the `9d5fbfb8798fe277bffda6d2d54f95815319d9a5` parent state for this evidence
change, the Task 5L deterministic gate recorded:

- self-test: 377 passed;
- focused Linux matrix tests: 23 passed, 1 expected live skip, 0 failed;
- Bash syntax, diff, scope, index, and encoding checks: passed;
- three independent Sol medium reviews: C0/I0/M0.

## Live commands

These repository-relative commands identify the exact authorized candidate for
each observed live lane:

```powershell
pwsh scripts\validation\issue-158\run-windows-owned-session.ps1 -CandidateSha bb698a6aad0f2b178d2d5a24596a8ce07fb64d63 -OperatorAuthorized
pwsh scripts\validation\issue-158\run-linux-current-file-matrix.ps1 -CandidateSha 9d5fbfb8798fe277bffda6d2d54f95815319d9a5 -OperatorAuthorized
```

## Final validation gate pending

The exact final release validation gate was intentionally still pending until
this evidence is committed. No result is claimed for these commands; their
required order is:

```powershell
pwsh scripts\agent\sync-claude-skills.ps1 -Check
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

Collector configuration validation is not applicable because no
`infra/otel-collector` path changed.
