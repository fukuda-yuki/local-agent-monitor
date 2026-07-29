# Local Monitor v1 UX0 — Requirement traceability self-review

Date: 2026-07-30  
Scope: #117 / #118 / #132 / #133–#148 / #153–#166 and the Product Owner dialogue

## Result

- Product intent and principal IA decisions are captured.
- The previous issue graph contained stale bodies and two implementation gaps.
- This review updates the stale issues and creates the missing owners before implementation delegation.

## Traceability

| Requirement | Primary owner | Implementation owner |
|---|---|---|
| AI-independent core + optional AI | #117, #153, #132, #118 | #135–#140, #145–#146, #163–#164 |
| No permanent sidebar; breadcrumb header | #153, #132 | #135, #136 |
| Repository catalog/identity/manual correction | #155 | #156 |
| Repository selection cards/unassigned/all/archive entry | #153, #132 | new Repository selection UI issue |
| Session Explorer/list/filter/direct-open | #133, #153, #132 | #134, #138 |
| Missing-state wording | #129, #153, #132 | #137 |
| Session header, Token/cache bars | #133, #153, #132 | #134, #139 |
| Hierarchical timeline + inspector | #133, #153, #132 | #134, #140 |
| Skill re-evaluation/backfill | #154 | #154 |
| Skill body/path raw-local snapshot | #157 | #158, #140 |
| `--sanitized-only` UI simplification | #159 | new C7b implementation issue |
| Session/Repository archive | #160 | #161 + UI owners |
| Session/detail/repository AI scope | #162 | #163, #164 |
| Session AI durable history only | #162 | #163 |
| Repository Compare instead of aggregate dashboard | #165 | #166 |
| Unified Settings modal | #153, #132 | #145, #146 |
| Cross-cutting validation | #147 | #147 |
| User docs | #148 | #148 |

## Gaps found and closure action

1. **#153 body was stale and comments were the only current source.**
   - Replace the body with the integrated current contract.
2. **Integrated visual was previously marked accepted before Product Owner review.**
   - Keep #153 open and mark the visual package `review pending`.
3. **Generic aggregate dashboard conflicted with the later Compare decision.**
   - Remove aggregate dashboard language from #132/#118/#147/#148; use #165/#166.
4. **Repository selection UI had no explicit implementation owner.**
   - Create a dedicated UI issue consuming #155/#156 and #133/#134.
5. **#159 had no implementation issue.**
   - Create C7b after the terminal contract is finalized.
6. **#135–#140 and #145–#146 bodies still described the retired sidebar/routes/screens.**
   - Rewrite them to the accepted shell, Session Explorer, Workspace, Inspector, and Settings modal.
7. **#132/#118 still required per-route sanitized fallback and excluded all Compare/effect wording.**
   - Update to the accepted `--sanitized-only` simplification and distinguish core Compare from quality-first effect verdict.
8. **#147/#148 were missing Archive, Compare, Session AI history, and Settings modal.**
   - Update validation/documentation scope.

## Not ready to decide without Codex/code investigation

The following are deliberately owned by contract Issues rather than invented in UX0:

- Repository locator canonicalization/fingerprint and migration (#155)
- Skill snapshot acquisition/storage/retention (#157)
- exact `--sanitized-only` terminal behavior (#159)
- archive state/history/query integration (#160)
- Session AI snapshot/store and transient-result retention (#162)
- Compare receipt formulas, low-N behavior and operational retention (#165)
- additive read DTOs, pagination, ordering and indexes (#133)

These do not block visual review, but their contracts must complete before coding their dependent implementation issues.
