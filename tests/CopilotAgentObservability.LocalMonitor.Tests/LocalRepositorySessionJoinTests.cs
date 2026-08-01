using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositorySessionJoinTests
{
    private const long RawRecordId = 41;
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ObservedAt = "2026-08-01T01:02:03.1234567+00:00";
    private const string TraceId = "11111111111111111111111111111111";
    private const string SpanId = "2222222222222222";
    private const string SessionId = "01900000-0000-7000-8000-000000000020";
    private const string EventId = "01900000-0000-7000-8000-000000000021";

    [Theory]
    [InlineData("github-copilot-cli", "copilot-cli")]
    [InlineData("github-copilot-vscode", "vscode")]
    public void Preflight_ReadsCurrentSchemasAndReturnsExactCaptureAndSessionIdentity(
        string catalogSurface,
        string sessionSurface)
    {
        using var database = new TestDatabase();
        CreateCurrentSchemas(database.Path);
        using var connection = Open(database.Path);
        Execute(connection, "CREATE TABLE marker(value INTEGER);");
        using var transaction = connection.BeginTransaction();
        InsertProvenance(connection, transaction, RawRecordId, Digest, catalogSurface, "1.2.3", ObservedAt);
        InsertSessionEvent(connection, transaction, sessionSurface: sessionSurface);
        var beforePreflight = ReadPreflightSnapshot(connection);
        SetQueryOnly(connection, enabled: true);

        var provenance = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
            connection,
            transaction,
            RawRecordId,
            Digest);

        Assert.Equal(LocalRepositoryCaptureProvenanceStatus.Valid, provenance.Status);
        Assert.NotNull(provenance.Provenance);
        Assert.Equal(RawRecordId, provenance.Provenance.RawRecordId);
        Assert.Equal(Digest, provenance.Provenance.RawPayloadSha256);
        Assert.Equal(catalogSurface, provenance.Provenance.SourceSurface);
        Assert.Equal("1.2.3", provenance.Provenance.SourceApplicationVersion);
        Assert.Equal(ObservedAt, provenance.Provenance.ObservedAt.ToString("O"));

        var joined = LocalRepositorySessionEventJoin.ResolveContext(
            connection,
            transaction,
            provenance.Provenance,
            TraceId,
            SpanId);

        Assert.Equal(LocalRepositorySessionEventJoinStatus.Matched, joined.Status);
        Assert.Equal(EventId, joined.SessionEventId);
        Assert.Equal(SessionId, joined.SessionId);
        Assert.Equal(beforePreflight, ReadPreflightSnapshot(connection));
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);

        SetQueryOnly(connection, enabled: false);
        transaction.Rollback();
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM source_schema_observations WHERE raw_record_id=41;"));
        Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM session_events;"));
    }

    [Fact]
    public void CaptureProvenance_DigestRowsCannotSubstituteForTheFixedRawRecordIdentity()
    {
        using var database = new TestDatabase();
        CreateCurrentSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertProvenance(connection, transaction, RawRecordId, OtherDigest, "github-copilot-cli", "wrong", "2026-08-01T00:00:00.0000000+00:00");
        InsertProvenance(connection, transaction, RawRecordId + 1, Digest, "github-copilot-vscode", "right", ObservedAt);

        var result = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
            connection,
            transaction,
            RawRecordId,
            Digest);

        Assert.Equal(LocalRepositoryCaptureProvenanceStatus.CatalogSchemaViolation, result.Status);
        Assert.Null(result.Provenance);
    }

    [Fact]
    public void CaptureProvenance_MissingExactRowIsCatalogSchemaViolationWithoutFallback()
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        Execute(connection, "CREATE TABLE source_trace_attribution_observations(raw_record_id INTEGER, source_surface TEXT); INSERT INTO source_trace_attribution_observations VALUES(41,'github-copilot-cli');");
        using var transaction = connection.BeginTransaction();
        InsertMinimalProvenance(connection, transaction, RawRecordId + 1, "payload_sha256", Digest, "github-copilot-cli", null, ObservedAt);

        var result = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
            connection,
            transaction,
            RawRecordId,
            Digest);

        Assert.Equal(LocalRepositoryCaptureProvenanceStatus.CatalogSchemaViolation, result.Status);
        Assert.Null(result.Provenance);
    }

    [Fact]
    public void CaptureProvenance_DuplicateExactRowsAreCatalogSchemaViolation()
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalProvenance(connection, transaction, RawRecordId, "payload_sha256", Digest, "github-copilot-cli", null, ObservedAt);
        InsertMinimalProvenance(connection, transaction, RawRecordId, "payload_sha256", Digest, "github-copilot-cli", null, ObservedAt);

        var result = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
            connection,
            transaction,
            RawRecordId,
            Digest);

        Assert.Equal(LocalRepositoryCaptureProvenanceStatus.CatalogSchemaViolation, result.Status);
        Assert.Null(result.Provenance);
    }

    [Theory]
    [InlineData("deleted_before_digest_v10", Digest)]
    [InlineData("payload_sha256", null)]
    [InlineData(null, Digest)]
    public void CaptureProvenance_MalformedPayloadEvidenceIsCatalogSchemaViolation(
        string? inputEvidenceKind,
        string? storedDigest)
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalProvenance(connection, transaction, RawRecordId, inputEvidenceKind, storedDigest, "github-copilot-cli", null, ObservedAt);

        var result = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
            connection,
            transaction,
            RawRecordId,
            Digest);

        Assert.Equal(LocalRepositoryCaptureProvenanceStatus.CatalogSchemaViolation, result.Status);
        Assert.Null(result.Provenance);
    }

    [Fact]
    public void CaptureProvenance_DigestContradictionIsCatalogSchemaViolation()
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalProvenance(connection, transaction, RawRecordId, "payload_sha256", OtherDigest, "github-copilot-cli", null, ObservedAt);

        var result = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
            connection,
            transaction,
            RawRecordId,
            Digest);

        Assert.Equal(LocalRepositoryCaptureProvenanceStatus.CatalogSchemaViolation, result.Status);
        Assert.Null(result.Provenance);
    }

    [Theory]
    [InlineData("github-copilot-cli", null)]
    [InlineData("github-copilot-vscode", "v")]
    [InlineData("github-copilot-cli", "1234567890123456789012345678901234567890123456789012345678901234")]
    public void CaptureProvenance_AcceptsClosedSurfacesAndNullableVersionBounds(
        string surface,
        string? version)
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalProvenance(connection, transaction, RawRecordId, "payload_sha256", Digest, surface, version, ObservedAt);

        var result = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
            connection,
            transaction,
            RawRecordId,
            Digest);

        Assert.Equal(LocalRepositoryCaptureProvenanceStatus.Valid, result.Status);
        Assert.Equal(surface, result.Provenance!.SourceSurface);
        Assert.Equal(version, result.Provenance.SourceApplicationVersion);
    }

    [Theory]
    [InlineData(null, null, ObservedAt)]
    [InlineData("github-copilot", null, ObservedAt)]
    [InlineData("copilot-cli", null, ObservedAt)]
    [InlineData("github-copilot-cli", "", ObservedAt)]
    [InlineData("github-copilot-cli", "12345678901234567890123456789012345678901234567890123456789012345", ObservedAt)]
    [InlineData("github-copilot-cli", "1 2", ObservedAt)]
    [InlineData("github-copilot-cli", "1/2", ObservedAt)]
    [InlineData("github-copilot-cli", "1\\2", ObservedAt)]
    [InlineData("github-copilot-cli", "vé", ObservedAt)]
    [InlineData("github-copilot-cli", null, "2026-08-01T01:02:03.123456+00:00")]
    [InlineData("github-copilot-cli", null, "2026-08-01T01:02:03.1234567Z")]
    [InlineData("github-copilot-cli", null, "2026-02-30T01:02:03.1234567+00:00")]
    public void CaptureProvenance_RejectsUnsupportedSurfaceVersionAndTimestamp(
        string? surface,
        string? version,
        string observedAt)
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalProvenance(connection, transaction, RawRecordId, "payload_sha256", Digest, surface, version, observedAt);

        var result = LocalRepositorySessionEventJoin.ReadCaptureProvenance(
            connection,
            transaction,
            RawRecordId,
            Digest);

        Assert.Equal(LocalRepositoryCaptureProvenanceStatus.CatalogSchemaViolation, result.Status);
        Assert.Null(result.Provenance);
    }

    [Fact]
    public void ResolveContext_ZeroExactIdentityRowsWaitsForSession()
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        var beforePreflight = ReadPreflightSnapshot(connection);
        SetQueryOnly(connection, enabled: true);

        var result = LocalRepositorySessionEventJoin.ResolveContext(
            connection,
            transaction,
            Provenance("github-copilot-cli"),
            TraceId,
            SpanId);

        Assert.Equal(LocalRepositorySessionEventJoinStatus.WaitingSession, result.Status);
        Assert.Null(result.SessionEventId);
        Assert.Null(result.SessionId);
        Assert.Equal(beforePreflight, ReadPreflightSnapshot(connection));
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        SetQueryOnly(connection, enabled: false);
    }

    [Fact]
    public void ResolveContext_MultipleExactIdentityRowsAreTerminalConflict()
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalSessionEvent(connection, transaction, EventId, SessionId, "otel-exact", $"{TraceId}/{SpanId}", "otel.span", TraceId, "copilot-cli");
        InsertMinimalSessionEvent(connection, transaction, "01900000-0000-7000-8000-000000000022", "01900000-0000-7000-8000-000000000023", "otel-exact", $"{TraceId}/{SpanId}", "otel.span", TraceId, "copilot-cli");

        var result = LocalRepositorySessionEventJoin.ResolveContext(
            connection,
            transaction,
            Provenance("github-copilot-cli"),
            TraceId,
            SpanId);

        Assert.Equal(LocalRepositorySessionEventJoinStatus.CatalogSessionIdentityConflict, result.Status);
        Assert.Null(result.SessionEventId);
        Assert.Null(result.SessionId);
    }

    [Theory]
    [InlineData("other", TraceId, "copilot-cli")]
    [InlineData("otel.span", "33333333333333333333333333333333", "copilot-cli")]
    [InlineData("otel.span", TraceId, "vscode")]
    [InlineData("otel.span", TraceId, null)]
    [InlineData("otel.span", TraceId, "github-copilot-cli")]
    public void ResolveContext_AnyTupleMismatchIsTerminalConflict(
        string type,
        string? storedTraceId,
        string? storedSurface)
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalSessionEvent(connection, transaction, EventId, SessionId, "otel-exact", $"{TraceId}/{SpanId}", type, storedTraceId, storedSurface);

        var result = LocalRepositorySessionEventJoin.ResolveContext(
            connection,
            transaction,
            Provenance("github-copilot-cli"),
            TraceId,
            SpanId);

        Assert.Equal(LocalRepositorySessionEventJoinStatus.CatalogSessionIdentityConflict, result.Status);
        Assert.Null(result.SessionEventId);
        Assert.Null(result.SessionId);
    }

    [Fact]
    public void ResolveContext_DoesNotFallbackToOtherAdapterOrNearbyTrace()
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalSessionEvent(connection, transaction, EventId, SessionId, "raw-otlp", $"{TraceId}/{SpanId}", "otel.span", TraceId, "copilot-cli");
        InsertMinimalSessionEvent(connection, transaction, "01900000-0000-7000-8000-000000000022", SessionId, "otel-exact", $"{TraceId}/3333333333333333", "otel.span", TraceId, "copilot-cli");

        var result = LocalRepositorySessionEventJoin.ResolveContext(
            connection,
            transaction,
            Provenance("github-copilot-cli"),
            TraceId,
            SpanId);

        Assert.Equal(LocalRepositorySessionEventJoinStatus.WaitingSession, result.Status);
    }

    [Fact]
    public void ResolveContext_IdentityPredicatesRemainBinaryAgainstNoCaseColumns()
    {
        const string caseTraceId = "abcdefabcdefabcdefabcdefabcdefab";
        const string caseSpanId = "abcdefabcdefabcd";
        using var database = new TestDatabase();
        CreateNoCaseSessionSchema(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalSessionEvent(connection, transaction, EventId, SessionId, "OTEL-EXACT", $"{caseTraceId}/{caseSpanId}", "otel.span", caseTraceId, "copilot-cli");
        InsertMinimalSessionEvent(connection, transaction, "01900000-0000-7000-8000-000000000022", "01900000-0000-7000-8000-000000000023", "otel-exact", $"{caseTraceId.ToUpperInvariant()}/{caseSpanId.ToUpperInvariant()}", "otel.span", caseTraceId, "copilot-cli");

        var result = LocalRepositorySessionEventJoin.ResolveContext(
            connection,
            transaction,
            Provenance("github-copilot-cli"),
            caseTraceId,
            caseSpanId);

        Assert.Equal(LocalRepositorySessionEventJoinStatus.WaitingSession, result.Status);
        Assert.Null(result.SessionEventId);
        Assert.Null(result.SessionId);
    }

    [Theory]
    [InlineData("OTEL.SPAN", "abcdefabcdefabcdefabcdefabcdefab", "copilot-cli")]
    [InlineData("otel.span", "ABCDEFABCDEFABCDEFABCDEFABCDEFAB", "copilot-cli")]
    [InlineData("otel.span", "abcdefabcdefabcdefabcdefabcdefab", "COPILOT-CLI")]
    public void ResolveContext_CaseOnlyTupleMismatchIsTerminalConflict(
        string type,
        string storedTraceId,
        string storedSurface)
    {
        const string caseTraceId = "abcdefabcdefabcdefabcdefabcdefab";
        const string caseSpanId = "abcdefabcdefabcd";
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalSessionEvent(connection, transaction, EventId, SessionId, "otel-exact", $"{caseTraceId}/{caseSpanId}", type, storedTraceId, storedSurface);

        var result = LocalRepositorySessionEventJoin.ResolveContext(
            connection,
            transaction,
            Provenance("github-copilot-cli"),
            caseTraceId,
            caseSpanId);

        Assert.Equal(LocalRepositorySessionEventJoinStatus.CatalogSessionIdentityConflict, result.Status);
        Assert.Null(result.SessionEventId);
        Assert.Null(result.SessionId);
    }

    [Theory]
    [InlineData("GITHUB-COPILOT-CLI")]
    [InlineData("github-copilot-unknown")]
    public void ResolveContext_UnsupportedCatalogSurfaceIsCatalogSchemaViolation(string sourceSurface)
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();
        InsertMinimalSessionEvent(connection, transaction, EventId, SessionId, "otel-exact", $"{TraceId}/{SpanId}", "otel.span", TraceId, "copilot-cli");

        var result = LocalRepositorySessionEventJoin.ResolveContext(
            connection,
            transaction,
            Provenance(sourceSurface),
            TraceId,
            SpanId);

        Assert.Equal(LocalRepositorySessionEventJoinStatus.CatalogSchemaViolation, result.Status);
        Assert.Null(result.SessionEventId);
        Assert.Null(result.SessionId);
    }

    [Theory]
    [InlineData("1111111111111111111111111111111", SpanId)]
    [InlineData("1111111111111111111111111111111A", SpanId)]
    [InlineData(TraceId, "222222222222222")]
    [InlineData(TraceId, "222222222222222A")]
    public void ResolveContext_NoncanonicalTraceOrSpanIsCatalogSchemaViolation(
        string traceId,
        string spanId)
    {
        using var database = new TestDatabase();
        CreateMinimalSchemas(database.Path);
        using var connection = Open(database.Path);
        using var transaction = connection.BeginTransaction();

        var result = LocalRepositorySessionEventJoin.ResolveContext(
            connection,
            transaction,
            Provenance("github-copilot-cli"),
            traceId,
            spanId);

        Assert.Equal(LocalRepositorySessionEventJoinStatus.CatalogSchemaViolation, result.Status);
        Assert.Null(result.SessionEventId);
        Assert.Null(result.SessionId);
    }

    [Fact]
    public void Preflight_RejectsATransactionOwnedByAnotherDatabaseWithoutTakingOwnership()
    {
        using var first = new TestDatabase();
        using var second = new TestDatabase();
        CreateMinimalSchemas(first.Path);
        CreateMinimalSchemas(second.Path);
        using var firstConnection = Open(first.Path);
        using var secondConnection = Open(second.Path);
        using var firstTransaction = firstConnection.BeginTransaction();
        using var secondTransaction = secondConnection.BeginTransaction();

        Assert.Throws<ArgumentException>(() => LocalRepositorySessionEventJoin.ReadCaptureProvenance(
            firstConnection,
            secondTransaction,
            RawRecordId,
            Digest));
        Assert.Throws<ArgumentException>(() => LocalRepositorySessionEventJoin.ResolveContext(
            firstConnection,
            secondTransaction,
            Provenance("github-copilot-cli"),
            TraceId,
            SpanId));

        Execute(firstConnection, firstTransaction, "INSERT INTO marker VALUES(1);");
        Execute(secondConnection, secondTransaction, "INSERT INTO marker VALUES(2);");
        firstTransaction.Rollback();
        secondTransaction.Commit();
        Assert.Equal(0L, ScalarLong(firstConnection, "SELECT COUNT(*) FROM marker;"));
        Assert.Equal(1L, ScalarLong(secondConnection, "SELECT COUNT(*) FROM marker;"));
    }

    private static LocalRepositoryCaptureProvenance Provenance(string surface) =>
        new(RawRecordId, Digest, surface, null, DateTimeOffset.ParseExact(ObservedAt, "O", System.Globalization.CultureInfo.InvariantCulture));

    private static void CreateCurrentSchemas(string path)
    {
        new SqliteSourceCompatibilityStore(path).CreateSchema();
        new SqliteSessionStore(path).CreateSchema();
    }

    private static void CreateMinimalSchemas(string path)
    {
        using var connection = Open(path);
        Execute(connection, """
            CREATE TABLE source_schema_observations(
                raw_record_id,
                input_evidence_kind,
                raw_payload_sha256,
                source_surface,
                source_application_version,
                observed_at);
            CREATE TABLE session_events(
                event_id,
                session_id,
                source_adapter,
                source_event_id,
                type,
                trace_id,
                source_surface);
            CREATE TABLE marker(value INTEGER);
            """);
    }

    private static void CreateNoCaseSessionSchema(string path)
    {
        using var connection = Open(path);
        Execute(connection, """
            CREATE TABLE session_events(
                event_id,
                session_id,
                source_adapter TEXT COLLATE NOCASE,
                source_event_id TEXT COLLATE NOCASE,
                type,
                trace_id,
                source_surface);
            """);
    }

    private static void InsertProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        string digest,
        string sourceSurface,
        string? sourceApplicationVersion,
        string observedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_schema_observations(
                observation_id,raw_record_id,input_evidence_kind,raw_payload_sha256,
                ingest_batch_id,source_surface,source_application_version,source_adapter,
                adapter_version,schema_fingerprint,inventory_hash,compatibility_state,
                reason_code,next_action,capture_content_state,unknown_span_count,
                unknown_event_count,unknown_attribute_count,overflow_distinct_count,
                overflow_occurrence_count,observed_at)
            VALUES(
                $observation_id,$raw_record_id,'payload_sha256',$digest,
                $ingest_batch_id,$source_surface,$source_application_version,'raw-otlp',
                '1','synthetic','synthetic','supported',NULL,'none','available',0,0,0,0,0,$observed_at);
            """;
        command.Parameters.AddWithValue("$observation_id", $"source-observation-{rawRecordId}");
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$ingest_batch_id", $"batch-{rawRecordId}");
        command.Parameters.AddWithValue("$source_surface", sourceSurface);
        command.Parameters.AddWithValue("$source_application_version", sourceApplicationVersion is null ? DBNull.Value : sourceApplicationVersion);
        command.Parameters.AddWithValue("$observed_at", observedAt);
        command.ExecuteNonQuery();
    }

    private static void InsertMinimalProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        string? inputEvidenceKind,
        string? digest,
        string? sourceSurface,
        string? sourceApplicationVersion,
        string observedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_schema_observations VALUES(
                $raw_record_id,$input_evidence_kind,$digest,$source_surface,$source_application_version,$observed_at);
            """;
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        command.Parameters.AddWithValue("$input_evidence_kind", inputEvidenceKind is null ? DBNull.Value : inputEvidenceKind);
        command.Parameters.AddWithValue("$digest", digest is null ? DBNull.Value : digest);
        command.Parameters.AddWithValue("$source_surface", sourceSurface is null ? DBNull.Value : sourceSurface);
        command.Parameters.AddWithValue("$source_application_version", sourceApplicationVersion is null ? DBNull.Value : sourceApplicationVersion);
        command.Parameters.AddWithValue("$observed_at", observedAt);
        command.ExecuteNonQuery();
    }

    private static void InsertSessionEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionSurface)
    {
        Execute(connection, transaction, $"""
            INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES('{SessionId}','completed','full','{ObservedAt}','not_captured','{ObservedAt}','{ObservedAt}');
            INSERT INTO session_events(event_id,session_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
            VALUES('{EventId}','{SessionId}','{sessionSurface}','{TraceId}','otel-exact','{TraceId}/{SpanId}','otel.span','{ObservedAt}','not_captured');
            """);
    }

    private static void InsertMinimalSessionEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        string sessionId,
        string sourceAdapter,
        string sourceEventId,
        string type,
        string? traceId,
        string? sourceSurface)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO session_events VALUES($event_id,$session_id,$source_adapter,$source_event_id,$type,$trace_id,$source_surface);";
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$source_adapter", sourceAdapter);
        command.Parameters.AddWithValue("$source_event_id", sourceEventId);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$trace_id", traceId is null ? DBNull.Value : traceId);
        command.Parameters.AddWithValue("$source_surface", sourceSurface is null ? DBNull.Value : sourceSurface);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static PreflightSnapshot ReadPreflightSnapshot(SqliteConnection connection) =>
        new(
            ScalarLong(connection, "SELECT COUNT(*) FROM source_schema_observations;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM session_events;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM marker;"),
            ScalarLong(connection, "SELECT total_changes();"));

    private static void SetQueryOnly(SqliteConnection connection, bool enabled) =>
        Execute(connection, $"PRAGMA query_only={(enabled ? "ON" : "OFF")};");

    private sealed record PreflightSnapshot(
        long ProvenanceRowCount,
        long SessionEventRowCount,
        long MarkerRowCount,
        long TotalChanges);

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"local-repository-session-join-{Guid.NewGuid():N}");

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Directory.Delete(directory, recursive: true);
        }
    }
}
