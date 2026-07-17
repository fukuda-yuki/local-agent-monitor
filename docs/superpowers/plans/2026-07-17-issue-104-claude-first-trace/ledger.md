# Issue #104 durable ledger

Continuously updated by the orchestrator after every delegation, review, and
integration step. Trust this file plus `git log` over conversation memory.

- Kickoff: `main@920ff43`, branch `claude/issue-104-implementation-447584`,
  worktree `.claude/worktrees/issue-104-implementation-447584`.
- Delegation default: Codex GPT-5.6 Luna, reasoning xhigh; reviewers are
  separate fresh runs.

## Task states

| Task | State | Commits | Focused tests | Full test | Review |
| --- | --- | --- | --- | --- | --- |
| T0 docs/specs/contract table | revised | b43ed58 + revision commit | n/a (docs) | — | Codex Luna xhigh read-only review #1: REVISE (1 Critical, 8 Important — all resolved in revision; log below); re-review pending |
| T1 red cross-surface test | pending | — | — | — | — |
| T2 fact mapper | pending | — | — | — | — |
| T3 store reads | done | 07f44c1 | Store 58/0, AppService 9/0, Migration 12/0 | solution 5554/0 by implementer | independent review pending (batch with T4) |
| T4 binding rule + observer | pending | — | — | — | — |
| T5 fact collector | pending | — | — | — | — |
| T6 first-trace verbs + adapter | pending | — | — | — | — |
| T-109 projection defect fix | pending | — | — | — | — |
| T7 cross-surface green | pending | — | — | — | — |
| T8 104-E matrix | pending | — | — | — | — |
| T9 freeze + handoff | pending | — | — | — | — |

## Open items / unverified inter-issue interfaces

- OPEN: confirm at T4 that the Doctor verification tables and the ingestion
  pipeline tables share one SQLite database file reachable from both the
  LocalMonitor process (observer writes) and the ConfigCli process (reads);
  record the actual path wiring here.
- OPEN (#110, hand to #106): live producer behavior for `user_prompt` key when
  `OTEL_LOG_USER_PROMPTS` is off — input conditions recorded in
  `handoff-106.md` at T9.
- OPEN (#103 collision watch): every collision-file edit is recorded below
  with a source-neutrality note.
- OPEN (T6): when `run_first_trace_doctor` emission lands, update the derived
  user-facing docs that still say it is never emitted: `README.md` (~L178),
  `docs/user-guide/local-monitor.md` (~L226), `scripts/local-monitor/README.md`
  (~L65). Consider a new `docs/decisions.md` entry at T9 (the #68 entry
  D-record correctly describes #68's own boundary and stays as history).
- OPEN (T4/T5): the monitor runtime-state row is a monitor-DB schema addition
  → implement via the existing monitor migration mechanism, with a committed
  prior-version DB fixture migration test (monitor-side write lands with T4,
  CLI read with T5).
- OPEN (T6): internal atomic exclusive-start store operation (review finding
  9) added to T6 scope.
- OPEN (T-109): if the fix introduces a new persisted/user-visible
  `source_adapter` label or wire vocabulary, update
  `source-schema-drift-claude-code.md` / `exact-binding.md` first (spec-first
  rule). The projection rule itself ("shared `trace_id` never projects
  `exact_linked`") is already normative in `exact-binding.md`.

## Collision-file edit log

| File | Task | Change | Source-neutral? |
| --- | --- | --- | --- |
| `docs/specifications/interfaces/configuration-setup.md` | T0 | Claude completion boundary + `run_first_trace_doctor` row activation | Claude-scoped rows only; no Copilot text touched |

## Review findings log

T0 review #1 (Codex Luna xhigh, read-only, on b43ed58): VERDICT REVISE.
Resolutions applied in the T0 revision commit:

1. (Critical) begin pre-window evaluation would always hit
   `partial_fact_snapshot` → begin now pins the Doctor contract's own
   pre-trace family values (`none`/`not_persisted`/`not_started`/
   `(not_required, not_applicable)`/completeness `unknown`), verified against
   `DoctorTestSnapshots.ReadyNoRealTrace()` and `DoctorValidation`
   cross-field rules.
2. readiness probe cannot separate no-listener from foreign owner → the fact
   collector performs its own `/health/live` probe with the setup contract's
   three-way classification.
3. higher-precedence observer insufficient for effective values → new
   read-only effective-value resolver (setup precedence, per-key
   effective/absent/conflict) pinned in the spec.
4. no read path for DB path / raw-access mode → `--database` argument with
   doctor-verb semantics; new source-neutral monitor runtime-state row for
   `raw_access`, trusted only when the liveness probe is monitor-live.
5. fixed `claude-code-otel` adapter vs raw-otlp exact binds → explicit v1
   candidate-eligibility rule (recognized claude-code provenance only;
   binding state stays valid; provenance never rewritten; documented
   residual).
6. valid-but-not-ready completion had no envelope code →
   `first_trace_not_ready` (success=false, exit 3) added.
7. trace-keyed chain selection could mix Sessions → session-keyed grouping
   with explicit-selection on any ambiguity (incl. one trace claimed by two
   Sessions).
8. `--expires-at` default undefined → exactly 10 minutes from the Doctor
   store clock at start.
9. active-check + start race → internal atomic exclusive-start inside one
   store transaction (spec'd under Internal store operations); adds a small
   internal store write to T6 scope.

Minor items for final review triage: (none yet)
