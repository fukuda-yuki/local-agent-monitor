using CopilotAgentObservability.Persistence.Sqlite.Doctor.ClaudeCode;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Sessions;
using System.Text.Json.Nodes;

namespace CopilotAgentObservability.Doctor.Tests.Persistence;

public sealed class ClaudeDoctorCandidateObserverTests
{
    private const string NativeSessionMarker = "NATIVE_SESSION_MARKER";
    private const string PromptMarker = "PROMPT_MARKER";
    private const string PathMarker = "C:\\marker\\prompt.txt";
    private static readonly DateTimeOffset Start = DoctorTestData.Now;
    private const string TraceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SpanId = "bbbbbbbbbbbbbbbb";

    [Fact]
    public void RunOnce_EmitsValidatedCandidatesWithOpaqueReferencesAndPersistedTimestamp()
    {
        using var fixture = CreateFixture();
        var sessionId = fixture.SeedSession(NativeSessionMarker, SessionCompleteness.Partial);
        var verification = fixture.StartVerification();
        var receivedAt = Start;
        fixture.SeedOtel(Payload(TraceId, SpanId, NativeSessionMarker), receivedAt);

        fixture.Observer.RunOnce();

        var candidates = Candidates(fixture, verification);
        Assert.Equal(Enum.GetValues<DoctorEvidenceKind>().Length, candidates.Count);
        Assert.All(candidates, candidate =>
        {
            Assert.True(DoctorValidation.IsValidEvidenceCandidate(candidate));
            Assert.Equal(receivedAt, candidate.ObservedAt);
            Assert.Equal(verification.ExpiresAt, candidate.ExpiresAt);
            Assert.Equal("claude-code", candidate.SourceSurface);
            Assert.Equal("claude-code-otel", candidate.SourceAdapter);
            Assert.Equal(DoctorEvidenceClass.RealSource, candidate.EvidenceClass);
        });
        Assert.Contains(candidates, candidate => candidate.EvidenceRef == $"claude-otel-ingest-{TraceId}-{SpanId}");
        Assert.Contains(candidates, candidate => candidate.EvidenceRef == $"claude-otel-raw-{TraceId}-{SpanId}");
        Assert.Contains(candidates, candidate => candidate.EvidenceRef == $"claude-otel-projection-{TraceId}-{SpanId}");
        Assert.Contains(candidates, candidate => candidate.EvidenceRef == $"claude-otel-binding-{TraceId}-{sessionId:D}");
        Assert.Contains(candidates, candidate => candidate.EvidenceRef == $"claude-otel-completeness-{sessionId:D}");
        var references = string.Join('|', candidates.Select(candidate => candidate.EvidenceRef));
        Assert.DoesNotContain(NativeSessionMarker, references, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptMarker, references, StringComparison.Ordinal);
        Assert.DoesNotContain(PathMarker, references, StringComparison.Ordinal);
    }

    [Fact]
    public void RunOnce_RecordsBeforeVerificationStartAreIgnored()
    {
        using var fixture = CreateFixture();
        fixture.SeedSession(NativeSessionMarker, SessionCompleteness.Partial);
        fixture.SeedOtel(Payload(TraceId, SpanId, NativeSessionMarker), Start.AddSeconds(-1));
        var verification = fixture.StartVerification();

        fixture.Observer.RunOnce();

        Assert.Empty(Candidates(fixture, verification));
    }

    [Fact]
    public void RunOnce_WithNoActiveVerificationIsANoop()
    {
        using var fixture = CreateFixture();
        var first = fixture.StartVerification();
        var second = fixture.StartVerification();

        Assert.Equal(DoctorResultCode.VerificationCancelled, fixture.Application.Cancel(first.VerificationId, first.Revision).Code);
        Assert.Equal(DoctorResultCode.VerificationCancelled, fixture.Application.Cancel(second.VerificationId, second.Revision).Code);

        fixture.Observer.RunOnce();

        var active = fixture.Application.ListActive("claude-code", fixture.Time.UtcNow);
        Assert.Equal(DoctorResultCode.VerificationActive, active.Code);
        Assert.Empty(active.Verifications ?? []);
        Assert.Empty(Candidates(fixture, first));
        Assert.Empty(Candidates(fixture, second));
    }

    [Fact]
    public void RunOnce_IsIdempotentAndRecomputesForEveryActiveVerification()
    {
        using var fixture = CreateFixture();
        var sessionId = fixture.SeedSession(NativeSessionMarker, SessionCompleteness.Partial);
        var first = fixture.StartVerification();
        var second = fixture.StartVerification();
        fixture.SeedOtel(Payload(TraceId, SpanId, NativeSessionMarker), Start.AddSeconds(1));

        fixture.Observer.RunOnce();
        fixture.Observer.RunOnce();

        Assert.Equal(5, Candidates(fixture, first).Count);
        Assert.Equal(5, Candidates(fixture, second).Count);
        Assert.All(Candidates(fixture, first), candidate =>
        {
            if (candidate.EvidenceKind is DoctorEvidenceKind.ExactSessionBinding or DoctorEvidenceKind.CompletenessContent)
            {
                Assert.Contains(sessionId.ToString("D"), candidate.EvidenceRef, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain(sessionId.ToString("D"), candidate.EvidenceRef, StringComparison.Ordinal);
            }
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("HOOK_NATIVE_B")]
    public void RunOnce_SharedTraceIdWithoutExactSessionIdNeverEmitsBindingCandidate(string? otelSessionId)
    {
        using var fixture = CreateFixture();
        fixture.SeedSession("HOOK_NATIVE_A", SessionCompleteness.Partial, TraceId, "claude-code-hook");
        var verification = fixture.StartVerification();
        fixture.SeedOtel(Payload(TraceId, SpanId, otelSessionId), Start.AddSeconds(1));

        fixture.Observer.RunOnce();

        var candidates = Candidates(fixture, verification);
        Assert.DoesNotContain(candidates, candidate => candidate.EvidenceKind == DoctorEvidenceKind.ExactSessionBinding);
        Assert.DoesNotContain(candidates, candidate => candidate.EvidenceKind == DoctorEvidenceKind.CompletenessContent);
        Assert.All(candidates, candidate => Assert.True(DoctorValidation.IsValidEvidenceCandidate(candidate)));

        var result = CompleteWithUnboundSession(fixture, verification, candidates);
        Assert.Contains(result.Evaluation?.States ?? [], state => state.StateCode == DoctorStateCode.SessionUnbound);
        Assert.DoesNotContain(result.Evaluation?.States ?? [], state => state.StateCode == DoctorStateCode.FirstTraceReady);
        Assert.Equal(DoctorResultCode.EvaluationCompleted, result.Code);
        Assert.Equal(DoctorVerificationState.Active, result.Verification?.State);
    }

    [Theory]
    [InlineData("otel-exact")]
    [InlineData("claude-code-otel")]
    public void RunOnce_SessionEventAdapterLabelAloneNeverBinds(string sourceAdapter)
    {
        using var fixture = CreateFixture();
        fixture.SeedSession(null, SessionCompleteness.Partial, TraceId, sourceAdapter);
        var verification = fixture.StartVerification();
        fixture.SeedOtel(Payload(TraceId, SpanId, null), Start.AddSeconds(1));

        fixture.Observer.RunOnce();

        var candidates = Candidates(fixture, verification);
        Assert.DoesNotContain(candidates, candidate => candidate.EvidenceKind == DoctorEvidenceKind.ExactSessionBinding);
        Assert.DoesNotContain(candidates, candidate => candidate.EvidenceKind == DoctorEvidenceKind.CompletenessContent);

        var result = CompleteWithUnboundSession(fixture, verification, candidates);
        Assert.Contains(result.Evaluation?.States ?? [], state => state.StateCode == DoctorStateCode.SessionUnbound);
        Assert.DoesNotContain(result.Evaluation?.States ?? [], state => state.StateCode == DoctorStateCode.FirstTraceReady);
        Assert.Equal(DoctorResultCode.EvaluationCompleted, result.Code);
        Assert.Equal(DoctorVerificationState.Active, result.Verification?.State);
    }

    [Fact]
    public void RunOnce_HookSessionEventsWithoutRecognizedOtelRecordsProduceNoCandidates()
    {
        using var fixture = CreateFixture();
        fixture.SeedSession("HOOK_NATIVE_A", SessionCompleteness.Partial, TraceId, "claude-code-hook");
        var verification = fixture.StartVerification();

        fixture.Observer.RunOnce();

        Assert.Empty(Candidates(fixture, verification));
    }

    [Fact]
    public void RunOnce_RecordsAtOrAfterVerificationExpiryAreIgnoredWithoutStoreError()
    {
        using var fixture = CreateFixture();
        var verification = fixture.StartVerification();
        fixture.SeedOtel(Payload(TraceId, SpanId, NativeSessionMarker), verification.ExpiresAt);
        fixture.SeedOtel(Payload(TraceId, SpanId, NativeSessionMarker), verification.ExpiresAt.AddSeconds(1));

        var exception = Record.Exception(fixture.Observer.RunOnce);

        Assert.Null(exception);
        var candidates = fixture.Application.ListCandidates(verification.VerificationId);
        Assert.Equal(DoctorResultCode.VerificationActive, candidates.Code);
        Assert.Empty(Candidates(fixture, verification));
    }

    private static ObserverFixture CreateFixture()
    {
        var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(Start);
        var retentionContext = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path, time);
        new RawTelemetryStore(database.Path, retentionContext, time, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateMonitorSchema();
        new SqliteSourceCompatibilityStore(database.Path, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        var sessionStore = new SqliteSessionStore(database.Path, time);
        sessionStore.CreateSchema();
        var application = SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(database.Path, time));
        return new ObserverFixture(database, time, sessionStore, application, new ClaudeDoctorCandidateObserver(database.Path, application, new RawTelemetryStore(database.Path, retentionContext, time), time));
    }

    private static IReadOnlyList<DoctorEvidenceCandidate> Candidates(
        ObserverFixture fixture,
        DoctorVerification verification) =>
        fixture.Application.ListCandidates(verification.VerificationId).Candidates
        ?? throw new InvalidOperationException("Candidate read did not return a list.");

    private static DoctorResult CompleteWithUnboundSession(
        ObserverFixture fixture,
        DoctorVerification verification,
        IReadOnlyList<DoctorEvidenceCandidate> candidates)
    {
        var selected = candidates
            .Where(candidate => candidate.EvidenceKind is
                DoctorEvidenceKind.Ingest or
                DoctorEvidenceKind.RawPersistence or
                DoctorEvidenceKind.Projection)
            .ToArray();
        Assert.Equal(
            new[]
            {
                DoctorEvidenceKind.Ingest,
                DoctorEvidenceKind.RawPersistence,
                DoctorEvidenceKind.Projection,
            },
            selected.Select(candidate => candidate.EvidenceKind).OrderBy(kind => kind));

        var snapshot = DoctorTestSnapshots.ReadyNoRealTrace() with
        {
            SourceSurface = verification.ExpectedSourceSurface,
            ExpectedSourceAdapter = verification.ExpectedSourceAdapter,
            VerificationId = verification.VerificationId,
            ObservedAt = fixture.Time.UtcNow,
            Observations = [],
            LastIngest = new LastIngestFacts(LastIngestOutcome.Accepted),
            RawPersistence = new RawPersistenceFacts(RawPersistenceOutcome.Persisted),
            Projection = new ProjectionFacts(ProjectionOutcome.Completed),
            ExactSessionBinding = new ExactSessionBindingFacts(
                ExactSessionBindingRequirement.Required,
                ExactSessionBindingOutcome.Unbound),
            CompletenessAndContent = new CompletenessAndContentFacts(
                DoctorCompleteness.Unknown,
                ContentCaptureStatus.Enabled,
                RawAccessStatus.Available),
        };

        return fixture.Application.Complete(
            verification.VerificationId,
            verification.Revision,
            snapshot,
            selected.Select(candidate => candidate.EvidenceRef).ToArray());
    }

    private static string Payload(string traceId, string spanId, string? sessionId)
    {
        var attributes = new JsonArray();
        if (sessionId is not null)
        {
            attributes.Add(Attribute("session.id", sessionId));
        }
        attributes.Add(Attribute("user_prompt", PromptMarker));
        attributes.Add(Attribute("path", PathMarker));

        var spans = new JsonArray
        {
            new JsonObject
            {
                ["traceId"] = traceId,
                ["spanId"] = spanId,
                ["name"] = "claude_code.llm_request",
                ["startTimeUnixNano"] = "1000000000",
                ["endTimeUnixNano"] = "2000000000",
                ["attributes"] = attributes,
            },
        };
        var scopeSpans = new JsonArray
        {
            new JsonObject { ["spans"] = spans },
        };
        var resourceSpans = new JsonArray
        {
            new JsonObject { ["scopeSpans"] = scopeSpans },
        };
        return new JsonObject { ["resourceSpans"] = resourceSpans }.ToJsonString();
    }

    private static JsonObject Attribute(string key, string value) => new()
    {
        ["key"] = key,
        ["value"] = new JsonObject { ["stringValue"] = value },
    };

    private sealed class ObserverFixture(
        DoctorTestDatabase database,
        DoctorTestTimeProvider time,
        SqliteSessionStore sessionStore,
        SqliteDoctorApplicationService application,
        ClaudeDoctorCandidateObserver observer) : IDisposable
    {
        public DoctorTestTimeProvider Time { get; } = time;
        public SqliteDoctorApplicationService Application { get; } = application;
        public ClaudeDoctorCandidateObserver Observer { get; } = observer;

        public DoctorVerification StartVerification() =>
            Assert.IsType<DoctorVerification>(Application.Start("claude-code", "claude-code-otel", Time.UtcNow.AddMinutes(5)).Verification);

        public Guid SeedSession(
            string? nativeSessionId,
            SessionCompleteness completeness,
            string? traceId = null,
            string sourceAdapter = "claude-code-hook")
        {
            var sessionId = Guid.CreateVersion7();
            var events = traceId is null
                ? []
                : new[]
                {
                    new ObservedSessionEvent(
                        Guid.CreateVersion7(),
                        sessionId,
                        null,
                        SessionSourceSurface.ClaudeCode,
                        null,
                        traceId,
                        null,
                        sourceAdapter,
                        "source-event-marker",
                        "UserPromptSubmit",
                        Time.UtcNow,
                        SessionContentState.Available),
                };
            sessionStore.Write(new(new(
                new ObservedSession(
                    sessionId,
                    ObservedSessionStatus.Unknown,
                    completeness,
                    null,
                    null,
                    null,
                    null,
                    Time.UtcNow,
                    SessionRawRetentionState.NotCaptured,
                    Time.UtcNow,
                    Time.UtcNow),
                nativeSessionId is null
                    ? []
                    : [new SessionNativeId(sessionId, SessionSourceSurface.ClaudeCode, nativeSessionId, SessionBindingKind.Native, Time.UtcNow)],
                [],
                events), []));
            return sessionId;
        }

        public void SeedOtel(
            string payload,
            DateTimeOffset receivedAt,
            string sourceSurface = "claude-code",
            string sourceAdapter = "claude-code-otel")
        {
            var raw = new RawTelemetryRecord(
                null,
                RawTelemetrySources.RawOtlp,
                TraceId,
                receivedAt,
                null,
                payload);
            var inventory = OtlpJsonStructuralWalker.Build(payload, receivedAt);
            var observation = SourceObservationBatchDraft.Create(
                Guid.CreateVersion7().ToString("D"),
                sourceSurface,
                null,
                sourceAdapter,
                "claude-otel-v1",
                inventory,
                SourceCompatibilityEvaluator.Assess(
                    sourceSurface,
                    null,
                    inventory,
                    observedRecognizedCount: 1,
                    VerifiedSourceFingerprintRegistry.Create([], [], [])),
                SourceCaptureContentState.Available,
                receivedAt);
            var committed = new SqliteIngestionCommitStore(
                database.Path,
                RawTelemetryStoreConnectionOptions.MonitorWriter)
                .Commit(ValidatedIngestionBatch.Create(raw, observation));
            var persisted = raw with { Id = committed.RawRecordId };
            var store = new RawTelemetryStore(database.Path, RawTelemetryStoreConnectionOptions.MonitorWriter);
            store.ApplyProjection(
                committed.RawRecordId,
                persisted.Source,
                persisted.ReceivedAt,
                MonitorProjectionBuilder.Build(persisted),
                receivedAt);
            store.ApplySpanProjection(
                committed.RawRecordId,
                MonitorSpanProjectionBuilder.Build(persisted),
                receivedAt);
        }

        public void Dispose() => database.Dispose();
    }
}
