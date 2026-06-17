namespace CopilotAgentObservability.ConfigCli;

internal sealed record SensitiveBundleWriteResult(
    string BundleId,
    string BundlePath,
    IReadOnlyDictionary<string, SensitiveBundleEvidenceEntry> EntriesByCandidateId);

internal sealed record SensitiveBundleEvidenceEntry(
    string EvidenceRef,
    string EvidenceFile,
    string SourceLocator,
    IReadOnlyList<string> ContentKinds,
    int FragmentCount);

internal static class SensitiveBundleWriter
{
    public static SensitiveBundleWriteResult Write(
        IReadOnlyList<(string CandidateId, string? TraceId, IReadOnlyList<RawEvidenceFragment> Fragments)> candidates,
        IReadOnlyList<SensitiveBundleSourceInput> sourceInputs,
        string? requestedOutputDir,
        DateTimeOffset now)
    {
        var runId = $"{now.UtcDateTime:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"[..25];
        var bundleRoot = requestedOutputDir ?? Path.Combine("tmp", "sprint3-sensitive", $"{runId}-diagcand");
        Directory.CreateDirectory(Path.Combine(bundleRoot, "evidence"));

        var bundleId = Path.GetFileName(Path.GetFullPath(bundleRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var entries = new Dictionary<string, SensitiveBundleEvidenceEntry>(StringComparer.Ordinal);
        var evidenceIndex = new JsonArray();

        foreach (var candidate in candidates)
        {
            if (candidate.Fragments.Count == 0)
            {
                continue;
            }

            var evidenceRef = $"bundle:{bundleId}:{candidate.CandidateId}";
            var evidenceFile = Path.Combine("evidence", $"{candidate.CandidateId}.json").Replace('\\', '/');
            var sourceLocator = candidate.Fragments[0].SourceLocator;
            var contentKinds = candidate.Fragments.Select(fragment => fragment.ContentKind).Distinct(StringComparer.Ordinal).ToArray();

            var fragments = new JsonArray();
            for (var index = 0; index < candidate.Fragments.Count; index++)
            {
                var fragment = candidate.Fragments[index];
                fragments.Add(new JsonObject
                {
                    ["fragment_id"] = $"fragment-{index + 1:0000}",
                    ["content_kind"] = fragment.ContentKind,
                    ["source_path"] = fragment.SourcePath,
                    ["sequence"] = index + 1,
                    ["value"] = fragment.Value,
                    ["sha256"] = ComputeSha256(fragment.Value),
                });
            }

            var evidenceJson = new JsonObject
            {
                ["schema_version"] = 1,
                ["evidence_ref"] = evidenceRef,
                ["diagnosis_candidate_id"] = candidate.CandidateId,
                ["trace_id"] = candidate.TraceId,
                ["source_locator"] = sourceLocator,
                ["fragments"] = fragments,
            };
            File.WriteAllText(Path.Combine(bundleRoot, evidenceFile), JsonOutput.WriteIndented(evidenceJson), Encoding.UTF8);

            var contentKindsJson = new JsonArray();
            foreach (var contentKind in contentKinds)
            {
                contentKindsJson.Add(contentKind);
            }

            evidenceIndex.Add(new JsonObject
            {
                ["evidence_ref"] = evidenceRef,
                ["diagnosis_candidate_id"] = candidate.CandidateId,
                ["trace_id"] = candidate.TraceId,
                ["source_locator"] = sourceLocator,
                ["evidence_file"] = evidenceFile,
                ["content_kinds"] = contentKindsJson,
                ["fragment_count"] = candidate.Fragments.Count,
            });

            entries[candidate.CandidateId] = new SensitiveBundleEvidenceEntry(
                evidenceRef,
                evidenceFile,
                sourceLocator,
                contentKinds,
                candidate.Fragments.Count);
        }

        var sourceInputsJson = new JsonArray();
        foreach (var sourceInput in sourceInputs)
        {
            sourceInputsJson.Add(new JsonObject
            {
                ["path"] = sourceInput.Path,
                ["sha256"] = sourceInput.Sha256,
                ["kind"] = sourceInput.Kind,
            });
        }

        var createdAt = now.ToUniversalTime();
        var manifest = new JsonObject
        {
            ["schema_version"] = 1,
            ["bundle_id"] = bundleId,
            ["created_at_utc"] = createdAt.ToString("O", CultureInfo.InvariantCulture),
            ["expires_at_utc"] = createdAt.AddDays(7).ToString("O", CultureInfo.InvariantCulture),
            ["generated_by_command"] = "generate-diagnosis-candidates",
            ["source_inputs"] = sourceInputsJson,
            ["content_included"] = true,
            ["delete_target_paths"] = new JsonArray(Path.GetFullPath(bundleRoot)),
            ["evidence_index"] = evidenceIndex,
        };
        File.WriteAllText(Path.Combine(bundleRoot, "manifest.json"), JsonOutput.WriteIndented(manifest), Encoding.UTF8);

        return new SensitiveBundleWriteResult(bundleId, bundleRoot, entries);
    }

    private static string ComputeSha256(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
