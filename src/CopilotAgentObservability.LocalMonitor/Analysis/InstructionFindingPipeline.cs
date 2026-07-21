using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class InstructionFindingPipelineV1
{
    private const string FindingDomain = "copilot-agent-observability/instruction-finding/v1";
    private const string DeduplicationDomain = "copilot-agent-observability/instruction-rule-dedup/v1";
    private const string CandidateDomain = "copilot-agent-observability/instruction-rule/v1";

    internal static InstructionFindingHandoffV1 Generate(
        long analysisRunId,
        InstructionFindingEvidenceIndexV1 evidenceIndex,
        IReadOnlyList<InstructionFindingDraftV1> drafts)
    {
        if (analysisRunId <= 0 || drafts is null)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);

        var safeAnchorTraceId = InstructionFindingReferenceTokenizationV1.TokenizeTrace(evidenceIndex.AnchorTraceId);
        var pendingById = new Dictionary<string, PendingFinding>(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            if (draft is null
                || !Enum.IsDefined(draft.Category)
                || !Enum.IsDefined(draft.AssessedVerdict)
                || !Enum.IsDefined(draft.ExtractorSource)
                || draft.EvidenceRefs is null
                || draft.EvidenceRefs.Count == 0
                || draft.EvidenceRefs.Any(reference => reference is null))
                throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);

            var references = draft.EvidenceRefs
                .Distinct()
                .Order(InstructionRawEvidenceReferenceComparerV1.Instance)
                .ToArray();
            var resolvedKinds = new Dictionary<InstructionRawEvidenceReferenceV1, IReadOnlySet<InstructionFindingEvidenceKindV1>>();
            foreach (var reference in references)
            {
                InstructionFindingContractValidationV1.ValidateRawReference(evidenceIndex.AnchorTraceId, reference);
                if (!evidenceIndex.TryResolve(reference, out var kinds))
                    throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.UnresolvedEvidenceReference);
                resolvedKinds.Add(reference, kinds);
            }

            var safeReferences = references
                .Select(InstructionFindingReferenceTokenizationV1.Tokenize)
                .Distinct()
                .Order(InstructionEvidenceReferenceComparerV1.Instance)
                .ToArray();
            var finalVerdict = DetermineVerdict(draft, references, resolvedKinds, evidenceIndex.AnchorTraceId);
            var findingId = CreateFindingId(analysisRunId, draft.Category, draft.ExtractorSource, safeReferences);
            var pending = new PendingFinding(
                findingId,
                draft.Category,
                draft.ExtractorSource,
                safeReferences,
                finalVerdict);

            if (pendingById.TryGetValue(findingId, out var existing))
            {
                if (!existing.HasSameIdentity(pending))
                    throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidDerivedIdentity);
                pendingById[findingId] = existing with { Verdict = LeastStrong(existing.Verdict, pending.Verdict) };
            }
            else
            {
                pendingById.Add(findingId, pending);
            }
        }

        var findings = pendingById.Values
            .OrderBy(finding => finding.FindingId, StringComparer.Ordinal)
            .Select(finding => CreateReceipt(analysisRunId, safeAnchorTraceId, finding))
            .ToArray();
        var handoff = new InstructionFindingHandoffV1(
            InstructionFindingContractsV1.HandoffSchemaVersion,
            analysisRunId,
            findings,
            BuildCandidates(analysisRunId, findings));
        ValidateHandoff(handoff);
        return handoff;
    }

    internal static void ValidateHandoff(InstructionFindingHandoffV1 handoff)
    {
        if (handoff is null
            || !string.Equals(handoff.SchemaVersion, InstructionFindingContractsV1.HandoffSchemaVersion, StringComparison.Ordinal)
            || handoff.AnalysisRunId <= 0
            || handoff.Findings is null
            || handoff.Candidates is null)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);

        foreach (var finding in handoff.Findings)
        {
            ValidateFinding(handoff.AnalysisRunId, finding);
        }

        var orderedFindingIds = handoff.Findings.Select(finding => finding.FindingId).Order(StringComparer.Ordinal).ToArray();
        if (!orderedFindingIds.SequenceEqual(handoff.Findings.Select(finding => finding.FindingId), StringComparer.Ordinal)
            || orderedFindingIds.Distinct(StringComparer.Ordinal).Count() != orderedFindingIds.Length)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);

        if (handoff.Candidates.Any(candidate => candidate is null))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);

        var orderedCandidateIds = handoff.Candidates.Select(candidate => candidate.CandidateId).Order(StringComparer.Ordinal).ToArray();
        if (!orderedCandidateIds.SequenceEqual(handoff.Candidates.Select(candidate => candidate.CandidateId), StringComparer.Ordinal)
            || orderedCandidateIds.Distinct(StringComparer.Ordinal).Count() != orderedCandidateIds.Length)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);

        var expectedCandidates = BuildCandidates(handoff.AnalysisRunId, handoff.Findings);
        if (expectedCandidates.Count != handoff.Candidates.Count)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidDerivedIdentity);
        for (var index = 0; index < expectedCandidates.Count; index++)
        {
            if (!CandidateEquals(expectedCandidates[index], handoff.Candidates[index]))
                throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidDerivedIdentity);
        }
    }

    private static void ValidateFinding(long analysisRunId, InstructionFindingReceiptV1? finding)
    {
        if (finding is null
            || !string.Equals(finding.SchemaVersion, InstructionFindingContractsV1.FindingSchemaVersion, StringComparison.Ordinal)
            || finding.AnalysisRunId != analysisRunId
            || !Enum.IsDefined(finding.Category)
            || !Enum.IsDefined(finding.Verdict)
            || !Enum.IsDefined(finding.ExtractorSource)
            || finding.EvidenceRefs is null
            || finding.EvidenceRefs.Count == 0
            || finding.EvidenceRefs.Any(reference => reference is null)
            || finding.EvidenceQuoteState != InstructionEvidenceQuoteStateV1.RawLocalOnly
            || finding.CandidateEligibility != (finding.Verdict == InstructionFindingVerdictV1.Supported
                ? InstructionCandidateEligibilityV1.Eligible
                : InstructionCandidateEligibilityV1.Ineligible))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);

        if (!InstructionFindingReferenceTokenizationV1.IsTraceReference(finding.AnchorTraceId))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
        var canonicalReferences = finding.EvidenceRefs.Distinct().Order(InstructionEvidenceReferenceComparerV1.Instance).ToArray();
        if (!canonicalReferences.SequenceEqual(finding.EvidenceRefs))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
        foreach (var reference in finding.EvidenceRefs)
        {
            InstructionFindingContractValidationV1.ValidateSafeReference(finding.AnchorTraceId, reference);
        }

        var template = InstructionFindingTemplateCatalogV1.Get(finding.Category);
        if (!string.Equals(finding.GapSummary, template.GapSummary, StringComparison.Ordinal)
            || !string.Equals(finding.SuggestedInstruction, template.RuleText, StringComparison.Ordinal))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);

        var expectedId = CreateFindingId(analysisRunId, finding.Category, finding.ExtractorSource, finding.EvidenceRefs);
        if (!string.Equals(expectedId, finding.FindingId, StringComparison.Ordinal))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidDerivedIdentity);
    }

    private static InstructionFindingReceiptV1 CreateReceipt(
        long analysisRunId,
        string anchorTraceId,
        PendingFinding finding)
    {
        var template = InstructionFindingTemplateCatalogV1.Get(finding.Category);
        return new InstructionFindingReceiptV1(
            InstructionFindingContractsV1.FindingSchemaVersion,
            finding.FindingId,
            analysisRunId,
            finding.Category,
            finding.Verdict,
            finding.ExtractorSource,
            anchorTraceId,
            finding.EvidenceRefs,
            InstructionEvidenceQuoteStateV1.RawLocalOnly,
            template.GapSummary,
            template.RuleText,
            finding.Verdict == InstructionFindingVerdictV1.Supported
                ? InstructionCandidateEligibilityV1.Eligible
                : InstructionCandidateEligibilityV1.Ineligible);
    }

    private static InstructionFindingVerdictV1 DetermineVerdict(
        InstructionFindingDraftV1 draft,
        IReadOnlyList<InstructionRawEvidenceReferenceV1> references,
        IReadOnlyDictionary<InstructionRawEvidenceReferenceV1, IReadOnlySet<InstructionFindingEvidenceKindV1>> kindsByReference,
        string anchorTraceId)
    {
        if (draft.AssessedVerdict != InstructionFindingVerdictV1.Supported)
            return draft.AssessedVerdict;

        var anchorTurnReferences = references
            .Where(reference => string.Equals(reference.TraceId, anchorTraceId, StringComparison.Ordinal)
                && kindsByReference[reference].Contains(InstructionFindingEvidenceKindV1.Turn))
            .Select(reference => (reference.TraceId, reference.TurnIndex))
            .Distinct()
            .Count();
        var hasAnchorErrorOrRetry = references.Any(reference =>
            string.Equals(reference.TraceId, anchorTraceId, StringComparison.Ordinal)
            && kindsByReference[reference].Contains(InstructionFindingEvidenceKindV1.ErrorOrRetrySpan));
        var hasAnchorInstruction = references.Any(reference =>
            string.Equals(reference.TraceId, anchorTraceId, StringComparison.Ordinal)
            && kindsByReference[reference].Contains(InstructionFindingEvidenceKindV1.InstructionSpan));
        var distinctTraces = references.Select(reference => reference.TraceId).Distinct(StringComparer.Ordinal).Count();

        var supported = draft.Category switch
        {
            InstructionFindingCategoryV1.GoalClarity => anchorTurnReferences >= 2,
            InstructionFindingCategoryV1.Ambiguity => anchorTurnReferences >= 2 || (anchorTurnReferences >= 1 && distinctTraces >= 2),
            InstructionFindingCategoryV1.AcceptanceCriteriaMissing => anchorTurnReferences >= 2,
            InstructionFindingCategoryV1.ScopeBoundaryMissing => anchorTurnReferences >= 1 && hasAnchorErrorOrRetry,
            InstructionFindingCategoryV1.TaskTooLarge => anchorTurnReferences >= 2 && hasAnchorInstruction,
            InstructionFindingCategoryV1.TestRequirementMissing => anchorTurnReferences >= 2,
            InstructionFindingCategoryV1.EvidenceRequirementMissing => anchorTurnReferences >= 2,
            InstructionFindingCategoryV1.EnvironmentAssumptionMissing => anchorTurnReferences >= 1 && hasAnchorErrorOrRetry,
            _ => false,
        };
        return supported ? InstructionFindingVerdictV1.Supported : InstructionFindingVerdictV1.Weak;
    }

    private static InstructionFindingVerdictV1 LeastStrong(
        InstructionFindingVerdictV1 left,
        InstructionFindingVerdictV1 right) =>
        Rank(left) <= Rank(right) ? left : right;

    private static int Rank(InstructionFindingVerdictV1 verdict) => verdict switch
    {
        InstructionFindingVerdictV1.Incomplete => 0,
        InstructionFindingVerdictV1.Weak => 1,
        InstructionFindingVerdictV1.Supported => 2,
        _ => throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract),
    };

    private static IReadOnlyList<InstructionRuleCandidateV1> BuildCandidates(
        long analysisRunId,
        IReadOnlyList<InstructionFindingReceiptV1> findings) =>
        findings
            .Where(finding => finding.CandidateEligibility == InstructionCandidateEligibilityV1.Eligible)
            .GroupBy(finding => CreateDeduplicationKey(finding.Category), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var template = InstructionFindingTemplateCatalogV1.Get(first.Category);
                var deduplicationKey = group.Key;
                return new InstructionRuleCandidateV1(
                    InstructionFindingContractsV1.CandidateSchemaVersion,
                    CreateCandidateId(deduplicationKey),
                    deduplicationKey,
                    group.Select(finding => finding.FindingId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    template.Title,
                    template.RuleText,
                    InstructionRuleTargetKindV1.PromptInstruction,
                    template.TargetHint,
                    template.ScopeHint,
                    new InstructionRuleProvenanceV1(
                        analysisRunId,
                        group.SelectMany(finding => finding.EvidenceRefs.Select(reference => reference.TraceId))
                            .Distinct(StringComparer.Ordinal)
                            .Order(StringComparer.Ordinal)
                            .ToArray()));
            })
            .OrderBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToArray();

    private static string CreateFindingId(
        long analysisRunId,
        InstructionFindingCategoryV1 category,
        InstructionFindingExtractorSourceV1 extractorSource,
        IReadOnlyList<InstructionEvidenceReferenceV1> references)
    {
        var fields = new List<string?>
        {
            analysisRunId.ToString(CultureInfo.InvariantCulture),
            category.ToWireValue(),
            extractorSource.ToWireValue(),
        };
        foreach (var reference in references)
        {
            fields.Add(reference.SessionId);
            fields.Add(reference.TraceId);
            fields.Add(reference.SpanId);
            fields.Add(reference.TurnIndex?.ToString(CultureInfo.InvariantCulture));
            fields.Add(reference.RelativePosition.ToWireValue());
        }

        return "instruction-finding-" + Hash(FindingDomain, fields);
    }

    private static string CreateDeduplicationKey(InstructionFindingCategoryV1 category)
    {
        var template = InstructionFindingTemplateCatalogV1.Get(category);
        return "instruction-rule-dedup-" + Hash(
            DeduplicationDomain,
            [
                category.ToWireValue(),
                InstructionRuleTargetKindV1.PromptInstruction.ToWireValue(),
                template.TargetHint,
                template.ScopeHint.ToWireValue(),
                InstructionFindingContractsV1.RuleTemplateVersion,
            ]);
    }

    private static string CreateCandidateId(string deduplicationKey) =>
        "instruction-rule-" + Hash(CandidateDomain, [deduplicationKey]);

    private static string Hash(string domain, IReadOnlyList<string?> fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(domain));
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            if (field is null)
            {
                BinaryPrimitives.WriteUInt32BigEndian(length, uint.MaxValue);
                hash.AppendData(length);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 12)).ToLowerInvariant();
    }

    private static bool CandidateEquals(InstructionRuleCandidateV1 left, InstructionRuleCandidateV1? right) =>
        right is not null
        && right.SourceFindingIds is not null
        && right.Provenance is not null
        && right.Provenance.TraceRefs is not null
        && string.Equals(left.SchemaVersion, right.SchemaVersion, StringComparison.Ordinal)
        && string.Equals(left.CandidateId, right.CandidateId, StringComparison.Ordinal)
        && string.Equals(left.DeduplicationKey, right.DeduplicationKey, StringComparison.Ordinal)
        && left.SourceFindingIds.SequenceEqual(right.SourceFindingIds, StringComparer.Ordinal)
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(left.RuleText, right.RuleText, StringComparison.Ordinal)
        && left.TargetKind == right.TargetKind
        && string.Equals(left.TargetHint, right.TargetHint, StringComparison.Ordinal)
        && left.ScopeHint == right.ScopeHint
        && left.Provenance.AnalysisRunId == right.Provenance.AnalysisRunId
        && left.Provenance.TraceRefs.SequenceEqual(right.Provenance.TraceRefs, StringComparer.Ordinal);

    private sealed record PendingFinding(
        string FindingId,
        InstructionFindingCategoryV1 Category,
        InstructionFindingExtractorSourceV1 ExtractorSource,
        IReadOnlyList<InstructionEvidenceReferenceV1> EvidenceRefs,
        InstructionFindingVerdictV1 Verdict)
    {
        internal bool HasSameIdentity(PendingFinding other) =>
            Category == other.Category
            && ExtractorSource == other.ExtractorSource
            && EvidenceRefs.SequenceEqual(other.EvidenceRefs);
    }
}

internal sealed record InstructionFindingTemplateV1(
    string GapSummary,
    string RuleText,
    string Title,
    string TargetHint,
    InstructionRuleScopeHintV1 ScopeHint);

internal static class InstructionFindingTemplateCatalogV1
{
    internal static InstructionFindingTemplateV1 Get(InstructionFindingCategoryV1 category) => category switch
    {
        InstructionFindingCategoryV1.GoalClarity => new("達成する成果の定義が不足している。", "作業開始前に、達成する成果と利用者が判断できる終了状態を明記する。", "Goal clarity", "task_prompt", InstructionRuleScopeHintV1.Task),
        InstructionFindingCategoryV1.Ambiguity => new("複数の解釈を防ぐ指定が不足している。", "複数の解釈が可能な語は、期待する解釈と除外する解釈を明記する。", "Disambiguate instructions", "task_prompt", InstructionRuleScopeHintV1.Task),
        InstructionFindingCategoryV1.AcceptanceCriteriaMissing => new("完了条件を確認できる証拠が不足している。", "実装前に完了条件と、それを確認する必須テストを明記する。", "Acceptance criteria", "task_prompt", InstructionRuleScopeHintV1.Task),
        InstructionFindingCategoryV1.ScopeBoundaryMissing => new("実施範囲と非対象範囲の境界が不足している。", "着手前に実施範囲、非対象範囲、変更してよい対象を明記する。", "Scope boundaries", "repository_guidance", InstructionRuleScopeHintV1.Repository),
        InstructionFindingCategoryV1.TaskTooLarge => new("一回の実行に対して作業範囲が大きすぎる。", "独立して検証できる単位に作業を分割し、各単位の完了条件を明記する。", "Bound task size", "task_prompt", InstructionRuleScopeHintV1.Task),
        InstructionFindingCategoryV1.TestRequirementMissing => new("必要なテスト範囲の指定が不足している。", "変更前に実行する対象テストと、完了前に実行する回帰テストを明記する。", "Test requirements", "repository_guidance", InstructionRuleScopeHintV1.Repository),
        InstructionFindingCategoryV1.EvidenceRequirementMissing => new("完了時に残す証跡の指定が不足している。", "完了を宣言する前に、必須テスト結果と必要な証跡の存在を確認する。", "Evidence requirements", "repository_guidance", InstructionRuleScopeHintV1.Repository),
        InstructionFindingCategoryV1.EnvironmentAssumptionMissing => new("実行に必要な環境前提の指定が不足している。", "着手前に、必要な実行環境、利用可能なツール、禁止された代替経路を明記する。", "Environment assumptions", "repository_guidance", InstructionRuleScopeHintV1.Repository),
        _ => throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract),
    };
}

internal static class InstructionFindingReferenceTokenizationV1
{
    private const string SessionDomain = "copilot-agent-observability/instruction-session-reference/v1";
    private const string TraceDomain = "copilot-agent-observability/instruction-trace-reference/v1";
    private const string SpanDomain = "copilot-agent-observability/instruction-span-reference/v1";

    internal static InstructionEvidenceReferenceV1 Tokenize(InstructionRawEvidenceReferenceV1 reference) =>
        new(
            reference.SessionId is null ? null : Tokenize("session-ref-", SessionDomain, reference.SessionId),
            TokenizeTrace(reference.TraceId),
            reference.SpanId is null ? null : Tokenize("span-ref-", SpanDomain, reference.SpanId),
            reference.TurnIndex,
            reference.RelativePosition);

    internal static string TokenizeTrace(string traceId) => Tokenize("trace-ref-", TraceDomain, traceId);

    internal static bool IsSessionReference(string? value) => IsReference(value, "session-ref-");
    internal static bool IsTraceReference(string? value) => IsReference(value, "trace-ref-");
    internal static bool IsSpanReference(string? value) => IsReference(value, "span-ref-");

    private static string Tokenize(string prefix, string domain, string value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(domain));
        var valueBytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)valueBytes.Length));
        hash.AppendData(length);
        hash.AppendData(valueBytes);
        return prefix + Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 16)).ToLowerInvariant();
    }

    private static bool IsReference(string? value, string prefix)
    {
        if (value is null || value.Length != prefix.Length + 32 || !value.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        foreach (var character in value.AsSpan(prefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }
}

internal sealed class InstructionRawEvidenceReferenceComparerV1 : IComparer<InstructionRawEvidenceReferenceV1>
{
    internal static InstructionRawEvidenceReferenceComparerV1 Instance { get; } = new();

    public int Compare(InstructionRawEvidenceReferenceV1? left, InstructionRawEvidenceReferenceV1? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var result = CompareNullable(left.SessionId, right.SessionId);
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

internal sealed class InstructionEvidenceReferenceComparerV1 : IComparer<InstructionEvidenceReferenceV1>
{
    internal static InstructionEvidenceReferenceComparerV1 Instance { get; } = new();

    public int Compare(InstructionEvidenceReferenceV1? left, InstructionEvidenceReferenceV1? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var result = CompareNullable(left.SessionId, right.SessionId);
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

internal static class InstructionFindingWireV1
{
    internal static string ToWireValue(this InstructionFindingCategoryV1 value) => value switch
    {
        InstructionFindingCategoryV1.GoalClarity => "goal_clarity",
        InstructionFindingCategoryV1.Ambiguity => "ambiguity",
        InstructionFindingCategoryV1.AcceptanceCriteriaMissing => "acceptance_criteria_missing",
        InstructionFindingCategoryV1.ScopeBoundaryMissing => "scope_boundary_missing",
        InstructionFindingCategoryV1.TaskTooLarge => "task_too_large",
        InstructionFindingCategoryV1.TestRequirementMissing => "test_requirement_missing",
        InstructionFindingCategoryV1.EvidenceRequirementMissing => "evidence_requirement_missing",
        InstructionFindingCategoryV1.EnvironmentAssumptionMissing => "environment_assumption_missing",
        _ => throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract),
    };

    internal static string ToWireValue(this InstructionFindingExtractorSourceV1 value) => value switch
    {
        InstructionFindingExtractorSourceV1.DeterministicPrepass => "deterministic_prepass",
        InstructionFindingExtractorSourceV1.PromptOnly => "prompt_only",
        _ => throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract),
    };

    internal static string ToWireValue(this InstructionEvidenceRelativePositionV1 value) => value switch
    {
        InstructionEvidenceRelativePositionV1.Anchor => "anchor",
        InstructionEvidenceRelativePositionV1.Previous => "previous",
        InstructionEvidenceRelativePositionV1.Following => "following",
        _ => throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract),
    };

    internal static string ToWireValue(this InstructionRuleTargetKindV1 value) => value switch
    {
        InstructionRuleTargetKindV1.PromptInstruction => "prompt_instruction",
        _ => throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract),
    };

    internal static string ToWireValue(this InstructionRuleScopeHintV1 value) => value switch
    {
        InstructionRuleScopeHintV1.Task => "task",
        InstructionRuleScopeHintV1.Repository => "repository",
        _ => throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract),
    };
}
