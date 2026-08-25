using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationContentDocumentsV1Tests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly Guid SampleSnapshotId = Guid.Parse("018f0f4e-7b2a-7c11-8a3f-123456789abc");
    private const string SampleReadAt = "2026-08-09T00:00:01.0000000+00:00";

    private const string MinimalAcceptedCurrentFileRequestJson =
        "{\"schema_version\":\"local-skill-current-file-read.request.v1\"}";

    private static readonly string[] HistoricalExpectedOrder =
    [
        "schema_version", "snapshot_id", "content_kind", "body", "definition_path",
        "body_sha256", "definition_path_sha256", "captured_at"
    ];

    private static readonly string[] CurrentFileResponseExpectedOrder =
    [
        "schema_version", "snapshot_id", "content_kind", "comparison", "historical_body_sha256",
        "current_body_sha256", "current_body_utf8_bytes", "body", "read_at"
    ];

    // --- Historical content ---

    [Fact]
    public void WriteHistoricalContent_EmitsPropertiesInExactSpecOrderWithNoneOmittedOrAdded()
    {
        var bytes = SkillInvocationContentDocumentsV1.WriteHistoricalContent(SampleHistoricalInput());
        using var document = JsonDocument.Parse(bytes);

        var order = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(HistoricalExpectedOrder, order);
        Assert.Equal("local-skill-invocation-snapshot.content.v1", document.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("historical_snapshot", document.RootElement.GetProperty("content_kind").GetString());
    }

    [Fact]
    public void WriteHistoricalContent_ByteShape_HasNoBomNoTrailingLfNoIndentation()
    {
        var bytes = SkillInvocationContentDocumentsV1.WriteHistoricalContent(SampleHistoricalInput());

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal((byte)'}', bytes[^1]);
        Assert.NotEqual((byte)'\n', bytes[^1]);
    }

    [Fact]
    public void WriteHistoricalContent_Body_EscapesExactlyPerGoldenWriterStringVectors()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(WriterGoldenPath()));
        string[] vectorNames = ["ascii-solidus", "nonascii-bmp", "nonascii-astral", "html-sensitive-and-quote", "backslash", "controls"];

        var rawBody = new StringBuilder();
        var expectedEscapedPayload = new StringBuilder();
        foreach (var name in vectorNames)
        {
            var vector = FindStringVector(golden, name);
            rawBody.Append(StrictUtf8.GetString(Convert.FromHexString(vector.GetProperty("input_utf8_hex").GetString()!)));

            var tokenBytes = Convert.FromHexString(vector.GetProperty("json_token_hex").GetString()!);
            // Strip the surrounding quote bytes the golden fixture fixes for a standalone string
            // token; the remaining bytes are pure ASCII escapes, safe to splice as literal text.
            expectedEscapedPayload.Append(Encoding.ASCII.GetString(tokenBytes, 1, tokenBytes.Length - 2));
        }

        var input = SampleHistoricalInput() with { Body = rawBody.ToString() };
        var documentBytes = SkillInvocationContentDocumentsV1.WriteHistoricalContent(input);
        var documentText = Encoding.UTF8.GetString(documentBytes);

        var expectedSegment = "\"body\":\"" + expectedEscapedPayload + "\",\"definition_path\":";
        Assert.Contains(expectedSegment, documentText, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteHistoricalContent_BodyWithUnpairedSurrogate_IsRejectedBeforeTheWriterRuns()
    {
        var unpaired = new string('\uD800', 1);
        Assert.True(SkillInvocationJsonWriterV1.ContainsUnpairedSurrogate(unpaired));

        var input = SampleHistoricalInput() with { Body = unpaired };

        Assert.Throws<ArgumentException>(() => SkillInvocationContentDocumentsV1.WriteHistoricalContent(input));
    }

    [Fact]
    public void WriteHistoricalContent_DefinitionPathWithUnpairedSurrogate_IsRejectedBeforeTheWriterRuns()
    {
        var unpaired = new string('\uDC00', 1);
        Assert.True(SkillInvocationJsonWriterV1.ContainsUnpairedSurrogate(unpaired));

        var input = SampleHistoricalInput() with { DefinitionPath = unpaired };

        Assert.Throws<ArgumentException>(() => SkillInvocationContentDocumentsV1.WriteHistoricalContent(input));
    }

    [Theory]
    [InlineData("2026-08-09T00:00:00.0000000+00:0")] // 32 characters
    [InlineData("2026-08-09T00:00:00.0000000+00:000")] // 34 characters
    public void WriteHistoricalContent_CapturedAtNotExactly33Characters_IsRejected(string capturedAt)
    {
        var input = SampleHistoricalInput() with { CapturedAt = capturedAt };

        Assert.Throws<ArgumentException>(() => SkillInvocationContentDocumentsV1.WriteHistoricalContent(input));
    }

    // --- Current-file request parser ---

    [Fact]
    public void ParseCurrentFileRequest_MinimalAcceptedBody_IsAcceptedAndExactly61Bytes()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalAcceptedCurrentFileRequestJson);
        Assert.Equal(61, bytes.Length);

        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(bytes);

        Assert.True(result.IsAccepted);
        Assert.Equal(SkillInvocationCurrentFileRequestOutcomeV1.Accepted, result.Outcome);
    }

    [Fact]
    public void ParseCurrentFileRequest_Exactly128Bytes_IsAccepted()
    {
        var body = PadWithLeadingWhitespace(MinimalAcceptedCurrentFileRequestJson, 128);
        Assert.Equal(128, Encoding.UTF8.GetByteCount(body));

        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(Encoding.UTF8.GetBytes(body));

        Assert.True(result.IsAccepted);
    }

    [Fact]
    public void ParseCurrentFileRequest_Exactly129Bytes_IsRejectedAsTooLarge()
    {
        var body = PadWithLeadingWhitespace(MinimalAcceptedCurrentFileRequestJson, 129);
        Assert.Equal(129, Encoding.UTF8.GetByteCount(body));

        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(Encoding.UTF8.GetBytes(body));

        Assert.Equal(SkillInvocationCurrentFileRequestOutcomeV1.TooLarge, result.Outcome);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"local-skill-current-file-read.request.v1\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void ParseCurrentFileRequest_RootIsNotASingleJsonObject_IsRejected(string json)
    {
        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(Encoding.UTF8.GetBytes(json));

        Assert.Equal(SkillInvocationCurrentFileRequestOutcomeV1.NotSingleJsonObject, result.Outcome);
    }

    [Fact]
    public void ParseCurrentFileRequest_MissingSchemaVersion_IsRejected()
    {
        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(Encoding.UTF8.GetBytes("{}"));

        Assert.Equal(SkillInvocationCurrentFileRequestOutcomeV1.SchemaVersionMissing, result.Outcome);
    }

    [Fact]
    public void ParseCurrentFileRequest_WrongSchemaVersionValue_IsRejected()
    {
        var json = "{\"schema_version\":\"local-skill-current-file-read.request.v2\"}";

        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(Encoding.UTF8.GetBytes(json));

        Assert.Equal(SkillInvocationCurrentFileRequestOutcomeV1.SchemaVersionInvalid, result.Outcome);
    }

    [Fact]
    public void ParseCurrentFileRequest_DuplicateSchemaVersionProperty_IsRejected()
    {
        var json = "{\"schema_version\":\"local-skill-current-file-read.request.v1\","
            + "\"schema_version\":\"local-skill-current-file-read.request.v1\"}";

        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(Encoding.UTF8.GetBytes(json));

        Assert.Equal(SkillInvocationCurrentFileRequestOutcomeV1.DuplicateProperty, result.Outcome);
    }

    [Fact]
    public void ParseCurrentFileRequest_UnknownExtraProperty_IsRejected()
    {
        var json = "{\"schema_version\":\"local-skill-current-file-read.request.v1\",\"extra\":\"x\"}";

        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(Encoding.UTF8.GetBytes(json));

        Assert.Equal(SkillInvocationCurrentFileRequestOutcomeV1.UnknownProperty, result.Outcome);
    }

    [Fact]
    public void ParseCurrentFileRequest_MalformedJson_IsRejected()
    {
        var json = "{\"schema_version\":\"local-skill-current-file-read.request.v1\"";

        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(Encoding.UTF8.GetBytes(json));

        Assert.Equal(SkillInvocationCurrentFileRequestOutcomeV1.MalformedJson, result.Outcome);
    }

    [Fact]
    public void ParseCurrentFileRequest_TrailingComma_IsRejected()
    {
        var json = "{\"schema_version\":\"local-skill-current-file-read.request.v1\",}";

        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(Encoding.UTF8.GetBytes(json));

        Assert.Equal(SkillInvocationCurrentFileRequestOutcomeV1.MalformedJson, result.Outcome);
    }

    [Fact]
    public void ParseCurrentFileRequest_Comment_IsRejected()
    {
        var json = "{\"schema_version\":\"local-skill-current-file-read.request.v1\" /* x */}";

        var result = SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(Encoding.UTF8.GetBytes(json));

        Assert.Equal(SkillInvocationCurrentFileRequestOutcomeV1.MalformedJson, result.Outcome);
    }

    [Fact]
    public void ParseCurrentFileRequest_NeverThrows_ForAnyMalformedOrInvalidInput()
    {
        byte[][] payloads =
        [
            Encoding.UTF8.GetBytes("[]"),
            Encoding.UTF8.GetBytes("\"x\""),
            Encoding.UTF8.GetBytes("1"),
            Encoding.UTF8.GetBytes("null"),
            Encoding.UTF8.GetBytes("{}"),
            Encoding.UTF8.GetBytes("{\"schema_version\":\"wrong\"}"),
            Encoding.UTF8.GetBytes(
                "{\"schema_version\":\"local-skill-current-file-read.request.v1\","
                + "\"schema_version\":\"local-skill-current-file-read.request.v1\"}"),
            Encoding.UTF8.GetBytes("{\"schema_version\":\"local-skill-current-file-read.request.v1\",\"extra\":\"x\"}"),
            Encoding.UTF8.GetBytes("{\"schema_version\":\"local-skill-current-file-read.request.v1\""),
            Encoding.UTF8.GetBytes("{\"schema_version\":\"local-skill-current-file-read.request.v1\",}"),
            Encoding.UTF8.GetBytes("{\"schema_version\":\"local-skill-current-file-read.request.v1\" /* x */}"),
            [0xFF, 0xFE, 0x00, 0x01],
            new byte[200]
        ];

        foreach (var payload in payloads)
        {
            var exception = Record.Exception(() => SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(payload));
            Assert.Null(exception);
        }
    }

    // --- Current-file response ---

    [Fact]
    public void WriteCurrentFileResponse_EmitsPropertiesInExactSpecOrder()
    {
        var body = Encoding.UTF8.GetBytes("hello");

        var bytes = SkillInvocationContentDocumentsV1.WriteCurrentFileResponse(
            SampleSnapshotId, body, new string('a', 64), body, SampleReadAt);
        using var document = JsonDocument.Parse(bytes);

        var order = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(CurrentFileResponseExpectedOrder, order);
        Assert.Equal("local-skill-current-file-read.response.v1", document.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("current_file", document.RootElement.GetProperty("content_kind").GetString());
    }

    // The next two tests are the digest trap: they prove `comparison` never consults a digest by
    // deliberately mismatching the supplied historical digest against what the byte comparison
    // would imply, in both directions, and asserting the byte-driven outcome wins each time.
    [Fact]
    public void WriteCurrentFileResponse_UnequalBytesWithAMatchingSuppliedDigest_StillReportsChanged()
    {
        var historical = Encoding.UTF8.GetBytes("abcde");
        var current = Encoding.UTF8.GetBytes("abcdf");
        var digestOfCurrentBytes = Convert.ToHexStringLower(SHA256.HashData(current));

        var bytes = SkillInvocationContentDocumentsV1.WriteCurrentFileResponse(
            SampleSnapshotId, historical, digestOfCurrentBytes, current, SampleReadAt);
        using var document = JsonDocument.Parse(bytes);

        Assert.Equal("changed", document.RootElement.GetProperty("comparison").GetString());
    }

    [Fact]
    public void WriteCurrentFileResponse_IdenticalBytesWithAWrongSuppliedDigest_StillReportsSame()
    {
        var body = Encoding.UTF8.GetBytes("identical-body");
        var wrongHistoricalDigest = new string('0', 64);

        var bytes = SkillInvocationContentDocumentsV1.WriteCurrentFileResponse(
            SampleSnapshotId, body, wrongHistoricalDigest, body, SampleReadAt);
        using var document = JsonDocument.Parse(bytes);

        Assert.Equal("same", document.RootElement.GetProperty("comparison").GetString());
    }

    [Theory]
    [MemberData(nameof(ComparisonCases))]
    public void WriteCurrentFileResponse_ComparisonReflectsExactByteEquality(byte[] historical, byte[] current, string expectedComparison)
    {
        var bytes = SkillInvocationContentDocumentsV1.WriteCurrentFileResponse(
            SampleSnapshotId, historical, new string('a', 64), current, SampleReadAt);
        using var document = JsonDocument.Parse(bytes);

        Assert.Equal(expectedComparison, document.RootElement.GetProperty("comparison").GetString());
    }

    public static IEnumerable<object[]> ComparisonCases()
    {
        yield return [Encoding.UTF8.GetBytes("abcde"), Encoding.UTF8.GetBytes("abcdX"), "changed"];
        yield return [Encoding.UTF8.GetBytes("abc"), Encoding.UTF8.GetBytes("abcdef"), "changed"];
        yield return [Array.Empty<byte>(), Array.Empty<byte>(), "same"];
        yield return [Array.Empty<byte>(), Encoding.UTF8.GetBytes("x"), "changed"];
    }

    [Fact]
    public void WriteCurrentFileResponse_CurrentBodySha256IsFreshlyComputedAndHistoricalIsEmittedAsSupplied()
    {
        var current = Encoding.UTF8.GetBytes("fresh-current-body");
        var suppliedHistoricalDigest = "deadbeef" + new string('0', 56);

        var bytes = SkillInvocationContentDocumentsV1.WriteCurrentFileResponse(
            SampleSnapshotId, Encoding.UTF8.GetBytes("irrelevant-historical-body"), suppliedHistoricalDigest, current, SampleReadAt);
        using var document = JsonDocument.Parse(bytes);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(current)), document.RootElement.GetProperty("current_body_sha256").GetString());
        Assert.Equal(suppliedHistoricalDigest, document.RootElement.GetProperty("historical_body_sha256").GetString());
    }

    [Fact]
    public void WriteCurrentFileResponse_CurrentBodyUtf8Bytes_EqualsLengthAndIsUnquoted()
    {
        var current = Encoding.UTF8.GetBytes("seven!!");

        var bytes = SkillInvocationContentDocumentsV1.WriteCurrentFileResponse(
            SampleSnapshotId, current, new string('a', 64), current, SampleReadAt);
        using var document = JsonDocument.Parse(bytes);

        var property = document.RootElement.GetProperty("current_body_utf8_bytes");
        Assert.Equal(JsonValueKind.Number, property.ValueKind);
        Assert.Equal((ulong)current.Length, property.GetUInt64());
    }

    [Fact]
    public void WriteCurrentFileResponse_ZeroLengthCurrentBody_EmitsUnsignedZeroToken()
    {
        var bytes = SkillInvocationContentDocumentsV1.WriteCurrentFileResponse(
            SampleSnapshotId, [], new string('a', 64), [], SampleReadAt);

        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"current_body_utf8_bytes\":0,", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteCurrentFileResponse_ReadAtIsEmittedExactlyAsSupplied()
    {
        var current = Encoding.UTF8.GetBytes("body");

        var bytes = SkillInvocationContentDocumentsV1.WriteCurrentFileResponse(
            SampleSnapshotId, current, new string('a', 64), current, SampleReadAt);
        using var document = JsonDocument.Parse(bytes);

        Assert.Equal(SampleReadAt, document.RootElement.GetProperty("read_at").GetString());
    }

    [Fact]
    public void ProductionFile_SamplesNoClock()
    {
        var text = File.ReadAllText(ProductionFilePath());

        Assert.DoesNotContain("TimeProvider", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime.Now", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTimeOffset.UtcNow", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteCurrentFileResponse_PropertyOrderAndByteCount_AreCultureIndependent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            var current = Encoding.UTF8.GetBytes("seven!!");

            var bytes = SkillInvocationContentDocumentsV1.WriteCurrentFileResponse(
                SampleSnapshotId, current, new string('a', 64), current, SampleReadAt);
            using var document = JsonDocument.Parse(bytes);

            var order = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(CurrentFileResponseExpectedOrder, order);

            var property = document.RootElement.GetProperty("current_body_utf8_bytes");
            Assert.Equal(JsonValueKind.Number, property.ValueKind);
            Assert.Equal((ulong)current.Length, property.GetUInt64());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void WriteHistoricalContent_PropertyOrder_IsCultureIndependent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            var bytes = SkillInvocationContentDocumentsV1.WriteHistoricalContent(SampleHistoricalInput());
            using var document = JsonDocument.Parse(bytes);

            var order = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(HistoricalExpectedOrder, order);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    // --- Shared helpers ---

    private static SkillInvocationHistoricalContentV1Input SampleHistoricalInput() => new(
        SnapshotId: SampleSnapshotId,
        Body: "print('hello')",
        DefinitionPath: ".claude/skills/review/SKILL.md",
        BodySha256: new string('3', 64),
        DefinitionPathSha256: new string('4', 64),
        CapturedAt: "2026-08-09T00:00:00.0000000+00:00");

    private static string PadWithLeadingWhitespace(string minimalJson, int targetByteLength)
    {
        var padding = targetByteLength - minimalJson.Length;
        return "{" + new string(' ', padding) + minimalJson[1..];
    }

    private static JsonElement FindStringVector(JsonDocument golden, string name)
    {
        foreach (var vector in golden.RootElement.GetProperty("string_vectors").EnumerateArray())
        {
            if (vector.GetProperty("name").GetString() == name)
            {
                return vector;
            }
        }

        throw new InvalidOperationException($"Golden string vector '{name}' was not found.");
    }

    private static string WriterGoldenPath() => FindRepoFile("TestData", "SkillInvocationSnapshot", "json-writer-v1.golden.json");

    private static string ProductionFilePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "CopilotAgentObservability.LocalMonitor",
                "Sessions", "SkillInvocationV2", "SkillInvocationContentDocumentsV1.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Production source file was not found: SkillInvocationContentDocumentsV1.cs");
    }

    private static string FindRepoFile(params string[] relativeSegments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var segments = new[] { directory.FullName, "tests", "CopilotAgentObservability.LocalMonitor.Tests" }
                .Concat(relativeSegments)
                .ToArray();
            var candidate = Path.Combine(segments);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Checked-in fixture was not found: {Path.Combine(relativeSegments)}");
    }
}
