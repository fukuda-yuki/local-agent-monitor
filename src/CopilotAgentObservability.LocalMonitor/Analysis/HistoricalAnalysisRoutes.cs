using System.Net.Mime;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class HistoricalAnalysisRoutes
{
    private const string ApiPrefix = "/api/historical-analysis/v1";
    private const string JsonContentType = "application/json";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly string[] PreviewProperties = ["schema_version", "selection"];
    private static readonly string[] SelectionProperties =
    [
        "repository",
        "workspace",
        "from",
        "to",
        "explicit_session_ids",
        "source_surfaces",
        "task_label",
        "experiment_label",
        "maximum_session_count",
        "sanitized_only",
    ];
    private static readonly string[] InstructionStartProperties =
    [
        "schema_version",
        "extraction_id",
        "raw_local_sha256",
        "model",
        "provider",
        "configuration_sha256",
        "timeout_ms",
        "prompt_template_version",
    ];
    private static readonly string[] EfficiencyStartProperties =
    [
        "schema_version",
        "extraction_id",
        "repository_safe_sha256",
    ];
    private static readonly string[] EvidenceResolveProperties =
    [
        "schema_version",
        "extraction_id",
        "repository_safe_sha256",
        "references",
    ];

    internal static void Map(WebApplication app, HistoricalAnalysisCoordinatorV1 coordinator)
    {
        app.MapPost($"{ApiPrefix}/preview", context => PreviewAsync(context, coordinator));
        app.MapPost($"{ApiPrefix}/instruction-runs", context => StartInstructionAsync(context, coordinator));
        app.MapGet(
            $"{ApiPrefix}/instruction-runs/{{analysisRunId}}",
            (string analysisRunId, HttpContext context) => GetInstructionAsync(context, coordinator, analysisRunId));
        app.MapPost($"{ApiPrefix}/efficiency-runs", context => StartEfficiencyAsync(context, coordinator));
        app.MapGet(
            $"{ApiPrefix}/efficiency-runs/{{analysisRunId}}",
            (string analysisRunId, HttpContext context) => GetEfficiencyAsync(context, coordinator, analysisRunId));
        app.MapPost($"{ApiPrefix}/evidence/resolve", context => ResolveEvidenceAsync(context, coordinator));
    }

    internal static bool IsPath(PathString path) =>
        path.StartsWithSegments(ApiPrefix)
        || path.Equals(new PathString("/historical-analysis"));

    internal static async Task WriteErrorAsync(HttpContext context, int statusCode, string error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = JsonContentType;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes(
                $"{{\"schema_version\":\"{HistoricalAnalysisContractsV1.ErrorSchemaVersion}\",\"error\":\"{error}\"}}"),
            context.RequestAborted);
    }

    private static async Task PreviewAsync(HttpContext context, HistoricalAnalysisCoordinatorV1 coordinator)
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
            await InvalidRequest(context);
            return;
        }

        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryDeserializePreview(body, out var request))
        {
            await InvalidRequest(context);
            return;
        }

        try
        {
            var response = await coordinator.PreviewAsync(request!, context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.Body.WriteAsync(
                JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                context.RequestAborted);
        }
        catch (HistoricalAnalysisException exception)
        {
            var status = exception.Code == HistoricalAnalysisErrorCodesV1.StoreUnavailable
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status400BadRequest;
            await WriteErrorAsync(context, status, exception.Code);
        }
    }

    private static async Task StartInstructionAsync(
        HttpContext context,
        HistoricalAnalysisCoordinatorV1 coordinator)
    {
        Prepare(context.Response);
        if (!await AuthorizePostAsync(context)) return;
        if (!RejectQuery(context))
        {
            await InvalidRequest(context);
            return;
        }
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryDeserializeInstructionStart(body, out var request))
        {
            await InvalidRequest(context);
            return;
        }

        try
        {
            var response = coordinator.StartInstruction(request!);
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            await context.Response.Body.WriteAsync(
                JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                context.RequestAborted);
        }
        catch (HistoricalAnalysisException exception)
        {
            await WriteErrorAsync(context, StatusFor(exception.Code), exception.Code);
        }
    }

    private static async Task GetInstructionAsync(
        HttpContext context,
        HistoricalAnalysisCoordinatorV1 coordinator,
        string analysisRunId)
    {
        Prepare(context.Response);
        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
            return;
        }
        if (!RejectQuery(context)
            || !long.TryParse(analysisRunId, NumberStyles.None, CultureInfo.InvariantCulture, out var runId)
            || runId <= 0
            || !string.Equals(analysisRunId, runId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            await InvalidRequest(context);
            return;
        }
        try
        {
            var response = HistoricalAnalysisInstructionReadResponseV1.From(
                coordinator.GetInstruction(runId));
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.Body.WriteAsync(
                JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                context.RequestAborted);
        }
        catch (HistoricalAnalysisException exception)
        {
            await WriteErrorAsync(context, StatusFor(exception.Code), exception.Code);
        }
    }

    private static async Task StartEfficiencyAsync(
        HttpContext context,
        HistoricalAnalysisCoordinatorV1 coordinator)
    {
        Prepare(context.Response);
        if (!await AuthorizePostAsync(context)) return;
        if (!RejectQuery(context))
        {
            await InvalidRequest(context);
            return;
        }
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryDeserializeEfficiencyStart(body, out var request))
        {
            await InvalidRequest(context);
            return;
        }

        try
        {
            var response = coordinator.StartEfficiency(request!);
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            await context.Response.Body.WriteAsync(
                JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                context.RequestAborted);
        }
        catch (HistoricalAnalysisException exception)
        {
            await WriteErrorAsync(context, StatusFor(exception.Code), exception.Code);
        }
    }

    private static async Task GetEfficiencyAsync(
        HttpContext context,
        HistoricalAnalysisCoordinatorV1 coordinator,
        string analysisRunId)
    {
        Prepare(context.Response);
        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
            return;
        }
        if (!RejectQuery(context))
        {
            await InvalidRequest(context);
            return;
        }
        try
        {
            var response = coordinator.GetEfficiency(analysisRunId);
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.Body.WriteAsync(
                JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                context.RequestAborted);
        }
        catch (HistoricalAnalysisException exception)
        {
            await WriteErrorAsync(context, StatusFor(exception.Code), exception.Code);
        }
    }

    private static async Task ResolveEvidenceAsync(
        HttpContext context,
        HistoricalAnalysisCoordinatorV1 coordinator)
    {
        Prepare(context.Response);
        if (!await AuthorizePostAsync(context)) return;
        if (!RejectQuery(context))
        {
            await InvalidRequest(context);
            return;
        }
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryDeserializeEvidenceResolve(body, out var request))
        {
            await InvalidRequest(context);
            return;
        }

        try
        {
            var response = coordinator.ResolveEvidence(request!);
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.Body.WriteAsync(
                JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions),
                context.RequestAborted);
        }
        catch (HistoricalAnalysisException exception)
        {
            await WriteErrorAsync(context, StatusFor(exception.Code), exception.Code);
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is > HistoricalAnalysisContractsV1.MaximumRequestBytes)
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
            if (total > HistoricalAnalysisContractsV1.MaximumRequestBytes)
            {
                await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large");
                return null;
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
        }
        return buffer.ToArray();
    }

    private static bool TryDeserializePreview(byte[] body, out HistoricalAnalysisPreviewRequestV1? request)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            var root = document.RootElement;
            var selection = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("selection", out var selected)
                    ? selected
                    : default;
            if (!HasExactProperties(root, PreviewProperties)
                || !HasExactProperties(selection, SelectionProperties)
                || !HasValidSelectionLexemes(selection))
                return false;
            request = JsonSerializer.Deserialize<HistoricalAnalysisPreviewRequestV1>(body, JsonOptions);
            return request is not null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryDeserializeInstructionStart(
        byte[] body,
        out HistoricalAnalysisInstructionStartRequestV1? request)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            if (!HasExactProperties(document.RootElement, InstructionStartProperties)) return false;
            request = JsonSerializer.Deserialize<HistoricalAnalysisInstructionStartRequestV1>(body, JsonOptions);
            return request is not null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryDeserializeEfficiencyStart(
        byte[] body,
        out HistoricalAnalysisEfficiencyStartRequestV1? request)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            if (!HasExactProperties(document.RootElement, EfficiencyStartProperties)) return false;
            request = JsonSerializer.Deserialize<HistoricalAnalysisEfficiencyStartRequestV1>(body, JsonOptions);
            return request is not null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryDeserializeEvidenceResolve(
        byte[] body,
        out HistoricalAnalysisEvidenceResolveRequestV1? request)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
            if (!HasExactProperties(document.RootElement, EvidenceResolveProperties)
                || document.RootElement.GetProperty("references").ValueKind != JsonValueKind.Array
                || document.RootElement.GetProperty("references").EnumerateArray()
                    .Any(reference => reference.ValueKind != JsonValueKind.String))
                return false;
            request = JsonSerializer.Deserialize<HistoricalAnalysisEvidenceResolveRequestV1>(body, JsonOptions);
            return request is not null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasExactProperties(JsonElement element, IReadOnlyList<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        return actual.SequenceEqual(expected, StringComparer.Ordinal)
            && actual.Distinct(StringComparer.Ordinal).Count() == actual.Length;
    }

    private static bool HasValidSelectionLexemes(JsonElement selection)
    {
        var ids = selection.GetProperty("explicit_session_ids");
        var surfaces = selection.GetProperty("source_surfaces");
        if (ids.ValueKind != JsonValueKind.Array || surfaces.ValueKind != JsonValueKind.Array) return false;

        var idValues = new List<string>();
        foreach (var element in ids.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) return false;
            var value = element.GetString()!;
            if (!Guid.TryParseExact(value, "D", out var id)
                || id.Version != 7
                || !string.Equals(value, id.ToString("D"), StringComparison.Ordinal))
                return false;
            idValues.Add(value);
        }
        if (idValues.Distinct(StringComparer.Ordinal).Count() != idValues.Count) return false;

        var surfaceValues = new List<string>();
        foreach (var element in surfaces.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) return false;
            surfaceValues.Add(element.GetString()!);
        }
        return surfaceValues.Distinct(StringComparer.Ordinal).Count() == surfaceValues.Count;
    }

    private static void Prepare(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.ContentType = JsonContentType;
    }

    private static Task InvalidRequest(HttpContext context) =>
        WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalAnalysisErrorCodesV1.InvalidRequest);

    private static async Task<bool> AuthorizePostAsync(HttpContext context)
    {
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
        if (!string.Equals(
            context.Request.ContentType?.Split(';', 2)[0],
            MediaTypeNames.Application.Json,
            StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
            return false;
        }
        return true;
    }

    private static bool RejectQuery(HttpContext context) => context.Request.Query.Count == 0;

    private static int StatusFor(string code) => code switch
    {
        HistoricalAnalysisErrorCodesV1.RunNotFound or
        HistoricalAnalysisErrorCodesV1.ExtractionNotFound => StatusCodes.Status404NotFound,
        HistoricalAnalysisErrorCodesV1.StaleExtraction => StatusCodes.Status409Conflict,
        HistoricalAnalysisErrorCodesV1.PreconditionFailed => StatusCodes.Status409Conflict,
        HistoricalAnalysisErrorCodesV1.ProviderUnavailable or
        HistoricalAnalysisErrorCodesV1.StoreUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new SessionSourceSurfaceConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    private sealed class SessionSourceSurfaceConverter : JsonConverter<SessionSourceSurface>
    {
        public override SessionSourceSurface Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            try
            {
                return SessionWire.ParseSourceSurface(reader.GetString()!);
            }
            catch (ArgumentException exception)
            {
                throw new JsonException("Invalid source surface.", exception);
            }
        }

        public override void Write(
            Utf8JsonWriter writer,
            SessionSourceSurface value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(SessionWire.ToWire(value));
    }
}
