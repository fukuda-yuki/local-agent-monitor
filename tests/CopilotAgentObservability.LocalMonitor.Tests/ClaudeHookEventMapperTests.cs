using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.LocalMonitor.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ClaudeHookEventMapperTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 7, 13, 1, 2, 3, TimeSpan.Zero);
    private static readonly ClaudeHookSourceMetadata VersionMetadata = new(
        "claude-hook-v1", "session-normalization-v1", "2.1.999-synthetic", null);
    private const string SyntheticFingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static TheoryData<string, string> AcceptedFixtures => new()
    {
        { "session-start.json", "SessionStart" },
        { "user-prompt-submit.json", "UserPromptSubmit" },
        { "pre-tool-use.json", "PreToolUse" },
        { "permission-request.json", "PermissionRequest" },
        { "post-tool-use.json", "PostToolUse" },
        { "post-tool-use-failure.json", "PostToolUseFailure" },
        { "subagent-start.json", "SubagentStart" },
        { "subagent-stop.json", "SubagentStop" },
        { "stop.json", "Stop" },
        { "stop-failure.json", "StopFailure" },
        { "session-end.json", "SessionEnd" },
    };

    [Theory]
    [MemberData(nameof(AcceptedFixtures))]
    public void TryMap_AcceptedProducerFixture_CreatesStrictClaudeEnvelope(string fixtureName, string eventType)
    {
        using var fixture = ReadFixture(fixtureName);

        var accepted = ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, VersionMetadata, contentCaptureEnabled: true, out var envelope);

        Assert.True(accepted);
        Assert.NotNull(envelope);
        Assert.Equal(1, envelope.SchemaVersion);
        Assert.Equal("claude-code-hook", envelope.SourceAdapter);
        Assert.Equal("claude-code", envelope.SourceSurface);
        Assert.Equal("SYNTHETIC_SESSION_0001", envelope.NativeSessionId);
        Assert.Equal("2.1.999-synthetic", envelope.SourceApplicationVersion);
        Assert.Equal("claude-hook-v1", envelope.AdapterVersion);
        Assert.Null(envelope.SchemaFingerprint);
        Assert.Equal("session-normalization-v1", envelope.NormalizationVersion);
        Assert.Null(envelope.ExplicitLink);

        var mappedEvent = Assert.Single(envelope.Events!);
        Assert.Equal(eventType, mappedEvent.Type);
        Assert.Equal(CapturedAt.ToString("O"), mappedEvent.OccurredAtValue);
        Assert.Equal(CanonicalHash(fixture.RootElement), mappedEvent.SourceEventId);
        Assert.Null(mappedEvent.ParentEventId);
        Assert.Null(mappedEvent.TraceId);
        Assert.True(JsonElement.DeepEquals(fixture.RootElement, mappedEvent.Payload));
        Assert.True(SessionIngestValidation.IsValid(envelope));
    }

    [Theory]
    [InlineData("pre-tool-use.json", "SYNTHETIC_PARENT_AGENT_ID")]
    [InlineData("subagent-start.json", "SYNTHETIC_SUBAGENT_0001")]
    [InlineData("subagent-stop.json", "SYNTHETIC_SUBAGENT_0001")]
    public void TryMap_AgentId_CopiesExactRunNativeIdentity(string fixtureName, string expectedAgentId)
    {
        using var fixture = ReadFixture(fixtureName);

        Assert.True(ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, VersionMetadata, true, out var envelope));

        Assert.Equal(expectedAgentId, Assert.Single(envelope!.Events!).RunNativeId);
    }

    [Fact]
    public void TryMap_OptionalFieldsAreAbsent_LeavesRunIdentityAbsentAndDoesNotAddPayloadMembers()
    {
        using var fixture = ReadFixture("session-end.json");

        Assert.True(ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, VersionMetadata, true, out var envelope));

        var mappedEvent = Assert.Single(envelope!.Events!);
        Assert.Null(mappedEvent.RunNativeId);
        Assert.False(mappedEvent.Payload.TryGetProperty("prompt_id", out _));
        Assert.False(mappedEvent.Payload.TryGetProperty("permission_mode", out _));
        Assert.False(mappedEvent.Payload.TryGetProperty("agent_id", out _));
    }

    [Fact]
    public void TryMap_PropertyOrderChanges_ProducesSameCanonicalSourceEventId()
    {
        using var fixture = ReadFixture("post-tool-use.json");
        var reversed = new JsonObject(fixture.RootElement.EnumerateObject().Reverse()
            .Select(property => KeyValuePair.Create<string, JsonNode?>(property.Name, JsonNode.Parse(property.Value.GetRawText()))));
        using var reordered = JsonDocument.Parse(reversed.ToJsonString());

        Assert.True(ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, VersionMetadata, true, out var first));
        Assert.True(ClaudeHookEventMapper.TryMap(reordered.RootElement, CapturedAt, VersionMetadata, true, out var second));

        Assert.Equal(Assert.Single(first!.Events!).SourceEventId, Assert.Single(second!.Events!).SourceEventId);
    }

    [Fact]
    public void TryMap_AdditionalAcceptedProducerMember_ChangesWholeObjectIdentity()
    {
        using var fixture = ReadFixture("session-end.json");
        var changedNode = ReadFixtureNode("session-end.json");
        changedNode["producer_extension"] = "SYNTHETIC_EXTENSION_MARKER";
        using var changed = JsonDocument.Parse(changedNode.ToJsonString());

        Assert.True(ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, VersionMetadata, true, out var first));
        Assert.True(ClaudeHookEventMapper.TryMap(changed.RootElement, CapturedAt, VersionMetadata, true, out var second));

        Assert.NotEqual(Assert.Single(first!.Events!).SourceEventId, Assert.Single(second!.Events!).SourceEventId);
        Assert.Equal("SYNTHETIC_EXTENSION_MARKER", Assert.Single(second.Events!).Payload.GetProperty("producer_extension").GetString());
    }

    [Fact]
    public void TryMap_PayloadContainsProvenanceLikeFields_UsesCallerMetadataAndCaptureTime()
    {
        var fixture = ReadFixtureNode("session-end.json");
        fixture["source_application_version"] = "current-unversioned-claude-code-hooks-documentation";
        fixture["adapter_version"] = "PAYLOAD_MUST_NOT_WIN";
        fixture["schema_fingerprint"] = "PAYLOAD_MUST_NOT_WIN";
        fixture["normalization_version"] = "PAYLOAD_MUST_NOT_WIN";
        fixture["occurred_at"] = "1900-01-01T00:00:00Z";
        using var changed = JsonDocument.Parse(fixture.ToJsonString());

        var metadata = new ClaudeHookSourceMetadata("caller-adapter-v2", "caller-normalization-v3", null, SyntheticFingerprint);
        Assert.True(ClaudeHookEventMapper.TryMap(changed.RootElement, CapturedAt, metadata, true, out var envelope));

        Assert.Null(envelope!.SourceApplicationVersion);
        Assert.Equal("caller-adapter-v2", envelope.AdapterVersion);
        Assert.Equal(SyntheticFingerprint, envelope.SchemaFingerprint);
        Assert.Equal("caller-normalization-v3", envelope.NormalizationVersion);
        Assert.Equal(CapturedAt.ToString("O"), Assert.Single(envelope.Events!).OccurredAtValue);
        Assert.NotEqual("current-unversioned-claude-code-hooks-documentation", envelope.SourceApplicationVersion);
    }

    [Fact]
    public void TryMap_UnsupportedNotification_RejectsWithoutEnvelope()
    {
        using var fixture = ReadFixture("unsupported-event.json");

        var accepted = ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, VersionMetadata, true, out var envelope);

        Assert.False(accepted);
        Assert.Null(envelope);
    }

    [Fact]
    public void TryMap_ContentCaptureDisabled_RejectsWithoutTransportingPayload()
    {
        using var fixture = ReadFixture("user-prompt-submit.json");

        var accepted = ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, VersionMetadata, contentCaptureEnabled: false, out var envelope);

        Assert.False(accepted);
        Assert.Null(envelope);
    }

    [Theory]
    [InlineData("session-end.json", "session_id", true)]
    [InlineData("session-end.json", "session_id", false)]
    [InlineData("session-end.json", "hook_event_name", true)]
    [InlineData("session-end.json", "transcript_path", true)]
    [InlineData("session-end.json", "cwd", false)]
    [InlineData("session-start.json", "source", true)]
    [InlineData("user-prompt-submit.json", "prompt", false)]
    [InlineData("pre-tool-use.json", "tool_name", true)]
    [InlineData("pre-tool-use.json", "tool_input", true)]
    [InlineData("pre-tool-use.json", "tool_use_id", false)]
    [InlineData("post-tool-use.json", "tool_response", true)]
    [InlineData("post-tool-use-failure.json", "error", false)]
    [InlineData("subagent-start.json", "agent_id", true)]
    [InlineData("subagent-start.json", "agent_type", false)]
    [InlineData("subagent-stop.json", "stop_hook_active", true)]
    [InlineData("subagent-stop.json", "agent_transcript_path", false)]
    [InlineData("subagent-stop.json", "last_assistant_message", true)]
    [InlineData("stop.json", "stop_hook_active", false)]
    [InlineData("stop.json", "last_assistant_message", false)]
    [InlineData("stop-failure.json", "error", true)]
    [InlineData("session-end.json", "reason", false)]
    public void TryMap_RequiredFieldMissingOrWrongType_Rejects(string fixtureName, string fieldName, bool remove)
    {
        var fixture = ReadFixtureNode(fixtureName);
        if (remove) fixture.Remove(fieldName);
        else fixture[fieldName] = 123;
        using var changed = JsonDocument.Parse(fixture.ToJsonString());

        var accepted = ClaudeHookEventMapper.TryMap(changed.RootElement, CapturedAt, VersionMetadata, true, out var envelope);

        Assert.False(accepted);
        Assert.Null(envelope);
    }

    [Theory]
    [InlineData("session-start.json", "model")]
    [InlineData("session-start.json", "session_title")]
    [InlineData("pre-tool-use.json", "permission_mode")]
    [InlineData("pre-tool-use.json", "agent_id")]
    [InlineData("permission-request.json", "permission_suggestions")]
    [InlineData("post-tool-use.json", "duration_ms")]
    [InlineData("post-tool-use-failure.json", "is_interrupt")]
    [InlineData("subagent-stop.json", "background_tasks")]
    [InlineData("subagent-stop.json", "session_crons")]
    [InlineData("stop-failure.json", "error_details")]
    public void TryMap_OptionalRecognizedFieldHasWrongType_Rejects(string fixtureName, string fieldName)
    {
        var fixture = ReadFixtureNode(fixtureName);
        fixture[fieldName] = new JsonObject();
        using var changed = JsonDocument.Parse(fixture.ToJsonString());

        Assert.False(ClaudeHookEventMapper.TryMap(changed.RootElement, CapturedAt, VersionMetadata, true, out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void TryMap_EffortLevelHasWrongType_Rejects()
    {
        var fixture = ReadFixtureNode("pre-tool-use.json");
        fixture["effort"]!["level"] = false;
        using var changed = JsonDocument.Parse(fixture.ToJsonString());

        Assert.False(ClaudeHookEventMapper.TryMap(changed.RootElement, CapturedAt, VersionMetadata, true, out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void TryMap_FingerprintOnlyMetadata_CopiesCallerValuesExactly()
    {
        using var fixture = ReadFixture("session-end.json");
        var metadata = new ClaudeHookSourceMetadata("adapter.v7", "normalization-v8", null, SyntheticFingerprint);

        Assert.True(ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, metadata, true, out var envelope));

        Assert.Equal("adapter.v7", envelope!.AdapterVersion);
        Assert.Equal("normalization-v8", envelope.NormalizationVersion);
        Assert.Null(envelope.SourceApplicationVersion);
        Assert.Equal(SyntheticFingerprint, envelope.SchemaFingerprint);
    }

    [Fact]
    public void TryMap_ConcreteVersionMetadata_CopiesCallerValuesExactly()
    {
        using var fixture = ReadFixture("session-end.json");
        var metadata = new ClaudeHookSourceMetadata("adapter+v7", "normalization_v8", "3.4.5-caller", null);

        Assert.True(ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, metadata, true, out var envelope));

        Assert.Equal("adapter+v7", envelope!.AdapterVersion);
        Assert.Equal("normalization_v8", envelope.NormalizationVersion);
        Assert.Equal("3.4.5-caller", envelope.SourceApplicationVersion);
        Assert.Null(envelope.SchemaFingerprint);
    }

    [Fact]
    public void TryMap_MissingMetadata_RejectsWithoutEnvelope()
    {
        using var fixture = ReadFixture("session-end.json");

        Assert.False(ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, null, true, out var envelope));
        Assert.Null(envelope);
    }

    public static TheoryData<string?, string?, string?, string?> InvalidMetadata => new()
    {
        { null, "normalization-v1", "3.4.5", null },
        { "", "normalization-v1", "3.4.5", null },
        { "adapter/v1", "normalization-v1", "3.4.5", null },
        { "adapter-v1", null, "3.4.5", null },
        { "adapter-v1", new string('n', 257), "3.4.5", null },
        { "adapter-v1", "normalization v1", "3.4.5", null },
        { "adapter-v1", "normalization-v1", null, null },
        { "adapter-v1", "normalization-v1", "", null },
        { "adapter-v1", "normalization-v1", "version/from/payload", null },
        { "adapter-v1", "normalization-v1", null, "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF" },
        { "adapter-v1", "normalization-v1", null, "0123456789abcdef" },
        { "adapter-v1", "normalization-v1", null, "g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" },
        { "adapter-v1", "normalization-v1", null, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0" },
        { new string('a', 257), "normalization-v1", "3.4.5", null },
    };

    [Theory]
    [MemberData(nameof(InvalidMetadata))]
    public void TryMap_InvalidMetadata_RejectsWithoutEnvelope(
        string? adapterVersion,
        string? normalizationVersion,
        string? sourceApplicationVersion,
        string? schemaFingerprint)
    {
        using var fixture = ReadFixture("session-end.json");
        var metadata = new ClaudeHookSourceMetadata(
            adapterVersion, normalizationVersion, sourceApplicationVersion, schemaFingerprint);

        Assert.False(ClaudeHookEventMapper.TryMap(fixture.RootElement, CapturedAt, metadata, true, out var envelope));
        Assert.Null(envelope);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"SessionEnd\"")]
    [InlineData("42")]
    public void TryMap_NonObjectRoot_Rejects(string json)
    {
        using var input = JsonDocument.Parse(json);

        Assert.False(ClaudeHookEventMapper.TryMap(input.RootElement, CapturedAt, VersionMetadata, true, out var envelope));
        Assert.Null(envelope);
    }

    [Theory]
    [InlineData("sessionend")]
    [InlineData("sessionEnd")]
    [InlineData("SESSIONEND")]
    [InlineData("SessionEnd ")]
    public void TryMap_CaseOrWhitespaceNearAllowlistedEvent_Rejects(string eventName)
    {
        var fixture = ReadFixtureNode("session-end.json");
        fixture["hook_event_name"] = eventName;
        using var changed = JsonDocument.Parse(fixture.ToJsonString());

        Assert.False(ClaudeHookEventMapper.TryMap(changed.RootElement, CapturedAt, VersionMetadata, true, out var envelope));
        Assert.Null(envelope);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    [InlineData(0, false)]
    public void TryMap_NativeSessionIdBoundary_IsStrict(int length, bool expected)
    {
        var fixture = ReadFixtureNode("session-end.json");
        fixture["session_id"] = length == 0 ? " " : new string('s', length);
        using var changed = JsonDocument.Parse(fixture.ToJsonString());

        Assert.Equal(expected, ClaudeHookEventMapper.TryMap(changed.RootElement, CapturedAt, VersionMetadata, true, out var envelope));
        Assert.Equal(expected, envelope is not null);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    [InlineData(0, false)]
    public void TryMap_RunNativeIdBoundary_IsStrict(int length, bool expected)
    {
        var fixture = ReadFixtureNode("pre-tool-use.json");
        fixture["agent_id"] = length == 0 ? " " : new string('a', length);
        using var changed = JsonDocument.Parse(fixture.ToJsonString());

        Assert.Equal(expected, ClaudeHookEventMapper.TryMap(changed.RootElement, CapturedAt, VersionMetadata, true, out var envelope));
        Assert.Equal(expected, envelope is not null);
    }

    [Theory]
    [InlineData("{\"session_id\":\"first\",\"session_id\":\"second\",\"transcript_path\":\"p\",\"cwd\":\"c\",\"hook_event_name\":\"SessionEnd\",\"reason\":\"other\"}")]
    [InlineData("{\"session_id\":\"s\",\"transcript_path\":\"p\",\"cwd\":\"c\",\"hook_event_name\":\"SessionEnd\",\"reason\":\"other\",\"extension\":{\"name\":1,\"name\":2}}")]
    [InlineData("{\"session_id\":\"s\",\"transcript_path\":\"p\",\"cwd\":\"c\",\"hook_event_name\":\"SessionEnd\",\"reason\":\"other\",\"extension\":[{\"name\":1,\"name\":2}]}")]
    public void TryMap_DuplicatePropertyAtAnyDepth_RejectsBeforeHashing(string json)
    {
        using var input = JsonDocument.Parse(json);

        Assert.False(ClaudeHookEventMapper.TryMap(input.RootElement, CapturedAt, VersionMetadata, true, out var envelope));
        Assert.Null(envelope);
    }

    [Fact]
    public void TryMap_ArrayOrderChanges_ChangesIdentityAndPreservesPayloadOrder()
    {
        var firstNode = ReadFixtureNode("stop.json");
        firstNode["background_tasks"] = new JsonArray("first", "second");
        var secondNode = firstNode.DeepClone().AsObject();
        secondNode["background_tasks"] = new JsonArray("second", "first");
        using var firstInput = JsonDocument.Parse(firstNode.ToJsonString());
        using var secondInput = JsonDocument.Parse(secondNode.ToJsonString());

        Assert.True(ClaudeHookEventMapper.TryMap(firstInput.RootElement, CapturedAt, VersionMetadata, true, out var first));
        Assert.True(ClaudeHookEventMapper.TryMap(secondInput.RootElement, CapturedAt, VersionMetadata, true, out var second));

        Assert.NotEqual(Assert.Single(first!.Events!).SourceEventId, Assert.Single(second!.Events!).SourceEventId);
        Assert.Equal("first", Assert.Single(first.Events!).Payload.GetProperty("background_tasks")[0].GetString());
    }

    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "TestData", "Claude", "hooks");
    private static JsonDocument ReadFixture(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureRoot, name)));
    private static JsonObject ReadFixtureNode(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureRoot, name)))!.AsObject();

    private static string CanonicalHash(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(element, writer);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
