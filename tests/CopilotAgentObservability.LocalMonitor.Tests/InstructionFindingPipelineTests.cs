using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using CopilotAgentObservability.InstructionFindings;
using CopilotAgentObservability.LocalMonitor.Analysis;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class InstructionFindingPipelineTests
{
    private const long AnalysisRunId = 123;
    private const string AnchorTraceId = "trace-anchor";

    [Fact]
    public void Generate_SupportedAcceptanceCriteria_ProducesEligibleReceiptAndCandidate()
    {
        var handoff = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(
                Turn("span-turn-1", 1),
                Turn("span-turn-2", 2)),
            [Draft(
                InstructionFindingCategoryV1.AcceptanceCriteriaMissing,
                InstructionFindingVerdictV1.Supported,
                Ref("span-turn-2", 2),
                Ref("span-turn-1", 1))]);

        var finding = Assert.Single(handoff.Findings);
        Assert.Equal(InstructionFindingContractsV1.HandoffSchemaVersion, handoff.SchemaVersion);
        Assert.Equal(InstructionFindingContractsV1.FindingSchemaVersion, finding.SchemaVersion);
        Assert.Matches("^instruction-finding-[0-9a-f]{24}$", finding.FindingId);
        Assert.Equal(AnalysisRunId, finding.AnalysisRunId);
        Assert.Equal(InstructionFindingCategoryV1.AcceptanceCriteriaMissing, finding.Category);
        Assert.Equal(InstructionFindingVerdictV1.Supported, finding.Verdict);
        Assert.Equal(InstructionFindingExtractorSourceV1.DeterministicPrepass, finding.ExtractorSource);
        Assert.Equal(SafeAnchorTraceId, finding.AnchorTraceId);
        Assert.Equal(new int?[] { 1, 2 }, finding.EvidenceRefs.Select(reference => reference.TurnIndex).ToArray());
        Assert.Equal(InstructionEvidenceQuoteStateV1.RawLocalOnly, finding.EvidenceQuoteState);
        Assert.Equal("完了条件を確認できる証拠が不足している。", finding.GapSummary);
        Assert.Equal("実装前に完了条件と、それを確認する必須テストを明記する。", finding.SuggestedInstruction);
        Assert.Equal(InstructionCandidateEligibilityV1.Eligible, finding.CandidateEligibility);

        var candidate = Assert.Single(handoff.Candidates);
        Assert.Equal(InstructionFindingContractsV1.CandidateSchemaVersion, candidate.SchemaVersion);
        Assert.Matches("^instruction-rule-[0-9a-f]{24}$", candidate.CandidateId);
        Assert.Matches("^instruction-rule-dedup-[0-9a-f]{24}$", candidate.DeduplicationKey);
        Assert.Equal(new[] { finding.FindingId }, candidate.SourceFindingIds);
        Assert.Equal("Acceptance criteria", candidate.Title);
        Assert.Equal(finding.SuggestedInstruction, candidate.RuleText);
        Assert.Equal(InstructionRuleTargetKindV1.PromptInstruction, candidate.TargetKind);
        Assert.Equal("task_prompt", candidate.TargetHint);
        Assert.Equal(InstructionRuleScopeHintV1.Task, candidate.ScopeHint);
        Assert.Equal(AnalysisRunId, candidate.Provenance.AnalysisRunId);
        Assert.Equal(new[] { SafeAnchorTraceId }, candidate.Provenance.TraceRefs);
    }

    [Theory]
    [InlineData("weak")]
    [InlineData("incomplete")]
    public void Generate_NonSupportedVerdict_IsIneligibleAndDoesNotGenerateCandidate(string verdictValue)
    {
        var verdict = verdictValue == "weak"
            ? InstructionFindingVerdictV1.Weak
            : InstructionFindingVerdictV1.Incomplete;
        var handoff = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2)),
            [Draft(InstructionFindingCategoryV1.AcceptanceCriteriaMissing, verdict, Ref("span-turn-1", 1), Ref("span-turn-2", 2))]);

        Assert.Equal(verdict, Assert.Single(handoff.Findings).Verdict);
        Assert.Equal(InstructionCandidateEligibilityV1.Ineligible, handoff.Findings[0].CandidateEligibility);
        Assert.Empty(handoff.Candidates);
    }

    [Fact]
    public void Generate_UndefinedVerdict_RejectsDraft()
    {
        var exception = Assert.Throws<InstructionFindingValidationException>(() =>
            InstructionFindingPipelineV1.Generate(
                AnalysisRunId,
                EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2)),
                [Draft(InstructionFindingCategoryV1.GoalClarity, (InstructionFindingVerdictV1)999, Ref("span-turn-1", 1), Ref("span-turn-2", 2))]));

        Assert.Equal(InstructionFindingValidationCodeV1.InvalidContract, exception.Code);
    }

    [Theory]
    [InlineData((int)InstructionFindingCategoryV1.GoalClarity)]
    [InlineData((int)InstructionFindingCategoryV1.Ambiguity)]
    [InlineData((int)InstructionFindingCategoryV1.AcceptanceCriteriaMissing)]
    [InlineData((int)InstructionFindingCategoryV1.ScopeBoundaryMissing)]
    [InlineData((int)InstructionFindingCategoryV1.TaskTooLarge)]
    [InlineData((int)InstructionFindingCategoryV1.TestRequirementMissing)]
    [InlineData((int)InstructionFindingCategoryV1.EvidenceRequirementMissing)]
    [InlineData((int)InstructionFindingCategoryV1.EnvironmentAssumptionMissing)]
    public void Generate_EachClosedCategoryMinimum_ProducesEligibleCandidate(int categoryValue)
    {
        var category = (InstructionFindingCategoryV1)categoryValue;
        var error = new InstructionFindingEvidenceLocationV1(
            null, AnchorTraceId, "span-error", null, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.ErrorOrRetrySpan);
        var instruction = new InstructionFindingEvidenceLocationV1(
            null, AnchorTraceId, "span-instruction", null, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.InstructionSpan);
        InstructionRawEvidenceReferenceV1[] evidenceRefs = category switch
        {
            InstructionFindingCategoryV1.ScopeBoundaryMissing or InstructionFindingCategoryV1.EnvironmentAssumptionMissing =>
                [Ref("span-turn-1", 1), error.ToReference()],
            InstructionFindingCategoryV1.TaskTooLarge =>
                [Ref("span-turn-1", 1), Ref("span-turn-2", 2), instruction.ToReference()],
            _ => [Ref("span-turn-1", 1), Ref("span-turn-2", 2)],
        };

        var handoff = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2), error, instruction),
            [Draft(category, InstructionFindingVerdictV1.Supported, [.. evidenceRefs])]);

        Assert.Equal(InstructionFindingVerdictV1.Supported, Assert.Single(handoff.Findings).Verdict);
        Assert.Equal(InstructionCandidateEligibilityV1.Eligible, handoff.Findings[0].CandidateEligibility);
        Assert.Single(handoff.Candidates);
    }

    [Fact]
    public void Generate_AllClosedCategories_ReconstructsTheSemanticMaximumOfEightCandidates()
    {
        var error = new InstructionFindingEvidenceLocationV1(
            null, AnchorTraceId, "span-error", null, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.ErrorOrRetrySpan);
        var instruction = new InstructionFindingEvidenceLocationV1(
            null, AnchorTraceId, "span-instruction", null, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.InstructionSpan);
        var drafts = Enum.GetValues<InstructionFindingCategoryV1>()
            .Select(category => Draft(
                category,
                InstructionFindingVerdictV1.Supported,
                category switch
                {
                    InstructionFindingCategoryV1.ScopeBoundaryMissing or InstructionFindingCategoryV1.EnvironmentAssumptionMissing =>
                        [Ref("span-turn-1", 1), error.ToReference()],
                    InstructionFindingCategoryV1.TaskTooLarge =>
                        [Ref("span-turn-1", 1), Ref("span-turn-2", 2), instruction.ToReference()],
                    _ => [Ref("span-turn-1", 1), Ref("span-turn-2", 2)],
                }))
            .ToArray();

        var handoff = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2), error, instruction),
            drafts);

        Assert.Equal(8, handoff.Findings.Count);
        Assert.Equal(8, handoff.Candidates.Count);
    }

    [Fact]
    public void Generate_SupportedAssessmentWithoutCategoryMinimum_DowngradesToWeak()
    {
        var handoff = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(Turn("span-turn-1", 1)),
            [Draft(InstructionFindingCategoryV1.TestRequirementMissing, InstructionFindingVerdictV1.Supported, Ref("span-turn-1", 1))]);

        var finding = Assert.Single(handoff.Findings);
        Assert.Equal(InstructionFindingVerdictV1.Weak, finding.Verdict);
        Assert.Equal(InstructionCandidateEligibilityV1.Ineligible, finding.CandidateEligibility);
        Assert.Empty(handoff.Candidates);
    }

    [Fact]
    public void Generate_SiblingOnlyEvidence_DowngradesSupportedAssessmentToWeak()
    {
        var siblingOne = new InstructionFindingEvidenceLocationV1(
            null,
            "trace-previous",
            "span-previous-1",
            1,
            InstructionEvidenceRelativePositionV1.Previous,
            InstructionFindingEvidenceKindV1.Turn);
        var siblingTwo = siblingOne with { SpanId = "span-previous-2", TurnIndex = 2 };
        var handoff = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(siblingOne, siblingTwo),
            [new InstructionFindingDraftV1(
                InstructionFindingCategoryV1.GoalClarity,
                InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.PromptOnly,
                [
                    new(null, siblingOne.TraceId, siblingOne.SpanId, siblingOne.TurnIndex, siblingOne.RelativePosition),
                    new(null, siblingTwo.TraceId, siblingTwo.SpanId, siblingTwo.TurnIndex, siblingTwo.RelativePosition),
                ])]);

        Assert.Equal(InstructionFindingVerdictV1.Weak, Assert.Single(handoff.Findings).Verdict);
        Assert.Empty(handoff.Candidates);
    }

    [Fact]
    public void Generate_UnrelatedAnchorSpan_DoesNotPromoteSiblingOnlyCategoryEvidence()
    {
        var anchorError = new InstructionFindingEvidenceLocationV1(
            null,
            AnchorTraceId,
            "span-anchor-error",
            null,
            InstructionEvidenceRelativePositionV1.Anchor,
            InstructionFindingEvidenceKindV1.ErrorOrRetrySpan);
        var siblingOne = new InstructionFindingEvidenceLocationV1(
            null,
            "trace-previous",
            "span-previous-1",
            1,
            InstructionEvidenceRelativePositionV1.Previous,
            InstructionFindingEvidenceKindV1.Turn);
        var siblingTwo = siblingOne with { SpanId = "span-previous-2", TurnIndex = 2 };
        var handoff = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(anchorError, siblingOne, siblingTwo),
            [new InstructionFindingDraftV1(
                InstructionFindingCategoryV1.GoalClarity,
                InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.PromptOnly,
                [anchorError.ToReference(), siblingOne.ToReference(), siblingTwo.ToReference()])]);

        Assert.Equal(InstructionFindingVerdictV1.Weak, Assert.Single(handoff.Findings).Verdict);
        Assert.Empty(handoff.Candidates);
    }

    [Fact]
    public void Generate_UnresolvedEvidenceReference_RejectsFinding()
    {
        var exception = Assert.Throws<InstructionFindingValidationException>(() =>
            InstructionFindingPipelineV1.Generate(
                AnalysisRunId,
                EvidenceIndex(Turn("span-turn-1", 1)),
                [Draft(InstructionFindingCategoryV1.GoalClarity, InstructionFindingVerdictV1.Supported, Ref("missing-span", 1))]));

        Assert.Equal(InstructionFindingValidationCodeV1.UnresolvedEvidenceReference, exception.Code);
    }

    [Fact]
    public void Generate_WrongSiblingDirection_RejectsFinding()
    {
        var sibling = new InstructionFindingEvidenceLocationV1(
            SessionId: "session-previous",
            TraceId: "trace-previous",
            SpanId: "span-previous-turn-1",
            TurnIndex: 1,
            RelativePosition: InstructionEvidenceRelativePositionV1.Previous,
            Kind: InstructionFindingEvidenceKindV1.Turn);
        var wrongDirection = new InstructionRawEvidenceReferenceV1(
            SessionId: "session-previous",
            TraceId: "trace-previous",
            SpanId: "span-previous-turn-1",
            TurnIndex: 1,
            RelativePosition: InstructionEvidenceRelativePositionV1.Following);

        var exception = Assert.Throws<InstructionFindingValidationException>(() =>
            InstructionFindingPipelineV1.Generate(
                AnalysisRunId,
                EvidenceIndex(Turn("span-turn-1", 1), sibling),
                [new InstructionFindingDraftV1(
                    InstructionFindingCategoryV1.Ambiguity,
                    InstructionFindingVerdictV1.Supported,
                    InstructionFindingExtractorSourceV1.PromptOnly,
                    [Ref("span-turn-1", 1), wrongDirection])]));

        Assert.Equal(InstructionFindingValidationCodeV1.UnresolvedEvidenceReference, exception.Code);
    }

    [Fact]
    public void Generate_ReorderedDuplicates_ProducesByteIdenticalHandoffAndDeduplicatesCandidates()
    {
        var index = EvidenceIndex(
            Turn("span-turn-1", 1),
            Turn("span-turn-2", 2),
            Turn("span-turn-3", 3));
        var first = Draft(
            InstructionFindingCategoryV1.EvidenceRequirementMissing,
            InstructionFindingVerdictV1.Supported,
            Ref("span-turn-1", 1),
            Ref("span-turn-2", 2));
        var duplicate = first with { EvidenceRefs = [Ref("span-turn-2", 2), Ref("span-turn-1", 1), Ref("span-turn-1", 1)] };
        var second = Draft(
            InstructionFindingCategoryV1.EvidenceRequirementMissing,
            InstructionFindingVerdictV1.Supported,
            Ref("span-turn-2", 2),
            Ref("span-turn-3", 3));

        var left = InstructionFindingPipelineV1.Generate(AnalysisRunId, index, [first, duplicate, second]);
        var right = InstructionFindingPipelineV1.Generate(AnalysisRunId, index, [second, duplicate, first]);

        Assert.Equal(InstructionFindingJsonV1.Serialize(left), InstructionFindingJsonV1.Serialize(right));
        Assert.Equal(2, left.Findings.Count);
        var candidate = Assert.Single(left.Candidates);
        Assert.Equal(2, candidate.SourceFindingIds.Count);
        Assert.Equal(candidate.SourceFindingIds.Order(StringComparer.Ordinal), candidate.SourceFindingIds);
        Assert.Equal(new[] { SafeAnchorTraceId }, candidate.Provenance.TraceRefs);
    }

    [Fact]
    public void Generate_DuplicateVerdicts_UsesLeastStrongVerdictIndependentOfOrder()
    {
        var index = EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2));
        var supported = Draft(InstructionFindingCategoryV1.GoalClarity, InstructionFindingVerdictV1.Supported, Ref("span-turn-1", 1), Ref("span-turn-2", 2));
        var weak = supported with { AssessedVerdict = InstructionFindingVerdictV1.Weak };

        var left = InstructionFindingPipelineV1.Generate(AnalysisRunId, index, [supported, weak]);
        var right = InstructionFindingPipelineV1.Generate(AnalysisRunId, index, [weak, supported]);

        Assert.Equal(InstructionFindingVerdictV1.Weak, SingleFindingVerdict(left));
        Assert.Equal(InstructionFindingJsonV1.Serialize(left), InstructionFindingJsonV1.Serialize(right));
        Assert.Empty(left.Candidates);
    }

    [Fact]
    public void Generate_RepositorySafeText_NeverCopiesProducerContent()
    {
        var forbiddenFragments = new[]
        {
            "raw user prompt sentinel",
            "raw assistant response sentinel",
            "tool argument sentinel",
            "source-code-fragment-sentinel",
            "sk-live-secret-sentinel",
            "person@example.test",
            @"C:\Users\person\private\source.cs",
        };
        var handoff = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2)),
            [Draft(InstructionFindingCategoryV1.EvidenceRequirementMissing, InstructionFindingVerdictV1.Supported, Ref("span-turn-1", 1), Ref("span-turn-2", 2))]);
        var json = Encoding.UTF8.GetString(InstructionFindingJsonV1.Serialize(handoff));

        foreach (var fragment in forbiddenFragments)
        {
            Assert.DoesNotContain(fragment, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Generate_RepositorySafeReferences_TokenizePotentiallyIdentifyingRawIds()
    {
        const string rawAnchorTraceId = "person.example.test";
        const string rawFirstSpanId = "span.person.example.test.1";
        const string rawSecondSpanId = "span.person.example.test.2";
        var locations = new[]
        {
            new InstructionFindingEvidenceLocationV1(null, rawAnchorTraceId, rawFirstSpanId, 1, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn),
            new InstructionFindingEvidenceLocationV1(null, rawAnchorTraceId, rawSecondSpanId, 2, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn),
        };
        var handoff = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            new InstructionFindingEvidenceIndexV1(rawAnchorTraceId, locations),
            [new InstructionFindingDraftV1(
                InstructionFindingCategoryV1.GoalClarity,
                InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.PromptOnly,
                locations.Select(location => location.ToReference()).ToArray())]);
        var json = Encoding.UTF8.GetString(InstructionFindingJsonV1.Serialize(handoff));
        var finding = Assert.Single(handoff.Findings);

        Assert.DoesNotContain(rawAnchorTraceId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(rawFirstSpanId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(rawSecondSpanId, json, StringComparison.Ordinal);
        Assert.Matches("^trace-ref-[0-9a-f]{32}$", finding.AnchorTraceId);
        Assert.All(finding.EvidenceRefs, reference => Assert.Matches("^trace-ref-[0-9a-f]{32}$", reference.TraceId));
        Assert.All(finding.EvidenceRefs, reference => Assert.Matches("^span-ref-[0-9a-f]{32}$", reference.SpanId));
    }

    [Fact]
    public void ReferenceTokenization_IsDeterministicAndDomainSeparatedByIdentifierKind()
    {
        const string rawIdentifier = "person.example.test";
        var reference = new InstructionRawEvidenceReferenceV1(
            rawIdentifier,
            rawIdentifier,
            rawIdentifier,
            1,
            InstructionEvidenceRelativePositionV1.Anchor);

        var first = InstructionFindingReferenceTokenizationV1.Tokenize(reference);
        var second = InstructionFindingReferenceTokenizationV1.Tokenize(reference);

        Assert.Equal(first, second);
        Assert.NotEqual(first.SessionId, first.TraceId);
        Assert.NotEqual(first.SessionId, first.SpanId);
        Assert.NotEqual(first.TraceId, first.SpanId);
        Assert.DoesNotContain(rawIdentifier, first.SessionId!, StringComparison.Ordinal);
        Assert.DoesNotContain(rawIdentifier, first.TraceId, StringComparison.Ordinal);
        Assert.DoesNotContain(rawIdentifier, first.SpanId!, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RoundTripPreservesFrozen72And73HandoffContract()
    {
        var original = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2)),
            [Draft(InstructionFindingCategoryV1.GoalClarity, InstructionFindingVerdictV1.Supported, Ref("span-turn-1", 1), Ref("span-turn-2", 2))]);

        var bytes = InstructionFindingJsonV1.Serialize(original);
        var json = Encoding.UTF8.GetString(bytes);
        var restored = InstructionFindingJsonV1.Deserialize(bytes);

        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
        Assert.Equal(original.Findings[0].FindingId, restored.Findings[0].FindingId);
        Assert.Equal(original.Findings[0].EvidenceRefs, restored.Findings[0].EvidenceRefs);
        Assert.Equal(original.Candidates[0].SourceFindingIds, restored.Candidates[0].SourceFindingIds);
        Assert.Equal(bytes, InstructionFindingJsonV1.Serialize(restored));
    }

    [Fact]
    public void FrozenFixture_AndJsonSchema_MatchTheVersionedHandoffContract()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "InstructionFindings", "instruction-finding-handoff.v1.json");
        var canonicalFixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "InstructionFindings", "instruction-finding-handoff.canonical.base64");
        var canonicalShaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "InstructionFindings", "instruction-finding-handoff.canonical.sha256");
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "InstructionFindings", "instruction-finding-handoff.schema.json");
        var canonicalFixtureBytes = Convert.FromBase64String(File.ReadAllText(canonicalFixturePath).Trim());
        var fixture = InstructionFindingJsonV1.Deserialize(canonicalFixtureBytes);
        var expected = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2)),
            [Draft(InstructionFindingCategoryV1.GoalClarity, InstructionFindingVerdictV1.Supported, Ref("span-turn-1", 1), Ref("span-turn-2", 2))]);
        using var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        var root = schema.RootElement;

        Assert.Equal(canonicalFixtureBytes, InstructionFindingJsonV1.Serialize(expected));
        Assert.Equal(
            File.ReadAllText(canonicalShaPath).Trim(),
            Convert.ToHexString(SHA256.HashData(InstructionFindingJsonV1.Serialize(expected))).ToLowerInvariant());
        Assert.Equal(InstructionFindingJsonV1.Serialize(expected), InstructionFindingJsonV1.Serialize(fixture));
        Assert.Throws<InstructionFindingValidationException>(() =>
            InstructionFindingJsonV1.Deserialize(File.ReadAllBytes(fixturePath)));
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.Equal(InstructionFindingContractsV1.HandoffSchemaVersion, root.GetProperty("properties").GetProperty("schema_version").GetProperty("const").GetString());
        Assert.Equal(
            Enum.GetValues<InstructionFindingCategoryV1>().Select(value => value.ToWireValue()).Order(StringComparer.Ordinal),
            root.GetProperty("$defs").GetProperty("category").GetProperty("enum").EnumerateArray().Select(value => value.GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ConsumerValidator_AndLocalMonitorContractUseOneRuntimeAuthority()
    {
        var authority = typeof(InstructionFindingHandoffConsumerV1).Assembly;

        Assert.Same(authority, typeof(InstructionFindingJsonV1).Assembly);
        Assert.Same(authority, typeof(InstructionFindingPipelineV1).Assembly);
        Assert.Same(authority, typeof(InstructionFindingContractsV1).Assembly);
    }

    [Fact]
    public void JsonSchema_ActualValidator_AcceptsFixtureAndRejectsRawIdentifierCarrier()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "InstructionFindings", "instruction-finding-handoff.v1.json");
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "InstructionFindings", "instruction-finding-handoff.schema.json");
        var invalidPath = Path.Combine(Path.GetTempPath(), $"instruction-finding-invalid-{Guid.NewGuid():N}.json");
        try
        {
            var valid = File.ReadAllText(fixturePath, Encoding.UTF8);
            using var document = JsonDocument.Parse(valid);
            var safeTraceReference = document.RootElement.GetProperty("findings")[0].GetProperty("anchor_trace_id").GetString()!;
            var invalid = valid.Replace(safeTraceReference, "person.example.test", StringComparison.Ordinal);
            File.WriteAllText(invalidPath, invalid, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Assert.True(ValidateWithPowerShellJsonSchema(fixturePath, schemaPath));
            Assert.False(ValidateWithPowerShellJsonSchema(invalidPath, schemaPath));
        }
        finally
        {
            File.Delete(invalidPath);
        }
    }

    [Fact]
    public void Deserialize_UnknownProperty_RejectsCarrier()
    {
        var valid = InstructionFindingPipelineV1.Generate(AnalysisRunId, EvidenceIndex(), []);
        var json = Encoding.UTF8.GetString(InstructionFindingJsonV1.Serialize(valid));
        var unknown = Encoding.UTF8.GetBytes(json[..^1] + ",\"unexpected\":true}");

        var exception = Assert.Throws<InstructionFindingValidationException>(() => InstructionFindingJsonV1.Deserialize(unknown));

        Assert.Equal(InstructionFindingValidationCodeV1.InvalidSerialization, exception.Code);
    }

    [Fact]
    public void Deserialize_NullFindingElement_RejectsCarrierWithBoundedValidationFailure()
    {
        const string json = "{\"schema_version\":\"instruction-finding-handoff.v1\",\"analysis_run_id\":123,\"findings\":[null],\"candidates\":[]}";

        var exception = Assert.Throws<InstructionFindingValidationException>(() =>
            InstructionFindingJsonV1.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(InstructionFindingValidationCodeV1.InvalidContract, exception.Code);
    }

    [Fact]
    public void Deserialize_TamperedDerivedId_RejectsCarrier()
    {
        var valid = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2)),
            [Draft(InstructionFindingCategoryV1.GoalClarity, InstructionFindingVerdictV1.Supported, Ref("span-turn-1", 1), Ref("span-turn-2", 2))]);
        var json = Encoding.UTF8.GetString(InstructionFindingJsonV1.Serialize(valid));
        var tampered = Encoding.UTF8.GetBytes(json.Replace(valid.Findings[0].FindingId, "instruction-finding-000000000000000000000000", StringComparison.Ordinal));

        var exception = Assert.Throws<InstructionFindingValidationException>(() => InstructionFindingJsonV1.Deserialize(tampered));

        Assert.Equal(InstructionFindingValidationCodeV1.InvalidDerivedIdentity, exception.Code);
    }

    [Fact]
    public void Generate_ZeroFindings_ProducesValidEmptyHandoff()
    {
        var handoff = InstructionFindingPipelineV1.Generate(AnalysisRunId, EvidenceIndex(), []);

        Assert.Empty(handoff.Findings);
        Assert.Empty(handoff.Candidates);
        var restored = InstructionFindingJsonV1.Deserialize(InstructionFindingJsonV1.Serialize(handoff));
        Assert.Equal(InstructionFindingJsonV1.Serialize(handoff), InstructionFindingJsonV1.Serialize(restored));
    }

    [Fact]
    public void EvidenceIndex_FromInstructionEvidence_ResolvesAnchorAndBoundedSiblingExactly()
    {
        var evidence = new InstructionEvidence(
            ErrorSpans: [new InstructionEvidenceErrorSpan("span-error", "shell", "timeout", "shell failed")],
            RetryChains: [new InstructionEvidenceRetryChain("shell", ["span-error", "span-retry"], "recovered")],
            TurnTokens: [new InstructionEvidenceTurnTokens(1, "span-turn-1", 10, 5)],
            UserInstruction: new InstructionEvidenceUserInstruction("span-turn-1", 1, "raw descriptor not exported"),
            Conversation: null,
            ConversationContext: new InstructionEvidenceConversationContext(
                "conversation-1",
                2,
                2,
                1,
                2,
                false,
                false,
                [
                    new InstructionEvidenceConversationTrace("trace-previous", -1, false, null, "raw sibling descriptor", 1, 1, 1, 2, 1, 0, ["span-previous-error"], []),
                    new InstructionEvidenceConversationTrace(AnchorTraceId, 0, true, null, "raw anchor descriptor", 1, 10, 5, 15, 1, 1, ["span-error"], ["shell"]),
                ]));
        var index = InstructionFindingEvidenceIndexFactoryV1.FromInstructionEvidence(AnchorTraceId, evidence);

        var environment = InstructionFindingPipelineV1.Generate(
            AnalysisRunId,
            index,
            [new InstructionFindingDraftV1(
                InstructionFindingCategoryV1.EnvironmentAssumptionMissing,
                InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.PromptOnly,
                [Ref("span-turn-1", 1), new(null, AnchorTraceId, "span-error", null, InstructionEvidenceRelativePositionV1.Anchor)])]);
        var ambiguity = InstructionFindingPipelineV1.Generate(
            AnalysisRunId + 1,
            index,
            [new InstructionFindingDraftV1(
                InstructionFindingCategoryV1.Ambiguity,
                InstructionFindingVerdictV1.Supported,
                InstructionFindingExtractorSourceV1.PromptOnly,
                [
                    Ref("span-turn-1", 1),
                    new(null, "trace-previous", null, 1, InstructionEvidenceRelativePositionV1.Previous),
                ])]);

        Assert.Equal(InstructionFindingVerdictV1.Supported, Assert.Single(environment.Findings).Verdict);
        Assert.Equal(InstructionFindingVerdictV1.Supported, Assert.Single(ambiguity.Findings).Verdict);
    }

    [Fact]
    public void SubmissionCollector_WireInput_AcceptsExactEvidenceAndRejectsInvalidCitation()
    {
        var collector = new InstructionFindingSubmissionCollectorV1(
            AnalysisRunId,
            EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2)));
        const string validRefs = "[{\"session_id\":null,\"trace_id\":\"trace-anchor\",\"span_id\":\"span-turn-1\",\"turn_index\":1,\"relative_position\":\"anchor\"},{\"session_id\":null,\"trace_id\":\"trace-anchor\",\"span_id\":\"span-turn-2\",\"turn_index\":2,\"relative_position\":\"anchor\"}]";
        const string invalidRefs = "[{\"session_id\":null,\"trace_id\":\"trace-anchor\",\"span_id\":\"span-missing\",\"turn_index\":1,\"relative_position\":\"anchor\"}]";

        var accepted = collector.SubmitWire("goal_clarity", "supported", "prompt_only", validRefs);
        var rejected = collector.SubmitWire("goal_clarity", "supported", "prompt_only", invalidRefs);
        var handoff = collector.BuildHandoff();

        Assert.Equal("{\"status\":\"accepted\"}", accepted);
        Assert.Equal("{\"status\":\"rejected\",\"code\":\"unresolved_evidence_reference\"}", rejected);
        Assert.Single(handoff.Findings);
        Assert.Single(handoff.Candidates);
    }

    [Fact]
    public void SubmissionCollector_NullEvidenceElement_ReturnsBoundedRejection()
    {
        var collector = new InstructionFindingSubmissionCollectorV1(
            AnalysisRunId,
            EvidenceIndex(Turn("span-turn-1", 1), Turn("span-turn-2", 2)));

        var result = collector.SubmitWire("goal_clarity", "supported", "prompt_only", "[null]");

        Assert.Equal("{\"status\":\"rejected\",\"code\":\"invalid_contract\"}", result);
        Assert.Empty(collector.BuildHandoff().Findings);
    }

    private static InstructionFindingVerdictV1 SingleFindingVerdict(InstructionFindingHandoffV1 handoff) =>
        Assert.Single(handoff.Findings).Verdict;

    private static bool ValidateWithPowerShellJsonSchema(string instancePath, string schemaPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("if (Test-Json -LiteralPath $env:CAO_SCHEMA_INSTANCE -SchemaFile $env:CAO_SCHEMA_FILE -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }");
        startInfo.Environment["CAO_SCHEMA_INSTANCE"] = instancePath;
        startInfo.Environment["CAO_SCHEMA_FILE"] = schemaPath;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pwsh for JSON Schema validation.");
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("JSON Schema validation timed out.");
        }

        return process.ExitCode == 0;
    }

    private static InstructionFindingEvidenceIndexV1 EvidenceIndex(params InstructionFindingEvidenceLocationV1[] locations) =>
        new(AnchorTraceId, locations);

    private static string SafeAnchorTraceId => InstructionFindingReferenceTokenizationV1.TokenizeTrace(AnchorTraceId);

    private static InstructionFindingEvidenceLocationV1 Turn(string spanId, int turnIndex) =>
        new(null, AnchorTraceId, spanId, turnIndex, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn);

    private static InstructionRawEvidenceReferenceV1 Ref(string spanId, int turnIndex) =>
        new(null, AnchorTraceId, spanId, turnIndex, InstructionEvidenceRelativePositionV1.Anchor);

    private static InstructionFindingDraftV1 Draft(
        InstructionFindingCategoryV1 category,
        InstructionFindingVerdictV1 verdict,
        params InstructionRawEvidenceReferenceV1[] evidenceRefs) =>
        new(category, verdict, InstructionFindingExtractorSourceV1.DeterministicPrepass, evidenceRefs);
}
