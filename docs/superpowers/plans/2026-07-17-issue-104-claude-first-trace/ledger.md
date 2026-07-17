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
| T0 docs/specs/contract table | in_progress | — | n/a (docs) | — | pending self-review record |
| T1 red cross-surface test | pending | — | — | — | — |
| T2 fact mapper | pending | — | — | — | — |
| T3 store reads | pending | — | — | — | — |
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
- OPEN (T-109): if the fix introduces a new persisted/user-visible
  `source_adapter` label or wire vocabulary, update
  `source-schema-drift-claude-code.md` / `exact-binding.md` first (spec-first
  rule). The projection rule itself ("shared `trace_id` never projects
  `exact_linked`") is already normative in `exact-binding.md`.

## Collision-file edit log

| File | Task | Change | Source-neutral? |
| --- | --- | --- | --- |
| `docs/specifications/interfaces/configuration-setup.md` | T0 | Claude completion boundary + `run_first_trace_doctor` row activation | Claude-scoped rows only; no Copilot text touched |

## Review findings log (Minor items for final review triage)

- (none yet)
