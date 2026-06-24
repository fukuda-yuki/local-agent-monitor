using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

public sealed class TracesModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long After { get; set; }

    internal MonitorProjectionPage<MonitorTraceRow> Result { get; private set; } = null!;

    public IActionResult OnGet()
    {
        if (After < 0)
        {
            return BadRequest();
        }

        var store = HttpContext.RequestServices.GetRequiredService<IMonitorProjectionStore>();
        Result = store.ListMonitorTraces(After, 50);
        return Page();
    }
}
