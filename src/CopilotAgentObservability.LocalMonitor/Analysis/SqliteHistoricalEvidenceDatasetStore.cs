using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class SqliteHistoricalEvidenceDatasetStoreV1
{
    private readonly string databasePath;

    internal SqliteHistoricalEvidenceDatasetStoreV1(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
    }

    internal void CreateSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS historical_evidence_datasets (
                extraction_id TEXT NOT NULL,
                representation TEXT NOT NULL CHECK(representation IN ('raw_local','repository_safe')),
                schema_version TEXT NOT NULL,
                snapshot_id TEXT NOT NULL,
                payload_json TEXT NOT NULL CHECK(length(CAST(payload_json AS BLOB))<=67108864),
                payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256)=64 AND payload_sha256=lower(payload_sha256)),
                created_at TEXT NOT NULL,
                PRIMARY KEY(extraction_id,representation)
            );
            """;
        command.ExecuteNonQuery();
    }

    internal void Save(HistoricalEvidenceExtractionV1 extraction, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ValidateExtraction(extraction);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        SaveOne(connection, transaction, extraction.RawLocal, extraction.RawLocalBytes, extraction.RawLocalSha256, createdAt);
        SaveOne(connection, transaction, extraction.RepositorySafe, extraction.RepositorySafeBytes, extraction.RepositorySafeSha256, createdAt);
        transaction.Commit();
    }

    internal HistoricalEvidenceExtractionV1? Get(string extractionId)
    {
        if (string.IsNullOrWhiteSpace(extractionId)) throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidContract);
        try
        {
            using var connection = Open();
            EnsurePayloadSizes(connection, extractionId);
            var rows = ReadRows(connection, extractionId);
            if (rows.Count == 0) return null;
            if (rows.Count != 2) throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
            var raw = ReadDataset(rows.Single(item => item.Representation == "raw_local"), HistoricalEvidenceRepresentationV1.RawLocal);
            var safe = ReadDataset(rows.Single(item => item.Representation == "repository_safe"), HistoricalEvidenceRepresentationV1.RepositorySafe);
            var extraction = new HistoricalEvidenceExtractionV1(raw.Dataset, safe.Dataset, raw.Bytes, safe.Bytes, raw.Checksum, safe.Checksum);
            ValidateExtraction(extraction);
            return extraction;
        }
        catch (HistoricalEvidenceValidationException exception) when (exception.Code == HistoricalEvidenceValidationCodeV1.InvalidPersistence)
        {
            throw;
        }
        catch (HistoricalEvidenceValidationException)
        {
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or InvalidCastException
            or FormatException or ArgumentException or OverflowException or NullReferenceException)
        {
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
        }
    }

    private static void SaveOne(SqliteConnection connection, SqliteTransaction transaction, HistoricalEvidenceDatasetV1 dataset, byte[] bytes, string checksum, DateTimeOffset createdAt)
    {
        var canonical = HistoricalEvidenceJsonV1.Serialize(dataset);
        if (!canonical.SequenceEqual(bytes) || HistoricalEvidenceExtractorV1.Sha256(bytes) != checksum)
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.ConflictingPersistence);
        var representation = dataset.Representation == HistoricalEvidenceRepresentationV1.RawLocal ? "raw_local" : "repository_safe";
        var payload = Encoding.UTF8.GetString(bytes);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO historical_evidence_datasets(extraction_id,representation,schema_version,snapshot_id,payload_json,payload_sha256,created_at)
            VALUES($id,$representation,$schema,$snapshot,$payload,$checksum,$created)
            ON CONFLICT(extraction_id,representation) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", dataset.ExtractionId);
        command.Parameters.AddWithValue("$representation", representation);
        command.Parameters.AddWithValue("$schema", dataset.SchemaVersion);
        command.Parameters.AddWithValue("$snapshot", dataset.SnapshotId);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$checksum", checksum);
        command.Parameters.AddWithValue("$created", createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() != 0) return;
        var existing = ReadRows(connection, dataset.ExtractionId).SingleOrDefault(item => item.Representation == representation);
        if (existing is null || existing.SchemaVersion != dataset.SchemaVersion || existing.SnapshotId != dataset.SnapshotId || existing.Payload != payload || existing.Checksum != checksum)
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.ConflictingPersistence);
    }

    private static void ValidateExtraction(HistoricalEvidenceExtractionV1 extraction)
    {
        if (extraction.RawLocal.Representation != HistoricalEvidenceRepresentationV1.RawLocal
            || extraction.RepositorySafe.Representation != HistoricalEvidenceRepresentationV1.RepositorySafe
            || extraction.RawLocal.ExtractionId != extraction.RepositorySafe.ExtractionId
            || extraction.RawLocal.ExtractionId != HistoricalEvidenceExtractorV1.ComputeExtractionId(extraction.RawLocal.SnapshotId, extraction.RawLocal.Selection)
            || extraction.RawLocal.SnapshotId != extraction.RepositorySafe.SnapshotId
            || !SelectionPairMatches(extraction.RawLocal.Selection, extraction.RepositorySafe.Selection)
            || extraction.RawLocal.TruncatedBefore != extraction.RepositorySafe.TruncatedBefore
            || extraction.RawLocal.TruncatedSessionCount != extraction.RepositorySafe.TruncatedSessionCount
            || extraction.RawLocal.Sessions.Count != extraction.RepositorySafe.Sessions.Count
            || extraction.RawLocal.ExcludedSessions.Count != extraction.RepositorySafe.ExcludedSessions.Count
            || extraction.RawLocal.EvidenceGroups.Select(item => (item.GroupId, item.Kind)).SequenceEqual(extraction.RepositorySafe.EvidenceGroups.Select(item => (item.GroupId, item.Kind))) == false)
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
        for (var index = 0; index < extraction.RawLocal.Sessions.Count; index++)
        {
            var raw = extraction.RawLocal.Sessions[index];
            var safe = extraction.RepositorySafe.Sessions[index];
            if (!Guid.TryParse(raw.SessionId, out var rawId) || SafeSession(rawId) != safe.SessionId
                || raw.SourceSurface != safe.SourceSurface || raw.SourceVersion != safe.SourceVersion || raw.AdapterVersion != safe.AdapterVersion
                || raw.Completeness != safe.Completeness || raw.SourceKind != safe.SourceKind || raw.ContentState != safe.ContentState
                || !raw.CompletenessReasons.SequenceEqual(safe.CompletenessReasons) || raw.DescriptorState != safe.DescriptorState
                || raw.Capabilities != safe.Capabilities || safe.RawLocalDescriptor is not null
                || !MetadataPairMatches(raw.Metadata, safe.Metadata, safe.SessionId))
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
        }
        for (var index = 0; index < extraction.RawLocal.ExcludedSessions.Count; index++)
        {
            var raw = extraction.RawLocal.ExcludedSessions[index];
            var safe = extraction.RepositorySafe.ExcludedSessions[index];
            if (!Guid.TryParse(raw.SessionId, out var rawId) || SafeSession(rawId) != safe.SessionId || raw.Reason != safe.Reason
                || (raw.Metadata is null) != (safe.Metadata is null)
                || raw.Metadata is not null && !MetadataPairMatches(raw.Metadata, safe.Metadata!, safe.SessionId))
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
        }
        for (var groupIndex = 0; groupIndex < extraction.RawLocal.EvidenceGroups.Count; groupIndex++)
        {
            var raw = extraction.RawLocal.EvidenceGroups[groupIndex];
            var safe = extraction.RepositorySafe.EvidenceGroups[groupIndex];
            if (raw.References.Count != safe.References.Count || safe.ExactCallId is not null || safe.ExactOwnershipId is not null
                || raw.NumericValue != safe.NumericValue || raw.Unit != safe.Unit || raw.Status != safe.Status
                || raw.CanonicalCallHash != safe.CanonicalCallHash || raw.FindingId != safe.FindingId
                || !FindingAssociationMatches(raw.FindingReceipt, raw.FindingCandidate, safe.FindingReceipt, safe.FindingCandidate))
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
            var expectedReferences = raw.References.Select(rawRef =>
            {
                if (!Guid.TryParse(rawRef.SessionId, out var rawSessionId)) throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
                var tokenized = InstructionFindingReferenceTokenizationV1.Tokenize(new(
                    rawSessionId.ToString(), rawRef.TraceId, rawRef.SpanId, rawRef.TurnIndex,
                    (InstructionEvidenceRelativePositionV1)(int)rawRef.RelativePosition));
                return new HistoricalEvidenceReferenceV1(tokenized.SessionId!, tokenized.TraceId, tokenized.SpanId, tokenized.TurnIndex, rawRef.RelativePosition);
            }).OrderBy(reference => reference.SessionId, StringComparer.Ordinal)
                .ThenBy(reference => reference.TraceId, StringComparer.Ordinal)
                .ThenBy(reference => reference.SpanId, StringComparer.Ordinal)
                .ThenBy(reference => reference.TurnIndex)
                .ThenBy(reference => reference.RelativePosition).ToArray();
            if (!expectedReferences.SequenceEqual(safe.References))
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
        }
        if (!DistributionMatches(extraction.RawLocal.Distribution, extraction.RepositorySafe.Distribution))
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
    }

    private static bool FindingAssociationMatches(
        InstructionFindingReceiptV1? rawReceipt,
        InstructionRuleCandidateV1? rawCandidate,
        InstructionFindingReceiptV1? safeReceipt,
        InstructionRuleCandidateV1? safeCandidate)
    {
        if (rawReceipt is null || safeReceipt is null)
            return rawReceipt is null && safeReceipt is null && rawCandidate is null && safeCandidate is null;
        if ((rawCandidate is null) != (safeCandidate is null)) return false;
        return JsonSerializer.SerializeToUtf8Bytes(rawReceipt).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(safeReceipt))
            && JsonSerializer.SerializeToUtf8Bytes(rawCandidate).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(safeCandidate));
    }

    private static string SafeSession(Guid id) => InstructionFindingReferenceTokenizationV1.Tokenize(new(
        id.ToString(), "x", null, 1, InstructionEvidenceRelativePositionV1.Anchor)).SessionId!;

    private static bool MetadataPairMatches(
        HistoricalDecisionMetadataV1 raw,
        HistoricalDecisionMetadataV1 safe,
        string safeSessionId)
    {
        if (safe.Repository != HistoricalEvidenceExtractorV1.TokenizeLabel("repository", raw.Repository)
            || safe.Workspace != HistoricalEvidenceExtractorV1.TokenizeLabel("workspace", raw.Workspace)
            || raw.StartedAt != safe.StartedAt || raw.EndedAt != safe.EndedAt || raw.LastSeenAt != safe.LastSeenAt
            || !raw.SourceSurfaces.SequenceEqual(safe.SourceSurfaces)
            || !raw.SourceProvenance.SequenceEqual(safe.SourceProvenance)
            || raw.Completeness != safe.Completeness
            || !raw.CompletenessReasons.SequenceEqual(safe.CompletenessReasons)
            || raw.SourceKind != safe.SourceKind || raw.ContentState != safe.ContentState
            || raw.Capabilities != safe.Capabilities
            || raw.ModelObservations.Count != safe.ModelObservations.Count
            || raw.DurationObservations.Count != safe.DurationObservations.Count)
            return false;
        var expectedModels = raw.ModelObservations.Select(value => new HistoricalModelObservationV1(
                value.Model, TokenizeReference(value.EvidenceRef, safeSessionId)))
            .OrderBy(value => value.Model, StringComparer.Ordinal).ThenBy(value => value.EvidenceRef.SessionId, StringComparer.Ordinal)
            .ThenBy(value => value.EvidenceRef.TraceId, StringComparer.Ordinal).ThenBy(value => value.EvidenceRef.SpanId, StringComparer.Ordinal)
            .ThenBy(value => value.EvidenceRef.TurnIndex).ThenBy(value => value.EvidenceRef.RelativePosition).ToArray();
        var expectedDurations = raw.DurationObservations.Select(value => new HistoricalDurationObservationV1(
                value.DurationMs, TokenizeReference(value.EvidenceRef, safeSessionId)))
            .OrderBy(value => value.DurationMs).ThenBy(value => value.EvidenceRef.SessionId, StringComparer.Ordinal)
            .ThenBy(value => value.EvidenceRef.TraceId, StringComparer.Ordinal).ThenBy(value => value.EvidenceRef.SpanId, StringComparer.Ordinal)
            .ThenBy(value => value.EvidenceRef.TurnIndex).ThenBy(value => value.EvidenceRef.RelativePosition).ToArray();
        return expectedModels.SequenceEqual(safe.ModelObservations) && expectedDurations.SequenceEqual(safe.DurationObservations);
    }

    private static HistoricalEvidenceReferenceV1 TokenizeReference(HistoricalEvidenceReferenceV1 raw, string safeSessionId)
    {
        var expected = InstructionFindingReferenceTokenizationV1.Tokenize(new(
            raw.SessionId, raw.TraceId, raw.SpanId, raw.TurnIndex,
            (InstructionEvidenceRelativePositionV1)(int)raw.RelativePosition));
        return new(safeSessionId, expected.TraceId, expected.SpanId, raw.TurnIndex, raw.RelativePosition);
    }

    private static bool SelectionPairMatches(HistoricalEvidenceSelectionProjectionV1 raw, HistoricalEvidenceSelectionProjectionV1 safe) =>
        safe.Repository == HistoricalEvidenceExtractorV1.TokenizeLabel("repository", raw.Repository)
        && safe.Workspace == HistoricalEvidenceExtractorV1.TokenizeLabel("workspace", raw.Workspace)
        && safe.TaskLabel == HistoricalEvidenceExtractorV1.TokenizeLabel("task", raw.TaskLabel)
        && safe.ExperimentLabel == HistoricalEvidenceExtractorV1.TokenizeLabel("experiment", raw.ExperimentLabel)
        && raw.From == safe.From && raw.To == safe.To
        && raw.SourceSurfaces.SequenceEqual(safe.SourceSurfaces)
        && raw.MaximumSessionCount == safe.MaximumSessionCount && raw.SanitizedOnly == safe.SanitizedOnly
        && raw.ExplicitSessionIds.Count == safe.ExplicitSessionIds.Count
        && raw.ExplicitSessionIds.Select(Guid.Parse).Select(SafeSession).SequenceEqual(safe.ExplicitSessionIds);

    private static bool DistributionMatches(HistoricalEvidenceDistributionV1 raw, HistoricalEvidenceDistributionV1 safe) =>
        raw.Completeness.Select(item => (item.Key, item.Count)).SequenceEqual(safe.Completeness.Select(item => (item.Key, item.Count)))
        && raw.SourceKinds.Select(item => (item.Key, item.Count)).SequenceEqual(safe.SourceKinds.Select(item => (item.Key, item.Count)))
        && raw.Capabilities.Select(item => (item.Key, item.Count)).SequenceEqual(safe.Capabilities.Select(item => (item.Key, item.Count)));

    private static StoredDataset ReadDataset(StoredRow row, HistoricalEvidenceRepresentationV1 expected)
    {
        var bytes = Encoding.UTF8.GetBytes(row.Payload);
        if (HistoricalEvidenceExtractorV1.Sha256(bytes) != row.Checksum)
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
        HistoricalEvidenceDatasetV1 dataset;
        try { dataset = HistoricalEvidenceJsonV1.Deserialize(bytes); }
        catch (HistoricalEvidenceValidationException) { throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence); }
        if (dataset.Representation != expected || dataset.SchemaVersion != row.SchemaVersion || dataset.SnapshotId != row.SnapshotId || dataset.ExtractionId != row.ExtractionId)
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
        return new(dataset, bytes, row.Checksum);
    }

    private static List<StoredRow> ReadRows(SqliteConnection connection, string extractionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT extraction_id,representation,schema_version,snapshot_id,payload_json,payload_sha256 FROM historical_evidence_datasets WHERE extraction_id=$id ORDER BY representation;";
        command.Parameters.AddWithValue("$id", extractionId);
        using var reader = command.ExecuteReader();
        var result = new List<StoredRow>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }

    private static void EnsurePayloadSizes(SqliteConnection connection, string extractionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(CAST(payload_json AS BLOB)) FROM historical_evidence_datasets WHERE extraction_id=$id;";
        command.Parameters.AddWithValue("$id", extractionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (reader.IsDBNull(0) || reader.GetInt64(0) > HistoricalEvidenceContractsV1.MaximumPayloadBytes)
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidPersistence);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private sealed record StoredRow(string ExtractionId, string Representation, string SchemaVersion, string SnapshotId, string Payload, string Checksum);
    private sealed record StoredDataset(HistoricalEvidenceDatasetV1 Dataset, byte[] Bytes, string Checksum);
}
