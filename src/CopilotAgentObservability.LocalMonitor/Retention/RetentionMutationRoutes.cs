using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal static class RetentionMutationRoutes
{
    private const int MaximumBodyBytes = 1_048_576;
    private const string JsonContentType = "application/json";
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    internal static void Map(WebApplication app, RetentionCatalogStore catalog, TimeProvider timeProvider, RetentionMutationApplicationService? applicationOverride = null)
    {
        var application = applicationOverride ?? new RetentionMutationApplicationService(catalog, timeProvider);
        app.MapPost("/api/retention/v1/previews", context => CreatePreviewAsync(context, application));
        app.MapGet("/api/retention/v1/previews/{previewId}", (string previewId, HttpContext context) => ReadPreviewAsync(context, application, previewId));
        app.MapPost("/api/retention/v1/confirmations", context => IssueConfirmationAsync(context, application));
        app.MapPost("/api/retention/v1/mutations", context => ExecuteMutationAsync(context, application));
        app.MapGet("/api/retention/v1/mutations/{operationId}", (string operationId, HttpContext context) => ReadMutationStatusAsync(context, application, operationId));
        app.MapGet("/api/retention/v1/items/{itemId}", (string itemId, HttpContext context) => ReadItemStateAsync(context, application, itemId));
        RetentionHistoryRoutes.Map(app, application);
    }

    internal static bool IsRetentionPath(PathString path) => path.StartsWithSegments("/api/retention/v1");

    internal static Task WriteErrorAsync(HttpContext context, int statusCode, string error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = JsonContentType;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{{\"error\":\"{error}\"}}"), context.RequestAborted).AsTask();
    }

    internal static void PrepareRetentionResponse(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.ContentType = JsonContentType;
    }

    private static async Task CreatePreviewAsync(HttpContext context, RetentionMutationApplicationService application)
    {
        if (!await AuthorizeBodyAsync(context)) return;
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryDeserialize(body, out RetentionMutationPreviewRequest? request))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, RetentionMutationErrorCodes.RequestInvalid);
            return;
        }

        var result = Invoke(() => application.CreatePreview(request, WorkflowKey(context)));
        if (result is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, RetentionMutationErrorCodes.CatalogUnavailable);
            return;
        }
        if (result.ErrorCode is not null)
        {
            await WriteApplicationErrorAsync(context, result.ErrorCode, confirmationIssue: false, preview: true);
            return;
        }

        await WriteJsonAsync(context, result.Preview!);
    }

    private static async Task ReadPreviewAsync(HttpContext context, RetentionMutationApplicationService application, string previewId)
    {
        PrepareRetentionResponse(context.Response);
        var result = Invoke(() => application.ReadPreview(previewId));
        if (result is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, RetentionMutationErrorCodes.CatalogUnavailable);
            return;
        }
        if (result.ErrorCode is not null)
        {
            await WriteApplicationErrorAsync(context, result.ErrorCode, confirmationIssue: false, preview: false);
            return;
        }

        await WriteJsonAsync(context, result.Preview!);
    }

    private static async Task IssueConfirmationAsync(HttpContext context, RetentionMutationApplicationService application)
    {
        if (!await AuthorizeBodyAsync(context)) return;
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryDeserialize(body, out RetentionConfirmationIssueRequest? request))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, RetentionMutationErrorCodes.RequestInvalid);
            return;
        }

        var result = Invoke(() => application.IssueConfirmation(request, WorkflowKey(context)));
        if (result is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, RetentionMutationErrorCodes.CatalogUnavailable);
            return;
        }
        if (result.ErrorCode is not null)
        {
            if (string.Equals(result.ErrorCode, RetentionMutationErrorCodes.ConfirmationConsumed, StringComparison.Ordinal)
                && result.OperationId is not null)
                context.Response.Headers.Location = $"/api/retention/v1/mutations/{Uri.EscapeDataString(result.OperationId)}";
            await WriteApplicationErrorAsync(context, result.ErrorCode, confirmationIssue: true, preview: false);
            return;
        }

        await WriteJsonAsync(context, result.Confirmation!);
    }

    private static async Task ExecuteMutationAsync(HttpContext context, RetentionMutationApplicationService application)
    {
        if (!await AuthorizeBodyAsync(context)) return;
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryDeserialize(body, out RetentionMutationConfirmRequest? request))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, RetentionMutationErrorCodes.RequestInvalid);
            return;
        }

        var result = Invoke(() => application.ExecuteMutation(request, WorkflowKey(context)));
        if (result is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, RetentionMutationErrorCodes.MutationTransactionFailed);
            return;
        }
        if (result.ErrorCode is not null)
        {
            await WriteApplicationErrorAsync(context, result.ErrorCode, confirmationIssue: false, preview: false);
            return;
        }

        await WriteJsonAsync(context, result.Result!);
    }

    private static async Task ReadMutationStatusAsync(HttpContext context, RetentionMutationApplicationService application, string operationId)
    {
        PrepareRetentionResponse(context.Response);
        var result = Invoke(() => application.ReadOperationStatus(operationId));
        if (result is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, RetentionMutationErrorCodes.CatalogUnavailable);
            return;
        }
        if (result.ErrorCode is not null)
        {
            await WriteApplicationErrorAsync(context, result.ErrorCode, confirmationIssue: false, preview: false);
            return;
        }

        await WriteJsonAsync(context, result.Status!);
    }

    private static async Task ReadItemStateAsync(HttpContext context, RetentionMutationApplicationService application, string itemId)
    {
        PrepareRetentionResponse(context.Response);
        var result = Invoke(() => application.ReadItemState(itemId));
        if (result is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, RetentionMutationErrorCodes.CatalogUnavailable);
            return;
        }
        if (result.ErrorCode is not null)
        {
            await WriteApplicationErrorAsync(context, result.ErrorCode, confirmationIssue: false, preview: false);
            return;
        }

        await WriteJsonAsync(context, result.Item!);
    }

    private static async Task<bool> AuthorizeBodyAsync(HttpContext context)
    {
        PrepareRetentionResponse(context.Response);
        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
            return false;
        }
        if (!MonitorHost.HasMonitorCsrfHeader(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "csrf_required");
            return false;
        }
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0], MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
            return false;
        }
        return true;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaximumBodyBytes)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large");
            return null;
        }

        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            total += read;
            if (total > MaximumBodyBytes)
            {
                await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large");
                return null;
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
        }

        return buffer.ToArray();
    }

    private static bool TryDeserialize<T>(byte[] body, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(body, Json);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
        catch (NotSupportedException)
        {
            value = default;
            return false;
        }
    }

    private static string? WorkflowKey(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString() switch
    {
        { Length: > 0 } value => value,
        _ => null
    };

    internal static async Task WriteApplicationErrorAsync(HttpContext context, string error, bool confirmationIssue, bool preview)
    {
        if (!RetentionMutationErrorCodeRegistry.All.Any(entry => string.Equals(entry.Code, error, StringComparison.Ordinal)))
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, RetentionMutationErrorCodes.CatalogUnavailable);
            return;
        }

        var entry = RetentionMutationErrorCodeRegistry.Get(error);
        var status = confirmationIssue
            ? entry.ConfirmationIssueHttpStatus ?? entry.HttpStatus
            : preview
                ? entry.PreviewHttpStatus ?? entry.HttpStatus
                : entry.HttpStatus;
        await WriteErrorAsync(context, status ?? StatusCodes.Status503ServiceUnavailable, error);
    }

    internal static async Task WriteJsonAsync<T>(HttpContext context, T value)
    {
        PrepareRetentionResponse(context.Response);
        context.Response.StatusCode = StatusCodes.Status200OK;
        await JsonSerializer.SerializeAsync(context.Response.Body, value, Json, context.RequestAborted);
    }

    internal static T? Invoke<T>(Func<T> action) where T : class
    {
        try
        {
            return action();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
        };
        options.Converters.Add(new RetentionStrictEnumJsonConverterFactory());
        options.Converters.Add(new RetentionDateTimeOffsetJsonConverter());
        options.Converters.Add(new RetentionErrorCodeJsonConverter());
        return options;
    }

    private sealed class RetentionStrictEnumJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum && typeToConvert != typeof(RetentionErrorCode);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(
                typeof(RetentionStrictEnumJsonConverter<>).MakeGenericType(typeToConvert),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [options],
                culture: null)!;
    }

    private sealed class RetentionStrictEnumJsonConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private readonly IReadOnlyDictionary<string, TEnum> byWireName;
        private readonly IReadOnlyDictionary<TEnum, string> byValue;

        public RetentionStrictEnumJsonConverter(JsonSerializerOptions options)
        {
            var values = Enum.GetValues<TEnum>().ToDictionary(
                static value => value,
                value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString())!,
                EqualityComparer<TEnum>.Default);
            byValue = values;
            byWireName = values.ToDictionary(static pair => pair.Value, static pair => pair.Key, StringComparer.Ordinal);
        }

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !byWireName.TryGetValue(reader.GetString()!, out var value))
                throw new JsonException();
            return value;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (!byValue.TryGetValue(value, out var wireName)) throw new JsonException();
            writer.WriteStringValue(wireName);
        }
    }

    private sealed class RetentionDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? DateTimeOffset.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)
                : throw new JsonException();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class RetentionErrorCodeJsonConverter : JsonConverter<RetentionErrorCode>
    {
        public override RetentionErrorCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new JsonException();

        public override void Write(Utf8JsonWriter writer, RetentionErrorCode value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch
            {
                RetentionErrorCode.MigrationBlocked => "retention_migration_blocked",
                RetentionErrorCode.MissingTimestamp => "retention_missing_timestamp",
                RetentionErrorCode.InvalidIdentity => "retention_invalid_identity",
                RetentionErrorCode.OwnershipMismatch => "retention_ownership_mismatch",
                RetentionErrorCode.CaptureIncomplete => "retention_capture_incomplete",
                RetentionErrorCode.LeaseConflict => "retention_lease_conflict",
                RetentionErrorCode.LeaseLost => "retention_lease_lost",
                RetentionErrorCode.DeleteBusy => "retention_delete_busy",
                RetentionErrorCode.DeletePermissionDenied => "retention_delete_permission_denied",
                RetentionErrorCode.DeleteIoFailed => "retention_delete_io_failed",
                RetentionErrorCode.UnexpectedSourceMissing => "retention_unexpected_source_missing",
                RetentionErrorCode.MaintenanceBusy => "retention_maintenance_busy",
                RetentionErrorCode.ItemLimitExceeded => "retention_item_limit_exceeded",
                _ => throw new JsonException()
            });
    }
}
