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

## Verification

- Ran `git diff --check`; no whitespace errors were reported.
- Documentation-only change. No product code, dependency, build, or test command was required.

## Residual Risk

- M2 still needs a concrete dashboard dataset schema before any prototype or Grafana JSON can be implemented.
- External outcome metrics need a separate privacy and identity-mapping design before real GitHub / Notion / HR data is connected.
