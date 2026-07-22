using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CopilotAgentObservability.LocalMonitor.Pages;

public sealed class HistoricalImportModel : PageModel
{
    public void OnGet()
    {
        Response.Headers.CacheControl = "no-store";
    }
}
