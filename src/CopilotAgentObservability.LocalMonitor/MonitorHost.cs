using System.Text.Encodings.Web;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Events;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor;

internal static class MonitorHost
{
    private const string JsonContentType = "application/json";
    private const string TracePath = "/v1/traces";

    private static readonly TimeSpan DefaultCommitTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new(JsonSerializerDefaults.Web);

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

        // Local Monitor is a local runtime tool, so read Secret Manager explicitly.
        // API key values must not reach logs, diagnostics, or repository-safe output.
        builder.Configuration.AddUserSecrets(typeof(MonitorHost).Assembly, optional: true, reloadOnChange: false);

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
        var summaryService = new MonitorSummaryService(projectionStore);
        builder.Services.AddSingleton(summaryService);
        var analysisStore = testOptions?.AnalysisStore ?? new SqliteMonitorAnalysisStore(options.DatabasePath);
        analysisStore.CreateSchema();
        builder.Services.AddSingleton(analysisStore);
        var analysisRunner = testOptions?.AnalysisRunner ?? new DotNetCopilotRawAnalysisRunner(analysisStore, projectionStore, builder.Configuration);
        builder.Services.AddSingleton(analysisRunner);

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
                var items = page.Items.Select(ToTraceDto);
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
        app.MapGet("/api/monitor/summary", async context =>
        {
            if (!TryParseLimitQuery(context, out var limit))
            {
                await WriteInvalidLimitQueryAsync(context);
                return;
            }

            try
            {
                var summary = summaryService.BuildSummary(limit);
                await WriteJsonAsync(context, new
                {
                    scope = new { limit, trace_count = summary.TraceCount },
                    latest_trace = ToTraceDto(summary.LatestTrace),
                    top_token_trace = ToTraceDto(summary.TopTokenTrace),
                    error_trace = ToTraceDto(summary.ErrorTrace),
                    per_model_summary = summary.PerModelSummary.Select(m => new
                    {
                        model = m.Model,
                        trace_count = m.TraceCount,
                        total_tokens = m.TotalTokens,
                        error_count = m.ErrorCount,
                    }),
                    per_client_kind_summary = summary.PerClientKindSummary.Select(c => new
                    {
                        client_kind = c.ClientKind,
                        trace_count = c.TraceCount,
                        total_tokens = c.TotalTokens,
                        error_count = c.ErrorCount,
                    }),
                });
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
            app.MapPost("/traces/{traceId}/analysis", async (string traceId, HttpContext context) =>
            {
                if (IsCrossSiteRequest(context))
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden", "The raw analysis action is same-origin only.");
                    return;
                }

                if (!HasMonitorCsrfHeader(context))
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status403Forbidden, "csrf_required", "The raw analysis action requires the local monitor CSRF header.");
                    return;
                }

                AnalysisStartPayload? payload;
                try
                {
                    payload = await JsonSerializer.DeserializeAsync<AnalysisStartPayload>(context.Request.Body, CaseInsensitiveJson, context.RequestAborted);
                }
                catch (JsonException)
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_payload", "Analysis request payload must be valid JSON.");
                    return;
                }

                if (!MonitorAnalysisWire.TryParseFocus(payload?.Focus, out var focus))
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_focus", "Analysis focus is not supported.");
                    return;
                }

                var start = analysisStore.StartRun(
                    traceId,
                    payload?.RawRecordId,
                    payload?.SpanId,
                    focus,
                    DateTimeOffset.UtcNow);
                await analysisRunner.StartAsync(
                    new MonitorAnalysisContext(start.RunId, traceId, payload?.RawRecordId, payload?.SpanId, focus),
                    context.RequestAborted);
                context.Response.Headers["Cache-Control"] = "no-store";
                await WriteJsonAsync(context, new
                {
                    run_id = start.RunId,
                    status = MonitorAnalysisStatus.Queued.ToWireValue(),
                });
            });

            app.MapGet("/traces/{traceId}/analysis/runs/{runId:long}", async (string traceId, long runId, HttpContext context) =>
            {
                if (IsCrossSiteRequest(context))
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden", "The raw analysis result is same-origin only.");
                    return;
                }

                var run = analysisStore.GetRun(runId);
                if (run is null || !string.Equals(run.TraceId, traceId, StringComparison.Ordinal))
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status404NotFound, "analysis_run_not_found", "No analysis run exists for that trace.");
                    return;
                }

                context.Response.Headers["Cache-Control"] = "no-store";
                await WriteJsonAsync(context, ToRunDto(run, includeRawResult: true));
            });

            app.MapGet("/traces/{traceId}/analysis/runs/{runId:long}/safe-summary", async (string traceId, long runId, HttpContext context) =>
            {
                var run = analysisStore.GetRun(runId);
                if (run is null || !string.Equals(run.TraceId, traceId, StringComparison.Ordinal))
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status404NotFound, "analysis_run_not_found", "No analysis run exists for that trace.");
                    return;
                }

                var summary = analysisStore.GenerateRepositorySafeSummary(runId, DateTimeOffset.UtcNow);
                context.Response.Headers["Cache-Control"] = "no-store";
                await WriteJsonAsync(context, new
                {
                    repository_safe = true,
                    run_id = summary.RunId,
                    generated_at = summary.GeneratedAt,
                    markdown = summary.Markdown,
                });
            });

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

            // A raw prompt-label surface (D039), alongside the raw-detail view above.
            // --sanitized-only removes it (route absent → 404). Same-origin enforced,
            // no-store, and no trace-id format validation (matches D035's
            // /traces/{traceId}/analysis/... precedent: an unknown/malformed id
            // simply yields prompt_label: null, which is a normal outcome).
            app.MapGet("/traces/{traceId}/prompt-label", async (string traceId, HttpContext context) =>
            {
                if (IsCrossSiteRequest(context))
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden", "The prompt label is same-origin only.");
                    return;
                }

                IReadOnlyList<RawTelemetryRecord> records;
                try
                {
                    records = projectionStore.ListRawRecordsByTraceId(traceId, 1);
                }
                catch (PersistenceBusyException)
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
                    return;
                }

                var label = records.Count > 0
                    ? MonitorPromptExtractor.ExtractPromptLabel(records[0].PayloadJson, traceId)
                    : null;

                context.Response.Headers["Cache-Control"] = "no-store";
                await WriteJsonAsync(context, new { trace_id = traceId, prompt_label = label });
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

    private static bool HasMonitorCsrfHeader(HttpContext context) =>
        string.Equals(context.Request.Headers["x-monitor-csrf"].ToString(), "local-monitor", StringComparison.Ordinal);

    /// <summary>The same <c>compactTrace</c>-shaped projection <c>/api/monitor/traces</c> emits per item, reused for the summary's embedded highlight traces.</summary>
    private static object? ToTraceDto(MonitorTraceRow? row) => row is null ? null : new
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
        repository_name = row.RepositoryName,
        workspace_label = row.WorkspaceLabel,
        repo_snapshot = row.RepoSnapshot,
    };

    private static object ToRunDto(MonitorAnalysisRun run, bool includeRawResult) => new
    {
        id = run.Id,
        trace_id = run.TraceId,
        raw_record_id = run.RawRecordId,
        span_id = run.SpanId,
        focus = run.Focus.ToWireValue(),
        status = run.Status.ToWireValue(),
        requested_at = run.RequestedAt,
        started_at = run.StartedAt,
        completed_at = run.CompletedAt,
        result_markdown = includeRawResult ? run.ResultMarkdown : null,
        error_message = run.ErrorMessage,
    };

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

    private static async Task WriteNoStoreFailureAsync(HttpContext context, int statusCode, string error, string message)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        await WriteFailureAsync(context, statusCode, error, message);
    }

    private static Task WriteInvalidCursorQueryAsync(HttpContext context) =>
        WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_query", "after must be a non-negative integer and limit must be between 1 and 200.");

    private static Task WriteInvalidLimitQueryAsync(HttpContext context) =>
        WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_query", "limit must be between 1 and 200.");

    private static bool TryParseLimitQuery(HttpContext context, out int limit)
    {
        const int defaultLimit = 50;
        const int maxLimit = 200;
        limit = defaultLimit;

        var limitValue = context.Request.Query["limit"].ToString();
        if (!string.IsNullOrEmpty(limitValue)
            && (!int.TryParse(limitValue, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > maxLimit))
        {
            return false;
        }

        return true;
    }

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

    public IMonitorAnalysisStore? AnalysisStore { get; init; }

    public IMonitorAnalysisRunner? AnalysisRunner { get; init; }

    public bool StartProjectionWorker { get; init; } = true;

    public TimeSpan? ProjectionPollInterval { get; init; }
}

internal sealed record AnalysisStartPayload(string? Focus, long? RawRecordId, string? SpanId);
