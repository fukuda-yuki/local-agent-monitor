using System.Diagnostics;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalAnalysisCoordinatorTests
{
    [Fact]
    public async Task PreviewAsync_ValidSelection_ReturnsExactOwnerProjectionAndPersistsBinding()
    {
        using var temp = new MonitorTempDirectory();
        var metadata = Metadata(1);
        var source = new PreviewSnapshotSource([metadata]);
        var store = new SqliteHistoricalEvidenceDatasetStoreV1(temp.DatabasePath);
        store.CreateSchema();
        var owner = new HistoricalEvidenceApplicationServiceV1(source, store, temp.TimeProvider);
        var coordinator = new HistoricalAnalysisCoordinatorV1(owner);
        var selection = new HistoricalEvidenceSelectionV1(
            "repo-a", null, null, null, [], [], null, null, 50, false);

        var response = await coordinator.PreviewAsync(
            new HistoricalAnalysisPreviewRequestV1(
                HistoricalAnalysisContractsV1.PreviewRequestSchemaVersion,
                selection),
            CancellationToken.None);

        Assert.Equal(HistoricalAnalysisContractsV1.PreviewResponseSchemaVersion, response.SchemaVersion);
        Assert.StartsWith("repository-ref-", response.Selection.Repository, StringComparison.Ordinal);
        Assert.Single(response.Included);
        Assert.Empty(response.Excluded);
        Assert.False(response.TruncatedBefore);
        Assert.Equal(0, response.TruncatedSessionCount);
        Assert.Matches("^historical-extraction-[a-z0-9]{32}$", response.ExtractionId);
        Assert.Matches("^[a-f0-9]{64}$", response.RawLocalSha256);
        Assert.Matches("^[a-f0-9]{64}$", response.RepositorySafeSha256);

        var persisted = Assert.IsType<HistoricalEvidenceExtractionV1>(owner.Get(response.ExtractionId));
        Assert.Equal(response.RawLocalSha256, persisted.RawLocalSha256);
        Assert.Equal(response.RepositorySafeSha256, persisted.RepositorySafeSha256);
        Assert.Equal([metadata.SessionId], source.ReadSessionIds);
    }

    [Fact]
    public async Task PreviewAsync_WindowAndMissingExplicitSession_PreservesOwnerOrderAndExactReasons()
    {
        using var temp = new MonitorTempDirectory();
        var sessions = new[] { Metadata(1), Metadata(2), Metadata(3) };
        var missing = Guid.Parse("018f0000-0000-7000-8000-000000000099");
        var source = new PreviewSnapshotSource(sessions);
        var store = new SqliteHistoricalEvidenceDatasetStoreV1(temp.DatabasePath);
        store.CreateSchema();
        var owner = new HistoricalEvidenceApplicationServiceV1(source, store, temp.TimeProvider);
        var coordinator = new HistoricalAnalysisCoordinatorV1(owner);
        var selection = new HistoricalEvidenceSelectionV1(
            "repo-a", null, null, null, [missing], [], null, null, 2, false);

        var response = await coordinator.PreviewAsync(
            new HistoricalAnalysisPreviewRequestV1(
                HistoricalAnalysisContractsV1.PreviewRequestSchemaVersion,
                selection),
            CancellationToken.None);

        Assert.Equal([2, 3], response.Included.Select(item => item.Metadata.StartedAt!.Value.Minute));
        Assert.Equal(
            [
                HistoricalSessionExclusionReasonV1.WindowTruncated,
                HistoricalSessionExclusionReasonV1.MissingSessionReference,
            ],
            response.Excluded.Select(item => item.Reason));
        Assert.NotNull(response.Excluded[0].Metadata);
        Assert.Null(response.Excluded[1].Metadata);
        Assert.True(response.TruncatedBefore);
        Assert.Equal(1, response.TruncatedSessionCount);
        Assert.Equal([sessions[1].SessionId, sessions[2].SessionId], source.ReadSessionIds);
    }

    [Fact]
    public async Task ResolveEvidence_UsesPersistedOwnerPairAndPreservesCallerOrder()
    {
        using var temp = new MonitorTempDirectory();
        var source = new PreviewSnapshotSource([Metadata(1)]);
        var owner = Owner(temp, source);
        var coordinator = new HistoricalAnalysisCoordinatorV1(owner);
        var preview = await CreatePreview(coordinator);
        var extraction = Assert.IsType<HistoricalEvidenceExtractionV1>(owner.Get(preview.ExtractionId));
        var safeReference = Assert.Single(Assert.Single(extraction.RepositorySafe.EvidenceGroups).References);

        var response = coordinator.ResolveEvidence(new(
            HistoricalAnalysisContractsV1.EvidenceResolveRequestSchemaVersion,
            preview.ExtractionId,
            preview.RepositorySafeSha256,
            [safeReference.SpanId!, safeReference.SessionId, safeReference.TraceId]));

        Assert.Equal(HistoricalAnalysisContractsV1.EvidenceResolveResponseSchemaVersion, response.SchemaVersion);
        Assert.Equal(
            [safeReference.SpanId, safeReference.SessionId, safeReference.TraceId],
            response.Resolutions.Select(resolution => resolution.Reference));
        Assert.Equal("/traces/trace-1?span=span-1", response.Resolutions[0].Target);
        Assert.Equal(
            "/diagnostics?session_id=018f0000-0000-7000-8000-000000000001",
            response.Resolutions[1].Target);
        Assert.Equal("/traces/trace-1", response.Resolutions[2].Target);
    }

    [Fact]
    public async Task ResolveEvidence_PairsReferencesByOwnerTokenInsteadOfIndependentArrayPosition()
    {
        using var temp = new MonitorTempDirectory();
        var owner = Owner(temp, new MultiReferenceSnapshotSource());
        var coordinator = new HistoricalAnalysisCoordinatorV1(owner);
        var preview = await CreatePreview(coordinator);

        var response = coordinator.ResolveEvidence(new(
            HistoricalAnalysisContractsV1.EvidenceResolveRequestSchemaVersion,
            preview.ExtractionId,
            preview.RepositorySafeSha256,
            [
                InstructionFindingReferenceTokenizationV1.TokenizeTrace("trace-a"),
                InstructionFindingReferenceTokenizationV1.TokenizeTrace("trace-b"),
            ]));

        Assert.Equal("/traces/trace-a", response.Resolutions[0].Target);
        Assert.Equal("/traces/trace-b", response.Resolutions[1].Target);
    }

    [Fact]
    public async Task ResolveEvidence_IndexesMetadataOnlyDurationAndModelReferencesByTokenMembership()
    {
        using var temp = new MonitorTempDirectory();
        var baseline = Metadata(1);
        var durationA = new HistoricalRawEvidenceReferenceV1(
            baseline.SessionId, "trace-a", "duration-span-a", 1, HistoricalEvidenceRelativePositionV1.Anchor);
        var durationB = new HistoricalRawEvidenceReferenceV1(
            baseline.SessionId, "trace-b", "duration-span-b", 2, HistoricalEvidenceRelativePositionV1.Anchor);
        var model = new HistoricalRawEvidenceReferenceV1(
            baseline.SessionId, "model-trace", "model-span", 3, HistoricalEvidenceRelativePositionV1.Anchor);
        var metadata = baseline with
        {
            EvidenceLocations =
            [
                .. baseline.EvidenceLocations,
                Location(durationA),
                Location(durationB),
                Location(model),
            ],
            DurationObservations =
            [
                new(100, durationA),
                new(100, durationB),
            ],
            ModelObservations = [new("model-a", model)],
        };
        var owner = Owner(temp, new PreviewSnapshotSource([metadata]));
        var coordinator = new HistoricalAnalysisCoordinatorV1(owner);
        var preview = await CreatePreview(coordinator);
        var extraction = Assert.IsType<HistoricalEvidenceExtractionV1>(owner.Get(preview.ExtractionId));
        var raw = Assert.Single(extraction.RawLocal.Sessions);
        var safe = Assert.Single(extraction.RepositorySafe.Sessions);
        var tokenizedRawDurationOrder = raw.Metadata.DurationObservations
            .Select(value => InstructionFindingReferenceTokenizationV1.TokenizeTrace(value.EvidenceRef.TraceId));
        Assert.False(tokenizedRawDurationOrder.SequenceEqual(
            safe.Metadata.DurationObservations.Select(value => value.EvidenceRef.TraceId)));

        var response = coordinator.ResolveEvidence(new(
            HistoricalAnalysisContractsV1.EvidenceResolveRequestSchemaVersion,
            preview.ExtractionId,
            preview.RepositorySafeSha256,
            [
                InstructionFindingReferenceTokenizationV1.TokenizeTrace("trace-a"),
                Tokenized(durationB).SpanId!,
                InstructionFindingReferenceTokenizationV1.TokenizeTrace("model-trace"),
                Tokenized(model).SpanId!,
            ]));

        Assert.Equal(
            [
                "/traces/trace-a",
                "/traces/trace-b?span=duration-span-b",
                "/traces/model-trace",
                "/traces/model-trace?span=model-span",
            ],
            response.Resolutions.Select(value => value.Target));
        Assert.All(response.Resolutions, value => Assert.Equal("resolved", value.ResolutionState));
    }

    [Fact]
    public async Task ResolveEvidence_IndexesExistingExcludedSessionsButKeepsMissingSessionNonNavigable()
    {
        using var temp = new MonitorTempDirectory();
        var sessions = new[]
        {
            Metadata(1),
            Metadata(2),
            Metadata(3),
            Metadata(4) with { Repository = "repo-b" },
        };
        var missing = Guid.Parse("018f0000-0000-7000-8000-000000000099");
        var source = new PreviewSnapshotSource(sessions);
        var owner = Owner(temp, source);
        var coordinator = new HistoricalAnalysisCoordinatorV1(owner);
        var preview = await coordinator.PreviewAsync(
            new(
                HistoricalAnalysisContractsV1.PreviewRequestSchemaVersion,
                new("repo-a", null, null, null, [sessions[3].SessionId, missing], [], null, null, 2, false)),
            CancellationToken.None);
        var window = Assert.Single(
            preview.Excluded,
            value => value.Reason == HistoricalSessionExclusionReasonV1.WindowTruncated);
        var filter = Assert.Single(
            preview.Excluded,
            value => value.Reason == HistoricalSessionExclusionReasonV1.FilterMismatch);
        var missingReference = Assert.Single(
            preview.Excluded,
            value => value.Reason == HistoricalSessionExclusionReasonV1.MissingSessionReference);

        var response = coordinator.ResolveEvidence(new(
            HistoricalAnalysisContractsV1.EvidenceResolveRequestSchemaVersion,
            preview.ExtractionId,
            preview.RepositorySafeSha256,
            [filter.SessionId, missingReference.SessionId, window.SessionId]));

        Assert.Equal(
            [
                "/diagnostics?session_id=018f0000-0000-7000-8000-000000000004",
                null,
                "/diagnostics?session_id=018f0000-0000-7000-8000-000000000001",
            ],
            response.Resolutions.Select(value => value.Target));
        Assert.Equal(["resolved", "missing", "resolved"], response.Resolutions.Select(value => value.ResolutionState));
        Assert.Equal([sessions[1].SessionId, sessions[2].SessionId], source.ReadSessionIds);
    }

    [Fact]
    public async Task ResolveEvidence_IndexesEveryExistingExcludedSessionWithoutReadingEvidence()
    {
        using var temp = new MonitorTempDirectory();
        var sessions = new[]
        {
            Metadata(5) with { Completeness = SessionCompleteness.Unbound },
            Metadata(6) with
            {
                ContentState = SessionContentState.NotCaptured,
                EvidenceLocations = [],
            },
            Metadata(7) with
            {
                SourceKind = HistoricalEvidenceSourceKindV1.HistoricalSummary,
                ContentState = SessionContentState.ExpiredPendingDeletion,
            },
        };
        var missing = Guid.Parse("018f0000-0000-7000-8000-000000000199");
        var source = new PreviewSnapshotSource(sessions);
        var owner = Owner(temp, source);
        var coordinator = new HistoricalAnalysisCoordinatorV1(owner);
        var preview = await coordinator.PreviewAsync(
            new(
                HistoricalAnalysisContractsV1.PreviewRequestSchemaVersion,
                new("repo-a", null, null, null, [missing], [], null, null, 50, false)),
            CancellationToken.None);
        var unbound = Assert.Single(
            preview.Excluded,
            value => value.Reason == HistoricalSessionExclusionReasonV1.Unbound);
        var missingEvidence = Assert.Single(
            preview.Excluded,
            value => value.Reason == HistoricalSessionExclusionReasonV1.MissingEvidenceReference);
        var invalidHistorical = Assert.Single(
            preview.Excluded,
            value => value.Reason == HistoricalSessionExclusionReasonV1.InvalidHistoricalCompleteness);
        var missingSession = Assert.Single(
            preview.Excluded,
            value => value.Reason == HistoricalSessionExclusionReasonV1.MissingSessionReference);

        var response = coordinator.ResolveEvidence(new(
            HistoricalAnalysisContractsV1.EvidenceResolveRequestSchemaVersion,
            preview.ExtractionId,
            preview.RepositorySafeSha256,
            [unbound.SessionId, missingEvidence.SessionId, invalidHistorical.SessionId, missingSession.SessionId]));

        Assert.Equal(
            [
                "/diagnostics?session_id=018f0000-0000-7000-8000-000000000005",
                "/diagnostics?session_id=018f0000-0000-7000-8000-000000000006",
                "/diagnostics?session_id=018f0000-0000-7000-8000-000000000007",
                null,
            ],
            response.Resolutions.Select(value => value.Target));
        Assert.Equal(
            ["available", "not_captured", "expired_pending_deletion", "not_applicable"],
            response.Resolutions.Select(value => value.ContentState));
        Assert.Equal(
            ["resolved", "resolved", "expired", "missing"],
            response.Resolutions.Select(value => value.ResolutionState));
        Assert.Empty(source.ReadSessionIds);
    }

    [Fact]
    public async Task EfficiencyRun_ApplicationCancellationCompletesCanceledAndDoesNotLeakTask()
    {
        using var temp = new MonitorTempDirectory();
        var source = new PreviewSnapshotSource([Metadata(1)]);
        var owner = Owner(temp, source);
        using var stopping = new CancellationTokenSource();
        var executor = new CancellationBlockingEfficiencyExecutor();
        var coordinator = new HistoricalAnalysisCoordinatorV1(
            owner,
            null,
            null,
            stopping.Token,
            executor,
            temp.TimeProvider,
            TimeSpan.FromSeconds(5));
        var preview = await CreatePreview(coordinator);
        var start = coordinator.StartEfficiency(new(
            HistoricalAnalysisContractsV1.EfficiencyStartRequestSchemaVersion,
            preview.ExtractionId,
            preview.RepositorySafeSha256));
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stopping.Cancel();
        await coordinator.DisposeAsync();

        var status = coordinator.GetEfficiency(start.AnalysisRunId);
        Assert.Equal("canceled", status.State);
        Assert.Null(status.Receipt);
        Assert.Null(status.ReceiptPayloadSha256);
        Assert.NotNull(status.CompletedAt);
    }

    [Fact]
    public async Task EfficiencyRun_TerminalSuccessIsNotOverwrittenByLaterShutdown()
    {
        using var temp = new MonitorTempDirectory();
        var source = new PreviewSnapshotSource([Metadata(1)]);
        var owner = Owner(temp, source);
        using var stopping = new CancellationTokenSource();
        var coordinator = new HistoricalAnalysisCoordinatorV1(
            owner,
            null,
            null,
            stopping.Token,
            new ImmediateEfficiencyExecutor(),
            temp.TimeProvider,
            TimeSpan.FromSeconds(5));
        var preview = await CreatePreview(coordinator);
        var start = coordinator.StartEfficiency(new(
            HistoricalAnalysisContractsV1.EfficiencyStartRequestSchemaVersion,
            preview.ExtractionId,
            preview.RepositorySafeSha256));
        var completed = await WaitForEfficiency(coordinator, start.AnalysisRunId, "zero_drivers");

        stopping.Cancel();
        await coordinator.DisposeAsync();

        var afterShutdown = coordinator.GetEfficiency(start.AnalysisRunId);
        Assert.Equal("zero_drivers", afterShutdown.State);
        Assert.Equal(completed.CompletedAt, afterShutdown.CompletedAt);
        Assert.NotNull(afterShutdown.Receipt);
        Assert.Equal(completed.ReceiptPayloadSha256, afterShutdown.ReceiptPayloadSha256);
    }

    [Fact]
    public async Task EfficiencyRuns_ProcessLocalCapacityRejectsAdditionalActiveRun()
    {
        using var temp = new MonitorTempDirectory();
        var source = new PreviewSnapshotSource([Metadata(1)]);
        var owner = Owner(temp, source);
        using var stopping = new CancellationTokenSource();
        var executor = new CancellationBlockingEfficiencyExecutor();
        var coordinator = new HistoricalAnalysisCoordinatorV1(
            owner,
            null,
            null,
            stopping.Token,
            executor,
            temp.TimeProvider,
            TimeSpan.FromSeconds(5));
        var preview = await CreatePreview(coordinator);
        var request = new HistoricalAnalysisEfficiencyStartRequestV1(
            HistoricalAnalysisContractsV1.EfficiencyStartRequestSchemaVersion,
            preview.ExtractionId,
            preview.RepositorySafeSha256);
        for (var index = 0; index < 32; index++)
        {
            Assert.Equal("queued", coordinator.StartEfficiency(request).State);
        }

        var exception = Assert.Throws<HistoricalAnalysisException>(
            () => coordinator.StartEfficiency(request));

        Assert.Equal(HistoricalAnalysisErrorCodesV1.PreconditionFailed, exception.Code);
        stopping.Cancel();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task EfficiencyRuns_TimedOutInvocationsThatIgnoreCancellationStillConsumeCapacity()
    {
        using var temp = new MonitorTempDirectory();
        var source = new PreviewSnapshotSource([Metadata(1)]);
        var owner = Owner(temp, source);
        using var stopping = new CancellationTokenSource();
        var executor = new IncompleteEfficiencyExecutor();
        var coordinator = new HistoricalAnalysisCoordinatorV1(
            owner,
            null,
            null,
            stopping.Token,
            executor,
            temp.TimeProvider,
            TimeSpan.FromMilliseconds(100));
        var preview = await CreatePreview(coordinator);
        var request = new HistoricalAnalysisEfficiencyStartRequestV1(
            HistoricalAnalysisContractsV1.EfficiencyStartRequestSchemaVersion,
            preview.ExtractionId,
            preview.RepositorySafeSha256);
        var starts = Enumerable.Range(0, 32)
            .Select(_ => coordinator.StartEfficiency(request))
            .ToArray();

        try
        {
            foreach (var start in starts)
            {
                await WaitForEfficiency(coordinator, start.AnalysisRunId, "timed_out");
            }

            var exception = Assert.Throws<HistoricalAnalysisException>(
                () => coordinator.StartEfficiency(request));

            Assert.Equal(HistoricalAnalysisErrorCodesV1.PreconditionFailed, exception.Code);
        }
        finally
        {
            executor.Complete(HistoricalEfficiencyAnalyzerV1.Analyze(owner.Get(preview.ExtractionId)!));
            stopping.Cancel();
            await coordinator.DisposeAsync();
        }
        Assert.All(
            starts,
            start => Assert.Equal("timed_out", coordinator.GetEfficiency(start.AnalysisRunId).State));
    }

    [Fact]
    public async Task EfficiencyRun_ShutdownCancelsOuterStateButTracksSynchronousInvocationUntilCompletion()
    {
        using var temp = new MonitorTempDirectory();
        var source = new PreviewSnapshotSource([Metadata(1)]);
        var owner = Owner(temp, source);
        using var stopping = new CancellationTokenSource();
        var executor = new SynchronouslyBlockingEfficiencyExecutor();
        var coordinator = new HistoricalAnalysisCoordinatorV1(
            owner,
            null,
            null,
            stopping.Token,
            executor,
            temp.TimeProvider,
            TimeSpan.FromSeconds(5));
        var preview = await CreatePreview(coordinator);
        var start = coordinator.StartEfficiency(new(
            HistoricalAnalysisContractsV1.EfficiencyStartRequestSchemaVersion,
            preview.ExtractionId,
            preview.RepositorySafeSha256));
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stopping.Cancel();
        var disposal = coordinator.DisposeAsync().AsTask();
        try
        {
            var canceled = await WaitForEfficiency(coordinator, start.AnalysisRunId, "canceled");

            Assert.Equal("canceled", canceled.State);
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            executor.Release.Set();
        }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("canceled", coordinator.GetEfficiency(start.AnalysisRunId).State);
    }

    [Theory]
    [InlineData(0, "queued")]
    [InlineData(1, "running")]
    [InlineData(2, "succeeded")]
    [InlineData(3, "zero_drivers")]
    [InlineData(4, "stale_extraction")]
    [InlineData(5, "analysis_failed")]
    [InlineData(6, "timed_out")]
    [InlineData(7, "canceled")]
    public void EfficiencyStateWire_PreservesEveryLifecycleState(int stateValue, string expected)
    {
        Assert.Equal(
            expected,
            HistoricalAnalysisEfficiencyStateWireV1.ToWireValue(
                (HistoricalAnalysisEfficiencyStateV1)stateValue));
    }

    private static HistoricalEvidenceApplicationServiceV1 Owner(
        MonitorTempDirectory temp,
        IHistoricalEvidenceSnapshotSourceV1 source)
    {
        var store = new SqliteHistoricalEvidenceDatasetStoreV1(temp.DatabasePath);
        store.CreateSchema();
        return new(source, store, temp.TimeProvider);
    }

    private static async Task<HistoricalAnalysisPreviewResponseV1> CreatePreview(
        HistoricalAnalysisCoordinatorV1 coordinator) =>
        await coordinator.PreviewAsync(
            new(
                HistoricalAnalysisContractsV1.PreviewRequestSchemaVersion,
                new("repo-a", null, null, null, [], [], null, null, 50, false)),
            CancellationToken.None);

    private static async Task<HistoricalAnalysisEfficiencyStatusResponseV1> WaitForEfficiency(
        HistoricalAnalysisCoordinatorV1 coordinator,
        string runId,
        string expectedState)
    {
        var deadline = Stopwatch.StartNew();
        string? lastState = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            var status = coordinator.GetEfficiency(runId);
            lastState = status.State;
            if (lastState == expectedState) return status;
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
        throw new Xunit.Sdk.XunitException(
            $"Efficiency run {runId} did not reach {expectedState}; last state was {lastState ?? "<none>"}.");
    }

    private static HistoricalSessionMetadataV1 Metadata(int number)
    {
        var id = Guid.Parse($"018f0000-0000-7000-8000-{number:D12}");
        return new(
            id,
            SessionSourceSurface.CopilotSdk,
            "1.0.0",
            "adapter.v1",
            SessionCompleteness.Full,
            [],
            HistoricalEvidenceSourceKindV1.LiveOtel,
            SessionContentState.Available,
            "repo-a",
            "workspace-a",
            null,
            null,
            new DateTimeOffset(2026, 7, 1, 0, number, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 1, 0, number, 30, TimeSpan.Zero),
            new HistoricalSessionCapabilitiesV1(
                true, false, false, false, false, false, false, false, false, false, false, false),
            [new(id, $"trace-{number}", $"span-{number}", 1, HistoricalEvidenceRelativePositionV1.Anchor)],
            []);
    }

    private static HistoricalEvidenceLocationV1 Location(HistoricalRawEvidenceReferenceV1 reference) =>
        new(
            reference.SessionId,
            reference.TraceId,
            reference.SpanId,
            reference.TurnIndex,
            reference.RelativePosition);

    private static InstructionEvidenceReferenceV1 Tokenized(HistoricalRawEvidenceReferenceV1 reference) =>
        InstructionFindingReferenceTokenizationV1.Tokenize(new(
            reference.SessionId.ToString(),
            reference.TraceId,
            reference.SpanId,
            reference.TurnIndex,
            (InstructionEvidenceRelativePositionV1)(int)reference.RelativePosition));

    private sealed class PreviewSnapshotSource(IReadOnlyList<HistoricalSessionMetadataV1> sessions)
        : IHistoricalEvidenceSnapshotSourceV1
    {
        internal List<Guid> ReadSessionIds { get; } = [];

        public ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(
            HistoricalEvidenceSelectionV1 selection,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IHistoricalEvidenceSnapshotLeaseV1>(new Lease(this, sessions));

        private sealed class Lease(
            PreviewSnapshotSource owner,
            IReadOnlyList<HistoricalSessionMetadataV1> sessions) : IHistoricalEvidenceSnapshotLeaseV1
        {
            public string SnapshotId => "snapshot-preview-v1";
            public IReadOnlyList<HistoricalSessionMetadataV1> Sessions => sessions;
            public long OmittedEarlierMatchingSessionCount => 0;

            public ValueTask<IReadOnlyList<HistoricalEvidenceGroupDraftV1>> ReadEvidenceAsync(
                Guid sessionId,
                bool includeDescriptors,
                CancellationToken cancellationToken)
            {
                owner.ReadSessionIds.Add(sessionId);
                var number = int.Parse(sessionId.ToString("D")[^12..]);
                return ValueTask.FromResult<IReadOnlyList<HistoricalEvidenceGroupDraftV1>>(
                [
                    new(
                        HistoricalEvidenceGroupKindV1.TurnRollup,
                        [new(sessionId, $"trace-{number}", $"span-{number}", 1, HistoricalEvidenceRelativePositionV1.Anchor)],
                        1,
                        "turn",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                ]);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class MultiReferenceSnapshotSource : IHistoricalEvidenceSnapshotSourceV1
    {
        private static readonly Guid SessionId =
            Guid.Parse("018f0000-0000-7000-8000-000000000021");
        private static readonly HistoricalRawEvidenceReferenceV1[] References =
        [
            new(SessionId, "trace-a", "span-a", 1, HistoricalEvidenceRelativePositionV1.Anchor),
            new(SessionId, "trace-b", "span-b", 2, HistoricalEvidenceRelativePositionV1.Anchor),
        ];

        public ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(
            HistoricalEvidenceSelectionV1 selection,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IHistoricalEvidenceSnapshotLeaseV1>(new Lease());

        private sealed class Lease : IHistoricalEvidenceSnapshotLeaseV1
        {
            public string SnapshotId => "snapshot-multi-reference-v1";
            public IReadOnlyList<HistoricalSessionMetadataV1> Sessions =>
            [
                new(
                    SessionId,
                    SessionSourceSurface.CopilotSdk,
                    "1.0.0",
                    "adapter.v1",
                    SessionCompleteness.Full,
                    [],
                    HistoricalEvidenceSourceKindV1.LiveOtel,
                    SessionContentState.NotCaptured,
                    "repo-a",
                    null,
                    null,
                    null,
                    new DateTimeOffset(2026, 7, 1, 0, 1, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 1, 0, 1, 30, TimeSpan.Zero),
                    new HistoricalSessionCapabilitiesV1(
                        true, false, false, false, false, false, false, false, false, false, false, false),
                    References.Select(reference => new HistoricalEvidenceLocationV1(
                        reference.SessionId,
                        reference.TraceId,
                        reference.SpanId,
                        reference.TurnIndex,
                        reference.RelativePosition)).ToArray(),
                    []),
            ];
            public long OmittedEarlierMatchingSessionCount => 0;

            public ValueTask<IReadOnlyList<HistoricalEvidenceGroupDraftV1>> ReadEvidenceAsync(
                Guid sessionId,
                bool includeDescriptors,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult<IReadOnlyList<HistoricalEvidenceGroupDraftV1>>(
                [
                    new(
                        HistoricalEvidenceGroupKindV1.TurnRollup,
                        References,
                        2,
                        "turn",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null),
                ]);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationBlockingEfficiencyExecutor : IHistoricalEfficiencyExecutorV1
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HistoricalEfficiencyAnalysisV1> AnalyzeAsync(
            HistoricalEvidenceExtractionV1 extraction,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation was not observed.");
        }
    }

    private sealed class ImmediateEfficiencyExecutor : IHistoricalEfficiencyExecutorV1
    {
        public Task<HistoricalEfficiencyAnalysisV1> AnalyzeAsync(
            HistoricalEvidenceExtractionV1 extraction,
            CancellationToken cancellationToken) =>
            Task.FromResult(HistoricalEfficiencyAnalyzerV1.Analyze(extraction));
    }

    private sealed class IncompleteEfficiencyExecutor : IHistoricalEfficiencyExecutorV1
    {
        private readonly TaskCompletionSource<HistoricalEfficiencyAnalysisV1> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HistoricalEfficiencyAnalysisV1> AnalyzeAsync(
            HistoricalEvidenceExtractionV1 extraction,
            CancellationToken cancellationToken) =>
            completion.Task;

        internal void Complete(HistoricalEfficiencyAnalysisV1 result) =>
            completion.TrySetResult(result);
    }

    private sealed class SynchronouslyBlockingEfficiencyExecutor : IHistoricalEfficiencyExecutorV1
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(initialState: false);

        public Task<HistoricalEfficiencyAnalysisV1> AnalyzeAsync(
            HistoricalEvidenceExtractionV1 extraction,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            Release.Wait();
            return Task.FromResult(HistoricalEfficiencyAnalyzerV1.Analyze(extraction));
        }
    }
}
