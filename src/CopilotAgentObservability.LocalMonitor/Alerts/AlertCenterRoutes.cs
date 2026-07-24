using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal static class AlertCenterRoutes
{
    private const int MaximumEvaluationRequestBytes = 4_096;
    private static readonly HashSet<string> AllowedQueryMembers = new(StringComparer.Ordinal)
    {
        "alert_id", "session_id", "trace_id", "severity", "state", "rule_id", "source_surface",
        "repository", "workspace", "completeness", "period", "from", "to", "offset", "limit",
    };
    private static readonly HashSet<string> AllowedQueryMembersV2 = new(StringComparer.Ordinal)
    {
        "alert_id", "session_id", "trace_id", "severity", "state", "rule_id", "source_surface",
        "repository", "workspace", "completeness", "period", "from", "to", "receipt_kind",
        "scope_kind", "currency", "coverage_state", "cursor", "limit",
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static bool IsPath(PathString path) =>
        path.StartsWithSegments("/api/alert-center/v1")
        || path.StartsWithSegments("/api/alert-center/v2")
        || string.Equals(path.Value, "/alerts", StringComparison.Ordinal);

    internal static void Map(
        WebApplication app,
        IAlertCenterReadModel readModel,
        IAlertCenterReadModelV2 readModelV2,
        IAlertCenterEvaluationCoordinator evaluationCoordinator,
        TimeProvider timeProvider)
    {
        app.MapGet("/api/alert-center/v1/alerts", context => ReadAsync(context, readModel, timeProvider));
        app.MapGet("/api/alert-center/v2/alerts", context => ReadV2Async(context, readModelV2, timeProvider));
        app.MapPost("/api/alert-center/v1/evaluations", context => EvaluateAsync(context, evaluationCoordinator));
    }

    private static async Task ReadV2Async(
        HttpContext context,
        IAlertCenterReadModelV2 readModel,
        TimeProvider timeProvider)
    {
        Prepare(context.Response);
        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await WriteErrorV2Async(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
            return;
        }
        if (!TryParseQueryV2(context.Request, timeProvider, out var query))
        {
            await WriteErrorV2Async(context, StatusCodes.Status400BadRequest, "alert_center_invalid_query");
            return;
        }
        var result = readModel.Read(query!);
        if (result.Status != AlertCenterReadStatusV2.Success || result.Snapshot is null)
        {
            var (status, code) = result.Status switch
            {
                AlertCenterReadStatusV2.SnapshotChanged =>
                    (StatusCodes.Status409Conflict, "alert_center_snapshot_changed"),
                AlertCenterReadStatusV2.InvalidQuery =>
                    (StatusCodes.Status400BadRequest, "alert_center_invalid_query"),
                AlertCenterReadStatusV2.ResponseTooLarge =>
                    (StatusCodes.Status503ServiceUnavailable, "alert_center_response_too_large"),
                AlertCenterReadStatusV2.Busy =>
                    (StatusCodes.Status503ServiceUnavailable, "alert_center_store_busy"),
                _ => (StatusCodes.Status503ServiceUnavailable, "alert_center_store_unavailable"),
            };
            await WriteErrorV2Async(context, status, code);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        await JsonSerializer.SerializeAsync(context.Response.Body, result.Snapshot, Json, context.RequestAborted);
    }

    private static Task WriteErrorV2Async(HttpContext context, int status, string code)
    {
        Prepare(context.Response);
        context.Response.StatusCode = status;
        return context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes($"{{\"schema_version\":\"{AlertCenterContractVersions.ErrorV2}\",\"error\":\"{code}\"}}"),
            context.RequestAborted).AsTask();
    }

    internal static Task WriteErrorAsync(HttpContext context, int status, string code)
    {
        Prepare(context.Response);
        context.Response.StatusCode = status;
        return context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes($"{{\"schema_version\":\"{AlertCenterContractVersions.Center}\",\"error\":\"{code}\"}}"),
            context.RequestAborted).AsTask();
    }

    internal static Task WriteErrorForPathAsync(
        HttpContext context,
        int status,
        string code) =>
        context.Request.Path.StartsWithSegments("/api/alert-center/v2")
            ? WriteErrorV2Async(context, status, code)
            : WriteErrorAsync(context, status, code);

    private static async Task ReadAsync(HttpContext context, IAlertCenterReadModel readModel, TimeProvider timeProvider)
    {
        Prepare(context.Response);
        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
            return;
        }
        if (!TryParseQuery(context.Request, timeProvider, out var query))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "alert_center_invalid_query");
            return;
        }
        var result = readModel.Read(query!);
        if (result.Status != AlertCenterReadStatus.Success || result.Snapshot is null)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                result.Status == AlertCenterReadStatus.Busy ? "alert_center_store_busy" : "alert_center_store_unavailable");
            return;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        await JsonSerializer.SerializeAsync(context.Response.Body, result.Snapshot, Json, context.RequestAborted);
    }

    private static async Task EvaluateAsync(
        HttpContext context,
        IAlertCenterEvaluationCoordinator coordinator)
    {
        Prepare(context.Response);
        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
            return;
        }
        if (!MonitorHost.HasMonitorCsrfHeader(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "csrf_required");
            return;
        }
        if (!string.Equals(
                context.Request.ContentType?.Split(';', 2)[0],
                MediaTypeNames.Application.Json,
                StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
            return;
        }
        if (context.Request.Query.Count != 0)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "alert_center_invalid_request");
            return;
        }
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryParseEvaluationRequest(body, out var sessionId, out var traceId))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "alert_center_invalid_request");
            return;
        }

        AlertCenterEvaluationResult result;
        try
        {
            result = coordinator.Evaluate(sessionId, traceId!);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            result = new(AlertCenterEvaluationStatus.StoreUnavailable);
        }
        if (result.Status == AlertCenterEvaluationStatus.Success && result.Response is not null)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await JsonSerializer.SerializeAsync(context.Response.Body, result.Response, Json, context.RequestAborted);
            return;
        }

        var (status, code) = result.Status switch
        {
            AlertCenterEvaluationStatus.SessionNotFound => (StatusCodes.Status404NotFound, "alert_center_session_not_found"),
            AlertCenterEvaluationStatus.TraceNotFound => (StatusCodes.Status404NotFound, "alert_center_trace_not_found"),
            AlertCenterEvaluationStatus.TraceNotOwned => (StatusCodes.Status404NotFound, "alert_center_trace_not_owned"),
            AlertCenterEvaluationStatus.SourcePartitionMissing => (StatusCodes.Status409Conflict, "alert_center_source_partition_missing"),
            AlertCenterEvaluationStatus.SourcePartitionAmbiguous => (StatusCodes.Status409Conflict, "alert_center_source_partition_ambiguous"),
            AlertCenterEvaluationStatus.TraceIncomplete => (StatusCodes.Status409Conflict, "alert_center_trace_incomplete"),
            AlertCenterEvaluationStatus.StoreBusy => (StatusCodes.Status503ServiceUnavailable, "alert_center_store_busy"),
            AlertCenterEvaluationStatus.StoreConflict => (StatusCodes.Status409Conflict, "alert_center_store_conflict"),
            AlertCenterEvaluationStatus.ContractRejected => (StatusCodes.Status409Conflict, "alert_center_contract_rejected"),
            _ => (StatusCodes.Status503ServiceUnavailable, "alert_center_store_unavailable"),
        };
        await WriteErrorAsync(context, status, code);
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaximumEvaluationRequestBytes)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large");
            return null;
        }
        await using var buffer = new MemoryStream();
        var chunk = new byte[1_024];
        var total = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            total += read;
            if (total > MaximumEvaluationRequestBytes)
            {
                await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large");
                return null;
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
        }
        return buffer.ToArray();
    }

    private static bool TryParseEvaluationRequest(byte[] body, out Guid sessionId, out string? traceId)
    {
        sessionId = Guid.Empty;
        traceId = null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = root.EnumerateObject().Select(item => item.Name).ToArray();
            if (names.Length != 3
                || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || names.Except(["schema_version", "session_id", "trace_id"], StringComparer.Ordinal).Any()) return false;
            if (!TryString(root, "schema_version", out var schemaVersion)
                || schemaVersion != AlertCenterContractVersions.EvaluationRequest
                || !TryString(root, "session_id", out var sessionText)
                || !TryCanonicalUuidV7(sessionText!, out sessionId)
                || !TryString(root, "trace_id", out traceId)
                || !OpaqueId(traceId!)) return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryString(JsonElement root, string name, out string? value)
    {
        var item = root.GetProperty(name);
        value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        return value is not null;
    }

    private static bool TryCanonicalUuidV7(string value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id)
        && id != Guid.Empty
        && value.Length == 36
        && value[14] == '7'
        && value[19] is '8' or '9' or 'a' or 'b'
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    private static bool TryParseQuery(HttpRequest request, TimeProvider timeProvider, out AlertCenterQuery? query)
    {
        query = null;
        if (request.Query.Any(pair => !AllowedQueryMembers.Contains(pair.Key) || pair.Value.Count != 1)) return false;
        string? Value(string name) => request.Query.TryGetValue(name, out var value) ? value[0] : null;
        var alertId = Value("alert_id");
        var sessionId = Value("session_id");
        var traceId = Value("trace_id");
        var severity = Value("severity");
        var state = Value("state");
        var ruleId = Value("rule_id");
        var source = Value("source_surface");
        var repository = Value("repository");
        var workspace = Value("workspace");
        var completeness = Value("completeness");
        if (alertId is not null && !CanonicalHash(alertId)
            || sessionId is not null && !OpaqueId(sessionId)
            || traceId is not null && !OpaqueId(traceId)
            || severity is not null && severity is not ("critical" or "warning" or "info")
            || state is not null && state is not ("open" or "acknowledged" or "dismissed" or "resolved" or "superseded")
            || ruleId is not null && !Token(ruleId)
            || source is not null && !Token(source)
            || repository is not null && !Label(repository)
            || workspace is not null && !Label(workspace)
            || completeness is not null && completeness is not ("unbound" or "partial" or "rich" or "full")) return false;

        var period = Value("period");
        var fromText = Value("from");
        var toText = Value("to");
        DateOnly from;
        DateOnly to;
        if (fromText is not null || toText is not null)
        {
            if (period is not null || fromText is null || toText is null
                || !DateOnly.TryParseExact(fromText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from)
                || !DateOnly.TryParseExact(toText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out to)
                || from > to || to.DayNumber - from.DayNumber >= 366) return false;
        }
        else
        {
            period ??= "30d";
            if (period is not ("today" or "7d" or "30d")) return false;
            to = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
            from = period switch { "today" => to, "7d" => to.AddDays(-6), _ => to.AddDays(-29) };
        }

        if (!TryInteger(Value("offset"), 0, 1_000_000, 0, out var offset)
            || !TryInteger(Value("limit"), 1, 100, 50, out var limit)) return false;
        query = new(alertId, sessionId, traceId, severity, state, ruleId, source, repository, workspace, completeness, from, to, offset, limit);
        return true;
    }

    private static bool TryParseQueryV2(
        HttpRequest request,
        TimeProvider timeProvider,
        out AlertCenterQueryV2? query)
    {
        query = null;
        if (!ValidEncodedQueryV2(request.QueryString.Value)
            || request.Query.Any(pair =>
                !AllowedQueryMembersV2.Contains(pair.Key)
                || pair.Value.Count != 1
                || pair.Value[0] is null
                || pair.Value[0]!.Length == 0))
        {
            return false;
        }
        string? Value(string name) => request.Query.TryGetValue(name, out var value) ? value[0] : null;
        var alertId = Value("alert_id");
        var sessionId = Value("session_id");
        var traceId = Value("trace_id");
        var severity = Value("severity");
        var state = Value("state");
        var ruleId = Value("rule_id");
        var source = Value("source_surface");
        var repository = Value("repository");
        var workspace = Value("workspace");
        var completeness = Value("completeness");
        var receiptKind = Value("receipt_kind") ?? "all";
        var scopeKind = Value("scope_kind") ?? "all";
        var currency = Value("currency") ?? "all";
        var coverageState = Value("coverage_state") ?? "all";
        var cursor = Value("cursor");
        if (alertId is not null && !CanonicalHash(alertId)
            || sessionId is not null && !OpaqueId(sessionId)
            || traceId is not null && !OpaqueId(traceId)
            || severity is not null && severity is not ("critical" or "warning" or "info")
            || state is not null && state is not ("open" or "acknowledged" or "dismissed" or "resolved" or "superseded")
            || ruleId is not null && !Token(ruleId)
            || source is not null && !Token(source)
            || repository is not null && !Label(repository)
            || workspace is not null && !Label(workspace)
            || completeness is not null && completeness is not ("unbound" or "partial" or "rich" or "full")
            || receiptKind is not ("all" or "receipt_v1" or "cost_receipt_v2")
            || scopeKind is not ("all" or "session" or "utc_day" or "rolling_period")
            || currency is not ("all" or "USD")
            || coverageState is not ("all" or "full" or "partial")
            || cursor is not null && (cursor.Length > 1_024
                || !cursor.StartsWith("alert-center-cursor-v2.", StringComparison.Ordinal)
                || cursor.Any(character => character is '%' or >= (char)0x80)))
        {
            return false;
        }

        var period = Value("period");
        var fromText = Value("from");
        var toText = Value("to");
        DateOnly from;
        DateOnly to;
        if (fromText is not null || toText is not null)
        {
            if (period is not null || fromText is null || toText is null
                || !DateOnly.TryParseExact(fromText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out from)
                || !DateOnly.TryParseExact(toText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out to)
                || from > to || to.DayNumber - from.DayNumber >= 366)
            {
                return false;
            }
        }
        else
        {
            period ??= "30d";
            if (period is not ("today" or "7d" or "30d")) return false;
            to = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
            from = period switch { "today" => to, "7d" => to.AddDays(-6), _ => to.AddDays(-29) };
        }
        if (!TryInteger(Value("limit"), 1, 100, 50, out var limit)) return false;
        query = new(
            alertId, sessionId, traceId, severity, state, ruleId, source, repository,
            workspace, completeness, from, to, receiptKind, scopeKind, currency,
            coverageState, cursor, limit);
        return true;
    }

    private static bool ValidEncodedQueryV2(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString)) return true;
        var raw = queryString[0] == '?' ? queryString[1..] : queryString;
        if (Encoding.UTF8.GetByteCount(raw) > 8_192) return false;
        var filters = new List<string>();
        var cursorCount = 0;
        foreach (var component in raw.Split('&'))
        {
            if (component.Length == 0) return false;
            var separator = component.IndexOf('=');
            var encodedName = separator < 0 ? component : component[..separator];
            if (!encodedName.Equals("cursor", StringComparison.Ordinal)
                && Uri.UnescapeDataString(encodedName).Equals("cursor", StringComparison.Ordinal))
            {
                return false;
            }
            if (component.StartsWith("cursor=", StringComparison.Ordinal))
            {
                cursorCount++;
                if (component.Length == "cursor=".Length
                    || component["cursor=".Length..].Contains('%', StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else
            {
                filters.Add(component);
            }
        }
        return cursorCount <= 1
            && Encoding.UTF8.GetByteCount(string.Join('&', filters)) <= 7_000;
    }

    private static bool TryInteger(string? value, int minimum, int maximum, int fallback, out int result)
    {
        result = fallback;
        return value is null || int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= minimum && result <= maximum;
    }

    private static bool CanonicalHash(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Token(string value) => value.Length is >= 1 and <= 128
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static bool OpaqueId(string value) => value.Length is >= 1 and <= 256 && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '\\' or '?' or '#');
    private static bool Label(string value) => AlertCenterLabelGuard.Accepts(value);
    private static void Prepare(HttpResponse response)
    {
        response.ContentType = "application/json";
        response.Headers.CacheControl = "no-store";
    }
}

internal static class AlertCenterLabelGuard
{
    internal static bool Accepts(string? value) => value is { Length: >= 1 and <= 256 }
        && !string.IsNullOrWhiteSpace(value)
        && !value.Any(char.IsControl)
        && string.Equals(MeasurementSanitizer.SanitizeFreeFormName(value), value, StringComparison.Ordinal)
        && !LooksLikePathOrCredential(value);

    private static bool LooksLikePathOrCredential(string value) =>
        value.Contains('/')
        || value.Contains('\\')
        || value.StartsWith("~", StringComparison.Ordinal)
        || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        || value.Length >= 2 && IsAsciiLetter(value[0]) && value[1] == ':'
        || ContainsSensitiveMarker(value, "authorization")
        || ContainsSensitiveMarker(value, "credential")
        || ContainsSensitiveMarker(value, "token")
        || ContainsSensitiveMarker(value, "api-key")
        || ContainsSensitiveMarker(value, "apikey");

    private static bool ContainsSensitiveMarker(string value, string marker)
    {
        var start = 0;
        while (start < value.Length)
        {
            var index = value.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var end = index + marker.Length;
            if ((index == 0 || !char.IsLetterOrDigit(value[index - 1]))
                && (end == value.Length || !char.IsLetterOrDigit(value[end])))
            {
                return true;
            }
            start = index + 1;
        }
        return false;
    }

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
