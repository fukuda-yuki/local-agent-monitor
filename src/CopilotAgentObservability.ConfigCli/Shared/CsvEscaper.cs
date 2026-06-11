namespace CopilotAgentObservability.ConfigCli;

internal static class CsvEscaper
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Any(character => character is ',' or '"' or '\r' or '\n')
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}
