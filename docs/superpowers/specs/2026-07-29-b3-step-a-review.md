# Issue #128 step A independent review

## Findings

### Blocking

1. **Blocking — `src/CopilotAgentObservability.Telemetry/SourceCompatibility/OtlpTraceSourceVersionResolver.cs:37` — a missing resource-scoped version is discarded when the same trace also has one valid version.**

   The resolver unions only valid values and tracks only invalid values. It does not retain that an envelope containing spans for the trace had no `service.version`; the `Missing` result at lines 99-102 is selected only when the union is empty. For a concrete batch with trace `T` in resource envelope A with no `service.version` and again in envelope B with recognised `1.0.74`, the accumulated evidence is `{1.0.74}` and the trace is persisted as `Resolved`. This fails open and can authorize a later Skill projection even though source version was missing for some of the trace's spans.

2. **Blocking — `src/CopilotAgentObservability.Persistence.Sqlite/SourceCompatibility/SqliteSourceCompatibilityStore.cs:362` — the read layer promotes a trace from `Missing` to `Resolved` across ingest batches.**

   `GetTraceSourceVersionResolution` returns `Resolved` whenever any stored observation is resolved after excluding conflicts and unrecognised rows; it never checks for a stored `Missing` row. For a concrete trace first ingested without `service.version` and later ingested with recognised `1.0.74`, storage correctly contains distinguishable `missing` and `resolved` rows, but the definitive read returns `Resolved("1.0.74")`. The read answer is therefore wrong and violates fail-closed resolution independently of the intra-batch defect above.

### Should-fix

3. **Should-fix — `docs/requirements.md:74` and `docs/spec.md:931` — the change duplicates the already-governing telemetry-ingestion rule into higher-level documents.**

   The requirements edit adds: “`resource-scoped service.version` ... `resolved / missing / conflicting / unrecognised`”, and the spec adds: “Monitor schema v8 additively associates each trace ... No unresolved state falls back to the batch label.” These are unrequested restatements of the existing **Copilot CLI Skill projection** specification, not required version-reference propagation. The concrete failure is creation of duplicate higher-priority authorities that can drift from the governing detailed specification.

4. **Should-fix — `tests/CopilotAgentObservability.LocalMonitor.Tests/SourceCompatibilityStoreTests.cs:302` — the claimed v7 migration fixture test is a freshly created v8 database with its table dropped and stamp rewritten.**

   Lines 304-312 call the current `CreateSchema`, then execute `DROP TABLE source_trace_version_observations` and rewrite the version to 7. This does not exercise the real predecessor schema. The committed historical-fixture suite covers only monitor v1-v5, so no actual v7 database carrying the predecessor source-compatibility schema is migrated. A migration that accidentally depends on objects or constraints introduced only by current `CreateSchema` can pass this test and fail for a user's v7 database.

5. **Should-fix — `tests/CopilotAgentObservability.LocalMonitor.Tests/SourceCompatibilityIngestionTests.cs:20` and `:77` — no test puts the same trace ID in two `resourceSpans` envelopes.**

   The mixed-version batch test uses different trace IDs, while the conflict test puts two `service.version` attributes in one resource envelope. Neither covers same-trace cross-envelope aggregation, especially one missing plus one recognised envelope. The concrete consequence is that finding 1 passes the added test suite.

6. **Should-fix — `tests/CopilotAgentObservability.LocalMonitor.Tests/SourceCompatibilityIngestionTests.cs:168` — the monitor API test does not pin pre-change response bytes.**

   The test captures `before` and `after` from the same changed executable and varies only the new table row. A DTO field, ordering, null-emission, or encoding change applied to both captures would pass. It proves only that changing `source_trace_version_observations` does not affect the eight responses; it is not a byte-identical regression against the frozen v1 bytes.

7. **Should-fix — `.claude/settings.local.json:1` — unrelated local agent permissions and hooks are present as an untracked working-tree file.**

   This is unrequested Issue #128 scope. Accidental inclusion would change agent command permissions and edit hooks, including allowing `git add`/`git commit`, rather than changing trace source-version persistence.

## Scope classification for the five requested documents

- `docs/requirements.md`: **mixed**. The line 70 edit “monitor v7” → “monitor v8” is required propagation of the required monitor schema bump. The line 74 per-trace resolution text is unrequested scope and is finding 3.
- `docs/spec.md`: **mixed**. The line 843 edit “monitor v7” → “monitor v8” is required schema-vector propagation. The new lines 931-935 restate the governing telemetry-ingestion behavior and are unrequested scope (finding 3).
- `docs/decisions.md:2497`: **required**. “monitor v7” → “monitor v8” keeps D070's current import/capture anchor aligned with the required monitor schema bump; it adds no new behavior.
- `docs/specifications/interfaces/sanitized-evidence-export.md:60`: **required**. “monitor v7” → “monitor v8” updates the exact database-version anchor after the required bump; it adds no carrier or export behavior.
- `docs/specifications/interfaces/sanitized-evidence-import.md:197`: **required**. “monitor v7” → “monitor v8” updates the supported version vector after the required bump; it adds no import behavior.

## Clean checks

- **Frozen contracts:** Clean by diff. `MonitorHost.cs` changes only `/v1/traces` ingestion; no `/api/monitor/*` DTO/serialization path, `/api/session-workspace/*` response path, `session_events.content_state` value, or raw-bearing-surface enumeration is changed.
- **Three distinguishable states:** Clean for isolated observations. Missing flows through resolver lines 99-102 to stored `resolution_state='missing'`; conflicting flows through lines 89-92 to `'conflicting'`; unrecognised flows through lines 94-97 or 108-111 to `'unrecognised'`. The table check at `SqliteSourceCompatibilityStore.cs:106-113` and parser at lines 699-704 preserve the state even when the version column is null. Mixed-observation resolution is not clean (findings 1-2).
- **Additive-only schema:** Clean apart from fixture evidence in finding 4. `source_trace_version_observations` is newly created, monitor version is bumped 7 → 8, existing columns are not repurposed, and `SourceObservationBatchDraft.SourceApplicationVersion` remains the batch value at `SourceCompatibilityModels.cs:642,654`.
- **Forbidden patterns:** No batch-version fallback, permissive version parsing, last-write-wins conflict resolution, compatibility shim, or dual read path appears in the diff. Findings 1-2 are separate fail-open aggregation defects.
- **Untrusted `service.version`:** Clean. The resolver uses the existing `SourceMetadata` producer-token policy (1-256 characters and no controls) at `SourceCompatibilityModels.cs:833-836`; invalid values are not persisted, logged, copied into repository artifacts, or included in exception text.
- **Two-version batch coverage:** Clean. `SourceCompatibilityIngestionTests.cs:20-25` has two `resourceSpans` envelopes with versions `1.0.74` and `1.0.75` and different trace IDs in one payload.
- **Other correctness cases:** Different valid values for one trace are set-aggregated to conflict rather than last-write-wins, and an envelope with zero spans creates no phantom trace-version row. Same-trace missing evidence is not clean (findings 1-2).

## Verification limitation

No build or test command was run because the review instruction prohibits modifying any file and those commands write build/test artifacts. This review is based on the requested working-tree diff and the files it touches.

## Verdict

**fix-first** — the most important reason is that missing version evidence can be silently upgraded to `Resolved`, so the stored/read answer is not fail closed.
