using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

public enum PricingReadStatus
{
    Success,
    NotFound,
    InvalidQuery,
    InvalidCursor,
    CatalogChanged,
    SnapshotChanged,
    ResponseTooLarge,
    Busy,
    Unavailable,
}

public sealed record PricingReadResult<T>(PricingReadStatus Status, T? Value = default);

public sealed record CostConfigurationReadV1(
    long HeadRevision,
    string? ConfigurationId,
    string? ConfigurationCatalogSha256,
    string ProviderCatalogSha256,
    string CatalogState,
    CostConfigurationV1? Configuration,
    int SelectedSessionCount,
    string SelectedSessionCountState);

public sealed record CostConfigurationVersionReadV1(
    long HeadRevision,
    string ConfigurationId,
    string CatalogSha256,
    DateTimeOffset CommittedAtUtc,
    CostConfigurationV1 Configuration);

public sealed record CostRecalculationTargetResultReadV1(
    string Kind,
    string? Status,
    string? EstimateId,
    string? Code);

public sealed record CostRecalculationTargetReadV1(
    int TargetOrdinal,
    string SessionId,
    long? BaseHeadRevision,
    string? BaseEstimateId,
    CostRecalculationTargetResultReadV1? Result);

public sealed record CostRecalculationEventReadV1(
    int EventSequence,
    string State,
    DateTimeOffset OccurredAtUtc,
    string? FailureCode);

public sealed record CostRecalculationBudgetResultReadV1(
    int ScopeOrdinal,
    CostBudgetScopeV1 Scope,
    string RuleId,
    string RuleVersion,
    string OutcomeKind,
    string EvaluationId,
    string? AlertId,
    int? SuppressionOrdinal,
    string? Code);

public sealed record CostRecalculationReadV1(
    string RunId,
    string RequestDigest,
    string State,
    int TargetCount,
    int ScopeCount,
    IReadOnlyList<CostRecalculationTargetReadV1> Targets,
    IReadOnlyList<CostRecalculationEventReadV1> Events,
    IReadOnlyList<CostRecalculationBudgetResultReadV1> BudgetResults,
    string? FailureCode);

public sealed record CostSessionRecalculationAttemptReadV1(
    long AttemptRevision,
    string RunId,
    DateTimeOffset CalculationTimeUtc,
    string Freshness,
    string Kind,
    string? EstimateStatus,
    string? EstimateId,
    string? Code);

public sealed record CostSessionActiveRecalculationReadV1(
    long AttemptRevision,
    string RunId,
    DateTimeOffset CalculationTimeUtc,
    string Freshness,
    string State);

public sealed record CostSessionRecalculationsReadV1(
    string SessionId,
    CostSessionActiveRecalculationReadV1? Active,
    IReadOnlyList<CostSessionRecalculationAttemptReadV1> Attempts,
    long? NextAfter);

public sealed partial class SqlitePricingReadStore
{
    private readonly string databasePath;

    public SqlitePricingReadStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
    }

    public PricingReadResult<CostConfigurationReadV1> ReadCurrentConfiguration(
        string providerCatalogSha256)
    {
        if (!LowerSha(providerCatalogSha256))
            return new(PricingReadStatus.Unavailable);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback<CostConfigurationReadV1>(transaction, PricingReadStatus.Unavailable);

            using var head = Command(
                connection,
                transaction,
                """
                SELECT h.head_revision,h.configuration_id,c.catalog_sha256,c.canonical_blob
                FROM pricing_configuration_heads h
                JOIN pricing_configurations c ON c.configuration_id=h.configuration_id
                ORDER BY h.head_revision DESC LIMIT 1;
                """);
            using var reader = head.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Commit();
                return new(
                    PricingReadStatus.Success,
                    new(
                        0,
                        null,
                        null,
                        providerCatalogSha256,
                        "unconfigured",
                        null,
                        0,
                        "exact"));
            }

            var headRevision = reader.GetInt64(0);
            var configurationId = reader.GetString(1);
            var configurationCatalogSha = reader.GetString(2);
            var canonical = ((byte[])reader[3]).ToArray();
            reader.Close();
            var consumed = CostConfigurationConsumerV1.Consume(canonical);
            if (consumed.Status != CostConsumerStatus.Success
                || consumed.Value is null
                || consumed.Value.ConfigurationId != configurationId
                || consumed.Value.CatalogSha256 != configurationCatalogSha)
                return Rollback<CostConfigurationReadV1>(transaction, PricingReadStatus.Unavailable);

            var selected = ReadConfiguredSessions(
                connection,
                transaction,
                consumed.Value,
                2_001);
            if (selected is null)
                return Rollback<CostConfigurationReadV1>(transaction, PricingReadStatus.Unavailable);
            var state = selected.Count <= 2_000 ? "exact" : "lower_bound";
            transaction.Commit();
            return new(
                PricingReadStatus.Success,
                new(
                    headRevision,
                    configurationId,
                    configurationCatalogSha,
                    providerCatalogSha256,
                    configurationCatalogSha == providerCatalogSha256 ? "matching" : "changed",
                    consumed.Value,
                    selected.Count,
                    state));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingReadStatus.Busy);
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or FormatException
                or ArgumentException)
        {
            return new(PricingReadStatus.Unavailable);
        }
    }

    public PricingReadResult<CostRecalculationReadV1> ReadRecalculation(string runId)
    {
        if (!CanonicalGuid(runId)) return new(PricingReadStatus.NotFound);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback<CostRecalculationReadV1>(transaction, PricingReadStatus.Unavailable);

            string requestDigest;
            byte[] requestBytes;
            int targetCount;
            int scopeCount;
            using (var run = Command(
                connection,
                transaction,
                """
                SELECT request_digest,canonical_request_blob,target_count,scope_count
                FROM pricing_recalculation_runs WHERE run_id=$run;
                """,
                ("$run", runId)))
            using (var reader = run.ExecuteReader())
            {
                if (!reader.Read())
                    return Rollback<CostRecalculationReadV1>(transaction, PricingReadStatus.NotFound);
                requestDigest = reader.GetString(0);
                requestBytes = ((byte[])reader[1]).ToArray();
                targetCount = reader.GetInt32(2);
                scopeCount = reader.GetInt32(3);
            }
            var request = CostRecalculationRequestCanonicalJsonV1.Consume(requestBytes);
            if (request.Status != CostConsumerStatus.Success
                || request.Value is null
                || requestDigest != CostIdentityV1.Hash(
                    "cost-recalculation-request/v1",
                    requestBytes)
                || request.Value.SessionIds.Count != targetCount
                || request.Value.BudgetScopes.Count != scopeCount)
                return Rollback<CostRecalculationReadV1>(transaction, PricingReadStatus.Unavailable);

            var targets = ReadTargets(connection, transaction, runId);
            var events = ReadEvents(connection, transaction, runId);
            var budgets = ReadBudgetResults(
                connection,
                transaction,
                runId,
                request.Value.BudgetScopes);
            if (budgets is null
                || targets.Count != targetCount
                || events.Count == 0
                || !targets.Select(item => item.TargetOrdinal).SequenceEqual(Enumerable.Range(0, targetCount))
                || !events.Select(item => item.EventSequence).SequenceEqual(Enumerable.Range(0, events.Count)))
                return Rollback<CostRecalculationReadV1>(transaction, PricingReadStatus.Unavailable);

            var state = events[^1].State;
            var failureCode = state == "failed" ? events[^1].FailureCode : null;
            var terminal = state is "succeeded" or "failed";
            if (terminal != targets.All(item => item.Result is not null)
                || (!terminal && budgets.Count != 0)
                || (state == "succeeded" && budgets.Count != scopeCount)
                || (state == "failed" && (failureCode is null || budgets.Count != 0))
                || (state != "failed" && failureCode is not null))
                return Rollback<CostRecalculationReadV1>(transaction, PricingReadStatus.Unavailable);

            transaction.Commit();
            return new(
                PricingReadStatus.Success,
                new(
                    runId,
                    requestDigest,
                    state,
                    targetCount,
                    scopeCount,
                    Array.AsReadOnly(targets.ToArray()),
                    Array.AsReadOnly(events.ToArray()),
                    Array.AsReadOnly(budgets.ToArray()),
                    failureCode));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingReadStatus.Busy);
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or FormatException
                or ArgumentException)
        {
            return new(PricingReadStatus.Unavailable);
        }
    }

    public PricingReadResult<CostConfigurationVersionReadV1> ReadConfigurationVersion(
        string configurationId)
    {
        if (!PrefixedSha(configurationId, "cost-configuration-"))
            return new(PricingReadStatus.NotFound);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback<CostConfigurationVersionReadV1>(
                    transaction,
                    PricingReadStatus.Unavailable);
            using var command = Command(
                connection,
                transaction,
                """
                SELECT h.head_revision,c.catalog_sha256,h.committed_at_utc,
                    c.canonical_blob,k.canonical_result_blob
                FROM pricing_configurations c
                JOIN pricing_configuration_heads h ON h.configuration_id=c.configuration_id
                JOIN pricing_configuration_commits k
                  ON k.head_revision=h.head_revision AND k.configuration_id=h.configuration_id
                WHERE c.configuration_id=$id;
                """,
                ("$id", configurationId));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return Rollback<CostConfigurationVersionReadV1>(
                    transaction,
                    PricingReadStatus.NotFound);
            var headRevision = reader.GetInt64(0);
            var catalogSha = reader.GetString(1);
            var committedAt = Parse(reader.GetString(2));
            var configurationBytes = ((byte[])reader[3]).ToArray();
            var resultBytes = ((byte[])reader[4]).ToArray();
            reader.Close();
            var configuration = CostConfigurationConsumerV1.Consume(configurationBytes);
            var result = CostConfigurationCommitConsumerV1.ConsumeResult(resultBytes);
            if (configuration.Status != CostConsumerStatus.Success
                || configuration.Value is null
                || result.Status != CostConsumerStatus.Success
                || result.Value is null
                || configuration.Value.ConfigurationId != configurationId
                || configuration.Value.CatalogSha256 != catalogSha
                || result.Value.ConfigurationId != configurationId
                || result.Value.HeadRevision != headRevision
                || result.Value.CatalogSha256 != catalogSha)
                return Rollback<CostConfigurationVersionReadV1>(
                    transaction,
                    PricingReadStatus.Unavailable);
            transaction.Commit();
            return new(
                PricingReadStatus.Success,
                new(
                    headRevision,
                    configurationId,
                    catalogSha,
                    committedAt,
                    configuration.Value));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingReadStatus.Busy);
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or FormatException
                or ArgumentException)
        {
            return new(PricingReadStatus.Unavailable);
        }
    }

    public PricingReadResult<CostSessionRecalculationsReadV1> ReadSessionRecalculations(
        string sessionId,
        ReadOnlyMemory<byte> currentProviderCatalogBytes,
        long? after,
        int limit = 50)
    {
        if (!CanonicalGuid(sessionId)) return new(PricingReadStatus.NotFound);
        if (limit is < 1 or > 100 || after is <= 0)
            return new(PricingReadStatus.InvalidCursor);
        try
        {
            var currentCatalog = PricingCatalogSnapshotConsumer.Deserialize(
                currentProviderCatalogBytes.Span);
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback<CostSessionRecalculationsReadV1>(
                    transaction,
                    PricingReadStatus.Unavailable);
            if (ScalarLong(
                    connection,
                    transaction,
                    "SELECT COUNT(*) FROM sessions WHERE session_id=$session;",
                    ("$session", sessionId)) != 1)
                return Rollback<CostSessionRecalculationsReadV1>(
                    transaction,
                    PricingReadStatus.NotFound);
            if (after is not null
                && ScalarLong(
                    connection,
                    transaction,
                    """
                    SELECT COUNT(*) FROM pricing_session_attempts
                    WHERE session_id=$session AND attempt_revision=$revision;
                    """,
                    ("$session", sessionId),
                    ("$revision", after.Value)) != 1)
                return Rollback<CostSessionRecalculationsReadV1>(
                    transaction,
                    PricingReadStatus.InvalidCursor);

            var active = ReadActiveAttempt(
                connection,
                transaction,
                sessionId,
                currentCatalog);
            var attempts = ReadTerminalAttempts(
                connection,
                transaction,
                sessionId,
                currentCatalog,
                after,
                limit + 1);
            if (!AttemptsAreContiguous(connection, transaction, sessionId)
                || active is not null && active.AttemptRevision != LatestAttemptRevision(
                    connection,
                    transaction,
                    sessionId) + 1)
                return Rollback<CostSessionRecalculationsReadV1>(
                    transaction,
                    PricingReadStatus.Unavailable);

            var hasMore = attempts.Count > limit;
            if (hasMore) attempts.RemoveAt(attempts.Count - 1);
            long? next = hasMore ? attempts[^1].AttemptRevision : null;
            transaction.Commit();
            return new(
                PricingReadStatus.Success,
                new(
                    sessionId,
                    active,
                    Array.AsReadOnly(attempts.ToArray()),
                    next));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingReadStatus.Busy);
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or FormatException
                or ArgumentException
                or PricingRegistryValidationException
                or PricingEstimateValidationException)
        {
            return new(PricingReadStatus.Unavailable);
        }
    }

    private static List<string>? ReadConfiguredSessions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CostConfigurationV1 configuration,
        int maximum)
    {
        var values = new List<string>();
        string? lastSeen = null;
        string? lastSessionId = null;
        while (values.Count < maximum)
        {
            using var command = Command(
                connection,
                transaction,
                """
                SELECT session_id,last_seen_at FROM sessions
                WHERE status IN ('completed','failed')
                  AND ($last_seen IS NULL OR last_seen_at>$last_seen
                    OR (last_seen_at=$last_seen AND session_id>$last_session))
                ORDER BY last_seen_at,session_id LIMIT 256;
                """,
                ("$last_seen", (object?)lastSeen ?? DBNull.Value),
                ("$last_session", (object?)lastSessionId ?? DBNull.Value));
            using var reader = command.ExecuteReader();
            var candidates = new List<(string SessionId, string LastSeen)>(256);
            while (reader.Read())
                candidates.Add((reader.GetString(0), reader.GetString(1)));
            reader.Close();
            if (candidates.Count == 0) break;
            foreach (var candidate in candidates)
            {
                var source = SqliteCostSessionSourcePartitionResolverV1.Resolve(
                    connection,
                    transaction,
                    candidate.SessionId);
                if (source.State == CostSessionSourcePartitionStateV1.Resolved
                    && configuration.SourceEntries.Count(item =>
                        item.SourceSurface == source.SourceSurface
                        && item.ApplicationVersion == source.SourceApplicationVersion) == 1)
                    values.Add(candidate.SessionId);
                if (values.Count == maximum) break;
            }
            lastSeen = candidates[^1].LastSeen;
            lastSessionId = candidates[^1].SessionId;
        }
        return values;
    }

    private static List<CostRecalculationTargetReadV1> ReadTargets(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT t.target_ordinal,t.session_id,t.base_head_revision,t.base_estimate_id,
                r.result_kind,r.estimate_status,r.estimate_id,r.result_code
            FROM pricing_recalculation_targets t
            LEFT JOIN pricing_recalculation_target_results r
                ON r.run_id=t.run_id AND r.target_ordinal=t.target_ordinal
            WHERE t.run_id=$run ORDER BY t.target_ordinal;
            """,
            ("$run", runId));
        using var reader = command.ExecuteReader();
        var values = new List<CostRecalculationTargetReadV1>();
        while (reader.Read())
        {
            CostRecalculationTargetResultReadV1? result = null;
            if (!reader.IsDBNull(4))
                result = new(
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7));
            values.Add(new(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                result));
        }
        return values;
    }

    private static List<CostRecalculationEventReadV1> ReadEvents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT event_sequence,event_kind,occurred_at_utc,failure_code
            FROM pricing_recalculation_events
            WHERE run_id=$run ORDER BY event_sequence;
            """,
            ("$run", runId));
        using var reader = command.ExecuteReader();
        var values = new List<CostRecalculationEventReadV1>();
        while (reader.Read())
            values.Add(new(
                reader.GetInt32(0),
                reader.GetString(1),
                Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return values;
    }

    private static List<CostRecalculationBudgetResultReadV1>? ReadBudgetResults(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        IReadOnlyList<CostBudgetScopeV1> requestedScopes)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT scope_ordinal,scope_kind,rule_id,rule_version,outcome_kind,
                evaluation_id,alert_id,suppression_ordinal,suppression_code
            FROM pricing_recalculation_budget_results
            WHERE run_id=$run ORDER BY scope_ordinal;
            """,
            ("$run", runId));
        using var reader = command.ExecuteReader();
        var values = new List<CostRecalculationBudgetResultReadV1>();
        while (reader.Read())
        {
            var ordinal = reader.GetInt32(0);
            if (ordinal < 0
                || ordinal >= requestedScopes.Count
                || reader.GetString(1) != requestedScopes[ordinal].ScopeKind)
                return null;
            values.Add(new(
                ordinal,
                requestedScopes[ordinal] with { },
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return values;
    }

    private static CostSessionActiveRecalculationReadV1? ReadActiveAttempt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        PricingCatalog currentCatalog)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT t.base_attempt_revision+1,t.run_id,r.calculation_time_utc,e.event_kind
            FROM pricing_recalculation_targets t
            JOIN pricing_recalculation_runs r ON r.run_id=t.run_id
            JOIN pricing_recalculation_events e ON e.run_id=t.run_id
            WHERE t.session_id=$session
              AND e.event_sequence=(SELECT MAX(x.event_sequence)
                  FROM pricing_recalculation_events x WHERE x.run_id=t.run_id)
              AND e.event_kind IN ('requested','running');
            """,
            ("$session", sessionId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var runId = reader.GetString(1);
        var value = new CostSessionActiveRecalculationReadV1(
            reader.GetInt64(0),
            runId,
            Parse(reader.GetString(2)),
            "pending",
            reader.GetString(3));
        if (reader.Read()) throw new InvalidOperationException("Multiple active recalculations exist.");
        reader.Close();
        return value with
        {
            Freshness = IsActiveAttemptFresh(
                connection,
                transaction,
                sessionId,
                runId,
                currentCatalog)
                ? "fresh"
                : "stale",
        };
    }

    private static List<CostSessionRecalculationAttemptReadV1> ReadTerminalAttempts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        PricingCatalog currentCatalog,
        long? after,
        int limit)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT a.attempt_revision,a.run_id,r.calculation_time_utc,a.result_kind,
                a.estimate_status,a.estimate_id,a.result_code
            FROM pricing_session_attempts a
            JOIN pricing_recalculation_runs r ON r.run_id=a.run_id
            WHERE a.session_id=$session
              AND ($after IS NULL OR a.attempt_revision<$after)
            ORDER BY a.attempt_revision DESC LIMIT $limit;
            """,
            ("$session", sessionId),
            ("$after", (object?)after ?? DBNull.Value),
            ("$limit", limit));
        using var reader = command.ExecuteReader();
        var rows = new List<(
            long Revision,
            string RunId,
            DateTimeOffset Time,
            string Kind,
            string? Status,
            string? EstimateId,
            string? Code)>();
        while (reader.Read())
            rows.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        reader.Close();
        return rows.Select(row => new CostSessionRecalculationAttemptReadV1(
            row.Revision,
            row.RunId,
            row.Time,
            IsTerminalAttemptFresh(
                connection,
                transaction,
                sessionId,
                row.RunId,
                row.Kind,
                row.EstimateId,
                currentCatalog) ? "fresh" : "stale",
            row.Kind,
            row.Status,
            row.EstimateId,
            row.Code)).ToList();
    }

    private static bool IsActiveAttemptFresh(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string runId,
        PricingCatalog currentCatalog)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT r.configuration_id,r.configuration_head_revision,r.catalog_sha256,
                h.configuration_id,h.head_revision
            FROM pricing_recalculation_runs r
            LEFT JOIN pricing_configuration_heads h
                ON h.head_revision=(SELECT MAX(head_revision) FROM pricing_configuration_heads)
            WHERE r.run_id=$run;
            """,
            ("$run", runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        var matchesCas = !reader.IsDBNull(3)
            && reader.GetString(0) == reader.GetString(3)
            && reader.GetInt64(1) == reader.GetInt64(4)
            && reader.GetString(2) == currentCatalog.CatalogSha256;
        reader.Close();
        return matchesCas
            && IsCapturedInputFresh(connection, transaction, sessionId, runId);
    }

    private static bool IsTerminalAttemptFresh(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string runId,
        string resultKind,
        string? estimateId,
        PricingCatalog currentCatalog)
    {
        if (!IsCapturedInputFresh(connection, transaction, sessionId, runId))
            return false;
        if (resultKind != "estimate") return true;
        return estimateId is not null
            && IsEstimateSemanticallyFresh(
                connection,
                transaction,
                estimateId,
                currentCatalog);
    }

    private static bool IsEstimateSemanticallyFresh(
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
                throw new InvalidOperationException("Estimate freshness source is missing.");
            estimateBytes = ((byte[])reader[0]).ToArray();
            exactCatalogBytes = ((byte[])reader[1]).ToArray();
            if (reader.Read())
                throw new InvalidOperationException("Estimate freshness source is ambiguous.");
        }

        var exactCatalog = PricingCatalogSnapshotConsumer.Deserialize(exactCatalogBytes);
        var original = PricingEstimateConsumer.Deserialize(estimateBytes, exactCatalog);
        var current = new PricingEstimationEngine(currentCatalog).Estimate(
            new(
                PricingContractVersions.EstimateRequest,
                original.CalculationTimeUtc,
                original.SupersedesEstimateId,
                original.Source,
                original.Usage));
        return PricingSelectionSemanticSignature(original)
            == PricingSelectionSemanticSignature(current);
    }

    internal static bool IsEstimateFreshForBudget(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string estimateId,
        PricingCatalog currentCatalog)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT run_id FROM pricing_estimates
            WHERE estimate_id=$estimate AND session_id=$session;
            """,
            ("$estimate", estimateId),
            ("$session", sessionId));
        if (command.ExecuteScalar() is not string runId)
            return false;
        return IsCapturedInputFresh(
                connection,
                transaction,
                sessionId,
                runId)
            && IsEstimateSemanticallyFresh(
                connection,
                transaction,
                estimateId,
                currentCatalog);
    }

    private static string PricingSelectionSemanticSignature(PricingEstimateRecord estimate)
    {
        using var stream = new MemoryStream();
        Frame(stream, "cost-pricing-selection-semantic/v1");
        Frame(stream, estimate.Status);
        Frame(stream, estimate.Amount?.ToString("G29", CultureInfo.InvariantCulture));
        Frame(stream, estimate.Currency);
        WriteCount(stream, estimate.Components.Count);
        foreach (var component in estimate.Components)
        {
            Frame(stream, component.Category);
            Frame(stream, component.Amount is null ? "missing" : "available");
            Frame(stream, component.Amount?.ToString("G29", CultureInfo.InvariantCulture));
            Frame(stream, component.MissingReason);
        }
        WriteCount(stream, estimate.Reasons.Count);
        foreach (var reason in estimate.Reasons) Frame(stream, reason);
        if (estimate.Registry is null)
        {
            Frame(stream, null);
        }
        else
        {
            Frame(stream, "selected");
            Frame(stream, estimate.Registry.SourceKind);
            Frame(stream, estimate.Registry.SourceId);
            Frame(stream, estimate.Registry.RegistryVersion);
            Frame(stream, estimate.Registry.EntryKey);
            Frame(stream, estimate.Registry.EffectiveFromUtc.ToString("O", CultureInfo.InvariantCulture));
            Frame(
                stream,
                estimate.Registry.EffectiveToUtc?.ToString("O", CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCount(Stream stream, int count)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, count);
        stream.Write(bytes);
    }

    private static void Frame(Stream stream, string? value)
    {
        if (value is null)
        {
            Span<byte> missingLength = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(missingLength, -1);
            stream.Write(missingLength);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static bool IsCapturedInputFresh(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string runId)
    {
        string capturedStatus;
        string capturedEffective;
        string capturedUpdated;
        string capturedState;
        int capturedCount;
        string capturedDigest;
        string? capturedSurface;
        string? capturedVersion;
        string runConfigurationId;
        using (var command = Command(
            connection,
            transaction,
            """
            SELECT t.session_status,t.session_effective_at_utc,t.session_updated_at_utc,
                t.source_partition_state,t.source_partition_count,t.source_partition_digest,
                t.source_surface,t.source_application_version,r.configuration_id
            FROM pricing_recalculation_targets t
            JOIN pricing_recalculation_runs r ON r.run_id=t.run_id
            WHERE t.run_id=$run AND t.session_id=$session;
            """,
            ("$run", runId),
            ("$session", sessionId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return false;
            capturedStatus = reader.GetString(0);
            capturedEffective = reader.GetString(1);
            capturedUpdated = reader.GetString(2);
            capturedState = reader.GetString(3);
            capturedCount = reader.GetInt32(4);
            capturedDigest = reader.GetString(5);
            capturedSurface = reader.IsDBNull(6) ? null : reader.GetString(6);
            capturedVersion = reader.IsDBNull(7) ? null : reader.GetString(7);
            runConfigurationId = reader.GetString(8);
        }

        string currentStatus;
        string currentEffective;
        string currentUpdated;
        using (var command = Command(
            connection,
            transaction,
            "SELECT status,last_seen_at,updated_at FROM sessions WHERE session_id=$session;",
            ("$session", sessionId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return false;
            currentStatus = reader.GetString(0);
            currentEffective = reader.GetString(1);
            currentUpdated = reader.GetString(2);
        }
        var currentSource = SqliteCostSessionSourcePartitionResolverV1.Resolve(
            connection,
            transaction,
            sessionId);
        if (capturedStatus != currentStatus
            || capturedEffective != currentEffective
            || capturedUpdated != currentUpdated
            || capturedState != Wire(currentSource.State)
            || capturedCount != currentSource.ObservationCount
            || capturedDigest != currentSource.Digest
            || capturedSurface != currentSource.SourceSurface
            || capturedVersion != currentSource.SourceApplicationVersion)
            return false;

        var capturedSelection = ReadSourceSelection(
            connection,
            transaction,
            runConfigurationId,
            capturedState,
            capturedSurface,
            capturedVersion);
        var currentConfigurationId = ScalarString(
            connection,
            transaction,
            """
            SELECT configuration_id FROM pricing_configuration_heads
            ORDER BY head_revision DESC LIMIT 1;
            """);
        var currentSelection = currentConfigurationId is null
            ? null
            : ReadSourceSelection(
                connection,
                transaction,
                currentConfigurationId,
                capturedState,
                capturedSurface,
                capturedVersion);
        return capturedSelection == currentSelection;
    }

    private static SourceSelection? ReadSourceSelection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string configurationId,
        string sourceState,
        string? sourceSurface,
        string? sourceVersion)
    {
        if (sourceState != "resolved") return SourceSelection.NotApplicable;
        using var command = Command(
            connection,
            transaction,
            "SELECT canonical_blob FROM pricing_configurations WHERE configuration_id=$id;",
            ("$id", configurationId));
        if (command.ExecuteScalar() is not byte[] bytes) return null;
        var consumed = CostConfigurationConsumerV1.Consume(bytes);
        if (consumed.Status != CostConsumerStatus.Success || consumed.Value is null)
            return null;
        var matches = consumed.Value.SourceEntries.Where(item =>
            item.SourceSurface == sourceSurface
            && item.ApplicationVersion == sourceVersion).ToArray();
        return matches.Length switch
        {
            0 => SourceSelection.Absent,
            1 => new(
                "present",
                matches[0].SourceSurface,
                matches[0].ApplicationVersion,
                matches[0].AdapterCapabilityVersion,
                matches[0].Provider,
                matches[0].BillingMode,
                matches[0].PricingRoute),
            _ => null,
        };
    }

    private static bool AttemptsAreContiguous(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var command = Command(
            connection,
            transaction,
            """
            SELECT attempt_revision FROM pricing_session_attempts
            WHERE session_id=$session ORDER BY attempt_revision;
            """,
            ("$session", sessionId));
        using var reader = command.ExecuteReader();
        long expected = 1;
        while (reader.Read())
            if (reader.GetInt64(0) != expected++) return false;
        return true;
    }

    private static long LatestAttemptRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId) =>
        ScalarLong(
            connection,
            transaction,
            """
            SELECT COALESCE(MAX(attempt_revision),0) FROM pricing_session_attempts
            WHERE session_id=$session;
            """,
            ("$session", sessionId));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static PricingReadResult<T> Rollback<T>(
        SqliteTransaction transaction,
        PricingReadStatus status)
    {
        transaction.Rollback();
        return new(status);
    }

    private static long ScalarLong(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? ScalarString(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteScalar() as string;
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command;
    }

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static bool CanonicalGuid(string? value) =>
        value is not null
        && Guid.TryParseExact(value, "D", out var parsed)
        && parsed.ToString("D") == value;

    private static bool LowerSha(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PrefixedSha(string? value, string prefix) =>
        value is not null
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && LowerSha(value[prefix.Length..]);

    private static string Wire(CostSessionSourcePartitionStateV1 state) =>
        state switch
        {
            CostSessionSourcePartitionStateV1.Resolved => "resolved",
            CostSessionSourcePartitionStateV1.Missing => "missing",
            CostSessionSourcePartitionStateV1.Incomplete => "incomplete",
            CostSessionSourcePartitionStateV1.Mixed => "mixed",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private sealed record SourceSelection(
        string State,
        string? Surface,
        string? Version,
        string? Capability,
        string? Provider,
        string? BillingMode,
        string? PricingRoute)
    {
        internal static SourceSelection NotApplicable { get; } =
            new("not_applicable", null, null, null, null, null, null);
        internal static SourceSelection Absent { get; } =
            new("absent", null, null, null, null, null, null);
    }
}
