using System.Text;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Alerts;

public static class AlertLifecycleContractVersions
{
    public const string Lifecycle = "alert.lifecycle.v1";
    public const string SanitizedExportProfile = "sanitized-alert-lifecycle.v1";
}

public enum AlertLifecycleState { Open, Acknowledged, Dismissed, Resolved, Superseded }

public enum AlertLifecycleAction { Acknowledge, Dismiss, Resolve, Reopen, Supersede, SourceDeleted }

public enum AlertLifecycleStoreStatus { Success, NotFound, Invalid, Conflict, Busy, Unavailable }

public sealed record AlertLifecycleEvent(
    string SchemaVersion,
    string EventId,
    string AlertId,
    long Revision,
    long ExpectedRevision,
    AlertLifecycleAction Action,
    AlertLifecycleState PreviousState,
    AlertLifecycleState State,
    DateTimeOffset OccurredAt,
    string Actor,
    string ReasonCode,
    string? Comment,
    string IdempotencyKey,
    string? OldAlertId,
    string? NewAlertId,
    string ResultCode);

public sealed record AlertLifecycleView(
    string SchemaVersion,
    string AlertId,
    AlertLifecycleState State,
    long Revision,
    DateTimeOffset? LastOccurredAt);

public sealed record AlertLifecycleMutation(
    string AlertId,
    AlertLifecycleAction Action,
    long ExpectedRevision,
    string ReasonCode,
    string? Comment,
    string IdempotencyKey,
    string Actor = "local_user",
    string? OldAlertId = null,
    string? NewAlertId = null);

public sealed record AlertLifecycleStoreResult(
    AlertLifecycleStoreStatus Status,
    string? Code = null,
    AlertLifecycleView? Lifecycle = null,
    AlertLifecycleEvent? Event = null,
    bool Replayed = false);

public sealed record AlertLifecycleHistoryResult(
    AlertLifecycleStoreStatus Status,
    IReadOnlyList<AlertLifecycleEvent> Events,
    string? Code = null);

public interface IAlertLifecycleStore
{
    AlertLifecycleStoreResult Initialize();
    AlertLifecycleStoreResult Get(string alertId);
    AlertLifecycleHistoryResult History(string alertId, int limit = 50);
    AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation);
    AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation);
    AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation);
    AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation);
}

public static class AlertLifecycleTransition
{
    public static bool TryApply(AlertLifecycleState current, AlertLifecycleAction action, out AlertLifecycleState next)
    {
        next = (current, action) switch
        {
            (AlertLifecycleState.Open, AlertLifecycleAction.Acknowledge) => AlertLifecycleState.Acknowledged,
            (AlertLifecycleState.Open or AlertLifecycleState.Acknowledged, AlertLifecycleAction.Dismiss) => AlertLifecycleState.Dismissed,
            (AlertLifecycleState.Open or AlertLifecycleState.Acknowledged, AlertLifecycleAction.Resolve) => AlertLifecycleState.Resolved,
            (AlertLifecycleState.Dismissed or AlertLifecycleState.Resolved, AlertLifecycleAction.Reopen) => AlertLifecycleState.Open,
            (not AlertLifecycleState.Superseded, AlertLifecycleAction.Supersede) => AlertLifecycleState.Superseded,
            (_, AlertLifecycleAction.SourceDeleted) => current,
            _ => current,
        };
        return next != current || action == AlertLifecycleAction.SourceDeleted;
    }
}

public static partial class AlertLifecycleValidation
{
    private const int MaximumCommentScalars = 256;
    private static readonly string[] SensitiveMarkers =
    [
        "authorization:", "bearer ", "basic ", "api_key", "api-key", "api.key", "apikey", "credential",
        "password", "secret", "token=", "token:", "prompt:", "response:", "content:", "tool argument", "tool result"
    ];

    public static bool IsReasonCode(string? value) => value is { Length: >= 1 and <= 64 }
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    public static bool IsIdempotencyKey(string? value) => value is { Length: 48 }
        && value.StartsWith("aid1_", StringComparison.Ordinal)
        && value.AsSpan(5).ToArray().All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    public static bool IsCanonicalAlertId(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsSanitizedComment(string? value)
    {
        if (value is null) return true;
        if (value.Length == 0 || value.EnumerateRunes().Count() > MaximumCommentScalars) return false;
        if (value.Any(character => char.IsControl(character) || character is '<' or '>' or '`' or '[' or ']' or '{' or '}')) return false;
        if (value.Contains('/') || value.Contains('\\') || UriOrDrivePrefix().IsMatch(value) || Email().IsMatch(value)) return false;
        return !SensitiveMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.IgnoreCase)]
    private static partial Regex UriOrDrivePrefix();

    [GeneratedRegex(@"\b[^\s@]+@[^\s@]+\.[^\s@]+\b")]
    private static partial Regex Email();
}
