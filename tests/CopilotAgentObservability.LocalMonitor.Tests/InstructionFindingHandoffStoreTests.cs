using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Analysis;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class InstructionFindingHandoffStoreTests
{
    [Fact]
    public void SaveAndGet_CanonicalHandoff_PreservesExactBytesAndChecksum()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteInstructionFindingHandoffStore(temp.DatabasePath);
        store.CreateSchema();
        var handoff = CreateHandoff(41, InstructionFindingCategoryV1.TestRequirementMissing);

        store.Save(handoff, DateTimeOffset.UnixEpoch.AddMinutes(2));
        var restored = Assert.IsType<InstructionFindingHandoffV1>(store.Get(41));

        Assert.Equal(InstructionFindingJsonV1.Serialize(handoff), InstructionFindingJsonV1.Serialize(restored));
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version, payload_json, payload_sha256, created_at FROM instruction_finding_handoffs WHERE analysis_run_id = 41;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(InstructionFindingContractsV1.HandoffSchemaVersion, reader.GetString(0));
        Assert.Equal(Encoding.UTF8.GetString(InstructionFindingJsonV1.Serialize(handoff)), reader.GetString(1));
        Assert.Matches("^[0-9a-f]{64}$", reader.GetString(2));
        Assert.Equal("1970-01-01T00:02:00.0000000+00:00", reader.GetString(3));
    }

    [Fact]
    public void Save_IdenticalRetry_IsIdempotentButConflictingRewriteFails()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteInstructionFindingHandoffStore(temp.DatabasePath);
        store.CreateSchema();
        var first = CreateHandoff(42, InstructionFindingCategoryV1.GoalClarity);
        var conflicting = CreateHandoff(42, InstructionFindingCategoryV1.Ambiguity);

        store.Save(first, DateTimeOffset.UnixEpoch);
        store.Save(first, DateTimeOffset.UnixEpoch.AddMinutes(1));
        var exception = Assert.Throws<InstructionFindingValidationException>(() =>
            store.Save(conflicting, DateTimeOffset.UnixEpoch.AddMinutes(2)));

        Assert.Equal(InstructionFindingValidationCodeV1.ConflictingPersistence, exception.Code);
        Assert.Equal(InstructionFindingJsonV1.Serialize(first), InstructionFindingJsonV1.Serialize(store.Get(42)!));
    }

    [Fact]
    public void Get_TamperedPayloadChecksum_RejectsPersistedCarrier()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteInstructionFindingHandoffStore(temp.DatabasePath);
        store.CreateSchema();
        store.Save(CreateHandoff(43, InstructionFindingCategoryV1.GoalClarity), DateTimeOffset.UnixEpoch);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE instruction_finding_handoffs SET payload_json = payload_json || ' ' WHERE analysis_run_id = 43;";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InstructionFindingValidationException>(() => store.Get(43));

        Assert.Equal(InstructionFindingValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public void Get_NoncanonicalPayloadWithMatchingChecksum_RejectsPersistedCarrier()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteInstructionFindingHandoffStore(temp.DatabasePath);
        store.CreateSchema();
        store.Save(CreateHandoff(44, InstructionFindingCategoryV1.GoalClarity), DateTimeOffset.UnixEpoch);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT payload_json FROM instruction_finding_handoffs WHERE analysis_run_id = 44;";
            var noncanonicalPayload = Assert.IsType<string>(read.ExecuteScalar()) + " ";
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(noncanonicalPayload))).ToLowerInvariant();

            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE instruction_finding_handoffs SET payload_json = $payload_json, payload_sha256 = $payload_sha256 WHERE analysis_run_id = 44;";
            update.Parameters.AddWithValue("$payload_json", noncanonicalPayload);
            update.Parameters.AddWithValue("$payload_sha256", checksum);
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        var exception = Assert.Throws<InstructionFindingValidationException>(() => store.Get(44));

        Assert.Equal(InstructionFindingValidationCodeV1.InvalidSerialization, exception.Code);
    }

    [Fact]
    public void CreateSchema_DefinesOnlyRepositorySafeHandoffColumns()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteInstructionFindingHandoffStore(temp.DatabasePath);
        store.CreateSchema();
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(instruction_finding_handoffs);";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));

        Assert.Equal(new[] { "analysis_run_id", "schema_version", "payload_json", "payload_sha256", "created_at" }, columns);
        Assert.DoesNotContain(columns, column => column.Contains("prompt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("response", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    private static InstructionFindingHandoffV1 CreateHandoff(long analysisRunId, InstructionFindingCategoryV1 category)
    {
        var locations = new[]
        {
            new InstructionFindingEvidenceLocationV1(null, "trace-anchor", "span-turn-1", 1, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn),
            new InstructionFindingEvidenceLocationV1(null, "trace-anchor", "span-turn-2", 2, InstructionEvidenceRelativePositionV1.Anchor, InstructionFindingEvidenceKindV1.Turn),
        };
        var references = locations
            .Select(location => location.ToReference())
            .ToArray();
        return InstructionFindingPipelineV1.Generate(
            analysisRunId,
            new InstructionFindingEvidenceIndexV1("trace-anchor", locations),
            [new InstructionFindingDraftV1(category, InstructionFindingVerdictV1.Supported, InstructionFindingExtractorSourceV1.PromptOnly, references)]);
    }
}
