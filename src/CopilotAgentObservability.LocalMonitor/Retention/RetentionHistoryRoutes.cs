using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal static class RetentionHistoryRoutes
{
    private const string InvalidCursor = "";

    internal static void Map(WebApplication app, RetentionMutationApplicationService application)
    {
        app.MapGet("/api/retention/v1/sessions/{sessionId}/history", (string sessionId, HttpContext context) =>
            ReadAsync(context, application, RetentionMutationTargetKind.Session, sessionId));
        app.MapGet("/api/retention/v1/items/{itemId}/history", (string itemId, HttpContext context) =>
            ReadAsync(context, application, RetentionMutationTargetKind.Item, itemId));
    }

    private static async Task ReadAsync(
        HttpContext context,
        RetentionMutationApplicationService application,
        RetentionMutationTargetKind targetKind,
        string targetId)
    {
        RetentionMutationRoutes.PrepareRetentionResponse(context.Response);
        if (!TryReadLimit(context.Request, out var limit))
        {
            await RetentionMutationRoutes.WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                RetentionMutationErrorCodes.RequestInvalid);
            return;
        }

        var cursor = ReadCursor(context.Request);
        var result = RetentionMutationRoutes.Invoke(() => application.ReadHistory(targetKind, targetId, limit, cursor));
        if (result is null)
        {
            await RetentionMutationRoutes.WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                RetentionMutationErrorCodes.CatalogUnavailable);
            return;
        }
        if (result.ErrorCode is not null)
        {
            await RetentionMutationRoutes.WriteApplicationErrorAsync(
                context,
                result.ErrorCode,
                confirmationIssue: false,
                preview: false);
            return;
        }

        await RetentionMutationRoutes.WriteJsonAsync(context, result.History!);
    }

    private static bool TryReadLimit(HttpRequest request, out int limit)
    {
        limit = RetentionMutationConstants.HistoryPageDefault;
        if (!request.Query.TryGetValue("limit", out var values)) return true;
        return values.Count == 1
            && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            && limit is >= 1 and <= RetentionMutationConstants.HistoryPageMaximum;
    }

    private static string? ReadCursor(HttpRequest request)
    {
        if (!request.Query.TryGetValue("cursor", out var values)) return null;
        return values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : InvalidCursor;
    }
}
