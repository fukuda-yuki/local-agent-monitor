# Tool Alert Rules

## Scope

This specification defines Issue #81: five source-neutral, deterministic
`IAlertRule` implementations over the Issue #80 `alert.snapshot.v1` contract.
The rules consume normalized signals only. Source-specific parsing, token/cache
rules, alert lifecycle and mutation, Alert Center presentation, and
model-generated recommendations are outside this interface.

All five rule IDs use rule version `1` and apply to `github-copilot-vscode`,
`github-copilot-cli`, `claude-code`, `codex-app`, and `codex-cli`. The compiled
handoff is `ToolAlertRulePack.CreateRules()`; consumers construct the Issue #80
registry from that list without changing receipt, evaluator, evidence, or store
contracts.

## Shared Input Vocabulary

The following names are fixed metadata tokens at the normalized snapshot
boundary. A producer declares a capability `available` only when every signal
fact required by that capability is authoritative. Missing per-signal facts do
not become defaults.

| Type | Fixed names and values |
| --- | --- |
| capability | `canonical-tool-arguments`, `explicit-retry-classification`, `stable-tool-ordering`, `tool-name`, `tool-ownership`, `tool-call-key`, `tool-call-ordering`, `tool-call-status`, `tool-retry-attempt`, `terminal-run-status`, `explicit-permission-duration`, `file-access-key`, `file-access-ownership`, `file-operation-type`, `file-access-ordering` |
| comparable key | `argument-hash`, `tool-name`, `ownership-key`, `retry-kind`, `tool-key`, `retry-chain-key`, `event-kind`, `file-key`, `operation-type`, optional `range-key` |
| comparable value | `retry-kind`: `none` or `explicit`; terminal `event-kind`: `terminal-run-status`; `operation-type`: `read`, `search`, `edit`, `watch`, or `poll` |
| metric | `retry-attempt` / `attempts`; `wait-duration` / `seconds` |

`argument-hash`, `tool-key`, `retry-chain-key`, `file-key`, and `range-key` are
opaque `sensitive_hmac` comparables. `tool-name`, `ownership-key`, `retry-kind`, and
`operation-type` are bounded `metadata_token` comparables. Raw tool names,
arguments, results, file names, paths, ranges, and malicious captured content
never enter rule output. A rule match contains numeric observed values and the
exact snapshot evidence references only.

The rules use signal `sequence`, then observation time and signal ID only as the
Issue #80 canonical tie-break. A `parent_signal_id` is an exact relationship,
not a proximity inference.

`historical_summary_only` and `ingest_gap` do not cause blanket suppression.
They suppress only an evaluation that needs facts which the incomplete interval
cannot establish. An observed positive count remains usable where additional
missing signals cannot invalidate the positive condition. Missing required
capabilities remain owned by the Issue #80 engine and use
`missing_required_capability`.

All five descriptors have trace scope. A snapshot without an exact trace ID
uses the declared bounded suppression `trace-scope-unavailable`; no trace rule
is evaluated and no match is returned.

## `repeated-identical-tool-call` Version 1

- scope/window: trace / `trace`
- required capabilities: `canonical-tool-arguments`,
  `explicit-retry-classification`, `stable-tool-ordering`, `tool-name`,
  `tool-ownership`
- grouping keys: `argument-hash`, `ownership-key`, `tool-name`
- threshold: `identical-call-count` / `calls`, inclusive bounds 1..100000,
  warning 3, critical 5
- rule suppression: `incomplete-signal-facts`

Only `tool_call` signals with exact `argument-hash`, `tool-name`, `ownership-key`, and
`retry-kind=none` participate. `retry-kind=explicit` is excluded so it cannot
duplicate `unrecovered-retry-chain`. Separate ownership keys, including
parallel branches, never combine. Each qualifying group at or above warning
creates one match with all and only that group's tool-call evidence. A missing
key on a relevant signal is incomplete rather than a near-match.

## `unrecovered-retry-chain` Version 1

- scope/window: trace / `trace`
- required capabilities: `tool-call-key`, `tool-call-ordering`,
  `tool-call-status`, `tool-retry-attempt`, `terminal-run-status`
- grouping keys: `tool-key`, `retry-chain-key`
- threshold: `retry-chain-length` / `attempts`, inclusive bounds 2..100000,
  warning 2, critical 3
- rule suppressions: `incomplete-signal-facts`, `unknown-terminal-status`

A chain consists of ordered `tool_call` signals sharing exact `tool-key` and
`retry-chain-key`, each with a positive integral `retry-attempt`. Version 1
requires attempts to start at 1 and increase contiguously by 1; a non-1 start,
gap, duplicate, or decrease is `incomplete-signal-facts`. It qualifies only
when a failed attempt has a later attempt. A final `success` is recovered and
emits no alert. A final `error` is unrecovered; length 2 is warning and length 3
or more is critical. A `session_event` raises an unrecovered chain to critical
only when `status=error`, `event-kind=terminal-run-status`, and
`parent_signal_id` exactly equals the final attempt signal ID. A generic linked
error event is not a terminal-run marker and does not escalate severity. Final
`unknown` or `cancelled` never becomes a failure and uses
`unknown-terminal-status`. An incomplete interval cannot prove that a final
error was unrecovered and uses `incomplete-signal-facts`. Once a tool signal
identifies a `retry-chain-key`, its exact `retry-attempt` is mandatory. A
chain-identified signal without that metric before, between, or after observed
attempts makes terminality incomplete and suppresses the rule.

## `high-tool-failure-ratio` Version 1

- scope/window: trace / `trace`
- required capability: `tool-call-status`
- grouping: trace
- threshold: `failure-ratio` / `ratio`, inclusive bounds 0..1, warning 0.40,
  critical 0.70
- fixed minimum known sample: 5 calls
- rule suppressions: `minimum-sample-unmet`, `incomplete-signal-facts`

The authoritative denominator is the number of `tool_call` signals whose
status is `success` or `error`; the numerator is the `error` subset. `unknown`
and `cancelled` are excluded from both and never become failures. A match records
`failure-count` / `calls`, `known-call-count` / `calls`,
`total-tool-call-count` / `calls`, `status-coverage` / `ratio`, and
`failure-ratio` / `ratio`, and carries every `tool_call` evidence reference,
including `unknown` and `cancelled` calls that contribute to total count and
coverage. Fewer than five known calls uses `minimum-sample-unmet`. Because gaps can change the
authoritative denominator, `historical_summary_only` or `ingest_gap` uses
`incomplete-signal-facts` before ratio evaluation.

## `excessive-permission-wait` Version 1

- scope/window: trace / `trace`
- required capability: `explicit-permission-duration`
- grouping: trace
- thresholds:
  - `individual-wait` / `seconds`, inclusive bounds 0..86400, warning 30,
    critical 120
  - `total-wait` / `seconds`, inclusive bounds 0..604800, warning 60,
    critical 300
- rule suppressions: `trace-scope-unavailable`,
  `incomplete-signal-facts`, `duration-out-of-range`

Only `permission` signals with one finite non-negative `wait-duration` metric in
seconds participate; missing start/end is not inferred. Evaluation requires an
exact snapshot trace ID and totals waits across that trace, never across the
Session. The observed values always include exact `maximum-wait` / `seconds`.
They include exact `total-wait` / `seconds` only when every permission duration
fact and the evaluated history interval are complete and the mathematical
decimal sum is exactly representable. Otherwise, a max-triggered match includes
`total-wait-lower-bound` / `seconds`: the greatest of the exact subtotal proven
before representability is lost and the exact maximum individual wait. These
total names are mutually exclusive. Exact accumulation detects both range
overflow and fractional precision loss and is independent of operand order.

Severity is critical when either effective critical threshold is met, and
warning when either effective warning threshold is met. Total thresholds are
used only with complete duration/history facts; an incomplete or partial sum
never drives warning or critical severity even when its lower bound crosses a
total threshold. Exact maximum-based severity remains conclusive, and the rule
also returns `incomplete-signal-facts`. With the defaults, an exact individual
wait at or above 120 seconds is critical. The inclusive maxima in the threshold
schema bound configuration, not observations. A positive exact duration is
never suppressed for being large, and an unrepresentable positive total never
suppresses an already conclusive match. Negative duration uses
`duration-out-of-range`. Exact evidence is every known permission wait that
contributes to the reported maximum, exact total, or lower bound. Arithmetic
never wraps, silently rounds, or escapes as an evaluator exception.

## `repeated-file-read-or-search` Version 1

- scope/window: trace / `trace`
- required capabilities: `file-access-key`, `file-access-ownership`,
  `file-operation-type`, `file-access-ordering`
- grouping keys: `file-key`, `ownership-key`, `operation-type`, optional
  `range-key`
- threshold: `access-count` / `accesses`, inclusive bounds 1..100000,
  warning 3, critical 5
- rule suppression: `incomplete-signal-facts`

Only `file_access` signals with exact `file-key`, `ownership-key`, and
`operation-type=read` or `search` participate. The source-neutral
`ownership-key` is the exact normalized owner, parent, or branch identity; it
must not be inferred from ordering or proximity. `watch` and `poll` are
excluded. When present, exact `range-key` is part of the group, so distinct
ranges/chunks never combine. Its absence is an authoritative whole-file/no-range
group, not an incomplete fact. An exact `operation-type=edit` resets read/search
segments only for the same `file-key` and `ownership-key`; edits for another
file or parallel ownership branch do not reset it.
Each segment/group at or above warning creates one match containing only that
group's exact evidence. A missing file, ownership, or operation fact is
incomplete rather than a near-match and suppresses the whole trace evaluation,
because the missing signal could be an intervening branch-local edit. An absent
`range-key` is valid, but a present `range-key` with any representation other
than `sensitive_hmac` is an incomplete signal fact. Likewise,
`historical_summary_only` or `ingest_gap` can hide an intervening edit and
always suppresses this rule, even when the observed count reaches a threshold.

## Determinism, Evidence, And Rule Relationships

Rules iterate the Issue #80 canonical signal order and sort matches by their
exact grouping identity before returning them. Numeric observed values use the
fixed names/units above. No dictionary enumeration order, current time, random
ID, source-specific field, raw captured value, local path, or exception text is
an input.

`repeated-identical-tool-call` excludes explicit retry signals and
`unrecovered-retry-chain` evaluates retry attempts, so the two rules do not
duplicate one retry chain. Other rules may legitimately reference the same
evidence because they report different deterministic conditions; Issue #80
deduplicates only identical rule matches, and Issue #83/#84 may present related
receipts without merging their immutable identities.

Every match has at least one exact reference from the normalized snapshot. The
Issue #80 evaluator still performs exact persisted resolution and rejects the
proposed receipt if any reference does not resolve.
