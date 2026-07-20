using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    internal RetentionMutationResult? ReadOperationReceipt(string? operationId)
    {
        using var connection = OpenMutationConnection();
        using var transaction = BeginMutationTransaction(connection);
        var result = ReadOperationReceiptWithinTransaction(connection, transaction, operationId);
        transaction.Commit();
        return result;
    }

    internal RetentionMutationResult? ReadOperationReceiptWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT result_json,last_replayed_at FROM retention_operation_receipts WHERE operation_id=$operation_id;";
        command.Parameters.AddWithValue("$operation_id", operationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var result = JsonSerializer.Deserialize<RetentionMutationResult>(reader.GetString(0));
        return result is null || reader.IsDBNull(1)
            ? result
            : result with { ResultCode = RetentionMutationResultCodes.Replayed, IdempotentReplay = true };
    }

    internal void MarkOperationReceiptReplayedWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        DateTimeOffset replayedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE retention_operation_receipts SET last_replayed_at=$last_replayed_at WHERE operation_id=$operation_id;";
        command.Parameters.AddWithValue("$last_replayed_at", replayedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$operation_id", operationId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed);
    }

    internal string? ReadSessionIdForItemWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId)
    {
        using (var kind = connection.CreateCommand())
        {
            kind.Transaction = transaction;
            kind.CommandText = "SELECT store_kind FROM retention_items WHERE item_id=$item_id;";
            kind.Parameters.AddWithValue("$item_id", itemId);
            if (!string.Equals(kind.ExecuteScalar() as string, "session_event_content", StringComparison.Ordinal))
                return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.session_id
            FROM retention_items i
            JOIN session_events e ON e.event_id=i.source_item_id
            WHERE i.item_id=$item_id AND i.store_kind='session_event_content';
            """;
        command.Parameters.AddWithValue("$item_id", itemId);
        return command.ExecuteScalar() as string;
    }

    internal void InsertOperationReceiptWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionMutationResult result,
        string targetItemSetDigest)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO retention_operation_receipts(
                operation_id,schema_version,result_code,target_kind,target_id,operation,scope,target_item_count,
                result_json,completion_code,expected_version,result_version,target_item_set_digest,created_at,completed_at)
            VALUES($operation_id,$schema_version,$result_code,$target_kind,$target_id,$operation,$scope,$target_item_count,
                $result_json,$completion_code,$expected_version,$result_version,$target_item_set_digest,$created_at,$completed_at);
            """;
        command.Parameters.AddWithValue("$operation_id", result.OperationId);
        command.Parameters.AddWithValue("$schema_version", result.SchemaVersion);
        command.Parameters.AddWithValue("$result_code", result.ResultCode);
        command.Parameters.AddWithValue("$target_kind", RetentionMutationWire.TargetKind(result.TargetKind));
        command.Parameters.AddWithValue("$target_id", result.TargetId);
        command.Parameters.AddWithValue("$operation", RetentionMutationWire.Operation(result.Operation));
        command.Parameters.AddWithValue("$scope", RetentionMutationWire.Scope(result.Scope));
        command.Parameters.AddWithValue("$target_item_count", result.TargetItemCount);
        command.Parameters.AddWithValue("$result_json", JsonSerializer.Serialize(result));
        command.Parameters.AddWithValue("$completion_code", result.ResultCode);
        command.Parameters.AddWithValue("$expected_version", result.ExpectedVersion);
        command.Parameters.AddWithValue("$result_version", result.ResultVersion);
        command.Parameters.AddWithValue("$target_item_set_digest", targetItemSetDigest);
        command.Parameters.AddWithValue("$created_at", result.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$completed_at", result.CompletedAt.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }
}
