using System.Security.Cryptography;
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
        var second = Session(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0", turnCount: 2);
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
            var secondGroups = request.Dataset.EvidenceGroups.Where(group => group.SessionId == second.SessionId.ToString()).ToArray();
            return new HistoricalInstructionProviderResultV1(
                HistoricalInstructionProviderCompletionV1.Complete,
                "trace-1",
                [
                    new HistoricalInstructionFindingSubmissionV1(
                        InstructionFindingCategoryV1.AcceptanceCriteriaMissing,
                        InstructionFindingVerdictV1.Supported,
                        InstructionFindingExtractorSourceV1.DeterministicPrepass,
                        firstGroups.SelectMany(group => group.References).Select(ToInstructionReference).ToArray(),
                        [.. firstGroups.Select(group => group.GroupId), .. secondGroups.Select(group => group.GroupId)]),
                ]);
        });
        var analysisStore = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        analysisStore.CreateSchema();
        var service = new HistoricalInstructionAnalysisApplicationServiceV1(extractionService, analysisStore, provider);
        var request = Request(extraction);
        var runId = service.Start(request);

        await service.RunAsync(runId, CancellationToken.None);

        var run = Assert.IsType<HistoricalInstructionAnalysisReadV1>(service.Get(runId));
        Assert.Equal(runId, HistoricalInstructionAnalysisReadConsumerV1.Validate(run));
        Assert.Equal(HistoricalInstructionAnalysisStateV1.Succeeded, run.State);
        Assert.Equal(sourceReadsAfterExtraction, source.ReadCount);
        Assert.Equal(runId, InstructionFindingHandoffConsumerV1.Validate(run.HandoffBytes));
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(run.Receipt);
        var support = Assert.Single(receipt.Findings);
        var review = ReviewCase("A");
        Assert.Equal(new[] { true, true }, review.PerSessionCategoryMinimumMet);
        Assert.Equal(review.ExpectedSupportKind!, Snake(support.SupportKind));
        Assert.Equal(review.SessionCount, support.RecurringCount);
        Assert.Equal(2, support.SupportingSessionIds.Count);
        Assert.Equal(review.ExpectedVerdict!, Snake(support.Verdict));
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
    public async Task RunAsync_UnrelatedSecondSessionCannotPromoteCategoryToRecurring()
    {
        var first = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", turnCount: 2);
        var second = Session(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0", turnCount: 1);
        var provider = new RecordingProvider(request =>
        {
            var firstGroups = request.Dataset.EvidenceGroups.Where(group => group.SessionId == first.SessionId.ToString()).ToArray();
            return Complete(request, firstGroups, request.Dataset.EvidenceGroups.ToArray());
        });
        using var fixture = await AnalysisFixture.CreateAsync([first, second], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var read = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        var finding = Assert.Single(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(read.Receipt).Findings);
        var review = ReviewCase("C");
        Assert.Equal(new[] { true, false }, review.PerSessionCategoryMinimumMet);
        Assert.Equal(review.ExpectedSupportKind!, Snake(finding.SupportKind));
        Assert.Equal(review.GroundedSessionCount, finding.RecurringCount);
        Assert.Equal(review.ExpectedVerdict!, Snake(finding.Verdict));
        Assert.Equal(review.ExpectedEligible, finding.CandidateEligibility == InstructionCandidateEligibilityV1.Eligible);
        Assert.Single(finding.SupportingSessionIds);
        Assert.Equal(
            fixture.Extraction.RawLocal.EvidenceGroups
                .Where(group => group.SessionId == first.SessionId.ToString())
                .Select(group => group.GroupId).Order(StringComparer.Ordinal),
            finding.SupportingGroupIds);
        Assert.All(finding.EvidenceRefs, reference =>
            Assert.Equal(finding.SupportingSessionIds[0], reference.SessionId));
        Assert.Equal(runId, HistoricalInstructionAnalysisReadConsumerV1.Validate(read));
    }

    [Fact]
    public async Task RunAsync_RedundantNonAnchorTurnGroupIsExcludedFromGroundedSupport()
    {
        var baseSession = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", turnCount: 2);
        var session = baseSession with
        {
            EvidenceLocations =
            [
                .. baseSession.EvidenceLocations,
                new HistoricalEvidenceLocationV1(
                    baseSession.SessionId,
                    "trace-context",
                    "span-context",
                    3,
                    HistoricalEvidenceRelativePositionV1.Previous),
            ],
        };
        var provider = new RecordingProvider(request =>
        {
            var anchorGroups = request.Dataset.EvidenceGroups
                .Where(group => group.References.Any(reference =>
                    reference.TraceId == "trace-1"
                    && reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Anchor))
                .ToArray();
            return Complete(request, anchorGroups, request.Dataset.EvidenceGroups.ToArray());
        });
        using var fixture = await AnalysisFixture.CreateAsync([session], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var support = Assert.Single(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(
            fixture.Service.Get(runId)!.Receipt).Findings);
        Assert.Equal(
            fixture.Extraction.RawLocal.EvidenceGroups
                .Where(group => group.References.Any(reference =>
                    reference.TraceId == "trace-1"
                    && reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Anchor))
                .Select(group => group.GroupId).Order(StringComparer.Ordinal),
            support.SupportingGroupIds);
        Assert.DoesNotContain(support.EvidenceRefs, reference =>
            reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Previous);
    }

    [Fact]
    public async Task RunAsync_AmbiguityGroundingKeepsRequiredNonTurnContextTrace()
    {
        var baseSession = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", turnCount: 1);
        var session = baseSession with
        {
            Capabilities = baseSession.Capabilities with { ErrorSpan = true },
            EvidenceLocations =
            [
                .. baseSession.EvidenceLocations,
                new HistoricalEvidenceLocationV1(
                    baseSession.SessionId,
                    "trace-context",
                    "error-context",
                    null,
                    HistoricalEvidenceRelativePositionV1.Previous),
            ],
        };
        var provider = new RecordingProvider(request =>
        {
            var anchorGroup = request.Dataset.EvidenceGroups.Single(group =>
                group.Kind == HistoricalEvidenceGroupKindV1.TurnRollup);
            return new HistoricalInstructionProviderResultV1(
                HistoricalInstructionProviderCompletionV1.Complete,
                "trace-1",
                [
                    new HistoricalInstructionFindingSubmissionV1(
                        InstructionFindingCategoryV1.Ambiguity,
                        InstructionFindingVerdictV1.Supported,
                        InstructionFindingExtractorSourceV1.DeterministicPrepass,
                        anchorGroup.References.Select(ToInstructionReference).ToArray(),
                        request.Dataset.EvidenceGroups.Select(group => group.GroupId).ToArray()),
                ]);
        });
        using var fixture = await AnalysisFixture.CreateAsync([session], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var support = Assert.Single(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(
            fixture.Service.Get(runId)!.Receipt).Findings);
        Assert.Equal(HistoricalInstructionSupportKindV1.SingleSession, support.SupportKind);
        Assert.Equal(InstructionFindingVerdictV1.Weak, support.Verdict);
        Assert.Equal(2, support.SupportingGroupIds.Count);
        Assert.Contains(support.EvidenceRefs, reference =>
            reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Previous);
    }

    [Fact]
    public async Task RunAsync_ScopeBoundaryGroundingRequiresTurnAndErrorInTheSameSession()
    {
        var baseSession = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", turnCount: 1);
        var session = baseSession with
        {
            Capabilities = baseSession.Capabilities with { ErrorSpan = true },
            EvidenceLocations =
            [
                .. baseSession.EvidenceLocations,
                new HistoricalEvidenceLocationV1(
                    baseSession.SessionId,
                    "trace-1",
                    "error-1",
                    null,
                    HistoricalEvidenceRelativePositionV1.Anchor),
            ],
        };
        var provider = new RecordingProvider(request =>
        {
            var groups = request.Dataset.EvidenceGroups.ToArray();
            return new HistoricalInstructionProviderResultV1(
                HistoricalInstructionProviderCompletionV1.Complete,
                "trace-1",
                [
                    new HistoricalInstructionFindingSubmissionV1(
                        InstructionFindingCategoryV1.ScopeBoundaryMissing,
                        InstructionFindingVerdictV1.Supported,
                        InstructionFindingExtractorSourceV1.DeterministicPrepass,
                        groups.SelectMany(group => group.References).Select(ToInstructionReference).ToArray(),
                        groups.Select(group => group.GroupId).ToArray()),
                ]);
        });
        using var fixture = await AnalysisFixture.CreateAsync([session], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var support = Assert.Single(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(
            fixture.Service.Get(runId)!.Receipt).Findings);
        var review = ReviewCase("D");
        Assert.Equal(new[] { true }, review.PerSessionCategoryMinimumMet);
        Assert.Equal(review.GroundedSessionCount, support.RecurringCount);
        Assert.Equal(review.ExpectedSupportKind!, Snake(support.SupportKind));
        Assert.Equal(review.ExpectedVerdict!, Snake(support.Verdict));
        Assert.Equal(review.ExpectedEligible,
            support.CandidateEligibility == InstructionCandidateEligibilityV1.Eligible);
        Assert.Contains(support.SupportingGroupIds, groupId =>
            fixture.Extraction.RawLocal.EvidenceGroups.Single(group => group.GroupId == groupId).Kind
                == HistoricalEvidenceGroupKindV1.ErrorSpan);
    }

    [Fact]
    public async Task RunAsync_AmbiguityContextGroundsTwoSessionsAndFinalAnchorSession()
    {
        var first = ContextSession(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0");
        var second = ContextSession(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0");
        var provider = new RecordingProvider(request =>
        {
            var firstGroups = request.Dataset.EvidenceGroups
                .Where(group => group.SessionId == first.SessionId.ToString()).ToArray();
            return new HistoricalInstructionProviderResultV1(
                HistoricalInstructionProviderCompletionV1.Complete,
                "trace-1",
                [
                    new HistoricalInstructionFindingSubmissionV1(
                        InstructionFindingCategoryV1.Ambiguity,
                        InstructionFindingVerdictV1.Supported,
                        InstructionFindingExtractorSourceV1.DeterministicPrepass,
                        firstGroups.SelectMany(group => group.References).Select(ToInstructionReference).ToArray(),
                        request.Dataset.EvidenceGroups.Select(group => group.GroupId).ToArray()),
                ]);
        });
        using var fixture = await AnalysisFixture.CreateAsync([first, second], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var read = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        var support = Assert.Single(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(read.Receipt).Findings);
        var finding = Assert.Single(InstructionFindingJsonV1.Deserialize(read.HandoffBytes).Findings);
        var review = ReviewCase("H");
        Assert.Equal(new[] { true, true }, review.PerSessionCategoryMinimumMet);
        Assert.Equal(review.GroundedSessionCount, support.RecurringCount);
        Assert.Equal(review.ExpectedSupportKind!, Snake(support.SupportKind));
        Assert.Equal(review.ExpectedVerdict!, Snake(support.Verdict));
        Assert.Equal(review.ExpectedEligible,
            support.CandidateEligibility == InstructionCandidateEligibilityV1.Eligible);
        Assert.Equal(InstructionFindingCategoryV1.Ambiguity, finding.Category);
        Assert.Contains(finding.EvidenceRefs,
            reference => reference.RelativePosition == InstructionEvidenceRelativePositionV1.Previous);
        Assert.Single(finding.EvidenceRefs.Select(reference => reference.SessionId).Distinct(StringComparer.Ordinal));
        Assert.Equal(runId, HistoricalInstructionAnalysisReadConsumerV1.Validate(read));
    }

    [Fact]
    public async Task RunAsync_ContextFromNonAnchorSessionCannotEnterFinalFinding()
    {
        var first = ContextSession(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0");
        var second = ContextSession(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0");
        var provider = new RecordingProvider(request =>
        {
            var anchor = request.Dataset.EvidenceGroups
                .Where(group => group.SessionId == first.SessionId.ToString())
                .SelectMany(group => group.References)
                .Single(reference => reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Anchor);
            var foreignContext = request.Dataset.EvidenceGroups
                .Where(group => group.SessionId == second.SessionId.ToString())
                .SelectMany(group => group.References)
                .Single(reference => reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Previous);
            return new HistoricalInstructionProviderResultV1(
                HistoricalInstructionProviderCompletionV1.Complete,
                "trace-1",
                [
                    new HistoricalInstructionFindingSubmissionV1(
                        InstructionFindingCategoryV1.Ambiguity,
                        InstructionFindingVerdictV1.Supported,
                        InstructionFindingExtractorSourceV1.DeterministicPrepass,
                        [ToInstructionReference(anchor), ToInstructionReference(foreignContext)],
                        request.Dataset.EvidenceGroups.Select(group => group.GroupId).ToArray()),
                ]);
        });
        using var fixture = await AnalysisFixture.CreateAsync([first, second], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.InvalidCitation);
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

        var run = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        var finding = Assert.Single(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(run.Receipt).Findings);
        var review = ReviewCase("B");
        Assert.Equal(new[] { true }, review.PerSessionCategoryMinimumMet);
        Assert.Equal(review.ExpectedSupportKind, Snake(finding.SupportKind));
        Assert.Equal(review.SessionCount, finding.RecurringCount);
        Assert.Equal(review.ExpectedVerdict, Snake(finding.Verdict));
        Assert.Equal(review.ExpectedEligible, finding.CandidateEligibility == InstructionCandidateEligibilityV1.Eligible);
        Assert.DoesNotContain("\"candidates\":[{", System.Text.Encoding.UTF8.GetString(run.HandoffBytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ExactSupportedSubmissionWithNoGroundedSessionBecomesInsufficientWeak()
    {
        var provider = new RecordingProvider(request =>
        {
            var groups = request.Dataset.EvidenceGroups.ToArray();
            return Complete(request, groups, groups);
        });
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", turnCount: 1)],
            provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var review = ReviewCase("E");
        Assert.Equal(new[] { false }, review.PerSessionCategoryMinimumMet);
        Assert.Equal(0, review.GroundedSessionCount);
        var read = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        var support = Assert.Single(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(read.Receipt).Findings);
        Assert.Equal(HistoricalInstructionAnalysisStateV1.Succeeded, read.State);
        Assert.Equal(review.GroundedSessionCount, support.RecurringCount);
        Assert.Equal(review.ExpectedSupportKind!, Snake(support.SupportKind));
        Assert.Equal(review.ExpectedVerdict!, Snake(support.Verdict));
        Assert.Equal(review.ExpectedEligible,
            support.CandidateEligibility == InstructionCandidateEligibilityV1.Eligible);
        Assert.Single(support.SupportingSessionIds);
        Assert.Equal(
            fixture.Extraction.RawLocal.EvidenceGroups
                .Where(group => group.Kind == HistoricalEvidenceGroupKindV1.TurnRollup)
                .Select(group => group.GroupId),
            support.SupportingGroupIds);
        Assert.NotEmpty(support.EvidenceRefs);
        Assert.Equal(runId, HistoricalInstructionAnalysisReadConsumerV1.Validate(read));
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

        var run = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        Assert.Equal(HistoricalInstructionAnalysisStateV1.ZeroFindings, run.State);
        Assert.Empty(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(run.Receipt).Findings);
        Assert.Equal(runId, InstructionFindingHandoffConsumerV1.Validate(run.HandoffBytes));
        Assert.Equal(runId, HistoricalInstructionAnalysisReadConsumerV1.Validate(run));
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
        foreach (var (assessedVerdict, reviewId) in new[]
        {
            (InstructionFindingVerdictV1.Weak, "F"),
            (InstructionFindingVerdictV1.Incomplete, "G"),
        })
        {
            var provider = new RecordingProvider(request =>
            {
                var groups = request.Dataset.EvidenceGroups.ToArray();
                var complete = Complete(request, groups, groups);
                return complete with
                {
                    Findings = [complete.Findings[0] with { AssessedVerdict = assessedVerdict }],
                };
            });
            using var fixture = await AnalysisFixture.CreateAsync(
                [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)],
                provider);
            var runId = fixture.Service.Start(Request(fixture.Extraction));

            await fixture.Service.RunAsync(runId, CancellationToken.None);

            var support = Assert.Single(Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(fixture.Service.Get(runId)!.Receipt).Findings);
            var review = ReviewCase(reviewId);
            Assert.Equal(new[] { false }, review.PerSessionCategoryMinimumMet);
            Assert.Equal(review.GroundedSessionCount, support.RecurringCount);
            Assert.Equal(review.ExpectedSupportKind!, Snake(support.SupportKind));
            Assert.Equal(review.ExpectedVerdict!, Snake(support.Verdict));
            Assert.Equal(assessedVerdict, support.Verdict);
            Assert.Equal(review.ExpectedEligible,
                support.CandidateEligibility == InstructionCandidateEligibilityV1.Eligible);
            Assert.Single(support.SupportingSessionIds);
            Assert.NotEmpty(support.SupportingGroupIds);
            Assert.NotEmpty(support.EvidenceRefs);
        }
    }

    [Fact]
    public async Task RunAsync_UnmappedListedGroupCannotSupportAnOtherwiseResolvedReference()
    {
        var baseSession = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1);
        var session = baseSession with
        {
            Capabilities = baseSession.Capabilities with { TokenRollup = true },
        };
        var provider = new RecordingProvider(request =>
        {
            var group = request.Dataset.EvidenceGroups.Single(
                value => value.Kind == HistoricalEvidenceGroupKindV1.TokenRollup);
            return Complete(request, [group], [group]);
        });
        using var fixture = await AnalysisFixture.CreateAsync([session], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.InvalidCitation);
    }

    [Fact]
    public async Task RunAsync_ProviderMutationCannotChangePersistedEvidenceAuthority()
    {
        var provider = new MutatingEvidenceProvider();
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2)],
            provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        Assert.True(provider.MutationApplied);
        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.InvalidCitation);
        var reopened = Assert.IsType<HistoricalEvidenceExtractionV1>(
            fixture.ExtractionService.Get(fixture.Extraction.RawLocal.ExtractionId));
        Assert.Equal(fixture.Extraction.RawLocalBytes, reopened.RawLocalBytes);
        Assert.Equal(fixture.Extraction.RawLocalSha256, reopened.RawLocalSha256);
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
    public async Task RunAsync_OwnedTimeoutWinsWhenItTriggersCallerCancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)],
            new CancelCallerWhenProviderTokenCancels(callerCancellation));
        var runId = fixture.Service.Start(Request(fixture.Extraction, timeoutMilliseconds: 25));

        await fixture.Service.RunAsync(runId, callerCancellation.Token);

        Assert.True(callerCancellation.IsCancellationRequested);
        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.TimedOut);
    }

    [Fact]
    public async Task RunAsync_ProviderSelfCancellationBeforeDeadlineIsProviderFailure()
    {
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)],
            new SelfCancelingProvider());
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.ProviderFailed);
    }

    [Fact]
    public async Task RunAsync_ProviderSelfCancellationWinsBeforeLaterCallerCancellation()
    {
        var provider = new ControllableSelfCancelingProvider();
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)],
            provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        using var callerCancellation = new CancellationTokenSource();
        var running = fixture.Service.RunAsync(runId, callerCancellation.Token);
        await provider.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        provider.Cancel();
        callerCancellation.Cancel();
        await running;

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.ProviderFailed);
    }

    [Fact]
    public async Task RunAsync_InFlightCallerCancellationHasDistinctTerminalState()
    {
        var provider = new SignalingWaitingProvider();
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)],
            provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        using var cancellation = new CancellationTokenSource();

        var running = fixture.Service.RunAsync(runId, cancellation.Token);
        await provider.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await running;

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.Canceled);
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
    public async Task RunAsync_AbsentExtractionIsStaleAndRetainsUnavailableReadProjection()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        var request = Request(fixture.Extraction) with
        {
            ExtractionId = "historical-extraction-00000000000000000000000000000000",
        };
        var runId = fixture.Service.Start(request);

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var read = AssertTerminalWithoutReceipt(
            fixture.Service,
            runId,
            HistoricalInstructionAnalysisStateV1.StaleExtraction);
        Assert.False(read.DatasetProjection.ContentAvailable);
        Assert.Empty(read.DatasetProjection.DatasetDistribution.Completeness);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task RunAsync_CorruptStoredExtractionFailsClosedAsExtractionInvalid()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        fixture.Execute(
            "UPDATE historical_evidence_datasets SET payload_sha256='" + new string('0', 64)
            + "' WHERE extraction_id='" + fixture.Extraction.RawLocal.ExtractionId + "' AND representation='raw_local';");

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        AssertTerminalWithoutReceipt(fixture.Service, runId, HistoricalInstructionAnalysisStateV1.ExtractionInvalid);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task RunAsync_PreExistingCorruptExtractionStillCreatesReadableExtractionInvalidRun()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        fixture.Execute(
            "UPDATE historical_evidence_datasets SET payload_sha256='" + new string('0', 64)
            + "' WHERE extraction_id='" + fixture.Extraction.RawLocal.ExtractionId + "' AND representation='raw_local';");

        var runId = fixture.Service.Start(Request(fixture.Extraction));
        var queued = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        Assert.Equal(HistoricalInstructionAnalysisStateV1.Queued, queued.State);
        Assert.False(queued.DatasetProjection.ContentAvailable);
        Assert.Equal(runId, HistoricalInstructionAnalysisReadConsumerV1.Validate(queued));
        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var read = AssertTerminalWithoutReceipt(
            fixture.Service,
            runId,
            HistoricalInstructionAnalysisStateV1.ExtractionInvalid);
        Assert.False(read.DatasetProjection.ContentAvailable);
        Assert.Empty(read.DatasetProjection.DatasetDistribution.Completeness);
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

        var read = AssertTerminalWithoutReceipt(
            fixture.Service,
            runId,
            HistoricalInstructionAnalysisStateV1.ContentUnavailable);
        Assert.True(read.DatasetProjection.SanitizedOnly);
        Assert.False(read.DatasetProjection.ContentAvailable);
        Assert.NotEmpty(read.DatasetProjection.DatasetDistribution.Completeness);
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

        var read = AssertTerminalWithoutReceipt(
            fixture.Service,
            runId,
            HistoricalInstructionAnalysisStateV1.NoEligibleSessions);
        Assert.False(read.DatasetProjection.SanitizedOnly);
        Assert.True(read.DatasetProjection.ContentAvailable);
        Assert.Empty(read.DatasetProjection.DatasetDistribution.Completeness);
        Assert.Empty(read.DatasetProjection.DatasetDistribution.SourceKinds);
        Assert.Empty(read.DatasetProjection.DatasetDistribution.Capabilities);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task RunAsync_EmptySanitizedDatasetUsesContentUnavailablePrecedence()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync([], provider, sanitizedOnly: true);
        var runId = fixture.Service.Start(Request(fixture.Extraction));

        await fixture.Service.RunAsync(runId, CancellationToken.None);

        var read = AssertTerminalWithoutReceipt(
            fixture.Service,
            runId,
            HistoricalInstructionAnalysisStateV1.ContentUnavailable);
        Assert.True(read.DatasetProjection.SanitizedOnly);
        Assert.False(read.DatasetProjection.ContentAvailable);
        Assert.Empty(read.DatasetProjection.DatasetDistribution.Completeness);
        Assert.Empty(read.DatasetProjection.DatasetDistribution.SourceKinds);
        Assert.Empty(read.DatasetProjection.DatasetDistribution.Capabilities);
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

        var run = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(run.Receipt);
        var receiptBytes = HistoricalInstructionAnalysisJsonV1.Serialize(receipt);
        Assert.DoesNotContain(rawMarker, System.Text.Encoding.UTF8.GetString(receiptBytes), StringComparison.Ordinal);
        Assert.DoesNotContain(rawMarker, System.Text.Encoding.UTF8.GetString(run.HandoffBytes), StringComparison.Ordinal);
        var support = Assert.Single(receipt.Findings);
        Assert.Equal(
            fixture.Extraction.RawLocal.EvidenceGroups
                .Where(group => group.Kind == HistoricalEvidenceGroupKindV1.TurnRollup)
                .Select(group => group.GroupId).Order(StringComparer.Ordinal),
            support.SupportingGroupIds);
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

    [Fact]
    public async Task ReceiptValidation_RejectsCredentialCarrierInModelOrProvider()
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

        foreach (var value in new[]
        {
            "sk-abcdefghijklmnopqrstuvwxyz",
            "github_pat_abcdefghijklmnopqrstuvwxyz",
            "glpat-abcdefghijklmnopqrstuvwx",
            "ghp_abcdefghijklmnopqrstuvwx",
            "api-key",
        })
        {
            Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
                HistoricalInstructionAnalysisJsonV1.Serialize(receipt with { Model = value }));
            Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
                HistoricalInstructionAnalysisJsonV1.Serialize(receipt with { Provider = value }));
        }
    }

    [Fact]
    public async Task ReceiptValidation_RejectsSupportedEligibleWithoutRecurringSupport()
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
        var invalid = receipt with
        {
            Findings =
            [
                finding with
                {
                    Verdict = InstructionFindingVerdictV1.Supported,
                    CandidateEligibility = InstructionCandidateEligibilityV1.Eligible,
                },
            ],
        };

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(
            () => HistoricalInstructionAnalysisJsonV1.Serialize(invalid));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public async Task StoreRead_RejectsCanonicalOneSessionSupportedEligiblePair()
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
        var read = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(read.Receipt);
        var supportedHandoff = SupportedAcceptanceHandoff(runId, fixture.Extraction.RawLocal);
        var supportedHandoffBytes = InstructionFindingJsonV1.Serialize(supportedHandoff);
        var receiptJson = System.Text.Encoding.UTF8.GetString(HistoricalInstructionAnalysisJsonV1.Serialize(receipt))
            .Replace("\"verdict\":\"weak\"", "\"verdict\":\"supported\"", StringComparison.Ordinal)
            .Replace("\"candidate_eligibility\":\"ineligible\"", "\"candidate_eligibility\":\"eligible\"", StringComparison.Ordinal)
            .Replace(receipt.HandoffSha256, Sha256(supportedHandoffBytes), StringComparison.Ordinal);
        fixture.ReplaceSuccessfulCarriers(
            runId,
            System.Text.Encoding.UTF8.GetBytes(receiptJson),
            supportedHandoffBytes);

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() => fixture.Service.Get(runId));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public async Task ReadConsumer_RejectsFabricatedRecurringSupportWithoutEvidenceFromEverySession()
    {
        var first = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2);
        var second = Session(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0", 2);
        var provider = new RecordingProvider(request =>
        {
            var firstGroups = request.Dataset.EvidenceGroups
                .Where(group => group.SessionId == first.SessionId.ToString()).ToArray();
            return Complete(request, firstGroups, request.Dataset.EvidenceGroups.ToArray());
        });
        using var fixture = await AnalysisFixture.CreateAsync([first, second], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        await fixture.Service.RunAsync(runId, CancellationToken.None);
        var read = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(read.Receipt);
        var finding = Assert.Single(receipt.Findings);
        var firstSession = finding.SupportingSessionIds[0];
        var fabricated = read with
        {
            Receipt = receipt with
            {
                Findings =
                [
                    finding with
                    {
                        EvidenceRefs = finding.EvidenceRefs
                            .Where(reference => reference.SessionId == firstSession).ToArray(),
                    },
                ],
            },
        };

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
            HistoricalInstructionAnalysisReadConsumerV1.Validate(fabricated));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public async Task ReceiptValidation_RejectsZeroGroundedCountForMultipleSupportingSessions()
    {
        var first = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2);
        var second = Session(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0", 2);
        var provider = new RecordingProvider(request =>
        {
            var firstGroups = request.Dataset.EvidenceGroups
                .Where(group => group.SessionId == first.SessionId.ToString()).ToArray();
            return Complete(request, firstGroups, request.Dataset.EvidenceGroups.ToArray());
        });
        using var fixture = await AnalysisFixture.CreateAsync([first, second], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        await fixture.Service.RunAsync(runId, CancellationToken.None);
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(fixture.Service.Get(runId)!.Receipt);
        var finding = Assert.Single(receipt.Findings);
        Assert.Equal(2, finding.SupportingSessionIds.Count);
        var invalid = receipt with
        {
            Findings =
            [
                finding with
                {
                    Verdict = InstructionFindingVerdictV1.Weak,
                    CandidateEligibility = InstructionCandidateEligibilityV1.Ineligible,
                    SupportKind = HistoricalInstructionSupportKindV1.InsufficientSupport,
                    RecurringCount = 0,
                },
            ],
        };

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
            HistoricalInstructionAnalysisJsonV1.Serialize(invalid));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public async Task ReadConsumer_RejectsZeroGroundedCountForMultipleSupportingSessions()
    {
        var first = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2);
        var second = Session(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0", 2);
        var provider = new RecordingProvider(request =>
        {
            var firstGroups = request.Dataset.EvidenceGroups
                .Where(group => group.SessionId == first.SessionId.ToString()).ToArray();
            return Complete(request, firstGroups, request.Dataset.EvidenceGroups.ToArray());
        });
        using var fixture = await AnalysisFixture.CreateAsync([first, second], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        await fixture.Service.RunAsync(runId, CancellationToken.None);
        var read = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(read.Receipt);
        var finding = Assert.Single(receipt.Findings);
        Assert.Equal(2, finding.SupportingSessionIds.Count);
        var handoff = InstructionFindingJsonV1.Deserialize(read.HandoffBytes);
        var handoffFinding = Assert.Single(handoff.Findings);
        var fabricatedHandoffBytes = InstructionFindingJsonV1.Serialize(handoff with
        {
            Findings =
            [
                handoffFinding with
                {
                    Verdict = InstructionFindingVerdictV1.Weak,
                    CandidateEligibility = InstructionCandidateEligibilityV1.Ineligible,
                },
            ],
            Candidates = [],
        });
        var fabricated = read with
        {
            Receipt = receipt with
            {
                HandoffSha256 = Sha256(fabricatedHandoffBytes),
                Findings =
                [
                    finding with
                    {
                        Verdict = InstructionFindingVerdictV1.Weak,
                        CandidateEligibility = InstructionCandidateEligibilityV1.Ineligible,
                        SupportKind = HistoricalInstructionSupportKindV1.InsufficientSupport,
                        RecurringCount = 0,
                    },
                ],
            },
            HandoffBytes = fabricatedHandoffBytes,
        };

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
            HistoricalInstructionAnalysisReadConsumerV1.Validate(fabricated));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public async Task ReceiptValidation_RejectsFewerGroupsThanSupportingSessions()
    {
        var first = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2);
        var second = Session(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0", 2);
        var provider = new RecordingProvider(request =>
        {
            var firstGroups = request.Dataset.EvidenceGroups
                .Where(group => group.SessionId == first.SessionId.ToString()).ToArray();
            return Complete(request, firstGroups, request.Dataset.EvidenceGroups.ToArray());
        });
        using var fixture = await AnalysisFixture.CreateAsync([first, second], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        await fixture.Service.RunAsync(runId, CancellationToken.None);
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(fixture.Service.Get(runId)!.Receipt);
        var finding = Assert.Single(receipt.Findings);
        var invalid = receipt with
        {
            Findings = [finding with { SupportingGroupIds = [finding.SupportingGroupIds[0]] }],
        };

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
            HistoricalInstructionAnalysisJsonV1.Serialize(invalid));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public async Task ReceiptValidation_RejectsPartialGroundedCountForMultipleSupportingSessions()
    {
        var first = Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 2);
        var second = Session(2, SessionCompleteness.Partial, SessionSourceSurface.ClaudeCode, "2.0.0", 2);
        var provider = new RecordingProvider(request =>
        {
            var firstGroups = request.Dataset.EvidenceGroups
                .Where(group => group.SessionId == first.SessionId.ToString()).ToArray();
            return Complete(request, firstGroups, request.Dataset.EvidenceGroups.ToArray());
        });
        using var fixture = await AnalysisFixture.CreateAsync([first, second], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        await fixture.Service.RunAsync(runId, CancellationToken.None);
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(fixture.Service.Get(runId)!.Receipt);
        var finding = Assert.Single(receipt.Findings);
        var invalid = receipt with
        {
            Findings =
            [
                finding with
                {
                    Verdict = InstructionFindingVerdictV1.Weak,
                    CandidateEligibility = InstructionCandidateEligibilityV1.Ineligible,
                    SupportKind = HistoricalInstructionSupportKindV1.SingleSession,
                    RecurringCount = 1,
                },
            ],
        };

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
            HistoricalInstructionAnalysisJsonV1.Serialize(invalid));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public async Task StoreRead_RejectsUnavailableProjectionWithNonemptyDistribution()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        fixture.MutateDatasetProjection(runId, json =>
            json.Replace("\"content_available\":true", "\"content_available\":false", StringComparison.Ordinal));

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() => fixture.Service.Get(runId));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public async Task DatasetProjectionValidation_RejectsMismatchedTotalsAndOvercountedCapability()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        var projection = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId)).DatasetProjection;
        var mismatchedTotal = projection with
        {
            DatasetDistribution = projection.DatasetDistribution with
            {
                SourceKinds = [new HistoricalDistributionCountV1("live_otel", 2)],
            },
        };
        var overcountedCapability = projection with
        {
            DatasetDistribution = projection.DatasetDistribution with
            {
                Capabilities = [new HistoricalDistributionCountV1("turn_rollup", 2)],
            },
        };

        foreach (var invalid in new[] { mismatchedTotal, overcountedCapability })
        {
            var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
                HistoricalInstructionAnalysisJsonV1.SerializeDatasetProjection(invalid));
            Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
        }
    }

    [Fact]
    public async Task ReadConsumer_RejectsProviderStageStateWithEmptyDatasetDistribution()
    {
        var provider = new RecordingProvider(_ => new(
            HistoricalInstructionProviderCompletionV1.Complete, string.Empty, []));
        using var fixture = await AnalysisFixture.CreateAsync(
            [Session(1, SessionCompleteness.Full, SessionSourceSurface.CopilotSdk, "1.0.0", 1)], provider);
        var runId = fixture.Service.Start(Request(fixture.Extraction));
        await fixture.Service.RunAsync(runId, CancellationToken.None);
        var read = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(read.Receipt);
        var empty = new HistoricalEvidenceDistributionV1([], [], []);
        var invalid = read with
        {
            DatasetProjection = read.DatasetProjection with { DatasetDistribution = empty },
            Receipt = receipt with { DatasetDistribution = empty },
        };

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
            HistoricalInstructionAnalysisReadConsumerV1.Validate(invalid));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public async Task ReadConsumer_RejectsValidSameRunHandoffThatDoesNotMatchReceiptFindings()
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
        var read = Assert.IsType<HistoricalInstructionAnalysisReadV1>(fixture.Service.Get(runId));
        var receipt = Assert.IsType<HistoricalInstructionAnalysisReceiptV1>(read.Receipt);
        var emptyHandoff = InstructionFindingJsonV1.Serialize(new InstructionFindingHandoffV1(
            InstructionFindingContractsV1.HandoffSchemaVersion,
            runId,
            [],
            []));
        var mismatched = read with
        {
            Receipt = receipt with { HandoffSha256 = Sha256(emptyHandoff) },
            HandoffBytes = emptyHandoff,
        };

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
            HistoricalInstructionAnalysisReadConsumerV1.Validate(mismatched));

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

    private static HistoricalInstructionAnalysisReadV1 AssertTerminalWithoutReceipt(
        HistoricalInstructionAnalysisApplicationServiceV1 service,
        long runId,
        HistoricalInstructionAnalysisStateV1 expected)
    {
        var run = Assert.IsType<HistoricalInstructionAnalysisReadV1>(service.Get(runId));
        Assert.Equal(expected, run.State);
        Assert.Null(run.Receipt);
        Assert.Empty(run.HandoffBytes);
        Assert.Equal(runId, HistoricalInstructionAnalysisReadConsumerV1.Validate(run));
        Assert.NotNull(run.DatasetProjection.DatasetDistribution.Completeness);
        Assert.NotNull(run.DatasetProjection.DatasetDistribution.SourceKinds);
        return run;
    }

    private static InstructionFindingHandoffV1 SupportedAcceptanceHandoff(
        long runId,
        HistoricalEvidenceDatasetV1 dataset)
    {
        var groups = dataset.EvidenceGroups
            .Where(group => group.Kind == HistoricalEvidenceGroupKindV1.TurnRollup)
            .ToArray();
        var locations = groups.SelectMany(group => group.References.Select(reference =>
                new InstructionFindingEvidenceLocationV1(
                    reference.SessionId,
                    reference.TraceId,
                    reference.SpanId,
                    reference.TurnIndex,
                    (InstructionEvidenceRelativePositionV1)(int)reference.RelativePosition,
                    InstructionFindingEvidenceKindV1.Turn)))
            .ToArray();
        var index = new InstructionFindingEvidenceIndexV1("trace-1", locations);
        var references = groups.SelectMany(group => group.References)
            .Select(ToInstructionReference)
            .Distinct()
            .Order(InstructionRawEvidenceReferenceComparerV1.Instance)
            .ToArray();
        return InstructionFindingPipelineV1.Generate(
            runId,
            index,
            [
                new InstructionFindingDraftV1(
                    InstructionFindingCategoryV1.AcceptanceCriteriaMissing,
                    InstructionFindingVerdictV1.Supported,
                    InstructionFindingExtractorSourceV1.DeterministicPrepass,
                    references),
            ]);
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
            item.GetProperty("grounded_session_count").GetInt32(),
            item.GetProperty("per_session_category_minimum_met").EnumerateArray()
                .Select(value => value.GetBoolean()).ToArray(),
            item.GetProperty("expected_support_kind").GetString()!,
            item.GetProperty("expected_verdict").GetString()!,
            item.GetProperty("expected_candidate_eligible").GetBoolean());
    }

    private static string Snake<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            char.IsUpper(character) && index > 0 ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    private static HistoricalSessionMetadataV1 ContextSession(
        int number,
        SessionCompleteness completeness,
        SessionSourceSurface sourceSurface,
        string sourceVersion)
    {
        var session = Session(number, completeness, sourceSurface, sourceVersion, turnCount: 1);
        return session with
        {
            EvidenceLocations =
            [
                .. session.EvidenceLocations,
                new HistoricalEvidenceLocationV1(
                    session.SessionId,
                    $"context-{number}",
                    $"context-span-{number}",
                    1,
                    HistoricalEvidenceRelativePositionV1.Previous),
            ],
        };
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
                var groups = session.EvidenceLocations.Select(location =>
                    location.SpanId?.StartsWith("error-", StringComparison.Ordinal) == true
                        ? new HistoricalEvidenceGroupDraftV1(
                            HistoricalEvidenceGroupKindV1.ErrorSpan,
                            [new(location.SessionId, location.TraceId, location.SpanId, location.TurnIndex, location.RelativePosition)],
                            null, null, "error", null, null, null, null, null)
                        : new HistoricalEvidenceGroupDraftV1(
                            HistoricalEvidenceGroupKindV1.TurnRollup,
                            [new(location.SessionId, location.TraceId, location.SpanId, location.TurnIndex, location.RelativePosition)],
                            1, "turn", null, null, null, null, null, null)).ToList();
                if (session.Capabilities.TokenRollup)
                {
                    var location = session.EvidenceLocations[0];
                    groups.Add(new HistoricalEvidenceGroupDraftV1(
                        HistoricalEvidenceGroupKindV1.TokenRollup,
                        [new(location.SessionId, location.TraceId, location.SpanId, location.TurnIndex, location.RelativePosition)],
                        10, HistoricalEvidenceScalarUnitsV1.TotalToken, null, null, null, null, null, null));
                }
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

    private sealed class MutatingEvidenceProvider : IHistoricalInstructionAnalysisProviderV1
    {
        internal bool MutationApplied { get; private set; }

        public Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
            HistoricalInstructionProviderRequestV1 request,
            CancellationToken cancellationToken)
        {
            var groups = Assert.IsAssignableFrom<IList<HistoricalEvidenceGroupV1>>(request.Dataset.EvidenceGroups);
            var groupIndex = groups.ToList().FindIndex(group =>
                group.Kind == HistoricalEvidenceGroupKindV1.TurnRollup);
            var group = groups[groupIndex];
            var references = Assert.IsAssignableFrom<IList<HistoricalEvidenceReferenceV1>>(group.References);
            var mutatedReference = references[0] with { TurnIndex = 999 };
            references[0] = mutatedReference;
            groups[groupIndex] = group with { References = references.ToArray() };

            var sessions = Assert.IsAssignableFrom<IList<HistoricalEvidenceSessionV1>>(request.Dataset.Sessions);
            sessions[0] = sessions[0] with { SourceVersion = "provider-mutated" };
            request.CanonicalDatasetBytes[0] ^= 0xff;
            MutationApplied = true;

            return Task.FromResult(new HistoricalInstructionProviderResultV1(
                HistoricalInstructionProviderCompletionV1.Complete,
                "trace-1",
                [
                    new HistoricalInstructionFindingSubmissionV1(
                        InstructionFindingCategoryV1.AcceptanceCriteriaMissing,
                        InstructionFindingVerdictV1.Supported,
                        InstructionFindingExtractorSourceV1.DeterministicPrepass,
                        groups.SelectMany(value => value.References).Select(ToInstructionReference).ToArray(),
                        groups.Select(value => value.GroupId).ToArray()),
                ]));
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

    private sealed class SignalingWaitingProvider : IHistoricalInstructionAnalysisProviderV1
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => entered.Task;

        public async Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
            HistoricalInstructionProviderRequestV1 request,
            CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class SelfCancelingProvider : IHistoricalInstructionAnalysisProviderV1
    {
        public Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
            HistoricalInstructionProviderRequestV1 request,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<HistoricalInstructionProviderResultV1>(new CancellationToken(canceled: true));
    }

    private sealed class ControllableSelfCancelingProvider : IHistoricalInstructionAnalysisProviderV1
    {
        private readonly TaskCompletionSource<HistoricalInstructionProviderResultV1> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => entered.Task;

        internal void Cancel() => completion.TrySetCanceled();

        public Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
            HistoricalInstructionProviderRequestV1 request,
            CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            return completion.Task;
        }
    }

    private sealed class CancelCallerWhenProviderTokenCancels(CancellationTokenSource callerCancellation)
        : IHistoricalInstructionAnalysisProviderV1
    {
        public async Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
            HistoricalInstructionProviderRequestV1 request,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<HistoricalInstructionProviderResultV1>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() =>
            {
                callerCancellation.Cancel();
                completion.TrySetCanceled(cancellationToken);
            });
            return await completion.Task;
        }
    }

    private sealed class AnalysisFixture : IDisposable
    {
        private readonly MonitorTempDirectory temp;

        private AnalysisFixture(
            MonitorTempDirectory temp,
            HistoricalEvidenceExtractionV1 extraction,
            HistoricalEvidenceApplicationServiceV1 extractionService,
            HistoricalInstructionAnalysisApplicationServiceV1 service)
        {
            this.temp = temp;
            Extraction = extraction;
            ExtractionService = extractionService;
            Service = service;
        }

        internal HistoricalEvidenceExtractionV1 Extraction { get; }
        internal HistoricalEvidenceApplicationServiceV1 ExtractionService { get; }
        internal HistoricalInstructionAnalysisApplicationServiceV1 Service { get; }

        internal void Execute(string sql)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal void MutateDatasetProjection(long runId, Func<string, string> mutation)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
            connection.Open();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT dataset_projection_json FROM historical_instruction_analysis_runs WHERE run_id=$id;";
            read.Parameters.AddWithValue("$id", runId);
            var json = Assert.IsType<string>(read.ExecuteScalar());
            var mutated = mutation(json);
            Assert.NotEqual(json, mutated);
            var bytes = System.Text.Encoding.UTF8.GetBytes(mutated);
            using var update = connection.CreateCommand();
            update.CommandText =
                "UPDATE historical_instruction_analysis_runs SET dataset_projection_json=$json,dataset_projection_sha256=$sha WHERE run_id=$id;";
            update.Parameters.AddWithValue("$json", mutated);
            update.Parameters.AddWithValue("$sha", Sha256(bytes));
            update.Parameters.AddWithValue("$id", runId);
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        internal void ReplaceSuccessfulCarriers(long runId, byte[] receiptBytes, byte[] handoffBytes)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE historical_instruction_analysis_runs SET receipt_json=$receipt,receipt_sha256=$receipt_sha,"
                + "handoff_json=$handoff,handoff_sha256=$handoff_sha WHERE run_id=$id;";
            command.Parameters.AddWithValue("$receipt", System.Text.Encoding.UTF8.GetString(receiptBytes));
            command.Parameters.AddWithValue("$receipt_sha", Sha256(receiptBytes));
            command.Parameters.AddWithValue("$handoff", System.Text.Encoding.UTF8.GetString(handoffBytes));
            command.Parameters.AddWithValue("$handoff_sha", Sha256(handoffBytes));
            command.Parameters.AddWithValue("$id", runId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

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
                    extractionService,
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
        int GroundedSessionCount,
        IReadOnlyList<bool> PerSessionCategoryMinimumMet,
        string ExpectedSupportKind,
        string ExpectedVerdict,
        bool ExpectedEligible);
}
