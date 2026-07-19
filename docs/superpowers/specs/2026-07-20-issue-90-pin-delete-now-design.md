# Issue #90 Pin, Unpin, and Delete-Now Design

## Status and purpose

This document is the pre-implementation contract for Issue #90. It pins the
single user-controlled mutation slice for `pin`, `unpin`, and `delete_now`
before persistence, API, or UI implementation. It does not promote these
decisions into the canonical product specifications and does not implement
code. The later 90-A task must promote the accepted portions without changing
the values in this document.

The authoritative Issue #89 foundation is the one retention catalog per Local
Monitor database, its seven persisted item states, catalog-gated reads,
irreversible read denial, durable queue, leases, exact adapters, and
forward-only cleanup. Issue #90 adds no second state machine, catalog, worker,
adapter, target selector, or physical-delete path.

Kickoff evidence for this contract is the exact base SHA:

`e622417e8d6d7ce403733b9a3df8a12d34659bda`

## Contract authority and inherited #89 pins

Issue #90 is governed by `local-notes/issue-90.md`. It builds on the approved
Issue #89 design and its plan section **Global contract and finite v1 pins**.
The following values are inherited verbatim and are not redefined here:

| Contract | Fixed v1 value | Issue #90 use |
| --- | --- | --- |
| Catalog and adapter coverage version | `1` | Every mutation uses the same catalog instance and five-adapter coverage contract. |
| Lifecycle | `expiring`, `retained_by_policy`, `expired_pending_deletion`, `deletion_queued`, `deleting`, `deleted`, `deletion_failed` | The only policy seam is `expiring <-> retained_by_policy`; all deletion movement uses the existing forward path. |
| Policies | `raw-default-90d` v1 = 90 days; `sensitive-bundle-7d` v1 = 7 days | Unpin uses the original `captured_at`, recorded `policy_id`, and recorded `policy_version`; it never starts a new TTL. |
| Scan item limit | 100 | The #90 exact target set and preview item list contain at most 100 items. |
| Claim batch limit | 100 | A delete-now outbox request can enqueue at most the same 100 exact items. |
| Scan elapsed budget | 30 seconds | A preview never waits beyond the existing 30-second catalog-resolution budget. |
| Queue/claim order | `expires_at ASC, item_id ASC` | Status and target item summaries use this order unless a history route specifies its own order. |
| Worker wake interval | 15 seconds | Delete-now does not add a wake mechanism; it relies on the #89 durable queue and existing worker wake. |
| Maximum active deletion workers | 2 | No #90 worker is created. |
| Access, operation, and deletion lease duration | 2 minutes | Preview reports active conflicts; confirmed deletion enters the #89 lease/quiescence path. |
| Lease renewal deadline | No later than 1 minute after acquisition or prior renewal | Unchanged for cleanup after a #90 queue request. |
| Active-operation quiescence and shutdown/drain bounds | 2 minutes each | Unchanged for delete-now handoff and recovery. |
| WAL-maintenance retry delay | 1 minute | Unchanged; WAL maintenance is not an item mutation result. |
| Delete attempts and retry schedule | Maximum 5; after failures 1, 5, 30 minutes, then 2 hours; failure 5 terminal | #90 never changes retry eligibility or attempts. |
| File item limits | 256 exact members and 128 MiB journaled bytes, independently for Sensitive Bundles and SDK directories | #90 only queues exact items; it does not alter adapter limits. |
| Status item-summary limit | 100 | New status/history projections reuse 100 as their maximum page size. |
| Ownership key | `(store_instance_id, store_kind, source_item_id)` | This is the #89 ownership key; it is not a Session-selection heuristic. |
| Existing error style | Fixed `retention_*` code, with no exception/provider/path/raw supplement | All new mutation codes below follow this style and do not reuse an existing #89 code. |

The inherited #89 rules also remain absolute: `not_captured` and `mixed` are
aggregate values only; `read_denied_at` is irreversible; a missing source is
not success without the #89 proof and receipt; and physical removal is
eventual adapter work after the durable queue commit.

## Fixed #90 v1 pins

The following values are new Issue #90 decisions:

| Contract | Fixed value |
| --- | --- |
| Target kinds | Exactly `session` and `item`. |
| Operations | Exactly `pin`, `unpin`, and `delete_now`. |
| Scopes | Exactly `session_items` and `single_item`; `session_items` is valid only with a `session` target, and `single_item` only with an `item` target. |
| Mutation target limit | 100 exact target items; a larger exact set fails closed with `retention_mutation_target_limit_exceeded` and is never partially applied. |
| Preview item order | `expires_at ASC, item_id ASC`, reusing the #89 order; a null `expires_at` sorts after every finite timestamp and ties by opaque `item_id`. |
| Preview item list | At most 100 exact items; omission is never used to signal success. |
| Exclusion reason registry | Exactly the seven codes in the Session-target table below. |
| Store-kind summary | At most the five #89 store kinds, ordered by the closed registry order in the #89 design. |
| Active-conflict registry | Exactly `active_read_lease`, `active_operation_lease`, `active_deletion_lease`, and `active_delete_intent`. |
| Confirmation lifetime | Exactly 5 minutes from preview creation. Issuing a token never extends the preview expiry. |
| Preview ID | `rpv1_` plus 22 unpadded base64url characters encoding 16 server-random bytes. |
| Confirmation ID | `rcid1_` plus 22 unpadded base64url characters encoding 16 server-random bytes. |
| Confirmation token | `rt90v1_` plus 22 unpadded base64url nonce characters, `_`, and 43 unpadded base64url secret characters. |
| Server nonce | Exactly 16 cryptographically secure random bytes per issued token; a collision is rejected rather than retried. |
| Token secret | Exactly 32 cryptographically secure random bytes; only its SHA-256 digest is persisted. |
| Idempotency key | `rid1_` plus 43 unpadded base64url characters encoding 32 client-generated random bytes; total length is 48 ASCII characters. |
| Idempotency lifetime | 365 days from first durable record creation; after expiry the key is permanently rejected and never reused. |
| Comment | Null or 1–256 Unicode scalar values, NFC-normalized, with no control character, CR/LF, URL, path separator, credential marker, database-key marker, or token value. |
| Actor label | Server-derived fixed value `local-user`; the client cannot supply or override it. |
| History page | Default 100 and maximum 100 entries; cursor pagination is exclusive and opaque. |
| History order | `occurred_at DESC, event_id DESC` (newest first). |
| HTTP cache policy | `Cache-Control: no-store` on every preview, confirmation, mutation, status, item, and history response. |
| Error body | Exactly `{"error":"<fixed-code>"}` with no message, rejected value, exception, path, or token. |

Every timestamp in this document is UTC RFC 3339 with seven fractional digits.
Every digest is lowercase hexadecimal with the fixed prefix `sha256-` unless
the field's format is explicitly stated otherwise.

## Scope and non-negotiable foundation

The command operates only on an exact local Session ID or one exact opaque
retention `item_id`. The catalog is the authority for target resolution,
current state, revision, read denial, and queue linkage. The Session workspace
aggregate never authorizes deletion of a raw item.

The command sequence is:

```text
exact target -> deterministic preview -> explicit confirmation issue
  -> one-time token -> one durable mutation transaction/outbox
  -> #89 worker status and append-only audit
```

Preview is read-only. Confirmation is explicit. A missing preview, stale
preview, implicit target, approximate search, repository/path/timestamp
matching, or client-inferred state cannot mutate anything.

## Target union and exact resolution

### Request shape

`RetentionMutationTarget` has exactly these fields:

| Field | Type and rule |
| --- | --- |
| `kind` | Required closed enum: `session` or `item`. |
| `id` | Required exact ID. For `session`, it is the existing local UUIDv7 Session ID. For `item`, it is the exact opaque #89 catalog `item_id` returned by an authoritative diagnostics read. It is never decoded, normalized, searched, or converted to a database key or path. |

The request body contains exactly one target object. A target with both kinds,
missing `id`, blank `id`, an invalid Session UUID, or any second selector is
`retention_mutation_request_invalid`. The target object has no repository,
workspace, trace, time, path, prompt, source-row, or free-text selector.

### Session target selection

A `session` target first resolves the exact Session ID in the current catalog
database. It then selects every catalog item whose persisted #89 linkage
proves ownership by that exact Session. The result is one exact item set, not
one query over raw storage and not a set assembled from client fields.

Only these #89 linkage forms qualify:

| #89 linkage | Qualification |
| --- | --- |
| Persisted exact `session_id` reference on the catalog item | Qualifies when it is byte-for-byte equal to the requested local Session ID and the catalog ownership key is valid. |
| Persisted exact `event_id` reference for `session_event_content` | Qualifies only after an exact `session_events.event_id` lookup proves that its persisted `session_id` equals the requested Session ID. |
| Exact `store_instance_id`, `store_kind`, and `source_item_id` ownership key | Required for catalog identity, but never sufficient by itself to prove Session ownership. |

The following are explicitly non-qualifying when no qualifying Session/Event
link exists: `run_id`, `trace_id`, `span_id`, `evidence_id`, native IDs,
`source_item_id` alone, repository, workspace, path, capture time, expiry time,
prompt-derived text, row proximity, and any query or similarity result. A raw
record or analysis item with only one of those values is excluded from a
Session mutation even if it appears related to the Session. An exact opaque
item target remains independently operable when the item is not Session-linked.

The closed exclusion registry for Session-target diagnostics is:

| Exclusion code | Meaning |
| --- | --- |
| `no_exact_session_link` | The candidate has no persisted exact `session_id` or qualifying `event_id -> session_id` relation. |
| `foreign_session_link` | The candidate has an exact Session/Event relation, but it proves a different Session. |
| `missing_ownership_proof` | The candidate lacks the #89 ownership key or immutable owner receipt. |
| `unknown_store_kind` | The candidate is not one of the five #89 v1 store kinds. |
| `caller_owned_source` | The candidate is a caller-owned input and therefore is not a product-owned retention item. |
| `external_backup_artifact` | The candidate is an offline, external, backup, snapshot, or other non-catalog purge artifact. |
| `catalog_instance_mismatch` | The candidate belongs to another catalog instance and cannot be resolved through the current database. |

No ninth exclusion code is permitted. Exclusions are counted from canonical
catalog linkage diagnostics only; the resolver never scans a repository,
filesystem, backup, path, or raw table to discover additional candidates.

The selected item set includes every exact-owned catalog item, including a
tombstone or denied item, so the preview can explain why an operation is
rejected. If the selected set has more than 100 items, the preview fails with
`retention_mutation_target_limit_exceeded`; it never truncates the set or
creates a partial command.

### Opaque item target resolution

An `item` target resolves exactly one catalog row by byte-for-byte opaque
`item_id` equality in the current catalog instance. The item is not required to
belong to a Session. The resolver does not decode the ID, accept a database
primary key, inspect a source path, expand to sibling items, or infer a
Session. A missing exact row returns `retention_target_not_found`. A row from a
non-product-owned or non-v1 store cannot be manufactured by supplying an
opaque-looking string; it is not a valid catalog item.

### Empty and not-applicable result

An existing Session with no exact-owned item has a successful read-only preview
response whose `result` is exactly `empty_not_applicable`,
`empty_reason` is exactly `no_exact_owned_items`,
`target_item_count` is `0`, `target_items` is `[]`, `mutation_allowed` is
`false`, and `confirmation_expires_at` is JSON `null`. The same shape is used
with `empty_reason = all_candidates_excluded` when canonical candidate
linkages exist but none qualify. `excluded_item_count` and
`excluded_items_by_reason` still report the exact sanitized exclusion counts.
No confirmation token can be issued for this result. A later confirmation
attempt returns `409` with `retention_target_empty`; it is never a no-op
mutation success.

An unknown Session is a `404` `retention_target_not_found`, not an empty
Session. An exact item with a denied, deleting, deleted, or otherwise
incompatible state is not an empty target; it receives an actionable preview
with `mutation_allowed = false` and its fixed precondition code.

## State model and transition/precondition matrix

`retained_by_policy` is the pinned/readable state. It preserves the original
`captured_at`, `policy_id`, `policy_version`, and the original policy-derived
expiry as historical metadata while automatic expiry is disabled. It does not
create a new policy or TTL. `expiring` is the unpinned/readable state.

The tables below are the complete mutation contract. A Session command is
atomic across all selected items: every item must be allowed or an explicit
idempotent case for that operation. One rejected item prevents partial
application to the other items.

| Operation | Current state | Preconditions and result | New state / pin state |
| --- | --- | --- | --- |
| `pin` | `expiring` | Exact target, revision, and readable catalog proof match. | `retained_by_policy`, pin `pinned`; automatic expiry stops; revision increments. |
| `pin` | `retained_by_policy` | Idempotent no-op; original policy/capture values remain unchanged. | `retained_by_policy`, pin `pinned`; revision is unchanged; an audit/idempotency result is still committed. |
| `pin` | `expired_pending_deletion` or `deletion_queued` or `deletion_failed` | Reject read-denied content with `retention_pin_read_denied`; never restore readability. | Unchanged. |
| `pin` | `deleting` | Reject with `retention_pin_deleting`; the #89 lease and forward cursor remain authoritative. | Unchanged. |
| `pin` | `deleted` | Reject with `retention_pin_deleted`; tombstone remains terminal. | Unchanged. |
| `pin` | `expiring` but `expires_at <= now` at commit | #89 expiry wins the race. Deny the read and queue forward, then reject pin with `retention_pin_expired`; no readable state is restored. | `deletion_queued`, pin `unpinned`. |
| `unpin` | `retained_by_policy` and original policy expiry is in the future | Recalculate `expires_at = original captured_at + recorded policy/version TTL`; do not use unpin time. | `expiring`, pin `unpinned`; revision increments. |
| `unpin` | `retained_by_policy` and recalculated expiry `<= now` | Directly set irreversible read denial and enqueue through #89 in the same transaction. No readable interval is created. | `deletion_queued`, pin `unpinned`; completion code `retention_unpin_expired_queued`. |
| `unpin` | `expiring` and `expires_at > now` | Idempotent no-op; no TTL reset and no revision change. | `expiring`, pin `unpinned`. |
| `unpin` | `expiring` and `expires_at <= now` | Apply #89 expiry/denial/queue directly rather than returning readable. | `deletion_queued`, pin `unpinned`; completion code `retention_unpin_expired_queued`. |
| `unpin` | `expired_pending_deletion` or `deletion_queued` or `deletion_failed` | Reject with `retention_unpin_read_denied`; no retry or resurrection is created by #90. | Unchanged. |
| `unpin` | `deleting` | Reject with `retention_unpin_deleting`; #89 forward recovery continues. | Unchanged. |
| `unpin` | `deleted` | Reject with `retention_unpin_deleted`; tombstone remains terminal. | Unchanged. |
| `delete_now` | `expiring` | Confirmed explicit deletion sets read denial and queues through #89. | `deletion_queued`, pin `unpinned`. |
| `delete_now` | `retained_by_policy` | Confirmed explicit deletion atomically clears pin, sets read denial, and queues through #89. No separate unpin round-trip is allowed. | `deletion_queued`, pin `unpinned`; completion code `retention_delete_now_superseded_pin`. |
| `delete_now` | `expired_pending_deletion` or `deletion_queued` | Idempotent already-queued result after a matching confirmation; no second queue row. | Unchanged; completion code `retention_delete_already_queued`. |
| `delete_now` | `deleting` | Reject with `retention_delete_already_deleting`; the existing #89 lease/cursor is not touched. | Unchanged. |
| `delete_now` | `deleted` | Reject with `retention_delete_already_deleted`; no resurrection or second receipt. | Unchanged. |
| `delete_now` | `deletion_failed` | Reject with `retention_delete_failed`; only the #89 retry eligibility path can requeue the item. | Unchanged. |

`deletion_queued` in the table means the existing #89 forward sequence
`expiring -> expired_pending_deletion -> deletion_queued` is committed in one
#90 transaction. #90 never jumps directly to `deleted`, cancels `deleting`, or
creates another cleanup worker. Pin, unpin, and delete-now do not operate on
`not_captured` or `mixed`, because those are not persisted item states.

## Deterministic preview contract

### Request DTO

`RetentionMutationPreviewRequest` has exactly:

| Field | Type and rule |
| --- | --- |
| `target` | `RetentionMutationTarget`, as defined above. |
| `operation` | `pin`, `unpin`, or `delete_now`. |
| `scope` | `session_items` or `single_item`, matching the target kind exactly. |
| `reason_code` | One of the seven audit reason codes below. |
| `comment` | Null or the exact 1–256 scalar, NFC-normalized safe comment described above. |

The request also requires the `Idempotency-Key` header in the exact `rid1_`
format. The server records a digest of the request and never echoes the
comment in the preview response.

### Response DTO

`RetentionMutationPreviewResponse` has exactly these top-level fields. A field
marked nullable is present as JSON `null`, never omitted.

| Field | Type and exact meaning |
| --- | --- |
| `schema_version` | Integer `1`. |
| `result` | `actionable` or `empty_not_applicable`. |
| `empty_reason` | Null, `no_exact_owned_items`, or `all_candidates_excluded`. |
| `mutation_allowed` | Boolean. It is `false` for an empty result or an incompatible precondition. |
| `preview_id` | Opaque `rpv1_...` ID. |
| `target_kind` | `session` or `item`. |
| `target_id` | Exact Session ID or exact opaque item ID; no database key/path. |
| `operation` | `pin`, `unpin`, or `delete_now`. |
| `scope` | `session_items` or `single_item`. |
| `source_state` | `available`, `not_captured`, `redacted`, `unsupported`, or `unknown`; it is `null` for a non-Session item when no source state exists. |
| `session_completeness` | `unbound`, `partial`, `rich`, `full`, or `null` for an item target without an exact Session. |
| `content_state` | Existing Session content state (`available`, `not_captured`, `redacted`, `unsupported`, or `expired_pending_deletion`) or `null` when not applicable. |
| `current_state` | `RetentionCurrentStateSummary`, defined below. |
| `target_items` | Array of `RetentionPreviewItem`, exact and ordered; maximum 100. |
| `target_item_count` | Nonnegative integer equal to `target_items.length`. |
| `store_kind_summary` | Array of `RetentionStoreKindSummary`, one entry for each represented #89 kind, maximum 5. |
| `excluded_item_count` | Nonnegative integer. |
| `excluded_items_by_reason` | Array of `RetentionExclusionSummary`, one entry per nonzero closed exclusion code, maximum 7, registry order. |
| `capture_expiry_policy_summary` | Array of `RetentionCaptureExpiryPolicySummary`, maximum 5, grouped by `(policy_id, policy_version)`. |
| `retained_metadata_impact` | `RetentionRetainedImpact`, defined below. |
| `active_cleanup_exclusion_conflicts` | Array of `RetentionActiveConflictSummary`, maximum 4, registry order. |
| `backup_non_purge_warning_code` | Exactly `retention_backup_not_purged` for every actionable preview; `null` only for an empty/not-applicable result. |
| `expected_state_version` | `v1-` plus a SHA-256 version-vector digest for every exact target item. |
| `target_item_set_digest` | SHA-256 digest of the exact `(item_id, store_kind)` set. |
| `preview_digest` | SHA-256 digest of the canonical sanitized preview input. |
| `confirmation_expires_at` | Preview creation time plus exactly 5 minutes for an actionable preview; JSON `null` for an empty/not-applicable result. |
| `rejection_code` | Null when confirmation may be issued, otherwise one fixed transition/precondition code. |

`RetentionPreviewItem` has exactly:

`item_id`, `store_kind`, `state`, `pin_state`, `delete_state`,
`captured_at`, `expires_at`, `policy_id`, `policy_version`, `read_denied_at`,
`queued_at`, `revision`, `retry_exhausted`, and `error_code`.

`pin_state` is `pinned`, `unpinned`, or `not_applicable`; `delete_state` is
`not_requested`, `queued`, `in_progress`, `deleted`, or `failed`. `error_code`
is null or an inherited #89 fixed code. No source item ID, path, locator,
database key, raw value, or exception is in this DTO.

`RetentionCurrentStateSummary` has exactly:

`readable_item_count`, `read_denied_item_count`, `pinned_item_count`,
`unpinned_item_count`, and `lifecycle_counts`.

`lifecycle_counts` contains exactly one nonnegative integer property for each
of the seven #89 lifecycle values, including zero values.

`RetentionStoreKindSummary` has exactly `store_kind`, `item_count`,
`readable_count`, and `read_denied_count`. `RetentionExclusionSummary` has
exactly `reason_code` and `item_count`.

`RetentionCaptureExpiryPolicySummary` has exactly `policy_id`,
`policy_version`, `item_count`, `captured_at_min`, `captured_at_max`,
`original_expires_at_min`, and `original_expires_at_max`. The two expiry fields
are the original policy-derived values, not a new unpin TTL.

`RetentionRetainedImpact` has exactly `raw_content_will_be_deleted`,
`session_metadata_retained`, `event_metadata_retained_count`,
`safe_summary_retained_count`, and `evidence_reference_retained_count`.
`raw_content_will_be_deleted` is `true` for a confirmed `delete_now` or an
unpin whose original expiry is already reached, and `false` for a future
readable pin/unpin preview. The preview always shows the field so the UI can
explain the consequence. Counts are nonnegative integers and never contain
content.

`RetentionActiveConflictSummary` has exactly `conflict_code`, `item_count`,
and `conflict_version`. `conflict_version` is a `v1-` SHA-256 digest of the
canonical active-conflict snapshot.

### Digest and canonicalization

All #90 digests use SHA-256 over UTF-8 JSON Canonicalization Scheme (JCS,
RFC 8785) output. JCS lexicographically orders object properties, emits no
whitespace, uses its specified JSON number representation, and encodes strings
as UTF-8. There is no delimiter concatenation, locale formatting, trimming,
case folding, or path normalization.

`target_item_set_digest` hashes a JCS array of exactly
`[{"item_id":<opaque>,"store_kind":<closed-kind>}, ...]`, sorted by
`item_id` ordinal and then `store_kind` ordinal. The array is empty for an
empty/not-applicable preview.

`expected_state_version` hashes a JCS array of exactly
`[{"item_id":<opaque>,"revision":<nonnegative integer>,"pin_state":<enum>,"state":<lifecycle>}, ...]`
with the same order. `active_cleanup_exclusion_conflicts` hashes the JCS
array of exactly
`[{"item_id":<opaque>,"conflict_code":<closed-code>,"lease_generation":<nonnegative integer>}, ...]`
in item/order registry order. The lease generation is a catalog version, not
a public source key.

`preview_digest` hashes a JCS object containing exactly the preview response
fields from `schema_version` through `backup_non_purge_warning_code`, plus
`expected_state_version` and `target_item_set_digest`, but excluding
`preview_id`, `preview_digest`, `confirmation_expires_at`, and all comments.
This makes the digest deterministic for the sanitized impact while the server
still binds the preview record and fixed five-minute expiry separately.

## Confirmation token contract

The preview response does not contain a confirmation token value. The UI must
first re-display the complete sanitized preview, then call the separate
confirmation-issue route. `RetentionConfirmationIssueResponse` is the only
API response that contains a token value; it is `no-store`, never logged, and
never placed in a preview, audit event, diagnostic, evidence record, URL, or
repository artifact.

The token wire format is exactly:

```text
rt90v1_<base64url-no-padding-16-byte-nonce>_<base64url-no-padding-32-byte-secret>
```

The server generates the nonce and secret with its cryptographically secure
random source. It checks the 16-byte nonce for an existing active nonce in the
same catalog; a collision returns `retention_confirmation_generation_failed`
and no token is issued. The stored token value is only the SHA-256 digest of
the exact token bytes. The server never logs, hashes into a diagnostic, or
persists the plaintext token.

The token binds exactly these fields:

1. `schema_version = 1`;
2. `target_kind` and exact `target_id`;
3. `operation` and `scope`;
4. `preview_digest`;
5. `expected_state_version`;
6. `target_item_set_digest`;
7. `active_conflict_snapshot` and its `conflict_version`;
8. `confirmation_expires_at`;
9. the server-generated nonce;
10. the exact `Idempotency-Key` value;
11. the selected `reason_code` and SHA-256 digest of the normalized comment.

The token is single-consumption. The catalog marks it consumed in the same
durable transaction as the mutation, idempotency result, audit event, and any
#89 queue request. A transaction rollback leaves the token unconsumed. A
committed result followed by a lost response is recovered through the same
idempotency key; the token itself is then rejected as consumed.

The closed reject-and-republish registry is:

| Condition | Fixed code | Republish rule |
| --- | --- | --- |
| Token format, digest, nonce, or stored token hash is invalid | `retention_confirmation_invalid` | Discard the token and create a new preview/key. |
| Preview ID or stored preview cannot be found | `retention_preview_not_found` | Create a new preview/key. |
| Preview or confirmation is past the exact five-minute expiry | `retention_confirmation_expired` or `retention_preview_expired` | Create a new preview/key; issuance never extends expiry. |
| Submitted operation, scope, target kind, or target ID does not equal the binding | `retention_confirmation_binding_mismatch` | Create a new preview/key. |
| Exact target item set changed | `retention_confirmation_target_changed` | Create a new preview/key. |
| Pin state changed | `retention_confirmation_pin_changed` | Create a new preview/key. |
| Retention state, policy, captured-at basis, or expiry changed | `retention_confirmation_retention_changed` | Create a new preview/key. |
| Any expected catalog/pin/revision vector changed | `retention_confirmation_version_changed` | Create a new preview/key. |
| An active cleanup conflict was added, removed, or changed | `retention_confirmation_conflict_changed` | Create a new preview/key. |
| Token was committed by another request | `retention_confirmation_consumed` | Use the stored idempotency result if the same key is available; otherwise create a new preview/key. |

These are the only token validation outcomes. A precondition rejection after
successful binding uses the transition error code in the mutation registry;
it never silently changes the target or republishes a token.

## Idempotency contract

Every preview, confirmation issue, and mutation request carries the exact
`Idempotency-Key` header. The key is client-generated, 48 ASCII characters in
the `rid1_` format, and contains no user text. It is scoped to one physical
retention catalog database. It is not shared across databases, Sessions,
processes, or users.

The catalog stores an idempotency row containing the SHA-256 key digest, the
request fingerprint, operation, target kind, target ID, preview ID, operation
ID, result DTO, created time, expiry time, and completion code. The row and its
365-day expiry are durable. After expiry the row is retained as a non-reusable
tombstone and the same key returns `retention_idempotency_expired`; it can
never cause a second destructive command.

The same key plus byte-identical canonical request returns the exact same
stored preview or mutation result, including a no-op result, without another
state transition or audit event. The same key with any different target,
operation, scope, reason, comment digest, preview digest, or confirmation
token returns `retention_idempotency_conflict`. A fresh preview after any
stale/expired/conflict rejection uses a new idempotency key.

## Append-only audit contract

Only a confirmed, durably committed command creates an audit event. This
includes a confirmed pin/unpin no-op and an already-queued delete-now result.
Preview reads, token issuance, invalid requests, stale-token rejects, and
unconfirmed precondition failures create no audit event. A command's audit
event and idempotency result are committed together with the state result.

`RetentionAuditEvent` has exactly these fields:

| Field | Type and rule |
| --- | --- |
| `event_id` | Opaque `rae1_` plus 22 unpadded base64url characters; server generated. |
| `event_type` | Exactly `retention_mutation`. |
| `target_kind` | `session` or `item`. |
| `target_id` | Exact Session ID or exact opaque item ID. |
| `session_id` | Exact Session ID for a Session target; JSON `null` for an item target without an exact Session. |
| `occurred_at` | Server UTC RFC 3339 time at durable commit. |
| `actor_label` | Exactly `local-user`. |
| `operation` | `pin`, `unpin`, or `delete_now`. |
| `reason_code` | Exactly one of the closed reason registry below. |
| `comment` | Null or the normalized 1–256 scalar safe comment; never raw evidence. |
| `previous_pin_state` | `pinned`, `unpinned`, `not_applicable`, or `mixed`. |
| `new_pin_state` | Same closed enum. |
| `previous_operation_state` | A seven-state count object with exactly one nonnegative count property per #89 lifecycle state. |
| `new_operation_state` | The same exact seven-key count object. |
| `request_idempotency_key` | The exact `rid1_...` value; it is never returned in logs or evidence, but is available in this audit read model. |
| `expected_version` | `v1-` plus the expected target-set version-vector digest. |
| `result_version` | `v1-` plus the resulting target-set version-vector digest. |
| `target_item_set_digest` | `sha256-` digest of the exact item/store-kind set. |
| `queue_request_id` | Opaque #89 durable queue request ID for delete-now or an unpin that queues; JSON `null` for pin and future-readable unpin. |
| `completion_code` | One code from the closed completion registry below. |
| `error_code` | JSON `null` for a committed successful result; otherwise one fixed mutation code. A committed command never has both a success-only completion and free-form exception text. |

The `previous_operation_state` and `new_operation_state` objects make a
Session event exact without storing one audit row per item. For an item target,
one key has value `1` and the other six have value `0`. The total of each
object equals the exact target item count.

The reason registry is exactly:

`research_needed`, `review_complete`, `privacy_request`, `data_minimization`,
`test_cleanup`, `operator_correction`, and `other_local_reason`.

The completion registry is exactly:

`retention_pin_applied`, `retention_pin_noop`,
`retention_unpin_applied`, `retention_unpin_noop`,
`retention_unpin_expired_queued`, `retention_delete_queued`,
`retention_delete_already_queued`, and
`retention_delete_now_superseded_pin`.

`retention_mutation_replayed` is a result-only code for a same-key response;
it is never written as a second audit event or used as an audit completion
code.

The audit store is append-only: events are never updated, overwritten, or
deleted by retry, restart, cleanup, or a later pin/unpin/delete-now command.
The only free-form field is the safe comment with the exact 256-scalar limit.
The following are forbidden in comments and all other audit fields, preview
fields, logs, diagnostics, and repository-safe evidence: raw bodies or raw
prompt/response/tool content, credentials or secrets, absolute or relative
paths, database primary keys, prompt-derived labels, private locators, full
exceptions, PII, and confirmation token values. Rejection is fail-closed;
these values are not redacted into a partially accepted audit event.

## Atomic mutation boundary and failure visibility

For a confirmed target set, one durable SQLite transaction/outbox boundary
commits all of the following or none of them:

1. the expected-revision check for every exact target item;
2. the pin state and lifecycle mutation, including the `retained_by_policy`
   to `expiring` unpin seam;
3. the original-capture/policy-based expiry recalculation;
4. irreversible `read_denied_at` and the #89 forward queue state for an
   expired-at-unpin or delete-now result;
5. the #89 durable deletion request and one opaque queue request ID when
   deletion is requested;
6. confirmation-token consumption;
7. the idempotency row and exact result DTO;
8. the append-only audit event.

For a Session target, all selected items are checked and committed in the same
transaction. There is no partial pin, unpin, or delete-now result. SQLite
source deletion remains outside this transaction and is performed only later
by the existing #89 adapter/worker.

If the transaction cannot commit, the server returns `503` with
`retention_mutation_transaction_failed` or `retention_audit_write_failed`,
with no operation ID, no queue ID, no success body, and no state presented as
committed. Normal rollback leaves the token and idempotency key eligible for
the exact retry rule. If the commit succeeded but the response was lost, a
same-key retry returns the stored result. A worker or adapter failure after
commit is visible only through the existing #89 item state, error code, retry
metadata, status read, and the #90 audit queue request reference; it cannot
make content readable again.

## Versioned API and read compatibility

### Exact route list

The #90 mutation/read surface is:

| Method and route | Request | Response |
| --- | --- | --- |
| `GET /api/retention/v1/status` | #89 query shape; optional `limit=1..100` is the existing fixed 100-item window. | Existing #89 `RetentionStatusResponse` unchanged. Items are ordered `expires_at ASC, item_id ASC`; the response has no new cursor or field. |
| `GET /api/retention/v1/sessions/{sessionId}` | Exact local Session ID. | Existing #89 `RetentionSessionStatusResponse` unchanged. |
| `GET /api/retention/v1/items/{itemId}` | Exact opaque item ID. | `RetentionItemStateResponse`. |
| `GET /api/retention/v1/sessions/{sessionId}/history?limit=1..100&cursor=<opaque>` | Optional limit defaults to 100; cursor is exclusive. | `RetentionHistoryResponse`. |
| `GET /api/retention/v1/items/{itemId}/history?limit=1..100&cursor=<opaque>` | Optional limit defaults to 100; cursor is exclusive. | `RetentionHistoryResponse`. |
| `POST /api/retention/v1/previews` | `RetentionMutationPreviewRequest` plus `Idempotency-Key`. | `RetentionMutationPreviewResponse`. |
| `GET /api/retention/v1/previews/{previewId}` | Exact opaque preview ID. | Same `RetentionMutationPreviewResponse`, never a token. |
| `POST /api/retention/v1/confirmations` | `RetentionConfirmationIssueRequest` plus `Idempotency-Key`. | `RetentionConfirmationIssueResponse`. |
| `POST /api/retention/v1/mutations` | `RetentionMutationConfirmRequest` plus matching `Idempotency-Key`. | `RetentionMutationResult`. |
| `GET /api/retention/v1/mutations/{operationId}` | Exact opaque operation ID. | `RetentionMutationStatusResponse`. |

`RetentionConfirmationIssueRequest` has exactly `preview_id` and
`preview_digest`. `RetentionConfirmationIssueResponse` has exactly
`schema_version`, `confirmation_id`, `confirmation_token`, and
`confirmation_expires_at`. `RetentionMutationConfirmRequest` has exactly
`confirmation_token`.

`RetentionItemStateResponse` has exactly `schema_version`, `item_id`,
`store_kind`, `state`, `pin_state`, `delete_state`, `policy_id`,
`policy_version`, `captured_at`, `expires_at`, `read_denied_at`, `queued_at`,
`deletion_started_at`, `deleted_at`, `attempt_count`, `retry_exhausted`,
`error_code`, `retry_at`, `revision`, and `session_id` (nullable). It uses only
#89 allowlisted fields plus #90 pin/delete state and revision.

`RetentionHistoryResponse` has exactly `schema_version`, `target_kind`,
`target_id`, `events`, and `next_cursor`. `events` contains the exact
`RetentionAuditEvent` schema above. `next_cursor` is null on the last page or
an opaque `rhc1_` cursor otherwise. History uses `occurred_at DESC,
event_id DESC`, and a cursor resumes strictly after the last pair. No cursor
contains a source key, path, comment, token, or raw value.

`RetentionMutationResult` has exactly `schema_version`, `operation_id`,
`result_code`, `target_kind`, `target_id`, `operation`, `scope`,
`target_item_count`, `pin_state`, `lifecycle_counts`, `read_denied`,
`queue_request_id`, `audit_event_id`, `expected_version`, `result_version`,
`backup_non_purge_warning_code`, `idempotent_replay`, `created_at`, and
`completed_at`. Its physical deletion status is the #89 state, not a claim
that the worker has completed.

`RetentionMutationStatusResponse` has exactly
`schema_version`, `operation_id`, `operation`, `target_kind`, `target_id`,
`status`, `result_code`, `lifecycle_counts`, `read_denied`,
`queue_request_id`, `audit_event_id`, `idempotent_replay`, `created_at`, and
`completed_at`, and `backup_non_purge_warning_code`. `status` is exactly
`committed` or `replayed`; a transaction failure leaves no operation record,
and later #89 worker failure is read from the #89 item status.

### HTTP status mapping

| HTTP status | Conditions |
| --- | --- |
| `200` | Read, preview, confirmation issue, committed mutation, no-op mutation, already-queued result, or idempotent replay. A committed delete-now response means read denial and durable queue handoff, not physical completion. |
| `400` | `retention_mutation_request_invalid`, `retention_idempotency_key_invalid`, or `retention_history_cursor_invalid`. |
| `401` | `retention_confirmation_invalid` when the token cannot be structurally or cryptographically validated. |
| `404` | `retention_target_not_found`, `retention_preview_not_found`, or `retention_operation_not_found`. |
| `409` | Any stale/expired/consumed/binding/precondition/idempotency conflict code except the committed `retention_delete_already_queued` result, plus `retention_target_empty` or `retention_target_not_applicable`. |
| `413` | `retention_mutation_target_limit_exceeded`. |
| `503` | `retention_confirmation_generation_failed`, `retention_mutation_transaction_failed`, `retention_audit_write_failed`, or inherited `retention_catalog_unavailable`. |

All error bodies are the exact one-property JSON shape
`{"error":"<code>"}` with no extra text. `401` responses do not identify
which token component failed. `409` responses do not echo the current or
submitted value. All routes are loopback/Host-header restricted, same-origin,
JSON-only where a body exists, CSRF-protected for state-changing requests,
and `no-store`.

### New mutation diagnostic/error registry

The following is the complete new #90 registry. It does not reuse a code from
the #89 registry, including `retention_lease_conflict`,
`retention_lease_lost`, `retention_unexpected_source_missing`, or
`retention_item_limit_exceeded`.

| Code | Use |
| --- | --- |
| `retention_mutation_request_invalid` | Invalid union, operation, scope, reason, comment, or body. |
| `retention_target_not_found` | Exact Session or item target is absent. |
| `retention_target_empty` | Confirmation requested for an explicit empty/not-applicable target. |
| `retention_target_not_applicable` | Exact target exists but is not a product-owned v1 mutation target. |
| `retention_mutation_target_limit_exceeded` | Exact Session target has more than 100 items. |
| `retention_preview_not_found` | Preview ID is not present in the current catalog. |
| `retention_preview_expired` | Preview read/issuance is past its five-minute expiry. |
| `retention_preview_digest_mismatch` | Supplied preview digest does not match the persisted preview. |
| `retention_confirmation_generation_failed` | Server nonce collision prevented safe token issuance. |
| `retention_confirmation_invalid` | Confirmation token format or digest is invalid. |
| `retention_confirmation_expired` | Confirmation token is past its fixed expiry. |
| `retention_confirmation_consumed` | Confirmation token was already committed. |
| `retention_confirmation_binding_mismatch` | Token binding does not match the confirmation request. |
| `retention_confirmation_target_changed` | Exact target item set digest changed. |
| `retention_confirmation_pin_changed` | Pin state changed after preview. |
| `retention_confirmation_retention_changed` | Retention policy, captured-at basis, or expiry changed. |
| `retention_confirmation_version_changed` | Expected catalog/revision vector changed. |
| `retention_confirmation_conflict_changed` | Active cleanup conflict snapshot changed. |
| `retention_idempotency_key_invalid` | Key format is not the exact 48-character `rid1_` format. |
| `retention_idempotency_conflict` | Same key was submitted for a different canonical request. |
| `retention_idempotency_expired` | The 365-day key lifetime has ended; key reuse is forbidden. |
| `retention_pin_read_denied` | Pin attempted on an already denied lifecycle. |
| `retention_pin_deleting` | Pin attempted while #89 is deleting. |
| `retention_pin_deleted` | Pin attempted on a deleted tombstone. |
| `retention_pin_expired` | Expiry won a pin race at commit. |
| `retention_unpin_read_denied` | Unpin attempted on an already denied lifecycle. |
| `retention_unpin_deleting` | Unpin attempted while #89 is deleting. |
| `retention_unpin_deleted` | Unpin attempted on a deleted tombstone. |
| `retention_delete_already_queued` | Delete-now is already durably queued. |
| `retention_delete_already_deleting` | Delete-now is already in #89 deletion. |
| `retention_delete_already_deleted` | Delete-now targets a terminal deleted tombstone. |
| `retention_delete_failed` | Delete-now targets an item in #89 `deletion_failed`; #89 retry rules remain authoritative. |
| `retention_mutation_transaction_failed` | Durable mutation transaction rolled back or could not commit. |
| `retention_audit_write_failed` | Required append-only audit write prevented commit. |
| `retention_backup_not_purged` | Fixed warning shown for every actionable preview/result; no backup purge is promised. |
| `retention_operation_not_found` | Mutation operation ID is absent. |
| `retention_history_cursor_invalid` | History cursor is malformed or not valid for the requested target. |

`retention_backup_not_purged` is a warning field, not a failure status. The
#89 codes remain available only where their existing catalog/worker contracts
already use them.

### Frozen Session workspace v1 projection

Issue #90 does not add pin, deletion, audit, preview, revision, or queue fields
to `/api/session-workspace/*` v1. Existing response property sets, enums,
status codes, and raw-response bytes remain exact. The #89 projection applies:

| Canonical #90 result | v1 event `content_state` | v1 Session `raw_retention_state` | Raw content route |
| --- | --- | --- | --- |
| Readable unpinned `expiring` | `available` | `expiring` when any readable sibling exists | Existing `200` |
| Readable pinned `retained_by_policy` | `available` | `expiring` when any readable sibling exists | Existing `200`; v1 does not reveal pin. |
| `expired_pending_deletion`, `deletion_queued`, `deleting`, any `deletion_failed`, or `deleted` | `expired_pending_deletion` | `expired_pending_deletion` when no readable sibling exists | Existing exact `410` body. |
| Confirmed delete-now before physical deletion | `expired_pending_deletion` | `expired_pending_deletion` when no readable sibling exists | Existing exact `410` body immediately after commit. |
| No represented item | Existing `not_captured`, `redacted`, or `unsupported` | `not_captured` | Existing `404`. |

A readable sibling retains the #89 safe precedence: it projects the Session
aggregate as `expiring`, while a selected denied event still returns its own
`410`. A deleted item never becomes `404` merely because physical deletion
completed.

## UI read model and UX contract

The UI consumes the same authoritative application result as the API. It does
not reconstruct state from the frozen workspace v1 shape, DOM text, URL
parameters, source tables, or client-side timers.

The Session target is the primary flow. From the Session workspace, the UI
offers a non-destructive `Manage retention` action that loads the exact
Session-target preview. The item target is available only from an explicit
selection in the #89 retention diagnostics item list; there is no free-form
item-ID input, approximate search, multi-Session selector, or path selector.

Delete-now is never the visually primary action. It is a secondary destructive
choice behind the retention details/confirmation step, has no default focus,
and cannot be triggered by pressing Enter on the initial Session action.

Before confirmation, the UI must re-display all of these sanitized values in
the same confirmation surface: target kind and exact ID, operation and scope,
exact item count and store-kind summary, current lifecycle/pin/delete state,
pin removal for delete-now, exact deletion target set summary, original
capture/expiry/policy summary, retained metadata/evidence impact, active
cleanup conflicts, fixed backup non-purge warning, expected version, preview
digest, and five-minute expiry. The UI never displays the token value as
preview content; the token is held in memory only for the dedicated confirm
request and is discarded after success, failure, navigation, or expiry.

On `retention_confirmation_*`, `retention_preview_*`, empty, stale, conflict,
or incompatible-state results, the UI discards the token, does not retry
automatically, fetches a fresh status/preview, and re-displays the new
sanitized values. It must not present the old operation as successful. After a
committed result it shows the operation status, immediate read-denied state,
queue handoff, audit reference, and the #89 physical worker status; it does
not claim physical deletion at transaction commit.

Accessibility is contractual: use a semantic labelled dialog, a visible
destructive-action warning, keyboard focus placement and return, Escape
cancellation, Enter only on the final explicit confirmation, labels and help
text associated with reason/comment controls, a screen-reader live region for
status/error changes, and text plus icon/state indicators rather than color
alone. Focus never lands on a destructive command before the target and impact
have been read.

Client state may contain only the sanitized DTO fields, opaque IDs, fixed
codes, digests, timestamps, and an in-memory confirmation token while the
confirmation request is outstanding. It must not contain raw bodies,
credentials, absolute/full paths, database primary keys, prompt-derived
labels, private locators, full exceptions, or token values in persistent
storage, URLs, analytics, logs, screenshots, or evidence.

## Security and no-leak pins

The existing Local Monitor local-first boundary remains in force: loopback
bind, Host-header validation, CORS disabled, same-origin checks, CSRF on every
state-changing route, JSON body limits, and `Cache-Control: no-store`.

The following are forbidden in preview responses, confirmation issue logs,
mutation responses, audit events, status/history DTOs, structured logs,
diagnostics, screenshots/evidence, committed fixtures, and repository-safe
handoff material:

- raw bodies, prompt/response content, system content, tool arguments/results,
  or raw payload fragments;
- credentials, secrets, authorization material, or token values;
- absolute or relative local paths, private locators, or filesystem names;
- database primary keys or source-row identifiers;
- prompt-derived labels, user-entered identifying text, PII, or full
  exception/provider text.

Opaque retention item IDs, local Session IDs where the existing API already
allows them, fixed store kinds, fixed state/error codes, safe digests, and
sanitized timestamps are the only identifiers permitted by the DTOs above.
The token value is permitted only in the dedicated in-memory confirmation
issue response and the immediately following request body; it is not an
audit or preview value.

## Out of scope and fixed warning

Issue #90 does not implement or authorize:

- a parallel retention lifecycle, catalog, cleanup worker, queue, lease, or
  raw-store deletion adapter;
- heuristic, query-based, proximity-based, path-based, prompt-based, or bulk
  targeting; a single exact Session scope is the only multi-item operation;
- multi-Session mutation, arbitrary raw-record selection, repository/time
  selection, or automatic diagnostics selection;
- backup, snapshot, offline, external, media, or secure-hardware purge;
- legal hold, team policy, remote administration, or shared-service governance;
- sanitized aggregate deletion, raw reconstruction, resurrection, retry-based
  restoration, import/replay/backup production integration, or future-store
  placeholders;
- any change to the #89 lease durations, scan/claim limits, retry policy,
  store registry, ownership key, read-denial behavior, or workspace v1 wire
  shape.

Every actionable preview and mutation result carries the fixed warning code
`retention_backup_not_purged`. It is a fixed warning with no backup
inventory, path, or promise of deletion. A later #88 contract may add an exact
backup inventory; #90 does not anticipate or infer it.

## Implementation mapping for 90-B through 90-F

90-A is this contract and its reviewed promotion into the canonical specs.
The implementation tasks below are ordered and name their deliverable,
test focus, and safe-parallelization boundary. “Yes” means file-disjoint work
is safe after the stated predecessor contract is committed; it does not permit
parallel changes to shared DTOs or the same persistence migration.

### 90-B — Persistence, versioning, and audit

| Order | Task | Deliverable | Test focus | File-disjoint and parallel? |
| --- | --- | --- | --- | --- |
| B1 | Add the additive v1 mutation schema for pin state, preview records, confirmation bindings, idempotency rows, operation receipts, and audit events. | Restart-safe catalog tables/indexes with exact version fields and no source-table duplication. | Migration from a fresh and populated #89 catalog; injected migration rollback; exact schema version. | No; schema owner must complete before B2–B4. |
| B2 | Implement exact target-set version vectors, target-set digests, and optimistic CAS persistence. | Stable `expected_version`/`result_version` materialization for 1–100 items. | Same revision rejects stale writes; concurrent writers cannot partially commit. | No; depends on B1. |
| B3 | Implement idempotency and append-only audit repositories with closed reason/comment/completion validation. | Durable same-key result lookup and exact `RetentionAuditEvent` rows. | Same-key same-result, different-request conflict, 365-day expiry tombstone, comment forbidden-input rejection, append-only enforcement, no-leak output. | No; shares B1 tables and B2 version records. |
| B4 | Implement confirmation binding/token hash storage and single-consumption persistence. | Hash-only token records with exact five-minute expiry and nonce collision behavior. | Format/nonce/hash validation, rollback leaves token unconsumed, commit consumes once, restart preserves consumption. | No; depends on B1–B3. |

90-B deliverable: restart-safe mutation/audit/confirmation persistence that
does not perform a lifecycle transition by itself.

### 90-C — Deterministic preview and conflict engine

| Order | Task | Deliverable | Test focus | File-disjoint and parallel? |
| --- | --- | --- | --- | --- |
| C1 | Implement Session exact-link resolution and the seven-code exclusion projection. | Exact `session | item` resolver using only the #89 qualifying linkage fields. | Positive direct Session/event linkage; negative run/trace/path/time proximity; every exclusion code; empty/not-applicable shape; foreign and unknown targets. | Yes with C2 only if C1 writes resolver contracts in a separate domain file; otherwise sequential. |
| C2 | Implement exact item-set collection, 100-item limit, store-kind summaries, capture/expiry/policy summaries, and retained-impact projection. | Sanitized `RetentionMutationPreviewResponse` read model with no raw/source locator. | Exact ordering, all seven lifecycle counts, 100-item boundary, 101-item fail-closed result, no source key/path leakage. | No; depends on C1 and B2. |
| C3 | Implement JCS SHA-256 preview/version/conflict digests and five-minute preview records. | Deterministic digest values and persisted preview ID/expiry. | Byte-identical digest under repeated reads; property-order/locale differences do not alter it; expiry does not extend on re-read. | No; depends on C2. |
| C4 | Implement confirmation issuance and all reject-and-republish checks. | Separate token issuance DTO and exact closed confirmation error mapping. | Target/item-set/pin/retention/version/conflict changes, operation/scope mismatch, expiry, invalid token, consumption, and nonce collision. | No; depends on B4 and C3. |

90-C deliverable: a deterministic preview and confirmation engine whose target
boundary and digest are identical to the later commit boundary.

### 90-D — Atomic mutation orchestration

| Order | Task | Deliverable | Test focus | File-disjoint and parallel? |
| --- | --- | --- | --- | --- |
| D1 | Implement `pin` apply/no-op and state preconditions. | `expiring -> retained_by_policy` seam with read-safe idempotent pin. | Pin all seven states, pin no-op, read-denied/deleting/deleted rejection, expiry race. | No; shared mutation orchestrator. |
| D2 | Implement `unpin` recalculation and direct expiry queue handoff. | Original `captured_at` + recorded policy/version calculation with no TTL reset. | Future expiry, already-expired unpin, `expiring` no-op, denied/deleting/deleted rejection, original timestamp preservation. | No; depends on D1’s CAS and shared orchestrator. |
| D3 | Implement delete-now supersession and the #89 durable queue request. | Atomic pin clear/read denial/queue for retained items and idempotent already-queued result. | Delete-now from expiring/retained/queued/deleting/deleted/failed; no second queue row; no resurrection. | No; depends on D1–D2 and #89 queue contract. |
| D4 | Close the transaction boundary and result replay behavior. | One all-or-none transaction for state, denial, token, idempotency, audit, and outbox. | Injected failure at each write; no partial state/result/audit; lost response replay; restart and two-writer race. | No; depends on D1–D3. |

90-D deliverable: lifecycle-consistent mutation orchestration with durable
queue handoff and no direct source deletion.

### 90-E — Versioned API and Local Monitor UI

| Order | Task | Deliverable | Test focus | File-disjoint and parallel? |
| --- | --- | --- | --- | --- |
| E1 | Add exact API DTOs, routes, HTTP/error mapping, CSRF/same-origin/no-store controls. | The route list and response fields in this document, with no workspace v1 changes. | JSON shape, status/error bytes, invalid/empty/stale/expired/conflict handling, no-leak headers/body. | No; establishes the shared application/API contract. |
| E2 | Add current item, status, operation status, and cursor-paged history projections. | Exact 100-entry status/history windows and fixed order/cursor behavior. | First/last cursor pages, duplicate/skip prevention, empty history, unknown target, 100/101 boundaries. | Yes with E3 after E1 DTOs are frozen; separate API read files. |
| E3 | Add the UI authoritative read model and Session-primary mutation flow. | Preview/confirmation/status UI with item target only from diagnostics selection. | Re-display rules, delete-now secondary action, in-memory token only, no client raw/path state. | Yes with E2 after E1 DTOs are frozen; separate UI files. |
| E4 | Add Playwright interaction and accessibility coverage. | Browser evidence for Session/item happy/error/status flows. | Keyboard/focus/screen-reader labels, stale/expired/empty/conflict errors, immediate read denial, backup warning, no token/raw/path leakage. | No; depends on E2 and E3. |

90-E deliverable: one safe API/UI surface over the same application result.

### 90-F — Integration, security, and closeout

| Order | Task | Deliverable | Test focus | File-disjoint and parallel? |
| --- | --- | --- | --- | --- |
| F1 | Run the lifecycle integration matrix and record operation/audit/queue evidence. | Repository-safe integration evidence for pin -> expiry suppression -> unpin -> queue and pinned Session -> delete-now. | Exact Session/item targets, empty target, already deleted, worker failure, restart, and physical completion through #89 only. | Yes with F2 after E4; separate test/evidence files. |
| F2 | Run the security/no-leak matrix and inspect all response/log/evidence surfaces. | Repository-safe no-leak evidence with synthetic markers only. | Raw/body/path/credential/database-key/prompt-label/token injection; loopback/Host/same-origin/CSRF/no-store controls. | Yes with F1 after E4; separate security tests. |
| F3 | Record #91 security rows and #88 tombstone/non-purge handoff. | Stable consumer handoff containing only opaque IDs, fixed codes, digests, and references. | Handoff schema/allowlist and no backup inventory claim. | No; depends on F1 and F2. |
| F4 | Run the pinned repository validation sequence. | Build, Playwright bootstrap, full test result, and exact unverified scope. | `dotnet build CopilotAgentObservability.slnx`; `pwsh scripts/test/install-playwright-chromium.ps1`; `dotnet test CopilotAgentObservability.slnx`. | No; final sequential closeout. |

90-F deliverable: repository-safe evidence and consumer handoff proving that
#90 uses #89’s read-denial, queue, adapter, lease, and tombstone contracts.

## Ambiguity decisions recorded for 90-A

The Issue #90 body leaves several mechanics open. This document resolves them
as follows:

1. The required preview fields do not include a token, while a confirmation
   needs one. Token issuance is therefore a separate `POST /confirmations`
   response; the preview response never carries a token value.
2. Session ownership means exact `session_id` or exact
   `event_id -> session_id` only. A run, trace, evidence, time, path, or
   prompt relationship is intentionally not ownership.
3. A Session mutation is one atomic command over all exact-owned items, with
   the inherited 100-item limit. It is not a bulk/multi-Session operation.
4. A pin preserves the original policy-derived expiry as inactive historical
   metadata. Unpin recalculates from the original capture/policy record and
   never resets the clock.
5. An unpin that reaches its original expiry at commit queues directly. A
   delete-now on an already queued item is a confirmed idempotent result; an
   item already deleting, deleted, or failed follows the explicit rejection
   rows rather than starting a second path.
6. `local-user` is the v1 actor label. The current OS user name is not placed
   in an audit event or response.
7. The existing #89 `/status` DTO remains an exact 100-item window with no new
   cursor field. Cursor pagination is added only to the new history reads, so
   workspace and #89 status response shapes are not silently expanded.
8. `retention_backup_not_purged` is a fixed warning, not a promise or an
   inferred backup inventory. Backup deletion remains outside #90.

These choices are preparation for 90-A. Until canonical specification changes
are approved, the current canonical documents remain the implementation
authority and continue to describe pin/unpin/delete-now as pending Issue #90.

## Acceptance and close condition

Issue #90 is ready for implementation only when 90-A has promoted this exact
contract and 90-B through 90-F can demonstrate every matrix above. Close
requires: exact `session | item` targeting; no heuristic or parallel cleanup;
preview and token binding; atomic pin/read-denial/idempotency/audit/queue
commit; frozen workspace v1 compatibility; append-only no-leak audit; status
and history pagination; accessible Session-primary UI; and repository-safe
handoff evidence for #91 and #88.
