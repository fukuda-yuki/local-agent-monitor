# Issue #91 Repository-Safe Evidence Ledger Template

This template is not final evidence. Do not fill final classifications or a
release decision until the dependency gate is satisfied and one immutable
`final_validation_sha` has been frozen.

## Candidate

| Field | Value |
| --- | --- |
| Matrix schema | `validation-matrix.v1` |
| Matrix prep SHA | `5180a0424ff5488354a3e173c74b7e931d28679d` |
| Final validation SHA | Unset — dependency gate open |
| Dependency accepted revisions and ancestry | Unset for final run |
| Inventory diff from prep SHA | Unset for final run |
| Date / environment boundary | Unset for final run |

## Evidence Safety

Record only bounded results, counts, exit codes, non-sensitive versions,
sanitized setting labels/effective states, and opaque references. Never record
credentials, authorization values, raw prompts/responses, tool bodies, PII,
database content, reversible synthetic markers, or machine-sensitive paths.

## Historical Evidence Compatibility

For each reused observation record the historical SHA, current candidate SHA,
surface, source/application/adapter version, sanitized settings labels,
environment boundary, compatibility basis, and exact capability that remains
unverified. A blocked or mismatched record is never passed.

## Active Rows

For every row from the final candidate inventory, fill every field required by
`validation-matrix.schema.json`, attach automated and live evidence references,
then run `scripts/validation/issue-91/scan-outputs.ps1` against every saved
sanitized output, log capture, and repository evidence artifact.

## Required Validation

Run against the unchanged candidate from the repository root:

```powershell
dotnet build CopilotAgentObservability.slnx
pwsh scripts\test\install-playwright-chromium.ps1
dotnet test CopilotAgentObservability.slnx
```

No failed, skipped, unavailable, timed-out, or substituted command is a pass.
