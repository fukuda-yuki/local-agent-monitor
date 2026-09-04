using CopilotAgentObservability.LocalMonitor.Diagnostics;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Projection;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

/// <summary>
/// Diagnostics (Sprint18 §6.7): readiness heading, the 4-stage pipeline
/// summary, the component table, the configured readiness thresholds, and the
/// collapsible ingestion-history section (C5) that client-fetches the sanitized
/// `GET /api/monitor/ingestions`. The direct URL remains available (D042 C1).
/// </summary>
public sealed class DiagnosticsModel : PageModel
{
    internal IReadOnlyList<SemanticAttributeCaptureRow> SemanticCaptures { get; private set; } = [];

    public IActionResult OnPostStartSemanticCapture(string sourceFamily)
    {
        Response.Headers.CacheControl = "no-store";
        if (sourceFamily is null || !SemanticAttributeKeyBaseline.Supports(sourceFamily)) return BadRequest();
        HttpContext.RequestServices.GetRequiredService<SqliteSourceCompatibilityStore>()
            .StartSemanticCapture(sourceFamily, HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
        return RedirectToPage();
    }

    public IActionResult OnPostCompleteSemanticCapture(string sourceFamily, string captureId)
    {
        Response.Headers.CacheControl = "no-store";
        if (sourceFamily is null || captureId is null) return BadRequest();
        var completed = HttpContext.RequestServices.GetRequiredService<SqliteSourceCompatibilityStore>()
            .CompleteSemanticCapture(sourceFamily, captureId, HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
        return completed ? RedirectToPage() : BadRequest();
    }
    internal MonitorReadiness Readiness { get; private set; } = null!;

    internal int IngestionStallThresholdSeconds { get; private set; }

    internal int ProjectionLagThresholdSeconds { get; private set; }

    internal RepositoryMetadataDiagnosticsSnapshot RepositoryMetadata { get; private set; } =
        RepositoryMetadataDiagnosticsSnapshot.Empty();

    public async Task OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        SemanticCaptures = HttpContext.RequestServices.GetRequiredService<SqliteSourceCompatibilityStore>()
            .ListSemanticCaptures(HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
        var health = HttpContext.RequestServices.GetRequiredService<MonitorHealthState>();
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();
        IngestionStallThresholdSeconds = options.IngestionStallThresholdSeconds;
        ProjectionLagThresholdSeconds = options.ProjectionLagThresholdSeconds;
        Readiness = health.Evaluate(options.IngestionStallThresholdSeconds, options.ProjectionLagThresholdSeconds);
        var store = HttpContext.RequestServices.GetRequiredService<IMonitorProjectionStore>();
        var leases = new RawRazorPageLeaseTracker();
        try
        {
            RepositoryMetadata = await new RepositoryMetadataDiagnosticsLoader(store)
                .LoadAsync(leases, HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch
        {
            await leases.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (leases.HasLeases) leases.Attach(HttpContext);
    }
}
