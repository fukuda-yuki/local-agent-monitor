using CopilotAgentObservability.LocalMonitor.Health;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

/// <summary>
/// Diagnostics (Sprint18 §6.7): readiness heading, the 4-stage pipeline
/// summary, the component table, the configured readiness thresholds, and the
/// collapsible ingestion-history section (C5) that client-fetches the sanitized
/// `GET /api/monitor/ingestions`. Reached from the sidebar status popover; the
/// direct URL keeps working (D042 C1).
/// </summary>
public sealed class DiagnosticsModel : PageModel
{
    internal MonitorReadiness Readiness { get; private set; } = null!;

    internal int IngestionStallThresholdSeconds { get; private set; }

    internal int ProjectionLagThresholdSeconds { get; private set; }

    public void OnGet()
    {
        var health = HttpContext.RequestServices.GetRequiredService<MonitorHealthState>();
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();
        IngestionStallThresholdSeconds = options.IngestionStallThresholdSeconds;
        ProjectionLagThresholdSeconds = options.ProjectionLagThresholdSeconds;
        Readiness = health.Evaluate(options.IngestionStallThresholdSeconds, options.ProjectionLagThresholdSeconds);
    }
}
