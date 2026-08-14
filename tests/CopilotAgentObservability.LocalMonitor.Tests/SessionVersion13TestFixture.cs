using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

internal static class SessionVersion13TestFixture
{
    private const string SkillTraceId = "99999999999999999999999999999999";

    internal static Version13RetentionBackedDiscriminator CreateRetentionBackedDiscriminator(
        string databasePath,
        RetentionCatalogContext retentionContext,
        DateTimeOffset capturedAt,
        bool retainedByPolicy,
        bool includeInstalledSkillDescendants,
        string identity)
    {
        var eventClock = new FixtureTimeProvider(capturedAt);
        var store = new SqliteSessionStore(databasePath, retentionContext, eventClock);
        store.CreateSchema();
        using (var payload = JsonDocument.Parse("{\"reason\":\"error\"}"))
        {
            new SessionEventNormalizer(store, eventClock).NormalizeAndWrite(new(
                1,
                "copilot-compatible-hook",
                "copilot-cli",
                $"{identity}-session",
                [new($"{identity}-event", "SessionEnd", capturedAt.ToString("O", CultureInfo.InvariantCulture), payload.RootElement.Clone())],
                SourceApplicationVersion: "1.0.0",
                AdapterVersion: "hook-v1",
                NormalizationVersion: "session-normalization-v1"));
        }

        string sessionId;
        string eventId;
        string itemId;
        DateTimeOffset skillObservedAt;
        using (var connection = Open(databasePath))
        {
            EnsureCompleteRetentionCoverage(connection);
            sessionId = Scalar<string>(connection, "SELECT session_id FROM sessions WHERE rowid=(SELECT MAX(rowid) FROM sessions);");
            eventId = Scalar<string>(connection, "SELECT event_id FROM session_events WHERE source_event_id=$id;", $"{identity}-event");
            itemId = Scalar<string>(connection, "SELECT item_id FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$id;", eventId);
            if (retainedByPolicy)
            {
                using var pin = connection.CreateCommand();
                pin.CommandText = "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE item_id=$item;";
                pin.Parameters.AddWithValue("$item", itemId);
                if (pin.ExecuteNonQuery() != 1) throw new InvalidOperationException("Unable to pin the Session migration fixture.");
            }
            skillObservedAt = NextSkillObservationTime(connection, capturedAt.AddDays(89));
        }

        if (includeInstalledSkillDescendants)
            PublishSessionBoundSkillProjection(
                databasePath,
                retentionContext,
                skillObservedAt,
                identity,
                sessionId);

        string expiresAt;
        byte[] ownershipReceipt;
        using (var connection = Open(databasePath))
        {
            expiresAt = Scalar<string>(connection, "SELECT expires_at FROM session_event_content WHERE event_id=$id;", eventId);
            ownershipReceipt = Scalar<byte[]>(connection, "SELECT ownership_receipt FROM retention_items WHERE item_id=$id;", itemId);
            DowngradeSessionEvents(connection);
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        return new(sessionId, eventId, itemId, expiresAt, ownershipReceipt);
    }

    internal static void DowngradeSessionEvents(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON;");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE session_events RENAME TO session_events_v14;
            CREATE TABLE session_events (
                event_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                run_id TEXT NULL,
                source_surface TEXT NULL CHECK (source_surface IS NULL OR source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown','claude-code')),
                parent_event_id TEXT NULL,
                trace_id TEXT NULL,
                status TEXT NULL,
                source_adapter TEXT NOT NULL,
                source_event_id TEXT NOT NULL,
                type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                content_state TEXT NOT NULL CHECK (content_state IN ('available','not_captured','redacted','unsupported','expired_pending_deletion')),
                source_application_version TEXT NULL,
                adapter_version TEXT NULL,
                schema_fingerprint TEXT NULL,
                normalization_version TEXT NULL,
                match_kind TEXT NULL CHECK (match_kind IS NULL OR match_kind IN ('exact_native','explicit_link','trace_continuity','conversation_id','none')),
                UNIQUE (source_adapter, source_event_id),
                UNIQUE (session_id, event_id),
                FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
                FOREIGN KEY (session_id, run_id) REFERENCES session_runs(session_id, run_id),
                FOREIGN KEY (session_id, parent_event_id) REFERENCES session_events(session_id, event_id)
            );
            INSERT INTO session_events(
                event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,
                occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind)
            SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,
                occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind
            FROM session_events_v14;
            DROP TABLE session_events_v14;
            UPDATE schema_version SET version=13 WHERE component='session';
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        Execute(connection, "PRAGMA legacy_alter_table=OFF; PRAGMA foreign_keys=ON;");
    }

    private static void EnsureCompleteRetentionCoverage(SqliteConnection connection) => Execute(
        connection,
        """
        INSERT INTO retention_adapter_coverage(store_kind,coverage_version)
        VALUES
            ('session_event_content',1),
            ('raw_record',1),
            ('analysis_run_raw',1),
            ('sensitive_bundle',1),
            ('analysis_sdk_directory',1)
        ON CONFLICT(store_kind) DO UPDATE SET coverage_version=excluded.coverage_version;
        """);

    private static DateTimeOffset NextSkillObservationTime(
        SqliteConnection connection,
        DateTimeOffset minimum)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(value)
            FROM (
                SELECT MAX(received_at) AS value FROM raw_records
                UNION ALL
                SELECT MAX(observed_at) AS value FROM source_schema_observations
                UNION ALL
                SELECT MAX(updated_at) AS value FROM skill_projection_generations
            );
            """;
        var latestValue = command.ExecuteScalar();
        if (latestValue is not string latest
            || !DateTimeOffset.TryParse(
                latest,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var latestAt)
            || latestAt < minimum)
        {
            return minimum;
        }
        return latestAt.AddSeconds(1);
    }

    private static void PublishSessionBoundSkillProjection(
        string databasePath,
        RetentionCatalogContext retentionContext,
        DateTimeOffset observedAt,
        string identity,
        string sessionId)
    {
        var nativeSessionId = $"{identity}-session";
        var payload = SessionBoundSkillPayload(nativeSessionId);
        new SqliteSourceCompatibilityStore(
            databasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        var inventory = OtlpJsonStructuralWalker.Build(payload, observedAt);
        var decision = SourceCompatibilityEvaluator.Assess(
            "github-copilot-cli",
            "1.0.74",
            inventory,
            observedRecognizedCount: 1,
            VerifiedSourceFingerprintRegistry.Create([], [], []));
        var observation = SourceObservationBatchDraft.Create(
            $"{identity}-skill-observation",
            "github-copilot-cli",
            "1.0.74",
            "github-copilot-otel",
            "adapter-1",
            inventory,
            decision,
            SourceCaptureContentState.Available,
            observedAt,
            [TraceSourceVersionResolutionDraft.Create(
                SkillTraceId,
                TraceSourceVersionResolutionState.Resolved,
                "1.0.74")]);
        var ingestionClock = new FixtureTimeProvider(observedAt);
        new SqliteIngestionCommitStore(
            databasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter,
            ingestionClock).Commit(ValidatedIngestionBatch.Create(
                RawOtlpIngestor.CreateRecordFromPayloadJson(payload, observedAt),
                observation));

        var workerClock = new FixtureTimeProvider(observedAt.AddSeconds(1));
        var rawStore = new RawTelemetryStore(
            databasePath,
            retentionContext,
            workerClock,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        var worker = new SkillProjectionWorker(
            new SqliteSkillProjectionStore(databasePath, rawStore),
            timeProvider: workerClock);
        for (var attempt = 0; attempt < 8 && !HasSessionBoundSkillProjection(databasePath, sessionId); attempt++)
        {
            var outcome = worker.RunNextAsync(workerClock.GetUtcNow()).GetAwaiter().GetResult();
            if (outcome == SkillProjectionWorkOutcome.NoWork)
                break;
        }

        using var validation = Open(databasePath);
        SourceCompatibilitySchemaV11.Validate(validation, transaction: null);
        SkillProjectionSchemaV1.Validate(validation, transaction: null);
        if (Scalar<long>(validation, "SELECT COUNT(*) FROM skill_projection_invocations WHERE session_id=$id;", sessionId) != 1
            || Scalar<long>(validation, "SELECT COUNT(*) FROM skill_projection_inventories WHERE session_id=$id;", sessionId) != 1
            || Scalar<long>(validation, "SELECT COUNT(*) FROM skill_projection_sdk_claims;") != 0)
        {
            throw new InvalidOperationException("Unable to publish the Session-bound Skill projection fixture.");
        }
    }

    private static bool HasSessionBoundSkillProjection(string databasePath, string sessionId)
    {
        using var connection = Open(databasePath);
        return Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM skill_projection_invocations WHERE session_id=$id;",
                sessionId) == 1
            && Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM skill_projection_inventories WHERE session_id=$id;",
                sessionId) == 1;
    }

    private static string SessionBoundSkillPayload(string nativeSessionId) =>
        """
        {"resourceSpans":[{
          "resource":{"attributes":[
            {"key":"service.version","value":{"stringValue":"1.0.74"}},
            {"key":"client.kind","value":{"stringValue":"copilot-cli"}}
          ]},
          "scopeSpans":[{"spans":[{
            "traceId":"TRACE_ID",
            "spanId":"2222222222222222",
            "name":"execute_tool skill",
            "attributes":[
              {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
              {"key":"gen_ai.tool.name","value":{"stringValue":"skill"}},
              {"key":"gen_ai.conversation.id","value":{"stringValue":NATIVE_SESSION_ID}},
              {"key":"github.copilot.skill.name","value":{"stringValue":"synthetic-skill"}},
              {"key":"github.copilot.skill.source","value":{"stringValue":"synthetic-source"}},
              {"key":"github.copilot.skill.invocation_trigger","value":{"stringValue":"synthetic-trigger"}},
              {"key":"github.copilot.context.skills","value":{"arrayValue":{"values":[
                {"stringValue":"synthetic-skill"}
              ]}}}
            ]
          }]}]
        }]}
        """
        .Replace("TRACE_ID", SkillTraceId, StringComparison.Ordinal)
        .Replace("NATIVE_SESSION_ID", JsonSerializer.Serialize(nativeSessionId), StringComparison.Ordinal);

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql, object? parameter = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameter is not null) command.Parameters.AddWithValue("$id", parameter);
        return (T)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal sealed record Version13RetentionBackedDiscriminator(
        string SessionId,
        string EventId,
        string ItemId,
        string ExpiresAt,
        byte[] OwnershipReceipt);

    private sealed class FixtureTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
