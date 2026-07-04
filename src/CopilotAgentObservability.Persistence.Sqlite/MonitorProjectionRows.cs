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
    string? RepoSnapshot,
    int? CacheReadTokens,
    int? CacheCreationTokens,
    string? TraceStatus);

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

/// <summary>
/// Sanitized aggregate over the <c>monitor_traces</c> rows whose
/// <c>last_seen_at</c> falls in a [start, end) window (Sprint18 overview, D044).
/// Sums are numeric-only. <see cref="CacheAwareInputTokens"/> is the input-token
/// sum restricted to rows with non-NULL cache columns so pre-v4 rows are excluded
/// from cache-rate denominators (D044 no-backfill rule).
/// </summary>
internal sealed record MonitorPeriodSummaryRow(
    int TraceCount,
    int ErrorTraceCount,
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long CacheAwareInputTokens);

/// <summary>Per-model slice of <see cref="MonitorPeriodSummaryRow"/> (model is NULL for rows without a projected primary_model).</summary>
internal sealed record MonitorModelPeriodSummaryRow(
    string? Model,
    int TraceCount,
    int ErrorTraceCount,
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long CacheAwareInputTokens);

/// <summary>Token sum per UTC hour-of-day (00–23) over a [start, end) window; the service shifts to local hours.</summary>
internal sealed record MonitorHourlyTokensRow(
    int UtcHour,
    long TotalTokens);

/// <summary>
/// Filter/sort/paging query for the Sprint18 trace-list endpoint. TraceIdSearch
/// is a TraceId substring only (never prompt text, D042 C8). Status is one of
/// ok / recovered / unrecovered / unknown (unknown = NULL trace_status rows).
/// Sort is a whitelisted key: tokens / time / duration.
/// </summary>
internal sealed record MonitorTraceListQuery(
    string? TraceIdSearch,
    string? Model,
    string? Status,
    string? StartInclusive,
    string? EndExclusive,
    string Sort,
    int Offset,
    int Limit);

/// <summary>An offset page of trace rows plus the total row count and token sum matching the same filters.</summary>
internal sealed record MonitorTraceListPage(
    IReadOnlyList<MonitorTraceRow> Items,
    int TotalMatched,
    long TotalMatchedTokens);
