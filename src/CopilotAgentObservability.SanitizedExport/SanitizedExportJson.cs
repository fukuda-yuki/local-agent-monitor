using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotAgentObservability.SanitizedExport;

public static class SanitizedExportJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] SerializeRequest(SanitizedExportRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Options);
    public static SanitizedExportRequest DeserializeRequest(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<SanitizedExportRequest>(bytes, Options) ?? throw new JsonException();
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
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
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
