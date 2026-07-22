using System.Text.Json;
using CopilotAgentObservability.InstructionFindings;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalInstructionAnalysisTests
{
    [Fact]
    public async Task RunAsync_UsesOnlyPersistedExtraction_AndEmitsRecurringIssue59Handoff()
    {
        using var temp = new MonitorTempDirectory();
        var first = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", turnCount: 2);
        var second = Session(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0", turnCount: 1);
        var source = new SnapshotSource([first, second]);
        var extractionStore = new SqliteHistoricalEvidenceDatasetStoreV1(temp.DatabasePath);
        extractionStore.CreateSchema();
        var extractionService = new HistoricalEvidenceApplicationServiceV1(source, extractionStore);
        var extraction = await extractionService.CreateAsync(
            HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [first.SessionId, second.SessionId]),
            CancellationToken.None);
        var sourceReadsAfterExtraction = source.ReadCount;
        source.Disable();

        var provider = new RecordingProvider(request =>
        {
            var firstGroups = request.Dataset.EvidenceGroups.Where(group => group.SessionId == first.SessionId.ToString()).ToArray();
            var secondGroup = request.Dataset.EvidenceGroups.Single(group => group.SessionId == second.SessionId.ToString());
            return new HistoricalInstructionProviderResultV1(
                HistoricalInstructionProviderCompletionV1.Complete,
                "trace-1",
                [
                    new HistoricalInstructionFindingSubmissionV1(
                        InstructionFindingCategoryV1.AcceptanceCriteriaMissing,
                        InstructionFindingVerdictV1.Supported,
                        InstructionFindingExtractorSourceV1.DeterministicPrepass,
                        firstGroups.SelectMany(group => group.References).Select(ToInstructionReference).ToArray(),
                        [.. firstGroups.Select(group => group.GroupId), secondGroup.GroupId]),
                ]);
        });
        var analysisStore = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        analysisStore.CreateSchema();
        var service = new HistoricalInstructionAnalysisApplicationServiceV1(extractionService, analysisStore, provider);
        var request = Request(extraction);
        var runId = service.Start(request);

        await service.RunAsync(runId, CancellationToken.None);

        var run = Assert.IsType<HistoricalInstructionAnalysisRunV1>(service.Get(runId));
        Assert.Equal(HistoricalInstructionAnalysisStateV1.Succeeded, run.State);
        Assert.Equal(sourceReadsAfterExtraction, source.ReadCount);
        Assert.Equal(runId, InstructionFindingHandoffConsumerV1.Validate(run.HandoffBytes));
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(run.Receipt);
        var support = Assert.Single(receipt.Findings);
        var review = ReviewCase("A");
        Assert.Equal(review.ExpectedSupportKind, Snake(support.SupportKind));
        Assert.Equal(review.SessionCount, support.RecurringCount);
        Assert.Equal(2, support.SupportingSessionIds.Count);
        Assert.Equal(review.ExpectedVerdict, Snake(support.Verdict));
        Assert.Equal(review.ExpectedEligible, support.CandidateEligibility == InstructionCandidateEligibilityV1.Eligible);
        Assert.Equal(2, support.CompletenessDistribution.Count);
        Assert.Equal(2, support.SourceSurfaceDistribution.Count);
        Assert.Equal(extraction.RepositorySafe.Distribution.Completeness, receipt.DatasetDistribution.Completeness);
        Assert.Equal(extraction.RepositorySafe.Distribution.SourceKinds, receipt.DatasetDistribution.SourceKinds);
        Assert.Equal(extraction.RepositorySafe.Distribution.Capabilities, receipt.DatasetDistribution.Capabilities);
        Assert.DoesNotContain(first.SessionId.ToString(), string.Join('|', support.SupportingSessionIds));
        Assert.All(support.EvidenceRefs, reference => Assert.StartsWith("session-ref-", reference.SessionId, StringComparison.Ordinal));
        var providerRequest = Assert.Single(provider.Requests);
        Assert.Equal(runId, providerRequest.RunId);
        Assert.Equal(request, providerRequest.Provenance);
        Assert.Equal(extraction.RawLocalBytes, providerRequest.CanonicalDatasetBytes);
        Assert.Equal(HistoricalInstructionAnalysisPromptV1.Template, providerRequest.Prompt);
        Assert.Contains("Do not search", providerRequest.Prompt, StringComparison.Ordinal);
        Assert.Contains(HistoricalInstructionAnalysisContractsV1.PromptTemplateVersion, providerRequest.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_OneSessionSupportedSubmissionBecomesWeakAndIneligible()
    {
        var session = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", turnCount: 2);
        var provider = new RecordingProvider(request =>
        {
            var groups = request.Dataset.EvidenceGroups.ToArray();
            return Complete(request, groups, groups);
        });
        using var fixture = await AnalysisFixture.CreateAsync([session], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var run = Assert.IsType<HistoricalInstructionAnalysisRunV1>(fixture.Service.Get(runId));
        var finding = Assert.Single(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(run.Receipt).Findings);
        var review = ReviewCase("B");
        Assert.Equal(review.ExpectedSupportKind, Snake(finding.SupportKind));
        Assert.Equal(review.SessionCount, finding.RecurringCount);
        Assert.Equal(review.ExpectedVerdict, Snake(finding.Verdict));
        Assert.Equal(review.ExpectedEligible, finding.CandidateEligibility == InstructionCandidateEligibilityV1.Eligible);
        Assert.DoesNotContain("\"candidates\":[{", System.Text.Encoding.UTF8.GetString(run.HandoffBytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ProviderCompleteZeroFindingsPersistsCanonicalEmptyHandoff()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var run = Assert.IsType<HistoricalInstructionAnalysisRunV1>(fixture.Service.Get(runId));
        Assert.Equal(HistoricalInstructionAnalysisStateV1.ZeroFindings, run.State);
        Assert.Empty(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(run.Receipt).Findings);
        Assert.Equal(runId, InstructionFindingHandoffConsumerV1.Validate(run.HandoffBytes));
    }

    [Fact]
    public async Task RunAsync_ProviderPartialIsNotSuccess()
    {
        var provider = new RecordingProvider(request =>
        {
            var groups = request.Dataset.EvidenceGroups.ToArray();
            return Complete(request, groups, groups) with
            {
                Completion = HistoricalInstructionProviderCompletionV1.Partial,
            };
        });
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.ProviderPartial);
    }

    [Fact]
    public async Task RunAsync_WeakAndIncompleteProviderVerdictsAreNeverUpgraded()
    {
        foreach (var assessedVerdict in new[] { InstructionFindingVerdictV1.Weak, InstructionFindingVerdictV1.Incomplete })
        {
            var provider = new RecordingProvider(request =>
            {
                var groups = request.Dataset.EvidenceGroups.ToArray();
                var complete = Complete(request, groups.Where(group => group.SessionId == request.Dataset.Sessions[0].SessionId).ToArray(), groups);
                return complete with
                {
                    Findings = [complete.Findings[0] with { AssessedVerdict = assessedVerdict }],
                };
            });
            using var fixture = await AnalysisFixture.CreateAsync(
                [
                    Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2),
                    Session(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0", 1),
                ], provider);
            var runId = fixture.Service.Start(Request(fixture.Extraction));

            await fixture.Service.RunAsync(runId, CancellationToken.None);

            var support = Assert.Single(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(fixture.Service.Get(runId)!.Receipt).Findings);
            Assert.Equal(assessedVerdict, support.Verdict);
            Assert.Equal(InstructionCandidateEligibilityV1.Ineligible, support.CandidateEligibility);
        }
    }

    [Fact]
    public async Task RunAsync_ProviderFailureIsNotSuccess()
    {
        var provider = new ThrowingProvider();
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.ProviderFailed);
    }

    [Fact]
    public async Task RunAsync_TimeoutIsDistinctFromProviderFailure()
    {
        var provider = new WaitingProvider();
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction, timeoutMilliseconds: 25));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.TimedOut);
    }

    [Fact]
    public async Task RunAsync_UnresolvedSupportingGroupRejectsWholeResult()
    {
        var provider = new RecordingProvider(request =>
        {
            var groups = request.Dataset.EvidenceGroups.ToArray();
            var complete = Complete(request, groups, groups);
            return complete with
            {
                Findings = [complete.Findings[0] with { SupportingGroupIds = ["historical-group-00000000000000000000000000000000"] }],
            };
        });
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.InvalidCitation);
    }

    [Fact]
    public async Task RunAsync_UnresolvedEvidenceReferenceRejectsWholeResult()
    {
        var provider = new RecordingProvider(request =>
        {
            var groups = request.Dataset.EvidenceGroups.ToArray();
            var complete = Complete(request, groups, groups);
            var reference = complete.Findings[0].EvidenceRefs[0] with { SpanId = "missing-span" };
            return complete with
            {
                Findings = [complete.Findings[0] with { EvidenceRefs = [reference] }],
            };
        });
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.InvalidCitation);
    }

    [Fact]
    public async Task RunAsync_StaleChecksumDoesNotCallProvider()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction, extractionSha256: new string('b', 64)));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.StaleExtraction);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task RunAsync_SanitizedOnlyDoesNotCallRawProvider()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)],
            provider,
            sanitizedOnly: true);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.ContentUnavailable);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task RunAsync_EmptyIncludedSetIsDistinctAndDoesNotCallProvider()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync([], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.NoEligibleSessions);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Start_RetryCreatesNewQueuedRunAndPreservesProvenance()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        var request = Request(fixture.Extraction);

        var first = fixture.Service.Start(request);
        var retry = fixture.Service.Start(request);

        Assert.NotEqual(first, retry);
        Assert.Equal(HistoricalInstructionAnalysisStateV1.Queued, fixture.Service.Get(first)!.State);
        Assert.Equal(HistoricalInstructionAnalysisStateV1.Queued, fixture.Service.Get(retry)!.State);
        Assert.Equal(request, fixture.Service.Get(first)!.Request);
        Assert.Equal(request, fixture.Service.Get(retry)!.Request);
    }

    [Fact]
    public async Task RunAsync_RawLocalDescriptorNeverEntersReceiptOrHandoff()
    {
        const string rawMarker = "raw prompt body marker alpha";
        var provider = new RecordingProvider(request =>
        {
            var turnGroups = request.Dataset.EvidenceGroups.Where(group => group.Kind == HistoricalEvidenceGroupKindV1.TurnRollup).ToArray();
            return Complete(request, turnGroups, request.Dataset.EvidenceGroups.ToArray());
        });
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2)],
            provider,
            rawDescriptor: rawMarker);
        Assert.Contains(rawMarker, System.Text.Encoding.UTF8.GetString(fixture.Extraction.RawLocalBytes), StringComparison.Ordinal);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var run = Assert.IsType<HistoricalInstructionAnalysisRunV1>(fixture.Service.Get(runId));
        var receiptBytes = HistoricalInstructionAnalysisJsonV1.Serialize(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(run.Receipt));
        Assert.DoesNotContain(rawMarker, System.Text.Encoding.UTF8.GetString(receiptBytes), StringComparison.Ordinal);
        Assert.DoesNotContain(rawMarker, System.Text.Encoding.UTF8.GetString(run.HandoffBytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiptValidation_RejectsPathLikeSourceDistributionKey()
    {
        var provider = new RecordingProvider(request =>
        {
            var groups = request.Dataset.EvidenceGroups.ToArray();
            return Complete(request, groups, groups);
        });
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        await fixture.Service.RunAsync(runId, CancellationToken.None);
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(fixture.Service.Get(runId)!.Receipt);
        var finding = Assert.Single(receipt.Findings);
        var unsafeReceipt = receipt with
        {
            Findings =
            [
                finding with
                {
                    SourceVersionDistribution = [new HistoricalDistributionCountV1("C:\\synthetic\\secret.txt", 1)],
                },
            ],
        };

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(
            () => HistoricalInstructionAnalysisJsonV1.Serialize(unsafeReceipt));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    private static HistoricalInstructionAnalysisRequestV1 Request(
        HistoricalEvidenceExtractionV1 extraction,
        string? extractionSha256 = null,
        int timeoutMilliseconds = 30_000) =>
        new(
            HistoricalInstructionAnalysisContractsV1.RequestSchemaVersion,
            extraction.RawLocal.ExtractionId,
            extractionSha256 ?? extraction.RawLocalSha256,
            "gpt-5",
            "copilot",
            new string('a', 64),
            timeoutMilliseconds,
            HistoricalInstructionAnalysisContractsV1.PromptTemplateVersion);

    private static HistoricalInstructionProviderResultV1 Complete(
        HistoricalInstructionProviderRequestV1 request,
        IReadOnlyList<HistoricalEvidenceGroupV1> evidenceGroups,
        IReadOnlyList<HistoricalEvidenceGroupV1> supportGroups) =>
        new(
            HistoricalInstructionProviderCompletionV1.Complete,
            "trace-1",
            [
                new HistoricalInstructionFindingSubmissionV1(
                    InstructionFindingCategoryV1.AcceptanceCriteriaMissing,
                    InstructionFindingVerdictV1.Supported,
                    InstructionFindingExtractorSourceV1.DeterministicPrepass,
                    evidenceGroups.SelectMany(group => group.References).Select(ToInstructionReference).ToArray(),
                    supportGroups.Select(group => group.GroupId).ToArray()),
            ]);

    private static void AssertTerminalWithoutReceipt(
        HistoricalInstructionAnalysisApplicationServiceV1 service,
        long runId,
        HistoricalInstructionAnalysisStateV1 expected)
    {
        var run = Assert.IsType<HistoricalInstructionAnalysisRunV1>(service.Get(runId));
        Assert.Equal(expected, run.State);
        Assert.Null(run.Receipt);
        Assert.Empty(run.HandoffBytes);
    }

    private static ReviewFixtureCase ReviewCase(string id)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "HistoricalInstructionAnalysis",
            "historical-instruction-analysis.ab-review.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("historical-instruction-analysis.ab-review.v1",
            document.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("github-issue-73-owner-contract",
            document.RootElement.GetProperty("review_authority").GetString());
        var item = document.RootElement.GetProperty("cases").EnumerateArray()
            .Single(value => value.GetProperty("id").GetString() == id);
        return new(
            item.GetProperty("session_count").GetInt32(),
            item.GetProperty("expected_support_kind").GetString()!,
            item.GetProperty("expected_verdict").GetString()!,
            item.GetProperty("expected_candidate_eligible").GetBoolean());
    }

    private static string Snake<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            char.IsUpper(character) && index > 0 ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));

    private static InstructionRawEvidenceReferenceV1 ToInstructionReference(HistoricalEvidenceReferenceV1 reference) =>
        new(reference.SessionId, reference.TraceId, reference.SpanId, reference.TurnIndex,
            (InstructionEvidenceRelativePositionV1)(int)reference.RelativePosition);

    private static HistoricalSessionMetadataV1 Session(
        int number,
        SessionCompleteness completeness,
        SessionSourceSurface sourceSurface,
        string sourceVersion,
        int turnCount)
    {
        var sessionId = Guid.Parse($"018f0000-0000-7000-8000-{number:D12}");
        var locations = Enumerable.Range(1, turnCount)
            .Select(turn => new HistoricalEvidenceLocationV1(
                sessionId, $"trace-{number}", $"span-{number}-{turn}", turn,
                HistoricalEvidenceRelativePositionV1.Anchor))
            .ToArray();
        return new HistoricalSessionMetadataV1(
            sessionId,
            sourceSurface,
            sourceVersion,
            "adapter.v1",
            completeness,
            completeness == SessionCompleteness.Partial ? ["historical_summary_only"] : [],
            HistoricalEvidenceSourceKindV1.LiveOtel,
            SessionContentState.Available,
            "owner/repository",
            "workspace",
            null,
            null,
            At(number),
            At(number),
            new HistoricalSessionCapabilitiesV1(true, false, false, false, false, false, false, false, false, false, false, false),
            locations,
            []);
    }

    private static DateTimeOffset At(int minute) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute);

    private sealed class SnapshotSource(
        IReadOnlyList<HistoricalSessionMetadataV1> sessions,
        string? rawDescriptor = null) : IHistoricalEvidenceSnapshotSourceV1
    {
        private bool available = true;

        public int ReadCount { get; private set; }

        public void Disable() => available = false;

        public ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(
            HistoricalEvidenceSelectionV1 selection,
            CancellationToken cancellationToken)
        {
            if (!available) throw new InvalidOperationException("source is unavailable after extraction");
            return ValueTask.FromResult<IHistoricalEvidenceSnapshotLeaseV1>(new Lease(this, sessions, rawDescriptor));
        }

        private sealed class Lease(
            SnapshotSource owner,
            IReadOnlyList<HistoricalSessionMetadataV1> sessions,
            string? rawDescriptor)
            : IHistoricalEvidenceSnapshotLeaseV1
        {
            public string SnapshotId => "snapshot-issue-73";
            public IReadOnlyList<HistoricalSessionMetadataV1> Sessions => sessions;
            public long OmittedEarlierMatchingSessionCount => 0;

            public ValueTask<IReadOnlyList<HistoricalEvidenceGroupDraftV1>> ReadEvidenceAsync(
                Guid sessionId,
                bool includeDescriptors,
                CancellationToken cancellationToken)
            {
                owner.ReadCount++;
                var session = sessions.Single(value => value.SessionId == sessionId);
                var groups = session.EvidenceLocations.Select(location => new HistoricalEvidenceGroupDraftV1(
                        HistoricalEvidenceGroupKindV1.TurnRollup,
                        [new(location.SessionId, location.TraceId, location.SpanId, location.TurnIndex, location.RelativePosition)],
                        1,
                        "turn",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null)).ToList();
                if (rawDescriptor is not null && includeDescriptors && session == sessions[0])
                {
                    var location = session.EvidenceLocations[0];
                    groups.Add(new HistoricalEvidenceGroupDraftV1(
                        HistoricalEvidenceGroupKindV1.UserCorrection,
                        [new(location.SessionId, location.TraceId, location.SpanId, location.TurnIndex, location.RelativePosition)],
                        null, null, null, null, null, null, null, rawDescriptor));
                }
                return ValueTask.FromResult<IReadOnlyList<HistoricalEvidenceGroupDraftV1>>(groups);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingProvider(
        Func<HistoricalInstructionProviderRequestV1, HistoricalInstructionProviderResultV1> resultFactory)
        : IHistoricalInstructionAnalysisProviderV1
    {
        public List<HistoricalInstructionProviderRequestV1> Requests { get; } = [];

        public Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
            HistoricalInstructionProviderRequestV1 request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(resultFactory(request));
        }
    }

    private sealed class ThrowingProvider : IHistoricalInstructionAnalysisProviderV1
    {
        public Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
            HistoricalInstructionProviderRequestV1 request,
            CancellationToken cancellationToken) =>
            Task.FromException<HistoricalInstructionProviderResultV1>(new InvalidOperationException("provider detail must not escape"));
    }

    private sealed class WaitingProvider : IHistoricalInstructionAnalysisProviderV1
    {
        public async Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
            HistoricalInstructionProviderRequestV1 request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class AnalysisFixture : IDisposable
    {
        private readonly MonitorTempDirectory temp;

        private AnalysisFixture(
            MonitorTempDirectory temp,
            HistoricalEvidenceExtractionV1 extraction,
            HistoricalInstructionAnalysisApplicationServiceV1 service)
        {
            this.temp = temp;
            Extraction = extraction;
            Service = service;
        }

        internal HistoricalEvidenceExtractionV1 Extraction { get; }
        internal HistoricalInstructionAnalysisApplicationServiceV1 Service { get; }

        internal static async Task<AnalysisFixture> CreateAsync(
            IReadOnlyList<HistoricalSessionMetadataV1> sessions,
            IHistoricalInstructionAnalysisProviderV1 provider,
            bool sanitizedOnly = false,
            string? rawDescriptor = null)
        {
            var temp = new MonitorTempDirectory();
            try
            {
                var prepared = rawDescriptor is null
                    ? sessions
                    : sessions.Select(session => session with
                    {
                        Capabilities = session.Capabilities with { RawLocalDescriptor = true },
                    }).ToArray();
                var source = new SnapshotSource(prepared, rawDescriptor);
                var extractionStore = new SqliteHistoricalEvidenceDatasetStoreV1(temp.DatabasePath);
                extractionStore.CreateSchema();
                var extractionService = new HistoricalEvidenceApplicationServiceV1(source, extractionStore);
                var selection = prepared.Count == 0
                    ? HistoricalEvidenceSelectionV1.Create(repository: "owner/repository", sanitizedOnly: sanitizedOnly)
                    : HistoricalEvidenceSelectionV1.Create(
                        explicitSessionIds: prepared.Select(session => session.SessionId).ToArray(),
                        sanitizedOnly: sanitizedOnly);
                var extraction = await extractionService.CreateAsync(selection, CancellationToken.None);
                var analysisStore = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
                analysisStore.CreateSchema();
                return new AnalysisFixture(
                    temp,
                    extraction,
                    new HistoricalInstructionAnalysisApplicationServiceV1(extractionService, analysisStore, provider));
            }
            catch
            {
                temp.Dispose();
                throw;
            }
        }

        public void Dispose() => temp.Dispose();
    }

    private sealed record ReviewFixtureCase(
        int SessionCount,
        string ExpectedSupportKind,
        string ExpectedVerdict,
        bool ExpectedEligible);
}
