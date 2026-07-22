using System.Text;
using System.Text.Json.Nodes;
using CopilotAgentObservability.LocalMonitor.Analysis;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalEvidenceDatasetStoreTests
{
    [Fact]
    public async Task SaveAndGet_PersistsBothFormsAtomicallyAndAcceptsIdenticalReplay()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None);

            store.Save(extraction, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            store.Save(extraction, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
            var reopened = store.Get(extraction.RawLocal.ExtractionId);

            Assert.NotNull(reopened);
            Assert.Equal(extraction.RawLocalBytes, reopened.RawLocalBytes);
            Assert.Equal(extraction.RepositorySafeBytes, reopened.RepositorySafeBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Save_RejectsConflictingRewrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None);
            store.Save(extraction, DateTimeOffset.UtcNow);
            var conflict = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None, status: "changed");

            var exception = Assert.Throws<HistoricalEvidenceValidationException>(() => { store.Save(conflict, DateTimeOffset.UtcNow); });
            Assert.Equal(HistoricalEvidenceValidationCodeV1.ConflictingPersistence, exception.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Get_RejectsTamperedChecksumOrPayload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None);
            store.Save(extraction, DateTimeOffset.UtcNow);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE historical_evidence_datasets SET payload_json='{}' WHERE representation='raw_local';";
                command.ExecuteNonQuery();
            }

            var exception = Assert.Throws<HistoricalEvidenceValidationException>(() => { store.Get(extraction.RawLocal.ExtractionId); });
            Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidPersistence, exception.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Get_RejectsChecksumMatchedForgedDerivedGroupIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None);
            store.Save(extraction, DateTimeOffset.UtcNow);
            var originalId = extraction.RawLocal.EvidenceGroups[0].GroupId;
            const string forgedId = "historical-group-00000000000000000000000000000000";

            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var select = connection.CreateCommand();
                select.CommandText = "SELECT representation,payload_json FROM historical_evidence_datasets ORDER BY representation;";
                using var reader = select.ExecuteReader();
                var rows = new List<(string Representation, string Payload)>();
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
                reader.Close();

                foreach (var row in rows)
                {
                    var payload = row.Payload.Replace(originalId, forgedId, StringComparison.Ordinal);
                    var checksum = HistoricalEvidenceExtractorV1.Sha256(Encoding.UTF8.GetBytes(payload));
                    using var update = connection.CreateCommand();
                    update.CommandText = "UPDATE historical_evidence_datasets SET payload_json=$payload,payload_sha256=$checksum WHERE representation=$representation;";
                    update.Parameters.AddWithValue("$payload", payload);
                    update.Parameters.AddWithValue("$checksum", checksum);
                    update.Parameters.AddWithValue("$representation", row.Representation);
                    update.ExecuteNonQuery();
                }
            }

            var exception = Assert.Throws<HistoricalEvidenceValidationException>(() => store.Get(extraction.RawLocal.ExtractionId));
            Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidPersistence, exception.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Get_RejectsChecksumMatchedSensitiveRawDescriptor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None, rawDescriptor: "benign descriptor");
            store.Save(extraction, DateTimeOffset.UtcNow);

            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var select = connection.CreateCommand();
                select.CommandText = "SELECT payload_json FROM historical_evidence_datasets WHERE representation='raw_local';";
                var node = JsonNode.Parse((string)select.ExecuteScalar()!)!.AsObject();
                node["sessions"]![0]!["raw_local_descriptor"] = "C:\\secret.txt";
                var payload = node.ToJsonString();
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE historical_evidence_datasets SET payload_json=$payload,payload_sha256=$checksum WHERE representation='raw_local';";
                update.Parameters.AddWithValue("$payload", payload);
                update.Parameters.AddWithValue("$checksum", HistoricalEvidenceExtractorV1.Sha256(Encoding.UTF8.GetBytes(payload)));
                update.ExecuteNonQuery();
            }

            var exception = Assert.Throws<HistoricalEvidenceValidationException>(() => store.Get(extraction.RawLocal.ExtractionId));
            Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidPersistence, exception.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Get_RejectsChecksumMatchedUnsafeSourceVersionInBothRepresentations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None);
            store.Save(extraction, DateTimeOffset.UtcNow);

            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var select = connection.CreateCommand();
                select.CommandText = "SELECT representation,payload_json FROM historical_evidence_datasets ORDER BY representation;";
                using var reader = select.ExecuteReader();
                var rows = new List<(string Representation, string Payload)>();
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
                reader.Close();
                foreach (var row in rows)
                {
                    var node = JsonNode.Parse(row.Payload)!.AsObject();
                    node["sessions"]![0]!["source_version"] = "C:\\secret";
                    var payload = node.ToJsonString();
                    using var update = connection.CreateCommand();
                    update.CommandText = "UPDATE historical_evidence_datasets SET payload_json=$payload,payload_sha256=$checksum WHERE representation=$representation;";
                    update.Parameters.AddWithValue("$payload", payload);
                    update.Parameters.AddWithValue("$checksum", HistoricalEvidenceExtractorV1.Sha256(Encoding.UTF8.GetBytes(payload)));
                    update.Parameters.AddWithValue("$representation", row.Representation);
                    update.ExecuteNonQuery();
                }
            }

            var exception = Assert.Throws<HistoricalEvidenceValidationException>(() => store.Get(extraction.RawLocal.ExtractionId));
            Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidPersistence, exception.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Get_MalformedRepresentationRowMapsToBoundedInvalidPersistence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None);
            store.Save(extraction, DateTimeOffset.UtcNow);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA ignore_check_constraints=ON; UPDATE historical_evidence_datasets SET representation='malformed' WHERE representation='raw_local';";
                command.ExecuteNonQuery();
            }

            var exception = Assert.Throws<HistoricalEvidenceValidationException>(() => store.Get(extraction.RawLocal.ExtractionId));
            Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidPersistence, exception.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Get_RejectsChecksumMatchedCrossRepresentationMetadataMismatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None);
            store.Save(extraction, DateTimeOffset.UtcNow);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var select = connection.CreateCommand();
                select.CommandText = "SELECT payload_json FROM historical_evidence_datasets WHERE representation='repository_safe';";
                var node = JsonNode.Parse((string)select.ExecuteScalar()!)!.AsObject();
                node["sessions"]![0]!["metadata"]!["repository"] = "repository-ref-00000000000000000000000000000000";
                var payload = node.ToJsonString();
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE historical_evidence_datasets SET payload_json=$payload,payload_sha256=$checksum WHERE representation='repository_safe';";
                update.Parameters.AddWithValue("$payload", payload);
                update.Parameters.AddWithValue("$checksum", HistoricalEvidenceExtractorV1.Sha256(Encoding.UTF8.GetBytes(payload)));
                update.ExecuteNonQuery();
            }

            var exception = Assert.Throws<HistoricalEvidenceValidationException>(() => store.Get(extraction.RawLocal.ExtractionId));
            Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidPersistence, exception.Code);
            Assert.Null(exception.InnerException);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Get_NullRequiredGraphMapsToBoundedInvalidPersistence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None);
            store.Save(extraction, DateTimeOffset.UtcNow);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var select = connection.CreateCommand();
                select.CommandText = "SELECT payload_json FROM historical_evidence_datasets WHERE representation='raw_local';";
                var node = JsonNode.Parse((string)select.ExecuteScalar()!)!.AsObject();
                node["sessions"] = null;
                var payload = node.ToJsonString();
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE historical_evidence_datasets SET payload_json=$payload,payload_sha256=$checksum WHERE representation='raw_local';";
                update.Parameters.AddWithValue("$payload", payload);
                update.Parameters.AddWithValue("$checksum", HistoricalEvidenceExtractorV1.Sha256(Encoding.UTF8.GetBytes(payload)));
                update.ExecuteNonQuery();
            }

            var exception = Assert.Throws<HistoricalEvidenceValidationException>(() => store.Get(extraction.RawLocal.ExtractionId));
            Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidPersistence, exception.Code);
            Assert.Null(exception.InnerException);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Get_RejectsOversizedPayloadBeforeTextMaterialization()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None);
            store.Save(extraction, DateTimeOffset.UtcNow);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA ignore_check_constraints=ON; UPDATE historical_evidence_datasets SET payload_json=CAST(zeroblob(67108865) AS TEXT) WHERE representation='raw_local';";
                command.ExecuteNonQuery();
            }

            var exception = Assert.Throws<HistoricalEvidenceValidationException>(() => store.Get(extraction.RawLocal.ExtractionId));
            Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidPersistence, exception.Code);
            Assert.Null(exception.InnerException);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Get_MapsChecksumMatchedNullModelToFixedPersistenceFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.sqlite");
        try
        {
            var store = new SqliteHistoricalEvidenceDatasetStoreV1(path);
            store.CreateSchema();
            var extraction = await HistoricalEvidenceTestFixture.CreateAsync(CancellationToken.None, model: "gpt-5");
            store.Save(extraction, DateTimeOffset.UtcNow);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var select = connection.CreateCommand();
                select.CommandText = "SELECT payload_json FROM historical_evidence_datasets WHERE representation='raw_local';";
                var node = JsonNode.Parse((string)select.ExecuteScalar()!)!.AsObject();
                node["sessions"]!.AsArray()[0]!["metadata"]!["model_observations"]!.AsArray()[0]!["model"] = null;
                var payload = node.ToJsonString();
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE historical_evidence_datasets SET payload_json=$payload,payload_sha256=$checksum WHERE representation='raw_local';";
                update.Parameters.AddWithValue("$payload", payload);
                update.Parameters.AddWithValue("$checksum", HistoricalEvidenceExtractorV1.Sha256(Encoding.UTF8.GetBytes(payload)));
                Assert.Equal(1, update.ExecuteNonQuery());
            }

            var exception = Assert.Throws<HistoricalEvidenceValidationException>(() => store.Get(extraction.RawLocal.ExtractionId));
            Assert.Equal(HistoricalEvidenceValidationCodeV1.InvalidPersistence, exception.Code);
            Assert.Null(exception.InnerException);
        }
        finally { File.Delete(path); }
    }
}

internal static class HistoricalEvidenceTestFixture
{
    internal static async Task<HistoricalEvidenceExtractionV1> CreateAsync(
        CancellationToken cancellationToken,
        string status = "error",
        string? rawDescriptor = null,
        string? model = null)
    {
        var sessionId = Guid.Parse("018f0000-0000-7000-8000-000000000001");
        var metadata = new HistoricalSessionMetadataV1(
            sessionId,
            CopilotAgentObservability.Telemetry.Sessions.SessionSourceSurface.CopilotSdk,
            "1.0.0", "adapter.v1",
            CopilotAgentObservability.Telemetry.Sessions.SessionCompleteness.Full,
            [], HistoricalEvidenceSourceKindV1.LiveOtel,
            CopilotAgentObservability.Telemetry.Sessions.SessionContentState.Available,
            "repo", "workspace", null, null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new(true, true, true, true, true, true, true, true, true, true, true, true),
            [new(sessionId, "trace-1", "span-1", 1, HistoricalEvidenceRelativePositionV1.Anchor)],
            [])
        {
            ModelObservations = model is null
                ? []
                : [new(model, new(sessionId, "trace-1", "span-1", 1, HistoricalEvidenceRelativePositionV1.Anchor))],
        };
        var source = new SingleSource(metadata, status, rawDescriptor);
        var selection = new HistoricalEvidenceSelectionV1(
            "repo", null, null, null, [], [], null, null, 50, false);
        return await HistoricalEvidenceExtractorV1.ExtractAsync(selection, source, cancellationToken);
    }

    private sealed class SingleSource(HistoricalSessionMetadataV1 metadata, string status, string? rawDescriptor) : IHistoricalEvidenceSnapshotSourceV1
    {
        public ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(HistoricalEvidenceSelectionV1 selection, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IHistoricalEvidenceSnapshotLeaseV1>(new Lease(metadata, status, rawDescriptor));

        private sealed class Lease(HistoricalSessionMetadataV1 metadata, string status, string? rawDescriptor) : IHistoricalEvidenceSnapshotLeaseV1
        {
            public string SnapshotId => "snapshot-store";
            public IReadOnlyList<HistoricalSessionMetadataV1> Sessions => [metadata];
            public long OmittedEarlierMatchingSessionCount => 0;
            public ValueTask<IReadOnlyList<HistoricalEvidenceGroupDraftV1>> ReadEvidenceAsync(Guid sessionId, bool includeDescriptors, CancellationToken cancellationToken) =>
                ValueTask.FromResult<IReadOnlyList<HistoricalEvidenceGroupDraftV1>>(
                [
                    new(rawDescriptor is null ? HistoricalEvidenceGroupKindV1.ErrorSpan : HistoricalEvidenceGroupKindV1.UserCorrection,
                        [new(metadata.SessionId, "trace-1", "span-1", 1, HistoricalEvidenceRelativePositionV1.Anchor)],
                        null, null, status, null, null, null, null, rawDescriptor)
                ]);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
