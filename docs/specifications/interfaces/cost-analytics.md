# Cost Analytics And Budget Alert Interface

## Scope and authority

Issue #95 owns the Local Monitor persistence, application, API, and UI that
consume the exact Issue #94 pricing contracts. It also owns three monetary
budget-rule implementations. The versioned alert carrier/evaluator/store
extension belongs to Issue #80, lifecycle compatibility belongs to Issue #83,
Alert Center compatibility belongs to Issue #84, sanitized-export
compatibility belongs to Issue #85, and runtime-backup compatibility belongs to
Issue #88.

This interface does not authorize runtime pricing fetch, provider billing API
access, credentials, invoice reconciliation, currency conversion, private
contract inference, automatic model switching, purchase/quota actions, quality
claims, or effect verdicts.

## Fixed v1 contracts

| Contract | Version |
| --- | --- |
| configuration | `cost.configuration.v1` |
| configuration read | `cost.configuration-read.v1` |
| configuration preview request | `cost.configuration-preview-request.v1` |
| configuration preview | `cost.configuration-preview.v1` |
| configuration commit request | `cost.configuration-commit.v1` |
| configuration commit result | `cost.configuration-commit-result.v1` |
| immutable configuration version | `cost.configuration-version.v1` |
| safe catalog projection | `cost.catalog.v1` |
| safe catalog cursor | `cost.catalog.cursor.v1` |
| recalculation request | `cost.recalculation-request.v1` |
| recalculation result | `cost.recalculation.v1` |
| Session recalculation history | `cost.session-recalculations.v1` |
| Session estimate view | `cost.session-estimates.v1` |
| exact Session estimate view | `cost.session-estimate.v1` |
| analytics view | `cost.analytics.v1` |
| analytics cursor | `cost.analytics.cursor.v1` |
| API error | `cost.error.v1` |
| canonical configuration JSON | `cost.canonical-json.v1` |
| SQLite component | `schema_version(component='pricing', version=1)` |

Unknown versions, fields, enum values, duplicate JSON properties, alternate
number/timestamp spellings, and over-bound inputs fail closed. Issue #94
`pricing.catalog-snapshot.v1`, `pricing.estimate-request.v1`, and
`pricing.estimate.v1` remain unchanged.

## Trusted catalog and source-adapter boundary

The application obtains a catalog only from an injected trusted local
`IPricingCatalogProvider`. The default provider loads the embedded reviewed
Issue #94 bundled registry plus zero to eight optional local-override registry
documents supplied in caller order by repeated Local Monitor startup option
`--pricing-registry-override <absolute-file>`, and returns:

- the frozen `PricingCatalog`;
- its exact canonical `pricing.catalog-snapshot.v1` bytes; and
- the exact SHA-256 already exposed by that catalog.

Each override path must be a fully qualified native local-filesystem path.
Relative, home-relative, current-drive, drive-relative, UNC/network,
device/extended-device, volume-GUID, alternate-data-stream, traversal, and
foreign lexical forms are rejected. Every ancestor and the final target must
be non-symlink/non-reparse; the final target is one regular file. The loader
opens no-follow and captures native file identity from the same handle before
and after the bounded read. Windows opens without delete sharing; Unix uses
descriptor-relative `openat`/`O_NOFOLLOW` plus `fstat` identity and keeps the
descriptor open through validation. Both require identity/type/length
stability, so a path rename/swap cannot change the bytes consumed. A platform
that cannot provide those native guarantees rejects the override option with
the fixed startup error; it does not fall back to path-only inspection. Each strict UTF-8
document is bounded to 1 MiB plus one sentinel byte, must reload through
`PricingRegistryLoader`, must declare `source_kind=local_override`, and must
pass the frozen #94 catalog/supersession rules when appended after the bundled
document. Duplicate paths/source IDs, more than eight files, a nonregular
target, decoding/shape failure, or a resulting catalog above the #94
64-document/4-MiB bounds fails startup with fixed
`pricing_catalog_unavailable`; no error/log contains the rejected path. Files
are read once before host construction;
later file changes have no effect until restart. No file watcher, network
fetch, environment-secret interpolation, credential field, or permissive
fallback exists.

After the Issue #88 monitor initialization has atomically ensured
`runtime_backup` v1 followed by `pricing` v1, the Local Monitor keeps that same
non-waiting restore lease and runs one pricing-owner immediate transaction
before host construction/readiness. That transaction strict-reloads and
insert-or-identical persists the provider's exact canonical catalog snapshot
only after its `Catalog`, canonical bytes, and SHA-256 identity agree,
deletes only expired configuration previews, closes every nonterminal
recalculation as `recalculation_interrupted` in the required keyset order, and
strictly validates the resulting pricing rows before its sole commit. Catalog
identity mismatch, row corruption, busy/unavailable storage, or failed
recovery rolls back the complete pricing-owner transaction and fails startup
with fixed path-free `pricing_store_unavailable`. The existing provider/input
failure remains the separate fixed `pricing_catalog_unavailable`.

No public HTTP route accepts a catalog, registry document, local-override
bytes, estimate bytes, catalog path, provider credential, invoice, or private
contract value. The private path and source file bytes never enter an API,
HTML, application-produced log/diagnostic, exception, SQLite scalar, or
repository-safe evidence. The startup argument necessarily exists in the
operating system's process arguments; the application never copies it into its
own diagnostics or public projections. The exact canonical catalog snapshot is
private database content.
The UI displays only strict catalog projections: bundled/local-override source
kind, repository-safe source label, registry version, effective interval,
stale-after/review date, currency, and opaque source/entry identity. It can
distinguish and preview modes against a local override but cannot upload,
fetch, edit, or reveal its path in v1.

An `IPricingEstimateSourceAdapter` is the only production authority that may
acquire positive Issue #94 source facts for an exact local Session. It does not
construct the final request or choose billing mode/pricing route. Its result
contains either:

- one immutable fact set with exact Session UUID/time, source surface/version,
  provider, exact model, quantities and five-field quantity provenance,
  completeness/reasons, and fixed adapter capability identity; or
- one fixed unavailable reason and no request.

The cost application selects the sole configuration source entry by exact
source surface/application version, then requires the adapter capability
version and provider to equal that entry. The entry supplies the explicit
billing mode and pricing route. The application encodes both values through
the frozen five-field `PricingValueProvenance` shape exactly as:

| Field | Exact value |
| --- | --- |
| `source_adapter` | `local-monitor-cost-configuration` |
| `source_version_or_schema_fingerprint` | `cost.configuration.v1` |
| `source_event_or_trace_span_id` | the exact configuration ID plus `.source-entry-` and the zero-padded three-digit canonical entry ordinal (`000..031`) |
| `capture_content_state` | `not_captured` |
| `normalization_version` | `cost-configuration-provenance.v1` |

The source-entry ordinal is zero-based after canonical source-entry sorting and
is rendered as exactly three decimal digits. The composed
`source_event_or_trace_span_id` is the only place where configuration ID and
source-entry ordinal are encoded. `capture_content_state` retains its frozen
security meaning and is never repurposed as an ordinal carrier.

The application supplies that same tuple independently as
`billing_mode_provenance` and `pricing_route_provenance`; all other request and
quantity provenance remains adapter-authored. It copies neither billing value
from a model label or registry. It then constructs the Issue #94 request from
that one frozen fact/configuration pair.
Before construction, source surface, source version, and every field of every
adapter-authored `PricingValueProvenance` must satisfy the frozen #94 safe-token
grammar `^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$`. An exact source/application
version that cannot be represented, including a value containing `+`, is
`source_mapping_unavailable`; it is never trimmed, normalized, case-folded,
hashed, or substituted.
Missing, ambiguous, or provider-mismatched entries are unavailable. The adapter
must obtain Session time, provider, exact model, and every quantity from
reviewed source/version mappings with the five-field Issue #94 provenance tuple.
It may not:

- sum Session-run or monitor trace/span rollups without a reviewed
  non-double-counting mapping;
- infer provider or billing route from a model name;
- substitute `total_tokens` for a missing category;
- turn a missing cache/reasoning/request quantity into zero;
- use current time for missing Session time;
- bind by repository, workspace, path, model, or timestamp proximity; or
- combine different model/provider/billing partitions into one estimate.

Adapter source resolution is all-agree and non-aggregating in v1. The reviewed
capability mapping first enumerates the complete bounded set of price-relevant
records that are exactly owned by the requested Session. All records used for
Session time, provider, and model must agree ordinally on the one exact
surface/application-version/provider/model partition. Each quantity category
must have exactly one authoritative record with its own five-field provenance,
or one source-native pre-aggregated record whose mapping explicitly guarantees
non-overlap; the adapter never sums multiple events/spans itself. Zero records,
two candidate records for one category, conflicting identities, a second
provider/model partition, or an unbounded/incomplete enumeration returns
`source_mapping_unavailable` and no #94 request. It never chooses first, last,
nearest, largest, or latest. This also means a multi-model Session is
unavailable in v1 rather than silently collapsed into one model.

Configuration preview, commit, recalculation capture, analytics, and budget
eligibility all use the same
`ICostSessionSourcePartitionResolverV1`; no layer reads a convenient single
event. For one exact Session it enumerates these closed observation kinds and
ranks:

1. rank 0 `session_run`: every `session_runs` row whose exact `session_id` FK
   names the Session, ordered by ordinal `run_id`;
2. rank 1 `session_event`: every `session_events` row whose exact `session_id`
   FK names the Session, ordered by ordinal `event_id`; and
3. rank 2 `source_schema_observation`: each #61 row selected by an exact
   `monitor_spans.raw_record_id` whose span trace is owned by one or more exact
   run/event rows and every such owner row names that same Session, ordered by
   numeric `raw_record_id` then ordinal `observation_id`.

A trace that is owned by more than one Session is not an exact binding and
makes the result `incomplete`. Repository/workspace/time proximity is never an
input. The digest identity projection for `session_run` is, in order,
`run_id`, `session_id`, nullable `trace_id`, `source_surface`, and `status`.
For `session_event` it is `event_id`, `session_id`, nullable `run_id`, nullable
`trace_id`, `source_surface`, nullable `source_application_version`,
`source_adapter`, `source_event_id`, and `occurred_at`. For
`source_schema_observation` it is `observation_id`, `raw_record_id`,
`source_surface`, `source_application_version`, nullable `source_adapter`,
nullable `adapter_version`, nullable `schema_fingerprint`, and `observed_at`.
These exact persisted fields are the update identity; no timestamp or current
row version is synthesized.

Every observation kind contributes its non-null source surface, and every
surface must be present. Session run/event surfaces are mapped to the #61
namespace only by this closed table:

| Session persisted surface | Exact #61 surface |
| --- | --- |
| `vscode` | `github-copilot-vscode` |
| `copilot-cli` | `github-copilot-cli` |
| `claude-code` | `claude-code` |

`copilot-sdk` and `hook-unknown` have no current #61 manifest mapping and are
`incomplete`; the similarly named First-Trace Doctor surface is not #61
authority. No prefix, adapter, model, or fallback mapping exists. #61 raw
observations are already in the right-hand namespace and are never remapped.
All mapped/raw surfaces must then agree
ordinally. The digest includes both the original persisted surface and nullable
mapped surface, and the resolved tuple returns only the exact #61 surface. A
run never contributes an
application version because `session_runs` has no such column. Every #61 raw
observation must have an application version; a Session event contributes its
version when present. At least one event or raw observation must supply a
version, and all supplied versions must agree. A null event version is allowed
only when another exact version-bearing event or #61 observation supplies the
one agreed version. Missing required surface, missing #61 version, ambiguous
trace ownership, or no version-bearing observation is `incomplete`; two
distinct surfaces or two distinct supplied versions is `mixed`.

The resolver reads one sentinel and at most 256 observations: zero is
`missing`, more than 256 is `incomplete`, and only the all-agree rules above
produce `resolved`. A repeated physical row with the same kind and identity is
`incomplete`; identical source facts under distinct persisted identities are
retained in the ordered sequence and never collapsed. The resolver returns the
closed state, nullable resolved tuple, observation count, and lowercase SHA-256
of length-framed `cost-session-source-partition/v1` plus every ordered rank,
kind, exact identity projection, surface, and nullable application version.
Preview selection and budget membership admit only `resolved`; other states
do not match and no public projection infers why. The exact resolver digest is
included in selection/eligibility digests and rechecked in the completion
transaction, so no producer can choose first/latest or disagree across those
surfaces.

Resolver state precedence is fixed. Read/store failure is an owning-store
failure outside this value union. Otherwise: (1) more than 256 rows is
`incomplete`; (2) zero rows is `missing`; (3) any malformed identity,
duplicate, ambiguous ownership, missing required surface/version, or unmapped
surface is `incomplete`; (4) only after those checks, two or more distinct
mapped surfaces or supplied versions is `mixed`; and (5) the sole complete
all-agree tuple is `resolved`. A Session satisfying both an incomplete and
mixed symptom is therefore `incomplete`.

At Issue #95 kickoff, the accepted Issue #61 manifests do not grant complete
positive pricing authority for GitHub Copilot, Claude Code, or Codex App.
Therefore the default adapter returns a fixed unavailable state. Synthetic
repository-safe adapters may exercise positive paths in automated validation,
but they are not provider-live evidence. Codex App remains
`not-estimable / subscription_or_contract_unknown` inside the #94 domain only
when an authorized exact Codex Session fact set reaches that engine. The Issue
#92 Desktop NO-GO and absent Issue #93 adapter mean the #95 default adapter
cannot currently produce that fact set, so a real Codex Session is
`unavailable / codex_adapter_unavailable` with no #94 record. Capability text
must not present the domain's hypothetical negative record as an observed
Session estimate.

## Immutable configuration

One configuration contains:

- schema/configuration versions and deterministic configuration ID;
- optional exact predecessor configuration ID;
- exact trusted catalog SHA-256;
- ordered source entries with exact source surface, application version,
  adapter/capability version, explicit provider, billing mode, and pricing
  route;
- ordered budget entries; and
- canonical creation time supplied by the application clock.

There are at most 32 source entries and three budget entries. IDs/tokens are
1..128 ASCII characters in the closed lowercase token grammars inherited from
#94/#61; application/adapter versions are 1..64 printable repository-safe
characters. Duplicate source surface/application-version tuples, duplicate rule
identities, unsafe labels, and over-bound collections are invalid. Provider and
adapter/capability version are identity-bearing validations on that sole tuple,
not alternate match candidates.

Source entries do not authorize an otherwise unavailable source adapter. They
only select an explicit billing mode/route after that adapter has supplied
authorized provider/model/quantity facts.

The three closed budget rule identities are:

- `rule_id=session-estimated-cost-threshold`, `rule_version=1`;
- `rule_id=daily-estimated-cost-threshold`, `rule_version=1`; and
- `rule_id=period-estimated-cost-threshold`, `rule_version=1`.

Every budget entry contains explicit:

- enabled state;
- currency (`USD` in v1);
- warning and critical threshold in canonical decimal text;
- minimum coverage basis points in `0..10000`; and
- window kind. Session uses `session`, daily uses UTC calendar `utc_day`, and
  period uses `rolling_period` with an explicit `window_days` in `2..366`.

There is no implicit default budget. An absent configuration or absent/disabled
entry means the rule is disabled and emits a versioned `rule_disabled`
suppression only when an explicit evaluation is requested. Merely registering
the three rules never enables them.

Configuration JSON is canonical UTF-8 without BOM or trailing newline, at
depth at most 16. Duplicate/unknown properties, noncanonical decimals or
timestamps, invalid UTF-16, and noncanonical entry order are rejected. The
preview request schema is `cost.configuration-preview-request.v1` with
properties in this exact order:

1. `schema_version`;
2. `source_entries`; and
3. `budget_entries`.

Canonical source entries are sorted by source surface then application version
and have exact property order `source_surface`, `application_version`,
`adapter_capability_version`, `provider`, `billing_mode`, `pricing_route`.
Canonical budget entries are sorted by the fixed three-rule registry order and
have exact property order `rule_id`, `rule_version`, `enabled`, `currency`,
`warning_threshold`, `critical_threshold`, `minimum_coverage_basis_points`,
`scope_kind`, `window_days`. Decimal text uses the frozen #94 invariant
canonical form. `window_days` is null except for the period rule. An absent
budget entry is absent from the array; a present disabled entry still carries
the complete validated shape. Absence and explicit disable are different
identity-bearing configurations, but both disable evaluation.

The configuration identity projection has properties in the exact order
`schema_version`, `predecessor_configuration_id`, `catalog_sha256`,
`source_entries`, `budget_entries`, `created_at_utc`. It deliberately omits
`configuration_id`. The final `cost.configuration.v1` canonical object has
`schema_version`, `configuration_id`, followed by those same remaining
properties in that order. Configuration ID is `cost-configuration-` plus the
lowercase SHA-256 of the length-framed `cost-configuration/v1` domain and the
exact identity-projection bytes. The consumer removes only
`configuration_id`, reserializes the projection, recomputes the ID, then
requires byte equality with the exact final serialization. This removes any
circular identity definition.

`CostConfigurationConsumerV1` is the sole strict reload seam for canonical
configuration bytes. It accepts one 1..1,048,576-byte read-only memory,
requires strict UTF-8/canonical `cost.configuration.v1`, revalidates every
closed field/count/order, configuration ID, catalog SHA, and canonical digest,
and returns an immutable defensive-copy value or the closed
`invalid | unsupported | too_large` result. It never returns partial values,
caller-owned buffers, parser/exception text, paths, or source bytes.
`CostRecalculationRequestConsumerV1` applies the same contract to one
1..1,048,576-byte canonical `cost.recalculation-request.v1`, including exact
request digest, caller order, counts, ID/key grammar, and configuration/catalog
references. Runtime backup/restore and the live store call these named seams;
neither reimplements permissive parsing.
`CostConfigurationCommitConsumerV1` likewise reloads a bounded canonical
`cost.configuration-commit.v1` request and its
`cost.configuration-commit-result.v1` result, revalidates every echoed preview
field and cross-document configuration/head/catalog equality, and returns only
immutable defensive copies or the same closed rejection union.
`CostConfigurationPreviewConsumerV1` strictly reloads the complete canonical
`cost.configuration-preview.v1` response, recomputes its preview digest, and
validates every configuration/head/catalog/selection/count projection.

`POST /api/costs/v1/configuration/preview` validates and freezes the proposed
configuration, binds it to the exact current configuration head, samples
`created_at` exactly once from the application clock, and returns the normalized
complete proposal including that timestamp, its canonical configuration
ID/digest, captured head, exact trusted catalog SHA, a selection digest over the
ordered affected Session identity/update facts, and bounded affected-Session
counts. In one immediate transaction it stores only a bounded transient preview
receipt containing those exact canonical response bytes, scalar projections,
and expiry exactly 15 minutes after `created_at`; it does not append a catalog
snapshot, configuration, head, or commit. Before insertion the owner deletes
expired previews by exact expiry/digest order. At most 32 unexpired receipts may
exist; a new distinct preview at capacity returns
`409 cost_preview_capacity_reached` without mutation. Same digest plus
byte-identical strict content is idempotent, while same digest plus different
content is conflict. Startup and commit admission perform the same expired-row
cleanup before readiness/use.
The preview identity projection is the canonical response fields in their
declared order through `overlap_count_state`, omitting only
`preview_digest`. Preview digest is lowercase SHA-256 of the length-framed
`cost-configuration-preview/v1` domain and those exact projection bytes. The
final response appends `preview_digest`; the strict consumer removes only that
field, recomputes it, and requires exact final byte equality. There is no
circular digest.
`POST /api/costs/v1/configurations` must echo that exact normalized proposal,
`created_at`, expected head, catalog SHA, selection digest, and preview digest.
After strict request-byte validation and the durable successful-replay lookup
below, a new commit looks up one unexpired stored preview, strictly reloads its
response, and requires every one of the eleven echoed response fields to be
byte-equivalent. Missing, expired, or nonidentical receipt is
`cost_stale_preview`; however, an existing owner-persisted receipt whose
canonical bytes/scalar projections fail the strict preview consumer is
`503 cost_store_unavailable`, never blamed on the client. A client-computed
digest without a stored receipt has no authority. The server then recomputes the current selection without sampling
another time, reloads the provider's exact catalog bytes through
`PricingCatalogSnapshotConsumer`, inserts that snapshot with
insert-or-identical semantics, and appends the referencing configuration plus
head entry plus an immutable commit receipt containing the exact canonical
commit request/result bytes, and deletes that consumed preview in one
`BEGIN IMMEDIATE` transaction. A stale
head/catalog/selection, changed
timestamp/proposal, or digest mismatch is a conflict. Existing configurations
are never updated or deleted. `GET /api/costs/v1/configuration` returns the
current configuration/head, that configuration's exact persisted catalog SHA,
the provider's immutable startup catalog SHA, and
`catalog_state=unconfigured | matching | changed`. Preview always binds the
provider catalog current for that process. A changed provider catalog requires
a new preview/commit even when billing/budget entries are otherwise identical;
the new configuration gets a new identity and the old catalog/configuration
remains readable history.

Commit is retry-safe after an indeterminate transport result. After strict
request/configuration byte validation, but before preview-receipt,
provider-catalog, current-head, or selection validation, the store looks up the immutable commit
receipt at head revision `expected_head_revision + 1`. If its stored request
SHA and canonical request bytes are byte-identical to the submitted request and
its exact head/configuration/catalog/predecessor cross-checks remain valid, the
store returns the stored canonical `cost.configuration-commit-result.v1` bytes
with the original `201`/`Location`. This exact replay succeeds even
when a later head now exists or the process's provider catalog has changed; it
does not recompute selection, sample time, append, or make the old commit
current again. If revision `expected_head_revision + 1` or its receipt exists
but the request bytes/digest or any cross-checked fact differs, the result is
`cost_idempotency_conflict`; a head without its required receipt is
store-unavailable corruption. Only when that
immutable success is absent do provider-catalog, current-head, and selection
CAS checks run, so an uncommitted old preview is stale.

Commit conflict mapping and precedence are closed. Strict JSON/schema/
canonical-form/field-shape failure is the applicable 400 code. The immutable
successor replay check above then returns either the original success or
`cost_idempotency_conflict` for an occupied-but-different successor. For a new
append, an internally inconsistent/absent/expired stored preview receipt,
submitted configuration ID, selection/preview digest, normalized proposal,
comparison count, or preview-bound timestamp is `cost_stale_preview`; a
strict-invalid persisted preview is `cost_store_unavailable`. Provider catalog
mismatch is then `cost_catalog_changed`; otherwise an
expected current-head revision/configuration mismatch is `cost_stale_head`;
otherwise a recomputed proposed selection mismatch is
`cost_selection_changed`. Those checks run in that order. No later condition
relabels an earlier one, and a store busy/unavailable result remains its 503
code.

The preview response schema is `cost.configuration-preview.v1` with exact
property order `schema_version`, `configuration`, `expected_head_revision`,
`expected_configuration_id`, `catalog_sha256`, `selection_digest`,
`proposed_match_count`, `current_match_count`, `current_match_count_state`,
`overlap_count`, `overlap_count_state`, `preview_digest`. Head revision is zero and expected
configuration ID is null when no head exists. The commit request schema is
`cost.configuration-commit.v1` with `schema_version` followed by those same
eleven response fields in the same order; every field after `schema_version` must
be byte-equivalent to preview output. The commit response
`cost.configuration-commit-result.v1` orders `schema_version`,
`configuration_id`, `head_revision`, `catalog_sha256`. The configuration read
`cost.configuration-read.v1` orders `schema_version`, `head_revision`,
`configuration_id`, `configuration_catalog_sha256`,
`provider_catalog_sha256`, `catalog_state`, `configuration`,
`selected_session_count`, `selected_session_count_state`; its configuration, ID, and configuration-catalog SHA
are null and its head is zero before the first commit. The provider catalog is
present after successful startup. Public consumers enforce the same canonical
forms and closed shapes as persistence consumers.
Configuration-read selection uses the same 2,001-row acquisition and
`selected_session_count_state=exact | lower_bound`; it never labels 2,001 as an
exact total.

`GET /api/costs/v1/configurations/{configuration_id}` is the immutable commit
Location and returns `cost.configuration-version.v1` ordered
`schema_version`, `head_revision`, `configuration_id`, `catalog_sha256`,
`committed_at_utc`, `configuration`. It resolves the exact configuration ID and
its one head/commit receipt; it never substitutes the current head. A
well-formed absent ID is `cost_configuration_not_found`. Initial and replayed
commit responses both use this exact route in `Location`, so a later head cannot
change the referenced representation.

`GET /api/costs/v1/catalog?after=<cursor>&limit=<1..100>` exposes only the
safe startup-catalog projection needed before any estimate exists. Its
`cost.catalog.v1` response orders `schema_version`, `catalog_sha256`, `sources`,
`entries`, `next_after`. At most 64 sources order by canonical document order
and each orders `source_kind`, `source_id`, `source_label`,
`registry_version`, `last_reviewed_date`, `stale_after_date`. Entries order by
canonical document/entry order and each orders `source_kind`, `source_id`,
`source_label`, `registry_version`, `entry_key`, `supersedes_entry_key`,
`selection_state`, `superseded_by_entry_key`, `provider`, `model`,
`billing_mode`, `pricing_route`, `effective_from_utc`, `effective_to_utc`,
`last_reviewed_date`, `stale_after_date`, `currency`,
`included_zero_incremental_cost`, `source_reference`. Nullable effective end
and reference are explicit. `selection_state` is `active | superseded`;
`superseded_by_entry_key` is null exactly for active and otherwise names the
unique later catalog entry whose `supersedes_entry_key` names this entry.
These reverse edges are owner-derived from the strict #94 chain, never inferred
from overlap/order in the UI. Reference is inert bundled HTTPS text and null
for local override. No rate, quantity, multiplier, alias, limitation, document
bytes, local path, or private source locator is projected.

Default catalog limit is 50. `next_after` is null or
`cost-catalog-cursor-v1.` plus unpadded base64url canonical
`cost.catalog.cursor.v1` JSON ordered `schema_version`, `catalog_sha256`,
`entry_key`. It is 1..512 ASCII characters. On continuation, malformed cursor
or limit is `cost_invalid_cursor`; a cursor catalog SHA different from the
current immutable startup catalog is then `cost_catalog_changed`; only under
the same SHA is a missing/nonmember exact entry key `cost_invalid_cursor`.
Cursor-excluded percent-encoded query bytes are capped at 7,000, so every
server cursor is resubmittable within the 8,192-byte query cap. Each response is
at most 8 MiB and one entry that cannot fit is
`cost_response_too_large`, never truncation. All IDs/labels/models pass
their #94 bounded safe-token/free-form output guards before serialization; a
guard failure makes the catalog projection store-unavailable. This route, not
canonical catalog bytes or estimate history, is the configuration UI's sole
catalog/mode data source.

Preview's commit-bound selection is the proposed set of terminal
(`completed | failed`) Sessions whose source resolver is `resolved` and whose
resolved source-surface/application-version tuple matches a proposed source
entry. It is ordered by `sessions.last_seen_at` then Session ID and records
Session ID, status, last-seen/update times, source-partition state/count/digest,
exact resolved source tuple, active estimate-head revision, and attempt
revision. Selection digest is
lowercase SHA-256 of those length-framed
facts under `cost-configuration-selection/v1`. Preview reads one extra proposed
row; more than 2,000 proposed matches returns `cost_request_too_large` and no
digest/commit token. Commit recomputes that full proposed set in its immediate
transaction, so it is a stale/CAS gate.

The current configuration is acquired separately only for UX comparison. It
reads at most 2,001 current matches. At most 2,000 uses
`current_match_count_state=exact`; overflow uses `lower_bound` and count 2,001.
Overlap is exact only when current acquisition is exact; otherwise it is a
lower bound over the acquired prefix. These bounded comparison counts are not
recomputed as commit CAS inputs. They are nevertheless bound by the stored
canonical preview bytes and preview digest, so commit must echo them exactly.
A proposal with zero or at most 2,000 matches therefore remains committable even
when the old configuration matches more than 2,000 Sessions; broad historical
state cannot permanently lock out a safe narrowing/disable operation.

## Recalculation and exact history

The canonical `cost.recalculation-request.v1` object has exactly these
properties in order:

1. `schema_version`;
2. `configuration_id`;
3. `expected_head_revision`;
4. `catalog_sha256`;
5. `session_ids`;
6. `budget_scopes`; and
7. `idempotency_key`.

`expected_head_revision` is the positive active revision named by
`configuration_id`. `session_ids` contains one to 100 unique accepted local
Session IDs in caller order. `budget_scopes` contains zero to eight unique
objects in caller order, using exactly one of these closed shapes and property
orders:

- Session: `scope_kind`, `session_id`;
- UTC day: `scope_kind`, `utc_date`;
- rolling period: `scope_kind`, `cutoff_utc`, `window_days`.

`scope_kind` is respectively `session`, `utc_day`, or `rolling_period`.
`utc_date` is exact `YYYY-MM-DD`. `cutoff_utc` is midnight UTC serialized with
exactly seven fractional digits and `Z`; `window_days` is an explicit integer
in `2..366`. When the period rule entry is present, the request value must equal
that entry even when it is disabled. When it is absent, the request value still
defines the requested half-open scope so the exact rule can return
`rule_disabled`; it does not create an implicit configuration entry. No scope
object carries source values, pricing bytes, thresholds, or an inferred
window.

After strict request syntax, canonical-byte, digest, and idempotency-key
validation, recalculation start first looks up that key before any provider
catalog, Session, head, source, or budget-preflight check. Same key plus the
byte-identical canonical request and digest returns the original run and its
current projection without revalidating dynamic state. Same key plus any
different byte or digest is `cost_idempotency_conflict`. Only a previously
unseen key continues through the following admission checks.

For a new run, the immediate admission transaction first requires the current
configuration head to equal both request `expected_head_revision` and
`configuration_id`. A historical or concurrently superseded head returns
`409 cost_stale_head` before catalog, overlap, budget, Session, or root checks.
The request catalog SHA must equal both the named persisted configuration
catalog and the provider's current immutable startup catalog. If the provider
catalog changed, start returns `409 cost_catalog_changed` before creating a run;
it never pairs an old configuration ID with new catalog bytes or silently uses
the persisted old catalog for a current recalculation. The user first previews
and commits a new configuration, after which old estimates/catalogs remain
historical read-only records and delta provenance.
Thus new-run conflict precedence after idempotent replay is
`cost_stale_head`, then `cost_catalog_changed`, then
`cost_recalculation_in_progress`, followed by bounded budget/Session admission.

The idempotency key is 16..128 ASCII characters in
`[A-Za-z0-9][A-Za-z0-9._-]*`. Request digest is lowercase SHA-256 over the
length-framed `cost-recalculation-request/v1` domain and the exact canonical
request bytes above. An intentional retry after failure uses a new key and new
run so the earlier failed run remains visible.

After exact idempotent replay lookup and before creating a different run, the
same immediate admission transaction checks whether any requested Session is a
target of a `requested` or `running` run. One overlap rejects the complete new
request as `409 cost_recalculation_in_progress`; it creates no second root,
attempt reservation, cancellation, or merge. Disjoint runs may proceed
concurrently. Once the prior run is terminal, a new key/request may target that
Session. Root/target insertion and this active-ownership check are serialized
by the database transaction; timestamp, repository, or in-memory worker state
is not used.

Budget admission is two-stage. Before an adapter call or any database mutation,
preflight checks at most eight evaluations, 4,000 aggregate member occurrences,
and 8,000 aggregate evidence references from the requested scopes and their
worst-case reference multiplicities. A cardinality/reference overrun is HTTP
`413 cost_request_too_large`; no run is created.

After the requested/running run exists and adapters plus the post-head eligible
set are known, the application serializes the exact candidate v2 snapshots,
evaluations, and receipts/suppressions in memory. Their checked combined
canonical byte length must be at most 16,777,216 before the shared completion
transaction starts. Overrun terminalizes the existing run as
`failed / budget_payload_too_large`, writes the matching failed target/attempt
rows, and writes no estimate/head/alert/budget-result row. It is observed from
the existing `202` polling resource, never remapped to a late `413`. No scope is
truncated or partially committed.

The application resolves every Session by its exact accepted local Session ID
and captures each exact active estimate head and attempt revision before
starting. Missing Sessions reject the complete request. A target Session whose
persisted status is not exactly `completed | failed` rejects the request as
`409 cost_session_not_eligible` before root creation. A Session budget scope
also requires its named Session to be in the exact resolved configured
eligibility set; an existing but ineligible Session produces that same 409 and
no run. It captures one exact application-clock `calculation_time_utc` for the
run; immutable `created_at_utc` is that same value, not a second clock sample.
The run stores it with the ordered targets, captured heads/attempt revisions,
budget scopes, and request fingerprint. Run creation commits one immutable
root, ordered targets, and sequence-0 `requested`. Worker acquisition appends
sequence-1 `running` in a separate immediate transaction before asking any
source adapter, so polling can genuinely observe `requested`.
An unavailable target remains an explicit target result; it is never silently
dropped or represented by a zero estimate.

Target result kind is exactly `estimate | unavailable | failed`.
`estimate` carries one strict #94 status and no failure code. `unavailable`
carries exactly one of:

- `source_mapping_unavailable`;
- `source_adapter_unavailable`; or
- `codex_adapter_unavailable`.

`failed` carries exactly one of:

- `source_adapter_failed`;
- `invalid_estimate_source`;
- `pricing_estimation_failed`;
- `budget_payload_too_large`;
- `stale_recalculation_input`;
- `stale_active_estimate`;
- `pricing_store_failed`;
- `alert_evaluation_failed`;
- `alert_store_failed`; or
- `recalculation_interrupted`.

Unavailable is an expected absence of authority and does not fail the run.
Thrown adapter/provider text, consumer rejection, stale state, arithmetic/
contract rejection, and store failure are failed and never remapped to
unavailable. The complete winning-failure order and persisted ledger projection
are fixed:

| Phase rank | `failure_phase` | Code rank and `failure_code` | `failure_ordinal_kind` |
| --- | --- | --- | --- |
| 0 | `head_input` | 0 `stale_recalculation_input`; 1 `stale_active_estimate` | `target` |
| 1 | `adapter` | 0 `source_adapter_failed` | `target` |
| 2 | `estimate_validation` | 0 `invalid_estimate_source`; 1 `pricing_estimation_failed` | `target` |
| 3 | `budget_payload` | 0 `budget_payload_too_large` | `scope` |
| 4 | `pricing_store` | 0 `pricing_store_failed` | `target` |
| 5 | `alert_evaluation` | 0 `alert_evaluation_failed` | `scope` |
| 6 | `alert_store` | 0 `alert_store_failed` | `scope` |
| 7 | `recovery` | 0 `recalculation_interrupted` | SQL null |

The lowest phase rank wins, then the lowest target/scope ordinal, then code
rank. Budget-payload ordinal is the first caller scope whose cumulative
canonical bytes cross the cap. Pricing-store ordinal is the lowest intended
target affected by the failed append. Recovery stores both ordinal columns
null. Every other failure stores the exact `target | scope` token and a
bounded ordinal. Changed Session status/update/source resolver state/count/
digest/resolved tuple, configuration, or catalog facts are
`stale_recalculation_input`; only a changed estimate-head or attempt-revision
CAS is `stale_active_estimate`. Recovery always uses
`recalculation_interrupted`. Polling exposes only these fixed codes.

For an available target, the application invokes the Issue #94 engine with the
trusted catalog. Before commit it serializes the estimate canonically and
strictly reloads:

1. the exact catalog bytes through `PricingCatalogSnapshotConsumer`; and
2. the exact estimate bytes through `PricingEstimateConsumer` using that
   reloaded catalog.

The reloaded estimate's complete embedded Issue #94 request must be
byte/field-equivalent to the application-generated request: Session/effective/
calculation time, predecessor, provider, model, billing mode, pricing route,
every quantity and five-field provenance tuple, and completeness/reasons.
Billing-mode and pricing-route provenance must equal the exact configuration/
source-entry tuple above. The frozen #94 request has no separate configuration
or catalog-selection field; none may be invented. Separately, the estimate
envelope's `catalog_sha256` must equal the selected configuration catalog, and
the strict consumer must receive those exact persisted catalog bytes.
Session/time/predecessor must therefore equal the requested Session UUID, persisted
`sessions.last_seen_at`, run calculation time, and captured active head. The
predecessor is null only for an initial estimate. Any mismatch is fixed
`invalid_estimate_source`; a changed head is `stale_active_estimate`. Neither
creates a branch nor retries against a newer head.

A successful completion atomically appends every validated #94 estimate, one
target result and one next Session-attempt revision per Session, a `succeeded`
event, and the next explicit head
revision for every target whose adapter produced a strict-valid estimate,
including `estimated`, `partial`, and `not-estimable`. Adapter-unavailable/
not-calculated targets remain in results and coverage but do not acquire a
head. A failed completion atomically appends one sequence-2 `failed` event
carrying the single winning fixed failure code and exactly one closed result/
next Session-attempt revision per target. An exact unavailable outcome
established before the selected failure phase remains unavailable; no validated
estimate survives rollback, and every other target is failed with the winning
code. Attempts exactly mirror those final target results. No estimate head
changes. A retry is a new run, so the earlier failure stays visible.

Event kind is only `requested | running | succeeded | failed`;
`recalculation_interrupted` is a `failure_code`, never an event kind. Startup
recovery runs after migrations and before HTTP readiness, keyset-ordering
nonterminal runs by `(calculation_time_utc,run_id)`. Requested-only appends
sequence 1 `failed/recalculation_interrupted`; requested+running appends
sequence 2 with that failure. Every target becomes failed with that code and
gets its next attempt revision; recovery never resumes adapter/alert work. A
nonterminal run that already has any result, attempt, estimate/head, budget, or
alert artifact is corruption and fails readiness without mutation.

Completion runs under one `BEGIN IMMEDIATE` transaction shared by the pricing
store and existing alert-engine store. It re-reads the configuration/catalog/
current active configuration-head revision/ID,
Session status/update/source resolver state/count/digest/resolved tuple,
active-head revision/identity, attempt revision, and eligibility facts,
constructs the post-head snapshot for every requested scope, and invokes the
pure evaluator with a transaction-bound
`AlertEvidenceResolutionScopeV2`. That scope reuses the immutable
strict-consumer-validated pending-estimate set from preflight and resolves
existing Session/estimate evidence through this same connection/transaction.
Every canonical snapshot/evaluation/receipt/
suppression byte sequence must equal the pre-transaction candidate used for the
16-MiB gate. A changed captured fact is the applicable stale failure; unequal
output with equal inputs is `alert_evaluation_failed`. Only then does it append
every pending estimate/result/head first, then the byte-equal v2
evaluations/receipts/suppressions through the participant, and finally one exact
`pricing_recalculation_budget_results` row per scope atomically. A pricing or
alert failure
rolls back every head/receipt write; a separate immediate transaction appends
the terminal failure event, complete target results, and Session-attempt
revisions described above without changing any head. No result can ambiguously
claim pricing success while its required alert phase is unknown.
If that separate terminal write also fails, the run remains nonterminal and
the API reports only fixed store failure; the undurable diagnostic is not
claimed as persisted. Startup recovery later closes it as
`recalculation_interrupted`.

The shared-transaction seam is exact. #95 calls the pure
`AlertEvaluationEngine.Evaluate` operation for each canonical v2 snapshot
during bounded preflight with a stable-read
`AlertEvidenceResolutionScopeV2`, then repeats it in the completion transaction
with a transaction-bound scope containing the identical
`StrictPendingPricingEvidenceV2` values. Ordered evidence resolutions and
canonical outputs are byte-equal gates; it
does not call the connection-owning `AlertEvaluationApplication.EvaluateAndAppend`
path. #80 adds the persistence-only
`ISqliteAlertEngineTransactionParticipantV2.AppendEvaluation` operation on the
existing `SqliteAlertEngineStore`. It accepts the already-open
`Microsoft.Data.Sqlite.SqliteConnection`, its active
`SqliteTransaction`, and one fully validated `AlertEvaluationResultV2`; it
performs the same insert-or-byte-identical v2 append and returns this closed
union: `success` with the typed evaluation/receipt/suppression identities;
`conflict`; `busy`; `unavailable`; `contract_rejected`; or
`invalid_transaction`. Expected outcomes
are returned, never thrown. Conflict/busy/unavailable map to the winning
`alert_store_failed` phase after rollback; `contract_rejected` maps to
`alert_store_failed` because the pure engine output already passed the
`alert_evaluation` phase. Invalid transaction is a programming
contract failure that also rolls back and is exposed only as that same fixed
failure code. Unexpected exceptions are caught at the unit-of-work boundary and
receive the same no-detail mapping. It must verify that
`transaction.Connection` is that connection, must not open another connection,
and must not begin, commit, roll back, retry, or dispose the caller's
transaction. The ordinary v1/v2 store methods retain their existing
self-managed transaction behavior.

#95 owns `SqliteCostRecalculationUnitOfWork`, the sole caller of that
transaction-participating operation. It opens one connection, enables and
verifies foreign keys, begins one immediate transaction, executes pricing
appends and each #80 append through the same transaction, appends the budget
result links, then commits exactly once. Any exception/non-success rolls back
exactly once and is translated to the fixed winning failure phase; the
separate failure-ledger transaction starts only after rollback. No public API
exposes the SQLite seam, and neither owner duplicates the other's canonical
validator.

The current estimate is the highest validated contiguous head revision, not
the greatest calculation timestamp or a "latest" query. Recalculation never
updates an earlier estimate. Delta is computed only from the exact predecessor:

- subtract amounts only when both are non-null and currencies are identical;
- never convert partial/not-estimable/missing to zero;
- retain changes in registry version, billing mode, source kind, coverage, and
  reasons; and
- show no quality or effect conclusion.

Member state first follows the exact active #94 head:
`estimated -> estimated`, `partial -> partial`, and
`not-estimable -> not_estimable`. A later failed/unavailable recalculation is
separate history and never replaces a valid head. With no head, the highest
contiguous `pricing_session_attempts.attempt_revision`—not a timestamp—
distinguishes
`missing` (never calculated), `unavailable` (adapter returned a fixed unavailable
reason), and `failed` (terminal target/run failure).

The current projection and every historical item apply an exact item-specific
stale gate independently. An estimate-backed item is `stale` when its captured
Session status/effective/update facts or source resolver state/count/digest/
resolved tuple differ from the current exact Session/resolver result, or when
its source/application-version/provider/billing-mode/pricing-route/adapter
capability partition no longer equals the sole current configuration entry for
that Session, or when the current exact catalog/configuration produces a
different relevant pricing-selection semantic signature for the item's strict
original #94 request facts. That signature is lowercase SHA-256 of
length-framed `cost-pricing-selection-semantic/v1` plus status, nullable
amount/currency, ordered component category/state/amount/missing reason, ordered
reasons, and selected registry source/version/entry/effective interval. It
excludes estimate ID, predecessor, calculation time, whole-catalog SHA, and
unselected catalog entries. The server obtains it only by strict #94 evaluation
against the exact original request facts and the applicable current catalog;
it never substitutes current quantities or matches by model/time proximity.
Thus a superseding price/registry outcome makes the old item stale while a
budget-only or semantically unrelated catalog/configuration change does not.
Estimate-backed historical evaluation uses that item's own captured target,
configuration, catalog, and attempt facts; it never inherits the active item's
state. A configuration change limited to budget policy or an unrelated source
entry does not make it stale. Historical catalog/registry versions remain exact
provenance and do not become current by substitution; a mixed registry set is
displayed rather than silently replaced. A stale record remains immutable
history but contributes no monetary amount. It remains in the coverage
denominator only while the Session is otherwise still in the exact terminal/
configured-source eligible set, until an explicit recalculation advances the
head.

A requested/running or terminal unavailable/failed attempt with no estimate
uses `cost-attempt-input-freshness/v1`, never the #94 pricing-selection semantic
signature. The server strictly reloads the attempt's run, target, and persisted
run configuration. It compares the target's captured Session
status/effective/update and resolver state/count/digest/resolved tuple to the
current exact facts, then derives a closed source-selection comparison state
`not_applicable | absent | present` for both the persisted run configuration and
the current active configuration. A non-resolved captured resolver tuple is
`not_applicable` and requires no source entry. A resolved tuple with no matching
entry is `absent`. A resolved tuple with a sole matching entry is `present` and
compares exact source surface, application version, adapter capability version,
provider, billing mode, and pricing route. Matching states remain fresh;
`present` additionally requires all six fields equal. A state change, ambiguity,
or unequal present entry is stale. Thus a valid `source_mapping_unavailable`
attempt with a still-unresolved tuple or still-absent mapping does not become
stale immediately. A provider-catalog change, unrelated catalog entry, or
budget-only configuration change does not make a terminal no-estimate attempt
stale because no model-backed catalog selection or #94 request exists. No
model, catalog-entry identity, quantity,
amount, component, estimate status, or pricing-selection signature is invented
for such an item. A requested/running projection additionally requires its
captured configuration ID/head revision/catalog SHA to remain the current
active head and provider catalog; otherwise it is stale because completion
would fail its head/catalog CAS. The input-freshness digest is lowercase
SHA-256 of length-framed `cost-attempt-input-freshness/v1`, the exact persisted
run/target/configuration selection facts, the current comparison facts, and the
two exact source-selection states, present-entry fields when applicable, and
the result token/code or requested/running state. Public `freshness` is the result
of this gate for no-estimate attempts and the estimate-backed gate above for
estimate attempts.

## Pricing SQLite component v1

The component is additive to the shared Local Monitor database and owns only:

- `pricing_catalog_snapshots`;
- `pricing_configuration_previews`;
- `pricing_configurations`;
- `pricing_configuration_heads`;
- `pricing_configuration_commits`;
- `pricing_recalculation_runs`;
- `pricing_recalculation_targets`;
- `pricing_recalculation_events`;
- `pricing_recalculation_target_results`;
- `pricing_recalculation_budget_results`;
- `pricing_session_attempts`;
- `pricing_estimates`; and
- `pricing_estimate_heads`;

plus the exact component-owned indexes and triggers below. Durable business
history is append-only. The sole exception is owner-only deletion of expired or
successfully consumed rows from transient `pricing_configuration_previews`;
those rows can never be updated/replaced and are not business history. The
store has no other repair, downgrade, delete, overwrite, or "latest by time"
API.

### Canonical schema authority

`Persistence.Sqlite.PricingSchemaV1` is the one production DDL/row authority
used by both #95 and #88. It exposes `Component="pricing"`, `Version=1`, an
ordered `OwnedObjects` manifest binding object type/name/target table/normalized
SQL, `Ensure(connection, transaction)`, `IsValid(connection, transaction)`,
and `ValidateRows(connection, transaction)`. #88 calls this authority directly;
it must not copy a pricing table list or DDL string.

Canonical creation SQL omits `IF NOT EXISTS`. `Ensure` creates the complete
component only when the component row and every `pricing_*` object are absent,
after validating exact Session v13, alert-engine v2, and runtime-backup v1
dependencies. It inserts `schema_version('pricing',1)` last. Once any pricing
object or component row exists, a missing, extra, type/target/SQL-mismatched
object fails closed and is never repaired. No additional column or owned object
is permitted.

The exact ordered table/column manifest is:

| Table | Columns in exact order |
| --- | --- |
| `pricing_catalog_snapshots` | `catalog_sha256 TEXT PRIMARY KEY`, `schema_version TEXT`, `canonical_blob BLOB`, `document_count INTEGER`, `first_recorded_at_utc TEXT` |
| `pricing_configuration_previews` | `preview_digest TEXT PRIMARY KEY`, `canonical_sha256 TEXT`, `canonical_blob BLOB`, `configuration_id TEXT`, `expected_head_revision INTEGER`, `expected_configuration_id TEXT NULL`, `catalog_sha256 TEXT`, `selection_digest TEXT`, `created_at_utc TEXT`, `expires_at_utc TEXT` |
| `pricing_configurations` | `configuration_id TEXT PRIMARY KEY`, `predecessor_configuration_id TEXT NULL UNIQUE`, `schema_version TEXT`, `catalog_sha256 TEXT`, `canonical_sha256 TEXT`, `canonical_blob BLOB`, `created_at_utc TEXT`, `source_count INTEGER`, `budget_count INTEGER`, unique `(configuration_id,catalog_sha256)` |
| `pricing_configuration_heads` | `head_revision INTEGER PRIMARY KEY`, `configuration_id TEXT UNIQUE`, `previous_head_revision INTEGER NULL UNIQUE`, `previous_configuration_id TEXT NULL UNIQUE`, `committed_at_utc TEXT`, unique `(head_revision,configuration_id)` |
| `pricing_configuration_commits` | `head_revision INTEGER PRIMARY KEY`, `configuration_id TEXT UNIQUE`, `preview_digest TEXT UNIQUE`, `request_sha256 TEXT`, `canonical_request_blob BLOB`, `canonical_result_blob BLOB`, unique `(head_revision,configuration_id)` |
| `pricing_recalculation_runs` | `run_id TEXT PRIMARY KEY`, `request_schema_version TEXT`, `idempotency_key TEXT UNIQUE`, `request_digest TEXT`, `canonical_request_blob BLOB`, `configuration_id TEXT`, `configuration_head_revision INTEGER`, `catalog_sha256 TEXT`, `calculation_time_utc TEXT`, `target_count INTEGER`, `scope_count INTEGER`, `created_at_utc TEXT` |
| `pricing_recalculation_targets` | `run_id TEXT`, `target_ordinal INTEGER`, `session_id TEXT`, `session_status TEXT`, `session_effective_at_utc TEXT`, `session_updated_at_utc TEXT`, `source_partition_state TEXT`, `source_partition_count INTEGER`, `source_partition_digest TEXT`, `source_surface TEXT NULL`, `source_application_version TEXT NULL`, `base_head_revision INTEGER NULL`, `base_estimate_id TEXT NULL`, `base_attempt_revision INTEGER`, primary key `(run_id,target_ordinal)`, unique `(run_id,session_id)` |
| `pricing_recalculation_events` | `run_id TEXT`, `event_sequence INTEGER`, `event_kind TEXT`, `occurred_at_utc TEXT`, `failure_phase TEXT NULL`, `failure_ordinal_kind TEXT NULL`, `failure_ordinal INTEGER NULL`, `failure_code TEXT NULL`, primary key `(run_id,event_sequence)`, unique `(run_id,event_kind)` |
| `pricing_recalculation_target_results` | `run_id TEXT`, `target_ordinal INTEGER`, `result_kind TEXT`, `estimate_status TEXT NULL`, `estimate_id TEXT NULL UNIQUE`, `result_code TEXT NULL`, primary key `(run_id,target_ordinal)` |
| `pricing_recalculation_budget_results` | `run_id TEXT`, `scope_ordinal INTEGER`, `scope_kind TEXT`, `scope_id TEXT`, `scope_start_utc TEXT NULL`, `scope_end_utc TEXT NULL`, `rule_id TEXT`, `rule_version TEXT`, `evaluation_id TEXT`, `outcome_kind TEXT`, `alert_id TEXT NULL`, `suppression_ordinal INTEGER NULL`, `suppression_code TEXT NULL`, primary key `(run_id,scope_ordinal)` |
| `pricing_session_attempts` | `session_id TEXT`, `attempt_revision INTEGER`, `run_id TEXT`, `target_ordinal INTEGER`, `result_kind TEXT`, `estimate_status TEXT NULL`, `estimate_id TEXT NULL`, `result_code TEXT NULL`, primary key `(session_id,attempt_revision)`, unique `(run_id,target_ordinal)`, unique `(session_id,run_id)` |
| `pricing_estimates` | `estimate_id TEXT PRIMARY KEY`, `supersedes_estimate_id TEXT NULL UNIQUE`, `schema_version TEXT`, `session_id TEXT`, `catalog_sha256 TEXT`, `configuration_id TEXT`, `source_entry_ordinal INTEGER`, `run_id TEXT`, `target_ordinal INTEGER`, `calculation_time_utc TEXT`, `session_effective_at_utc TEXT`, `status TEXT`, `source_surface TEXT`, `source_application_version TEXT`, `provider TEXT`, `model TEXT`, `billing_mode TEXT`, `pricing_route TEXT`, `registry_version TEXT NULL`, `registry_source_kind TEXT NULL`, `currency TEXT NULL`, `amount_text TEXT NULL`, `canonical_sha256 TEXT`, `canonical_blob BLOB`, unique `(session_id,estimate_id)`, unique `(run_id,target_ordinal)` |
| `pricing_estimate_heads` | `session_id TEXT`, `head_revision INTEGER`, `estimate_id TEXT`, `previous_head_revision INTEGER NULL`, `previous_estimate_id TEXT NULL`, primary key `(session_id,head_revision)`, unique `(session_id,estimate_id)`, unique `(estimate_id)`, unique `(session_id,head_revision,estimate_id)` |

Every manifest column is `NOT NULL` unless it explicitly says `NULL`; every
listed primary/unique key is exact and no unlisted column or constraint is
permitted. All IDs, hashes, timestamps, counts, tokens, decimals, and BLOB lengths have
both SQL CHECKs and streaming semantic validation. SHA-256 is 64 lowercase
hex; timestamps are exact 33-character seven-fraction UTC; local Session IDs
are exact lowercase nonempty canonical Guid `D` values (historical Guid
versions remain valid); run IDs are canonical UUIDv7; amounts are canonical
decimal TEXT and never `REAL`/`NUMERIC`. Catalog/preview/configuration/
commit-request/commit-result/recalculation-request/estimate BLOBs are
respectively bounded to 4 MiB/1 MiB/1 MiB/1 MiB/64 KiB/1 MiB/1 MiB.

Exact row checks additionally require:

- catalog schema `pricing.catalog-snapshot.v1`, 1..64 documents, strict
  consumer bytes, and digest equality;
- preview schema `cost.configuration-preview.v1`, strict consumer bytes,
  canonical SHA/preview digest, exact head/catalog/selection/count/time scalar
  projections, zero-head/null-configuration equivalence, and expiry exactly
  15 minutes after canonical creation time; the whole table contains at most
  32 rows, including expired rows awaiting owner cleanup;
- configuration schema `cost.configuration.v1`, 0..32 source entries, 0..3
  budget entries, exact consumer/ID/hash/count/time projections;
- commit request/result schemas `cost.configuration-commit.v1` and
  `cost.configuration-commit-result.v1`, strict consumer bytes, request SHA,
  exact echoed preview fields, and head/configuration/catalog cross-equality;
- request schema `cost.recalculation-request.v1`, idempotency grammar, 1..100
  targets, 0..8 scopes, and exact consumer/digest projections;
- target ordinal `0..99`, terminal Session status `completed | failed`, exact
  source-partition state `resolved | missing | incomplete | mixed`, observation
  count `0..257` where 257 is the sole overflow sentinel and cannot be
  `resolved`, 64-lowercase resolver digest, source fields both present only for
  `resolved` and both null otherwise, base-head fields both null or both
  present, and nonnegative attempt revision;
- event sequence `0..2`: sequence 0 only `requested`; sequence 1 `running` or
  recovery `failed`; sequence 2 `succeeded | failed` after running. Non-failure
  events have all failure columns null and a failed event has the exact
  phase/ordinal/code shape;
- result/attempt shape exactly `estimate` with status
  `estimated | partial | not-estimable` and estimate ID, `unavailable` with one
  declared unavailable code, or `failed` with one declared failure code;
- scope ordinal `0..7`, one exact Session/day/period shape, and one fixed
  rule ID with separate version `1`;
- budget outcome exactly `receipt | suppression | no_match`, with only the
  corresponding alert ID or suppression ordinal/code fields present;
- estimate scalar projections and exact catalog/configuration/source-entry/run
  ownership equal the strict #94 record and configuration provenance; and
- head revision one has both predecessor fields null, while revision N names
  exactly revision N-1 and the exact prior identity. Configuration head
  predecessor must equal the configuration's own predecessor.

`canonical_sha256` is always lowercase SHA-256 of the exact bounded canonical
BLOB in its row. `pricing_configuration_heads.committed_at_utc` is sampled once
inside the successful append transaction and preserved on exact replay.
`pricing_recalculation_runs.created_at_utc` must byte-equal that run's single
captured `calculation_time_utc`. These equalities are SQL/semantic row
invariants, not presentation conventions.
The sequence-0 requested event uses that same run timestamp. Running, terminal,
and recovery event timestamps are each sampled exactly once inside their own
successful append transaction and preserved on replay. Event timestamps are
nondecreasing by sequence; no ordering or identity uses wall-clock time.

Every FK uses `ON DELETE RESTRICT`. They bind configuration to catalog/
predecessor, configuration head to its exact preceding head/configuration,
configuration commit to its exact head/configuration, run
to its exact configuration head/catalog, target to run/Session/optional exact
base head, event/result to run/target, budget result to exact #80 evaluation and
optional receipt or suppression, attempt to Session/target/result/estimate,
estimate to Session/catalog/configuration/run-target/same-Session predecessor,
and estimate head to same-Session estimate/predecessor/exact previous head.
`ValidateRows` additionally proves every budget parent is v2 and all
evaluation/receipt/suppression identities agree; an FK-resolving v1 parent is
still invalid.

The exact named indexes are:

```sql
CREATE INDEX pricing_recalculation_runs_recovery_idx
ON pricing_recalculation_runs(calculation_time_utc,run_id);
CREATE INDEX pricing_recalculation_targets_session_idx
ON pricing_recalculation_targets(session_id,run_id,target_ordinal);
CREATE INDEX pricing_recalculation_events_kind_idx
ON pricing_recalculation_events(event_kind,run_id,event_sequence);
CREATE INDEX pricing_estimates_analytics_idx
ON pricing_estimates(session_effective_at_utc,provider,model,billing_mode,
registry_version,currency,estimate_id);
CREATE INDEX pricing_recalculation_budget_alert_idx
ON pricing_recalculation_budget_results(alert_id,run_id,scope_ordinal);
```

Every durable table has exact `<table>_no_update`, `<table>_no_delete`, and
`<table>_no_replace` triggers generated by one compile-time template in
`PricingSchemaV1`. `pricing_configuration_previews` instead has exact
`pricing_configuration_previews_no_update` and
`pricing_configuration_previews_no_replace` triggers; its owner may issue only
the expiry/consumption deletes above. The no-replace predicate covers every
primary and unique key, so `INSERT OR REPLACE` cannot delete through an
alternate unique key.
Exact contiguous-insert guards are
`pricing_configuration_heads_contiguous_insert`,
`pricing_recalculation_targets_contiguous_insert`,
`pricing_recalculation_events_contiguous_insert`,
`pricing_recalculation_budget_results_contiguous_insert`,
`pricing_session_attempts_contiguous_insert`, and
`pricing_estimate_heads_contiguous_insert`. #88 validates the normalized SQL
from this same manifest.

Independent schema tests also compare the production manifest with a
test-owned literal golden list of every object name, column/nullability,
normalized DDL, CHECK, FK action, index, and trigger predicate. The golden is
not generated from `PricingSchemaV1`; changing production and backup consumers
to the same incorrect manifest therefore cannot make the test pass.

`pricing_catalog_snapshots` has `catalog_sha256` as primary key and stores the
exact 1..4 MiB canonical BLOB, schema version, bounded `1..64` document count,
and `first_recorded_at_utc`. That time is sampled only for the first successful
insert, is not caller controlled, and is preserved on idempotent replay and
restore. Same SHA plus identical strict bytes/projections returns the existing
row without sampling or comparing a new time; same SHA plus different bytes or
byte-derived projection is a conflict.

`pricing_configuration_previews` stores one strict canonical preview response
per preview digest, with canonical SHA and the exact configuration/head/catalog/
selection/time projections plus exact 15-minute expiry needed for lookup and
validation. It references no uncommitted configuration or catalog row. Same
digest is insert-or-byte-identical; every other preview is a new insert within
the 32-active cap. Only expired/consumed rows are deleted in owner transactions.
It is transient server-issued confirmation authority, not a configuration head
or authorization to bypass current CAS checks.

`pricing_configurations` has configuration ID as primary key, optional unique
predecessor self-reference, referenced exact catalog SHA, exact canonical
configuration BLOB bounded to 1 MiB, canonical creation time, source/budget
counts, and digest projections. `pricing_configuration_heads` is a singleton
contiguous revision ledger: revision one has no predecessor; revision N names
N-1 and its configuration. A configuration can appear in one head revision and
one predecessor can have at most one successor.
`pricing_configuration_commits` is a mandatory one-to-one child of each head
and records the consumed preview digest as a validated historical scalar, not a
foreign key to the transient row. Its stored request preserves the complete
preview fields after the transient receipt is deleted.
It stores the exact canonical commit request, its SHA-256, and the exact
canonical successful result. Request/result strict reload must reproduce the
head revision, configuration ID, and catalog SHA; insertion occurs in the same
transaction as the head. It is the sole lost-response replay authority and is
never synthesized from current selection or provider state.

`pricing_estimates` stores the canonical estimate BLOB and only bounded query
projections validated against it: estimate/predecessor/Session/catalog IDs,
calculation and Session times, status, provider, model, billing mode, optional
currency, and optional canonical decimal amount text. SQLite REAL/NUMERIC is
not monetary authority. The exact catalog row is required. `session_id`
references `sessions(session_id) ON DELETE RESTRICT`. A composite self-reference
requires the predecessor to belong to the same Session. Estimate ID is primary
key, `(session_id, estimate_id)` is unique, and a non-null predecessor is unique
so successful history cannot fork.

`pricing_estimate_heads` is the explicit per-Session head ledger. Revision one
has no previous estimate. Revision N is exactly N-1 plus one, names the
preceding head, and the new estimate's predecessor must be that same ID.
The primary key is `(session_id, head_revision)` and both
`(session_id, estimate_id)` and estimate ID are unique. An initial head is
therefore unique even though its estimate predecessor is null. Head and
estimate insertion occur together after an exact base-revision/base-estimate
CAS.

`pricing_recalculation_runs` has UUIDv7 run ID as primary key, unique bounded
idempotency key, request digest, exact canonical request BLOB bounded to 1 MiB,
configuration/catalog identities, and captured calculation time. Run state is
derived only from its contiguous event ledger; the immutable root has no mutable
terminal projection. Same key/digest/bytes returns the existing run; same key
with different digest/bytes is conflict.
`pricing_recalculation_targets` uses `(run_id, target_ordinal)` as primary key,
keeps caller order, has a unique `(run_id, session_id)`, references Session with
`ON DELETE RESTRICT`, and stores the captured Session status/effective/update
facts, resolver state/count/digest/resolved tuple, base head revision/estimate,
and base attempt revision.
`pricing_recalculation_events` uses `(run_id, event_sequence)` and contiguous
sequence. `pricing_recalculation_target_results` uses
`(run_id, target_ordinal)` and stores exactly one closed result with optional
estimate identity or fixed unavailable/failure code. Events and results never
store exception/provider text.
`pricing_recalculation_budget_results` uses `(run_id, scope_ordinal)` as primary
key and stores the exact requested scope kind/ID, selected rule ID/version,
evaluation ID, and one closed outcome:
`receipt` with alert ID, `suppression` with suppression ordinal/fixed code, or
`no_match` with neither. It has exact FKs to the same-transaction #80
evaluation and, when present, receipt or `(evaluation_id,
suppression_ordinal)`. Every successful run has exactly one result per requested
scope. No query finds a run's alerts by time/current state or by scanning
unrelated evaluations.
`pricing_session_attempts` uses `(session_id, attempt_revision)` as primary key,
has unique `(session_id, run_id)`, references the target `(run_id, session_id)`,
and advances exactly one contiguous revision under the run transaction. It
records only run/target identity and closed result kind, never a mutable state.

Configuration heads follow the same contiguous append-only rule. Recalculation
roots, targets, events, and results preserve caller order, exact captured heads,
and one closed state transition:

```text
requested -> running -> succeeded | failed
requested -> failed(recalculation_interrupted)
```

All component creation/migration occurs in one immediate transaction and
inserts `schema_version(component='pricing', version=1)` last. Any future
version, missing/mismatched owned object, owned object without the component
row, malformed BLOB/scalar pair, invalid sequence, invalid predecessor, or
extra `pricing_*` object fails closed without mutation.

Every canonical configuration/request/catalog/estimate insert uses
insert-or-byte-identical semantics. Same ID plus different bytes or scalar
projection is conflict. Exact triggers reject UPDATE, DELETE, and replacement
of every owned row; exact indexes/FKs/CHECKs enforce the relationships above.
The store revalidates canonical bytes and scalar equality before returning any
projection.

The fixed full migration tail is:

```text
historical_instruction_analysis
  -> historical_import
  -> sanitized_import
  -> runtime_backup
  -> pricing
```

This preserves Issue #79 -> #86 -> #88 as an unchanged subsequence. A declared
pricing component without Session v13, `alert_engine` v2, or runtime-backup v1
is a forged, unsupported vector. Startup upgrades/validates the shared alert
engine to v2 before creating or opening pricing, then creates pricing after
runtime-backup in the same final migration transaction. Issue #88 backs up the complete database, includes pricing
component/table row counts, validates exact owned shape/bytes, migrates an
older valid runtime-backup-v1 source by appending pricing v1, and restores
pricing rows without adding a ZIP member or Retention kind.

## Session and analytics projections

Session estimate reads return the exact Session ID, explicit calculation state,
ordered immutable estimate history, active-head identity, predecessor delta,
and each Issue #94 status:

- `estimated`, including explicit zero incremental cost;
- `partial`;
- `not-estimable`;
- not calculated/source adapter unavailable;
- recalculation running; and
- recalculation failed.

Each estimate view displays amount/currency only when present, all components,
coverage and missing categories, provider/model/billing mode/route, registry
version/effective range/source kind/label, catalog SHA, reasons/disclaimer, and
calculation/predecessor identities. Reviewed source references are rendered as
inert text only. They are never links and the server never fetches them.

The exact session-history response is `cost.session-estimates.v1` with property
order:

```text
schema_version
session_id
calculation_state
active_head_revision
active_estimate_id
latest_attempt_revision
latest_attempt
items
next_after
```

`calculation_state` is
`estimated | partial | not_estimable | not_calculated | requested | running | failed |
unavailable | stale`. A non-stale active head wins that field even when a later
attempt failed or was unavailable; `latest_attempt` exposes the latter
separately, including its freshness. A stale active head yields `stale`.
Only with no active head is the field derived from the latest exact attempt:
its stale freshness yields `stale`, otherwise its kind yields the matching
state; no attempt yields `not_calculated`.
Nullable head/attempt/cursor members are emitted explicitly.

`latest_attempt` is null or orders `attempt_revision`, `run_id`,
`calculation_time_utc`, `freshness`, `kind`, `estimate_status`, `estimate_id`,
`code`. `freshness` is `fresh | stale` under the exact per-item stale gate.
Kind is `requested | running | estimate | unavailable | failed`; only estimate carries
the exact #94 `estimated | partial | not-estimable` status plus estimate ID,
only unavailable/failed carries a fixed code, and
requested/running carry neither. A requested/running projection comes from the
sole exact nonterminal target and uses `base_attempt_revision + 1` as its
reserved next revision; no
attempt row is claimed until terminal completion/recovery. The no-overlap
admission rule makes this projection singular. Each `items` element orders:

```text
head_revision
estimate_id
predecessor_estimate_id
calculation_time_utc
session_effective_at_utc
estimate_status
freshness
amount_kind
amount
currency
provider
model
billing_mode
pricing_route
catalog_sha256
configuration_id
registry
components
coverage
reasons
delta
disclaimer
```

`estimate_status` is the immutable exact #94
`estimated | partial | not-estimable` status. `freshness` is independently
`fresh | stale` under the per-item gate, so staleness never erases original
status. `amount_kind=complete_total` only for a fresh `estimated` record;
`provisional_known_component_subtotal` only for a fresh `partial` record;
otherwise it is `not_applicable` with null amount/currency. A component orders
`category`, `state`, `amount`, `missing_reason`, where state is
`available | missing`; it never exposes quantity, rate, provenance token, or
private source locator. A registry projection is null when the strict #94
estimate has no selected registry entry. When present it orders `registry_version`,
`source_kind`, `source_id`, `source_label`, `entry_key`,
`effective_from_utc`, `effective_to_utc`, `last_reviewed_date`,
`stale_after_date`, `currency`, `source_reference`. These are strict #94
repository-safe fields. `source_reference` is present only for a bundled entry
and is null for a local override; a local-override label is an explicitly
operator-provided repository-safe label that passed the #94 free-form guard,
never a path-derived value. A bundled reference remains inert text.

Coverage orders `required_categories`, `estimated_categories`,
`missing_categories` in the strict #94 category order. Reasons retain the
strict bounded #94 order. Delta orders `state`, `amount`, `currency`,
`basis_freshness`, `changed_fields`. It is `available` when this item and its
exact predecessor both have immutable `estimate_status=estimated`, non-null
amounts in the same currency, and checked subtraction is representable,
regardless of their current freshness. `basis_freshness` is
`both_fresh | includes_stale`; it is null unless state is available.
Otherwise amount/currency are null and state is
`not_applicable | unrepresentable`. Changed fields are a sorted subset
of `status | amount | provider | model | billing_mode | pricing_route |
registry | catalog | configuration | coverage | components | session_time`.
Delta never labels a negative amount as an improvement or savings.
Available `delta.amount` is exactly current item amount minus exact predecessor
amount in their shared currency.
An `includes_stale` delta is explicitly historical comparability only; neither
operand contributes a current analytic/budget amount because of the delta.
`disclaimer` is the always-present fixed safe token
`estimated_cost_not_invoice.v1`; it is never arbitrary text. The partial
`amount_kind` above is the additional machine-readable provisional warning.

The response contains at most 100 items and 8 MiB UTF-8. A single safe
projection that cannot fit is `503 cost_response_too_large`; it is not
truncated. No response includes canonical catalog/estimate bytes, registry
documents, rates/quantities, or provenance identifiers.

Analytics first creates one contribution for every exact eligible Session in
the explicit half-open UTC range. Its base grouping tuple is UTC effective
date, source surface, nullable provider, nullable exact model, nullable billing
mode, nullable repository, nullable workspace, nullable registry version, and
nullable currency. Date comes from the exact current Session-effective time;
source comes only from the current `resolved` source-partition tuple. Every
contribution also captures current Session status/update time and resolver
state/count/digest/resolved tuple. Pricing dimensions come only from a strict
non-stale active #94 estimate.

Repository/workspace come from exact Session lookup through the accepted
sanitized free-form guard at output time, even when the stored row predates that
guard. A missing, path-like, email/credential-like, unsafe, or conflicting
label is never echoed and becomes null. Null is distinct from the literal token
`unknown`, sorts separately, and is listed in the ordered
`unknown_dimensions`; an explicit filter never matches null. No repository or
workspace value is normalized, truncated, joined across Sessions, or used as
identity.
`unknown_dimensions` contains only null dimensions, in this closed order:
`provider`, `model`, `billing_mode`, `repository`, `workspace`,
`registry_version`, `currency`, `component_category`. It omits non-null
dimensions and has no other token.

For each base tuple:

```text
denominator = distinct selected eligible Sessions assigned to the tuple
numerator   = those Sessions whose current member state is estimated
coverage_bps = floor(numerator * 10000 / denominator)
```

The seven state counts (`estimated`, `partial`, `not_estimable`, `missing`,
`failed`, `unavailable`, `stale`) sum exactly to denominator. Explicit estimated
zero remains in numerator. These base-group counts are copied unchanged to
component rows and are not additive across them.

Each strict active estimate contributes one component row per exact #94
component category, including a missing component with null amount. A Session
with no component contributes one row with null component category. The final
group key is the base tuple plus nullable component category. For each row:

- `estimated_amount` is the checked exact sum of non-null component amounts from
  estimated Sessions only;
- `partial_known_component_amount` is the checked exact sum of non-null
  components from partial Sessions only and is explicitly provisional with
  exact reasons, never a lower-bound or actual-cost claim;
- the two subtotals are never merged, and estimate-level amount is not added a
  second time or allocated proportionally;
- missing/null component, not-estimable, missing, failed, unavailable, and
  stale states contribute no monetary value; and
- a present exact zero component is an available zero contribution.

Each subtotal state is `available | not_applicable | unrepresentable`.
Available requires at least one contribution, including zero; not-applicable
has null amount; checked decimal overflow is unrepresentable with null amount.
No subtotal is rounded, wrapped, clamped, or converted. For every strict
estimate the checked sum of its non-null components must equal the #94 amount;
disagreement makes the analytics read unavailable.

The exact group DTO orders:

```text
utc_date, source_surface, provider, model, billing_mode,
repository, workspace, registry_version, currency, component_category,
group_id,
unknown_dimensions, eligible_session_count, estimated_session_count,
partial_session_count, not_estimable_session_count, missing_session_count,
failed_session_count, unavailable_session_count, stale_session_count,
coverage_basis_points, component_session_count,
estimated_component_session_count, partial_component_session_count,
estimated_amount_state, estimated_amount,
partial_known_component_amount_state, partial_known_component_amount,
partial_reason_counts
```

Groups order by UTC date, source, provider, model, billing mode, repository,
workspace, registry version, currency, component category, then group ID, all
ascending; null sorts before non-null and strings compare ordinally.
`group_id` is `cost-analytics-group-` plus lowercase SHA-256 over a
length-framed `cost-analytics-group/v1` domain and complete nullable group key,
with null framed distinctly from empty text.

`partial_reason_counts` is an ordered array of `reason`, `session_count`.
Reason is one exact #94 estimate reason and count is the number of distinct
partial Sessions that contribute a non-null amount to that exact component
row and carry that reason. Entries follow the frozen #94 reason registry order
and omit zero counts. The array is empty unless the row's provisional partial
subtotal has at least one contribution. Counts explain that row only and never
turn the subtotal into a lower-bound or actual-cost claim.

`component_session_count` is the number of distinct Sessions represented by
the exact component key, including a present missing component;
`estimated_component_session_count` and
`partial_component_session_count` count distinct Sessions contributing a
non-null amount to the corresponding row subtotal. Both are at most component
Session count. Base-group state counts remain copied context and are not
additive across component rows.

Analytics also materializes monetary totals before group pagination. It never
derives a snapshot total from one component page. `range_totals` has at most
2,000 rows because each tuple requires at least one of the at-most-2,000
eligible Sessions. Rows order by nullable registry version then currency. Each
row orders
`registry_version`, `currency`, `estimated_amount_state`, `estimated_amount`,
`partial_known_component_amount_state`,
`partial_known_component_amount`, `partial_reason_counts`.
`daily_totals` has at most 2,000 rows ordered by UTC date, nullable registry
version, then currency and adds `utc_date` first to that same shape. Totals use
each strict estimate's top-level amount exactly once: non-stale `estimated`
amounts feed the estimated subtotal, and non-stale `partial` known-component
subtotals feed the separately provisional partial subtotal. Their state and
checked arithmetic rules equal component subtotal rules. Total
`partial_reason_counts` count distinct contributing partial Sessions for that
exact range/day, registry-version, and currency tuple. Currency or registry
versions are never combined or converted. Nonmonetary states remain in
`overall` but contribute to no total.

The exact analytics response is `cost.analytics.v1` with property order:

```text
schema_version
snapshot_id
state
cap_reason
eligible_session_count
eligible_session_lower_bound
group_lower_bound
filters
overall
range_totals
daily_totals
groups
next_cursor
```

State is `complete | incomplete`; cap reason is null or
`eligible_session_limit | group_limit`. Canonical filters order `from`, `to`,
`source_surface`, `provider`, `model`, `billing_mode`, `status`,
`registry_version`, `currency`, `repository`, `workspace`, `limit`, with every
nullable field explicit and no cursor. On a complete response, eligible count
is exact, both lower-bound fields and cap reason are null, and `overall` orders
`eligible_session_count`, the seven state counts in the group-DTO order,
`coverage_numerator`, `coverage_denominator`, `coverage_basis_points`.
It also returns complete `range_totals` and `daily_totals`.
On eligible-session acquisition overflow, exact count/overall/percentage/next
cursor and group lower bound are null, eligible-session lower bound is 2,001,
and all totals/groups are empty. Group-limit
overflow occurs only after exact eligible
acquisition; eligible-session lower bound is null, group lower bound is 2,001,
exact eligible count remains present, overall is null, and no monetary
total/group is returned. Range and daily totals need no separate cap or
overflow state because each distinct tuple requires at least one of the
at-most-2,000 exact eligible Sessions.
When bounds coincide, cap rank is fixed: rank 0
`eligible_session_limit`, rank 1 `group_limit`. Acquisition checks the 2,001st
eligible Session and then the 2,001st ordered component-group key; only the
lowest present rank is projected and hashed.

Each group is exactly the ordered DTO above. All nullable dimensions and
amounts are explicit null; state/count/amount invariants are revalidated before
serialization. `groups.Count <= limit`, and `next_cursor` is present only for
a complete snapshot with further fully validated groups. The full UTF-8
response is at most 8 MiB. One group that cannot fit alone is
`503 cost_response_too_large`, never an empty cursor loop.

Analytics and budget evaluation reuse the same active configuration and exact
eligible acquisition below. An unselected source is outside numerator and
denominator rather than silently diluting another budget. A zero denominator
has no percentage and no budget match. Mixed currencies/registry versions are
separate exact groups and never receive a combined total.

## Budget evaluation and alert v2

Budget evaluation reuses the single Issue #80 evaluator/store and the Issue #83
lifecycle. It never creates a synthetic Session or a second alert stack.

There is one mutable configuration authority: the append-only active
`cost.configuration.v1` head. For each requested scope the cost application
projects its three budget entries deterministically into
`alert.config.v2` in fixed rule-ID order. The projection copies the exact
enabled/currency/threshold/minimum-coverage/window fields and binds the source
cost configuration ID, configuration-head revision, and catalog SHA into the
alert configuration hash, evaluation identity, and receipt. No independent
Alert Center or #80 configuration write API exists for these rules.

Budget eligibility is independent of the one-to-100 recalculation target list.
Inside one stable read transaction the cost application enumerates the complete
eligible set and captures its deterministic eligibility digest plus every
active estimate head revision. A Session is eligible in v1 when its persisted
status is exactly `completed` or `failed`, its current source resolver state is
`resolved`, and that exact resolved source-surface/application-version tuple
matches one active configuration source entry. Its canonical window time is the exact current persisted
`sessions.last_seen_at`, captured with `sessions.updated_at` and rechecked
before append. Active/unknown Sessions and unselected source versions
are ineligible. A selected Session with an unavailable adapter, missing
estimate, partial/not-estimable/stale head, or prior recalculation failure
remains in the denominator. If a current last-seen/update/source fact changes
during evaluation, the eligibility digest changes and the append fails stale;
an old receipt retains its old exact window and is never rewritten. Missing/
ambiguous source identity fails that Session's exact eligibility match and
receives no inferred public reason or configured source. Codex App enters the universe only
when an exact Codex source/version entry is explicitly configured.

The complete set is ordered by Session-effective time then Session UUID and is
bounded to 2,000. Acquisition reads one extra key only to detect overflow. An
overflow produces an `incomplete` v2 snapshot with the exact window, no member
projection, and lower-bound count 2,001; it may produce only
`eligible_set_incomplete` suppression after the applicable rule is present and
enabled; an absent/disabled rule produces the earlier `rule_disabled`. It never
produces a partial monetary alert.
Database/read failure returns an application/store failure and appends no
evaluation. Exact membership, active heads, and eligibility digest are
rechecked in the append transaction; a change yields a stale conflict rather
than an evaluation over mixed snapshots.

The cost application builds one exact Issue #80 v2 snapshot from that captured
eligible set for:

- one explicitly selected Session;
- each requested UTC calendar day; and
- each requested rolling period ending at an explicit midnight-UTC cutoff.

Every requested scope is built even when its fixed rule entry is absent or
disabled. Registry metadata fixes the Session/day/rolling-period scope kind;
the request fixes a rolling period's `window_days`. A present period entry must
match that value. Rule absence/disable is evaluated only after scope
applicability, so the result is `rule_disabled` rather than an invented
incomplete, empty, currency, or coverage outcome.

A rolling period contains `window_days` complete UTC calendar days and its
half-open end is the explicit cutoff. A non-midnight cutoff is invalid. Session
scope requires the named Session to be eligible. Day/period scopes may contain
zero eligible Sessions; that is a complete empty snapshot with numerator and
denominator zero, null coverage basis points, no monetary amount, and no
evidence. With an applicable present/enabled rule it produces only
`no_eligible_sessions`; absent/disabled still produces `rule_disabled`.

Each snapshot contains exact sorted Session IDs, exact estimate IDs, exact
catalog SHA/registry version/billing mode sets, nullable currency and
estimated-only amount exactly under the #80 v2 estimated-member and aggregate
state rules, numerator, denominator, nullable coverage basis points, and exact
Session plus pricing-estimate evidence references. Identifier sets may be
empty. Session scope has nullable bounds; day/period scope carries its exact
half-open UTC window. Member and scope order is Session-effective time then
Session UUID. Evidence order and cross-field equality follow the #80 v2
contract. A non-null member currency must be the sole v1 currency `USD`;
another currency is contract rejection before append, never a split,
conversion, or suppression. No repository/time/path heuristic creates
membership.

The three rules:

- emit warning/critical only when their explicit matching configuration is
  enabled, currency matches, denominator is nonzero, coverage meets the
  configured minimum, and estimated-only amount reaches the threshold;
- emit `insufficient_estimate_coverage`, `no_eligible_sessions`,
  `eligible_set_incomplete`, `no_covered_estimate`,
  `aggregate_amount_not_representable`, or `rule_disabled` suppression
  otherwise;
- never count partial/not-estimable/missing as zero;
- never compare a zero threshold when no estimated member exists and never
  round/wrap/clamp an unrepresentable aggregate;
- keep included estimated zero as covered; and
- include estimate/catalog/registry/billing/coverage identity in the canonical
  v2 receipt, never in lifecycle comments or arbitrary summary text.

Alert lifecycle actions remain the exact Issue #83 v1 event/API contract keyed
by immutable alert ID. Alert Center v1 remains compatible; an additive
version-aware #84 read route presents v1 and v2 receipts and exact multi-Session
links. The #85 sanitized bundle v1 continues to export only exact
`alert.receipt.v1` carriers. It recognizes alert-engine schema v2, selects v1
rows only, and privately owner-validates bounded engine-v2 schema/structure
metadata. It never reads, projects, materializes into the export pipeline,
counts, hashes, or exports pricing or v2 canonical payload bytes.

## HTTP and browser surface

Local Monitor exposes:

- `GET /api/costs/v1/configuration`;
- `GET /api/costs/v1/configurations/{configuration_id}`;
- `GET /api/costs/v1/catalog?after=<cursor>&limit=<1..100>`;
- `POST /api/costs/v1/configuration/preview`;
- `POST /api/costs/v1/configurations`;
- `POST /api/costs/v1/recalculations`;
- `GET /api/costs/v1/recalculations/{run_id}`;
- `GET /api/costs/v1/sessions/{session_id}/recalculations?after=<attempt_revision>&limit=<1..100>`;
- `GET /api/costs/v1/sessions/{session_id}/estimates?after=<estimate_id>&limit=<1..100>`;
- `GET /api/costs/v1/sessions/{session_id}/estimates/{estimate_id}`;
- `GET /api/costs/v1/analytics?from=<UTC>&to=<UTC>&after=<cursor>&limit=<1..100>`;
  and
- `GET /costs`.

The API accepts at most 1 MiB strict JSON and at most the documented bounded
query length. Every API/page request requires a valid loopback Host and
same-origin context. GET is mutation-free. POST additionally requires JSON and
`x-monitor-csrf`; all responses are `Cache-Control: no-store`, CORS remains off,
and errors contain only:

```json
{"schema_version":"cost.error.v1","error":"<fixed_code>"}
```

Closed status classes are:

| Condition | HTTP |
| --- | --- |
| invalid ID/query/body/version/configuration | `400` |
| wrong origin or CSRF | `403` |
| Session/configuration/estimate/run not found | `404` |
| stale preview/head, preview capacity, idempotency/active-run/session-eligibility/snapshot conflict, source/catalog change | `409` |
| oversized request | `413` |
| unsupported media type | `415` |
| store busy/unavailable or response too large | `503` |

A known Session without an estimate returns HTTP 200 with explicit
not-calculated/unavailable state, never 404 or a zero.

Query strings are at most 8,192 UTF-8 bytes. Duplicate or unknown keys, empty
values, invalid percent encoding, noncanonical IDs/times, or values outside the
closed vocabularies are invalid; no last-value-wins parsing exists.
Configuration reads return `cost.configuration-read.v1` with exact head
revision, optional full current configuration, configuration/provider catalog
SHAs plus catalog state, and bounded
selection counts. Preview returns the normalized proposal/time, preview digest,
captured head/catalog/selection digest, and counts described above. A successful
configuration commit returns `201` and `Location` for the immutable
configuration-version route, never the singleton current-head read.

A new or byte-equivalent idempotent recalculation start returns `202`,
`Location: /api/costs/v1/recalculations/{run_id}`, and
the same fixed `cost.recalculation.v1` shape as polling, with properties in this
exact order:

1. `schema_version`;
2. `run_id`;
3. `request_digest`;
4. `state`;
5. `target_count`;
6. `scope_count`;
7. `targets`;
8. `events`;
9. `budget_results`; and
10. `failure_code`.

`run_id` is canonical lowercase nonempty UUIDv7 text, `request_digest` is 64
lowercase hexadecimal characters, state is
`requested | running | succeeded | failed`, and top-level `failure_code` is
null unless state is failed.

Each target is ordered by caller ordinal and has exact property order
`target_ordinal`, `session_id`, `base_head_revision`, `base_estimate_id`,
`result`. While nonterminal, `result` is null. A terminal result is exactly one
of:

- estimate: `kind`, `status`, `estimate_id`;
- unavailable: `kind`, `code`;
- failed: `kind`, `code`.

Estimate result `status` uses the exact #94
`estimated | partial | not-estimable` tokens; underscore
`not_estimable` is reserved for calculation/analytics member state.

Each event is ordered by contiguous sequence and has exact property order
`event_sequence`, `state`, `occurred_at_utc`, `failure_code`. Its failure code
is non-null only for a failed event. Each successful budget result is ordered
by caller scope ordinal and has exact property order `scope_ordinal`, `scope`,
`rule_id`, `rule_version`, `outcome`. `scope` repeats the exact canonical
request-scope object. Outcome is exactly one of:

- receipt: `kind`, `evaluation_id`, `alert_id`;
- suppression: `kind`, `evaluation_id`, `suppression_ordinal`, `code`;
- no match: `kind`, `evaluation_id`.

For requested/running, results are null, budget results are empty, and
top-level failure code is null. For succeeded, every target is estimate or
unavailable and `budget_results.Count == scope_count`. For failed, every target
is closed under the terminal-precedence rules, budget results are empty, and
top-level failure code is the winning code. Null is never replaced by empty
text or zero. The response never returns canonical bytes or adapter/provider
error text.

Session estimate history is ordered by contiguous head revision descending.
`after` is the exact last estimate ID from the previous page; default limit is
50. The response contains at most 100 items/8 MiB, exact active head, separate
attempt state, predecessor delta, and `next_after`; an invalid/nonmember cursor
is `cost_invalid_cursor`, not an empty page.

Session recalculation history makes every durable retry discoverable without a
known run ID. `cost.session-recalculations.v1` orders `schema_version`,
`session_id`, `active`, `attempts`, `next_after`. `active` is null or the sole
reserved nonterminal attempt projection with property order
`attempt_revision`, `run_id`, `calculation_time_utc`, `freshness`, `state`,
`recalculation_href`; state is `requested | running` and the href is the exact
run route. Freshness is `fresh | stale` under the same gate. Each terminal
`attempts` item orders `attempt_revision`, `run_id`, `calculation_time_utc`,
`freshness`, `kind`, `estimate_status`, `estimate_id`, `code`,
`recalculation_href`. Freshness and kind/null rules equal `latest_attempt`; the href uses
that exact run ID. Items order by contiguous attempt revision descending.
`after` is a positive exact attempt revision from the preceding page and must
belong to that Session; default limit is 50. Active, when present, is separate
and does not consume or become the cursor. At most 100 terminal items and 8 MiB
are returned, and `next_after` is the last returned terminal revision only when
older attempts exist. A malformed/nonmember revision is
`cost_invalid_cursor`. Completing or starting another run never deletes,
hides, or renumbers an earlier terminal attempt.

The exact-estimate route returns `cost.session-estimate.v1` with property order
`schema_version`, `session_id`, `active_head_revision`, `active_estimate_id`,
`item`. The item is the identical safe item projection defined above. It looks
up only the exact `(session_id,estimate_id)` pair and never current/latest/time
proximity; a well-formed missing or other-Session estimate is
`404 cost_estimate_not_found`. This route is the sole API target for an Alert
Center estimate deep link.

Analytics accepts exact seven-fraction UTC `Z` `from`/`to`, a nonempty half-open
range of at most 366 days, and optional single values for
`source_surface`, `provider`, `model`, `billing_mode`, `status`,
`registry_version`, `currency`, `repository`, and `workspace`. These filters
use exact stored values; repository/workspace pass the accepted safe-label
guard and one unsafe value is invalid. `model` must pass the #94 bounded
free-form output guard before exact matching/echo; an unsafe model query is
`400 cost_invalid_query`. V1 currency accepts only literal `USD`; any other
value is `400 cost_invalid_query`, never no-match or conversion. Status values are
exactly `estimated | partial | not_estimable | missing | failed | unavailable |
stale`. Default limit is 50.

The response contains top-level overall seven state counts and coverage over
the complete filtered eligible set plus the component groups. One acquisition
is bounded to 2,000 eligible Sessions and 2,000 groups. If either bound is
exceeded, response state is `incomplete` with exact cap reason/lower bound, no
monetary group or overall percentage, and no cursor; it never publishes
truncated totals. For complete acquisition, each UTF-8 page is at most 8 MiB
and may contain fewer than limit.

Canonical `filter_digest` is lowercase SHA-256 of the length-framed
`cost-analytics-filter/v1` domain and the canonical `filters` bytes above,
including `limit` and excluding `after`. Cursor is
`cost-analytics-cursor-v1.` plus unpadded base64url canonical JSON with
`schema_version=cost.analytics.cursor.v1`, ordering `schema_version`,
`snapshot_id`, `filter_digest`, `limit`, `group_id`. The complete cursor is
1..768 ASCII characters and is treated as opaque. The percent-encoded
analytics query excluding `after` is separately bounded to 7,000 UTF-8 bytes,
so every server-emitted cursor can be resubmitted within the 8,192-byte total
query cap. A continuation must have the same filter digest/limit and its group
ID must identify exactly one member of the recomputed filtered snapshot; the
server resumes after that member's recomputed complete sort tuple. Precedence
is: malformed/schema/length,
filter-digest, or limit mismatch is `cost_invalid_cursor`; the server then
recomputes the snapshot and a snapshot-ID mismatch is
`cost_analytics_snapshot_changed`; only under the same snapshot is a missing or
nonmember group ID `cost_invalid_cursor`.

Analytics snapshot ID is `cost-analytics-snapshot-` plus lowercase SHA-256 of a
length-framed `cost-analytics-snapshot/v1` domain, configuration/head, range,
current provider startup catalog SHA, canonical filters, cap state, and each
acquired ordered Session's ID, status,
effective/update time, current resolver state/count/digest/resolved tuple,
active-head revision/identity, attempt revision, and strict estimate/component
identity used by grouping. That per-item identity includes the exact recomputed
freshness token and either the pricing-selection semantic signature or
no-estimate input-freshness digest defined above. It is always non-null and is
not persisted. A
complete snapshot hashes every acquired fact plus every ordered range-total,
daily-total, and group identity. Eligible overflow hashes exactly the first
2,001 ordered Session facts and `eligible_session_limit`; group overflow
hashes the complete at-most-2,000 Session facts, the first 2,001 ordered group
keys, and `group_limit`. Thus incomplete identity
is bounded and never claims unseen totals. A next-page request recomputes
the digest in one stable read transaction and compares it to the cursor before
returning any group. A changed head/Session/configuration yields
`409 cost_analytics_snapshot_changed`; pages never mix database states.

The closed public error mapping is:

| HTTP | Fixed code |
| --- | --- |
| `400` | `invalid_host`, `cost_invalid_request`, `cost_invalid_query`, `cost_invalid_id`, `cost_invalid_cursor`, `cost_invalid_configuration` |
| `403` | `cross_origin_forbidden`, `csrf_required` |
| `404` | `cost_session_not_found`, `cost_estimate_not_found`, `cost_configuration_not_found`, `cost_recalculation_not_found` |
| `409` | `cost_stale_preview`, `cost_preview_capacity_reached`, `cost_stale_head`, `cost_catalog_changed`, `cost_selection_changed`, `cost_idempotency_conflict`, `cost_recalculation_in_progress`, `cost_session_not_eligible`, `cost_analytics_snapshot_changed` |
| `413` | `cost_request_too_large` |
| `415` | `unsupported_media_type` |
| `503` | `cost_store_busy`, `cost_store_unavailable`, `cost_response_too_large` |

No route substitutes one code for another or reflects an internal exception.

`/costs` is a separate result-oriented page. The accepted two-item primary
sidebar remains unchanged. A fixed contextual Cost entry on Overview and
Diagnostics always opens `/costs`, including when no adapter/estimate/receipt
exists; exact Session and Alert Center links may open a narrower context. Its
initial query is closed to
`session_id` followed by optional `estimate_id`; estimate requires Session.
Session is one accepted canonical local UUID and estimate is one exact #94
estimate ID. Query text is at most 8,192 UTF-8 bytes; empty, duplicate, unknown,
malformed, or reversed/standalone fields are `400 cost_invalid_query`. A
Session link is exactly
`/costs?session_id=<percent-encoded-canonical-session-id>`. An estimate link is
exactly that query followed by
`&estimate_id=<percent-encoded-exact-estimate-id>`, and the page resolves it
only through the exact-estimate API above. It never scans paged history to find
the estimate.

The page supports date/source/model/mode/status/registry plus exact
repository/workspace filters, component and coverage detail, configuration
preview/commit, exact bundled/local-override source and effective dates,
configuration/provider catalog match/changed state, explicit recalculation,
history/delta, budget state, the selected range's estimated and explicitly
provisional partial totals, and a UTC daily trend. Every monetary total/trend is
visibly labeled with its exact currency and registry version; the page never
combines their rows or derives a total from the current component page. It also supports loading,
empty, incomplete, stale, running, failed, and unavailable states. An
incomplete analytics acquisition announces its exact cap/lower-bound state and
withholds all range/daily totals; it never announces a global zero, total,
latest, or top result. Keyboard order,
visible focus, native labels, status live regions, and reduced-motion behavior
are required. Full canonical history or raw evidence is never retained in
long-lived browser state.

Repository/workspace inputs submit the exact accepted label without
normalization or inference, and each component row preserves those two
dimensions separately from null/unknown. Exact estimate presentation includes
its catalog SHA plus registry review and stale-after dates. It shows a stale
registry-metadata warning exactly when the UTC date of
`calculation_time_utc` is later than `stale_after_date`, matching the #94
estimation time basis. An `included_zero_incremental_cost` component is labeled
as zero additional cost and never as a free plan, seat, or subscription.

The browser keeps at most one configuration/preview, one catalog page of at most
100 entries plus at most 64 source projections, one 100-target/eight-scope
recalculation projection, one 100-item recalculation-history page, one 100-item
Session-estimate-history page, one exact estimate, and one 100-group analytics
page. The page renders every failed/unavailable/successful retry from the
recalculation-history route with its exact run link. It stores no cost data in local/session
storage, IndexedDB, URL fragments, caches, or service workers. Every filter,
catalog/configuration refresh, cursor, recalculation polling transition, or
context-link change increments a
request generation, aborts prior GET/poll fetches, and applies a response only
when generation plus returned snapshot/cursor/context identity still match.
Mutation POSTs are serialized and never client-aborted after dispatch; controls
remain disabled until a valid response or transport ambiguity. Ambiguous
configuration commit is reconciled by replaying the exact commit request, and
ambiguous recalculation start by replaying its exact idempotency key/request.
Generation still prevents an older mutation response from rendering over newer
state. Commit/recalculation success invalidates configuration, history,
analytics, and Alert Center-derived cost context before refetch. A late or
failed response cannot overwrite a newer loading/success/failure state.
For one accepted `requested | running` recalculation, the local browser polls
the exact run resource at a fixed 100 ms cadence for at most 40 successful GET
observations; a generation-aborted fetch that yields no projection is not an
observation. If the 40th observation is still nonterminal, the browser stops
polling without cancelling, failing, succeeding, or otherwise mutating the
server run, presents the exact local state
`polling_stopped · retryable`, releases the serialized mutation, and re-enables
its controls. That local state is neither a terminal run result nor a transport
failure. The enabled controls permit an explicit retry, and a subsequent
current-filter/history read permits explicit readback of the still-server-owned
run; the browser performs neither action silently.

## Security and retention

Pricing configuration and canonical estimates are metadata-only local runtime
records. They use the Session aggregate's retention lifetime: Session deletion
is blocked while exact pricing history refers to it, and no repository/path
heuristic cascade exists. Catalog/estimate bytes are not raw prompt content and
create no raw Retention item; they are nevertheless omitted from sanitized
evidence export v1 and repository-safe evidence.

DTOs, UI, alerts, logs, errors, and evidence must not contain raw prompts,
responses, system prompts, tool arguments/results, source/file bodies,
credentials, account/organization/private-contract identifiers, invoices, PII,
private source locators, or local paths. Store/adapter/JSON/SQLite exception
text is never reflected. Source labels/references are bounded Issue #94 values
rendered inertly. Public hashes prove integrity, not authenticity.

## Validation and release

Issue #95 owns direct active rows:

- `91-A-095`: automated pricing persistence, recalculation, API/UI, alert v2,
  lifecycle/Alert Center, migration/export/backup, accessibility, and
  Playwright;
- `91-S-095`: strict identity, canonical-byte, API, no-leak, private-override,
  malformed/tamper/future-version, archive, and scanner coverage; and
- `91-L-095`: genuine GitHub Copilot and Claude Code source/version-to-estimate
  and budget-receipt readback.

Issue #95 was never a future-registry entry; the canonical future registry
remains unchanged. `91-A-095` and `91-S-095` must pass. If the repository is
correct but reviewed positive source mappings remain unavailable,
`91-L-095` is `blocked_external/high` with exact retry condition and unverified
capability, yielding `release_ready_with_external_blockers`. Synthetic
execution never promotes that live row.

Required automated proof includes exact v1 alert golden compatibility, v2
canonical/store/read behavior, disabled/configured budget rules, insufficient
coverage suppression, estimated zero, partial/unknown/subscription/Codex/stale/
mixed-registry states, append-only recalculation and retry history, fresh and
supported-upgrade migrations, backup/restore round-trip, sanitized-export v1
coexistence, API security/status mapping, Playwright/accessibility, full build
and tests, repository-safe scans, and artifact checksums.

The evidence-chain verifier must resolve `matrix_prep_sha` to an exact commit
that is an ancestor of the frozen candidate. The committed live-validation
record binds both SHAs, the required command results, actual RED/failure
history, and OS-specific security coverage. Unsupported-OS tests are explicit
not-applicable skips; failure to create a required symlink/reparse/FIFO
prerequisite on the applicable OS is an explicit skip and prevents
`91-S-095=passed`.
