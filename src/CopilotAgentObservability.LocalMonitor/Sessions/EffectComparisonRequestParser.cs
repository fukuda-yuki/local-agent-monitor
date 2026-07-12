using System.Text.Json;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal static class EffectComparisonRequestParser
{
    private static readonly HashSet<string> RequestNames = ["proposal_id", "proposal_revision", "apply_id", "sessions"];
    private static readonly HashSet<string> SessionNames = ["session_id", "classification", "case_key", "exclusion_reason"];

    public static bool TryParse(byte[] body, out EffectComparisonRequest? request)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!ExactObject(root, RequestNames) || !String(root, "proposal_id", out var proposalText)
                || !String(root, "apply_id", out var applyText) || !root.TryGetProperty("proposal_revision", out var revision)
                || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt32(out var proposalRevision)
                || proposalRevision < 1
                || !root.TryGetProperty("sessions", out var sessionsElement) || sessionsElement.ValueKind != JsonValueKind.Array
                || proposalText is null || applyText is null || !UuidV7(proposalText, out var proposalId) || !UuidV7(applyText, out var applyId)) return false;

            var sessions = new List<EffectCohortSession>();
            foreach (var item in sessionsElement.EnumerateArray())
            {
                if (!ExactObject(item, SessionNames) || !String(item, "session_id", out var sessionText)
                    || !String(item, "classification", out var classification) || sessionText is null || !UuidV7(sessionText, out var sessionId)
                    || !item.TryGetProperty("case_key", out var caseElement) || !item.TryGetProperty("exclusion_reason", out var reasonElement)) return false;
                var caseKey = caseElement.ValueKind == JsonValueKind.String ? caseElement.GetString() : null;
                var exclusion = reasonElement.ValueKind == JsonValueKind.String ? reasonElement.GetString() : reasonElement.ValueKind == JsonValueKind.Null ? null : "\0";
                if (caseKey is null || exclusion == "\0") return false;
                sessions.Add(new(sessionId, classification!, caseKey, exclusion));
            }
            request = new(proposalId, proposalRevision, applyId, sessions);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool ExactObject(JsonElement value, HashSet<string> names) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() == names.Count
        && value.EnumerateObject().Count() == names.Count && value.EnumerateObject().All(item => names.Contains(item.Name));

    private static bool String(JsonElement value, string name, out string? result)
    {
        result = null;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && (result = property.GetString()) is not null;
    }

    private static bool UuidV7(string value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id.ToString("D")[14] == '7';
}
