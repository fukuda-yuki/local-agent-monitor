using System.Text.Encodings.Web;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Doctor.ClaudeCode;
using CopilotAgentObservability.LocalMonitor.Events;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.LocalMonitor.ProposalApply;
using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.LocalMonitor.SourceCompatibility;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Doctor.ClaudeCode;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry.Sessions;
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
        if (testOptions?.UseUserSecrets ?? true)
        {
            builder.Configuration.AddUserSecrets(typeof(MonitorHost).Assembly, optional: true, reloadOnChange: false);
        }
        if (testOptions?.ConfigurationValues is not null)
        {
            builder.Configuration.AddInMemoryCollection(testOptions.ConfigurationValues);
        }

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

        var timeProvider = testOptions?.TimeProvider ?? TimeProvider.System;
        var queue = testOptions?.Queue ?? new IngestionQueue(timeProvider);
        var health = testOptions?.Health ?? new MonitorHealthState();
        health.SetLoopbackBound(true);
        var commitTimeout = testOptions?.CommitTimeout ?? DefaultCommitTimeout;
        var sourceMetadataProvider = testOptions?.SourceMetadataProvider ?? FixedOtlpTraceSourceMetadataProvider.Default;
        var sourceFingerprintRegistry = testOptions?.SourceFingerprintRegistry
            ?? VerifiedSourceFingerprintRegistry.Create([], [], []);
        var eventBroker = new MonitorEventBroker();
        builder.Services.AddRazorPages();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(health);
        var retentionContext = RetentionCatalogContext.InitializeNewOwnedDatabase(options.DatabasePath, timeProvider);
        builder.Services.AddSingleton(retentionContext);
        var doctorApplication = testOptions?.DoctorApplication
            ?? CreateDoctorApplication(options.DatabasePath, timeProvider, testOptions?.DoctorApplicationFactory);
        builder.Services.AddSingleton(doctorApplication);
        var doctorUiApplication = testOptions?.DoctorUiApplication
            ?? new DoctorUiApplication(options.DatabasePath, options.Url);
        builder.Services.AddSingleton(doctorUiApplication);
        if (testOptions is null)
        {
            var observer = new ClaudeDoctorCandidateObserver(options.DatabasePath, retentionContext, timeProvider);
            builder.Services.AddHostedService(_ => new ClaudeDoctorCandidateWorker(observer));
        }

        var compatibilityStore = testOptions?.SourceCompatibilityStore
            ?? new SqliteSourceCompatibilityStore(options.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        compatibilityStore.CreateSchema();
        var runtimeStateStore = new SqliteMonitorRuntimeStateStore(
            options.DatabasePath,
            timeProvider,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        runtimeStateStore.CreateSchema();
        runtimeStateStore.Upsert(options.SanitizedOnly);
        if (testOptions?.StartWriter ?? true)
        {
            var commitStore = testOptions?.IngestionCommitStore
                ?? new SqliteIngestionCommitStore(options.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter, timeProvider);
            var worker = new IngestionWriterWorker(queue, commitStore, compatibilityStore, health);
            builder.Services.AddHostedService(_ => worker);
        }

        var projectionStore = testOptions?.ProjectionStore
            ?? new RawTelemetryStoreProjectionStore(
                new RawTelemetryStore(options.DatabasePath, retentionContext, timeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter));
        builder.Services.AddSingleton(projectionStore);
        var summaryService = new MonitorSummaryService(projectionStore);
        builder.Services.AddSingleton(summaryService);
        var overviewService = new MonitorOverviewService(projectionStore, testOptions?.TimeProvider);
        builder.Services.AddSingleton(overviewService);
        var analysisStore = testOptions?.AnalysisStore ?? new SqliteMonitorAnalysisStore(options.DatabasePath, retentionContext, timeProvider);
        analysisStore.CreateSchema();
        builder.Services.AddSingleton(analysisStore);
        var sessionTimeProvider = timeProvider;
        ISessionStore sessionStore = testOptions?.SessionStore ?? new SqliteSessionStore(options.DatabasePath, retentionContext, sessionTimeProvider);
        sessionStore.CreateSchema();
        builder.Services.AddSingleton(sessionStore);
        var proposalApplyRuntimePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!, "proposal-apply");
        var proposalApplyService = new ProposalApplyService(options.ApplyRoots ?? [], proposalApplyRuntimePath, sessionStore);
        builder.Services.AddSingleton(proposalApplyService);
        var sessionEventQueue = testOptions?.SessionEventQueue ?? new SessionEventQueue();
        var sessionEventNormalizer = new SessionEventNormalizer(sessionStore, sessionTimeProvider);
        var sessionOtelEnricher = new SqliteSessionOtelEnricher(options.DatabasePath, sessionStore, retentionContext, sessionTimeProvider);
        builder.Services.AddSingleton(sessionEventQueue);
        builder.Services.AddSingleton(sessionEventNormalizer);
        builder.Services.AddSingleton(sessionOtelEnricher);
        if (testOptions?.StartSessionWriter ?? true)
        {
            builder.Services.AddHostedService(_ => new SessionEventWriterWorker(sessionEventQueue, sessionEventNormalizer));
        }
        var analysisRunner = testOptions?.AnalysisRunner
            ?? new DotNetCopilotRawAnalysisRunner(
                analysisStore,
                projectionStore,
                builder.Configuration,
                new AnalysisSdkDirectoryOwner(new RetentionCatalogStore(retentionContext, timeProvider), timeProvider),
                new CopilotAnalysisSdkExecutor(),
                timeProvider);
        builder.Services.AddSingleton(analysisRunner);

        if (testOptions?.StartProjectionWorker ?? true)
        {
            // Registered after the ingestion writer so its synchronous migration
            // runs first; the projection worker also guards on migration_complete.
            var projectionWorker = new ProjectionWorker(
                projectionStore,
                health,
                compatibilityStore,
                eventBroker: eventBroker,
                pollInterval: testOptions?.ProjectionPollInterval);
            builder.Services.AddHostedService(_ => projectionWorker);
        }
        if (testOptions?.StartSessionOtelEnrichment ?? true)
        {
            builder.Services.AddHostedService(_ => new SessionOtelEnrichmentWorker(sessionOtelEnricher, testOptions?.SessionOtelPollInterval));
        }

        var retentionCatalog = new RetentionCatalogStore(retentionContext, timeProvider);
        var retentionAdapters = new RetentionAdapterRegistry([
            new SessionEventContentRetentionAdapter(retentionCatalog),
            new RawRecordRetentionAdapter(retentionCatalog),
            new MonitorAnalysisRetentionAdapter(retentionCatalog),
            new SensitiveBundleRetentionAdapter(retentionCatalog, timeProvider),
            new AnalysisSdkDirectoryRetentionAdapter(retentionCatalog, timeProvider)
        ]);
        retentionCatalog.RegisterAdapterCoverage(retentionAdapters);
        builder.Services.AddSingleton(retentionCatalog);
        builder.Services.AddSingleton(retentionAdapters);
        if (testOptions?.StartRetentionCleanupWorker ?? true)
        {
            builder.Services.AddHostedService(_ => new RetentionCleanupWorker(new RetentionCleanupCoordinator(retentionCatalog, retentionAdapters, timeProvider), timeProvider));
        }

        var app = builder.Build();
        async Task<SourceProjectionState> ProjectTraceAsync(MonitorTraceRow row, CancellationToken cancellationToken)
        {
            var result = await projectionStore.ListRawRecordsByTraceIdAsync(row.TraceId, 200, RetentionReadKind.Access, cancellationToken);
            if (result.Disposition == RetentionReadDisposition.Busy) throw new PersistenceBusyException();
            if (result.Lease is null) return SourceProjectionStateBuilder.Build([], FindSessionForTrace(sessionStore, row.TraceId));
            await using var lease = result.Lease;
            var observations = lease.Value
                .Select(record => record.Id is { } id ? compatibilityStore.GetByRawRecordId(id) : null)
                .Where(observation => observation is not null)
                .Cast<SourceCompatibilityRow>()
                .ToArray();
            var session = FindSessionForTrace(sessionStore, row.TraceId);
            return SourceProjectionStateBuilder.Build(observations, session);
        }

        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                if (RetentionMutationRoutes.IsRetentionPath(context.Request.Path))
                {
                    await RetentionMutationRoutes.WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, RetentionMutationErrorCodes.CatalogUnavailable);
                    return;
                }
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
            var retentionPath = RetentionMutationRoutes.IsRetentionPath(context.Request.Path);
            var sanitizedExportPath = SanitizedExportRoutes.IsPath(context.Request.Path);
            if (retentionPath || sanitizedExportPath)
            {
                context.Response.Headers.CacheControl = "no-store";
            }
            if (!IsValidHostHeader(context.Request.Host.Host))
            {
                if (retentionPath)
                {
                    await RetentionMutationRoutes.WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_host");
                }
                else if (sanitizedExportPath)
                {
                    await SanitizedExportRoutes.ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_host");
                }
                else if (DoctorUiRoutes.IsDoctorUiPath(context.Request.Path))
                {
                    await DoctorUiRoutes.WriteInvalidHostAsync(context);
                }
                else if (DoctorRoutes.IsDoctorPath(context.Request.Path))
                {
                    await DoctorRoutes.WriteInvalidHostAsync(context);
                }
                else
                {
                    await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_host", "Host header must be loopback.");
                }
                return;
            }

            await next();
        });
        app.UseStaticFiles();
        app.MapRazorPages();
        DoctorRoutes.Map(app, doctorApplication);
        DoctorUiRoutes.Map(app, doctorUiApplication);
        DoctorEvidenceRoutes.Map(app, compatibilityStore, sessionStore);
        RetentionStatusRoutes.Map(app, retentionCatalog, () => testOptions?.StartRetentionCleanupWorker ?? true);
        RetentionMutationRoutes.Map(app, retentionCatalog, timeProvider, testOptions?.RetentionMutationApplicationFactory?.Invoke(retentionCatalog, timeProvider));
        SanitizedExportRoutes.Map(app, options.DatabasePath);
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
        SessionRoutes.Map(
            app,
            options,
            sessionEventQueue,
            sessionStore,
            proposalApplyService,
            sessionOtelEnricher,
            testOptions?.SessionCommitTimeout ?? DefaultCommitTimeout,
            sessionTimeProvider,
            projectionStore,
            compatibilityStore);
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
        app.MapGet("/api/monitor/source-diagnostics", async context =>
        {
            if (!TryParseCursorQuery(context, out var after, out var limit))
            {
                await WriteInvalidCursorQueryAsync(context);
                return;
            }

            try
            {
                var rows = compatibilityStore.List(after, limit);
                var items = rows.Select(row => new
                {
                    observation_id = row.ObservationId,
                    ingest_batch_id = row.IngestBatchId,
                    source_surface = row.SourceSurface,
                    source_application_version = row.SourceApplicationVersion,
                    source_adapter = row.SourceAdapter,
                    adapter_version = row.AdapterVersion,
                    schema_fingerprint = row.SchemaFingerprint,
                    inventory_hash = row.InventoryHash,
                    compatibility_state = row.CompatibilityState,
                    reason_codes = row.ReasonCodes,
                    unknown_span_count = row.UnknownSpanCount,
                    unknown_event_count = row.UnknownEventCount,
                    unknown_attribute_count = row.UnknownAttributeCount,
                    observed_at = row.ObservedAt,
                    next_action = row.NextAction,
                });
                long? nextCursor = rows.Count == limit && compatibilityStore.List(rows[^1].Id, 1).Count > 0
                    ? rows[^1].Id
                    : null;
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
                var items = new List<object>();
                foreach (var row in page.Items) items.Add(ToTraceDto(row, await ProjectTraceAsync(row, context.RequestAborted))!);
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
        app.MapGet("/api/monitor/traces/{traceId}/agent-graph", async (string traceId, HttpContext context) =>
        {
            try
            {
                if (projectionStore.GetMonitorTrace(traceId) is null)
                {
                    await WriteFailureAsync(context, StatusCodes.Status404NotFound, "trace_not_found", "The requested trace was not found.");
                    return;
                }

                var spans = projectionStore.GetSpansForTrace(traceId);
                var exactClaudeRawRecordIds = spans
                    .Select(span => span.RawRecordId)
                    .Distinct()
                    .OrderBy(rawRecordId => rawRecordId)
                    .Take(200)
                    .Where(record => string.Equals(
                        compatibilityStore.GetByRawRecordId(record)?.SourceSurface,
                        "claude-code",
                        StringComparison.Ordinal))
                    .ToHashSet();
                var graph = exactClaudeRawRecordIds.Count == 0
                    ? AgentExecutionGraphBuilder.Build(spans)
                    : AgentExecutionGraphBuilder.Build(spans, exactClaudeRawRecordIds);
                await WriteJsonAsync(context, new
                {
                    summary = new
                    {
                        main_agent_name = graph.Summary.MainAgentName,
                        root_agent_count = graph.Summary.RootAgentCount,
                        subagent_invocation_count = graph.Summary.SubagentInvocationCount,
                        unique_subagent_count = graph.Summary.UniqueSubagentCount,
                        max_agent_depth = graph.Summary.MaxAgentDepth,
                        parallel_agent_group_count = graph.Summary.ParallelAgentGroupCount,
                        relationship_quality = graph.Summary.RelationshipQuality,
                        agent_presence = graph.Summary.AgentPresence,
                    },
                    agents = graph.Agents.Select(agent => new
                    {
                        span_id = agent.SpanId,
                        agent_name = agent.AgentName,
                        agent_role = agent.AgentRole,
                        caller_agent_span_id = agent.CallerAgentSpanId,
                        model = agent.Model,
                        started_at = agent.StartedAt,
                        ended_at = agent.EndedAt,
                        duration_ms = agent.DurationMs,
                        input_tokens = agent.InputTokens,
                        output_tokens = agent.OutputTokens,
                        total_tokens = agent.TotalTokens,
                        status = agent.Status,
                        child_agent_count = agent.ChildAgentCount,
                        agent_depth = agent.AgentDepth,
                        relationship_source = agent.RelationshipSource,
                        relationship_confidence = agent.RelationshipConfidence,
                    }),
                    span_ownership = graph.SpanOwnership.Select(ownership => new
                    {
                        span_id = ownership.SpanId,
                        owning_agent_span_id = ownership.OwningAgentSpanId,
                        relationship_source = ownership.RelationshipSource,
                        relationship_confidence = ownership.RelationshipConfidence,
                    }),
                    parallel_groups = graph.ParallelGroups.Select(group => group.SpanIds),
                    graph_warnings = graph.GraphWarnings,
                });
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
                    latest_trace = ToTraceDto(summary.LatestTrace, summary.LatestTrace is null ? null : await ProjectTraceAsync(summary.LatestTrace, context.RequestAborted)),
                    top_token_trace = ToTraceDto(summary.TopTokenTrace, summary.TopTokenTrace is null ? null : await ProjectTraceAsync(summary.TopTokenTrace, context.RequestAborted)),
                    error_trace = ToTraceDto(summary.ErrorTrace, summary.ErrorTrace is null ? null : await ProjectTraceAsync(summary.ErrorTrace, context.RequestAborted)),
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
        app.MapGet("/api/monitor/overview", async context =>
        {
            var period = context.Request.Query["period"].ToString();
            if (string.IsNullOrEmpty(period))
            {
                period = "today";
            }

            if (!MonitorOverviewService.IsSupportedPeriod(period))
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_query", "period must be today, 7d, or 30d.");
                return;
            }

            try
            {
                var overview = overviewService.BuildOverview(period);
                await WriteJsonAsync(context, new
                {
                    period = overview.Period.Period,
                    range = new
                    {
                        start = MonitorOverviewService.FormatUtc(overview.Period.Start),
                        end = MonitorOverviewService.FormatUtc(overview.Period.End),
                    },
                    kpi = new
                    {
                        tokens_total = overview.Kpi.TokensTotal,
                        tokens_previous_period = overview.Kpi.TokensPreviousPeriod,
                        tokens_change_pct = overview.Kpi.TokensChangePct,
                        uncached_tokens_total = overview.Kpi.UncachedTokensTotal,
                        uncached_tokens_previous_period = overview.Kpi.UncachedTokensPreviousPeriod,
                        uncached_tokens_change_pct = overview.Kpi.UncachedTokensChangePct,
                        cache_read_tokens_total = overview.Kpi.CacheReadTokensTotal,
                        cache_aware_input_tokens = overview.Kpi.CacheAwareInputTokens,
                        effective_input_tokens = overview.Kpi.EffectiveInputTokens,
                        cache_compression_pct = overview.Kpi.CacheCompressionPct,
                        cache_read_rate_pct = overview.Kpi.CacheReadRatePct,
                        error_trace_count = overview.Kpi.ErrorTraceCount,
                        trace_count = overview.Kpi.TraceCount,
                    },
                    per_model = overview.PerModel.Select(model => new
                    {
                        model = model.Model,
                        trace_count = model.TraceCount,
                        error_trace_count = model.ErrorTraceCount,
                        total_tokens = model.TotalTokens,
                        input_tokens = model.InputTokens,
                        output_tokens = model.OutputTokens,
                        cache_read_tokens = model.CacheReadTokens,
                        cache_creation_tokens = model.CacheCreationTokens,
                        cache_read_rate_pct = model.CacheReadRatePct,
                    }),
                    hourly_tokens = overview.HourlyTokens.Select(hour => new
                    {
                        hour = hour.Hour,
                        total_tokens = hour.TotalTokens,
                    }),
                });
            }
            catch (PersistenceBusyException)
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
            }
        });
        app.MapGet("/api/monitor/trace-list", async context =>
        {
            if (!TryParseTraceListQuery(context, overviewService, out var query))
            {
                await WriteFailureAsync(context, StatusCodes.Status400BadRequest, "invalid_query", "trace-list query parameters are invalid.");
                return;
            }

            try
            {
                var page = projectionStore.ListMonitorTracesFiltered(query);
                var items = new List<object>();
                foreach (var row in page.Items) items.Add(ToTraceListDto(row, await ProjectTraceAsync(row, context.RequestAborted)));
                await WriteJsonAsync(context, new
                {
                    items,
                    total_matched = page.TotalMatched,
                    total_matched_tokens = page.TotalMatchedTokens,
                    offset = query.Offset,
                    limit = query.Limit,
                });
            }
            catch (PersistenceBusyException)
            {
                await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
            }
        });
        app.MapGet("/api/analysis/options", async context =>
        {
            var analysisOptions = CopilotAnalysisOptions.From(app.Configuration);
            await WriteJsonAsync(context, new
            {
                default_profile = analysisOptions.DefaultProfile,
                default_model = analysisOptions.DefaultModel,
                reasoning_efforts = analysisOptions.ReasoningEfforts,
                profiles = analysisOptions.Profiles.Select(profile => new
                {
                    id = profile.Id,
                    display_name = profile.DisplayName,
                    timeout_seconds = profile.TimeoutSeconds,
                    default_reasoning_effort = profile.DefaultReasoningEffort,
                }),
                models = analysisOptions.Models.Select(model => new
                {
                    id = model.Id,
                    display_name = model.DisplayName,
                    provider = model.Provider,
                    supports_reasoning_effort = model.SupportsReasoningEffort,
                    is_default = model.IsDefault,
                }),
            });
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
                    timeProvider.GetUtcNow());
                await analysisRunner.StartAsync(
                    new MonitorAnalysisContext(
                        start.RunId,
                        traceId,
                        payload?.RawRecordId,
                        payload?.SpanId,
                        focus,
                        Question: payload?.Question,
                        History: payload?.History
                            ?.Select(turn => new AnalysisHistoryTurn(turn.Question, turn.Answer))
                            .ToList(),
                        OperationToken: start.OperationToken),
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

                var rawRead = await analysisStore.ReadRawSnapshotAsync(runId, context.RequestAborted);
                if (rawRead.Disposition != RetentionReadDisposition.Granted || rawRead.Lease is null)
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status404NotFound, "analysis_run_not_found", "No analysis run exists for that trace.");
                    return;
                }

                await using var rawLease = rawRead.Lease;
                context.Response.Headers["Cache-Control"] = "no-store";
                await WriteJsonAsync(context, ToRunDto(run, rawLease.Value));
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

                RetentionReadResult<RawTelemetryRecord> result;
                try
                {
                    result = await projectionStore.GetRawRecordByIdAsync(rawRecordId, RetentionReadKind.Access, context.RequestAborted);
                }
                catch (PersistenceBusyException)
                {
                    await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
                    return;
                }

                if (result.Disposition == RetentionReadDisposition.Busy)
                {
                    await WriteFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
                    return;
                }
                if (result.Lease is null)
                {
                    await WriteFailureAsync(context, StatusCodes.Status404NotFound, "raw_record_not_found", "No raw record exists for that id.");
                    return;
                }

                await using (var lease = result.Lease)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.Headers["Cache-Control"] = "no-store";
                    context.Response.ContentType = "text/html; charset=utf-8";
                    var encodedPayload = HtmlEncoder.Default.Encode(lease.Value.PayloadJson);
                    await context.Response.WriteAsync(
                        $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Raw record {rawRecordId}</title></head><body><pre>{encodedPayload}</pre></body></html>");
                }
            });

            // A raw span-detail surface for the Sprint18 span inspector (D043).
            // Same route-boundary pattern as D035/D039: registered only in the
            // raw-default posture (route absent → 404 under --sanitized-only),
            // same-origin enforced, no-store, 404 for unknown trace/span ids.
            app.MapGet("/traces/{traceId}/spans/{spanId}/detail", async (string traceId, string spanId, HttpContext context) =>
            {
                if (IsCrossSiteRequest(context))
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden", "The span detail is same-origin only.");
                    return;
                }

                MonitorSpanRow? spanRow;
                RetentionReadResult<RawTelemetryRecord>? rawResult = null;
                try
                {
                    spanRow = projectionStore.GetMonitorSpan(traceId, spanId);
                    if (spanRow is not null)
                    {
                        rawResult = await projectionStore.GetRawRecordByIdAsync(spanRow.RawRecordId, RetentionReadKind.Access, context.RequestAborted);
                    }
                }
                catch (PersistenceBusyException)
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
                    return;
                }

                if (rawResult?.Disposition == RetentionReadDisposition.Busy)
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
                    return;
                }
                if (spanRow is null || rawResult?.Lease is null)
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status404NotFound, "span_not_found", "No span exists for that trace and span id.");
                    return;
                }

                // Best-effort formatted extraction; the raw span JSON always works.
                await using var spanLease = rawResult.Lease;
                var detail = SpanDetailExtractor.Extract(spanLease.Value.PayloadJson, traceId, spanId);

                context.Response.Headers["Cache-Control"] = "no-store";
                await WriteJsonAsync(context, new
                {
                    trace_id = traceId,
                    span_id = spanId,
                    span = new
                    {
                        operation = spanRow.Operation,
                        category = spanRow.Category,
                        tool_name = spanRow.ToolName,
                        mcp_tool_name = spanRow.McpToolName,
                        agent_name = spanRow.AgentName,
                        request_model = spanRow.RequestModel,
                        response_model = spanRow.ResponseModel,
                        parent_span_id = spanRow.ParentSpanId,
                        status = spanRow.Status,
                        error_type = spanRow.ErrorType,
                        input_tokens = spanRow.InputTokens,
                        output_tokens = spanRow.OutputTokens,
                        total_tokens = spanRow.TotalTokens,
                        cache_read_tokens = spanRow.CacheReadTokens,
                        cache_creation_tokens = spanRow.CacheCreationTokens,
                        duration_ms = spanRow.DurationMs,
                        start_time = spanRow.StartTime,
                        end_time = spanRow.EndTime,
                    },
                    tool = detail?.Tool is { } tool
                        ? new
                        {
                            arguments = tool.Arguments,
                            result_tail = tool.ResultTail,
                            result_token_estimate = tool.ResultTokenEstimate,
                            exit_code = tool.ExitCode,
                        }
                        : (object?)null,
                    llm = detail?.Llm is { } llm
                        ? new
                        {
                            messages = llm.Messages.Select(message => new
                            {
                                role = message.Role,
                                size_chars = message.SizeChars,
                                token_estimate = message.TokenEstimate,
                                preview = message.Preview,
                            }),
                            response_preview = llm.ResponsePreview,
                            response_token_estimate = llm.ResponseTokenEstimate,
                        }
                        : (object?)null,
                    error_message = detail?.ErrorMessage,
                    raw_span_json = detail?.RawSpanJson,
                });
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

                RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>> result;
                try
                {
                    result = await projectionStore.ListRawRecordsByTraceIdAsync(traceId, MonitorPromptExtractor.RecordScanLimit, RetentionReadKind.Access, context.RequestAborted);
                }
                catch (PersistenceBusyException)
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
                    return;
                }

                if (result.Disposition == RetentionReadDisposition.Busy)
                {
                    await WriteNoStoreFailureAsync(context, StatusCodes.Status503ServiceUnavailable, "persistence_busy", "The local monitor raw store is busy.");
                    return;
                }
                await using var promptLease = result.Lease;
                var label = MonitorPromptExtractor.ExtractFirstPromptLabel(
                    (promptLease?.Value ?? []).Select(record => record.PayloadJson), traceId);

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

            ValidatedIngestionBatch batch;
            OtlpTraceSourceMetadata? metadata = null;
            try
            {
                var observedAt = timeProvider.GetUtcNow();
                var decodedPayload = OtlpTracePayloadDecoder.DecodeTracePayload(context.Request.ContentType, body);
                var recognizedPayloadJson = OtlpJsonRecognizedPayloadBuilder.Build(decodedPayload.PayloadJson);
                var record = RawOtlpIngestor.CreateRecordFromPayloadJson(
                    decodedPayload.PayloadJson,
                    recognizedPayloadJson,
                    observedAt);
                metadata = sourceMetadataProvider.GetMetadata();
                var decision = SourceCompatibilityEvaluator.Assess(
                    metadata.SourceSurface,
                    metadata.SourceApplicationVersion,
                    decodedPayload.StructuralInventory,
                    CountRecognizedSpanEnvelopes(decodedPayload.StructuralInventory),
                    sourceFingerprintRegistry);
                var captureContentState = ClaudeOtlpCaptureContentStateResolver.Derive(decodedPayload.PayloadJson)
                    ?? metadata.CaptureContentState;
                var observation = SourceObservationBatchDraft.Create(
                    Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture),
                    metadata.SourceSurface,
                    metadata.SourceApplicationVersion,
                    metadata.SourceAdapter,
                    metadata.AdapterVersion,
                    decodedPayload.StructuralInventory,
                    decision,
                    captureContentState,
                    observedAt);
                batch = ValidatedIngestionBatch.Create(record, observation);
            }
            catch (UnsupportedOtlpContentTypeException)
            {
                await WriteFailureAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_content_type", "Only application/json and application/x-protobuf are supported.");
                return;
            }
            catch (JsonException)
            {
                await RecordAdapterFailureAsync(
                    context,
                    queue,
                    health,
                    commitTimeout,
                    SourceAdapterFailureDraft.CreateParseFailure(
                        Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture),
                        null, null, null, null, null, null,
                        timeProvider.GetUtcNow()),
                    StatusCodes.Status400BadRequest,
                    "invalid_payload",
                    "Trace payload is not valid OTLP JSON.");
                return;
            }
            catch (InvalidDataException)
            {
                await RecordAdapterFailureAsync(
                    context,
                    queue,
                    health,
                    commitTimeout,
                    SourceAdapterFailureDraft.CreateParseFailure(
                        Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture),
                        null, null, null, null, null, null,
                        timeProvider.GetUtcNow()),
                    StatusCodes.Status400BadRequest,
                    "invalid_payload",
                    "Trace payload is not valid OTLP trace data.");
                return;
            }
            catch (Exception)
            {
                await RecordAdapterFailureAsync(
                    context,
                    queue,
                    health,
                    commitTimeout,
                    SourceAdapterFailureDraft.CreateAdapterException(
                        Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture),
                        null,
                        metadata?.SourceSurface,
                        metadata?.SourceApplicationVersion,
                        metadata?.SourceAdapter,
                        metadata?.AdapterVersion,
                        metadata?.CaptureContentState,
                        timeProvider.GetUtcNow()),
                    StatusCodes.Status500InternalServerError,
                    "internal_error",
                    "The request could not be processed.");
                return;
            }

            if (!queue.TryEnqueue(batch, out var request))
            {
                await WriteQueueRejectedAsync(context, queue, health);
                return;
            }

            var result = await AwaitCommitAsync(context, request, health, commitTimeout);
            if (result is null)
            {
                return;
            }

            switch (result.Status)
            {
                case IngestionCommitStatus.Committed:
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = JsonContentType;
                    await context.Response.WriteAsync(
                        $$"""{"accepted":true,"rawRecordId":{{result.RawRecordId}},"observationId":{{result.ObservationId}}}""");
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

    private static IDoctorHttpApplication CreateDoctorApplication(
        string databasePath,
        TimeProvider timeProvider,
        Func<string, TimeProvider, IDoctorHttpApplication>? factory)
    {
        try
        {
            return factory?.Invoke(databasePath, timeProvider)
                ?? SqliteDoctorHttpApplication.Create(databasePath, timeProvider);
        }
        catch
        {
            return StatelessDoctorHttpApplication.Instance;
        }
    }

    private static int CountRecognizedSpanEnvelopes(SourceStructuralInventory inventory)
    {
        var count = inventory.StructuralOccurrences
            .Where(item => item.Envelope == SourceStructuralEnvelope.Span
                && item.Role == SourceStructuralRole.Envelope
                && item.Unknown is null)
            .Sum(item => (long)item.Count.Value);
        return checked((int)Math.Min(SourceOccurrenceCount.Maximum, count));
    }

    private static async Task RecordAdapterFailureAsync(
        HttpContext context,
        IngestionQueue queue,
        MonitorHealthState health,
        TimeSpan commitTimeout,
        SourceAdapterFailureDraft failure,
        int failureStatusCode,
        string failureCode,
        string failureMessage)
    {
        if (!queue.TryEnqueue(failure, out var request))
        {
            await WriteQueueRejectedAsync(context, queue, health);
            return;
        }

        var result = await AwaitCommitAsync(context, request, health, commitTimeout);
        if (result is null)
        {
            return;
        }

        switch (result.Status)
        {
            case IngestionCommitStatus.Committed:
                await WriteFailureAsync(
                    context,
                    failureStatusCode,
                    failureCode,
                    failureMessage);
                break;
            case IngestionCommitStatus.Busy:
                await WriteFailureAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "persistence_busy",
                    "Trace payload could not be persisted because the raw store is busy.");
                break;
            default:
                await WriteFailureAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "persistence_failed",
                    "Trace payload could not be persisted.");
                break;
        }
    }

    private static async Task WriteQueueRejectedAsync(
        HttpContext context,
        IngestionQueue queue,
        MonitorHealthState health)
    {
        health.RecordBackpressure();
        if (queue.IsClosed)
        {
            await WriteFailureAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "shutting_down",
                "The local monitor is shutting down.");
        }
        else
        {
            await WriteFailureAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "queue_full",
                "The local monitor ingestion queue is full.");
        }
    }

    private static async Task<IngestionCommitResult?> AwaitCommitAsync(
        HttpContext context,
        IngestionWriteRequest request,
        MonitorHealthState health,
        TimeSpan commitTimeout)
    {
        try
        {
            return await request.Completion.WaitAsync(commitTimeout, context.RequestAborted);
        }
        catch (TimeoutException)
        {
            health.RecordBackpressure();
            await WriteFailureAsync(
                context,
                StatusCodes.Status504GatewayTimeout,
                "commit_timeout",
                "Trace payload was not committed within the allowed time.");
            return null;
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "shutting_down",
                "The local monitor is shutting down.");
            return null;
        }
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

    internal static bool HasMonitorCsrfHeader(HttpContext context) =>
        string.Equals(context.Request.Headers["x-monitor-csrf"].ToString(), "local-monitor", StringComparison.Ordinal);

    /// <summary>The same <c>compactTrace</c>-shaped projection <c>/api/monitor/traces</c> emits per item, reused for the summary's embedded highlight traces.</summary>
    private static object? ToTraceDto(MonitorTraceRow? row, SourceProjectionState? state = null) => row is null ? null : new
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
        source_diagnostic = state?.SourceDiagnostic?.ToWire(),
        binding_state = state?.BindingState ?? "otel_only",
        completeness = state?.Completeness ?? "unbound",
        completeness_reason_codes = state?.CompletenessReasonCodes ?? ["missing_native_session_id"],
        content_state = state?.ContentState,
    };

    /// <summary>
    /// Sprint18 trace-list item: the compactTrace fields plus the v4 cache rollup
    /// and trace_status (D044). This is a **new** endpoint's shape — the existing
    /// <see cref="ToTraceDto"/> consumers (`/api/monitor/traces`, `/api/monitor/summary`)
    /// keep their pinned shape/ordering (D042 C6).
    /// </summary>
    private static object ToTraceListDto(MonitorTraceRow row, SourceProjectionState? state = null) => new
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
        cache_read_tokens = row.CacheReadTokens,
        cache_creation_tokens = row.CacheCreationTokens,
        trace_status = row.TraceStatus,
        source_diagnostic = state?.SourceDiagnostic?.ToWire(),
        binding_state = state?.BindingState ?? "otel_only",
        completeness = state?.Completeness ?? "unbound",
        completeness_reason_codes = state?.CompletenessReasonCodes ?? ["missing_native_session_id"],
        content_state = state?.ContentState,
    };

    private static SessionDetail? FindSessionForTrace(ISessionStore store, string traceId)
    {
        try
        {
            return store.ListMostRecent(200)
                .Select(item => store.GetDetail(item.SessionId))
                .FirstOrDefault(detail => detail is not null
                    && (detail.Runs.Any(run => run.TraceId == traceId) || detail.Events.Any(eventItem => eventItem.TraceId == traceId)));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryParseTraceListQuery(HttpContext context, MonitorOverviewService overviewService, out MonitorTraceListQuery query)
    {
        query = null!;

        var search = context.Request.Query["q"].ToString();
        var model = context.Request.Query["model"].ToString();

        var status = context.Request.Query["status"].ToString();
        if (!string.IsNullOrEmpty(status)
            && status is not ("ok" or "recovered" or "unrecovered" or "unknown" or "error"))
        {
            return false;
        }

        string? startInclusive = null;
        string? endExclusive = null;
        var period = context.Request.Query["period"].ToString();
        if (!string.IsNullOrEmpty(period) && !string.Equals(period, "all", StringComparison.Ordinal))
        {
            if (!MonitorOverviewService.IsSupportedPeriod(period))
            {
                return false;
            }

            var resolved = overviewService.ResolvePeriod(period);
            startInclusive = MonitorOverviewService.FormatUtc(resolved.Start);
            endExclusive = MonitorOverviewService.FormatUtc(resolved.End);
        }

        var sort = context.Request.Query["sort"].ToString();
        if (string.IsNullOrEmpty(sort))
        {
            sort = "tokens";
        }
        else if (sort is not ("tokens" or "time" or "duration"))
        {
            return false;
        }

        var offset = 0;
        var offsetValue = context.Request.Query["offset"].ToString();
        if (!string.IsNullOrEmpty(offsetValue)
            && (!int.TryParse(offsetValue, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
        {
            return false;
        }

        if (!TryParseLimitQuery(context, out var limit))
        {
            return false;
        }

        query = new MonitorTraceListQuery(
            TraceIdSearch: string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            Model: string.IsNullOrWhiteSpace(model) ? null : model,
            Status: string.IsNullOrEmpty(status) ? null : status,
            StartInclusive: startInclusive,
            EndExclusive: endExclusive,
            Sort: sort,
            Offset: offset,
            Limit: limit);
        return true;
    }

    private static object ToRunDto(MonitorAnalysisRun run, AnalysisRunRawSnapshot? rawSnapshot = null) => new
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
        result_markdown = rawSnapshot?.ResultMarkdown,
        error_message = rawSnapshot?.ErrorMessage,
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

internal interface IOtlpTraceSourceMetadataProvider
{
    OtlpTraceSourceMetadata GetMetadata();
}

internal sealed class OtlpTraceSourceMetadata
{
    private OtlpTraceSourceMetadata(
        string sourceSurface,
        string? sourceApplicationVersion,
        string sourceAdapter,
        string adapterVersion,
        SourceCaptureContentState captureContentState)
    {
        SourceSurface = sourceSurface;
        SourceApplicationVersion = sourceApplicationVersion;
        SourceAdapter = sourceAdapter;
        AdapterVersion = adapterVersion;
        CaptureContentState = captureContentState;
    }

    public string SourceSurface { get; }
    public string? SourceApplicationVersion { get; }
    public string SourceAdapter { get; }
    public string AdapterVersion { get; }
    public SourceCaptureContentState CaptureContentState { get; }

    public static OtlpTraceSourceMetadata Create(
        string sourceSurface,
        string? sourceApplicationVersion,
        string sourceAdapter,
        string adapterVersion,
        SourceCaptureContentState captureContentState)
    {
        ValidateRequired(sourceSurface, nameof(sourceSurface));
        ValidateOptional(sourceApplicationVersion, nameof(sourceApplicationVersion));
        ValidateRequired(sourceAdapter, nameof(sourceAdapter));
        ValidateRequired(adapterVersion, nameof(adapterVersion));
        if (!Enum.IsDefined(captureContentState))
        {
            throw new ArgumentOutOfRangeException(nameof(captureContentState));
        }

        return new OtlpTraceSourceMetadata(
            sourceSurface,
            sourceApplicationVersion,
            sourceAdapter,
            adapterVersion,
            captureContentState);
    }

    private static void ValidateRequired(string value, string parameterName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("Source metadata must be non-empty, bounded, and control-character free.", parameterName);
        }
    }

    private static void ValidateOptional(string? value, string parameterName)
    {
        if (value is not null && !IsValid(value))
        {
            throw new ArgumentException("Source metadata must be bounded and control-character free when present.", parameterName);
        }
    }

    private static bool IsValid(string value) =>
        value.Length is > 0 and <= 256 && value.All(character => !char.IsControl(character));
}

internal sealed class FixedOtlpTraceSourceMetadataProvider : IOtlpTraceSourceMetadataProvider
{
    public static FixedOtlpTraceSourceMetadataProvider Default { get; } = new(
        OtlpTraceSourceMetadata.Create(
            "raw-otlp",
            sourceApplicationVersion: null,
            "raw-otlp",
            "1",
            SourceCaptureContentState.Unsupported));

    private readonly OtlpTraceSourceMetadata metadata;

    public FixedOtlpTraceSourceMetadataProvider(OtlpTraceSourceMetadata metadata)
    {
        this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    public OtlpTraceSourceMetadata GetMetadata() => metadata;
}

internal sealed class MonitorHostTestOptions
{
    public Func<RetentionCatalogStore, TimeProvider, RetentionMutationApplicationService>? RetentionMutationApplicationFactory { get; init; }

    public IDoctorHttpApplication? DoctorApplication { get; init; }

    public IDoctorUiApplication? DoctorUiApplication { get; init; }

    public Func<string, TimeProvider, IDoctorHttpApplication>? DoctorApplicationFactory { get; init; }

    public IngestionQueue? Queue { get; init; }

    public IIngestionCommitStore? IngestionCommitStore { get; init; }

    public ISourceCompatibilityStore? SourceCompatibilityStore { get; init; }

    public IOtlpTraceSourceMetadataProvider? SourceMetadataProvider { get; init; }

    public VerifiedSourceFingerprintRegistry? SourceFingerprintRegistry { get; init; }

    public MonitorHealthState? Health { get; init; }

    public TimeSpan? CommitTimeout { get; init; }

    public bool StartWriter { get; init; } = true;

    public IMonitorProjectionStore? ProjectionStore { get; init; }

    public IMonitorAnalysisStore? AnalysisStore { get; init; }

    public IMonitorAnalysisRunner? AnalysisRunner { get; init; }

    public bool StartProjectionWorker { get; init; } = true;

    public ISessionStore? SessionStore { get; init; }

    public SessionEventQueue? SessionEventQueue { get; init; }

    public bool StartSessionWriter { get; init; } = true;

    public TimeSpan? SessionCommitTimeout { get; init; }

    public bool StartSessionOtelEnrichment { get; init; } = true;

    public bool StartRetentionCleanupWorker { get; init; }

    public TimeSpan? SessionOtelPollInterval { get; init; }

    public TimeProvider? TimeProvider { get; set; }

    public TimeSpan? ProjectionPollInterval { get; init; }

    public IReadOnlyDictionary<string, string?>? ConfigurationValues { get; init; }

    public bool UseUserSecrets { get; init; } = true;
}

internal sealed record AnalysisStartPayload(
    string? Focus,
    long? RawRecordId,
    string? SpanId,
    string? Question = null,
    IReadOnlyList<AnalysisHistoryTurnPayload>? History = null);

/// <summary>Wire shape of one prior drawer-chat turn (history resend, D045).</summary>
internal sealed record AnalysisHistoryTurnPayload(string? Question, string? Answer);
