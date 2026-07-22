using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalEvidenceProductionTests
{
    [Theory]
    [InlineData("native_ids")]
    [InlineData("runs")]
    public async Task SnapshotSource_RejectsEachBoundedChildCollectionAboveItsLimit(string childKind)
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 7, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            null, null, now, now, now, SessionRawRetentionState.NotCaptured, now, now);
        var nativeIds = childKind == "native_ids"
            ? Enumerable.Range(0, HistoricalEvidenceContractsV1.MaximumNativeIdsPerSession + 1)
                .Select(index => new SessionNativeId(sessionId, SessionSourceSurface.ClaudeCode, $"native-{index}", SessionBindingKind.Native, now.AddTicks(index))).ToArray()
            : [];
        var runs = childKind == "runs"
            ? Enumerable.Range(0, HistoricalEvidenceContractsV1.MaximumRunsPerSession + 1)
                .Select(index => new ObservedSessionRun(Guid.CreateVersion7(), sessionId, SessionSourceSurface.ClaudeCode,
                    $"run-{index}", null, null, null, ObservedSessionStatus.Completed, now.AddTicks(index), now.AddTicks(index + 1), null, null, null)).ToArray()
            : [];
        store.Write(new(new(session, nativeIds, runs, []), []));
        var source = new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath, store);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            source.OpenSnapshotAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public async Task HostApplication_EmbedsPersistedIssue59ReceiptAndCandidateWithoutOutOfBandReopen()
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        const string traceId = "7123456789abcdef0123456789abcdef";
        const string firstSpan = "7123456789abcdef";
        const string secondSpan = "8123456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            "owner/repository", null, now, now, now, SessionRawRetentionState.NotCaptured, now, now);
        var events = new[]
        {
            new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null, traceId, "ok",
                "claude-code-otel", $"{traceId}/{firstSpan}", "otel.span", now, SessionContentState.NotCaptured),
            new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null, traceId, "ok",
                "claude-code-otel", $"{traceId}/{secondSpan}", "otel.span", now.AddTicks(1), SessionContentState.NotCaptured),
        };
        store.Write(new(new(session, [], [], events), []));
        var analysisStore = app.Services.GetRequiredService<IMonitorAnalysisStore>();
        var analysisRunId = analysisStore.StartRun(traceId, null, firstSpan, MonitorAnalysisFocus.InstructionDiagnosis, now).RunId;
        var rawReferences = new[]
        {
            new InstructionRawEvidenceReferenceV1(sessionId.ToString(), traceId, firstSpan, 1, InstructionEvidenceRelativePositionV1.Anchor),
            new InstructionRawEvidenceReferenceV1(sessionId.ToString(), traceId, secondSpan, 2, InstructionEvidenceRelativePositionV1.Anchor),
        };
        var evidenceIndex = new InstructionFindingEvidenceIndexV1(traceId,
        [
            new(sessionId.ToString(), traceId, firstSpan, 1, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn),
            new(sessionId.ToString(), traceId, secondSpan, 2, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn),
        ]);
        var handoff = InstructionFindingPipelineV1.Generate(analysisRunId, evidenceIndex,
        [
            new(InstructionFindingCategoryV1.AcceptanceCriteriaMissing, InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.DeterministicPrepass, rawReferences),
        ]);
        Assert.Single(handoff.Candidates);
        new SqliteInstructionFindingHandoffStore(temp.DatabasePath).Save(handoff, now);

        var service = app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>();
        var created = await service.CreateAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), CancellationToken.None);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM instruction_finding_handoffs WHERE analysis_run_id=$id;";
            delete.Parameters.AddWithValue("$id", analysisRunId);
            Assert.Equal(1, delete.ExecuteNonQuery());
        }

        var reopened = Assert.IsType<HistoricalEvidenceExtractionV1>(service.Get(created.RawLocal.ExtractionId));
        var group = Assert.Single(reopened.RawLocal.EvidenceGroups, value => value.Kind == HistoricalEvidenceGroupKindV1.InstructionFinding);
        Assert.Equal(Assert.Single(handoff.Findings).FindingId, group.FindingReceipt!.FindingId);
        Assert.Equal(handoff.Findings[0].EvidenceRefs, group.FindingReceipt.EvidenceRefs);
        Assert.Equal(Assert.Single(handoff.Candidates).CandidateId, group.FindingCandidate!.CandidateId);
        Assert.True(Assert.Single(reopened.RawLocal.Sessions).Capabilities.InstructionFindingReference);
    }

    [Fact]
    public async Task SnapshotSource_RejectsEventOverflowWithoutCallingUnboundedSessionDetailRead()
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            null, null, now, now, now, SessionRawRetentionState.NotCaptured, now, now);
        var events = Enumerable.Range(0, 4097).Select(index => new ObservedSessionEvent(
            Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null, null, "ok",
            "bounded-test", $"event-{index}", "metadata", now.AddTicks(index), SessionContentState.NotCaptured)).ToArray();
        store.Write(new(new(session, [], [], events), []));
        var proxy = DispatchProxy.Create<ISessionStore, GetDetailRejectingProxy>();
        var source = new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath, proxy);

        var exception = await Assert.ThrowsAsync<HistoricalEvidenceValidationException>(() =>
            source.OpenSnapshotAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), CancellationToken.None).AsTask());

        Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidContract, exception.Code);
        Assert.Equal(0, ((GetDetailRejectingProxy)(object)proxy).GetDetailCount);
    }

    [Fact]
    public async Task HostApplication_CreatesPersistsAndReopensConsumerCompleteDatasetFromSessionStore()
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        const string traceId = "0123456789abcdef0123456789abcdef";
        const string spanId = "0123456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            "owner/repository", "workspace", now, now.AddSeconds(3), now.AddSeconds(3), SessionRawRetentionState.NotCaptured, now, now);
        var run = new ObservedSessionRun(runId, sessionId, SessionSourceSurface.ClaudeCode, null, traceId, null, "gpt-5.4",
            ObservedSessionStatus.Completed, now, now.AddSeconds(3), 10, 20, 30);
        var @event = new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, runId, SessionSourceSurface.ClaudeCode,
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "otel.span", now,
            SessionContentState.NotCaptured, "2.1.215", "adapter.v1");
        store.Write(new(new(session, [], [run], [@event]), []));

        var service = app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>();
        var created = await service.CreateAsync(HistoricalEvidenceSelectionV1.Create(
            repository: "owner/repository", sourceSurfaces: [SessionSourceSurface.ClaudeCode], sanitizedOnly: true), CancellationToken.None);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET repository='mutated-after-extraction' WHERE session_id=$id;";
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var reopened = service.Get(created.RawLocal.ExtractionId);

        Assert.NotNull(reopened);
        var raw = Assert.Single(reopened.RawLocal.Sessions);
        Assert.Equal([SessionSourceSurface.ClaudeCode], raw.Metadata.SourceSurfaces);
        Assert.Equal("2.1.215", Assert.Single(raw.Metadata.SourceProvenance).SourceApplicationVersion);
        Assert.Equal("gpt-5.4", Assert.Single(raw.Metadata.ModelObservations).Model);
        Assert.Equal(3000, Assert.Single(raw.Metadata.DurationObservations).DurationMs);
        Assert.Contains(reopened.RawLocal.EvidenceGroups, group => group.Kind == HistoricalEvidenceGroupKindV1.TokenRollup);
        Assert.StartsWith("repository-ref-", reopened.RepositorySafe.Selection.Repository);
        Assert.StartsWith("repository-ref-", Assert.Single(reopened.RepositorySafe.Sessions).Metadata.Repository);
        Assert.Equal("owner/repository", raw.Metadata.Repository);
    }

    [Fact]
    public async Task SnapshotIdentityChangesWhenOnlyTheOmittedMatchingCountChanges()
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 10, 30, 0, TimeSpan.Zero);
        WriteExactSession(store, "owner/snapshot", now.AddMinutes(1), "4123456789abcdef0123456789abcdef", "4123456789abcdef");
        WriteExactSession(store, "owner/snapshot", now.AddMinutes(2), "5123456789abcdef0123456789abcdef", "5123456789abcdef");
        var service = app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>();
        var selection = HistoricalEvidenceSelectionV1.Create(repository: "owner/snapshot", maximumSessionCount: 1, sanitizedOnly: true);
        var first = await service.CreateAsync(selection, CancellationToken.None);

        WriteExactSession(store, "owner/snapshot", now, "6123456789abcdef0123456789abcdef", "6123456789abcdef");
        var second = await service.CreateAsync(selection, CancellationToken.None);

        Assert.NotEqual(first.RawLocal.SnapshotId, second.RawLocal.SnapshotId);
        Assert.NotEqual(first.RawLocal.ExtractionId, second.RawLocal.ExtractionId);
        Assert.Equal(1, first.RawLocal.TruncatedSessionCount);
        Assert.Equal(2, second.RawLocal.TruncatedSessionCount);
    }

    [Fact]
    public async Task HostApplication_ProcessesTheMaximumWindowWithinTheBoundedBudget()
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 10, 45, 0, TimeSpan.Zero);
        foreach (var index in Enumerable.Range(1, HistoricalEvidenceContractsV1.MaximumSessions + 1))
            WriteExactSession(store, "owner/max-window", now.AddSeconds(index), index.ToString("x32"), index.ToString("x16"));
        var service = app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var extraction = await service.CreateAsync(HistoricalEvidenceSelectionV1.Create(
            repository: "owner/max-window", maximumSessionCount: HistoricalEvidenceContractsV1.MaximumSessions, sanitizedOnly: true), CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(HistoricalEvidenceContractsV1.MaximumSessions, extraction.RawLocal.Sessions.Count);
        Assert.Equal(1, extraction.RawLocal.TruncatedSessionCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Maximum-window extraction took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task SnapshotSource_ReadsDescriptorOnlyAfterRawCapabilityAndGrantedRetentionLease()
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        const string traceId = "1123456789abcdef0123456789abcdef";
        const string spanId = "1123456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            "owner/repository", "workspace", now, now.AddSeconds(1), now.AddSeconds(1), SessionRawRetentionState.Expiring, now, now);
        var @event = new ObservedSessionEvent(eventId, sessionId, null, SessionSourceSurface.ClaudeCode,
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "user.message", now,
            SessionContentState.Available, "2.1.215", "adapter.v1");
        store.Write(new(new(session, [], [], [@event]),
            [new SessionEventContent(eventId, "application/json", "{\"text\":\"Use focused tests\"}", now, now.AddDays(1))]));
        var reader = new RecordingContentReader(new(
            SessionContentReadDisposition.Granted,
            new SessionContentReadLease(
                new SessionEventContent(eventId, "application/json", "{\"text\":\"Use focused tests\"}", now, now.AddDays(1)),
                () => ValueTask.CompletedTask)));
        var source = new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath, store, reader);

        var sanitized = await HistoricalEvidenceExtractorV1.ExtractAsync(
            HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId], sanitizedOnly: true), source, CancellationToken.None);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(HistoricalDescriptorStateV1.NotRequested, Assert.Single(sanitized.RawLocal.Sessions).DescriptorState);

        var raw = await HistoricalEvidenceExtractorV1.ExtractAsync(
            HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), source, CancellationToken.None);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal("Use focused tests", Assert.Single(raw.RawLocal.Sessions).RawLocalDescriptor);
        Assert.Null(Assert.Single(raw.RepositorySafe.Sessions).RawLocalDescriptor);
    }

    [Theory]
    [InlineData((int)SessionContentReadDisposition.Denied)]
    [InlineData((int)SessionContentReadDisposition.Busy)]
    public async Task SnapshotSource_DeniedOrBusyCurrentLeaseDoesNotMaterializeDescriptor(int dispositionValue)
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 11, 30, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        const string traceId = "9123456789abcdef0123456789abcdef";
        const string spanId = "9123456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            null, null, now, now, now, SessionRawRetentionState.Expiring, now, now);
        var @event = new ObservedSessionEvent(eventId, sessionId, null, SessionSourceSurface.ClaudeCode, null, traceId, "ok",
            "claude-code-otel", $"{traceId}/{spanId}", "user.message", now, SessionContentState.Available);
        store.Write(new(new(session, [], [], [@event]),
            [new SessionEventContent(eventId, "application/json", "{\"text\":\"sensitive body\"}", now, now.AddDays(1))]));
        var reader = new RecordingContentReader(new((SessionContentReadDisposition)dispositionValue, null));
        var source = new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath, store, reader);

        var extraction = await HistoricalEvidenceExtractorV1.ExtractAsync(
            HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), source, CancellationToken.None);

        Assert.Equal(1, reader.ReadCount);
        Assert.Null(Assert.Single(extraction.RawLocal.Sessions).RawLocalDescriptor);
    }

    [Theory]
    [InlineData((int)SessionContentState.Redacted, (int)SessionRawRetentionState.Expiring)]
    [InlineData((int)SessionContentState.Available, (int)SessionRawRetentionState.NotCaptured)]
    public async Task SnapshotSource_DoesNotTouchContentReaderWithoutAvailableRetainedRawPosture(int contentValue, int retentionValue)
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        const string traceId = "2123456789abcdef0123456789abcdef";
        const string spanId = "2123456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            null, null, now, now, now, (SessionRawRetentionState)retentionValue, now, now);
        var @event = new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode,
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "user.message", now,
            (SessionContentState)contentValue);
        store.Write(new(new(session, [], [], [@event]), []));
        var reader = new RecordingContentReader(new(SessionContentReadDisposition.Denied, null));
        var source = new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath, store, reader);

        var extraction = await HistoricalEvidenceExtractorV1.ExtractAsync(
            HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), source, CancellationToken.None);

        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(HistoricalDescriptorStateV1.Unavailable, Assert.Single(extraction.RawLocal.Sessions).DescriptorState);
    }

    [Fact]
    public async Task SnapshotSource_UsesCurrentRetentionAuthorityInsteadOfCapturedSessionState()
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var realStore = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 13, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        const string traceId = "3123456789abcdef0123456789abcdef";
        const string spanId = "3123456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            null, null, now, now, now, SessionRawRetentionState.Expiring, now, now);
        var @event = new ObservedSessionEvent(eventId, sessionId, null, SessionSourceSurface.ClaudeCode,
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "user.message", now, SessionContentState.Available);
        realStore.Write(new(new(session, [], [], [@event]), []));
        var authority = DispatchProxy.Create<ISessionStore, NotCapturedRetentionProxy>();
        var reader = new RecordingContentReader(new(SessionContentReadDisposition.Granted,
            new SessionContentReadLease(new SessionEventContent(eventId, "application/json", "{\"text\":\"must not read\"}", now, now.AddDays(1)), () => ValueTask.CompletedTask)));
        var source = new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath, authority, reader);

        var extraction = await HistoricalEvidenceExtractorV1.ExtractAsync(
            HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), source, CancellationToken.None);

        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(HistoricalDescriptorStateV1.Unavailable, Assert.Single(extraction.RawLocal.Sessions).DescriptorState);
    }

    private sealed class RecordingContentReader(SessionContentReadResult result) : IHistoricalSessionContentReaderV1
    {
        internal int ReadCount { get; private set; }

        public ValueTask<SessionContentReadResult> ReadContentAsync(Guid sessionId, Guid eventId, CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(result);
        }
    }

    private static void WriteExactSession(ISessionStore store, string repository, DateTimeOffset at, string traceId, string spanId)
    {
        var sessionId = Guid.CreateVersion7();
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            repository, null, at, at, at, SessionRawRetentionState.NotCaptured, at, at);
        var @event = new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode,
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "otel.span", at, SessionContentState.NotCaptured);
        store.Write(new(new(session, [], [], [@event]), []));
    }

    private class GetDetailRejectingProxy : DispatchProxy
    {
        internal int GetDetailCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISessionStore.GetDetail))
            {
                GetDetailCount++;
                return null;
            }
            return targetMethod?.ReturnType == typeof(ValueTask<SessionContentReadResult>)
                ? ValueTask.FromResult(new SessionContentReadResult(SessionContentReadDisposition.Denied, null))
                : targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    private class NotCapturedRetentionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(ISessionStore.GetRawRetentionState)
                ? SessionRawRetentionState.NotCaptured
                : targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    }
}
