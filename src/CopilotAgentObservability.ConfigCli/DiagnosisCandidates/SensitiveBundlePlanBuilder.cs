using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.ConfigCli;

internal sealed class SensitiveBundlePlanCandidate
{
    internal SensitiveBundlePlanCandidate(string candidateId, string? traceId, IReadOnlyList<RawEvidenceFragment> fragments) =>
        (CandidateId, TraceId, Fragments) = (candidateId, traceId, fragments?.ToArray()!);

    internal string CandidateId { get; }
    internal string? TraceId { get; }
    internal IReadOnlyList<RawEvidenceFragment> Fragments { get; }
    public override string ToString() => GetType().FullName!;
}

internal sealed class SensitiveBundlePlanEntry
{
    internal SensitiveBundlePlanEntry(string evidenceRef, string evidenceFile, IReadOnlyList<string> contentKinds, int fragmentCount) =>
        (EvidenceRef, EvidenceFile, ContentKinds, FragmentCount) = (evidenceRef, evidenceFile, contentKinds.ToArray(), fragmentCount);

    internal string EvidenceRef { get; }
    internal string EvidenceFile { get; }
    internal IReadOnlyList<string> ContentKinds { get; }
    internal int FragmentCount { get; }
    public override string ToString() => GetType().FullName!;
}

internal sealed class SensitiveBundlePlannedMember
{
    private readonly byte[] utf8;
    private readonly byte[]? sha256;

    internal SensitiveBundlePlannedMember(int ordinal, string relativePath, RetentionFileCaptureMemberKind kind, byte[] utf8, int deletionOrder)
    {
        Ordinal = ordinal;
        RelativePath = relativePath;
        Kind = kind;
        this.utf8 = utf8.ToArray();
        ByteLength = kind == RetentionFileCaptureMemberKind.File ? this.utf8.Length : null;
        sha256 = kind == RetentionFileCaptureMemberKind.File ? SHA256.HashData(this.utf8) : null;
        DeletionOrder = deletionOrder;
    }

    internal int Ordinal { get; }
    internal string RelativePath { get; }
    internal RetentionFileCaptureMemberKind Kind { get; }
    internal byte[] Utf8 => utf8.ToArray();
    internal long? ByteLength { get; }
    internal byte[]? Sha256 => sha256?.ToArray();
    internal int DeletionOrder { get; }
    internal RetentionFileCaptureMember ToCaptureMember() => new(Ordinal, RelativePath, Kind, ByteLength, sha256, DeletionOrder);
    public override string ToString() => GetType().FullName!;
}

internal sealed class SensitiveBundlePlan
{
    private readonly byte[] manifestUtf8;
    private readonly byte[] markerSha256;
    private readonly byte[] manifestSha256;
    internal SensitiveBundlePlan(byte[] manifestUtf8, IReadOnlyList<SensitiveBundlePlannedMember> members, IReadOnlyDictionary<string, SensitiveBundlePlanEntry> entriesByCandidateId)
    {
        this.manifestUtf8 = manifestUtf8.ToArray();
        Members = Array.AsReadOnly(members.ToArray());
        EntriesByCandidateId = new System.Collections.ObjectModel.ReadOnlyDictionary<string, SensitiveBundlePlanEntry>(new Dictionary<string, SensitiveBundlePlanEntry>(entriesByCandidateId, StringComparer.Ordinal));
        markerSha256 = SHA256.HashData(Members[0].Utf8);
        manifestSha256 = SHA256.HashData(this.manifestUtf8);
    }

    internal byte[] ManifestUtf8 => manifestUtf8.ToArray();
    internal byte[] MarkerSha256 => markerSha256.ToArray();
    internal byte[] ManifestSha256 => manifestSha256.ToArray();
    internal IReadOnlyList<SensitiveBundlePlannedMember> Members { get; }
    internal IReadOnlyDictionary<string, SensitiveBundlePlanEntry> EntriesByCandidateId { get; }
    internal RetentionFileCapturePlanInput ToCapturePlanInput() => new(markerSha256, manifestSha256, Members.Select(static member => member.ToCaptureMember()).ToArray());
    public override string ToString() => GetType().FullName!;
}

internal sealed class SensitiveBundlePlanLimitException : ArgumentException
{
    internal SensitiveBundlePlanLimitException() : base("Sensitive bundle plan exceeds retention limits.") { }
}

internal static class SensitiveBundlePlanBuilder
{
    private const string InvalidInputMessage = "Invalid sensitive bundle plan input.";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static SensitiveBundlePlan Build(
        RetentionFileCaptureReservation reservation,
        IReadOnlyList<SensitiveBundlePlanCandidate> candidates,
        IReadOnlyList<SensitiveBundleSourceInput> sourceInputs)
    {
        if (reservation is null || candidates is null || sourceInputs is null || reservation.Phase != RetentionCapturePhase.Reserved)
        {
            throw Invalid();
        }

        if (sourceInputs.Any(static source => source is null || string.IsNullOrWhiteSpace(source.Kind) || !IsSha256(source.Sha256)))
        {
            throw Invalid();
        }

        var orderedCandidates = candidates.OrderBy(static candidate => candidate.CandidateId, StringComparer.Ordinal).ToArray();
        if (orderedCandidates.Any(static candidate => candidate is null || !IsCandidateId(candidate.CandidateId) || candidate.Fragments is null)
            || orderedCandidates.Select(static candidate => candidate.CandidateId).Distinct(StringComparer.Ordinal).Count() != orderedCandidates.Length)
        {
            throw Invalid();
        }

        var evidenceCandidates = orderedCandidates.Where(static candidate => candidate.Fragments.Count > 0).ToArray();
        if (evidenceCandidates.Length == 0)
        {
            throw Invalid();
        }
        var evidence = new List<(SensitiveBundlePlanCandidate Candidate, byte[] Utf8, SensitiveBundlePlanEntry Entry)>();
        foreach (var candidate in evidenceCandidates)
        {
            if (candidate.Fragments.Any(static fragment => fragment is null
                || string.IsNullOrWhiteSpace(fragment.ContentKind)
                || fragment.SourceLocator is null
                || fragment.SourcePath is null
                || fragment.Value is null)) throw Invalid();
            var evidenceRef = $"bundle:{reservation.CaptureId}:{candidate.CandidateId}";
            var file = $"evidence/{candidate.CandidateId}.json";
            var kinds = candidate.Fragments.Select(static fragment => fragment.ContentKind).Distinct(StringComparer.Ordinal).ToArray();
            evidence.Add((candidate, WriteEvidence(evidenceRef, candidate, kinds), new SensitiveBundlePlanEntry(evidenceRef, file, kinds, candidate.Fragments.Count)));
        }

        var manifest = WriteManifest(reservation, sourceInputs.OrderBy(static source => source.Kind, StringComparer.Ordinal).ThenBy(static source => source.Sha256, StringComparer.Ordinal).ToArray(), evidence);
        byte[] marker;
        try
        {
            marker = RetentionFileCaptureOwnershipMarker.Create(reservation.StoreInstanceId, reservation.CaptureId, reservation.ReservedAtText, reservation.ReservedAtTicks, reservation.OwnerToken);
        }
        catch (ArgumentException)
        {
            throw Invalid();
        }
        var members = new List<SensitiveBundlePlannedMember>
        {
            new(0, RetentionFileCaptureContracts.OwnerMarkerName, RetentionFileCaptureMemberKind.OwnerMarker, marker, evidence.Count + 2),
            new(1, "evidence", RetentionFileCaptureMemberKind.Directory, [], evidence.Count + 1)
        };
        for (var index = 0; index < evidence.Count; index++)
        {
            members.Add(new(index + 2, evidence[index].Entry.EvidenceFile, RetentionFileCaptureMemberKind.File, evidence[index].Utf8, index));
        }

        members.Add(new(members.Count, "manifest.json", RetentionFileCaptureMemberKind.File, manifest, evidence.Count));
        if (members.Count > RetentionFileCaptureContracts.MaximumMemberCount || members.Where(static member => member.ByteLength.HasValue).Sum(static member => member.ByteLength!.Value) > RetentionFileCaptureContracts.MaximumMemberBytes)
        {
            throw new SensitiveBundlePlanLimitException();
        }

        return new SensitiveBundlePlan(manifest, members, evidence.ToDictionary(static item => item.Candidate.CandidateId, static item => item.Entry, StringComparer.Ordinal));
    }

    private static byte[] WriteManifest(RetentionFileCaptureReservation reservation, IReadOnlyList<SensitiveBundleSourceInput> sources, IReadOnlyList<(SensitiveBundlePlanCandidate Candidate, byte[] Utf8, SensitiveBundlePlanEntry Entry)> evidence)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("bundle_id", reservation.CaptureId);
            writer.WriteString("reserved_at_utc", reservation.ReservedAtText);
            writer.WriteString("expires_at_utc", DateTimeOffset.ParseExact(reservation.ReservedAtText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None).AddDays(7).ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("generated_by_command", "generate-diagnosis-candidates");
            writer.WritePropertyName("source_inputs"); writer.WriteStartArray();
            foreach (var source in sources)
            {
                writer.WriteStartObject(); writer.WriteString("kind", source.Kind); writer.WriteString("sha256", source.Sha256); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteBoolean("content_included", true);
            writer.WritePropertyName("evidence_index"); writer.WriteStartArray();
            foreach (var item in evidence)
            {
                writer.WriteStartObject(); writer.WriteString("evidence_ref", item.Entry.EvidenceRef); writer.WriteString("diagnosis_candidate_id", item.Candidate.CandidateId); writer.WriteString("evidence_file", item.Entry.EvidenceFile);
                writer.WritePropertyName("content_kinds"); writer.WriteStartArray(); foreach (var kind in item.Entry.ContentKinds) writer.WriteStringValue(kind); writer.WriteEndArray();
                writer.WriteNumber("fragment_count", item.Entry.FragmentCount); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
        }
        return stream.ToArray().Concat("\n"u8.ToArray()).ToArray();
    }

    private static byte[] WriteEvidence(string evidenceRef, SensitiveBundlePlanCandidate candidate, IReadOnlyList<string> kinds)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject(); writer.WriteNumber("schema_version", 1); writer.WriteString("evidence_ref", evidenceRef); writer.WriteString("diagnosis_candidate_id", candidate.CandidateId);
            if (candidate.TraceId is null) writer.WriteNull("trace_id"); else writer.WriteString("trace_id", candidate.TraceId);
            writer.WriteString("source_locator", candidate.Fragments[0].SourceLocator);
            writer.WritePropertyName("fragments"); writer.WriteStartArray();
            for (var index = 0; index < candidate.Fragments.Count; index++)
            {
                var fragment = candidate.Fragments[index];
                writer.WriteStartObject(); writer.WriteString("fragment_id", $"fragment-{index + 1:0000}"); writer.WriteString("content_kind", fragment.ContentKind); writer.WriteString("source_path", fragment.SourcePath); writer.WriteNumber("sequence", index + 1); writer.WriteString("value", fragment.Value); writer.WriteString("sha256", Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(fragment.Value))).ToLowerInvariant()); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
        }
        return stream.ToArray().Concat("\n"u8.ToArray()).ToArray();
    }

    private static bool IsCandidateId(string? value) => value is { Length: > 0 and <= 64 } && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static ArgumentException Invalid() => new(InvalidInputMessage);
}
