using Microsoft.Data.Sqlite;
using System.Numerics;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class LocalWorkspaceSessionSnapshotContributor : ILocalRepositorySessionSnapshotContributor
{
    private const int MaximumSessions = 10_000;
    private readonly TimeProvider timeProvider;
    private readonly Action<string>? statementObserver;
    private readonly ISkillRegistryGenerationAuthority? registryAuthority;

    internal LocalWorkspaceSessionSnapshotContributor(
        TimeProvider? timeProvider = null,
        Action<string>? statementObserver = null,
        ISkillRegistryGenerationAuthority? registryAuthority = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.statementObserver = statementObserver;
        this.registryAuthority = registryAuthority;
    }

    public ValueTask<LocalRepositorySessionContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositoryScopeRequest request,
        CancellationToken cancellationToken)
    {
        var acceptedAt = timeProvider.GetUtcNow();
        var now = acceptedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return transaction.ReadAsync((connection, sqliteTransaction, token) =>
            ReadRowsAsync(connection, sqliteTransaction, acceptedAt, now, request.TargetSessionId, statementObserver, registryAuthority, token), cancellationToken);
    }

    private static async ValueTask<LocalRepositorySessionContribution> ReadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset acceptedAt,
        string now,
        string? targetSessionId,
        Action<string>? statementObserver,
        ISkillRegistryGenerationAuthority? registryAuthority,
        CancellationToken cancellationToken)
    {
        var rows = new List<MutableRow>();
        var retentionInstalled = TableExists(connection, transaction, "retention_items");
        using (var command = connection.CreateCommand())
        {
            statementObserver?.Invoke("sessions");
            command.Transaction = transaction;
            command.CommandText = retentionInstalled ? (targetSessionId is null ? """
                SELECT p.session_id,p.sort_group,p.sort_epoch_ms,
                       CASE WHEN p.label_state='recorded' AND CASE WHEN c.event_id IS NOT NULL
                         AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                         AND i.expires_at=c.expires_at COLLATE BINARY
                         AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY)) THEN 1 ELSE 0 END=0 THEN 'expired' ELSE p.label_state END,
                       CASE WHEN p.label_state='recorded' AND c.event_id IS NOT NULL
                         AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                         AND i.expires_at=c.expires_at COLLATE BINARY
                         AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY)) THEN p.label_text END,
                       p.status,p.completeness,p.source_state,p.model_state,
                       timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed
                FROM local_workspace_sessions p
                LEFT JOIN session_events e ON e.event_id=p.label_source_identity AND e.session_id=p.session_id AND e.content_state='available'
                LEFT JOIN session_event_content c ON c.event_id=e.event_id
                LEFT JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=e.event_id
                WHERE 1=1
                ORDER BY p.session_id COLLATE BINARY LIMIT 10001;
                """ : """
                SELECT p.session_id,p.sort_group,p.sort_epoch_ms,
                       CASE WHEN p.label_state='recorded' AND CASE WHEN c.event_id IS NOT NULL
                         AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                         AND i.expires_at=c.expires_at COLLATE BINARY
                         AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY)) THEN 1 ELSE 0 END=0 THEN 'expired' ELSE p.label_state END,
                       CASE WHEN p.label_state='recorded' AND c.event_id IS NOT NULL
                         AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                         AND i.expires_at=c.expires_at COLLATE BINARY
                         AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY)) THEN p.label_text END,
                       p.status,p.completeness,p.source_state,p.model_state,
                       timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed
                FROM local_workspace_sessions p
                LEFT JOIN session_events e ON e.event_id=p.label_source_identity AND e.session_id=p.session_id AND e.content_state='available'
                LEFT JOIN session_event_content c ON c.event_id=e.event_id
                LEFT JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=e.event_id
                WHERE p.session_id=$target_session_id
                ORDER BY p.session_id COLLATE BINARY LIMIT 2;
                """) : (targetSessionId is null ? """
                SELECT p.session_id,p.sort_group,p.sort_epoch_ms,
                       CASE WHEN p.label_state='recorded' AND (c.event_id IS NULL OR c.expires_at COLLATE BINARY <= $now COLLATE BINARY) THEN 'expired' ELSE p.label_state END,
                       CASE WHEN p.label_state='recorded' AND c.event_id IS NOT NULL AND c.expires_at COLLATE BINARY > $now COLLATE BINARY THEN p.label_text END,
                       p.status,p.completeness,p.source_state,p.model_state,
                       timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed
                FROM local_workspace_sessions p
                LEFT JOIN session_events e ON e.event_id=p.label_source_identity AND e.session_id=p.session_id AND e.content_state='available'
                LEFT JOIN session_event_content c ON c.event_id=e.event_id AND c.expires_at=p.label_expires_at
                WHERE 1=1
                ORDER BY p.session_id COLLATE BINARY LIMIT 10001;
                """ : """
                SELECT p.session_id,p.sort_group,p.sort_epoch_ms,
                       CASE WHEN p.label_state='recorded' AND (c.event_id IS NULL OR c.expires_at COLLATE BINARY <= $now COLLATE BINARY) THEN 'expired' ELSE p.label_state END,
                       CASE WHEN p.label_state='recorded' AND c.event_id IS NOT NULL AND c.expires_at COLLATE BINARY > $now COLLATE BINARY THEN p.label_text END,
                       p.status,p.completeness,p.source_state,p.model_state,
                       timing_state,started_at,ended_at,last_seen_at,last_seen_epoch_ms,duration_ms,capture_notes,revision_seed
                FROM local_workspace_sessions p
                LEFT JOIN session_events e ON e.event_id=p.label_source_identity AND e.session_id=p.session_id AND e.content_state='available'
                LEFT JOIN session_event_content c ON c.event_id=e.event_id AND c.expires_at=p.label_expires_at
                WHERE p.session_id=$target_session_id
                ORDER BY p.session_id COLLATE BINARY LIMIT 2;
                """);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$target_session_id", (object?)targetSessionId ?? DBNull.Value);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count == MaximumSessions)
                    throw new InvalidOperationException("local_repository_session_limit_exceeded");
                rows.Add(new(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetInt64(13), reader.IsDBNull(14) ? null : reader.GetInt64(14), reader.GetString(15), reader.GetString(16)));
            }
        }
        var byId = rows.ToDictionary(row => row.SessionId, StringComparer.Ordinal);
        var ids = System.Text.Json.JsonSerializer.Serialize(byId.Keys.Order(StringComparer.Ordinal));
        await ReadPairs(connection, transaction, "sources", "SELECT session_id,source FROM local_workspace_session_sources WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY session_id,source;", ids, byId, static (row, value) => row.Sources.Add(value), statementObserver, cancellationToken);
        await ReadPairs(connection, transaction, "models", "SELECT session_id,model FROM local_workspace_session_models WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY session_id,model;", ids, byId, static (row, value) => row.Models.Add(value), statementObserver, cancellationToken);
        var labelAuthority = retentionInstalled
            ? """
              SELECT 1 FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
              JOIN retention_items i ON i.store_kind='session_event_content' AND i.source_item_id=e.event_id AND i.expires_at=c.expires_at
              WHERE e.session_id=f.session_id AND e.event_id=f.source_identity AND e.content_state='available'
                AND i.read_denied_at IS NULL AND i.deleted_at IS NULL AND i.error_code IS NULL
                AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at COLLATE BINARY > $now COLLATE BINARY))
              """
            : """
              SELECT 1 FROM session_events e JOIN session_event_content c ON c.event_id=e.event_id
              WHERE e.session_id=f.session_id AND e.event_id=f.source_identity AND e.content_state='available'
                AND c.expires_at=f.expires_at COLLATE BINARY AND c.expires_at COLLATE BINARY > $now COLLATE BINARY
              """;
        await ReadPairs(connection, transaction, "search", $"""
            SELECT f.session_id,f.normalized_text FROM local_workspace_session_search_facts f
            WHERE f.session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))
              AND (f.expires_at IS NULL OR f.expires_at COLLATE BINARY > $now COLLATE BINARY)
              AND (f.kind='skill' OR (f.kind='label' AND EXISTS(
                {labelAuthority}))
                OR f.kind='tool')
            GROUP BY f.session_id,f.normalized_text ORDER BY f.session_id,f.normalized_text COLLATE BINARY;
            """, ids, byId, static (row, value) => row.SearchTexts.Add(value), statementObserver, cancellationToken, now);
        using (var command = connection.CreateCommand())
        {
            statementObserver?.Invoke("activity");
            command.Transaction = transaction;
            command.CommandText = "SELECT session_id,kind,state,count FROM local_workspace_session_activity WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids)) ORDER BY session_id,kind;";
            command.Parameters.AddWithValue("$ids", ids);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (byId.TryGetValue(reader.GetString(0), out var row)) row.Activity[reader.GetString(1)] = new(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3));
        }
        var currentSkills = SkillProjectionReadService.ReadCurrentInvocationProjection(
            connection, transaction, byId.Keys.ToArray(), acceptedAt, registryAuthority);
        foreach (var row in rows)
        {
            if (currentSkills.TryGetValue(row.SessionId, out var projection))
                row.Activity["skill"] = projection.State == "current"
                    ? new("recorded", projection.InvocationCount)
                    : new(projection.State, null);
            else
                row.Activity["skill"] = new("not_observed", null);
        }
        using (var command = connection.CreateCommand())
        {
            statementObserver?.Invoke("tokens");
            command.Transaction = transaction;
            command.CommandText = """
                WITH ranked AS (
                  SELECT *,row_number() OVER(PARTITION BY session_id,execution_id ORDER BY authority_rank,authority,source_identity) ordinal
                  FROM local_workspace_token_observations WHERE session_id IN (SELECT CAST(value AS TEXT) FROM json_each($ids))), selected AS (SELECT * FROM ranked WHERE ordinal=1)
                SELECT session_id,execution_id,authority,input_tokens,output_tokens,total_tokens,reasoning_tokens,cache_read_tokens,cache_creation_tokens
                FROM selected ORDER BY session_id,execution_id;
                """;
            command.Parameters.AddWithValue("$ids", ids);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var observations = new Dictionary<string, List<TokenObservation>>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sessionId = reader.GetString(0);
                if (!observations.TryGetValue(sessionId, out var values)) observations[sessionId] = values = [];
                values.Add(new(reader.GetString(2), Nullable(reader,3), Nullable(reader,4), Nullable(reader,5), Nullable(reader,6), Nullable(reader,7), Nullable(reader,8)));
            }
            foreach (var pair in observations) if (byId.TryGetValue(pair.Key, out var row)) row.TokenAggregate = ReadTokens(pair.Value);
        }
        return new(Array.AsReadOnly(rows.Select(static row => (ILocalRepositorySessionSnapshotRow)row.Freeze()).ToArray()));
    }

    private static LocalWorkspaceTokenFacts ReadTokens(IReadOnlyList<TokenObservation> rows)
    {
        var total = rows.Count; var available = rows.Count(r => r.Values.Any(v => v is not null));
        var authority = rows.Select(r=>r.Authority).Distinct(StringComparer.Ordinal).Count()==1 ? rows[0].Authority : "mixed";
        var overflow = false;
        LocalWorkspaceFact<long> Fact(Func<TokenObservation,long?> select)
        {
            var values=rows.Select(select).ToArray(); var count=values.Count(v=>v is not null);
            if(count==0)return new("not_observed",null); if(count!=total)return new("capture_gap",null);
            try { long sum=0; foreach(var value in values) sum=checked(sum+value!.Value); return new("recorded",sum); }
            catch(OverflowException){overflow=true;return new("oversized",null);}
        }
        var input=Fact(r=>r.Input); var output=Fact(r=>r.Output); var producerTotal=Fact(r=>r.Total); var reasoning=Fact(r=>r.Reasoning); var cache=Fact(r=>r.CacheRead); var creation=Fact(r=>r.CacheCreation);
        var inconsistent=input.State=="recorded"&&cache.State=="recorded"&&cache.Value>input.Value;
        var componentGap = new[] { input, output, producerTotal, reasoning, cache, creation }.Any(f => f.State == "capture_gap");
        var overall=overflow?"oversized":inconsistent?"inconsistent":available==0?"not_observed":available<total||componentGap?"capture_gap":"recorded";
        LocalWorkspaceFact<long> derived;
        if(inconsistent) derived=new("inconsistent",null); else if(input.State!="recorded"||cache.State!="recorded") derived=new(input.State=="capture_gap"||cache.State=="capture_gap"?"capture_gap":"not_observed",null); else derived=new("recorded",input.Value!.Value-cache.Value!.Value);
        LocalWorkspaceFact<long> ratio;
        if(inconsistent) ratio=new("inconsistent",null); else if(input.State!="recorded"||cache.State!="recorded"||input.Value==0) ratio=new(input.State=="capture_gap"||cache.State=="capture_gap"?"capture_gap":"not_observed",null); else { var value=(BigInteger)cache.Value!.Value*10_000/input.Value!.Value; ratio=value>long.MaxValue?new("oversized",null):new("recorded",(long)value); }
        return new(authority,overall,available,total,input,output,producerTotal,reasoning,cache,creation,derived,ratio);
    }

    private sealed record TokenObservation(string Authority,long? Input,long? Output,long? Total,long? Reasoning,long? CacheRead,long? CacheCreation)
    { internal IEnumerable<long?> Values => [Input,Output,Total,Reasoning,CacheRead,CacheCreation]; }

    private static long? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task ReadPairs(SqliteConnection connection, SqliteTransaction transaction, string name, string sql, string ids, Dictionary<string, MutableRow> rows, Action<MutableRow, string> add, Action<string>? statementObserver, CancellationToken token, string? now = null)
    {
        statementObserver?.Invoke(name);
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.Parameters.AddWithValue("$ids", ids); if (now is not null) command.Parameters.AddWithValue("$now", now);
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
        private readonly string sourceState;
        private readonly string modelState;
        private readonly string timingState;
        private readonly string? startedAt;
        private readonly string? endedAt;
        private readonly string? lastSeenAt;
        private readonly long? lastSeenEpoch;
        private readonly long? duration;
        private readonly string notes;
        private readonly string revision;

        internal MutableRow(string sessionId, long sortGroup, long sortEpoch, string labelState, string? label, string status, string completeness, string sourceState, string modelState, string timingState, string? startedAt, string? endedAt, string? lastSeenAt, long? lastSeenEpoch, long? duration, string notes, string revision)
        {
            SessionId = sessionId; this.sortGroup = sortGroup; this.sortEpoch = sortEpoch;
            this.labelState = labelState; this.label = label; this.status = status; this.completeness = completeness; this.sourceState = sourceState; this.modelState = modelState;
            this.timingState = timingState; this.startedAt = startedAt; this.endedAt = endedAt; this.lastSeenAt = lastSeenAt; this.lastSeenEpoch = lastSeenEpoch;
            this.duration = duration; this.notes = notes; this.revision = revision;
        }

        internal string SessionId { get; }
        internal List<string> Sources { get; } = [];
        internal List<string> Models { get; } = [];
        internal List<string> SearchTexts { get; } = [];
        internal Dictionary<string, LocalWorkspaceFact<long>> Activity { get; } = new(StringComparer.Ordinal);
        internal LocalWorkspaceTokenFacts? TokenAggregate { get; set; }
        internal LocalWorkspaceProjectionRow Freeze()
        {
            LocalWorkspaceFact<long> A(string kind) => Activity.TryGetValue(kind, out var fact) ? fact : new("not_observed", null);
            var tokens = TokenAggregate ?? new("none", "not_observed", 0, 0, new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null));
            return new(SessionId, sortGroup, sortEpoch, labelState, label, status, completeness,
                new(sourceState, Array.AsReadOnly(Sources.ToArray())), new(modelState, Array.AsReadOnly(Models.ToArray())), new(A("skill"), A("tool"), A("subagent"), A("error"), A("retry")), tokens,
                timingState, startedAt, endedAt, lastSeenAt, lastSeenEpoch, duration, Array.AsReadOnly(notes.Length == 0 ? [] : notes.Split(',', StringSplitOptions.RemoveEmptyEntries)), Array.AsReadOnly(SearchTexts.ToArray()), revision);
        }
    }
}
