using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal static class AlertLifecycleRoutes
{
    private const int MaximumBodyBytes = 65_536;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal static bool IsAlertPath(PathString path) => path.StartsWithSegments("/api/alerts/v1");

    internal static void Map(WebApplication app, IAlertLifecycleStore store)
    {
        app.MapGet("/api/alerts/v1/{alertId}/lifecycle", (string alertId, HttpContext context) => ReadAsync(context, store, alertId));
        app.MapGet("/api/alerts/v1/{alertId}/lifecycle/history", (string alertId, HttpContext context) => HistoryAsync(context, store, alertId));
        app.MapPost("/api/alerts/v1/{alertId}/lifecycle/actions", (string alertId, HttpContext context) => MutateAsync(context, store, alertId));
    }

    internal static Task WriteErrorAsync(HttpContext context, int status, string code)
    {
        Prepare(context.Response);
        context.Response.StatusCode = status;
        return context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{{\"schema_version\":\"alert.lifecycle.v1\",\"error\":\"{code}\"}}"), context.RequestAborted).AsTask();
    }

    private static async Task ReadAsync(HttpContext context, IAlertLifecycleStore store, string alertId)
    {
        if (!await AuthorizeReadAsync(context)) return;
        if (!EnsureInitialized(store, out var initialization)) { await WriteStoreErrorAsync(context, initialization); return; }
        var result = store.Get(alertId);
        if (result.Status != AlertLifecycleStoreStatus.Success) { await WriteStoreErrorAsync(context, result); return; }
        await WriteJsonAsync(context, LifecycleDto(result.Lifecycle!));
    }

    private static async Task HistoryAsync(HttpContext context, IAlertLifecycleStore store, string alertId)
    {
        if (!await AuthorizeReadAsync(context)) return;
        var limit = 50;
        if (context.Request.Query.Count > 1
            || context.Request.Query.Any(pair => !string.Equals(pair.Key, "limit", StringComparison.Ordinal))
            || context.Request.Query.TryGetValue("limit", out var value) && (!int.TryParse(value, out limit) || limit is < 1 or > 100))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "alert_invalid_limit");
            return;
        }
        if (!EnsureInitialized(store, out var initialization)) { await WriteStoreErrorAsync(context, initialization); return; }
        var result = store.History(alertId, limit);
        if (result.Status != AlertLifecycleStoreStatus.Success) { await WriteHistoryErrorAsync(context, result); return; }
        await WriteJsonAsync(context, new
        {
            schema_version = AlertLifecycleContractVersions.Lifecycle,
            alert_id = alertId,
            events = result.Events.Select(EventDto).ToArray(),
        });
    }

    private static async Task MutateAsync(HttpContext context, IAlertLifecycleStore store, string alertId)
    {
        Prepare(context.Response);
        if (MonitorHost.IsCrossSiteRequest(context)) { await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden"); return; }
        if (!MonitorHost.HasMonitorCsrfHeader(context)) { await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "csrf_required"); return; }
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0], MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type"); return;
        }
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        AlertLifecycleMutationRequest? request;
        try { request = JsonSerializer.Deserialize<AlertLifecycleMutationRequest>(body, Json); }
        catch (JsonException) { await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "alert_invalid_request"); return; }
        catch (NotSupportedException) { await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "alert_invalid_request"); return; }
        if (request is null || request.SchemaVersion != AlertLifecycleContractVersions.Lifecycle || !TryUserAction(request.Action, out var action))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "alert_invalid_request"); return;
        }
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var mutation = new AlertLifecycleMutation(alertId, action, request.ExpectedRevision, request.ReasonCode, request.Comment, key);
        if (!EnsureInitialized(store, out var initialization)) { await WriteStoreErrorAsync(context, initialization); return; }
        var result = store.Mutate(mutation);
        if (result.Status != AlertLifecycleStoreStatus.Success) { await WriteStoreErrorAsync(context, result); return; }
        await WriteJsonAsync(context, new
        {
            schema_version = AlertLifecycleContractVersions.Lifecycle,
            alert_id = result.Lifecycle!.AlertId,
            state = State(result.Lifecycle.State),
            revision = result.Lifecycle.Revision,
            last_occurred_at = Time(result.Lifecycle.LastOccurredAt),
            @event = EventDto(result.Event!),
            idempotent_replay = result.Replayed,
        });
    }

    private static async Task<bool> AuthorizeReadAsync(HttpContext context)
    {
        Prepare(context.Response);
        if (!MonitorHost.IsCrossSiteRequest(context)) return true;
        await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
        return false;
    }

    private static bool EnsureInitialized(IAlertLifecycleStore store, out AlertLifecycleStoreResult result)
    {
        result = store.Initialize();
        return result.Status == AlertLifecycleStoreStatus.Success;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaximumBodyBytes) { await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large"); return null; }
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            total += read;
            if (total > MaximumBodyBytes) { await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large"); return null; }
            await buffer.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
        }
        return buffer.ToArray();
    }

    private static async Task WriteStoreErrorAsync(HttpContext context, AlertLifecycleStoreResult result)
    {
        var status = result.Status switch
        {
            AlertLifecycleStoreStatus.NotFound => StatusCodes.Status404NotFound,
            AlertLifecycleStoreStatus.Invalid => StatusCodes.Status400BadRequest,
            AlertLifecycleStoreStatus.Conflict => StatusCodes.Status409Conflict,
            AlertLifecycleStoreStatus.Busy or AlertLifecycleStoreStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
        await WriteErrorAsync(context, status, result.Code ?? "alert_lifecycle_store_unavailable");
    }

    private static Task WriteHistoryErrorAsync(HttpContext context, AlertLifecycleHistoryResult result) => WriteErrorAsync(context, result.Status switch
    {
        AlertLifecycleStoreStatus.NotFound => StatusCodes.Status404NotFound,
        AlertLifecycleStoreStatus.Invalid => StatusCodes.Status400BadRequest,
        AlertLifecycleStoreStatus.Busy or AlertLifecycleStoreStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status503ServiceUnavailable,
    }, result.Code ?? "alert_lifecycle_store_unavailable");

    private static async Task WriteJsonAsync<T>(HttpContext context, T value)
    {
        Prepare(context.Response);
        context.Response.StatusCode = StatusCodes.Status200OK;
        await JsonSerializer.SerializeAsync(context.Response.Body, value, Json, context.RequestAborted);
    }

    private static void Prepare(HttpResponse response) { response.ContentType = "application/json"; response.Headers.CacheControl = "no-store"; }
    private static object LifecycleDto(AlertLifecycleView value) => new { schema_version = value.SchemaVersion, alert_id = value.AlertId, state = State(value.State), revision = value.Revision, last_occurred_at = Time(value.LastOccurredAt) };
    private static object EventDto(AlertLifecycleEvent value) => new
    {
        schema_version = value.SchemaVersion, event_id = value.EventId, alert_id = value.AlertId, revision = value.Revision, expected_revision = value.ExpectedRevision,
        action = Action(value.Action), previous_state = State(value.PreviousState), state = State(value.State), occurred_at = Time(value.OccurredAt),
        actor = value.Actor, reason_code = value.ReasonCode, comment = value.Comment, old_alert_id = value.OldAlertId, new_alert_id = value.NewAlertId,
        result_code = value.ResultCode,
    };
    private static string? Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    private static bool TryUserAction(string value, out AlertLifecycleAction action) { action = value switch { "acknowledge" => AlertLifecycleAction.Acknowledge, "dismiss" => AlertLifecycleAction.Dismiss, "resolve" => AlertLifecycleAction.Resolve, "reopen" => AlertLifecycleAction.Reopen, _ => (AlertLifecycleAction)(-1) }; return (int)action >= 0; }
    private static string State(AlertLifecycleState value) => value.ToString().ToLowerInvariant();
    private static string Action(AlertLifecycleAction value) => value == AlertLifecycleAction.SourceDeleted ? "source_deleted" : value.ToString().ToLowerInvariant();

    private sealed record AlertLifecycleMutationRequest(
        [property: JsonPropertyName("schema_version")] string SchemaVersion,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("expected_revision")] long ExpectedRevision,
        [property: JsonPropertyName("reason_code")] string ReasonCode,
        [property: JsonPropertyName("comment")] string? Comment = null);
}
