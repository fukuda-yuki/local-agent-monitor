# Local Monitor v1 Contract Index

Status: **Accepted input to Issue #118**

This index identifies the single authority for every Local Monitor v1 behavior. Consumers must not create parallel schemas, fallback readers or alternate state vocabularies.

| Area | Product/contract authority | Implementation owner |
|---|---|---|
| Product definition | `docs/superpowers/specs/2026-07-28-local-monitor-v1-product-definition.md` | — |
| IA/routes/layout/states | `docs/specifications/interfaces/local-monitor-v1-ia.md`, #132 | #135–#140, #145–#146, #167 |
| Source claims/missing semantics | #129 | #137 and feature owners |
| Repository catalog/locator/assignment | #155 | #156 |
| Skill projection validity/re-run | #154 | #154 |
| Skill body/path snapshot/current file | #157 | #158 |
| Sanitized-only receiver posture | #159 | #168 |
| Session/Repository archive | #160 | #161 |
| Optional AI snapshots/results/history | #162 | #163/#164 |
| Repository Session Compare | #165 | #166 |
| Repository/Session Workspace reads | #133 | #134 |
| Japanese sentence-level copy | #169 | #169 and UI owners |
| Cross-cutting validation | #147 | #147 |
| User documentation | accepted canonical docs | #148 |

## Core dependencies

```text
telemetry fixes / #154
        +
#156 Repository
#158 Skill raw detail
#161 Archive
#168 sanitized-only host
        ↓
#134 Workspace reads
        ↓
#135/#136 shell/routes
#167 Repository selection
#138 Session Explorer
#139/#140 Session detail
#145/#146 Settings
#166 Compare
#163 Session/node AI
#164 Repository/Compare AI
        ↓
#169 copy review
        ↓
#147 validation
        ↓
#148 user docs
```

## Rules

- Frozen `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE are never widened.
- `/api/local-monitor/v1/*` is the raw-default local human-UI namespace and is absent in sanitized-only posture.
- Repository, Session and hierarchy identities are exact and opaque.
- Missing values are not zero.
- Compare values are deterministic and AI-independent.
- AI can interpret accepted snapshots but cannot recalculate Compare facts or explore outside scope.
- Archive is not deletion, retention or pin.
- Historical Skill snapshot is not the current file.
- Sentence-level wording may change under #169 without changing contracts, routes or structured selectors.
