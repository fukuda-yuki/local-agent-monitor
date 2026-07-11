using System.Text.Json.Serialization;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal sealed record ImprovementProposalRequest(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("target_kind")] string? TargetKind,
    [property: JsonPropertyName("target_label")] string? TargetLabel,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("expected_effect")] string? ExpectedEffect,
    [property: JsonPropertyName("risk_note")] string? RiskNote,
    [property: JsonPropertyName("source_sessions")] IReadOnlyList<string>? SourceSessions,
    [property: JsonPropertyName("evidence_refs")] IReadOnlyList<ImprovementProposalEvidenceReferenceRequest>? EvidenceReferences);

internal sealed record ImprovementProposalEvidenceReferenceRequest(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("reference_id")] string? ReferenceId);

internal sealed record ImprovementProposalStatusRequest(
    [property: JsonPropertyName("status")] string? Status);
