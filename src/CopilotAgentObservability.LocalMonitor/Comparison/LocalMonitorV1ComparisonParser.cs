using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor;

internal sealed record LocalMonitorV1ComparisonPreviewRequest(
    IReadOnlyList<string> CohortA,
    IReadOnlyList<string> CohortB,
    bool IncludeArchived);

internal sealed record LocalMonitorV1ComparisonCreateRequest(
    IReadOnlyList<string> CohortA,
    IReadOnlyList<string> CohortB,
    bool IncludeArchived,
    string SelectionSha256,
    string PreviewRevision);

internal sealed class LocalMonitorV1ComparisonRequestException : Exception
{
    internal LocalMonitorV1ComparisonRequestException() : base("invalid_request") { }
    internal string Code => "invalid_request";
}

internal static class LocalMonitorV1ComparisonParser
{
    private const int MaximumRequestBytes = 16_384;

    internal static LocalMonitorV1ComparisonPreviewRequest ParsePreview(ReadOnlySpan<byte> entity)
    {
        if (entity.Length is < 1 or > MaximumRequestBytes
            || entity.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            Reject();
        try
        {
            var reader = new Utf8JsonReader(entity, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            Expect(ref reader, JsonTokenType.StartObject);
            ExpectProperty(ref reader, "schema_version");
            Expect(ref reader, JsonTokenType.String);
            if (!reader.ValueTextEquals("local-monitor-comparison-preview.request.v1"u8)) Reject();
            ExpectProperty(ref reader, "cohorts");
            Expect(ref reader, JsonTokenType.StartObject);
            ExpectProperty(ref reader, "a");
            var cohortA = ReadCohort(ref reader);
            ExpectProperty(ref reader, "b");
            var cohortB = ReadCohort(ref reader);
            Expect(ref reader, JsonTokenType.EndObject);
            ExpectProperty(ref reader, "include_archived");
            if (!reader.Read() || reader.TokenType is not (JsonTokenType.True or JsonTokenType.False)) Reject();
            var includeArchived = reader.GetBoolean();
            Expect(ref reader, JsonTokenType.EndObject);
            if (reader.Read() || cohortA.Count + cohortB.Count > 200) Reject();
            return new(cohortA, cohortB, includeArchived);
        }
        catch (JsonException)
        {
            throw new LocalMonitorV1ComparisonRequestException();
        }
        catch (InvalidOperationException)
        {
            throw new LocalMonitorV1ComparisonRequestException();
        }
    }

    internal static LocalMonitorV1ComparisonCreateRequest ParseCreate(ReadOnlySpan<byte> entity)
    {
        if (entity.Length is < 1 or > MaximumRequestBytes
            || entity.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) Reject();
        try
        {
            var reader = new Utf8JsonReader(entity, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            Expect(ref reader, JsonTokenType.StartObject);
            ExpectProperty(ref reader, "schema_version");
            Expect(ref reader, JsonTokenType.String);
            if (!reader.ValueTextEquals("local-monitor-comparison-create.request.v1"u8)) Reject();
            ExpectProperty(ref reader, "cohorts");
            Expect(ref reader, JsonTokenType.StartObject);
            ExpectProperty(ref reader, "a");
            var cohortA = ReadCohort(ref reader);
            ExpectProperty(ref reader, "b");
            var cohortB = ReadCohort(ref reader);
            Expect(ref reader, JsonTokenType.EndObject);
            ExpectProperty(ref reader, "include_archived");
            if (!reader.Read() || reader.TokenType is not (JsonTokenType.True or JsonTokenType.False)) Reject();
            var includeArchived = reader.GetBoolean();
            ExpectProperty(ref reader, "selection_sha256");
            var selectionSha256 = ReadDigest(ref reader);
            ExpectProperty(ref reader, "preview_revision");
            var previewRevision = ReadDigest(ref reader);
            Expect(ref reader, JsonTokenType.EndObject);
            if (reader.Read() || cohortA.Count + cohortB.Count > 200) Reject();
            return new(cohortA, cohortB, includeArchived, selectionSha256, previewRevision);
        }
        catch (JsonException)
        {
            throw new LocalMonitorV1ComparisonRequestException();
        }
        catch (InvalidOperationException)
        {
            throw new LocalMonitorV1ComparisonRequestException();
        }
    }

    private static string ReadDigest(ref Utf8JsonReader reader)
    {
        Expect(ref reader, JsonTokenType.String);
        var value = reader.GetString();
        if (value is null || value.Length != 64
            || value.Any(static character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) Reject();
        return value!;
    }

    private static IReadOnlyList<string> ReadCohort(ref Utf8JsonReader reader)
    {
        Expect(ref reader, JsonTokenType.StartArray);
        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String || values.Count == 199) Reject();
            var value = reader.GetString();
            if (value is null || !IsCanonicalUuidV7(value)) Reject();
            values.Add(value!);
        }
        if (reader.TokenType != JsonTokenType.EndArray || values.Count == 0) Reject();
        return Array.AsReadOnly(values.ToArray());
    }

    private static void ExpectProperty(ref Utf8JsonReader reader, string name)
    {
        Expect(ref reader, JsonTokenType.PropertyName);
        if (!reader.ValueTextEquals(name)) Reject();
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType token)
    {
        if (!reader.Read() || reader.TokenType != token) Reject();
    }

    private static bool IsCanonicalUuidV7(string value)
    {
        if (value.Length != 36 || value[8] != '-' || value[13] != '-' || value[18] != '-' || value[23] != '-'
            || value[14] != '7' || value[19] is not ('8' or '9' or 'a' or 'b')) return false;
        for (var index = 0; index < value.Length; index++)
            if (index is not (8 or 13 or 18 or 23) && value[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static void Reject() => throw new LocalMonitorV1ComparisonRequestException();
}
