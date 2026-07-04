namespace CopilotAgentObservability.ConfigCli;

internal static class JsonOutput
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public static string WriteIndented<T>(T value)
    {
        return JsonSerializer.Serialize(value, IndentedOptions) + Environment.NewLine;
    }
}
