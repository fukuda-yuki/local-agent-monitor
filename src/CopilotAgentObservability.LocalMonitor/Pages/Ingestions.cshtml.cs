using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

public sealed class IngestionsModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long After { get; set; }

    internal MonitorProjectionPage<MonitorIngestionRow> Result { get; private set; } = null!;

    internal bool RawAvailable { get; private set; }

    public IActionResult OnGet()
    {
        if (After < 0)
        {
            return BadRequest();
        }

        var store = HttpContext.RequestServices.GetRequiredService<IMonitorProjectionStore>();
        var options = HttpContext.RequestServices.GetRequiredService<MonitorOptions>();
        RawAvailable = !options.SanitizedOnly;
        Result = store.ListMonitorIngestions(After, 50);
        return Page();
    }
}
