using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Persistence.Sqlite;

public sealed class SqliteAlertLifecycleStore : IAlertLifecycleStore
{
    private readonly string connectionString;
    private readonly TimeProvider timeProvider;

    public SqliteAlertLifecycleStore(string connectionString, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A connection string is required.", nameof(connectionString));
        this.connectionString = connectionString;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AlertLifecycleStoreResult Initialize()
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var version = AlertLifecycleSchemaV1.ReadVersion(connection, transaction);
            if (version is null)
            {
                if (AlertLifecycleSchemaV1.AnyObjectsExist(connection, transaction)) return Unavailable();
                AlertLifecycleSchemaV1.Create(connection, transaction);
            }
            if (!AlertLifecycleSchemaV1.IsValid(connection, transaction)) return Unavailable();
            transaction.Commit();
            return new(AlertLifecycleStoreStatus.Success);
        }
        catch (SqliteException exception) when (IsBusy(exception)) { return Busy(); }
        catch (SqliteException) { return Unavailable(); }
        catch (InvalidOperationException) { return Unavailable(); }
        catch (FormatException) { return Unavailable(); }
    }

    public AlertLifecycleStoreResult Get(string alertId)
    {
        if (!AlertLifecycleValidation.IsCanonicalAlertId(alertId)) return NotFound();
        try
        {
            using var connection = Open();
            if (!AlertLifecycleSchemaV1.IsValid(connection, null)) return Unavailable();
            if (!ReceiptExists(connection, null, alertId)) return NotFound();
            var latest = ReadLatest(connection, null, alertId);
            return new(AlertLifecycleStoreStatus.Success, Lifecycle: latest is null ? LazyOpen(alertId) : View(latest));
        }
        catch (SqliteException exception) when (IsBusy(exception)) { return Busy(); }
        catch (SqliteException) { return Unavailable(); }
    }

    public AlertLifecycleHistoryResult History(string alertId, int limit = 50)
    {
        if (limit is < 1 or > 100) return new(AlertLifecycleStoreStatus.Invalid, [], "alert_invalid_limit");
        if (!AlertLifecycleValidation.IsCanonicalAlertId(alertId)) return new(AlertLifecycleStoreStatus.NotFound, [], "alert_not_found");
        try
        {
            using var connection = Open();
            if (!AlertLifecycleSchemaV1.IsValid(connection, null)) return HistoryUnavailable();
            if (!ReceiptExists(connection, null, alertId)) return new(AlertLifecycleStoreStatus.NotFound, [], "alert_not_found");
            using var command = Command(connection, null,
                "SELECT event_id,alert_id,revision,expected_revision,action,previous_state,state,occurred_at,actor,reason_code,comment,idempotency_key,old_alert_id,new_alert_id,result_code FROM alert_lifecycle_events WHERE alert_id=$alert ORDER BY revision DESC LIMIT $limit;",
                ("$alert", alertId), ("$limit", limit));
            using var reader = command.ExecuteReader();
            var events = new List<AlertLifecycleEvent>();
            while (reader.Read()) events.Add(ReadEvent(reader));
            return new(AlertLifecycleStoreStatus.Success, events);
        }
        catch (SqliteException exception) when (IsBusy(exception)) { return new(AlertLifecycleStoreStatus.Busy, [], "alert_lifecycle_store_busy"); }
        catch (SqliteException) { return HistoryUnavailable(); }
    }

    public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) =>
        mutation.Actor == "local_user"
        && mutation.Action is AlertLifecycleAction.Acknowledge or AlertLifecycleAction.Dismiss or AlertLifecycleAction.Resolve or AlertLifecycleAction.Reopen
        && mutation.OldAlertId is null && mutation.NewAlertId is null
            ? Append(mutation, requireNewReceipt: false)
            : Invalid(mutation.Actor == "local_user" ? "alert_invalid_action" : ActorError(mutation.Actor));

    public AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation) =>
        mutation.Actor == "local_system"
        && mutation.Action == AlertLifecycleAction.Resolve
        && mutation.Comment is null && mutation.OldAlertId is null && mutation.NewAlertId is null
            ? Append(mutation, requireNewReceipt: false)
            : Invalid(mutation.Actor == "local_system" ? "alert_invalid_reevaluation" : ActorError(mutation.Actor));

    public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) =>
        mutation.Actor == "local_system"
        && mutation.Action == AlertLifecycleAction.Supersede
        && mutation.Comment is null
        && mutation.OldAlertId == mutation.AlertId
        && AlertLifecycleValidation.IsCanonicalAlertId(mutation.NewAlertId)
        && mutation.NewAlertId != mutation.AlertId
            ? Append(mutation, requireNewReceipt: true)
            : Invalid(mutation.Actor == "local_system" ? "alert_invalid_supersession" : ActorError(mutation.Actor));

    public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) =>
        mutation.Actor == "local_system"
        && mutation.Action == AlertLifecycleAction.SourceDeleted
        && mutation.Comment is null && mutation.OldAlertId is null && mutation.NewAlertId is null
            ? Append(mutation, requireNewReceipt: false)
            : Invalid(mutation.Actor == "local_system" ? "alert_invalid_source_deletion" : ActorError(mutation.Actor));

    private static string ActorError(string actor) =>
        actor is "local_user" or "local_system" ? "alert_invalid_actor" : "alert_invalid_request";

    private AlertLifecycleStoreResult Append(AlertLifecycleMutation mutation, bool requireNewReceipt)
    {
        var validation = Validate(mutation);
        if (validation is not null) return validation;
        var requestHash = RequestHash(mutation);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!AlertLifecycleSchemaV1.IsValid(connection, transaction)) return Unavailable();

            var prior = ReadByIdempotencyKey(connection, transaction, mutation.IdempotencyKey);
            if (prior is not null)
            {
                var priorHash = ReadRequestHash(connection, transaction, mutation.IdempotencyKey);
                transaction.Commit();
                return priorHash == requestHash
                    ? new(AlertLifecycleStoreStatus.Success, Lifecycle: View(prior), Event: prior, Replayed: true)
                    : Conflict("alert_idempotency_conflict");
            }

            if (!ReceiptExists(connection, transaction, mutation.AlertId)) return NotFound();
            if (requireNewReceipt && !ReceiptExists(connection, transaction, mutation.NewAlertId!)) return NotFound();
            var latest = ReadLatest(connection, transaction, mutation.AlertId);
            var current = latest?.State ?? AlertLifecycleState.Open;
            var revision = latest?.Revision ?? 0;
            if (revision != mutation.ExpectedRevision) return Conflict("alert_revision_conflict");
            if (!AlertLifecycleTransition.TryApply(current, mutation.Action, out var next)) return Conflict("alert_invalid_transition");

            var occurredAt = timeProvider.GetUtcNow().ToUniversalTime();
            var eventId = Hash("copilot-agent-observability/alert-lifecycle-event/v1\0" + mutation.IdempotencyKey);
            Execute(connection, transaction,
                "INSERT INTO alert_lifecycle_events(event_id,alert_id,revision,expected_revision,action,previous_state,state,occurred_at,actor,reason_code,comment,idempotency_key,request_hash,old_alert_id,new_alert_id,result_code) VALUES($event,$alert,$revision,$expected,$action,$previous,$state,$occurred,$actor,$reason,$comment,$key,$hash,$old,$new,'alert_lifecycle_updated');",
                ("$event", eventId), ("$alert", mutation.AlertId), ("$revision", revision + 1), ("$expected", mutation.ExpectedRevision), ("$action", Wire(mutation.Action)),
                ("$previous", Wire(current)), ("$state", Wire(next)), ("$occurred", occurredAt.ToString("O", CultureInfo.InvariantCulture)),
                ("$actor", mutation.Actor), ("$reason", mutation.ReasonCode), ("$comment", (object?)mutation.Comment ?? DBNull.Value),
                ("$key", mutation.IdempotencyKey), ("$hash", requestHash), ("$old", (object?)mutation.OldAlertId ?? DBNull.Value),
                ("$new", (object?)mutation.NewAlertId ?? DBNull.Value));
            transaction.Commit();
            var appended = new AlertLifecycleEvent(AlertLifecycleContractVersions.Lifecycle, eventId, mutation.AlertId, revision + 1, mutation.ExpectedRevision, mutation.Action,
                current, next, occurredAt, mutation.Actor, mutation.ReasonCode, mutation.Comment, mutation.IdempotencyKey,
                mutation.OldAlertId, mutation.NewAlertId, "alert_lifecycle_updated");
            return new(AlertLifecycleStoreStatus.Success, Lifecycle: View(appended), Event: appended);
        }
        catch (SqliteException exception) when (IsBusy(exception)) { return Busy(); }
        catch (SqliteException) { return Unavailable(); }
    }

    private static AlertLifecycleStoreResult? Validate(AlertLifecycleMutation value)
    {
        if (!AlertLifecycleValidation.IsCanonicalAlertId(value.AlertId) || value.ExpectedRevision < 0
            || value.Actor is not ("local_user" or "local_system")) return Invalid("alert_invalid_request");
        if (!AlertLifecycleValidation.IsReasonCode(value.ReasonCode)) return Invalid("alert_invalid_reason_code");
        if (!AlertLifecycleValidation.IsSanitizedComment(value.Comment)) return Invalid("alert_comment_not_sanitized");
        if (!AlertLifecycleValidation.IsIdempotencyKey(value.IdempotencyKey)) return Invalid("alert_invalid_idempotency_key");
        return null;
    }

    private static AlertLifecycleEvent? ReadLatest(SqliteConnection connection, SqliteTransaction? transaction, string alertId)
    {
        using var command = Command(connection, transaction,
            "SELECT event_id,alert_id,revision,expected_revision,action,previous_state,state,occurred_at,actor,reason_code,comment,idempotency_key,old_alert_id,new_alert_id,result_code FROM alert_lifecycle_events WHERE alert_id=$alert ORDER BY revision DESC LIMIT 1;", ("$alert", alertId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEvent(reader) : null;
    }

    private static AlertLifecycleEvent? ReadByIdempotencyKey(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = Command(connection, transaction,
            "SELECT event_id,alert_id,revision,expected_revision,action,previous_state,state,occurred_at,actor,reason_code,comment,idempotency_key,old_alert_id,new_alert_id,result_code FROM alert_lifecycle_events WHERE idempotency_key=$key;", ("$key", key));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEvent(reader) : null;
    }

    private static string? ReadRequestHash(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = Command(connection, transaction, "SELECT request_hash FROM alert_lifecycle_events WHERE idempotency_key=$key;", ("$key", key));
        return command.ExecuteScalar() as string;
    }

    private static AlertLifecycleEvent ReadEvent(SqliteDataReader reader) => new(
        AlertLifecycleContractVersions.Lifecycle, reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), Action(reader.GetString(4)),
        State(reader.GetString(5)), State(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetString(14));

    private static bool ReceiptExists(SqliteConnection connection, SqliteTransaction? transaction, string alertId)
    {
        using var command = Command(connection, transaction, "SELECT count(*) FROM alert_receipts WHERE alert_id=$alert;", ("$alert", alertId));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string RequestHash(AlertLifecycleMutation value) => Hash(JsonSerializer.Serialize(new
    {
        schema_version = AlertLifecycleContractVersions.Lifecycle,
        alert_id = value.AlertId,
        action = Wire(value.Action),
        expected_revision = value.ExpectedRevision,
        reason_code = value.ReasonCode,
        comment = value.Comment,
        actor = value.Actor,
        old_alert_id = value.OldAlertId,
        new_alert_id = value.NewAlertId,
    }));

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static AlertLifecycleView LazyOpen(string alertId) => new(AlertLifecycleContractVersions.Lifecycle, alertId, AlertLifecycleState.Open, 0, null);
    private static AlertLifecycleView View(AlertLifecycleEvent value) => new(value.SchemaVersion, value.AlertId, value.State, value.Revision, value.OccurredAt);
    private static AlertLifecycleStoreResult Invalid(string code) => new(AlertLifecycleStoreStatus.Invalid, code);
    private static AlertLifecycleStoreResult Conflict(string code) => new(AlertLifecycleStoreStatus.Conflict, code);
    private static AlertLifecycleStoreResult NotFound() => new(AlertLifecycleStoreStatus.NotFound, "alert_not_found");
    private static AlertLifecycleStoreResult Busy() => new(AlertLifecycleStoreStatus.Busy, "alert_lifecycle_store_busy");
    private static AlertLifecycleStoreResult Unavailable() => new(AlertLifecycleStoreStatus.Unavailable, "alert_lifecycle_store_unavailable");
    private static AlertLifecycleHistoryResult HistoryUnavailable() => new(AlertLifecycleStoreStatus.Unavailable, [], "alert_lifecycle_store_unavailable");
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString) { DefaultTimeout = 1 }; connection.Open(); using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1000;"; pragma.ExecuteNonQuery(); return connection; }
    private static bool IsBusy(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters) { using var command = Command(connection, transaction, sql, parameters); command.ExecuteNonQuery(); }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value); return command; }

    private static string Wire(AlertLifecycleState value) => value switch { AlertLifecycleState.Open => "open", AlertLifecycleState.Acknowledged => "acknowledged", AlertLifecycleState.Dismissed => "dismissed", AlertLifecycleState.Resolved => "resolved", AlertLifecycleState.Superseded => "superseded", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
    private static string Wire(AlertLifecycleAction value) => value switch { AlertLifecycleAction.Acknowledge => "acknowledge", AlertLifecycleAction.Dismiss => "dismiss", AlertLifecycleAction.Resolve => "resolve", AlertLifecycleAction.Reopen => "reopen", AlertLifecycleAction.Supersede => "supersede", AlertLifecycleAction.SourceDeleted => "source_deleted", _ => throw new ArgumentOutOfRangeException(nameof(value)) };
    private static AlertLifecycleState State(string value) => value switch { "open" => AlertLifecycleState.Open, "acknowledged" => AlertLifecycleState.Acknowledged, "dismissed" => AlertLifecycleState.Dismissed, "resolved" => AlertLifecycleState.Resolved, "superseded" => AlertLifecycleState.Superseded, _ => throw new FormatException() };
    private static AlertLifecycleAction Action(string value) => value switch { "acknowledge" => AlertLifecycleAction.Acknowledge, "dismiss" => AlertLifecycleAction.Dismiss, "resolve" => AlertLifecycleAction.Resolve, "reopen" => AlertLifecycleAction.Reopen, "supersede" => AlertLifecycleAction.Supersede, "source_deleted" => AlertLifecycleAction.SourceDeleted, _ => throw new FormatException() };
}
