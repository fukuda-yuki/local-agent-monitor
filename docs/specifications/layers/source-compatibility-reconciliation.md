# Source Compatibility Reconciliation Specification

Status: **Accepted for Issue #154 (DC154-01)**

This specification owns the current interpretation of immutable trace-scoped
source-version observations. It does not own source attribution, unknown
attribute-key detection, Session identity, or a public repair interface.

## Authority and immutable observations

`source_trace_version_observations` remains the immutable observation of what
one accepted ingest decoded at capture time. Its exact identity is:

```text
(source_observation_id, trace_id)
```

An existing base row is never updated or deleted to change `missing`,
`unrecognised`, `conflicting`, `resolved`, or its exact version token. A later
row for the same trace does not supersede an earlier row. In particular:

```text
missing + a separate resolved observation = missing
```

There is no external HTTP route, management route, UI action, manual SQL
workflow, generic repository mutation, or version-token entry form that can
change an interpretation. Raw input that is no longer retained cannot be
replaced by a manually entered version.

`SourceCompatibilityReconciler` is the sole writer of interpretation
corrections. Every other production owner can only append a new immutable base
observation or read the effective interpretation.

The schema enforces this boundary. `source_trace_version_observations` has
`BEFORE UPDATE` and `BEFORE DELETE` rejection triggers. Its parent
`source_schema_observations` rejects delete while any trace-version child
exists, and the child relationship is `ON UPDATE RESTRICT ON DELETE RESTRICT`;
the installed `ON DELETE CASCADE` path is removed by the accepted migration.
Deleting a raw Retention item therefore cannot cascade-delete an admitted
source observation. No generic cleanup or restore path may disable these
guards.

## Append-only interpretation revisions

The source-compatibility owner adds:

- an append-only interpretation supersession ledger;
- one interpretation head per base observation;
- one current compatibility revision per trace; and
- durable reconciliation operation receipts.

These objects are the additive `monitor` schema-v11 source-compatibility
authority. Exact monitor v10 is the supported predecessor. A database declaring
v11 must contain every v11-owned table, index and immutability trigger; an
older declaration with any colliding v11-owned name, or a v11 declaration with
a missing/extra/malformed owned object, fails before mutation.

Every supersession row contains at least:

| Field | Contract |
| --- | --- |
| `supersession_id` | opaque immutable identity |
| `source_observation_id`, `trace_id` | exact target base observation |
| `previous_interpretation_revision` | exact current revision before the operation |
| `new_interpretation_revision` | previous revision plus one |
| `derived_state` | `resolved`, `missing`, `unrecognised`, or `conflicting` |
| `exact_version` | exact retained token only when the state permits it |
| `reason` | `decoder_revision` or `registry_revision` |
| `retained_input_sha256` | 64 lowercase hexadecimal characters |
| `resolver_revision`, `registry_revision` | exact reviewed implementation/registry revisions |
| `created_at` | canonical UTC timestamp |
| `operation_fingerprint` | 64 lowercase hexadecimal characters |

Revision zero is the immutable base interpretation. Revisions for one exact
target are contiguous and append-only. The head identifies only the current
revision; it does not copy or mutate the base row. Supersession ledger rows and
receipts reject update and delete. A head update is legal only in the same
transaction that appends its exact next ledger row.

The v11 table ownership is:

| Table | Required key/content |
| --- | --- |
| `source_trace_version_interpretation_supersessions` | integer `supersession_id` primary key; exact target; previous/new revision; state/version; reason; retained-input/fingerprint digests; resolver/registry revisions; created time; unique exact target/new revision |
| `source_trace_version_interpretation_heads` | primary key `(source_observation_id, trace_id)`; current revision and exact supersession ID |
| `source_trace_compatibility_revisions` | `trace_id` primary key; nonnegative current revision; current effective state/version; updated time |
| `source_compatibility_reconciliation_receipts` | opaque `operation_key` primary key; request fingerprint; exact target/expected revision; fixed outcome; nullable resulting supersession/revision; created time |

IDs/revisions are nonnegative SQLite integers. Trace IDs are exact 32 lowercase
hexadecimal characters. Digests are exact 64 lowercase hexadecimal characters.
Times are UTC with seven fractional digits and `+00:00`. Revision tokens are
1..128 visible ASCII bytes with whitespace, controls and path separators
rejected. Every target foreign key is `ON UPDATE RESTRICT ON DELETE RESTRICT`.
Supersession and receipt tables have update/delete rejection triggers.

## Accepted triggers

The reconciler accepts only two internal triggers:

1. `decoder_revision`: replay the same retained raw record bytes with a newly
   accepted decoder/adapter revision. It may change `missing` to `resolved`
   only when the exact `service.version` token is recovered from those same
   bytes.
2. `registry_revision`: evaluate an already stored exact version token against
   a newly accepted registry. It may change `unrecognised` to `resolved`
   without changing that token.

Only decoder replay may resolve `missing`. Registry revision cannot create a
token that the retained observation did not contain. A decoder/registry result
whose semantic state and exact version equal the current interpretation is a
no-op: it appends no ledger row, changes no compatibility revision, creates no
OTel Skill generation or queue row, and reuses the durable receipt for an
identical operation.

## Effective trace aggregation

Every trace consumer uses effective interpretations (base revision zero or the
revision named by its head) and this exact precedence:

1. any effective `conflicting`, or more than one distinct exact version token:
   `conflicting`;
2. otherwise any effective `unrecognised`: `unrecognised`;
3. otherwise all effective observations are `resolved` and have one distinct
   exact version: `resolved`;
4. otherwise: `missing`.

`missing` is not a neutral element. An unrelated later resolved observation
does not hide a missing observation. The only way to remove that missing
contribution is an exact decoder supersession of the same
`(source_observation_id, trace_id)`.

## Canonical binary framing

The reconciliation and projection owners share one internal hashing codec:

- domain literals are exact UTF-8 bytes, including shown NUL bytes;
- unsigned integers are fixed-width big-endian (`U32BE` or `U64BE`);
- a required byte string is `U32BE(byte_length) || bytes`;
- a nullable byte string is `0x00` for null, or
  `0x01 || U32BE(byte_length) || bytes` for a present value;
- strings are strict UTF-8 with no trim, case fold, normalization, BOM, or
  trailing newline;
- SHA-256 values inside a hash input are their 32 decoded bytes, not hexadecimal
  text.

The operation fingerprint is:

```text
SHA256(
  UTF8("skill-projection-reconcile\0v1\0")
  || frame(U64BE(source_observation_id))
  || frame(decoded 16-byte lowercase trace_id)
  || frame(U64BE(expected_interpretation_revision))
  || frame(decoded retained_input_sha256)
  || frame(UTF8(resolver_revision))
  || frame(UTF8(registry_revision))
  || frame(UTF8(projector_version))
)
```

Every field is required for this fingerprint. The operation key is an opaque
internal idempotency key stored separately from the fingerprint. The same key
and fingerprint replays the stored result; the same key with a different
fingerprint is a hard conflict.

## Atomic change boundary

When an accepted correction changes the effective trace state or exact
version, one `BEGIN IMMEDIATE` SQLite transaction must:

1. append the supersession ledger row;
2. update the exact interpretation head;
3. increment the trace `compatibility_revision` exactly once;
4. immediately invalidate the old current OTel Skill generation;
5. create the desired OTel Skill projection generation;
6. persist its exact input frontier;
7. create its durable queue row; and
8. persist the idempotency receipt.

Neither the worker nor another connection can observe queue work before this
transaction commits. Rollback leaves all eight effects absent. The shared
Skill-generation participant defined by
[Skill Projection](skill-projection.md) performs steps 3–7; the reconciler does
not duplicate that logic.

Ordinary validated OTel ingestion uses the same participant in its
raw/Retention/source-observation transaction. A new base observation that
changes effective compatibility performs the same
revision/invalidation/generation operation.
A new eligible raw input that leaves compatibility unchanged may require a new
projection frontier/generation, but does not increment
`compatibility_revision`. This ordinary-input case is distinct from a
reconciliation semantic no-op.

## Backup and restore

Runtime backup includes immutable base observations, the supersession ledger,
interpretation heads, trace compatibility revisions, and reconciliation
receipts. Restore orders these authorities as:

```text
base raw and source observations
  -> interpretation ledger and heads
  -> Retention
  -> skill_projection generation, frontier, queue and current pointer
```

Restore fails closed for a non-contiguous revision chain, a head that does not
name the exact terminal revision, a target mismatch, a digest/fingerprint
format error, or disagreement between the trace revision and OTel Skill
generation state. SDK Session/Event claims are not trace-generation-bound. It
never resolves an observation merely because raw input is absent after restore.

## Non-authorities and frozen boundaries

- Issue #152 remains the sole unresolved owner of unknown attribute-key drift;
  this contract does not claim that #125 or #151 resolves it.
- Source attribution and source-version compatibility remain separate exact
  authorities.
- No Repository, Session, name, path, timestamp, ordinal, proximity, or
  cardinality heuristic may identify or supersede an observation.
- `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE shape, order and bytes
  are unchanged.

## Required deterministic coverage

Tests must prove at least:

- separate `missing` plus `resolved` remains `missing`;
- exact same-observation decoder supersession can become `resolved`;
- an exact unrecognised token can become resolved by registry revision;
- direct update/delete of immutable observation/ledger rows and parent-cascade
  deletion of a base observation are rejected;
- a semantic no-op adds no revision, generation, queue, or duplicate receipt;
- queue work is not visible before the correction transaction commits; and
- backup/restore preserves the effective interpretation and revision chain.
