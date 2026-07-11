using System.Text.Json.Serialization;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal sealed record ProposalApplyDraftRequest(
    [property: JsonPropertyName("proposal_id")] string? ProposalId,
    [property: JsonPropertyName("root_id")] string? RootId,
    [property: JsonPropertyName("files")] IReadOnlyList<ProposalApplyFileRequest>? Files);
internal sealed record ProposalApplyFileRequest([property: JsonPropertyName("relative_path")] string? RelativePath, [property: JsonPropertyName("replacement_text")] string? ReplacementText);
internal sealed record ProposalApplySelectionRequest([property: JsonPropertyName("selection_revision")] int? SelectionRevision, [property: JsonPropertyName("selected_hunk_ids")] IReadOnlyList<string>? SelectedHunkIds);
internal sealed record ProposalApplyApprovalRequest([property: JsonPropertyName("selection_revision")] int? SelectionRevision, [property: JsonPropertyName("approval_digest")] string? ApprovalDigest);
