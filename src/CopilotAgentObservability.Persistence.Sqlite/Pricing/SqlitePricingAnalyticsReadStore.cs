using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

public sealed partial class SqlitePricingReadStore
{
    private static readonly Regex AnalyticsEmail = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AnalyticsCredential = new(
        @"(?:^|[^A-Za-z0-9])(?:sk-|gh[pousr]_|github_pat_|glpat-|AKIA|AIza|xox[baprs]-|Bearer\s+|Basic\s+|Authorization\s*[:=]|(?:api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|token|secret|password|credential)\s*[:=]\s*\S+)|(?:sk-[A-Za-z0-9_-]{32,}|gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{20,}|AKIA[A-Z0-9]{16}|AIza[A-Za-z0-9_-]{30,}|xox[baprs]-[A-Za-z0-9-]{20,})|-----BEGIN [^-]*(?:PRIVATE KEY|CERTIFICATE)-----",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public PricingReadResult<CostAnalyticsReadV1> ReadAnalytics(
        CostAnalyticsQueryV1 query,
        ReadOnlyMemory<byte> currentProviderCatalogBytes)
    {
        ArgumentNullException.ThrowIfNull(query);
        var preflight = SqliteCostAnalyticsProjectorV1.Preflight(query);
        if (preflight != PricingReadStatus.Success)
            return new(preflight);
        try
        {
            var currentCatalog = PricingCatalogSnapshotConsumer.Deserialize(
                currentProviderCatalogBytes.Span);
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback<CostAnalyticsReadV1>(
                    transaction,
                    PricingReadStatus.Unavailable);

            var (headRevision, configurationId, configuration) =
                ReadAnalyticsConfiguration(connection, transaction);
            var members = configuration is null
                ? []
                : ReadAnalyticsMembers(
                    connection,
                    transaction,
                    query,
                    configuration,
                    currentCatalog);
            var projected = SqliteCostAnalyticsProjectorV1.Project(
                query,
                headRevision,
                configurationId,
                currentCatalog.CatalogSha256,
                members);
            if (projected.Status == PricingReadStatus.Success)
                transaction.Commit();
            else
                transaction.Rollback();
            return projected;
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

    private static (
        long HeadRevision,
        string? ConfigurationId,
        CostConfigurationV1? Configuration)
        ReadAnalyticsConfiguration(
            SqliteConnection connection,
            SqliteTransaction transaction)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT h.head_revision,h.configuration_id,c.canonical_blob
            FROM pricing_configuration_heads h
            JOIN pricing_configurations c ON c.configuration_id=h.configuration_id
            ORDER BY h.head_revision DESC LIMIT 1;
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (0, null, null);
        var revision = reader.GetInt64(0);
        var id = reader.GetString(1);
        var bytes = ((byte[])reader[2]).ToArray();
        if (reader.Read())
            throw new InvalidOperationException("Pricing configuration head is ambiguous.");
        reader.Close();
        var consumed = CostConfigurationConsumerV1.Consume(bytes);
        if (consumed.Status != CostConsumerStatus.Success
            || consumed.Value is null
            || consumed.Value.ConfigurationId != id)
            throw new InvalidOperationException("Pricing configuration head is invalid.");
        return (revision, id, consumed.Value);
    }

    private static IReadOnlyList<CostAnalyticsMemberV1> ReadAnalyticsMembers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CostAnalyticsQueryV1 query,
        CostConfigurationV1 configuration,
        PricingCatalog currentCatalog)
    {
        var members = new List<CostAnalyticsMemberV1>();
        string? lastSeen = null;
        string? lastSessionId = null;
        while (members.Count < 2_001)
        {
            using var command = Command(
                connection,
                transaction,
                SessionCurrentUseEligibilitySqlV1.EligibleSessionIdsCte + """
                SELECT session.session_id,session.status,session.last_seen_at,
                       session.updated_at,session.repository,session.workspace
                FROM sessions session
                JOIN current_session_use_eligibility eligible
                  ON eligible.session_id=session.session_id
                WHERE session.last_seen_at >= $from AND session.last_seen_at < $to
                  AND ($last_seen IS NULL OR session.last_seen_at>$last_seen
                    OR (session.last_seen_at=$last_seen AND session.session_id>$last_session))
                ORDER BY session.last_seen_at,session.session_id LIMIT 256;
                """,
                ("$from", FormatAnalyticsTimestamp(query.From)),
                ("$to", FormatAnalyticsTimestamp(query.To)),
                ("$last_seen", (object?)lastSeen ?? DBNull.Value),
                ("$last_session", (object?)lastSessionId ?? DBNull.Value));
            using var reader = command.ExecuteReader();
            var sessions = new List<AnalyticsSessionRow>(256);
            while (reader.Read())
                sessions.Add(new(
                    reader.GetString(0),
                    reader.GetString(1),
                    Parse(reader.GetString(2)),
                    Parse(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            reader.Close();
            if (sessions.Count == 0) break;
            foreach (var session in sessions)
            {
                var source = SqliteCostSessionSourcePartitionResolverV1.Resolve(
                    connection,
                    transaction,
                    session.SessionId);
                if (source.State != CostSessionSourcePartitionStateV1.Resolved
                    || configuration.SourceEntries.Count(entry =>
                        entry.SourceSurface == source.SourceSurface
                        && entry.ApplicationVersion == source.SourceApplicationVersion) != 1)
                    continue;
                var member = BuildAnalyticsMember(
                    connection,
                    transaction,
                    session,
                    source,
                    currentCatalog);
                if (SqliteCostAnalyticsProjectorV1.Matches(member, query))
                    members.Add(member);
                if (members.Count == 2_001) break;
            }
            lastSeen = FormatAnalyticsTimestamp(sessions[^1].EffectiveAtUtc);
            lastSessionId = sessions[^1].SessionId;
        }
        return Array.AsReadOnly(members.ToArray());
    }

    private static CostAnalyticsMemberV1 BuildAnalyticsMember(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalyticsSessionRow session,
        CostSessionSourcePartitionResultV1 source,
        PricingCatalog currentCatalog)
    {
        var activeHead = ReadActiveHead(connection, transaction, session.SessionId);
        var latest = ReadLatestAttempt(
            connection,
            transaction,
            session.SessionId,
            currentCatalog);
        var attemptRevision = latest?.AttemptRevision ?? 0;
        if (activeHead is null)
        {
            var noHeadState = latest is null
                ? "missing"
                : latest.Freshness == "stale"
                    ? "stale"
                    : latest.Kind switch
                    {
                        "unavailable" => "unavailable",
                        "failed" => "failed",
                        "estimate" => latest.EstimateStatus == "not-estimable"
                            ? "not_estimable"
                            : latest.EstimateStatus!,
                        _ => "missing",
                    };
            return new(
                session.SessionId,
                session.Status,
                session.EffectiveAtUtc,
                session.UpdatedAtUtc,
                "resolved",
                source.ObservationCount,
                source.Digest,
                source.SourceSurface!,
                source.SourceApplicationVersion!,
                SafeAnalyticsLabel(session.Repository),
                SafeAnalyticsLabel(session.Workspace),
                noHeadState,
                null,
                null,
                attemptRevision,
                ReadAttemptFreshnessIdentity(
                    connection,
                    transaction,
                    session,
                    source,
                    latest),
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                []);
        }

        var item = BuildEstimateItem(
            connection,
            transaction,
            session.SessionId,
            activeHead.HeadRevision,
            activeHead.EstimateId,
            currentCatalog);
        ValidateComponentTotal(item);
        var fresh = item.Freshness == "fresh";
        var state = fresh
            ? item.EstimateStatus == "not-estimable"
                ? "not_estimable"
                : item.EstimateStatus
            : "stale";
        return new(
            session.SessionId,
            session.Status,
            session.EffectiveAtUtc,
            session.UpdatedAtUtc,
            "resolved",
            source.ObservationCount,
            source.Digest,
            source.SourceSurface!,
            source.SourceApplicationVersion!,
            SafeAnalyticsLabel(session.Repository),
            SafeAnalyticsLabel(session.Workspace),
            state,
            item.HeadRevision,
            item.EstimateId,
            attemptRevision,
            ReadCurrentPricingSemanticSignature(
                connection,
                transaction,
                item.EstimateId,
                currentCatalog),
            fresh ? item.Provider : null,
            fresh ? item.Model : null,
            fresh ? item.BillingMode : null,
            fresh ? item.Registry?.RegistryVersion : null,
            fresh ? item.Currency : null,
            fresh ? item.Amount : null,
            Array.AsReadOnly(item.Components.Select(component =>
                new CostAnalyticsComponentV1(
                    component.Category,
                    component.Amount,
                    component.MissingReason)).ToArray()),
            Array.AsReadOnly(item.Reasons.ToArray()));
    }

    private static void ValidateComponentTotal(CostSessionEstimateItemReadV1 item)
    {
        if (item.Freshness != "fresh" || item.EstimateStatus == "not-estimable") return;
        var amount = 0m;
        foreach (var component in item.Components)
            if (component.Amount is not null)
                amount = checked(amount + component.Amount.Value);
        if (item.Amount != amount)
            throw new InvalidOperationException("Pricing estimate component sum is invalid.");
    }

    internal static string? SafeAnalyticsLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value is "." or ".."
            || value.Any(char.IsControl)
            || value.Contains("://", StringComparison.Ordinal)
            || value.Contains('/')
            || value.Contains('\\')
            || AnalyticsEmail.IsMatch(value)
            || AnalyticsCredential.IsMatch(value)
            || Path.IsPathRooted(value))
            return null;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index]))
                    return null;
            }
            else if (char.IsLowSurrogate(value[index]))
                return null;
        }
        return value;
    }

    private static string ReadCurrentPricingSemanticSignature(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string estimateId,
        PricingCatalog currentCatalog)
    {
        byte[] estimateBytes;
        byte[] exactCatalogBytes;
        using (var command = Command(
            connection,
            transaction,
            """
            SELECT e.canonical_blob,c.canonical_blob
            FROM pricing_estimates e
            JOIN pricing_catalog_snapshots c ON c.catalog_sha256=e.catalog_sha256
            WHERE e.estimate_id=$estimate;
            """,
            ("$estimate", estimateId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("Analytics estimate freshness source is missing.");
            estimateBytes = ((byte[])reader[0]).ToArray();
            exactCatalogBytes = ((byte[])reader[1]).ToArray();
            if (reader.Read())
                throw new InvalidOperationException("Analytics estimate freshness source is ambiguous.");
        }
        var exactCatalog = PricingCatalogSnapshotConsumer.Deserialize(exactCatalogBytes);
        var original = PricingEstimateConsumer.Deserialize(estimateBytes, exactCatalog);
        var current = new PricingEstimationEngine(currentCatalog).Estimate(new(
            PricingContractVersions.EstimateRequest,
            original.CalculationTimeUtc,
            original.SupersedesEstimateId,
            original.Source,
            original.Usage));
        return PricingSelectionSemanticSignature(current);
    }

    private static string ReadAttemptFreshnessIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnalyticsSessionRow session,
        CostSessionSourcePartitionResultV1 currentSource,
        CostSessionLatestAttemptReadV1? attempt)
    {
        using var stream = new MemoryStream();
        Frame(stream, "cost-attempt-input-freshness/v1");
        Frame(stream, attempt?.RunId);
        Frame(stream, attempt?.Kind ?? "missing");
        Frame(stream, attempt?.Code);
        Frame(stream, attempt?.Freshness);
        Frame(stream, session.SessionId);
        Frame(stream, session.Status);
        Frame(stream, FormatAnalyticsTimestamp(session.EffectiveAtUtc));
        Frame(stream, FormatAnalyticsTimestamp(session.UpdatedAtUtc));
        Frame(stream, Wire(currentSource.State));
        Frame(
            stream,
            currentSource.ObservationCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        Frame(stream, currentSource.Digest);
        Frame(stream, currentSource.SourceSurface);
        Frame(stream, currentSource.SourceApplicationVersion);
        if (attempt is not null)
        {
            using var command = Command(
                connection,
                transaction,
                """
                SELECT r.configuration_id,r.configuration_head_revision,r.catalog_sha256,
                    t.session_status,t.session_effective_at_utc,t.session_updated_at_utc,
                    t.source_partition_state,t.source_partition_count,
                    t.source_partition_digest,t.source_surface,
                    t.source_application_version
                FROM pricing_recalculation_runs r
                JOIN pricing_recalculation_targets t ON t.run_id=r.run_id
                WHERE r.run_id=$run AND t.session_id=$session;
                """,
                ("$run", attempt.RunId),
                ("$session", session.SessionId));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("Analytics attempt freshness source is missing.");
            var capturedConfigurationId = reader.GetString(0);
            var capturedState = reader.GetString(6);
            var capturedSurface = reader.IsDBNull(9) ? null : reader.GetString(9);
            var capturedVersion = reader.IsDBNull(10) ? null : reader.GetString(10);
            Frame(stream, capturedConfigurationId);
            for (var ordinal = 1; ordinal <= 10; ordinal++)
                Frame(stream, reader.IsDBNull(ordinal)
                    ? null
                    : Convert.ToString(
                        reader.GetValue(ordinal),
                        System.Globalization.CultureInfo.InvariantCulture));
            if (reader.Read())
                throw new InvalidOperationException("Analytics attempt freshness source is ambiguous.");
            reader.Close();

            var capturedSelection = ReadSourceSelection(
                connection,
                transaction,
                capturedConfigurationId,
                capturedState,
                capturedSurface,
                capturedVersion);
            using var currentHead = Command(
                connection,
                transaction,
                """
                SELECT configuration_id,head_revision
                FROM pricing_configuration_heads
                ORDER BY head_revision DESC LIMIT 1;
                """);
            using var headReader = currentHead.ExecuteReader();
            string? currentConfigurationId = null;
            long? currentHeadRevision = null;
            if (headReader.Read())
            {
                currentConfigurationId = headReader.GetString(0);
                currentHeadRevision = headReader.GetInt64(1);
            }
            headReader.Close();
            Frame(stream, currentConfigurationId);
            Frame(
                stream,
                currentHeadRevision?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            var currentSelection = currentConfigurationId is null
                ? null
                : ReadSourceSelection(
                    connection,
                    transaction,
                    currentConfigurationId,
                    Wire(currentSource.State),
                    currentSource.SourceSurface,
                    currentSource.SourceApplicationVersion);
            FrameSelection(stream, capturedSelection);
            FrameSelection(stream, currentSelection);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void FrameSelection(Stream stream, SourceSelection? selection)
    {
        Frame(stream, selection?.State);
        Frame(stream, selection?.Surface);
        Frame(stream, selection?.Version);
        Frame(stream, selection?.Capability);
        Frame(stream, selection?.Provider);
        Frame(stream, selection?.BillingMode);
        Frame(stream, selection?.PricingRoute);
    }

    private static string FormatAnalyticsTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
            System.Globalization.CultureInfo.InvariantCulture);

    private sealed record AnalyticsSessionRow(
        string SessionId,
        string Status,
        DateTimeOffset EffectiveAtUtc,
        DateTimeOffset UpdatedAtUtc,
        string? Repository,
        string? Workspace);
}
