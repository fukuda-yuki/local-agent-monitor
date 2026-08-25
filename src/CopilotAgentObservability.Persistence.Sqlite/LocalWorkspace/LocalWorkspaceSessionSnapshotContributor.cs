using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class LocalWorkspaceSessionSnapshotContributor : ILocalRepositorySessionSnapshotContributor
{
    private const int MaximumSessions = 10_000;
    public ValueTask<LocalRepositorySessionContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositoryScopeRequest request,
        CancellationToken cancellationToken)
    {
        return transaction.ReadAsync(ReadRowsAsync, cancellationToken);
    }

    private static async ValueTask<LocalRepositorySessionContribution> ReadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rows = new List<MutableRow>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT session_id,sort_group,sort_epoch_ms,label_state,label_text,status,completeness,
                       timing_state,started_at,ended_at,duration_ms,capture_notes,revision_seed
                FROM local_workspace_sessions ORDER BY session_id COLLATE BINARY LIMIT 10001;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count == MaximumSessions)
                    throw new InvalidOperationException("local_repository_session_limit_exceeded");
                rows.Add(new(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.GetString(11), reader.GetString(12)));
            }
        }
        var byId = rows.ToDictionary(row => row.SessionId, StringComparer.Ordinal);
        await ReadPairs(connection, transaction, "SELECT session_id,source FROM local_workspace_session_sources ORDER BY session_id,source;", byId, static (row, value) => row.Sources.Add(value), cancellationToken);
        await ReadPairs(connection, transaction, "SELECT session_id,model FROM local_workspace_session_models ORDER BY session_id,model;", byId, static (row, value) => row.Models.Add(value), cancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id,kind,state,count FROM local_workspace_session_activity ORDER BY session_id,kind;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (byId.TryGetValue(reader.GetString(0), out var row)) row.Activity[reader.GetString(1)] = new(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3));
        }
        var skillAggregates = SkillProjectionReadService.ReadSessionInvocationAggregates(connection, transaction, byId.Keys.ToArray());
        foreach (var row in rows)
            row.Activity["skill"] = skillAggregates.TryGetValue(row.SessionId, out var aggregate)
                ? new("recorded", aggregate.InvocationCount)
                : new("not_observed", null);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                WITH ranked AS (
                  SELECT *,row_number() OVER(PARTITION BY session_id,execution_id ORDER BY authority_rank,authority,source_identity) ordinal
                  FROM local_workspace_token_observations), selected AS (SELECT * FROM ranked WHERE ordinal=1)
                SELECT session_id,COUNT(*),SUM(input_tokens IS NOT NULL),SUM(input_tokens),SUM(output_tokens),SUM(total_tokens),SUM(reasoning_tokens),SUM(cache_read_tokens),SUM(cache_creation_tokens),
                       MIN(authority),MAX(authority),SUM(CASE WHEN input_tokens IS NOT NULL AND cache_read_tokens IS NOT NULL AND cache_read_tokens>input_tokens THEN 1 ELSE 0 END)
                FROM selected GROUP BY session_id ORDER BY session_id;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (byId.TryGetValue(reader.GetString(0), out var row)) row.TokenAggregate = ReadTokens(reader);
        }
        return new(Array.AsReadOnly(rows.Select(static row => (ILocalRepositorySessionSnapshotRow)row.Freeze()).ToArray()));
    }

    private static LocalWorkspaceTokenFacts ReadTokens(SqliteDataReader reader)
    {
        var totalExecutions = reader.GetInt64(1);
        var available = reader.GetInt64(2);
        var authority = reader.GetString(9) == reader.GetString(10) ? reader.GetString(9) : "mixed";
        var inconsistent = reader.GetInt64(11) != 0;
        var input = Nullable(reader, 3); var cache = Nullable(reader, 7);
        LocalWorkspaceFact<long> Fact(long? value) => new(value is null ? "not_observed" : "recorded", value);
        var newInput = !inconsistent && input is not null && cache is not null ? input - cache : null;
        var ratio = !inconsistent && input > 0 && cache is not null ? cache * 10_000 / input : null;
        return new(authority, inconsistent ? "inconsistent" : available == totalExecutions ? "recorded" : "partial", available, totalExecutions,
            Fact(input), Fact(Nullable(reader, 4)), Fact(Nullable(reader, 5)), Fact(Nullable(reader, 6)), Fact(cache), Fact(Nullable(reader, 8)),
            new(newInput is null ? inconsistent ? "inconsistent" : "not_observed" : "recorded", newInput),
            new(ratio is null ? inconsistent ? "inconsistent" : "not_observed" : "recorded", ratio));
    }

    private static long? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static async Task ReadPairs(SqliteConnection connection, SqliteTransaction transaction, string sql, Dictionary<string, MutableRow> rows, Action<MutableRow, string> add, CancellationToken token)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) if (rows.TryGetValue(reader.GetString(0), out var row)) add(row, reader.GetString(1));
    }

    private sealed class MutableRow
    {
        private readonly long sortGroup;
        private readonly long sortEpoch;
        private readonly string labelState;
        private readonly string? label;
        private readonly string status;
        private readonly string completeness;
        private readonly string timingState;
        private readonly string? startedAt;
        private readonly string? endedAt;
        private readonly long? duration;
        private readonly string notes;
        private readonly string revision;

        internal MutableRow(string sessionId, long sortGroup, long sortEpoch, string labelState, string? label, string status, string completeness, string timingState, string? startedAt, string? endedAt, long? duration, string notes, string revision)
        {
            SessionId = sessionId; this.sortGroup = sortGroup; this.sortEpoch = sortEpoch;
            this.labelState = labelState; this.label = label; this.status = status; this.completeness = completeness;
            this.timingState = timingState; this.startedAt = startedAt; this.endedAt = endedAt;
            this.duration = duration; this.notes = notes; this.revision = revision;
        }

        internal string SessionId { get; }
        internal List<string> Sources { get; } = [];
        internal List<string> Models { get; } = [];
        internal Dictionary<string, LocalWorkspaceFact<long>> Activity { get; } = new(StringComparer.Ordinal);
        internal LocalWorkspaceTokenFacts? TokenAggregate { get; set; }
        internal LocalWorkspaceProjectionRow Freeze()
        {
            LocalWorkspaceFact<long> A(string kind) => Activity.TryGetValue(kind, out var fact) ? fact : new("not_observed", null);
            var tokens = TokenAggregate ?? new("none", "not_observed", 0, 0, new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null));
            return new(SessionId, sortGroup, sortEpoch, labelState, label, status, completeness,
                Array.AsReadOnly(Sources.ToArray()), Array.AsReadOnly(Models.ToArray()), new(A("skill"), A("tool"), A("subagent"), A("error"), A("retry")), tokens,
                timingState, startedAt, endedAt, duration, Array.AsReadOnly(notes.Length == 0 ? [] : notes.Split(',', StringSplitOptions.RemoveEmptyEntries)), revision);
        }
    }
}
