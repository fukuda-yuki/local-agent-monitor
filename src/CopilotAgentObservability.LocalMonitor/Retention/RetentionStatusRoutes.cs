using System.Text.Json;
using System.Globalization;
using System.Text.Encodings.Web;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal static class RetentionStatusRoutes
{
    private static readonly byte[] SessionNotFound = "{\"error\":\"session_not_found\"}"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static void Map(WebApplication app, RetentionCatalogStore catalog, Func<bool> workerEnabled)
    {
        app.MapGet("/api/retention/v1/status", async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!catalog.TryReadStatusSnapshot(workerEnabled(), out var snapshot)) { await WriteJson(context, Unknown()); return; }
            await WriteJson(context, From(snapshot!));
        });
        app.MapGet("/api/retention/v1/sessions/{sessionId}", async (string sessionId, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParseExact(sessionId, "D", out _) || !catalog.TryReadSessionStatusSnapshot(sessionId, out var snapshot) || !snapshot!.SessionExists)
            {
                context.Response.StatusCode = 404; context.Response.ContentType = "application/json"; await context.Response.Body.WriteAsync(SessionNotFound, context.RequestAborted); return;
            }
            await WriteJson(context, new RetentionSessionStatusResponse(1, sessionId, snapshot.RawRetentionState, snapshot.ReadableCount, snapshot.ReadDeniedCount,
                new(snapshot.ExpiringCount, snapshot.RetainedByPolicyCount, snapshot.ExpiredPendingDeletionCount, snapshot.DeletionQueuedCount, snapshot.DeletingCount, snapshot.DeletedCount, snapshot.DeletionFailedCount)));
        });
    }

    private static RetentionStatusResponse Unknown() => new(1, null, null, null, null, null, null, null, null, "unknown", null, 1, 1, []);
    private static RetentionStatusResponse From(RetentionStatusSnapshot value) => new(1, value.PendingCount, value.QueuedCount, value.DeletingCount, value.FailedCount, value.RetryExhaustedCount, value.OrphanOrUnexpectedMissingCount, value.ExpiredButReadableViolationCount, value.OldestPendingAgeSeconds, value.WorkerState, Timestamp(value.LastSuccessfulRunAt), 1, 1, value.Items.Select(Item).ToArray());
    private static RetentionStatusItemResponse Item(RetentionStatusItemSnapshot item) => new(item.ItemId, item.StoreKind, item.InventoryCategory, item.State, item.PolicyId, item.PolicyVersion, Timestamp(item.CapturedAt), Timestamp(item.ExpiresAt), Timestamp(item.ReadDeniedAt), Timestamp(item.QueuedAt), Timestamp(item.DeletionStartedAt), Timestamp(item.DeletedAt), item.AttemptCount, item.RetryExhausted, item.ErrorCode, Timestamp(item.RetryAt));
    private static string? Timestamp(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static Task WriteJson(HttpContext context, object value) { context.Response.ContentType = "application/json"; return JsonSerializer.SerializeAsync(context.Response.Body, value, value.GetType(), Json, context.RequestAborted); }
}
