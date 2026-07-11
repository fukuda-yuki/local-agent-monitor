using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Telemetry.Sessions;

public enum ObjectiveResult { Pass, Fail }
public enum ObjectiveSeverity { Normal, Severe }

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
        receipt.ObjectiveEvaluationId != Guid.Empty && receipt.SessionId != Guid.Empty && receipt.RunId != Guid.Empty
        && !string.IsNullOrWhiteSpace(receipt.TraceId)
        && (receipt.Result != ObjectiveResult.Pass || receipt.Severity == ObjectiveSeverity.Normal)
        && IdentifierValue(receipt.EvaluatorId, 100) && IdentifierValue(receipt.EvaluatorVersion, 100)
        && IdentifierValue(receipt.CriterionId, 100) && IdentifierValue(receipt.CaseKey, 200)
        && receipt.Evidence is { Count: >= 1 and <= 10 }
        && receipt.Evidence.All(e => e is not null && e.Kind is "run" or "event" or "trace" or "gate" && !string.IsNullOrWhiteSpace(e.ReferenceId))
        && receipt.Evidence.Select(e => (e.Kind, e.ReferenceId)).Distinct().Count() == receipt.Evidence.Count;

    public static bool IdentifierValue(string? value, int maximum) => value is { Length: >= 1 } && value.Length <= maximum && Identifier().IsMatch(value);
}
