# M5: Remote Managed Endpoint Warning

## Goal

Support remote managed endpoint profiles without implementing consent workflow
or shared-service governance in this repository.

## Scope

- `remote-managed-langfuse`
- `remote-managed-collector`

## Requirements

- README and user-facing docs warn that remote / shared endpoints require
  access control, retention, deletion process, masking / redaction, user notice
  or consent, identity handling, and credential handling before use.
- Config output uses placeholders only.
- The repository does not implement the user consent workflow.

## Verification

- Documentation warning is present in README and user guide.
- Unit tests verify placeholder-only output for remote managed profiles.

## Validation Notes

2026-06-21 M5 updated remote managed profile warnings:

- README, Getting Started, the user guide, and telemetry collection guide warn
  that remote / shared endpoint use requires access control, retention,
  deletion process, masking / redaction, user notice or consent, identity
  handling, and credential handling before telemetry is sent.
- Profile-aware CLI output for `remote-managed-langfuse` and
  `remote-managed-collector` now also states that this repository does not
  implement a remote or shared endpoint user consent workflow.
- Generated remote managed profile endpoints remain placeholders:
  `https://<langfuse-host>/api/public/otel` and
  `https://<collector-host>`.
- Generated credentials remain placeholders only.

Automated validation:

- Added focused tests for remote managed PowerShell and Codex App profile
  output warning text.
- `dotnet test tests\CopilotAgentObservability.ConfigCli.Tests\CopilotAgentObservability.ConfigCli.Tests.csproj --filter FullyQualifiedName~ConfigSamplesTests.CreateProfile`
  succeeded with 29 passed tests after the warning update.
