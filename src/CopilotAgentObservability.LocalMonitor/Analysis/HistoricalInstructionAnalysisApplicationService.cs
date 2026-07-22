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

    internal long Start(HistoricalInstructionAnalysisRequestV1 request) =>
        store.Start(request, timeProvider.GetUtcNow());

    internal HistoricalInstructionAnalysisRunV1? Get(long runId) => store.Get(runId);

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
            Complete(runId, HistoricalInstructionAnalysisStateV1.StaleExtraction);
            return;
        }

        if (extraction is null
            || extraction.RawLocal.ExtractionId != run.Request.ExtractionId
            || extraction.RawLocalSha256 != run.Request.ExtractionSha256)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.StaleExtraction);
            return;
        }

        if (extraction.RawLocal.Sessions.Count == 0)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.NoEligibleSessions);
            return;
        }

        if (extraction.RawLocal.Selection.SanitizedOnly)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.ContentUnavailable);
            return;
        }

        HistoricalInstructionProviderResultV1 providerResult;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(run.Request.TimeoutMilliseconds));
        try
        {
            var request = new HistoricalInstructionProviderRequestV1(
                runId,
                run.Request,
                extraction.RawLocal,
                extraction.RawLocalBytes.ToArray(),
                HistoricalInstructionAnalysisPromptV1.Template);
            providerResult = await provider.AnalyzeAsync(request, timeoutSource.Token)
                .WaitAsync(TimeSpan.FromMilliseconds(run.Request.TimeoutMilliseconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.TimedOut);
            return;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.TimedOut);
            return;
        }
        catch (OperationCanceledException)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.ProviderFailed);
            return;
        }
        catch (Exception)
        {
            Complete(runId, HistoricalInstructionAnalysisStateV1.ProviderFailed);
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
            return BuildReceipt(run, extraction, empty, emptyBytes, []);
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

        var assessed = new List<AssessedSubmission>();
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

            var rawSessionIds = supportGroups.Select(group => group.SessionId).Distinct(StringComparer.Ordinal).ToArray();
            if (rawSessionIds.Any(sessionId => !sessionPairs.ContainsKey(sessionId))) throw InvalidCitation();
            var verdict = submission.AssessedVerdict == InstructionFindingVerdictV1.Supported && rawSessionIds.Length < 2
                ? InstructionFindingVerdictV1.Weak
                : submission.AssessedVerdict;
            var draft = new InstructionFindingDraftV1(submission.Category, verdict, submission.ExtractorSource, references);
            var identityHandoff = InstructionFindingPipelineV1.Generate(run.RunId, evidenceIndex, [draft]);
            var identity = AssertSingleFinding(identityHandoff);
            assessed.Add(new(identity.FindingId, draft, supportGroupIds));
        }

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

        return BuildReceipt(run, extraction, handoff, handoffBytes, supports);
    }

    private static BuiltResult BuildReceipt(
        HistoricalInstructionAnalysisRunV1 run,
        HistoricalEvidenceExtractionV1 extraction,
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
            extraction.RepositorySafe.TruncatedBefore,
            extraction.RepositorySafe.Selection.SanitizedOnly,
            ContentAvailable: true,
            extraction.RepositorySafe.Distribution,
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

    private static string Snake<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            char.IsUpper(character) && index > 0 ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HistoricalInstructionAnalysisValidationException InvalidCitation() =>
        new(HistoricalInstructionAnalysisValidationCodeV1.InvalidCitation);

    private sealed record AssessedSubmission(string FindingId, InstructionFindingDraftV1 Draft, IReadOnlyList<string> SupportingGroupIds);
    private sealed record BuiltResult(HistoricalInstructionAnalysisReceiptV1 Receipt, byte[] HandoffBytes);
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
