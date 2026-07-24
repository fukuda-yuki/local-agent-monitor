using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CopilotAgentObservability.LocalMonitor.Pages;

public sealed class CostsModel : PageModel
{
    private const int MaximumQueryBytes = 8_192;
    private const string EstimatePrefix = "pricing-estimate-";

    public string? SessionId { get; private set; }
    public string? EstimateId { get; private set; }

    public IActionResult OnGet()
    {
        Response.Headers.CacheControl = "no-store";
        if (MonitorHost.IsCrossSiteRequest(HttpContext))
            return Error(StatusCodes.Status403Forbidden, "cross_origin_forbidden");
        if (!TryReadContext(out var sessionId, out var estimateId))
            return Error(StatusCodes.Status400BadRequest, "cost_invalid_query");
        SessionId = sessionId;
        EstimateId = estimateId;
        return Page();
    }

    private bool TryReadContext(out string? sessionId, out string? estimateId)
    {
        sessionId = null;
        estimateId = null;
        var raw = Request.QueryString.Value;
        if (string.IsNullOrEmpty(raw)) return true;
        if (Encoding.UTF8.GetByteCount(raw) - 1 > MaximumQueryBytes) return false;
        var parts = raw[1..].Split('&');
        if (parts.Length is < 1 or > 2) return false;
        if (!TryPair(parts[0], "session_id", out sessionId)
            || !ValidUuid7(sessionId))
            return false;
        if (parts.Length == 1) return true;
        return TryPair(parts[1], "estimate_id", out estimateId)
            && ValidEstimateId(estimateId);
    }

    private static bool TryPair(string pair, string expectedKey, out string? value)
    {
        value = null;
        var separator = pair.IndexOf('=');
        if (separator <= 0
            || separator == pair.Length - 1
            || pair.IndexOf('=', separator + 1) >= 0)
            return false;
        try
        {
            var key = Uri.UnescapeDataString(pair[..separator]);
            value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            return key == expectedKey
                && Uri.EscapeDataString(key) == pair[..separator]
                && Uri.EscapeDataString(value) == pair[(separator + 1)..];
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool ValidUuid7(string? value) =>
        value is { Length: 36 }
        && value == value.ToLowerInvariant()
        && Guid.TryParseExact(value, "D", out _)
        && value[14] == '7'
        && value[19] is '8' or '9' or 'a' or 'b';

    private static bool ValidEstimateId(string? value) =>
        value is not null
        && value.Length == EstimatePrefix.Length + 64
        && value.StartsWith(EstimatePrefix, StringComparison.Ordinal)
        && value.AsSpan(EstimatePrefix.Length).ToArray().All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ContentResult Error(int status, string code) => new()
    {
        StatusCode = status,
        ContentType = "application/json",
        Content = $$"""{"schema_version":"cost.error.v1","error":"{{code}}"}""",
    };
}
