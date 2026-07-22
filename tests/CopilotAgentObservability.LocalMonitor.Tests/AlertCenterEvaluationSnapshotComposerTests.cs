using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Alerts;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class AlertCenterEvaluationSnapshotComposerTests
{
    [Theory]
    [InlineData(SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "github-copilot-vscode", "missing_required_capability")]
    [InlineData(SessionSourceSurface.CopilotCli, "github-copilot-cli-otel", "github-copilot-cli", "missing_required_capability")]
    [InlineData(SessionSourceSurface.ClaudeCode, "claude-code-otel", "claude-code", "missing_required_capability")]
    [InlineData(null, "raw-otlp", "raw-otlp", "source_not_applicable")]
    public void Compose_ExactPartitionCreatesSuppressionOnlyTenRuleSnapshot(
        SessionSourceSurface? surface,
        string adapter,
        string expectedSurface,
        string expectedSuppression)
    {
        using var temp = NewTemp();
        var traceId = "exact-trace";
        var sessionId = Guid.CreateVersion7();
        var detail = Detail(sessionId, traceId, surface, adapter, "1.2.3");
        var sessionStore = Store(temp, detail);
        var projection = new ComposerProjectionStore();
        projection.Add(traceId);
        var compatibility = new ComposerCompatibilityStore();
        compatibility.Add(1, expectedSurface, "1.2.3");
        var composer = new AlertCenterEvaluationSnapshotComposer(sessionStore, projection, compatibility);

        var composed = composer.Compose(sessionId, traceId);

        Assert.Equal(AlertCenterSnapshotCompositionStatus.Success, composed.Status);
        var snapshot = Assert.IsType<AlertNormalizedSnapshot>(composed.Snapshot);
        Assert.Equal(expectedSurface, snapshot.SourceSurface);
        Assert.Equal("1.2.3", snapshot.SourceVersion);
        Assert.Equal(sessionId.ToString("D"), snapshot.SessionId);
        Assert.Equal(traceId, snapshot.TraceId);
        Assert.Equal(3, snapshot.Signals.Count);
        Assert.Equal(
            ["monitor-span-row-v1:1", $"session-event-row-v1:{detail.Events[0].EventId:D}", "source-observation-row-v1:1"],
            snapshot.Signals.Select(item => item.Evidence.EvidenceId).Order(StringComparer.Ordinal));
        Assert.All(snapshot.Signals, item =>
        {
            Assert.Equal(AlertSignalStatus.Unknown, item.Status);
            Assert.Empty(item.Metrics);
            Assert.Empty(item.ComparableKeys);
        });
        Assert.All(snapshot.Capabilities, item => Assert.Equal(AlertCapabilityAvailability.Unknown, item.Availability));

        var policy = AlertCenterEvaluationPolicy.Create();
        var evaluation = new AlertEvaluationEngine(policy.Registry, new RejectingEvidenceResolver())
            .Evaluate(snapshot, policy.Configuration);
        Assert.Empty(evaluation.Receipts);
        Assert.Equal(10, evaluation.Suppressions.Count);
        Assert.All(evaluation.Suppressions, item => Assert.Equal(expectedSuppression, item.Code));
        Assert.Empty(evaluation.RejectedMatches);
    }

    [Fact]
    public void Compose_RejectsMissingSessionTraceAndExactOwnershipWithoutFallback()
    {
        using var temp = NewTemp();
        var projection = new ComposerProjectionStore();
        projection.Add("present-trace");
        var sessionStore = Store(temp);
        var composer = new AlertCenterEvaluationSnapshotComposer(sessionStore, projection, new ComposerCompatibilityStore());

        Assert.Equal(
            AlertCenterSnapshotCompositionStatus.SessionNotFound,
            composer.Compose(Guid.CreateVersion7(), "present-trace").Status);

        var sessionId = Guid.CreateVersion7();
        sessionStore.Write(new SessionWriteBatch(Detail(sessionId, "owned-trace", SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "1.2.3"), []));
        Assert.Equal(
            AlertCenterSnapshotCompositionStatus.TraceNotFound,
            composer.Compose(sessionId, "missing-trace").Status);
        Assert.Equal(
            AlertCenterSnapshotCompositionStatus.TraceNotOwned,
            composer.Compose(sessionId, "present-trace").Status);
    }

    [Fact]
    public void Compose_RejectsMissingOrAmbiguousSourceVersionPartition()
    {
        using var temp = NewTemp();
        var projection = new ComposerProjectionStore();
        projection.Add("missing-version");
        projection.Add("mixed-version");
        projection.Add("mixed-source");
        projection.Add("missing-source");
        projection.Add("default-raw-otlp");
        var store = Store(temp);
        var compatibility = new ComposerCompatibilityStore();
        compatibility.Add(1, "github-copilot-vscode", "1.2.3");
        compatibility.Add(2, "github-copilot-vscode", "1.2.3");
        compatibility.Add(3, "github-copilot-vscode", "1.2.3");
        compatibility.Add(4, "github-copilot-vscode", "1.2.3");
        compatibility.Add(5, "raw-otlp", null);
        var composer = new AlertCenterEvaluationSnapshotComposer(store, projection, compatibility);

        var missingVersion = Guid.CreateVersion7();
        store.Write(new SessionWriteBatch(Detail(missingVersion, "missing-version", SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", null), []));

        var mixedVersion = Guid.CreateVersion7();
        var mixedVersionDetail = Detail(mixedVersion, "mixed-version", SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "1.2.3");
        mixedVersionDetail = mixedVersionDetail with
        {
            Events = [.. mixedVersionDetail.Events, Event(mixedVersion, mixedVersionDetail.Runs[0].RunId, "mixed-version", SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "2.0.0")],
        };
        store.Write(new SessionWriteBatch(mixedVersionDetail, []));

        var mixedSource = Guid.CreateVersion7();
        var mixedSourceDetail = Detail(mixedSource, "mixed-source", SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "1.2.3");
        mixedSourceDetail = mixedSourceDetail with
        {
            Events = [.. mixedSourceDetail.Events, Event(mixedSource, mixedSourceDetail.Runs[0].RunId, "mixed-source", SessionSourceSurface.ClaudeCode, "claude-code-otel", "1.2.3")],
        };
        store.Write(new SessionWriteBatch(mixedSourceDetail, []));

        var missingSource = Guid.CreateVersion7();
        store.Write(new SessionWriteBatch(Detail(missingSource, "missing-source", null, "unknown-adapter", "1.2.3"), []));

        var defaultRawOtlp = Guid.CreateVersion7();
        store.Write(new SessionWriteBatch(Detail(defaultRawOtlp, "default-raw-otlp", null, "raw-otlp", null), []));

        Assert.Equal(AlertCenterSnapshotCompositionStatus.SourcePartitionMissing, composer.Compose(missingVersion, "missing-version").Status);
        Assert.Equal(AlertCenterSnapshotCompositionStatus.SourcePartitionAmbiguous, composer.Compose(mixedVersion, "mixed-version").Status);
        Assert.Equal(AlertCenterSnapshotCompositionStatus.SourcePartitionAmbiguous, composer.Compose(mixedSource, "mixed-source").Status);
        Assert.Equal(AlertCenterSnapshotCompositionStatus.SourcePartitionMissing, composer.Compose(missingSource, "missing-source").Status);
        Assert.Equal(AlertCenterSnapshotCompositionStatus.SourcePartitionMissing, composer.Compose(defaultRawOtlp, "default-raw-otlp").Status);
    }

    [Fact]
    public void Compose_IgnoresOtherTracePartitionsInsideExactSession()
    {
        using var temp = NewTemp();
        var sessionId = Guid.CreateVersion7();
        var detail = Detail(sessionId, "selected-trace", SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "1.2.3");
        var otherRun = Run(sessionId, "other-trace", SessionSourceSurface.ClaudeCode);
        detail = detail with
        {
            Runs = [.. detail.Runs, otherRun],
            Events = [.. detail.Events, Event(sessionId, otherRun.RunId, "other-trace", SessionSourceSurface.ClaudeCode, "claude-code-otel", "9.9.9")],
        };
        var sessionStore = Store(temp, detail);
        var projection = new ComposerProjectionStore();
        projection.Add("selected-trace");
        var compatibility = new ComposerCompatibilityStore();
        compatibility.Add(1, "github-copilot-vscode", "1.2.3");

        var composed = new AlertCenterEvaluationSnapshotComposer(sessionStore, projection, compatibility).Compose(sessionId, "selected-trace");

        Assert.Equal(AlertCenterSnapshotCompositionStatus.Success, composed.Status);
        Assert.Equal("github-copilot-vscode", composed.Snapshot!.SourceSurface);
        Assert.Equal("1.2.3", composed.Snapshot.SourceVersion);
    }

    [Fact]
    public void Compose_RequiresEveryMonitorRowToHaveOneExactSourceCompatibilityPartition()
    {
        using var temp = NewTemp();
        var sessionId = Guid.CreateVersion7();
        var traceId = "source-observation-trace";
        var sessionStore = Store(temp, Detail(sessionId, traceId, SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "1.2.3"));
        var projection = new ComposerProjectionStore();
        projection.Add(traceId, spanCount: 2);
        var compatibility = new ComposerCompatibilityStore();
        compatibility.Add(1, "github-copilot-vscode", "1.2.3");
        var composer = new AlertCenterEvaluationSnapshotComposer(sessionStore, projection, compatibility);

        Assert.Equal(AlertCenterSnapshotCompositionStatus.SourcePartitionMissing, composer.Compose(sessionId, traceId).Status);

        compatibility.Add(2, "github-copilot-vscode", null);
        Assert.Equal(AlertCenterSnapshotCompositionStatus.SourcePartitionMissing, composer.Compose(sessionId, traceId).Status);

        compatibility.Add(2, "claude-code", "1.2.3");
        Assert.Equal(AlertCenterSnapshotCompositionStatus.SourcePartitionAmbiguous, composer.Compose(sessionId, traceId).Status);

        compatibility.Add(2, "github-copilot-vscode", "2.0.0");
        Assert.Equal(AlertCenterSnapshotCompositionStatus.SourcePartitionAmbiguous, composer.Compose(sessionId, traceId).Status);

        compatibility.Add(2, "github-copilot-vscode", "1.2.3");
        var composed = composer.Compose(sessionId, traceId);
        Assert.Equal(AlertCenterSnapshotCompositionStatus.Success, composed.Status);
        Assert.Equal(5, composed.Snapshot!.Signals.Count);
        Assert.Contains(composed.Snapshot.Signals, item => item.Evidence.EvidenceId == "source-observation-row-v1:1");
        Assert.Contains(composed.Snapshot.Signals, item => item.Evidence.EvidenceId == "source-observation-row-v1:2");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void Compose_RejectsMissingOrIncompleteMonitorSpanProjection(int persistedSpanCount, int reportedSpanCount)
    {
        using var temp = NewTemp();
        var sessionId = Guid.CreateVersion7();
        var traceId = $"incomplete-span-projection-{persistedSpanCount}-{reportedSpanCount}";
        var sessionStore = Store(temp, Detail(
            sessionId,
            traceId,
            SessionSourceSurface.VisualStudioCode,
            "github-copilot-vscode-otel",
            "1.2.3"));
        var projection = new ComposerProjectionStore();
        projection.Add(traceId, persistedSpanCount, reportedSpanCount);
        var compatibility = new ComposerCompatibilityStore();
        for (var rawRecordId = 1; rawRecordId <= persistedSpanCount; rawRecordId++)
        {
            compatibility.Add(rawRecordId, "github-copilot-vscode", "1.2.3");
        }

        var composed = new AlertCenterEvaluationSnapshotComposer(sessionStore, projection, compatibility)
            .Compose(sessionId, traceId);

        Assert.Equal(AlertCenterSnapshotCompositionStatus.TraceIncomplete, composed.Status);
        Assert.Null(composed.Snapshot);
    }

    [Fact]
    public void EvidenceResolver_RequiresExactRowSessionTraceSpanAndTimestampTuple()
    {
        using var temp = NewTemp();
        var sessionId = Guid.CreateVersion7();
        var traceId = "exact-evidence-trace";
        var detail = Detail(sessionId, traceId, SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "1.2.3");
        var sessionStore = Store(temp, detail);
        var projection = new ComposerProjectionStore();
        projection.Add(traceId);
        var compatibility = new ComposerCompatibilityStore();
        compatibility.Add(1, "github-copilot-vscode", "1.2.3");
        var resolver = new AlertCenterEvidenceResolver(sessionStore, projection, compatibility);
        var observedAt = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        var exact = new AlertEvidenceReference(
            AlertEvidenceKind.Span,
            "monitor-span-row-v1:1",
            sessionId.ToString("D"),
            traceId,
            "span-1",
            null,
            null,
            null,
            observedAt);

        var resolved = resolver.Resolve(exact);

        Assert.True(resolver.Exists(exact));
        Assert.Equal(AlertCenterEvidenceAvailability.Available, resolved.Availability);
        Assert.Equal($"/traces/{traceId}?span=span-1", resolved.Href);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Available,
            resolver.ResolveForReceipt(exact, "github-copilot-vscode", "1.2.3").Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.ResolveForReceipt(exact, "github-copilot-cli", "1.2.3").Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.ResolveForReceipt(exact, "github-copilot-vscode", "9.9.9").Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Missing,
            resolver.Resolve(exact with { EvidenceId = "monitor-span-row-v1:999" }).Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(exact with { EvidenceId = "monitor-span-row-v1:01" }).Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(exact with { SpanId = "same-trace-wrong-span" }).Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(exact with { ObservedAt = observedAt.AddSeconds(1) }).Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Missing,
            resolver.Resolve(exact with { SessionId = Guid.CreateVersion7().ToString("D") }).Availability);

        var missingObservation = new AlertCenterEvidenceResolver(sessionStore, projection, new ComposerCompatibilityStore());
        Assert.False(missingObservation.Exists(exact));
        Assert.Equal(AlertCenterEvidenceAvailability.Unknown, missingObservation.Resolve(exact).Availability);

        var sourceObservation = new AlertEvidenceReference(
            AlertEvidenceKind.Trace,
            "source-observation-row-v1:1",
            sessionId.ToString("D"),
            traceId,
            null,
            null,
            null,
            null,
            observedAt);
        Assert.True(resolver.Exists(sourceObservation));
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(sourceObservation with { EvidenceId = "source-observation-row-v1:01" }).Availability);
        Assert.Equal(
            $"/traces/{traceId}",
            resolver.Resolve(sourceObservation).Href);
    }

    [Fact]
    public void EvidenceResolver_ReportsExactExpiredSessionEventWithoutLosingSanitizedNavigation()
    {
        using var temp = NewTemp();
        var sessionId = Guid.CreateVersion7();
        var traceId = "expired-event-trace";
        var detail = Detail(sessionId, traceId, SessionSourceSurface.ClaudeCode, "claude-code-otel", "1.2.3");
        var expired = detail.Events[0] with { ContentState = SessionContentState.ExpiredPendingDeletion };
        detail = detail with { Events = [expired] };
        var sessionStore = Store(temp, detail);
        var projection = new ComposerProjectionStore();
        projection.Add(traceId);
        var compatibility = new ComposerCompatibilityStore();
        compatibility.Add(1, "claude-code", "1.2.3");
        var resolver = new AlertCenterEvidenceResolver(sessionStore, projection, compatibility);
        var reference = new AlertEvidenceReference(
            AlertEvidenceKind.Event,
            $"session-event-row-v1:{expired.EventId:D}",
            sessionId.ToString("D"),
            traceId,
            null,
            null,
            expired.EventId.ToString("D"),
            null,
            expired.OccurredAt);

        var resolved = resolver.Resolve(reference);

        Assert.True(resolver.Exists(reference));
        Assert.Equal(AlertCenterEvidenceAvailability.Expired, resolved.Availability);
        Assert.Equal("expired", resolved.ContentState);
        Assert.Equal($"/diagnostics?session_id={sessionId:D}", resolved.Href);
    }

    [Fact]
    public void EvidenceResolver_ResolvesOpaqueEventEvidenceByExactEventIdentityAndTuple()
    {
        using var temp = NewTemp();
        var sessionId = Guid.CreateVersion7();
        var traceId = "generic-event-trace";
        var detail = Detail(sessionId, traceId, SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "1.2.3");
        var exactEvent = Assert.Single(detail.Events);
        var sessionStore = Store(temp, detail);
        var projection = new ComposerProjectionStore();
        projection.Add(traceId);
        var resolver = new AlertCenterEvidenceResolver(sessionStore, projection, new ComposerCompatibilityStore());
        var reference = new AlertEvidenceReference(
            AlertEvidenceKind.Event,
            "opaque-event-evidence",
            sessionId.ToString("D"),
            traceId,
            null,
            null,
            exactEvent.EventId.ToString("D"),
            null,
            exactEvent.OccurredAt);

        var resolved = resolver.ResolveForReceipt(reference, "github-copilot-vscode", "1.2.3");

        Assert.Equal(AlertCenterEvidenceAvailability.Available, resolved.Availability);
        Assert.Equal("not_captured", resolved.ContentState);
        Assert.Equal($"/diagnostics?session_id={sessionId:D}", resolved.Href);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Missing,
            resolver.Resolve(reference with { EventId = Guid.CreateVersion7().ToString("D") }).Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(reference with { TraceId = "foreign-trace" }).Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(reference with { ObservedAt = exactEvent.OccurredAt.AddTicks(1) }).Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.ResolveForReceipt(reference, "github-copilot-cli", "1.2.3").Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(reference with
            {
                EvidenceId = $"session-event-row-v1:{Guid.CreateVersion7():D}",
            }).Availability);
    }

    [Fact]
    public void EvidenceResolver_ResolvesGenericSessionWithOptionalTraceAndRetentionState()
    {
        using var temp = NewTemp();
        var sessionId = Guid.CreateVersion7();
        var traceId = "generic-session-trace";
        var detail = Detail(sessionId, traceId, SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "1.2.3");
        detail = detail with { Session = detail.Session with { RawRetentionState = SessionRawRetentionState.Expiring } };
        var sessionStore = Store(temp, detail);
        var projection = new ComposerProjectionStore();
        projection.Add(traceId);
        var resolver = new AlertCenterEvidenceResolver(sessionStore, projection, new ComposerCompatibilityStore());
        var reference = new AlertEvidenceReference(
            AlertEvidenceKind.Session,
            "opaque-session-evidence",
            sessionId.ToString("D"),
            null,
            null,
            null,
            null,
            null,
            detail.Session.LastSeenAt);

        var available = resolver.ResolveForReceipt(reference, "unrelated-source", "9.9.9");

        Assert.Equal(AlertCenterEvidenceAvailability.Available, available.Availability);
        Assert.Equal("available", available.ContentState);
        Assert.Equal($"/diagnostics?session_id={sessionId:D}", available.Href);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Available,
            resolver.ResolveForReceipt(reference with { TraceId = traceId }, "github-copilot-vscode", "1.2.3").Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.ResolveForReceipt(reference with { TraceId = traceId }, "github-copilot-cli", "1.2.3").Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(reference with { TraceId = "foreign-trace" }).Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(reference with { EventId = "unverified-child" }).Availability);

        var expiredStore = Store(temp, detail with
        {
            Session = detail.Session with { RawRetentionState = SessionRawRetentionState.ExpiredPendingDeletion },
        });
        var expired = new AlertCenterEvidenceResolver(expiredStore, projection, new ComposerCompatibilityStore()).Resolve(reference);
        Assert.Equal(AlertCenterEvidenceAvailability.Expired, expired.Availability);
        Assert.Equal("expired", expired.ContentState);
        Assert.Equal($"/diagnostics?session_id={sessionId:D}", expired.Href);

        using var missingTemp = NewTemp();
        var missing = new AlertCenterEvidenceResolver(Store(missingTemp), projection, new ComposerCompatibilityStore()).Resolve(reference);
        Assert.Equal(AlertCenterEvidenceAvailability.Missing, missing.Availability);
        Assert.Equal(AlertCenterEvidenceAvailability.Unknown, resolver.Resolve(reference with { SessionId = "not-a-canonical-uuid" }).Availability);
    }

    [Fact]
    public void EvidenceResolver_ResolvesGenericTraceOnlyThroughExactProjectionOwnershipAndPartition()
    {
        using var temp = NewTemp();
        var sessionId = Guid.CreateVersion7();
        var traceId = "generic-trace";
        var detail = Detail(sessionId, traceId, SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "1.2.3");
        var sessionStore = Store(temp, detail);
        var projection = new ComposerProjectionStore();
        projection.Add(traceId);
        var resolver = new AlertCenterEvidenceResolver(sessionStore, projection, new ComposerCompatibilityStore());
        var reference = new AlertEvidenceReference(
            AlertEvidenceKind.Trace,
            "arbitrary-opaque-trace-evidence",
            sessionId.ToString("D"),
            traceId,
            null,
            null,
            null,
            null,
            detail.Session.LastSeenAt);

        var available = resolver.ResolveForReceipt(reference, "github-copilot-vscode", "1.2.3");

        Assert.Equal(AlertCenterEvidenceAvailability.Available, available.Availability);
        Assert.Equal($"/traces/{traceId}", available.Href);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.ResolveForReceipt(reference, "github-copilot-vscode", "9.9.9").Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(reference with { TraceId = "not-owned" }).Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(reference with { SpanId = "unverified-child" }).Availability);
        Assert.Equal(
            AlertCenterEvidenceAvailability.Unknown,
            resolver.Resolve(reference with { EvidenceId = "source-observation-row-v1:01" }).Availability);

        var missingProjection = new AlertCenterEvidenceResolver(
            sessionStore,
            new ComposerProjectionStore(),
            new ComposerCompatibilityStore());
        Assert.Equal(AlertCenterEvidenceAvailability.Missing, missingProjection.Resolve(reference).Availability);
    }

    [Fact]
    public void Compose_SeparatesBusyFromUnavailableStoreFailures()
    {
        using var temp = NewTemp();
        var sessionId = Guid.CreateVersion7();
        var detail = Detail(sessionId, "busy-trace", SessionSourceSurface.VisualStudioCode, "github-copilot-vscode-otel", "1.2.3");
        var sessionStore = Store(temp, detail);

        Assert.Equal(
            AlertCenterSnapshotCompositionStatus.StoreBusy,
            new AlertCenterEvaluationSnapshotComposer(sessionStore, new ThrowingProjectionStore(busy: true), new ComposerCompatibilityStore())
                .Compose(sessionId, "busy-trace").Status);
        Assert.Equal(
            AlertCenterSnapshotCompositionStatus.StoreUnavailable,
            new AlertCenterEvaluationSnapshotComposer(sessionStore, new ThrowingProjectionStore(busy: false), new ComposerCompatibilityStore())
                .Compose(sessionId, "busy-trace").Status);
    }

    private static MonitorTempDirectory NewTemp() => new()
    {
        TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
    };

    private static SqliteSessionStore Store(MonitorTempDirectory temp, SessionDetail? detail = null)
    {
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        if (detail is not null) store.Write(new SessionWriteBatch(detail, []));
        return store;
    }

    private static SessionDetail Detail(
        Guid sessionId,
        string traceId,
        SessionSourceSurface? surface,
        string adapter,
        string? version)
    {
        var observed = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        var run = Run(sessionId, traceId, surface);
        return new(
            new ObservedSession(
                sessionId,
                ObservedSessionStatus.Completed,
                SessionCompleteness.Rich,
                "repo-fixture",
                "workspace-fixture",
                observed,
                observed.AddMinutes(1),
                observed.AddMinutes(1),
                SessionRawRetentionState.NotCaptured,
                observed,
                observed.AddMinutes(1)),
            [],
            [run],
            [Event(sessionId, run.RunId, traceId, surface, adapter, version)]);
    }

    private static ObservedSessionRun Run(Guid sessionId, string traceId, SessionSourceSurface? surface)
    {
        var observed = new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        return new(
            Guid.CreateVersion7(), sessionId, surface, null, traceId, null, null,
            ObservedSessionStatus.Completed, observed, observed.AddMinutes(1), null, null, null);
    }

    private static ObservedSessionEvent Event(
        Guid sessionId,
        Guid runId,
        string traceId,
        SessionSourceSurface? surface,
        string adapter,
        string? version) => new(
            Guid.CreateVersion7(),
            sessionId,
            runId,
            surface,
            null,
            traceId,
            "completed",
            adapter,
            $"event-{Guid.NewGuid():N}",
            "trace",
            new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero),
            SessionContentState.NotCaptured,
            version,
            "monitor-projection-v1",
            null,
            "session-normalization-v1");

    private sealed class RejectingEvidenceResolver : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => false;
    }

    private sealed class ComposerProjectionStore : ProjectionStoreTestDouble
    {
        private readonly Dictionary<string, MonitorTraceRow> traces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<MonitorSpanRow>> spans = new(StringComparer.Ordinal);

        internal void Add(string traceId, int spanCount = 1, int? reportedSpanCount = null)
        {
            traces[traceId] = new MonitorTraceRow(
                traces.Count + 1,
                traceId,
                null,
                null,
                null,
                null,
                null,
                null,
                reportedSpanCount ?? spanCount,
                0,
                0,
                "2026-07-23T10:00:00.0000000Z",
                "2026-07-23T10:01:00.0000000Z",
                "2026-07-23T10:01:01.0000000Z",
                null,
                null,
                null,
                null,
                null,
                60_000,
                null,
                "repo-fixture",
                "workspace-fixture",
                null,
                null,
                null,
                "ok");
            var firstId = spans.Values.Sum(items => items.Count) + 1;
            spans[traceId] = Enumerable.Range(0, spanCount)
                .Select(index => new MonitorSpanRow(
                    Id: firstId + index,
                    RawRecordId: firstId + index,
                    TraceId: traceId,
                    SpanId: $"span-{firstId + index}",
                    ParentSpanId: null,
                    SpanOrdinal: index,
                    Operation: "fixture",
                    Category: null,
                    ToolName: null,
                    ToolType: null,
                    McpToolName: null,
                    McpServerHash: null,
                    AgentName: null,
                    RequestModel: null,
                    ResponseModel: null,
                    InputTokens: null,
                    OutputTokens: null,
                    TotalTokens: null,
                    ReasoningTokens: null,
                    CacheReadTokens: null,
                    CacheCreationTokens: null,
                    Status: null,
                    ErrorType: null,
                    FinishReasons: null,
                    ConversationId: null,
                    DurationMs: null,
                    StartTime: new DateTimeOffset(2026, 7, 23, 10, 0, index, TimeSpan.Zero).ToString("O"),
                    EndTime: null,
                    ProjectedAt: "2026-07-23T10:01:01.0000000Z"))
                .ToList();
        }

        public override MonitorTraceRow? GetMonitorTrace(string traceId) => traces.GetValueOrDefault(traceId);
        public override IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId) => spans.GetValueOrDefault(traceId) ?? [];
    }

    private sealed class ComposerCompatibilityStore : ISourceCompatibilityStore
    {
        private readonly Dictionary<long, SourceCompatibilityRow> rows = [];

        internal void Add(long rawRecordId, string? surface, string? version)
        {
            rows[rawRecordId] = new SourceCompatibilityRow(
                Id: rawRecordId,
                ObservationId: $"observation-{rawRecordId}",
                RawRecordId: rawRecordId,
                IngestBatchId: $"batch-{rawRecordId}",
                SourceSurface: surface,
                SourceApplicationVersion: version,
                SourceAdapter: surface is null ? null : $"{surface}-otel",
                AdapterVersion: "1",
                SchemaFingerprint: "schema-fixture",
                InventoryHash: "inventory-fixture",
                CompatibilityState: SourceCompatibilityState.Supported,
                ReasonCodes: [],
                NextAction: SourceCompatibilityNextActions.None,
                CaptureContentState: SourceCaptureContentState.NotCaptured,
                UnknownSpanCount: 0,
                UnknownEventCount: 0,
                UnknownAttributeCount: 0,
                OverflowDistinctCount: 0,
                OverflowOccurrenceCount: 0,
                ObservedAt: new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero),
                UnknownObservations: []);
        }

        public void CreateSchema() { }
        public long RecordAdapterFailure(SourceAdapterFailureDraft failure) => throw new NotSupportedException();
        public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId) => rows.GetValueOrDefault(rawRecordId);
        public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit) => rows.Values
            .Where(item => item.Id > (after ?? 0))
            .OrderBy(item => item.Id)
            .Take(limit)
            .ToArray();
    }

    private sealed class ThrowingProjectionStore(bool busy) : ProjectionStoreTestDouble
    {
        public override MonitorTraceRow? GetMonitorTrace(string traceId)
        {
            if (busy) throw new PersistenceBusyException();
            throw new InvalidOperationException("sanitized fixture failure");
        }
    }
}
