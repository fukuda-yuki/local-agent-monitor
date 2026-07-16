using System.Text.Json;

namespace CopilotAgentObservability.Doctor.Tests;

public sealed class DoctorValidationTests
{
    [Fact]
    public void Evaluate_UnsupportedFactSchema_ReturnsFixedUnsupportedSchemaResult()
    {
        var result = DoctorEvaluator.Evaluate(DoctorTestSnapshots.ReadyNoRealTrace() with { SchemaVersion = "doctor.facts.v2" });

        Assert.False(result.Success);
        Assert.Equal(DoctorResultCode.UnsupportedSchemaVersion, result.Code);
        Assert.Null(result.Evaluation);
        Assert.Null(result.Verification);
    }

    [Fact]
    public void Evaluate_InvalidLexicalBoundsAndObservationShapes_ReturnsFixedInvalidInput()
    {
        var baseline = DoctorTestSnapshots.ReadyNoRealTrace();
        var validObservation = DoctorTestSnapshots.Observation(DoctorEvidenceKind.Ingest, "event-1");
        var cases = new DoctorFactSnapshot[]
        {
            baseline with { SourceSurface = "GitHub-Copilot" },
            baseline with { SourceSurface = new string('a', 65) },
            baseline with { ExpectedSourceAdapter = "adapter/value" },
            baseline with { ObservedAt = baseline.ObservedAt.ToOffset(TimeSpan.FromHours(9)) },
            baseline with { VerificationId = "not-a-uuid" },
            baseline with { Observations = Enumerable.Range(0, 17).Select(index => validObservation with { EvidenceRef = $"event-{index}" }).ToArray() },
            baseline with { Observations = [validObservation, validObservation with { EvidenceKind = DoctorEvidenceKind.Projection }] },
            baseline with { Observations = [validObservation with { SourceSurface = "claude-code" }] },
            baseline with { ExpectedSourceAdapter = "adapter-v1", Observations = [validObservation] },
            baseline with { Observations = [validObservation with { SourceAdapter = "Adapter/V1" }] },
            baseline with { Observations = [validObservation with { EvidenceClass = (DoctorEvidenceClass)99 }] },
            baseline with { InstallAndSourceVersion = new InstallAndSourceVersionFacts((MonitorInstallStatus)99, SourceVersionStatus.Supported, SourceFeatureStatus.Available) },
        };

        foreach (var snapshot in cases)
        {
            AssertInvalidInput(snapshot);
        }
    }

    [Fact]
    public void Evaluate_InvalidCrossFieldCombinations_ReturnsFixedInvalidInput()
    {
        var baseline = DoctorTestSnapshots.ReadyNoRealTrace();
        var cases = new DoctorFactSnapshot[]
        {
            baseline with { ProcessReceiverAndPort = new ProcessReceiverAndPortFacts(MonitorProcessStatus.NotRunning, ReceiverBindStatus.Bound, PortOwnerStatus.Monitor) },
            baseline with { ProcessReceiverAndPort = new ProcessReceiverAndPortFacts(MonitorProcessStatus.Running, ReceiverBindStatus.Bound, PortOwnerStatus.Foreign) },
            baseline with { ProcessReceiverAndPort = new ProcessReceiverAndPortFacts(MonitorProcessStatus.Running, ReceiverBindStatus.NotBound, PortOwnerStatus.Monitor) },
            baseline with { ExactSessionBinding = new ExactSessionBindingFacts(ExactSessionBindingRequirement.Required, ExactSessionBindingOutcome.NotApplicable) },
            baseline with { ExactSessionBinding = new ExactSessionBindingFacts(ExactSessionBindingRequirement.Required, ExactSessionBindingOutcome.ExactBound), CompletenessAndContent = new CompletenessAndContentFacts(DoctorCompleteness.Unbound, ContentCaptureStatus.Enabled, RawAccessStatus.Available) },
            baseline with { ExactSessionBinding = new ExactSessionBindingFacts(ExactSessionBindingRequirement.Required, ExactSessionBindingOutcome.Unbound), CompletenessAndContent = new CompletenessAndContentFacts(DoctorCompleteness.Full, ContentCaptureStatus.Enabled, RawAccessStatus.Available) },
        };

        foreach (var snapshot in cases)
        {
            AssertInvalidInput(snapshot);
        }
    }

    [Fact]
    public void Evaluate_InvalidObservationClassKindAndFamilyCombinations_ReturnsFixedInvalidInput()
    {
        var baseline = DoctorTestSnapshots.ReadyNoRealTrace();
        var cases = new DoctorFactSnapshot[]
        {
            baseline with { Observations = [DoctorTestSnapshots.Observation(DoctorEvidenceKind.ExactSessionBinding, "probe-binding", DoctorEvidenceClass.SyntheticProbe)] },
            baseline with { Observations = [DoctorTestSnapshots.Observation(DoctorEvidenceKind.CompletenessContent, "probe-content", DoctorEvidenceClass.SyntheticProbe)] },
            baseline with { LastIngest = null, Observations = [DoctorTestSnapshots.Observation(DoctorEvidenceKind.Ingest, "event-ingest")] },
            baseline with { RawPersistence = null, Observations = [DoctorTestSnapshots.Observation(DoctorEvidenceKind.RawPersistence, "event-raw")] },
            baseline with { Projection = null, Observations = [DoctorTestSnapshots.Observation(DoctorEvidenceKind.Projection, "event-projection")] },
            baseline with { ExactSessionBinding = null, Observations = [DoctorTestSnapshots.Observation(DoctorEvidenceKind.ExactSessionBinding, "event-binding")] },
            baseline with { CompletenessAndContent = null, Observations = [DoctorTestSnapshots.Observation(DoctorEvidenceKind.CompletenessContent, "event-content")] },
        };

        foreach (var snapshot in cases)
        {
            AssertInvalidInput(snapshot);
        }
    }

    public static TheoryData<string> UnsafeEvidenceReferences => new()
    {
        "user@example.com",
        "Bearer abc123",
        "Basic abc123",
        "Authorization: hidden",
        "api_key=hidden",
        "secret-value",
        "password=value",
        "prompt: raw",
        "response: raw",
        "content: raw",
        "tool argument raw",
        "tool result raw",
        "https://example.test/trace",
        @"C:\Users\person\trace.json",
        "C:/Users/person/trace.json",
        @"\\server\share\trace.json",
        "../trace.json",
        "/home/person/trace.json",
    };

    [Theory]
    [MemberData(nameof(UnsafeEvidenceReferences))]
    public void Evaluate_UnsafeEvidenceReference_ReturnsFixedInvalidInputWithoutEcho(string evidenceRef)
    {
        var snapshot = DoctorTestSnapshots.ReadyNoRealTrace() with
        {
            Observations = [DoctorTestSnapshots.Observation(DoctorEvidenceKind.Ingest, evidenceRef)],
        };

        var result = DoctorEvaluator.Evaluate(snapshot);
        var json = DoctorJson.SerializeResult(result);

        Assert.Equal(DoctorResultCode.InvalidInput, result.Code);
        Assert.DoesNotContain(evidenceRef, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_AcceptedReferencesAndCandidateShape_EnforceSharedBounds()
    {
        var references = Enumerable.Range(0, DoctorValidation.MaximumAcceptedEvidenceReferences)
            .Select(index => $"event-{index}")
            .ToArray();
        var candidate = new DoctorEvidenceCandidate(
            "018f3f4d-7a3b-7c11-8e21-123456789abc",
            "018f3f4d-7a3b-7c11-8e21-123456789abd",
            "github-copilot-vscode",
            SourceAdapter: null,
            DoctorEvidenceClass.RealSource,
            DoctorEvidenceKind.Ingest,
            "event-ingest",
            DoctorTestSnapshots.ObservedAt,
            DoctorTestSnapshots.ObservedAt.AddMinutes(5));

        Assert.True(DoctorValidation.AreValidAcceptedEvidenceReferences(references, allowEmpty: false));
        Assert.False(DoctorValidation.AreValidAcceptedEvidenceReferences(references.Append("event-16").ToArray(), allowEmpty: false));
        Assert.False(DoctorValidation.AreValidAcceptedEvidenceReferences(["event-1", "event-1"], allowEmpty: false));
        Assert.False(DoctorValidation.AreValidAcceptedEvidenceReferences([], allowEmpty: false));
        Assert.True(DoctorValidation.IsValidEvidenceCandidate(candidate));
        Assert.Equal(100, DoctorValidation.MaximumEvidenceCandidates);
    }

    [Fact]
    public void Validation_VerificationState_EnforcesWindowTerminalTimestampAndAcceptedEvidenceShape()
    {
        var active = new DoctorVerification(
            "018f3f4d-7a3b-7c11-8e21-123456789abc",
            "github-copilot-vscode",
            ExpectedSourceAdapter: null,
            DoctorVerificationState.Active,
            Revision: 1,
            DoctorTestSnapshots.ObservedAt,
            DoctorTestSnapshots.ObservedAt.AddMinutes(5),
            CompletedAt: null,
            CancelledAt: null,
            AcceptedEvidenceRefs: []);
        var completed = active with
        {
            State = DoctorVerificationState.Completed,
            Revision = 2,
            CompletedAt = DoctorTestSnapshots.ObservedAt.AddMinutes(1),
            AcceptedEvidenceRefs = ["event-ingest"],
        };

        Assert.True(DoctorValidation.IsValidVerification(active));
        Assert.True(DoctorValidation.IsValidVerification(completed));
        Assert.False(DoctorValidation.IsValidVerification(completed with { AcceptedEvidenceRefs = [] }));
        Assert.False(DoctorValidation.IsValidVerification(active with { AcceptedEvidenceRefs = ["event-ingest"] }));
        Assert.False(DoctorValidation.IsValidVerification(completed with { CompletedAt = completed.ExpiresAt.AddTicks(1) }));
        Assert.False(DoctorValidation.IsValidVerification(active with { ExpiresAt = active.StartedAt.AddSeconds(59) }));
        Assert.False(DoctorValidation.IsValidVerification(active with { ExpiresAt = active.StartedAt.AddMinutes(30).AddTicks(1) }));
    }

    [Fact]
    public void DeserializeFactSnapshot_DuplicateUnknownIntegerAndNoncanonicalValues_AreRejectedWithSanitizedError()
    {
        var validJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "monitor-not-running.facts.json"));
        var cases = new[]
        {
            validJson.Replace("\"schema_version\":", "\"schema_version\":\"doctor.facts.v1\",\"schema_version\":", StringComparison.Ordinal),
            validJson.Replace("\"monitor_install\": \"installed\"", "\"monitor_install\": \"installed\",\"monitor_install\":\"not_installed\"", StringComparison.Ordinal),
            validJson.Replace("\"source_surface\":", "\"unknown_property\":true,\"source_surface\":", StringComparison.Ordinal),
            validJson.Replace("\"monitor_install\": \"installed\"", "\"monitor_install\":1", StringComparison.Ordinal),
            validJson.Replace("\"monitor_install\": \"installed\"", "\"monitor_install\":\"Installed\"", StringComparison.Ordinal),
            validJson.Replace("2026-07-16T01:02:03.0000000Z", "2026-07-16T10:02:03.0000000+09:00", StringComparison.Ordinal),
            validJson.Replace("2026-07-16T01:02:03.0000000Z", "2026-07-16T01:02:03Z", StringComparison.Ordinal),
            validJson.Replace("\"observed_at\": \"2026-07-16T01:02:03.0000000Z\"", "\"observed_at\":123", StringComparison.Ordinal),
            "{\"schema_version\":\"doctor.facts.v1\",\"source_surface\":\"secret-value",
        };

        foreach (var json in cases)
        {
            var exception = Assert.Throws<JsonException>(() => DoctorJson.DeserializeFactSnapshot(json));
            Assert.Equal("invalid_input", exception.Message);
            Assert.DoesNotContain("secret-value", exception.ToString(), StringComparison.Ordinal);
        }
    }

    private static void AssertInvalidInput(DoctorFactSnapshot snapshot)
    {
        var result = DoctorEvaluator.Evaluate(snapshot);
        Assert.False(result.Success);
        Assert.Equal(DoctorResultCode.InvalidInput, result.Code);
        Assert.Null(result.Evaluation);
        Assert.Null(result.Verification);
    }
}
