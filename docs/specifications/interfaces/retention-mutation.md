# Retention Mutation Interface

Issue #90 defines the versioned Local Monitor retention mutation/read surface
over the Issue #89 retention catalog. This document is the canonical v1 API
contract for `pin`, `unpin`, and `delete_now`. It is additive to the frozen
Session workspace v1 surface and does not create a second lifecycle, catalog,
worker, queue entity, or physical-delete path.

## Contract foundation and fixed values

Every mutation uses the one catalog instance for the Local Monitor database
and the existing five-adapter coverage contract. These inherited #89 values
remain fixed:

| Contract | Fixed v1 value |
| --- | --- |
| Catalog and adapter coverage version | `1` |
| Lifecycle | `expiring`, `retained_by_policy`, `expired_pending_deletion`, `deletion_queued`, `deleting`, `deleted`, `deletion_failed` |
| Policies | `raw-default-90d` v1 = 90 days; `sensitive-bundle-7d` v1 = 7 days |
| Scan item limit | `100` |
| Claim batch limit | `100` |
| Scan elapsed budget | `30 seconds` |
| Queue/claim order | `expires_at ASC, item_id ASC` |
| Worker wake interval | `15 seconds` |
| Maximum active deletion workers | `2` |
| Access, operation, and deletion lease duration | `2 minutes` |
| Lease renewal deadline | No later than `1 minute` after acquisition or prior renewal |
| Active-operation quiescence and shutdown/drain bounds | `2 minutes` each |
| WAL-maintenance retry delay | `1 minute` |
| Delete attempts and retry schedule | Maximum `5`; after failures `1`, `5`, `30` minutes, then `2 hours`; failure `5` terminal |
| File item limits | `256` exact members and `128 MiB` journaled bytes, independently for Sensitive Bundles and SDK directories |
| Status item-summary limit | `100` |
| Ownership key | `(store_instance_id, store_kind, source_item_id)` |
| Existing error style | Fixed `retention_*` code, with no exception/provider/path/raw supplement |

`not_captured` and `mixed` are aggregate values only and are never persisted
item states. `read_denied_at` is irreversible. A missing source is not success
without the #89 proof and receipt. Physical removal is eventual adapter work
after the durable queue commit.

The new Issue #90 v1 values are:

| Contract | Fixed value |
| --- | --- |
| Target kinds | Exactly `session` and `item` |
| Operations | Exactly `pin`, `unpin`, and `delete_now` |
| Scopes | Exactly `session_items` and `single_item`; `session_items` is valid only with a `session` target, and `single_item` only with an `item` target |
| Mutation target limit | `100` exact target items; a larger exact set fails closed with `retention_mutation_target_limit_exceeded` and is never partially applied |
| Preview item order | `expires_at ASC, item_id ASC`; a null `expires_at` sorts after every finite timestamp and ties by opaque `item_id` |
| Preview item list | At most `100` exact items; omission is never used to signal success |
| Exclusion reason registry | Exactly `missing_ownership_proof` for computable per-item counts; structural exclusions are fixed statements, not counted codes |
| Store-kind summary | At most the five #89 store kinds, in this order: `session_event_content`, `raw_record`, `analysis_run_raw`, `sensitive_bundle`, `analysis_sdk_directory` |
| Active-conflict registry | Exactly `active_read_lease`, `active_operation_lease`, `active_deletion_lease`, and `active_delete_intent` |
| Confirmation lifetime | Exactly `5 minutes` from preview creation; issue/reissue never extends preview expiry |
| Preview ID | `rpv1_` plus `22` unpadded base64url characters encoding `16` server-random bytes |
| Confirmation ID | `rcid1_` plus `22` unpadded base64url characters encoding `16` server-random bytes |
| Confirmation token | `rt90v1_` plus `22` unpadded base64url nonce characters, `_`, and `43` unpadded base64url secret characters |
| Server nonce | Exactly `16` cryptographically secure random bytes per issued token; a collision is rejected rather than retried |
| Token secret | Exactly `32` cryptographically secure random bytes; plaintext is never persisted; the persisted hash is SHA-256 over the exact full ASCII token string; at most one unconsumed token exists for a preview |
| Idempotency key | `rid1_` plus `43` unpadded base64url characters encoding `32` client-generated random bytes; total length `48` ASCII characters |
| Idempotency lifetime | `365 days` from first durable record creation; after expiry the key is permanently rejected and never reused |
| Pin-state source of truth | `pin_state` is derived: `pinned` iff lifecycle state is `retained_by_policy`; otherwise a represented catalog item is `unpinned` |
| Comment | Null or `1–256` Unicode scalar values, NFC-normalized, with no control character, CR/LF, URL, path separator, credential marker, database-key marker, or token value |
| Actor label | Server-derived fixed value `local-user`; the client cannot supply or override it |
| History page | Default `100` and maximum `100` entries; cursor pagination is exclusive and opaque |
| History order | `occurred_at DESC, event_id DESC` (newest first) |
| HTTP cache policy | `Cache-Control: no-store` on every preview, confirmation, mutation, status, item, and history response |
| Error body | Exactly `{"error":"<fixed-code>"}` with no message, rejected value, exception, path, or token |

Every timestamp is UTC RFC 3339 with seven fractional digits. Every digest is
lowercase hexadecimal with the fixed prefix `sha256-`, unless the field's
format is explicitly stated otherwise.

## Exact target union and resolution

`RetentionMutationTarget` has exactly these fields:

| Field | Type and rule |
| --- | --- |
| `kind` | Required closed enum: `session` or `item` |
| `id` | Required exact ID. For `session`, an existing local UUIDv7 Session ID. For `item`, the exact opaque #89 catalog `item_id` returned by an authoritative diagnostics read. It is never decoded, normalized, searched, or converted to a database key or path. |

The request body contains exactly one target object. Both kinds, missing or
blank `id`, an invalid Session UUID, a second selector, or any repository,
workspace, trace, time, path, prompt, source-row, or free-text selector is
`retention_mutation_request_invalid`.

### Session target linkage

A `session` target resolves the exact Session ID in the current catalog
database and evaluates one deterministic catalog-only projection. It never
scans raw/source stores, the filesystem, backups, or a repository, and never
assembles a set from client fields. The #89 catalog has no persisted
`session_id` column on `retention_items`; the mutation schema must not add one.

Only this linkage qualifies in v1:

| #89 linkage | Qualification |
| --- | --- |
| `session_event_content.source_item_id` joined exactly to `session_events.event_id` | Qualifies only when the joined row's persisted `session_id` equals the requested Session ID and the #89 ownership key/receipt validation passes. |
| Any other #89 `store_kind` | Item-target-only in v1 and never included in a Session target, even when another source field appears related. This exclusion is by construction, not a per-item diagnostic count. |
| Exact `(store_instance_id, store_kind, source_item_id)` ownership key | Required for catalog identity, but never sufficient by itself to prove Session ownership. |

`run_id`, `trace_id`, `span_id`, `evidence_id`, native IDs, `source_item_id`
alone, repository, workspace, path, capture time, expiry time, prompt-derived
text, row proximity, and query or similarity results are non-qualifying. A raw
record, analysis item, caller-owned input, backup artifact, or other item with
only one of those values is excluded even if it appears related. An exact
opaque item target remains independently operable when the item is not
Session-linked.

The only computable per-item Session exclusion is:

| Exclusion code | Meaning |
| --- | --- |
| `missing_ownership_proof` | A catalog `session_event_content` row in the exact requested-Session event join fails #89 ownership-key or immutable-owner-receipt validation. |

The candidate universe is exactly current `retention_items` rows with
`store_kind='session_event_content'` joined on
`source_item_id=session_events.event_id` and `session_events.session_id` equal
to the requested Session ID. Valid ownership selects the item; failed
ownership proof contributes one `missing_ownership_proof` count.

`no_exact_owned_items` is used when this joined candidate set is empty.
`all_candidates_excluded` is used only when the joined set is nonempty and
every row is excluded for `missing_ownership_proof`. Rows with no event join,
foreign Session joins, non-session-linked kinds, unknown/future kinds,
caller-owned inputs, external/backup artifacts, and other catalog instances
are fixed exclusions by construction. They create no per-item counts or extra
codes. No additional exclusion code is permitted.

The selected item set includes every exact-owned catalog item, including a
tombstone or denied item, so the preview can explain a rejection. More than
`100` items fails with HTTP `413` `retention_mutation_target_limit_exceeded`,
produces no preview record, and never truncates or partially applies.

### Opaque item target

An `item` target resolves exactly one catalog row by byte-for-byte opaque
`item_id` equality in the current catalog instance. It need not belong to a
Session. The resolver does not decode the ID, accept a database primary key,
inspect a source path, expand to siblings, or infer a Session. A missing exact
row is `retention_target_not_found`. A row from a non-product-owned or
non-v1 store cannot be manufactured by supplying an opaque-looking string.

## Mutation state and transition contract

`retained_by_policy` is the pinned/readable state. It preserves the original
`captured_at`, `policy_id`, `policy_version`, and original policy-derived
expiry as historical metadata while automatic expiry is disabled. It does not
create a new policy or TTL. `expiring` is the unpinned/readable state.

A Session command is atomic across all selected items: every item must be
allowed or an explicit idempotent case for that operation. One rejected item
prevents partial application to the other items. The effect columns below are
ordered as token consumed / audit / idempotency / state change.

### PREVIEW-STAGE rejections

The preview returns HTTP `200` with `mutation_allowed = false` and the fixed
`rejection_code`. A confirmation issue against that preview returns HTTP `409`
with the same code and no token. These rows cannot reach the mutation route.

| Operation | Preview current state | Fixed `rejection_code` | Preview / confirmation issue | Token / audit / idempotency / state |
| --- | --- | --- | --- | --- |
| `pin` | `expired_pending_deletion`, `deletion_queued`, or `deletion_failed` | `retention_pin_read_denied` | `200` / `409` | No / No / Yes (fixed rejection) / No |
| `pin` | `deleting` | `retention_pin_deleting` | `200` / `409` | No / No / Yes (fixed rejection) / No |
| `pin` | `deleted` | `retention_pin_deleted` | `200` / `409` | No / No / Yes (fixed rejection) / No |
| `unpin` | `expired_pending_deletion`, `deletion_queued`, or `deletion_failed` | `retention_unpin_read_denied` | `200` / `409` | No / No / Yes (fixed rejection) / No |
| `unpin` | `deleting` | `retention_unpin_deleting` | `200` / `409` | No / No / Yes (fixed rejection) / No |
| `unpin` | `deleted` | `retention_unpin_deleted` | `200` / `409` | No / No / Yes (fixed rejection) / No |
| `delete_now` | `deleting` | `retention_delete_already_deleting` | `200` / `409` | No / No / Yes (fixed rejection) / No |
| `delete_now` | `deleted` | `retention_delete_already_deleted` | `200` / `409` | No / No / Yes (fixed rejection) / No |
| `delete_now` | `deletion_failed` | `retention_delete_failed` | `200` / `409` | No / No / Yes (fixed rejection) / No |

### COMMIT-STAGE outcomes

These rows require a valid bound token issued from a preview with
`rejection_code = null`. At mutation time, binding and drift checks run in the
single order defined below; a failing check never falls through to a later
transition code.

| Operation | Commit current state | Result and allowed transition | New state / derived pin state | Completion / token / audit / idempotency / state |
| --- | --- | --- | --- | --- |
| `pin` | `expiring` | Exact target, revision, and readable catalog proof match | `retained_by_policy`; `pinned`; expiry stops; revision increments | `retention_pin_applied` / Yes / Yes / Yes / Yes |
| `pin` | `retained_by_policy` | Idempotent no-op; original policy/capture values remain unchanged | `retained_by_policy`; `pinned`; revision unchanged | `retention_pin_noop` / Yes / Yes / Yes / No |
| `pin` | `expiring` but `expires_at <= now` | Clock-only race; pure `retention_pin_expired` rejection, leaving the item for the #89 scan | Unchanged; `unpinned` | Rejection / No / No / Yes (fixed rejection) / No |
| `unpin` | `retained_by_policy`, original policy expiry in future | Recalculate `expires_at = original captured_at + recorded policy/version TTL`; never use unpin time | `expiring`; `unpinned`; revision increments | `retention_unpin_applied` / Yes / Yes / Yes / Yes |
| `unpin` | `retained_by_policy`, recalculated expiry `<= now` | Sequentially execute `retained_by_policy -> expiring -> expired_pending_deletion -> deletion_queued`; no readable interval | `deletion_queued`; `unpinned` | `retention_unpin_expired_queued` / Yes / Yes / Yes / Yes |
| `unpin` | `expiring`, `expires_at > now` | Idempotent no-op; no TTL reset | `expiring`; `unpinned`; revision unchanged | `retention_unpin_noop` / Yes / Yes / Yes / No |
| `unpin` | `expiring`, `expires_at <= now` | Sequentially execute `expiring -> expired_pending_deletion -> deletion_queued`; no readable interval | `deletion_queued`; `unpinned` | `retention_unpin_expired_queued` / Yes / Yes / Yes / Yes |
| `delete_now` | `expiring` | Sequentially execute `expiring -> expired_pending_deletion -> deletion_queued`, with irreversible read denial | `deletion_queued`; `unpinned` | `retention_delete_queued` / Yes / Yes / Yes / Yes |
| `delete_now` | `retained_by_policy` | Clear the derived pin through the seam, then execute `retained_by_policy -> expiring -> expired_pending_deletion -> deletion_queued` | `deletion_queued`; `unpinned` | `retention_delete_now_superseded_pin` / Yes / Yes / Yes / Yes |
| `delete_now` | `expired_pending_deletion` | Execute `expired_pending_deletion -> deletion_queued` | `deletion_queued`; `unpinned` | `retention_delete_queued` / Yes / Yes / Yes / Yes |
| `delete_now` | `deletion_queued` | Actionable-idempotent result; no second state transition or queue entity | Unchanged; `unpinned` | `retention_delete_already_queued` / Yes / Yes / Yes / No |

Every executed transition increments the item revision exactly once, and
`result_version` uses the final revisions. These sequential updates occur
inside the one `BEGIN IMMEDIATE` transaction. #90 never jumps directly to
`deleted`, cancels `deleting`, resurrects an item, or operates on `not_captured`
or `mixed`.

## Preview contract

`RetentionMutationPreviewRequest` has exactly:

| Field | Type and rule |
| --- | --- |
| `target` | `RetentionMutationTarget` |
| `operation` | `pin`, `unpin`, or `delete_now` |
| `scope` | `session_items` or `single_item`, matching target kind exactly |
| `reason_code` | One of the seven audit reason codes |
| `comment` | Null or the exact `1–256` scalar, NFC-normalized safe comment |

The `Idempotency-Key` header is required in the exact `rid1_` format. The
server records a digest and never echoes the comment in the preview response.

`RetentionMutationPreviewResponse` has exactly these top-level fields. A
nullable field is present as JSON `null`, never omitted:

| Field | Type and exact meaning |
| --- | --- |
| `schema_version` | Integer `1` |
| `result` | `actionable` or `empty_not_applicable` |
| `empty_reason` | Null, `no_exact_owned_items`, or `all_candidates_excluded` |
| `mutation_allowed` | Boolean; `false` for an empty result or incompatible precondition |
| `preview_id` | Opaque `rpv1_...` ID |
| `target_kind` | `session` or `item` |
| `target_id` | Exact Session ID or exact opaque item ID; no database key/path |
| `operation` | `pin`, `unpin`, or `delete_now` |
| `scope` | `session_items` or `single_item` |
| `source_state` | `available`, `not_captured`, `redacted`, `unsupported`, or `unknown`; null for a non-Session item when no source state exists |
| `session_completeness` | `unbound`, `partial`, `rich`, `full`, or null for an item target without an exact Session |
| `content_state` | Existing Session content state (`available`, `not_captured`, `redacted`, `unsupported`, or `expired_pending_deletion`) or null when not applicable |
| `current_state` | `RetentionCurrentStateSummary` |
| `target_items` | Array of exact ordered `RetentionPreviewItem`, maximum `100` |
| `target_item_count` | Nonnegative integer equal to `target_items.length` |
| `store_kind_summary` | Array of `RetentionStoreKindSummary`, maximum `5` |
| `excluded_item_count` | Nonnegative integer |
| `excluded_items_by_reason` | Array of `RetentionExclusionSummary`; maximum `1`, registry order |
| `capture_expiry_policy_summary` | Array of `RetentionCaptureExpiryPolicySummary`, maximum `5` |
| `retained_metadata_impact` | `RetentionRetainedImpact` |
| `active_cleanup_exclusion_conflicts` | Array of `RetentionActiveConflictSummary`, maximum `4`, registry order |
| `backup_non_purge_warning_code` | Exactly `retention_backup_not_purged` for every actionable preview; null only for empty/not-applicable |
| `expected_state_version` | `v1-` plus a SHA-256 version-vector digest for every exact target item |
| `target_item_set_digest` | SHA-256 digest of the exact `(item_id, store_kind)` set |
| `preview_digest` | SHA-256 digest of the canonical sanitized preview input |
| `confirmation_expires_at` | Preview creation time plus exactly `5 minutes` for an actionable preview; null for empty/not-applicable |
| `rejection_code` | Null when confirmation may be issued; otherwise one fixed transition/precondition code |

`RetentionPreviewItem` has exactly:

`item_id`, `store_kind`, `state`, `pin_state`, `delete_state`, `captured_at`,
`expires_at`, `policy_id`, `policy_version`, `read_denied_at`, `queued_at`,
`revision`, `retry_exhausted`, and `error_code`.

`pin_state` is derived as `pinned` iff `state=retained_by_policy`, `unpinned`
for every other represented catalog item, or `not_applicable` only when no
catalog item is represented. `delete_state` is `not_requested`, `queued`,
`in_progress`, `deleted`, or `failed`. `error_code` is null or an inherited #89
fixed code. No source item ID, path, locator, database key, raw value, or
exception is in this DTO.

`RetentionCurrentStateSummary` has exactly
`readable_item_count`, `read_denied_item_count`, `pinned_item_count`,
`unpinned_item_count`, and `lifecycle_counts`. `lifecycle_counts` has exactly
one nonnegative integer property for each of the seven lifecycle values,
including zero values.

`RetentionStoreKindSummary` has exactly `store_kind`, `item_count`,
`readable_count`, and `read_denied_count`. `RetentionExclusionSummary` has
exactly `reason_code` and `item_count`.

`RetentionCaptureExpiryPolicySummary` has exactly `policy_id`, `policy_version`,
`item_count`, `captured_at_min`, `captured_at_max`,
`original_expires_at_min`, and `original_expires_at_max`. The expiry fields are
the original policy-derived values, not a new unpin TTL.

`RetentionRetainedImpact` has exactly `raw_content_will_be_deleted`,
`session_metadata_retained`, `event_metadata_retained_count`,
`safe_summary_retained_count`, and `evidence_reference_retained_count`.
`raw_content_will_be_deleted` is true for a confirmed `delete_now` or an
unpin whose original expiry is already reached, and false for a future
readable pin/unpin preview. Counts are nonnegative integers and contain no
content.

`RetentionActiveConflictSummary` has exactly `conflict_code`, `item_count`,
and `conflict_version`. `conflict_version` is a `v1-` SHA-256 digest of the
canonical active-conflict snapshot.

An existing Session with no exact-owned item returns HTTP `200` with exactly
`result = empty_not_applicable`, `empty_reason = no_exact_owned_items`,
`target_item_count = 0`, `target_items = []`, `mutation_allowed = false`, and
`confirmation_expires_at = null`. When canonical candidate linkages exist but
all fail ownership proof, the same shape uses
`empty_reason = all_candidates_excluded`; exclusion counts remain exact.
No token can be issued. A later confirmation attempt returns HTTP `409`
`retention_target_empty`, never a no-op mutation success. An unknown Session
is `404` `retention_target_not_found`, not an empty Session. An exact item in a
denied, deleting, deleted, or otherwise incompatible state is an actionable
preview with `mutation_allowed = false` and its fixed precondition code.

An already-`deletion_queued` `delete_now` preview is actionable-idempotent:
`mutation_allowed = true`, `rejection_code = null`, confirmation is allowed,
and the committed result is `retention_delete_already_queued` without a second
state transition.

## Digest and canonicalization

All #90 digests use SHA-256 over UTF-8 JSON Canonicalization Scheme (JCS,
RFC 8785) output. JCS lexicographically orders object properties, emits no
whitespace, uses its specified JSON number representation, and encodes strings
as UTF-8. There is no delimiter concatenation, locale formatting, trimming,
case folding, or path normalization.

`target_item_set_digest` hashes a JCS array of exactly
`[{"item_id":<opaque>,"store_kind":<closed-kind>}, ...]`, sorted by `item_id`
ordinal and then `store_kind` ordinal. It is empty for an empty/not-applicable
preview.

`expected_state_version` hashes a JCS array of exactly
`[{"item_id":<opaque>,"revision":<nonnegative integer>,"pin_state":<enum>,"state":<lifecycle>}, ...]`
with the same order. The active conflict snapshot hashes a JCS array of
exactly
`[{"item_id":<opaque>,"conflict_code":<closed-code>,"lease_generation":<nonnegative integer>}, ...]`
in item/order registry order. Lease generation is a catalog version, not a
public source key.

`preview_digest` hashes a JCS object containing the preview response fields,
including `rejection_code`, plus `expected_state_version` and
`target_item_set_digest`. The only excluded inputs are `preview_id`,
`preview_digest`, `confirmation_expires_at`, and comments. This binds the
complete sanitized impact, including state-bearing rejection, while the
server binds the preview record and fixed five-minute expiry separately.

## Confirmation issue and token

The preview response never contains a confirmation token. The UI must first
re-display the complete sanitized preview and then call the separate
confirmation-issue route. A successful fresh or reissued
`RetentionConfirmationIssueResponse` has exactly `schema_version`,
`confirmation_id`, `confirmation_token`, and `confirmation_expires_at`.
It is the only API response containing a token. A consumed-preview retry
returns stored mutation linkage/result under `retention_confirmation_consumed`
and is not a token response.

The token wire format is exactly:

```text
rt90v1_<base64url-no-padding-16-byte-nonce>_<base64url-no-padding-32-byte-secret>
```

The server generates nonce and secret with its cryptographically secure random
source. A collision with an existing active nonce in the same catalog returns
`retention_confirmation_generation_failed` and issues no token. Persistence
stores only SHA-256 over the exact full ASCII token string bytes, including the
prefix and separators. The server never logs or persists the plaintext token
or secret.

The token binds exactly:

1. `schema_version = 1`;
2. `target_kind` and exact `target_id`;
3. `operation` and `scope`;
4. `preview_digest`;
5. `expected_state_version`;
6. `target_item_set_digest`;
7. `active_conflict_snapshot` and its `conflict_version`;
8. `confirmation_expires_at`;
9. the server-generated nonce;
10. the exact workflow `Idempotency-Key` value shared by all three POST steps;
11. the selected `reason_code` and SHA-256 digest of the normalized comment.

The token is single-consumption, and at most one unconsumed token binding
exists for a preview. Issue/reissue atomically enforces that singleton. The
catalog marks a token consumed in the same durable transaction as the
mutation, mutation-step idempotency result, audit event, and any #89
`deletion_queued` transition. Rollback leaves the token unconsumed.

A same-key, byte-identical `confirmation_issue` retry is the one replay
exception. It atomically invalidates a prior unconsumed binding, generates a
fresh nonce and secret, stores the new full-token hash and `confirmation_id`,
and returns a fresh token. It reuses the original preview expiry and never
extends the five-minute window. If the prior token was consumed, it returns
the stored mutation linkage under `retention_confirmation_consumed` and never
issues a token or confirmation ID. An invalidated token is
`retention_confirmation_invalid`; only the fresh response token can commit.

## Idempotency contract

Every preview, confirmation issue, and mutation request carries the same exact
client-generated `Idempotency-Key` for one workflow. It is 48-character ASCII
in the `rid1_` format, contains no user text, is scoped to one physical
retention catalog database, and is not shared across databases, Sessions,
processes, or users.

Durable rows are keyed by
`(SHA-256(exact ASCII Idempotency-Key bytes), step)`, where `step` is exactly
`preview`, `confirmation_issue`, or `mutation`. Each row contains the request
fingerprint, step-specific result metadata, created time, expiry time, and
completion code. Result metadata never contains plaintext token material. The
365-day lifetime starts with the first durable step record; expiry leaves a
non-reusable tombstone and returns `retention_idempotency_expired` for every
step.

The canonical request bytes are JCS UTF-8 bytes of the validated request body
after NFC comment normalization:

- `preview`: `target`, `operation`, `scope`, `reason_code`, and `comment`;
- `confirmation_issue`: `preview_id` and `preview_digest`;
- `mutation`: `confirmation_token`, `operation`, `scope`, `target_kind`, and
  `target_id`.

The same key, step, and byte-identical request returns the exact stored result
without another state transition or audit event, except for the
confirmation-issue reissue rule above. The same key and step with any
different canonical request returns `retention_idempotency_conflict`.
Different steps sharing one workflow key never conflict. A fresh workflow
after stale, expired, or conflict rejection uses a new key.

The token binding field `workflow_idempotency_key` is the exact full ASCII
header value, even when lookup uses its digest. The mutation `operation_id` is
the #90-owned correlation for the committed result and any #89 queue
transition; it is not a #89 queue request identifier.

## Append-only audit contract

Only a confirmed, durably committed command creates an audit event. This
includes a confirmed pin/unpin no-op and an already-queued delete-now result.
Preview reads, token issuance, invalid requests, stale-token rejects, and
unconfirmed precondition failures create no audit event. Audit and idempotency
result commit with the state result.

`RetentionAuditEvent` has exactly:

| Field | Type and rule |
| --- | --- |
| `event_id` | Opaque `rae1_` plus `22` unpadded base64url characters; server generated |
| `operation_id` | #90-owned opaque mutation correlation and, when items are queued, the queue-transition correlation reference |
| `event_type` | Exactly `retention_mutation` |
| `target_kind` | `session` or `item` |
| `target_id` | Exact Session ID or exact opaque item ID |
| `session_id` | Exact Session ID for a Session target; JSON null for an item target without an exact Session |
| `occurred_at` | Server UTC RFC 3339 time at durable commit |
| `actor_label` | Exactly `local-user` |
| `operation` | `pin`, `unpin`, or `delete_now` |
| `reason_code` | One of the closed reason registry |
| `comment` | Null or normalized safe comment; never raw evidence |
| `previous_pin_state` | `pinned`, `unpinned`, `not_applicable`, or `mixed` |
| `new_pin_state` | Same closed enum |
| `previous_operation_state` | Seven-state count object with exactly one nonnegative count per lifecycle state |
| `new_operation_state` | Same exact seven-key count object |
| `request_idempotency_key` | Exact `rid1_...` value; stored/returned only in audit/history read models and never logged or emitted by other DTOs |
| `expected_version` | `v1-` plus expected target-set version-vector digest |
| `result_version` | `v1-` plus resulting target-set version-vector digest |
| `target_item_set_digest` | `sha256-` digest of exact item/store-kind set |
| `completion_code` | One code from the closed completion registry |
| `error_code` | JSON null for committed successful result; otherwise one fixed mutation code |

The previous and new seven-state objects make a Session event exact without
one audit row per item. For an item target, one key is `1` and the other six
are `0`; each object totals the exact target item count.

The reason registry is exactly:

`research_needed`, `review_complete`, `privacy_request`, `data_minimization`,
`test_cleanup`, `operator_correction`, and `other_local_reason`.

The completion registry is exactly:

`retention_pin_applied`, `retention_pin_noop`,
`retention_unpin_applied`, `retention_unpin_noop`,
`retention_unpin_expired_queued`, `retention_delete_queued`,
`retention_delete_already_queued`, and
`retention_delete_now_superseded_pin`.

`retention_mutation_replayed` is result-only for a same-workflow-key,
same-step response. It is never a second audit event or an audit completion
code. The audit store is append-only: events are never updated, overwritten,
or deleted by retry, restart, cleanup, or a later command.

## Durable atomic boundary

For a confirmed target set, one durable SQLite transaction commits all of the
following or none of them:

1. expected-revision checks for every exact target item;
2. pin state and lifecycle mutation, including the
   `retained_by_policy -> expiring` seam and only the #89-allowed forward steps;
3. original-capture/policy-based expiry recalculation;
4. irreversible `read_denied_at` and forward item-state transitions to
   `deletion_queued` for an expired-at-unpin or delete-now result;
5. `operation_id` and exact state-transition counts correlated to that
   operation;
6. confirmation-token consumption;
7. per-step idempotency row and exact result DTO;
8. append-only audit event.

The transaction never creates a new lifecycle edge. SQLite source deletion is
outside this transaction and is performed only later by the existing #89
adapter/worker. If commit cannot complete, return `503`
`retention_mutation_transaction_failed` or `retention_audit_write_failed`,
with no operation ID and no body presenting success. Rollback leaves the token
and workflow key eligible for the exact retry rule. A successful commit with a
lost response is recovered by the same workflow key and mutation step. Worker
or adapter failure cannot make content readable again.

## Versioned routes and DTOs

The complete surface is:

| Method and route | Request | Response |
| --- | --- | --- |
| `GET /api/retention/v1/status` | Existing route; no `limit` query parameter; fixed `100`-item window | Existing #89 `RetentionStatusResponse`, byte-identical |
| `GET /api/retention/v1/sessions/{sessionId}` | Exact local Session ID | Existing #89 `RetentionSessionStatusResponse`, unchanged |
| `GET /api/retention/v1/items/{itemId}` | Exact opaque item ID | `RetentionItemStateResponse` |
| `GET /api/retention/v1/sessions/{sessionId}/history?limit=1..100&cursor=<opaque>` | Optional limit defaults to `100`; cursor exclusive | `RetentionHistoryResponse` |
| `GET /api/retention/v1/items/{itemId}/history?limit=1..100&cursor=<opaque>` | Optional limit defaults to `100`; cursor exclusive | `RetentionHistoryResponse` |
| `POST /api/retention/v1/previews` | `RetentionMutationPreviewRequest` plus `Idempotency-Key` | `RetentionMutationPreviewResponse` |
| `GET /api/retention/v1/previews/{previewId}` | Exact opaque preview ID | Same preview response, never a token |
| `POST /api/retention/v1/confirmations` | `RetentionConfirmationIssueRequest` plus `Idempotency-Key` | `RetentionConfirmationIssueResponse` |
| `POST /api/retention/v1/mutations` | `RetentionMutationConfirmRequest` plus matching `Idempotency-Key` | `RetentionMutationResult` |
| `GET /api/retention/v1/mutations/{operationId}` | Exact opaque operation ID | `RetentionMutationStatusResponse` |

`RetentionConfirmationIssueRequest` has exactly `preview_id` and
`preview_digest`. `RetentionMutationConfirmRequest` has exactly
`confirmation_token`, `operation`, `scope`, `target_kind`, and `target_id`.
The latter four are explicit echoes of the intended command and are checked
against the token binding; they are not client-authoritative selectors.

`RetentionItemStateResponse` has exactly:

`schema_version`, `item_id`, `store_kind`, `state`, `pin_state`,
`delete_state`, `policy_id`, `policy_version`, `captured_at`, `expires_at`,
`read_denied_at`, `queued_at`, `deletion_started_at`, `deleted_at`,
`attempt_count`, `retry_exhausted`, `error_code`, `retry_at`, `revision`, and
`session_id` (nullable).

`session_id` is derived only from the exact
`session_event_content -> session_events` join; it is null for every other
store kind and is not a `retention_items` column. The response uses only #89
allowlisted fields plus #90 pin/delete state and revision.

`RetentionHistoryResponse` has exactly `schema_version`, `target_kind`,
`target_id`, `events`, and `next_cursor`. `events` contains the exact
`RetentionAuditEvent` schema above. `next_cursor` is null on the last page or
an opaque `rhc1_` cursor otherwise. History uses
`occurred_at DESC, event_id DESC`, and a cursor resumes strictly after the
last pair. No cursor contains a source key, path, comment, token, or raw value.

`RetentionMutationResult` has exactly:

`schema_version`, `operation_id`, `result_code`, `target_kind`, `target_id`,
`operation`, `scope`, `target_item_count`, `pin_state`, `lifecycle_counts`,
`read_denied`, `audit_event_id`, `expected_version`, `result_version`,
`backup_non_purge_warning_code`, `idempotent_replay`, `created_at`, and
`completed_at`.

`operation_id` is the #90 correlation for any `deletion_queued` transition;
physical deletion status is the #89 item state, not a claim that the worker
has completed.

`RetentionMutationStatusResponse` has exactly:

`schema_version`, `operation_id`, `operation`, `target_kind`, `target_id`,
`status`, `result_code`, `lifecycle_counts`, `read_denied`, `audit_event_id`,
`idempotent_replay`, `created_at`, `completed_at`, and
`backup_non_purge_warning_code`.

`status` is exactly `committed` or `replayed`. A transaction failure leaves no
operation record. Later #89 worker failure is read from #89 item status. No
queue request ID is exposed or persisted by #90.

## HTTP stages, mapping, and diagnostics

The stage classification is normative and mutually exclusive:

- `REQUEST-STAGE`: the request fails before it can produce a new preview
  record or stage result.
- `PREVIEW-STAGE rejection`: preview succeeds with HTTP `200`,
  `mutation_allowed = false`, and `rejection_code`; confirmation issue
  returns HTTP `409` with the same code and no token.
- `CONFIRMATION-ISSUE-STAGE`: an addressable persisted preview cannot produce
  a fresh confirmation. A consumed-binding result belongs to this class
  because it returns stored linkage and no new token; the mutation route may
  return the same code at mutation-time check 2.
- `COMMIT-STAGE`: a token is presented to the mutation route and enters the
  ordered evaluation or durable transaction. Outcomes are `401`, `409`, or
  `503`, or `200` for committed success, no-op, actionable-idempotent result,
  or replay.
- `WARNING`: a fixed warning field attached to an otherwise successful result.

| HTTP status | Conditions |
| --- | --- |
| `200` | Reads; every preview including PREVIEW-STAGE rejection; fresh/reissued confirmation issue; committed success, no-op, actionable-idempotent `retention_delete_already_queued`, or idempotent mutation replay. A committed delete-now means read denial and durable queue handoff, not physical completion. |
| `400` | REQUEST-STAGE `retention_mutation_request_invalid`, `retention_idempotency_key_invalid`, or `retention_history_cursor_invalid` |
| `401` | COMMIT-STAGE `retention_confirmation_invalid` at mutation-time check 1 |
| `404` | REQUEST-STAGE `retention_target_not_found`, `retention_preview_not_found`, or `retention_operation_not_found` |
| `409` | REQUEST-STAGE `retention_idempotency_conflict` or `retention_idempotency_expired`; PREVIEW-STAGE confirmation issue for `retention_target_not_applicable` and fixed transition codes; CONFIRMATION-ISSUE-STAGE `retention_target_empty`, `retention_preview_expired`, or `retention_preview_digest_mismatch`; mutation-time checks 2–9; or post-check `retention_pin_expired`. `retention_delete_already_queued` is the committed `200` exception. |
| `413` | REQUEST-STAGE `retention_mutation_target_limit_exceeded`; no preview record |
| `503` | REQUEST-STAGE inherited `retention_catalog_unavailable`; CONFIRMATION-ISSUE-STAGE `retention_confirmation_generation_failed`; or COMMIT-STAGE `retention_mutation_transaction_failed` / `retention_audit_write_failed` |

All error bodies are exactly `{"error":"<code>"}` with no extra text. `401`
does not identify which token component failed. `409` does not echo current or
submitted values. All routes are loopback/Host-header restricted,
same-origin, JSON-only where a body exists, CSRF-protected for state-changing
requests, and `no-store`.

### Mutation-time evaluation order

The first failing check returns exactly one code. The order is:

| Order | Condition | Fixed code | Republish rule |
| --- | --- | --- | --- |
| `1` | Structural or cryptographic token validity, including format, digest, nonce, stored token hash, or a token invalidated by reissue | `retention_confirmation_invalid` | Discard token and create a new preview/key |
| `2` | Token was committed by another request | `retention_confirmation_consumed` | Return stored mutation linkage/result when available; never issue a token; new preview/key for a new operation |
| `3` | Confirmation token is past the exact five-minute expiry | `retention_confirmation_expired` | Create a new preview/key; issuance never extends expiry |
| `4` | Submitted operation, scope, target kind, or target ID differs from binding | `retention_confirmation_binding_mismatch` | Create a new preview/key |
| `5` | Exact target item set digest changed | `retention_confirmation_target_changed` | Create a new preview/key |
| `6` | Pin-state vector changed | `retention_confirmation_pin_changed` | Create a new preview/key |
| `7` | Retention policy, captured-at basis, or expiry changed | `retention_confirmation_retention_changed` | Create a new preview/key |
| `8` | Active cleanup conflict snapshot was added, removed, or changed | `retention_confirmation_conflict_changed` | Create a new preview/key |
| `9` | Full expected catalog/revision vector changed after the preceding checks | `retention_confirmation_version_changed` | Create a new preview/key |

Before mutation-time evaluation, a missing preview or stored preview is
`retention_preview_not_found`, and an expired preview read or confirmation
issue is `retention_preview_expired`. Confirmation issue validates the supplied
digest and returns `retention_preview_digest_mismatch` only on that route. A
stored non-null preview `rejection_code` returns HTTP `409` with that same
code and no token. After successful binding, only the clock-only
`retention_pin_expired` race can reject a transition.

The complete diagnostic registry is:

| Code | Reachability class | Use |
| --- | --- | --- |
| `retention_mutation_request_invalid` | REQUEST-STAGE (preview/confirmation/mutation request) `400` | Invalid union, operation, scope, reason, comment, or body; no preview record |
| `retention_target_not_found` | REQUEST-STAGE (preview request, item read, history read) `404` | Exact Session or item target absent |
| `retention_mutation_target_limit_exceeded` | REQUEST-STAGE (preview request) `413` | Exact Session target has more than `100` items; no preview record |
| `retention_preview_not_found` | REQUEST-STAGE (preview read/confirmation issue) `404` | Preview ID absent from current catalog |
| `retention_idempotency_key_invalid` | REQUEST-STAGE (preview/confirmation/mutation request) `400` | Key is not exact 48-character `rid1_` format |
| `retention_idempotency_conflict` | REQUEST-STAGE (preview/confirmation/mutation request) `409` | Same key and step with a different canonical request |
| `retention_idempotency_expired` | REQUEST-STAGE (preview/confirmation/mutation request) `409` | 365-day key lifetime ended; reuse forbidden |
| `retention_operation_not_found` | REQUEST-STAGE (mutation status read) `404` | Operation ID absent |
| `retention_history_cursor_invalid` | REQUEST-STAGE (history read) `400` | Cursor malformed or invalid for requested target |
| `retention_catalog_unavailable` | REQUEST-STAGE (any retention route) `503` | Inherited #89 catalog availability failure; no #90 record is produced or addressed |
| `retention_target_not_applicable` | PREVIEW-STAGE rejection (`200` preview / `409` issue) | Exact row exists but kind is outside the closed five-kind registry or ownership identifies a non-product-owned imported reference |
| `retention_pin_read_denied` | PREVIEW-STAGE rejection (`200` / `409`) | Pin attempted on already denied lifecycle |
| `retention_pin_deleting` | PREVIEW-STAGE rejection (`200` / `409`) | Pin attempted while #89 is deleting |
| `retention_pin_deleted` | PREVIEW-STAGE rejection (`200` / `409`) | Pin attempted on deleted tombstone |
| `retention_unpin_read_denied` | PREVIEW-STAGE rejection (`200` / `409`) | Unpin attempted on already denied lifecycle |
| `retention_unpin_deleting` | PREVIEW-STAGE rejection (`200` / `409`) | Unpin attempted while #89 is deleting |
| `retention_unpin_deleted` | PREVIEW-STAGE rejection (`200` / `409`) | Unpin attempted on deleted tombstone |
| `retention_delete_already_deleting` | PREVIEW-STAGE rejection (`200` / `409`) | Delete-now already in #89 deletion |
| `retention_delete_already_deleted` | PREVIEW-STAGE rejection (`200` / `409`) | Delete-now targets terminal deleted tombstone |
| `retention_delete_failed` | PREVIEW-STAGE rejection (`200` / `409`) | Delete-now targets #89 `deletion_failed`; #89 retry rules remain authoritative |
| `retention_target_empty` | CONFIRMATION-ISSUE-STAGE `409` | Preview was the exact empty/not-applicable shape; no token can be issued |
| `retention_preview_expired` | CONFIRMATION-ISSUE-STAGE `409` | Preview read or issue is past five-minute expiry |
| `retention_preview_digest_mismatch` | CONFIRMATION-ISSUE-STAGE `409` | Supplied preview digest differs; confirmation issue only |
| `retention_confirmation_generation_failed` | CONFIRMATION-ISSUE-STAGE `503` | Server nonce collision prevented safe issuance |
| `retention_confirmation_consumed` | CONFIRMATION-ISSUE-STAGE `409` (also mutation-time check 2) | Token already committed; return stored linkage/result and never issue a new token |
| `retention_confirmation_invalid` | COMMIT-STAGE `401` (mutation-time check 1) | Token format, full-token hash, nonce, or cryptographic validation invalid, including reissued token |
| `retention_confirmation_expired` | COMMIT-STAGE `409` (mutation-time check 3) | Token past fixed expiry |
| `retention_confirmation_binding_mismatch` | COMMIT-STAGE `409` (mutation-time check 4) | Token binding differs from confirmation request |
| `retention_confirmation_target_changed` | COMMIT-STAGE `409` (mutation-time check 5) | Exact target item set digest changed |
| `retention_confirmation_pin_changed` | COMMIT-STAGE `409` (mutation-time check 6) | Pin-state vector changed |
| `retention_confirmation_retention_changed` | COMMIT-STAGE `409` (mutation-time check 7) | Policy, captured-at basis, or expiry changed |
| `retention_confirmation_conflict_changed` | COMMIT-STAGE `409` (mutation-time check 8) | Active cleanup conflict snapshot changed |
| `retention_confirmation_version_changed` | COMMIT-STAGE `409` (mutation-time check 9) | Full expected catalog/revision vector changed |
| `retention_pin_expired` | COMMIT-STAGE `409` | Clock-only pin race at commit |
| `retention_mutation_transaction_failed` | COMMIT-STAGE `503` | Durable mutation transaction rolled back or could not commit |
| `retention_audit_write_failed` | COMMIT-STAGE `503` | Required append-only audit write prevented commit |
| `retention_delete_already_queued` | COMMIT-STAGE `200` | Delete-now was actionable-idempotent and already durably queued |
| `retention_backup_not_purged` | WARNING `200` | Fixed warning on every actionable preview/result; no backup purge is promised |

`retention_backup_not_purged` is a warning field, not a failure status.
Inherited #89 codes remain available only where their existing catalog/worker
contracts already use them. The #90 registry does not reuse
`retention_lease_conflict`, `retention_lease_lost`,
`retention_unexpected_source_missing`, or `retention_item_limit_exceeded`.

## Pagination, ordering, and compatibility

`GET /api/retention/v1/status` has no `limit` query parameter, always uses the
fixed `100`-item window, remains byte-identical to the existing #89
`RetentionStatusResponse`, and orders items by `expires_at ASC, item_id ASC`.
It has no new cursor or field. Session/item current state reads use exact IDs.

History pages accept `limit=1..100`, default to `100`, and use an exclusive,
opaque cursor. The order is `occurred_at DESC, event_id DESC` (newest first),
and `next_cursor` is null only on the final page. A history cursor is bound to
its requested target and contains no source key, path, comment, token, or raw
value.

Issue #90 does not add pin, deletion, audit, preview, revision, or queue fields
to `/api/session-workspace/*` v1. The frozen projection is:

| Canonical #90 result | v1 event `content_state` | v1 Session `raw_retention_state` | Raw content route |
| --- | --- | --- | --- |
| Readable unpinned `expiring` | `available` | `expiring` when any readable sibling exists | Existing `200` |
| Readable pinned `retained_by_policy` | `available` | `expiring` when any readable sibling exists | Existing `200`; v1 does not reveal pin |
| `expired_pending_deletion`, `deletion_queued`, `deleting`, any `deletion_failed`, or `deleted` | `expired_pending_deletion` | `expired_pending_deletion` when no readable sibling exists | Existing exact `410` body |
| Confirmed delete-now before physical deletion | `expired_pending_deletion` | `expired_pending_deletion` when no readable sibling exists | Existing exact `410` body immediately after commit |
| No represented item | Existing `not_captured`, `redacted`, or `unsupported` | `not_captured` | Existing `404` |

The existing expired raw-content response is HTTP `410` with:

```json
{ "error": "raw_content_expired", "content_state": "expired_pending_deletion" }
```

A readable sibling retains #89 precedence: the Session aggregate projects as
`expiring`, while a selected denied event returns its own exact `410`. A
deleted item never becomes `404` merely because physical deletion completed.

## Local security and no-leak requirements

The surface is loopback/Host-header restricted, CORS-disabled, same-origin,
and CSRF-protected for state-changing requests. All listed responses are
`no-store`. Preview, audit, logs, history, cursors, diagnostics, screenshots,
evidence, fixtures, and repository-safe output never contain raw content,
credentials, paths, database keys, source-row IDs, prompt-derived labels,
PII, full exceptions, or token values. The only token-bearing surfaces are the
dedicated confirmation issue response and the immediately following mutation
request body. Persistence stores only the full-token SHA-256 hash. The fixed
actor label is `local-user`. The exact workflow key is the only explicit
idempotency-key-in-audit/history exception and is never logged or emitted by
other DTOs.
