using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class OwnedSessionEnvelopeMapperV1Tests
{
    private static readonly Guid EventId = Guid.Parse("AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE");
    private static readonly Guid ParentId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 24, 12, 34, 56, TimeSpan.FromHours(9));

    [Fact]
    public void TryMap_TypedStartMapsExactV1EnvelopeAndOnlyCandidateProvenance()
    {
        var source = new SessionStartEvent
        {
            Id = EventId,
            ParentId = ParentId,
            AgentId = "agent-7",
            Timestamp = Timestamp,
            Data = new SessionStartData
            {
                SessionId = "native-session",
                CopilotVersion = "1.0.65",
                Producer = "copilot",
                StartTime = Timestamp,
                Version = 1,
            },
        };

        var envelope = Assert.IsType<SessionIngestEnvelope>(
            OwnedSessionEnvelopeMapperV1.TryMap("native-session", SkillInvocationV2TestIdentity.V1065, source));

        AssertEnvelope(envelope, "session.start", "agent-7");
        Assert.Equal("native-session", envelope.Events![0].Payload.GetProperty("sessionId").GetString());
        Assert.Equal("1.0.65", envelope.Events[0].Payload.GetProperty("copilotVersion").GetString());
        AssertWireOmitsProducerIdentityInternals(envelope);
    }

    [Fact]
    public void TryMap_TypedTaskCompleteMapsExactV1EnvelopeAndTypedPayload()
    {
        var source = new SessionTaskCompleteEvent
        {
            Id = EventId,
            ParentId = ParentId,
            AgentId = "agent-8",
            Timestamp = Timestamp,
            Data = new SessionTaskCompleteData { Success = true },
        };

        var envelope = Assert.IsType<SessionIngestEnvelope>(
            OwnedSessionEnvelopeMapperV1.TryMap("native-session", SkillInvocationV2TestIdentity.V1065, source));

        AssertEnvelope(envelope, "session.task_complete", "agent-8");
        Assert.True(envelope.Events![0].Payload.GetProperty("success").GetBoolean());
        AssertWireOmitsProducerIdentityInternals(envelope);
    }

    [Fact]
    public void TryMap_RejectsInvalidTypedEventIdentityTimeDataAndNativeSession()
    {
        var valid = Start();
        Assert.Null(OwnedSessionEnvelopeMapperV1.TryMap("", SkillInvocationV2TestIdentity.V1065, valid));
        Assert.Null(OwnedSessionEnvelopeMapperV1.TryMap("native", SkillInvocationV2TestIdentity.V1065,
            Start(id: Guid.Parse("AAAAAAAA-BBBB-1CCC-8DDD-EEEEEEEEEEEE"))));
        Assert.Null(OwnedSessionEnvelopeMapperV1.TryMap("native", SkillInvocationV2TestIdentity.V1065,
            Start(parentId: Guid.Parse("11111111-2222-1333-8444-555555555555"))));
        Assert.Null(OwnedSessionEnvelopeMapperV1.TryMap("native", SkillInvocationV2TestIdentity.V1065,
            new SessionStartEvent { Id = EventId, Timestamp = default,
                Data = new SessionStartData { SessionId = "native", CopilotVersion = "1.0.65",
                    Producer = "copilot", StartTime = Timestamp, Version = 1 } }));
        Assert.Null(OwnedSessionEnvelopeMapperV1.TryMap("native", SkillInvocationV2TestIdentity.V1065,
            new SessionStartEvent { Id = EventId, Timestamp = Timestamp, Data = null! }));
    }

    private static SessionStartEvent Start(
        Guid? id = null,
        Guid? parentId = null,
        DateTimeOffset? timestamp = null) => new()
    {
        Id = id ?? EventId,
        ParentId = parentId ?? ParentId,
        Timestamp = timestamp ?? Timestamp,
        Data = new SessionStartData { SessionId = "native", CopilotVersion = "1.0.65", Producer = "copilot", StartTime = Timestamp, Version = 1 },
    };

    private static void AssertEnvelope(SessionIngestEnvelope envelope, string type, string runNativeId)
    {
        Assert.Equal(1, envelope.SchemaVersion);
        Assert.Equal("copilot-sdk-stream", envelope.SourceAdapter);
        Assert.Equal("copilot-sdk", envelope.SourceSurface);
        Assert.Equal("native-session", envelope.NativeSessionId);
        Assert.Null(envelope.ExplicitLink);
        Assert.Equal(SkillInvocationV2TestIdentity.V1065.SourceApplicationVersion, envelope.SourceApplicationVersion);
        Assert.Equal(SkillInvocationV2TestIdentity.V1065.AdapterVersion, envelope.AdapterVersion);
        Assert.Equal(SkillInvocationV2TestIdentity.V1065.SchemaFingerprint, envelope.SchemaFingerprint);
        Assert.Equal(SkillInvocationV2TestIdentity.V1065.NormalizationVersion, envelope.NormalizationVersion);
        var item = Assert.Single(envelope.Events!);
        Assert.Equal(EventId.ToString("D").ToLowerInvariant(), item.SourceEventId);
        Assert.Equal(ParentId.ToString("D").ToLowerInvariant(), item.ParentEventId);
        Assert.Equal(type, item.Type);
        Assert.Equal("2026-08-24T03:34:56.0000000Z", item.OccurredAtValue);
        Assert.Equal(runNativeId, item.RunNativeId);
        Assert.Null(item.TraceId);
    }

    private static void AssertWireOmitsProducerIdentityInternals(SessionIngestEnvelope envelope)
    {
        var json = JsonSerializer.Serialize(envelope);
        Assert.DoesNotContain("protocolVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("registryRevision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certifiedIdentity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payloadSchema", json, StringComparison.OrdinalIgnoreCase);
    }
}
