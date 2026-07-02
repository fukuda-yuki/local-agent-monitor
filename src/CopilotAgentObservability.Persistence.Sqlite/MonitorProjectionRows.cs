namespace CopilotAgentObservability.Persistence.Sqlite;

/// <summary>
/// Sanitized read DTO for a <c>monitor_ingestions</c> row. Allowlist columns
/// only — never the raw payload or PII. The cursor key is <see cref="RawRecordId"/>.
/// </summary>
internal sealed record MonitorIngestionRow(
    long RawRecordId,
    string ReceivedAt,
    string Source,
    string? TraceId,
    string? ClientKind,
    int? SpanCount,
    string ProjectedAt);

/// <summary>
/// Sanitized read DTO for a <c>monitor_traces</c> row. Allowlist columns only.
/// The cursor key is the projection-row <see cref="Id"/>.
/// </summary>
internal sealed record MonitorTraceRow(
    long Id,
    string TraceId,
    string? ClientKind,
    string? ExperimentId,
    string? TaskId,
    string? TaskCategory,
    string? AgentVariant,
    string? PromptVersion,
    int? SpanCount,
    int? ToolCallCount,
    int? ErrorCount,
    string? FirstSeenAt,
    string? LastSeenAt,
    string ProjectedAt,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int? TurnCount,
    int? AgentInvocationCount,
    double? DurationMs,
    string? PrimaryModel,
    string? RepositoryName,
    string? WorkspaceLabel,
    string? RepoSnapshot);

/// <summary>Backlog and the oldest unprocessed ingestion time, for projection-lag readiness.</summary>
internal sealed record MonitorProjectionStatus(
    int Backlog,
    DateTimeOffset? OldestUnprocessedReceivedAt);

/// <summary>
/// A cursor page: at most <c>limit</c> items, plus whether a further row exists
/// (detected by reading one row past the limit). The caller derives
/// <c>next_cursor</c> from the last item's cursor key when <see cref="HasMore"/>.
/// </summary>
internal sealed record MonitorProjectionPage<T>(
    IReadOnlyList<T> Items,
    bool HasMore);

/// <summary>
/// Sanitized read DTO for a <c>monitor_spans</c> row. Allowlist columns only.
/// </summary>
internal sealed record MonitorSpanRow(
    long Id,
    long RawRecordId,
    string TraceId,
    string? SpanId,
    string? ParentSpanId,
    int SpanOrdinal,
    string? Operation,
    string? Category,
    string? ToolName,
    string? ToolType,
    string? McpToolName,
    string? McpServerHash,
    string? AgentName,
    string? RequestModel,
    string? ResponseModel,
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int? ReasoningTokens,
    int? CacheReadTokens,
    int? CacheCreationTokens,
    string? Status,
    string? ErrorType,
    string? FinishReasons,
    string? ConversationId,
    double? DurationMs,
    string? StartTime,
    string? EndTime,
    string ProjectedAt);

/// <summary>
/// Sanitized read DTO for the rollup columns on a <c>monitor_traces</c> row
/// (Sprint9 additive columns).
/// </summary>
internal sealed record MonitorTraceRollupRow(
    int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int? TurnCount,
    int? AgentInvocationCount,
    double? DurationMs,
    string? PrimaryModel);
