# Local Monitor v1 Product Definition

Status: **Accepted**  
Product owner approval: 2026-07-30  
Parent: Issue #117  
IA specification: [`docs/specifications/interfaces/local-monitor-v1-ia.md`](local-monitor-v1-ia.md)

This document supersedes the earlier defect-ledger draft and the retired Revision 12 IA requirements. Git history and GitHub work items retain implementation history; they do not redefine this current product contract.

UI language is Japanese. This document is agent-facing and is therefore written in English.

## Product statement

Local Monitor is a loopback-only, single-user application that reconstructs what was actually captured from local AI coding-agent Sessions and presents it at the level of the controls the user owns.

It has two independent layers:

1. an AI-independent observation and investigation core;
2. optional AI analysis and AI improvement suggestions, using GitHub Copilot SDK in v1.

The product remains useful and complete when no LLM, provider authentication or API key is available.

## Primary user job

The user can select a local Repository, find a Session, understand its execution, and descend to exact evidence without needing Trace/Span expertise.

The core must expose, when captured and authorized:

- Session instruction, source, status and timing;
- input, output and total Token values;
- cache reads, new input and cache creation/write values;
- Skill invocations;
- Tool calls, inputs, results, failures and retries;
- Sub-agent lifecycle, input, activity and Token values;
- Agent/Sub-agent/Skill/Tool parentage, timing and parallelism;
- errors, permissions and recovery;
- exact Session / Run / Trace / Span / Event / raw-record references;
- honest capture, source-support, certification and expiry states.

Missing information is never converted to zero or inferred from proximity, names or free text.

## Repository-first organization

The user-facing hierarchy is:

```text
Repository selection
  -> Session Explorer
      -> Session detail
      -> explicit cohort comparison
```

Repository identity is a Local Monitor UUIDv7, not a display name, path or URL. Exact supported locators and manual user assignment are separate provenance. Sessions with no exact assignment remain reachable through an unassigned virtual scope.

Repository and Session archive are reversible visibility/selection states. Archive is not deletion, retention or pinning.

## Session detail

Session detail is an execution workspace with a contextual inspector.

The top summary separates:

- `トークン合計`: input and output;
- `入力トークンの内訳`: cache read, new input and optional cache write/creation.

The execution view combines semantic hierarchy and timing in one hierarchical timeline. It does not split the same evidence into separate tree and waterfall pages.

The inspector shows Tool, Skill, Sub-agent, Error, Permission or Event detail according to the selected exact node. Raw bodies and technical identifiers are on-demand local detail, not normal list data.

A historical Skill snapshot and the current file are distinct objects. The current file is never presented as the content used by a past Session without an exact historical snapshot.

## Cross-Session comparison

The core includes an AI-independent comparison of two explicit Session cohorts.

Comparison:

- uses exact Session IDs, explicit filter snapshots, or exact Skill-snapshot digest boundaries;
- calculates a fixed registry of Token, cache, timing, execution-volume, Skill, Tool, Sub-agent, Error and Retry facts;
- displays per-Session medians, ranges and available denominators;
- exposes all named rows through search/pagination rather than top-N ranking;
- provides exact drill-down;
- does not use an LLM to calculate values, select “important” differences or generate warnings;
- does not display quality evidence, an aggregate score, anomaly ranking or an improvement/effect verdict.

An optional AI action may interpret the deterministic comparison receipt, but it cannot recalculate the comparison.

## Optional AI

The v1 provider is GitHub Copilot SDK.

AI is visible only when the provider is ready and starts only from an explicit user action.

Supported scopes:

1. whole Session;
2. one exact Session node;
3. an explicit Repository Session selection;
4. an accepted deterministic comparison snapshot.

The Local Monitor server creates bounded immutable snapshots and process-internal read tools. The provider never receives the SQLite file or arbitrary SQL access and cannot explore outside the accepted scope.

AI output is a separate interpretation surface containing:

- scope and snapshot;
- summary;
- evidence-backed findings;
- concrete improvement suggestions;
- limitations;
- provider/model/template provenance;
- exact evidence navigation.

AI output is not an observed timeline node and is not treated as product fact.

Persistence differs by scope:

- whole-Session primary reports have durable immutable history;
- node results are transient;
- Repository-selection and Compare results have bounded operational persistence and no permanent history;
- follow-up Q&A is not persisted in v1.

## Settings and operation

Normal investigation pages remain uncluttered. Receiver state and Settings open one Unified Settings modal with sections for:

- state;
- receiver;
- AI;
- Repository management;
- archive;
- storage/backup;
- diagnostics.

Complex or destructive operations may open focused details or confirmations. There is no permanent Settings dashboard or nested product navigation.

## Raw and sanitized posture

The human Local Monitor UI is a raw-default local surface for one trusted user.

Raw-local reads remain loopback, same-origin, no-store, retention-authorized and escaped inert text. Raw content, paths, credentials and PII must not leak to logs, URLs, repository artifacts or sanitized machine APIs.

`--sanitized-only` is a receiver-only posture:

- ingest, health and accepted machine APIs remain available;
- Razor Pages, static human UI and raw-local UI APIs are not registered;
- there is no per-screen metadata-only fallback UI.

Existing frozen `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE bytes remain unchanged.

## Claim discipline

The UI distinguishes:

- value recorded;
- no positive observation in this record;
- source does not provide the signal;
- capture/projection gap;
- value recorded but certification pending;
- raw content not captured, expired, malformed or oversized;
- invalid projection or inconsistent derived values.

The product does not infer:

- Repository identity from name/path/time proximity;
- Session identity from Repository/time proximity;
- Agent or parent identity from labels/timing;
- Skill non-use from a missing event;
- Sub-agent instruction from unrelated Tool input;
- zero from missing values.

## Out of scope for the primary Local Monitor v1 journey

- manual Human or Objective Evaluation workflow;
- manual persisted evidence selection;
- an empty improvement-proposal form or proposal draft;
- Candidate/Recommended-centered manual proposal lifecycle;
- automatic file apply or rollback;
- automatic effect verdict;
- automatic prioritization, composite score or anomaly ranking;
- a generic Repository KPI/usage dashboard;
- a permanent AI or saved-comparison top-level destination;
- multiple AI providers or generic API-key management in v1.

Existing Canvas/store/API functionality for evaluation, proposal, apply, rollback or effect measurement is not deleted by this definition. It is not part of the primary Local Monitor v1 IA.

## Contract authorities

- #129: source truth and claim catalogue
- #133: Repository/Session Workspace reads
- #155: Repository catalog and assignment
- #157: Skill invocation snapshot
- #159: sanitized-only receiver posture
- #160: archive
- #162: optional AI
- #165: deterministic comparison
- #132: final IA, route, state, wording and dimensions
- #169: sentence-level Japanese microcopy after initial integration

## Implementation prerequisites

Telemetry/projection defects that prevent the accepted UI from making valid claims remain implementation prerequisites, including the relevant Skill, Sub-agent, failure, source-capability and source-identification issues. Pre-release Skill projection data does not require compatibility or backfill solely for preservation.
