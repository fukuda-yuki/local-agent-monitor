# Local Monitor v1 UX0 — Requirement traceability self-review

Date: 2026-07-30  
Scope: #117 / #118 / #132 / #133–#168 and the Product Owner dialogue

## Result

Every Product Owner requirement in the UX0 dialogue is now represented by the integrated #153 contract, this committed design package, or an explicit contract / implementation Issue.

This does **not** mean UI implementation is authorized. Visual approval, contract completion, #132, and #118 remain required gates.

## Traceability

| Requirement | Primary authority | Implementation owner |
|---|---|---|
| AI-independent core + optional AI | #117, #153, #132, #118 | #135–#140, #145–#146, #163–#164 |
| No permanent sidebar; breadcrumb header | #153, #132 | #135, #136 |
| Repository catalog / identity / manual correction | #155 | #156 |
| Repository cards / all / unassigned / archive entry | #153, #132 | #167 |
| Session Explorer / filter / direct-open | #133, #153, #132 | #134, #138 |
| Compare selection instead of aggregate Dashboard | #165, #153, #132 | #166, #138 |
| Missing-state Japanese | #129, #153, #132 | #137 |
| Session header and Token/cache bars | #133, #153, #132 | #134, #139 |
| Hierarchical timeline + inspector | #133, #153, #132 | #134, #140 |
| Skill projection correctness with no old-data compatibility | #154 | #154 |
| Skill body/path raw-local snapshot | #157 | #158, #140 |
| `--sanitized-only` no-human-UI simplification | #159 | #168 |
| Session / Repository archive | #160 | #161, #138, #145, #167 |
| Session/detail/Repository AI scopes | #162 | #163, #164 |
| Session AI durable history only | #162 | #163 |
| Unified Settings modal | #153, #132 | #135, #145, #146 |
| Cross-cutting validation | #147 | #147 |
| User documentation | #148 | #148 |

## Gaps found and corrected

1. **#153 body was stale and comments were the only current source.**
   - Replaced with the integrated current contract.
2. **Integrated visual was previously marked accepted before Product Owner review.**
   - The old acceptance is superseded. The seven-screen package is explicitly review pending.
3. **Generic aggregate/KPI dashboard conflicted with the later Compare decision.**
   - Removed from #132/#118/#138/#147/#148. #165/#166 own Repository Session Compare.
4. **Repository selection UI had no implementation owner.**
   - Created #167.
5. **#159 had no implementation owner.**
   - Created #168 and updated #159.
6. **#135–#140 and #145–#146 still described retired sidebar/routes/screens.**
   - Rewritten to the accepted shell, Session Explorer, Workspace, Inspector, and Settings modal.
7. **#132/#118 still required per-route sanitized fallback and excluded all Compare behavior.**
   - Updated to the accepted `--sanitized-only` simplification and core Compare / effect-verdict separation.
8. **#133/#134 did not provide the approved Repository/Explorer/Workspace read contract.**
   - Rewritten around additive successor reads and separate raw-local routes.
9. **#147/#148 were missing Archive, Compare, Session AI history, Skill raw detail, and Settings.**
   - Rewritten.
10. **#154 still assumed old Skill data compatibility/backfill.**
    - Rewritten: pre-release Skill projection data may be discarded; no compatibility shim, dual path, or historical backfill obligation.

## Deliberately unresolved contract investigations

These are assigned technical decisions, not missing product requirements:

- Repository locator canonicalization / fingerprint / migration: #155
- Workspace successor DTO/order/index/performance: #133
- Skill projection generation / race / destructive transition: #154
- Skill snapshot acquisition/storage/retention: #157
- exact `--sanitized-only` terminal behavior: #159
- archive schema/history/query integration: #160
- Session AI snapshot/store and transient-result retention: #162
- Compare formulas, low-N behavior, snapshot and operational retention: #165

Codex may decide these implementation contracts only within the fixed Product Owner boundaries.

## Gate result

```text
Visual review is ready.
Contract/spec work is not yet complete.
UI implementation is not yet authorized.
```
