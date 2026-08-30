using System.Security.Cryptography;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record LocalAiProjectionNodeV1(
    string NodeId, string ExecutionId, string? ParentNodeId, IReadOnlyList<string> ExactReferences, JsonElement? Metadata = null);
internal sealed record LocalAiRawEvidenceV1(string EvidenceId, string NodeId, LocalWorkspaceContentAvailability Locator);

internal sealed record LocalAiProjectionInputV1(
    string SessionId,
    string Revision,
    IReadOnlyList<string> Executions,
    IReadOnlyList<LocalAiProjectionNodeV1> Nodes,
    IReadOnlyList<string> SanitizedSpanObservations,
    string? AnchorNodeId = null,
    IReadOnlyList<LocalAiRawEvidenceV1>? RawEvidence = null);

internal sealed record LocalAiSnapshotProjectionV1(
    string SnapshotId,
    string ScopeKind,
    string SessionId,
    string? NodeId,
    string AnchorId,
    string Revision,
    byte[] PayloadCanonicalJson,
    byte[] EvidenceIndexCanonicalJson,
    string PayloadSha256,
    IReadOnlySet<string> EvidenceIdentifiers,
    IReadOnlyDictionary<string, LocalAiRawEvidenceV1>? RawEvidence = null);

internal sealed class LocalAiScopeTooLargeException : Exception
{
    internal LocalAiScopeTooLargeException() : base("scope_too_large") { }
}

internal static class LocalAiSnapshotProjectionBuilderV1
{
    internal const int MaximumExecutions = 256;
    internal const int MaximumEvents = 4096;
    internal const int MaximumSanitizedSpanObservations = 4096;

    internal static LocalAiSnapshotProjectionV1 BuildSession(LocalAiProjectionInputV1 input)
    {
        Validate(input);
        return Build(input, "session", null, input.SessionId,
            input.Nodes.Select(static node => node.NodeId).Concat(input.SanitizedSpanObservations)
                .Concat((input.RawEvidence ?? []).Select(static item => item.EvidenceId)), input.RawEvidence ?? []);
    }

    internal static LocalAiSnapshotProjectionV1 BuildNode(LocalAiProjectionInputV1 input)
    {
        Validate(input);
        if (string.IsNullOrWhiteSpace(input.AnchorNodeId)) throw new ArgumentException("local_ai_node_anchor_required");
        var byId = input.Nodes.ToDictionary(static node => node.NodeId, StringComparer.Ordinal);
        if (!byId.TryGetValue(input.AnchorNodeId, out var anchor)) throw new ArgumentException("local_ai_node_anchor_not_found");
        var admitted = new HashSet<string>(StringComparer.Ordinal) { anchor.NodeId };
        var current = anchor;
        while (current.ParentNodeId is { } parent)
        {
            if (!byId.TryGetValue(parent, out current!)) throw new ArgumentException("local_ai_node_relation_invalid");
            if (!admitted.Add(current.NodeId)) throw new ArgumentException("local_ai_node_relation_invalid");
        }
        var queue = new Queue<string>(); queue.Enqueue(anchor.NodeId);
        while (queue.TryDequeue(out var parent))
            foreach (var child in input.Nodes.Where(node => node.ParentNodeId == parent))
                if (admitted.Add(child.NodeId)) queue.Enqueue(child.NodeId);
        foreach (var reference in admitted.SelectMany(id => byId[id].ExactReferences).Distinct(StringComparer.Ordinal).ToArray())
            if (byId.TryGetValue(reference, out var related) && related.ExecutionId == anchor.ExecutionId) admitted.Add(reference);
        var raw = (input.RawEvidence ?? []).Where(item => admitted.Contains(item.NodeId)).ToArray();
        return Build(input, "node", anchor.NodeId, anchor.NodeId, admitted.Concat(raw.Select(static item => item.EvidenceId)), raw);
    }

    private static void Validate(LocalAiProjectionInputV1 input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Executions.Count > MaximumExecutions || input.Nodes.Count > MaximumEvents
            || input.SanitizedSpanObservations.Count > MaximumSanitizedSpanObservations)
            throw new LocalAiScopeTooLargeException();
        if (string.IsNullOrWhiteSpace(input.SessionId) || string.IsNullOrWhiteSpace(input.Revision))
            throw new ArgumentException("local_ai_projection_invalid");
    }

    private static LocalAiSnapshotProjectionV1 Build(LocalAiProjectionInputV1 input, string kind, string? nodeId,
        string anchorId, IEnumerable<string> identifiers, IReadOnlyList<LocalAiRawEvidenceV1> rawEvidence)
    {
        var evidence = identifiers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var payload = LocalAiCanonicalJsonV1.Serialize(JsonSerializer.SerializeToElement(new
        {
            schema_version = "local_ai_snapshot_projection:1",
            scope_kind = kind,
            session_id = input.SessionId,
            node_id = nodeId,
            anchor_id = anchorId,
            revision = input.Revision,
            executions = input.Executions.Order(StringComparer.Ordinal).ToArray(),
            nodes = input.Nodes.Where(node => evidence.Contains(node.NodeId, StringComparer.Ordinal))
                .OrderBy(static node => node.NodeId, StringComparer.Ordinal)
                .Select(static node => new { node_id = node.NodeId, execution_id = node.ExecutionId,
                    parent_node_id = node.ParentNodeId, facts = node.Metadata }).ToArray(),
            sanitized_span_observations = input.SanitizedSpanObservations.Order(StringComparer.Ordinal).ToArray(),
            raw_content = rawEvidence.OrderBy(static item => item.EvidenceId, StringComparer.Ordinal)
                .Select(static item => new { evidence_id=item.EvidenceId, state=item.Locator.State,
                    selected_utf8_bytes=item.Locator.SelectedUtf8Bytes }).ToArray(),
        }));
        var index = LocalAiCanonicalJsonV1.Serialize(JsonSerializer.SerializeToElement(new { evidence_refs = evidence }));
        return new(Guid.CreateVersion7().ToString(), kind, input.SessionId, nodeId, anchorId, input.Revision,
            payload, index, Convert.ToHexStringLower(SHA256.HashData(payload)), evidence.ToHashSet(StringComparer.Ordinal),
            rawEvidence.ToDictionary(static item => item.EvidenceId, StringComparer.Ordinal));
    }
}

internal interface ILocalAiSnapshotProjectionServiceV1
{
    ValueTask<LocalAiSnapshotProjectionV1> ReadSessionAsync(string sessionId, CancellationToken token);
    ValueTask<LocalAiSnapshotProjectionV1> ReadNodeAsync(string sessionId, string nodeId, CancellationToken token);
    ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 snapshot, CancellationToken token);
}

internal sealed class LocalAiRawReadException : Exception
{
    internal LocalAiRawReadException(string code) : base(code) { }
}

internal sealed class LocalAiRawReadCapabilityV1
{
    private const int MaximumReads = 64;
    private const int MaximumReadBytes = 1_048_576;
    private const int MaximumAggregateBytes = 16_777_216;
    private readonly IReadOnlySet<string> evidence;
    private readonly Func<string, CancellationToken, ValueTask<byte[]>> reader;
    private int reads;
    private int aggregateBytes;

    internal LocalAiRawReadCapabilityV1(IEnumerable<string> evidence,
        Func<string, CancellationToken, ValueTask<byte[]>> retentionLeaseReader)
    {
        this.evidence = evidence.ToHashSet(StringComparer.Ordinal);
        reader = retentionLeaseReader ?? throw new ArgumentNullException(nameof(retentionLeaseReader));
    }

    internal async ValueTask<byte[]> ReadAsync(string identifier, CancellationToken token)
    {
        if (!evidence.Contains(identifier)) throw new LocalAiRawReadException("raw_scope_rejected");
        if (Interlocked.Increment(ref reads) > MaximumReads) throw new LocalAiRawReadException("raw_read_limit");
        var bytes = await reader(identifier, token).ConfigureAwait(false);
        if (bytes.Length > MaximumReadBytes) throw new LocalAiRawReadException("raw_read_too_large");
        if (Interlocked.Add(ref aggregateBytes, bytes.Length) > MaximumAggregateBytes)
            throw new LocalAiRawReadException("raw_aggregate_too_large");
        return bytes;
    }
}
