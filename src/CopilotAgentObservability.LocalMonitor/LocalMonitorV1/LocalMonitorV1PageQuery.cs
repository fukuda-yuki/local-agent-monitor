using System.Globalization;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal sealed record LocalMonitorV1PageQuery
{
    internal required LocalMonitorV1PrimaryRouteKind RouteKind { get; init; }
    internal DateTimeOffset? From { get; init; }
    internal DateTimeOffset? To { get; init; }
    internal IReadOnlyList<string> Sources { get; init; } = [];
    internal IReadOnlyList<string> Statuses { get; init; } = [];
    internal bool? HasSkill { get; init; }
    internal bool? HasSubagent { get; init; }
    internal bool? HasError { get; init; }
    internal bool? HasRetry { get; init; }
    internal string ArchiveScope { get; init; } = "active_only";
    internal string? Cursor { get; init; }
    internal bool CompareMode { get; init; }
    internal string? ExecutionId { get; init; }
    internal string? NodeId { get; init; }
    internal string? AnalysisId { get; init; }
    internal string? Settings { get; init; }
}

internal static class LocalMonitorV1PageQueryParser
{
    private static readonly HashSet<string> Settings = new(
        ["state", "receiver", "ai", "repositories", "archive", "storage", "diagnostics"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> Sources = new(
        ["copilot-sdk", "copilot-cli", "vscode", "hook-unknown", "claude-code"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> Statuses = new(
        ["active", "completed", "failed", "unknown"],
        StringComparer.Ordinal);

    internal static bool TryParse(
        LocalMonitorV1PrimaryRouteKind routeKind,
        string? rawQuery,
        out LocalMonitorV1PageQuery? query)
    {
        query = null;
        if (!TryComponents(rawQuery, out var components)) return false;

        return routeKind switch
        {
            LocalMonitorV1PrimaryRouteKind.RepositorySelection or
            LocalMonitorV1PrimaryRouteKind.ComparisonDetail =>
                TrySelection(routeKind, components, out query),
            LocalMonitorV1PrimaryRouteKind.SessionDetail =>
                TrySessionDetail(components, out query),
            LocalMonitorV1PrimaryRouteKind.RepositorySessions or
            LocalMonitorV1PrimaryRouteKind.AllSessions or
            LocalMonitorV1PrimaryRouteKind.UnassignedSessions =>
                TryExplorer(routeKind, components, out query),
            _ => false,
        };
    }

    private static bool TrySelection(
        LocalMonitorV1PrimaryRouteKind routeKind,
        IReadOnlyList<KeyValuePair<string, string>> components,
        out LocalMonitorV1PageQuery? query)
    {
        query = null;
        if (!OnlyKeys(components, "settings")
            || !TrySingleton(components, "settings", out var settings)
            || settings is not null && !Settings.Contains(settings))
        {
            return false;
        }

        query = new() { RouteKind = routeKind, Settings = settings };
        return true;
    }

    private static bool TrySessionDetail(
        IReadOnlyList<KeyValuePair<string, string>> components,
        out LocalMonitorV1PageQuery? query)
    {
        query = null;
        if (!OnlyKeys(components, "execution", "node", "analysis", "settings")
            || !TrySingleton(components, "execution", out var execution)
            || !TrySingleton(components, "node", out var node)
            || !TrySingleton(components, "analysis", out var analysis)
            || !TrySingleton(components, "settings", out var settings)
            || execution is not null && !LocalMonitorV1Identity.TryParseUuidV7(execution, out _)
            || node is not null && !LocalMonitorV1Identity.IsCanonicalNodeId(node)
            || analysis is not null && !LocalMonitorV1Identity.TryParseUuidV7(analysis, out _)
            || settings is not null && !Settings.Contains(settings))
        {
            return false;
        }

        query = new()
        {
            RouteKind = LocalMonitorV1PrimaryRouteKind.SessionDetail,
            ExecutionId = execution,
            NodeId = node,
            AnalysisId = analysis,
            Settings = settings,
        };
        return true;
    }

    private static bool TryExplorer(
        LocalMonitorV1PrimaryRouteKind routeKind,
        IReadOnlyList<KeyValuePair<string, string>> components,
        out LocalMonitorV1PageQuery? query)
    {
        query = null;
        if (!OnlyKeys(
                components,
                "from", "to", "source", "status", "has_skill", "has_subagent",
                "has_error", "has_retry", "archive_scope", "cursor", "mode", "settings")
            || !TrySingleton(components, "from", out var fromText)
            || !TrySingleton(components, "to", out var toText)
            || !TrySingleton(components, "has_skill", out var hasSkillText)
            || !TrySingleton(components, "has_subagent", out var hasSubagentText)
            || !TrySingleton(components, "has_error", out var hasErrorText)
            || !TrySingleton(components, "has_retry", out var hasRetryText)
            || !TrySingleton(components, "archive_scope", out var archiveScopeText)
            || !TrySingleton(components, "cursor", out var cursor)
            || !TrySingleton(components, "mode", out var mode)
            || !TrySingleton(components, "settings", out var settings)
            || !TryRepeated(components, "source", Sources, out var sources)
            || !TryRepeated(components, "status", Statuses, out var statuses)
            || !TryTimestamp(fromText, out var from)
            || !TryTimestamp(toText, out var to)
            || from is not null && to is not null && from >= to
            || !TryBoolean(hasSkillText, out var hasSkill)
            || !TryBoolean(hasSubagentText, out var hasSubagent)
            || !TryBoolean(hasErrorText, out var hasError)
            || !TryBoolean(hasRetryText, out var hasRetry)
            || archiveScopeText is not null and not ("active_only" or "include_archived")
            || cursor is not null && !LocalMonitorV1SessionCursorCodec.IsStructurallyCanonical(cursor)
            || mode is not null and not "compare"
            || settings is not null && !Settings.Contains(settings))
        {
            return false;
        }

        query = new()
        {
            RouteKind = routeKind,
            From = from,
            To = to,
            Sources = sources,
            Statuses = statuses,
            HasSkill = hasSkill,
            HasSubagent = hasSubagent,
            HasError = hasError,
            HasRetry = hasRetry,
            ArchiveScope = archiveScopeText ?? "active_only",
            Cursor = cursor,
            CompareMode = mode is not null,
            Settings = settings,
        };
        return true;
    }

    private static bool TryComponents(
        string? rawQuery,
        out IReadOnlyList<KeyValuePair<string, string>> components)
    {
        components = [];
        if (string.IsNullOrEmpty(rawQuery) || string.Equals(rawQuery, "?", StringComparison.Ordinal)) return true;
        if (rawQuery[0] != '?') return false;

        var parsed = new List<KeyValuePair<string, string>>();
        foreach (var component in rawQuery[1..].Split('&'))
        {
            var equals = component.IndexOf('=');
            if (equals <= 0
                || equals != component.LastIndexOf('=')
                || equals == component.Length - 1
                || component.Contains(';', StringComparison.Ordinal)
                || component.Any(char.IsWhiteSpace))
            {
                return false;
            }

            parsed.Add(KeyValuePair.Create(component[..equals], component[(equals + 1)..]));
        }

        components = parsed;
        return true;
    }

    private static bool OnlyKeys(
        IReadOnlyList<KeyValuePair<string, string>> components,
        params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        return components.All(component => set.Contains(component.Key));
    }

    private static bool TrySingleton(
        IReadOnlyList<KeyValuePair<string, string>> components,
        string key,
        out string? value)
    {
        value = null;
        foreach (var component in components)
        {
            if (!string.Equals(component.Key, key, StringComparison.Ordinal)) continue;
            if (value is not null) return false;
            value = component.Value;
        }

        return true;
    }

    private static bool TryRepeated(
        IReadOnlyList<KeyValuePair<string, string>> components,
        string key,
        IReadOnlySet<string> allowed,
        out IReadOnlyList<string> values)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            if (!string.Equals(component.Key, key, StringComparison.Ordinal)) continue;
            if (set.Count == 16 || !allowed.Contains(component.Value) || !set.Add(component.Value))
            {
                values = [];
                return false;
            }
        }

        values = Array.AsReadOnly(set.ToArray());
        return true;
    }

    private static bool TryTimestamp(string? value, out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (value is null) return true;
        if (value.Length != 35
            || !value.EndsWith("%2B00:00", StringComparison.Ordinal))
        {
            return false;
        }

        var decoded = string.Concat(value.AsSpan(0, 27), "+00:00");
        if (!DateTimeOffset.TryParseExact(
                decoded,
                "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero
            || !string.Equals(
                parsed.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
                decoded,
                StringComparison.Ordinal))
        {
            return false;
        }

        timestamp = parsed;
        return true;
    }

    private static bool TryBoolean(string? value, out bool? parsed)
    {
        parsed = value switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
        return value is null or "true" or "false";
    }
}

internal static class LocalMonitorV1CanonicalUrlBuilder
{
    internal static string Build(LocalMonitorV1PrimaryPathResult path, LocalMonitorV1PageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Cursor is not null)
            throw new InvalidOperationException("local_monitor_v1_cursor_eligibility_unproven");
        return Build(path, query, includeCursor: false);
    }

    internal static string Build(
        LocalMonitorV1PrimaryPathResult path,
        LocalMonitorV1PageQuery query,
        LocalMonitorV1SessionSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MatchesSafeExplorerState(path, query, request))
            throw new InvalidOperationException("local_monitor_v1_request_url_state_mismatch");
        var includeCursor = LocalMonitorV1UrlState.IsCursorEligible(request)
            && string.Equals(query.Cursor, request.Cursor, StringComparison.Ordinal);
        if (LocalMonitorV1UrlState.IsCursorEligible(request)
            && !string.Equals(query.Cursor, request.Cursor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("local_monitor_v1_request_url_state_mismatch");
        }
        return Build(path, query, includeCursor);
    }

    private static string Build(
        LocalMonitorV1PrimaryPathResult path,
        LocalMonitorV1PageQuery query,
        bool includeCursor)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(query);
        if (path.Classification != LocalMonitorV1PathClassification.Matched
            || path.RouteKind != query.RouteKind)
        {
            throw new InvalidOperationException("local_monitor_v1_url_state_invalid");
        }

        var rawPath = path.RouteKind switch
        {
            LocalMonitorV1PrimaryRouteKind.RepositorySelection => "/",
            LocalMonitorV1PrimaryRouteKind.RepositorySessions => $"/repositories/{path.RepositoryId}/sessions",
            LocalMonitorV1PrimaryRouteKind.AllSessions => "/sessions",
            LocalMonitorV1PrimaryRouteKind.UnassignedSessions => "/sessions/unassigned",
            LocalMonitorV1PrimaryRouteKind.SessionDetail => $"/sessions/{path.SessionId}",
            LocalMonitorV1PrimaryRouteKind.ComparisonDetail => $"/repositories/{path.RepositoryId}/comparisons/{path.ComparisonId}",
            _ => throw new InvalidOperationException("local_monitor_v1_url_state_invalid"),
        };

        var components = new List<string>();
        if (query.RouteKind is LocalMonitorV1PrimaryRouteKind.RepositorySessions
            or LocalMonitorV1PrimaryRouteKind.AllSessions
            or LocalMonitorV1PrimaryRouteKind.UnassignedSessions)
        {
            AddTimestamp(components, "from", query.From);
            AddTimestamp(components, "to", query.To);
            AddRepeated(components, "source", query.Sources);
            AddRepeated(components, "status", query.Statuses);
            AddBoolean(components, "has_skill", query.HasSkill);
            AddBoolean(components, "has_subagent", query.HasSubagent);
            AddBoolean(components, "has_error", query.HasError);
            AddBoolean(components, "has_retry", query.HasRetry);
            if (!string.Equals(query.ArchiveScope, "active_only", StringComparison.Ordinal))
                components.Add($"archive_scope={query.ArchiveScope}");
            if (includeCursor && query.Cursor is not null) components.Add($"cursor={query.Cursor}");
            if (query.CompareMode) components.Add("mode=compare");
        }
        else if (query.RouteKind == LocalMonitorV1PrimaryRouteKind.SessionDetail)
        {
            if (query.ExecutionId is not null) components.Add($"execution={query.ExecutionId}");
            if (query.NodeId is not null) components.Add($"node={query.NodeId}");
            if (query.AnalysisId is not null) components.Add($"analysis={query.AnalysisId}");
        }

        if (query.Settings is not null) components.Add($"settings={query.Settings}");
        return components.Count == 0 ? rawPath : $"{rawPath}?{string.Join('&', components)}";
    }

    private static void AddTimestamp(List<string> components, string key, DateTimeOffset? value)
    {
        if (value is null) return;
        components.Add($"{key}={value.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}%2B00:00");
    }

    private static void AddRepeated(List<string> components, string key, IReadOnlyList<string> values)
    {
        foreach (var value in values.Order(StringComparer.Ordinal)) components.Add($"{key}={value}");
    }

    private static void AddBoolean(List<string> components, string key, bool? value)
    {
        if (value is null) return;
        components.Add($"{key}={(value.Value ? "true" : "false")}");
    }

    private static bool MatchesSafeExplorerState(
        LocalMonitorV1PrimaryPathResult path,
        LocalMonitorV1PageQuery query,
        LocalMonitorV1SessionSearchRequest request)
    {
        if (path.Classification != LocalMonitorV1PathClassification.Matched
            || path.RouteKind != query.RouteKind
            || query.RouteKind is not (
                LocalMonitorV1PrimaryRouteKind.RepositorySessions
                or LocalMonitorV1PrimaryRouteKind.AllSessions
                or LocalMonitorV1PrimaryRouteKind.UnassignedSessions))
        {
            return false;
        }

        var scopeMatches = query.RouteKind switch
        {
            LocalMonitorV1PrimaryRouteKind.RepositorySessions =>
                request.Scope == "repository"
                && string.Equals(request.RepositoryId, path.RepositoryId, StringComparison.Ordinal),
            LocalMonitorV1PrimaryRouteKind.AllSessions =>
                request.Scope == "all" && request.RepositoryId is null,
            LocalMonitorV1PrimaryRouteKind.UnassignedSessions =>
                request.Scope == "unassigned" && request.RepositoryId is null,
            _ => false,
        };

        return scopeMatches
            && request.From == query.From
            && request.To == query.To
            && request.Sources.SequenceEqual(query.Sources, StringComparer.Ordinal)
            && request.Statuses.SequenceEqual(query.Statuses, StringComparer.Ordinal)
            && request.HasSkill == query.HasSkill
            && request.HasSubagent == query.HasSubagent
            && request.HasError == query.HasError
            && request.HasRetry == query.HasRetry
            && string.Equals(request.ArchiveScope, query.ArchiveScope, StringComparison.Ordinal);
    }
}
