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

        app.MapGet("/api/session-workspace/improvement-proposals", async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var sessionId = context.Request.Query["session_id"].ToString();
            if (!TryUuidV7(sessionId, out var id))
            {
                await Failure(context, 400, "invalid_session_id");
                return;
            }

            await Json(context, new { items = store.ListImprovementProposals(id).Select(ProposalDto) });
        });

        app.MapPost("/api/session-workspace/improvement-proposals", async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (MonitorHost.IsCrossSiteRequest(context)) { await Failure(context, 403, "cross_origin_forbidden"); return; }
            if (!MonitorHost.HasMonitorCsrfHeader(context)) { await Failure(context, 403, "csrf_required"); return; }
            if (!IsJson(context.Request)) { await Failure(context, 415, "unsupported_media_type"); return; }

            var body = await ReadBoundedBody(context.Request, MaximumBodyBytes, context.RequestAborted);
            if (body is null) { await Failure(context, 413, "request_too_large"); return; }
            if (!TryParseProposal(body, out var request)) { await Failure(context, 400, "invalid_proposal_request"); return; }
            if (request!.Status != "candidate") { await Failure(context, 400, "invalid_proposal_status"); return; }
            if (HasUnsafeProposalContent(request)) { await Failure(context, 400, "unsafe_proposal_content"); return; }
            if (!TryCreateProposal(request, timeProvider.GetUtcNow(), out var proposal)) { await Failure(context, 400, "invalid_proposal_request"); return; }

            var sourceDetails = proposal.SourceSessionIds.Select(id => store.GetDetail(id)).ToArray();
            if (sourceDetails.Any(detail => detail is null)) { await Failure(context, 400, "evidence_not_found"); return; }
            if (sourceDetails.Any(detail => !IsExactTerminal(detail!))) { await Failure(context, 400, "evidence_not_exact_bound"); return; }
            if (!EvidenceReferencesResolve(proposal, sourceDetails!)) { await Failure(context, 400, "evidence_not_found"); return; }

            try { store.CreateImprovementProposal(proposal); }
            catch (InvalidOperationException) { await Failure(context, 400, "invalid_proposal_request"); return; }
            context.Response.StatusCode = 201;
            await JsonBody(context, ProposalDto(proposal));
        });

        app.MapPut("/api/session-workspace/improvement-proposals/{proposalId}/status", async (string proposalId, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (MonitorHost.IsCrossSiteRequest(context)) { await Failure(context, 403, "cross_origin_forbidden"); return; }
            if (!MonitorHost.HasMonitorCsrfHeader(context)) { await Failure(context, 403, "csrf_required"); return; }
            if (!IsJson(context.Request)) { await Failure(context, 415, "unsupported_media_type"); return; }
            if (!TryUuidV7(proposalId, out var id)) { await Failure(context, 400, "invalid_proposal_id"); return; }
            var body = await ReadBoundedBody(context.Request, MaximumBodyBytes, context.RequestAborted);
            if (body is null) { await Failure(context, 413, "request_too_large"); return; }
            if (!TryParseProposalStatus(body, out var status)) { await Failure(context, 400, "invalid_proposal_request"); return; }
            if (status == "verified") { await Failure(context, 400, "verification_owned_by_compare"); return; }
            if (status is not "candidate" and not "recommended") { await Failure(context, 400, "invalid_proposal_status"); return; }

            var proposal = store.GetImprovementProposal(id);
            if (proposal is null) { await Failure(context, 404, "proposal_not_found"); return; }
            if (proposal.Status == ImprovementProposalStatus.Verified) { await Failure(context, 400, "verification_owned_by_compare"); return; }
            if (status == "recommended" && !HasRecommendationEvidence(store, proposal)) { await Failure(context, 400, "insufficient_recommendation_evidence"); return; }
            try { store.UpdateImprovementProposalStatus(id, status == "candidate" ? ImprovementProposalStatus.Candidate : ImprovementProposalStatus.Recommended, timeProvider.GetUtcNow()); }
            catch (InvalidOperationException) { await Failure(context, 400, status == "recommended" ? "recommendation_already_exists" : "invalid_proposal_status"); return; }

            var updated = store.GetImprovementProposal(id)!;
            await Json(context, ProposalDto(updated));
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
                human_evaluation = store.GetHumanEvaluation(id) is { } evaluation ? new
                {
                    verdict = evaluation.Verdict,
                    recorded_at = evaluation.RecordedAt,
                } : null,
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

        app.MapPut("/api/session-workspace/sessions/{sessionId}/human-evaluation", async (string sessionId, HttpContext context) =>
        {
            if (MonitorHost.IsCrossSiteRequest(context))
            {
                await Failure(context, 403, "cross_origin_forbidden");
                return;
            }
            if (!MonitorHost.HasMonitorCsrfHeader(context))
            {
                await Failure(context, 403, "csrf_required");
                return;
            }
            if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase))
            {
                await Failure(context, 415, "unsupported_media_type");
                return;
            }
            if (!Guid.TryParseExact(sessionId, "D", out var id))
            {
                await Failure(context, 400, "invalid_session_id");
                return;
            }

            var body = await ReadBoundedBody(context.Request, MaximumBodyBytes, context.RequestAborted);
            if (body is null || !TryParseHumanEvaluation(body, out var verdict))
            {
                await Failure(context, 400, "invalid_human_evaluation_request");
                return;
            }
            if (store.GetDetail(id) is null)
            {
                await Failure(context, 404, "session_not_found");
                return;
            }

            if (verdict is null)
            {
                store.ClearHumanEvaluation(id);
            }
            else
            {
                store.UpsertHumanEvaluation(new(id, verdict, timeProvider.GetUtcNow()));
            }
            context.Response.StatusCode = 204;
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

    private static bool IsJson(HttpRequest request) =>
        string.Equals(request.ContentType?.Split(';', 2)[0], "application/json", StringComparison.OrdinalIgnoreCase);

    private static bool TryUuidV7(string value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && id.ToString("D")[14] == '7';

    private static bool TryParseProposal(byte[] body, out ImprovementProposalRequest? request)
    {
        try { request = JsonSerializer.Deserialize<ImprovementProposalRequest>(body, StrictJson); return request is not null; }
        catch (JsonException) { request = null; return false; }
    }

    private static bool TryParseProposalStatus(byte[] body, out string? status)
    {
        try { status = JsonSerializer.Deserialize<ImprovementProposalStatusRequest>(body, StrictJson)?.Status; return status is not null; }
        catch (JsonException) { status = null; return false; }
    }

    private static bool HasUnsafeProposalContent(ImprovementProposalRequest request) =>
        new[] { request.TargetLabel, request.Title, request.Summary, request.ExpectedEffect, request.RiskNote }
            .Any(value => value is not null && IsUnsafeProposalText(value));

    private static bool IsUnsafeProposalText(string value) =>
        MeasurementSanitizer.IsUnsafeStringValue(value)
        || Uri.TryCreate(value, UriKind.Absolute, out _)
        || value.Contains("../", StringComparison.Ordinal)
        || value.Contains(@"..\", StringComparison.Ordinal)
        || value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\')
        || System.Text.RegularExpressions.Regex.IsMatch(value, @"\b[A-Za-z]:(?:\\|[A-Za-z0-9_.-])")
        || System.Text.RegularExpressions.Regex.IsMatch(value, @"\b(public|private|protected|internal)\s+(static\s+)?(void|class|record|interface|[A-Za-z_][A-Za-z0-9_<>]*)\s+[A-Za-z_][A-Za-z0-9_]*\s*\(")
        || System.Text.RegularExpressions.Regex.IsMatch(value, @"\bconst\s+(?:[A-Za-z_][A-Za-z0-9_<>]*\s+)+[A-Za-z_][A-Za-z0-9_]*\s*=|\busing\s+[A-Za-z_][A-Za-z0-9_.]*\s*;|\bnamespace\s+[A-Za-z_][A-Za-z0-9_.]*\s*;")
        || value.Contains("```", StringComparison.Ordinal);

    private static bool TryCreateProposal(ImprovementProposalRequest request, DateTimeOffset now, out ImprovementProposal proposal)
    {
        proposal = default!;
        if (request.TargetKind is not ("skill" or "agent" or "instructions" or "template" or "hook_config")
            || !IsBounded(request.TargetLabel, 200) || !IsBounded(request.Title, 200)
            || !IsBounded(request.Summary, 2000) || !IsBounded(request.ExpectedEffect, 1000) || !IsBounded(request.RiskNote, 1000)
            || request.SourceSessions is not { Count: > 0 }
            || request.EvidenceReferences is not { Count: >= 1 and <= 10 }) return false;

        var sourceIds = new List<Guid>();
        foreach (var sourceSession in request.SourceSessions)
        {
            if (!TryUuidV7(sourceSession, out var sourceId) || sourceIds.Contains(sourceId)) return false;
            sourceIds.Add(sourceId);
        }
        var references = new List<ImprovementProposalEvidenceReference>();
        foreach (var reference in request.EvidenceReferences)
        {
            if (reference.Kind is not ("event" or "run" or "trace" or "gate") || !IsBounded(reference.ReferenceId, 512)) return false;
            references.Add(new(reference.Kind, reference.ReferenceId!));
        }
        proposal = new ImprovementProposal(Guid.CreateVersion7(), ImprovementProposalStatus.Candidate, request.TargetKind, request.TargetLabel!, request.Title!, request.Summary!, request.ExpectedEffect!, request.RiskNote!, sourceIds, references, now, now, null, null);
        return true;
    }

    private static bool IsExactTerminal(SessionDetail detail) =>
        detail.Session.Status is ObservedSessionStatus.Completed or ObservedSessionStatus.Failed
        && detail.NativeIds.Any(id => id.BindingKind == SessionBindingKind.Native);

    private static bool EvidenceReferencesResolve(ImprovementProposal proposal, SessionDetail?[] details) =>
        proposal.EvidenceReferences.All(reference => EvidenceReferenceResolves(reference, details));

    private static bool EvidenceReferenceResolves(ImprovementProposalEvidenceReference reference, IEnumerable<SessionDetail?> details) =>
        details.Any(detail => detail is not null && reference.Kind switch
        {
            "event" => Guid.TryParse(reference.ReferenceId, out var eventId) && detail.Events.Any(item => item.EventId == eventId),
            "run" => Guid.TryParse(reference.ReferenceId, out var runId) && detail.Runs.Any(item => item.RunId == runId),
            "trace" => detail.Runs.Any(item => string.Equals(item.TraceId, reference.ReferenceId, StringComparison.Ordinal)),
            "gate" => reference.ReferenceId == "terminal" && detail.Events.Any(item => item.Type is "session.shutdown" or "session.task_complete" or "SessionEnd" or "Stop")
                || reference.ReferenceId == "error" && detail.Events.Any(item => item.Status == "error"),
            _ => false,
        });

    private static bool HasRecommendationEvidence(ISessionStore store, ImprovementProposal proposal)
    {
        if (proposal.SourceSessionIds.Count < 2) return false;
        var details = proposal.SourceSessionIds.Select(store.GetDetail).ToArray();
        if (details.Any(detail => detail is null || !IsExactTerminal(detail))) return false;

        var evidencedSessions = new HashSet<Guid>();
        foreach (var reference in proposal.EvidenceReferences)
        {
            foreach (var detail in details.Where(detail => detail is not null).Cast<SessionDetail>())
            {
                if (EvidenceReferenceResolves(reference, [detail])) evidencedSessions.Add(detail.Session.SessionId);
            }
        }
        return evidencedSessions.Count >= 2;
    }

    private static object ProposalDto(ImprovementProposal proposal) => new
    {
        proposal_id = proposal.ProposalId,
        status = proposal.Status switch { ImprovementProposalStatus.Candidate => "candidate", ImprovementProposalStatus.Recommended => "recommended", ImprovementProposalStatus.Verified => "verified", _ => throw new ArgumentOutOfRangeException() },
        target_kind = proposal.TargetKind,
        target_label = proposal.TargetLabel,
        title = proposal.Title,
        summary = proposal.Summary,
        expected_effect = proposal.ExpectedEffect,
        risk_note = proposal.RiskNote,
        source_sessions = proposal.SourceSessionIds,
        evidence_refs = proposal.EvidenceReferences.Select(reference => new { kind = reference.Kind, reference_id = reference.ReferenceId }),
        created_at = proposal.CreatedAt,
        updated_at = proposal.UpdatedAt,
        recommended_at = proposal.RecommendedAt,
        verified_at = proposal.VerifiedAt,
    };

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

    private static bool TryParseHumanEvaluation(byte[] body, out string? verdict)
    {
        verdict = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || !string.Equals(properties[0].Name, "verdict", StringComparison.Ordinal))
            {
                return false;
            }

            if (properties[0].Value.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (properties[0].Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            verdict = properties[0].Value.GetString();
            return verdict is "expected" or "problem";
        }
        catch (JsonException)
        {
            return false;
        }
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
