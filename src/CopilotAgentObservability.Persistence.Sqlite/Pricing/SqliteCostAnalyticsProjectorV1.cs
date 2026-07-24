using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Pricing;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

public sealed record CostAnalyticsQueryV1(
    DateTimeOffset From,
    DateTimeOffset To,
    string? SourceSurface,
    string? Provider,
    string? Model,
    string? BillingMode,
    string? Status,
    string? RegistryVersion,
    string? Currency,
    string? Repository,
    string? Workspace,
    int Limit = 50,
    string? After = null);

public sealed record CostAnalyticsFiltersV1(
    string From,
    string To,
    string? SourceSurface,
    string? Provider,
    string? Model,
    string? BillingMode,
    string? Status,
    string? RegistryVersion,
    string? Currency,
    string? Repository,
    string? Workspace,
    int Limit);

public sealed record CostAnalyticsOverallV1(
    int EligibleSessionCount,
    int EstimatedSessionCount,
    int PartialSessionCount,
    int NotEstimableSessionCount,
    int MissingSessionCount,
    int FailedSessionCount,
    int UnavailableSessionCount,
    int StaleSessionCount,
    int CoverageNumerator,
    int CoverageDenominator,
    int? CoverageBasisPoints);

public sealed record CostAnalyticsPartialReasonCountV1(string Reason, int SessionCount);

public sealed record CostAnalyticsTotalV1(
    string? RegistryVersion,
    string? Currency,
    string EstimatedAmountState,
    decimal? EstimatedAmount,
    string PartialKnownComponentAmountState,
    decimal? PartialKnownComponentAmount,
    IReadOnlyList<CostAnalyticsPartialReasonCountV1> PartialReasonCounts);

public sealed record CostAnalyticsDailyTotalV1(
    DateOnly UtcDate,
    string? RegistryVersion,
    string? Currency,
    string EstimatedAmountState,
    decimal? EstimatedAmount,
    string PartialKnownComponentAmountState,
    decimal? PartialKnownComponentAmount,
    IReadOnlyList<CostAnalyticsPartialReasonCountV1> PartialReasonCounts);

public sealed record CostAnalyticsGroupV1(
    DateOnly UtcDate,
    string SourceSurface,
    string? Provider,
    string? Model,
    string? BillingMode,
    string? Repository,
    string? Workspace,
    string? RegistryVersion,
    string? Currency,
    string? ComponentCategory,
    string GroupId,
    IReadOnlyList<string> UnknownDimensions,
    int EligibleSessionCount,
    int EstimatedSessionCount,
    int PartialSessionCount,
    int NotEstimableSessionCount,
    int MissingSessionCount,
    int FailedSessionCount,
    int UnavailableSessionCount,
    int StaleSessionCount,
    int CoverageBasisPoints,
    int ComponentSessionCount,
    int EstimatedComponentSessionCount,
    int PartialComponentSessionCount,
    string EstimatedAmountState,
    decimal? EstimatedAmount,
    string PartialKnownComponentAmountState,
    decimal? PartialKnownComponentAmount,
    IReadOnlyList<CostAnalyticsPartialReasonCountV1> PartialReasonCounts);

public sealed record CostAnalyticsReadV1(
    string SchemaVersion,
    string SnapshotId,
    string State,
    string? CapReason,
    int? EligibleSessionCount,
    int? EligibleSessionLowerBound,
    int? GroupLowerBound,
    CostAnalyticsFiltersV1 Filters,
    CostAnalyticsOverallV1? Overall,
    IReadOnlyList<CostAnalyticsTotalV1> RangeTotals,
    IReadOnlyList<CostAnalyticsDailyTotalV1> DailyTotals,
    IReadOnlyList<CostAnalyticsGroupV1> Groups,
    string? NextCursor);

internal sealed record CostAnalyticsComponentV1(
    string Category,
    decimal? Amount,
    string? MissingReason);

internal sealed record CostAnalyticsMemberV1(
    string SessionId,
    string SessionStatus,
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string SourcePartitionState,
    int SourcePartitionCount,
    string SourcePartitionDigest,
    string SourceSurface,
    string SourceApplicationVersion,
    string? Repository,
    string? Workspace,
    string State,
    long? ActiveHeadRevision,
    string? ActiveEstimateId,
    long AttemptRevision,
    string IdentityDigest,
    string? Provider,
    string? Model,
    string? BillingMode,
    string? RegistryVersion,
    string? Currency,
    decimal? Amount,
    IReadOnlyList<CostAnalyticsComponentV1> Components,
    IReadOnlyList<string> Reasons);

internal static class SqliteCostAnalyticsProjectorV1
{
    private const int MaximumEligibleSessions = 2_000;
    private const int MaximumGroups = 2_000;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const string CursorPrefix = "cost-analytics-cursor-v1.";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private static readonly string[] States =
    [
        "estimated", "partial", "not_estimable", "missing",
        "failed", "unavailable", "stale",
    ];
    private static readonly string[] UnknownDimensionOrder =
    [
        "provider", "model", "billing_mode", "repository", "workspace",
        "registry_version", "currency", "component_category",
    ];
    private static readonly string[] ReasonOrder =
    [
        PricingEstimateReasons.UnknownModel,
        PricingEstimateReasons.UnknownBillingMode,
        PricingEstimateReasons.SubscriptionAllocationUnknown,
        PricingEstimateReasons.SubscriptionOrContractUnknown,
        PricingEstimateReasons.CustomContract,
        PricingEstimateReasons.MissingTokenCategory,
        PricingEstimateReasons.UnsupportedProviderRoute,
        PricingEstimateReasons.PartialSource,
        PricingEstimateReasons.RegistryOutOfDate,
        PricingEstimateReasons.OutsideEffectiveRange,
    ];

    internal static PricingReadResult<CostAnalyticsReadV1> Project(
        CostAnalyticsQueryV1 query,
        long configurationHeadRevision,
        string? configurationId,
        string providerCatalogSha256,
        IReadOnlyList<CostAnalyticsMemberV1> acquiredMembers)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(acquiredMembers);
        if (!ValidQuery(query))
            return new(PricingReadStatus.InvalidQuery);
        if (!LowerSha(providerCatalogSha256) || !MembersValid(acquiredMembers))
            return new(PricingReadStatus.Unavailable);

        var filters = new CostAnalyticsFiltersV1(
            FormatUtc(query.From),
            FormatUtc(query.To),
            query.SourceSurface,
            query.Provider,
            query.Model,
            query.BillingMode,
            query.Status,
            query.RegistryVersion,
            query.Currency,
            query.Repository,
            query.Workspace,
            query.Limit);
        var filterBytes = JsonSerializer.SerializeToUtf8Bytes(filters, JsonOptions);
        var filterDigest = Hash("cost-analytics-filter/v1", Encoding.UTF8.GetString(filterBytes));
        if (!TryReadCursor(query.After, out var cursor)
            || cursor is not null
                && (cursor.FilterDigest != filterDigest || cursor.Limit != query.Limit))
            return new(PricingReadStatus.InvalidCursor);

        var members = acquiredMembers
            .Where(member => Matches(member, query))
            .OrderBy(member => member.EffectiveAtUtc)
            .ThenBy(member => member.SessionId, StringComparer.Ordinal)
            .ToArray();
        if (members.Length > MaximumEligibleSessions)
        {
            var bounded = members.Take(MaximumEligibleSessions + 1).ToArray();
            var snapshot = Snapshot(
                configurationHeadRevision,
                configurationId,
                providerCatalogSha256,
                filterDigest,
                "eligible_session_limit",
                bounded,
                []);
            if (cursor is not null && cursor.SnapshotId != snapshot)
                return new(PricingReadStatus.SnapshotChanged);
            if (cursor is not null)
                return new(PricingReadStatus.InvalidCursor);
            return new(
                PricingReadStatus.Success,
                new(
                    "cost.analytics.v1",
                    snapshot,
                    "incomplete",
                    "eligible_session_limit",
                    null,
                    2_001,
                    null,
                    filters,
                    null,
                    [],
                    [],
                    [],
                    null));
        }

        var groups = BuildGroups(members);
        if (groups.Count > MaximumGroups)
        {
            var bounded = groups.Take(MaximumGroups + 1).ToArray();
            var snapshot = Snapshot(
                configurationHeadRevision,
                configurationId,
                providerCatalogSha256,
                filterDigest,
                "group_limit",
                members,
                bounded.Select(group => group.GroupId));
            if (cursor is not null && cursor.SnapshotId != snapshot)
                return new(PricingReadStatus.SnapshotChanged);
            if (cursor is not null)
                return new(PricingReadStatus.InvalidCursor);
            return new(
                PricingReadStatus.Success,
                new(
                    "cost.analytics.v1",
                    snapshot,
                    "incomplete",
                    "group_limit",
                    members.Length,
                    null,
                    2_001,
                    filters,
                    null,
                    [],
                    [],
                    [],
                    null));
        }

        var overall = Overall(members);
        var rangeTotals = BuildRangeTotals(members);
        var dailyTotals = BuildDailyTotals(members);
        var completeSnapshot = Snapshot(
            configurationHeadRevision,
            configurationId,
            providerCatalogSha256,
            filterDigest,
            "complete",
            members,
            groups.Select(group => group.GroupId)
                .Concat(rangeTotals.Select(TotalIdentity))
                .Concat(dailyTotals.Select(DailyTotalIdentity)));
        if (cursor is not null && cursor.SnapshotId != completeSnapshot)
            return new(PricingReadStatus.SnapshotChanged);

        var start = 0;
        if (cursor is not null)
        {
            start = groups.FindIndex(group => group.GroupId == cursor.GroupId);
            if (start < 0) return new(PricingReadStatus.InvalidCursor);
            start++;
        }
        var remaining = groups.Skip(start).ToList();
        var page = remaining.Take(query.Limit).ToList();
        while (true)
        {
            var hasMore = remaining.Count > page.Count;
            var next = hasMore && page.Count > 0
                ? EncodeCursor(new(
                    "cost.analytics.cursor.v1",
                    completeSnapshot,
                    filterDigest,
                    query.Limit,
                    page[^1].GroupId))
                : null;
            var response = new CostAnalyticsReadV1(
                "cost.analytics.v1",
                completeSnapshot,
                "complete",
                null,
                members.Length,
                null,
                null,
                filters,
                overall,
                rangeTotals,
                dailyTotals,
                page.AsReadOnly(),
                next);
            if (JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions).Length <= MaximumResponseBytes)
                return new(PricingReadStatus.Success, response);
            if (page.Count <= 1) return new(PricingReadStatus.ResponseTooLarge);
            page.RemoveAt(page.Count - 1);
        }
    }

    internal static PricingReadStatus Preflight(CostAnalyticsQueryV1 query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ValidQuery(query)) return PricingReadStatus.InvalidQuery;
        var filters = new CostAnalyticsFiltersV1(
            FormatUtc(query.From),
            FormatUtc(query.To),
            query.SourceSurface,
            query.Provider,
            query.Model,
            query.BillingMode,
            query.Status,
            query.RegistryVersion,
            query.Currency,
            query.Repository,
            query.Workspace,
            query.Limit);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(filters, JsonOptions);
        var digest = Hash("cost-analytics-filter/v1", Encoding.UTF8.GetString(bytes));
        return !TryReadCursor(query.After, out var cursor)
            || cursor is not null
                && (cursor.FilterDigest != digest || cursor.Limit != query.Limit)
            ? PricingReadStatus.InvalidCursor
            : PricingReadStatus.Success;
    }

    private static bool ValidQuery(CostAnalyticsQueryV1 query) =>
        query.From.Offset == TimeSpan.Zero
        && query.To.Offset == TimeSpan.Zero
        && query.From < query.To
        && query.To - query.From <= TimeSpan.FromDays(366)
        && query.Limit is >= 1 and <= 100
        && (query.SourceSurface is null || LowerToken(query.SourceSurface, 128))
        && (query.Provider is null or "github_copilot" or "claude_code" or "codex_app" or "unknown")
        && (query.Model is null || SqlitePricingReadStore.SafeAnalyticsLabel(query.Model) == query.Model)
        && (query.BillingMode is null or
            "github_ai_credits" or "github_legacy_requests" or "plan_included" or
            "anthropic_api_tokens" or "cloud_provider_api_tokens" or "subscription" or
            "custom_enterprise" or "unknown")
        && (query.Status is null || States.Contains(query.Status, StringComparer.Ordinal))
        && (query.RegistryVersion is null || LowerToken(query.RegistryVersion, 128))
        && (query.Currency is null or "USD")
        && (query.Repository is null
            || SqlitePricingReadStore.SafeAnalyticsLabel(query.Repository) == query.Repository)
        && (query.Workspace is null
            || SqlitePricingReadStore.SafeAnalyticsLabel(query.Workspace) == query.Workspace);

    private static bool MembersValid(IReadOnlyList<CostAnalyticsMemberV1> members)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (!Guid.TryParseExact(member.SessionId, "D", out var parsed)
                || parsed.ToString("D") != member.SessionId
                || !ids.Add(member.SessionId)
                || member.SessionStatus is not ("completed" or "failed")
                || member.SourcePartitionState != "resolved"
                || !LowerSha(member.SourcePartitionDigest)
                || !States.Contains(member.State, StringComparer.Ordinal)
                || member.Components.Select(component => component.Category)
                    .Distinct(StringComparer.Ordinal).Count() != member.Components.Count)
                return false;
            if (member.State is "estimated" or "partial")
            {
                if (member.Amount is null || member.Currency != "USD")
                    return false;
                try
                {
                    var sum = 0m;
                    foreach (var component in member.Components)
                        if (component.Amount is not null)
                            sum = checked(sum + component.Amount.Value);
                    if (sum != member.Amount) return false;
                }
                catch (OverflowException)
                {
                    return false;
                }
            }
        }
        return true;
    }

    internal static bool Matches(CostAnalyticsMemberV1 member, CostAnalyticsQueryV1 query) =>
        member.EffectiveAtUtc >= query.From
        && member.EffectiveAtUtc < query.To
        && Match(query.SourceSurface, member.SourceSurface)
        && Match(query.Provider, member.Provider)
        && Match(query.Model, member.Model)
        && Match(query.BillingMode, member.BillingMode)
        && Match(query.Status, member.State)
        && Match(query.RegistryVersion, member.RegistryVersion)
        && Match(query.Currency, member.Currency)
        && Match(query.Repository, member.Repository)
        && Match(query.Workspace, member.Workspace);

    private static bool Match(string? filter, string? value) =>
        filter is null || value is not null && filter == value;

    private static List<CostAnalyticsGroupV1> BuildGroups(
        IReadOnlyList<CostAnalyticsMemberV1> members)
    {
        var baseGroups = members.GroupBy(member => new BaseKey(
            DateOnly.FromDateTime(member.EffectiveAtUtc.UtcDateTime),
            member.SourceSurface,
            member.Provider,
            member.Model,
            member.BillingMode,
            member.Repository,
            member.Workspace,
            member.RegistryVersion,
            member.Currency));
        var result = new List<CostAnalyticsGroupV1>();
        foreach (var baseGroup in baseGroups)
        {
            var materialized = baseGroup.ToArray();
            var counts = Overall(materialized);
            var categories = materialized
                .SelectMany(member => member.Components.Count == 0
                    ? new string?[] { null }
                    : member.Components.Select(component => (string?)component.Category))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, NullableOrdinalComparer.Instance)
                .ToArray();
            foreach (var category in categories)
            {
                var componentMembers = materialized
                    .Select(member => (
                        Member: member,
                        Component: member.Components.SingleOrDefault(component =>
                            component.Category == category)))
                    .Where(pair => category is null
                        ? pair.Member.Components.Count == 0
                        : pair.Component is not null)
                    .ToArray();
                var estimated = Sum(componentMembers
                    .Where(pair => pair.Member.State == "estimated"
                        && pair.Component?.Amount is not null)
                    .Select(pair => pair.Component!.Amount!.Value));
                var partial = Sum(componentMembers
                    .Where(pair => pair.Member.State == "partial"
                        && pair.Component?.Amount is not null)
                    .Select(pair => pair.Component!.Amount!.Value));
                var key = new GroupKey(baseGroup.Key, category);
                result.Add(new(
                    key.Base.UtcDate,
                    key.Base.SourceSurface,
                    key.Base.Provider,
                    key.Base.Model,
                    key.Base.BillingMode,
                    key.Base.Repository,
                    key.Base.Workspace,
                    key.Base.RegistryVersion,
                    key.Base.Currency,
                    key.ComponentCategory,
                    GroupId(key),
                    UnknownDimensions(key),
                    counts.EligibleSessionCount,
                    counts.EstimatedSessionCount,
                    counts.PartialSessionCount,
                    counts.NotEstimableSessionCount,
                    counts.MissingSessionCount,
                    counts.FailedSessionCount,
                    counts.UnavailableSessionCount,
                    counts.StaleSessionCount,
                    counts.CoverageBasisPoints ?? 0,
                    componentMembers.Select(pair => pair.Member.SessionId).Distinct().Count(),
                    componentMembers.Count(pair => pair.Member.State == "estimated"
                        && pair.Component?.Amount is not null),
                    componentMembers.Count(pair => pair.Member.State == "partial"
                        && pair.Component?.Amount is not null),
                    estimated.State,
                    estimated.Amount,
                    partial.State,
                    partial.Amount,
                    ReasonCounts(componentMembers
                        .Where(pair => pair.Member.State == "partial"
                            && pair.Component?.Amount is not null)
                        .Select(pair => pair.Member))));
            }
        }
        result.Sort(GroupComparer.Instance);
        return result;
    }

    private static CostAnalyticsOverallV1 Overall(
        IReadOnlyList<CostAnalyticsMemberV1> members)
    {
        var estimated = members.Count(member => member.State == "estimated");
        var denominator = members.Count;
        return new(
            denominator,
            estimated,
            members.Count(member => member.State == "partial"),
            members.Count(member => member.State == "not_estimable"),
            members.Count(member => member.State == "missing"),
            members.Count(member => member.State == "failed"),
            members.Count(member => member.State == "unavailable"),
            members.Count(member => member.State == "stale"),
            estimated,
            denominator,
            denominator == 0 ? null : estimated * 10_000 / denominator);
    }

    private static IReadOnlyList<CostAnalyticsTotalV1> BuildRangeTotals(
        IReadOnlyList<CostAnalyticsMemberV1> members) =>
        Array.AsReadOnly(members
            .Where(ContributesToTotal)
            .GroupBy(member => new TotalKey(member.RegistryVersion, member.Currency))
            .OrderBy(group => group.Key.RegistryVersion, NullableOrdinalComparer.Instance)
            .ThenBy(group => group.Key.Currency, NullableOrdinalComparer.Instance)
            .Select(group => CreateTotal(group.Key, group))
            .ToArray());

    private static IReadOnlyList<CostAnalyticsDailyTotalV1> BuildDailyTotals(
        IReadOnlyList<CostAnalyticsMemberV1> members) =>
        Array.AsReadOnly(members
            .Where(ContributesToTotal)
            .GroupBy(member => new DailyKey(
                DateOnly.FromDateTime(member.EffectiveAtUtc.UtcDateTime),
                member.RegistryVersion,
                member.Currency))
            .OrderBy(group => group.Key.UtcDate)
            .ThenBy(group => group.Key.RegistryVersion, NullableOrdinalComparer.Instance)
            .ThenBy(group => group.Key.Currency, NullableOrdinalComparer.Instance)
            .Select(group =>
            {
                var total = CreateTotal(
                    new(group.Key.RegistryVersion, group.Key.Currency),
                    group);
                return new CostAnalyticsDailyTotalV1(
                    group.Key.UtcDate,
                    total.RegistryVersion,
                    total.Currency,
                    total.EstimatedAmountState,
                    total.EstimatedAmount,
                    total.PartialKnownComponentAmountState,
                    total.PartialKnownComponentAmount,
                    total.PartialReasonCounts);
            })
            .ToArray());

    private static bool ContributesToTotal(CostAnalyticsMemberV1 member) =>
        member.State is "estimated" or "partial"
        && member.Amount is not null;

    private static CostAnalyticsTotalV1 CreateTotal(
        TotalKey key,
        IEnumerable<CostAnalyticsMemberV1> source)
    {
        var members = source.ToArray();
        var estimated = Sum(members
            .Where(member => member.State == "estimated" && member.Amount is not null)
            .Select(member => member.Amount!.Value));
        var partial = Sum(members
            .Where(member => member.State == "partial" && member.Amount is not null)
            .Select(member => member.Amount!.Value));
        return new(
            key.RegistryVersion,
            key.Currency,
            estimated.State,
            estimated.Amount,
            partial.State,
            partial.Amount,
            ReasonCounts(members.Where(member =>
                member.State == "partial" && member.Amount is not null)));
    }

    private static IReadOnlyList<CostAnalyticsPartialReasonCountV1> ReasonCounts(
        IEnumerable<CostAnalyticsMemberV1> members)
    {
        var values = members
            .SelectMany(member => member.Reasons.Distinct(StringComparer.Ordinal)
                .Select(reason => (reason, member.SessionId)))
            .GroupBy(value => value.reason, StringComparer.Ordinal)
            .OrderBy(group => Array.IndexOf(ReasonOrder, group.Key))
            .Select(group => new CostAnalyticsPartialReasonCountV1(
                group.Key,
                group.Select(value => value.SessionId).Distinct(StringComparer.Ordinal).Count()))
            .ToArray();
        return Array.AsReadOnly(values);
    }

    private static SumResult Sum(IEnumerable<decimal> values)
    {
        var any = false;
        var total = 0m;
        try
        {
            foreach (var value in values)
            {
                any = true;
                total = checked(total + value);
            }
            return any ? new("available", total) : new("not_applicable", null);
        }
        catch (OverflowException)
        {
            return new("unrepresentable", null);
        }
    }

    private static IReadOnlyList<string> UnknownDimensions(GroupKey key)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        if (key.Base.Provider is null) missing.Add("provider");
        if (key.Base.Model is null) missing.Add("model");
        if (key.Base.BillingMode is null) missing.Add("billing_mode");
        if (key.Base.Repository is null) missing.Add("repository");
        if (key.Base.Workspace is null) missing.Add("workspace");
        if (key.Base.RegistryVersion is null) missing.Add("registry_version");
        if (key.Base.Currency is null) missing.Add("currency");
        if (key.ComponentCategory is null) missing.Add("component_category");
        return Array.AsReadOnly(UnknownDimensionOrder.Where(missing.Contains).ToArray());
    }

    private static string GroupId(GroupKey key) =>
        "cost-analytics-group-" + Hash(
            "cost-analytics-group/v1",
            key.Base.UtcDate.ToString("yyyy-MM-dd"),
            key.Base.SourceSurface,
            key.Base.Provider,
            key.Base.Model,
            key.Base.BillingMode,
            key.Base.Repository,
            key.Base.Workspace,
            key.Base.RegistryVersion,
            key.Base.Currency,
            key.ComponentCategory);

    private static string Snapshot(
        long headRevision,
        string? configurationId,
        string catalogSha,
        string filterDigest,
        string capState,
        IEnumerable<CostAnalyticsMemberV1> members,
        IEnumerable<string> derivedIdentities) =>
        "cost-analytics-snapshot-" + Hash(
            "cost-analytics-snapshot/v1",
            [
                headRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                configurationId,
                catalogSha,
                filterDigest,
                capState,
                .. members.SelectMany(MemberIdentity),
                .. derivedIdentities,
            ]);

    private static IEnumerable<string?> MemberIdentity(CostAnalyticsMemberV1 member) =>
    [
        member.SessionId,
        member.SessionStatus,
        member.EffectiveAtUtc.ToString("O"),
        member.UpdatedAtUtc.ToString("O"),
        member.SourcePartitionState,
        member.SourcePartitionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        member.SourcePartitionDigest,
        member.SourceSurface,
        member.SourceApplicationVersion,
        member.ActiveHeadRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        member.ActiveEstimateId,
        member.AttemptRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
        member.State,
        member.IdentityDigest,
    ];

    private static string TotalIdentity(CostAnalyticsTotalV1 total) =>
        Hash(
            "cost-analytics-total/v1",
            total.RegistryVersion,
            total.Currency,
            total.EstimatedAmountState,
            total.EstimatedAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            total.PartialKnownComponentAmountState,
            total.PartialKnownComponentAmount?.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

    private static string DailyTotalIdentity(CostAnalyticsDailyTotalV1 total) =>
        Hash(
            "cost-analytics-daily-total/v1",
            total.UtcDate.ToString("yyyy-MM-dd"),
            TotalIdentity(new(
                total.RegistryVersion,
                total.Currency,
                total.EstimatedAmountState,
                total.EstimatedAmount,
                total.PartialKnownComponentAmountState,
                total.PartialKnownComponentAmount,
                total.PartialReasonCounts)));

    private static string Hash(string domain, params string?[] values) =>
        Hash(domain, (IEnumerable<string?>)values);

    private static string Hash(string domain, IEnumerable<string?> values)
    {
        using var stream = new MemoryStream();
        WriteFrame(stream, domain);
        foreach (var value in values) WriteFrame(stream, value);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteFrame(Stream stream, string? value)
    {
        Span<byte> length = stackalloc byte[4];
        if (value is null)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, -1);
            stream.Write(length);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static bool TryReadCursor(string? encoded, out CursorV1? cursor)
    {
        cursor = null;
        if (encoded is null) return true;
        if (encoded.Length is < 1 or > 768
            || !encoded.StartsWith(CursorPrefix, StringComparison.Ordinal))
            return false;
        try
        {
            var payload = encoded[CursorPrefix.Length..];
            if (payload.Length == 0 || payload.Contains('='))
                return false;
            var padding = new string('=', (4 - payload.Length % 4) % 4);
            var bytes = Convert.FromBase64String(
                payload.Replace('-', '+').Replace('_', '/') + padding);
            var value = JsonSerializer.Deserialize<CursorV1>(bytes, JsonOptions);
            if (value is null
                || value.SchemaVersion != "cost.analytics.cursor.v1"
                || !value.SnapshotId.StartsWith("cost-analytics-snapshot-", StringComparison.Ordinal)
                || !LowerSha(value.SnapshotId["cost-analytics-snapshot-".Length..])
                || !LowerSha(value.FilterDigest)
                || value.Limit is < 1 or > 100
                || !value.GroupId.StartsWith("cost-analytics-group-", StringComparison.Ordinal)
                || !LowerSha(value.GroupId["cost-analytics-group-".Length..])
                || !JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).AsSpan().SequenceEqual(bytes)
                || EncodeCursor(value) != encoded)
                return false;
            cursor = value;
            return true;
        }
        catch (Exception exception) when (exception is
            FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static string EncodeCursor(CursorV1 cursor)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor, JsonOptions);
        return CursorPrefix + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool LowerSha(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool LowerToken(string value, int maximum) =>
        value.Length is >= 1
        && value.Length <= maximum
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '_'
            or '-');

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);

    private sealed record CursorV1(
        string SchemaVersion,
        string SnapshotId,
        string FilterDigest,
        int Limit,
        string GroupId);

    private sealed record BaseKey(
        DateOnly UtcDate,
        string SourceSurface,
        string? Provider,
        string? Model,
        string? BillingMode,
        string? Repository,
        string? Workspace,
        string? RegistryVersion,
        string? Currency);

    private sealed record GroupKey(BaseKey Base, string? ComponentCategory);
    private sealed record TotalKey(string? RegistryVersion, string? Currency);
    private sealed record DailyKey(DateOnly UtcDate, string? RegistryVersion, string? Currency);
    private sealed record SumResult(string State, decimal? Amount);

    private sealed class NullableOrdinalComparer : IComparer<string?>
    {
        internal static NullableOrdinalComparer Instance { get; } = new();

        public int Compare(string? x, string? y) =>
            x is null ? y is null ? 0 : -1
            : y is null ? 1
            : StringComparer.Ordinal.Compare(x, y);
    }

    private sealed class GroupComparer : IComparer<CostAnalyticsGroupV1>
    {
        internal static GroupComparer Instance { get; } = new();

        public int Compare(CostAnalyticsGroupV1? x, CostAnalyticsGroupV1? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var value = x.UtcDate.CompareTo(y.UtcDate);
            if (value != 0) return value;
            foreach (var pair in new[]
            {
                (x.SourceSurface, y.SourceSurface),
                (x.Provider, y.Provider),
                (x.Model, y.Model),
                (x.BillingMode, y.BillingMode),
                (x.Repository, y.Repository),
                (x.Workspace, y.Workspace),
                (x.RegistryVersion, y.RegistryVersion),
                (x.Currency, y.Currency),
                (x.ComponentCategory, y.ComponentCategory),
                (x.GroupId, y.GroupId),
            })
            {
                value = NullableOrdinalComparer.Instance.Compare(pair.Item1, pair.Item2);
                if (value != 0) return value;
            }
            return 0;
        }
    }
}
