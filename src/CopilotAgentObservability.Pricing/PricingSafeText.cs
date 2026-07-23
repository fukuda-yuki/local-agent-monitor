using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Pricing;

internal static class PricingSafeText
{
    private static readonly Regex CredentialMarkerPattern = new(
        @"(?:^|[^A-Za-z0-9])(?:sk-|gh[pousr]_|github_pat_|glpat-|AKIA|AIza|xox[baprs]-)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AuthorizationMarkerPattern = new(
        @"(?:^|[^A-Za-z0-9])(?:(?:Bearer|Basic)\s+|Authorization\s*[:=])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HighConfidenceCredentialPattern = new(
        @"(?:sk-[A-Za-z0-9_-]{32,}|gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{20,}|AKIA[A-Z0-9]{16}|AIza[A-Za-z0-9_-]{30,}|xox[baprs]-[A-Za-z0-9-]{20,})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PrivateKeyMarkerPattern = new(
        @"-----BEGIN [^-]*(?:PRIVATE KEY|CERTIFICATE)-----",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SecretAssignmentPattern = new(
        @"(?:^|[^A-Za-z0-9])(?:api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|token|secret|password|credential)\s*[:=]\s*\S+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EmailPattern = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static bool ContainsCredentialMarker(string value) =>
        CredentialMarkerPattern.IsMatch(value)
        || HighConfidenceCredentialPattern.IsMatch(value)
        || AuthorizationMarkerPattern.IsMatch(value)
        || PrivateKeyMarkerPattern.IsMatch(value)
        || SecretAssignmentPattern.IsMatch(value);

    internal static bool ContainsEmail(string value) =>
        EmailPattern.IsMatch(value);

    internal static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}
