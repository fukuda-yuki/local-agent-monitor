# Issue #128 (B3) Skill projection gating findings

## Q1. Does `compatibility_state = schema_drift_detected` suppress span projection?

No. `SchemaDriftDetected` is diagnostic state, reason, and next action. It has no
projection-suppression flag.

The deciding `SourceCompatibilityDecision.ForState` lines are:

```csharp
SourceCompatibilityState.SchemaDriftDetected => CreateSingle(
    state, SourceCompatibilityReasonCodes.SchemaDriftDetected,
    SourceCompatibilityNextActions.CaptureFixtureAndReviewMapping),
```

`CreateSingle` only returns `new(state, SourceReasonSet.Create([reason]), action)`.
`SourceObservationBatchDraft.Create` rejects only this state:

```csharp
if (decision.State == SourceCompatibilityState.AdapterFailure)
{
    throw new ArgumentException(
        "Successful batch observations cannot carry adapter failure decisions.",
        nameof(decision));
}
```

The span builder receives no decision:

```csharp
public static IReadOnlyList<MonitorSpanProjection> Build(RawTelemetryRecord record)
```

It adds either a Claude projection or `ProjectSpan(span, ordinal)` for every
decoded span. Therefore `schema_drift_detected` alone does not suppress span
projection. A Skill projection added to this path remains visible under the
measured captures' compatibility state and is not silently dropped because of
that state.

The new Skill contract imposes a separate per-trace source-version gate. An
unrecognised version fails that gate even though compatibility state does not
gate the existing generic span projection.

## Q2. Why is `source_unknown_observations` empty?

The evaluator ordering is not the cause.

`SourceCompatibilityEvaluator.Assess` receives an already constructed
`SourceStructuralInventory`. The fingerprint check returns before:

```csharp
if (inventory.HasUnknownFields)
{
    return SourceCompatibilityDecision.ForState(
        SourceCompatibilityState.SupportedWithUnknownFields);
}
```

That return changes only the decision; it does not mutate or discard inventory.
Unknown-field state was computed earlier:

```csharp
public bool HasUnknownFields =>
    UnknownSpanCount != 0 ||
    UnknownEventCount != 0 ||
    UnknownAttributeCount != 0;
```

`SourceStructuralInventory.Create` counts only occurrences whose `Unknown` value
is non-null and derives `RetainedUnknownIdentities` from the same occurrences.
`SourceUnknownObservationDraft.Create` additionally requires its identity to
exist in `parent.Inventory.RetainedUnknownIdentities`.

The measured `unknown_attribute_count = 0` therefore means the inventory already
contained no attributes classified as unknown. The empty
`source_unknown_observations` table follows from that separate inventory result,
not from the later `SchemaDriftDetected` return.

The permitted files do not show the decoder/classifier that constructs
`SourceStructuralOccurrence` and decides whether `Unknown` is populated. That
decoder/classifier file is required to determine why 51 arriving keys produced
zero unknown attributes. Its filename cannot be identified without the
repository sweep prohibited by this task.

## Q3. Where must `service.version` be captured so it is per-trace?

It must be read inside the outer `resourceSpans` loop, before spans are flattened.
For each `resourceSpan`, the existing exact read is:

```csharp
var resourceAttributes = OtlpSpanReader.ReadResourceAttributes(resourceSpan);
var sourceVersion =
    OtlpSpanReader.ReadString(resourceAttributes, "service.version");
```

The resource-scoped value must be associated with every contained span's
`TraceId`. Resolution then occurs independently for each trace, allowing
different `resourceSpan` envelopes in one ingest batch to bind different
versions to different trace IDs.

The current source-observation shape cannot represent that binding.
`SourceObservationBatchDraft` has one scalar:

```csharp
public string? SourceApplicationVersion { get; }
```

Its only ingest identity is `public string IngestBatchId { get; }`; it has no
`TraceId`. It therefore cannot store or retrieve two source versions as values
bound to two traces in one ingest batch. Multiple unbound observation rows would
not establish which version belongs to which trace.

A per-trace storage field or trace-keyed relation is required. Missing,
conflicting, or unrecognised per-trace version evidence fails the Skill
projection closed.

These findings require no change to the frozen v1 API shapes, the five
`session_events.content_state` values, or the closed raw-bearing surface
enumeration.
