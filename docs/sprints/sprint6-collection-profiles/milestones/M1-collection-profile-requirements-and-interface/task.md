# M1: Collection Profile Requirements and Interface

## Goal

Define collection profiles as a public product interface and update the source
of truth before implementation.

## Scope

- Add `CAO_COLLECTION_PROFILE` as the public profile selector.
- Define all required profile values.
- Mark `raw-only` as the minimum required profile.
- Mark `docker-desktop-langfuse` as the standard full profile.
- Mark all other proposed profiles as required support targets.
- Preserve existing explicit Langfuse and Collector commands during Sprint6.
- State that downstream schemas do not vary by profile.

## Verification

- Source-of-truth documents list the same profile values.
- Remote managed profiles include a warning and do not imply consent workflow
  support in this repository.
- Sprint7 owns the repository-hosted receiver implementation.
