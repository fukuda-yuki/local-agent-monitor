using System.IO.Compression;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalComparisonBackupTests
{
    [Fact]
    public async Task BackupDropsEveryComparisonCategoryOnlyFromStaging()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("Synthetic comparison", null, fixture.Key(21)));
        var snapshot = LocalComparisonStoreTests.Snapshot(repositoryId: repository.RepositoryId);
        var clock = new MutableTimeProvider(snapshot.CreatedAt);
        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var service = new SqliteRuntimeBackupService(clock);
        Assert.True(service.CreateAndPublish(fixture.DatabasePath, Path.Combine(directory, "warmup.zip")).Success);
        using (var source = Open(fixture.DatabasePath)) LocalComparisonSchemaV1.Ensure(source);
        var comparisons = new SqliteLocalComparisonStore(fixture.DatabasePath, clock);
        Assert.Equal(LocalComparisonAcceptStatus.Accepted, comparisons.Accept(snapshot, default));
        Assert.Equal(LocalComparisonSaveStatus.Found, comparisons.SavedLifetime(snapshot.RepositoryId, snapshot.ComparisonId, true, default).Status);
        var output = Path.Combine(directory, "comparison.zip");

        var created = service.CreateAndPublish(fixture.DatabasePath, output);
        Assert.True(created.Success, created.ErrorCode);

        using (var source = Open(fixture.DatabasePath))
        {
            Assert.Equal(2, Scalar(source, "SELECT version FROM schema_version WHERE component='local_comparison';"));
            Assert.All(LocalComparisonSchemaV1.TableNames, name => Assert.True(Exists(source, name)));
            Assert.Equal(1, Scalar(source, "SELECT COUNT(*) FROM local_comparison_saved_lifetimes WHERE is_saved=1;"));
        }
        Assert.Equal(snapshot.Results[0].Payload, comparisons.Read(snapshot.RepositoryId, snapshot.ComparisonId, default).Snapshot!.Results[0].Payload);
        var extracted = Path.Combine(directory, "extracted.sqlite");
        using (var archive = ZipFile.OpenRead(output)) archive.GetEntry("database.sqlite")!.ExtractToFile(extracted);
        using var staged = Open(extracted);
        Assert.Equal(0, Scalar(staged, "SELECT COUNT(*) FROM schema_version WHERE component='local_comparison';"));
        Assert.All(LocalComparisonSchemaV1.TableNames, name => Assert.False(Exists(staged, name)));
        staged.Close();
        var restoredPath = Path.Combine(directory, "restored", "monitor.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(restoredPath)!);
        var restored = service.Restore(output, restoredPath, new RuntimeRestoreOptions());
        Assert.True(restored.Success, restored.ErrorCode);
        var reopened = new SqliteLocalComparisonStore(restoredPath, clock);
        reopened.EnsureSchema();
        Assert.Empty(reopened.ListSaved(snapshot.RepositoryId, default).Items);
        Assert.Equal(LocalComparisonReadStatus.NotFound, reopened.Read(snapshot.RepositoryId, snapshot.ComparisonId, default).Status);
    }

    private static SqliteConnection Open(string path) { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); c.Open(); return c; }
    private static long Scalar(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); }
    private static bool Exists(SqliteConnection c, string name) { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name=$name;"; cmd.Parameters.AddWithValue("$name", name); return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1; }
}
