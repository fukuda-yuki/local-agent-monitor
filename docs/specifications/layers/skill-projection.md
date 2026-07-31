# Skill Projection Specification

Status: **Accepted for Issue #154 (DC154-01)**

This specification is the single current-valid Skill claim authority. It owns
projection generations, exact input frontiers, queue/retry state, publication
fences and current reads. It does not own the historical Skill body/current
file HTTP contract or final Local Monitor UI.

## Claim arms and exact identity

The closed source-arm set is:

```text
otel_trace_span
sdk_session_event
```

`otel_trace_span` claims retain exact trace/span/raw-record identity and require
the current trace SourceCompatibility resolution and OTel generation revision.
It is the only arm governed by the trace generation/frontier/queue machinery
below.

`sdk_session_event` is an independent exact current-valid claim arm in the same
component and read authority. It is not trace-generation-bound and must remain
representable when producer trace ID and span ID are both absent. Each SDK claim
stores and validates all of:

- exact local Session ID;
- exact persisted local Session Event ID;
- exact producer source Event ID;
- exact source adapter and source surface;
- exact mandatory source application version;
- exact adapter version and normalization version;
- exact payload schema and schema fingerprint;
- exact payload digest; and
- any producer-supplied trace ID and span ID without synthesis.

The current compatibility registry evaluates this complete tuple:

```text
(
  source_application_version,
  adapter_version,
  normalization_version,
  payload_schema,
  schema_fingerprint
)
```

All five tuple fields are required and non-null;
`source_application_version` is mandatory. Tuple comparison is ordinal and
exact. There is no latest-version, application-family, missing-field, or
partial-tuple fallback. A retained event or raw snapshot does not make an
unsupported tuple current-valid.

#157/#158 still own the SDK transport, atomic Session/Event/snapshot writer and
the accepted registry seed. This specification pins the claim fields and
predicate but does not make that writer implementation-ready: no SDK claim may
be admitted until #158 fixes the remaining wire contract and accepted exact
registry tuple. Adding it must use this component and read authority, not a
second Skill projection or reader.

The single read authority merges one current OTel claim and one current SDK
claim only when the SDK event explicitly carries both producer trace ID and
producer span ID and those two identifiers exactly equal the OTel claim's trace
ID and span ID. Trace ID alone never merges. Name, path, time, Session/Run
proximity, ordinal and cardinality are never linkage. Without that exact pair,
the two rows remain separate positive observations; for the same exact Session,
the aggregate invocation count is `null` and state is
`certification_pending`, rather than the two observations being added. A raw
Skill snapshot cannot create or resurrect an invocation claim.

For the Copilot CLI OTel arm, the existing allowlist and positive-observation
semantics remain:

- `github.copilot.skill.name`;
- `github.copilot.skill.source`;
- `github.copilot.skill.invocation_trigger`;
- `github.copilot.tool.parameters.skill_name`; and
- `github.copilot.context.skills`.

`gen_ai.tool.name=skill` may identify the dedicated tool span but does not
supply the Skill identifier. Available-name inventory is not proof that an
unobserved Skill did not run. Exact native Session binding remains governed by
the Session identity contract; contextual similarity never binds it.

## Independent component and destructive transition

Skill projection is the independently versioned SQLite component:

```text
skill_projection:1
```

It owns:

- OTel generations and desired/current trace-generation pointers;
- ordered OTel generation input-frontier rows;
- durable OTel queue rows and leases;
- independent exact SDK Session/Event claim rows;
- idempotency receipts; and
- projected invocation/inventory claim rows.

Source observation/interpretation tables remain owned by the source
compatibility authority, including the current trace
`compatibility_revision`. A generation stores the exact revision it consumed
but does not create a second current-revision table. Retention items and leases
remain owned by Retention.

The v1 component owns these exact namespaces:

| Table | Required key/content |
| --- | --- |
| `skill_projection_generations` | OTel-only integer generation primary key; trace; compatibility revision; frontier digest; projector version; lifecycle; created/updated times; unique generation tuple |
| `skill_projection_generation_inputs` | OTel generation plus zero-based input ordinal primary key; exact raw-record owner identity bytes; exact input digest |
| `skill_projection_trace_heads` | OTel trace primary key; nullable unique desired and current generation pointers; updated time |
| `skill_projection_queue` | OTel generation primary key; repeated unique generation tuple; closed queue state; attempt/lease/retry/error fields |
| `skill_projection_operation_receipts` | opaque operation key primary key; semantic fingerprint; fixed outcome; nullable generation; created time |
| `skill_projection_invocations` | OTel generation plus exact non-null raw-record/span source identity; sanitized invocation fields and exact provenance |
| `skill_projection_inventories` | OTel generation plus exact non-null raw-record/trace source identity; bounded inventory facts |
| `skill_projection_inventory_names` | inventory identity plus zero-based name ordinal; sanitized name |
| `skill_projection_sdk_claims` | local UUIDv7 claim primary key; exact local Session/Event and producer source Event identities; exact source adapter/surface/application version, adapter/normalization versions, payload schema/fingerprint/digest; nullable producer trace/span; sanitized invocation fields; created time; no generation foreign key |

All foreign keys are `ON UPDATE RESTRICT ON DELETE RESTRICT`. Component-owned
receipt and published claim rows reject update/delete. SDK claim rows are
append-only and have no foreign key to an OTel generation or trace head.
Generations and queue rows change only through the closed transitions below.
Digests are 64 lowercase hexadecimal characters, projector/revision/error
tokens are closed or visible ASCII bounded to 128 bytes, and times are UTC with
seven fractional digits and `+00:00`.

The recognized pre-release transition is destructive only for obsolete
Skill-owned state:

1. drop the old `monitor_skill_invocations`,
   `monitor_skill_inventories`, `monitor_skill_inventory_names`, their indexes,
   and any old Skill-only stage marker;
2. retain the existing span-projection meaning of
   `monitor_ingestions.span_projected_at` but remove every Skill completion
   meaning from that column;
3. create an empty `skill_projection:1`; and
4. remove the old reader/writer.

No old Skill row is copied or backfilled, and no dual reader/writer or
compatibility shim is retained. Session, raw record, source observation,
monitor span, Retention and unrelated component data is not deleted. An
unknown intermediate Skill schema fails before mutation and requires a fresh
runtime database; it is not guessed or repaired.

## OTel compatibility revision and generations

Each trace has one monotonically increasing `compatibility_revision`, starting
at zero before any effective source-version interpretation exists. A semantic
change to effective resolution or exact version increments it exactly once.

A generation binds:

```text
(trace_id, compatibility_revision, input_frontier_sha256, projector_version)
```

and that tuple is unique. Its closed lifecycle is:

```text
pending
retry_pending
current
superseded
input_unavailable
failed_terminal
```

The independently persisted queue has the closed states required by the
Product Owner:

```text
pending
leased
completed
superseded
input_unavailable
failed_terminal
```

The queue row has the same unique
`(trace_id, compatibility_revision, input_frontier_sha256, projector_version)`
tuple and an exact foreign key to its generation.

`retry_pending` is a generation lifecycle value, not a queue state. A retryable
failure sets the generation to `retry_pending` and the queue row back to
`pending`. A successful publication sets the generation to `current` and the
queue to `completed`. A stale desired generation sets both to `superseded`.
Unavailable projection input sets both to `input_unavailable`. A deterministic
non-retryable invariant failure sets both to `failed_terminal`.

Only one desired generation and at most one current generation may exist for a
trace. Creating a new desired generation immediately clears the current
pointer and makes the preceding current generation `superseded`. This happens
inside the correction/ingestion transaction; a worker is not required for
invalidation.

## Exact OTel input frontier

The worker never re-queries “all raw records visible now.” Each generation
stores the exact ordered input identities and digests selected in the
transaction that created it.

The frontier hash uses domain `skill-projection-frontier\0v2\0`. After the
domain, every value is framed independently as its U32BE byte length followed
by its bytes. Integers use unsigned decimal ASCII with no sign or leading
zeroes; strings use their exact UTF-8 bytes.

The canonical framed value sequence is:

```text
trace_id
item_count
for each item ordered by (raw_record_id, source_observation_id):
  source_observation_id
  raw_record_id
  input_evidence_kind
  evidence_value
```

`item_count`, `source_observation_id`, and `raw_record_id` use the integer
encoding above. `input_evidence_kind` is the exact tagged-union token
`payload_sha256` or `deleted_before_digest_v10`. For `payload_sha256`,
`evidence_value` is the exact 64-character lowercase hexadecimal SHA-256 of the
stored strict UTF-8 `payload_json` bytes. For `deleted_before_digest_v10`,
`evidence_value` is the predecessor-schema marker token `10`. Duplicate
`(raw_record_id, source_observation_id)` identities are rejected; discovery
order and timestamps are irrelevant.

This v2 framing supersedes the unintegrated v1 frontier framing. There is no
v1 dual read, fallback, or compatibility path.

The generation stores the ordered rows as well as the hash. Publication
recomputes the hash from those rows and rejects a mismatch. Raw expiry never
causes the worker to substitute a smaller frontier.

SDK claims do not enter an OTel trace frontier. Their exact payload digest and
complete identity/compatibility fields are persisted directly in
`skill_projection_sdk_claims` by the future #158 atomic writer.

## Exact claim idempotency

No uniqueness rule depends on nullable `span_id`. OTel projected rows belong to
one generation and have a non-null exact source identity:

- OTel invocation:
  `(generation_id, otel_trace_span, raw_record_id, span_ordinal)`;
- OTel inventory:
  `(generation_id, otel_trace_span, raw_record_id, trace_id)`.

The relevant OTel tuple is a declared unique key. Present trace/span identifiers
are stored and validated against the exact frontier input, but a nullable span
ID is never a conflict or idempotency key. Re-executing one generation therefore
inserts the identical row or fails on different persisted fields; it cannot
create a second current claim.

An SDK claim has unique `(session_id, event_id)` and reuses the existing exact
Session source identity key `(source_adapter, source_event_id)`. On either
collision, every persisted identity, compatibility-tuple, payload-digest, nullable
trace/span and sanitized claim field must be identical for a no-op; any
difference is a hard corruption conflict that rolls back the caller's whole
transaction. The persisted local Event must belong to the exact local Session
and must carry the exact same source Event ID, adapter, surface, application
version, adapter version, normalization version, payload schema, schema
fingerprint and payload digest. A snapshot row is neither identity evidence nor
a claim writer. `source_surface` is an exact replay/equality field but does not
widen the existing `(source_adapter, source_event_id)` source identity.

## Shared atomic generation participant

One transaction-aware generation participant is used by:

- ordinary validated OTel ingestion;
- the sole `SourceCompatibilityReconciler`.

No OTel caller independently updates compatibility revision, invalidates
current OTel claims, or enqueues projection.

For a source-compatibility semantic change, the participant bumps the
compatibility revision, invalidates current, creates one desired generation,
stores its frontier, and queues it in the caller's existing SQLite transaction.
For a new eligible raw input that changes the frontier while the effective
resolution remains the same resolved version, it performs the same
invalidation/generation/queue operation without bumping compatibility revision.
An exact replay with unchanged effective semantics and unchanged frontier is a
no-op.

When effective resolution is not `resolved`, current OTel claims remain invalid.
The queued generation is deterministically superseded without publishing
claims. This preserves an auditable generation for the atomic state change
without treating unresolved input as positive Skill evidence.

The future #158 writer uses a separate transaction-aware SDK claim participant
owned by this component. In the same SQLite transaction as the exact
Session/Event/content/Retention writes, it inserts-or-verifies the complete SDK
claim row. It does not create or depend on an OTel trace generation, head,
frontier or queue. Until #158 pins and implements the wire parser and accepted
registry seed, that participant has no production admission path.

## OTel queue leases, retry and recovery

Each queue row persists:

- `state`;
- `attempt_count`;
- nullable `lease_owner`;
- `lease_generation`;
- nullable `lease_expires_at`;
- nullable `next_attempt_at`; and
- a nullable fixed sanitized error code.

A claim transaction changes an eligible `pending` row to `leased`, increments
`attempt_count`, writes a fresh opaque owner and monotonically increasing lease
generation, and sets `lease_expires_at` to exactly 30 seconds after claim time.
Only that owner/generation may complete or requeue the claim. A `leased` row
whose lease expired is reclaimable as the same generation; recovery does not
create another generation or claim row.

Projection is not required to finish inside the initial lease. While work is
active, one bounded heartbeat runs at most once per 10 seconds. In one SQLite
transaction it:

1. verifies the same desired generation, queue owner/generation and unexpired
   queue lease;
2. extends that same queue lease to 30 seconds after heartbeat time; and
3. renews every frontier Retention operation lease with its same item revision,
   owner and lease generation when it has reached the Retention v1 renewal
   deadline.

Retention v1's operation-lease duration is two minutes and its renewal deadline
is one minute. Renewal never changes the frontier or reacquires a different
item. If a heartbeat is busy, rejected, or observes any lost/expired lease, the
worker cancels projection, discards all constructed rows, and must not publish.
It may requeue the same generation as `retry_pending` only in a fresh
transaction that proves the same queue owner/generation and unexpired queue
lease; otherwise it makes no queue/generation mutation and expiry recovery or a
reclaiming worker controls that row. A later attempt acquires a new complete
composite Retention lease before rereading the frontier. This makes work longer
than either initial lease publishable through bounded renewal without treating
an expired capability as current.

Only the still-current queue owner/generation may record a retryable failure.
That owner uses no blocking sleep loop; it atomically sets the generation to
`retry_pending`, returns the queue to `pending`, clears lease ownership, and
sets:

```text
next_attempt_at =
  failure_time
  + min(2^min(max(attempt_count - 1, 0), 9), 300) seconds
```

`attempt_count` is a saturating nonnegative 64-bit integer and does not itself
cause terminal data loss.
`failed_terminal` is reserved for a deterministic non-retryable schema,
identity, digest or invariant failure. SQLite busy or a temporarily unavailable
Retention operation lease is retryable for a current queue owner. Queue-lease
loss makes the stale worker perform no mutation and leaves retry/reclaim to the
current owner. A source item that is authoritatively expired, deleted or
read-denied is
`input_unavailable`, not a retry or a synthetic empty projection.

Duplicate execution cannot duplicate a current row: generation identity and
claim natural keys are unique, and publication verifies the same queue
owner/generation in its transaction.

## OTel Retention eligibility and publication fence

Source compatibility validity and raw availability are independent. A
`resolved` trace with expired, deleted or read-denied frontier input has:

```text
generation outcome = input_unavailable
current OTel Skill claim = none
```

Before reading, the worker acquires one composite Retention operation lease
covering every frontier item. It holds the lease through parsing, projection
construction and publication. The lease capability binds each exact item,
catalog revision, lease owner and lease generation. No partial frontier is
returned.

The publication transaction revalidates all of:

- generation compatibility revision equals the current trace revision;
- current SourceCompatibility resolution is `resolved`;
- generation is still the unique desired generation;
- queue owner/generation and 30-second lease are current;
- every Retention operation lease has the exact renewed
  item-revision/owner/generation and expires after publication time;
- persisted frontier rows recompute to `input_frontier_sha256`;
- every input digest still matches the retained bytes; and
- projector version equals the generation projector version.

Every state mutation in a failing publication transaction first verifies the
same unexpired queue owner/generation. If queue ownership or its lease was lost,
the stale worker discards all work and makes no generation or queue mutation;
the current/reclaiming owner alone controls that row. While queue ownership is
still current, a compatibility, desired-generation, frontier or projector
change sets generation/queue to `superseded`; a Retention-only lease loss sets
the generation to `retry_pending` and returns the queue to `pending`; and
authoritatively unavailable input sets both to `input_unavailable`. No failing
path publishes or preserves a current claim.

## Single current read authority

The Skill projection read service is the sole reader for invocation/inventory
claims. For the `otel_trace_span` arm it returns a claim only when:

```text
generation.compatibility_revision
    == current_trace_compatibility_revision
AND current SourceCompatibility resolution == resolved
AND generation is the trace current pointer
AND generation lifecycle == current
```

For the `sdk_session_event` arm it returns a claim only when:

```text
the local Event belongs to the exact local Session
AND every stored source/provenance/digest field equals that Event
AND the current registry exactly accepts the complete compatibility tuple
```

SDK validity does not require a trace, span, SourceCompatibility row, OTel
generation, trace head, frontier or queue. If a producer trace/span pair exists,
it participates only in the exact cross-arm merge rule above.

These checks are not delegated to a screen, ad hoc SQL filter, Workspace
composer, or raw snapshot service. A raw snapshot may be available while the
claim is absent, stale or invalid.

Projected values remain sanitized identifiers and contain no Skill body or
absolute path. The raw body/path boundary is independently owned by #157/#158.

## Backup and restore

Runtime backup includes the complete `skill_projection:1` namespace:

- OTel generation-bound compatibility revision values;
- generations and desired/current pointers;
- frontier rows and hashes;
- queue state and leases;
- idempotency receipts;
- projected OTel invocation/inventory rows; and
- exact SDK claim rows with their full Session/Event/source/provenance,
  compatibility-tuple, payload-digest and nullable trace/span fields.

The component restores after Monitor/source interpretation and Retention, and
before the future `skill_invocation_snapshot` and Workspace components. A queue
row captured as `leased` restores as `pending` for the same generation with
lease owner/expiry cleared; its attempt count is preserved.

Restore fails closed unless revision chains, desired/current pointers, frontier
hashes, generation/queue state pairs and claim uniqueness are consistent.
It also validates every SDK local Session/Event foreign key, exact source Event
mapping, full compatibility tuple, payload digest and both uniqueness keys.
Registry rejection makes the restored SDK claim non-current; restore never
drops, rewrites or upgrades its tuple.
An older supported backup without `skill_projection` initializes an empty v1
component and discards obsolete pre-release Skill rows. A partial, newer or
unknown Skill component fails before mutation.

## Frozen boundaries and prohibited fallbacks

This component changes no `/api/monitor/*`, `/api/session-workspace/*` v1 or SSE
shape, property order or bytes. It adds no compatibility reader/writer,
historical backfill, permissive parser, raw-content route, UI route or
sanitized-only human fallback. It does not infer Session/source/parent identity,
convert missing to zero, or claim that #152 unknown attribute-key drift is
resolved.

## Required deterministic coverage

Tests must prove at least:

- exact supersession of a missing base observation enables a resolved
  generation, while a separate later resolved observation does not;
- the correction transaction hides queue work until commit;
- immediate invalidation makes an old claim unreadable before worker completion;
- a revision change immediately before publish cannot make an old generation
  current;
- work exceeding the initial queue lease renews the exact queue and Retention
  leases and can publish, while a lost heartbeat capability cannot publish;
- a stale worker whose queue lease was reclaimed cannot mutate the reclaimed
  queue/generation, while a still-current owner may requeue a Retention-only
  lease loss;
- an expired/lost Retention lease cannot publish;
- retry/crash recovery reuses the same generation and does not duplicate claims;
- a frontier change during ordinary resolved ingestion uses the shared atomic
  participant;
- a current-registry-supported SDK claim remains current-valid with neither
  trace nor span and without any OTel generation;
- SDK claim equality validates every local/source/provenance/compatibility field
  and rejects a differing collision atomically;
- an unsupported SDK compatibility tuple is invalid even when its event and raw
  snapshot exist;
- trace-only linkage does not merge arms, while an exact producer trace/span
  pair does; unlinked same-Session observations yield `null` count and
  `certification_pending`;
- pending/leased queue backup and restore preserve work, with leased restored
  as pending; and
- raw expiry before reprojection produces `input_unavailable`.
