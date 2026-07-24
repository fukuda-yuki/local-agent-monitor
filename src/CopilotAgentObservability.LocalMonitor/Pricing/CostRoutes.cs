using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;

namespace CopilotAgentObservability.LocalMonitor.Pricing;

internal static class CostRoutes
{
    private const int MaximumBodyBytes = 1_048_576;
    private const int MaximumQueryBytes = 8_192;
    private const int MaximumResponseBytes = 8 * 1_048_576;
    private const string JsonContentType = "application/json";
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    internal static bool IsPath(PathString path) =>
        path.StartsWithSegments("/api/costs/v1")
        || path.Equals("/costs");

    internal static void Map(WebApplication app, CostHttpApplication application)
    {
        app.MapGet("/api/costs/v1/configuration", context =>
            Read(context, [], () => application.ReadCurrentConfiguration()));
        app.MapGet("/api/costs/v1/configurations/{configurationId}", (
            string configurationId,
            HttpContext context) =>
            Read(
                context,
                [],
                () => ValidConfigurationId(configurationId)
                    ? application.ReadConfigurationVersion(configurationId)
                    : CostHttpResult.InvalidId()));
        app.MapGet("/api/costs/v1/catalog", context =>
            Read(context, ["after", "limit"], () =>
            {
                if (!TryQuery(context, ["after", "limit"], out var query))
                    return CostHttpResult.InvalidQuery();
                if (EncodedQueryBytesExcluding(context, "after") > 7_000)
                    return CostHttpResult.InvalidQuery();
                if (!TryLimit(query, out var limit))
                    return new(400, Error: "cost_invalid_cursor");
                query.TryGetValue("after", out var after);
                return application.ReadCatalog(after, limit);
            }));
        app.MapPost("/api/costs/v1/configuration/preview", context =>
            Write(context, "cost_invalid_configuration", application.PreviewConfiguration));
        app.MapPost("/api/costs/v1/configurations", context =>
            Write(context, "cost_invalid_configuration", application.CommitConfiguration));
        app.MapPost("/api/costs/v1/recalculations", context =>
            Write(context, "cost_invalid_request", application.StartRecalculation));
        app.MapGet("/api/costs/v1/recalculations/{runId}", (
            string runId,
            HttpContext context) =>
            Read(
                context,
                [],
                () => ValidUuid7(runId)
                    ? application.ReadRecalculation(runId)
                    : CostHttpResult.InvalidId()));
        app.MapGet("/api/costs/v1/sessions/{sessionId}/recalculations", (
            string sessionId,
            HttpContext context) =>
            Read(context, ["after", "limit"], () =>
            {
                if (!ValidUuid7(sessionId)) return CostHttpResult.InvalidId();
                if (!TryQuery(context, ["after", "limit"], out var query)
                    || !TryLimit(query, out var limit))
                    return CostHttpResult.InvalidQuery();
                if (!TryPositiveLong(query, "after", out var after))
                    return new(400, Error: "cost_invalid_cursor");
                return application.ReadSessionRecalculations(sessionId, after, limit);
            }));
        app.MapGet("/api/costs/v1/sessions/{sessionId}/estimates", (
            string sessionId,
            HttpContext context) =>
            Read(context, ["after", "limit"], () =>
            {
                if (!ValidUuid7(sessionId)) return CostHttpResult.InvalidId();
                if (!TryQuery(context, ["after", "limit"], out var query)
                    || !TryLimit(query, out var limit))
                    return CostHttpResult.InvalidQuery();
                query.TryGetValue("after", out var after);
                return application.ReadSessionEstimates(sessionId, after, limit);
            }));
        app.MapGet("/api/costs/v1/sessions/{sessionId}/estimates/{estimateId}", (
            string sessionId,
            string estimateId,
            HttpContext context) =>
            Read(
                context,
                [],
                () => ValidUuid7(sessionId) && ValidEstimateId(estimateId)
                    ? application.ReadSessionEstimate(sessionId, estimateId)
                    : CostHttpResult.InvalidId()));
        app.MapGet("/api/costs/v1/analytics", context =>
            Read(
                context,
                [
                    "from", "to", "source_surface", "provider", "model",
                    "billing_mode", "status", "registry_version", "currency",
                    "repository", "workspace", "after", "limit",
                ],
                () => Analytics(context, application)));
    }

    internal static Task ErrorAsync(HttpContext context, int status, string code) =>
        WriteResponse(context, new(status, Error: code));

    private static CostHttpResult Analytics(
        HttpContext context,
        CostHttpApplication application)
    {
        if (!TryQuery(
                context,
                [
                    "from", "to", "source_surface", "provider", "model",
                    "billing_mode", "status", "registry_version", "currency",
                    "repository", "workspace", "after", "limit",
                ],
                out var query)
            || EncodedQueryBytesExcluding(context, "after") > 7_000
            || !TryLimit(query, out var limit)
            || !query.TryGetValue("from", out var fromText)
            || !query.TryGetValue("to", out var toText)
            || !TryUtc(fromText, out var from)
            || !TryUtc(toText, out var to)
            || to <= from
            || to - from > TimeSpan.FromDays(366))
            return CostHttpResult.InvalidQuery();
        return application.ReadAnalytics(new(
            from,
            to,
            Value(query, "source_surface"),
            Value(query, "provider"),
            Value(query, "model"),
            Value(query, "billing_mode"),
            Value(query, "status"),
            Value(query, "registry_version"),
            Value(query, "currency"),
            Value(query, "repository"),
            Value(query, "workspace"),
            limit,
            Value(query, "after")));
    }

    private static async Task Read(
        HttpContext context,
        IReadOnlyList<string> allowed,
        Func<CostHttpResult> operation)
    {
        if (!TryCommon(context, write: false, out var failure)
            || !TryQuery(context, allowed, out _))
        {
            await WriteResponse(context, failure ?? CostHttpResult.InvalidQuery());
            return;
        }
        await WriteResponse(context, operation());
    }

    private static async Task Write(
        HttpContext context,
        string invalidCode,
        Func<ReadOnlyMemory<byte>, CostHttpResult> operation)
    {
        if (!TryCommon(context, write: true, out var failure))
        {
            await WriteResponse(context, failure!);
            return;
        }
        if (!TryQuery(context, [], out _))
        {
            await WriteResponse(context, new(400, Error: "cost_invalid_query"));
            return;
        }
        if (!string.Equals(
                context.Request.ContentType?.Split(';', 2)[0].Trim(),
                JsonContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponse(context, new(415, Error: "unsupported_media_type"));
            return;
        }
        var body = await ReadBody(context);
        if (body is null)
        {
            await WriteResponse(context, new(413, Error: "cost_request_too_large"));
            return;
        }
        if (body.Value.Length == 0)
        {
            await WriteResponse(context, new(400, Error: invalidCode));
            return;
        }
        await WriteResponse(context, operation(body.Value));
    }

    private static bool TryCommon(
        HttpContext context,
        bool write,
        out CostHttpResult? failure)
    {
        failure = null;
        context.Response.Headers["Cache-Control"] = "no-store";
        if (!MonitorOptions.IsAllowedLoopbackHost(context.Request.Host.Host))
        {
            failure = new(400, Error: "invalid_host");
            return false;
        }
        if (MonitorHost.IsCrossSiteRequest(context))
        {
            failure = new(403, Error: "cross_origin_forbidden");
            return false;
        }
        if (write && !MonitorHost.HasMonitorCsrfHeader(context))
        {
            failure = new(403, Error: "csrf_required");
            return false;
        }
        return true;
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBody(HttpContext context)
    {
        if (context.Request.ContentLength is > MaximumBodyBytes) return null;
        using var stream = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer);
            if (read == 0) break;
            if (stream.Length + read > MaximumBodyBytes) return null;
            stream.Write(buffer, 0, read);
        }
        return stream.ToArray();
    }

    private static async Task WriteResponse(HttpContext context, CostHttpResult result)
    {
        context.Response.StatusCode = result.Status;
        context.Response.ContentType = JsonContentType;
        context.Response.Headers["Cache-Control"] = "no-store";
        if (result.Location is not null) context.Response.Headers.Location = result.Location;
        if (result.Error is not null)
        {
            await context.Response.WriteAsync(
                $$"""{"schema_version":"cost.error.v1","error":"{{result.Error}}"}""");
            return;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Value, Json);
        if (bytes.Length > MaximumResponseBytes)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Remove("Location");
            await context.Response.WriteAsync(
                """{"schema_version":"cost.error.v1","error":"cost_response_too_large"}""");
            return;
        }
        await context.Response.Body.WriteAsync(bytes);
    }

    private static bool TryQuery(
        HttpContext context,
        IReadOnlyList<string> allowed,
        out Dictionary<string, string> values)
    {
        values = new(StringComparer.Ordinal);
        var raw = context.Request.QueryString.Value;
        if (string.IsNullOrEmpty(raw)) return true;
        if (Encoding.UTF8.GetByteCount(raw) - 1 > MaximumQueryBytes) return false;
        foreach (var component in raw[1..].Split('&'))
        {
            if (component.Length == 0) return false;
            var parts = component.Split('=', 2);
            if (parts.Length != 2
                || parts[0].Length == 0
                || parts[1].Length == 0
                || !ValidPercent(parts[0])
                || !ValidPercent(parts[1]))
                return false;
            if (!TryDecode(parts[0], out var key)
                || !TryDecode(parts[1], out var value))
                return false;
            if (!allowed.Contains(key)
                || value.Length == 0
                || !values.TryAdd(key, value))
                return false;
        }
        return true;
    }

    private static bool ValidPercent(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length
                || !Uri.IsHexDigit(value[index + 1])
                || !Uri.IsHexDigit(value[index + 2]))
                return false;
            index += 2;
        }
        return true;
    }

    private static int EncodedQueryBytesExcluding(
        HttpContext context,
        string excludedKey)
    {
        var raw = context.Request.QueryString.Value;
        if (string.IsNullOrEmpty(raw)) return 0;
        var count = 0;
        var retained = 0;
        foreach (var component in raw[1..].Split('&'))
        {
            var separator = component.IndexOf('=');
            if (separator <= 0
                || !TryDecode(component[..separator], out var key)
                || key == excludedKey)
                continue;
            if (retained++ > 0) count++;
            count += Encoding.UTF8.GetByteCount(component);
        }
        return count;
    }

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            using var bytes = new MemoryStream();
            Span<byte> encoded = stackalloc byte[4];
            for (var index = 0; index < value.Length;)
            {
                if (value[index] == '%')
                {
                    bytes.WriteByte(Convert.ToByte(value.Substring(index + 1, 2), 16));
                    index += 3;
                    continue;
                }
                var rune = Rune.GetRuneAt(value, index);
                var count = rune.EncodeToUtf8(encoded);
                bytes.Write(encoded[..count]);
                index += rune.Utf16SequenceLength;
            }
            decoded = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or DecoderFallbackException
                or FormatException)
        {
            return false;
        }
    }

    private static bool TryLimit(
        IReadOnlyDictionary<string, string> query,
        out int limit)
    {
        limit = 50;
        return !query.TryGetValue("limit", out var value)
            || int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                && limit is >= 1 and <= 100;
    }

    private static bool TryPositiveLong(
        IReadOnlyDictionary<string, string> query,
        string key,
        out long? value)
    {
        value = null;
        if (!query.TryGetValue(key, out var text)) return true;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
            return false;
        value = parsed;
        return true;
    }

    private static bool TryUtc(string value, out DateTimeOffset parsed)
    {
        parsed = default;
        return value.Length == 28
            && value.EndsWith('Z')
            && DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed);
    }

    private static string? Value(
        IReadOnlyDictionary<string, string> query,
        string key) =>
        query.TryGetValue(key, out var value) ? value : null;

    private static bool ValidUuid7(string value) =>
        value.Length == 36
        && value == value.ToLowerInvariant()
        && Guid.TryParseExact(value, "D", out _)
        && value[14] == '7'
        && value[19] is '8' or '9' or 'a' or 'b';

    private static bool ValidConfigurationId(string value) =>
        PrefixedSha(value, "cost-configuration-");

    private static bool ValidEstimateId(string value) =>
        PrefixedSha(value, "pricing-estimate-");

    private static bool PrefixedSha(string value, string prefix) =>
        value.Length == prefix.Length + 64
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).ToArray().All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(
                value.ToUniversalTime().ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    CultureInfo.InvariantCulture));
    }
}

internal sealed record CostHttpResult(
    int Status,
    object? Value = null,
    string? Error = null,
    string? Location = null)
{
    internal static CostHttpResult InvalidId() => new(400, Error: "cost_invalid_id");
    internal static CostHttpResult InvalidQuery() => new(400, Error: "cost_invalid_query");
}

internal sealed class CostHttpApplication : BackgroundService
{
    private readonly SqliteCostConfigurationApplicationService configurations;
    private readonly SqlitePricingReadStore reads;
    private readonly SqliteCostRecalculationCoordinatorV1 coordinator;
    private readonly byte[] catalogBytes;
    private readonly TimeProvider timeProvider;
    private readonly Channel<string> executionQueue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    internal CostHttpApplication(
        string databasePath,
        SqlitePricingStore store,
        IPricingCatalogProvider catalogProvider,
        TimeProvider timeProvider,
        ISqliteAlertEngineTransactionParticipantV2? alertParticipant)
    {
        reads = new(databasePath);
        this.timeProvider = timeProvider;
        catalogBytes = catalogProvider.CanonicalCatalogBytes.ToArray();
        configurations = new(
            store,
            reads,
            catalogProvider.Catalog,
            catalogBytes,
            catalogProvider.CatalogSha256);
        coordinator = new(
            databasePath,
            DefaultPricingEstimateSourceAdapterV1.Instance,
            timeProvider,
            alertParticipant);
    }

    internal CostHttpResult ReadCurrentConfiguration() =>
        Map(configurations.ReadCurrentConfiguration());

    internal CostHttpResult ReadConfigurationVersion(string configurationId) =>
        Map(configurations.ReadConfigurationVersion(configurationId));

    internal CostHttpResult ReadCatalog(string? after, int limit) =>
        Map(configurations.ReadCatalog(after, limit));

    internal CostHttpResult PreviewConfiguration(ReadOnlyMemory<byte> bytes) =>
        Map(configurations.PreviewConfiguration(bytes));

    internal CostHttpResult CommitConfiguration(ReadOnlyMemory<byte> bytes)
    {
        var consumed = CostConfigurationCommitConsumerV1.ConsumeRequest(bytes);
        return consumed.Status == CostConsumerStatus.Success && consumed.Value is not null
            ? Map(configurations.CommitConfiguration(consumed.Value), successStatus: 201)
            : new(400, Error: "cost_invalid_configuration");
    }

    internal CostHttpResult StartRecalculation(ReadOnlyMemory<byte> bytes)
    {
        var consumed = CostRecalculationRequestCanonicalJsonV1.Consume(bytes);
        if (consumed.Status != CostConsumerStatus.Success || consumed.Value is null)
            return new(400, Error: "cost_invalid_request");
        var runId = Guid.CreateVersion7().ToString("D");
        var start = coordinator.Start(
            runId,
            consumed.Value,
            catalogBytes,
            timeProvider.GetUtcNow());
        if (start.Status != PricingStoreStatus.Success || start.Value is null)
            return StoreFailure(start.Status, start.ErrorCode);
        var result = ReadRecalculation(start.Value);
        if (result.Error is null)
        {
            if (!executionQueue.Writer.TryWrite(start.Value))
                return new(503, Error: "cost_store_unavailable");
            return result with
            {
                Status = 202,
                Location = $"/api/costs/v1/recalculations/{start.Value}",
            };
        }
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in executionQueue.Reader.ReadAllAsync(stoppingToken))
            _ = coordinator.Execute(runId);
    }

    internal CostHttpResult ReadRecalculation(string runId) =>
        MapRead(
            reads.ReadRecalculation(runId),
            "cost_recalculation_not_found",
            value => new
            {
                schema_version = "cost.recalculation.v1",
                value.RunId,
                value.RequestDigest,
                value.State,
                value.TargetCount,
                value.ScopeCount,
                targets = value.Targets.Select(target => new
                {
                    target.TargetOrdinal,
                    target.SessionId,
                    target.BaseHeadRevision,
                    target.BaseEstimateId,
                    result = target.Result is null ? null : TargetResult(target.Result),
                }),
                events = value.Events,
                budget_results = value.BudgetResults.Select(BudgetResult),
                value.FailureCode,
            });

    internal CostHttpResult ReadSessionRecalculations(
        string sessionId,
        long? after,
        int limit) =>
        MapRead(
            reads.ReadSessionRecalculations(sessionId, catalogBytes, after, limit),
            "cost_session_not_found",
            value => new
            {
                schema_version = "cost.session-recalculations.v1",
                value.SessionId,
                active = value.Active is null ? null : new
                {
                    value.Active.AttemptRevision,
                    value.Active.RunId,
                    value.Active.CalculationTimeUtc,
                    value.Active.Freshness,
                    value.Active.State,
                    recalculation_href =
                        $"/api/costs/v1/recalculations/{value.Active.RunId}",
                },
                attempts = value.Attempts.Select(attempt => new
                {
                    attempt.AttemptRevision,
                    attempt.RunId,
                    attempt.CalculationTimeUtc,
                    attempt.Freshness,
                    attempt.Kind,
                    attempt.EstimateStatus,
                    attempt.EstimateId,
                    attempt.Code,
                    recalculation_href =
                        $"/api/costs/v1/recalculations/{attempt.RunId}",
                }),
                next_after = value.NextAfter,
            });

    internal CostHttpResult ReadSessionEstimates(
        string sessionId,
        string? after,
        int limit) =>
        MapRead(
            reads.ReadSessionEstimates(sessionId, catalogBytes, after, limit),
            "cost_session_not_found",
            value => new
            {
                schema_version = "cost.session-estimates.v1",
                value.SessionId,
                value.CalculationState,
                value.ActiveHeadRevision,
                value.ActiveEstimateId,
                value.LatestAttemptRevision,
                value.LatestAttempt,
                value.Items,
                value.NextAfter,
            });

    internal CostHttpResult ReadSessionEstimate(string sessionId, string estimateId) =>
        MapRead(
            reads.ReadSessionEstimate(sessionId, estimateId, catalogBytes),
            "cost_estimate_not_found",
            value => new
            {
                schema_version = "cost.session-estimate.v1",
                value.SessionId,
                value.ActiveHeadRevision,
                value.ActiveEstimateId,
                value.Item,
            });

    internal CostHttpResult ReadAnalytics(CostAnalyticsQueryV1 query) =>
        MapRead(
            reads.ReadAnalytics(query, catalogBytes),
            "cost_store_unavailable",
            value => new
            {
                value.SchemaVersion,
                value.SnapshotId,
                value.State,
                value.CapReason,
                value.EligibleSessionCount,
                value.EligibleSessionLowerBound,
                value.GroupLowerBound,
                value.Filters,
                value.Overall,
                value.RangeTotals,
                value.DailyTotals,
                value.Groups,
                next_cursor = value.NextCursor,
            });

    private static object TargetResult(CostRecalculationTargetResultReadV1 result) =>
        result.Kind == "estimate"
            ? new { result.Kind, result.Status, result.EstimateId }
            : new { result.Kind, result.Code };

    private static object BudgetResult(CostRecalculationBudgetResultReadV1 result) =>
        new
        {
            result.ScopeOrdinal,
            result.Scope,
            result.RuleId,
            result.RuleVersion,
            outcome = result.OutcomeKind switch
            {
                "receipt" => (object)new
                {
                    kind = result.OutcomeKind,
                    result.EvaluationId,
                    result.AlertId,
                },
                "suppression" => new
                {
                    kind = result.OutcomeKind,
                    result.EvaluationId,
                    result.SuppressionOrdinal,
                    result.Code,
                },
                _ => new { kind = result.OutcomeKind, result.EvaluationId },
            },
        };

    private static CostHttpResult Map<T>(
        CostConfigurationApplicationResult<T> result,
        int successStatus = 200) =>
        result.Success && result.Value is not null
            ? new(successStatus, result.Value, Location: result.Location)
            : Error(result.ErrorCode);

    private static CostHttpResult MapRead<T, TResult>(
        PricingReadResult<T> result,
        string notFound,
        Func<T, TResult> project) where T : class =>
        result.Status switch
        {
            PricingReadStatus.Success when result.Value is not null =>
                new(200, project(result.Value)),
            PricingReadStatus.NotFound => new(404, Error: notFound),
            PricingReadStatus.InvalidQuery => new(400, Error: "cost_invalid_query"),
            PricingReadStatus.InvalidCursor => new(400, Error: "cost_invalid_cursor"),
            PricingReadStatus.CatalogChanged => new(409, Error: "cost_catalog_changed"),
            PricingReadStatus.SnapshotChanged =>
                new(409, Error: "cost_analytics_snapshot_changed"),
            PricingReadStatus.ResponseTooLarge =>
                new(503, Error: "cost_response_too_large"),
            PricingReadStatus.Busy => new(503, Error: "cost_store_busy"),
            _ => new(503, Error: "cost_store_unavailable"),
        };

    private static CostHttpResult Error(string? code) =>
        code switch
        {
            "cost_configuration_not_found" or "cost_session_not_found" =>
                new(404, Error: code),
            "cost_preview_capacity_reached" or "cost_stale_preview"
                or "cost_stale_head" or "cost_catalog_changed"
                or "cost_selection_changed" or "cost_idempotency_conflict"
                or "cost_recalculation_in_progress" or "cost_session_not_eligible"
                => new(409, Error: code),
            "cost_store_busy" => new(503, Error: code),
            "cost_store_unavailable" or "cost_response_too_large" =>
                new(503, Error: code),
            "cost_request_too_large" => new(413, Error: code),
            _ => new(400, Error: code ?? "cost_invalid_request"),
        };

    private static CostHttpResult StoreFailure(
        PricingStoreStatus status,
        string? code) =>
        status switch
        {
            PricingStoreStatus.Busy => new(503, Error: "cost_store_busy"),
            PricingStoreStatus.Unavailable => new(503, Error: "cost_store_unavailable"),
            _ => Error(code),
        };
}
