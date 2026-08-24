using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using GitHub.Copilot;
using SkillInvocationNormalizedJsonV1 = CopilotAgentObservability.LocalMonitor.Tests.SkillInvocationNormalizedJsonTestWriter;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationNormalizedJsonV1Tests
{
    [Fact]
    public void TryWrite_UsesExplicitCertifiedDefinitionAndNeverSdkAuxiliaryContent()
    {
        var sourceEvent = CompleteEvent();
        sourceEvent.Data.Content = "\n\n";

        Assert.True(SkillInvocationNormalizedJsonV1.TryWrite(
            "native-session", sourceEvent, "# synthetic native definition", out var bodyUtf8));

        using var document = JsonDocument.Parse(Assert.IsType<byte[]>(bodyUtf8));
        var payload = document.RootElement.GetProperty("events")[0].GetProperty("payload");
        Assert.Equal("# synthetic native definition", payload.GetProperty("content").GetString());
        Assert.Equal("description", payload.GetProperty("description").GetString());
        Assert.DoesNotContain("\n\n", Encoding.UTF8.GetString(bodyUtf8), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryWrite_RejectsMissingOrMalformedCertifiedDefinition(bool malformed)
    {
        string? certifiedContent = malformed ? new string((char)0xD800, 1) : null;
        Assert.False(SkillInvocationNormalizedJsonV1.TryWrite(
            "native-session", CompleteEvent(), certifiedContent, out var bodyUtf8));
        Assert.Null(bodyUtf8);
    }

    [Fact]
    public void TryWrite_AllSdkFields_EmitsExactOrderedNormalizedJson()
    {
        var sourceEvent = CompleteEvent();

        var written = SkillInvocationNormalizedJsonV1.TryWrite("native-session", sourceEvent, out var bodyUtf8);

        Assert.True(written);
        Assert.Equal(
            "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"native-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4\\u002Bcao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v2\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"018f0f4e-7b2a-4c11-8a3b-123456789abc\",\"source_parent_event_id\":\"aaaaaaaa-aaaa-4aaa-9aaa-aaaaaaaaaaaa\",\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:30.1234567\\u002B00:00\",\"run_native_id\":\"agent-7\",\"source_ephemeral\":true,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"skill-name\",\"path\":\"skills/SKILL.md\",\"content\":\"body\",\"allowedTools\":[\"second\",\"first\"],\"description\":\"description\",\"pluginName\":\"plugin-name\",\"pluginVersion\":\"1.2.3\",\"source\":\"plugin\",\"trigger\":\"agent-invoked\"}}]}",
            Encoding.UTF8.GetString(Assert.IsType<byte[]>(bodyUtf8)));

        using var document = JsonDocument.Parse(bodyUtf8);
        var payload = document.RootElement.GetProperty("events")[0].GetProperty("payload");
        Assert.Equal(
            ["name", "path", "content", "allowedTools", "description", "pluginName", "pluginVersion", "source", "trigger"],
            payload.EnumerateObject().Select(property => property.Name));
        Assert.False(payload.TryGetProperty("model", out _));
        Assert.Equal(["second", "first"], payload.GetProperty("allowedTools").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void TryWrite_AbsentSdkOptionalsAndNonTrueEphemeral_OmitsOptionalsAndWritesFalse(bool? ephemeral)
    {
        var sourceEvent = RequiredOnlyEvent();
        sourceEvent.Ephemeral = ephemeral;

        var written = SkillInvocationNormalizedJsonV1.TryWrite("native-session", sourceEvent, out var bodyUtf8);

        Assert.True(written);
        Assert.Equal(
            "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"native-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4\\u002Bcao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v2\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"018f0f4e-7b2a-4c11-8a3b-123456789abc\",\"source_parent_event_id\":null,\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000\\u002B00:00\",\"run_native_id\":null,\"source_ephemeral\":false,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"skill-name\",\"path\":\"skills/SKILL.md\",\"content\":\"body\"}}]}",
            Encoding.UTF8.GetString(Assert.IsType<byte[]>(bodyUtf8)));
    }

    [Fact]
    public void TryWrite_ProducerStrings_MatchCheckedInWriterGoldenTokens()
    {
        using var golden = JsonDocument.Parse(File.ReadAllBytes(GoldenPath()));
        foreach (var vector in golden.RootElement.GetProperty("string_vectors").EnumerateArray())
        {
            var sourceEvent = RequiredOnlyEvent();
            sourceEvent.Data.Content = StrictUtf8.GetString(Convert.FromHexString(vector.GetProperty("input_utf8_hex").GetString()!));

            Assert.True(SkillInvocationNormalizedJsonV1.TryWrite("native-session", sourceEvent, out var bodyUtf8));

            var expectedToken = Convert.FromHexString(vector.GetProperty("json_token_hex").GetString()!);
            Assert.True(
                Assert.IsType<byte[]>(bodyUtf8).AsSpan().IndexOf(expectedToken) >= 0,
                $"Golden token was not emitted for {vector.GetProperty("name").GetString()}.");
        }
    }

    [Theory]
    [MemberData(nameof(UnpairedSurrogateCases))]
    public void TryWrite_UnpairedSurrogateInAnyProducerString_ReturnsUnavailableWithoutBody(
        string position,
        int invalidCodeUnit)
    {
        var invalidValue = new string((char)invalidCodeUnit, 1);
        var sessionId = "native-session";
        var sourceEvent = CompleteEvent();
        switch (position)
        {
            case "session": sessionId = invalidValue; break;
            case "agent": sourceEvent.AgentId = invalidValue; break;
            case "name": sourceEvent.Data.Name = invalidValue; break;
            case "path": sourceEvent.Data.Path = invalidValue; break;
            case "content": sourceEvent.Data.Content = invalidValue; break;
            case "allowedTools": sourceEvent.Data.AllowedTools = ["first", invalidValue, "last"]; break;
            case "description": sourceEvent.Data.Description = invalidValue; break;
            case "pluginName": sourceEvent.Data.PluginName = invalidValue; break;
            case "pluginVersion": sourceEvent.Data.PluginVersion = invalidValue; break;
            case "source": sourceEvent.Data.Source = invalidValue; break;
            case "trigger": sourceEvent.Data.Trigger = new SkillInvokedTrigger(invalidValue); break;
            default: throw new InvalidOperationException($"Unknown test position {position}.");
        }

        var written = SkillInvocationNormalizedJsonV1.TryWrite(sessionId, sourceEvent, out var bodyUtf8);

        Assert.False(written);
        Assert.Null(bodyUtf8);
    }

    [Theory]
    [InlineData("source-version")]
    [InlineData("source-variant")]
    [InlineData("parent-version")]
    [InlineData("parent-variant")]
    [InlineData("timestamp-default")]
    public void TryWrite_InvalidSdkIdentityOrTime_ReturnsUnavailableWithoutBody(string invalidField)
    {
        var sourceEvent = CompleteEvent();
        switch (invalidField)
        {
            case "source-version": sourceEvent.Id = Guid.Parse("018f0f4e-7b2a-3c11-8a3b-123456789abc"); break;
            case "source-variant": sourceEvent.Id = Guid.Parse("018f0f4e-7b2a-4c11-7a3b-123456789abc"); break;
            case "parent-version": sourceEvent.ParentId = Guid.Parse("aaaaaaaa-aaaa-3aaa-9aaa-aaaaaaaaaaaa"); break;
            case "parent-variant": sourceEvent.ParentId = Guid.Parse("aaaaaaaa-aaaa-4aaa-7aaa-aaaaaaaaaaaa"); break;
            case "timestamp-default": sourceEvent.Timestamp = default; break;
            default: throw new InvalidOperationException($"Unknown invalid field {invalidField}.");
        }

        var written = SkillInvocationNormalizedJsonV1.TryWrite("native-session", sourceEvent, out var bodyUtf8);

        Assert.False(written);
        Assert.Null(bodyUtf8);
    }

    [Theory]
    [MemberData(nameof(InvalidSessionIds))]
    public void TryWrite_InvalidNativeSessionId_ReturnsUnavailableWithoutBody(string? sessionId)
    {
        var written = SkillInvocationNormalizedJsonV1.TryWrite(sessionId, CompleteEvent(), out var bodyUtf8);

        Assert.False(written);
        Assert.Null(bodyUtf8);
    }

    [Fact]
    public void TryWrite_MaximumNativeSessionScalarAndByteBounds_ProducesCompatibleParserHandoff()
    {
        var sessionId = string.Concat(Enumerable.Repeat("\U0001F600", 256));
        var capability = new TestRuntimeCapability();

        Assert.True(SkillInvocationNormalizedJsonV1.TryWrite(sessionId, CompleteEvent(), out var bodyUtf8));
        var batch = SkillInvocationV2Parser.Parse(Assert.IsType<byte[]>(bodyUtf8), capability);

        var accepted = Assert.Single(batch.AcceptedEnvelopes);
        Assert.Same(capability, batch.RuntimeCapability);
        Assert.Equal(SkillInvocationPayloadState.Available, accepted.PayloadState);
        Assert.Equal("skill-name", accepted.Name);
        Assert.Equal("skills/SKILL.md", accepted.DefinitionPath!.Text);
        Assert.Equal("body", accepted.Body!.Text);
        Assert.Equal("plugin", accepted.Source);
        Assert.Equal("agent-invoked", accepted.Trigger);
    }

    public static IEnumerable<object[]> UnpairedSurrogateCases()
    {
        foreach (var position in new[]
        {
            "session", "agent", "name", "path", "content", "allowedTools", "description",
            "pluginName", "pluginVersion", "source", "trigger"
        })
        {
            yield return [position, 0xd800];
            yield return [position, 0xdc00];
        }
    }

    public static IEnumerable<object?[]> InvalidSessionIds()
    {
        yield return [null];
        yield return [string.Empty];
        yield return ["contains\0nul"];
        yield return [new string('s', 257)];
    }

    private static SkillInvokedEvent CompleteEvent() => new()
    {
        Id = Guid.Parse("018f0f4e-7b2a-4c11-8a3b-123456789abc"),
        ParentId = Guid.Parse("aaaaaaaa-aaaa-4aaa-9aaa-aaaaaaaaaaaa"),
        Timestamp = new DateTimeOffset(2026, 8, 9, 5, 45, 30, TimeSpan.FromHours(5.75)).AddTicks(1_234_567),
        AgentId = "agent-7",
        Ephemeral = true,
        Data = new SkillInvokedData
        {
            Name = "skill-name",
            Path = "skills/SKILL.md",
            Content = "body",
            AllowedTools = ["second", "first"],
            Description = "description",
            PluginName = "plugin-name",
            PluginVersion = "1.2.3",
            Source = "plugin",
            Trigger = SkillInvokedTrigger.AgentInvoked
        }
    };

    private static SkillInvokedEvent RequiredOnlyEvent() => new()
    {
        Id = Guid.Parse("018f0f4e-7b2a-4c11-8a3b-123456789abc"),
        Timestamp = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
        Data = new SkillInvokedData
        {
            Name = "skill-name",
            Path = "skills/SKILL.md",
            Content = "body"
        }
    };

    private static string GoldenPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "tests",
                "CopilotAgentObservability.LocalMonitor.Tests",
                "TestData",
                "SkillInvocationSnapshot",
                "json-writer-v1.golden.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Checked-in skill invocation writer golden was not found.");
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed class TestRuntimeCapability : ISkillInvocationV2RuntimeCapability
    {
        public CopilotAgentObservability.LocalMonitor.SkillRuntime.CertifiedSkillProducerIdentityV1 CertifiedIdentity => SkillInvocationV2TestIdentity.V1065;
    }
}
