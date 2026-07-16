using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Doctor;

public static class DoctorValidation
{
    public const int MaximumSourceTokenLength = 64;
    public const int MaximumEvidenceReferenceLength = 128;
    public const int MaximumObservations = 16;
    public const int MaximumAcceptedEvidenceReferences = 16;
    public const int MaximumEvidenceCandidates = 100;

    private static readonly Regex SourceTokenPattern = new(
        "^[a-z0-9][a-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant);

    private static readonly Regex UuidV7Pattern = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant);

    private static readonly Regex EmailPattern = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UriPattern = new(
        @"[a-z][a-z0-9+.-]*://",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsValidFactSnapshot(DoctorFactSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return IsSourceToken(snapshot.SourceSurface)
            && IsOptionalSourceToken(snapshot.ExpectedSourceAdapter)
            && IsCanonicalUtc(snapshot.ObservedAt)
            && (snapshot.VerificationId is null || IsUuidV7(snapshot.VerificationId))
            && AreValidEnums(snapshot)
            && AreValidCrossFields(snapshot)
            && AreValidObservations(snapshot);
    }

    public static bool AreValidAcceptedEvidenceReferences(
        IReadOnlyList<string>? evidenceReferences,
        bool allowEmpty)
    {
        if (evidenceReferences is null
            || evidenceReferences.Count > MaximumAcceptedEvidenceReferences
            || (!allowEmpty && evidenceReferences.Count == 0))
        {
            return false;
        }

        return evidenceReferences.All(IsValidEvidenceReference)
            && evidenceReferences.Distinct(StringComparer.Ordinal).Count() == evidenceReferences.Count;
    }

    public static bool IsValidEvidenceCandidate(DoctorEvidenceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return IsUuidV7(candidate.CandidateId)
            && IsUuidV7(candidate.VerificationId)
            && IsSourceToken(candidate.SourceSurface)
            && IsOptionalSourceToken(candidate.SourceAdapter)
            && Enum.IsDefined(candidate.EvidenceClass)
            && Enum.IsDefined(candidate.EvidenceKind)
            && IsValidEvidenceReference(candidate.EvidenceRef)
            && IsCanonicalUtc(candidate.ObservedAt)
            && IsCanonicalUtc(candidate.ExpiresAt)
            && candidate.ExpiresAt > candidate.ObservedAt
            && IsAllowedClassKind(candidate.EvidenceClass, candidate.EvidenceKind);
    }

    public static bool IsValidVerification(DoctorVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification);

        if (!IsUuidV7(verification.VerificationId)
            || !IsSourceToken(verification.ExpectedSourceSurface)
            || !IsOptionalSourceToken(verification.ExpectedSourceAdapter)
            || !Enum.IsDefined(verification.State)
            || verification.Revision <= 0
            || !IsCanonicalUtc(verification.StartedAt)
            || !IsCanonicalUtc(verification.ExpiresAt)
            || verification.ExpiresAt <= verification.StartedAt
            || verification.ExpiresAt - verification.StartedAt < TimeSpan.FromMinutes(1)
            || verification.ExpiresAt - verification.StartedAt > TimeSpan.FromMinutes(30)
            || !AreValidAcceptedEvidenceReferences(verification.AcceptedEvidenceRefs, allowEmpty: true))
        {
            return false;
        }

        return verification.State switch
        {
            DoctorVerificationState.Active or DoctorVerificationState.Expired =>
                verification.CompletedAt is null
                && verification.CancelledAt is null
                && verification.AcceptedEvidenceRefs.Count == 0,
            DoctorVerificationState.Completed =>
                IsTerminalTimestampInWindow(verification.CompletedAt, verification)
                && verification.CancelledAt is null
                && verification.AcceptedEvidenceRefs.Count > 0,
            DoctorVerificationState.Cancelled =>
                verification.CompletedAt is null
                && IsTerminalTimestampInWindow(verification.CancelledAt, verification)
                && verification.AcceptedEvidenceRefs.Count == 0,
            _ => false,
        };
    }

    public static bool IsValidEvidenceReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumEvidenceReferenceLength
            || value.Any(char.IsControl))
        {
            return false;
        }

        return !EmailPattern.IsMatch(value)
            && !UriPattern.IsMatch(value)
            && !value.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("Basic ", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("Authorization:", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("api_key", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("secret", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("password", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("credential", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("token", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("prompt:", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("response:", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("content:", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("tool argument", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("tool result", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(value, @"[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)
            && !value.StartsWith(@"\\", StringComparison.Ordinal)
            && !value.Contains("../", StringComparison.Ordinal)
            && !value.Contains(@"..\", StringComparison.Ordinal)
            && !value.StartsWith("/", StringComparison.Ordinal);
    }

    public static bool IsSourceToken(string? value) =>
        value is not null && SourceTokenPattern.IsMatch(value);

    public static bool IsUuidV7(string? value) =>
        value is not null && UuidV7Pattern.IsMatch(value);

    private static bool AreValidObservations(DoctorFactSnapshot snapshot)
    {
        if (snapshot.Observations is null
            || snapshot.Observations.Count > MaximumObservations)
        {
            return false;
        }

        var evidenceReferences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in snapshot.Observations)
        {
            if (observation is null
                || !string.Equals(observation.SourceSurface, snapshot.SourceSurface, StringComparison.Ordinal)
                || !IsOptionalSourceToken(observation.SourceAdapter)
                || (snapshot.ExpectedSourceAdapter is not null
                    && !string.Equals(observation.SourceAdapter, snapshot.ExpectedSourceAdapter, StringComparison.Ordinal))
                || !Enum.IsDefined(observation.EvidenceClass)
                || !Enum.IsDefined(observation.EvidenceKind)
                || !IsAllowedClassKind(observation.EvidenceClass, observation.EvidenceKind)
                || !IsValidEvidenceReference(observation.EvidenceRef)
                || !evidenceReferences.Add(observation.EvidenceRef)
                || !IsCanonicalUtc(observation.ObservedAt)
                || !HasObservationFamily(snapshot, observation.EvidenceKind))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasObservationFamily(DoctorFactSnapshot snapshot, DoctorEvidenceKind kind) => kind switch
    {
        DoctorEvidenceKind.Ingest => snapshot.LastIngest is not null,
        DoctorEvidenceKind.RawPersistence => snapshot.RawPersistence is not null,
        DoctorEvidenceKind.Projection => snapshot.Projection is not null,
        DoctorEvidenceKind.ExactSessionBinding => snapshot.ExactSessionBinding is not null,
        DoctorEvidenceKind.CompletenessContent => snapshot.CompletenessAndContent is not null,
        _ => false,
    };

    private static bool IsAllowedClassKind(DoctorEvidenceClass evidenceClass, DoctorEvidenceKind evidenceKind) =>
        evidenceClass == DoctorEvidenceClass.RealSource
        || evidenceKind is DoctorEvidenceKind.Ingest or DoctorEvidenceKind.RawPersistence or DoctorEvidenceKind.Projection;

    private static bool AreValidCrossFields(DoctorFactSnapshot snapshot)
    {
        var process = snapshot.ProcessReceiverAndPort;
        if (process is not null
            && ((process.MonitorProcess == MonitorProcessStatus.NotRunning && process.ReceiverBind == ReceiverBindStatus.Bound)
                || (process.ReceiverBind == ReceiverBindStatus.Bound && process.PortOwner is PortOwnerStatus.Foreign or PortOwnerStatus.None)
                || (process.ReceiverBind == ReceiverBindStatus.NotBound && process.PortOwner == PortOwnerStatus.Monitor)))
        {
            return false;
        }

        var binding = snapshot.ExactSessionBinding;
        if (binding is not null
            && !IsValidBindingCombination(binding.Requirement, binding.Outcome))
        {
            return false;
        }

        var completeness = snapshot.CompletenessAndContent?.Completeness;
        if (binding?.Requirement == ExactSessionBindingRequirement.Required
            && ((binding.Outcome == ExactSessionBindingOutcome.ExactBound && completeness == DoctorCompleteness.Unbound)
                || (binding.Outcome == ExactSessionBindingOutcome.Unbound
                    && completeness is DoctorCompleteness.Partial or DoctorCompleteness.Rich or DoctorCompleteness.Full)))
        {
            return false;
        }

        return true;
    }

    private static bool IsValidBindingCombination(
        ExactSessionBindingRequirement requirement,
        ExactSessionBindingOutcome outcome) =>
        outcome != ExactSessionBindingOutcome.NotApplicable
            || requirement == ExactSessionBindingRequirement.NotRequired;

    private static bool AreValidEnums(DoctorFactSnapshot snapshot) =>
        IsEnum(snapshot.InstallAndSourceVersion?.MonitorInstall)
        && IsEnum(snapshot.InstallAndSourceVersion?.SourceVersion)
        && IsEnum(snapshot.InstallAndSourceVersion?.SourceFeature)
        && IsEnum(snapshot.ProcessReceiverAndPort?.MonitorProcess)
        && IsEnum(snapshot.ProcessReceiverAndPort?.ReceiverBind)
        && IsEnum(snapshot.ProcessReceiverAndPort?.PortOwner)
        && IsEnum(snapshot.SourceEffectiveConfiguration?.EndpointAlignment)
        && IsEnum(snapshot.EndpointReachability?.Reachability)
        && IsEnum(snapshot.ProtocolAndSignalCompatibility?.Protocol)
        && IsEnum(snapshot.ProtocolAndSignalCompatibility?.TraceSignal)
        && IsEnum(snapshot.SourceVersionAndSchemaDiagnostics?.Compatibility)
        && IsEnum(snapshot.SourceVersionAndSchemaDiagnostics?.Schema)
        && IsEnum(snapshot.LastIngest?.Outcome)
        && IsEnum(snapshot.RawPersistence?.Outcome)
        && IsEnum(snapshot.Projection?.Outcome)
        && IsEnum(snapshot.ExactSessionBinding?.Requirement)
        && IsEnum(snapshot.ExactSessionBinding?.Outcome)
        && IsEnum(snapshot.CompletenessAndContent?.Completeness)
        && IsEnum(snapshot.CompletenessAndContent?.ContentCapture)
        && IsEnum(snapshot.CompletenessAndContent?.RawAccess)
        && IsEnum(snapshot.RestartOrNewProcess?.Requirement);

    private static bool IsOptionalSourceToken(string? value) => value is null || IsSourceToken(value);

    private static bool IsCanonicalUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static bool IsCanonicalUtc(DateTimeOffset? value) => value is not null && IsCanonicalUtc(value.Value);

    private static bool IsTerminalTimestampInWindow(
        DateTimeOffset? value,
        DoctorVerification verification) =>
        IsCanonicalUtc(value)
        && value >= verification.StartedAt
        && value <= verification.ExpiresAt;

    private static bool IsEnum<T>(T? value) where T : struct, Enum =>
        value is null || Enum.IsDefined(value.Value);
}
