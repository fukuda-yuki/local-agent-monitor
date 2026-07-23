# Alert Rule Engine Interface

## Scope

This specification defines Issue #80: the source-neutral, deterministic alert
receipt, compiled rule registry, evaluation engine, immutable engine store, and
read interfaces consumed by Issues #81, #82, #83, #84, and #85. It also defines
the additive source-neutral evaluate-and-append application boundary and the
bounded trusted-store query handoff required by Issue #84.

The v1 engine does not ship a concrete alert rule. Issues #81 and #82 own rule
implementations and their source-neutral fixtures. Issue #83 owns lifecycle
state/events, mutation routes, optimistic concurrency, idempotency, and the
schema migration for those additions. Issue #84 owns Alert Center reads/UI and
recurring-pattern aggregation. Issue #95 adds the three fixed monetary budget
rules through the additive v2 contract below. Notifications, LLM-only
judgement, and automatic improvement/apply remain outside this interface.

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

Issue #95 adds these #80-owned versions without changing the table above:

| Contract | Additive version |
| --- | --- |
| normalized snapshot | `alert.snapshot.v2` |
| engine configuration | `alert.config.v2` |
| alert receipt | `alert.receipt.v2` |
| evaluation result | `alert.evaluation.v2` |
| suppression result | `alert.suppression.v2` |
| reserved receipt profile token | `sanitized-alert-receipt.v2` |
| canonical JSON | `alert.canonical-json.v2` |
| SQLite component | `schema_version(component='alert_engine', version=2)` |

`sanitized-alert-receipt.v2` identifies the closed v2 receipt profile only. It
is not an activated Issue #85 export carrier; sanitized export v1 continues to
exclude it.

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

## Evaluate-And-Append Application Boundary

`AlertEvaluationApplication` is the single production composition boundary for
engine execution. Its constructor receives one `AlertRuleRegistry`, one
`AlertEngineConfiguration`, one `IAlertEvidenceResolver`, and one existing
`IAlertEngineStore`. The registry, configuration, and resolver selection are
fixed for the lifetime of the application object; the configuration and its
collections are defensively frozen. `EvaluateAndAppend` accepts only one
caller-provided, already-normalized `AlertNormalizedSnapshot`. Source-specific
discovery, parsing, mapping, raw reads, background scheduling, and lifecycle
mutation remain outside this boundary.

For every call the application:

1. initializes/validates the existing engine store;
2. evaluates with `AlertEvaluationEngine` and the construction-time inputs;
3. appends the complete canonical evaluation through `IAlertEngineStore`; and
4. returns a bounded immutable typed outcome only after append returns the exact
   valid success pair.

The closed statuses are `success`, `initialization_busy`,
`initialization_unavailable`, `append_busy`, `append_unavailable`,
`append_conflict`, and `contract_rejected`. Success has no code and returns the
evaluation/input/configuration identities, ordered receipt IDs, typed
suppression facts, and typed rejected-match rule ID/version/code facts. The
derived identity counts are the lengths of those immutable collections. Every
non-success returns no identity or outcome. Valid contract failures preserve
only their bounded `AlertContractException.Code`; another nonfatal evaluation
exception is reduced to `alert_contract_rejected` without detail. Unknown/malformed store
status-code pairs and nonfatal store exceptions fail closed to the applicable
initialization/append unavailable status. Missing capability is a successful
appended evaluation with a suppression; unresolved evidence is a successful
appended evaluation with a rejected match. Neither is reclassified as a store
or contract failure. A byte-identical repeated evaluation remains the existing
idempotent store success, while a same identity with different stored bytes is
`append_conflict` and never success.

The public handoff is
`EvaluateAndAppend(AlertNormalizedSnapshot) -> AlertEvaluationApplicationResult`.
On success its `Outcome` is `AlertEvaluationOutcomeV1`; `Identity` is the
derived identity/count view of that same outcome. `Outcome.ReceiptIds`,
`Outcome.Suppressions`, and `Outcome.RejectedMatches` preserve engine order and
are read-only. They are null on every non-success through the enclosing result.

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

### Bounded trusted-store query interface

`IAlertEngineQueryStore` is additive to `IAlertEngineStore`; existing methods,
statuses, serializer behavior, and schema ownership do not change. Its page
limit is exactly `1..100`, and canonical bytes returned in one page total at
most 8,388,608 bytes. A page may therefore contain fewer than the requested
item limit without splitting or truncating a record. Invalid cursor, ID,
ordinal, or limit returns `invalid / invalid_alert_query`; unknown evaluation
suppression lookup returns `not_found / alert_not_found`; locked and
invalid/unreadable state return only `busy / alert_store_busy` or
`unavailable / alert_store_unavailable`. Successful results have a null code.
Any other status-code pair is invalid.

The three queries are:

- receipts after an optional canonical 64-lowercase-hex alert-ID cursor,
  ordered by `alert_id` ordinal ascending;
- evaluations after an optional canonical 64-lowercase-hex evaluation-ID
  cursor, ordered by `evaluation_id` ordinal ascending, projecting only the
  evaluation/input/configuration identities and non-negative receipt and
  suppression counts; and
- suppressions for one exact evaluation ID after an optional non-negative
  suppression ordinal, ordered by `suppression_ordinal` ascending, projecting
  the ordinal, evaluation/rule/code metadata, missing-capability tokens, and
  exact canonical suppression bytes.

The public method signatures are
`ListReceipts(string? afterAlertId, int limit)`,
`ListEvaluations(string? afterEvaluationId, int limit)`, and
`ListSuppressions(string evaluationId, long? afterSuppressionOrdinal, int limit)`.
Their result types are respectively `AlertReceiptQueryPage`,
`AlertEvaluationQueryPage`, and `AlertSuppressionQueryPage`.

Each method reads at most `limit + 1` rows to determine whether a next cursor
exists and returns at most `limit` items. The next cursor is the last returned
alert ID, evaluation ID, or suppression ordinal only when another row exists;
otherwise it is null. Empty receipt/evaluation pages are successful. A known
evaluation with no remaining suppressions returns an empty successful page.

`SqliteAlertEngineStore` implements both interfaces using only fixed,
parameterized statements over `alert_evaluations`, `alert_receipts`, and
`alert_suppressions`. Every evaluation is converted back to exact UTF-8 and
passed through `AlertEvaluationConsumerV1`, which strictly reconstructs its
receipts, suppressions, and rejected matches through the #80 authorities,
byte-compares the full value with `SerializeEvaluation`, and projects only
validated identity fields and child counts. SQLite scalar identity fields and
correlated receipt/suppression counts must exactly match that projection;
otherwise the whole page is unavailable. Configuration versions use the same
`^[a-z0-9][a-z0-9._-]{0,127}$` token authority as engine configuration and may
not begin with punctuation. Every receipt is converted back to exact UTF-8 and
passed through the shared strict authority used by
`AlertCenterReceiptConsumerV1`; a receipt query item carries those bytes and the
sealed fully typed Alert Center projection. Suppression JSON is
strict-reconstructed by the #80-owned
`AlertSuppressionConsumerV1.Validate(ReadOnlySpan<byte>)`, byte-compared with
`AlertCanonicalJson.SerializeSuppression`, and projected as evaluation ID,
rule ID/version, code, and a read-only canonical missing-capability list. Its
query item carries those bytes and sealed typed projection. All three consumers use
the same 8 MiB/no-leak rejection posture. One invalid row,
invalid/newer/broken schema,
decode or conversion failure makes the whole page unavailable with no items or
cursor. No query repairs a row, truncates a record, reads raw/content tables,
accepts SQL, or makes provenance/authentication claims. The fact that the
production SQLite implementation acquired bytes from its configured trusted
local database is separate from the consumer's internal-consistency proof.

## Optional Sanitized Export Profile

Issue #85 may include the exact canonical `alert.receipt.v1` bytes under profile
`sanitized-alert-receipt.v1`, plus evaluation/config/schema versions and hashes.
It must reject unknown receipt/profile versions and must not infer missing
receipt types. Issue #85 v1 intentionally excludes the lifecycle events owned
by Issue #83. Including lifecycle data requires a named future versioned export
profile; the v1 exporter must not infer the profile, rewrite the immutable
receipt, or include raw evidence.

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

`AlertCenterReceiptConsumerV1.Validate(ReadOnlySpan<byte>)` is a separate
additive #80-owned projection over the same strict parse, semantic validation,
canonical byte comparison, and derived-alert-ID authority. It does not add a
second parser or change `AlertReceiptConsumerV1.Validate` or its sealed
five-field envelope. It returns one sealed `AlertCenterReceiptProjectionV1`
with exactly the receipt fields #84 needs: alert/evaluation/rule identities,
severity and initial state, source/version, Session/optional trace, exact
evidence references, numeric observed values/effective thresholds,
configuration identity, required capabilities, completeness/reasons,
first/last observation, evaluation-input hash, and receipt summary. The
projection omits the constant v1 schema/profile fields and exposes every
collection read-only. It returns no raw JSON, source body, arbitrary provider
text, path, credential, PII, lifecycle state/history, repository/workspace
label, or provenance/authentication claim. Failure is the existing fixed
`invalid_alert_receipt` no-leak exception.

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
public receipt-consumer compatibility boundary. The Wave 3 repair additionally
proves fresh/identical/conflicting application execution, append failure and
contract-rejection mapping, appended missing/unresolved evidence outcomes,
receipt/evaluation/suppression pagination and page-byte bounds, owner-validated
fully typed receipt/evaluation/suppression projections, canonical-evaluation
tamper and child-count mismatch refusal, aggregate two-record byte-cap cursor
behavior, suppression-ordinal cursor behavior, invalid/newer schema no-leak
refusal, and unchanged v1 table inventory, existing five-field API, and golden hashes.

Handoffs:

- #81/#82 implement `IAlertRule` only and register descriptors/rules without
  changing v1 receipt, evidence, suppression, configuration, or evaluator
  contracts.
- #83 references immutable `alert_id`/evaluation metadata and adds separate
  lifecycle tables/events/API; it never adds lifecycle fields to the receipt.
- #84 constructs source-specific snapshots outside #80, invokes
  `AlertEvaluationApplication` explicitly, and consumes only
  `IAlertEngineQueryStore` pages after successful initialization. It uses the
  returned owner-validated typed receipt/suppression projections and evaluation
  metadata; it adds no receipt parser, direct SQL, client evaluation,
  background analysis, source/evidence inference, or provenance claim. Receipt
  ordering is alert-ID order, not chronology; #84 may deterministically
  group/sort only the bounded validated page data it has acquired and must not
  infer missing cross-page facts. Lifecycle state/history still comes only from
  #83, repository/workspace labels come only from their accepted sanitized
  authority, and rule formulas/descriptions come from the frozen registry.
- #85 consumes only `sanitized-alert-receipt.v1` canonical bytes and explicitly
  records unavailable future profiles.

## Additive estimated-cost alert v2

### Ownership and compatibility

Issue #80 owns additive `alert.snapshot.v2`, `alert.config.v2`,
`alert.receipt.v2`, `alert.evaluation.v2`, `sanitized-alert-receipt.v2`, and
`alert.canonical-json.v2`. Issue #95 owns only the three compiled cost rules and
the exact snapshot producer defined by the cost interface.

V2 exists because v1 has one Session/optional trace scope, numeric-only/default-
enabled configuration, and no typed estimate/catalog/registry/billing/coverage
carrier. A daily or period receipt cannot be represented by selecting a
representative Session or by recurring aggregation over per-Session receipts.
No synthetic Session is permitted.

Every v1 public type, canonical byte, ID domain, golden, serializer, consumer,
store method, query method, rule, and API remains byte/behavior compatible. V1
consumers reject every v2 contract/profile. V2 has distinct types, serializers,
consumers, and identity domains but uses overloads on the same
`AlertEvaluationEngine`, the same `AlertEvaluationApplication`, the same
`SqliteAlertEngineStore`, and the same Issue #83 lifecycle. It is not a second
evaluator/store/lifecycle.

### Normalized monetary snapshot

The public v2 logical tokens and canonical property order are fixed:

- acquisition: `complete | incomplete`;
- aggregate: `available | unrepresentable | not_applicable`;
- scope: `session | utc_day | rolling_period`;
- member: `estimated | partial | not_estimable | missing | failed |
  unavailable | stale`;
- evidence: `session | pricing_estimate`; and
- completeness: `full | partial`.

`AlertCostScopeV2` serializes `scope_id`, `kind`, `window_start_utc`,
`window_end_utc`, `session_ids`. `AlertCostMemberV2` serializes
`session_id`, `session_effective_at_utc`, `session_updated_at_utc`,
`source_surface`, `source_application_version`, `state`, `attempt_revision`,
`attempt_result_kind`, `attempt_result_code`, `head_revision`, `estimate_id`,
`estimate_calculation_time_utc`, `catalog_sha256`, `registry_version`,
`provider`, `model`, `billing_mode`, `amount`, `currency`.
`AlertEvidenceReferenceV2` serializes `kind`, `evidence_id`, `session_id`,
`observed_at_utc`.

`AlertNormalizedSnapshotV2` canonical JSON serializes exactly:

```text
schema_version
context_kind
source_surface
source_version
acquisition_state
acquisition_reasons
aggregate_state
eligibility_digest
eligible_count
eligible_lower_bound
scope
currency
amount
estimated_count
partial_count
not_estimable_count
missing_count
failed_count
unavailable_count
stale_count
coverage_numerator
coverage_denominator
coverage_basis_points
members
evidence
completeness
completeness_reasons
first_observed_at
last_observed_at
```

All nullable members are emitted explicitly as JSON `null`; absent property,
empty text, and numeric zero are never substitutes. UTC uses exact
seven-fraction `Z`, canonical decimal uses invariant `G29` JSON number form,
enum wire text is exactly the tokens above, and arrays retain only their
specified canonical order. `acquisition_reasons` and `completeness_reasons` are
closed to the single token `eligible_set_incomplete`. A complete snapshot has
`acquisition_reasons=[]`, `completeness=full`, and
`completeness_reasons=[]`; it has exact count equal to member count, null lower
bound, all seven member-state counts summing to that count, numerator equal to
estimated count, and denominator equal to eligible count. An incomplete
snapshot has `acquisition_reasons=["eligible_set_incomplete"]`,
`completeness=partial`,
`completeness_reasons=["eligible_set_incomplete"]`, null exact/state/coverage
counts, lower bound 2,001, no members/evidence, and null first/last observation.
No other reason, order, duplicate, or empty/partial combination is valid.

`AlertNormalizedSnapshotV2` has:

- exact schema and fixed context kind `estimated_cost`;
- source surface/version for the producing Local Monitor cost application;
- acquisition state `complete | incomplete`;
- aggregate state `available | unrepresentable | not_applicable`;
- exact eligibility digest and bounded eligible-count/lower-bound facts;
- one exact scope;
- one currency when acquisition is complete and at least one estimated member;
- canonical estimated-only amount only when aggregate state is `available`;
- coverage numerator, denominator, and nullable basis points;
- ordered exact member facts;
- ordered exact Session and pricing-estimate evidence references;
- alert-v2 acquisition completeness/reasons; and
- nullable first/last observed UTC times.

The scope has one kind (`session | utc_day | rolling_period`), a deterministic
scope ID, nullable UTC start/end, and zero to 2,000 unique canonical local
Session IDs accepted by the Session store in the same order as the member facts.
A `session` scope contains
one Session and both bounds are null. Aggregate scopes require an exact
half-open interval: UTC-day boundaries are midnight UTC, and a rolling period
is two to 366 complete UTC days ending at the caller's explicit midnight-UTC
cutoff. Window end must follow start. Scope ID is `cost-scope-` plus lowercase
SHA-256 of a length-framed `alert-cost-scope/v2` domain, kind, bounds,
eligibility digest, and ordered Session IDs.

Each member fact has exact Session ID and one fixed state
`estimated | partial | not_estimable | missing | failed | unavailable | stale`.
An estimated member has exact estimate ID, catalog SHA-256, registry version,
billing mode, and canonical amount equal to the strict Issue #94 record.
Other states keep any available exact estimate/catalog/registry/mode identity
but never invent an amount or zero. Every estimate ID is unique and belongs to
that exact Session. Canonical member order is exact Session-effective
observation time and then Session ID, both ascending.

Every member has non-null canonical Session/effective/update facts and the one
resolved source surface/application version. `attempt_revision` is zero only
for `missing` and otherwise is the positive highest contiguous terminal attempt
revision. `attempt_result_kind` is closed to
`estimate | unavailable | failed`; estimate has null code, unavailable has one
declared fixed unavailable code, and failed has one declared fixed recalculation
failure code. The remaining legal combinations are exactly:

| Member state | Attempt result | Head / estimate / calculation time / catalog | Registry / provider / model / billing mode | Amount / currency |
| --- | --- | --- | --- | --- |
| `estimated` | latest closed attempt shape; positive revision | all non-null; positive head revision; estimate is the exact active strict #94 `estimated` record | all non-null | exact canonical amount, including zero / `USD` |
| `partial` | latest closed attempt shape; positive revision | all non-null; positive head revision; estimate is the exact active strict #94 `partial` record | all non-null | exact canonical partial amount / `USD` |
| `not_estimable` | latest closed attempt shape; positive revision | all non-null; positive head revision; estimate is the exact active strict #94 `not-estimable` record | registry version nullable exactly when that record has no selected registry; provider/model/billing mode non-null | null / null |
| `missing` | revision `0`; result kind/code null | all null | all null | null / null |
| `failed` | positive revision; `failed` plus one fixed failure code | all null | all null | null / null |
| `unavailable` | positive revision; `unavailable` plus one declared fixed unavailable code | all null | all null | null / null |
| `stale` with active head | latest closed attempt shape; positive revision | all non-null and name the exact stale active strict #94 record | registry nullability follows that record; provider/model/billing mode non-null | null / null |
| `stale` without active head | positive revision; `failed` or `unavailable` with its matching fixed code | all null | all null | null / null |

For a head-backed row, `head_revision` and `estimate_id` are the exact active
head pair, and estimate calculation time/catalog/provenance equal that strict
record. A later failed or unavailable attempt may have a higher attempt
revision without replacing the head; in that case the attempt fields retain
that later outcome while the active-head status still selects
`estimated | partial | not_estimable`. When the latest attempt kind is
`estimate`, its estimate identity is the active head identity. `stale` always
suppresses member amount/currency even when its referenced immutable estimate
contains them. Any other null/non-null pairing, result-kind/code pairing,
status/amount pairing, or estimate/head/evidence mismatch is invalid canonical
input.

For a complete snapshot, the numerator equals the number of distinct
`estimated` members and the denominator equals all members. Basis points are
`floor(numerator * 10000 / denominator)` using checked integer arithmetic.
For a complete aggregate scope with denominator zero, basis points, currency,
and amount are null and the member/evidence collections are empty. A session
scope can never be empty. An estimated zero amount remains in the numerator.
Currency is present if and only if the estimated-member count is positive.
Aggregate state is `not_applicable` and amount is null when that count is zero.
With a positive count, aggregate state is `available` with the exact canonical
sum, or `unrepresentable` with a null amount when checked Issue #94 decimal
addition cannot represent the sum. Included-plan estimated zero therefore
carries its exact currency, `available`, and canonical `0`, while an
unknown-only set carries neither currency nor amount. All estimated members
must use the one snapshot currency, and that currency must be exact `USD`.
Any non-USD member or snapshot currency is invalid input rejected before rule
execution; it is not a suppression. Partial known amounts are not folded into
the aggregate.

An incomplete snapshot represents only acquisition overflow. It has an exact
aggregate window, lower-bound eligible count 2,001, aggregate state
`not_applicable`, null currency/amount/coverage, and no members or evidence. It
can produce only
`eligible_set_incomplete` after an applicable rule is present and enabled; an
absent/disabled rule still produces the earlier `rule_disabled`. It cannot
execute a monetary threshold.
Its eligibility digest binds the exact window, captured database/component
versions, lower-bound count, and ordered first 2,001 identity/update/head facts.
A complete digest binds the exact full ordered set and head facts. Empty and
incomplete snapshots have null first/last observed times; a nonempty complete
snapshot derives them from its first/last canonical member.

The digest is lowercase SHA-256 of a length-framed
`alert-cost-eligibility/v2` domain, scope kind/bounds, Session/pricing/
alert-engine component versions, source cost-configuration ID/head/catalog SHA,
complete count or overflow lower-bound 2,001, and each ordered fact:
Session ID/effective/update time, exact source surface/application version,
source-partition resolver digest, active head revision/estimate ID, and
Session-attempt revision/result kind/fixed code. Null/absent values are framed
distinctly from zero/empty. A complete
snapshot hashes all facts; overflow hashes exactly the first 2,001 facts. The
append transaction recomputes this same digest after proposed heads/attempts and
rejects any mismatch as stale before persisting an evaluation.

`AlertEvidenceReferenceV2` has the closed kind `session | pricing_estimate`,
one opaque evidence ID, the owning Session ID, and exact observation time.
There is exactly one `session` reference per member. Its evidence ID and owning
Session ID are that exact UUID and its observation time is the exact
Session-effective time. A member with an estimate identity additionally has
exactly one `pricing_estimate` reference whose evidence ID is that estimate ID,
whose owner is the same Session, and whose observation time is the estimate
calculation time. Canonical evidence order is kind, Session ID, evidence ID,
then observation time, where kind rank is fixed as `session=0` and
`pricing_estimate=1` rather than wire-text lexical order. Scope members, member
facts, and Session references must name the same ordered Session set. Every
present member estimate identity must have exactly one matching
pricing-estimate reference, and no such reference may exist without that member
identity. The v2 resolver validates both kinds through a trusted exact read
scope plus the narrowly typed pending-estimate overlay below.
Repository, workspace, path, time proximity, model, catalog hash alone, or
current-head lookup cannot create membership or resolve evidence.

The #95 `IAlertEvidenceResolverV2` implementation resolves `session` only by
the exact accepted local Session ID and requires exact Session-effective-time
equality. It resolves `pricing_estimate` only by exact estimate ID, exact
owning Session, and exact calculation-time equality after strict referenced
catalog and estimate consumer validation. It deliberately does not require the
estimate to be the current head because an immutable historical receipt can
reference a superseded exact estimate. An absent exact ID is `unresolved`;
an existing/pending ID with mismatched owner/time or malformed strict bytes is
`contract_rejected`; and read-view busy/unavailable is `store_failure`.
Repository/workspace/path/model/catalog alone, current/latest, or time
proximity never resolves evidence.

New #95 estimates are not yet store-visible during bounded preflight or the
transaction-time byte-equality evaluation. `AlertEvidenceResolutionScopeV2`
therefore contains:

- one exact existing-evidence read view, bound either to the preflight stable
  read transaction or to the caller's completion connection/transaction; and
- zero to 100 immutable `StrictPendingPricingEvidenceV2` values, each created
  only after strict #94 catalog/estimate consumer reload and carrying exact
  estimate ID, owning Session ID, calculation time, catalog SHA, canonical
  estimate SHA, run ID, and target ordinal.

Pending values are exactly the estimates scheduled for that same atomic
pricing append, in target-ordinal order, with unique estimate IDs and targets.
They contain no raw content, source path, quantity, rate, or arbitrary label.
For `pricing_estimate`, an exact pending ID is checked first: every reference
field must match or the result is `contract_rejected`; an exact match is
`resolved`. Only an ID absent from the pending set may query the existing exact
store. Session references always use the scoped exact Session read view.
Preflight and completion use the same immutable pending values; completion
binds existing reads to the supplied open SQLite connection/transaction. The
ordered resolution result for every reference must be byte-equivalent in both
phases. A changed existing fact is stale; different resolution with unchanged
scope is `alert_evaluation_failed`. After the equality gate, #95 inserts the
pending estimates before the alert participant append so every receipt FK is
store-resolvable in that same transaction. No general caller-supplied evidence
or permissive in-memory existence claim is admitted.

Bounds are 2,000 members, 4,000 evidence references, 2,000 estimate identities,
2,000 distinct catalog hashes/registry versions/billing modes, 8 MiB canonical
snapshot/evaluation/receipt each, and JSON depth 16. Duplicate/unknown fields,
unknown states, noncanonical decimals/timestamps/order, mismatched counts/
amounts/scope/evidence, malformed IDs, or unavailable exact evidence reject
before rule execution.

The exact public owner seams are:

```text
IAlertEvidenceResolverV2.Resolve(
  AlertEvidenceReferenceV2 reference,
  AlertEvidenceResolutionScopeV2 scope)
  -> resolved | unresolved | store_failure | contract_rejected

IAlertRuleV2.Descriptor -> AlertRuleDescriptorV2
IAlertRuleV2.Evaluate(AlertRuleContextV2 context) -> AlertRuleOutcomeV2

AlertEvaluationEngine.Evaluate(
  AlertRuleIdentityV2 selectedRule,
  AlertNormalizedSnapshotV2 snapshot,
  AlertEngineConfigurationV2 configuration,
  AlertEvidenceResolutionScopeV2 evidenceScope)
  -> success(AlertEvaluationResultV2)
   | unresolved_evidence
   | store_failure
   | contract_rejected

AlertEvaluationApplication.EvaluateAndAppend(
  AlertRuleIdentityV2 selectedRule,
  AlertNormalizedSnapshotV2 snapshot)
  -> AlertEvaluationApplicationResultV2
```

`AlertRuleOutcomeV2` has nullable severity and nullable suppression code:
severity only is a match, code only is a suppression, both null is no match,
and both non-null is invalid rule output. The application obtains the current
configuration/registry/evidence resolver through its existing injected owner
dependencies. The ordinary application constructs a no-pending scope bound to
its own stable store read; callers cannot supply a different store, resolver,
or pending candidate set per request. The #95 coordinator is the sole
authorized alternate scope constructor described above.
The engine resolves every ordered reference before rule execution. Any
`unresolved` result returns `unresolved_evidence`; any resolver
`contract_rejected` returns the same engine outcome; any resolver
`store_failure` returns `store_failure`. None creates an evaluation, rejection
row, receipt, or suppression, and expected resolver outcomes are never thrown.
The ordinary application preserves those typed failures. The #95 path maps all
three pre-append failures to fixed `alert_evaluation_failed` after applying its
phase/ordinal precedence; it never disguises store failure as absent evidence.

`AlertEvaluationApplication.EvaluateAndAppend` remains the ordinary
connection-owning production path: it evaluates and then invokes the
self-managed v2 store append. Issue #95 does not invoke that application method
inside its caller-owned pricing transaction. Its exact path is:

```text
preflightCandidate = AlertEvaluationEngine.Evaluate(
  selectedRule,
  preflightSnapshot,
  frozenConfiguration,
  preflightEvidenceScope)

transactionCandidate = AlertEvaluationEngine.Evaluate(
  selectedRule,
  transactionSnapshot,
  frozenConfiguration,
  transactionEvidenceScope)

RequireCanonicalByteEquality(
  preflightCandidate,
  transactionCandidate)

appendResult = ISqliteAlertEngineTransactionParticipantV2.AppendEvaluation(
  openConnection,
  activeTransaction,
  transactionCandidate)
```

The #95 unit of work obtains the same frozen registry, configuration, and
trusted candidate-aware evidence resolver as the ordinary application
composition. For each
requested scope it evaluates once during bounded preflight, uses those exact
candidate bytes for the response-size gate, then rebuilds the snapshot from
revalidated state and evaluates once again inside the completion transaction.
Every snapshot/evaluation/receipt/suppression byte must equal its preflight
candidate before append. Changed authoritative facts use the applicable stale
failure; unequal output from byte-equal inputs is `alert_evaluation_failed`.
It then appends the strict pending pricing rows/heads before the alert rows.
Only the transaction candidate is passed to the participant. #95 never calls
the ordinary application/self-managed-store append. This is the only alternate
composition path and exists solely to preserve the one pricing-plus-alert
transaction.

### Explicit v2 configuration and registry

`AlertEngineConfigurationV2` contains schema/configuration versions, exact
source cost-configuration ID, source configuration-head revision, source
configuration catalog SHA-256, and zero or one entry for each registered v2
rule. The source catalog is the configuration-time trusted catalog and is
distinct from each member estimate's historical catalog SHA. Unlike v1, an
unlisted v2 rule is disabled. No placeholder entry is synthesized for it. Each
present `AlertBudgetRuleConfigurationV2`, including an explicitly disabled
entry, freezes:

- exact rule ID plus separate version `1`, and enabled state;
- `USD`;
- warning and critical canonical decimal thresholds;
- minimum coverage basis points;
- exact scope kind; and
- period days when the scope is `rolling_period`.

Its canonical JSON property order is exactly `schema_version`,
`configuration_version`, `source_cost_configuration_id`,
`source_configuration_head_revision`,
`source_configuration_catalog_sha256`, `rules`. Each present rule orders
`rule_id`, `rule_version`, `enabled`, `currency`, `warning_threshold`,
`critical_threshold`, `minimum_coverage_basis_points`, `scope_kind`,
`window_days`; null window days is emitted explicitly. Configuration hash is
lowercase SHA-256 of a length-framed `alert-configuration/v2` domain and those
exact bytes.

`schema_version` is exact `alert.config.v2` and `configuration_version` is the
fixed token `cost.configuration.v1`. The latter identifies the one accepted
source configuration contract; it is not copied from caller text, derived from
the configuration ID, or varied by revision. The evaluation, suppression, and
receipt copy that exact fixed value.

Thresholds are non-negative and warning cannot exceed critical. Session/day
rules reject period days. The period rule requires `2..366`. Configuration
canonicalization orders only present entries in the fixed three-rule registry
order. Missing and explicitly disabled are distinct canonical configurations
but both evaluate to `rule_disabled` when that rule is selected. The hash and
evaluation identity therefore distinguish source configuration/head/catalog,
absence, explicit disabled state, currency, threshold, coverage, and window
changes. There is no permissive default, placeholder, or source override.

The producer source surface is fixed
`local-monitor-cost-analytics` and its source version is fixed `1`.
Alert-v2 acquisition completeness is a separate closed contract, not an
extension of the Issue #61 source-capability reason registry. It is `full` for
a complete eligible-set acquisition, including a
complete empty set. It is `partial` with the sole reason
`eligible_set_incomplete` for acquisition overflow. Pricing coverage/member
status does not alter source completeness; it is represented only by the
coverage and member fields.

`AlertRuleRegistryV2` contains exactly the Issue #95 descriptors/rules:

- `session-estimated-cost-threshold` version `1`, title
  `Estimated Session cost threshold`, description
  `Compares one Session's estimated USD amount with configured warning and critical thresholds.`,
  formula `estimated USD amount >= configured threshold`, scope
  `session`, and evaluation-window label `session`;
- `daily-estimated-cost-threshold` version `1`, title
  `Estimated daily cost threshold`, description
  `Compares estimated USD amount across one UTC calendar day with configured warning and critical thresholds.`,
  the same formula, scope `utc_day`, and evaluation-window label `utc_day`; and
- `period-estimated-cost-threshold` version `1`, title
  `Estimated rolling-period cost threshold`, description
  `Compares estimated USD amount across one configured rolling period with warning and critical thresholds.`,
  the same formula, scope `rolling_period`, and evaluation-window label
  `rolling_period`.

`AlertRuleDescriptorV2` has exact property order `rule_id`, `rule_version`,
`title`, `description`, `formula`, `scope_kind`, `evaluation_window`. Every
value above is immutable bounded repository-safe registered code metadata.
Descriptor identity is exact rule ID/version; the registry never falls back to
another version.

Their scopes and window tokens must match their configuration. Arbitrary
scripts, dynamic expressions, currency conversion, notifications, quality
logic, or model recommendations are prohibited.

The caller names exactly one registered rule identity with the snapshot. The
other two rules produce no outcome for that evaluation. The selected rule
validates scope even when its entry is absent or disabled, so a misrouted
snapshot can produce `scope_not_applicable` and an absent/disabled entry can
produce `rule_disabled`. Registry metadata supplies the expected scope kind.
For a present period entry, the requested/snapshot window length must equal its
`window_days`; an absent period entry uses the explicit requested window only
to construct the scope and does not synthesize configuration. That rule
produces at most one suppression, receipt, or no-match outcome with this exact
precedence:

1. `scope_not_applicable`;
2. `rule_disabled`;
3. `eligible_set_incomplete`;
4. `no_eligible_sessions`;
5. `no_covered_estimate`;
6. `aggregate_amount_not_representable`;
7. `insufficient_estimate_coverage`;
8. inclusive critical/warning comparison, otherwise no match.

Thus, after the rule is applicable, present, and enabled, a complete empty
scope produces only `no_eligible_sessions` and an incomplete scope produces
only `eligible_set_incomplete`. One input cannot accumulate several reasons
whose order would change canonical evaluation bytes.

### Evaluation, suppression, and receipt

The existing `AlertEvaluationEngine` v2 overload validates/canonicalizes the
selected rule identity, snapshot, and complete configuration, computes
domain-separated v2 hashes, then applies the single precedence chain above.
Unknown/unregistered rule identity is invalid input, not a suppression.

`input_hash` is lowercase SHA-256 of a length-framed `alert-input/v2` domain,
selected rule ID/version, exact canonical snapshot bytes, and exact canonical
configuration bytes. `evaluation_id` is lowercase SHA-256 of a length-framed
`alert-evaluation/v2` domain, input hash, configuration hash, and selected rule
identity. It is therefore known before a receipt is constructed.

Canonical `alert.evaluation.v2` property order is exactly:

```text
schema_version
evaluation_id
input_hash
configuration_version
configuration_hash
selected_rule_id
selected_rule_version
source_cost_configuration_id
source_configuration_head_revision
source_configuration_catalog_sha256
scope_kind
scope_id
scope_start_utc
scope_end_utc
eligibility_digest
currency
aggregate_state
eligible_count
estimated_count
partial_count
not_estimable_count
missing_count
failed_count
unavailable_count
stale_count
coverage_basis_points
first_observed_at
last_observed_at
receipts
suppressions
rejected_matches
```

`schema_version` is exact `alert.evaluation.v2`.
The bounded context fields are copied from the validated configuration and
snapshot before rule execution and are present for match, suppression, and
no-match outcomes. Their nullability is exactly the normalized-snapshot
contract above. A stored no-match evaluation therefore remains sufficient for
the version-aware evaluation projection without reconstructing pricing state.
Receipts/suppressions contain at most one total selected-rule outcome; rejected
matches is the explicit empty array in v2.

A canonical v2 suppression uses
`schema_version=alert.suppression.v2` and orders:

```text
schema_version
evaluation_id
rule_id
rule_version
code
source_cost_configuration_id
source_configuration_head_revision
source_configuration_catalog_sha256
configuration_version
configuration_hash
scope_kind
scope_id
scope_start_utc
scope_end_utc
eligibility_digest
currency
aggregate_state
eligible_count
estimated_count
partial_count
not_estimable_count
missing_count
failed_count
unavailable_count
stale_count
coverage_basis_points
first_observed_at
last_observed_at
```

This exact bounded context is why #84 can present a suppressed budget
evaluation without reading pricing tables or reconstructing the snapshot.

Closed suppressions are:

- `rule_disabled`;
- `scope_not_applicable`;
- `no_eligible_sessions`;
- `eligible_set_incomplete`;
- `no_covered_estimate`;
- `aggregate_amount_not_representable`;
- `insufficient_estimate_coverage`.

No alert is emitted for a disabled/mismatched rule, zero denominator, zero
estimated members, coverage below the configured minimum, or an aggregate that
cannot be represented exactly by the Issue #94 decimal boundary. Non-USD input
is contract-invalid and produces no evaluation, receipt, or suppression.
`no_covered_estimate` is evaluated before any zero threshold comparison, so an
unknown-only set cannot become a zero-cost match.
`aggregate_amount_not_representable` preserves no wrapped, rounded, partial, or
clamped amount. A match compares only the estimated-only amount with configured
thresholds: `amount >= critical` emits critical; otherwise
`amount >= warning` emits warning; otherwise it emits no receipt and invents no
suppression. Equality is inclusive and warning must not exceed critical. It
does not count missing/partial/not-estimable as zero.

An immutable `alert.receipt.v2` contains:

- schema/profile, deterministic alert/evaluation IDs, rule/version, severity,
  and initial `open`;
- source surface/version and exact scope;
- exact Session and pricing-estimate evidence;
- observed amount/coverage/count values and effective thresholds;
- exact currency;
- ordered member estimate/catalog/registry/billing/status facts;
- source cost-configuration ID/head/catalog SHA plus alert configuration
  version/hash;
- completeness/reasons and first/last observation;
- evaluation input hash; and
- registered repository-safe summary.

Its canonical property order is exactly:

```text
schema_version
sanitized_export_profile
alert_id
evaluation_id
rule_id
rule_version
severity
initial_state
source_surface
source_version
scope
evidence
currency
aggregate_state
observed_amount
warning_threshold
critical_threshold
eligible_count
estimated_count
partial_count
not_estimable_count
missing_count
failed_count
unavailable_count
stale_count
coverage_numerator
coverage_denominator
coverage_basis_points
members
source_cost_configuration_id
source_configuration_head_revision
source_configuration_catalog_sha256
configuration_version
configuration_hash
completeness
completeness_reasons
first_observed_at
last_observed_at
input_hash
summary
```

`sanitized_export_profile` is the receipt profile token
`sanitized-alert-receipt.v2`, not authorization for #85 export. The alert
identity projection omits only `alert_id`; `alert_id` is lowercase SHA-256 of a
length-framed `alert-receipt/v2` domain and those projection bytes. The final
serializer inserts the derived ID in the position above and consumers remove
only that property, recompute, and byte-compare. Summary comes from the fixed
rule/severity/scope template registry and contains no amount, user label, or
arbitrary text.

It contains no raw content, tool arguments/results, source/file body,
credential, account/contract/invoice identifier, PII, path/URI, arbitrary
provider text, exception text, or model-generated prose. Identity uses distinct
length-framed `alert-evaluation/v2` and `alert-receipt/v2` domains. Same
snapshot/config/registry produces byte-equivalent output; any configuration,
scope, member, estimate, catalog, registry, mode, status, coverage, currency, or
amount change changes the input/receipt identity.

`AlertReceiptConsumerV2`, `AlertEvaluationConsumerV2`, and
`AlertCenterReceiptConsumerV2` are the only byte consumers. They enforce the
closed shape/bounds/semantics, exact canonical byte comparison, derived
identities, defensive copies, and fixed no-leak `invalid_alert_*` errors.
Consumer validation proves internal consistency, not source authenticity;
trusted local acquisition and exact cost-store resolution remain separate.

### Engine schema v2 and queries

The existing `alert_engine` component migrates from version 1 to version 2.
The same three tables remain the only engine-owned tables. Their v2 definitions
permit only the paired v1 or v2 evaluation/receipt schema versions; v1 rows are
copied byte-for-byte with the same scalar identities and ordinals. Suppressions
remain attached to their exact evaluation. No v1 row is reserialized.

Migration first validates the exact v1 schema and every v1 row through the v1
consumer. It then disables foreign-key enforcement on the connection before
starting one immediate transaction, creates fixed-name temporary replacement
tables, copies the scalar columns and canonical TEXT/BLOB bytes without
reserialization, recreates the exact indexes/triggers, swaps the three tables,
and runs `foreign_key_check`. Version 2 is written last and the transaction is
committed only after exact row-count/byte checks. Foreign-key enforcement is
restored in a `finally` path and verified. Any invalid v1 row, unexpected owned
object, failed copy, lifecycle-parent mismatch, or future/broken schema rolls
back without mutation. The existing lifecycle foreign key continues to
reference `alert_receipts`.

The live `alert_receipts` parent is never renamed in a way that rewrites the
child lifecycle FK to a temporary table. Replacement order/copy uses the exact
owner migration primitive that leaves the lifecycle DDL target text
`REFERENCES alert_receipts(alert_id)` unchanged. Commit validates that exact
target in addition to `foreign_key_check`, row counts, scalar identities, and
canonical bytes.

`IAlertEngineStore` v1 methods continue to append/read v1. The additive v2 store
interface appends/reads v2 through the same `SqliteAlertEngineStore`.
Insert-or-byte-identical remains idempotent; same identity/different bytes is
conflict across both versions. Existing v1 query methods enumerate exact v1
rows only. A new version-aware bounded query enumerates owner-validated sealed
v1/v2 projections in alert-ID order with the existing 1..100 and aggregate
8 MiB page bounds. It never passes v2 bytes to a v1 consumer or silently drops
an invalid row.

The additive persistence-only
`ISqliteAlertEngineTransactionParticipantV2.AppendEvaluation` operation accepts
an already-open `SqliteConnection`, its active `SqliteTransaction`, and one
fully validated v2 evaluation. It runs the exact same append validator and SQL
as the ordinary v2 store and never creates, commits, rolls back, retries,
disposes, or replaces the caller transaction/connection.

`AlertEngineTransactionAppendResultV2` is a closed typed union:

- `success` carries the exact evaluation ID, ordered receipt IDs, and ordered
  suppression evaluation-ID/ordinal identities;
- `conflict` carries no identities and means the deterministic identity already
  exists with different canonical bytes;
- `busy` carries no identities and means SQLite reported busy/locked;
- `unavailable` carries no identities and covers invalid/unreadable schema or
  rows and any other nonfatal store failure;
- `invalid_transaction` carries no identities and means the connection is
  closed, the transaction is inactive, or `transaction.Connection` is not the
  supplied connection; and
- `contract_rejected` carries no identities and means the supplied result
  fails the same strict v2 validator used by the ordinary append.

A byte-identical replay is `success`. Connection/transaction validation happens
before any SQL. The participant translates nonfatal SQLite/validation
exceptions into that union and never exposes exception text. The #95 unit of
work treats every non-success as failure of its alert-store phase, rolls back
the whole pricing-plus-alert transaction once, and maps it to the fixed
`alert_store_failed` recalculation code; it does not retry, switch to the
ordinary append, or commit pricing-only state. Engine contract failure before
the participant is the distinct alert-evaluation phase and fixed
`alert_evaluation_failed` recalculation code. #95 is the sole owner/caller of
the combined unit of work; this seam does not create a second alert store or a
public transaction API.

The version-aware query also exposes sealed evaluation/suppression pages in
evaluation-ID/suppression-ordinal order with the same `1..100` and 8 MiB page
bounds. A v2 evaluation projection contains only evaluation/rule/configuration
identities, source cost-configuration ID/head/catalog SHA, scope kind/ID/bounds,
eligibility digest, optional USD currency,
eligible/estimated/partial/not-estimable/missing/failed/unavailable/stale counts,
nullable coverage basis points, aggregate state, and first/last observed time.
A v2 suppression projection adds the one fixed suppression code. It returns no
snapshot members, canonical bytes, provider/model labels, source references,
paths, PII, raw values, or arbitrary text. These projections let #84 report a
coverage-suppressed cost run even when no receipt exists.

The exact additive query interface is:

```text
IAlertEngineVersionedQueryStore.ListReceiptsVersioned(
  after_alert_id: string?, limit: int)
  -> AlertVersionedReceiptQueryPage

IAlertEngineVersionedQueryStore.ListEvaluationsVersioned(
  after_evaluation_id: string?, limit: int)
  -> AlertVersionedEvaluationQueryPage

IAlertEngineVersionedQueryStore.ListSuppressionsVersioned(
  evaluation_id: string,
  after_suppression_ordinal: long?,
  limit: int)
  -> AlertVersionedSuppressionQueryPage
```

Each page has exact store state
`success | invalid | not_found | busy | unavailable`, a nullable fixed code,
ordered items, nullable next owner cursor, exhausted boolean, and
canonical-byte count. `success` has null code. Invalid cursor, evaluation ID,
suppression ordinal, or limit returns
`invalid / invalid_alert_query`. A well-formed unknown evaluation ID is
`not_found / alert_not_found` only for `ListSuppressionsVersioned`; receipt and
evaluation enumeration never use `not_found`. Busy and unreadable/invalid store
state return `busy / alert_store_busy` and
`unavailable / alert_store_unavailable`. Every non-success has empty items,
null cursor, `exhausted=false`, and zero canonical-byte count. Limit is
`1..100`; one successful page is at most 8,388,608 canonical bytes. Owner
cursors remain exact raw alert/evaluation IDs or suppression ordinals and are
distinct from the public Alert Center snapshot cursor. A receipt item has
`contract_version=v1 | v2`, exact canonical bytes, and exactly one non-null
sealed `receipt_v1 | receipt_v2` projection. Evaluation and suppression items
use the equivalent closed union. Unknown/mixed/tampered rows fail the page
`unavailable`; they are never skipped.

`AlertCenterReceiptProjectionV2` exposes read-only alert/evaluation/rule/
severity/state/source identities, exact scope and eligibility digest, ordered
evidence/members, currency/aggregate state/amount/thresholds, eligible and
seven state counts, coverage numerator/denominator/basis points, cost
configuration ID/head/catalog SHA, alert configuration version/hash,
completeness/reasons/times/input hash/summary. Its property order follows that
list and it returns defensive immutable collections.

Every existing v1 append/read/query filters the parent evaluation and receipt
scalar schema to v1 even when the component row is version 2. Receipt children
must pair v1 evaluation/v1 receipt or v2 evaluation/v2 receipt; mixed-version
graphs are invalid. Because suppression rows have no independent schema scalar,
query dispatch first validates/reads the parent evaluation version and then
uses that version's suppression consumer. V2 identities never leak through a
v1 cursor/result.

Issue #83 accepts the valid engine v2 parent while keeping lifecycle v1. Issue
#84 v1 route consumes the v1-only query, while its v2 route consumes the
version-aware query. Issue #85 recognizes a valid engine v2 database but
explicitly selects exact receipt-v1 rows for its frozen bundle v1; v2 rows and
pricing bytes are never export candidates.

### Required v2 proofs

Tests must cover:

- every v1 golden/hash/API/query byte unchanged and every v1 consumer rejecting
  v2;
- canonical v2 bytes/IDs, order, bounds, duplicate/unknown/tamper/future
  refusal, fixed errors, and defensive immutability;
- exact multi-Session evidence and estimate ownership with heuristic negatives;
- three rules disabled by default, warning/critical, wrong scope, non-USD input
  rejection,
  zero denominator, insufficient coverage, partial/not-estimable/missing
  denominator, and included estimated zero;
- schema-v1-to-v2 migration, v1 row byte preservation, lifecycle foreign-key
  continuity, rollback, restart, future/broken schema, pagination, and mixed
  v1/v2 rows;
- Issue #83 mutation against a v2 receipt without lifecycle schema/API changes;
- Issue #84 version-aware read/navigation and v1 route compatibility; and
- Issue #85 exact v1-only export from v1-only, v2-only, and mixed stores.
