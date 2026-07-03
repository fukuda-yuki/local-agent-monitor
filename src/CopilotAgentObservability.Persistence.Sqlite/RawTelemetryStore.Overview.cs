namespace CopilotAgentObservability.Persistence.Sqlite;

/// <summary>
/// Sprint18 (D044) read queries for the overview dashboard and the filtered
/// trace list. Sanitized aggregates over <c>monitor_traces</c> only — no raw
/// payload, no prompt text, no PII. Period membership is
/// <c>last_seen_at &gt;= $start AND last_seen_at &lt; $end</c> using ISO-8601 UTC
/// ("O" format) strings, which compare correctly as text. Cache-rate
/// denominators exclude pre-v4 rows whose cache columns are NULL (no backfill).
/// </summary>
internal sealed partial class RawTelemetryStore
{
    private const string PeriodAggregateColumns =
        """
        COUNT(*),
        SUM(CASE WHEN COALESCE(error_count, 0) > 0 THEN 1 ELSE 0 END),
        COALESCE(SUM(total_tokens), 0),
        COALESCE(SUM(input_tokens), 0),
        COALESCE(SUM(output_tokens), 0),
        COALESCE(SUM(cache_read_tokens), 0),
        COALESCE(SUM(cache_creation_tokens), 0),
        COALESCE(SUM(CASE WHEN cache_read_tokens IS NOT NULL THEN input_tokens END), 0)
        """;

    /// <summary>Aggregate KPI sums over the traces whose last_seen_at falls in [startInclusive, endExclusive).</summary>
    public MonitorPeriodSummaryRow GetPeriodSummary(string startInclusive, string endExclusive)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {PeriodAggregateColumns}
            FROM monitor_traces
            WHERE last_seen_at >= $start AND last_seen_at < $end;
            """;
        AddParameter(command, "$start", startInclusive);
        AddParameter(command, "$end", endExclusive);

        using var reader = command.ExecuteReader();
        reader.Read();
        return new MonitorPeriodSummaryRow(
            TraceCount: reader.GetInt32(0),
            ErrorTraceCount: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            TotalTokens: reader.GetInt64(2),
            InputTokens: reader.GetInt64(3),
            OutputTokens: reader.GetInt64(4),
            CacheReadTokens: reader.GetInt64(5),
            CacheCreationTokens: reader.GetInt64(6),
            CacheAwareInputTokens: reader.GetInt64(7));
    }

    /// <summary>Per-primary-model aggregate over the same window, token-heavy models first.</summary>
    public IReadOnlyList<MonitorModelPeriodSummaryRow> GetPerModelPeriodSummary(string startInclusive, string endExclusive)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT primary_model, {PeriodAggregateColumns}
            FROM monitor_traces
            WHERE last_seen_at >= $start AND last_seen_at < $end
            GROUP BY primary_model
            ORDER BY COALESCE(SUM(total_tokens), 0) DESC, COUNT(*) DESC;
            """;
        AddParameter(command, "$start", startInclusive);
        AddParameter(command, "$end", endExclusive);

        using var reader = command.ExecuteReader();
        var rows = new List<MonitorModelPeriodSummaryRow>();
        while (reader.Read())
        {
            rows.Add(new MonitorModelPeriodSummaryRow(
                Model: reader.IsDBNull(0) ? null : reader.GetString(0),
                TraceCount: reader.GetInt32(1),
                ErrorTraceCount: reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                TotalTokens: reader.GetInt64(3),
                InputTokens: reader.GetInt64(4),
                OutputTokens: reader.GetInt64(5),
                CacheReadTokens: reader.GetInt64(6),
                CacheCreationTokens: reader.GetInt64(7),
                CacheAwareInputTokens: reader.GetInt64(8)));
        }

        return rows;
    }

    /// <summary>
    /// Token sum per UTC hour-of-day over the window. last_seen_at is always the
    /// "O"-format UTC string (yyyy-MM-ddTHH:...), so the hour is characters 12–13.
    /// </summary>
    public IReadOnlyList<MonitorHourlyTokensRow> GetHourlyTokenDistribution(string startInclusive, string endExclusive)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CAST(substr(last_seen_at, 12, 2) AS INTEGER), COALESCE(SUM(total_tokens), 0)
            FROM monitor_traces
            WHERE last_seen_at >= $start AND last_seen_at < $end
            GROUP BY substr(last_seen_at, 12, 2)
            ORDER BY 1;
            """;
        AddParameter(command, "$start", startInclusive);
        AddParameter(command, "$end", endExclusive);

        using var reader = command.ExecuteReader();
        var rows = new List<MonitorHourlyTokensRow>();
        while (reader.Read())
        {
            rows.Add(new MonitorHourlyTokensRow(
                UtcHour: reader.GetInt32(0),
                TotalTokens: reader.GetInt64(1)));
        }

        return rows;
    }

    /// <summary>Highest-token traces in the window (token-desc, NULL totals excluded).</summary>
    public IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {TraceRowColumns}
            FROM monitor_traces
            WHERE last_seen_at >= $start AND last_seen_at < $end AND total_tokens IS NOT NULL
            ORDER BY total_tokens DESC, id DESC
            LIMIT $limit;
            """;
        AddParameter(command, "$start", startInclusive);
        AddParameter(command, "$end", endExclusive);
        AddParameter(command, "$limit", limit);

        return ReadTraceRows(command);
    }

    /// <summary>Most recently seen traces (ORDER BY last_seen_at DESC).</summary>
    public IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {TraceRowColumns}
            FROM monitor_traces
            ORDER BY last_seen_at DESC, id DESC
            LIMIT $limit;
            """;
        AddParameter(command, "$limit", limit);

        return ReadTraceRows(command);
    }

    /// <summary>
    /// Offset page of traces matching the Sprint18 trace-list filters, plus the
    /// total matching row count. The search term matches TraceId substrings only.
    /// </summary>
    public MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query)
    {
        var conditions = new List<string>();
        var parameters = new List<(string Name, object? Value)>();

        if (!string.IsNullOrEmpty(query.TraceIdSearch))
        {
            conditions.Add("trace_id LIKE $search ESCAPE '\\'");
            parameters.Add(("$search", "%" + EscapeLikePattern(query.TraceIdSearch) + "%"));
        }

        if (!string.IsNullOrEmpty(query.Model))
        {
            conditions.Add("primary_model = $model");
            parameters.Add(("$model", query.Model));
        }

        if (!string.IsNullOrEmpty(query.Status))
        {
            if (string.Equals(query.Status, "unknown", StringComparison.Ordinal))
            {
                conditions.Add("trace_status IS NULL");
            }
            else if (string.Equals(query.Status, "error", StringComparison.Ordinal))
            {
                // "error" = any error trace, recovered or not (overview KPI link).
                conditions.Add("trace_status IN ('recovered', 'unrecovered')");
            }
            else
            {
                conditions.Add("trace_status = $status");
                parameters.Add(("$status", query.Status));
            }
        }

        if (!string.IsNullOrEmpty(query.StartInclusive))
        {
            conditions.Add("last_seen_at >= $range_start");
            parameters.Add(("$range_start", query.StartInclusive));
        }

        if (!string.IsNullOrEmpty(query.EndExclusive))
        {
            conditions.Add("last_seen_at < $range_end");
            parameters.Add(("$range_end", query.EndExclusive));
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        // Whitelisted sort keys only — the endpoint validates before calling.
        var orderClause = query.Sort switch
        {
            "time" => "ORDER BY last_seen_at DESC, id DESC",
            "duration" => "ORDER BY duration_ms IS NULL, duration_ms DESC, id DESC",
            _ => "ORDER BY total_tokens IS NULL, total_tokens DESC, id DESC",
        };

        using var connection = OpenConnection();

        int totalMatched;
        long totalMatchedTokens;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*), COALESCE(SUM(total_tokens), 0) FROM monitor_traces {whereClause};";
            foreach (var (name, value) in parameters)
            {
                AddParameter(countCommand, name, value);
            }

            using var countReader = countCommand.ExecuteReader();
            countReader.Read();
            totalMatched = countReader.GetInt32(0);
            totalMatchedTokens = countReader.GetInt64(1);
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {TraceRowColumns}
            FROM monitor_traces
            {whereClause}
            {orderClause}
            LIMIT $limit OFFSET $offset;
            """;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        AddParameter(command, "$limit", query.Limit);
        AddParameter(command, "$offset", query.Offset);

        return new MonitorTraceListPage(ReadTraceRows(command), totalMatched, totalMatchedTokens);
    }

    private const string TraceRowColumns =
        """
        id, trace_id, client_kind, experiment_id, task_id, task_category, agent_variant, prompt_version,
        span_count, tool_call_count, error_count, first_seen_at, last_seen_at, projected_at,
        input_tokens, output_tokens, total_tokens, turn_count, agent_invocation_count, duration_ms, primary_model,
        repository_name, workspace_label, repo_snapshot, cache_read_tokens, cache_creation_tokens, trace_status
        """;

    private static IReadOnlyList<MonitorTraceRow> ReadTraceRows(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var items = new List<MonitorTraceRow>();
        while (reader.Read())
        {
            items.Add(ReadTraceRow(reader));
        }

        return items;
    }

    private static MonitorTraceRow ReadTraceRow(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        TraceId: reader.GetString(1),
        ClientKind: reader.IsDBNull(2) ? null : reader.GetString(2),
        ExperimentId: reader.IsDBNull(3) ? null : reader.GetString(3),
        TaskId: reader.IsDBNull(4) ? null : reader.GetString(4),
        TaskCategory: reader.IsDBNull(5) ? null : reader.GetString(5),
        AgentVariant: reader.IsDBNull(6) ? null : reader.GetString(6),
        PromptVersion: reader.IsDBNull(7) ? null : reader.GetString(7),
        SpanCount: reader.IsDBNull(8) ? null : reader.GetInt32(8),
        ToolCallCount: reader.IsDBNull(9) ? null : reader.GetInt32(9),
        ErrorCount: reader.IsDBNull(10) ? null : reader.GetInt32(10),
        FirstSeenAt: reader.IsDBNull(11) ? null : reader.GetString(11),
        LastSeenAt: reader.IsDBNull(12) ? null : reader.GetString(12),
        ProjectedAt: reader.GetString(13),
        InputTokens: reader.IsDBNull(14) ? null : reader.GetInt32(14),
        OutputTokens: reader.IsDBNull(15) ? null : reader.GetInt32(15),
        TotalTokens: reader.IsDBNull(16) ? null : reader.GetInt32(16),
        TurnCount: reader.IsDBNull(17) ? null : reader.GetInt32(17),
        AgentInvocationCount: reader.IsDBNull(18) ? null : reader.GetInt32(18),
        DurationMs: reader.IsDBNull(19) ? null : reader.GetDouble(19),
        PrimaryModel: reader.IsDBNull(20) ? null : reader.GetString(20),
        RepositoryName: reader.IsDBNull(21) ? null : reader.GetString(21),
        WorkspaceLabel: reader.IsDBNull(22) ? null : reader.GetString(22),
        RepoSnapshot: reader.IsDBNull(23) ? null : reader.GetString(23),
        CacheReadTokens: reader.IsDBNull(24) ? null : reader.GetInt32(24),
        CacheCreationTokens: reader.IsDBNull(25) ? null : reader.GetInt32(25),
        TraceStatus: reader.IsDBNull(26) ? null : reader.GetString(26));

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
