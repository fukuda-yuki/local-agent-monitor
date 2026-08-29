# Local Monitor v1 Contract Index

Status: **Accepted current authority**

This index identifies the single authority for every Local Monitor v1 behavior. Consumers must not create parallel schemas, fallback readers or alternate state vocabularies.

| Area | Product/contract authority | Implementation owner |
|---|---|---|
| Product definition | `docs/superpowers/specs/2026-07-28-local-monitor-v1-product-definition.md` | — |
| IA/page hierarchy/layout/states | [`local-monitor-v1-ia.md`](local-monitor-v1-ia.md), #132 | #135–#140, #145–#146, #167 |
| Human route/URL/Session collection request transport | [`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md), PO136-A2b | #136 pure parsers |
| Source claims/missing semantics | #129 | #137 and feature owners |
| Repository catalog/locator/assignment | [`local-repository-catalog.md`](local-repository-catalog.md), [DC156-12–19 executable closure](local-repository-catalog-executable.md), #155 | #156 |
| Source-version interpretation correction | [`source-compatibility-reconciliation.md`](../layers/source-compatibility-reconciliation.md), #154 | #154 |
| Skill projection validity/re-run | [`skill-projection.md`](../layers/skill-projection.md), [exact-v10 input evidence](../contracts/skill-projection-v1-deleted-before-digest.md), #154 | #154 |
| Skill v1 correction/v2 transport/body/path snapshot/current file | [`skill-invocation-snapshot.md`](skill-invocation-snapshot.md), #119/#157/#158, D083 | #158 after the prerequisite join and implementation/release gates |
| Session terminal facts/outcome aggregation | [`canvas-session-workspace.md`](canvas-session-workspace.md), #124 | #124 |
| Sanitized-only receiver posture | #159 | #168 |
| Session/Repository archive | [`local-archive.md`](local-archive.md), #160, D082 | #161 |
| Optional AI snapshots/results/history | #162 plus the run-ID route amendment in [`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md) | #163/#164 |
| Repository Session Compare | [`local-monitor-v1-comparison.md`](local-monitor-v1-comparison.md), #165, plus shared transport in [`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md) | #166 |
| Session collection success response | [`local-monitor-v1-session-collection.md`](local-monitor-v1-session-collection.md), composing #133 semantics and [`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md) request/transport/cursor rules | #134 |
| Other Repository/Session Workspace reads | #133 semantic requirements and the execution/node amendment in [`local-monitor-v1-route-transport.md`](local-monitor-v1-route-transport.md) | #134 |
| Session summary/timeline/node/content v2 exact response and coherent snapshot | [`local-monitor-v1-session-detail.md`](local-monitor-v1-session-detail.md) | #134 |
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

The #136 pure typed path/query/request/cursor foundation composes with the
accepted exact success wire in `local-monitor-v1-session-collection.md`.
#133's incomplete response prose is superseded by that sole success authority;
#134 alone maps and serializes the POST. The functional Explorer and atomic
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
  Its exact success response and opaque cursor are owned solely by
  `local-monitor-v1-repository-collection.md`.
- The sole Session collection transport is
  `POST /api/local-monitor/v1/sessions`. Its request transport composes with
  the sole success authority in `local-monitor-v1-session-collection.md`; #134
  alone maps, reads and serializes it. #133's former GET and incomplete response
  prose are superseded. There is no GET alias, saved-search handle,
  compatibility reader, fallback or second Workspace reader.
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
- The four Session-detail success schemas are sole-current v2 and share one
  same-origin guard. `local_workspace_projection:5` is the sole detail reader;
  v4 `SubagentStart.agent_id` content mapping is removed because `agent_id` is
  technical/native Run identity only.
- Known comparison expiry is a fixed 410 backed only by #165/#166's minimal
  append-only runtime-database tombstone. Unknown or Repository-mismatched
  comparison IDs are fixed 404. Runtime backup excludes the complete closed
  24-hour Compare operational namespace by the staging categories in
  `runtime-backup-restore.md`, never by mutating source; manifest/restore omit
  them and restore startup rematerializes only owner-required empty schema.
- #156, #161 and #134 use one `ILocalRepositoryScopeSnapshotService`; #161
  supplies complete direct Session/Repository archive facts, #156 validates
  those facts and alone composes assignment-dependent effective archive
  eligibility/reason, and #161/#134 add no direct catalog SQL or parallel
  reader.
- D082's singular executable archive owner is
  [`local-archive.md`](local-archive.md). #161 owns `local_archive:1`, its
  current/history state machine, direct-fact reader implementation, exact
  raw-default `GET /api/local-monitor/v1/archive`,
  `POST /api/local-monitor/v1/archive-actions`, and
  `GET /api/local-monitor/v1/archived-items` routes, and runtime-backup
  validation. The component is ordered immediately after
  `local_repository_catalog:1` and before Retention. Those
  routes/application/contributor are absent from
  `--sanitized-only`; no alias, compatibility path, UI, precomposed eligibility
  or second archive/catalog reader exists.
- Repository automatic creation uses exact admitted GitHub locator fingerprints
  only. It never uses name/label/path/CWD/prompt/time/cardinality, and an
  archived exact owner is reused without restore or duplication.
- Frozen `/api/monitor/*`, `/api/session-workspace/*` v1 and SSE are never widened.
- Frozen Session ingest v1 supports `skill.started | skill.completed`;
  `skill.invoked` is unsupported and no other v1 shape, enum, limit, status,
  error or response byte changes. #124 changes only which existing public
  `status`/`ended_at` values are selected from private Session-14 facts; it adds
  no public enum, field, or representation.
- `/api/local-monitor/v1/*` is the raw-default local human-UI namespace and is absent in sanitized-only posture.
- Repository, Session and hierarchy identities are exact and opaque.
- Missing values are not zero.
- Exact-v10 `deleted_before_digest_v10` is tagged predecessor evidence, never a fabricated payload digest; its OTel Skill projection outcome is fail-closed `input_unavailable` under the dedicated contract above.
- Compare values are deterministic and AI-independent.
- The five Repository-scoped Compare operations, seven closed schemas, bounds,
  paging, and errors are owned only by [`local-monitor-v1-comparison.md`](local-monitor-v1-comparison.md).
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
- D083 closes the remaining #119/#158 product decisions in the canonical
  snapshot interface. That closure does not claim implementation or release:
  the nonregistered #119 parser/handoff follows mandatory live-Issue
  reconciliation, #158 runtime work waits for the exact prerequisite join, and
  host activation, route registration and release remain gated by the focused,
  platform, live, full-validation and review evidence in that interface.
- After those gates pass, D083 adds to raw-default composition only the additive
  `POST /api/session-ingest/v2/events` and the three exact Skill routes:
  `GET /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}`,
  `GET /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}/content`,
  and
  `POST /api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}/current-file-read`.
  Receiver-only/`--sanitized-only` composition registers none of them. The
  current-file POST additionally requires a nonempty valid retained-root set
  and an independently certified Windows or Linux platform; zero roots and
  unsupported zero-root platforms leave only that POST absent.
- The complete snapshot namespace is absent from sanitized export/import; no
  empty carrier, v1 fallback, compatibility writer or dual path is permitted.
- Sentence-level wording may change under #169 without changing contracts, routes or structured selectors.
- Repository identity/assignment is exact-only; no name/path/CWD/prompt/time/
  cardinality heuristic is permitted, and Issue #152 remains unresolved.
