using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

public sealed class SqliteSessionStore : ISessionStore
{
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;

    public SqliteSessionStore(string databasePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void CreateSchema()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open(initialize: true);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "SELECT version FROM schema_version WHERE component = 'session';";
        var existingVersion = versionCommand.ExecuteScalar();
        if (existingVersion is not null && Convert.ToInt32(existingVersion) != 1)
        {
            throw new InvalidOperationException("Unsupported Session schema version.");
        }

        if (existingVersion is null)
        {
            Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('session',1);");
        }

        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Write(SessionWriteBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        ValidateBatch(connection, transaction, batch);
        var orderedRuns = OrderRuns(batch.Detail.Runs);
        var orderedEvents = OrderEvents(batch.Detail.Events);
        var canonicalEventIds = ResolveCanonicalEventIds(connection, transaction, batch.Detail.Events);
        WriteSession(connection, transaction, batch.Detail.Session);

        foreach (var nativeId in batch.Detail.NativeIds)
        {
            Execute(connection, transaction, """
                INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at)
                VALUES($session_id,$source_surface,$native_session_id,$binding_kind,$observed_at)
                ON CONFLICT(source_surface,native_session_id) DO NOTHING;
                """,
                ("$session_id", Id(nativeId.SessionId)), ("$source_surface", SessionWire.ToWire(nativeId.SourceSurface)),
                ("$native_session_id", nativeId.NativeSessionId), ("$binding_kind", SessionWire.ToWire(nativeId.BindingKind)),
                ("$observed_at", Timestamp(nativeId.ObservedAt)));
        }

        foreach (var run in orderedRuns)
        {
            Execute(connection, transaction, """
                INSERT INTO session_runs(run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,started_at,ended_at,input_tokens,output_tokens,total_tokens,status)
                VALUES($run_id,$session_id,$source_surface,$native_run_id,$trace_id,$parent_run_id,$model,$started_at,$ended_at,$input_tokens,$output_tokens,$total_tokens,$status)
                ON CONFLICT(run_id) DO NOTHING;
                """,
                ("$run_id", Id(run.RunId)), ("$session_id", Id(run.SessionId)),
                ("$source_surface", run.SourceSurface is null ? null : SessionWire.ToWire(run.SourceSurface.Value)),
                ("$native_run_id", run.NativeRunId), ("$trace_id", run.TraceId), ("$parent_run_id", run.ParentRunId is null ? null : Id(run.ParentRunId.Value)),
                ("$model", run.Model), ("$started_at", Timestamp(run.StartedAt)), ("$ended_at", Timestamp(run.EndedAt)),
                ("$input_tokens", run.InputTokens), ("$output_tokens", run.OutputTokens), ("$total_tokens", run.TotalTokens),
                ("$status", SessionWire.ToWire(run.Status)));
        }

        foreach (var item in orderedEvents)
        {
            var eventId = canonicalEventIds[item.EventId];
            var parentEventId = item.ParentEventId is not null && canonicalEventIds.TryGetValue(item.ParentEventId.Value, out var canonicalParentEventId)
                ? canonicalParentEventId
                : item.ParentEventId;
            Execute(connection, transaction, """
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state)
                VALUES($event_id,$session_id,$run_id,$source_surface,$parent_event_id,$trace_id,$status,$source_adapter,$source_event_id,$type,$occurred_at,$content_state)
                ON CONFLICT(source_adapter,source_event_id) DO NOTHING;
                """,
                ("$event_id", Id(eventId)), ("$session_id", Id(item.SessionId)), ("$run_id", item.RunId is null ? null : Id(item.RunId.Value)),
                ("$source_surface", item.SourceSurface is null ? null : SessionWire.ToWire(item.SourceSurface.Value)),
                ("$parent_event_id", parentEventId is null ? null : Id(parentEventId.Value)), ("$trace_id", item.TraceId), ("$status", item.Status),
                ("$source_adapter", item.SourceAdapter), ("$source_event_id", item.SourceEventId), ("$type", item.Type),
                ("$occurred_at", Timestamp(item.OccurredAt)), ("$content_state", SessionWire.ToWire(item.ContentState)));
        }

        foreach (var content in batch.Content)
        {
            var eventId = canonicalEventIds[content.EventId];
            Execute(connection, transaction, """
                INSERT INTO session_event_content(event_id,content_kind,content_json,captured_at,expires_at)
                VALUES($event_id,$content_kind,$content_json,$captured_at,$expires_at)
                ON CONFLICT(event_id) DO NOTHING;
                """,
                ("$event_id", Id(eventId)), ("$content_kind", content.ContentKind), ("$content_json", content.ContentJson),
                ("$captured_at", Timestamp(content.CapturedAt)), ("$expires_at", Timestamp(content.ExpiresAt)));
        }

        transaction.Commit();
    }

    private static IReadOnlyList<ObservedSessionRun> OrderRuns(IReadOnlyList<ObservedSessionRun> runs) =>
        TopologicalOrder(runs, run => run.RunId, run => run.ParentRunId);

    private static IReadOnlyList<ObservedSessionEvent> OrderEvents(IReadOnlyList<ObservedSessionEvent> events) =>
        TopologicalOrder(events, item => item.EventId, item => item.ParentEventId);

    private static IReadOnlyList<T> TopologicalOrder<T>(
        IReadOnlyList<T> items,
        Func<T, Guid> getId,
        Func<T, Guid?> getParentId)
    {
        if (items.Select(getId).Distinct().Count() != items.Count)
        {
            throw new InvalidOperationException("Session aggregate relationship graph is invalid.");
        }

        var remaining = items.ToDictionary(getId);
        var ordered = new List<T>(items.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(item => getParentId(item) is not Guid parentId || !remaining.ContainsKey(parentId))
                .OrderBy(getId)
                .ToArray();
            if (ready.Length == 0)
            {
                throw new InvalidOperationException("Session aggregate relationship graph contains a cycle.");
            }

            foreach (var item in ready)
            {
                ordered.Add(item);
                remaining.Remove(getId(item));
            }
        }

        return ordered;
    }

    private static IReadOnlyDictionary<Guid, Guid> ResolveCanonicalEventIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ObservedSessionEvent> events)
    {
        var result = new Dictionary<Guid, Guid>();
        foreach (var group in events.GroupBy(item => (item.SourceAdapter, item.SourceEventId)))
        {
            var persistedId = ReadEventId(connection, transaction, group.Key.SourceAdapter, group.Key.SourceEventId);
            var canonicalId = persistedId ?? group.Min(item => item.EventId);
            foreach (var item in group)
            {
                result.Add(item.EventId, canonicalId);
            }
        }

        return result;
    }

    private static void ValidateBatch(SqliteConnection connection, SqliteTransaction transaction, SessionWriteBatch batch)
    {
        var sessionId = batch.Detail.Session.SessionId;
        var sessionIdText = Id(sessionId);
        var runIds = batch.Detail.Runs.Select(run => run.RunId).ToHashSet();
        var eventIds = batch.Detail.Events.Select(item => item.EventId).ToHashSet();

        if (batch.Detail.NativeIds.Any(nativeId => nativeId.SessionId != sessionId)
            || batch.Detail.Runs.Any(run => run.SessionId != sessionId)
            || batch.Detail.Events.Any(item => item.SessionId != sessionId)
            || batch.Content.Any(content => !eventIds.Contains(content.EventId)))
        {
            throw OwnershipViolation();
        }

        foreach (var nativeId in batch.Detail.NativeIds)
        {
            EnsureExistingOwnerMatches(
                connection,
                transaction,
                "SELECT session_id FROM session_native_ids WHERE source_surface=$first AND native_session_id=$second COLLATE BINARY;",
                sessionIdText,
                ("$first", SessionWire.ToWire(nativeId.SourceSurface)),
                ("$second", nativeId.NativeSessionId));
        }

        foreach (var run in batch.Detail.Runs)
        {
            EnsureExistingOwnerMatches(
                connection,
                transaction,
                "SELECT session_id FROM session_runs WHERE run_id=$first;",
                sessionIdText,
                ("$first", Id(run.RunId)));
            if (run.ParentRunId is not null && !runIds.Contains(run.ParentRunId.Value))
            {
                EnsureReferenceOwnedBySession(connection, transaction, "session_runs", "run_id", run.ParentRunId.Value, sessionIdText);
            }
        }

        foreach (var item in batch.Detail.Events)
        {
            EnsureExistingOwnerMatches(
                connection,
                transaction,
                "SELECT session_id FROM session_events WHERE event_id=$first;",
                sessionIdText,
                ("$first", Id(item.EventId)));
            EnsureExistingOwnerMatches(
                connection,
                transaction,
                "SELECT session_id FROM session_events WHERE source_adapter=$first AND source_event_id=$second;",
                sessionIdText,
                ("$first", item.SourceAdapter),
                ("$second", item.SourceEventId));

            if (item.RunId is not null && !runIds.Contains(item.RunId.Value))
            {
                EnsureReferenceOwnedBySession(connection, transaction, "session_runs", "run_id", item.RunId.Value, sessionIdText);
            }

            if (item.ParentEventId is not null && !eventIds.Contains(item.ParentEventId.Value))
            {
                EnsureReferenceOwnedBySession(connection, transaction, "session_events", "event_id", item.ParentEventId.Value, sessionIdText);
            }
        }
    }

    private static void EnsureReferenceOwnedBySession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string idColumn,
        Guid id,
        string expectedSessionId)
    {
        EnsureExistingOwnerMatches(
            connection,
            transaction,
            $"SELECT session_id FROM {table} WHERE {idColumn}=$first;",
            expectedSessionId,
            ("$first", Id(id)),
            requireExisting: true);
    }

    private static void EnsureExistingOwnerMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string expectedSessionId,
        (string Name, object? Value) first,
        (string Name, object? Value)? second = null,
        bool requireExisting = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Add(command, first.Name, first.Value);
        if (second is not null) Add(command, second.Value.Name, second.Value.Value);
        var owner = command.ExecuteScalar() as string;
        if ((requireExisting && owner is null)
            || (owner is not null && !string.Equals(owner, expectedSessionId, StringComparison.Ordinal)))
        {
            throw OwnershipViolation();
        }
    }

    private static InvalidOperationException OwnershipViolation() =>
        new("Session aggregate ownership validation failed.");

    public ObservedSession? Resolve(SessionSourceSurface sourceSurface, string nativeSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeSessionId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.session_id,s.status,s.completeness,s.repository,s.workspace,s.started_at,s.ended_at,s.last_seen_at,s.raw_retention_state,s.created_at,s.updated_at
            FROM session_native_ids n JOIN sessions s ON s.session_id=n.session_id
            WHERE n.source_surface=$source_surface AND n.native_session_id=$native_session_id COLLATE BINARY;
            """;
        Add(command, "$source_surface", SessionWire.ToWire(sourceSurface));
        Add(command, "$native_session_id", nativeSessionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    public IReadOnlyList<ObservedSession> ListMostRecent(int limit)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at
            FROM sessions ORDER BY last_seen_at DESC, session_id DESC LIMIT $limit;
            """;
        Add(command, "$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<ObservedSession>();
        while (reader.Read()) result.Add(ReadSession(reader));
        return result;
    }

    public SessionDetail? GetDetail(Guid sessionId)
    {
        using var connection = Open();
        var session = ReadSession(connection, sessionId);
        if (session is null) return null;

        var nativeIds = new List<SessionNativeId>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT session_id,source_surface,native_session_id,binding_kind,observed_at FROM session_native_ids WHERE session_id=$id ORDER BY observed_at,source_surface,native_session_id;";
            Add(command, "$id", Id(sessionId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) nativeIds.Add(new(Guid.Parse(reader.GetString(0)), SessionWire.ParseSourceSurface(reader.GetString(1)), reader.GetString(2), SessionWire.ParseBindingKind(reader.GetString(3)), ParseTimestamp(reader.GetString(4))));
        }

        var runs = new List<ObservedSessionRun>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT run_id,session_id,source_surface,native_run_id,trace_id,parent_run_id,model,status,started_at,ended_at,input_tokens,output_tokens,total_tokens FROM session_runs WHERE session_id=$id ORDER BY started_at,run_id;";
            Add(command, "$id", Id(sessionId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) runs.Add(new(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), NullableSurface(reader, 2), NullableString(reader, 3), NullableString(reader, 4), NullableGuid(reader, 5), NullableString(reader, 6),
                SessionWire.ParseStatus(reader.GetString(7)), NullableTimestamp(reader, 8), NullableTimestamp(reader, 9), NullableInt64(reader, 10), NullableInt64(reader, 11), NullableInt64(reader, 12)));
        }

        var events = new List<ObservedSessionEvent>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,content_state FROM session_events WHERE session_id=$id ORDER BY occurred_at,event_id;";
            Add(command, "$id", Id(sessionId));
            using var reader = command.ExecuteReader();
            while (reader.Read()) events.Add(new(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), NullableGuid(reader, 2), NullableSurface(reader, 3), NullableGuid(reader, 4), NullableString(reader, 5), NullableString(reader, 6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), ParseTimestamp(reader.GetString(10)), SessionWire.ParseContentState(reader.GetString(11))));
        }

        return new(session, nativeIds, runs, events);
    }
    public SessionContentLookup? GetContent(Guid sessionId, Guid eventId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.event_id,c.content_kind,c.content_json,c.captured_at,c.expires_at
            FROM session_event_content c
            JOIN session_events e ON e.event_id=c.event_id
            WHERE e.session_id=$session_id AND e.event_id=$event_id;
            """;
        Add(command, "$session_id", Id(sessionId));
        Add(command, "$event_id", Id(eventId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var content = new SessionEventContent(
            Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            ParseTimestamp(reader.GetString(3)), ParseTimestamp(reader.GetString(4)));
        return content.ExpiresAt > timeProvider.GetUtcNow()
            ? new(SessionContentState.Available, content)
            : new(SessionContentState.ExpiredPendingDeletion, null);
    }
    public SessionProjectionState? GetProjectionState(string projectorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectorKey);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT projector_key,projection_cursor,unsupported_event_version_count,updated_at FROM session_projection_state WHERE projector_key=$key;";
        Add(command, "$key", projectorKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetString(0), NullableInt64(reader, 1), reader.GetInt64(2), ParseTimestamp(reader.GetString(3)))
            : null;
    }

    public void UpsertProjectionState(SessionProjectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_projection_state(projector_key,projection_cursor,unsupported_event_version_count,updated_at)
            VALUES($key,$cursor,$unsupported,$updated_at)
            ON CONFLICT(projector_key) DO UPDATE SET projection_cursor=excluded.projection_cursor,
            unsupported_event_version_count=excluded.unsupported_event_version_count,updated_at=excluded.updated_at;
            """;
        Add(command, "$key", state.ProjectorKey);
        Add(command, "$cursor", state.ProjectionCursor);
        Add(command, "$unsupported", state.UnsupportedEventVersionCount);
        Add(command, "$updated_at", Timestamp(state.UpdatedAt));
        command.ExecuteNonQuery();
    }

    private static void WriteSession(SqliteConnection connection, SqliteTransaction transaction, ObservedSession value) =>
        Execute(connection, transaction, """
            INSERT INTO sessions(session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES($session_id,$status,$completeness,$repository,$workspace,$started_at,$ended_at,$last_seen_at,$raw_retention_state,$created_at,$updated_at)
            ON CONFLICT(session_id) DO UPDATE SET status=excluded.status,completeness=excluded.completeness,repository=excluded.repository,workspace=excluded.workspace,
            started_at=excluded.started_at,ended_at=excluded.ended_at,last_seen_at=excluded.last_seen_at,raw_retention_state=excluded.raw_retention_state,updated_at=excluded.updated_at;
            """,
            ("$session_id", Id(value.SessionId)), ("$status", SessionWire.ToWire(value.Status)), ("$completeness", SessionWire.ToWire(value.Completeness)),
            ("$repository", value.Repository), ("$workspace", value.Workspace), ("$started_at", Timestamp(value.StartedAt)), ("$ended_at", Timestamp(value.EndedAt)),
            ("$last_seen_at", Timestamp(value.LastSeenAt)), ("$raw_retention_state", SessionWire.ToWire(value.RawRetentionState)),
            ("$created_at", Timestamp(value.CreatedAt)), ("$updated_at", Timestamp(value.UpdatedAt)));

    private static Guid? ReadEventId(SqliteConnection connection, SqliteTransaction transaction, string adapter, string sourceEventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT event_id FROM session_events WHERE source_adapter=$adapter AND source_event_id=$source_event_id;";
        Add(command, "$adapter", adapter);
        Add(command, "$source_event_id", sourceEventId);
        return command.ExecuteScalar() is string value ? Guid.Parse(value) : null;
    }

    private static ObservedSession? ReadSession(SqliteConnection connection, Guid sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id,status,completeness,repository,workspace,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at FROM sessions WHERE session_id=$id;";
        Add(command, "$id", Id(sessionId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    private static ObservedSession ReadSession(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), SessionWire.ParseStatus(reader.GetString(1)), SessionWire.ParseCompleteness(reader.GetString(2)),
        NullableString(reader, 3), NullableString(reader, 4), NullableTimestamp(reader, 5), NullableTimestamp(reader, 6), ParseTimestamp(reader.GetString(7)),
        SessionWire.ParseRawRetentionState(reader.GetString(8)), ParseTimestamp(reader.GetString(9)), ParseTimestamp(reader.GetString(10)));

    private static string Id(Guid value) => value.ToString("D");
    private static string Timestamp(DateTimeOffset value) => value.ToString("O");
    private static string? Timestamp(DateTimeOffset? value) => value?.ToString("O");
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? NullableTimestamp(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid? NullableGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    private static long? NullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static SessionSourceSurface? NullableSurface(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : SessionWire.ParseSourceSurface(reader.GetString(ordinal));

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private SqliteConnection Open(bool initialize = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON;");
        Execute(connection, "PRAGMA busy_timeout=5000;");
        if (initialize)
        {
            Execute(connection, "PRAGMA journal_mode=WAL;");
        }

        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS sessions (
            session_id TEXT PRIMARY KEY,
            status TEXT NOT NULL CHECK (status IN ('active','completed','failed','unknown')),
            completeness TEXT NOT NULL CHECK (completeness IN ('unbound','partial','rich','full')),
            repository TEXT NULL,
            workspace TEXT NULL,
            started_at TEXT NULL,
            ended_at TEXT NULL,
            last_seen_at TEXT NOT NULL,
            raw_retention_state TEXT NOT NULL CHECK (raw_retention_state IN ('expiring','expired_pending_deletion','not_captured')),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS session_native_ids (
            session_id TEXT NOT NULL,
            source_surface TEXT NOT NULL CHECK (source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown')),
            native_session_id TEXT NOT NULL,
            binding_kind TEXT NOT NULL CHECK (binding_kind IN ('native','explicit_resume','explicit_handoff','trace_context')),
            observed_at TEXT NOT NULL,
            PRIMARY KEY (source_surface, native_session_id),
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS session_runs (
            run_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            source_surface TEXT NULL CHECK (source_surface IS NULL OR source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown')),
            native_run_id TEXT NULL,
            trace_id TEXT NULL,
            parent_run_id TEXT NULL,
            model TEXT NULL,
            started_at TEXT NULL,
            ended_at TEXT NULL,
            input_tokens INTEGER NULL CHECK (input_tokens IS NULL OR input_tokens >= 0),
            output_tokens INTEGER NULL CHECK (output_tokens IS NULL OR output_tokens >= 0),
            total_tokens INTEGER NULL CHECK (total_tokens IS NULL OR total_tokens >= 0),
            status TEXT NOT NULL CHECK (status IN ('active','completed','failed','unknown')),
            UNIQUE (session_id, run_id),
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
            FOREIGN KEY (session_id, parent_run_id) REFERENCES session_runs(session_id, run_id)
        );

        CREATE TABLE IF NOT EXISTS session_events (
            event_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            run_id TEXT NULL,
            source_surface TEXT NULL CHECK (source_surface IS NULL OR source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown')),
            parent_event_id TEXT NULL,
            trace_id TEXT NULL,
            status TEXT NULL,
            source_adapter TEXT NOT NULL,
            source_event_id TEXT NOT NULL,
            type TEXT NOT NULL,
            occurred_at TEXT NOT NULL,
            content_state TEXT NOT NULL CHECK (content_state IN ('available','not_captured','redacted','unsupported','expired_pending_deletion')),
            UNIQUE (source_adapter, source_event_id),
            UNIQUE (session_id, event_id),
            FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
            FOREIGN KEY (session_id, run_id) REFERENCES session_runs(session_id, run_id),
            FOREIGN KEY (session_id, parent_event_id) REFERENCES session_events(session_id, event_id)
        );

        CREATE TABLE IF NOT EXISTS session_event_content (
            event_id TEXT PRIMARY KEY,
            content_kind TEXT NOT NULL,
            content_json TEXT NOT NULL,
            captured_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            FOREIGN KEY (event_id) REFERENCES session_events(event_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS session_projection_state (
            projector_key TEXT PRIMARY KEY,
            projection_cursor INTEGER NULL CHECK (projection_cursor IS NULL OR projection_cursor >= 0),
            unsupported_event_version_count INTEGER NOT NULL CHECK (unsupported_event_version_count >= 0),
            updated_at TEXT NOT NULL
        );
        """;
}
