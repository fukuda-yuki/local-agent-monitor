using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;

internal sealed record GitHubCopilotNativeSessionSelection(string SourceSurface, string NativeSessionId);

internal sealed record GitHubCopilotDoctorEvidenceSelection(
    string VerificationId,
    string Target,
    long RawRecordId,
    GitHubCopilotNativeSessionSelection? NativeSession);

internal sealed record GitHubCopilotDoctorEvidenceResult(
    DoctorResult ObservationResult,
    DoctorFactSnapshot Snapshot,
    IReadOnlyList<DoctorEvidenceKind> ObservedKinds,
    IReadOnlyList<string> EvidenceRefs,
    bool SessionUnbound);

internal static class GitHubCopilotDoctorEvidenceAdapter
{
    private const string DoctorAdapter = "github-copilot-doctor";

    public static GitHubCopilotDoctorEvidenceResult Observe(
        string databasePath,
        TimeProvider timeProvider,
        GitHubCopilotDoctorEvidenceSelection selection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(selection);

        var partition = Partition.For(selection.Target);
        var service = SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(databasePath, timeProvider));
        var status = service.Status(selection.VerificationId);
        var empty = Empty(status, partition, selection.VerificationId, timeProvider.GetUtcNow());
        if (status.Code != DoctorResultCode.VerificationActive || status.Verification is not { } verification ||
            verification.State != DoctorVerificationState.Active ||
            !string.Equals(verification.ExpectedSourceSurface, partition.SourceSurface, StringComparison.Ordinal) ||
            !string.Equals(verification.ExpectedSourceAdapter, DoctorAdapter, StringComparison.Ordinal))
        {
            return empty;
        }

        var rawStore = new RawTelemetryStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        var raw = rawStore.GetRawRecordById(selection.RawRecordId);
        if (raw is null || raw.ReceivedAt < verification.StartedAt || raw.ReceivedAt >= verification.ExpiresAt ||
            !string.Equals(raw.Source, RawTelemetrySources.RawOtlp, StringComparison.Ordinal) ||
            !HasExactClientKind(raw.ResourceAttributesJson, partition.ClientKind))
        {
            return empty;
        }

        var compatibility = new SqliteSourceCompatibilityStore(
            databasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter).GetByRawRecordId(selection.RawRecordId);
        if (compatibility is null ||
            !string.Equals(compatibility.SourceSurface, RawTelemetrySources.RawOtlp, StringComparison.Ordinal) ||
            !string.Equals(compatibility.SourceAdapter, RawTelemetrySources.RawOtlp, StringComparison.Ordinal) ||
            compatibility.CompatibilityState == SourceCompatibilityState.AdapterFailure)
        {
            return empty;
        }

        var binding = ResolveBinding(databasePath, selection.NativeSession, partition, raw.TraceId);
        if (binding is not null && !WithinVerificationWindow(binding.ObservedAt, verification))
        {
            binding = null;
        }
        if (partition.RequiresBinding && binding is null)
        {
            return empty with { SessionUnbound = true };
        }

        var disposition = rawStore.GetProjectionDisposition(selection.RawRecordId);
        if (disposition is not null && !WithinVerificationWindow(disposition.UpdatedAt, verification))
        {
            disposition = null;
        }

        var evidence = new List<EvidenceDescriptor>
        {
            new(DoctorEvidenceKind.Ingest, raw.ReceivedAt, Identity: null),
            new(DoctorEvidenceKind.RawPersistence, raw.ReceivedAt, Identity: null),
        };
        if (disposition is not null)
        {
            evidence.Add(new(DoctorEvidenceKind.Projection, disposition.UpdatedAt, Identity: null));
        }
        if (binding is not null)
        {
            evidence.Add(new(DoctorEvidenceKind.ExactSessionBinding, binding.ObservedAt, binding.Identity));
            evidence.Add(new(DoctorEvidenceKind.CompletenessContent, binding.ObservedAt, binding.Identity));
        }

        var evidenceRefs = new List<string>(evidence.Count);
        var observationResult = status;
        foreach (var descriptor in evidence)
        {
            var evidenceRef = OpaqueReference(
                selection.VerificationId,
                selection.RawRecordId,
                descriptor.Kind,
                descriptor.Identity);
            evidenceRefs.Add(evidenceRef);
            if (CandidateExists(databasePath, selection.VerificationId, evidenceRef))
            {
                continue;
            }

            observationResult = service.ObserveCandidate(new DoctorEvidenceCandidate(
                Guid.CreateVersion7(descriptor.ObservedAt).ToString("D"),
                selection.VerificationId,
                partition.SourceSurface,
                DoctorAdapter,
                DoctorEvidenceClass.RealSource,
                descriptor.Kind,
                evidenceRef,
                descriptor.ObservedAt,
                verification.ExpiresAt));
            if (observationResult.Code != DoctorResultCode.VerificationActive)
            {
                if (CandidateExists(databasePath, selection.VerificationId, evidenceRef))
                {
                    observationResult = service.Status(selection.VerificationId);
                    continue;
                }
                return Empty(observationResult, partition, selection.VerificationId, raw.ReceivedAt);
            }
        }

        var snapshot = Snapshot(
            partition,
            selection.VerificationId,
            raw.ReceivedAt,
            compatibility,
            disposition,
            binding);
        return new(observationResult, snapshot, evidence.Select(item => item.Kind).ToArray(), evidenceRefs, binding is null);
    }

    private static DoctorFactSnapshot Snapshot(
        Partition partition,
        string verificationId,
        DateTimeOffset observedAt,
        SourceCompatibilityRow compatibility,
        ProjectionDisposition? disposition,
        BindingResolution? binding) => new(
        DoctorSchemaVersions.FactsV1,
        partition.SourceSurface,
        DoctorAdapter,
        observedAt,
        verificationId,
        [],
        null,
        null,
        null,
        null,
        null,
        CompatibilityFacts(compatibility),
        new LastIngestFacts(LastIngestOutcome.Accepted),
        new RawPersistenceFacts(RawPersistenceOutcome.Persisted),
        new ProjectionFacts(disposition?.State switch
        {
            ProjectionDispositionState.NotStarted => ProjectionOutcome.NotStarted,
            ProjectionDispositionState.Pending => ProjectionOutcome.Pending,
            ProjectionDispositionState.Completed => ProjectionOutcome.Completed,
            ProjectionDispositionState.Failed => ProjectionOutcome.Failed,
            _ => ProjectionOutcome.Unknown,
        }),
        new ExactSessionBindingFacts(
            ExactSessionBindingRequirement.Required,
            binding is null ? ExactSessionBindingOutcome.Unbound : ExactSessionBindingOutcome.ExactBound),
        binding is null
            ? new CompletenessAndContentFacts(DoctorCompleteness.Unbound, ContentCaptureStatus.Unknown, RawAccessStatus.Unknown)
            : ContentFacts(binding),
        null);

    private static GitHubCopilotDoctorEvidenceResult Empty(
        DoctorResult result,
        Partition partition,
        string verificationId,
        DateTimeOffset observedAt)
    {
        var snapshot = new DoctorFactSnapshot(
            DoctorSchemaVersions.FactsV1,
            partition.SourceSurface,
            DoctorAdapter,
            observedAt,
            verificationId,
            [],
            null,
            null,
            null,
            null,
            null,
            new SourceVersionAndSchemaDiagnosticsFacts(SourceCompatibilityStatus.Unknown, SchemaStatus.Unknown),
            new LastIngestFacts(LastIngestOutcome.Unknown),
            new RawPersistenceFacts(RawPersistenceOutcome.Unknown),
            new ProjectionFacts(ProjectionOutcome.Unknown),
            new ExactSessionBindingFacts(ExactSessionBindingRequirement.Unknown, ExactSessionBindingOutcome.Unknown),
            new CompletenessAndContentFacts(DoctorCompleteness.Unknown, ContentCaptureStatus.Unknown, RawAccessStatus.Unknown),
            null);
        return new(result, snapshot, [], [], SessionUnbound: false);
    }

    private static SourceVersionAndSchemaDiagnosticsFacts CompatibilityFacts(SourceCompatibilityRow row) => new(
        row.CompatibilityState switch
        {
            SourceCompatibilityState.Supported or SourceCompatibilityState.SupportedWithUnknownFields => SourceCompatibilityStatus.Supported,
            SourceCompatibilityState.UnsupportedSourceVersion => SourceCompatibilityStatus.UnsupportedSourceVersion,
            _ => SourceCompatibilityStatus.Unknown,
        },
        row.CompatibilityState switch
        {
            SourceCompatibilityState.Supported or SourceCompatibilityState.SupportedWithUnknownFields => SchemaStatus.Matching,
            SourceCompatibilityState.SchemaDriftDetected or SourceCompatibilityState.RecognizedRecordDropDetected => SchemaStatus.DriftDetected,
            _ => SchemaStatus.Unknown,
        });

    private static CompletenessAndContentFacts ContentFacts(BindingResolution binding) => new(
        binding.Detail.Session.Completeness switch
        {
            SessionCompleteness.Partial => DoctorCompleteness.Partial,
            SessionCompleteness.Rich => DoctorCompleteness.Rich,
            SessionCompleteness.Full => DoctorCompleteness.Full,
            _ => DoctorCompleteness.Unbound,
        },
        binding.Event.ContentState == SessionContentState.Available
            ? ContentCaptureStatus.Enabled
            : binding.Event.ContentState == SessionContentState.Unsupported
                ? ContentCaptureStatus.Unsupported
                : ContentCaptureStatus.Disabled,
        binding.Detail.Session.RawRetentionState == SessionRawRetentionState.Expiring
            ? RawAccessStatus.Available
            : RawAccessStatus.SanitizedOnly);

    private static BindingResolution? ResolveBinding(
        string databasePath,
        GitHubCopilotNativeSessionSelection? selection,
        Partition partition,
        string? traceId)
    {
        if (selection is null || string.IsNullOrWhiteSpace(traceId) ||
            !string.Equals(selection.SourceSurface, partition.NativeSurfaceWire, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(selection.NativeSessionId))
        {
            return null;
        }

        var store = new SqliteSessionStore(databasePath);
        var session = store.Resolve(partition.NativeSurface, selection.NativeSessionId);
        if (session is null || store.GetDetail(session.SessionId) is not { } detail)
        {
            return null;
        }

        var nativeIds = detail.NativeIds.Where(item => item.BindingKind == SessionBindingKind.Native &&
                item.SourceSurface == partition.NativeSurface &&
                string.Equals(item.NativeSessionId, selection.NativeSessionId, StringComparison.Ordinal)).ToArray();
        var runs = detail.Runs.Where(item => item.SourceSurface == partition.NativeSurface &&
            string.Equals(item.TraceId, traceId, StringComparison.Ordinal)).ToArray();
        if (nativeIds.Length != 1 || runs.Length != 1)
        {
            return null;
        }

        var run = runs[0];
        var events = detail.Events.Where(item => item.RunId == run.RunId &&
            item.SourceSurface == partition.NativeSurface &&
            string.Equals(item.TraceId, traceId, StringComparison.Ordinal) &&
            string.Equals(item.SourceAdapter, partition.EventAdapter, StringComparison.Ordinal)).ToArray();
        if (events.Length != 1)
        {
            return null;
        }

        var timestamps = new List<DateTimeOffset> { nativeIds[0].ObservedAt, events[0].OccurredAt };
        if (run.StartedAt is { } startedAt)
        {
            timestamps.Add(startedAt);
        }
        if (run.EndedAt is { } endedAt)
        {
            timestamps.Add(endedAt);
        }
        var identity = $"{selection.SourceSurface}|{selection.NativeSessionId}|{session.SessionId:D}";
        return new(detail, events[0], timestamps.Max(), identity);
    }

    private static bool WithinVerificationWindow(DateTimeOffset observedAt, DoctorVerification verification) =>
        observedAt >= verification.StartedAt && observedAt < verification.ExpiresAt;

    private static bool HasExactClientKind(string? attributesJson, string expected)
    {
        if (attributesJson is null)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(attributesJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("client.kind", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), expected, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string OpaqueReference(
        string verificationId,
        long rawRecordId,
        DoctorEvidenceKind kind,
        string? identity)
    {
        var hash = Hash(verificationId, rawRecordId, kind, identity);
        return $"gc_doctor_{hash[..8]}_{hash[8..16]}_{hash[16..24]}_{hash[24..32]}_{hash[32..40]}";
    }

    private static string Hash(string verificationId, long rawRecordId, DoctorEvidenceKind kind, string? identity) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"issue103|{verificationId}|{rawRecordId}|{kind}|{identity}")));

    private static bool CandidateExists(string databasePath, string verificationId, string evidenceRef)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM doctor_verification_evidence WHERE verification_id = $verification_id AND evidence_ref = $evidence_ref);";
        command.Parameters.AddWithValue("$verification_id", verificationId);
        command.Parameters.AddWithValue("$evidence_ref", evidenceRef);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private sealed record EvidenceDescriptor(
        DoctorEvidenceKind Kind,
        DateTimeOffset ObservedAt,
        string? Identity);

    private sealed record BindingResolution(
        SessionDetail Detail,
        ObservedSessionEvent Event,
        DateTimeOffset ObservedAt,
        string Identity);

    private sealed record Partition(
        string SourceSurface,
        string ClientKind,
        SessionSourceSurface NativeSurface,
        string NativeSurfaceWire,
        string EventAdapter,
        bool RequiresBinding)
    {
        public static Partition For(string target) => target switch
        {
            "vscode" => new("github-copilot-vscode", "vscode-copilot-chat", SessionSourceSurface.VisualStudioCode, "vscode", "copilot-compatible-hook", false),
            "cli" => new("github-copilot-cli", "copilot-cli", SessionSourceSurface.CopilotCli, "copilot-cli", "copilot-compatible-hook", false),
            "app-sdk" => new("github-copilot-app-sdk", "copilot-app-sdk", SessionSourceSurface.CopilotSdk, "copilot-sdk", "copilot-sdk-stream", true),
            _ => throw new ArgumentException("Unsupported GitHub Copilot Doctor target.", nameof(target)),
        };
    }
}
