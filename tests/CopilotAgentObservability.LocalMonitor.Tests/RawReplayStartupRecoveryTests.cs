using System.Globalization;
using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.RawReplay;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RawReplayStartupRecoveryTests
{
    [Theory]
    [InlineData("member_8_verified_before_cursor")]
    [InlineData("published_intent_before_move")]
    [InlineData("moved_before_completion")]
    public async Task Build_recovers_raw_replay_capture_before_the_host_is_exposed(string crashPoint)
    {
        using var temp = new MonitorTempDirectory();
        const string replayId = "startup-replay";
        var archive = Archive();
        var execution = new RawReplayEngine().Replay(replayId, archive);
        Assert.True(execution.Success, execution.ErrorCode);
        var catalog = new RetentionCatalogStore(temp.RetentionContext, temp.TimeProvider);
        var crashing = new RetentionSensitiveBundleStore(catalog, point =>
        {
            if (point == crashPoint) throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.CaptureRawReplay(
            replayId,
            archive,
            execution,
            Path.Combine(temp.Path, "raw-replays")));
        var captureId = RetentionRawReplayStore.CaptureId(replayId);

        using (BuildHost(temp)) { }

        Assert.Equal("complete", Scalar<string>(temp.DatabasePath,
            "SELECT phase FROM retention_file_capture_reservations WHERE capture_id=$capture;", ("$capture", captureId)));
        Assert.Equal(1L, Scalar<long>(temp.DatabasePath,
            "SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle' AND source_item_id=$capture;", ("$capture", captureId)));
        Assert.True(Directory.Exists(Path.Combine(temp.Path, "raw-replays", captureId)));

        using (BuildHost(temp)) { }

        Assert.Equal(1L, Scalar<long>(temp.DatabasePath,
            "SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle' AND source_item_id=$capture;", ("$capture", captureId)));
        var retained = await new RetentionRawReplayStore(catalog, Path.Combine(temp.Path, "raw-replays"), temp.TimeProvider)
            .ReadAsync(replayId, CancellationToken.None);
        Assert.Equal(RetainedRawReplayReadDisposition.Granted, retained.Disposition);
        await Assert.IsType<RetainedRawReplayLease>(retained.Lease).DisposeAsync();
    }

    [Fact]
    public async Task Build_cleans_an_incomplete_owned_capture_and_records_the_blocker_idempotently()
    {
        using var temp = new MonitorTempDirectory();
        const string replayId = "partial-replay";
        var archive = Archive();
        var execution = new RawReplayEngine().Replay(replayId, archive);
        Assert.True(execution.Success, execution.ErrorCode);
        var catalog = new RetentionCatalogStore(temp.RetentionContext, temp.TimeProvider);
        var crashing = new RetentionSensitiveBundleStore(catalog, point =>
        {
            if (point == "member_0_verified_before_cursor") throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.CaptureRawReplay(
            replayId,
            archive,
            execution,
            Path.Combine(temp.Path, "raw-replays")));
        var captureId = RetentionRawReplayStore.CaptureId(replayId);
        var staging = Scalar<string>(temp.DatabasePath,
            "SELECT staging_locator FROM retention_file_capture_reservations WHERE capture_id=$capture;", ("$capture", captureId));

        using (BuildHost(temp)) { }

        Assert.False(Directory.Exists(staging));
        Assert.Equal("retention_capture_incomplete", Scalar<string>(temp.DatabasePath,
            "SELECT error_code FROM retention_file_capture_reservations WHERE capture_id=$capture;", ("$capture", captureId)));
        Assert.Equal(0L, Scalar<long>(temp.DatabasePath,
            "SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle' AND source_item_id=$capture;", ("$capture", captureId)));
        var firstState = ReservationState(temp.DatabasePath, captureId);

        using (BuildHost(temp)) { }

        Assert.Equal(firstState, ReservationState(temp.DatabasePath, captureId));
        var retained = await new RetentionRawReplayStore(catalog, Path.Combine(temp.Path, "raw-replays"), temp.TimeProvider)
            .ReadAsync(replayId, CancellationToken.None);
        Assert.Equal(RetainedRawReplayReadDisposition.NotFound, retained.Disposition);
    }

    [Fact]
    public void Build_drains_more_than_one_startup_recovery_batch_before_exposing_the_host()
    {
        using var temp = new MonitorTempDirectory();
        var catalog = new RetentionCatalogStore(temp.RetentionContext, temp.TimeProvider);
        var count = RetentionFileCaptureContracts.MaximumMemberCount + 1;
        for (var index = 0; index < count; index++)
            catalog.ReserveSensitiveBundle(Path.Combine(temp.Path, $"pending-{index:000}"));
        Assert.Equal(count, Scalar<long>(temp.DatabasePath,
            "SELECT COUNT(*) FROM retention_file_capture_reservations WHERE phase <> 'complete' AND error_code IS NULL;"));

        using (BuildHost(temp)) { }

        Assert.Equal(0L, Scalar<long>(temp.DatabasePath,
            "SELECT COUNT(*) FROM retention_file_capture_reservations WHERE phase <> 'complete' AND error_code IS NULL;"));
        using (BuildHost(temp)) { }
        Assert.Equal(0L, Scalar<long>(temp.DatabasePath,
            "SELECT COUNT(*) FROM retention_file_capture_reservations WHERE phase <> 'complete' AND error_code IS NULL;"));
    }

    private static WebApplication BuildHost(MonitorTempDirectory temp) => MonitorHost.Build(
        new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes),
        new MonitorHostTestOptions
        {
            TimeProvider = temp.TimeProvider,
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartRetentionCleanupWorker = false,
            UseUserSecrets = false,
        });

    private static byte[] Archive()
    {
        var service = new RawReplayArchiveService();
        var now = new DateTimeOffset(2026, 7, 23, 4, 5, 6, TimeSpan.Zero);
        var snapshot = new RawReplaySnapshot(
            "startup-snapshot",
            now,
            "monitor-v1",
            [new RawReplayRecord(1, "raw-otlp", "trace-one", now, null,
                "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"trace-one\",\"spanId\":\"span-one\"}]}]}]}",
                1,
                new("copilot-cli", "1", "otlp-json", "adapter-v1", "schema-v1", new string('a', 64),
                    "supported", "available", "not_applied_raw_capture", RawReplayContractVersions.CredentialScanner))],
            [],
            ["session_content_not_requested"]);
        var control = new RawReplayExportControl(
            RawReplayContractVersions.ExportControl,
            RawReplayContractVersions.BundleProfile,
            now,
            new(RawRecordIds: [1]),
            false,
            false,
            null,
            null);
        var preview = service.Preview(snapshot, control);
        var created = service.Create(snapshot, control with
        {
            PreviewDigest = preview.PreviewDigest,
            Consent = new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase),
        });
        Assert.True(created.Success, created.ErrorCode);
        return created.ArchiveBytes!;
    }

    private static string ReservationState(string databasePath, string captureId) => Scalar<string>(databasePath,
        "SELECT phase || '|' || durable_cursor || '|' || COALESCE(error_code,'') FROM retention_file_capture_reservations WHERE capture_id=$capture;",
        ("$capture", captureId));

    private static T Scalar<T>(string databasePath, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private sealed class SimulatedCrashException : Exception;
}
