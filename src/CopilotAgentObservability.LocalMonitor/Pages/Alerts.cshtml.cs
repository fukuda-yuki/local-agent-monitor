using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Pages;

public sealed class AlertsModel : PageModel
{
    public bool SanitizedOnly { get; private set; }

    public IActionResult OnGet()
    {
        Response.Headers.CacheControl = "no-store";
        if (MonitorHost.IsCrossSiteRequest(HttpContext))
        {
            return new ContentResult
            {
                StatusCode = StatusCodes.Status403Forbidden,
                ContentType = "application/json",
                Content = "{\"schema_version\":\"alert.center.v1\",\"error\":\"cross_origin_forbidden\"}",
            };
        }
        SanitizedOnly = HttpContext.RequestServices.GetRequiredService<MonitorOptions>().SanitizedOnly;
        return Page();
    }
}
