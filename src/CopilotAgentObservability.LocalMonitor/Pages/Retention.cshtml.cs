using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CopilotAgentObservability.LocalMonitor.Pages;

public sealed class RetentionModel : PageModel
{
    public string TargetKind { get; private set; } = string.Empty;

    public string TargetId { get; private set; } = string.Empty;

    public IActionResult OnGet(string targetKind, string targetId)
    {
        Response.Headers.CacheControl = "no-store";
        if (MonitorHost.IsCrossSiteRequest(HttpContext))
        {
            return new ContentResult
            {
                StatusCode = StatusCodes.Status403Forbidden,
                ContentType = "application/json",
                Content = "{\"error\":\"cross_origin_forbidden\"}",
            };
        }

        if (targetKind is not ("session" or "item") || string.IsNullOrWhiteSpace(targetId))
        {
            return NotFound();
        }

        if (targetKind == "session"
            && (!Guid.TryParseExact(targetId, "D", out var sessionId)
                || !string.Equals(targetId, sessionId.ToString("D"), StringComparison.Ordinal)))
        {
            return NotFound();
        }

        TargetKind = targetKind;
        TargetId = targetId;
        return Page();
    }
}
