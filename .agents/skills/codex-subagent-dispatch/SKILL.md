---
name: codex-subagent-dispatch
description: Use this skill when delegating work to Codex subagents. It provides mission card templates for reader and writer agents, including scope, permissions, output format, and stop conditions.
---

# Codex Subagent Dispatch

Use this skill when the task should be delegated to one or more Codex subagents.

The main Codex chat remains the coordinator. Subagents are bounded workers.

If this repository-local skill is not auto-discovered by the active Codex surface, reference this
file explicitly or install the skill into the configured Codex skills location before relying on it.

## Agent selection

Use the smallest sufficient agent.

### Reader agents

- `spark-reader`: fast read-only scouting
- `high-reader`: careful read-only analysis
- `max-reader`: maximum-effort read-only analysis for high-risk or ambiguous issues

### Writer agents

- `spark-writer`: tiny low-risk edits
- `high-writer`: normal implementation, tests, and documentation edits
- `max-writer`: hard, risky, architecture-sensitive, or subtle correctness work

## Reader Mission Card

When delegating to a reader agent, use this template:

```text
Use <agent-name>.

Mission:
<What the agent must determine.>

Scope:
<Directories, files, features, or behavior to inspect.>

Known context:
<Only the context needed for this task. Do not paste the entire conversation.>

Do not touch:
<Files, areas, or decisions that are out of scope. File modification is prohibited.>

Output needed:
- Summary
- Relevant files and symbols
- Evidence
- Risks or unknowns
- Recommended next step

Stop condition:
<When the agent should stop and report instead of continuing.>
```

## Writer Mission Card

When delegating to a writer agent, use this template:

```text
Use <agent-name>.

Mission:
<The specific change to implement.>

Owned write scope:
<Files or directories the agent may modify.>

Allowed read scope:
<Files or directories the agent may inspect.>

Do not touch:
<Files, directories, behavior, contracts, or unrelated areas that must not be changed.>

Behavioral requirements:
<Compatibility, conventions, expected behavior, or constraints.>

Validation expected:
<Tests, commands, build checks, or manual validation expected.>

Output needed:
- Files changed
- What changed
- Validation performed
- Remaining risks

Stop condition:
<When the agent should stop and report instead of editing further.>
```

## Operating rules

* Do not delegate vague work.
* Do not give subagents the whole conversation unless necessary.
* Always define scope, permissions, output format, and stop condition.
* Prefer reader agents before writer agents when the implementation scope is unclear.
* Prefer `max-*` only when the cost is justified by risk or ambiguity.
* The main chat must integrate results and make final decisions.
