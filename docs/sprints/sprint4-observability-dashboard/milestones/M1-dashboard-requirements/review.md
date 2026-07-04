# M1 Review: Dashboard Requirements

## Scope Reviewed

- `docs/requirements.md`
- `docs/spec.md`
- `docs/task.md`
- `docs/sprints/sprint4-observability-dashboard/README.md`
- `docs/sprints/sprint4-observability-dashboard/milestones/M1-dashboard-requirements/task.md`

## Findings

- No blocking issues found.
- Sprint4 is defined as Agent workflow observability dashboard requirements, not repository-changing auto-improvement.
- Existing repository-changing auto-improvement references in source-of-truth documents were moved to Sprint5 or later to avoid conflicting Sprint4 goals.
- Dashboard scope is bounded to aggregate / sanitized views with drilldown references, not raw prompt / response / tool result browsing.
- Outcome linkage is documented as a future candidate, with GitHub / Notion / HR system production integration out of Sprint4 initial scope.
- Deep research report follow-up was reviewed against Sprint4 source-of-truth boundaries.
- TTFT, trace duration, tool total duration, retry, subagent / nested agent, approval wait, and stuck session are now separated as dashboard requirements.
- Panel candidates distinguish count, duration, error, retry, approval wait, cost, and candidate backlog so tool prioritization is not based on call count alone.
- Organization usage / ROI, quality / safety evaluation, and external outcome linkage remain future candidates rather than Sprint4 initial dashboard scope.
- Microsoft Learn's AI coding agents with Grafana guidance was reflected as the reason to make Grafana-first dashboard + Langfuse drilldown the M4 prototype path's first candidate.
- Grafana product / hosting / backend choices remain undecided; M1 only sets the prototype comparison priority.

## Self-review Follow-up

- Finding: Sprint4 success criteria required users and decision targets, but the added dashboard requirements mostly described views and metrics. This was valid. Fixed by adding primary users and decision targets to `docs/requirements.md`, `docs/spec.md`, and the sprint README.
- Finding: M4 prototype direction was updated in requirements/spec/README, but `docs/task.md` still only mentioned a generic prototype path. This was valid. Fixed by adding Grafana JSON dashboard as the first candidate while preserving comparison with static report and repository-local preview.
- Finding: The Microsoft Learn reference in the sprint README was named but not linked. This was valid. Fixed by adding the source URL.
- Finding: Grafana-first prototype could appear to conflict with the existing non-goal of production Grafana adoption. This was reviewed and not a conflict because M1 now limits the decision to prototype priority and keeps product / hosting / backend choices undecided.

## Verification

- Ran `git diff --check`; no whitespace errors were reported.
- Documentation-only change. No product code, dependency, build, or test command was required.

## Residual Risk

- M2 still needs a concrete dashboard dataset schema before any prototype or Grafana JSON can be implemented.
- External outcome metrics need a separate privacy and identity-mapping design before real GitHub / Notion / HR data is connected.
