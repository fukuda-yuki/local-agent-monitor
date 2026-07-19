using System.Security.Cryptography;
using System.Reflection;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionFileCaptureSchemaTests
{
    private const string StoreId = "00112233445566778899aabbccddeeff";

    [Fact]
    public void CreateSchema_AddsFileCaptureReservationAndMemberTablesToTheCommittedRetentionV1Fixture()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "retention", "retention-catalog-v1.sqlite");
        var copy = Path.Combine(Path.GetTempPath(), $"retention-file-capture-{Guid.NewGuid():N}.sqlite");
        File.Copy(source, copy);
        try
        {
            var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();

            new RetentionCatalogStore(copy).CreateSchema();
            SqliteConnection.ClearAllPools();
            new RetentionCatalogStore(copy).CreateSchema();

            Assert.Equal(sourceHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant());
            using var connection = Open(copy);
            Assert.Equal(1L, Scalar<long>(connection, "SELECT version FROM retention_component_versions WHERE component='retention';"));
            Assert.True(TableExists(connection, "retention_file_capture_reservations"));
            Assert.True(TableExists(connection, "retention_file_capture_members"));
            Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_retention_file_capture_reservations_phase_updated';"));
            Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_retention_file_capture_members_deletion_order';"));
            Assert.Equal("ok", Scalar<string>(connection, "PRAGMA integrity_check;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { copy, copy + "-wal", copy + "-shm" }) if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    [Fact]
    public void SensitiveBundleReceipt_BindsCanonicalCaptureReservationMarkerManifestAndOwnerToken()
    {
        var assembly = typeof(RetentionOwnershipReceipt).Assembly;
        var inputType = assembly.GetType("CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionSensitiveBundleOwnershipReceiptInput");
        Assert.NotNull(inputType);
        var create = typeof(RetentionOwnershipReceipt).GetMethod("CreateSensitiveBundle", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(create);
        var token = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var marker = SHA256.HashData("marker"u8);
        var manifest = SHA256.HashData("manifest"u8);
        var input = Activator.CreateInstance(inputType!, StoreId, "0123456789abcdef0123456789abcdef", "2026-07-19T01:02:03.0000000+00:00", 639200197230000000L, marker, manifest, token)!;
        var receipt = Assert.IsType<byte[]>(create!.Invoke(null, [input]));
        var changedMarker = Activator.CreateInstance(inputType!, StoreId, "0123456789abcdef0123456789abcdef", "2026-07-19T01:02:03.0000000+00:00", 639200197230000000L, SHA256.HashData("other-marker"u8), manifest, token)!;

        Assert.Equal(32, receipt.Length);
        Assert.NotEqual(receipt, Assert.IsType<byte[]>(create.Invoke(null, [changedMarker])));
    }

    [Fact]
    public void FileCaptureContracts_AllowOnlyCanonicalRelativeMembersWithinThePinnedLimits()
    {
        var type = typeof(RetentionOwnershipReceipt).Assembly.GetType("CopilotAgentObservability.Persistence.Sqlite.Retention.RetentionFileCaptureContracts");
        Assert.NotNull(type);
        var validate = type!.GetMethod("IsCanonicalRelativePath", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(validate);

        Assert.True(Assert.IsType<bool>(validate!.Invoke(null, ["evidence/raw.json"])));
        foreach (var invalid in new[] { "", "/rooted", "C:/drive", "evidence\\raw.json", "evidence/../raw.json", "./raw.json", ".retention-owner.v1/" })
            Assert.False(Assert.IsType<bool>(validate.Invoke(null, [invalid])));
        Assert.Equal(256, Assert.IsType<int>(type.GetProperty("MaximumMemberCount", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)));
        Assert.Equal(128 * 1024 * 1024L, Assert.IsType<long>(type.GetProperty("MaximumMemberBytes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)));
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }
}
