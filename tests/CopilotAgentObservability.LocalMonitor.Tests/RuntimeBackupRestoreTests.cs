using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupRestoreTests
{
    [Fact]
    public void Restore_reconciles_current_terminal_tombstone_and_never_restores_raw_bytes()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawAndTombstone(temp.Target);

        var preview = service.Preview(bundle, temp.Target);
        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(1, preview.TerminalReconciliationCount);
        Assert.False(preview.RequiresConfirmation);
        Assert.True(result.Success, result.ErrorCode);
        Assert.True(result.PreRestoreBackupCreated);
        Assert.Equal(64, result.PreRestoreBackupSha256?.Length);
        Assert.True(File.Exists(Path.Combine(temp.Root, "runtime-backups", result.PreRestoreBackupFileName!)));
        using var restored = temp.Open(temp.Target);
        Assert.Equal(0L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal("deleted", temp.Scalar<string>(restored, "SELECT state FROM retention_items;"));
        Assert.Equal(1L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_tombstones;"));
        Assert.Equal("2026-04-02T00:00:00.0000000+00:00", temp.Scalar<string>(restored, "SELECT deleted_at FROM retention_items;"));
    }

    [Fact]
    public void Non_terminal_missing_source_requires_archive_bound_confirmation_before_reintroduction()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawWithoutTombstone(temp.Target);

        var preview = service.Preview(bundle, temp.Target);
        var blocked = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());
        var wrong = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(AllowResurrection: true, ConfirmationDigest: new string('0', 64)));
        using (var target = temp.Open(temp.Target))
        {
            temp.Execute(target, "UPDATE retention_items SET revision=revision+1;");
            temp.Execute(target, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var changedPreview = service.Preview(bundle, temp.Target);
        var stale = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(AllowResurrection: true, ConfirmationDigest: preview.ConfirmationDigest));
        var restored = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(AllowResurrection: true, ConfirmationDigest: changedPreview.ConfirmationDigest));

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.True(preview.RequiresConfirmation);
        Assert.Equal(1, preview.NonTerminalReintroductionCount);
        Assert.NotNull(preview.NonTerminalReintroductionDigest);
        Assert.NotNull(preview.ConfirmationDigest);
        Assert.NotEqual(preview.NonTerminalReintroductionDigest, changedPreview.NonTerminalReintroductionDigest);
        Assert.NotEqual(preview.ConfirmationDigest, changedPreview.ConfirmationDigest);
        var previewJson = System.Text.Encoding.UTF8.GetString(RuntimeBackupJson.SerializeResult(preview));
        Assert.DoesNotContain(new string('a', 32), previewJson, StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Target, previewJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreResurrectionBlocked, blocked.ErrorCode);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreResurrectionBlocked, wrong.ErrorCode);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreResurrectionBlocked, stale.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.Equal(1, restored.NonTerminalReintroductionCount);
        using var database = temp.Open(temp.Target);
        Assert.Equal(1L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM raw_records;"));
    }

    [Fact]
    public void Current_read_denial_is_reconciled_without_confirmation_and_staged_raw_is_removed()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.MarkReadDenied(temp.Target);

        var preview = service.Preview(bundle, temp.Target);
        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(1, preview.TerminalReconciliationCount);
        Assert.False(preview.RequiresConfirmation);
        Assert.True(result.Success, result.ErrorCode);
        using var database = temp.Open(temp.Target);
        Assert.Equal(0L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal("expired_pending_deletion", temp.Scalar<string>(database, "SELECT state FROM retention_items;"));
        Assert.Equal("2026-04-01T00:00:00.0000000+00:00", temp.Scalar<string>(database, "SELECT read_denied_at FROM retention_items;"));
        Assert.Equal(0L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM retention_tombstones;"));
    }

    [Fact]
    public void Terminal_reconciliation_preserves_a_failed_deletion_retry_cursor()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "failed-deletion-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        using (var target = temp.Open(temp.Target))
        {
            temp.Execute(target, "UPDATE retention_items SET state='deletion_failed',revision=5,read_denied_at='2026-04-01T00:00:00.0000000+00:00',queued_at='2026-04-01T00:00:00.0000000+00:00',deletion_started_at='2026-04-01T01:00:00.0000000+00:00',attempt_count=1,next_retry_at='2026-04-01T02:00:00.0000000+00:00',error_code='retention_delete_io_failed';");
            temp.Execute(target, $"INSERT INTO retention_delete_journal(item_id,durable_cursor,intent_at,expected_revision) VALUES('{new string('a', 32)}','7','2026-04-01T01:01:00.0000000+00:00',4);");
            temp.Execute(target, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(temp.Target);
        Assert.Equal("deletion_failed", temp.Scalar<string>(restored, "SELECT state FROM retention_items;"));
        Assert.Equal(5L, temp.Scalar<long>(restored, "SELECT revision FROM retention_items;"));
        Assert.Equal("7", temp.Scalar<string>(restored, "SELECT durable_cursor FROM retention_delete_journal;"));
        Assert.Equal(4L, temp.Scalar<long>(restored, "SELECT expected_revision FROM retention_delete_journal;"));
        Assert.Equal(0L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM raw_records;"));
    }

    [Fact]
    public void Terminal_reconciliation_accepts_deleting_before_the_first_delete_intent()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "pre-intent-deleting-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        using (var target = temp.Open(temp.Target))
        {
            temp.Execute(target, "UPDATE retention_items SET state='deleting',revision=4,read_denied_at='2026-04-01T00:00:00.0000000+00:00',queued_at='2026-04-01T00:00:00.0000000+00:00',deletion_started_at='2026-04-01T01:00:00.0000000+00:00';");
            temp.Execute(target, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(temp.Target);
        Assert.Equal("deleting", temp.Scalar<string>(restored, "SELECT state FROM retention_items;"));
        Assert.Equal(0L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_delete_journal;"));
        Assert.Equal(0L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_leases;"));
        Assert.Equal(0L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM raw_records;"));
    }

    [Fact]
    public void Terminal_reconciliation_copies_the_exact_item_audit_and_linked_receipt()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "audit-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawAndTombstone(temp.Target);
        var operationId = temp.AddItemAudit(temp.Target);

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(temp.Target);
        Assert.Equal(1L, temp.Scalar<long>(restored, $"SELECT COUNT(*) FROM retention_audit_events WHERE operation_id='{operationId}' AND target_kind='item' AND target_id='{new string('a', 32)}';"));
        Assert.Equal(1L, temp.Scalar<long>(restored, $"SELECT COUNT(*) FROM retention_operation_receipts WHERE operation_id='{operationId}' AND target_kind='item' AND target_id='{new string('a', 32)}';"));
        Assert.Equal("deleted", temp.Scalar<string>(restored, $"SELECT completion_code FROM retention_operation_receipts WHERE operation_id='{operationId}';"));
    }

    [Fact]
    public void Terminal_reconciliation_pages_more_than_256_audits_and_receipts()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "paged-audit-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawAndTombstone(temp.Target);
        temp.AddItemAudits(temp.Target, 300);

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(temp.Target);
        Assert.Equal(300L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(300L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_operation_receipts WHERE operation IN ('pin','unpin','delete_now');"));
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("receipt")]
    public void Oversized_archive_reconciliation_cell_fails_before_target_swap(string location)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        temp.AddItemAudit(temp.Source);
        temp.MakeReconciliationCellOversized(temp.Source, location);
        var bundle = Path.Combine(temp.Root, $"oversized-{location}-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "current", includeRaw: true);
        temp.DeleteRawAndTombstone(temp.Target);
        temp.AddItemAudit(temp.Target);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Preview_and_restore_page_a_large_valid_terminal_catalog()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "many-terminal-items", includeRaw: false);
        temp.AddDeletedItems(temp.Source, 300);
        var bundle = Path.Combine(temp.Root, "many-terminal-items.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);

        var preview = service.Preview(bundle, temp.Target);
        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(300, preview.TerminalReconciliationCount);
        Assert.NotNull(preview.TerminalReconciliationDigest);
        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(300, result.TerminalReconciliationCount);
        using var restored = temp.Open(temp.Target);
        Assert.Equal(300L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_items WHERE state='deleted';"));
        Assert.Equal(300L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_tombstones;"));
    }

    [Fact]
    public void Missing_current_terminal_audit_shape_fails_reconciliation_without_swapping_target()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawAndTombstone(temp.Target);
        using (var target = temp.Open(temp.Target))
        {
            temp.Execute(target, "DROP TABLE retention_operation_receipts;");
            temp.Execute(target, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Fact]
    public void Colliding_staged_retention_item_primary_key_fails_without_orphaning_raw_content()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        using (var source = temp.Open(temp.Source))
        {
            var receipt = RetentionOwnershipReceipt.CreateRawRecord(new(
                new string('2', 32), 2, "2026-01-01T00:00:00.0000000+00:00",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime.Ticks, 1, SHA256.HashData([7])));
            temp.Execute(source, "UPDATE raw_records SET id=2 WHERE id=1;");
            temp.Execute(source, $"UPDATE retention_items SET source_item_id='2',ownership_receipt=X'{Convert.ToHexString(receipt)}' WHERE item_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';");
            temp.Execute(source, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "current", includeRaw: true);
        temp.DeleteRawAndTombstone(temp.Target);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Fact]
    public void Supported_pre_retention_archive_is_backfilled_before_installation()
    {
        using var temp = new RestoreTemp();
        temp.CreatePreRetentionDatabase(temp.Source, "older");
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        var destination = Path.Combine(temp.Root, "fresh", "monitor.db");

        var result = service.Restore(bundle, destination, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(destination);
        Assert.Equal(1L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record' AND source_item_id='1';"));
    }

    [Fact]
    public void Pre_retention_archive_cannot_bypass_current_terminal_lineage()
    {
        using var temp = new RestoreTemp();
        temp.CreatePreRetentionDatabase(temp.Source, "older");
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "current", includeRaw: true);
        temp.DeleteRawAndTombstone(temp.Target);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Post_swap_io_or_permission_fault_atomically_restores_exact_old_database(bool permissionFailure)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));
        var service = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterSwap)
            {
                if (permissionFailure) throw new UnauthorizedAccessException("injected-permission-fault");
                throw new IOException("injected-io-fault");
            }
        });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRolledBack, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
        using var restored = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Post_swap_retention_invariant_drift_is_detected_and_rolls_back_old_database()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: true);
        var service = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint != RuntimeBackupCheckpoints.AfterSwap) return;
            using var installed = temp.Open(temp.Target);
            temp.Execute(installed, "UPDATE retention_items SET captured_at='invalid';");
        });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRolledBack, result.ErrorCode);
        using var restored = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", temp.Scalar<string>(restored, "SELECT captured_at FROM retention_items;"));
    }

    [Fact]
    public void Pre_restore_output_rejects_bundle_database_and_restore_control_file_collisions()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var reserved = new[]
        {
            bundle,
            temp.Target,
            temp.Target + "-journal",
            temp.Target + "-wal",
            temp.Target + "-shm",
            temp.Target + ".runtime-restore.lock",
            temp.Target + ".runtime-restore-stage",
            temp.Target + ".runtime-restore-rollback",
            temp.Target + ".runtime-restore-rollback-journal",
            temp.Target + ".runtime-restore-rollback-wal",
            temp.Target + ".runtime-restore-rollback-shm",
            temp.Target + ".runtime-restore-journal.json",
            temp.Target + ".runtime-restore-journal.json.commit",
        };

        foreach (var path in reserved)
        {
            var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(PreRestoreOutputPath: path));
            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, result.ErrorCode);
        }

        using var database = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(database, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Theory]
    [InlineData("-journal")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData(".runtime-restore.lock")]
    [InlineData(".runtime-restore-stage")]
    [InlineData(".runtime-restore-rollback")]
    [InlineData(".runtime-restore-rollback-journal")]
    [InlineData(".runtime-restore-rollback-wal")]
    [InlineData(".runtime-restore-rollback-shm")]
    [InlineData(".runtime-restore-journal.json")]
    [InlineData(".runtime-restore-journal.json.commit")]
    public void Restore_bundle_rejects_database_sidecar_and_control_collisions_without_mutation(string suffix)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var validBundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, validBundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = File.ReadAllBytes(temp.Target);
        var collidingBundle = temp.Target + suffix;
        File.Copy(validBundle, collidingBundle);

        var result = service.Restore(collidingBundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, result.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        Assert.True(File.Exists(collidingBundle));
    }

    [Fact]
    public void Restore_uses_caller_selected_pre_restore_output_without_overwrite_or_path_disclosure()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var preRestore = Path.Combine(temp.Root, "operator-pre-restore.zip");

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(PreRestoreOutputPath: preRestore));

        Assert.True(result.Success, result.ErrorCode);
        Assert.True(result.PreRestoreBackupCreated);
        Assert.Equal("operator-pre-restore.zip", result.PreRestoreBackupFileName);
        Assert.True(File.Exists(preRestore));
        Assert.True(service.Inspect(preRestore).Success);
        using (var json = JsonDocument.Parse(RuntimeBackupJson.SerializeResult(result)))
            Assert.DoesNotContain(JsonStrings(json.RootElement), value => value.Contains(temp.Root, StringComparison.OrdinalIgnoreCase));
        using var restored = temp.Open(temp.Target);
        Assert.Equal("new", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_receipt_io_or_permission_failure_before_swap_preserves_exact_old_database_and_cleans_owned_controls(bool permissionFailure)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));
        var service = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.DuringStageReceipt)
            {
                if (permissionFailure) throw new UnauthorizedAccessException("injected-permission-fault");
                throw new IOException("injected-io-fault");
            }
        });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreFailed, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
        using var restored = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Idle_live_sqlite_owner_and_active_sidecars_are_rejected_before_restore_work()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        using var liveOwner = temp.Open(temp.Target);

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.MonitorMustBeStopped, result.ErrorCode);
        Assert.Equal("old", temp.Scalar<string>(liveOwner, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Dangling_reparse_sidecar_is_not_treated_as_absent()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "dangling-sidecar.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = File.ReadAllBytes(temp.Target);
        var sidecar = temp.Target + "-wal";
        try { File.CreateSymbolicLink(sidecar, Path.Combine(temp.Root, "missing-sidecar-target")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw Xunit.Sdk.SkipException.ForSkip($"Cannot create reparse fixture: {exception.GetType().Name}"); }

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.MonitorMustBeStopped, result.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        Assert.True(new FileInfo(sidecar).LinkTarget is not null);
    }

    [Fact]
    public void Startup_recovery_uses_outside_database_journal_to_restore_exact_old_database_after_process_crash()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "crash-source.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterSwap) throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-rollback"));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
        Assert.False(File.Exists(temp.Target + ".runtime-restore-journal.json"));
        Assert.False(File.Exists(temp.Target + ".runtime-restore-rollback"));
        using var database = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(database, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterOwnerJournal, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterArchiveExtracted, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterMigration, true)]
    [InlineData(RuntimeBackupCheckpoints.DuringStageReceipt, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterReceiptPersisted, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterStageValidated, true)]
    [InlineData(RuntimeBackupCheckpoints.BeforeSwap, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterOwnerJournal, false)]
    [InlineData(RuntimeBackupCheckpoints.BeforeSwap, false)]
    public void Owner_journal_recovery_cleans_only_its_stage_and_preserves_the_pre_swap_destination(string faultPoint, bool targetExisted)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "pre-swap.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        byte[]? before = null;
        if (targetExisted)
        {
            temp.CreateDatabase(temp.Target, "old", includeRaw: false);
            before = File.ReadAllBytes(temp.Target);
        }
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        if (targetExisted)
        {
            Assert.Equal(before, File.ReadAllBytes(temp.Target));
            using var target = temp.Open(temp.Target);
            Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        }
        else Assert.False(File.Exists(temp.Target));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterSwap, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterInstalledJournalCandidateFlushed, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterInstalledValidation, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterSwap, false)]
    [InlineData(RuntimeBackupCheckpoints.AfterInstalledValidation, false)]
    public void Pre_commit_crash_recovery_rolls_back_existing_or_fresh_destination(string faultPoint, bool targetExisted)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "pre-commit.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        byte[]? before = null;
        if (targetExisted)
        {
            temp.CreateDatabase(temp.Target, "old", includeRaw: false);
            before = File.ReadAllBytes(temp.Target);
        }
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        if (targetExisted)
        {
            Assert.Equal(before, File.ReadAllBytes(temp.Target));
            using var target = temp.Open(temp.Target);
            Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        }
        else Assert.False(File.Exists(temp.Target));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterJournalCommitted, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterRollbackDeleted, true)]
    [InlineData(RuntimeBackupCheckpoints.BeforeJournalDeleted, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterJournalDeleted, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed, false)]
    [InlineData(RuntimeBackupCheckpoints.AfterJournalDeleted, false)]
    public void Commit_decision_crash_recovery_keeps_validated_new_database_and_receipt(string faultPoint, bool targetExisted)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "committed.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        if (targetExisted) temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        using var target = temp.Open(temp.Target);
        Assert.Equal("new", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        Assert.Equal(1L, temp.Scalar<long>(target, "SELECT COUNT(*) FROM runtime_backup_receipts WHERE operation_kind='restore' AND result_code='restore_succeeded';"));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recovery_promotes_a_pending_commit_before_forward_cleanup(bool targetExisted)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "pending-commit-promotion.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        if (targetExisted) temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var crashingRestore = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashingRestore.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var crashingRecovery = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterRecoveryCommitPromoted) throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() => crashingRecovery.Initialize(temp.Target));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));
        Assert.False(File.Exists(temp.Target + ".runtime-restore-journal.json.commit"));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        using var target = temp.Open(temp.Target);
        Assert.Equal("new", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        Assert.Equal(1L, temp.Scalar<long>(target, "SELECT COUNT(*) FROM runtime_backup_receipts WHERE operation_kind='restore' AND result_code='restore_succeeded';"));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Invalid_committed_target_falls_back_to_exact_verified_rollback()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "committed-fallback.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = File.ReadAllBytes(temp.Target);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        using (var installed = temp.Open(temp.Target))
        {
            temp.Execute(installed, "UPDATE runtime_probe SET value='tampered';");
            temp.Execute(installed, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterSwap)]
    [InlineData(RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed)]
    public void Missing_installed_target_is_recreated_from_the_exact_verified_rollback(string faultPoint)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "missing-installed.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = File.ReadAllBytes(temp.Target);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        File.Delete(temp.Target);

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterSwap)]
    [InlineData(RuntimeBackupCheckpoints.AfterInstalledValidation)]
    public void Recovery_recognizes_an_already_completed_pre_commit_rollback(string faultPoint)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "completed-pre-commit-rollback.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = File.ReadAllBytes(temp.Target);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var rollback = temp.Target + ".runtime-restore-rollback";
        File.Replace(rollback, temp.Target, null, ignoreMetadataErrors: true);
        Assert.False(File.Exists(rollback));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed)]
    [InlineData(RuntimeBackupCheckpoints.AfterJournalCommitted)]
    public void Recovery_recognizes_an_already_completed_committed_fallback_rollback(string faultPoint)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "completed-committed-fallback-rollback.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = File.ReadAllBytes(temp.Target);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var rollback = temp.Target + ".runtime-restore-rollback";
        File.Replace(rollback, temp.Target, null, ignoreMetadataErrors: true);
        Assert.False(File.Exists(rollback));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Prepared_stage_hash_mismatch_is_retained_and_fails_closed()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "prepared-mismatch.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.BeforeSwap) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var journalPath = temp.Target + ".runtime-restore-journal.json";
        using var document = JsonDocument.Parse(File.ReadAllBytes(journalPath));
        var stage = Path.Combine(temp.Root, document.RootElement.GetProperty("stage_file_name").GetString()!);
        using (var stream = new FileStream(stage, FileMode.Open, FileAccess.Write, FileShare.None)) stream.WriteByte(0xff);

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.True(File.Exists(journalPath));
        Assert.True(File.Exists(stage));
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Recovery_refuses_active_target_sidecar_without_deleting_owned_rollback_or_journal()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "sidecar-recovery.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterSwap) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var sidecar = temp.Target + "-wal";
        File.WriteAllText(sidecar, "active-or-unknown");

        var blocked = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(blocked.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, blocked.ErrorCode);
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-rollback"));
        File.Delete(sidecar);
        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);
        Assert.True(recovered.Success, recovered.ErrorCode);
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Startup_rejects_unowned_legacy_stage_without_deleting_or_mutating()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));
        var stage = temp.Target + ".runtime-restore-stage";
        File.Copy(temp.Target, stage);

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.True(File.Exists(stage));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Fact]
    public void Startup_rejects_unknown_v1_journal_and_preserves_all_artifacts()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "staged", includeRaw: false);
        File.Copy(temp.Source, temp.Target + ".runtime-restore-stage");
        File.WriteAllText(
            temp.Target + ".runtime-restore-journal.json",
            $$"""{"schema_version":"runtime-restore-journal.v1","archive_sha256":"{{new string('1', 64)}}","target_existed":false}""");

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.False(File.Exists(temp.Target));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-stage"));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));
    }

    [Fact]
    public void Startup_rejects_invalid_nullable_hash_kind_and_preserves_nonce_bound_stage()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "staged", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "invalid-journal-kind.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterOwnerJournal) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var journalPath = temp.Target + ".runtime-restore-journal.json";
        var journal = File.ReadAllText(journalPath);
        using var document = JsonDocument.Parse(journal);
        var stage = Path.Combine(temp.Root, document.RootElement.GetProperty("stage_file_name").GetString()!);
        File.Copy(temp.Source, stage);
        File.WriteAllText(journalPath, journal.Replace("\"target_before_sha256\":null", "\"target_before_sha256\":7", StringComparison.Ordinal));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.True(File.Exists(journalPath));
        Assert.True(File.Exists(stage));
        Assert.False(File.Exists(temp.Target));
    }

    [Theory]
    [InlineData("legacy-stage")]
    [InlineData("legacy-stage-wal")]
    [InlineData("dynamic-stage")]
    [InlineData("dynamic-stage-shm")]
    [InlineData("rollback-journal")]
    [InlineData("rollback-wal")]
    [InlineData("rollback-shm")]
    [InlineData("journal-commit")]
    public void Startup_fails_closed_and_preserves_every_unowned_reserved_artifact(string kind)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var artifact = kind switch
        {
            "legacy-stage" => temp.Target + ".runtime-restore-stage",
            "legacy-stage-wal" => temp.Target + ".runtime-restore-stage-wal",
            "dynamic-stage" => Path.Combine(temp.Root, ".runtime-restore-stage-unowned.sqlite"),
            "dynamic-stage-shm" => Path.Combine(temp.Root, ".runtime-restore-stage-unowned.sqlite-shm"),
            "rollback-journal" => temp.Target + ".runtime-restore-rollback-journal",
            "rollback-wal" => temp.Target + ".runtime-restore-rollback-wal",
            "rollback-shm" => temp.Target + ".runtime-restore-rollback-shm",
            "journal-commit" => temp.Target + ".runtime-restore-journal.json.commit",
            _ => throw new InvalidOperationException(),
        };
        File.WriteAllText(artifact, "unowned");
        var before = File.ReadAllBytes(temp.Target);

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.True(File.Exists(artifact));
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
    }

    [Fact]
    public void Startup_rejects_unowned_orphan_rollback_and_preserves_new_target()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "old", includeRaw: false);
        temp.CreateDatabase(temp.Target, "new", includeRaw: false);
        File.Copy(temp.Source, temp.Target + ".runtime-restore-rollback");

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.True(File.Exists(temp.Target + ".runtime-restore-rollback"));
        using var database = temp.Open(temp.Target);
        Assert.Equal("new", temp.Scalar<string>(database, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Read_only_preflight_rejects_future_monitor_without_changing_candidate_bytes()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "future", includeRaw: false);
        using (var connection = temp.Open(temp.Source))
        {
            temp.Execute(connection, "UPDATE schema_version SET version=999 WHERE component='monitor';");
            temp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Source));

        var result = new SqliteRuntimeBackupService(temp.Clock).PreflightForMigration(temp.Source);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
        Assert.Throws<InvalidOperationException>(() => MonitorHost.Build(
            new MonitorOptions(temp.Source, "http://127.0.0.1:0", false, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, StartRetentionCleanupWorker = false, UseUserSecrets = false }));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
    }

    [Fact]
    public void Restore_to_fresh_destination_preserves_original_retention_clock_and_is_cross_directory_portable()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "portable", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "portable.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        var otherMachineDirectory = Directory.CreateDirectory(Path.Combine(temp.Root, "synthetic-other-machine"));
        var destination = Path.Combine(otherMachineDirectory.FullName, "monitor.db");

        var result = service.Restore(bundle, destination, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(destination);
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", temp.Scalar<string>(restored, "SELECT captured_at FROM retention_items;"));
        Assert.Equal("2026-04-01T00:00:00.0000000+00:00", temp.Scalar<string>(restored, "SELECT expires_at FROM retention_items;"));
        Assert.Equal("raw-default-90d", temp.Scalar<string>(restored, "SELECT policy_id FROM retention_items;"));
        Assert.Equal(1L, temp.Scalar<long>(restored, "SELECT policy_version FROM retention_items;"));
        Assert.Equal("portable", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
        Assert.Equal("database_ready", result.ReadinessCheck);
        Assert.Equal("doctor_store_ready", result.DoctorCheck);
        Assert.False(result.PreRestoreBackupCreated);
        var preflight = service.PreflightForMigration(destination);
        Assert.True(preflight.Success, preflight.ErrorCode);
        Assert.Equal(7, preflight.ComponentVersions!["monitor"]);
        Assert.Equal(13, preflight.ComponentVersions["session"]);
        Assert.Equal(1, preflight.ComponentVersions["retention"]);
        Assert.Equal(1, preflight.ComponentVersions["doctor"]);
        Assert.Equal(1, preflight.ComponentVersions["alert_engine"]);
        Assert.Equal(1, preflight.ComponentVersions["alert_lifecycle"]);
        Assert.Equal(1, preflight.ComponentVersions["runtime_backup"]);
        Assert.Equal(1, preflight.ComponentVersions["first_trace_navigation"]);
    }

    private sealed class RestoreTemp : IDisposable
    {
        internal RestoreTemp()
        {
            Root = Path.Combine(Path.GetTempPath(), $"runtime-restore-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Source = Path.Combine(Root, "source.db");
            Target = Path.Combine(Root, "target.db");
            Clock = new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 2, 3, 4, TimeSpan.Zero));
        }

        internal string Root { get; }
        internal string Source { get; }
        internal string Target { get; }
        internal TimeProvider Clock { get; }

        internal void CreateDatabase(string path, string value, bool includeRaw)
        {
            using var connection = Open(path);
            Execute(connection, "PRAGMA journal_mode=WAL;");
            using (var transaction = connection.BeginTransaction()) { MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction); transaction.Commit(); }
            using (var transaction = connection.BeginTransaction()) { RetentionSchemaMigrator.Apply(connection, transaction); transaction.Commit(); }
            Execute(connection, $"UPDATE retention_store_instances SET store_instance_id='{new string('2', 32)}' WHERE id=1;");
            Execute(connection, "CREATE TABLE runtime_probe(id INTEGER PRIMARY KEY,value TEXT NOT NULL);");
            using (var insert = connection.CreateCommand()) { insert.CommandText = "INSERT INTO runtime_probe(id,value) VALUES(1,$value);"; insert.Parameters.AddWithValue("$value", value); insert.ExecuteNonQuery(); }
            if (!includeRaw) return;
            var token = SHA256.HashData([7]);
            using (var raw = connection.CreateCommand()) { raw.CommandText = "INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token) VALUES(1,'raw-otlp',NULL,'2026-01-01T00:00:00.0000000+00:00','{}','{\"secret\":\"private\"}',1,$token);"; raw.Parameters.AddWithValue("$token", token); raw.ExecuteNonQuery(); }
            var receipt = RetentionOwnershipReceipt.CreateRawRecord(new(
                new string('2', 32), 1, "2026-01-01T00:00:00.0000000+00:00",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime.Ticks, 1, token));
            using var item = connection.CreateCommand();
            item.CommandText = "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES($id,$store,'raw_record','1',1,$receipt,'2026-01-01T00:00:00.0000000+00:00','2026-04-01T00:00:00.0000000+00:00','raw-default-90d',1,'expiring',1,1);";
            item.Parameters.AddWithValue("$id", new string('a', 32)); item.Parameters.AddWithValue("$store", new string('2', 32)); item.Parameters.AddWithValue("$receipt", receipt); item.ExecuteNonQuery();
        }

        internal void CreatePreRetentionDatabase(string path, string value)
        {
            using var connection = Open(path);
            Execute(connection, "PRAGMA journal_mode=WAL;");
            using (var transaction = connection.BeginTransaction()) { MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction); transaction.Commit(); }
            Execute(connection, "CREATE TABLE runtime_probe(id INTEGER PRIMARY KEY,value TEXT NOT NULL);");
            using (var insert = connection.CreateCommand()) { insert.CommandText = "INSERT INTO runtime_probe(id,value) VALUES(1,$value);"; insert.Parameters.AddWithValue("$value", value); insert.ExecuteNonQuery(); }
            var token = SHA256.HashData([7]);
            using (var raw = connection.CreateCommand()) { raw.CommandText = "INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token) VALUES(1,'raw-otlp',NULL,'2026-01-01T00:00:00.0000000+00:00','{}','{\"secret\":\"private\"}',1,$token);"; raw.Parameters.AddWithValue("$token", token); raw.ExecuteNonQuery(); }
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void DeleteRawAndTombstone(string path)
        {
            using var connection = Open(path);
            Execute(connection, "DELETE FROM raw_records;");
            Execute(connection, "UPDATE retention_items SET state='deleted',revision=9,read_denied_at='2026-04-01T00:00:00.0000000+00:00',queued_at='2026-04-01T00:00:00.0000000+00:00',deleted_at='2026-04-02T00:00:00.0000000+00:00';");
            Execute(connection, $"INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES('{new string('a', 32)}','2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00');");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void DeleteRawWithoutTombstone(string path)
        {
            using var connection = Open(path);
            Execute(connection, "DELETE FROM raw_records;");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void MarkReadDenied(string path)
        {
            using var connection = Open(path);
            Execute(connection, "UPDATE retention_items SET state='expired_pending_deletion',revision=4,read_denied_at='2026-04-01T00:00:00.0000000+00:00',queued_at='2026-04-01T00:00:00.0000000+00:00';");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal string AddItemAudit(string path)
        {
            var operationId = "33333333-3333-3333-3333-333333333333";
            var itemId = new string('a', 32);
            var digest = new string('d', 64);
            using var connection = Open(path);
            Execute(connection, $"INSERT INTO retention_operation_receipts(operation_id,schema_version,result_code,target_kind,target_id,operation,scope,target_item_count,result_json,completion_code,expected_version,result_version,target_item_set_digest,created_at,completed_at,last_replayed_at) VALUES('{operationId}',1,'completed','item','{itemId}','delete_now','single_item',1,'{{}}','deleted','8','9','{digest}','2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00',NULL);");
            Execute(connection, $"INSERT INTO retention_audit_events(event_id,operation_id,event_type,target_kind,target_id,session_id,occurred_at,actor_label,operation,reason_code,comment,previous_pin_state,new_pin_state,previous_operation_state,new_operation_state,request_idempotency_key,expected_version,result_version,target_item_set_digest,completion_code,error_code) VALUES('44444444-4444-4444-4444-444444444444','{operationId}','retention_mutation','item','{itemId}',NULL,'2026-04-02T00:00:00.0000000+00:00','local-user','delete_now','operator_request',NULL,'not_applicable','not_applicable','deleting','deleted','55555555-5555-5555-5555-555555555555','8','9','{digest}','deleted',NULL);");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            return operationId;
        }

        internal void MakeReconciliationCellOversized(string path, string location)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = location switch
            {
                "audit" => "UPDATE retention_audit_events SET comment=$value;",
                "receipt" => "UPDATE retention_operation_receipts SET result_json=$value;",
                _ => throw new ArgumentOutOfRangeException(nameof(location)),
            };
            command.Parameters.AddWithValue("$value", new string('x', RuntimeBackupLimits.MaximumReconciliationTextBytes + 1));
            command.ExecuteNonQuery();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void AddItemAudits(string path, int count)
        {
            var itemId = new string('a', 32);
            var digest = new string('d', 64);
            using var connection = Open(path);
            using var transaction = connection.BeginTransaction();
            using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = "INSERT INTO retention_operation_receipts(operation_id,schema_version,result_code,target_kind,target_id,operation,scope,target_item_count,result_json,completion_code,expected_version,result_version,target_item_set_digest,created_at,completed_at,last_replayed_at) VALUES($operation,1,'completed','item',$item,'delete_now','single_item',1,'{}','deleted','8','9',$digest,'2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00',NULL);";
            receipt.Parameters.Add("$operation", SqliteType.Text);
            receipt.Parameters.AddWithValue("$item", itemId);
            receipt.Parameters.AddWithValue("$digest", digest);
            using var audit = connection.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO retention_audit_events(event_id,operation_id,event_type,target_kind,target_id,session_id,occurred_at,actor_label,operation,reason_code,comment,previous_pin_state,new_pin_state,previous_operation_state,new_operation_state,request_idempotency_key,expected_version,result_version,target_item_set_digest,completion_code,error_code) VALUES($event,$operation,'retention_mutation','item',$item,NULL,'2026-04-02T00:00:00.0000000+00:00','local-user','delete_now','operator_request',NULL,'not_applicable','not_applicable','deleting','deleted',$request,'8','9',$digest,'deleted',NULL);";
            audit.Parameters.Add("$event", SqliteType.Text);
            audit.Parameters.Add("$operation", SqliteType.Text);
            audit.Parameters.AddWithValue("$item", itemId);
            audit.Parameters.Add("$request", SqliteType.Text);
            audit.Parameters.AddWithValue("$digest", digest);
            for (var index = 0; index < count; index++)
            {
                var operationId = $"operation-{index:D4}";
                receipt.Parameters["$operation"].Value = operationId;
                receipt.ExecuteNonQuery();
                audit.Parameters["$event"].Value = $"event-{index:D4}";
                audit.Parameters["$operation"].Value = operationId;
                audit.Parameters["$request"].Value = $"request-{index:D4}";
                audit.ExecuteNonQuery();
            }
            transaction.Commit();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void AddDeletedItems(string path, int count)
        {
            using var connection = Open(path);
            using var transaction = connection.BeginTransaction();
            using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,deleted_at,adapter_coverage_version) VALUES($item,$store,'raw_record',$source,1,zeroblob(32),'2026-01-01T00:00:00.0000000+00:00','2026-04-01T00:00:00.0000000+00:00','raw-default-90d',1,'deleted',5,'2026-04-01T00:00:00.0000000+00:00','2026-04-01T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00',1);";
            item.Parameters.Add("$item", SqliteType.Text);
            item.Parameters.AddWithValue("$store", new string('2', 32));
            item.Parameters.Add("$source", SqliteType.Text);
            using var tombstone = connection.CreateCommand();
            tombstone.Transaction = transaction;
            tombstone.CommandText = "INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES($item,'2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00');";
            tombstone.Parameters.Add("$item", SqliteType.Text);
            for (var index = 0; index < count; index++)
            {
                var itemId = (index + 1).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
                item.Parameters["$item"].Value = itemId;
                item.Parameters["$source"].Value = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                item.ExecuteNonQuery();
                tombstone.Parameters["$item"].Value = itemId;
                tombstone.ExecuteNonQuery();
            }
            transaction.Commit();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        internal void Execute(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        internal T Scalar<T>(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
        internal void AssertNoRestoreControls()
        {
            Assert.False(File.Exists(Target + ".runtime-restore-journal.json"));
            Assert.False(File.Exists(Target + ".runtime-restore-journal.json.commit"));
            Assert.False(File.Exists(Target + ".runtime-restore-rollback"));
            Assert.False(File.Exists(Target + ".runtime-restore-rollback-journal"));
            Assert.False(File.Exists(Target + ".runtime-restore-rollback-wal"));
            Assert.False(File.Exists(Target + ".runtime-restore-rollback-shm"));
            Assert.False(File.Exists(Target + ".runtime-restore-stage"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Root, ".runtime-restore-stage-*", SearchOption.TopDirectoryOnly));
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static IEnumerable<string> JsonStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) yield return element.GetString()!;
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
            foreach (var value in JsonStrings(item)) yield return value;
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            foreach (var value in JsonStrings(property.Value)) yield return value;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value; }
    private sealed class SimulatedProcessCrashException : Exception;
}
