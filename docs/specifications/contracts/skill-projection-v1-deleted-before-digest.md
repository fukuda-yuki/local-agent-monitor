# Skill projection v1 deleted-before-digest contract

Status: **Accepted — DC154-03**

This contract governs only the exact supported monitor-schema v10 predecessor in which a projection-bearing source observation retains its exact `raw_record_id` after the referenced raw row was deleted before a payload digest was captured. DC154-01 remains authoritative for all other #154 behavior.

## Persisted representation

Projection input evidence is the closed tagged union `payload_sha256 | deleted_before_digest_v10`, keyed by exact `source_observation_id` and `raw_record_id`. `payload_sha256` requires one exact 64-character lowercase hexadecimal digest. `deleted_before_digest_v10` requires a null digest and is never interpreted as a digest or database corruption.

Only one-way migration from the exact accepted v10 shape may create the marker, when the observation and projection-bearing child remain, `raw_record_id` is non-null, and the referenced raw row is absent in the same migration snapshot. A present raw row yields its real digest or migration fails closed. Runtime writers and later migrations cannot create or rewrite the marker.

## Correction outcome

`decoder_revision` requires original payload bytes. Against the marker, correction is prohibited and the idempotent result is `input_unavailable`; no supersession, head, compatibility revision, generation, invalidation, or queue work changes.

`registry_revision` may still reinterpret an exact persisted `unrecognised` token. Any resulting marker-bearing desired generation is terminal `input_unavailable`, is never pending or leased, acquires no Retention lease, publishes no projection rows, and supplies no current OTel Skill claim.

## Frontier and fingerprint framing

All `skill_projection:1` frontiers use domain `skill-projection-frontier\0v2\0`. Hash the canonical length-prefixed sequence `trace_id`, item count, then every item ordered by `(raw_record_id, source_observation_id)` with both identities, `input_evidence_kind`, and `evidence_value`. The digest variant uses the exact digest; the marker variant uses predecessor-schema token `10`. The tag makes the frames disjoint, and no item may be omitted or shortened.

All reconciliation fingerprints use domain `skill-projection-reconcile\0v2\0` and hash target observation/trace identity, expected interpretation revision, raw-record identity, tagged evidence value, resolver revision, registry revision, and projector version. Same key/fingerprint replays the result; a different fingerprint hard-conflicts. The unintegrated v1 framing is replaced, not dual-read.

## Backup, restore, and reads

Backup includes tagged input evidence, marker frontier items, terminal generation/queue state, v2 hashes, and receipts. Restore preserves a current-schema marker exactly; exact-v10 restore derives a real digest from a present row and the marker from an absent referenced row. It never searches elsewhere for bytes or upgrades the marker in place.

Restore fails closed on identity/variant contradictions, a digest on the marker, a raw row contradicting the marker, frontier/hash/fingerprint mismatch, non-terminal marker work, projected rows, or a current claim over a marker-bearing generation. The single read authority returns no `otel_trace_span` claim for such a generation even when SourceCompatibility is `resolved`; raw snapshots and `sdk_session_event` cannot bypass this rule.
