# B3 Step A Fix Re-review

## Findings

### Blocking — missing evidence masks a syntactically valid unrecognised version

File: `src/CopilotAgentObservability.Telemetry/SourceCompatibility/OtlpTraceSourceVersionResolver.cs:101`

`ResolveTrace` checks `HasMissingVersion` before it asks the registry whether the single collected version is recognised. For trace T with:

- envelope A carrying a span for T and no `service.version`; and
- envelope B carrying a span for T and `service.version = "9.9.9"`, where that valid token is absent from the registry,

the accumulated evidence is `Versions = {"9.9.9"}`, `HasInvalidVersion = false`, and `HasMissingVersion = true`. The conflicting and invalid-token branches do not match, so lines 101–104 return `Missing`. The registry check at lines 107–113 is never reached.

The required precedence is conflicting > unrecognised > missing, so this case must be `Unrecognised`. The wrong `Missing` state is then written as the observation; the definitive read cannot recover the unrecognised evidence because the stored row no longer contains it. This is a new fail-closed classification defect introduced by the missing flag.

### Minor — the storage regression test does not pin the required resulting state

File: `tests/CopilotAgentObservability.LocalMonitor.Tests/SourceCompatibilityStoreTests.cs:126`

The assertion checks only that the result is not `Resolved`. It would also pass if the missing-plus-resolved observations incorrectly aggregated to `Conflicting` or `Unrecognised`, although the contract requires `Missing`. This does not make the test worthless against the reported old bug—the old getter returned `Resolved`, so the old code would fail this assertion—but it leaves the exact fixed state unpinned.

## Requested checks

- Blocking 1: clean for the concrete reported scenario. The versionless envelope sets `HasMissingVersion = true`; the recognised envelope contributes `1.0.74`; lines 101–104 return `Missing`; the writer stores that draft; and the single stored `Missing` row reads back as `Missing`.
- Blocking 2: clean for the concrete reported scenario. Stored rows `(Missing, null)` and `(Resolved, "1.0.74")` produce one distinct version, no conflicting or unrecognised row, and `observations.All(...Resolved)` is false at `SqliteSourceCompatibilityStore.cs:362`; the fallback is `Missing`.
- Every contributing envelope lacks a version: clean. At least one span creates trace evidence, every such envelope sets the missing flag, no version is collected, and the result is `Missing`.
- One malformed version plus one valid version: clean. `HasInvalidVersion` is set, one valid version is collected, and the invalid-token branch returns `Unrecognised`; if more than one distinct valid version is also present, the earlier conflicting branch wins.
- Zero-span envelope: clean. No trace ID is associated with that envelope, so it contributes no evidence. If the payload has no spans for a trace, no trace-resolution row is produced.
- Absent `resource` object: clean. Resource evidence is empty; any span under that `resourceSpans` entry marks its trace as missing.
- Precedence: conflicting correctly wins over invalid/unrecognised and missing evidence. Invalid-token evidence correctly wins over missing. Syntactically valid but registry-unrecognised evidence does not win over missing, as described in the blocking finding.
- `Resolved` reachability: clean. A single envelope with one registry-recognised version produces one version, no invalid or missing flag, a `Resolved` draft, one `Resolved` row, and a definitive `Resolved` result because all stored observations are resolved and agree.
- Added resolver regression test: `PostTraces_SameTraceWithMissingAndRecognisedResourceVersionsResolvesMissing` pins the reported defect exactly. The old resolver lost the missing envelope, returned `Resolved`, and the old code would fail the exact `Missing` assertion.
- Added storage regression test: `GetTraceSourceVersionResolution_MissingAndResolvedObservationsDoesNotReturnResolved` would fail against the old getter because that getter returned `Resolved` when any row was resolved. It is therefore a real regression guard, subject to the exact-state gap above.
- Storage path: clean for blocking 2. `InsertBatch` still forwards each per-observation draft to `InsertTraceSourceVersionResolution`, which writes its state and version without read aggregation. The blocking-2 correction is confined to definitive-read aggregation. Blocking 1 intentionally changes the draft reaching that unchanged writer.

No test command was run: the task permits creating only this review file, while the relevant test hosts create temporary databases and build/test commands may generate additional files. The conclusions above are based on fresh, line-by-line value-flow inspection rather than a green suite.

## Verdict

**fix-first** — the missing flag currently overrides a valid but registry-unrecognised version, violating the required precedence and permanently storing the wrong state.
