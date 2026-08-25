using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationJsonWriterV1Tests
{
    private const string GoldenSha256 = "9f95ad12d58be87869ced76fb995832e94a102c4ffb43588f8cd4f380200166e";
    private const string GenericRouteNotFoundSha256 = "9efd316487e88e9c4ca2440f058d7097518cd01205e5ed1788bd37010f758855";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [Fact]
    public void Golden_ChecksumMatchesCheckedInFixture()
    {
        var bytes = File.ReadAllBytes(GoldenPath());

        Assert.Equal(GoldenSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Golden_StringVectors_MatchExactWriterTokenBytes()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("string_vectors").EnumerateArray())
        {
            AssertStringVector(vector);
        }
    }

    [Fact]
    public void Golden_NumberVectors_MatchExactWriterTokenBytes()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("number_vectors").EnumerateArray())
        {
            AssertNumberVector(vector);
        }
    }

    [Fact]
    public void Golden_ProducerRejectionVectors_AreDetectedBeforeTheWriterAndPairsAreNot()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("producer_rejection_vectors").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString();
            var codeUnit = (char)Convert.ToUInt16(vector.GetProperty("input_utf16_code_unit_hex").GetString(), 16);
            Assert.Equal("reject_before_writer", vector.GetProperty("disposition").GetString());

            Assert.True(
                SkillInvocationJsonWriterV1.ContainsUnpairedSurrogate(new string(codeUnit, 1)),
                $"Expected {name} to be detected as an unpaired surrogate.");
        }

        Assert.False(SkillInvocationJsonWriterV1.ContainsUnpairedSurrogate("😀"));
    }

    [Fact]
    public void Options_MatchTheOwnedContract()
    {
        Assert.False(SkillInvocationJsonWriterV1.Options.Indented);
        Assert.False(SkillInvocationJsonWriterV1.Options.SkipValidation);
        Assert.Same(JavaScriptEncoder.Default, SkillInvocationJsonWriterV1.Options.Encoder);
    }

    [Fact]
    public void WriteErrorEntity_MethodNotAllowed_IsExact30ByteEntity()
    {
        var bodyUtf8 = SkillInvocationJsonWriterV1.WriteErrorEntity("method_not_allowed");

        Assert.Equal(30, bodyUtf8.Length);
        Assert.Equal(Encoding.UTF8.GetBytes("{\"error\":\"method_not_allowed\"}"), bodyUtf8);
        Assert.NotEqual(0xEF, bodyUtf8[0]);
        Assert.NotEqual((byte)'\n', bodyUtf8[^1]);
    }

    [Fact]
    public void WriteErrorEntity_SessionEventContentNotFound_MatchesPinnedGenericRouteDenialFixture()
    {
        var bodyUtf8 = SkillInvocationJsonWriterV1.WriteErrorEntity("session_event_content_not_found");

        Assert.Equal(43, bodyUtf8.Length);
        Assert.Equal(GenericRouteNotFoundSha256, Convert.ToHexStringLower(SHA256.HashData(bodyUtf8)));

        var fixtureBytes = Convert.FromHexString(File.ReadAllText(GenericRouteNotFoundFixturePath()).Trim());
        Assert.Equal(fixtureBytes, bodyUtf8);
    }

    [Fact]
    public void Write_And_WriteErrorEntity_NeverEmitABomFirstByte()
    {
        var errorBody = SkillInvocationJsonWriterV1.WriteErrorEntity("method_not_allowed");
        var documentBody = SkillInvocationJsonWriterV1.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", "probe");
            writer.WriteEndObject();
        });

        Assert.NotEqual(0xEF, errorBody[0]);
        Assert.NotEqual(0xEF, documentBody[0]);
    }

    [Fact]
    public void Golden_StringAndNumberVectors_AreCultureIndependent()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
            foreach (var vector in golden.RootElement.GetProperty("string_vectors").EnumerateArray())
            {
                AssertStringVector(vector);
            }

            foreach (var vector in golden.RootElement.GetProperty("number_vectors").EnumerateArray())
            {
                AssertNumberVector(vector);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static void AssertStringVector(JsonElement vector)
    {
        var name = vector.GetProperty("name").GetString();
        var input = StrictUtf8.GetString(Convert.FromHexString(vector.GetProperty("input_utf8_hex").GetString()!));
        var expectedToken = Convert.FromHexString(vector.GetProperty("json_token_hex").GetString()!);

        var actual = SkillInvocationJsonWriterV1.Write(writer => writer.WriteStringValue(input));

        Assert.True(expectedToken.AsSpan().SequenceEqual(actual), $"String vector '{name}' did not match the golden token bytes.");
    }

    private static void AssertNumberVector(JsonElement vector)
    {
        var name = vector.GetProperty("name").GetString();
        var value = vector.GetProperty("value").GetUInt64();
        var expectedToken = Convert.FromHexString(vector.GetProperty("json_token_hex").GetString()!);

        var actual = SkillInvocationJsonWriterV1.Write(writer => writer.WriteNumberValue(value));

        Assert.True(expectedToken.AsSpan().SequenceEqual(actual), $"Number vector '{name}' did not match the golden token bytes.");
    }

    private static string GoldenPath() => FindRepoFile("TestData", "SkillInvocationSnapshot", "json-writer-v1.golden.json");

    private static string GenericRouteNotFoundFixturePath() =>
        FindRepoFile("TestData", "SkillInvocationSnapshot", "generic-route-not-found.body.hex");

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
