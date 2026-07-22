using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.InstructionFindings;

namespace CopilotAgentObservability.SanitizedImport;

internal static class SanitizedImportGraphProjector
{
    internal static (IReadOnlyList<SanitizedImportGraphNode> Nodes, IReadOnlyList<SanitizedImportGraphEdge> Edges) Project(
        IReadOnlyList<SanitizedImportRecord> records,
        IReadOnlyList<SanitizedImportUnresolved> knownMissing)
    {
        var builder = new Builder();
        foreach (var record in records)
        {
            var root = builder.Define($"record:{record.RecordType}", record.RecordId, record.LocalRecordId);
            using var document = JsonDocument.Parse(record.CanonicalBytes);
            switch (record.RecordType)
            {
                case "repository_metadata_projection":
                    ProjectRepository(builder, record, root, document.RootElement);
                    break;
                case "instruction_finding_handoff":
                    InstructionFindingHandoffConsumerV1.Validate(record.CanonicalBytes);
                    ProjectInstruction(builder, record, root, document.RootElement);
                    break;
                case "alert_receipt":
                    AlertReceiptConsumerV1.Validate(record.CanonicalBytes);
                    ProjectAlert(builder, record, root, document.RootElement);
                    break;
                default:
                    throw new InvalidDataException();
            }
        }
        foreach (var item in knownMissing) builder.Reference(item.NodeKind, item.SourceId, item.State);
        return builder.Build();
    }

    private static void ProjectRepository(Builder builder, SanitizedImportRecord record, NodeKey root, JsonElement value)
    {
        var sessionId = Nullable(value, "session_id");
        if (sessionId is not null)
            builder.Link(record, root, builder.Define("session", sessionId, record.LocalRecordId), "defines_session", Json("source", "repository_metadata_projection"));
        var traceId = Nullable(value, "trace_id");
        if (traceId is not null)
            builder.Link(record, root, builder.Define("trace", traceId, record.LocalRecordId), "defines_trace", Json("source", "repository_metadata_projection"));
    }

    private static void ProjectInstruction(Builder builder, SanitizedImportRecord record, NodeKey root, JsonElement value)
    {
        var analysisRun = value.GetProperty("analysis_run_id").GetInt64().ToString(CultureInfo.InvariantCulture);
        var analysis = builder.Define("instruction_analysis_run", analysisRun, record.LocalRecordId);
        builder.Link(record, root, analysis, "defines_analysis_run", Json("analysis_run_id", analysisRun));

        var findingOrdinal = 0;
        foreach (var finding in value.GetProperty("findings").EnumerateArray())
        {
            var findingId = finding.GetProperty("finding_id").GetString()!;
            var findingNode = builder.Define("instruction_finding", findingId, record.LocalRecordId);
            builder.Link(record, analysis, findingNode, "contains_finding", Json("ordinal", findingOrdinal++));
            var anchor = finding.GetProperty("anchor_trace_id").GetString()!;
            builder.Link(record, findingNode, builder.Reference("trace", anchor), "anchor_trace", Json("anchor", true));
            var evidenceOrdinal = 0;
            foreach (var evidence in finding.GetProperty("evidence_refs").EnumerateArray())
            {
                var provenance = Json("evidence_ordinal", evidenceOrdinal, "relative_position", evidence.GetProperty("relative_position").GetString()!);
                LinkOptional(builder, record, findingNode, evidence, "session_id", "session", "evidence_session", provenance);
                LinkOptional(builder, record, findingNode, evidence, "trace_id", "trace", "evidence_trace", provenance);
                LinkOptional(builder, record, findingNode, evidence, "span_id", "span", "evidence_span", provenance);
                if (evidence.GetProperty("turn_index").ValueKind == JsonValueKind.Number)
                {
                    var traceId = evidence.GetProperty("trace_id").GetString()!;
                    var turn = Json("trace_id", traceId, "turn_index", evidence.GetProperty("turn_index").GetInt32());
                    builder.Link(record, findingNode, builder.Reference("turn", turn), "evidence_turn", provenance);
                }
                evidenceOrdinal++;
            }
        }

        var candidateOrdinal = 0;
        foreach (var candidate in value.GetProperty("candidates").EnumerateArray())
        {
            var candidateId = candidate.GetProperty("candidate_id").GetString()!;
            var candidateNode = builder.Define("instruction_candidate", candidateId, record.LocalRecordId);
            builder.Link(record, analysis, candidateNode, "contains_candidate", Json("ordinal", candidateOrdinal++));
            var sourceOrdinal = 0;
            foreach (var sourceFinding in candidate.GetProperty("source_finding_ids").EnumerateArray())
                builder.Link(record, candidateNode, builder.Reference("instruction_finding", sourceFinding.GetString()!),
                    "source_finding", Json("ordinal", sourceOrdinal++));
            var traceOrdinal = 0;
            foreach (var trace in candidate.GetProperty("provenance").GetProperty("trace_refs").EnumerateArray())
                builder.Link(record, candidateNode, builder.Reference("trace", trace.GetString()!),
                    "provenance_trace", Json("ordinal", traceOrdinal++));
        }
    }

    private static void ProjectAlert(Builder builder, SanitizedImportRecord record, NodeKey root, JsonElement value)
    {
        var alertId = value.GetProperty("alert_id").GetString()!;
        var alert = builder.Define("alert", alertId, record.LocalRecordId);
        builder.Link(record, root, alert, "defines_alert", Json("source", "alert_receipt"));
        builder.Link(record, alert, builder.Define("alert_evaluation", value.GetProperty("evaluation_id").GetString()!, record.LocalRecordId),
            "evaluation", Json("receipt_only", true));
        builder.Link(record, alert, builder.Reference("session", value.GetProperty("session_id").GetString()!),
            "session", Json("receipt", true));
        LinkOptional(builder, record, alert, value, "trace_id", "trace", "trace", Json("receipt", true));

        var evidenceOrdinal = 0;
        foreach (var evidence in value.GetProperty("evidence").EnumerateArray())
        {
            var evidenceNode = builder.Define("alert_evidence", evidence.GetProperty("evidence_id").GetString()!, record.LocalRecordId);
            var provenance = Json("evidence_ordinal", evidenceOrdinal, "kind", evidence.GetProperty("kind").GetString()!);
            builder.Link(record, alert, evidenceNode, "contains_evidence", provenance);
            LinkOptional(builder, record, evidenceNode, evidence, "session_id", "session", "evidence_session", provenance);
            LinkOptional(builder, record, evidenceNode, evidence, "trace_id", "trace", "evidence_trace", provenance);
            LinkOptional(builder, record, evidenceNode, evidence, "span_id", "span", "evidence_span", provenance);
            LinkOptional(builder, record, evidenceNode, evidence, "turn_id", "turn", "evidence_turn", provenance);
            LinkOptional(builder, record, evidenceNode, evidence, "event_id", "event", "evidence_event", provenance);
            LinkOptional(builder, record, evidenceNode, evidence, "tool_call_id", "tool_call", "evidence_tool_call", provenance);
            evidenceOrdinal++;
        }
    }

    private static void LinkOptional(Builder builder, SanitizedImportRecord record, NodeKey source, JsonElement value,
        string property, string kind, string relation, string provenance)
    {
        var id = Nullable(value, property);
        if (id is not null) builder.Link(record, source, builder.Reference(kind, id), relation, provenance);
    }

    private static string? Nullable(JsonElement value, string property) =>
        value.GetProperty(property).ValueKind == JsonValueKind.Null ? null : value.GetProperty(property).GetString();

    private static string Json(string name, object value) => JsonSerializer.Serialize(new Dictionary<string, object> { [name] = value });
    private static string Json(string firstName, object first, string secondName, object second) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { [firstName] = first, [secondName] = second });

    private readonly record struct NodeKey(string Kind, string SourceId);

    private sealed class Builder
    {
        private readonly Dictionary<NodeKey, NodeDraft> nodes = [];
        private readonly List<EdgeDraft> edges = [];
        private int ordinal;

        internal NodeKey Define(string kind, string sourceId, string recordLocalId)
        {
            var key = new NodeKey(kind, sourceId);
            AddNode(key, "defined", recordLocalId);
            return key;
        }

        internal NodeKey Reference(string kind, string sourceId, string state = "external")
        {
            var key = new NodeKey(kind, sourceId);
            AddNode(key, state, null);
            return key;
        }

        internal void Link(SanitizedImportRecord record, NodeKey source, NodeKey target, string relation, string provenance)
        {
            if (edges.Count >= SanitizedImportLimits.MaximumGraphEdges) throw new SanitizedImportGraphLimitException();
            edges.Add(new(record.LocalRecordId, source, target, relation, ordinal++, provenance));
        }

        internal (IReadOnlyList<SanitizedImportGraphNode> Nodes, IReadOnlyList<SanitizedImportGraphEdge> Edges) Build()
        {
            if (nodes.Count > SanitizedImportLimits.MaximumGraphNodes || edges.Count > SanitizedImportLimits.MaximumGraphEdges)
                throw new SanitizedImportGraphLimitException();
            var projectedNodes = nodes.OrderBy(item => item.Key.Kind, StringComparer.Ordinal).ThenBy(item => item.Key.SourceId, StringComparer.Ordinal)
                .Select(item => new SanitizedImportGraphNode(
                    NodeId(item.Key), item.Key.Kind, item.Key.SourceId, item.Value.State, item.Value.DefiningRecordLocalId)).ToArray();
            var nodeByKey = projectedNodes.ToDictionary(item => new NodeKey(item.NodeKind, item.SourceId));
            var projectedEdges = edges.Select(item =>
            {
                var source = nodeByKey[item.Source];
                var target = nodeByKey[item.Target];
                var resolution = target.State == "defined" ? "resolved" : target.State;
                return new SanitizedImportGraphEdge(
                    SanitizedImportIdentity.Hash("sanitized-import-edge.v1", item.RecordLocalId, source.LocalNodeId,
                        target.LocalNodeId, item.Relation, item.Ordinal.ToString(CultureInfo.InvariantCulture), item.Provenance),
                    item.RecordLocalId, source.LocalNodeId, target.LocalNodeId, item.Relation, item.Ordinal,
                    resolution, item.Provenance);
            }).OrderBy(item => item.LocalEdgeId, StringComparer.Ordinal).ToArray();
            return (projectedNodes, projectedEdges);
        }

        private void AddNode(NodeKey key, string state, string? recordLocalId)
        {
            if (!nodes.TryGetValue(key, out var current))
            {
                if (nodes.Count >= SanitizedImportLimits.MaximumGraphNodes) throw new SanitizedImportGraphLimitException();
                nodes[key] = new(state, recordLocalId);
            }
            else if (Rank(state) > Rank(current.State))
                nodes[key] = new(state, recordLocalId);
        }

        private static int Rank(string state) => state switch { "defined" => 3, "missing" => 2, _ => 1 };
        private static string NodeId(NodeKey key) => SanitizedImportIdentity.Hash("sanitized-import-node.v1", key.Kind, key.SourceId);

        private sealed record NodeDraft(string State, string? DefiningRecordLocalId);
        private sealed record EdgeDraft(string RecordLocalId, NodeKey Source, NodeKey Target, string Relation, int Ordinal, string Provenance);
    }
}

internal sealed class SanitizedImportGraphLimitException : Exception;
