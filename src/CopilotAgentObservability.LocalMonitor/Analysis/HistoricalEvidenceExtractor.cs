using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static partial class HistoricalEvidenceExtractorV1
{
    private const string ExtractionDomain = "copilot-agent-observability/historical-extraction/v1";
    private const string GroupDomain = "copilot-agent-observability/historical-evidence-group/v1";
    private static readonly string[] CompletenessReasonOrder =
    [
        "missing_native_session_id", "missing_trace_context", "trace_signal_disabled", "content_capture_disabled",
        "unsupported_source_version", "ingest_gap", "hook_only", "historical_summary_only", "unknown_span_kind",
        "schema_drift_detected", "planned_source_not_enabled",
    ];

    [GeneratedRegex(@"(?i)(?:[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9.-]+|[a-z]:[\\/][^\s]*|\\\\[^\s]+|(?<![a-z0-9])/[a-z0-9._~/-]+|github_pat_[a-z0-9_]{20,}|gh[pousr]_[a-z0-9]{20,}|glpat-[a-z0-9_-]{20,}|sk-[a-z0-9_-]{20,}|akia[a-z0-9]{16}|(?:authorization\s*:\s*bearer|bearer\s+)[a-z0-9._~+/-]{8,}|(?:password|passwd|api[_-]?key|token|secret)\s*[:=]\s*\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveDescriptorPattern();

    internal static async ValueTask<HistoricalEvidenceExtractionV1> ExtractAsync(
        HistoricalEvidenceSelectionV1 selection,
        IHistoricalEvidenceSnapshotSourceV1 source,
        CancellationToken cancellationToken)
    {
        ValidateSelection(selection);
        ArgumentNullException.ThrowIfNull(source);
        await using var snapshot = await source.OpenSnapshotAsync(selection, cancellationToken).ConfigureAwait(false);
        ValidateSnapshot(snapshot, selection);

        var ordered = snapshot.Sessions
            .OrderBy(item => item.StartedAt ?? item.LastSeenAt)
            .ThenBy(item => item.SessionId.ToString(), StringComparer.Ordinal)
            .ToArray();
        var excluded = new List<(Guid Id, HistoricalSessionExclusionReasonV1 Reason)>();
        var eligible = new List<HistoricalSessionMetadataV1>();
        foreach (var metadata in ordered)
        {
            var exclusion = Exclusion(selection, metadata);
            if (exclusion is { } reason)
            {
                if (reason == HistoricalSessionExclusionReasonV1.FilterMismatch
                    && !selection.ExplicitSessionIds.Contains(metadata.SessionId))
                    throw Invalid();
                excluded.Add((metadata.SessionId, reason));
            }
            else eligible.Add(metadata);
        }

        foreach (var missing in selection.ExplicitSessionIds
                     .Except(snapshot.Sessions.Select(item => item.SessionId))
                     .OrderBy(item => item.ToString(), StringComparer.Ordinal))
            excluded.Add((missing, HistoricalSessionExclusionReasonV1.MissingSessionReference));

        var returnedMatchingOverflow = Math.Max(0, eligible.Count - selection.MaximumSessionCount);
        long truncatedSessionCount;
        try { truncatedSessionCount = checked(snapshot.OmittedEarlierMatchingSessionCount + returnedMatchingOverflow); }
        catch (OverflowException exception) { throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidContract, exception); }
        var truncatedBefore = truncatedSessionCount > 0;
        if (returnedMatchingOverflow > 0)
        {
            foreach (var truncated in eligible.Take(returnedMatchingOverflow))
                excluded.Add((truncated.SessionId, HistoricalSessionExclusionReasonV1.WindowTruncated));
            eligible = eligible.TakeLast(selection.MaximumSessionCount).ToList();
        }
        excluded = excluded.OrderBy(item => item.Id.ToString(), StringComparer.Ordinal).ToList();

        var extractionId = "historical-extraction-" + ExtractionDigest(snapshot.SnapshotId, HistoricalEvidenceJsonV1.SerializeSelection(CanonicalInputSelection(selection)));
        var rawSessions = new List<HistoricalEvidenceSessionV1>();
        var safeSessions = new List<HistoricalEvidenceSessionV1>();
        var rawGroups = new List<HistoricalEvidenceGroupV1>();
        var safeGroups = new List<HistoricalEvidenceGroupV1>();
        foreach (var metadata in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var drafts = await snapshot.ReadEvidenceAsync(metadata.SessionId, !selection.SanitizedOnly, cancellationToken).ConfigureAwait(false);
            if (drafts.Count > HistoricalEvidenceContractsV1.MaximumGroupsPerSession)
                throw Invalid();

            var capabilities = EffectiveCapabilities(metadata);
            var descriptor = Descriptor(selection.SanitizedOnly, drafts);
            rawSessions.Add(ProjectSession(metadata, metadata.SessionId.ToString(), capabilities, descriptor.State, descriptor.Value));
            safeSessions.Add(ProjectSession(metadata, SafeSession(metadata.SessionId), capabilities, descriptor.State, null));

            var sessionRawGroups = new List<HistoricalEvidenceGroupV1>();
            var sessionSafeGroups = new List<HistoricalEvidenceGroupV1>();
            foreach (var draft in drafts)
            {
                ValidateDraft(metadata, draft);
                var canonicalReferences = draft.References
                    .Distinct()
                    .OrderBy(item => item, HistoricalRawReferenceComparer.Instance)
                    .ToArray();
                var groupId = GroupId(metadata.SessionId, draft, canonicalReferences);
                var rawGroup = ProjectGroup(groupId, draft, metadata.SessionId.ToString(), canonicalReferences, safe: false);
                var safeGroup = ProjectGroup(groupId, draft, SafeSession(metadata.SessionId), canonicalReferences, safe: true);
                AddGroup(sessionRawGroups, rawGroup);
                AddGroup(sessionSafeGroups, safeGroup);
            }
            sessionRawGroups.Sort(GroupComparer.Instance);
            sessionSafeGroups.Sort(GroupComparer.Instance);
            rawGroups.AddRange(sessionRawGroups);
            safeGroups.AddRange(sessionSafeGroups);
        }
        var rawExcluded = excluded.Select(item => new HistoricalExcludedSessionV1(item.Id.ToString(), item.Reason)).ToArray();
        var safeExcluded = excluded.Select(item => new HistoricalExcludedSessionV1(SafeSession(item.Id), item.Reason)).ToArray();
        var distribution = Distribution(rawSessions);
        var raw = new HistoricalEvidenceDatasetV1(
            HistoricalEvidenceContractsV1.RawLocalSchemaVersion, extractionId, snapshot.SnapshotId,
            HistoricalEvidenceRepresentationV1.RawLocal, ProjectSelection(selection, safe: false), truncatedBefore, truncatedSessionCount,
            rawSessions, rawExcluded, rawGroups, distribution);
        var safe = new HistoricalEvidenceDatasetV1(
            HistoricalEvidenceContractsV1.RepositorySafeSchemaVersion, extractionId, snapshot.SnapshotId,
            HistoricalEvidenceRepresentationV1.RepositorySafe, ProjectSelection(selection, safe: true), truncatedBefore, truncatedSessionCount,
            safeSessions, safeExcluded, safeGroups, distribution);
        var rawBytes = HistoricalEvidenceJsonV1.Serialize(raw);
        var safeBytes = HistoricalEvidenceJsonV1.Serialize(safe);
        return new(raw, safe, rawBytes, safeBytes, Sha256(rawBytes), Sha256(safeBytes));
    }

    internal static void ValidateDataset(HistoricalEvidenceDatasetV1 dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (!Enum.IsDefined(dataset.Representation)) throw Invalid();
        var raw = dataset.Representation == HistoricalEvidenceRepresentationV1.RawLocal;
        if (dataset.SchemaVersion != (raw ? HistoricalEvidenceContractsV1.RawLocalSchemaVersion : HistoricalEvidenceContractsV1.RepositorySafeSchemaVersion)
            || !ExtractionIdPattern().IsMatch(dataset.ExtractionId)
            || !SnapshotIdPattern().IsMatch(dataset.SnapshotId)
            || dataset.TruncatedSessionCount < 0
            || dataset.TruncatedBefore != (dataset.TruncatedSessionCount > 0)
            || dataset.Sessions.Count > HistoricalEvidenceContractsV1.MaximumSessions)
            throw Invalid();
        ValidateSelectionProjection(dataset.Selection, raw);
        var returnedWindowExclusionCount = dataset.ExcludedSessions.Count(item => item.Reason == HistoricalSessionExclusionReasonV1.WindowTruncated);
        if (dataset.Sessions.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != dataset.Sessions.Count
            || dataset.ExcludedSessions.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != dataset.ExcludedSessions.Count
            || dataset.ExcludedSessions.Count > HistoricalEvidenceContractsV1.MaximumSessions * 2 + 1
            || dataset.TruncatedBefore != (returnedWindowExclusionCount > 0)
            || dataset.TruncatedSessionCount < returnedWindowExclusionCount
            || raw && !dataset.ExcludedSessions.SequenceEqual(dataset.ExcludedSessions.OrderBy(item => item.SessionId, StringComparer.Ordinal))
            || dataset.EvidenceGroups.Count > HistoricalEvidenceContractsV1.MaximumSessions * HistoricalEvidenceContractsV1.MaximumGroupsPerSession
            || dataset.EvidenceGroups.Select(item => item.GroupId).Distinct(StringComparer.Ordinal).Count() != dataset.EvidenceGroups.Count)
            throw Invalid();
        var sessionsById = dataset.Sessions.ToDictionary(item => item.SessionId, StringComparer.Ordinal);
        var includedIds = sessionsById.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var session in dataset.Sessions)
        {
            if (!Enum.IsDefined(session.SourceSurface)
                || !Enum.IsDefined(session.Completeness)
                || !Enum.IsDefined(session.SourceKind)
                || !Enum.IsDefined(session.ContentState)
                || !Enum.IsDefined(session.DescriptorState)
                || raw && !IsCanonicalSessionId(session.SessionId)
                || !raw && !InstructionFindingReferenceTokenizationV1.IsSessionReference(session.SessionId)
                || !raw && session.RawLocalDescriptor is not null
                || session.RawLocalDescriptor is { } descriptor
                    && (string.IsNullOrWhiteSpace(descriptor)
                        || descriptor.EnumerateRunes().Count() > HistoricalEvidenceContractsV1.MaximumDescriptorLength
                        || descriptor.Any(char.IsControl)
                        || SensitiveDescriptorPattern().IsMatch(descriptor))
                || raw && (session.DescriptorState == HistoricalDescriptorStateV1.Available) != (session.RawLocalDescriptor is not null)
                || !SafeOptionalToken(session.SourceVersion)
                || !SafeOptionalToken(session.AdapterVersion)
                || session.Completeness == SessionCompleteness.Unbound
                || session.SourceKind == HistoricalEvidenceSourceKindV1.HistoricalSummary
                    && (session.Completeness != SessionCompleteness.Partial
                        || !session.CompletenessReasons.Contains("historical_summary_only", StringComparer.Ordinal)
                        || session.Capabilities.RepeatedToolCall
                        || session.Capabilities.SubagentFanOut
                        || session.Capabilities.RawLocalDescriptor)
                || !CanonicalReasons(session.CompletenessReasons))
                throw Invalid();
        }
        foreach (var excluded in dataset.ExcludedSessions)
            if (!Enum.IsDefined(excluded.Reason)
                || includedIds.Contains(excluded.SessionId)
                || raw && !IsCanonicalSessionId(excluded.SessionId)
                || !raw && !InstructionFindingReferenceTokenizationV1.IsSessionReference(excluded.SessionId)) throw Invalid();
        var sessionOrder = dataset.Sessions.Select((item, index) => (item.SessionId, index)).ToDictionary(item => item.SessionId, item => item.index, StringComparer.Ordinal);
        var canonicalGroups = dataset.EvidenceGroups.OrderBy(item => sessionOrder.GetValueOrDefault(item.SessionId, int.MaxValue)).ThenBy(item => item.Kind).ThenBy(item => item.GroupId, StringComparer.Ordinal).ToArray();
        if (!dataset.EvidenceGroups.SequenceEqual(canonicalGroups)) throw Invalid();
        foreach (var group in dataset.EvidenceGroups)
        {
            if (!Enum.IsDefined(group.Kind)) throw Invalid();
            if (includedIds.Contains(group.SessionId) && !Supports(sessionsById[group.SessionId].Capabilities, group.Kind))
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.MissingExactCapability);
            if (!GroupIdPattern().IsMatch(group.GroupId)
                || group.References.Count is < 1 or > HistoricalEvidenceContractsV1.MaximumReferencesPerGroup
                || group.SessionId != group.References[0].SessionId
                || !includedIds.Contains(group.SessionId)
                || raw && !group.References.SequenceEqual(group.References.OrderBy(item => item, HistoricalReferenceComparer.Instance))
                || raw && group.Kind == HistoricalEvidenceGroupKindV1.RepeatedToolCall && group.ExactCallId is null && !HashPattern().IsMatch(group.CanonicalCallHash ?? "")
                || raw && group.Kind == HistoricalEvidenceGroupKindV1.SubagentFanOut && group.ExactOwnershipId is null
                || group.Kind == HistoricalEvidenceGroupKindV1.InstructionFinding && !FindingIdPattern().IsMatch(group.FindingId ?? "")
                || group.CanonicalCallHash is not null && !HashPattern().IsMatch(group.CanonicalCallHash)
                || group.NumericValue < 0
                || !SafeOptionalToken(group.Unit)
                || !SafeOptionalToken(group.Status)
                || raw && group.ExactCallId is { } call && !SafeLocalCarrier(call)
                || raw && group.ExactOwnershipId is { } owner && !SafeLocalCarrier(owner)
                || !raw && (group.ExactCallId is not null || group.ExactOwnershipId is not null))
                throw Invalid();
            foreach (var reference in group.References)
            {
                if (!Enum.IsDefined(reference.RelativePosition)
                    || reference.SessionId != group.SessionId
                    || raw && (!IsCanonicalSessionId(reference.SessionId) || string.IsNullOrWhiteSpace(reference.TraceId))
                    || !raw && (!InstructionFindingReferenceTokenizationV1.IsSessionReference(reference.SessionId)
                        || !InstructionFindingReferenceTokenizationV1.IsTraceReference(reference.TraceId)
                        || reference.SpanId is not null && !InstructionFindingReferenceTokenizationV1.IsSpanReference(reference.SpanId)))
                    throw Invalid();
            }
            if (raw && group.GroupId != ComputeGroupId(group))
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidDerivedIdentity);
        }
        ValidateDistribution(dataset.Distribution, dataset.Sessions.Count);
        if (!DistributionEquals(dataset.Distribution, Distribution(dataset.Sessions)))
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidDerivedIdentity);
    }

    internal static string ComputeExtractionId(string snapshotId, HistoricalEvidenceSelectionProjectionV1 rawSelection)
    {
        if (!SnapshotIdPattern().IsMatch(snapshotId)) throw Invalid();
        var input = new HistoricalEvidenceSelectionV1(
            rawSelection.Repository, rawSelection.Workspace, rawSelection.From, rawSelection.To,
            rawSelection.ExplicitSessionIds.Select(Guid.Parse).ToArray(), rawSelection.SourceSurfaces,
            rawSelection.TaskLabel, rawSelection.ExperimentLabel, rawSelection.MaximumSessionCount, rawSelection.SanitizedOnly);
        ValidateSelection(input);
        return "historical-extraction-" + ExtractionDigest(snapshotId, HistoricalEvidenceJsonV1.SerializeSelection(CanonicalInputSelection(input)));
    }

    private static bool CanonicalReasons(IReadOnlyList<string> reasons)
    {
        try { return reasons.Distinct(StringComparer.Ordinal).SequenceEqual(reasons) && reasons.OrderBy(ReasonIndex).SequenceEqual(reasons); }
        catch (HistoricalEvidenceValidationException) { return false; }
    }

    private static void ValidateDistribution(HistoricalEvidenceDistributionV1 distribution, int maximumCount)
    {
        foreach (var rows in new[] { distribution.Completeness, distribution.SourceKinds, distribution.Capabilities })
            if (rows.Any(item => item.Count is < 1 || item.Count > maximumCount)
                || rows.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != rows.Count
                || !rows.Select(item => item.Key).SequenceEqual(rows.Select(item => item.Key).Order(StringComparer.Ordinal))) throw Invalid();
    }

    private static HistoricalEvidenceSelectionV1 CanonicalInputSelection(HistoricalEvidenceSelectionV1 selection) => selection with
    {
        ExplicitSessionIds = selection.ExplicitSessionIds.Distinct().OrderBy(item => item.ToString(), StringComparer.Ordinal).ToArray(),
        SourceSurfaces = selection.SourceSurfaces.Distinct().Order().ToArray(),
        From = selection.From?.ToUniversalTime(),
        To = selection.To?.ToUniversalTime(),
    };

    private static HistoricalEvidenceSelectionProjectionV1 ProjectSelection(HistoricalEvidenceSelectionV1 selection, bool safe)
    {
        var canonical = CanonicalInputSelection(selection);
        return new(canonical.Repository, canonical.Workspace, canonical.From, canonical.To,
            canonical.ExplicitSessionIds.Select(item => safe ? SafeSession(item) : item.ToString()).ToArray(),
            canonical.SourceSurfaces, canonical.TaskLabel, canonical.ExperimentLabel,
            canonical.MaximumSessionCount, canonical.SanitizedOnly);
    }

    private static void ValidateSelectionProjection(HistoricalEvidenceSelectionProjectionV1 selection, bool raw)
    {
        if (selection.MaximumSessionCount is < 1 or > HistoricalEvidenceContractsV1.MaximumSessions
            || selection.ExplicitSessionIds.Count > HistoricalEvidenceContractsV1.MaximumSessions
            || selection.ExplicitSessionIds.Distinct(StringComparer.Ordinal).Count() != selection.ExplicitSessionIds.Count
            || selection.SourceSurfaces.Distinct().Count() != selection.SourceSurfaces.Count
            || selection.SourceSurfaces.Any(surface => !Enum.IsDefined(surface))
            || !selection.SourceSurfaces.SequenceEqual(selection.SourceSurfaces.Order())
            || selection.From is { Offset: var fromOffset } && fromOffset != TimeSpan.Zero
            || selection.To is { Offset: var toOffset } && toOffset != TimeSpan.Zero
            || selection.From is { } from && selection.To is { } to && from >= to)
            throw Invalid();
        if (selection.Repository is null && selection.Workspace is null && selection.From is null && selection.To is null
            && selection.ExplicitSessionIds.Count == 0 && selection.SourceSurfaces.Count == 0
            && selection.TaskLabel is null && selection.ExperimentLabel is null)
            throw Invalid();
        foreach (var label in new[] { selection.Repository, selection.Workspace, selection.TaskLabel, selection.ExperimentLabel })
            if (!SafeLabel(label)) throw Invalid();
        foreach (var id in selection.ExplicitSessionIds)
            if (raw ? !IsCanonicalSessionId(id) : !InstructionFindingReferenceTokenizationV1.IsSessionReference(id)) throw Invalid();
        if (raw && !selection.ExplicitSessionIds.SequenceEqual(selection.ExplicitSessionIds.Order(StringComparer.Ordinal))) throw Invalid();
    }

    private static void ValidateSelection(HistoricalEvidenceSelectionV1 selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.MaximumSessionCount is < 1 or > HistoricalEvidenceContractsV1.MaximumSessions
            || selection.ExplicitSessionIds.Count > HistoricalEvidenceContractsV1.MaximumSessions
            || selection.ExplicitSessionIds.Distinct().Count() != selection.ExplicitSessionIds.Count
            || selection.SourceSurfaces.Distinct().Count() != selection.SourceSurfaces.Count
            || selection.SourceSurfaces.Any(surface => !Enum.IsDefined(surface))
            || selection.From is { } from && selection.To is { } to && from >= to
            || !HasScope(selection))
            throw Invalid();
        foreach (var label in new[] { selection.Repository, selection.Workspace, selection.TaskLabel, selection.ExperimentLabel })
            if (!SafeLabel(label)) throw Invalid();
    }

    private static bool HasScope(HistoricalEvidenceSelectionV1 selection) =>
        selection.Repository is not null || selection.Workspace is not null || selection.From is not null || selection.To is not null
        || selection.ExplicitSessionIds.Count > 0 || selection.SourceSurfaces.Count > 0 || selection.TaskLabel is not null || selection.ExperimentLabel is not null;

    private static void ValidateSnapshot(IHistoricalEvidenceSnapshotLeaseV1 snapshot, HistoricalEvidenceSelectionV1 selection)
    {
        if (!SnapshotIdPattern().IsMatch(snapshot.SnapshotId)
            || snapshot.OmittedEarlierMatchingSessionCount < 0
            || snapshot.Sessions.Count > selection.MaximumSessionCount + 1 + selection.ExplicitSessionIds.Count
            || snapshot.Sessions.Select(item => item.SessionId).Distinct().Count() != snapshot.Sessions.Count)
            throw Invalid();
        foreach (var metadata in snapshot.Sessions) ValidateMetadata(metadata);
        var returnedMatching = snapshot.Sessions.Count(item => MatchesSelectionFilters(selection, item));
        var returnedNonExplicitMatching = snapshot.Sessions.Count(item =>
            !selection.ExplicitSessionIds.Contains(item.SessionId) && MatchesSelectionFilters(selection, item));
        if (returnedNonExplicitMatching > selection.MaximumSessionCount + 1
            || snapshot.OmittedEarlierMatchingSessionCount > 0
                && (!HasNonExplicitScope(selection) || returnedMatching < selection.MaximumSessionCount + 1))
            throw Invalid();
    }

    private static void ValidateMetadata(HistoricalSessionMetadataV1 metadata)
    {
        if (!Enum.IsDefined(metadata.SourceSurface) || !Enum.IsDefined(metadata.Completeness)
            || !Enum.IsDefined(metadata.SourceKind) || !Enum.IsDefined(metadata.ContentState)
            || !SafeOptionalToken(metadata.SourceVersion) || !SafeOptionalToken(metadata.AdapterVersion)
            || metadata.EvidenceLocations.Count > HistoricalEvidenceContractsV1.MaximumGroupsPerSession * HistoricalEvidenceContractsV1.MaximumReferencesPerGroup
            || metadata.InstructionFindingIds.Distinct(StringComparer.Ordinal).Count() != metadata.InstructionFindingIds.Count
            || metadata.InstructionFindingIds.Any(id => !FindingIdPattern().IsMatch(id)))
            throw Invalid();
        foreach (var label in new[] { metadata.Repository, metadata.Workspace, metadata.TaskLabel, metadata.ExperimentLabel })
            if (!SafeLabel(label)) throw Invalid();
    }

    private static HistoricalSessionExclusionReasonV1? Exclusion(HistoricalEvidenceSelectionV1 selection, HistoricalSessionMetadataV1 item)
    {
        if (!MatchesSelectionFilters(selection, item))
            return HistoricalSessionExclusionReasonV1.FilterMismatch;
        if (item.Completeness == SessionCompleteness.Unbound) return HistoricalSessionExclusionReasonV1.Unbound;
        if (item.EvidenceLocations.Count == 0) return HistoricalSessionExclusionReasonV1.MissingEvidenceReference;
        if (item.SourceKind == HistoricalEvidenceSourceKindV1.HistoricalSummary
            && (item.Completeness != SessionCompleteness.Partial || !item.CompletenessReasons.Contains("historical_summary_only", StringComparer.Ordinal)))
            return HistoricalSessionExclusionReasonV1.InvalidHistoricalCompleteness;
        return null;
    }

    private static bool MatchesSelectionFilters(HistoricalEvidenceSelectionV1 selection, HistoricalSessionMetadataV1 item)
    {
        var time = item.StartedAt ?? item.LastSeenAt;
        return !(!HasNonExplicitScope(selection) && selection.ExplicitSessionIds.Count > 0 && !selection.ExplicitSessionIds.Contains(item.SessionId)
            || selection.Repository is not null && !string.Equals(selection.Repository, item.Repository, StringComparison.Ordinal)
            || selection.Workspace is not null && !string.Equals(selection.Workspace, item.Workspace, StringComparison.Ordinal)
            || selection.TaskLabel is not null && !string.Equals(selection.TaskLabel, item.TaskLabel, StringComparison.Ordinal)
            || selection.ExperimentLabel is not null && !string.Equals(selection.ExperimentLabel, item.ExperimentLabel, StringComparison.Ordinal)
            || selection.SourceSurfaces.Count > 0 && !selection.SourceSurfaces.Contains(item.SourceSurface)
            || selection.From is { } from && time < from
            || selection.To is { } to && time >= to);
    }

    private static bool HasNonExplicitScope(HistoricalEvidenceSelectionV1 selection) =>
        selection.Repository is not null || selection.Workspace is not null || selection.From is not null || selection.To is not null
        || selection.SourceSurfaces.Count > 0 || selection.TaskLabel is not null || selection.ExperimentLabel is not null;

    private static HistoricalSessionCapabilitiesV1 EffectiveCapabilities(HistoricalSessionMetadataV1 item) =>
        item.SourceKind == HistoricalEvidenceSourceKindV1.HistoricalSummary
            ? item.Capabilities with { RepeatedToolCall = false, SubagentFanOut = false, RawLocalDescriptor = false }
            : item.Capabilities;

    private static (HistoricalDescriptorStateV1 State, string? Value) Descriptor(bool sanitizedOnly, IReadOnlyList<HistoricalEvidenceGroupDraftV1> drafts)
    {
        if (sanitizedOnly) return (HistoricalDescriptorStateV1.NotRequested, null);
        var candidates = drafts.Select(item => item.RawDescriptor).OfType<string>().Select(ProjectDescriptor).ToArray();
        if (candidates.Length == 0) return (HistoricalDescriptorStateV1.Unavailable, null);
        if (candidates.Any(item => item.State == HistoricalDescriptorStateV1.RejectedSensitive))
            return (HistoricalDescriptorStateV1.RejectedSensitive, null);
        var selected = candidates.Select(item => item.Value).Where(item => item is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault();
        return selected is null ? (HistoricalDescriptorStateV1.Unavailable, null) : (HistoricalDescriptorStateV1.Available, selected);
    }

    private static (HistoricalDescriptorStateV1 State, string? Value) ProjectDescriptor(string candidate)
    {
        var firstLine = candidate.Split('\n', 2)[0].TrimEnd('\r').Trim();
        if (firstLine.Length == 0 || firstLine.Any(char.IsControl) || SensitiveDescriptorPattern().IsMatch(firstLine))
            return (HistoricalDescriptorStateV1.RejectedSensitive, null);
        var bounded = string.Concat(firstLine.EnumerateRunes().Take(HistoricalEvidenceContractsV1.MaximumDescriptorLength).Select(rune => rune.ToString()));
        return (HistoricalDescriptorStateV1.Available, bounded);
    }

    private static HistoricalEvidenceSessionV1 ProjectSession(
        HistoricalSessionMetadataV1 item, string id, HistoricalSessionCapabilitiesV1 capabilities,
        HistoricalDescriptorStateV1 descriptorState, string? descriptor) =>
        new(id, item.SourceSurface, item.SourceVersion, item.AdapterVersion, item.Completeness,
            item.CompletenessReasons.Distinct(StringComparer.Ordinal).OrderBy(ReasonIndex).ThenBy(value => value, StringComparer.Ordinal).ToArray(),
            item.SourceKind, item.ContentState, descriptorState, descriptor, capabilities);

    private static int ReasonIndex(string value)
    {
        var index = Array.IndexOf(CompletenessReasonOrder, value);
        if (index < 0) throw Invalid();
        return index;
    }

    private static void ValidateDraft(HistoricalSessionMetadataV1 metadata, HistoricalEvidenceGroupDraftV1 draft)
    {
        if (!Enum.IsDefined(draft.Kind)) throw Invalid();
        if (draft.RawDescriptor is not null && draft.Kind != HistoricalEvidenceGroupKindV1.UserCorrection) throw Invalid();
        if (draft.Kind == HistoricalEvidenceGroupKindV1.RepeatedToolCall
            && string.IsNullOrWhiteSpace(draft.ExactCallId) && !HashPattern().IsMatch(draft.CanonicalCallHash ?? ""))
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.MissingExactCapability);
        if (draft.Kind == HistoricalEvidenceGroupKindV1.SubagentFanOut && string.IsNullOrWhiteSpace(draft.ExactOwnershipId))
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.MissingExactCapability);
        if (!Supports(EffectiveCapabilities(metadata), draft.Kind))
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.MissingExactCapability);
        if (draft.References.Count is < 1 or > HistoricalEvidenceContractsV1.MaximumReferencesPerGroup) throw Invalid();
        foreach (var reference in draft.References)
        {
            if (reference.SessionId != metadata.SessionId
                || reference.TurnIndex is <= 0
                || string.IsNullOrWhiteSpace(reference.TraceId)
                || reference.TraceId.Length > 4096
                || reference.SpanId is { Length: > 4096 }
                || !metadata.EvidenceLocations.Contains(new(reference.SessionId, reference.TraceId, reference.SpanId, reference.TurnIndex, reference.RelativePosition)))
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.UnresolvedEvidenceReference);
        }
        if (draft.NumericValue < 0 || !SafeOptionalToken(draft.Unit) || !SafeOptionalToken(draft.Status)
            || draft.ExactCallId is { } call && !SafeLocalCarrier(call)
            || draft.ExactOwnershipId is { } owner && !SafeLocalCarrier(owner)) throw Invalid();
        if (draft.FindingId is not null && !FindingIdPattern().IsMatch(draft.FindingId)) throw Invalid();
        if (draft.Kind == HistoricalEvidenceGroupKindV1.InstructionFinding
            && (draft.FindingId is null || !metadata.InstructionFindingIds.Contains(draft.FindingId, StringComparer.Ordinal)))
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.UnresolvedEvidenceReference);
    }

    private static bool Supports(HistoricalSessionCapabilitiesV1 capabilities, HistoricalEvidenceGroupKindV1 kind) => kind switch
    {
        HistoricalEvidenceGroupKindV1.TurnRollup => capabilities.TurnRollup,
        HistoricalEvidenceGroupKindV1.TokenRollup => capabilities.TokenRollup,
        HistoricalEvidenceGroupKindV1.CacheRollup => capabilities.CacheRollup,
        HistoricalEvidenceGroupKindV1.ErrorSpan => capabilities.ErrorSpan,
        HistoricalEvidenceGroupKindV1.RetryChain => capabilities.RetryChain,
        HistoricalEvidenceGroupKindV1.RepeatedToolCall => capabilities.RepeatedToolCall,
        HistoricalEvidenceGroupKindV1.PermissionWait => capabilities.PermissionWait,
        HistoricalEvidenceGroupKindV1.SubagentFanOut => capabilities.SubagentFanOut,
        HistoricalEvidenceGroupKindV1.UserCorrection => capabilities.RawLocalDescriptor,
        HistoricalEvidenceGroupKindV1.QualityReference => capabilities.QualityReference,
        HistoricalEvidenceGroupKindV1.SourceDifference => capabilities.SourceComparison,
        HistoricalEvidenceGroupKindV1.InstructionFinding => capabilities.InstructionFindingReference,
        _ => false,
    };

    private static bool SafeOptionalToken(string? value) => value is null || value.Length is > 0 and <= 128 && SafeTokenPattern().IsMatch(value);
    private static bool SafeLocalCarrier(string value) => value.Length is > 0 and <= 256 && !value.Any(char.IsControl) && !SensitiveDescriptorPattern().IsMatch(value);
    private static bool SafeLabel(string? value) => value is null
        || value.Length is > 0 and <= 256
            && !string.IsNullOrWhiteSpace(value)
            && !value.Any(char.IsControl)
            && !SensitiveDescriptorPattern().IsMatch(value);

    private static HistoricalEvidenceGroupV1 ProjectGroup(
        string groupId, HistoricalEvidenceGroupDraftV1 draft, string sessionId,
        IReadOnlyList<HistoricalRawEvidenceReferenceV1> references, bool safe) =>
        new(groupId, draft.Kind, sessionId,
            references.Select(reference => ProjectReference(reference, safe)).ToArray(),
            draft.NumericValue, draft.Unit, draft.Status,
            safe ? null : draft.ExactCallId, draft.CanonicalCallHash,
            safe ? null : draft.ExactOwnershipId, draft.FindingId);

    private static HistoricalEvidenceReferenceV1 ProjectReference(HistoricalRawEvidenceReferenceV1 reference, bool safe)
    {
        if (!safe) return new(reference.SessionId.ToString(), reference.TraceId, reference.SpanId, reference.TurnIndex, reference.RelativePosition);
        var tokenized = InstructionFindingReferenceTokenizationV1.Tokenize(new(
            reference.SessionId.ToString(), reference.TraceId, reference.SpanId, reference.TurnIndex,
            (InstructionEvidenceRelativePositionV1)(int)reference.RelativePosition));
        return new(tokenized.SessionId!, tokenized.TraceId, tokenized.SpanId, tokenized.TurnIndex, reference.RelativePosition);
    }

    private static HistoricalEvidenceDistributionV1 Distribution(IReadOnlyList<HistoricalEvidenceSessionV1> sessions)
    {
        var completeness = sessions.GroupBy(item => Wire(item.Completeness)).OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new HistoricalDistributionCountV1(item.Key, item.Count())).ToArray();
        var sourceKinds = sessions.GroupBy(item => Snake(item.SourceKind)).OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new HistoricalDistributionCountV1(item.Key, item.Count())).ToArray();
        var capabilityCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var session in sessions)
            foreach (var (key, present) in CapabilityValues(session.Capabilities))
                if (present) capabilityCounts[key] = capabilityCounts.GetValueOrDefault(key) + 1;
        return new(completeness, sourceKinds, capabilityCounts.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new HistoricalDistributionCountV1(item.Key, item.Value)).ToArray());
    }

    private static bool DistributionEquals(HistoricalEvidenceDistributionV1 left, HistoricalEvidenceDistributionV1 right) =>
        CountsEqual(left.Completeness, right.Completeness)
        && CountsEqual(left.SourceKinds, right.SourceKinds)
        && CountsEqual(left.Capabilities, right.Capabilities);

    private static bool CountsEqual(IReadOnlyList<HistoricalDistributionCountV1> left, IReadOnlyList<HistoricalDistributionCountV1> right) =>
        left.Select(item => (item.Key, item.Count)).SequenceEqual(right.Select(item => (item.Key, item.Count)));

    private static IEnumerable<(string, bool)> CapabilityValues(HistoricalSessionCapabilitiesV1 value)
    {
        yield return ("turn_rollup", value.TurnRollup); yield return ("token_rollup", value.TokenRollup); yield return ("cache_rollup", value.CacheRollup);
        yield return ("error_span", value.ErrorSpan); yield return ("retry_chain", value.RetryChain); yield return ("repeated_tool_call", value.RepeatedToolCall);
        yield return ("permission_wait", value.PermissionWait); yield return ("subagent_fan_out", value.SubagentFanOut); yield return ("raw_local_descriptor", value.RawLocalDescriptor);
        yield return ("quality_reference", value.QualityReference); yield return ("source_comparison", value.SourceComparison); yield return ("instruction_finding_reference", value.InstructionFindingReference);
    }

    private static string GroupId(Guid sessionId, HistoricalEvidenceGroupDraftV1 draft, IReadOnlyList<HistoricalRawEvidenceReferenceV1> references)
    {
        var fields = new List<string?> { sessionId.ToString(), Snake(draft.Kind), draft.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture), draft.Unit, draft.Status, draft.ExactCallId, draft.CanonicalCallHash, draft.ExactOwnershipId, draft.FindingId };
        foreach (var item in references)
        {
            fields.Add(item.SessionId.ToString()); fields.Add(item.TraceId); fields.Add(item.SpanId);
            fields.Add(item.TurnIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture)); fields.Add(Snake(item.RelativePosition));
        }
        return "historical-group-" + Digest(GroupDomain, fields.ToArray());
    }

    private static string ComputeGroupId(HistoricalEvidenceGroupV1 group)
    {
        if (!Guid.TryParseExact(group.SessionId, "D", out var sessionId)) throw Invalid();
        var references = group.References.Select(item => new HistoricalRawEvidenceReferenceV1(
            sessionId, item.TraceId, item.SpanId, item.TurnIndex, item.RelativePosition)).ToArray();
        var draft = new HistoricalEvidenceGroupDraftV1(
            group.Kind, references, group.NumericValue, group.Unit, group.Status, group.ExactCallId,
            group.CanonicalCallHash, group.ExactOwnershipId, group.FindingId, null);
        return GroupId(sessionId, draft, references);
    }

    private static void AddGroup(List<HistoricalEvidenceGroupV1> groups, HistoricalEvidenceGroupV1 candidate)
    {
        var existing = groups.FirstOrDefault(item => item.GroupId == candidate.GroupId);
        if (existing is null)
        {
            groups.Add(candidate);
            return;
        }
        if (!GroupContentEquals(existing, candidate))
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidDerivedIdentity);
    }

    private static bool GroupContentEquals(HistoricalEvidenceGroupV1 left, HistoricalEvidenceGroupV1 right) =>
        left.GroupId == right.GroupId && left.Kind == right.Kind && left.SessionId == right.SessionId
        && left.References.SequenceEqual(right.References)
        && left.NumericValue == right.NumericValue && left.Unit == right.Unit && left.Status == right.Status
        && left.ExactCallId == right.ExactCallId && left.CanonicalCallHash == right.CanonicalCallHash
        && left.ExactOwnershipId == right.ExactOwnershipId && left.FindingId == right.FindingId;

    private static bool IsCanonicalSessionId(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value;

    private static string SafeSession(Guid id) => InstructionFindingReferenceTokenizationV1.Tokenize(new(id.ToString(), "x", null, 1, InstructionEvidenceRelativePositionV1.Anchor)).SessionId!;
    private static string ExtractionDigest(string snapshotId, ReadOnlySpan<byte> selectionBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(ExtractionDomain));
        AppendField(hash, Encoding.UTF8.GetBytes(snapshotId));
        AppendField(hash, selectionBytes);
        return Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 16)).ToLowerInvariant();
    }

    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        hash.AppendData(length); hash.AppendData(bytes);
    }

    private static string Digest(string domain, params string?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(domain));
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            if (field is null) BinaryPrimitives.WriteUInt32BigEndian(length, uint.MaxValue);
            else
            {
                var bytes = Encoding.UTF8.GetBytes(field);
                BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
                hash.AppendData(length); hash.AppendData(bytes); continue;
            }
            hash.AppendData(length);
        }
        return Convert.ToHexString(hash.GetHashAndReset().AsSpan(0, 16)).ToLowerInvariant();
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Wire(SessionCompleteness value) => SessionWire.ToWire(value);
    private static string Snake<T>(T value) where T : struct, Enum => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
    private static HistoricalEvidenceValidationException Invalid() => new(HistoricalEvidenceValidationCodeV1.InvalidContract);

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant)] private static partial Regex SnapshotIdPattern();
    [GeneratedRegex("^historical-extraction-[0-9a-f]{32}$", RegexOptions.CultureInvariant)] private static partial Regex ExtractionIdPattern();
    [GeneratedRegex("^historical-group-[0-9a-f]{32}$", RegexOptions.CultureInvariant)] private static partial Regex GroupIdPattern();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
    [GeneratedRegex("^instruction-finding-[0-9a-f]{24}$", RegexOptions.CultureInvariant)] private static partial Regex FindingIdPattern();
    [GeneratedRegex("^[A-Za-z0-9._:+-]+$", RegexOptions.CultureInvariant)] private static partial Regex SafeTokenPattern();

    private sealed class HistoricalRawReferenceComparer : IComparer<HistoricalRawEvidenceReferenceV1>
    {
        internal static HistoricalRawReferenceComparer Instance { get; } = new();
        public int Compare(HistoricalRawEvidenceReferenceV1? x, HistoricalRawEvidenceReferenceV1? y)
        {
            if (ReferenceEquals(x, y)) return 0; if (x is null) return -1; if (y is null) return 1;
            var result = StringComparer.Ordinal.Compare(x.SessionId.ToString(), y.SessionId.ToString()); if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(x.TraceId, y.TraceId); if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(x.SpanId, y.SpanId); if (result != 0) return result;
            result = Nullable.Compare(x.TurnIndex, y.TurnIndex); return result != 0 ? result : x.RelativePosition.CompareTo(y.RelativePosition);
        }
    }

    private sealed class HistoricalReferenceComparer : IComparer<HistoricalEvidenceReferenceV1>
    {
        internal static HistoricalReferenceComparer Instance { get; } = new();
        public int Compare(HistoricalEvidenceReferenceV1? x, HistoricalEvidenceReferenceV1? y)
        {
            if (ReferenceEquals(x, y)) return 0; if (x is null) return -1; if (y is null) return 1;
            var result = StringComparer.Ordinal.Compare(x.SessionId, y.SessionId); if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(x.TraceId, y.TraceId); if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(x.SpanId, y.SpanId); if (result != 0) return result;
            result = Nullable.Compare(x.TurnIndex, y.TurnIndex); return result != 0 ? result : x.RelativePosition.CompareTo(y.RelativePosition);
        }
    }

    private sealed class GroupComparer : IComparer<HistoricalEvidenceGroupV1>
    {
        internal static GroupComparer Instance { get; } = new();
        public int Compare(HistoricalEvidenceGroupV1? x, HistoricalEvidenceGroupV1? y)
        {
            if (ReferenceEquals(x, y)) return 0; if (x is null) return -1; if (y is null) return 1;
            var result = x.Kind.CompareTo(y.Kind); return result != 0 ? result : StringComparer.Ordinal.Compare(x.GroupId, y.GroupId);
        }
    }
}
