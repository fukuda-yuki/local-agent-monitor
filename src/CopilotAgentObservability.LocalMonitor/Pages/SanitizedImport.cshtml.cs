using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CopilotAgentObservability.LocalMonitor.Pages;

public sealed class SanitizedImportModel : PageModel
{
    public IActionResult OnGet()
    {
        Response.Headers.CacheControl = "no-store";
        if (!MonitorHost.IsCrossSiteRequest(HttpContext)) return Page();
        return new ContentResult
        {
            StatusCode = StatusCodes.Status403Forbidden,
            ContentType = "application/json",
            Content = "{\"error\":\"cross_origin_forbidden\"}",
        };
    }
}
