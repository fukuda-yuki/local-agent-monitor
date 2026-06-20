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
