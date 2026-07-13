namespace CopilotAgentObservability.Telemetry;

public sealed class VerifiedSourceFingerprintEvidence
{
    private VerifiedSourceFingerprintEvidence(string sourceSurface, string sourceApplicationVersion, string schemaFingerprint)
    {
        SourceSurface = sourceSurface;
        SourceApplicationVersion = sourceApplicationVersion;
        SchemaFingerprint = schemaFingerprint;
    }

    public string SourceSurface { get; }
    public string SourceApplicationVersion { get; }
    public string SchemaFingerprint { get; }

    public static VerifiedSourceFingerprintEvidence Create(
        string sourceSurface,
        string sourceApplicationVersion,
        string schemaFingerprint)
    {
        RegistryEvidenceValidation.ValidateSurfaceVersionFingerprint(
            sourceSurface, sourceApplicationVersion, schemaFingerprint);
        return new VerifiedSourceFingerprintEvidence(sourceSurface, sourceApplicationVersion, schemaFingerprint);
    }
}

public sealed class IncompatibleSourceVersionEvidence
{
    private IncompatibleSourceVersionEvidence(string sourceSurface, string sourceApplicationVersion)
    {
        SourceSurface = sourceSurface;
        SourceApplicationVersion = sourceApplicationVersion;
    }

    public string SourceSurface { get; }
    public string SourceApplicationVersion { get; }

    public static IncompatibleSourceVersionEvidence Create(string sourceSurface, string sourceApplicationVersion)
    {
        SourceMetadata.ValidateRequired(sourceSurface, nameof(sourceSurface));
        SourceMetadata.ValidateRequired(sourceApplicationVersion, nameof(sourceApplicationVersion));
        return new IncompatibleSourceVersionEvidence(sourceSurface, sourceApplicationVersion);
    }
}

public sealed class SourceRecognitionProfileEvidence
{
    private SourceRecognitionProfileEvidence(
        string sourceSurface,
        string sourceApplicationVersion,
        string schemaFingerprint,
        SourceOccurrenceCount expectedRecognizedCount)
    {
        SourceSurface = sourceSurface;
        SourceApplicationVersion = sourceApplicationVersion;
        SchemaFingerprint = schemaFingerprint;
        ExpectedRecognizedCount = expectedRecognizedCount;
    }

    public string SourceSurface { get; }
    public string SourceApplicationVersion { get; }
    public string SchemaFingerprint { get; }
    public SourceOccurrenceCount ExpectedRecognizedCount { get; }

    public static SourceRecognitionProfileEvidence Create(
        string sourceSurface,
        string sourceApplicationVersion,
        string schemaFingerprint,
        SourceOccurrenceCount expectedRecognizedCount)
    {
        RegistryEvidenceValidation.ValidateSurfaceVersionFingerprint(
            sourceSurface, sourceApplicationVersion, schemaFingerprint);
        ArgumentNullException.ThrowIfNull(expectedRecognizedCount);
        return new SourceRecognitionProfileEvidence(
            sourceSurface, sourceApplicationVersion, schemaFingerprint, expectedRecognizedCount);
    }
}

public sealed class VerifiedSourceFingerprintRegistry
{
    private readonly IReadOnlyList<VerifiedSourceFingerprintEvidence> fingerprints;
    private readonly IReadOnlyList<IncompatibleSourceVersionEvidence> incompatibleVersions;
    private readonly IReadOnlyList<SourceRecognitionProfileEvidence> recognitionProfiles;

    private VerifiedSourceFingerprintRegistry(
        VerifiedSourceFingerprintEvidence[] fingerprints,
        IncompatibleSourceVersionEvidence[] incompatibleVersions,
        SourceRecognitionProfileEvidence[] recognitionProfiles)
    {
        this.fingerprints = Array.AsReadOnly(fingerprints);
        this.incompatibleVersions = Array.AsReadOnly(incompatibleVersions);
        this.recognitionProfiles = Array.AsReadOnly(recognitionProfiles);
    }

    public static VerifiedSourceFingerprintRegistry Create(
        IEnumerable<VerifiedSourceFingerprintEvidence> fingerprints,
        IEnumerable<IncompatibleSourceVersionEvidence> incompatibleVersions,
        IEnumerable<SourceRecognitionProfileEvidence> recognitionProfiles)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        ArgumentNullException.ThrowIfNull(incompatibleVersions);
        ArgumentNullException.ThrowIfNull(recognitionProfiles);

        var fingerprintSnapshot = Snapshot(fingerprints, nameof(fingerprints));
        var incompatibleSnapshot = Snapshot(incompatibleVersions, nameof(incompatibleVersions));
        var profileSnapshot = Snapshot(recognitionProfiles, nameof(recognitionProfiles));

        RejectDuplicateOrConflictingFingerprints(fingerprintSnapshot);
        RejectDuplicateIncompatibleVersions(incompatibleSnapshot);
        RejectDuplicateOrConflictingProfiles(profileSnapshot);

        var verifiedVersionKeys = fingerprintSnapshot.Select(item => VersionKey(item.SourceSurface, item.SourceApplicationVersion))
            .ToHashSet(StringComparer.Ordinal);
        if (incompatibleSnapshot.Any(item => verifiedVersionKeys.Contains(VersionKey(item.SourceSurface, item.SourceApplicationVersion))))
        {
            throw new ArgumentException("A source version cannot be both verified and explicitly incompatible.");
        }

        var verifiedTriples = fingerprintSnapshot.Select(item => TripleKey(
            item.SourceSurface, item.SourceApplicationVersion, item.SchemaFingerprint)).ToHashSet(StringComparer.Ordinal);
        if (profileSnapshot.Any(item => !verifiedTriples.Contains(TripleKey(
            item.SourceSurface, item.SourceApplicationVersion, item.SchemaFingerprint))))
        {
            throw new ArgumentException("A recognition profile must reference exact verified fingerprint evidence.");
        }

        return new VerifiedSourceFingerprintRegistry(fingerprintSnapshot, incompatibleSnapshot, profileSnapshot);
    }

    public bool IsKnownFingerprint(string sourceSurface, string schemaFingerprint)
    {
        SourceMetadata.ValidateRequired(sourceSurface, nameof(sourceSurface));
        RegistryEvidenceValidation.ValidateFingerprint(schemaFingerprint, nameof(schemaFingerprint));
        return fingerprints.Any(item =>
            StringComparer.Ordinal.Equals(item.SourceSurface, sourceSurface) &&
            StringComparer.Ordinal.Equals(item.SchemaFingerprint, schemaFingerprint));
    }

    public bool IsExplicitlyIncompatible(string sourceSurface, string? sourceApplicationVersion)
    {
        SourceMetadata.ValidateRequired(sourceSurface, nameof(sourceSurface));
        SourceMetadata.ValidateOptional(sourceApplicationVersion, nameof(sourceApplicationVersion));
        return sourceApplicationVersion is not null && incompatibleVersions.Any(item =>
            StringComparer.Ordinal.Equals(item.SourceSurface, sourceSurface) &&
            StringComparer.Ordinal.Equals(item.SourceApplicationVersion, sourceApplicationVersion));
    }

    public bool TryGetRecognitionProfile(
        string sourceSurface,
        string? sourceApplicationVersion,
        string schemaFingerprint,
        out SourceRecognitionProfileEvidence profile)
    {
        SourceMetadata.ValidateRequired(sourceSurface, nameof(sourceSurface));
        SourceMetadata.ValidateOptional(sourceApplicationVersion, nameof(sourceApplicationVersion));
        RegistryEvidenceValidation.ValidateFingerprint(schemaFingerprint, nameof(schemaFingerprint));

        SourceRecognitionProfileEvidence? match = null;
        if (sourceApplicationVersion is not null)
        {
            match = recognitionProfiles.SingleOrDefault(item =>
                StringComparer.Ordinal.Equals(item.SourceSurface, sourceSurface) &&
                StringComparer.Ordinal.Equals(item.SourceApplicationVersion, sourceApplicationVersion));
        }
        match ??= recognitionProfiles
            .Where(item => StringComparer.Ordinal.Equals(item.SourceSurface, sourceSurface) &&
                StringComparer.Ordinal.Equals(item.SchemaFingerprint, schemaFingerprint))
            .OrderBy(item => item.SourceApplicationVersion, StringComparer.Ordinal)
            .FirstOrDefault();

        profile = match!;
        return match is not null;
    }

    private static T[] Snapshot<T>(IEnumerable<T> values, string parameterName) where T : class
    {
        var snapshot = values.ToArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException("Registry evidence cannot contain null entries.", parameterName);
        }
        return snapshot;
    }

    private static void RejectDuplicateOrConflictingFingerprints(VerifiedSourceFingerprintEvidence[] evidence)
    {
        foreach (var group in evidence.GroupBy(item => VersionKey(item.SourceSurface, item.SourceApplicationVersion), StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                throw new ArgumentException("Verified fingerprint evidence is duplicated or conflicting.");
            }
        }
    }

    private static void RejectDuplicateIncompatibleVersions(IncompatibleSourceVersionEvidence[] evidence)
    {
        if (evidence.GroupBy(item => VersionKey(item.SourceSurface, item.SourceApplicationVersion), StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Incompatible-version evidence is duplicated.");
        }
    }

    private static void RejectDuplicateOrConflictingProfiles(SourceRecognitionProfileEvidence[] evidence)
    {
        if (evidence.GroupBy(item => TripleKey(item.SourceSurface, item.SourceApplicationVersion, item.SchemaFingerprint), StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Recognition-profile evidence is duplicated or conflicting.");
        }
        if (evidence.GroupBy(item => FingerprintKey(item.SourceSurface, item.SchemaFingerprint), StringComparer.Ordinal)
            .Any(group => group.Select(item => item.ExpectedRecognizedCount.Value).Distinct().Count() > 1))
        {
            throw new ArgumentException("One source fingerprint cannot have conflicting recognition profiles.");
        }
    }

    private static string VersionKey(string surface, string version) => $"{surface.Length}:{surface}{version.Length}:{version}";
    private static string FingerprintKey(string surface, string fingerprint) =>
        $"{surface.Length}:{surface}{fingerprint.Length}:{fingerprint}";
    private static string TripleKey(string surface, string version, string fingerprint) =>
        $"{VersionKey(surface, version)}{fingerprint.Length}:{fingerprint}";
}

internal static class RegistryEvidenceValidation
{
    private static readonly Regex FingerprintPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public static void ValidateSurfaceVersionFingerprint(string surface, string version, string fingerprint)
    {
        SourceMetadata.ValidateRequired(surface, nameof(surface));
        SourceMetadata.ValidateRequired(version, nameof(version));
        ValidateFingerprint(fingerprint, nameof(fingerprint));
    }

    public static void ValidateFingerprint(string fingerprint, string parameterName)
    {
        if (fingerprint is null || !FingerprintPattern.IsMatch(fingerprint))
        {
            throw new ArgumentException("Schema fingerprint must be 64 lowercase hexadecimal characters.", parameterName);
        }
    }
}
