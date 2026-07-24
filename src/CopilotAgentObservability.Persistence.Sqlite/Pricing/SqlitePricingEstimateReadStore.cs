using System.Text.Json;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

public sealed record CostEstimateRegistryReadV1(
    string RegistryVersion,
    string SourceKind,
    string SourceId,
    string SourceLabel,
    string EntryKey,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    DateOnly LastReviewedDate,
    DateOnly StaleAfterDate,
    string Currency,
    string? SourceReference);

public sealed record CostEstimateComponentReadV1(
    string Category,
    string State,
    decimal? Amount,
    string? MissingReason);

public sealed record CostEstimateCoverageReadV1(
    IReadOnlyList<string> RequiredCategories,
    IReadOnlyList<string> EstimatedCategories,
    IReadOnlyList<string> MissingCategories);

public sealed record CostEstimateDeltaReadV1(
    string State,
    decimal? Amount,
    string? Currency,
    string? BasisFreshness,
    IReadOnlyList<string> ChangedFields);

public sealed record CostSessionEstimateItemReadV1(
    long HeadRevision,
    string EstimateId,
    string? PredecessorEstimateId,
    DateTimeOffset CalculationTimeUtc,
    DateTimeOffset SessionEffectiveAtUtc,
    string EstimateStatus,
    string Freshness,
    string AmountKind,
    decimal? Amount,
    string? Currency,
    string Provider,
    string Model,
    string BillingMode,
    string PricingRoute,
    string CatalogSha256,
    string ConfigurationId,
    CostEstimateRegistryReadV1? Registry,
    IReadOnlyList<CostEstimateComponentReadV1> Components,
    CostEstimateCoverageReadV1 Coverage,
    IReadOnlyList<string> Reasons,
    CostEstimateDeltaReadV1 Delta,
    string Disclaimer);

public sealed record CostSessionLatestAttemptReadV1(
    long AttemptRevision,
    string RunId,
    DateTimeOffset CalculationTimeUtc,
    string Freshness,
    string Kind,
    string? EstimateStatus,
    string? EstimateId,
    string? Code);

public sealed record CostSessionEstimatesReadV1(
    string SessionId,
    string CalculationState,
    long? ActiveHeadRevision,
    string? ActiveEstimateId,
    long? LatestAttemptRevision,
    CostSessionLatestAttemptReadV1? LatestAttempt,
    IReadOnlyList<CostSessionEstimateItemReadV1> Items,
    string? NextAfter);

public sealed record CostSessionEstimateReadV1(
    string SessionId,
    long? ActiveHeadRevision,
    string? ActiveEstimateId,
    CostSessionEstimateItemReadV1 Item);

public sealed partial class SqlitePricingReadStore
{
    private const int MaximumEstimateResponseBytes = 8 * 1024 * 1024;

    public PricingReadResult<CostSessionEstimatesReadV1> ReadSessionEstimates(
        string sessionId,
        ReadOnlyMemory<byte> currentProviderCatalogBytes,
        string? after,
        int limit = 50)
    {
        if (!CanonicalGuid(sessionId)) return new(PricingReadStatus.NotFound);
        if (limit is < 1 or > 100
            || after is not null && !PrefixedSha(after, "pricing-estimate-"))
            return new(PricingReadStatus.InvalidCursor);
        try
        {
            var currentCatalog = PricingCatalogSnapshotConsumer.Deserialize(
                currentProviderCatalogBytes.Span);
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback<CostSessionEstimatesReadV1>(
                    transaction,
                    PricingReadStatus.Unavailable);
            if (!SessionExists(connection, transaction, sessionId))
                return Rollback<CostSessionEstimatesReadV1>(
                    transaction,
                    PricingReadStatus.NotFound);

            long? afterRevision = null;
            if (after is not null)
            {
                using var cursor = Command(
                    connection,
                    transaction,
                    """
                    SELECT head_revision FROM pricing_estimate_heads
                    WHERE session_id=$session AND estimate_id=$estimate;
                    """,
                    ("$session", sessionId),
                    ("$estimate", after));
                if (cursor.ExecuteScalar() is not long revision)
                    return Rollback<CostSessionEstimatesReadV1>(
                        transaction,
                        PricingReadStatus.InvalidCursor);
                afterRevision = revision;
            }

            var headRows = ReadHeadRows(
                connection,
                transaction,
                sessionId,
                afterRevision,
                limit + 1);
            var hasMore = headRows.Count > limit;
            if (hasMore) headRows.RemoveAt(headRows.Count - 1);
            var items = headRows
                .Select(row => BuildEstimateItem(
                    connection,
                    transaction,
                    sessionId,
                    row.HeadRevision,
                    row.EstimateId,
                    currentCatalog))
                .ToArray();
            var activeHead = ReadActiveHead(connection, transaction, sessionId);
            var latestAttempt = ReadLatestAttempt(
                connection,
                transaction,
                sessionId,
                currentCatalog);
            var calculationState = CalculateState(
                connection,
                transaction,
                sessionId,
                activeHead,
                latestAttempt,
                currentCatalog);
            var response = new CostSessionEstimatesReadV1(
                sessionId,
                calculationState,
                activeHead?.HeadRevision,
                activeHead?.EstimateId,
                latestAttempt?.AttemptRevision,
                latestAttempt,
                Array.AsReadOnly(items),
                null);
            var fitted = ApplyEstimatePageByteLimit(
                response,
                hasMore,
                MaximumEstimateResponseBytes);
            if (fitted.Status != PricingReadStatus.Success)
                return Rollback<CostSessionEstimatesReadV1>(transaction, fitted.Status);
            transaction.Commit();
            return fitted;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingReadStatus.Busy);
        }
        catch (Exception exception) when (exception is
            SqliteException or
            InvalidOperationException or
            FormatException or
            ArgumentException or
            OverflowException or
            PricingRegistryValidationException or
            PricingEstimateValidationException)
        {
            return new(PricingReadStatus.Unavailable);
        }
    }

    public PricingReadResult<CostSessionEstimateReadV1> ReadSessionEstimate(
        string sessionId,
        string estimateId,
        ReadOnlyMemory<byte> currentProviderCatalogBytes)
    {
        if (!CanonicalGuid(sessionId)
            || !PrefixedSha(estimateId, "pricing-estimate-"))
            return new(PricingReadStatus.NotFound);
        try
        {
            var currentCatalog = PricingCatalogSnapshotConsumer.Deserialize(
                currentProviderCatalogBytes.Span);
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback<CostSessionEstimateReadV1>(
                    transaction,
                    PricingReadStatus.Unavailable);
            if (!SessionExists(connection, transaction, sessionId))
                return Rollback<CostSessionEstimateReadV1>(
                    transaction,
                    PricingReadStatus.NotFound);
            using var head = Command(
                connection,
                transaction,
                """
                SELECT head_revision FROM pricing_estimate_heads
                WHERE session_id=$session AND estimate_id=$estimate;
                """,
                ("$session", sessionId),
                ("$estimate", estimateId));
            if (head.ExecuteScalar() is not long headRevision)
                return Rollback<CostSessionEstimateReadV1>(
                    transaction,
                    PricingReadStatus.NotFound);
            var activeHead = ReadActiveHead(connection, transaction, sessionId);
            var response = new CostSessionEstimateReadV1(
                sessionId,
                activeHead?.HeadRevision,
                activeHead?.EstimateId,
                BuildEstimateItem(
                    connection,
                    transaction,
                    sessionId,
                    headRevision,
                    estimateId,
                    currentCatalog));
            if (SerializedSize(response) > MaximumEstimateResponseBytes)
                return Rollback<CostSessionEstimateReadV1>(
                    transaction,
                    PricingReadStatus.ResponseTooLarge);
            transaction.Commit();
            return new(PricingReadStatus.Success, response);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingReadStatus.Busy);
        }
        catch (Exception exception) when (exception is
            SqliteException or
            InvalidOperationException or
            FormatException or
            ArgumentException or
            OverflowException or
            PricingRegistryValidationException or
            PricingEstimateValidationException)
        {
            return new(PricingReadStatus.Unavailable);
        }
    }

    private static CostSessionEstimateItemReadV1 BuildEstimateItem(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        long headRevision,
        string estimateId,
        PricingCatalog currentCatalog)
    {
        var current = ReadEstimateProjection(
            connection,
            transaction,
            sessionId,
            headRevision,
            estimateId,
            currentCatalog);
        EstimateProjection? predecessor = null;
        if (current.Estimate.SupersedesEstimateId is not null)
        {
            using var predecessorHead = Command(
                connection,
                transaction,
                """
                SELECT head_revision FROM pricing_estimate_heads
                WHERE session_id=$session AND estimate_id=$estimate;
                """,
                ("$session", sessionId),
                ("$estimate", current.Estimate.SupersedesEstimateId));
            if (predecessorHead.ExecuteScalar() is not long predecessorRevision)
                throw new InvalidOperationException("Estimate predecessor head is missing.");
            predecessor = ReadEstimateProjection(
                connection,
                transaction,
                sessionId,
                predecessorRevision,
                current.Estimate.SupersedesEstimateId,
                currentCatalog);
        }

        return new(
            current.HeadRevision,
            current.Estimate.EstimateId,
            current.Estimate.SupersedesEstimateId,
            current.Estimate.CalculationTimeUtc,
            current.Estimate.Source.SessionObservedAtUtc,
            current.Estimate.Status,
            current.Fresh ? "fresh" : "stale",
            AmountKind(current.Estimate.Status, current.Fresh),
            current.Fresh && current.Estimate.Status != "not-estimable"
                ? current.Estimate.Amount
                : null,
            current.Fresh && current.Estimate.Status != "not-estimable"
                ? current.Estimate.Currency
                : null,
            current.Estimate.Source.Provider,
            current.Estimate.Source.ModelId,
            current.Estimate.Source.BillingMode,
            current.Estimate.Source.PricingRoute,
            current.Estimate.CatalogSha256,
            current.ConfigurationId,
            ProjectRegistry(current.Estimate.Registry, current.ExactCatalog),
            ProjectComponents(current.Estimate.Components),
            ProjectCoverage(current.Estimate.Coverage),
            Array.AsReadOnly(current.Estimate.Reasons.ToArray()),
            ProjectDelta(current, predecessor),
            "estimated_cost_not_invoice.v1");
    }

    private static EstimateProjection ReadEstimateProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        long headRevision,
        string estimateId,
        PricingCatalog currentCatalog)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT e.canonical_blob,c.canonical_blob,e.configuration_id,e.run_id
            FROM pricing_estimate_heads h
            JOIN pricing_estimates e
              ON e.session_id=h.session_id AND e.estimate_id=h.estimate_id
            JOIN pricing_catalog_snapshots c ON c.catalog_sha256=e.catalog_sha256
            WHERE h.session_id=$session AND h.head_revision=$revision
              AND h.estimate_id=$estimate;
            """,
            ("$session", sessionId),
            ("$revision", headRevision),
            ("$estimate", estimateId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("Estimate projection source is missing.");
        var estimateBytes = ((byte[])reader[0]).ToArray();
        var catalogBytes = ((byte[])reader[1]).ToArray();
        var configurationId = reader.GetString(2);
        var runId = reader.GetString(3);
        if (reader.Read())
            throw new InvalidOperationException("Estimate projection source is ambiguous.");
        reader.Close();
        var exactCatalog = PricingCatalogSnapshotConsumer.Deserialize(catalogBytes);
        var estimate = PricingEstimateConsumer.Deserialize(estimateBytes, exactCatalog);
        if (estimate.EstimateId != estimateId
            || estimate.Source.SessionId != sessionId)
            throw new InvalidOperationException("Estimate projection identity is invalid.");
        var fresh = IsCapturedInputFresh(connection, transaction, sessionId, runId)
            && IsEstimateSemanticallyFresh(
                connection,
                transaction,
                estimateId,
                currentCatalog);
        return new(headRevision, configurationId, runId, estimate, exactCatalog, fresh);
    }

    private static CostSessionLatestAttemptReadV1? ReadLatestAttempt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        PricingCatalog currentCatalog)
    {
        var active = ReadActiveAttempt(
            connection,
            transaction,
            sessionId,
            currentCatalog);
        if (active is not null)
            return new(
                active.AttemptRevision,
                active.RunId,
                active.CalculationTimeUtc,
                active.Freshness,
                active.State,
                null,
                null,
                null);
        var terminal = ReadTerminalAttempts(
            connection,
            transaction,
            sessionId,
            currentCatalog,
            null,
            1).SingleOrDefault();
        return terminal is null
            ? null
            : new(
                terminal.AttemptRevision,
                terminal.RunId,
                terminal.CalculationTimeUtc,
                terminal.Freshness,
                terminal.Kind,
                terminal.EstimateStatus,
                terminal.EstimateId,
                terminal.Code);
    }

    private static string CalculateState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        EstimateHead? activeHead,
        CostSessionLatestAttemptReadV1? latestAttempt,
        PricingCatalog currentCatalog)
    {
        if (activeHead is not null)
        {
            var projection = ReadEstimateProjection(
                connection,
                transaction,
                sessionId,
                activeHead.HeadRevision,
                activeHead.EstimateId,
                currentCatalog);
            if (!projection.Fresh) return "stale";
            return projection.Estimate.Status == "not-estimable"
                ? "not_estimable"
                : projection.Estimate.Status;
        }
        if (latestAttempt is null) return "not_calculated";
        if (latestAttempt.Freshness == "stale") return "stale";
        return latestAttempt.Kind switch
        {
            "estimate" => latestAttempt.EstimateStatus == "not-estimable"
                ? "not_estimable"
                : latestAttempt.EstimateStatus!,
            "requested" => "requested",
            "running" => "running",
            "unavailable" => "unavailable",
            "failed" => "failed",
            _ => throw new InvalidOperationException("Invalid attempt kind."),
        };
    }

    private static CostEstimateDeltaReadV1 ProjectDelta(
        EstimateProjection current,
        EstimateProjection? predecessor)
    {
        if (predecessor is null)
            return new(
                "not_applicable",
                null,
                null,
                null,
                Array.Empty<string>());
        var changed = ChangedFields(current, predecessor);
        if (current.Estimate.Status != "estimated"
            || predecessor.Estimate.Status != "estimated"
            || current.Estimate.Amount is null
            || predecessor.Estimate.Amount is null
            || current.Estimate.Currency is null
            || current.Estimate.Currency != predecessor.Estimate.Currency)
            return new("not_applicable", null, null, null, changed);
        try
        {
            return new(
                "available",
                checked(current.Estimate.Amount.Value - predecessor.Estimate.Amount.Value),
                current.Estimate.Currency,
                current.Fresh && predecessor.Fresh ? "both_fresh" : "includes_stale",
                changed);
        }
        catch (OverflowException)
        {
            return new("unrepresentable", null, null, null, changed);
        }
    }

    private static IReadOnlyList<string> ChangedFields(
        EstimateProjection current,
        EstimateProjection predecessor)
    {
        var values = new List<string>();
        AddIf(values, "status", current.Estimate.Status != predecessor.Estimate.Status);
        AddIf(values, "amount", current.Estimate.Amount != predecessor.Estimate.Amount
            || current.Estimate.Currency != predecessor.Estimate.Currency);
        AddIf(values, "provider", current.Estimate.Source.Provider != predecessor.Estimate.Source.Provider);
        AddIf(values, "model", current.Estimate.Source.ModelId != predecessor.Estimate.Source.ModelId);
        AddIf(values, "billing_mode", current.Estimate.Source.BillingMode != predecessor.Estimate.Source.BillingMode);
        AddIf(values, "pricing_route", current.Estimate.Source.PricingRoute != predecessor.Estimate.Source.PricingRoute);
        AddIf(values, "registry", !RegistryEquals(current.Estimate.Registry, predecessor.Estimate.Registry));
        AddIf(values, "catalog", current.Estimate.CatalogSha256 != predecessor.Estimate.CatalogSha256);
        AddIf(values, "configuration", current.ConfigurationId != predecessor.ConfigurationId);
        AddIf(values, "coverage", !CoverageEquals(current.Estimate.Coverage, predecessor.Estimate.Coverage));
        AddIf(values, "components", !ComponentsEqual(current.Estimate.Components, predecessor.Estimate.Components));
        AddIf(values, "session_time", current.Estimate.Source.SessionObservedAtUtc
            != predecessor.Estimate.Source.SessionObservedAtUtc);
        values.Sort(StringComparer.Ordinal);
        return Array.AsReadOnly(values.ToArray());
    }

    private static void AddIf(List<string> values, string value, bool condition)
    {
        if (condition) values.Add(value);
    }

    private static bool RegistryEquals(
        PricingRegistryProvenance? left,
        PricingRegistryProvenance? right) =>
        left == right;

    private static bool CoverageEquals(
        PricingEstimateCoverage left,
        PricingEstimateCoverage right) =>
        left.RequiredCategories.SequenceEqual(right.RequiredCategories, StringComparer.Ordinal)
        && left.EstimatedCategories.SequenceEqual(right.EstimatedCategories, StringComparer.Ordinal)
        && left.MissingCategories.SequenceEqual(right.MissingCategories, StringComparer.Ordinal);

    private static bool ComponentsEqual(
        IReadOnlyList<PricingEstimateComponent> left,
        IReadOnlyList<PricingEstimateComponent> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair =>
            pair.First.Category == pair.Second.Category
            && pair.First.Amount == pair.Second.Amount
            && pair.First.MissingReason == pair.Second.MissingReason);

    private static CostEstimateRegistryReadV1? ProjectRegistry(
        PricingRegistryProvenance? registry,
        PricingCatalog exactCatalog)
    {
        if (registry is null) return null;
        var document = exactCatalog.Documents.Single(item =>
            item.SourceId == registry.SourceId
            && item.RegistryVersion == registry.RegistryVersion
            && item.SourceKind == registry.SourceKind);
        return new(
            registry.RegistryVersion,
            registry.SourceKind,
            registry.SourceId,
            document.SourceLabel,
            registry.EntryKey,
            registry.EffectiveFromUtc,
            registry.EffectiveToUtc,
            registry.LastReviewedDate,
            document.StaleAfterDate,
            registry.Currency,
            registry.SourceKind == PricingRegistrySourceKinds.Bundled
                ? registry.SourceReference
                : null);
    }

    private static IReadOnlyList<CostEstimateComponentReadV1> ProjectComponents(
        IReadOnlyList<PricingEstimateComponent> components) =>
        Array.AsReadOnly(components.Select(component => new CostEstimateComponentReadV1(
            component.Category,
            component.Amount is null ? "missing" : "available",
            component.Amount,
            component.MissingReason)).ToArray());

    private static CostEstimateCoverageReadV1 ProjectCoverage(
        PricingEstimateCoverage coverage) =>
        new(
            Array.AsReadOnly(coverage.RequiredCategories.ToArray()),
            Array.AsReadOnly(coverage.EstimatedCategories.ToArray()),
            Array.AsReadOnly(coverage.MissingCategories.ToArray()));

    private static string AmountKind(string status, bool fresh) =>
        fresh
            ? status switch
            {
                "estimated" => "complete_total",
                "partial" => "provisional_known_component_subtotal",
                _ => "not_applicable",
            }
            : "not_applicable";

    private static List<EstimateHead> ReadHeadRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        long? afterRevision,
        int limit)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT head_revision,estimate_id FROM pricing_estimate_heads
            WHERE session_id=$session
              AND ($after IS NULL OR head_revision<$after)
            ORDER BY head_revision DESC LIMIT $limit;
            """,
            ("$session", sessionId),
            ("$after", (object?)afterRevision ?? DBNull.Value),
            ("$limit", limit));
        using var reader = command.ExecuteReader();
        var values = new List<EstimateHead>();
        while (reader.Read())
            values.Add(new(reader.GetInt64(0), reader.GetString(1)));
        return values;
    }

    private static EstimateHead? ReadActiveHead(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT head_revision,estimate_id FROM pricing_estimate_heads
            WHERE session_id=$session ORDER BY head_revision DESC LIMIT 1;
            """,
            ("$session", sessionId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetString(1)) : null;
    }

    private static bool SessionExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId) =>
        ScalarLong(
            connection,
            transaction,
            "SELECT COUNT(*) FROM sessions WHERE session_id=$session;",
            ("$session", sessionId)) == 1;

    private static int SerializedSize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            }).Length;

    internal static PricingReadResult<CostSessionEstimatesReadV1>
        ApplyEstimatePageByteLimit(
            CostSessionEstimatesReadV1 response,
            bool sourceHasMore,
            int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var items = response.Items.ToList();
        var originalCount = items.Count;
        while (true)
        {
            var continuation = items.Count > 0
                && (sourceHasMore || items.Count < originalCount)
                    ? items[^1].EstimateId
                    : null;
            var candidate = response with
            {
                Items = Array.AsReadOnly(items.ToArray()),
                NextAfter = continuation,
            };
            if (SerializedSize(candidate) <= maximumBytes)
                return new(PricingReadStatus.Success, candidate);
            if (items.Count <= 1)
                return new(PricingReadStatus.ResponseTooLarge);
            items.RemoveAt(items.Count - 1);
        }
    }

    private sealed record EstimateHead(long HeadRevision, string EstimateId);

    private sealed record EstimateProjection(
        long HeadRevision,
        string ConfigurationId,
        string RunId,
        PricingEstimateRecord Estimate,
        PricingCatalog ExactCatalog,
        bool Fresh);
}
