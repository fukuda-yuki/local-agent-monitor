# Local Monitor v1 Contract Index

Status: **Accepted current authority**

This index identifies the single authority for every Local Monitor v1 behavior. Consumers must not create parallel schemas, fallback readers or alternate state vocabularies.

| Area | Product/contract authority | Implementation owner |
|---|---|---|
| Product definition | `docs/superpowers/specs/2026-07-28-local-monitor-v1-product-definition.md` | — |
| IA/page hierarchy/layout/states | [`local-monitor-v1-ia.md`](local-monitor-v1-ia.md), #132 | #135–#140, #145–#146, #167 |
| Human route/URL/Session collection request transport | [`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md), PO136-A2b | #136 pure parsers; active mapping waits for the exact #134 response contract |
| Source claims/missing semantics | #129 | #137 and feature owners |
| Repository catalog/locator/assignment | [`local-repository-catalog.md`](local-repository-catalog.md), [DC156-12–19 executable closure](local-repository-catalog-executable.md), #155 | #156 |
| Source-version interpretation correction | [`source-compatibility-reconciliation.md`](../layers/source-compatibility-reconciliation.md), #154 | #154 |
| Skill projection validity/re-run | [`skill-projection.md`](../layers/skill-projection.md), [exact-v10 input evidence](../contracts/skill-projection-v1-deleted-before-digest.md), #154 | #154 |
| Skill v1 correction/v2 transport/body/path snapshot/current file | [`skill-invocation-snapshot.md`](skill-invocation-snapshot.md), #119/#157/#158 | #158 after gate closure |
| Sanitized-only receiver posture | #159 | #168 |
| Session/Repository archive | #160 | #161 |
| Optional AI snapshots/results/history | #162 plus the run-ID route amendment in [`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md) | #163/#164 |
| Repository Session Compare | #165 plus the identity/expiry amendment in [`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md) | #166 |
| Repository/Session Workspace reads | #133 semantic requirements plus the collection-request/execution/node amendment in [`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md); exact Session-collection success wire remains a required later canonical #134 contract | #134 after that response contract closes |
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
#134 exact response contract / Workspace reads
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

The #136 pure typed path/query/request/cursor foundation may be implemented
before #134. #133 does not yet define the complete exact Session-collection
success wire. Active page/endpoint registration therefore remains gated on a
later canonical #134 Workspace-read response contract as well as its semantic
owners: #134 alone eventually maps the POST, the functional Explorer and atomic
`/traces` retirement are #138-owned, and `/historical-analysis` retirement is
#164-owned. No placeholder route, inferred DTO/serializer or substitute reader
is permitted.

## Rules

- The complete `local_repository_catalog:1` contract is `READY` under base
  DC156-01–11 plus executable closure DC156-12–19. The closure is the accepted
  amendment to every temporary `only parser READY` / unresolved-blocker summary
  written before DC156-12–19. Implementers consume the closure rather than
  treating those historical status sentences as a current gate.
- DC156-12–19 fix automatic exact creation/binding, physical-source/context
  framing, exact Session Event join, automatic revision history, one
  fixed-frontier queue/recovery path, complete wire bytes/bounds, opaque
  raw-reference durability and the single SQLite snapshot composition seam.
- Repository reconciliation has one discovery/enqueue path only: the durable
  monitor-span cursor. There is no ingest-time alternate enqueue, route-trigger,
  scan-on-read or fallback cursor.
- V1 has no accepted observed Repository-label source. Locator/display
  components are not label evidence; assignment responses always contain exact
  `"observed_label_candidates":[]`, and no catalog table stores a candidate.
- #134 alone maps and serializes
  `GET /api/local-monitor/v1/repositories`. #156 owns its five
  management/action routes, not the composite Repository-card read.
- The sole Session collection transport is
  `POST /api/local-monitor/v1/sessions`. Its request transport is exact, but
  #133's current response description is not a complete wire contract. #134
  alone may map, read and serialize it only after the later canonical response
  contract closes; there is no GET alias, saved-search handle, compatibility
  reader, fallback or second Workspace reader.
- Human primary paths are lowercase, slashless and exact. Matched malformed
  IDs/queries fail closed; literal/case/slash near-path aliases are empty
  no-store 404 and never redirect.
- Exact `/sessions/unassigned` has reserved static precedence over the Session
  ID template; its case variants are near-path empty 404, not malformed-ID 400.
- Dynamic Session Explorer `q` and `model` values are current-page/POST-body
  state only. They never enter URL/history/storage/cache/log/error/cursor
  state; non-default limit is also transient. URL cursor eligibility requires
  exact q=null/model=[]/limit=null/default 50. The exact cursor carries only a
  process-keyed HMAC filter binding.
- Canonical Repository/Session/execution/AI/comparison route IDs are local
  lowercase UUIDv7; timeline node IDs are `node-` plus 32 lowercase hex.
- Known comparison expiry is a fixed 410 backed only by #165/#166's minimal
  append-only runtime-database tombstone. Unknown or Repository-mismatched
  comparison IDs are fixed 404. Runtime backup transactionally drops the exact
  tombstone table from its staging copy before inventory/hash/archive, never
  from source; manifest/restore omit it and restore startup creates it empty.
  Future #166 operational tables require their own exact backup amendment.
- #156, #161 and #134 use one `ILocalRepositoryScopeSnapshotService`; #161
  composes archive eligibility and #161/#134 add no direct catalog SQL or
  parallel reader.
- Repository automatic creation uses exact admitted GitHub locator fingerprints
  only. It never uses name/label/path/CWD/prompt/time/cardinality, and an
  archived exact owner is reused without restore or duplication.
- Frozen `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE are never widened.
- Frozen Session ingest v1 supports `skill.started | skill.completed`;
  `skill.invoked` is unsupported and no other v1 shape, enum, limit, status,
  error or response byte changes.
- `/api/local-monitor/v1/*` is the raw-default local human-UI namespace and is absent in sanitized-only posture.
- Repository, Session and hierarchy identities are exact and opaque.
- Missing values are not zero.
- Exact-v10 `deleted_before_digest_v10` is tagged predecessor evidence, never a fabricated payload digest; its OTel Skill projection outcome is fail-closed `input_unavailable` under the dedicated contract above.
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
