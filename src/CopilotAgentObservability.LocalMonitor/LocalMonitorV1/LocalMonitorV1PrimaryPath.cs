namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal static class LocalMonitorV1Identity
{
    internal static bool TryParseUuidV7(string? value, out Guid id)
    {
        id = default;
        if (value is null
            || value.Length != 36
            || value[8] != '-'
            || value[13] != '-'
            || value[18] != '-'
            || value[23] != '-'
            || value[14] != '7'
            || value[19] is not ('8' or '9' or 'a' or 'b'))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (index is 8 or 13 or 18 or 23) continue;
            if (value[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }

        return Guid.TryParseExact(value, "D", out id)
            && string.Equals(id.ToString("D"), value, StringComparison.Ordinal);
    }

    internal static bool IsCanonicalNodeId(string? value)
    {
        if (value is null
            || value.Length != 37
            || !value.StartsWith("node-", StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = 5; index < value.Length; index++)
        {
            if (value[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }

        return true;
    }
}

internal enum LocalMonitorV1PathClassification
{
    Matched,
    MatchedInvalid,
    NearPath,
}

internal enum LocalMonitorV1PrimaryRouteKind
{
    RepositorySelection,
    RepositorySessions,
    AllSessions,
    UnassignedSessions,
    SessionDetail,
    ComparisonDetail,
}

internal sealed record LocalMonitorV1PrimaryPathResult(
    LocalMonitorV1PathClassification Classification,
    LocalMonitorV1PrimaryRouteKind? RouteKind = null,
    string? RepositoryId = null,
    string? SessionId = null,
    string? ComparisonId = null);

internal static class LocalMonitorV1PrimaryPathParser
{
    private static readonly LocalMonitorV1PrimaryPathResult NearPath = new(LocalMonitorV1PathClassification.NearPath);
    private static readonly LocalMonitorV1PrimaryPathResult MatchedInvalid = new(LocalMonitorV1PathClassification.MatchedInvalid);

    internal static LocalMonitorV1PrimaryPathResult Classify(string? rawPath)
    {
        if (string.Equals(rawPath, "/", StringComparison.Ordinal))
            return Matched(LocalMonitorV1PrimaryRouteKind.RepositorySelection);
        if (string.Equals(rawPath, "/sessions", StringComparison.Ordinal))
            return Matched(LocalMonitorV1PrimaryRouteKind.AllSessions);
        if (string.Equals(rawPath, "/sessions/unassigned", StringComparison.Ordinal))
            return Matched(LocalMonitorV1PrimaryRouteKind.UnassignedSessions);
        if (string.IsNullOrEmpty(rawPath) || rawPath[0] != '/') return NearPath;

        var segments = rawPath.Split('/');
        if (segments.Length == 3
            && segments[0].Length == 0
            && string.Equals(segments[1], "sessions", StringComparison.Ordinal))
        {
            var sessionId = segments[2];
            if (sessionId.Length == 0
                || string.Equals(sessionId, "unassigned", StringComparison.OrdinalIgnoreCase)
                || HasEncodedSeparator(sessionId))
            {
                return NearPath;
            }

            return LocalMonitorV1Identity.TryParseUuidV7(sessionId, out _)
                ? Matched(LocalMonitorV1PrimaryRouteKind.SessionDetail, sessionId: sessionId)
                : MatchedInvalid;
        }

        if (segments.Length == 4
            && segments[0].Length == 0
            && string.Equals(segments[1], "repositories", StringComparison.Ordinal)
            && string.Equals(segments[3], "sessions", StringComparison.Ordinal))
        {
            var repositoryId = segments[2];
            if (repositoryId.Length == 0 || HasEncodedSeparator(repositoryId)) return NearPath;
            return LocalMonitorV1Identity.TryParseUuidV7(repositoryId, out _)
                ? Matched(LocalMonitorV1PrimaryRouteKind.RepositorySessions, repositoryId: repositoryId)
                : MatchedInvalid;
        }

        if (segments.Length == 5
            && segments[0].Length == 0
            && string.Equals(segments[1], "repositories", StringComparison.Ordinal)
            && string.Equals(segments[3], "comparisons", StringComparison.Ordinal))
        {
            var repositoryId = segments[2];
            var comparisonId = segments[4];
            if (repositoryId.Length == 0
                || comparisonId.Length == 0
                || HasEncodedSeparator(repositoryId)
                || HasEncodedSeparator(comparisonId))
            {
                return NearPath;
            }

            return LocalMonitorV1Identity.TryParseUuidV7(repositoryId, out _)
                && LocalMonitorV1Identity.TryParseUuidV7(comparisonId, out _)
                ? Matched(LocalMonitorV1PrimaryRouteKind.ComparisonDetail, repositoryId, comparisonId: comparisonId)
                : MatchedInvalid;
        }

        return NearPath;
    }

    private static LocalMonitorV1PrimaryPathResult Matched(
        LocalMonitorV1PrimaryRouteKind kind,
        string? repositoryId = null,
        string? sessionId = null,
        string? comparisonId = null) =>
        new(LocalMonitorV1PathClassification.Matched, kind, repositoryId, sessionId, comparisonId);

    private static bool HasEncodedSeparator(string value) =>
        value.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        || value.Contains("%5c", StringComparison.OrdinalIgnoreCase);
}
