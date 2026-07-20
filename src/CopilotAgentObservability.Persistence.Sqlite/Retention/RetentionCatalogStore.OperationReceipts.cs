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
        command.CommandText = "SELECT result_json FROM retention_operation_receipts WHERE operation_id=$operation_id;";
        command.Parameters.AddWithValue("$operation_id", operationId);
        var value = command.ExecuteScalar() as string;
        return value is null ? null : JsonSerializer.Deserialize<RetentionMutationResult>(value);
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
