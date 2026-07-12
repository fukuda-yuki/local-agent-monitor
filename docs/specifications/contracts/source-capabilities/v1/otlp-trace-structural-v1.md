# OTLP Trace Structural Descriptor v1

This file is the executable-design source of truth for the structural inventory
used by Issues #62-#65. It pins OpenTelemetry Proto release `v1.10.0`, commit
`ca839c51f706f5d53bfb46f06c3e90c3af3a52c6`. Later OTLP revisions require a
new reviewed descriptor version; application-version updates do not.

## Canonical encoding

Canonical structural identities have these fields, in this order:

1. `signal`: fixed `trace`
2. `envelope`: one code from the table below
3. `role`: `envelope`, `known_field`, `span_name`, `event_name`,
   `attribute_key`, `schema_url`, `unknown_json_property`, or
   `unknown_protobuf_field`
4. `name_token`: a fixed descriptor/transport identifier or a keyed producer
   name token
5. `structural_type`: `object`, `array`, `string`, `bool`, `int`, `double`,
   `bytes`, `null`, `span`, `event`, `attribute`, `varint`, `fixed32`,
   `fixed64`, or `length_delimited`

An identity is encoded as ASCII `source-schema-identity-v1`, one NUL byte, then
each field as a four-byte unsigned big-endian UTF-8 byte length followed by its
UTF-8 bytes. Identities are sorted by unsigned lexicographic comparison of
their complete encoded bytes.

Producer-controlled names use:

```text
sha256:<64 lowercase hexadecimal characters>
SHA256(UTF8("source-structure-v1\0" + role + "\0" + raw_name))
```

The role token is exactly one of `span_name`, `event_name`, `attribute_key`,
`schema_url`, or `unknown_json_property`. Instrumentation-scope name/version
and field values are not structural names and are never retained.

The schema fingerprint hashes ASCII `source-schema-fingerprint-v1`, one NUL,
then every full-set identity as a four-byte big-endian byte length plus encoded
identity. The inventory hash uses ASCII `source-inventory-hash-v1`, one NUL,
then the same sequence with an eight-byte unsigned big-endian bounded occurrence
count after each identity. SHA-256 output is 64 lowercase hexadecimal
characters. Diagnostic truncation occurs only after both hashes are computed.

Fixed unknown identifiers are:

```text
json:<envelope>:property:<producer-name-token>:type:<structural-type>
json:<envelope>:known:<field-code>:actual:<structural-type>
protobuf:<envelope>:field:<positive-decimal>:wire:<varint|fixed64|length_delimited|fixed32>
```

## Descriptor table

`message` means a length-delimited child message. Repeated message fields use
JSON arrays. The type column is the transport-independent `structural_type`.
Its accepted JSON representation is: `object`/`array`/`string`/`bool` map to the
same JSON kind; `double` is a JSON number; `bytes` is a JSON string; and `int`
is a decimal JSON string for `start_time_unix_nano`, `end_time_unix_nano`,
`event.time_unix_nano`, and `any_value.int`, but a JSON number for counts,
enums, status code, flags, `any_value.string_strindex`, and
`key_value.key_strindex`. Any other JSON kind is a known-wrong-type unknown.
Known fields produce transport-independent `known_field` identities using the
field code and semantic type shown here.

Identity emission is exact:

- Each accepted message/object emits `(trace, E, envelope, E, object)` once.
- Each correctly typed singular known field occurrence emits
  `(trace, E, known_field, F, T)`. A repeated field emits that identity once per
  element; an absent or empty repeated field emits none.
- `span.name` additionally emits `(trace, span, span_name, H, string)`;
  `event.name` emits `(trace, event, event_name, H, string)`; every `KeyValue.key`
  emits `(trace, key_value, attribute_key, H, string)`; and every schema URL
  emits `(trace, E, schema_url, H, string)`, where `H` is its role-specific
  producer-name token. The known-field identity is emitted as well.
- `EntityRef.idKeys[]` and `descriptionKeys[]` additionally emit
  `(trace, entity_ref, attribute_key, H, string)` for each item.
- A wrong-typed known JSON field emits only
  `(trace, E, unknown_json_property,
  json:<E>:known:<F>:actual:<actual-structural-type>,
  <actual-structural-type>)`.
  An unknown JSON property or protobuf field emits only its fixed transport
  identity. Its tuple envelope is the containing envelope, its role is the
  corresponding unknown role, its name token is the complete fixed identifier,
  and its structural type is the observed JSON or protobuf type.
- Descriptor-known Trace-ignored fields emit their `known_field` identity but
  no content identity and do not increment unknown counts.

| Envelope code | JSON field | Semantic type | Protobuf tag/wire | Field code / child |
| --- | --- | --- | --- | --- |
| `request` | `resourceSpans` | array | 1/message | `request.resource_spans` / `resource_spans` |
| `resource_spans` | `resource` | object | 1/message | `resource_spans.resource` / `resource` |
| `resource_spans` | `scopeSpans` | array | 2/message | `resource_spans.scope_spans` / `scope_spans` |
| `resource_spans` | `schemaUrl` | string | 3/length_delimited | `resource_spans.schema_url` / hashed `schema_url` |
| `resource` | `attributes` | array | 1/message | `resource.attributes` / `key_value` |
| `resource` | `droppedAttributesCount` | int | 2/varint | `resource.dropped_attributes_count` |
| `resource` | `entityRefs` | array | 3/message | `resource.entity_refs` / `entity_ref` |
| `scope_spans` | `scope` | object | 1/message | `scope_spans.scope` / `scope` |
| `scope_spans` | `spans` | array | 2/message | `scope_spans.spans` / `span` |
| `scope_spans` | `schemaUrl` | string | 3/length_delimited | `scope_spans.schema_url` / hashed `schema_url` |
| `scope` | `name` | string | 1/length_delimited | `scope.name` |
| `scope` | `version` | string | 2/length_delimited | `scope.version` |
| `scope` | `attributes` | array | 3/message | `scope.attributes` / `key_value` |
| `scope` | `droppedAttributesCount` | int | 4/varint | `scope.dropped_attributes_count` |
| `span` | `traceId` | bytes | 1/length_delimited | `span.trace_id` |
| `span` | `spanId` | bytes | 2/length_delimited | `span.span_id` |
| `span` | `traceState` | string | 3/length_delimited | `span.trace_state` |
| `span` | `parentSpanId` | bytes | 4/length_delimited | `span.parent_span_id` |
| `span` | `name` | string | 5/length_delimited | `span.name` / hashed `span_name` |
| `span` | `kind` | int | 6/varint | `span.kind` |
| `span` | `startTimeUnixNano` | int | 7/fixed64 | `span.start_time_unix_nano` |
| `span` | `endTimeUnixNano` | int | 8/fixed64 | `span.end_time_unix_nano` |
| `span` | `attributes` | array | 9/message | `span.attributes` / `key_value` |
| `span` | `droppedAttributesCount` | int | 10/varint | `span.dropped_attributes_count` |
| `span` | `events` | array | 11/message | `span.events` / `event` |
| `span` | `droppedEventsCount` | int | 12/varint | `span.dropped_events_count` |
| `span` | `links` | array | 13/message | `span.links` / `link` |
| `span` | `droppedLinksCount` | int | 14/varint | `span.dropped_links_count` |
| `span` | `status` | object | 15/message | `span.status` / `status` |
| `span` | `flags` | int | 16/fixed32 | `span.flags` |
| `event` | `timeUnixNano` | int | 1/fixed64 | `event.time_unix_nano` |
| `event` | `name` | string | 2/length_delimited | `event.name` / hashed `event_name` |
| `event` | `attributes` | array | 3/message | `event.attributes` / `key_value` |
| `event` | `droppedAttributesCount` | int | 4/varint | `event.dropped_attributes_count` |
| `link` | `traceId` | bytes | 1/length_delimited | `link.trace_id` |
| `link` | `spanId` | bytes | 2/length_delimited | `link.span_id` |
| `link` | `traceState` | string | 3/length_delimited | `link.trace_state` |
| `link` | `attributes` | array | 4/message | `link.attributes` / `key_value` |
| `link` | `droppedAttributesCount` | int | 5/varint | `link.dropped_attributes_count` |
| `link` | `flags` | int | 6/fixed32 | `link.flags` |
| `status` | `message` | string | 2/length_delimited | `status.message` |
| `status` | `code` | int | 3/varint | `status.code` |
| `key_value` | `key` | string | 1/length_delimited | `key_value.key` / hashed `attribute_key` |
| `key_value` | `value` | object | 2/message | `key_value.value` / `any_value` |
| `any_value` | `stringValue` | string | 1/length_delimited | `any_value.string` |
| `any_value` | `boolValue` | bool | 2/varint | `any_value.bool` |
| `any_value` | `intValue` | int | 3/varint | `any_value.int` |
| `any_value` | `doubleValue` | double | 4/fixed64 | `any_value.double` |
| `any_value` | `arrayValue` | object | 5/message | `any_value.array` / `array_value` |
| `any_value` | `kvlistValue` | object | 6/message | `any_value.kvlist` / `key_value_list` |
| `any_value` | `bytesValue` | bytes | 7/length_delimited | `any_value.bytes` |
| `any_value` | `stringValueStrindex` | int | 8/varint | `any_value.string_strindex` / Trace-ignored nonfatal |
| `array_value` | `values` | array | 1/message | `array_value.values` / `any_value` |
| `key_value_list` | `values` | array | 1/message | `key_value_list.values` / `key_value` |
| `key_value` | `keyStrindex` | int | 3/varint | `key_value.key_strindex` / Trace-ignored nonfatal |
| `entity_ref` | `schemaUrl` | string | 1/length_delimited | `entity_ref.schema_url` / hashed `schema_url` |
| `entity_ref` | `type` | string | 2/length_delimited | `entity_ref.type` |
| `entity_ref` | `idKeys` | array | 3/length_delimited | `entity_ref.id_keys` / hashed `attribute_key` items |
| `entity_ref` | `descriptionKeys` | array | 4/length_delimited | `entity_ref.description_keys` / hashed `attribute_key` items |

The two `*strindex` fields are defined by pinned common.proto for Profiles only.
For Trace input they are descriptor-known, ignored nonfatally exactly as the
upstream comment requires, and are never dereferenced or treated as unknown.
`Resource.entityRefs` is part of Trace resources and is recursively walked.

An envelope identity uses its fixed envelope code and semantic type. A valid
required trace signal is one JSON object reached through
`request.resourceSpans[].scopeSpans[].spans[]`, or one well-formed protobuf
`Span` message reached through request tag 1, resource-spans tag 2, scope-spans
tag 2. The Span message may be empty. A wrong-typed optional Span field is
unknown but does not invalidate that containing Span envelope. A wrong-typed
hierarchy field or non-object JSON span item does not create a valid Span.
Malformed protobuf is an adapter parse failure, not a missing-signal result.

## Closed inventory domain

`SourceStructuralNameToken`, `SourceOccurrenceCount`, `SourceUnknownIdentity`,
`SourceStructuralInventory`, and all observation/failure drafts use private
constructors or validating factories. Undefined enums are rejected. Collections
are copied to immutable snapshots, sorted canonically, and cannot be mutated by
the caller. The inventory factory alone computes full-set hashes, exact aggregate
unknown span/event/attribute occurrence counts, the retained first 256 unknown
rows, and overflow counts. Callers cannot provide a fingerprint, hash, aggregate
count, or `HasUnknownFields` independently.

The registry rejects duplicate or conflicting evidence for a surface/version/
fingerprint and exposes separate verified-fingerprint, incompatible-version,
and recognition-profile inputs. `DecodedOtlpTracePayload` has no implicit string
conversion; consumers explicitly use `PayloadJson`.

## Required executable matrix

Each named test uses independently written expected strings/tokens/hashes; it
does not call production token/hash helpers to form its oracle.

| Test | Input | Expected |
| --- | --- | --- |
| `Build_KnownNestedEnvelope_JsonAndProtobufMatchGoldenFingerprints` | Equivalent payload containing every descriptor envelope | Equal hard-coded hashes; no unknowns. |
| `Build_UnknownJsonFieldAtEachEnvelope_IsCapturedAndSanitized` | Every envelope with dotted PII/JWT-like names and marker values | Per-kind aggregate increments; literal names/values absent; pinned tokens present. |
| `Build_UnknownProtobufFieldAtEachEnvelope_IsCapturedBeforeConversion` | Field 99 with every protobuf wire type | Transport identity/count retained; canonical JSON omits it; values absent. |
| `Decode_OfficialSpanAndLinkFlagsFixed32_AreRecognizedAndConverted` | Tags 16/6 fixed32 | Converted; not unknown. |
| `Decode_VarintFlags_AreUnknownRatherThanRecognized` | Tags 16/6 varint | Transport-scoped unknowns. |
| `Build_KnownFieldWithWrongJsonType_IsUnknown` | Every descriptor field with one wrong JSON type | Known-wrong-type identity; hierarchy mismatch cannot satisfy required signal; optional Span mismatch leaves containing Span valid. |
| `Build_AllProducerNamesAreHashed` | Semantic key, dotted PII, JWT/token, path, span/event/schema names | No literal appears in serialized inventory. |
| `Build_ValueChangesDoNotChangeEitherHash` | Same structure, different values | Both hashes unchanged. |
| `Build_NameOrTypeChangeChangesSchemaFingerprint` | Change only name/type | Fingerprint changes without exposing input. |
| `Build_OrderAndCountSemanticsAreIndependent` | Reorder, then add one occurrence | Reorder changes neither; count changes inventory hash only. |
| `Build_OverflowStillHashesFullStructuralSet` | Two sets differing only in identity 257 | Retained/overflow counts equal; both hashes differ. |
| `Build_AggregateUnknownCountsIgnoreRetainedRowLimit` | More than 256 unknowns of all kinds | Exact per-kind bounded occurrence totals independent of retained rows. |
| `InventoryFactory_RejectsInvalidAndDefensivelyCopies` | Undefined enums, invalid counts/tokens, mutable inputs | Invalid rejected; later caller mutation has no effect. |
| `Registry_RejectsDuplicateAndConflictingEvidence` | Duplicate/conflicting evidence table | Exact construction failures. |
| `Assess_VersionAndFingerprintPolicy` | Version/fingerprint/incompatible/missing-signal table | Canonical state/reason/action. |
| `Assess_CombinedConditionsFollowCanonicalPrecedence` | Drop+drift, unsupported+drift, drift+unknown | Drop, unsupported, drift. |
| `ObservationFactory_StateReasonAndCaptureAreClosed` | All factories plus undefined casts | Only valid state/reason/action/capture products construct. |
| `UnknownFactory_ValidatesNameCountTimeAndReference` | Boundary/malformed token/count/time/kind table | Exact accept/reject outcomes. |
| `ReasonSet_DeduplicatesAndOrdersHardCodedVocabulary` | Scrambled duplicates | Exact six-value canonical order. |
| `DecodedPayload_ConsumersExplicitlyUsePayloadJson` | Monitor host and raw receiver requests | Existing raw payload behavior unchanged without implicit conversion. |
