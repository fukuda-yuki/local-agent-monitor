# Issue #55 Proposal Apply Design

## Goal

Allow a local user to apply an explicitly approved Issue #54 proposal to a
configured local configuration, Skill, or repository working-tree file without
granting Canvas arbitrary filesystem authority or leaking file content outside
the local helper screen.

## Considered Approaches

1. **Canvas writes directly to files.** This is rejected: it exposes ambient
   filesystem authority to the Canvas/runtime boundary and makes root, CSRF,
   audit, recovery, and source-data handling unenforceable in one place.
2. **A separate apply-worker process.** This would make privilege separation
   stronger but adds a process protocol, installation/version lifecycle, and
   recovery coordination that are not otherwise part of the Local Monitor.
3. **A narrow Local Monitor apply engine behind token-gated Canvas helper
   proxying.** This is selected. It keeps privileged filesystem logic in one
   testable .NET boundary while retaining existing loopback, Host-header,
   same-origin, CSRF, no-store, and Canvas token-gate controls.

## Architecture

The Local Monitor receives repeated `--apply-root kind=absolute-directory`
options at startup. It canonicalizes and validates those roots once and
revalidates them before every privileged operation. APIs use opaque root IDs
and normalized relative paths only. The helper screen may render a full diff
as inert local text but no action DTO, log, prompt, or repository-safe output
contains file paths or content.

An apply draft stores local-only base/replacement content. The server produces
stable unified-diff hunk IDs and accepts a selected-hunk subset. Approval binds
the proposal, root, targets, base hashes, selected hunks, selected replacement
hashes, and revision into an immutable digest. Applying has no editable content
or path fields.

The transaction first preflights every target, snapshots every original,
flushes a write-ahead journal, stages same-volume replacements, performs each
atomic replacement, then writes a committed marker. Any error, or an
uncommitted journal discovered on startup, restores every original. Rollback
uses the same mechanism but first requires every target to remain at its
recorded post-apply hash.

## Failure Model

| Failure / attack | Required outcome |
| --- | --- |
| traversal, absolute/UNC/device/URI path | reject before file access |
| symlink/junction/reparse or changed root | reject with no write |
| stale/missing/replaced target | reject all targets with no write |
| selection or approval mutation | invalidates approval; new approval required |
| write/flush/replace fault | restore all originals; record fixed outcome |
| process crash before commit | startup recovery restores all originals |
| external edit before rollback | reject rollback without write |
| cross-site/CSRF/token misuse | reject before draft/approval/apply mutation |
| source/diff/path leak | never emit it outside token-gated helper display |

## Test Strategy

Use injected filesystem fault points to prove each journal boundary produces
either all-original recovery or all-applied state. Include destructive tests
for stale multi-file preflight, rollback after external edits, reparse targets,
selection/approval binding, and one-time rollback. Route and Canvas contract
tests prove the privilege and no-leak boundaries. The repository-wide build,
Playwright bootstrap, and test suite remain the final validation gate.
