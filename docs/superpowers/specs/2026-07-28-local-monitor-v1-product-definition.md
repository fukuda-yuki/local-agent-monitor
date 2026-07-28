# Local Monitor v1 Product Definition and Defect Ledger

Status: proposed. Supersedes
`docs/superpowers/specs/2026-07-27-local-monitor-ia-rebuild-requirements.md`
(Revision 12), which is retired rather than revised.

This document defines what Local Monitor is for, what it is allowed to claim, and
which of its current shortcomings are engineering defects rather than limits of
the available evidence. It deliberately contains no information architecture. The
IA is derived from this document in a later step, once the prerequisite in
"Before the IA can be designed" is settled.

UI language is Japanese. This document is agent-facing and therefore written in
English, per `AGENTS.md`.

## Why Revision 12 is retired

Revision 12 failed in two ways that a thirteenth revision would not fix.

**It derived the screen hierarchy from the data model.** Every screen boundary
corresponded to a persisted object type or an API owner, so the user was
repeatedly asked "which data layer do you want?" when their question was "what
happened, and what should I do about it?". The consequences were structural: an
index page over 2-4 repositories, a five-step workflow rendered as a five-tab
strip, and the same information owned by three different screens.

**It recorded fixable defects as honest limitations.** Roughly half the document
was a vocabulary for stating that a value could not be produced. Much of what it
declared unavailable is not absent from the telemetry; it is discarded, unwired,
or uncertified by this product's own code. Writing that down as a permanent
limitation, in careful language, prevented anyone from fixing it.

A concrete instance: Revision 12 states that Skill identity is unavailable
because no explicit producer exists. The conclusion is correct. The stated reason
is false, and the false reason is what made the gap look permanent.

## Measured facts

Every claim in this section was checked against code or data on 2026-07-27/28.
Nothing here is inferred from a specification.

### What the owner's telemetry actually contains

Counted over a retained SQLite database of real GitHub Copilot CLI / BYOK
traffic (21 traces, 952 spans). This is one snapshot, not a population estimate.

| Field | Populated |
| --- | --- |
| `primary_model` | 21/21 |
| trace input / output / total tokens | 21/21 |
| `experiment_id` | 21/21 |
| `monitor_spans.tool_name` | 380/952, present in all 21 traces |
| `monitor_spans.conversation_id` | 346/952 |
| span token fields | 345/952 |
| `client_kind` | 6/21 |
| `repository_name` | **0/21** |
| `prompt_version` | **0/21** |
| `task_id`, `task_category`, `agent_variant` | **0/21** |
| `monitor_spans.agent_name` | **0/952** |
| Skill or sub-agent session events | **0** |

The load-bearing consequence: the exact values available to group or compare the
owner's real executions are model, tool name, and conversation id. Repository,
prompt version, task identity and agent name are not available in practice.

### What the source capability contract declares

`docs/specifications/contracts/source-capabilities/v1/manifests/` holds five
manifests. The owner's sources are `github-copilot-cli` and
`github-copilot-vscode`. Both declare `available` for trace and hook signals,
native session identity, trace/span identity and parentage, timing, and the
content capture gate; `unavailable` for the source version detector; and
`unknown` for every field of `model_tokens`, `tool_calls`, `errors`,
`agent_ownership`, `prompt_response`, `file_diff`, `retry_attempt` and
`permission`.

The manifest schema has **no capability category for Skills**.

All five manifests are byte-identical on the `agent_ownership` line and nearly
identical throughout. They are untouched defaults, not the result of per-source
investigation.

Token values are displayed by the product today while the contract for the
sources that supply them says `unknown`. A capability declaration and the
presence of data are therefore two different things, and the product currently
collapses four distinct states into one "unavailable" message:

1. unsupported by the source;
2. not observed on this record;
3. not captured because of configuration or an ingest gap;
4. observed in practice but not contractually certified.

### What the GitHub Copilot SDK actually emits

Verified directly against
`~/.nuget/packages/github.copilot.sdk/1.0.4/lib/net10.0/GitHub.Copilot.SDK.xml`.

- Skill events: `skill.invoked` only, carrying `name`, `path`, `description`,
  `pluginName`, `source`, `trigger`.
- Sub-agent events: `subagent.started`, `subagent.completed`, `subagent.failed`,
  `subagent.selected`, `subagent.deselected`.
- `GitHub.Copilot.Rpc.SkillsApi` exposes `ListAsync` and `GetInvokedAsync`.

`SessionEventNormalizer` allowlists `skill.started` and `skill.completed`
(`SessionEventNormalizer.cs:146`). Those two strings occur nowhere else in the
repository — no producer, fixture, test or document emits them. Real
`skill.invoked` events therefore normalize to `content_state=unsupported`, and
the payload, including the Skill name, is discarded. The same allowlist omits
`subagent.failed`, `subagent.selected` and `subagent.deselected`.

### Where the improvement cycle lives

Human Evaluation, proposal creation and promotion, apply draft / approval /
apply / rollback, and effect comparison all have complete stores and APIs. Their
only user interface is the GitHub Copilot **Canvas extension**. Local Monitor has
no entry point to any of them. Objective Evaluation has no interface anywhere;
the only way to record one is a direct `POST` to
`/api/session-workspace/objective-evaluations`.

Apply is additionally inert unless the monitor was started with `--apply-root`,
which the production launch path does not pass (`MonitorHost.cs:208`).

"The user selected this Span as evidence" is a JavaScript variable in the Canvas
extension. It reaches the database only when a proposal is created. One Session
may carry several candidate proposals and one proposal several apply drafts, and
no persisted pointer records which the user is advancing.

## Purpose

> **Local Monitor v1 is the complete loopback-only application for reconstructing
> what was actually captured from the user's AI coding-agent sessions, and
> presenting it at the level of the controls the user owns.**
>
> It presents exact record identity, parentage and timing, and presents token,
> tool, Skill and sub-agent activity **only when positively observed**. It
> distinguishes what the source does not support, what was not observed on a
> record, what was not captured, and what is observed but uncertified.
>
> It does not treat missing telemetry as proof of non-use, an AI judgement as
> evidence, or an observed pattern as proof of causation.

**Improvement is out of v1 scope.** Proposal creation and promotion, apply,
rollback and effect comparison are not v1 promises. Their stores and APIs exist
and Canvas remains their interface; Local Monitor gains them in a later stage,
once the internal model beneath them has been built up. This is a deliberate
scope decision, not an oversight, and it is why the improvement-state contract is
not a prerequisite for the v1 IA.

v1 also does not promise that the product understands the user's recurring
practices, identifies departures from a valid baseline, or proves that a
configuration change improved the agent.

### The organising principle

Lead with provable facts at the level of a control the user owns — an
instruction, a Skill, a custom agent, a model choice, a tool configuration.
Retain lower-level telemetry as inspectable evidence rather than presenting it as
a decision.

The user is not competent to judge whether an individual tool call was
appropriate, and the product has no counterfactual that would let it judge either.
The user *is* competent to judge whether a Skill they configured participated,
whether the work was decomposed the way they intended, and whether a run departed
from what they expected. Those are the facts v1 leads with.

### Product boundary: Local Monitor and Canvas

Local Monitor is the product. It must be complete on its own, because the user
works in GitHub Copilot CLI and VS Code Copilot Chat and must not be required to
open the GitHub Copilot App.

Canvas is an optional satellite inside the Copilot App, whose job is immediate
post-session reflection on the session the user has just run. Overlap between
Canvas and Local Monitor is intended and is not a defect. Revision 12's plan to
move UI ownership away from Canvas is retired; nothing is taken from Canvas.

Because improvement is out of v1 scope, the improvement cycle remains reachable
only through Canvas during v1. That is accepted for v1 and is not accepted
permanently: **when improvement enters scope, no capability required to complete
it may exist only in Canvas.**

## What the product may claim

Five layers. A statement may only be made at the layer its evidence supports.

| Layer | Membership | Permitted claim |
| --- | --- | --- |
| Exact identity | Native IDs and exact references | "This record is, or refers to, this Session / Trace / Span / receipt." |
| Positively observed | A captured event or a non-null measured value | "This was observed." Never "this is all that happened." |
| Conditionally complete | Observed **plus** a coverage statement for the interval and source | "Within captured coverage, this is the complete set." |
| Exploratory | Similarity, clustering, or an AI hypothesis | "These may be related; review them." Never evidence, priority, or cohort membership. |
| Unsupported | The source cannot supply it | "This source does not provide this." |

Two rules follow, and they replace Revision 12's single `算出不可` vocabulary:

- **An absence claim requires closed-world coverage.** "Skill X was not invoked"
  is valid only when the coverage statement proves such an invocation would have
  been captured. Otherwise the only honest statement is "no invocation of Skill X
  was observed".
- **The four unavailability states are distinguished in the UI.** Unsupported by
  the source, not observed on this record, not captured due to configuration or
  an ingest gap, and observed but uncertified are four different messages. Field
  names and `acquisition_state` are not user-facing vocabulary.

Similarity is not banned. It is banned from silently becoming fact: it may
nominate candidates for review, and may never establish record identity, evidence
lineage, cohort membership, alert eligibility, priority, or effect verification.

The existing hard constraints are unchanged and restated because they survive:
no composite score of any kind; no heuristic merge of Session identity; never
render unknown or missing as zero; observed fact, deterministic derivation and AI
finding are never mixed; loopback bind, Host validation, same-origin, CSRF,
`no-store` and escaped inert text; raw prompts, responses and tool arguments never
leave the local UI.

## Release stages

**Stage 1 — Observed-session review. This is the v1 release gate.** Reconstruct
what was captured and present it at control level: native Session identity, the
user's instruction, exact trace and span parentage, timing, captured hook events,
observed tool activity, observed sub-agent starts, and positively observed token
values — each carrying which of the four unavailability states applies when it is
absent. Largely supported by existing data.

**Stage 2 — Comparison against an explicitly selected population.** Opens when
the product can present a run's captured figures against a cohort the user
selected by hand, or a population it declares exploratory, with coverage stated.
This is the smallest honest form of "this run looks unusual" and needs no new
producer.

**Stage 3 — Evidence-grade recurring-practice comparison.** Opens when a versioned
`case_key` is declared at invocation and propagated to resulting runs and
delegations, so the user confirms once per recurring practice rather than
labelling every run. Only then may the product say "this run used materially more
captured tokens than previous runs of this same declared practice".

**Stage 4 — Certified absence claims.** Opens when all of the following exist: a
capability category for the relevant control including Skills; an active
configuration inventory recording what was configured and available at the time
of each run; proof that invocation events would have been emitted; proof that
capture was healthy for the interval; and source version or schema-fingerprint
coverage. Only then may the product say "configured Skill X was not invoked".

For GitHub Copilot CLI specifically, `SkillsApi.ListAsync` may supply the active
configuration inventory directly, which would open this stage earlier for that
source alone. Stage boundaries are per-source, not global.

**Stage 5 — Improvement inside Local Monitor.** Opens when the improvement-state
contract below is settled: persisted evidence selection; an explicit rule for
which proposal and which apply draft the user is advancing; human-reachable
Objective Evaluation; and a production apply root or a clearly stated fail-closed
configuration state. Deliberately last, because the internal model beneath it is
not built up enough to promise it now.

## The owner's four wants

| Want | Verdict | What v1 may say |
| --- | --- | --- |
| Which Skills fired | **Reachable for Copilot CLI, pending a spike; blocked for VS Code Copilot Chat** | Copilot CLI has no skill hook event, so hooks can never carry this. The `GitHub.Copilot.SDK` package this product already references exposes session enumeration plus `SkillsApi.ListAsync` and `GetInvokedAsync`, giving a non-Canvas path — unproven until the spike below. Separately, correcting the allowlist to `skill.invoked` recovers Skill names on the SDK event stream, which benefits Canvas users today. VS Code Copilot Chat has no known channel for this. |
| How many sub-agents, and what they were asked | **Counts partly deliverable; task text blocked** | "Observed sub-agent starts: N", with names when the payload carried them. Not "the number of sub-agents" without identity coverage. The delegated task text has no dedicated field in any source; only best-effort extraction from raw tool arguments exists. |
| This run used abnormally more tokens than usual | **Stage 2, not v1** | Compare captured token counts against an explicitly selected cohort, or a population declared exploratory, and report delta, rank or percentile with coverage. Never "compared with the same task as usual" before `case_key`. v1 shows the run's own captured figures with their coverage, and no comparison. |
| An AI that frankly says what to change | **Out of v1 scope** | The Copilot analysis drawer already exists over the trace screen and v1 neither extends nor removes it. When this does become a promise, it is an evidence-linked hypothesis — what to change, the observation motivating it, the expected effect, and what later result would falsify it — never "your instruction was bad" or "this tool call was wasted" as established fact. |

The required v1 vocabulary follows from the table: "captured token count", not
"total tokens"; "observed Skill invocation", not "Skills used"; "observed
sub-agent starts", not "number of sub-agents"; "proposal", not "finding";
"comparison", not "effect"; "no invocation observed", not "was not invoked".

## Defect ledger

These are engineering defects and omissions. They must be fixed, not absorbed
into the product's stated limitations. Each is independent of the IA.

The **v1?** column says whether the defect blocks the v1 gate. Defects that do not
block v1 are still defects; they are not reclassified as limitations because
improvement moved out of scope.

| # | Defect | Evidence | Effect | v1? |
| --- | --- | --- | --- | --- |
| 1 | The event allowlist waits for `skill.started` / `skill.completed`, which no producer emits; the real SDK event is `skill.invoked` | `SessionEventNormalizer.cs:146`; SDK XML | Skill invocations normalize to `unsupported` and the Skill name is discarded | **blocks v1** |
| 2 | The same allowlist omits `subagent.failed`, `subagent.selected`, `subagent.deselected` | same | Sub-agent failure and selection signals are discarded | **blocks v1** |
| 3 | The secret filter removes every property whose key contains `token` | `SessionSecretFilter.cs:38` | Per-sub-agent `totalTokens` from SDK events is destroyed before storage | **blocks v1** |
| 4 | Local Monitor has no UI for any part of the improvement cycle | Canvas extension is the only interface | The product's headline capability is unreachable from the product | Stage 5 |
| 5 | Objective Evaluation has no UI anywhere | only `POST /api/session-workspace/objective-evaluations` | A documented quality signal cannot be recorded by a human | Stage 5 |
| 6 | Production launch does not pass `--apply-root` | `MonitorHost.cs:208` | Apply is inert in production; shipping the control without this is a false capability | Stage 5 |
| 7 | The normalizer has no failure branch; any recognised terminal event sets the Session to `Completed` | `SessionEventNormalizer.cs:49` | Session-level failure cannot be expressed even though Run, Event and Trace carry failure signals | **blocks v1** |
| 8 | Nothing in the product emits `vcs.repository.name`; only consumer and projection code exists | measured 0/21 | Repository grouping does not work by default | not v1 |
| 9 | All five source capability manifests are untouched defaults | five manifests, byte-identical on `agent_ownership` | Capability-gated behaviour is suppressed for reasons unrelated to the data | **blocks v1** |
| 10 | Evidence selection is not persisted, and no pointer records the current proposal or apply draft | Canvas JavaScript state | The improvement workflow cannot be resumed, and no next-action rule is computable | Stage 5 |
| 11 | The hook installer registers 7 of the 10 events Copilot CLI supports, omitting `PostToolUseFailure`, `PermissionRequest` and `SessionEnd` | `install-session-hooks.ps1:58`; bundled CLI 1.0.65 hook union | Tool failures, permission decisions and clean session ends are never captured, although the normalizer already accepts all three | **blocks v1** |

Defects 1, 2, 3, 7, 9 and 11 block v1 because v1's entire promise is to present
what was observed, honestly labelled. A defect that silently discards an
observation, never subscribes to it, or suppresses a claim for a reason unrelated
to the data, defeats that promise directly.

Defects 1, 2 and 11 share a shape worth naming: **this product listens for events
that are not sent, and does not listen for events that are.** That is the single
largest reason the owner reports seeing nothing.

Two of these need care rather than a direct fix:

- **#7** must not be fixed by marking a Session failed because any child Run
  failed. A recovered tool failure is not a failed Session. It needs an explicit
  terminal failure signal or a documented precedence rule.
- **#8**, once populated, still leaves `repository_name` a display label. It must
  not become a URL key or a database identity merely because it is now non-null.
  That was Revision 12's error.

## Genuine v1 limits

These are not defects. They are claims the available evidence cannot support, and
v1 must not promise them.

| Cannot promise | Blocking fact |
| --- | --- |
| "Configured Skill X was not invoked" | No closed-world Skill coverage contract exists. For Copilot CLI this may lift early if the `SkillsApi.ListAsync` spike succeeds; for VS Code Copilot Chat it does not |
| "These are all the sub-agents, and this is exactly what each was asked" | Sub-agent identity coverage and task capture are not certified, and no source has a dedicated task field |
| "This run is anomalous relative to the same recurring practice" | No propagated `case_key` or equivalent exact cohort identity |
| "This instruction was bad" | Observational telemetry does not establish causal attribution |
| "This tool call was wasted" | No counterfactual evidence of what would have happened without it |
| "There is exactly one correct next action" | Several candidate proposals and drafts are simultaneously valid, and none is marked current |

## Before the IA can be designed

One thing must be settled, and it is not a capability programme.

**The source-truth and claim catalogue.** An empirical field inventory for GitHub
Copilot CLI and VS Code Copilot Chat only: what arrives, with what coverage, under
which source and schema fingerprint, and which layer of the claim table each field
can support. Manifest promotion follows this evidence rather than preceding it.

The improvement-state contract was the second prerequisite while improvement was
in scope. It is now a Stage 5 prerequisite and no longer blocks the v1 IA.

The IA does **not** have to wait for full manifest promotion, an active
configuration inventory, `case_key`, or repository emission — provided those
capabilities are excluded from v1 or carry an explicit qualification. The IA must
not assume that repository identity, Session-level failure, negative Skill claims,
or same-practice baselines already work.

The IA must also not design around the improvement cycle. It may reserve a place
for it, but it may not make the v1 screens depend on a workflow that v1 does not
deliver — which is exactly what Revision 12 did.

## Resolved: the Copilot CLI hook set

Measured against the bundled GitHub Copilot CLI 1.0.65
(`src/CopilotAgentObservability.LocalMonitor/obj/**/copilot-cli/1.0.65/win32-x64/sdk/index.d.ts`),
the hook event union is exactly ten values:

`SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`,
`PostToolUseFailure`, `PermissionRequest`, `SubagentStart`, `SubagentStop`,
`Stop`, `SessionEnd`.

`StopFailure` is **not** a Copilot CLI event; it belongs to Claude Code. There is
**no skill hook event of any kind** — skills are exposed only through the RPC/SDK
API, never through hooks.

`install-session-hooks.ps1:58` registers seven of the ten, omitting
`PostToolUseFailure`, `PermissionRequest` and `SessionEnd`, all three of which
`SessionEventNormalizer` already accepts. That is defect 11.

## Resolved: a non-Canvas path to Skill identity exists

The .NET package this product already depends on
(`GitHub.Copilot.SDK` 1.0.4) exposes:

- `CopilotClient.ListSessionsAsync(SessionListFilter, CancellationToken)`
- `CopilotClient.ResumeSessionAsync(String, ResumeSessionConfig, CancellationToken)`
- `SkillsApi.ListAsync` — the **available** Skill set
- `SkillsApi.GetInvokedAsync` — the **invoked** Skill set
- `ServerSessionsApi.ListAsync`, `ServerSessionsApi.GetEventFilePathAsync`

So Local Monitor can, at the API level, enumerate the user's own local Copilot CLI
sessions and ask which Skills were available and which were invoked, in .NET,
without the GitHub Copilot App. The bundled CLI's own type definitions describe
`getInvokedSkills()` as the list of skills invoked in a session, retained
specifically so skill permissions survive a session resume.

This is an API surface, not a proven capability. Before it is relied on, a spike
must establish: whether `ResumeSessionAsync` can read a session another process
owns; whether it has side effects on a session the user is actively running;
whether the invoked set survives session completion; and what happens when the
CLI version differs from the bundled one.

If the spike succeeds, `SkillsApi.ListAsync` also supplies the active
configuration inventory that Stage 4 requires, which would bring certified
absence claims — "configured Skill X was not invoked" — forward for Copilot CLI
specifically. It supplies nothing for VS Code Copilot Chat, whose only channel
into this product remains OpenTelemetry.

## Open questions

1. **Does the Skill spike above succeed?** Until it does, the Skill row of the
   four-wants table stands as written.
2. **Does VS Code Copilot Chat have any channel other than OpenTelemetry?** No
   captured artifact in this repository shows a skill-like attribute on a VS Code
   Copilot Chat span, and the one fixture that exists is declared a
   repository-safe conformance fixture rather than a live capture. Until measured,
   VS Code Copilot Chat is assumed to supply trace structure, timing and tokens
   only.

## Next steps

1. Run the Skill spike described above, and measure what VS Code Copilot Chat
   actually emits.
2. Produce the source-truth and claim catalogue for the two real sources.
3. Design the v1 information architecture from this definition, including the
   Japanese label vocabulary. Names are chosen after each surface's purpose is
   fixed, not before.
4. Replace `docs/requirements.md`, `docs/spec.md` and the affected files under
   `docs/specifications/` from the approved IA.

Steps 2 and 3 can overlap: the IA may be drafted against the claim catalogue's
structure while the catalogue's contents are still being measured, provided no
screen assumes a field that has not been observed.

The improvement-state contract is Stage 5 work and is not on this path.

Defects 1, 2, 3, 7, 9 and 11 should be fixed before the v1 IA is implemented,
because they change what the screens can honestly show. Defects 1, 2 and 11 are
small, local corrections with a large effect on what is visible, and are the
cheapest work identified anywhere in this document. Defects 5 and 6 are
independent of everything above and can be fixed at any time.
