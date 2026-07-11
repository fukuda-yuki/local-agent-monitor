using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Telemetry.Sessions;

public enum ObjectiveResult { Pass, Fail }
public enum ObjectiveSeverity { Normal, Severe }
public enum EffectVerdict { Improved, NoChange, Regressed, InsufficientEvidence }

public sealed record EffectCohortSession(
    Guid SessionId, string Classification, string CaseKey, string? ExclusionReason);

public sealed record EffectComparisonRequest(
    Guid ProposalId, int ProposalRevision, Guid ApplyId,
    IReadOnlyList<EffectCohortSession> Sessions);

public sealed record EffectReceipt(
    Guid ComparisonId, int CohortRevision, Guid ProposalId,
    int ProposalRevision, Guid ApplyId, EffectVerdictResult Result,
    string VerificationState, DateTimeOffset RecordedAt);

public sealed record SessionEffectFacts(
    Guid SessionId, string Side, string CaseKey, bool QualityPass,
    bool SevereFailure, long? DurationMs, long? TotalTokens,
    IReadOnlyList<string> EvidenceIds);

public sealed record EffectComparisonFacts(
    bool LinkageValid, IReadOnlyList<SessionEffectFacts> Pre,
    IReadOnlyList<SessionEffectFacts> Post,
    IReadOnlyList<string> InsufficiencyReasons);

public sealed record EffectVerdictResult(
    EffectVerdict Verdict, int PrePass, int PreCount, int PostPass,
    int PostCount, decimal? PreDurationMedian, decimal? PostDurationMedian,
    decimal? DurationDelta, decimal? PreTokenMedian,
    decimal? PostTokenMedian, decimal? TokenDelta,
    IReadOnlyList<string> Reasons);

public sealed record ObjectiveEvaluationEvidence(string Kind, string ReferenceId);

public sealed record ObjectiveEvaluationReceipt(
    Guid ObjectiveEvaluationId, Guid SessionId, Guid RunId, string TraceId,
    ObjectiveResult Result, ObjectiveSeverity Severity,
    string EvaluatorId, string EvaluatorVersion, string CriterionId,
    string CaseKey, IReadOnlyList<ObjectiveEvaluationEvidence> Evidence,
    DateTimeOffset RecordedAt);

public static partial class ObjectiveEvaluationValidation
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]*$")]
    private static partial Regex Identifier();

    public static bool IsValid(ObjectiveEvaluationReceipt receipt) =>
        IsUuidVersion7(receipt.ObjectiveEvaluationId) && receipt.SessionId != Guid.Empty && receipt.RunId != Guid.Empty
        && !string.IsNullOrWhiteSpace(receipt.TraceId)
        && receipt.Result is ObjectiveResult.Pass or ObjectiveResult.Fail
        && receipt.Severity is ObjectiveSeverity.Normal or ObjectiveSeverity.Severe
        && (receipt.Result != ObjectiveResult.Pass || receipt.Severity == ObjectiveSeverity.Normal)
        && IdentifierValue(receipt.EvaluatorId, 100) && IdentifierValue(receipt.EvaluatorVersion, 100)
        && IdentifierValue(receipt.CriterionId, 100) && IdentifierValue(receipt.CaseKey, 200)
        && receipt.Evidence is { Count: >= 1 and <= 10 }
        && receipt.Evidence.All(e => e is not null && e.Kind is "run" or "event" or "trace" or "gate" && !string.IsNullOrWhiteSpace(e.ReferenceId))
        && receipt.Evidence.Select(e => (e.Kind, e.ReferenceId)).Distinct().Count() == receipt.Evidence.Count;

    public static bool IdentifierValue(string? value, int maximum) => value is { Length: >= 1 } && value.Length <= maximum && Identifier().IsMatch(value);

    private static bool IsUuidVersion7(Guid value) => value != Guid.Empty && value.ToString("D")[14] == '7';
}
