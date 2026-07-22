using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupManifestValidationTests
{
    [Theory]
    [InlineData("\"source_application_version\":\"1.2.3\"", "\"source_application_version\":null")]
    [InlineData("\"source_platform\":\"windows\"", "\"source_platform\":[]")]
    [InlineData("\"kind\":\"ephemeral_runtime\"", "\"kind\":null")]
    [InlineData("\"policies\":[\"raw-default-90d/v1\"]", "\"policies\":[1]")]
    public void Required_string_tokens_reject_null_and_non_string_values(string original, string replacement)
    {
        var malformed = Replace(ValidManifest(), original, replacement);

        AssertManifestInvalid(malformed);
    }

    [Fact]
    public void Oversized_string_token_is_manifest_invalid()
    {
        var malformed = Replace(
            ValidManifest(),
            "\"source_platform\":\"windows\"",
            $"\"source_platform\":\"{new string('x', 257)}\"");

        AssertManifestInvalid(malformed);
    }

    [Fact]
    public void Oversized_list_is_manifest_invalid()
    {
        var policies = string.Join(",", Enumerable.Range(0, RuntimeBackupLimits.MaximumInventoryItems + 1)
            .Select(index => $"\"policy-{index:D3}/v1\""));
        var malformed = Replace(
            ValidManifest(),
            "\"policies\":[\"raw-default-90d/v1\"]",
            $"\"policies\":[{policies}]");

        AssertManifestInvalid(malformed);
    }

    [Fact]
    public void Duplicate_nested_property_is_manifest_invalid()
    {
        var malformed = Replace(
            ValidManifest(),
            "\"method\":\"sqlite_online_backup\"",
            "\"method\":\"sqlite_online_backup\",\"method\":\"sqlite_online_backup\"");

        AssertManifestInvalid(malformed);
    }

    [Fact]
    public void Json_beyond_the_depth_limit_is_manifest_invalid()
    {
        var nested = new string('[', RuntimeBackupLimits.MaximumJsonDepth + 1)
            + "\"windows\""
            + new string(']', RuntimeBackupLimits.MaximumJsonDepth + 1);
        var malformed = Replace(
            ValidManifest(),
            "\"source_platform\":\"windows\"",
            $"\"source_platform\":{nested}");

        AssertManifestInvalid(malformed);
    }

    [Fact]
    public void Backup_window_has_canonical_property_order_and_preserves_unavailable_cursors_as_null()
    {
        var manifest = ValidManifest();
        var text = Encoding.UTF8.GetString(manifest);

        Assert.Contains(
            "\"snapshot\":{\"method\":\"sqlite_online_backup\",\"source_journal_mode\":\"wal\",\"integrity_check\":\"ok\",\"foreign_key_check\":\"ok\",\"snapshot_id\":",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            JsonEncoded("\"backup_window\":{\"started_at\":\"2026-07-23T01:02:01.0000000+00:00\",\"completed_at\":\"2026-07-23T01:02:05.0000000+00:00\",\"projection_cursors_at_start\":{\"monitor\":9,\"unavailable\":null},\"projection_cursors_at_end\":{\"monitor\":11,\"unavailable\":null}}"),
            text,
            StringComparison.Ordinal);

        var parsed = RuntimeBackupJson.ParseManifest(manifest);

        Assert.Null(parsed.BackupWindow.ProjectionCursorsAtStart["unavailable"]);
        Assert.Null(parsed.ProjectionCursors["unavailable"]);
        Assert.Null(parsed.BackupWindow.ProjectionCursorsAtEnd["unavailable"]);
    }

    [Theory]
    [InlineData("\"completed_at\":\"2026-07-23T01:02:05.0000000+00:00\"", "\"completed_at\":\"2026-07-23T01:02:00.0000000+00:00\"")]
    [InlineData("\"started_at\":\"2026-07-23T01:02:01.0000000+00:00\"", "\"started_at\":\"2026-07-23T02:02:01.0000000+01:00\"")]
    public void Backup_window_rejects_reversed_or_non_utc_timestamps(string original, string replacement)
    {
        var malformed = Replace(ValidManifest(), original, replacement);

        AssertManifestInvalid(malformed);
    }

    [Theory]
    [InlineData("\"projection_cursors\":{\"monitor\":10,\"unavailable\":null}", "\"projection_cursors\":{\"monitor\":8,\"unavailable\":null}")]
    [InlineData("\"projection_cursors_at_end\":{\"monitor\":11,\"unavailable\":null}", "\"projection_cursors_at_end\":{\"monitor\":8,\"unavailable\":null}")]
    public void Backup_window_rejects_comparable_cursor_regression(string original, string replacement)
    {
        var malformed = Replace(ValidManifest(), original, replacement);

        AssertManifestInvalid(malformed);
    }

    [Fact]
    public void Backup_window_rejects_missing_cursor_vector_members()
    {
        var malformed = Replace(
            ValidManifest(),
            "\"projection_cursors_at_start\":{\"monitor\":9,\"unavailable\":null}",
            "\"projection_cursors_at_start\":{\"monitor\":9}");

        AssertManifestInvalid(malformed);
    }

    [Fact]
    public void Backup_window_rejects_cursor_vectors_over_the_inventory_limit()
    {
        var cursors = string.Join(",", Enumerable.Range(0, RuntimeBackupLimits.MaximumInventoryItems + 1)
            .Select(index => $"\"cursor_{index:D3}\":{index}"));
        var malformed = Replace(
            ValidManifest(),
            "\"projection_cursors_at_start\":{\"monitor\":9,\"unavailable\":null}",
            $"\"projection_cursors_at_start\":{{{cursors}}}");

        AssertManifestInvalid(malformed);
    }

    [Fact]
    public void Backup_window_rejects_noncanonical_nested_property_order_as_manifest_invalid()
    {
        var malformed = Replace(
            ValidManifest(),
            "\"started_at\":\"2026-07-23T01:02:01.0000000+00:00\",\"completed_at\":\"2026-07-23T01:02:05.0000000+00:00\"",
            "\"completed_at\":\"2026-07-23T01:02:05.0000000+00:00\",\"started_at\":\"2026-07-23T01:02:01.0000000+00:00\"");

        AssertManifestInvalid(malformed);
    }

    [Theory]
    [InlineData("configured")]
    [InlineData("empty")]
    [InlineData("absent")]
    public void Proposal_apply_external_state_accepts_only_the_closed_configuration_states(string sourceState)
    {
        var parsed = RuntimeBackupJson.ParseManifest(ValidManifest(sourceState));

        var proposal = Assert.Single(parsed.ExternalState, item => item.Kind == "proposal_apply");
        Assert.Equal(sourceState, proposal.SourceState);
        Assert.Equal("configuration_only", proposal.Consistency);
        Assert.Equal("reconfigure_apply_roots", proposal.RestoreAction);
    }

    [Theory]
    [InlineData("\"source_state\":\"absent\",\"included\":false,\"consistency\":\"configuration_only\"", "\"source_state\":\"unknown\",\"included\":false,\"consistency\":\"configuration_only\"")]
    [InlineData("\"consistency\":\"configuration_only\",\"restore_action\":\"reconfigure_apply_roots\"", "\"consistency\":\"must_be_empty\",\"restore_action\":\"none\"")]
    public void Proposal_apply_external_state_rejects_values_outside_the_closed_contract(string original, string replacement)
    {
        var malformed = Replace(ValidManifest(), original, replacement);

        AssertManifestInvalid(malformed);
    }

    [Theory]
    [InlineData("2026-01-01T00:00:00.0000000+00:00", "not-a-timestamp")]
    [InlineData("2026-01-01T00:00:00.0000000+00:00", "2026-01-01T01:00:00.0000000+01:00")]
    public void Retention_bounds_reject_malformed_or_non_utc_timestamps(string original, string replacement)
    {
        var malformed = Replace(ValidManifest(), original, replacement);

        AssertManifestInvalid(malformed);
    }

    [Fact]
    public void Manifest_writer_rejects_unbounded_tokens_before_archive_creation()
    {
        var exception = Assert.Throws<RuntimeBackupException>(() => RuntimeBackupJson.WriteManifest(
            ValidData() with { SourcePlatform = new string('x', 257) }));

        Assert.Equal(RuntimeBackupErrorCodes.ManifestInvalid, exception.Code);
    }

    private static byte[] ValidManifest(string proposalState = "absent") => RuntimeBackupJson.WriteManifest(ValidData(proposalState));

    private static RuntimeBackupManifestData ValidData(string proposalState = "absent") => new(
        new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero),
        "1.2.3",
        "windows",
        new string('a', 64),
        4096,
        "wal",
        new RuntimeBackupBackupWindow(
            new DateTimeOffset(2026, 7, 23, 1, 2, 1, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 23, 1, 2, 5, TimeSpan.Zero),
            new Dictionary<string, long?>(StringComparer.Ordinal) { ["monitor"] = 9, ["unavailable"] = null },
            new Dictionary<string, long?>(StringComparer.Ordinal) { ["monitor"] = 11, ["unavailable"] = null }),
        new Dictionary<string, int>(StringComparer.Ordinal) { ["monitor"] = 7, ["runtime_backup"] = 1 },
        new Dictionary<string, long>(StringComparer.Ordinal) { ["runtime_probe"] = 1 },
        new Dictionary<string, long?>(StringComparer.Ordinal) { ["monitor"] = 10, ["unavailable"] = null },
        new RuntimeBackupRetentionSummary(
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["analysis_run_raw"] = 0,
                ["analysis_sdk_directory"] = 0,
                ["raw_record"] = 0,
                ["sensitive_bundle"] = 0,
                ["session_event_content"] = 0,
            },
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["deleted"] = 0,
                ["deleting"] = 0,
                ["deletion_failed"] = 0,
                ["deletion_queued"] = 0,
                ["expired_pending_deletion"] = 0,
                ["expiring"] = 0,
                ["retained_by_policy"] = 0,
            },
            0,
            "2026-01-01T00:00:00.0000000+00:00",
            "2026-01-02T00:00:00.0000000+00:00",
            "2026-04-01T00:00:00.0000000+00:00",
            "2026-04-02T00:00:00.0000000+00:00",
            ["raw-default-90d/v1"]),
        [
            new("ephemeral_runtime", "absent", false, "ephemeral", "restart_rematerializes"),
            new("setup_storage", "absent", false, "host_bound", "rerun_setup"),
            new("proposal_apply", proposalState, false, "configuration_only", "reconfigure_apply_roots"),
            new("operator_backups", "not_inventoried", false, "operator_owned", "retain_or_delete_separately"),
        ]);

    private static byte[] Replace(byte[] manifest, string original, string replacement)
    {
        var text = Encoding.UTF8.GetString(manifest);
        original = JsonEncoded(original);
        replacement = JsonEncoded(replacement);
        Assert.Contains(original, text, StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(text.Replace(original, replacement, StringComparison.Ordinal));
    }

    private static string JsonEncoded(string value) => value.Replace("+", "\\u002B", StringComparison.Ordinal);

    private static void AssertManifestInvalid(byte[] manifest)
    {
        var exception = Assert.Throws<RuntimeBackupException>(() => RuntimeBackupJson.ParseManifest(manifest));
        Assert.Equal(RuntimeBackupErrorCodes.ManifestInvalid, exception.Code);
    }
}
