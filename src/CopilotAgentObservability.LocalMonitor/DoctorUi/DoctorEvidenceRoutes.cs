using System.Net;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.LocalMonitor.SourceCompatibility;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor;

internal static class DoctorEvidenceRoutes
{
    public static void Map(
        WebApplication app,
        ISourceCompatibilityStore compatibilityStore,
        ISessionStore sessionStore)
    {
        app.MapGet(
            "/api/doctor/ui/v1/source-diagnostics/{observationId}",
            (string observationId, HttpContext context) => SourceDiagnostic(context, compatibilityStore, observationId));
        app.MapGet(
            "/api/doctor/ui/v1/sessions/{sessionId}",
            (string sessionId, HttpContext context) => Session(context, sessionStore, sessionId));
    }

    private static Task SourceDiagnostic(
        HttpContext context,
        ISourceCompatibilityStore store,
        string observationId)
    {
        Prepare(context);
        if (!Authorize(context) || context.Request.Query.Count != 0
            || !DoctorValidation.IsValidEvidenceReference(observationId))
        {
            return Error(context, StatusCodes.Status400BadRequest, "invalid_evidence_identity");
        }
        SourceCompatibilityRow? row;
        try
        {
            row = store.GetByObservationId(observationId);
        }
        catch
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable, "evidence_unavailable");
        }
        if (row is null)
        {
            return Error(context, StatusCodes.Status404NotFound, "evidence_not_found");
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        return context.Response.WriteAsJsonAsync(new
        {
            schema_version = "doctor.ui.v1",
            observation = new
            {
                observation_id = row.ObservationId,
                source_diagnostic = SourceDiagnosticDto.FromRows([row])!.ToWire(),
                observed_at = row.ObservedAt,
            },
        });
    }

    private static Task Session(HttpContext context, ISessionStore store, string sessionId)
    {
        Prepare(context);
        if (!Authorize(context) || context.Request.Query.Count != 0
            || !Guid.TryParseExact(sessionId, "D", out var id)
            || id.Version != 7
            || !string.Equals(sessionId, id.ToString("D"), StringComparison.Ordinal))
        {
            return Error(context, StatusCodes.Status400BadRequest, "invalid_evidence_identity");
        }
        SessionDetail? detail;
        try
        {
            detail = store.GetDetail(id);
        }
        catch
        {
            return Error(context, StatusCodes.Status503ServiceUnavailable, "evidence_unavailable");
        }
        if (detail is null)
        {
            return Error(context, StatusCodes.Status404NotFound, "evidence_not_found");
        }
        var session = detail.Session;
        context.Response.StatusCode = StatusCodes.Status200OK;
        return context.Response.WriteAsJsonAsync(new
        {
            schema_version = "doctor.ui.v1",
            session = new
            {
                session_id = session.SessionId,
                status = SessionWire.ToWire(session.Status),
                completeness = SessionWire.ToWire(session.Completeness),
                started_at = session.StartedAt,
                ended_at = session.EndedAt,
                last_seen_at = session.LastSeenAt,
            },
        });
    }

    private static bool Authorize(HttpContext context) =>
        MonitorOptions.IsAllowedLoopbackHost(context.Request.Host.Host)
        && (context.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(context.Connection.RemoteIpAddress));

    private static void Prepare(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json";
    }

    private static Task Error(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { schema_version = "doctor.ui.v1", error = code });
    }
}
