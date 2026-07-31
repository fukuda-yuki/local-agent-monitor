# Local Monitor v1 Contract Index

Status: **Accepted current authority**

This index identifies the single authority for every Local Monitor v1 behavior. Consumers must not create parallel schemas, fallback readers or alternate state vocabularies.

| Area | Product/contract authority | Implementation owner |
|---|---|---|
| Product definition | `docs/superpowers/specs/2026-07-28-local-monitor-v1-product-definition.md` | — |
| IA/routes/layout/states | `docs/specifications/interfaces/local-monitor-v1-ia.md`, #132 | #135–#140, #145–#146, #167 |
| Source claims/missing semantics | #129 | #137 and feature owners |
| Repository catalog/locator/assignment | [`local-repository-catalog.md`](local-repository-catalog.md), #155 | #156 |
| Source-version interpretation correction | [`source-compatibility-reconciliation.md`](../layers/source-compatibility-reconciliation.md), #154 | #154 |
| Skill projection validity/re-run | [`skill-projection.md`](../layers/skill-projection.md), #154 | #154 |
| Skill v1 correction/v2 transport/body/path snapshot/current file | [`skill-invocation-snapshot.md`](skill-invocation-snapshot.md), #119/#157/#158 | #158 after gate closure |
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

- Only the Repository locator parser/canonicalization/fingerprint slice is
  `READY`; full #156 remains `BLOCKED_DECISION` under the explicit gaps in the
  Repository catalog contract.
- #134 alone maps and serializes
  `GET /api/local-monitor/v1/repositories`. #156 owns its five gated
  management/action routes, not the composite Repository-card read.
- #156, #161 and #134 use one `ILocalRepositoryScopeSnapshotService`; #161
  composes archive eligibility and #161/#134 add no direct catalog SQL or
  parallel reader.
- Frozen `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE are never widened.
- Frozen Session ingest v1 supports `skill.started | skill.completed`;
  `skill.invoked` is unsupported and no other v1 shape, enum, limit, status,
  error or response byte changes.
- `/api/local-monitor/v1/*` is the raw-default local human-UI namespace and is absent in sanitized-only posture.
- Repository, Session and hierarchy identities are exact and opaque.
- Missing values are not zero.
- Compare values are deterministic and AI-independent.
- AI can interpret accepted snapshots but cannot recalculate Compare facts or explore outside scope.
- Archive is not deletion, retention or pin.
- Historical Skill snapshot is not the current file.
- A raw Skill snapshot cannot create or resurrect a stale/invalid invocation
  claim; the single #154 read authority checks OTel claims against current
  resolved trace generation and SDK claims against their exact current-registry
  tuple without requiring trace/span.
- OTel/SDK claims merge only on exact producer trace ID plus span ID. No
  trace-only, name/path/time/cardinality, Session or discovery heuristic is
  permitted.
- #119 Skill v2 and #158 production remain `BLOCKED_DECISION` until every exact
  wire/mapping, error/media/`405` byte, schema/fingerprint/registry, equality/
  content byte-domain, classification/nullability/name/path, success/discovery
  literal and historical-to-discovery identity-proof decision in the canonical
  snapshot interface is closed.
- The complete snapshot namespace is absent from sanitized export/import; no
  empty carrier, v1 fallback, compatibility writer or dual path is permitted.
- Sentence-level wording may change under #169 without changing contracts, routes or structured selectors.
- Repository identity/assignment is exact-only; no name/path/CWD/prompt/time/
  cardinality heuristic is permitted, and Issue #152 remains unresolved.
