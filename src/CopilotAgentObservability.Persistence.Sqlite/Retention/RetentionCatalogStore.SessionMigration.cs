using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed record RetentionSessionEventContentForMigration(
    string ContentKind,
    string ContentJson,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt);

public sealed partial class RetentionCatalogStore
{
    private static readonly IReadOnlyDictionary<(string Type, string Name), RetentionSchemaObjectFingerprint> CurrentV1Objects =
        new Dictionary<(string Type, string Name), RetentionSchemaObjectFingerprint>
        {
            [("index", "IX_retention_analysis_sdk_directory_members_deletion_order")] = new("retention_analysis_sdk_directory_members", "be867a7805e5b5335d524af8a3600dfdfb7d52325b54d44438d41737cfe43322"),
            [("index", "IX_retention_analysis_sdk_directory_reservations_phase_updated")] = new("retention_analysis_sdk_directory_reservations", "0d1de65c2ed087e86e71593e1cbf1c5acdb3384da9fadbe1b5298cdb484df8af"),
            [("index", "IX_retention_audit_events_target")] = new("retention_audit_events", "684eda941c78e859d2a5670447da0ac79fb6709d7ccffb5d16f16d248ba114c2"),
            [("index", "IX_retention_confirmation_bindings_expiry")] = new("retention_confirmation_bindings", "ec27b5e6b1e0db83464f5d95443a094717e21d9b61a1e0bf8ed8b10996aef41f"),
            [("index", "IX_retention_confirmation_bindings_one_active_preview")] = new("retention_confirmation_bindings", "331388d240e34aa3fa7c3dd33892c562b6191e6a7d9c1e4aa87f7ae1958199ba"),
            [("index", "IX_retention_confirmation_bindings_preview")] = new("retention_confirmation_bindings", "3d26025142be8a558d40cab74a3bfa649005fbec4999db3c4dc88dfe5172d9c2"),
            [("index", "IX_retention_confirmation_bindings_token_hash")] = new("retention_confirmation_bindings", "fc32c9b8b214715803a2d2dbf61d7117bd11718c8a0a2e393d3d27828a2a3f97"),
            [("index", "IX_retention_file_capture_members_deletion_order")] = new("retention_file_capture_members", "0229fc5f64d25965c3ff76dfb4de569579b9818fce8f1cf668ad69d7058b7821"),
            [("index", "IX_retention_file_capture_reservations_phase_updated")] = new("retention_file_capture_reservations", "15af236347c43f8e9af7c173c1faea32cd685a3673cb6012f4e56ac902d54e2a"),
            [("index", "IX_retention_items_expiry")] = new("retention_items", "e16de7a1637159a14c6464ef267e9a7bb5f3e3950dd5980541b1801a84c3372a"),
            [("index", "IX_retention_items_worker_order")] = new("retention_items", "9da9c4ca6e050ef84ae0df41db82ce803994022b232a987f4159408e62574da7"),
            [("index", "IX_retention_leases_kind_expiry")] = new("retention_leases", "e3e3128021d3fc951b101f52743af28882d8a39a0d504ea0124ab09915804935"),
            [("index", "IX_retention_legacy_bundle_journal_root_locator")] = new("retention_legacy_bundle_journal", "8add251ebc8274b4b94f4d92389302590a48f62fe25facd6c318f2151d29a42c"),
            [("index", "IX_retention_mutation_idempotency_expiry")] = new("retention_mutation_idempotency", "e0c9041ee33d4eabffee98046f5f3e47e2a13228042b7f9b3c3cdb50aa8aae5f"),
            [("index", "IX_retention_mutation_previews_digest")] = new("retention_mutation_previews", "003e7c8f44ff22be5787f482eea24fc131d4f44eb9ef75ed336598b023a3c6c1"),
            [("index", "IX_retention_mutation_previews_expiry")] = new("retention_mutation_previews", "e7944f9dee9bb3794d70d4e2599429aaa0ee50ea594836652128b8ba3b940806"),
            [("index", "IX_retention_mutation_previews_target")] = new("retention_mutation_previews", "86a7d59163051b9817f82f00eee62f4705cd62322664e3d1cb93bd203595c592"),
            [("index", "IX_retention_operation_receipts_target")] = new("retention_operation_receipts", "b8e147a814e095e0cfefad81dad0079e5972ad7669261bb98bba39b364a99306"),
            [("table", "retention_adapter_coverage")] = new("retention_adapter_coverage", "527ca898cc9bbacc5fa88652a2df0e573c2b5621938c23784ecb303b8c36edec"),
            [("table", "retention_analysis_sdk_directory_members")] = new("retention_analysis_sdk_directory_members", "083382ad0c03baf5e848cf55f5cf53cedb37a5659270a65d1c6973fedae0b13e"),
            [("table", "retention_analysis_sdk_directory_reservations")] = new("retention_analysis_sdk_directory_reservations", "f05f7a167989a942d43cc41ef8ca01be626ae3184d548e2bebced85b8185ac62"),
            [("table", "retention_audit_events")] = new("retention_audit_events", "ac38156b2610e7e5c89848cb8eac0cd4e622b03739e324d39a6ca5cc0b299dbc"),
            [("table", "retention_capture_journal")] = new("retention_capture_journal", "0cfd9439056f6409f9d9de6f8a263facf121b4db8da36a2db929dd3461370fd4"),
            [("table", "retention_component_versions")] = new("retention_component_versions", "1eb27baba97205b5dca799170857fba8efa0a6a390472d98a17ac6d337143d50"),
            [("table", "retention_confirmation_bindings")] = new("retention_confirmation_bindings", "3ff3baa2e46649566657105366c4578f908d7f4c48566d30525c93e563b35a97"),
            [("table", "retention_delete_journal")] = new("retention_delete_journal", "db447b3c1cf51f68d41be3642cc97264ad5d6d7e15b082bdbf207263d6446660"),
            [("table", "retention_file_capture_members")] = new("retention_file_capture_members", "26c7faf0e59bee4ea73c2352de056ed188aed9c520916f06e3b5b8366305f5ce"),
            [("table", "retention_file_capture_reservations")] = new("retention_file_capture_reservations", "8f82c894d35868e9e9e019654a938ee97c918bc400dd6b104eb039364dd1f195"),
            [("table", "retention_items")] = new("retention_items", "bc5e73df657236a88095bf1b5cd010c1d5340dc2fd52f98b03cc934b4a913222"),
            [("table", "retention_leases")] = new("retention_leases", "fba428f909acb066565faa7d7bf5c3caba89d4e5a2a83fa9fbc12b30caa95db5"),
            [("table", "retention_legacy_bundle_blockers")] = new("retention_legacy_bundle_blockers", "9f4dd3368eb3db80b665c47a2eb7f46c49634d1c363076055503080256912eda"),
            [("table", "retention_legacy_bundle_journal")] = new("retention_legacy_bundle_journal", "a6c9b5b4954883fa8a8260fb89e938274c863da1449a3ecb65c942718d1790d2"),
            [("table", "retention_mutation_idempotency")] = new("retention_mutation_idempotency", "954c744b79bb82fb13b0c4ab0d793370cf9a48fddd14af648479b71cacb254e6"),
            [("table", "retention_mutation_previews")] = new("retention_mutation_previews", "b2127000967a9e41067078d12e7241c0acdc6cff437c938d6cf8967d3d3ee95b"),
            [("table", "retention_operation_receipts")] = new("retention_operation_receipts", "081d4cc4ff32d25c669de16d4c7148a5932407a03b26f0b4ea9be6541ef4bdbd"),
            [("table", "retention_store_instances")] = new("retention_store_instances", "5f3332463c56a453aca2aef49eabde28fb54523e89f3d78f587cba0fc49d7d28"),
            [("table", "retention_tombstones")] = new("retention_tombstones", "f617719ec4e4f5e85721ca6bc364dec209005943a8fbeb809e2430b836733b09"),
            [("table", "retention_worker_state")] = new("retention_worker_state", "684840b371ca3fae55d78d80ca12bc90b37313ae8fcc750c23f83bea6410f3eb"),
        };

    private static readonly IReadOnlySet<string> AllowedExternalRetentionTriggers = new HashSet<string>(StringComparer.Ordinal)
    {
        "retention_monitor_analysis_runs_token_immutable",
        "retention_raw_records_token_immutable",
        "retention_session_event_content_token_immutable",
    };

    internal static RetentionSessionEventContentForMigration? ReadAuthorizedSessionEventContentForMigration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        string sessionId,
        string? runId,
        string sourceAdapter,
        string sourceEventId,
        string contentState,
        DateTimeOffset migrationNow)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAdapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEventId);

        if (!RequireCurrentV1SchemaWhenPresent(connection, transaction)) return null;
        var storeInstanceId = ReadCurrentV1StoreInstanceId(connection, transaction);

        using var contentCommand = connection.CreateCommand();
        contentCommand.Transaction = transaction;
        contentCommand.CommandText = "SELECT event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token,typeof(retention_owner_token) FROM session_event_content WHERE event_id=$event;";
        contentCommand.Parameters.AddWithValue("$event", eventId);
        using var contentReader = contentCommand.ExecuteReader();
        if (!contentReader.Read()) return null;
        var contentEventId = contentReader.GetString(0);
        var contentKind = contentReader.GetString(1);
        var contentJson = contentReader.GetString(2);
        var capturedAtText = contentReader.GetString(3);
        var expiresAtText = contentReader.GetString(4);
        var ownerToken = contentReader.GetFieldValue<byte[]>(5);
        var ownerTokenType = contentReader.GetString(6);
        if (contentReader.Read()) throw InvalidAuthority();
        contentReader.Close();

        if (!string.Equals(contentEventId, eventId, StringComparison.Ordinal)
            || !string.Equals(contentKind, "application/json", StringComparison.Ordinal)
            || !string.Equals(contentState, "available", StringComparison.Ordinal))
            return null;
        if (!string.Equals(ownerTokenType, "blob", StringComparison.Ordinal) || ownerToken.Length != 32)
            throw InvalidAuthority();
        if (!DateTimeOffset.TryParseExact(capturedAtText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var capturedAt)
            || !DateTimeOffset.TryParseExact(expiresAtText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAt))
            throw InvalidAuthority();

        using var catalogCommand = connection.CreateCommand();
        catalogCommand.Transaction = transaction;
        catalogCommand.CommandText = """
            SELECT receipt_version,typeof(receipt_version),ownership_receipt,typeof(ownership_receipt),state,read_denied_at,
                   expires_at,adapter_coverage_version,typeof(adapter_coverage_version)
            FROM retention_items
            WHERE store_instance_id=$store AND store_kind='session_event_content' AND source_item_id=$event;
            """;
        catalogCommand.Parameters.AddWithValue("$store", storeInstanceId);
        catalogCommand.Parameters.AddWithValue("$event", eventId);
        using var catalogReader = catalogCommand.ExecuteReader();
        if (!catalogReader.Read()) throw InvalidAuthority();
        var receiptVersion = catalogReader.GetInt64(0);
        var receiptVersionType = catalogReader.GetString(1);
        var receipt = catalogReader.GetFieldValue<byte[]>(2);
        var receiptType = catalogReader.GetString(3);
        var state = catalogReader.GetString(4);
        var denied = !catalogReader.IsDBNull(5);
        var catalogExpiresAtText = catalogReader.GetString(6);
        var coverageVersion = catalogReader.GetInt64(7);
        var coverageVersionType = catalogReader.GetString(8);
        if (catalogReader.Read()
            || receiptVersion != 1
            || !string.Equals(receiptVersionType, "integer", StringComparison.Ordinal)
            || receipt.Length != 32
            || !string.Equals(receiptType, "blob", StringComparison.Ordinal)
            || coverageVersion != 1
            || !string.Equals(coverageVersionType, "integer", StringComparison.Ordinal))
            throw InvalidAuthority();
        catalogReader.Close();
        if (!DateTimeOffset.TryParseExact(catalogExpiresAtText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var catalogExpiresAt))
            throw InvalidAuthority();

        var expectedReceipt = RetentionOwnershipReceipt.CreateSession(new(
            storeInstanceId,
            eventId,
            contentKind,
            capturedAtText,
            capturedAt.UtcDateTime.Ticks,
            expiresAtText,
            expiresAt.UtcDateTime.Ticks,
            sessionId,
            runId,
            sourceAdapter,
            sourceEventId,
            ownerToken));
        if (!RetentionOwnershipReceipt.Matches(expectedReceipt, receipt)) throw InvalidAuthority();
        if (denied || state is not ("expiring" or "retained_by_policy")) return null;
        if (state == "expiring" && (catalogExpiresAt <= migrationNow || expiresAt <= migrationNow)) return null;
        return new(contentKind, contentJson, capturedAt, expiresAt);
    }

    private static bool RequireCurrentV1SchemaWhenPresent(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var hasCoreObject = false;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT type,name
                FROM sqlite_schema
                WHERE name NOT LIKE 'sqlite_%'
                  AND (name LIKE 'retention_%' OR name LIKE 'IX_retention_%')
                ORDER BY type,name;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = (Type: reader.GetString(0), Name: reader.GetString(1));
                if (CurrentV1Objects.ContainsKey(key))
                {
                    hasCoreObject = true;
                    continue;
                }
                if (key.Type == "trigger" && AllowedExternalRetentionTriggers.Contains(key.Name)) continue;
                throw InvalidAuthority();
            }
        }
        if (!hasCoreObject) return false;

        var names = CurrentV1Objects.Keys.Select(key => key.Name).ToHashSet(StringComparer.Ordinal);
        var actual = SqliteOwnedSchemaAuthority.Read(
            connection,
            transaction,
            (name, _) => names.Contains(name));
        if (actual.Count != CurrentV1Objects.Count) throw InvalidAuthority();
        foreach (var expected in CurrentV1Objects)
        {
            if (!actual.TryGetValue(expected.Key, out var actualObject)
                || !string.Equals(actualObject.Table, expected.Value.Table, StringComparison.Ordinal)
                || !string.Equals(
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actualObject.Sql))).ToLowerInvariant(),
                    expected.Value.NormalizedSqlSha256,
                    StringComparison.Ordinal))
                throw InvalidAuthority();
        }
        return true;
    }

    private static string ReadCurrentV1StoreInstanceId(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        RequireCurrentV1ComponentVersion(connection, transaction);
        var storeInstanceId = ReadCurrentV1StoreSingleton(connection, transaction);
        RequireCurrentV1Coverage(connection, transaction);
        return storeInstanceId;
    }

    private static void RequireCurrentV1ComponentVersion(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT typeof(component),component,typeof(version),version FROM retention_component_versions;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || !string.Equals(reader.GetString(0), "text", StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), "retention", StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), "integer", StringComparison.Ordinal)
            || reader.GetInt64(3) != 1
            || reader.Read())
            throw InvalidAuthority();
    }

    private static string ReadCurrentV1StoreSingleton(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT typeof(id),id,typeof(store_instance_id),store_instance_id FROM retention_store_instances;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || !string.Equals(reader.GetString(0), "integer", StringComparison.Ordinal)
            || reader.GetInt64(1) != 1
            || !string.Equals(reader.GetString(2), "text", StringComparison.Ordinal))
            throw InvalidAuthority();
        var storeInstanceId = reader.GetString(3);
        if (storeInstanceId.Length != 32
            || storeInstanceId.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || reader.Read())
            throw InvalidAuthority();
        return storeInstanceId;
    }

    private static void RequireCurrentV1Coverage(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var missing = new HashSet<string>(
            ["session_event_content", "raw_record", "analysis_run_raw", "sensitive_bundle", "analysis_sdk_directory"],
            StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT typeof(store_kind),store_kind,typeof(coverage_version),coverage_version FROM retention_adapter_coverage;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!string.Equals(reader.GetString(0), "text", StringComparison.Ordinal)
                || !string.Equals(reader.GetString(2), "integer", StringComparison.Ordinal)
                || reader.GetInt64(3) != 1
                || !missing.Remove(reader.GetString(1)))
                throw InvalidAuthority();
        }
        if (missing.Count != 0) throw InvalidAuthority();
    }

    private static InvalidOperationException InvalidAuthority() =>
        new("Invalid Retention authority during Session migration.");

    private sealed record RetentionSchemaObjectFingerprint(string Table, string NormalizedSqlSha256);
}
