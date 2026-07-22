using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotAgentObservability.SanitizedExport;

public static class SanitizedExportJson
{
    private static readonly string[] ControlProperties = ["schema_version", "created_at", "selection"];
    private static readonly string[] SelectionProperties =
    [
        "session_ids", "trace_ids", "source_surfaces", "repository_names", "workspace_labels",
        "start_inclusive", "end_exclusive", "receipt_types",
    ];
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static byte[] SerializeRequest(SanitizedExportRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Options);
    internal static SanitizedExportRequest DeserializeRequest(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<SanitizedExportRequest>(bytes, Options) ?? throw new JsonException();
    public static byte[] SerializeControlRequest(SanitizedExportControlRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Options);
    public static SanitizedExportControlRequest DeserializeControlRequest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > SanitizedExportLimits.MaximumControlRequestBytes) throw new JsonException();
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        RequireExactProperties(document.RootElement, ControlProperties);
        RequireExactProperties(document.RootElement.GetProperty("selection"), SelectionProperties);
        return JsonSerializer.Deserialize<SanitizedExportControlRequest>(bytes, Options) ?? throw new JsonException();
    }
    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            WriteIndented = false,
        };
        options.Converters.Add(new CanonicalDateTimeOffsetConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    private static void RequireExactProperties(JsonElement element, IReadOnlyList<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException();
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Count || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
            || expected.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
            throw new JsonException();
    }

    private sealed class CanonicalDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || !DateTimeOffset.TryParseExact(reader.GetString(), Format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                throw new JsonException();
            return value;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            if (value.Offset != TimeSpan.Zero) throw new JsonException();
            writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
        }
    }
}

public sealed record SanitizedExportInspectionResult(
    bool Success,
    string? ErrorCode,
    string? ArchiveSha256,
    string? ManifestSchemaVersion = null,
    string? BundleSchemaVersion = null,
    string? BundleProfile = null,
    int RecordCount = 0,
    long TotalUncompressedBytes = 0);

public sealed record SanitizedExportResultView(
    bool Success,
    string? ErrorCode,
    string? ArchiveSha256,
    string? PublishedFileName,
    SanitizedExportPreview Preview)
{
    public static SanitizedExportResultView From(SanitizedExportResult result) =>
        new(result.Success, result.ErrorCode, result.ArchiveSha256, result.PublishedFileName, result.Preview);
}
