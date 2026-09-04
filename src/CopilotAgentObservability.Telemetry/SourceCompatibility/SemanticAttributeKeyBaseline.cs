namespace CopilotAgentObservability.Telemetry;

internal static class SemanticAttributeKeyBaseline
{
    internal const string Id = "issue-129-ae02f8a7-v1";
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Keys = Load();

    internal static IReadOnlySet<string> ForSource(string sourceFamily) => Keys[sourceFamily];
    internal static bool Supports(string sourceFamily) => Keys.ContainsKey(sourceFamily);

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Load()
    {
        using var stream = typeof(SemanticAttributeKeyBaseline).Assembly.GetManifestResourceStream("semantic-attribute-key-baseline.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("sources").EnumerateObject().ToDictionary(
            source => source.Name,
            source => (IReadOnlySet<string>)source.Value.EnumerateArray().Select(key =>
                SourceStructuralNameToken.FromProducerName(SourceStructuralRole.AttributeKey, key.GetString()!).Value).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }
}
