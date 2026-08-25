using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

internal sealed record SkillInvocationHistoricalContentV1Input(
    Guid SnapshotId,
    string Body,
    string DefinitionPath,
    string BodySha256,
    string DefinitionPathSha256,
    string CapturedAt);

internal enum SkillInvocationCurrentFileRequestOutcomeV1
{
    Accepted,
    TooLarge,
    NotSingleJsonObject,
    SchemaVersionMissing,
    SchemaVersionInvalid,
    DuplicateProperty,
    UnknownProperty,
    MalformedJson
}

internal readonly record struct SkillInvocationCurrentFileRequestV1Result(SkillInvocationCurrentFileRequestOutcomeV1 Outcome)
{
    internal bool IsAccepted => Outcome == SkillInvocationCurrentFileRequestOutcomeV1.Accepted;
}

internal static class SkillInvocationContentDocumentsV1
{
    internal const string HistoricalSchemaVersion = "local-skill-invocation-snapshot.content.v1";
    internal const string HistoricalContentKind = "historical_snapshot";

    internal const string CurrentFileRequestSchemaVersion = "local-skill-current-file-read.request.v1";
    internal const int CurrentFileRequestMaxBytes = 128;

    internal const string CurrentFileResponseSchemaVersion = "local-skill-current-file-read.response.v1";
    internal const string CurrentFileContentKind = "current_file";

    internal const string ContentType = SkillInvocationMetadataDocumentV1.ContentType;
    internal const string CacheControl = SkillInvocationMetadataDocumentV1.CacheControl;

    private const string ComparisonSame = "same";
    private const string ComparisonChanged = "changed";

    private const int TimestampLength = 33;

    // Copied verbatim from SkillInvocationV2Parser.Parse so a trailing comma or a comment is
    // rejected the same way at both JSON boundaries this component owns.
    private static readonly JsonReaderOptions RequestReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] SchemaVersionPropertyUtf8 = "schema_version"u8.ToArray();
    private static readonly byte[] CurrentFileRequestSchemaVersionUtf8 = "local-skill-current-file-read.request.v1"u8.ToArray();

    internal static byte[] WriteHistoricalContent(SkillInvocationHistoricalContentV1Input input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireNoUnpairedSurrogate(input.Body, nameof(input.Body));
        RequireNoUnpairedSurrogate(input.DefinitionPath, nameof(input.DefinitionPath));
        RequireTimestampLength(input.CapturedAt, nameof(input.CapturedAt));

        return SkillInvocationJsonWriterV1.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", HistoricalSchemaVersion);
            writer.WriteString("snapshot_id", input.SnapshotId.ToString("D"));
            writer.WriteString("content_kind", HistoricalContentKind);
            writer.WriteString("body", input.Body);
            writer.WriteString("definition_path", input.DefinitionPath);
            writer.WriteString("body_sha256", input.BodySha256);
            writer.WriteString("definition_path_sha256", input.DefinitionPathSha256);
            writer.WriteString("captured_at", input.CapturedAt);
            writer.WriteEndObject();
        });
    }

    internal static SkillInvocationCurrentFileRequestV1Result ParseCurrentFileRequest(ReadOnlySpan<byte> requestUtf8)
    {
        if (requestUtf8.Length > CurrentFileRequestMaxBytes)
        {
            return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.TooLarge);
        }

        try
        {
            var reader = new Utf8JsonReader(requestUtf8, RequestReaderOptions);

            if (!reader.Read())
            {
                return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.MalformedJson);
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.NotSingleJsonObject);
            }

            var seenSchemaVersion = false;
            var schemaVersionValid = false;
            var sawDuplicateProperty = false;
            var sawUnknownProperty = false;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.MalformedJson);
                }

                var isSchemaVersionProperty = reader.ValueTextEquals(SchemaVersionPropertyUtf8);

                if (!reader.Read())
                {
                    return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.MalformedJson);
                }

                if (!isSchemaVersionProperty)
                {
                    sawUnknownProperty = true;
                    SkipValue(ref reader);
                    continue;
                }

                if (seenSchemaVersion)
                {
                    sawDuplicateProperty = true;
                    SkipValue(ref reader);
                    continue;
                }

                seenSchemaVersion = true;
                schemaVersionValid = reader.TokenType == JsonTokenType.String
                    && reader.ValueTextEquals(CurrentFileRequestSchemaVersionUtf8);
            }

            if (reader.TokenType != JsonTokenType.EndObject)
            {
                return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.MalformedJson);
            }

            if (reader.Read())
            {
                // Something followed the closing brace: not the single JSON object the contract requires.
                return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.MalformedJson);
            }

            if (sawDuplicateProperty)
            {
                return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.DuplicateProperty);
            }

            if (sawUnknownProperty)
            {
                return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.UnknownProperty);
            }

            if (!seenSchemaVersion)
            {
                return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.SchemaVersionMissing);
            }

            if (!schemaVersionValid)
            {
                return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.SchemaVersionInvalid);
            }

            return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.Accepted);
        }
        catch (JsonException)
        {
            return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.MalformedJson);
        }
        catch (InvalidOperationException)
        {
            return Outcome(SkillInvocationCurrentFileRequestOutcomeV1.MalformedJson);
        }
    }

    // comparison is byte identity, never digest identity: this signature takes the two raw UTF-8
    // byte sequences and nothing else, so there is no digest value in scope for it to consult even
    // by mistake. historicalBodySha256 below is a caller-supplied value used only for the
    // historical_body_sha256 property; it never participates in the equality decision.
    internal static bool BodiesAreByteIdentical(ReadOnlySpan<byte> historicalBodyUtf8, ReadOnlySpan<byte> currentBodyUtf8) =>
        historicalBodyUtf8.SequenceEqual(currentBodyUtf8);

    internal static byte[] WriteCurrentFileResponse(
        Guid snapshotId,
        ReadOnlySpan<byte> historicalBodyUtf8,
        string historicalBodySha256,
        ReadOnlySpan<byte> currentBodyUtf8,
        string readAt)
    {
        ArgumentNullException.ThrowIfNull(historicalBodySha256);
        RequireTimestampLength(readAt, nameof(readAt));

        var comparison = BodiesAreByteIdentical(historicalBodyUtf8, currentBodyUtf8) ? ComparisonSame : ComparisonChanged;
        var currentBodySha256 = Convert.ToHexStringLower(SHA256.HashData(currentBodyUtf8));
        var currentBodyUtf8Bytes = (ulong)currentBodyUtf8.Length;
        var currentBody = StrictUtf8.GetString(currentBodyUtf8);

        return SkillInvocationJsonWriterV1.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", CurrentFileResponseSchemaVersion);
            writer.WriteString("snapshot_id", snapshotId.ToString("D"));
            writer.WriteString("content_kind", CurrentFileContentKind);
            writer.WriteString("comparison", comparison);
            writer.WriteString("historical_body_sha256", historicalBodySha256);
            writer.WriteString("current_body_sha256", currentBodySha256);
            SkillInvocationJsonWriterV1.WriteUnsignedNumber(writer, "current_body_utf8_bytes", currentBodyUtf8Bytes);
            writer.WriteString("body", currentBody);
            writer.WriteString("read_at", readAt);
            writer.WriteEndObject();
        });
    }

    private static SkillInvocationCurrentFileRequestV1Result Outcome(SkillInvocationCurrentFileRequestOutcomeV1 outcome) => new(outcome);

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is not (JsonTokenType.StartArray or JsonTokenType.StartObject))
        {
            return;
        }

        var depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.CurrentDepth == depth && reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
            {
                return;
            }
        }

        throw new JsonException("Unexpected end of current-file request JSON while skipping a value.");
    }

    private static void RequireNoUnpairedSurrogate(string value, string parameterName)
    {
        if (SkillInvocationJsonWriterV1.ContainsUnpairedSurrogate(value))
        {
            throw new ArgumentException(
                "Historical content text must not contain an unpaired UTF-16 surrogate.",
                parameterName);
        }
    }

    private static void RequireTimestampLength(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != TimestampLength)
        {
            throw new ArgumentException(
                $"An r0001 timestamp must render to exactly {TimestampLength} characters.",
                parameterName);
        }
    }
}
