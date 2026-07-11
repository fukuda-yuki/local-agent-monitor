using System.Text.Json.Serialization;
using CopilotAgentObservability.Telemetry.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal static class SessionRoutes
{
    public const string IngestPath = "/api/session-ingest/v1/events";
    public const int MaximumBodyBytes = 1_048_576;
    private const string NormalizerProjectorKey = "session-normalizer";
    private const string OtelProjectorKey = "session-otel-enrichment";
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static void Map(
        WebApplication app,
        MonitorOptions monitorOptions,
        SessionEventQueue queue,
        ISessionStore store,
        SqliteSessionOtelEnricher otelEnricher,
        TimeSpan commitTimeout,
        TimeProvider timeProvider)
    {
        app.MapPost(IngestPath, async context =>
        {
            if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
            {
                await Failure(context, 415, "unsupported_media_type");
                return;
            }

            if (!string.Equals(context.Request.Headers["X-CAO-Session-Event-Version"].ToString(), "1", StringComparison.Ordinal))
            {
                IncrementUnsupportedVersion(store, timeProvider);
                await Failure(context, 400, "unsupported_session_event_version");
                return;
            }

            var body = await ReadBoundedBody(context.Request, MaximumBodyBytes, context.RequestAborted);
            if (body is null)
            {
                await Failure(context, 413, "request_too_large");
                return;
            }

            SessionIngestEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<SessionIngestEnvelope>(body, StrictJson);
            }
            catch (JsonException)
            {
                await Failure(context, 400, "invalid_session_event_request");
                return;
            }

            if (envelope?.SchemaVersion != 1)
            {
                IncrementUnsupportedVersion(store, timeProvider);
                await Failure(context, 400, "unsupported_session_event_version");
                return;
            }
            if (!SessionIngestValidation.IsValid(envelope))
            {
                await Failure(context, 400, "invalid_session_event_request");
                return;
            }
            if (!HasValidExplicitBinding(store, envelope!))
            {
                await Failure(context, 400, "invalid_session_event_request");
                return;
            }
            if (!queue.TryEnqueue(envelope!, out var writeRequest))
            {
                await Failure(context, 503, "session_event_queue_full");
                return;
            }

            SessionEventCommitStatus status;
            try
            {
                status = await writeRequest.Completion.WaitAsync(commitTimeout, context.RequestAborted);
            }
            catch (TimeoutException)
            {
                await Failure(context, 504, "session_event_commit_timeout");
                return;
            }
            catch (OperationCanceledException)
            {
                await Failure(context, 503, "session_store_busy");
                return;
            }

            if (status == SessionEventCommitStatus.Committed)
            {
                context.Response.StatusCode = 204;
                return;
            }
            await Failure(context, 503, "session_store_busy");
        });

        app.MapGet("/api/session-workspace/sessions", async context =>
        {
            if (!TryLimit(context.Request.Query["limit"].ToString(), out var limit))
            {
                await Failure(context, 400, "invalid_session_workspace_query");
                return;
            }
            var items = store.ListMostRecent(limit).Select(item => SessionDto(item, store.GetDetail(item.SessionId)?.NativeIds ?? [], store.GetRawRetentionState(item.SessionId)));
            await Json(context, new { items });
        });

        app.MapGet("/api/session-workspace/sessions/{sessionId}", async (string sessionId, HttpContext context) =>
        {
            if (!Guid.TryParseExact(sessionId, "D", out var id))
            {
                await Failure(context, 400, "invalid_session_id");
                return;
            }
            if (store.GetDetail(id) is not { } detail)
            {
                await Failure(context, 404, "session_not_found");
                return;
            }
            await Json(context, new
            {
                session = SessionDto(detail.Session, detail.NativeIds, store.GetRawRetentionState(detail.Session.SessionId)),
                native_ids = detail.NativeIds.Select(item => new
                {
                    source_surface = SessionWire.ToWire(item.SourceSurface),
                    native_session_id = item.NativeSessionId,
                    binding_kind = SessionWire.ToWire(item.BindingKind),
                    observed_at = item.ObservedAt,
                }),
                runs = detail.Runs.Select(item => new
                {
                    run_id = item.RunId,
                    source_surface = item.SourceSurface is null ? null : SessionWire.ToWire(item.SourceSurface.Value),
                    native_run_id = item.NativeRunId,
                    trace_id = item.TraceId,
                    parent_run_id = item.ParentRunId,
                    model = item.Model,
                    status = SessionWire.ToWire(item.Status),
                    started_at = item.StartedAt,
                    ended_at = item.EndedAt,
                    input_tokens = item.InputTokens,
                    output_tokens = item.OutputTokens,
                    total_tokens = item.TotalTokens,
                }),
                events = detail.Events.Select(item => new
                {
                    event_id = item.EventId,
                    run_id = item.RunId,
                    source_surface = item.SourceSurface is null ? null : SessionWire.ToWire(item.SourceSurface.Value),
                    parent_event_id = item.ParentEventId,
                    status = item.Status,
                    type = item.Type,
                    occurred_at = item.OccurredAt,
                    content_state = SessionWire.ToWire(item.ContentState),
                }),
            });
        });

        app.MapGet("/api/session-workspace/resolve", async context =>
        {
            var nativeId = context.Request.Query["native_session_id"].ToString();
            if (!TrySurface(context.Request.Query["source_surface"].ToString(), out var surface) || !IsBounded(nativeId, 256))
            {
                await Failure(context, 400, "invalid_session_resolution_request");
                return;
            }
            var session = store.Resolve(surface, nativeId);
            if (session is null)
            {
                context.Response.StatusCode = 404;
                await JsonBody(context, new { binding_status = "unbound" });
                return;
            }
            await Json(context, new
            {
                binding_status = "bound",
                session_id = session.SessionId,
                completeness = SessionWire.ToWire(session.Completeness),
            });
        });

        app.MapGet("/api/session-workspace/status", async context =>
        {
            var normalizer = store.GetProjectionState(NormalizerProjectorKey);
            var otel = store.GetProjectionState(OtelProjectorKey);
            await Json(context, new
            {
                schema_version = 1,
                normalizer_status = queue.IsClosed ? "degraded" : "ready",
                unsupported_event_version_count = normalizer?.UnsupportedEventVersionCount ?? 0,
                projection_cursor = otel?.ProjectionCursor,
                projection_backlog = queue.Count + SafeBacklog(otelEnricher),
            });
        });

        if (!monitorOptions.SanitizedOnly)
        {
            app.MapGet("/sessions/{sessionId}/events/{eventId}/content", async (string sessionId, string eventId, HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                if (MonitorHost.IsCrossSiteRequest(context))
                {
                    await Failure(context, 403, "cross_origin_forbidden");
                    return;
                }
                if (!Guid.TryParseExact(sessionId, "D", out var sessionGuid)
                    || !Guid.TryParseExact(eventId, "D", out var eventGuid)
                    || store.GetContent(sessionGuid, eventGuid) is not { } lookup)
                {
                    await Failure(context, 404, "session_event_content_not_found");
                    return;
                }
                if (lookup.State == SessionContentState.ExpiredPendingDeletion)
                {
                    context.Response.StatusCode = 410;
                    await JsonBody(context, new { error = "raw_content_expired", content_state = "expired_pending_deletion" });
                    return;
                }
                var content = lookup.Content!;
                await Json(context, new
                {
                    event_id = content.EventId,
                    content_kind = content.ContentKind,
                    content = content.ContentJson,
                    captured_at = content.CapturedAt,
                    expires_at = content.ExpiresAt,
                });
            });
        }
    }

    private static object SessionDto(ObservedSession item, IReadOnlyList<SessionNativeId> nativeIds, SessionRawRetentionState rawRetentionState) => new
    {
        session_id = item.SessionId,
        status = SessionWire.ToWire(item.Status),
        completeness = SessionWire.ToWire(item.Completeness),
        source_surfaces = nativeIds.Select(id => SessionWire.ToWire(id.SourceSurface)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
        repository = item.Repository,
        workspace = item.Workspace,
        started_at = item.StartedAt,
        ended_at = item.EndedAt,
        last_seen_at = item.LastSeenAt,
        raw_retention_state = SessionWire.ToWire(rawRetentionState),
    };

    private static bool HasValidExplicitBinding(ISessionStore store, SessionIngestEnvelope envelope)
    {
        if (envelope.ExplicitLink is not { } link)
        {
            return true;
        }

        var linkSurface = SessionWire.ParseSourceSurface(link.SourceSurface!);
        var target = store.Resolve(linkSurface, link.NativeSessionId!);
        if (target is null)
        {
            return false;
        }

        var sourceSurface = SessionWire.ParseSourceSurface(envelope.SourceSurface!);
        var current = store.Resolve(sourceSurface, envelope.NativeSessionId!);
        return current is null || current.SessionId == target.SessionId;
    }

    private static bool IsBounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    private static bool TrySurface(string value, out SessionSourceSurface surface)
    {
        try { surface = SessionWire.ParseSourceSurface(value); return true; }
        catch (ArgumentException) { surface = default; return false; }
    }

    private static bool TryLimit(string value, out int limit)
    {
        limit = 50;
        return string.IsNullOrEmpty(value)
            || (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit is >= 1 and <= 200);
    }

    private static async Task<byte[]?> ReadBoundedBody(HttpRequest request, int maximumBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumBytes) return null;
        using var buffer = new MemoryStream();
        var bytes = new byte[81920];
        while (true)
        {
            var read = await request.Body.ReadAsync(bytes, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maximumBytes) return null;
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }
    }

    private static void IncrementUnsupportedVersion(ISessionStore store, TimeProvider timeProvider)
    {
        try
        {
            var current = store.GetProjectionState(NormalizerProjectorKey);
            store.UpsertProjectionState(new(NormalizerProjectorKey, current?.ProjectionCursor, (current?.UnsupportedEventVersionCount ?? 0) + 1, timeProvider.GetUtcNow()));
        }
        catch { }
    }

    private static long SafeBacklog(SqliteSessionOtelEnricher enricher)
    {
        try { return enricher.CountBacklog(); }
        catch { return 0; }
    }

    private static Task Failure(HttpContext context, int status, string error)
    {
        context.Response.StatusCode = status;
        return JsonBody(context, new { error });
    }

    private static Task Json(HttpContext context, object value)
    {
        context.Response.StatusCode = 200;
        return JsonBody(context, value);
    }

    private static async Task JsonBody(HttpContext context, object value)
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(value));
    }
}
