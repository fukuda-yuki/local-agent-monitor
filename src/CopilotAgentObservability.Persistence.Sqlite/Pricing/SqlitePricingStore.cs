using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

public enum PricingStoreStatus
{
    Success,
    Conflict,
    Busy,
    Unavailable,
    ContractRejected,
    CapacityReached,
}

public sealed record PricingStoreResult(PricingStoreStatus Status);

public sealed record PricingStoreResult<T>(PricingStoreStatus Status, T? Value);

public sealed record PersistedPricingCatalogSnapshot(
    string CatalogSha256,
    byte[] CanonicalBytes,
    int DocumentCount,
    DateTimeOffset FirstRecordedAtUtc);

internal sealed record PricingProviderCatalogWrite(
    string CatalogSha256,
    ReadOnlyMemory<byte> CanonicalBytes);

internal sealed record PricingConfigurationSelectionFactWrite(
    string SessionId,
    string SessionStatus,
    DateTimeOffset SessionLastSeenAtUtc,
    DateTimeOffset SessionUpdatedAtUtc,
    string SourcePartitionState,
    int SourcePartitionCount,
    string SourcePartitionDigest,
    string SourceSurface,
    string SourceApplicationVersion,
    long? ActiveHeadRevision,
    string? ActiveEstimateId,
    long AttemptRevision);

internal static class PricingConfigurationSelectionDigestV1
{
    internal static string Create(IReadOnlyList<PricingConfigurationSelectionFactWrite> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Count > 2_000
            || selection.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != selection.Count
            || !selection.SequenceEqual(
                selection
                    .OrderBy(item => item.SessionLastSeenAtUtc)
                    .ThenBy(item => item.SessionId, StringComparer.Ordinal)))
            throw new ArgumentException("Pricing configuration selection is invalid.", nameof(selection));

        using var stream = new MemoryStream();
        Frame(stream, "cost-configuration-selection/v1");
        Frame(stream, selection.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var item in selection)
        {
            if (!Guid.TryParseExact(item.SessionId, "D", out var sessionId)
                || sessionId.ToString("D") != item.SessionId
                || item.SessionStatus is not ("completed" or "failed")
                || item.SessionLastSeenAtUtc.Offset != TimeSpan.Zero
                || item.SessionUpdatedAtUtc.Offset != TimeSpan.Zero
                || item.SessionUpdatedAtUtc < item.SessionLastSeenAtUtc
                || item.SourcePartitionState != "resolved"
                || item.SourcePartitionCount is < 1 or > 256
                || !LowerSha(item.SourcePartitionDigest)
                || !LowerToken(item.SourceSurface, 128)
                || !Printable(item.SourceApplicationVersion, 64)
                || (item.ActiveHeadRevision is null) != (item.ActiveEstimateId is null)
                || item.ActiveHeadRevision is < 1
                || item.ActiveEstimateId is not null
                    && !PrefixedSha(item.ActiveEstimateId, "pricing-estimate-")
                || item.AttemptRevision < 0)
                throw new ArgumentException("Pricing configuration selection is invalid.", nameof(selection));

            Frame(stream, item.SessionId);
            Frame(stream, item.SessionStatus);
            Frame(stream, Format(item.SessionLastSeenAtUtc));
            Frame(stream, Format(item.SessionUpdatedAtUtc));
            Frame(stream, item.SourcePartitionState);
            Frame(stream, item.SourcePartitionCount.ToString(CultureInfo.InvariantCulture));
            Frame(stream, item.SourcePartitionDigest);
            Frame(stream, item.SourceSurface);
            Frame(stream, item.SourceApplicationVersion);
            FrameNullable(stream, item.ActiveHeadRevision?.ToString(CultureInfo.InvariantCulture));
            FrameNullable(stream, item.ActiveEstimateId);
            Frame(stream, item.AttemptRevision.ToString(CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void FrameNullable(Stream stream, string? value)
    {
        Frame(stream, value is null ? "0" : "1");
        if (value is not null) Frame(stream, value);
    }

    private static void Frame(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private static bool LowerSha(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PrefixedSha(string? value, string prefix) =>
        value is not null
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && LowerSha(value[prefix.Length..]);

    private static bool LowerToken(string? value, int maximumLength) =>
        value is not null
        && value.Length is >= 1
        && value.Length <= maximumLength
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '_'
            or '-');

    private static bool Printable(string? value, int maximumLength) =>
        value is not null
        && value.Length is >= 1
        && value.Length <= maximumLength
        && value.All(character => character is >= '!' and <= '~');
}

internal sealed record PricingRecalculationTargetCapture(
    string SessionId,
    string SessionStatus,
    DateTimeOffset SessionEffectiveAtUtc,
    DateTimeOffset SessionUpdatedAtUtc,
    string SourcePartitionState,
    int SourcePartitionCount,
    string SourcePartitionDigest,
    string? SourceSurface,
    string? SourceApplicationVersion,
    long? BaseHeadRevision,
    string? BaseEstimateId,
    long BaseAttemptRevision);

internal sealed record PricingTargetCompletionWrite(
    int TargetOrdinal,
    string ResultKind,
    string? ResultCode,
    int? SourceEntryOrdinal,
    PricingEstimateRequest? ExpectedRequest,
    ReadOnlyMemory<byte> CanonicalEstimateBytes)
{
    public static PricingTargetCompletionWrite Estimate(
        int targetOrdinal,
        int sourceEntryOrdinal,
        PricingEstimateRequest expectedRequest,
        ReadOnlyMemory<byte> canonicalEstimateBytes) =>
        new(targetOrdinal, "estimate", null, sourceEntryOrdinal, expectedRequest, canonicalEstimateBytes);

    public static PricingTargetCompletionWrite Unavailable(int targetOrdinal, string resultCode) =>
        new(targetOrdinal, "unavailable", resultCode, null, null, ReadOnlyMemory<byte>.Empty);

    public static PricingTargetCompletionWrite Failed(int targetOrdinal, string resultCode) =>
        new(targetOrdinal, "failed", resultCode, null, null, ReadOnlyMemory<byte>.Empty);
}

internal sealed record PricingBudgetResultWrite(
    int ScopeOrdinal,
    string ScopeKind,
    string ScopeId,
    string EligibilityDigest,
    IReadOnlyList<string> EligibleSessionIds,
    DateTimeOffset? ScopeStartUtc,
    DateTimeOffset? ScopeEndUtc,
    string RuleId,
    string RuleVersion,
    string EvaluationId,
    string OutcomeKind,
    string? AlertId,
    int? SuppressionOrdinal,
    string? SuppressionCode);

internal static class PricingAlertCostScopeIdentityV2
{
    internal static string Create(
        string scopeKind,
        DateTimeOffset? windowStartUtc,
        DateTimeOffset? windowEndUtc,
        string eligibilityDigest,
        IEnumerable<string> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(scopeKind);
        ArgumentNullException.ThrowIfNull(eligibilityDigest);
        ArgumentNullException.ThrowIfNull(sessionIds);
        if (scopeKind is not ("session" or "utc_day" or "rolling_period"))
            throw new ArgumentOutOfRangeException(nameof(scopeKind));

        var values = new List<byte[]>
        {
            Encoding.UTF8.GetBytes("alert-cost-scope/v2"),
            Encoding.UTF8.GetBytes(scopeKind),
            Encoding.UTF8.GetBytes(NullableAlertTimestamp(windowStartUtc)),
            Encoding.UTF8.GetBytes(NullableAlertTimestamp(windowEndUtc)),
            Encoding.UTF8.GetBytes(eligibilityDigest),
        };
        values.AddRange(sessionIds.Select(Encoding.UTF8.GetBytes));
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            stream.Write(length);
            stream.Write(value);
        }

        return "cost-scope-" + Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string NullableAlertTimestamp(DateTimeOffset? value) =>
        value is null
            ? "\0"
            : value.Value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture);
}

internal sealed record PricingRunFailureWrite(
    string FailurePhase,
    string? FailureOrdinalKind,
    int? FailureOrdinal,
    string FailureCode);

public sealed partial class SqlitePricingStore
{
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;

    public SqlitePricingStore(string databasePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void CreateSchema()
    {
        using var connection = Open(SqliteOpenMode.ReadWriteCreate);
        using var transaction = connection.BeginTransaction(deferred: false);
        PricingSchemaV1.Ensure(connection, transaction);
        transaction.Commit();
    }

    public PricingStoreResult PutCatalogSnapshot(ReadOnlyMemory<byte> canonicalBytes)
    {
        var frozenBytes = canonicalBytes.ToArray();
        PricingCatalog catalog;
        try
        {
            catalog = PricingCatalogSnapshotConsumer.Deserialize(frozenBytes);
        }
        catch (PricingRegistryValidationException)
        {
            return new(PricingStoreStatus.ContractRejected);
        }
        try
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback(transaction, PricingStoreStatus.Unavailable);

            using (var read = Command(connection, transaction, "SELECT canonical_blob,document_count FROM pricing_catalog_snapshots WHERE catalog_sha256=$sha;", ("$sha", catalog.CatalogSha256)))
            using (var reader = read.ExecuteReader())
            {
                if (reader.Read())
                {
                    var same = ((byte[])reader[0]).AsSpan().SequenceEqual(frozenBytes)
                        && reader.GetInt32(1) == catalog.Documents.Count;
                    transaction.Rollback();
                    return new(same ? PricingStoreStatus.Success : PricingStoreStatus.Conflict);
                }
            }

            var firstRecordedAtUtc = timeProvider.GetUtcNow();
            using var insert = Command(
                connection,
                transaction,
                """
                INSERT INTO pricing_catalog_snapshots(
                    catalog_sha256,schema_version,canonical_blob,document_count,first_recorded_at_utc)
                VALUES($sha,'pricing.catalog-snapshot.v1',$blob,$count,$time);
                """,
                ("$sha", catalog.CatalogSha256),
                ("$blob", frozenBytes),
                ("$count", catalog.Documents.Count),
                ("$time", Format(firstRecordedAtUtc)));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(PricingStoreStatus.Success);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingStoreStatus.Busy);
        }
        catch (SqliteException)
        {
            return new(PricingStoreStatus.Unavailable);
        }
    }

    public PersistedPricingCatalogSnapshot? GetCatalogSnapshot(string catalogSha256)
    {
        if (catalogSha256.Length != 64
            || catalogSha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return null;
        try
        {
            using var connection = Open(SqliteOpenMode.ReadOnly);
            if (!PricingSchemaV1.ValidateRows(connection, null)) return null;
            using var command = Command(
                connection,
                null,
                "SELECT canonical_blob,document_count,first_recorded_at_utc FROM pricing_catalog_snapshots WHERE catalog_sha256=$sha;",
                ("$sha", catalogSha256));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new(
                catalogSha256,
                ((byte[])reader[0]).ToArray(),
                reader.GetInt32(1),
                DateTimeOffset.ParseExact(
                    reader.GetString(2),
                    "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public PricingStoreResult PutConfigurationPreview(
        CostConfigurationPreviewV1 preview)
    {
        byte[] canonical;
        CostConfigurationPreviewV1 strict;
        try
        {
            canonical = CostConfigurationPreviewCanonicalJsonV1.Serialize(preview);
            var consumed = CostConfigurationPreviewConsumerV1.Consume(canonical);
            if (consumed.Status != CostConsumerStatus.Success || consumed.Value is null)
                return new(PricingStoreStatus.ContractRejected);
            strict = consumed.Value;
        }
        catch (ArgumentException)
        {
            return new(PricingStoreStatus.ContractRejected);
        }

        try
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback(transaction, PricingStoreStatus.Unavailable);
            var nowUtc = timeProvider.GetUtcNow();
            if (strict.Configuration.CreatedAtUtc.AddMinutes(15) <= nowUtc)
                return Rollback(transaction, PricingStoreStatus.ContractRejected);
            using (var cleanup = Command(
                connection,
                transaction,
                "DELETE FROM pricing_configuration_previews WHERE expires_at_utc<=$now;",
                ("$now", Format(nowUtc))))
                cleanup.ExecuteNonQuery();

            using (var read = Command(
                connection,
                transaction,
                "SELECT canonical_blob FROM pricing_configuration_previews WHERE preview_digest=$digest;",
                ("$digest", strict.PreviewDigest)))
            {
                var existing = read.ExecuteScalar();
                if (existing is byte[] bytes)
                {
                    transaction.Rollback();
                    return new(bytes.AsSpan().SequenceEqual(canonical)
                        ? PricingStoreStatus.Success
                        : PricingStoreStatus.Conflict);
                }
            }
            using (var count = Command(connection, transaction, "SELECT COUNT(*) FROM pricing_configuration_previews;"))
            {
                if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= 32)
                    return Rollback(transaction, PricingStoreStatus.CapacityReached);
            }
            using var insert = Command(
                connection,
                transaction,
                """
                INSERT INTO pricing_configuration_previews(
                    preview_digest,canonical_sha256,canonical_blob,configuration_id,
                    expected_head_revision,expected_configuration_id,catalog_sha256,
                    selection_digest,created_at_utc,expires_at_utc)
                VALUES($digest,$canonical_sha,$blob,$configuration_id,$head,$expected_configuration_id,
                    $catalog_sha,$selection_digest,$created_at,$expires_at);
                """,
                ("$digest", strict.PreviewDigest),
                ("$canonical_sha", Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()),
                ("$blob", canonical),
                ("$configuration_id", strict.Configuration.ConfigurationId),
                ("$head", strict.ExpectedHeadRevision),
                ("$expected_configuration_id", (object?)strict.ExpectedConfigurationId ?? DBNull.Value),
                ("$catalog_sha", strict.CatalogSha256),
                ("$selection_digest", strict.SelectionDigest),
                ("$created_at", Format(strict.Configuration.CreatedAtUtc)),
                ("$expires_at", Format(strict.Configuration.CreatedAtUtc.AddMinutes(15))));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(PricingStoreStatus.Success);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingStoreStatus.Busy);
        }
        catch (SqliteException)
        {
            return new(PricingStoreStatus.Unavailable);
        }
    }

    public int CountConfigurationPreviews()
    {
        try
        {
            using var connection = Open(SqliteOpenMode.ReadOnly);
            if (!PricingSchemaV1.ValidateRows(connection, null)) return -1;
            using var command = Command(connection, null, "SELECT COUNT(*) FROM pricing_configuration_previews;");
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return -1;
        }
    }

    internal PricingStoreResult<CostConfigurationCommitResultV1> AppendConfigurationCommitApplication(
        CostConfigurationPreviewV1 preview,
        PricingProviderCatalogWrite providerCatalog,
        IReadOnlyList<PricingConfigurationSelectionFactWrite> recomputedSelection)
    {
        ArgumentNullException.ThrowIfNull(providerCatalog);
        ArgumentNullException.ThrowIfNull(recomputedSelection);
        byte[] requestBytes;
        byte[] providerCatalogBytes;
        PricingConfigurationSelectionFactWrite[] selection;
        CostConfigurationPreviewV1 strict;
        try
        {
            requestBytes = CostConfigurationCommitConsumerV1.SerializeRequest(preview);
            var consumed = CostConfigurationCommitConsumerV1.ConsumeRequest(requestBytes);
            if (consumed.Status != CostConsumerStatus.Success || consumed.Value is null)
                return new(PricingStoreStatus.ContractRejected, null);
            strict = consumed.Value;
            providerCatalogBytes = providerCatalog.CanonicalBytes.ToArray();
            selection = recomputedSelection.Select(FreezeSelectionFact).ToArray();
        }
        catch (ArgumentException)
        {
            return new(PricingStoreStatus.ContractRejected, null);
        }

        try
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback<CostConfigurationCommitResultV1>(transaction, PricingStoreStatus.Unavailable);
            var successor = checked(strict.ExpectedHeadRevision + 1);
            using (var replay = Command(
                connection,
                transaction,
                "SELECT canonical_request_blob,canonical_result_blob FROM pricing_configuration_commits WHERE head_revision=$revision;",
                ("$revision", successor)))
            using (var reader = replay.ExecuteReader())
            {
                if (reader.Read())
                {
                    var same = ((byte[])reader[0]).AsSpan().SequenceEqual(requestBytes);
                    var replayResultBytes = (byte[])reader[1];
                    transaction.Rollback();
                    var consumedResult = CostConfigurationCommitConsumerV1.ConsumeResult(replayResultBytes);
                    return same && consumedResult.Status == CostConsumerStatus.Success
                        ? new(PricingStoreStatus.Success, consumedResult.Value)
                        : new(PricingStoreStatus.Conflict, null);
                }
            }

            var committedAtUtc = timeProvider.GetUtcNow();
            using (var cleanup = Command(
                connection,
                transaction,
                "DELETE FROM pricing_configuration_previews WHERE expires_at_utc<=$now;",
                ("$now", Format(committedAtUtc))))
                cleanup.ExecuteNonQuery();
            using (var receipt = Command(
                connection,
                transaction,
                "SELECT canonical_blob,expires_at_utc FROM pricing_configuration_previews WHERE preview_digest=$digest;",
                ("$digest", strict.PreviewDigest)))
            using (var reader = receipt.ExecuteReader())
            {
                if (!reader.Read()
                    || !((byte[])reader[0]).AsSpan().SequenceEqual(
                        CostConfigurationPreviewCanonicalJsonV1.Serialize(strict))
                    || string.CompareOrdinal(reader.GetString(1), Format(committedAtUtc)) <= 0)
                    return Rollback<CostConfigurationCommitResultV1>(transaction, PricingStoreStatus.Conflict);
            }

            PricingCatalog currentCatalog;
            try
            {
                currentCatalog = PricingCatalogSnapshotConsumer.Deserialize(providerCatalogBytes);
            }
            catch (PricingRegistryValidationException)
            {
                return Rollback<CostConfigurationCommitResultV1>(transaction, PricingStoreStatus.Unavailable);
            }
            if (providerCatalog.CatalogSha256 != currentCatalog.CatalogSha256
                || strict.CatalogSha256 != currentCatalog.CatalogSha256)
                return Rollback<CostConfigurationCommitResultV1>(transaction, PricingStoreStatus.Conflict);

            using (var head = Command(connection, transaction, "SELECT head_revision,configuration_id FROM pricing_configuration_heads ORDER BY head_revision DESC LIMIT 1;"))
            using (var reader = head.ExecuteReader())
            {
                var hasHead = reader.Read();
                if ((hasHead ? reader.GetInt64(0) : 0) != strict.ExpectedHeadRevision
                    || (hasHead ? reader.GetString(1) : null) != strict.ExpectedConfigurationId
                    || strict.Configuration.PredecessorConfigurationId != strict.ExpectedConfigurationId)
                    return Rollback<CostConfigurationCommitResultV1>(transaction, PricingStoreStatus.Conflict);
            }

            string selectionDigest;
            try
            {
                selectionDigest = PricingConfigurationSelectionDigestV1.Create(selection);
            }
            catch (ArgumentException)
            {
                return Rollback<CostConfigurationCommitResultV1>(transaction, PricingStoreStatus.ContractRejected);
            }
            if (selection.Length != strict.ProposedMatchCount
                || selectionDigest != strict.SelectionDigest)
                return Rollback<CostConfigurationCommitResultV1>(transaction, PricingStoreStatus.Conflict);

            var catalogInsertStatus = InsertCatalogSnapshot(
                connection,
                transaction,
                currentCatalog,
                providerCatalogBytes,
                committedAtUtc);
            if (catalogInsertStatus != PricingStoreStatus.Success)
                return Rollback<CostConfigurationCommitResultV1>(transaction, catalogInsertStatus);

            var configurationBytes = CostConfigurationCanonicalJsonV1.Serialize(strict.Configuration);
            using (var configuration = Command(
                connection,
                transaction,
                """
                INSERT INTO pricing_configurations(
                    configuration_id,predecessor_configuration_id,schema_version,catalog_sha256,
                    canonical_sha256,canonical_blob,created_at_utc,source_count,budget_count)
                VALUES($id,$predecessor,'cost.configuration.v1',$catalog,$sha,$blob,$created,$sources,$budgets);
                """,
                ("$id", strict.Configuration.ConfigurationId),
                ("$predecessor", (object?)strict.Configuration.PredecessorConfigurationId ?? DBNull.Value),
                ("$catalog", strict.CatalogSha256),
                ("$sha", Convert.ToHexString(SHA256.HashData(configurationBytes)).ToLowerInvariant()),
                ("$blob", configurationBytes),
                ("$created", Format(strict.Configuration.CreatedAtUtc)),
                ("$sources", strict.Configuration.SourceEntries.Count),
                ("$budgets", strict.Configuration.BudgetEntries.Count)))
                configuration.ExecuteNonQuery();

            using (var head = Command(
                connection,
                transaction,
                """
                INSERT INTO pricing_configuration_heads(
                    head_revision,configuration_id,previous_head_revision,previous_configuration_id,committed_at_utc)
                VALUES($revision,$configuration,$previous_revision,$previous_configuration,$committed);
                """,
                ("$revision", successor),
                ("$configuration", strict.Configuration.ConfigurationId),
                ("$previous_revision", strict.ExpectedHeadRevision == 0 ? DBNull.Value : strict.ExpectedHeadRevision),
                ("$previous_configuration", (object?)strict.ExpectedConfigurationId ?? DBNull.Value),
                ("$committed", Format(committedAtUtc))))
                head.ExecuteNonQuery();

            var result = CostConfigurationCommitConsumerV1.CreateResult(
                strict.Configuration.ConfigurationId,
                successor,
                strict.CatalogSha256);
            var resultBytes = CostConfigurationCommitConsumerV1.SerializeResult(result);
            using (var commit = Command(
                connection,
                transaction,
                """
                INSERT INTO pricing_configuration_commits(
                    head_revision,configuration_id,preview_digest,request_sha256,
                    canonical_request_blob,canonical_result_blob)
                VALUES($revision,$configuration,$preview,$request_sha,$request,$result);
                """,
                ("$revision", successor),
                ("$configuration", strict.Configuration.ConfigurationId),
                ("$preview", strict.PreviewDigest),
                ("$request_sha", Convert.ToHexString(SHA256.HashData(requestBytes)).ToLowerInvariant()),
                ("$request", requestBytes),
                ("$result", resultBytes)))
                commit.ExecuteNonQuery();
            using (var consume = Command(
                connection,
                transaction,
                "DELETE FROM pricing_configuration_previews WHERE preview_digest=$digest;",
                ("$digest", strict.PreviewDigest)))
                consume.ExecuteNonQuery();
            transaction.Commit();
            return new(PricingStoreStatus.Success, result);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingStoreStatus.Busy, null);
        }
        catch (SqliteException)
        {
            return new(PricingStoreStatus.Unavailable, null);
        }
    }

    internal PricingStoreResult<string> StartRecalculationApplication(
        string runId,
        CostRecalculationRequestV1 request,
        IReadOnlyList<PricingRecalculationTargetCapture> targets,
        DateTimeOffset calculationTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!IsCanonicalUuidV7(runId)
            || calculationTimeUtc.Offset != TimeSpan.Zero)
            return new(PricingStoreStatus.ContractRejected, null);
        byte[] canonical;
        CostConsumerResult<CostRecalculationRequestV1> consumed;
        try
        {
            canonical = CostRecalculationRequestCanonicalJsonV1.Serialize(request);
            consumed = CostRecalculationRequestCanonicalJsonV1.Consume(canonical);
        }
        catch (ArgumentException)
        {
            return new(PricingStoreStatus.ContractRejected, null);
        }
        if (consumed.Status != CostConsumerStatus.Success
            || targets.Count != request.SessionIds.Count
            || !targets.Select(target => target.SessionId).SequenceEqual(request.SessionIds, StringComparer.Ordinal)
            || targets.Any(target => !IsRecalculationTargetShapeValid(target))
            || request.BudgetScopes
                .Where(scope => scope.ScopeKind == "session")
                .Any(scope => !request.SessionIds.Contains(scope.SessionId!, StringComparer.Ordinal)))
            return new(PricingStoreStatus.ContractRejected, null);
        try
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback<string>(transaction, PricingStoreStatus.Unavailable);
            var digest = CostIdentityV1.Hash("cost-recalculation-request/v1", canonical);
            using (var replay = Command(connection, transaction, "SELECT run_id,request_digest,canonical_request_blob FROM pricing_recalculation_runs WHERE idempotency_key=$key;", ("$key", request.IdempotencyKey)))
            using (var reader = replay.ExecuteReader())
            {
                if (reader.Read())
                {
                    var storedRunId = reader.GetString(0);
                    var same = reader.GetString(1) == digest
                        && ((byte[])reader[2]).AsSpan().SequenceEqual(canonical);
                    transaction.Rollback();
                    return same
                        ? new(PricingStoreStatus.Success, storedRunId)
                        : new(PricingStoreStatus.Conflict, null);
                }
            }
            using (var head = Command(
                connection,
                transaction,
                "SELECT head_revision,configuration_id FROM pricing_configuration_heads ORDER BY head_revision DESC LIMIT 1;"))
            using (var reader = head.ExecuteReader())
                if (!reader.Read()
                    || reader.GetInt64(0) != request.ExpectedHeadRevision
                    || reader.GetString(1) != request.ConfigurationId)
                    return Rollback<string>(transaction, PricingStoreStatus.Conflict);
            using (var overlap = Command(
                connection,
                transaction,
                """
                SELECT COUNT(*) FROM pricing_recalculation_targets t
                JOIN pricing_recalculation_events e ON e.run_id=t.run_id
                WHERE t.session_id IN (SELECT value FROM json_each($sessions))
                  AND NOT EXISTS(SELECT 1 FROM pricing_recalculation_events terminal
                    WHERE terminal.run_id=t.run_id AND terminal.event_kind IN ('succeeded','failed'));
                """,
                ("$sessions", JsonSerializer.Serialize(request.SessionIds))))
                if (Convert.ToInt64(overlap.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    return Rollback<string>(transaction, PricingStoreStatus.Conflict);

            using (var root = Command(
                connection,
                transaction,
                """
                INSERT INTO pricing_recalculation_runs(
                    run_id,request_schema_version,idempotency_key,request_digest,canonical_request_blob,
                    configuration_id,configuration_head_revision,catalog_sha256,calculation_time_utc,
                    target_count,scope_count,created_at_utc)
                VALUES($run,'cost.recalculation-request.v1',$key,$digest,$blob,$configuration,$head,
                    $catalog,$time,$targets,$scopes,$time);
                """,
                ("$run", runId), ("$key", request.IdempotencyKey), ("$digest", digest),
                ("$blob", canonical), ("$configuration", request.ConfigurationId),
                ("$head", request.ExpectedHeadRevision), ("$catalog", request.CatalogSha256),
                ("$time", Format(calculationTimeUtc)), ("$targets", targets.Count),
                ("$scopes", request.BudgetScopes.Count)))
                root.ExecuteNonQuery();
            for (var ordinal = 0; ordinal < targets.Count; ordinal++)
            {
                var target = targets[ordinal];
                using var insert = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO pricing_recalculation_targets(
                        run_id,target_ordinal,session_id,session_status,session_effective_at_utc,
                        session_updated_at_utc,source_partition_state,source_partition_count,
                        source_partition_digest,source_surface,source_application_version,
                        base_head_revision,base_estimate_id,base_attempt_revision)
                    VALUES($run,$ordinal,$session,$status,$effective,$updated,$partition_state,
                        $partition_count,$partition_digest,$surface,$version,$base_head,$base_estimate,$base_attempt);
                    """,
                    ("$run", runId), ("$ordinal", ordinal), ("$session", target.SessionId),
                    ("$status", target.SessionStatus), ("$effective", Format(target.SessionEffectiveAtUtc)),
                    ("$updated", Format(target.SessionUpdatedAtUtc)), ("$partition_state", target.SourcePartitionState),
                    ("$partition_count", target.SourcePartitionCount), ("$partition_digest", target.SourcePartitionDigest),
                    ("$surface", (object?)target.SourceSurface ?? DBNull.Value),
                    ("$version", (object?)target.SourceApplicationVersion ?? DBNull.Value),
                    ("$base_head", (object?)target.BaseHeadRevision ?? DBNull.Value),
                    ("$base_estimate", (object?)target.BaseEstimateId ?? DBNull.Value),
                    ("$base_attempt", target.BaseAttemptRevision));
                insert.ExecuteNonQuery();
            }
            using (var requested = Command(
                connection,
                transaction,
                "INSERT INTO pricing_recalculation_events(run_id,event_sequence,event_kind,occurred_at_utc) VALUES($run,0,'requested',$time);",
                ("$run", runId), ("$time", Format(calculationTimeUtc))))
                requested.ExecuteNonQuery();
            transaction.Commit();
            return new(PricingStoreStatus.Success, runId);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingStoreStatus.Busy, null);
        }
        catch (SqliteException)
        {
            return new(PricingStoreStatus.Unavailable, null);
        }
    }

    public PricingStoreResult RecoverInterruptedRuns()
    {
        try
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback(transaction, PricingStoreStatus.Unavailable);
            var runs = new List<(string RunId, int NextSequence)>();
            using (var select = Command(
                connection,
                transaction,
                """
                SELECT r.run_id,MAX(e.event_sequence)+1
                FROM pricing_recalculation_runs r
                JOIN pricing_recalculation_events e ON e.run_id=r.run_id
                GROUP BY r.run_id
                HAVING MAX(CASE WHEN e.event_kind IN ('succeeded','failed') THEN 1 ELSE 0 END)=0
                ORDER BY r.calculation_time_utc,r.run_id;
                """))
            using (var reader = select.ExecuteReader())
                while (reader.Read()) runs.Add((reader.GetString(0), reader.GetInt32(1)));
            foreach (var run in runs)
            {
                using (var artifacts = Command(
                    connection,
                    transaction,
                    """
                    SELECT
                      (SELECT COUNT(*) FROM pricing_recalculation_target_results WHERE run_id=$run)+
                      (SELECT COUNT(*) FROM pricing_session_attempts WHERE run_id=$run)+
                      (SELECT COUNT(*) FROM pricing_estimates WHERE run_id=$run)+
                      (SELECT COUNT(*) FROM pricing_recalculation_budget_results WHERE run_id=$run);
                    """,
                    ("$run", run.RunId)))
                    if (Convert.ToInt64(artifacts.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                        return Rollback(transaction, PricingStoreStatus.Unavailable);
                var occurredAtUtc = timeProvider.GetUtcNow();
                using (var failed = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO pricing_recalculation_events(
                        run_id,event_sequence,event_kind,occurred_at_utc,failure_phase,
                        failure_ordinal_kind,failure_ordinal,failure_code)
                    VALUES($run,$sequence,'failed',$time,'recovery',NULL,NULL,'recalculation_interrupted');
                    """,
                    ("$run", run.RunId), ("$sequence", run.NextSequence), ("$time", Format(occurredAtUtc))))
                    failed.ExecuteNonQuery();
                var targets = new List<(int Ordinal, string SessionId, long BaseAttempt)>();
                using (var selectTargets = Command(
                    connection,
                    transaction,
                    "SELECT target_ordinal,session_id,base_attempt_revision FROM pricing_recalculation_targets WHERE run_id=$run ORDER BY target_ordinal;",
                    ("$run", run.RunId)))
                using (var reader = selectTargets.ExecuteReader())
                    while (reader.Read()) targets.Add((reader.GetInt32(0), reader.GetString(1), reader.GetInt64(2)));
                foreach (var target in targets)
                {
                    using (var result = Command(
                        connection,
                        transaction,
                        "INSERT INTO pricing_recalculation_target_results(run_id,target_ordinal,result_kind,result_code) VALUES($run,$ordinal,'failed','recalculation_interrupted');",
                        ("$run", run.RunId), ("$ordinal", target.Ordinal)))
                        result.ExecuteNonQuery();
                    using var attempt = Command(
                        connection,
                        transaction,
                        """
                        INSERT INTO pricing_session_attempts(
                            session_id,attempt_revision,run_id,target_ordinal,result_kind,result_code)
                        VALUES($session,$revision,$run,$ordinal,'failed','recalculation_interrupted');
                        """,
                        ("$session", target.SessionId), ("$revision", target.BaseAttempt + 1),
                        ("$run", run.RunId), ("$ordinal", target.Ordinal));
                    attempt.ExecuteNonQuery();
                }
            }
            transaction.Commit();
            return new(PricingStoreStatus.Success);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingStoreStatus.Busy);
        }
        catch (SqliteException)
        {
            return new(PricingStoreStatus.Unavailable);
        }
    }

    public PricingStoreResult MarkRecalculationRunning(string runId)
    {
        try
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback(transaction, PricingStoreStatus.Unavailable);
            using (var state = Command(
                connection,
                transaction,
                "SELECT group_concat(event_kind,',') FROM (SELECT event_kind FROM pricing_recalculation_events WHERE run_id=$run ORDER BY event_sequence);",
                ("$run", runId)))
            {
                var value = state.ExecuteScalar() as string;
                if (value == "requested,running")
                {
                    transaction.Rollback();
                    return new(PricingStoreStatus.Success);
                }
                if (value != "requested") return Rollback(transaction, PricingStoreStatus.Conflict);
            }
            var occurredAtUtc = timeProvider.GetUtcNow();
            using var insert = Command(
                connection,
                transaction,
                "INSERT INTO pricing_recalculation_events(run_id,event_sequence,event_kind,occurred_at_utc) VALUES($run,1,'running',$time);",
                ("$run", runId), ("$time", Format(occurredAtUtc)));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(PricingStoreStatus.Success);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingStoreStatus.Busy);
        }
        catch (SqliteException)
        {
            return new(PricingStoreStatus.Unavailable);
        }
    }

    internal PricingStoreResult AppendEstimateSuccessApplication(
        string runId,
        int targetOrdinal,
        int sourceEntryOrdinal,
        PricingEstimateRequest expectedRequest,
        ReadOnlyMemory<byte> canonicalEstimateBytes) =>
        AppendRecalculationCompletionApplication(
            runId,
            [PricingTargetCompletionWrite.Estimate(
                targetOrdinal,
                sourceEntryOrdinal,
                expectedRequest,
                canonicalEstimateBytes)],
            [],
            failure: null);

    internal PricingStoreResult AppendRecalculationCompletionApplication(
        string runId,
        IReadOnlyList<PricingTargetCompletionWrite> targetResults,
        IReadOnlyList<PricingBudgetResultWrite> budgetResults,
        PricingRunFailureWrite? failure)
    {
        ArgumentNullException.ThrowIfNull(targetResults);
        ArgumentNullException.ThrowIfNull(budgetResults);
        PricingTargetCompletionWrite[] frozenTargetResults;
        PricingBudgetResultWrite[] frozenBudgetResults;
        try
        {
            frozenTargetResults = targetResults.Select(FreezeTargetCompletion).ToArray();
            frozenBudgetResults = budgetResults.Select(FreezeBudgetResult).ToArray();
        }
        catch (ArgumentException)
        {
            return new(PricingStoreStatus.ContractRejected);
        }
        if (!IsCanonicalUuidV7(runId)
            || frozenTargetResults.Length is < 1 or > 100
            || frozenTargetResults.Select(item => item.TargetOrdinal).Distinct().Count() != frozenTargetResults.Length
            || !frozenTargetResults.Select(item => item.TargetOrdinal).SequenceEqual(Enumerable.Range(0, frozenTargetResults.Length))
            || frozenBudgetResults.Select(item => item.ScopeOrdinal).Distinct().Count() != frozenBudgetResults.Length
            || !frozenBudgetResults.Select(item => item.ScopeOrdinal).SequenceEqual(Enumerable.Range(0, frozenBudgetResults.Length))
            || frozenTargetResults.Any(item => !IsTargetCompletionShapeValid(item))
            || frozenBudgetResults.Any(item => !IsBudgetResultShapeValid(item))
            || !IsFailureShapeValid(failure))
            return new(PricingStoreStatus.ContractRejected);
        try
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback(transaction, PricingStoreStatus.Unavailable);

            CostRecalculationRequestV1 request;
            var targetCount = 0;
            var scopeCount = 0;
            using (var root = Command(
                connection,
                transaction,
                """
                SELECT canonical_request_blob,target_count,scope_count
                FROM pricing_recalculation_runs
                WHERE run_id=$run;
                """,
                ("$run", runId)))
            using (var reader = root.ExecuteReader())
            {
                if (!reader.Read()) return Rollback(transaction, PricingStoreStatus.Conflict);
                var consumed = CostRecalculationRequestCanonicalJsonV1.Consume((byte[])reader[0]);
                if (consumed.Status != CostConsumerStatus.Success || consumed.Value is null)
                    return Rollback(transaction, PricingStoreStatus.Unavailable);
                request = consumed.Value;
                targetCount = reader.GetInt32(1);
                scopeCount = reader.GetInt32(2);
            }

            var eventKinds = new List<string>();
            using (var events = Command(
                connection,
                transaction,
                "SELECT event_kind FROM pricing_recalculation_events WHERE run_id=$run ORDER BY event_sequence;",
                ("$run", runId)))
            using (var reader = events.ExecuteReader())
                while (reader.Read()) eventKinds.Add(reader.GetString(0));
            if (!eventKinds.SequenceEqual(["requested", "running"], StringComparer.Ordinal)
                || frozenTargetResults.Length != targetCount
                || frozenBudgetResults.Length != (failure is null ? scopeCount : 0)
                || failure?.FailureOrdinalKind == "target"
                    && failure.FailureOrdinal >= targetCount
                || failure?.FailureOrdinalKind == "scope"
                    && failure.FailureOrdinal >= scopeCount
                || (failure is null && frozenTargetResults.Any(item => item.ResultKind == "failed"))
                || (failure is not null
                    && (frozenTargetResults.All(item => item.ResultKind != "failed")
                        || frozenTargetResults.Any(item => item.ResultKind == "estimate")
                        || frozenTargetResults.Where(item => item.ResultKind == "failed")
                            .Any(item => item.ResultCode != failure.FailureCode))))
                return Rollback(transaction, PricingStoreStatus.ContractRejected);

            var targets = new Dictionary<int, TargetFacts>();
            using (var targetCommand = Command(
                connection,
                transaction,
                """
                SELECT t.target_ordinal,t.session_id,t.session_effective_at_utc,
                       t.base_head_revision,t.base_estimate_id,t.base_attempt_revision,
                       r.configuration_id,r.catalog_sha256,r.calculation_time_utc
                FROM pricing_recalculation_targets t
                JOIN pricing_recalculation_runs r ON r.run_id=t.run_id
                WHERE t.run_id=$run
                ORDER BY t.target_ordinal;
                """,
                ("$run", runId)))
            using (var reader = targetCommand.ExecuteReader())
                while (reader.Read())
                    targets.Add(
                        reader.GetInt32(0),
                        new(
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetInt64(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.GetInt64(5),
                            reader.GetString(6),
                            reader.GetString(7),
                            reader.GetString(8)));

            foreach (var write in frozenTargetResults)
            {
                if (!targets.TryGetValue(write.TargetOrdinal, out var target))
                    return Rollback(transaction, PricingStoreStatus.ContractRejected);
                PricingEstimateRecord? estimate = null;
                if (write.ResultKind == "estimate")
                {
                    estimate = ValidateAndInsertEstimate(
                        connection,
                        transaction,
                        runId,
                        write,
                        target);
                    if (estimate is null)
                        return Rollback(transaction, PricingStoreStatus.ContractRejected);
                }

                using (var result = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO pricing_recalculation_target_results(
                        run_id,target_ordinal,result_kind,estimate_status,estimate_id,result_code)
                    VALUES($run,$ordinal,$kind,$status,$estimate,$code);
                    """,
                    ("$run", runId),
                    ("$ordinal", write.TargetOrdinal),
                    ("$kind", write.ResultKind),
                    ("$status", (object?)estimate?.Status ?? DBNull.Value),
                    ("$estimate", (object?)estimate?.EstimateId ?? DBNull.Value),
                    ("$code", (object?)write.ResultCode ?? DBNull.Value)))
                    result.ExecuteNonQuery();
                using (var attempt = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO pricing_session_attempts(
                        session_id,attempt_revision,run_id,target_ordinal,result_kind,
                        estimate_status,estimate_id,result_code)
                    VALUES($session,$revision,$run,$ordinal,$kind,$status,$estimate,$code);
                    """,
                    ("$session", target.SessionId),
                    ("$revision", target.BaseAttemptRevision + 1),
                    ("$run", runId),
                    ("$ordinal", write.TargetOrdinal),
                    ("$kind", write.ResultKind),
                    ("$status", (object?)estimate?.Status ?? DBNull.Value),
                    ("$estimate", (object?)estimate?.EstimateId ?? DBNull.Value),
                    ("$code", (object?)write.ResultCode ?? DBNull.Value)))
                    attempt.ExecuteNonQuery();
                if (estimate is not null)
                {
                    using var head = Command(
                        connection,
                        transaction,
                        """
                        INSERT INTO pricing_estimate_heads(
                            session_id,head_revision,estimate_id,previous_head_revision,previous_estimate_id)
                        VALUES($session,$revision,$estimate,$previous_revision,$previous_estimate);
                        """,
                        ("$session", target.SessionId),
                        ("$revision", (target.BaseHeadRevision ?? 0) + 1),
                        ("$estimate", estimate.EstimateId),
                        ("$previous_revision", (object?)target.BaseHeadRevision ?? DBNull.Value),
                        ("$previous_estimate", (object?)target.BaseEstimateId ?? DBNull.Value));
                    head.ExecuteNonQuery();
                }
            }

            foreach (var write in frozenBudgetResults)
            {
                if (!BudgetMatchesRequest(write, request.BudgetScopes[write.ScopeOrdinal])
                    || !BudgetParentsAreValid(connection, transaction, write))
                    return Rollback(transaction, PricingStoreStatus.ContractRejected);
                using var insert = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO pricing_recalculation_budget_results(
                        run_id,scope_ordinal,scope_kind,scope_id,scope_start_utc,scope_end_utc,
                        rule_id,rule_version,evaluation_id,outcome_kind,alert_id,
                        suppression_ordinal,suppression_code)
                    VALUES($run,$ordinal,$kind,$id,$start,$end,$rule,$version,$evaluation,
                        $outcome,$alert,$suppression_ordinal,$suppression_code);
                    """,
                    ("$run", runId),
                    ("$ordinal", write.ScopeOrdinal),
                    ("$kind", write.ScopeKind),
                    ("$id", write.ScopeId),
                    ("$start", write.ScopeStartUtc is null ? DBNull.Value : Format(write.ScopeStartUtc.Value)),
                    ("$end", write.ScopeEndUtc is null ? DBNull.Value : Format(write.ScopeEndUtc.Value)),
                    ("$rule", write.RuleId),
                    ("$version", write.RuleVersion),
                    ("$evaluation", write.EvaluationId),
                    ("$outcome", write.OutcomeKind),
                    ("$alert", (object?)write.AlertId ?? DBNull.Value),
                    ("$suppression_ordinal", (object?)write.SuppressionOrdinal ?? DBNull.Value),
                    ("$suppression_code", (object?)write.SuppressionCode ?? DBNull.Value));
                insert.ExecuteNonQuery();
            }

            var occurredAtUtc = timeProvider.GetUtcNow();
            using (var terminal = Command(
                connection,
                transaction,
                """
                INSERT INTO pricing_recalculation_events(
                    run_id,event_sequence,event_kind,occurred_at_utc,failure_phase,
                    failure_ordinal_kind,failure_ordinal,failure_code)
                VALUES($run,2,$kind,$time,$phase,$ordinal_kind,$ordinal,$code);
                """,
                ("$run", runId),
                ("$kind", failure is null ? "succeeded" : "failed"),
                ("$time", Format(occurredAtUtc)),
                ("$phase", (object?)failure?.FailurePhase ?? DBNull.Value),
                ("$ordinal_kind", (object?)failure?.FailureOrdinalKind ?? DBNull.Value),
                ("$ordinal", (object?)failure?.FailureOrdinal ?? DBNull.Value),
                ("$code", (object?)failure?.FailureCode ?? DBNull.Value)))
                terminal.ExecuteNonQuery();
            transaction.Commit();
            return new(PricingStoreStatus.Success);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingStoreStatus.Busy);
        }
        catch (SqliteException)
        {
            return new(PricingStoreStatus.Unavailable);
        }
    }

    private PricingEstimateRecord? ValidateAndInsertEstimate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        PricingTargetCompletionWrite write,
        TargetFacts target)
    {
        if (write.SourceEntryOrdinal is not { } sourceEntryOrdinal)
            return null;
        PricingCatalog catalog;
        using (var catalogCommand = Command(
            connection,
            transaction,
            "SELECT canonical_blob FROM pricing_catalog_snapshots WHERE catalog_sha256=$sha;",
            ("$sha", target.CatalogSha256)))
        {
            if (catalogCommand.ExecuteScalar() is not byte[] catalogBytes) return null;
            try
            {
                catalog = PricingCatalogSnapshotConsumer.Deserialize(catalogBytes);
            }
            catch (PricingRegistryValidationException)
            {
                return null;
            }
        }
        PricingEstimateRecord estimate;
        try
        {
            estimate = PricingEstimateConsumer.Deserialize(write.CanonicalEstimateBytes.Span, catalog);
            if (write.ExpectedRequest is null
                || !PricingCanonicalJson.Serialize(
                        new PricingEstimationEngine(catalog).Estimate(write.ExpectedRequest))
                    .AsSpan()
                    .SequenceEqual(write.CanonicalEstimateBytes.Span))
                return null;
        }
        catch (Exception exception) when (
            exception is PricingEstimateValidationException
                or PricingRegistryValidationException
                or ArgumentException
                or InvalidOperationException
                or OverflowException)
        {
            return null;
        }
        CostConfigurationV1 configuration;
        using (var configurationCommand = Command(
            connection,
            transaction,
            "SELECT canonical_blob FROM pricing_configurations WHERE configuration_id=$id;",
            ("$id", target.ConfigurationId)))
        {
            if (configurationCommand.ExecuteScalar() is not byte[] bytes) return null;
            var consumed = CostConfigurationConsumerV1.Consume(bytes);
            if (consumed.Status != CostConsumerStatus.Success || consumed.Value is null) return null;
            configuration = consumed.Value;
        }
        if (sourceEntryOrdinal >= configuration.SourceEntries.Count) return null;
        var sourceEntry = configuration.SourceEntries[sourceEntryOrdinal];
        var expectedProvenanceId = target.ConfigurationId + $".source-entry-{sourceEntryOrdinal:000}";
        if (estimate.CatalogSha256 != target.CatalogSha256
            || estimate.Source.SessionId != target.SessionId
            || Format(estimate.Source.SessionObservedAtUtc) != target.SessionEffectiveAtUtc
            || Format(estimate.CalculationTimeUtc) != target.CalculationTimeUtc
            || estimate.SupersedesEstimateId != target.BaseEstimateId
            || estimate.Source.SourceSurface != sourceEntry.SourceSurface
            || estimate.Source.SourceVersion != sourceEntry.ApplicationVersion
            || estimate.Source.Provider != sourceEntry.Provider
            || estimate.Source.BillingMode != sourceEntry.BillingMode
            || estimate.Source.PricingRoute != sourceEntry.PricingRoute
            || !ConfigurationProvenanceMatches(estimate.Source.BillingModeProvenance, expectedProvenanceId)
            || !ConfigurationProvenanceMatches(estimate.Source.PricingRouteProvenance, expectedProvenanceId))
            return null;

        using var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO pricing_estimates(
                estimate_id,supersedes_estimate_id,schema_version,session_id,catalog_sha256,
                configuration_id,source_entry_ordinal,run_id,target_ordinal,calculation_time_utc,
                session_effective_at_utc,status,source_surface,source_application_version,
                provider,model,billing_mode,pricing_route,registry_version,registry_source_kind,
                currency,amount_text,canonical_sha256,canonical_blob)
            VALUES($estimate,$predecessor,'pricing.estimate.v1',$session,$catalog,$configuration,
                $source_ordinal,$run,$target,$calculation,$effective,$status,$surface,$version,
                $provider,$model,$billing,$route,$registry_version,$registry_kind,$currency,
                $amount,$canonical_sha,$blob);
            """,
            ("$estimate", estimate.EstimateId),
            ("$predecessor", (object?)estimate.SupersedesEstimateId ?? DBNull.Value),
            ("$session", target.SessionId),
            ("$catalog", target.CatalogSha256),
            ("$configuration", target.ConfigurationId),
            ("$source_ordinal", sourceEntryOrdinal),
            ("$run", runId),
            ("$target", write.TargetOrdinal),
            ("$calculation", target.CalculationTimeUtc),
            ("$effective", target.SessionEffectiveAtUtc),
            ("$status", estimate.Status),
            ("$surface", estimate.Source.SourceSurface),
            ("$version", estimate.Source.SourceVersion),
            ("$provider", estimate.Source.Provider),
            ("$model", estimate.Source.ModelId),
            ("$billing", estimate.Source.BillingMode),
            ("$route", estimate.Source.PricingRoute),
            ("$registry_version", (object?)estimate.Registry?.RegistryVersion ?? DBNull.Value),
            ("$registry_kind", (object?)estimate.Registry?.SourceKind ?? DBNull.Value),
            ("$currency", (object?)estimate.Currency ?? DBNull.Value),
            ("$amount", estimate.Amount is null ? DBNull.Value : estimate.Amount.Value.ToString(CultureInfo.InvariantCulture)),
            ("$canonical_sha", Convert.ToHexString(SHA256.HashData(write.CanonicalEstimateBytes.Span)).ToLowerInvariant()),
            ("$blob", write.CanonicalEstimateBytes.ToArray()));
        insert.ExecuteNonQuery();
        return estimate;
    }

    private static PricingStoreStatus InsertCatalogSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PricingCatalog catalog,
        byte[] canonicalBytes,
        DateTimeOffset firstRecordedAtUtc)
    {
        using (var read = Command(
            connection,
            transaction,
            "SELECT canonical_blob,document_count FROM pricing_catalog_snapshots WHERE catalog_sha256=$sha;",
            ("$sha", catalog.CatalogSha256)))
        using (var reader = read.ExecuteReader())
        {
            if (reader.Read())
            {
                return ((byte[])reader[0]).AsSpan().SequenceEqual(canonicalBytes)
                    && reader.GetInt32(1) == catalog.Documents.Count
                        ? PricingStoreStatus.Success
                        : PricingStoreStatus.Conflict;
            }
        }

        using var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO pricing_catalog_snapshots(
                catalog_sha256,schema_version,canonical_blob,document_count,first_recorded_at_utc)
            VALUES($sha,'pricing.catalog-snapshot.v1',$blob,$count,$time);
            """,
            ("$sha", catalog.CatalogSha256),
            ("$blob", canonicalBytes),
            ("$count", catalog.Documents.Count),
            ("$time", Format(firstRecordedAtUtc)));
        insert.ExecuteNonQuery();
        return PricingStoreStatus.Success;
    }

    private static PricingTargetCompletionWrite FreezeTargetCompletion(
        PricingTargetCompletionWrite value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var estimateBytes = value.CanonicalEstimateBytes.ToArray();
        return value with
        {
            ExpectedRequest = value.ExpectedRequest is null
                ? null
                : FreezeEstimateRequest(value.ExpectedRequest),
            CanonicalEstimateBytes = estimateBytes,
        };
    }

    private static PricingConfigurationSelectionFactWrite FreezeSelectionFact(
        PricingConfigurationSelectionFactWrite value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value with { };
    }

    private static PricingBudgetResultWrite FreezeBudgetResult(
        PricingBudgetResultWrite value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.EligibleSessionIds);
        return value with
        {
            EligibleSessionIds = Array.AsReadOnly(value.EligibleSessionIds.ToArray()),
        };
    }

    private static PricingEstimateRequest FreezeEstimateRequest(PricingEstimateRequest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(value.Source);
        ArgumentNullException.ThrowIfNull(value.Usage);
        ArgumentNullException.ThrowIfNull(value.Source.CompletenessReasons);
        static PricingValueProvenance FreezeProvenance(PricingValueProvenance provenance) =>
            (provenance ?? throw new ArgumentNullException(nameof(provenance))) with { };
        static PricingQuantity? FreezeQuantity(PricingQuantity? quantity) =>
            quantity is null
                ? null
                : quantity with { Provenance = FreezeProvenance(quantity.Provenance) };

        return value with
        {
            Source = value.Source with
            {
                CompletenessReasons = Array.AsReadOnly(
                    value.Source.CompletenessReasons.ToArray()),
                SessionTimeProvenance = FreezeProvenance(value.Source.SessionTimeProvenance),
                ProviderProvenance = FreezeProvenance(value.Source.ProviderProvenance),
                ModelProvenance = FreezeProvenance(value.Source.ModelProvenance),
                BillingModeProvenance = FreezeProvenance(value.Source.BillingModeProvenance),
                PricingRouteProvenance = FreezeProvenance(value.Source.PricingRouteProvenance),
            },
            Usage = value.Usage with
            {
                InputTokens = FreezeQuantity(value.Usage.InputTokens),
                OutputTokens = FreezeQuantity(value.Usage.OutputTokens),
                CacheReadTokens = FreezeQuantity(value.Usage.CacheReadTokens),
                CacheWrite5mTokens = FreezeQuantity(value.Usage.CacheWrite5mTokens),
                CacheWrite1hTokens = FreezeQuantity(value.Usage.CacheWrite1hTokens),
                ReasoningTokens = FreezeQuantity(value.Usage.ReasoningTokens),
                RequestCount = FreezeQuantity(value.Usage.RequestCount),
                CreditCount = FreezeQuantity(value.Usage.CreditCount),
            },
        };
    }

    private static bool IsTargetCompletionShapeValid(PricingTargetCompletionWrite value) =>
        value.TargetOrdinal is >= 0 and <= 99
        && value.ResultKind switch
        {
            "estimate" => value.ResultCode is null
                && value.SourceEntryOrdinal is >= 0 and <= 31
                && value.ExpectedRequest is not null
                && value.CanonicalEstimateBytes.Length is >= 1 and <= 1_048_576,
            "unavailable" => value.ResultCode is
                    "source_mapping_unavailable"
                    or "source_adapter_unavailable"
                    or "codex_adapter_unavailable"
                && value.SourceEntryOrdinal is null
                && value.ExpectedRequest is null
                && value.CanonicalEstimateBytes.IsEmpty,
            "failed" => IsFailureCode(value.ResultCode)
                && value.SourceEntryOrdinal is null
                && value.ExpectedRequest is null
                && value.CanonicalEstimateBytes.IsEmpty,
            _ => false,
        };

    private static bool IsRecalculationTargetShapeValid(PricingRecalculationTargetCapture value)
    {
        if (!IsCanonicalUuid(value.SessionId)
            || value.SessionStatus is not ("completed" or "failed")
            || value.SessionEffectiveAtUtc.Offset != TimeSpan.Zero
            || value.SessionUpdatedAtUtc.Offset != TimeSpan.Zero
            || !IsLowerSha(value.SourcePartitionDigest)
            || value.BaseAttemptRevision < 0
            || (value.BaseHeadRevision is null) != (value.BaseEstimateId is null)
            || value.BaseHeadRevision is < 1
            || value.BaseEstimateId is not null
                && !IsPrefixedSha(value.BaseEstimateId, "pricing-estimate-"))
            return false;
        return value.SourcePartitionState switch
        {
            "resolved" => value.SourcePartitionCount is >= 1 and <= 256
                && IsLowerToken(value.SourceSurface)
                && IsSafeVersion(value.SourceApplicationVersion),
            "missing" or "incomplete" or "mixed" => value.SourcePartitionCount is >= 0 and <= 257
                && value.SourceSurface is null
                && value.SourceApplicationVersion is null,
            _ => false,
        };
    }

    private static bool IsBudgetResultShapeValid(PricingBudgetResultWrite value)
    {
        var ruleMatchesScope = value.ScopeKind switch
        {
            "session" => value.RuleId == "session-estimated-cost-threshold"
                && value.ScopeStartUtc is null
                && value.ScopeEndUtc is null,
            "utc_day" => value.RuleId == "daily-estimated-cost-threshold"
                && value.ScopeStartUtc is { Offset.Ticks: 0 } dayStart
                && value.ScopeEndUtc is { Offset.Ticks: 0 } dayEnd
                && dayStart.TimeOfDay == TimeSpan.Zero
                && dayEnd == dayStart.AddDays(1),
            "rolling_period" => value.RuleId == "period-estimated-cost-threshold"
                && value.ScopeStartUtc is { Offset.Ticks: 0 } periodStart
                && value.ScopeEndUtc is { Offset.Ticks: 0 } periodEnd
                && periodStart.TimeOfDay == TimeSpan.Zero
                && periodEnd.TimeOfDay == TimeSpan.Zero
                && periodEnd > periodStart
                && (periodEnd - periodStart).TotalDays is >= 2 and <= 366,
            _ => false,
        };
        if (value.ScopeOrdinal is < 0 or > 7
            || !ruleMatchesScope
            || value.RuleVersion != "1"
            || !IsPrefixedSha(value.ScopeId, "cost-scope-")
            || !IsLowerSha(value.EligibilityDigest)
            || value.EligibleSessionIds.Count > 2_000
            || value.EligibleSessionIds.Distinct(StringComparer.Ordinal).Count()
                != value.EligibleSessionIds.Count
            || value.EligibleSessionIds.Any(sessionId => !IsCanonicalUuid(sessionId))
            || !IsLowerSha(value.EvaluationId))
            return false;
        return value.OutcomeKind switch
        {
            "receipt" => IsLowerSha(value.AlertId)
                && value.SuppressionOrdinal is null
                && value.SuppressionCode is null,
            "suppression" => value.AlertId is null
                && value.SuppressionOrdinal is >= 0
                && IsSuppressionCode(value.SuppressionCode),
            "no_match" => value.AlertId is null
                && value.SuppressionOrdinal is null
                && value.SuppressionCode is null,
            _ => false,
        };
    }

    private static bool IsFailureShapeValid(PricingRunFailureWrite? value)
    {
        if (value is null) return true;
        return value.FailurePhase switch
        {
            "head_input" => value.FailureOrdinalKind == "target"
                && value.FailureOrdinal is >= 0 and <= 99
                && value.FailureCode is "stale_recalculation_input" or "stale_active_estimate",
            "adapter" => value.FailureOrdinalKind == "target"
                && value.FailureOrdinal is >= 0 and <= 99
                && value.FailureCode == "source_adapter_failed",
            "estimate_validation" => value.FailureOrdinalKind == "target"
                && value.FailureOrdinal is >= 0 and <= 99
                && value.FailureCode is "invalid_estimate_source" or "pricing_estimation_failed",
            "budget_payload" => value.FailureOrdinalKind == "scope"
                && value.FailureOrdinal is >= 0 and <= 7
                && value.FailureCode == "budget_payload_too_large",
            "pricing_store" => value.FailureOrdinalKind == "target"
                && value.FailureOrdinal is >= 0 and <= 99
                && value.FailureCode == "pricing_store_failed",
            "alert_evaluation" => value.FailureOrdinalKind == "scope"
                && value.FailureOrdinal is >= 0 and <= 7
                && value.FailureCode == "alert_evaluation_failed",
            "alert_store" => value.FailureOrdinalKind == "scope"
                && value.FailureOrdinal is >= 0 and <= 7
                && value.FailureCode == "alert_store_failed",
            _ => false,
        };
    }

    private static bool BudgetMatchesRequest(
        PricingBudgetResultWrite value,
        CostBudgetScopeV1 scope)
    {
        if (value.ScopeKind != scope.ScopeKind
            || value.ScopeId != PricingAlertCostScopeIdentityV2.Create(
                value.ScopeKind,
                value.ScopeStartUtc,
                value.ScopeEndUtc,
                value.EligibilityDigest,
                value.EligibleSessionIds))
            return false;
        return scope.ScopeKind switch
        {
            "session" => value.ScopeStartUtc is null
                && value.ScopeEndUtc is null
                && value.EligibleSessionIds.Count == 1
                && value.EligibleSessionIds[0] == scope.SessionId,
            "utc_day" => DateOnly.TryParseExact(scope.UtcDate, "yyyy-MM-dd", out var date)
                && value.ScopeStartUtc == new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                && value.ScopeEndUtc == new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            "rolling_period" => value.ScopeEndUtc == scope.CutoffUtc
                && value.ScopeStartUtc == scope.CutoffUtc!.Value.AddDays(-scope.WindowDays!.Value),
            _ => false,
        };
    }

    private static bool BudgetParentsAreValid(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PricingBudgetResultWrite value)
    {
        using (var evaluation = Command(
            connection,
            transaction,
            "SELECT COUNT(*) FROM alert_evaluations WHERE evaluation_id=$evaluation AND schema_version='alert.evaluation.v2';",
            ("$evaluation", value.EvaluationId)))
            if (Convert.ToInt64(evaluation.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) return false;
        if (value.OutcomeKind == "receipt")
        {
            using var receipt = Command(
                connection,
                transaction,
                """
                SELECT COUNT(*) FROM alert_receipts
                WHERE alert_id=$alert
                  AND evaluation_id=$evaluation
                  AND schema_version='alert.receipt.v2';
                """,
                ("$alert", value.AlertId!),
                ("$evaluation", value.EvaluationId));
            return Convert.ToInt64(receipt.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        if (value.OutcomeKind == "suppression")
        {
            using var suppression = Command(
                connection,
                transaction,
                """
                SELECT COUNT(*) FROM alert_suppressions
                WHERE evaluation_id=$evaluation
                  AND suppression_ordinal=$ordinal
                  AND rule_id=$rule
                  AND rule_version=$version
                  AND code=$code;
                """,
                ("$evaluation", value.EvaluationId),
                ("$ordinal", value.SuppressionOrdinal!.Value),
                ("$rule", value.RuleId),
                ("$version", value.RuleVersion),
                ("$code", value.SuppressionCode!));
            return Convert.ToInt64(suppression.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        return true;
    }

    private static bool IsCanonicalUuidV7(string value) =>
        Guid.TryParseExact(value, "D", out var parsed)
        && parsed.ToString("D") == value
        && value[14] == '7'
        && value[19] is '8' or '9' or 'a' or 'b';

    private static bool IsCanonicalUuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed)
        && parsed.ToString("D") == value;

    private static bool IsLowerSha(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsPrefixedSha(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal)
        && IsLowerSha(value[prefix.Length..]);

    private static bool IsLowerToken(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '_'
            or '-');

    private static bool IsSafeVersion(string? value) =>
        value is { Length: >= 1 and <= 64 }
        && value.All(character => character is >= '!' and <= '~');

    private static bool IsFailureCode(string? value) =>
        value is
            "source_adapter_failed"
            or "invalid_estimate_source"
            or "pricing_estimation_failed"
            or "budget_payload_too_large"
            or "stale_recalculation_input"
            or "stale_active_estimate"
            or "pricing_store_failed"
            or "alert_evaluation_failed"
            or "alert_store_failed"
            or "recalculation_interrupted";

    private static bool IsSuppressionCode(string? value) =>
        value is
            "rule_disabled"
            or "no_eligible_sessions"
            or "eligible_set_incomplete"
            or "no_covered_estimate"
            or "aggregate_amount_not_representable"
            or "insufficient_estimate_coverage";

    private sealed record TargetFacts(
        string SessionId,
        string SessionEffectiveAtUtc,
        long? BaseHeadRevision,
        string? BaseEstimateId,
        long BaseAttemptRevision,
        string ConfigurationId,
        string CatalogSha256,
        string CalculationTimeUtc);

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private static bool ConfigurationProvenanceMatches(
        PricingValueProvenance value,
        string expectedEventId) =>
        value.SourceAdapter == "local-monitor-cost-configuration"
        && value.SourceVersionOrSchemaFingerprint == "cost.configuration.v1"
        && value.SourceEventOrTraceSpanId == expectedEventId
        && value.CaptureContentState == "not_captured"
        && value.NormalizationVersion == "cost-configuration-provenance.v1";

    private static PricingStoreResult Rollback(SqliteTransaction transaction, PricingStoreStatus status)
    {
        transaction.Rollback();
        return new(status);
    }

    private static PricingStoreResult<T> Rollback<T>(SqliteTransaction transaction, PricingStoreStatus status)
    {
        transaction.Rollback();
        return new(status, default);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }
}
