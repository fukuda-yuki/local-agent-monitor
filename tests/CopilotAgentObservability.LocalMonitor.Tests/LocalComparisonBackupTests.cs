using System.IO.Compression;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalComparisonBackupTests
{
    [Fact]
    public void BackupDropsEveryComparisonCategoryOnlyFromStaging()
    {
        using var temp = new RuntimeBackupArchiveTests.RuntimeBackupTemp();
        temp.CreateDatabase("source");
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);
        Assert.True(service.CreateAndPublish(temp.DatabasePath, Path.Combine(temp.DirectoryPath, "warmup.zip")).Success);
        using (var source = Open(temp.DatabasePath)) LocalComparisonSchemaV1.Ensure(source);
        var output = Path.Combine(temp.DirectoryPath, "comparison.zip");

        var created = service.CreateAndPublish(temp.DatabasePath, output);
        Assert.True(created.Success, created.ErrorCode);

        using (var source = Open(temp.DatabasePath))
        {
            Assert.Equal(1, Scalar(source, "SELECT version FROM schema_version WHERE component='local_comparison';"));
            Assert.All(LocalComparisonSchemaV1.TableNames, name => Assert.True(Exists(source, name)));
        }
        var extracted = Path.Combine(temp.DirectoryPath, "extracted.sqlite");
        using (var archive = ZipFile.OpenRead(output)) archive.GetEntry("database.sqlite")!.ExtractToFile(extracted);
        using var staged = Open(extracted);
        Assert.Equal(0, Scalar(staged, "SELECT COUNT(*) FROM schema_version WHERE component='local_comparison';"));
        Assert.All(LocalComparisonSchemaV1.TableNames, name => Assert.False(Exists(staged, name)));
    }

    private static SqliteConnection Open(string path) { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); c.Open(); return c; }
    private static long Scalar(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture); }
    private static bool Exists(SqliteConnection c, string name) { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name=$name;"; cmd.Parameters.AddWithValue("$name", name); return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1; }
}
