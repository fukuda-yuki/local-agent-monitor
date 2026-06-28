using System.Text.Encodings.Web;
using CopilotAgentObservability.LocalMonitor.Events;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor;

internal static class MonitorHost
{
    private const string JsonContentType = "application/json";
    private const string TracePath = "/v1/traces";

    private static readonly TimeSpan DefaultCommitTimeout = TimeSpan.FromSeconds(5);

    public static WebApplication Build(MonitorOptions options)
    {
        return Build(options, testOptions: null);
    }

    internal static WebApplication Build(MonitorOptions options, MonitorHostTestOptions? testOptions)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Pin the application name to this assembly so MVC discovers the monitor's
            // compiled Razor pages and so the static web assets manifest
            // (CopilotAgentObservability.LocalMonitor.staticwebassets.runtime.json)
            // resolves under both the real host and the test host (whose entry
            // assembly is the test runner, not this one).
            ApplicationName = typeof(MonitorHost).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(options.Url);
        // Serve wwwroot/monitor.css and wwwroot/monitor.js from the static web
        // assets manifest. CreateBuilder only auto-loads it in Development, but the
        // monitor runs in the default Production environment, so load it explicitly.
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.ConfigureKestrel(kestrelOptions =>
        {
            kestrelOptions.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
        });

        var queue = testOptions?.Queue ?? new IngestionQueue();
        var health = testOptions?.Health ?? new MonitorHealthState();
        health.SetLoopbackBound(true);
        var commitTimeout = testOptions?.CommitTimeout ?? DefaultCommitTimeout;
        var eventBroker = new MonitorEventBroker();
        builder.Services.AddRazorPages();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(health);

        if (testOptions?.StartWriter ?? true)
        {
            var writer = testOptions?.Writer
                ?? new RawTelemetryStoreWriter(
                    new RawTelemetryStore(options.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter));
            var worker = new IngestionWriterWorker(queue, writer, health);
            builder.Services.AddHostedService(_ => worker);
        }

        var projectionStore = testOptions?.ProjectionStore
            ?? new RawTelemetryStoreProjectionStore(
                new RawTelemetryStore(options.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter));
        builder.Services.AddSingleton(projectionStore);

        if (testOptions?.StartProjectionWorker ?? true)
        {
            // Registered after the ingestion writer so its synchronous migration
            // runs first; the projection worker also guards on migration_complete.
            var projectionWorker = new ProjectionWorker(projectionStore, health, eventBroker: eventBroker, pollInterval: testOptions?.ProjectionPollInterval);
            builder.Services.AddHostedService(_ => projectionWorker);
        }

        var app = builder.Build();
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                if (exception is BadHttpRequestException badRequestException
                    && badRequestException.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    await WriteFailureAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large", "Trace payload exceeds the configured request body size limit.");
                    return;
                }

                await WriteFailureAsync(context, StatusCodes.Status500InternalServerError, "internal_error", "The request could not be processed.");
            });
        });
        app.Use(async (context, next) =>
        {
            if (!IsValidHostHeader(context.Request.Host.Host))
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_host", "Host header must be loopback.");
                return;
            }

            await next();
        });
        app.UseStaticFiles();
        app.MapRazorPages();
        app.MapGet("/health/live", async context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = JsonContentType;
            await context.Response.WriteAsync("""{"status":"live"}""");
        });
        app.MapGet("/health/ready", async context =>
        {
            var readiness = health.Evaluate(options.IngestionStallThresholdSeconds, options.ProjectionLagThresholdSeconds);
            context.Response.StatusCode = readiness.IsReady
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = JsonContentType;
            await context.Response.WriteAsync(MonitorReadinessJson.Serialize(readiness));
        });
        app.MapGet("/api/monitor/ingestions", async context =>
        {
            if (!TryParseCursorQuery(context, out var after, out var limit))
            {
                await WriteInvalidCursorQueryAsync(context);
                return;
            }

            try
            {
                var page = projectionStore.ListMonitorIngestions(after, limit);
                var items = page.Items.Select(row => new
                {
                    raw_record_id = row.RawRecordId,
                    received_at = row.ReceivedAt,
                    source = row.Source,
                    trace_id = row.TraceId,
                    client_kind = row.ClientKind,
                    span_count = row.SpanCount,
                    projected_at = row.ProjectedAt,
                });
                long? nextCursor = page.HasMore && page.Items.Count > 0 ? page.Items[^1].RawRecordId : null;
                await WriteJsonAsync(context, new { items, next_cursor = nextCursor });
            }
            catch (PersistenceBusyException)
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
            }
        });
        app.MapGet("/api/monitor/traces", async context =>
        {
            if (!TryParseCursorQuery(context, out var after, out var limit))
            {
                await WriteInvalidCursorQueryAsync(context);
                return;
            }

            try
            {
                var page = projectionStore.ListMonitorTraces(after, limit);
                var items = page.Items.Select(row => new
                {
                    id = row.Id,
                    trace_id = row.TraceId,
                    client_kind = row.ClientKind,
                    experiment_id = row.ExperimentId,
                    task_id = row.TaskId,
                    task_category = row.TaskCategory,
                    agent_variant = row.AgentVariant,
                    prompt_version = row.PromptVersion,
                    span_count = row.SpanCount,
                    tool_call_count = row.ToolCallCount,
                    error_count = row.ErrorCount,
                    first_seen_at = row.FirstSeenAt,
                    last_seen_at = row.LastSeenAt,
                    projected_at = row.ProjectedAt,
                    input_tokens = row.InputTokens,
                    output_tokens = row.OutputTokens,
                    total_tokens = row.TotalTokens,
                    turn_count = row.TurnCount,
                    agent_invocation_count = row.AgentInvocationCount,
                    duration_ms = row.DurationMs,
                    primary_model = row.PrimaryModel,
                });
                long? nextCursor = page.HasMore && page.Items.Count > 0 ? page.Items[^1].Id : null;
                await WriteJsonAsync(context, new { items, next_cursor = nextCursor });
            }
            catch (PersistenceBusyException)
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
            }
        });
        app.MapGet("/api/monitor/traces/{traceId}/spans", async (string traceId, HttpContext context) =>
        {
            if (!TryParseCursorQuery(context, out var after, out var limit))
            {
                await WriteInvalidCursorQueryAsync(context);
                return;
            }

            try
            {
                var page = projectionStore.ListMonitorSpans(traceId, after, limit);
                var items = page.Items.Select(row => new
                {
                    id = row.Id,
                    raw_record_id = row.RawRecordId,
                    trace_id = row.TraceId,
                    span_id = row.SpanId,
                    parent_span_id = row.ParentSpanId,
                    span_ordinal = row.SpanOrdinal,
                    operation = row.Operation,
                    category = row.Category,
                    tool_name = row.ToolName,
                    tool_type = row.ToolType,
                    mcp_tool_name = row.McpToolName,
                    mcp_server_hash = row.McpServerHash,
                    agent_name = row.AgentName,
                    request_model = row.RequestModel,
                    response_model = row.ResponseModel,
                    input_tokens = row.InputTokens,
                    output_tokens = row.OutputTokens,
                    total_tokens = row.TotalTokens,
                    reasoning_tokens = row.ReasoningTokens,
                    cache_read_tokens = row.CacheReadTokens,
                    cache_creation_tokens = row.CacheCreationTokens,
                    status = row.Status,
                    error_type = row.ErrorType,
                    finish_reasons = row.FinishReasons,
                    conversation_id = row.ConversationId,
                    duration_ms = row.DurationMs,
                    start_time = row.StartTime,
                    end_time = row.EndTime,
                    projected_at = row.ProjectedAt,
                });
                long? nextCursor = page.HasMore && page.Items.Count > 0 ? page.Items[^1].Id : null;
                await WriteJsonAsync(context, new { items, next_cursor = nextCursor });
            }
            catch (PersistenceBusyException)
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
            }
        });
        app.MapGet("/events", async context =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentType = "text/event-stream";
            using var subscription = eventBroker.Subscribe();
            await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try
            {
                await foreach (var evt in subscription.Reader.ReadAllAsync(context.RequestAborted))
                {
                    await context.Response.WriteAsync($"id: {evt.Id}\nevent: {evt.Type}\ndata: {{}}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected or host shutting down; end the stream quietly.
            }
        });
        if (!options.SanitizedOnly)
        {
            // A raw / PII surface (alongside the trace-detail page), active by
            // default per D023; --sanitized-only removes it (route absent → 404).
            // Same-origin enforced (cross-site browser reads cannot exfiltrate),
            // no-store, and the payload rendered as HTML-encoded inert text.
            app.MapGet("/traces/{rawRecordId:long}/raw", async (long rawRecordId, HttpContext context) =>
            {
                if (IsCrossSiteRequest(context))
                {
                    await WriteFailureAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden", "The raw detail view is same-origin only.");
                    return;
                }

                RawTelemetryRecord? record;
                try
                {
                    record = projectionStore.GetRawRecordById(rawRecordId);
                }
                catch (PersistenceBusyException)
                {
                    await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
                    return;
                }

                if (record is null)
                {
                    await WriteFailureAsync(context, StatusCodes.Status404NotFound, "raw_record_not_found", "No raw record exists for that id.");
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.ContentType = "text/html; charset=utf-8";
                var encodedPayload = HtmlEncoder.Default.Encode(record.PayloadJson);
                await context.Response.WriteAsync(
                    $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Raw record {rawRecordId}</title></head><body><pre>{encodedPayload}</pre></body></html>");
            });
        }

        app.MapPost(TracePath, async context =>
        {
            if (context.Request.ContentLength > options.MaxRequestBodyBytes)
            {
                await WriteFailureAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large", "Trace payload exceeds the configured request body size limit.");
                return;
            }

            byte[] body;
            try
            {
                using var bodyStream = new MemoryStream();
                await context.Request.Body.CopyToAsync(bodyStream, context.RequestAborted);
                body = bodyStream.ToArray();
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                await WriteFailureAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large", "Trace payload exceeds the configured request body size limit.");
                return;
            }
            catch (OperationCanceledException)
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "shutting_down", "The local monitor is shutting down.");
                return;
            }

            string payloadJson;
            try
            {
                payloadJson = OtlpTracePayloadDecoder.DecodeTracePayload(context.Request.ContentType, body);
                OtlpTracePayloadDecoder.EnsurePayloadContainsSpan(payloadJson);
            }
            catch (UnsupportedOtlpContentTypeException)
            {
                await WriteFailureAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_content_type", "Only application/json and application/x-protobuf are supported.");
                return;
            }
            catch (JsonException)
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_payload", "Trace payload is not valid OTLP JSON.");
                return;
            }
            catch (InvalidDataException)
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_payload", "Trace payload is not valid OTLP trace data.");
                return;
            }

            RawTelemetryRecord record;
            try
            {
                record = RawOtlpIngestor.CreateRecordFromPayloadJson(payloadJson, DateTimeOffset.UtcNow);
            }
            catch (JsonException)
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_payload", "Trace payload is not valid OTLP JSON.");
                return;
            }
            catch (InvalidDataException)
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_payload", "Trace payload is not valid OTLP trace data.");
                return;
            }

            if (!queue.TryEnqueue(record, out var request))
            {
                health.RecordBackpressure();
                if (queue.IsClosed)
                {
                    await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "shutting_down", "The local monitor is shutting down.");
                }
                else
                {
                    await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "queue_full", "The local monitor ingestion queue is full.");
                }

                return;
            }

            IngestionCommitResult result;
            try
            {
                result = await request.Completion.WaitAsync(commitTimeout, context.RequestAborted);
            }
            catch (TimeoutException)
            {
                // A commit that never acks is a form of "writer unable to commit":
                // start the same stall window queue-full / persistence failures use
                // so sustained timeouts surface as ingestion_stalled in readiness.
                health.RecordBackpressure();
                await WriteFailureAsync(context, StatusCodes.Status504GatewayTimeout, "commit_timeout", "Trace payload was not committed within the allowed time.");
                return;
            }
            catch (OperationCanceledException)
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "shutting_down", "The local monitor is shutting down.");
                return;
            }

            switch (result.Status)
            {
                case IngestionCommitStatus.Committed:
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = JsonContentType;
                    await context.Response.WriteAsync($$"""{"accepted":true,"rawRecordId":{{result.RawRecordId}}}""");
                    break;
                case IngestionCommitStatus.Busy:
                    await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "Trace payload could not be persisted because the raw store is busy.");
                    break;
                default:
                    await WriteFailureAsync(context, StatusCodes.Status500InternalServerError, "persistence_failed", "Trace payload could not be persisted.");
                    break;
            }
        });
        app.MapMethods(TracePath, ["GET", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], async context =>
        {
            await WriteFailureAsync(context, StatusCodes.Status405MethodNotAllowed, "method_not_allowed", "Only POST is supported for /v1/traces.");
        });
        app.MapFallback(async context =>
        {
            await WriteFailureAsync(context, StatusCodes.Status404NotFound, "unsupported_endpoint", "Only /v1/traces is supported.");
        });

        return app;
    }

    public static async Task<int> RunAsync(MonitorOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        await using var app = Build(options);
        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (IOException exception) when (IsAddressInUse(exception))
        {
            error.WriteLine("error: failed to start local monitor: port is already in use.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }

        output.WriteLine($"Local monitor listening on {options.Url}.");
        output.WriteLine($"Raw store: {options.DatabasePath}");
        output.WriteLine("Press Ctrl+C to stop.");

        try
        {
            await app.WaitForShutdownAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        return 0;
    }

    private static bool IsValidHostHeader(string? host)
    {
        return !string.IsNullOrWhiteSpace(host) && MonitorOptions.IsAllowedLoopbackHost(host);
    }

    /// <summary>
    /// Strict same-origin check for the raw-bearing routes (raw-detail route and
    /// the trace-detail page): a browser <c>Sec-Fetch-Site</c> other than
    /// <c>same-origin</c> / <c>none</c>, or an <c>Origin</c> that does not match the
    /// request's own scheme/host/port, is a cross-site request and is refused
    /// (blocks other-origin browser exfiltration).
    /// </summary>
    internal static bool IsCrossSiteRequest(HttpContext context)
    {
        var secFetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (!string.IsNullOrEmpty(secFetchSite)
            && !string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(secFetchSite, "none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var origin = context.Request.Headers["Origin"].ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            var expected = $"{context.Request.Scheme}://{context.Request.Host.Value}";
            if (!string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current.GetType().Name.Contains("AddressInUse", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WriteFailureAsync(HttpContext context, int statusCode, string error, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = JsonContentType;
        await context.Response.WriteAsync($$"""{"accepted":false,"error":"{{error}}","message":"{{message}}"}""");
    }

    private static Task WriteInvalidCursorQueryAsync(HttpContext context) =>
        WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_query", "after must be a non-negative integer and limit must be between 1 and 200.");

    private static async Task WriteJsonAsync(HttpContext context, object body)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = JsonContentType;
        await context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    private static bool TryParseCursorQuery(HttpContext context, out long after, out int limit)
    {
        const int defaultLimit = 50;
        const int maxLimit = 200;
        after = 0;
        limit = defaultLimit;

        var afterValue = context.Request.Query["after"].ToString();
        if (!string.IsNullOrEmpty(afterValue)
            && (!long.TryParse(afterValue, NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))
        {
            return false;
        }

        var limitValue = context.Request.Query["limit"].ToString();
        if (!string.IsNullOrEmpty(limitValue)
            && (!int.TryParse(limitValue, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > maxLimit))
        {
            return false;
        }

        return true;
    }
}

internal sealed class MonitorHostTestOptions
{
    public IngestionQueue? Queue { get; init; }

    public IRawTelemetryWriter? Writer { get; init; }

    public MonitorHealthState? Health { get; init; }

    public TimeSpan? CommitTimeout { get; init; }

    public bool StartWriter { get; init; } = true;

    public IMonitorProjectionStore? ProjectionStore { get; init; }

    public bool StartProjectionWorker { get; init; } = true;

    public TimeSpan? ProjectionPollInterval { get; init; }
}
