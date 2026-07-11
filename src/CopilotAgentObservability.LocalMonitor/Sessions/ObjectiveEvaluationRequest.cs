using System.Text.Json;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal sealed record ObjectiveEvaluationRequest(string? SessionId, string? RunId, string? TraceId, string? Result, string? Severity, string? EvaluatorId, string? EvaluatorVersion, string? CriterionId, string? CaseKey, IReadOnlyList<ObjectiveEvaluationEvidence>? EvidenceRefs)
{
    private static readonly HashSet<string> Names = ["session_id", "run_id", "trace_id", "result", "severity", "evaluator_id", "evaluator_version", "criterion_id", "case_key", "evidence_refs"];

    public static bool TryParse(byte[] body, out ObjectiveEvaluationRequest? request)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject()) if (!Names.Contains(property.Name) || !seen.Add(property.Name)) return false;
            if (seen.Count != Names.Count) return false;
            var root = document.RootElement;
            if (!String(root, "session_id", out var session) || !String(root, "run_id", out var run) || !String(root, "trace_id", out var trace) || !String(root, "result", out var result) || !String(root, "severity", out var severity) || !String(root, "evaluator_id", out var evaluator) || !String(root, "evaluator_version", out var version) || !String(root, "criterion_id", out var criterion) || !String(root, "case_key", out var key) || !root.GetProperty("evidence_refs").ValueKind.Equals(JsonValueKind.Array)) return false;
            var evidence = new List<ObjectiveEvaluationEvidence>();
            foreach (var element in root.GetProperty("evidence_refs").EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) return false;
                var props = element.EnumerateObject().ToArray();
                if (props.Length != 2 || props.Select(p => p.Name).Distinct().Count() != 2 || !props.Any(p => p.Name == "kind") || !props.Any(p => p.Name == "reference_id") || !String(element, "kind", out var kind) || !String(element, "reference_id", out var id)) return false;
                evidence.Add(new(kind!, id!));
            }
            request = new(session, run, trace, result, severity, evaluator, version, criterion, key, evidence);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool String(JsonElement value, string name, out string? result)
    {
        result = null;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && (result = property.GetString()) is not null;
    }
}
