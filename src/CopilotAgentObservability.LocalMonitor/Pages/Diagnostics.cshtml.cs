using CopilotAgentObservability.LocalMonitor.Health;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

public sealed class DiagnosticsModel : PageModel
{
    internal MonitorReadiness Readiness { get; private set; } = null!;

    public void OnGet()
    {
        var health = HttpContext.RequestServices.GetRequiredService<MonitorHealthState>();
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();
        Readiness = health.Evaluate(options.IngestionStallThresholdSeconds, options.ProjectionLagThresholdSeconds);
    }
}
