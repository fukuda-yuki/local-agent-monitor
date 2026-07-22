using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using System.Reflection;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalEvidenceProductionTests
{
    [Fact]
    public async Task SnapshotSource_DoesNotDoubleCountExplicitRowsInsideTheMatchingUnion()
    {
        using var temp = new MonitorTempDirectory();
        using var app = BuildHost(temp.DatabasePath);
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 6, 0, 0, TimeSpan.Zero);
        var sessions = Enumerable.Range(0, 10)
            .Select(index => WriteExactSession(store, "owner/union", now.AddMinutes(index), $"{index + 1:x32}", $"{index + 1:x16}"))
            .ToArray();
        var service = app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>();

        var result = await service.CreateAsync(HistoricalEvidenceSelectionV1.Create(
            repository: "owner/union", explicitSessionIds: [sessions[0]], maximumSessionCount: 2, sanitizedOnly: true), CancellationToken.None);

        Assert.Equal(8, result.RawLocal.TruncatedSessionCount);
        Assert.Equal(2, result.RawLocal.Sessions.Count);
    }

    [Fact]
    public async Task SnapshotSource_ExplicitOnlySelectionHasNoSourceOmission()
    {
        using var temp = new MonitorTempDirectory();
        using var app = BuildHost(temp.DatabasePath);
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 6, 30, 0, TimeSpan.Zero);
        var sessions = Enumerable.Range(0, 4)
            .Select(index => WriteExactSession(store, "owner/explicit", now.AddMinutes(index), $"{index + 20:x32}", $"{index + 20:x16}"))
            .ToArray();
        var service = app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>();

        var result = await service.CreateAsync(HistoricalEvidenceSelectionV1.Create(
            explicitSessionIds: sessions, maximumSessionCount: 2, sanitizedOnly: true), CancellationToken.None);

        Assert.Equal(2, result.RawLocal.TruncatedSessionCount);
        Assert.Equal(2, result.RawLocal.ExcludedSessions.Count(item => item.Reason == HistoricalSessionExclusionReasonV1.WindowTruncated));
    }

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
                "claude-code-otel", $"{traceId}/{firstSpan}", "otel.span", now, SessionContentState.NotCaptured, MatchKind: SessionMatchKind.ExactNative),
            new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null, traceId, "ok",
                "claude-code-otel", $"{traceId}/{secondSpan}", "otel.span", now.AddTicks(1), SessionContentState.NotCaptured, MatchKind: SessionMatchKind.ExactNative),
        };
        store.Write(new(new(session, [], [], events), []));
        InsertSpans(temp.DatabasePath, traceId,
        [
            Span(1, firstSpan, operation: "chat", totalTokens: 10),
            Span(2, secondSpan, operation: "chat", totalTokens: 10),
        ]);
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
    public async Task HostApplication_ConsumesRealIssue59NullSessionTurnOnlyAndMultiSourceEvidence()
    {
        using var temp = new MonitorTempDirectory();
        using var app = BuildHost(temp.DatabasePath);
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 8, 30, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        const string traceId = "a123456789abcdef0123456789abcdef";
        var spanIds = new[] { "a123456789abcdef", "b123456789abcdef", "c123456789abcdef" };
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            "owner/repository", null, now, now, now, SessionRawRetentionState.NotCaptured, now, now);
        var events = spanIds.Select((span, index) => new ObservedSessionEvent(
            Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null, traceId, "ok",
            "claude-code-otel", $"{traceId}/{span}", "otel.span", now.AddTicks(index), SessionContentState.NotCaptured, MatchKind: SessionMatchKind.ExactNative)).ToArray();
        store.Write(new(new(session, [], [], events), []));
        InsertSpans(temp.DatabasePath, traceId, spanIds.Select((span, index) => Span(index + 1, span, operation: "chat", totalTokens: 10)).ToArray());
        var evidence = InstructionEvidenceExtractor.Extract(traceId, ReadSpans(temp.DatabasePath, traceId), [], []);
        var index = InstructionFindingEvidenceIndexFactoryV1.FromInstructionEvidence(traceId, evidence);
        var analysisRunId = app.Services.GetRequiredService<IMonitorAnalysisStore>()
            .StartRun(traceId, null, spanIds[0], MonitorAnalysisFocus.InstructionDiagnosis, now).RunId;
        var handoff = InstructionFindingPipelineV1.Generate(analysisRunId, index,
        [
            new(InstructionFindingCategoryV1.AcceptanceCriteriaMissing, InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.DeterministicPrepass,
                [new(null, traceId, spanIds[0], 1, InstructionEvidenceRelativePositionV1.Anchor), new(null, traceId, spanIds[1], 2, InstructionEvidenceRelativePositionV1.Anchor)]),
            new(InstructionFindingCategoryV1.AcceptanceCriteriaMissing, InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.DeterministicPrepass,
                [new(null, traceId, null, 2, InstructionEvidenceRelativePositionV1.Anchor), new(null, traceId, null, 3, InstructionEvidenceRelativePositionV1.Anchor)]),
        ]);
        var candidate = Assert.Single(handoff.Candidates);
        Assert.Equal(2, candidate.SourceFindingIds.Count);
        new SqliteInstructionFindingHandoffStore(temp.DatabasePath).Save(handoff, now);

        var result = await app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>()
            .CreateAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), CancellationToken.None);

        var groups = result.RawLocal.EvidenceGroups.Where(group => group.Kind == HistoricalEvidenceGroupKindV1.InstructionFinding).ToArray();
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group => Assert.Equal(candidate.CandidateId, group.FindingCandidate?.CandidateId));
        Assert.Contains(groups, group => group.References.All(reference => reference.SpanId is null));
    }

    [Fact]
    public async Task HostApplication_ConsumesRealIssue59PreviousAndFollowingSiblingEvidence()
    {
        using var temp = new MonitorTempDirectory();
        using var app = BuildHost(temp.DatabasePath);
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 8, 40, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        const string previousTrace = "b123456789abcdef0123456789abcdef";
        const string anchorTrace = "c123456789abcdef0123456789abcdef";
        const string followingTrace = "d123456789abcdef0123456789abcdee";
        const string previousSpan = "b123456789abcdef";
        const string anchorSpan = "c123456789abcdef";
        const string followingSpan = "d123456789abcdee";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            "owner/repository", null, now, now, now, SessionRawRetentionState.NotCaptured, now, now);
        var events = new[]
        {
            new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null, previousTrace, "ok",
                "claude-code-otel", $"{previousTrace}/{previousSpan}", "otel.span", now, SessionContentState.NotCaptured, MatchKind: SessionMatchKind.ExactNative),
            new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null, anchorTrace, "ok",
                "claude-code-otel", $"{anchorTrace}/{anchorSpan}", "otel.span", now.AddTicks(1), SessionContentState.NotCaptured, MatchKind: SessionMatchKind.ExactNative),
            new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null, followingTrace, "ok",
                "claude-code-otel", $"{followingTrace}/{followingSpan}", "otel.span", now.AddTicks(2), SessionContentState.NotCaptured, MatchKind: SessionMatchKind.ExactNative),
        };
        store.Write(new(new(session, [], [], events), []));
        InsertSpans(temp.DatabasePath, previousTrace, [Span(1, previousSpan, operation: "chat", totalTokens: 10)]);
        InsertSpans(temp.DatabasePath, anchorTrace, [Span(2, anchorSpan, operation: "chat", totalTokens: 10)]);
        InsertSpans(temp.DatabasePath, followingTrace, [Span(3, followingSpan, operation: "chat", totalTokens: 10)]);
        var evidence = new InstructionEvidence([], [], [new(1, anchorSpan, 5, 5)], null, null,
            new("conversation", 3, 1, 0, 2, false, false,
            [
                new(previousTrace, -1, false, null, null, 1, 5, 5, 10, 0, 0, [], []),
                new(anchorTrace, 0, true, null, null, 1, 5, 5, 10, 0, 0, [], []),
                new(followingTrace, 1, false, null, null, 1, 5, 5, 10, 0, 0, [], []),
            ]));
        var index = InstructionFindingEvidenceIndexFactoryV1.FromInstructionEvidence(anchorTrace, evidence);
        var analysisRunId = app.Services.GetRequiredService<IMonitorAnalysisStore>()
            .StartRun(anchorTrace, null, anchorSpan, MonitorAnalysisFocus.InstructionDiagnosis, now).RunId;
        var handoff = InstructionFindingPipelineV1.Generate(analysisRunId, index,
        [
            new(InstructionFindingCategoryV1.Ambiguity, InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.PromptOnly,
                [new(null, anchorTrace, anchorSpan, 1, InstructionEvidenceRelativePositionV1.Anchor),
                 new(null, previousTrace, null, 1, InstructionEvidenceRelativePositionV1.Previous)]),
            new(InstructionFindingCategoryV1.Ambiguity, InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.PromptOnly,
                [new(null, anchorTrace, anchorSpan, 1, InstructionEvidenceRelativePositionV1.Anchor),
                 new(null, followingTrace, null, 1, InstructionEvidenceRelativePositionV1.Following)]),
        ]);
        new SqliteInstructionFindingHandoffStore(temp.DatabasePath).Save(handoff, now);

        var result = await app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>()
            .CreateAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), CancellationToken.None);

        var references = result.RawLocal.EvidenceGroups.Where(item => item.Kind == HistoricalEvidenceGroupKindV1.InstructionFinding)
            .SelectMany(item => item.References).ToArray();
        Assert.Contains(references, reference => reference.TraceId == previousTrace
            && reference.TurnIndex == 1 && reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Previous);
        Assert.Contains(references, reference => reference.TraceId == followingTrace
            && reference.TurnIndex == 1 && reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Following);
    }

    [Fact]
    public async Task SnapshotSource_DoesNotAttributeNullSurfaceEventProvenanceOrTruncateRunDuration()
    {
        using var temp = new MonitorTempDirectory();
        using var app = BuildHost(temp.DatabasePath);
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 8, 42, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        const string traceId = "c223456789abcdef0123456789abcdef";
        const string spanId = "c223456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            null, null, now, now, now, SessionRawRetentionState.NotCaptured, now, now);
        var native = new SessionNativeId(sessionId, SessionSourceSurface.ClaudeCode, "native", SessionBindingKind.Native, now);
        var run = new ObservedSessionRun(runId, sessionId, SessionSourceSurface.ClaudeCode, "run", traceId, null, null,
            ObservedSessionStatus.Completed, now, now.AddTicks(1), null, null, null);
        var @event = new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, runId, null, null, traceId, "ok",
            "claude-code-otel", $"{traceId}/{spanId}", "otel.span", now, SessionContentState.NotCaptured,
            "event-version", "adapter-version", MatchKind: SessionMatchKind.ExactNative);
        store.Write(new(new(session, [native], [run], [@event]), []));
        InsertSpans(temp.DatabasePath, traceId, [Span(1, spanId, operation: "chat")]);

        var result = await app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>()
            .CreateAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), CancellationToken.None);

        var metadata = Assert.Single(result.RawLocal.Sessions).Metadata;
        var provenance = Assert.Single(metadata.SourceProvenance);
        Assert.Equal(SessionSourceSurface.ClaudeCode, provenance.SourceSurface);
        Assert.Null(provenance.SourceApplicationVersion);
        Assert.Null(provenance.AdapterVersion);
        Assert.Empty(metadata.DurationObservations);
    }

    [Fact]
    public async Task SnapshotIdentityBindsConsumedSpanFactsWithoutSessionRevisionChange()
    {
        using var temp = new MonitorTempDirectory();
        using var app = BuildHost(temp.DatabasePath);
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 8, 44, 0, TimeSpan.Zero);
        const string traceId = "c323456789abcdef0123456789abcdef";
        const string spanId = "c323456789abcdef";
        var sessionId = WriteExactSession(store, "owner/repository", now, traceId, spanId);
        InsertSpans(temp.DatabasePath, traceId, [Span(1, spanId, operation: "chat", totalTokens: 10)]);
        var service = app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>();
        var selection = HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]);
        var first = await service.CreateAsync(selection, CancellationToken.None);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE monitor_spans SET total_tokens=11 WHERE trace_id=$trace;";
            update.Parameters.AddWithValue("$trace", traceId);
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        var second = await service.CreateAsync(selection, CancellationToken.None);

        Assert.NotEqual(first.RawLocal.SnapshotId, second.RawLocal.SnapshotId);
        Assert.NotEqual(first.RawLocal.ExtractionId, second.RawLocal.ExtractionId);
    }

    [Fact]
    public async Task SnapshotSource_ProjectsOnlyAuthoritativeExactSpanAndObjectiveFamilies()
    {
        using var temp = new MonitorTempDirectory();
        using var app = BuildHost(temp.DatabasePath);
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 8, 45, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        const string traceId = "d123456789abcdef0123456789abcdef";
        var spans = new[]
        {
            Span(1, "d123456789abcdef", operation: "chat", totalTokens: 30, cacheReadTokens: 5),
            Span(2, "e123456789abcdef", toolName: "shell", status: "error", errorType: "failed"),
            Span(3, "f123456789abcdef", toolName: "shell", status: "ok"),
        };
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            "owner/exact", null, now, now.AddSeconds(1), now.AddSeconds(1), SessionRawRetentionState.Expiring, now, now);
        var native = new SessionNativeId(sessionId, SessionSourceSurface.ClaudeCode, "native-exact", SessionBindingKind.Native, now);
        var run = new ObservedSessionRun(runId, sessionId, SessionSourceSurface.ClaudeCode, "run-exact", traceId, null, null,
            ObservedSessionStatus.Completed, now, now.AddSeconds(1), null, null, null);
        var events = spans.Select((span, index) => new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, runId,
            SessionSourceSurface.ClaudeCode, null, traceId, "ok", "claude-code-otel", $"{traceId}/{span.SpanId}",
            index == 0 ? "user.message" : "otel.span", now.AddTicks(index), SessionContentState.NotCaptured,
            MatchKind: SessionMatchKind.ExactNative)).ToArray();
        store.Write(new(new(session, [native], [run], events), []));
        InsertSpans(temp.DatabasePath, traceId, spans);
        store.CreateObjectiveEvaluation(new(Guid.CreateVersion7(), sessionId, runId, traceId, ObjectiveResult.Pass,
            ObjectiveSeverity.Normal, "eval", "v1", "criterion", "case", [new("trace", traceId)], now));

        var result = await app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>()
            .CreateAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), CancellationToken.None);

        var projected = Assert.Single(result.RawLocal.Sessions);
        Assert.True(projected.Capabilities.TurnRollup);
        Assert.True(projected.Capabilities.TokenRollup);
        Assert.True(projected.Capabilities.CacheRollup);
        Assert.True(projected.Capabilities.ErrorSpan);
        Assert.True(projected.Capabilities.RetryChain);
        Assert.True(projected.Capabilities.QualityReference);
        Assert.False(projected.Capabilities.RepeatedToolCall);
        Assert.False(projected.Capabilities.PermissionWait);
        Assert.False(projected.Capabilities.SubagentFanOut);
        Assert.False(projected.Capabilities.RawLocalDescriptor);
        Assert.False(projected.Capabilities.SourceComparison);
        Assert.Contains(result.RawLocal.EvidenceGroups, group => group.Kind == HistoricalEvidenceGroupKindV1.TurnRollup);
        Assert.Contains(result.RawLocal.EvidenceGroups, group => group.Kind == HistoricalEvidenceGroupKindV1.TokenRollup && group.NumericValue == 30);
        Assert.Contains(result.RawLocal.EvidenceGroups, group => group.Kind == HistoricalEvidenceGroupKindV1.CacheRollup && group.NumericValue == 5);
        Assert.Contains(result.RawLocal.EvidenceGroups, group => group.Kind == HistoricalEvidenceGroupKindV1.ErrorSpan);
        Assert.Contains(result.RawLocal.EvidenceGroups, group => group.Kind == HistoricalEvidenceGroupKindV1.RetryChain && group.Status == "recovered");
        Assert.Contains(result.RawLocal.EvidenceGroups, group => group.Kind == HistoricalEvidenceGroupKindV1.QualityReference && group.Status == "pass");
        Assert.DoesNotContain(result.RawLocal.EvidenceGroups, group => group.Kind is HistoricalEvidenceGroupKindV1.UserCorrection or HistoricalEvidenceGroupKindV1.SubagentFanOut);
    }

    [Fact]
    public async Task SnapshotSource_DoesNotTreatUnmatchedAdapterNamedEventAsExactOtel()
    {
        using var temp = new MonitorTempDirectory();
        using var app = BuildHost(temp.DatabasePath);
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 8, 50, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        const string traceId = "1123456789abcdef0123456789abcdee";
        const string spanId = "1123456789abcdee";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            null, null, now, now, now, SessionRawRetentionState.NotCaptured, now, now);
        store.Write(new(new(session, [], [], [new(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode,
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "otel.span", now, SessionContentState.NotCaptured)]), []));
        InsertSpans(temp.DatabasePath, traceId, [Span(1, spanId, operation: "chat", totalTokens: 10)]);

        var result = await app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>()
            .CreateAsync(HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), CancellationToken.None);

        Assert.Empty(result.RawLocal.Sessions);
        Assert.Contains(result.RawLocal.ExcludedSessions, item => item.Reason == HistoricalSessionExclusionReasonV1.MissingEvidenceReference);
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
            SessionContentState.NotCaptured, "2.1.215", "adapter.v1", MatchKind: SessionMatchKind.ExactNative);
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
        var analysisStore = app.Services.GetRequiredService<IMonitorAnalysisStore>();
        var handoffStore = new SqliteInstructionFindingHandoffStore(temp.DatabasePath);
        foreach (var index in Enumerable.Range(1, HistoricalEvidenceContractsV1.MaximumSessions + 1))
        {
            var traceId = index.ToString("x32");
            var spanId = index.ToString("x16");
            WriteExactSession(store, "owner/max-window", now.AddSeconds(index), traceId, spanId);
            var runId = analysisStore.StartRun(traceId, null, spanId, MonitorAnalysisFocus.InstructionDiagnosis, now.AddTicks(index)).RunId;
            var evidence = new InstructionEvidence([new(spanId, "tool", "error", "tool failed")], [], [], null, null, null);
            var handoff = InstructionFindingPipelineV1.Generate(runId,
                InstructionFindingEvidenceIndexFactoryV1.FromInstructionEvidence(traceId, evidence),
                [new(InstructionFindingCategoryV1.EnvironmentAssumptionMissing, InstructionFindingVerdictV1.Weak,
                    InstructionFindingExtractorSourceV1.DeterministicPrepass,
                    [new(null, traceId, spanId, null, InstructionEvidenceRelativePositionV1.Anchor)])]);
            handoffStore.Save(handoff, now.AddTicks(index));
        }
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
            SessionContentState.Available, "2.1.215", "adapter.v1", MatchKind: SessionMatchKind.ExactNative);
        var initial = @event with { EventId = Guid.CreateVersion7(), SourceEventId = $"{traceId}/0123456789abcdef", OccurredAt = now.AddTicks(-1) };
        store.Write(new(new(session, [], [], [initial, @event]),
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
            "claude-code-otel", $"{traceId}/{spanId}", "user.message", now, SessionContentState.Available, MatchKind: SessionMatchKind.ExactNative);
        var initial = @event with { EventId = Guid.CreateVersion7(), SourceEventId = $"{traceId}/8123456789abcdef", OccurredAt = now.AddTicks(-1) };
        store.Write(new(new(session, [], [], [initial, @event]),
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
            (SessionContentState)contentValue, MatchKind: SessionMatchKind.ExactNative);
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
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "user.message", now, SessionContentState.Available, MatchKind: SessionMatchKind.ExactNative);
        var initial = @event with { EventId = Guid.CreateVersion7(), SourceEventId = $"{traceId}/2123456789abcdef", OccurredAt = now.AddTicks(-1) };
        realStore.Write(new(new(session, [], [], [initial, @event]), []));
        var authority = DispatchProxy.Create<ISessionStore, NotCapturedRetentionProxy>();
        var reader = new RecordingContentReader(new(SessionContentReadDisposition.Granted,
            new SessionContentReadLease(new SessionEventContent(eventId, "application/json", "{\"text\":\"must not read\"}", now, now.AddDays(1)), () => ValueTask.CompletedTask)));
        var source = new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath, authority, reader);

        var extraction = await HistoricalEvidenceExtractorV1.ExtractAsync(
            HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), source, CancellationToken.None);

        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(HistoricalDescriptorStateV1.Unavailable, Assert.Single(extraction.RawLocal.Sessions).DescriptorState);
    }

    [Fact]
    public async Task SnapshotSource_BindsGrantedDescriptorOutcomeIntoSnapshotIdentity()
    {
        using var temp = new MonitorTempDirectory();
        using var app = BuildHost(temp.DatabasePath);
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 13, 30, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var correctionId = Guid.CreateVersion7();
        const string traceId = "4123456789abcdef0123456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            null, null, now, now, now, SessionRawRetentionState.Expiring, now, now);
        var initial = new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null,
            traceId, "ok", "claude-code-otel", $"{traceId}/4123456789abcdef", "user.message", now,
            SessionContentState.Available, MatchKind: SessionMatchKind.ExactNative);
        var correction = initial with
        {
            EventId = correctionId,
            SourceEventId = $"{traceId}/5123456789abcdef",
            OccurredAt = now.AddTicks(1),
        };
        store.Write(new(new(session, [], [], [initial, correction]),
            [new SessionEventContent(correctionId, "application/json", "{\"text\":\"Use focused tests\"}", now, now.AddDays(1))]));
        var reader = new MutableContentReader(correctionId, now, "Use focused tests") { Disposition = SessionContentReadDisposition.Granted };
        var source = new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath, store, reader);
        var selection = HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]);

        var first = await HistoricalEvidenceExtractorV1.ExtractAsync(selection, source, CancellationToken.None);
        reader.Disposition = SessionContentReadDisposition.Denied;
        var denied = await HistoricalEvidenceExtractorV1.ExtractAsync(selection, source, CancellationToken.None);
        reader.Disposition = SessionContentReadDisposition.Granted;
        var repeated = await HistoricalEvidenceExtractorV1.ExtractAsync(selection, source, CancellationToken.None);

        Assert.NotEqual(first.RawLocal.SnapshotId, denied.RawLocal.SnapshotId);
        Assert.NotEqual(first.RawLocal.ExtractionId, denied.RawLocal.ExtractionId);
        Assert.NotEqual(first.RawLocalBytes, denied.RawLocalBytes);
        Assert.Equal(first.RawLocal.SnapshotId, repeated.RawLocal.SnapshotId);
        Assert.Equal(first.RawLocal.ExtractionId, repeated.RawLocal.ExtractionId);
        Assert.Equal(first.RawLocalBytes, repeated.RawLocalBytes);
        Assert.Equal("Use focused tests", Assert.Single(first.RawLocal.Sessions).RawLocalDescriptor);
        Assert.Equal(HistoricalDescriptorStateV1.Unavailable, Assert.Single(denied.RawLocal.Sessions).DescriptorState);
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

    private sealed class MutableContentReader(Guid eventId, DateTimeOffset now, string descriptor) : IHistoricalSessionContentReaderV1
    {
        internal SessionContentReadDisposition Disposition { get; set; }

        public ValueTask<SessionContentReadResult> ReadContentAsync(Guid sessionId, Guid requestedEventId, CancellationToken cancellationToken)
        {
            Assert.Equal(eventId, requestedEventId);
            if (Disposition != SessionContentReadDisposition.Granted)
                return ValueTask.FromResult(new SessionContentReadResult(Disposition, null));
            return ValueTask.FromResult(new SessionContentReadResult(Disposition,
                new SessionContentReadLease(new SessionEventContent(eventId, "application/json",
                    JsonSerializer.Serialize(new { text = descriptor }), now, now.AddDays(1)), () => ValueTask.CompletedTask)));
        }
    }

    private static Guid WriteExactSession(ISessionStore store, string repository, DateTimeOffset at, string traceId, string spanId)
    {
        var sessionId = Guid.CreateVersion7();
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            repository, null, at, at, at, SessionRawRetentionState.NotCaptured, at, at);
        var @event = new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode,
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "otel.span", at, SessionContentState.NotCaptured, MatchKind: SessionMatchKind.ExactNative);
        store.Write(new(new(session, [], [], [@event]), []));
        return sessionId;
    }

    private static WebApplication BuildHost(string databasePath) => MonitorHost.Build(
        new MonitorOptions(databasePath, "http://127.0.0.1:0", true, 31_457_280),
        new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });

    private static MonitorSpanRow Span(int ordinal, string spanId, string? operation = null, string? category = null,
        string? toolName = null, string? toolType = null, string? parentSpanId = null, string? agentName = null,
        int? totalTokens = null, int? cacheReadTokens = null, int? cacheCreationTokens = null,
        string? status = null, string? errorType = null, double? durationMs = null) =>
        new(ordinal, ordinal, "unused", spanId, parentSpanId, ordinal, operation, category, toolName, toolType,
            null, null, agentName, null, null, null, null, totalTokens, null, cacheReadTokens, cacheCreationTokens,
            status, errorType, null, null, durationMs, null, null, "2026-07-22T00:00:00.0000000+00:00");

    private static void InsertSpans(string databasePath, string traceId, IReadOnlyList<MonitorSpanRow> spans)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        foreach (var span in spans)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,tool_name,tool_type,agent_name,total_tokens,cache_read_tokens,cache_creation_tokens,status,error_type,duration_ms,projected_at)
                VALUES($raw,$trace,$span,$parent,$ordinal,$operation,$category,$tool,$type,$agent,$tokens,$cacheRead,$cacheCreate,$status,$error,$duration,$projected);
                """;
            command.Parameters.AddWithValue("$raw", span.RawRecordId);
            command.Parameters.AddWithValue("$trace", traceId);
            command.Parameters.AddWithValue("$span", (object?)span.SpanId ?? DBNull.Value);
            command.Parameters.AddWithValue("$parent", (object?)span.ParentSpanId ?? DBNull.Value);
            command.Parameters.AddWithValue("$ordinal", span.SpanOrdinal);
            command.Parameters.AddWithValue("$operation", (object?)span.Operation ?? DBNull.Value);
            command.Parameters.AddWithValue("$category", (object?)span.Category ?? DBNull.Value);
            command.Parameters.AddWithValue("$tool", (object?)span.ToolName ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", (object?)span.ToolType ?? DBNull.Value);
            command.Parameters.AddWithValue("$agent", (object?)span.AgentName ?? DBNull.Value);
            command.Parameters.AddWithValue("$tokens", (object?)span.TotalTokens ?? DBNull.Value);
            command.Parameters.AddWithValue("$cacheRead", (object?)span.CacheReadTokens ?? DBNull.Value);
            command.Parameters.AddWithValue("$cacheCreate", (object?)span.CacheCreationTokens ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", (object?)span.Status ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)span.ErrorType ?? DBNull.Value);
            command.Parameters.AddWithValue("$duration", (object?)span.DurationMs ?? DBNull.Value);
            command.Parameters.AddWithValue("$projected", span.ProjectedAt);
            command.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<MonitorSpanRow> ReadSpans(string databasePath, string traceId)
    {
        var store = new CopilotAgentObservability.Persistence.Sqlite.RawTelemetryStore(databasePath);
        return store.GetSpansForTrace(traceId);
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
