using System.Globalization;
using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.RawReplay;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RetentionRawReplayStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public async Task ReplayAsync_UsesExistingSensitiveBundleCaptureAndOperationReadLease()
    {
        using var fixture = new Fixture();
        var archive = Archive(1, "trace-one");
        var store = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent);

        var first = await store.ReplayAsync("replay-one", archive, CancellationToken.None);

        Assert.True(first.Success, first.ErrorCode);
        Assert.False(first.IdempotentReplay);
        Assert.Equal(0, first.Result!.ExternalModelInvocations);
        Assert.Equal(1, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle' AND policy_id='sensitive-bundle-7d' AND policy_version=1;"));
        Assert.Equal("complete", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations;"));
        Assert.Single(Directory.EnumerateDirectories(fixture.BundleParent));

        var retained = await store.ReadAsync("replay-one", CancellationToken.None);
        Assert.Equal(RetainedRawReplayReadDisposition.Granted, retained.Disposition);
        await using var lease = Assert.IsType<RetainedRawReplayLease>(retained.Lease);
        Assert.Equal(first.Result.ArchiveSha256, lease.Receipt.ArchiveSha256);
        Assert.Equal(1, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        await lease.DisposeAsync();
        Assert.Equal(0, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Fact]
    public async Task ReplayAsync_IsDurablyIdempotentAndRejectsSameIdWithDifferentArchive()
    {
        using var fixture = new Fixture();
        var firstStore = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent);
        var archive = Archive(1, "trace-one");
        Assert.True((await firstStore.ReplayAsync("replay-stable", archive, CancellationToken.None)).Success);

        var reopened = new RetentionRawReplayStore(fixture.Reopen(), fixture.BundleParent);
        var retry = await reopened.ReplayAsync("replay-stable", archive, CancellationToken.None);
        var conflict = await reopened.ReplayAsync("replay-stable", Archive(2, "trace-two"), CancellationToken.None);

        Assert.True(retry.Success, retry.ErrorCode);
        Assert.True(retry.IdempotentReplay);
        Assert.False(conflict.Success);
        Assert.Equal("replay_id_conflict", conflict.ErrorCode);
        Assert.Equal(1, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle';"));
        Assert.Single(Directory.EnumerateDirectories(fixture.BundleParent));
    }

    private static byte[] Archive(long id, string trace)
    {
        var service = new RawReplayArchiveService();
        var snapshot = new RawReplaySnapshot("snapshot", Now, "monitor-v1",
            [new RawReplayRecord(id, "raw-otlp", trace, Now, null,
                $"{{\"resourceSpans\":[{{\"scopeSpans\":[{{\"spans\":[{{\"traceId\":\"{trace}\",\"spanId\":\"span\"}}]}}]}}]}}", 1,
                new("copilot-cli", "1", "otlp-json", "adapter-v1", "schema-v1", new string('a', 64), "supported", "available", "not_applied_raw_capture", RawReplayContractVersions.CredentialScanner))],
            [], ["session_content_not_requested"]);
        var control = new RawReplayExportControl(RawReplayContractVersions.ExportControl, RawReplayContractVersions.BundleProfile, Now,
            new(RawRecordIds: [id]), false, false, null, null);
        var preview = service.Preview(snapshot, control);
        var created = service.Create(snapshot, control with
        {
            PreviewDigest = preview.PreviewDigest,
            Consent = new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase),
        });
        Assert.True(created.Success, created.ErrorCode);
        return created.ArchiveBytes!;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"retained-raw-replay-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            DatabasePath = Path.Combine(Root, "retention.db");
            BundleParent = Path.Combine(Root, "bundles");
            Context = RetentionCatalogContext.InitializeNewOwnedDatabase(DatabasePath, new FixedTimeProvider(Now));
            Catalog = new RetentionCatalogStore(Context, new FixedTimeProvider(Now));
        }

        public string Root { get; }
        public string DatabasePath { get; }
        public string BundleParent { get; }
        public RetentionCatalogContext Context { get; }
        public RetentionCatalogStore Catalog { get; }
        public RetentionCatalogStore Reopen() => new(RetentionCatalogContext.AdoptExistingCatalogV1(DatabasePath), new FixedTimeProvider(Now));

        public T Scalar<T>(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
