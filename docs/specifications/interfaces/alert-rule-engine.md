# Alert Rule Engine Interface

## Scope

This specification defines Issue #80: the source-neutral, deterministic alert
receipt, compiled rule registry, evaluation engine, immutable engine store, and
read interfaces consumed by Issues #81, #82, #83, #84, and #85.

The engine does not ship a concrete alert rule. Issues #81 and #82 own rule
implementations and their source-neutral fixtures. Issue #83 owns lifecycle
state/events, mutation routes, optimistic concurrency, idempotency, and the
schema migration for those additions. Issue #84 owns Alert Center reads/UI and
recurring-pattern aggregation. Notifications, monetary-budget alerts, LLM-only
judgement, and automatic improvement/apply are outside this interface.

## Versioned Contracts

The fixed v1 versions are:

| Contract | Version |
| --- | --- |
| normalized snapshot | `alert.snapshot.v1` |
| engine configuration | `alert.config.v1` |
| alert receipt | `alert.receipt.v1` |
| evaluation result | `alert.evaluation.v1` |
| sanitized export profile | `sanitized-alert-receipt.v1` |
| canonical JSON | `alert.canonical-json.v1` |
| sensitive comparable hash | `alert.hmac-sha256.v1` |
| SQLite component | `schema_version(component='alert_engine', version=1)` |

An unknown contract version is rejected. Adding/removing a field, changing a
closed vocabulary, changing canonical ordering/serialization, weakening exact
evidence validation, or changing a hash framing rule requires a new version.

## Normalized Snapshot

`AlertNormalizedSnapshot` is sanitized, source-neutral input. It contains:

- source surface and observed source version;
- exact local Session ID and optional exact trace ID;
- Issue #61 completeness state and canonical reason set;
- first/last observed canonical UTC times;
- declared capability facts using `available | unavailable | unknown`;
- ordered normalized signals.

Each signal has a stable sanitized signal ID, one fixed kind
(`llm_call | tool_call | permission | file_access | session_event`), a
non-negative sequence, canonical UTC time, optional parent signal ID, one fixed
status (`success | error | cancelled | unknown`), numeric metrics, opaque
comparable keys, and one exact evidence reference. Metrics are finite decimals
with a versioned name/unit. A comparable key is either a bounded metadata token
or a v1 HMAC label; it is never a prompt/tool body, argument/result, source
fragment, credential, PII, or path.

Source-specific parsing and field mapping occur before this boundary. Missing
token/cache/status/limit/duration values are absent or `unknown`; they are never
converted to zero. Repository/workspace/timestamp proximity does not create a
signal, identity, ownership, or evidence relationship. Historical-only input
keeps `historical_summary_only` and cannot be promoted to full evidence.

Canonical snapshot ordering is:

1. completeness reasons in the Issue #61 canonical reason order;
2. capabilities by ordinal capability name;
3. signals by sequence, observed time, then signal ID;
4. signal metrics and comparable keys by ordinal name.

Comparable-key kind is explicit: `metadata_token` accepts only the bounded
metadata-token grammar, while `sensitive_hmac` accepts only the exact
`hmac-sha256-v1:<64 lowercase hex>` shape. A raw argument, file name, or path
cannot be placed in either form.

Duplicate capability, metric, comparable-key, sequence/signal ID, or exact
evidence identity is invalid input rather than last-write-wins data.

## Exact Evidence Reference And Validation

An `AlertEvidenceReference` contains a fixed evidence kind
(`session | trace | span | turn | event | tool_call`), opaque evidence ID,
exact Session ID, optional exact trace/span/turn/event/tool-call IDs, and the
evidence observation time. IDs are bounded opaque values; path/URI separators,
control characters, whitespace, query strings, and fragments are rejected.

Every receipt evidence reference must:

1. be present byte-for-byte in the normalized snapshot;
2. match the receipt Session and trace scope;
3. resolve through the injected `IAlertEvidenceResolver` at evaluation time.

The resolver performs an exact persisted lookup. It may not use latest,
repository, workspace, conversation, timestamp proximity, names, content, or
hash reversal. If one reference is absent or unresolved, that proposed receipt
is rejected with fixed code `unresolved_evidence`; no partial receipt is
persisted. Duplicate references are de-duplicated and sorted by evidence kind,
Session, trace, span, turn, event, tool-call, opaque evidence ID, and time.

## Compiled Rule Registry

Rules are compiled implementations of `IAlertRule`; arbitrary scripts,
expressions, model prompts, and runtime code loading are not allowed. A
descriptor freezes:

- stable rule ID and version;
- repository-safe title and description;
- required capabilities;
- scope (`session | trace | cross_session`);
- grouping-key names;
- evaluation-window token;
- warning/critical threshold schema;
- fixed suppression codes;
- applicable source surfaces.

`IAlertRule.Evaluate` returns one `AlertRuleOutcome`: zero or more matches plus
zero or more rule-level suppressions. Suppressions contain only a bounded code;
they carry no message, observed/raw value, identifier, path, or exception text.
This lets concrete rules explicitly report minimum-sample, partial-input,
unsupported-cache, or unknown-limit conditions without inventing a match.

Descriptors are validated and exposed in rule-ID/version order. Duplicate rule
ID/version pairs are rejected. Rule IDs, versions, capability names, grouping
keys, source surfaces, metric names, units, window tokens, and suppression codes
use bounded lowercase metadata tokens. Titles/descriptions are registered code
metadata, never captured source text.

Threshold definitions specify name, unit, direction
(`higher_is_worse | lower_is_worse`), inclusive minimum/maximum, and warning and
critical defaults. Overrides must remain within the inclusive range. Warning
must not be more severe than critical: warning <= critical for
`higher_is_worse`, warning >= critical for `lower_is_worse`.

## Configuration

`AlertEngineConfiguration` uses `alert.config.v1`, a bounded configuration
version, and at most one configuration entry per registered rule. Each entry
contains enabled/disabled state, numeric threshold overrides, and an optional
source-surface allowlist that must be a subset of the descriptor applicability.
Unlisted rules use the descriptor defaults and are enabled.

Invalid version, duplicate rule/config/threshold/source entry, unknown rule,
unknown threshold, non-finite/out-of-range value, invalid warning/critical
relationship, or impossible source override returns explicit
`invalid_configuration`. There is no permissive parsing or silent fallback.

The config hash is lowercase SHA-256 over canonical v1 configuration bytes,
including defaults expanded in registered rule order. Receipt fields keep both
the caller configuration version and exact config hash. A threshold change
therefore creates a new evaluation/receipt identity and never rewrites an
existing receipt.

Canonical decimal text uses invariant `G29`: insignificant trailing zeroes and
a signed or scaled decimal zero are removed before hashing/serialization.
Numerically equal decimal inputs such as `1.0` and `1.00` therefore produce the
same snapshot/config hashes, alert IDs, and canonical bytes.

## Evaluation

The evaluator is pure apart from exact evidence resolution and an optional
append-only engine store. It validates and canonicalizes input/config, computes
the input/config hashes, then evaluates registered rules in rule-ID/version
order.

For each enabled and source-applicable rule:

- any required capability that is `unavailable`, `unknown`, or absent produces
  one bounded suppression with code `missing_required_capability` and the
  sorted missing capability names; the rule is not invoked and no alert is
  emitted. This and the other two engine suppression codes cannot be emitted
  by a rule;
- a disabled rule or source override produces `rule_disabled` or
  `source_not_applicable` suppression;
- a rule match supplies only severity, numeric observed values, exact snapshot
  evidence references, and first/last observed times; the engine adds the
  effective registered thresholds;
- a rule-level suppression is accepted only when its bounded code is declared
  by that descriptor and is not an engine suppression code; undeclared codes
  fail as bounded `invalid_rule_output`;
- evidence validation occurs before receipt creation;
- identical matches and suppressions are de-duplicated by canonical identity;
- receipt order is severity (`critical`, then `warning`, then `info`), rule ID,
  rule version, first observed, evidence identity, then alert ID.

An evaluation result includes receipts, suppressions, and rejected proposed
receipts. Rejections expose only rule ID/version and a fixed code; rejected IDs,
raw values, exception text, and resolver details are not returned or logged.
One rejected match does not convert valid independent matches into alerts or
suppressions and does not invent missing evidence.

The evaluation input hash is lowercase SHA-256 over canonical snapshot bytes.
The evaluation ID and alert IDs are domain-separated SHA-256 values over
length-framed canonical identifiers, including the schema/rule/config/input
versions and hashes. Delimiter concatenation, trimming, case folding, current
time, random IDs, process state, dictionary enumeration order, and local-machine
state are not inputs. The same normalized snapshot, registry, and configuration
therefore produce byte-equivalent `alert.evaluation.v1` bytes.

## Alert Receipt

An immutable `alert.receipt.v1` contains:

- schema version and sanitized export profile;
- deterministic alert and evaluation IDs;
- rule ID/version and `critical | warning | info` severity;
- initial state `open` (lifecycle changes are separate Issue #83 events);
- source surface/version and exact Session/optional trace ID;
- canonical exact evidence references;
- numeric observed values and effective thresholds;
- config version/hash;
- sorted required capabilities;
- Issue #61 completeness and canonical reasons;
- first/last observed canonical UTC times;
- evaluation input hash;
- the registered repository-safe rule title as summary.

The receipt is immutable. Issue #83 must reference `alert_id` and append state
events; it must not update receipt JSON, evidence, rule/config version, hashes,
or observed values. Re-evaluation with unchanged rule/input/config resolves to
the same alert ID. A changed rule/config/input creates a different evaluation
identity; any superseding relationship belongs to Issue #83.

Observed values and thresholds are numeric only. Receipt fields never contain a
raw prompt/response/system prompt, tool arguments/results, source/file body,
PII, credential/token/header, path/URI, arbitrary exception text, or arbitrary
model-generated prose.

## Sensitive Comparable Hashing

Raw argument/file comparisons occur before receipt creation through
`AlertSensitiveValueHasher`. V1 uses HMAC-SHA-256 with:

- a caller-owned private key of at least 32 bytes;
- domain `copilot-agent-observability/alert-comparable/v1`;
- length-framed scope ID, purpose token, and exact UTF-8 value;
- output label `hmac-sha256-v1:` plus 64 lowercase hex characters.

The private key and raw value are never persisted, logged, serialized, or
returned. Keyed HMAC is mandatory even for low-entropy values; unkeyed hashing
of raw arguments, file names, short secret values, booleans, enum-like values,
or local paths is not an available API. Comparison labels are scoped to the
explicit evaluation/session scope, may be used only as within-scope comparable
keys, and may not become Session identity, merge keys, public labels, or receipt
observed values. Exact source-provided hashes remain opaque source metadata and
are never reversed.

## Engine Persistence And Read Interfaces

`IAlertEngineStore` exposes only:

- initialize/validate the `alert_engine` v1 component;
- append one completed canonical evaluation atomically;
- read one evaluation by deterministic evaluation ID;
- read one immutable receipt by alert ID;
- list suppressions for one evaluation in canonical order.

Initialize/append return `success | busy | unavailable | conflict` with the
fixed error codes below. Reads return `success | not_found | busy |
unavailable`; a suppression-list read returns an empty successful list for a
known evaluation with no suppressions. `not_found` is distinct from store
failure and uses `alert_not_found`. Store failures use `alert_store_busy`,
`alert_store_unavailable`, or `alert_store_conflict`; raw SQLite messages are
never returned.

SQLite v1 uses separate additive tables `alert_evaluations`, `alert_receipts`,
and `alert_suppressions`. Canonical result/receipt/suppression JSON is stored as
exact UTF-8 text and is never regenerated from unordered SQL rows. Re-appending
the same ID with byte-identical bytes is idempotent; the same ID with different
bytes is `alert_store_conflict`. There is no update/delete API in Issue #80.

Schema creation uses one `BEGIN IMMEDIATE` transaction, creates/validates only
the alert tables, and inserts `schema_version(component='alert_engine',1)` last.
Failure rolls back to the exact pre-alert schema/rows. The `alert_engine`
component owns exactly `alert_evaluations`, `alert_receipts`, and
`alert_suppressions`; it validates those names and definitions only. Existing monitor,
Session, Doctor, retention, and source-compatibility component versions/rows are
unchanged. Tables owned by another versioned component, including Issue #83
lifecycle tables, coexist and are neither validated nor changed by this
component. A newer engine component, a missing engine-owned table, or a
definition-mismatched engine-owned table fails closed as
`alert_store_unavailable`; it is not repaired, downgraded, or migrated
permissively. Busy/locked maps to `alert_store_busy`.

The store is the immutable engine result source for later read/API adapters.
Issue #80 adds no HTTP/CLI route. Issue #83 owns lifecycle persistence and API;
Issue #84 owns UI/read-model routes. Both consume these IDs/bytes rather than
creating a second receipt/evidence identity model.

## Optional Sanitized Export Profile

Issue #85 may include the exact canonical `alert.receipt.v1` bytes under profile
`sanitized-alert-receipt.v1`, plus evaluation/config/schema versions and hashes.
It must reject unknown receipt/profile versions and must not infer missing
receipt types. Lifecycle events are a separate optional profile owned by Issue
#83. The exporter may mark an absent future lifecycle profile unavailable; it
must not rewrite the immutable receipt or include raw evidence.

### Canonical receipt consumer compatibility boundary

`AlertReceiptConsumerV1.Validate(ReadOnlySpan<byte>)` is the only public v1
byte-consumer authority. It accepts one exact canonical UTF-8
`alert.receipt.v1` value and returns a sealed projection containing only
`AlertId`, `SessionId`, optional `TraceId`, `SourceSurface`, and
`LastObservedAt`. Its constructor is not public and it exposes no receipt body,
evidence, observed value, threshold, rule text, configuration value, or raw JSON.

Before returning that projection the consumer:

1. rejects empty input or input above 8,388,608 bytes and parses with maximum
   JSON depth 3, the fixed maximum depth of the exact v1 receipt shape;
2. rejects malformed JSON, comments, trailing data, unknown or duplicate fields,
   unknown closed-enum values, and any receipt/profile version other than the
   fixed v1 values;
3. validates lowercase hashes, bounded metadata tokens, opaque IDs, bounded
   summary text, non-empty evidence/observed-value collections, canonical
   numeric/unique effective-threshold entries, unique fields and collection
   identities, exact evidence Session/trace scope and kind-required IDs,
   canonical completeness-reason ceilings/order, required-capability
   uniqueness/order, and receipt time order;
4. serializes the parsed receipt with `AlertCanonicalJson.SerializeReceipt` and
   requires byte-for-byte equality with the supplied UTF-8, thereby rejecting
   alternate field/collection order, whitespace, escapes, timestamp spellings,
   and decimal spellings;
5. recomputes `alert_id` with the exact owner `alert-receipt/v1` derivation used
   by the engine and requires equality; `evaluation_id`, `evaluation_input_hash`,
   and `configuration_hash` receive lowercase hash-shape checks only because a
   receipt does not contain the complete registry fingerprint, normalized
   snapshot, or expanded configuration needed to recompute them; and
6. maps every failure to `AlertReceiptConsumerException` with fixed code
   `invalid_alert_receipt` and message `Alert receipt is invalid.`, without
   source bytes, identifiers, paths, values, parser text, or inner exceptions.

The consumer semantic implementation and behavior-neutral alert-ID derivation
helper are owned with the #80 contract; a downstream consumer may not copy or
relax them. Existing serializer, evaluator, and store admission behavior is not
changed by this additive boundary. Validation proves only canonical receipt-v1
structure and receipt-internal consistency. It does not prove store provenance,
a signature, authorization, source-evidence resolution history, or that a caller
supplied the bytes from the engine store. Receipt-only validation also cannot
prove that summary equals the registered rule title or that thresholds,
required capabilities, source, or completeness match the absent descriptor,
configuration, or normalized snapshot. A self-consistent fabricated receipt can
recompute `alert_id`; trusted store acquisition and the downstream bundle scanner
remain separate requirements.

The historical `alert-receipt-v1.golden.json` remains the byte/SHA fixture for
the serializer and is unchanged. Its deliberately fabricated `aaaa...` alert ID
is not derivation-valid, so it is not a positive consumer fixture. Consumer
acceptance is pinned instead to deterministic bytes produced through the real
engine path; tests also prove every covered engine-produced receipt validates.

The 8 MiB gate is additive to this public consumer/export boundary only. It does
not change reachable producer serialization or existing persistence bytes. A
downstream component encountering a larger receipt reports it unavailable or
failed without truncation or a partial-success artifact. Raising the ceiling
requires a named future consumer/profile revision; it is not a permissive v1
fallback.

## Required Tests And Handoffs

Issue #80 tests cover canonical byte equivalence, rule/config ordering,
duplicate evidence/config rejection, missing capability suppression, unresolved
evidence rejection, invalid thresholds, mixed source applicability, partial and
historical completeness, receipt privacy, low-entropy HMAC safety, immutable
append/read, fresh database, supported existing-database initialization,
transaction rollback, newer/broken schema refusal, unchanged serializer-golden
fixture bytes/hash, an engine-produced consumer-golden hash, and the strict
public receipt-consumer compatibility boundary.

Handoffs:

- #81/#82 implement `IAlertRule` only and register descriptors/rules without
  changing v1 receipt, evidence, suppression, configuration, or evaluator
  contracts.
- #83 references immutable `alert_id`/evaluation metadata and adds separate
  lifecycle tables/events/API; it never adds lifecycle fields to the receipt.
- #85 consumes only `sanitized-alert-receipt.v1` canonical bytes and explicitly
  records unavailable future profiles.
