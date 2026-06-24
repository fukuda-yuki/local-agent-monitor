using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

public sealed class IndexModel : PageModel
{
    internal MonitorReadiness Readiness { get; private set; } = null!;

    internal IReadOnlyList<MonitorIngestionRow> RecentIngestions { get; private set; } = [];

    public void OnGet()
    {
        var health = HttpContext.RequestServices.GetRequiredService<MonitorHealthState>();
        var store = HttpContext.RequestServices.GetRequiredService<IMonitorProjectionStore>();
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();
        Readiness = health.Evaluate(options.IngestionStallThresholdSeconds, options.ProjectionLagThresholdSeconds);
        RecentIngestions = store.ListMonitorIngestions(0, 10).Items;
    }
}
