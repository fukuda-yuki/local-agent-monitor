namespace CopilotAgentObservability.Telemetry;

internal static class RawStoreDefaults
{
    public const int SchemaVersion = 1;

    public static string DefaultDatabasePath => Path.Combine("data", "raw-store.db");
}

internal static class RawTelemetrySources
{
    public const string RawOtlp = "raw-otlp";
    public const string CollectorOutput = "collector-output";
    public const string LangfuseExport = "langfuse-export";

    private static readonly HashSet<string> AllowedValues = new(StringComparer.Ordinal)
    {
        RawOtlp,
        CollectorOutput,
        LangfuseExport,
    };

    public static bool IsAllowed(string source)
    {
        return AllowedValues.Contains(source);
    }
}

internal sealed record RawTelemetryRecord(
    long? Id,
    string Source,
    string? TraceId,
    DateTimeOffset ReceivedAt,
    string? ResourceAttributesJson,
    string PayloadJson,
    int SchemaVersion = RawStoreDefaults.SchemaVersion);
