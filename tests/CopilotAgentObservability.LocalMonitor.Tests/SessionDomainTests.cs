using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionDomainTests
{
    public static TheoryData<string, string> LegacyAdapterSurfacePairs => new()
    {
        { "copilot-sdk-stream", "copilot-sdk" },
        { "copilot-compatible-hook", "copilot-cli" },
        { "copilot-compatible-hook", "vscode" },
        { "copilot-compatible-hook", "hook-unknown" },
    };

    public static TheoryData<string, string> ForbiddenAdapterSurfacePairs
    {
        get
        {
            var valid = LegacyAdapterSurfacePairs
                .Select(pair => (Adapter: (string)pair[0], Surface: (string)pair[1]))
                .Append(("claude-code-hook", "claude-code"))
                .ToHashSet();
            var data = new TheoryData<string, string>();
            foreach (var adapter in new[]
                     {
                         "copilot-sdk-stream",
                         "copilot-compatible-hook",
                         "claude-code-hook",
                         "claude-code-otel",
                         "claude-code-otel+claude-code-hook",
                     })
            {
                foreach (var surface in new[] { "copilot-sdk", "copilot-cli", "vscode", "hook-unknown", "claude-code" })
                {
                    if (!valid.Contains((adapter, surface))) data.Add(adapter, surface);
                }
            }
            return data;
        }
    }

    public static TheoryData<string> InvalidProvenanceTokens => new()
    {
        { "" },
        { " leading" },
        { "trailing " },
        { "two words" },
        { "line\nbreak" },
        { "control\u0001" },
        { "C:/sensitive/path" },
        { "https:example.invalid" },
        { "error(message)" },
        { "-leading-dash" },
        { new string('a', 257) },
    };

    public static TheoryData<SessionCompleteness, string> CompletenessValues => new()
    {
        { SessionCompleteness.Unbound, "unbound" },
        { SessionCompleteness.Partial, "partial" },
        { SessionCompleteness.Rich, "rich" },
        { SessionCompleteness.Full, "full" },
    };

    public static TheoryData<ObservedSessionStatus, string> StatusValues => new()
    {
        { ObservedSessionStatus.Active, "active" },
        { ObservedSessionStatus.Completed, "completed" },
        { ObservedSessionStatus.Failed, "failed" },
        { ObservedSessionStatus.Unknown, "unknown" },
    };

    public static TheoryData<SessionSourceSurface, string> SourceSurfaceValues => new()
    {
        { SessionSourceSurface.CopilotSdk, "copilot-sdk" },
        { SessionSourceSurface.CopilotCli, "copilot-cli" },
        { SessionSourceSurface.VisualStudioCode, "vscode" },
        { SessionSourceSurface.HookUnknown, "hook-unknown" },
        { SessionSourceSurface.ClaudeCode, "claude-code" },
    };

    public static TheoryData<SessionSourceAdapter, string> SourceAdapterValues => new()
    {
        { SessionSourceAdapter.CopilotSdkStream, "copilot-sdk-stream" },
        { SessionSourceAdapter.CopilotCompatibleHook, "copilot-compatible-hook" },
        { SessionSourceAdapter.ClaudeCodeOtel, "claude-code-otel" },
        { SessionSourceAdapter.ClaudeCodeHook, "claude-code-hook" },
    };

    public static TheoryData<SessionBindingKind, string> BindingKindValues => new()
    {
        { SessionBindingKind.Native, "native" },
        { SessionBindingKind.ExplicitResume, "explicit_resume" },
        { SessionBindingKind.ExplicitHandoff, "explicit_handoff" },
        { SessionBindingKind.TraceContext, "trace_context" },
    };

    public static TheoryData<SessionContentState, string> ContentStateValues => new()
    {
        { SessionContentState.Available, "available" },
        { SessionContentState.NotCaptured, "not_captured" },
        { SessionContentState.Redacted, "redacted" },
        { SessionContentState.Unsupported, "unsupported" },
        { SessionContentState.ExpiredPendingDeletion, "expired_pending_deletion" },
    };

    public static TheoryData<SessionRawRetentionState, string> RawRetentionStateValues => new()
    {
        { SessionRawRetentionState.Expiring, "expiring" },
        { SessionRawRetentionState.ExpiredPendingDeletion, "expired_pending_deletion" },
        { SessionRawRetentionState.NotCaptured, "not_captured" },
    };

    [Theory]
    [MemberData(nameof(CompletenessValues))]
    public void CompletenessWireMapping_IsExplicitAndStrict(SessionCompleteness value, string wire)
    {
        Assert.Equal(wire, SessionWire.ToWire(value));
        Assert.Equal(value, SessionWire.ParseCompleteness(wire));
        Assert.Throws<ArgumentException>(() => SessionWire.ParseCompleteness(wire.ToUpperInvariant()));
    }

    [Theory]
    [MemberData(nameof(StatusValues))]
    public void StatusWireMapping_IsExplicitAndStrict(ObservedSessionStatus value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseStatus, value, wire);

    [Theory]
    [MemberData(nameof(SourceSurfaceValues))]
    public void SourceSurfaceWireMapping_IsExplicitAndStrict(SessionSourceSurface value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseSourceSurface, value, wire);

    [Theory]
    [MemberData(nameof(SourceAdapterValues))]
    public void SourceAdapterWireMapping_IsActualAdapterOnlyAndStrict(SessionSourceAdapter value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseSourceAdapter, value, wire);

    [Fact]
    public void SourceAdapterWireMapping_RejectsCompositeRegistryLabel() =>
        Assert.Throws<ArgumentException>(() => SessionWire.ParseSourceAdapter("claude-code-otel+claude-code-hook"));

    [Theory]
    [MemberData(nameof(BindingKindValues))]
    public void BindingKindWireMapping_IsExplicitAndStrict(SessionBindingKind value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseBindingKind, value, wire);

    [Theory]
    [MemberData(nameof(ContentStateValues))]
    public void ContentStateWireMapping_IsExplicitAndStrict(SessionContentState value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseContentState, value, wire);

    [Theory]
    [MemberData(nameof(RawRetentionStateValues))]
    public void RawRetentionStateWireMapping_IsExplicitAndStrict(SessionRawRetentionState value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseRawRetentionState, value, wire);

    [Fact]
    public void WireFormatting_RejectsUndefinedEnumMembers()
    {
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionCompleteness)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((ObservedSessionStatus)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionSourceSurface)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionSourceAdapter)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionBindingKind)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionContentState)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionRawRetentionState)int.MaxValue));
    }

    [Theory]
    [InlineData(false, false, false, false, false, false, false, false, false, SessionCompleteness.Unbound)]
    [InlineData(true, false, true, true, true, true, true, false, false, SessionCompleteness.Partial)]
    [InlineData(true, true, false, true, true, true, true, false, false, SessionCompleteness.Partial)]
    [InlineData(true, true, true, false, true, true, true, false, false, SessionCompleteness.Partial)]
    [InlineData(true, true, true, true, false, true, true, false, false, SessionCompleteness.Rich)]
    [InlineData(true, true, true, true, true, false, true, false, false, SessionCompleteness.Rich)]
    [InlineData(true, true, true, true, true, true, false, false, false, SessionCompleteness.Rich)]
    [InlineData(true, true, true, true, true, true, true, true, false, SessionCompleteness.Rich)]
    [InlineData(true, true, true, true, true, true, true, false, true, SessionCompleteness.Rich)]
    [InlineData(true, true, true, true, true, true, true, false, false, SessionCompleteness.Full)]
    public void Completeness_IsRecomputedFromExplicitEvidence(
        bool hasNativeId,
        bool hasLifecycleStart,
        bool hasUserInstruction,
        bool hasEvidenceFamily,
        bool hasTerminalEvidence,
        bool hasExactLinkedOtelEnrichment,
        bool hasAllSurfaceRequiredEvidence,
        bool hasUnsupportedVersion,
        bool hasIngestGap,
        SessionCompleteness expected)
    {
        var evidence = new SessionCompletenessEvidence(
            hasNativeId,
            hasLifecycleStart,
            hasUserInstruction,
            hasEvidenceFamily,
            hasTerminalEvidence,
            hasExactLinkedOtelEnrichment,
            hasAllSurfaceRequiredEvidence,
            hasUnsupportedVersion,
            hasIngestGap);

        Assert.Equal(expected, SessionCompletenessCalculator.Calculate(evidence));
    }

    [Fact]
    public void LocalIds_AreUuidVersion7()
    {
        var session = ObservedSession.Create(
            ObservedSessionStatus.Active,
            SessionCompleteness.Partial,
            repository: null,
            workspace: null,
            startedAt: null,
            endedAt: null,
            lastSeenAt: DateTimeOffset.UnixEpoch,
            SessionRawRetentionState.NotCaptured);
        var run = ObservedSessionRun.Create(session.SessionId, status: ObservedSessionStatus.Active);
        var @event = ObservedSessionEvent.Create(
            session.SessionId,
            run.RunId,
            sourceAdapter: "copilot-sdk-stream",
            sourceEventId: "event-1",
            type: "session.started",
            occurredAt: DateTimeOffset.UnixEpoch,
            SessionContentState.NotCaptured);

        Assert.Equal(7, session.SessionId.Version);
        Assert.Equal(7, run.RunId.Version);
        Assert.Equal(7, @event.EventId.Version);
        Assert.Equal(session.SessionId.ToString("D"), session.SessionId.ToString());
    }

    [Fact]
    public void ClaudeHookEnvelope_RequiresExactAdapterSurfaceAndCompleteProvenance()
    {
        var valid = DeserializeEnvelope(
            """
            {
              "schema_version": 1,
              "source_adapter": "claude-code-hook",
              "source_surface": "claude-code",
              "source_application_version": null,
              "adapter_version": "1",
              "schema_fingerprint": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
              "normalization_version": "session-v1",
              "native_session_id": "claude-session",
              "events": [{"source_event_id":"hook-event","type":"SessionStart","occurred_at":"2026-07-12T00:00:00Z","payload":{}}]
            }
            """);

        Assert.True(SessionIngestValidation.IsValid(valid));
        Assert.Equal("1", valid.AdapterVersion);
        Assert.Null(valid.SourceApplicationVersion);
        Assert.Equal("session-v1", valid.NormalizationVersion);

        var versioned = valid with
        {
            SourceApplicationVersion = "2.1.207",
            SchemaFingerprint = null,
        };
        Assert.True(SessionIngestValidation.IsValid(versioned));
    }

    [Theory]
    [InlineData("claude-code-otel+claude-code-hook", "claude-code", "1", "2.1.207", null, "session-v1")]
    [InlineData("claude-code-otel", "claude-code", "1", "2.1.207", null, "session-v1")]
    [InlineData("claude-code-hook", "hook-unknown", "1", "2.1.207", null, "session-v1")]
    [InlineData("copilot-compatible-hook", "claude-code", null, null, null, null)]
    [InlineData("claude-code-hook", "claude-code", null, "2.1.207", null, "session-v1")]
    [InlineData("claude-code-hook", "claude-code", "1", null, null, "session-v1")]
    [InlineData("claude-code-hook", "claude-code", "1", "2.1.207", null, null)]
    [InlineData("claude-code-hook", "claude-code", "1", "2.1.207", "NOT-A-SHA-256", "session-v1")]
    public void ClaudeHookEnvelope_RejectsUnknownAdaptersInvalidPairsAndMissingProvenance(
        string sourceAdapter,
        string sourceSurface,
        string? adapterVersion,
        string? sourceApplicationVersion,
        string? schemaFingerprint,
        string? normalizationVersion)
    {
        var envelope = new SessionIngestEnvelope(
            1,
            sourceAdapter,
            sourceSurface,
            "claude-session",
            [new SessionIngestEvent("hook-event", "SessionStart", "2026-07-12T00:00:00Z", JsonDocument.Parse("{}").RootElement.Clone())],
            SourceApplicationVersion: sourceApplicationVersion,
            AdapterVersion: adapterVersion,
            SchemaFingerprint: schemaFingerprint,
            NormalizationVersion: normalizationVersion);

        Assert.False(SessionIngestValidation.IsValid(envelope));
    }

    [Theory]
    [MemberData(nameof(LegacyAdapterSurfacePairs))]
    public void LegacyEnvelope_AllCanonicalAdapterSurfacePairsRemainValid(string sourceAdapter, string sourceSurface)
    {
        var envelope = ValidEnvelope(sourceAdapter, sourceSurface) with
        {
            SourceApplicationVersion = null,
            AdapterVersion = null,
            SchemaFingerprint = null,
            NormalizationVersion = null,
        };

        Assert.True(SessionIngestValidation.IsValid(envelope));
    }

    [Theory]
    [MemberData(nameof(ForbiddenAdapterSurfacePairs))]
    public void Envelope_RejectsEveryForbiddenAdapterSurfacePair(string sourceAdapter, string sourceSurface) =>
        Assert.False(SessionIngestValidation.IsValid(ValidEnvelope(sourceAdapter, sourceSurface)));

    [Theory]
    [MemberData(nameof(InvalidProvenanceTokens))]
    public void ClaudeHookEnvelope_RejectsNonMetadataTokensForEveryTokenField(string invalidToken)
    {
        var envelope = ValidEnvelope("claude-code-hook", "claude-code");

        Assert.False(SessionIngestValidation.IsValid(envelope with { SourceApplicationVersion = invalidToken }));
        Assert.False(SessionIngestValidation.IsValid(envelope with { AdapterVersion = invalidToken }));
        Assert.False(SessionIngestValidation.IsValid(envelope with { NormalizationVersion = invalidToken }));
    }

    [Fact]
    public void ClaudeHookEnvelope_AcceptsMetadataTokenMaximumLength()
    {
        var token = "A" + new string('z', 255);
        var envelope = ValidEnvelope("claude-code-hook", "claude-code") with
        {
            SourceApplicationVersion = token,
            AdapterVersion = token,
            NormalizationVersion = token,
        };

        Assert.True(SessionIngestValidation.IsValid(envelope));
    }

    [Theory]
    [InlineData(63, false, false)]
    [InlineData(65, false, false)]
    [InlineData(64, true, false)]
    [InlineData(64, false, true)]
    public void ClaudeHookEnvelope_RejectsInvalidFingerprintShape(int length, bool uppercase, bool nonHex)
    {
        var fingerprint = new string('a', length);
        if (uppercase) fingerprint = fingerprint.ToUpperInvariant();
        if (nonHex) fingerprint = fingerprint[..^1] + "g";

        Assert.False(SessionIngestValidation.IsValid(
            ValidEnvelope("claude-code-hook", "claude-code") with { SchemaFingerprint = fingerprint }));
    }

    [Fact]
    public void CopilotEnvelope_PreservesV1ContractWithoutAdditiveProvenance()
    {
        var envelope = new SessionIngestEnvelope(
            1,
            "copilot-compatible-hook",
            "hook-unknown",
            "copilot-session",
            [new SessionIngestEvent("hook-event", "SessionStart", "2026-07-12T00:00:00Z", JsonDocument.Parse("{}").RootElement.Clone())]);

        Assert.True(SessionIngestValidation.IsValid(envelope));
        Assert.Null(envelope.SourceApplicationVersion);
        Assert.Null(envelope.AdapterVersion);
        Assert.Null(envelope.SchemaFingerprint);
        Assert.Null(envelope.NormalizationVersion);
    }

    [Fact]
    public void EventDomain_PreservesAdditiveProvenanceWithoutChangingIdentity()
    {
        var sessionId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();

        var @event = ObservedSessionEvent.Create(
            sessionId,
            runId,
            "claude-code-hook",
            "hook-event",
            "SessionStart",
            DateTimeOffset.UnixEpoch,
            SessionContentState.NotCaptured,
            sourceApplicationVersion: null,
            adapterVersion: "1",
            schemaFingerprint: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            normalizationVersion: "session-v1");

        Assert.Equal(sessionId, @event.SessionId);
        Assert.Equal(runId, @event.RunId);
        Assert.Equal("claude-code-hook", @event.SourceAdapter);
        Assert.Null(@event.SourceApplicationVersion);
        Assert.Equal("1", @event.AdapterVersion);
        Assert.Equal("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", @event.SchemaFingerprint);
        Assert.Equal("session-v1", @event.NormalizationVersion);
    }

    private static SessionIngestEnvelope DeserializeEnvelope(string json) =>
        JsonSerializer.Deserialize<SessionIngestEnvelope>(json)!;

    private static SessionIngestEnvelope ValidEnvelope(string sourceAdapter, string sourceSurface) => new(
        1,
        sourceAdapter,
        sourceSurface,
        "native-session",
        [new SessionIngestEvent("event-1", "SessionStart", "2026-07-12T00:00:00Z", JsonDocument.Parse("{}").RootElement.Clone())],
        SourceApplicationVersion: "2.1.207",
        AdapterVersion: "adapter-v1",
        SchemaFingerprint: new string('a', 64),
        NormalizationVersion: "normalization-v1");

    private static void AssertStrict<T>(Func<T, string> format, Func<string, T> parse, T value, string wire)
        where T : struct, Enum
    {
        Assert.Equal(wire, format(value));
        Assert.Equal(value, parse(wire));
        Assert.Throws<ArgumentException>(() => parse(wire.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => parse("unknown-value"));
    }
}
