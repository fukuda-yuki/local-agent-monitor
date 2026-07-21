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
| capability | `canonical-tool-arguments`, `explicit-retry-classification`, `stable-tool-ordering`, `tool-name`, `tool-ownership`, `tool-call-key`, `tool-call-ordering`, `tool-call-status`, `tool-retry-attempt`, `explicit-permission-duration`, `file-access-key`, `file-operation-type`, `file-access-ordering` |
| comparable key | `argument-hash`, `tool-name`, `ownership-key`, `retry-kind`, `tool-key`, `retry-chain-key`, `file-key`, `operation-type`, optional `range-key` |
| comparable value | `retry-kind`: `none` or `explicit`; `operation-type`: `read`, `search`, `edit`, `watch`, or `poll` |
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
  `tool-call-status`, `tool-retry-attempt`
- grouping keys: `tool-key`, `retry-chain-key`
- threshold: `retry-chain-length` / `attempts`, inclusive bounds 2..100000,
  warning 2, critical 3
- rule suppressions: `incomplete-signal-facts`, `unknown-terminal-status`

A chain consists of ordered `tool_call` signals sharing exact `tool-key` and
`retry-chain-key`, each with a positive integral `retry-attempt`. It qualifies
only when a failed attempt has a later attempt. A final `success` is recovered
and emits no alert. A final `error` is unrecovered; length 2 is warning and
length 3 or more is critical. A `session_event` with `status=error` and
`parent_signal_id` equal to the final attempt signal ID also raises an
unrecovered chain to critical. Final `unknown` or `cancelled` never becomes a
failure and uses `unknown-terminal-status`. An incomplete interval cannot prove
that a final error was unrecovered and uses `incomplete-signal-facts`.

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
`failure-ratio` / `ratio`, and carries all known-call evidence. Fewer than five
known calls uses `minimum-sample-unmet`. Because gaps can change the
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
  `incomplete-signal-facts`

Only `permission` signals with one finite non-negative `wait-duration` metric in
seconds participate; missing start/end is not inferred. Evaluation requires an
exact snapshot trace ID and totals waits across that trace, never across the
Session. The observed values are `maximum-wait` / `seconds` and `total-wait` /
`seconds`. Severity is critical when either critical threshold is met, warning
when either warning threshold is met. Exact evidence is every permission wait
that contributes to the triggering maximum or total. An individual threshold
crossing remains conclusive in an incomplete interval; a total-only conclusion
requires a complete interval.

## `repeated-file-read-or-search` Version 1

- scope/window: trace / `trace`
- required capabilities: `file-access-key`, `file-operation-type`,
  `file-access-ordering`
- grouping keys: `file-key`, `operation-type`, optional `range-key`
- threshold: `access-count` / `accesses`, inclusive bounds 1..100000,
  warning 3, critical 5
- rule suppression: `incomplete-signal-facts`

Only `file_access` signals with exact `file-key` and `operation-type=read` or
`search` participate. `watch` and `poll` are excluded. When present,
`range-key` is part of the group, so distinct ranges/chunks never combine.
An exact `operation-type=edit` for the same `file-key` resets every read/search
segment for that file at that sequence; edits for other files do not reset it.
Each segment/group at or above warning creates one match containing only that
group's exact evidence. A missing required key is incomplete rather than a
near-match.

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
