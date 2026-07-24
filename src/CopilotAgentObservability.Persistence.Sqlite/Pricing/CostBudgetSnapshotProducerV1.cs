using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite.Costs;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

internal sealed record CostBudgetEligibleMemberV1(
    AlertCostMemberV2 Member,
    string SourcePartitionDigest);

internal sealed record CostBudgetEligibilityContextV1(
    string ConfigurationId,
    long ConfigurationHeadRevision,
    string CatalogSha256);

internal static class CostBudgetSnapshotProducerV1
{
    internal static AlertNormalizedSnapshotV2 Create(
        CostBudgetScopeV1 requestedScope,
        IReadOnlyList<CostBudgetEligibleMemberV1> eligibleMembers,
        CostBudgetEligibilityContextV1 context)
    {
        ArgumentNullException.ThrowIfNull(requestedScope);
        ArgumentNullException.ThrowIfNull(eligibleMembers);
        ArgumentNullException.ThrowIfNull(context);
        if (eligibleMembers.Count > 2_000)
            return CreateIncomplete(
                requestedScope,
                EligibilityDigest(requestedScope, eligibleMembers.Take(2_001).ToArray(), context));

        var (kind, start, end, sessionId) = Scope(requestedScope);
        var selectedFacts = eligibleMembers
            .Where(fact => Includes(fact.Member, kind, start, end, sessionId))
            .OrderBy(fact => fact.Member.SessionEffectiveAtUtc)
            .ThenBy(fact => fact.Member.SessionId, StringComparer.Ordinal)
            .ToArray();
        var selected = selectedFacts.Select(fact => fact.Member with { }).ToArray();
        if (kind == AlertCostScopeKindV2.Session && selected.Length != 1)
            throw new ArgumentException("Session budget scope is not eligible.", nameof(requestedScope));

        var digest = EligibilityDigest(requestedScope, selectedFacts, context);
        var scope = new AlertCostScopeV2(
            AlertCostScopeIdentityV2.Create(
                kind,
                start,
                end,
                digest,
                selected.Select(member => member.SessionId)),
            kind,
            start,
            end,
            Array.AsReadOnly(selected.Select(member => member.SessionId).ToArray()));
        var evidence = selected
            .Select(member => new AlertEvidenceReferenceV2(
                AlertEvidenceKindV2.Session,
                member.SessionId,
                member.SessionId,
                member.SessionEffectiveAtUtc))
            .Concat(selected
                .Where(member => member.EstimateId is not null)
                .Select(member => new AlertEvidenceReferenceV2(
                    AlertEvidenceKindV2.PricingEstimate,
                    member.EstimateId!,
                    member.SessionId,
                    member.EstimateCalculationTimeUtc!.Value)))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.SessionId, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ThenBy(item => item.ObservedAtUtc)
            .ToArray();

        var estimatedCount = selected.LongCount(member =>
            member.State == AlertCostMemberStateV2.Estimated);
        decimal? amount = null;
        var aggregate = AlertCostAggregateStateV2.NotApplicable;
        if (estimatedCount > 0)
        {
            try
            {
                amount = selected
                    .Where(member => member.State == AlertCostMemberStateV2.Estimated)
                    .Select(member => member.Amount!.Value)
                    .Aggregate(0m, checked((sum, value) => sum + value));
                aggregate = AlertCostAggregateStateV2.Available;
            }
            catch (OverflowException)
            {
                aggregate = AlertCostAggregateStateV2.Unrepresentable;
            }
        }

        var denominator = (long)selected.Length;
        var snapshot = new AlertNormalizedSnapshotV2(
            AlertContractVersionsV2.Snapshot,
            "estimated_cost",
            "local-monitor-cost-analytics",
            "1",
            AlertCostAcquisitionStateV2.Complete,
            [],
            aggregate,
            digest,
            denominator,
            null,
            scope,
            estimatedCount > 0 ? "USD" : null,
            amount,
            estimatedCount,
            Count(selected, AlertCostMemberStateV2.Partial),
            Count(selected, AlertCostMemberStateV2.NotEstimable),
            Count(selected, AlertCostMemberStateV2.Missing),
            Count(selected, AlertCostMemberStateV2.Failed),
            Count(selected, AlertCostMemberStateV2.Unavailable),
            Count(selected, AlertCostMemberStateV2.Stale),
            estimatedCount,
            denominator,
            denominator == 0
                ? null
                : checked((int)(estimatedCount * 10_000 / denominator)),
            Array.AsReadOnly(selected),
            Array.AsReadOnly(evidence),
            AlertCostCompletenessV2.Full,
            [],
            selected.Length == 0 ? null : selected[0].SessionEffectiveAtUtc,
            selected.Length == 0 ? null : selected[^1].SessionEffectiveAtUtc);
        _ = AlertCanonicalJsonV2.SerializeSnapshot(snapshot);
        return snapshot;
    }

    internal static AlertNormalizedSnapshotV2 CreateIncomplete(
        CostBudgetScopeV1 requestedScope,
        string eligibilityDigest)
    {
        ArgumentNullException.ThrowIfNull(requestedScope);
        var (kind, start, end, _) = Scope(requestedScope);
        if (kind == AlertCostScopeKindV2.Session)
            throw new ArgumentException("A Session scope cannot be incomplete.", nameof(requestedScope));
        var scope = new AlertCostScopeV2(
            AlertCostScopeIdentityV2.Create(kind, start, end, eligibilityDigest, []),
            kind,
            start,
            end,
            []);
        var snapshot = new AlertNormalizedSnapshotV2(
            AlertContractVersionsV2.Snapshot,
            "estimated_cost",
            "local-monitor-cost-analytics",
            "1",
            AlertCostAcquisitionStateV2.Incomplete,
            ["eligible_set_incomplete"],
            AlertCostAggregateStateV2.NotApplicable,
            eligibilityDigest,
            null,
            2_001,
            scope,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            [],
            AlertCostCompletenessV2.Partial,
            ["eligible_set_incomplete"],
            null,
            null);
        _ = AlertCanonicalJsonV2.SerializeSnapshot(snapshot);
        return snapshot;
    }

    internal static string EligibilityDigest(
        CostBudgetScopeV1 requestedScope,
        IReadOnlyList<CostBudgetEligibleMemberV1> members,
        CostBudgetEligibilityContextV1 context)
    {
        var (kind, start, end, _) = Scope(requestedScope);
        var values = new List<byte[]>
        {
            Encoding.UTF8.GetBytes("alert-cost-eligibility/v2"),
            Encoding.UTF8.GetBytes(ScopeKind(kind)),
            Encoding.UTF8.GetBytes(start is null ? "\0" : Timestamp(start.Value)),
            Encoding.UTF8.GetBytes(end is null ? "\0" : Timestamp(end.Value)),
            Encoding.UTF8.GetBytes("13"),
            Encoding.UTF8.GetBytes("1"),
            Encoding.UTF8.GetBytes("2"),
            Encoding.UTF8.GetBytes(context.ConfigurationId),
            Encoding.UTF8.GetBytes(context.ConfigurationHeadRevision.ToString(CultureInfo.InvariantCulture)),
            Encoding.UTF8.GetBytes(context.CatalogSha256),
            Encoding.UTF8.GetBytes(
                (members.Count > 2_000 ? 2_001 : members.Count)
                .ToString(CultureInfo.InvariantCulture)),
        };
        foreach (var fact in members.Take(2_001))
        {
            var member = fact.Member;
            values.Add(Encoding.UTF8.GetBytes(member.SessionId));
            values.Add(Encoding.UTF8.GetBytes(Timestamp(member.SessionEffectiveAtUtc)));
            values.Add(Encoding.UTF8.GetBytes(Timestamp(member.SessionUpdatedAtUtc)));
            values.Add(Encoding.UTF8.GetBytes(member.SourceSurface));
            values.Add(Encoding.UTF8.GetBytes(member.SourceApplicationVersion));
            values.Add(Encoding.UTF8.GetBytes(fact.SourcePartitionDigest));
            values.Add(Encoding.UTF8.GetBytes(member.HeadRevision?.ToString(CultureInfo.InvariantCulture) ?? "\0"));
            values.Add(Encoding.UTF8.GetBytes(member.EstimateId ?? "\0"));
            values.Add(Encoding.UTF8.GetBytes(member.AttemptRevision.ToString(CultureInfo.InvariantCulture)));
            values.Add(Encoding.UTF8.GetBytes(AttemptKind(member.AttemptResultKind)));
            values.Add(Encoding.UTF8.GetBytes(member.AttemptResultCode ?? "\0"));
        }
        return Hash(values);
    }

    private static string Hash(IEnumerable<byte[]> values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            stream.Write(length);
            stream.Write(value);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static (
        AlertCostScopeKindV2 Kind,
        DateTimeOffset? Start,
        DateTimeOffset? End,
        string? SessionId) Scope(CostBudgetScopeV1 scope) =>
        scope.ScopeKind switch
        {
            "session" => (AlertCostScopeKindV2.Session, null, null, scope.SessionId),
            "utc_day" => Day(scope.UtcDate!),
            "rolling_period" => (
                AlertCostScopeKindV2.RollingPeriod,
                scope.CutoffUtc!.Value.AddDays(-scope.WindowDays!.Value),
                scope.CutoffUtc.Value,
                null),
            _ => throw new ArgumentException("Cost budget scope is invalid.", nameof(scope)),
        };

    private static (
        AlertCostScopeKindV2 Kind,
        DateTimeOffset? Start,
        DateTimeOffset? End,
        string? SessionId) Day(string value)
    {
        var date = DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var start = new DateTimeOffset(
            date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        return (AlertCostScopeKindV2.UtcDay, start, start.AddDays(1), null);
    }

    private static bool Includes(
        AlertCostMemberV2 member,
        AlertCostScopeKindV2 kind,
        DateTimeOffset? start,
        DateTimeOffset? end,
        string? sessionId) =>
        kind == AlertCostScopeKindV2.Session
            ? member.SessionId == sessionId
            : member.SessionEffectiveAtUtc >= start
                && member.SessionEffectiveAtUtc < end;

    private static long Count(
        IEnumerable<AlertCostMemberV2> members,
        AlertCostMemberStateV2 state) =>
        members.LongCount(member => member.State == state);

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture);

    private static string ScopeKind(AlertCostScopeKindV2 kind) => kind switch
    {
        AlertCostScopeKindV2.Session => "session",
        AlertCostScopeKindV2.UtcDay => "utc_day",
        AlertCostScopeKindV2.RollingPeriod => "rolling_period",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string AttemptKind(AlertCostAttemptResultKindV2? kind) => kind switch
    {
        null => "\0",
        AlertCostAttemptResultKindV2.Estimate => "estimate",
        AlertCostAttemptResultKindV2.Unavailable => "unavailable",
        AlertCostAttemptResultKindV2.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
