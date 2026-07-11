# Canvas Proposal Apply Interface

## Scope

This specification defines Issue #55. It adds a human-approved, local-only
file apply flow to an existing Issue #54 improvement proposal. It does not
change proposal status, Session identity, evidence identity, telemetry ingest,
Agent ownership, or Issue #45 `session.send()` behavior.

The flow is deliberately a privileged Local Monitor operation, not a Canvas
action. Canvas owns only a per-launch-token helper page that proxies the local
screen's requests and renders source/diff content as inert text. Canvas action
DTOs, `session.send()` prompts, logs, and repository-safe artifacts receive no
path, source, diff, replacement, credential, token, raw Session content, or
snapshot content.

## Explicit Roots And Target Safety

The Local Monitor accepts zero or more repeated startup options:

```text
--apply-root user_config=<absolute-directory>
--apply-root skill=<absolute-directory>
--apply-root repository=<absolute-directory>
```

`kind` is exactly `user_config`, `skill`, or `repository`. There is no default
root and no UI/API that registers a root. Each option names an existing
directory. Startup rejects a missing, duplicate canonical, symlink/junction/
reparse-bearing root or a root whose ancestors through its volume root contain
a reparse point. The process keeps the canonical path private and exposes only
an opaque `root_id`, `kind`, and a fixed user-facing kind label. An unavailable
or changed root after startup is not silently rediscovered; apply is rejected.

A target is valid only when all of the following hold at preview, approval,
apply, recovery, and rollback:

- `relative_path` is nonempty, uses neither an absolute, drive-relative, UNC,
  device, URI, nor `.` / `..` segment, and resolves below the selected root.
- Every ancestor from root to target, and the target itself, exists and has no
  symlink, junction, or other reparse attribute.
- The target is an existing regular file. This release cannot create, delete,
  rename, move, chmod, or modify a directory.
- The file count is 1..10 and each preview/replacement is at most 256 KiB;
  the request body is at most 1 MiB. Duplicate canonical targets are rejected.

Git is not an integration or fallback: the engine neither invokes git nor
creates a branch, commit, tag, push, pull request, stash, or checkout.

## Draft, Diff, And Approval

`POST /api/session-workspace/proposal-applies/drafts` receives an existing
proposal ID, root ID, and 1..10 `relative_path` / complete `replacement_text`
pairs. The request carries source text only between the token-gated helper
screen and Local Monitor; neither participant logs it. The server reads each
current target, calculates `base_sha256`, creates deterministic line-oriented
unified-diff hunks, and stores the original/replacement text only in the local
apply store.

The draft response contains a local-screen-only full diff and, per hunk, a
stable ID and default `selected=true`. `PUT .../drafts/{draftId}/selection`
accepts exactly the subset of hunk IDs. It replays that subset against the
stored base text, re-diffs the resulting selected replacement, and produces a
new immutable selection revision. An empty selection is rejected. Selection
changes invalidate any pending approval; the previous revision cannot apply.

`POST .../drafts/{draftId}/approve` requires the current `selection_revision`
and its `approval_digest` (SHA-256 of canonical draft identity, proposal ID,
root ID, canonical relative paths, base hashes, selected hunk IDs, selected
replacement hashes, and selection revision). It records an `approved` revision
with `actor_kind = local_user`. Approval never changes the Issue #54 proposal
lifecycle. A changed root, base hash, target identity, hunk selection, or
replacement requires a new selection/approval; there is no approval edit.

## Apply Transaction And Recovery

`POST .../drafts/{draftId}/apply` accepts no editable target, path, hunk, or
content fields. It requires the approved revision and validates its proposal,
root, targets, approval digest, and every current target SHA-256 before any
write. If one file is missing, reparse-bearing, root-escaping, hash-mismatched,
or otherwise invalid it returns a fixed failure and writes no target file.

After the all-target preflight passes, the engine:

1. copies every original file into a private snapshot directory and flushes it;
2. writes and flushes a journal with `prepared` state and all original/post
   hashes;
3. stages every selected replacement in the same directory/volume as its
   target, flushes it, and performs an atomic replace for each target while
   recording and flushing each completed replacement;
4. writes and flushes `committed`, then records a sanitized applied audit.

If any step before committed fails, the engine restores every replaced target
from the snapshot, flushes the journal outcome, and returns `apply_failed`.
On startup and before accepting another mutation, an uncommitted journal is
recovered to every original snapshot. Therefore the observable durable outcome
after recovery is either all selected replacements or all originals; a partial
transaction is never accepted as a usable state. Snapshot/journal retention is
local runtime data and is not exposed as a browser/file browsing API.

## Rollback

`POST .../applies/{applyId}/rollback` has no target/path/content fields. It is
available only for one successfully committed apply that has not been rolled
back. Before any restoration, every current target must have the exact
recorded post-apply SHA-256 and pass root/reparse validation. A mismatch returns
`rollback_stale` with no write. A successful rollback uses the same journal and
all-restored recovery protocol, records `rolled_back`, and cannot run again.

## Local Monitor Interface

All routes below are loopback/Host-header restricted, same-origin,
`x-monitor-csrf: local-monitor`, JSON-only where they have a body, no-store,
and body-bounded to 1 MiB. They are available only when at least one valid
`--apply-root` was configured. No response except helper-only draft display
contains a target path, source/diff/replacement text, or snapshot content.

```text
GET  /api/session-workspace/proposal-applies/roots
POST /api/session-workspace/proposal-applies/drafts
GET  /api/session-workspace/proposal-applies/drafts/{draftId}
PUT  /api/session-workspace/proposal-applies/drafts/{draftId}/selection
POST /api/session-workspace/proposal-applies/drafts/{draftId}/approve
POST /api/session-workspace/proposal-applies/drafts/{draftId}/apply
POST /api/session-workspace/proposal-applies/{applyId}/rollback
```

`GET roots` returns `{ "items": [{ "root_id", "kind", "label" }] }`.
Draft creation requires `proposal_id`, `root_id`, and `files`; selection
requires `selection_revision` and `selected_hunk_ids`; approval requires
`selection_revision` and `approval_digest`. `GET draft` and the draft-creation
response are helper-screen-only and contain full diff/hunks. All other success
responses are sanitized state objects with opaque IDs, state, hashes, and
timestamps.

Fixed error objects have exactly `{ "error": "<code>" }`. Relevant codes are
`apply_not_configured`, `invalid_apply_root`, `invalid_root_id`,
`invalid_relative_path`, `unsafe_reparse_path`, `target_not_regular_file`,
`target_outside_root`, `duplicate_target`, `request_too_large`,
`proposal_not_found`, `invalid_apply_request`, `draft_not_found`,
`invalid_selection`, `selection_stale`, `approval_required`,
`approval_digest_mismatch`, `apply_stale`, `apply_failed`, `apply_not_found`,
`rollback_stale`, `rollback_not_available`, `cross_origin_forbidden`,
`csrf_required`, and `unsupported_media_type`.

## Persistence And Audit

The additive local tables are `proposal_apply_drafts`,
`proposal_apply_files`, `proposal_apply_hunks`, `proposal_apply_revisions`,
`proposal_applies`, and `proposal_apply_audit`. Original/replacement/snapshot
content and journal files live only in the private Local Monitor runtime data
directory, never in proposal tables or repository-safe records. Durable rows
reference the existing proposal and its source Sessions but do not change
`improvement_proposals` status or evidence.

Audit exposes only opaque proposal/draft/apply/root IDs, source Session IDs,
actor kind, state/error code, timestamps, file count, and SHA-256 values. It
does not persist or return absolute paths, relative paths, source/diff text,
replacement text, raw Session content, credentials, tokens, or exceptions.

## UI And Tests

The Improve tab may expose an "Apply locally" entry only for a selected
existing proposal and only through the helper local screen. The screen lists
configured root kinds (not paths), shows the full diff, lets the user deselect
whole files or individual hunks, then shows the exact selected diff and an
explicit approval/apply sequence. It clearly renders stale, rejected,
recovered, failed, applied, and rolled-back outcomes. All source/diff display
uses inert text; no raw Session content is rendered.

Required automated evidence includes:

- route policy/no-echo tests for loopback, same-origin, CSRF, content type,
  size, missing configuration, and helper token gating;
- traversal, absolute/UNC/device, duplicate, root-escape, symlink/junction /
  reparse ancestor, file-kind, and root-change rejection tests;
- full diff, individual-hunk deselection, selection-stale, immutable approval,
  and approval-digest mismatch tests;
- stale-before-apply multi-file tests proving no target changes;
- deterministic fault-injection tests after snapshot, journal prepare, each
  replacement, and commit-marker boundaries, proving all-original recovery or
  all-applied outcome;
- rollback stale/external-edit, one-time rollback, and successful atomic
  rollback tests; and
- contract tests proving no git invocation, Canvas action payload, log, or
  repository-safe output carries path/source/diff/snapshot data.

## Non-Goals

No automatic approval/apply, remote or non-loopback mutation, arbitrary path
registration, git integration, directory mutation, delete/rename, patch from
telemetry/model output, proposal auto-generation/promotion, cross-machine
sync, or Issue #56 comparison verdict is included.
