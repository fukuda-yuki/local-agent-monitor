using System.Security.Cryptography;
using CopilotAgentObservability.InstructionFindings;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class HistoricalInstructionAnalysisApplicationServiceV1
{
    private readonly HistoricalEvidenceApplicationServiceV1 extractionService;
    private readonly SqliteHistoricalInstructionAnalysisStoreV1 store;
    private readonly IHistoricalInstructionAnalysisProviderV1 provider;
    private readonly TimeProvider timeProvider;

    internal HistoricalInstructionAnalysisApplicationServiceV1(
        HistoricalEvidenceApplicationServiceV1 extractionService,
        SqliteHistoricalInstructionAnalysisStoreV1 store,
        IHistoricalInstructionAnalysisProviderV1 provider,
        TimeProvider? timeProvider = null)
    {
        this.extractionService = extractionService ?? throw new ArgumentNullException(nameof(extractionService));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal long Start(HistoricalInstructionAnalysisRequestV1 request)
    {
        HistoricalInstructionAnalysisJsonV1.ValidateRequest(request);
        HistoricalEvidenceExtractionV1? extraction;
        try
        {
            extraction = extractionService.Get(request.ExtractionId);
        }
        catch (HistoricalEvidenceValidationException)
        {
            extraction = null;
        }
        var projection = extraction is null
            ? UnavailableDatasetProjection()
            : new HistoricalInstructionAnalysisDatasetProjectionV1(
                extraction.RepositorySafe.TruncatedBefore,
                extraction.RepositorySafe.Selection.SanitizedOnly,
                ContentAvailable: !extraction.RepositorySafe.Selection.SanitizedOnly,
                extraction.RepositorySafe.Distribution);
        return store.Start(request, projection, timeProvider.GetUtcNow());
    }

    internal HistoricalInstructionAnalysisReadV1? Get(long runId) => store.Get(runId)?.ToRead();

    internal async Task RunAsync(long runId, CancellationToken cancellationToken)
    {
        var run = store.Get(runId) ?? throw new HistoricalInstructionAnalysisValidationException(
            HistoricalInstructionAnalysisValidationCodeV1.InvalidContract);
        store.MarkRunning(runId, timeProvider.GetUtcNow());

        HistoricalEvidenceExtractionV1? extraction;
        try
        {
            extraction = extractionService.Get(run.Request.ExtractionId);
        }
        catch (HistoricalEvidenceValidationException)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.ExtractionInvalid);
            return;
        }

        if (extraction is null
            || extraction.RawLocal.ExtractionId != run.Request.ExtractionId
            || extraction.RawLocalSha256 != run.Request.ExtractionSha256)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.StaleExtraction);
            return;
        }

        if (!run.DatasetProjection.ContentAvailable && !run.DatasetProjection.SanitizedOnly)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.ExtractionInvalid);
            return;
        }

        if (extraction.RawLocal.Selection.SanitizedOnly)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.ContentUnavailable);
            return;
        }

        if (extraction.RawLocal.Sessions.Count == 0)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.NoEligibleSessions);
            return;
        }

        HistoricalInstructionProviderResultV1 providerResult;
        using var timeoutSource = new CancellationTokenSource();
        using var providerCancellation = new CancellationTokenSource();
        var cancellationCause = new CancellationCauseTracker();
        using var callerCancellationRegistration = cancellationToken.Register(
            () => cancellationCause.CancelProvider(
                ProviderCancellationCause.Caller,
                providerCancellation));
        using var timeoutCancellationRegistration = timeoutSource.Token.Register(
            () => cancellationCause.CancelProvider(
                ProviderCancellationCause.Timeout,
                providerCancellation));
        timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(run.Request.TimeoutMilliseconds));
        try
        {
            var request = new HistoricalInstructionProviderRequestV1(
                runId,
                run.Request,
                extraction.RawLocal,
                extraction.RawLocalBytes.ToArray(),
                HistoricalInstructionAnalysisPromptV1.Template);
            var providerTask = provider.AnalyzeAsync(request, providerCancellation.Token);
            cancellationCause.ObserveProviderCancellation(providerTask);
            providerResult = await providerTask
                .WaitAsync(providerCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationCause.Current == ProviderCancellationCause.Caller)
        {
            await cancellationCause.PropagationCompleted.ConfigureAwait(false);
            Complete(runId, HistoricalInstructionAnalysisStateV1.Canceled);
            return;
        }
        catch (OperationCanceledException) when (cancellationCause.Current == ProviderCancellationCause.Timeout)
        {
            await cancellationCause.PropagationCompleted.ConfigureAwait(false);
            Complete(runId, HistoricalInstructionAnalysisStateV1.TimedOut);
            return;
        }
        catch (Exception)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.ProviderFailed);
            return;
        }

        if (cancellationCause.Current == ProviderCancellationCause.Caller)
        {
            await cancellationCause.PropagationCompleted.ConfigureAwait(false);
            Complete(runId, HistoricalInstructionAnalysisStateV1.Canceled);
            return;
        }
        if (cancellationCause.Current == ProviderCancellationCause.Timeout)
        {
            await cancellationCause.PropagationCompleted.ConfigureAwait(false);
            Complete(runId, HistoricalInstructionAnalysisStateV1.TimedOut);
            return;
        }

        if (providerResult is null)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.ProviderFailed);
            return;
        }
        if (providerResult.Completion == HistoricalInstructionProviderCompletionV1.Partial)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.ProviderPartial);
            return;
        }
        if (providerResult.Completion != HistoricalInstructionProviderCompletionV1.Complete)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.ProviderFailed);
            return;
        }

        BuiltResult built;
        try
        {
            built = Build(run, extraction, providerResult);
        }
        catch (Exception exception) when (exception is HistoricalInstructionAnalysisValidationException
            or InstructionFindingValidationException
            or InstructionFindingHandoffConsumerValidationException)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.InvalidCitation);
            return;
        }
        store.Complete(runId, built.Receipt.State, built.Receipt, built.HandoffBytes, timeProvider.GetUtcNow());
    }

    private void Complete(long runId, HistoricalInstructionAnalysisStateV1 state) =>
        store.Complete(runId, state, null, null, timeProvider.GetUtcNow());

    private static BuiltResult Build(
        HistoricalInstructionAnalysisRunV1 run,
        HistoricalEvidenceExtractionV1 extraction,
        HistoricalInstructionProviderResultV1 providerResult)
    {
        if (providerResult.Findings is null || providerResult.Findings.Any(finding => finding is null)) throw InvalidCitation();
        if (providerResult.Findings.Count == 0)
        {
            var empty = new InstructionFindingHandoffV1(
                InstructionFindingContractsV1.HandoffSchemaVersion,
                run.RunId,
                [],
                []);
            var emptyBytes = InstructionFindingJsonV1.Serialize(empty);
            if (InstructionFindingHandoffConsumerV1.Validate(emptyBytes) != run.RunId) throw InvalidCitation();
            return BuildReceipt(run, empty, emptyBytes, []);
        }

        InstructionFindingContractValidationV1.ValidateRawId(providerResult.AnchorTraceId);
        var rawGroups = extraction.RawLocal.EvidenceGroups.ToDictionary(group => group.GroupId, StringComparer.Ordinal);
        var safeGroups = extraction.RepositorySafe.EvidenceGroups.ToDictionary(group => group.GroupId, StringComparer.Ordinal);
        if (rawGroups.Count != safeGroups.Count || rawGroups.Keys.Any(key => !safeGroups.ContainsKey(key))) throw InvalidCitation();
        var sessionPairs = PairSessions(extraction);

        var locations = extraction.RawLocal.EvidenceGroups
            .SelectMany(group => EvidenceKind(group.Kind) is { } kind
                ? group.References
                    .Where(reference => reference.TraceId == providerResult.AnchorTraceId
                        && reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Anchor)
                    .Select(reference => new InstructionFindingEvidenceLocationV1(
                        reference.SessionId,
                        reference.TraceId,
                        reference.SpanId,
                        reference.TurnIndex,
                        InstructionEvidenceRelativePositionV1.Anchor,
                        kind))
                : [])
            .Distinct()
            .ToArray();
        var evidenceIndex = new InstructionFindingEvidenceIndexV1(providerResult.AnchorTraceId, locations);

        var validated = new List<ValidatedSubmission>();
        foreach (var submission in providerResult.Findings)
        {
            if (!Enum.IsDefined(submission.Category)
                || !Enum.IsDefined(submission.AssessedVerdict)
                || !Enum.IsDefined(submission.ExtractorSource)
                || submission.EvidenceRefs is null
                || submission.EvidenceRefs.Count == 0
                || submission.EvidenceRefs.Any(reference => reference is null)
                || submission.SupportingGroupIds is null
                || submission.SupportingGroupIds.Count == 0
                || submission.SupportingGroupIds.Any(string.IsNullOrEmpty))
                throw InvalidCitation();

            var supportGroupIds = submission.SupportingGroupIds
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (supportGroupIds.Any(groupId => !rawGroups.ContainsKey(groupId))) throw InvalidCitation();
            var supportGroups = supportGroupIds.Select(groupId => rawGroups[groupId]).ToArray();
            var supportReferences = supportGroups.SelectMany(group => group.References).Select(ToInstructionReference).ToHashSet();
            var references = submission.EvidenceRefs.Distinct().Order(InstructionRawEvidenceReferenceComparerV1.Instance).ToArray();
            foreach (var reference in references)
            {
                if (reference.TraceId != providerResult.AnchorTraceId
                    || reference.RelativePosition != InstructionEvidenceRelativePositionV1.Anchor
                    || !supportReferences.Contains(reference)
                    || !evidenceIndex.TryResolve(reference, out _))
                    throw InvalidCitation();
            }

            if (supportGroups.Select(group => group.SessionId).Distinct(StringComparer.Ordinal)
                .Any(sessionId => !sessionPairs.ContainsKey(sessionId))) throw InvalidCitation();
            var draft = new InstructionFindingDraftV1(
                submission.Category,
                submission.AssessedVerdict,
                submission.ExtractorSource,
                references);
            var identityHandoff = InstructionFindingPipelineV1.Generate(run.RunId, evidenceIndex, [draft]);
            var identity = AssertSingleFinding(identityHandoff);
            validated.Add(new(identity.FindingId, draft, supportGroupIds));
        }

        var assessed = validated
            .GroupBy(value => value.FindingId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                if (group.Any(value => value.Draft.Category != first.Draft.Category
                    || value.Draft.ExtractorSource != first.Draft.ExtractorSource
                    || !value.Draft.EvidenceRefs.SequenceEqual(first.Draft.EvidenceRefs)))
                    throw InvalidCitation();
                var submittedGroupIds = group.SelectMany(value => value.SupportingGroupIds)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var grounding = GroundSupportingSessions(
                    run.RunId,
                    first.Draft.Category,
                    first.Draft.ExtractorSource,
                    submittedGroupIds.Select(groupId => rawGroups[groupId]).ToArray());
                if (grounding.Count == 0) throw InvalidCitation();
                var assessedVerdict = group.Select(value => value.Draft.AssessedVerdict)
                    .Aggregate(LeastStrong);
                var verdict = assessedVerdict == InstructionFindingVerdictV1.Supported && grounding.Count < 2
                    ? InstructionFindingVerdictV1.Weak
                    : assessedVerdict;
                return new AssessedSubmission(
                    first.FindingId,
                    first.Draft with { AssessedVerdict = verdict },
                    grounding.SelectMany(value => value.GroupIds)
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            })
            .OrderBy(value => value.FindingId, StringComparer.Ordinal)
            .ToArray();

        var handoff = InstructionFindingPipelineV1.Generate(run.RunId, evidenceIndex, assessed.Select(value => value.Draft).ToArray());
        var handoffBytes = InstructionFindingJsonV1.Serialize(handoff);
        if (InstructionFindingHandoffConsumerV1.Validate(handoffBytes) != run.RunId) throw InvalidCitation();

        var supports = handoff.Findings.Select(finding =>
        {
            var matches = assessed.Where(value => value.FindingId == finding.FindingId).ToArray();
            if (matches.Length == 0) throw InvalidCitation();
            var groupIds = matches.SelectMany(value => value.SupportingGroupIds)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var rawSupportGroups = groupIds.Select(groupId => rawGroups[groupId]).ToArray();
            var rawSessionIds = rawSupportGroups.Select(group => group.SessionId)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var safeSessions = rawSessionIds.Select(sessionId => sessionPairs[sessionId]).ToArray();
            var evidenceRefs = groupIds.Select(groupId => safeGroups[groupId]).SelectMany(group => group.References)
                .Distinct().Order(HistoricalEvidenceReferenceComparerV1.Instance).ToArray();
            return new HistoricalInstructionFindingSupportV1(
                finding.FindingId,
                finding.Verdict,
                finding.CandidateEligibility,
                rawSessionIds.Length >= 2 ? HistoricalInstructionSupportKindV1.Recurring : HistoricalInstructionSupportKindV1.SingleSession,
                safeSessions.Select(session => session.SessionId).Order(StringComparer.Ordinal).ToArray(),
                groupIds,
                rawSessionIds.Length,
                Distribution(safeSessions.Select(session => SessionWire.ToWire(session.SourceSurface))),
                Distribution(safeSessions.Select(session => session.SourceVersion ?? "unavailable")),
                Distribution(safeSessions.Select(session => Snake(session.SourceKind))),
                Distribution(safeSessions.Select(session => Snake(session.Completeness))),
                evidenceRefs);
        }).ToArray();

        return BuildReceipt(run, handoff, handoffBytes, supports);
    }

    private static BuiltResult BuildReceipt(
        HistoricalInstructionAnalysisRunV1 run,
        InstructionFindingHandoffV1 handoff,
        byte[] handoffBytes,
        IReadOnlyList<HistoricalInstructionFindingSupportV1> supports)
    {
        var state = handoff.Findings.Count == 0
            ? HistoricalInstructionAnalysisStateV1.ZeroFindings
            : HistoricalInstructionAnalysisStateV1.Succeeded;
        var receipt = new HistoricalInstructionAnalysisReceiptV1(
            HistoricalInstructionAnalysisContractsV1.ReceiptSchemaVersion,
            run.RunId,
            run.Request.ExtractionId,
            run.Request.ExtractionSha256,
            state,
            run.Request.Model,
            run.Request.Provider,
            run.Request.ConfigurationSha256,
            run.Request.TimeoutMilliseconds,
            run.Request.PromptTemplateVersion,
            run.DatasetProjection.TruncatedBefore,
            run.DatasetProjection.SanitizedOnly,
            run.DatasetProjection.ContentAvailable,
            run.DatasetProjection.DatasetDistribution,
            Sha256(handoffBytes),
            supports);
        _ = HistoricalInstructionAnalysisJsonV1.Serialize(receipt);
        return new(receipt, handoffBytes);
    }

    private static Dictionary<string, HistoricalEvidenceSessionV1> PairSessions(HistoricalEvidenceExtractionV1 extraction)
    {
        if (extraction.RawLocal.Sessions.Count != extraction.RepositorySafe.Sessions.Count) throw InvalidCitation();
        var result = new Dictionary<string, HistoricalEvidenceSessionV1>(StringComparer.Ordinal);
        for (var index = 0; index < extraction.RawLocal.Sessions.Count; index++)
        {
            var raw = extraction.RawLocal.Sessions[index];
            var safe = extraction.RepositorySafe.Sessions[index];
            if (!Guid.TryParseExact(raw.SessionId, "D", out _)
                || !InstructionFindingReferenceTokenizationV1.IsSessionReference(safe.SessionId)
                || !result.TryAdd(raw.SessionId, safe))
                throw InvalidCitation();
        }
        return result;
    }

    private static IReadOnlyList<GroundedSession> GroundSupportingSessions(
        long runId,
        InstructionFindingCategoryV1 category,
        InstructionFindingExtractorSourceV1 extractorSource,
        IReadOnlyList<HistoricalEvidenceGroupV1> supportGroups)
    {
        var grounded = new List<GroundedSession>();
        foreach (var sessionGroup in supportGroups.GroupBy(group => group.SessionId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var mappedGroups = sessionGroup.Where(group => EvidenceKind(group.Kind) is not null).ToArray();
            var candidateAnchors = mappedGroups.SelectMany(group => group.References)
                .Where(reference => reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Anchor)
                .Select(reference => reference.TraceId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            foreach (var candidateAnchor in candidateAnchors)
            {
                var contributingGroups = mappedGroups
                    .Where(group => group.References.Any(reference =>
                        (reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Anchor)
                            == string.Equals(reference.TraceId, candidateAnchor, StringComparison.Ordinal)))
                    .OrderBy(group => group.GroupId, StringComparer.Ordinal)
                    .ToArray();
                if (!IsSupportedSessionGrounding(
                    runId,
                    category,
                    extractorSource,
                    candidateAnchor,
                    contributingGroups)) continue;
                var minimalGroups = contributingGroups.ToList();
                foreach (var removable in contributingGroups)
                {
                    var without = minimalGroups.Where(group => group.GroupId != removable.GroupId).ToArray();
                    if (IsSupportedSessionGrounding(
                        runId,
                        category,
                        extractorSource,
                        candidateAnchor,
                        without)) minimalGroups = [.. without];
                }
                grounded.Add(new GroundedSession(
                    minimalGroups.Select(group => group.GroupId).ToArray()));
                break;
            }
        }
        return grounded;
    }

    private static bool IsSupportedSessionGrounding(
        long runId,
        InstructionFindingCategoryV1 category,
        InstructionFindingExtractorSourceV1 extractorSource,
        string candidateAnchor,
        IReadOnlyList<HistoricalEvidenceGroupV1> groups)
    {
        var locations = groups.SelectMany(group =>
            group.References
                .Where(reference =>
                    (reference.RelativePosition == HistoricalEvidenceRelativePositionV1.Anchor)
                        == string.Equals(reference.TraceId, candidateAnchor, StringComparison.Ordinal))
                .Select(reference => new InstructionFindingEvidenceLocationV1(
                    reference.SessionId,
                    reference.TraceId,
                    reference.SpanId,
                    reference.TurnIndex,
                    (InstructionEvidenceRelativePositionV1)(int)reference.RelativePosition,
                    EvidenceKind(group.Kind)!.Value)))
            .Distinct()
            .ToArray();
        if (locations.Length == 0) return false;
        try
        {
            var index = new InstructionFindingEvidenceIndexV1(candidateAnchor, locations);
            var references = locations.Select(location => location.ToReference())
                .Distinct().Order(InstructionRawEvidenceReferenceComparerV1.Instance).ToArray();
            var handoff = InstructionFindingPipelineV1.Generate(
                runId,
                index,
                [new InstructionFindingDraftV1(
                    category,
                    InstructionFindingVerdictV1.Supported,
                    extractorSource,
                    references)]);
            return AssertSingleFinding(handoff).Verdict == InstructionFindingVerdictV1.Supported;
        }
        catch (InstructionFindingValidationException)
        {
            return false;
        }
    }

    private static InstructionFindingReceiptV1 AssertSingleFinding(InstructionFindingHandoffV1 handoff) =>
        handoff.Findings.Count == 1 ? handoff.Findings[0] : throw InvalidCitation();

    private static InstructionRawEvidenceReferenceV1 ToInstructionReference(HistoricalEvidenceReferenceV1 reference) =>
        new(
            reference.SessionId,
            reference.TraceId,
            reference.SpanId,
            reference.TurnIndex,
            (InstructionEvidenceRelativePositionV1)(int)reference.RelativePosition);

    private static InstructionFindingEvidenceKindV1? EvidenceKind(HistoricalEvidenceGroupKindV1 kind) => kind switch
    {
        HistoricalEvidenceGroupKindV1.TurnRollup => InstructionFindingEvidenceKindV1.Turn,
        HistoricalEvidenceGroupKindV1.ErrorSpan or HistoricalEvidenceGroupKindV1.RetryChain => InstructionFindingEvidenceKindV1.ErrorOrRetrySpan,
        HistoricalEvidenceGroupKindV1.UserCorrection => InstructionFindingEvidenceKindV1.InstructionSpan,
        _ => null,
    };

    private static IReadOnlyList<HistoricalDistributionCountV1> Distribution(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new HistoricalDistributionCountV1(group.Key, group.Count()))
            .ToArray();

    private static InstructionFindingVerdictV1 LeastStrong(
        InstructionFindingVerdictV1 left,
        InstructionFindingVerdictV1 right) =>
        Rank(left) <= Rank(right) ? left : right;

    private static int Rank(InstructionFindingVerdictV1 verdict) => verdict switch
    {
        InstructionFindingVerdictV1.Incomplete => 0,
        InstructionFindingVerdictV1.Weak => 1,
        InstructionFindingVerdictV1.Supported => 2,
        _ => throw InvalidCitation(),
    };

    private static HistoricalInstructionAnalysisDatasetProjectionV1 UnavailableDatasetProjection() =>
        new(
            TruncatedBefore: false,
            SanitizedOnly: false,
            ContentAvailable: false,
            new HistoricalEvidenceDistributionV1([], [], []));

    private static string Snake<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            char.IsUpper(character) && index > 0 ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HistoricalInstructionAnalysisValidationException InvalidCitation() =>
        new(HistoricalInstructionAnalysisValidationCodeV1.InvalidCitation);

    private sealed record ValidatedSubmission(string FindingId, InstructionFindingDraftV1 Draft, IReadOnlyList<string> SupportingGroupIds);
    private sealed record AssessedSubmission(string FindingId, InstructionFindingDraftV1 Draft, IReadOnlyList<string> SupportingGroupIds);
    private sealed record GroundedSession(IReadOnlyList<string> GroupIds);
    private sealed record BuiltResult(HistoricalInstructionAnalysisReceiptV1 Receipt, byte[] HandoffBytes);

    private enum ProviderCancellationCause { None, Provider, Timeout, Caller }

    private sealed class CancellationCauseTracker
    {
        private int cause;
        private Task? observedProviderTask;
        private readonly TaskCompletionSource propagationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ProviderCancellationCause Current =>
            (ProviderCancellationCause)Volatile.Read(ref cause);

        internal Task PropagationCompleted => propagationCompleted.Task;

        internal void ObserveProviderCancellation(Task providerTask)
        {
            Volatile.Write(ref observedProviderTask, providerTask);
            if (providerTask.IsCanceled) MarkProviderCancellation();
            _ = providerTask.ContinueWith(
                static (task, state) =>
                {
                    if (task.IsCanceled) ((CancellationCauseTracker)state!).MarkProviderCancellation();
                },
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal void CancelProvider(
            ProviderCancellationCause candidate,
            CancellationTokenSource providerCancellation)
        {
            if (Volatile.Read(ref observedProviderTask)?.IsCanceled == true)
            {
                MarkProviderCancellation();
                return;
            }
            if (Interlocked.CompareExchange(
                    ref cause,
                    (int)candidate,
                    (int)ProviderCancellationCause.None) == (int)ProviderCancellationCause.None)
            {
                try
                {
                    providerCancellation.Cancel();
                }
                finally
                {
                    propagationCompleted.TrySetResult();
                }
            }
        }

        private void MarkProviderCancellation() =>
            Interlocked.CompareExchange(
                ref cause,
                (int)ProviderCancellationCause.Provider,
                (int)ProviderCancellationCause.None);
    }
}

internal sealed class HistoricalEvidenceReferenceComparerV1 : IComparer<HistoricalEvidenceReferenceV1>
{
    internal static HistoricalEvidenceReferenceComparerV1 Instance { get; } = new();

    public int Compare(HistoricalEvidenceReferenceV1? left, HistoricalEvidenceReferenceV1? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var result = StringComparer.Ordinal.Compare(left.SessionId, right.SessionId);
        if (result != 0) return result;
        result = StringComparer.Ordinal.Compare(left.TraceId, right.TraceId);
        if (result != 0) return result;
        result = CompareNullable(left.SpanId, right.SpanId);
        if (result != 0) return result;
        result = Nullable.Compare(left.TurnIndex, right.TurnIndex);
        return result != 0 ? result : left.RelativePosition.CompareTo(right.RelativePosition);
    }

    private static int CompareNullable(string? left, string? right)
    {
        if (left is null) return right is null ? 0 : -1;
        return right is null ? 1 : StringComparer.Ordinal.Compare(left, right);
    }
}
