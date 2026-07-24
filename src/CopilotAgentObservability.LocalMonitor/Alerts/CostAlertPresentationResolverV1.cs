using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal interface ICostAlertEstimateReadStoreV1
{
    PricingReadResult<CostSessionEstimateReadV1> ReadSessionEstimate(
        string sessionId,
        string estimateId,
        ReadOnlyMemory<byte> currentProviderCatalogBytes);
}

internal sealed class SqliteCostAlertEstimateReadStoreV1(
    SqlitePricingReadStore inner) : ICostAlertEstimateReadStoreV1
{
    public PricingReadResult<CostSessionEstimateReadV1> ReadSessionEstimate(
        string sessionId,
        string estimateId,
        ReadOnlyMemory<byte> currentProviderCatalogBytes) =>
        inner.ReadSessionEstimate(sessionId, estimateId, currentProviderCatalogBytes);
}

internal sealed class CostAlertPresentationResolverV1 : ICostAlertPresentationResolverV1
{
    private const int MaximumMembers = 2_000;
    private const int MaximumEvidence = 4_000;
    private const int MaximumResultBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private readonly ISessionStore sessionStore;
    private readonly ICostAlertEstimateReadStoreV1 estimateStore;
    private readonly byte[] currentProviderCatalogBytes;

    internal CostAlertPresentationResolverV1(
        ISessionStore sessionStore,
        ICostAlertEstimateReadStoreV1 estimateStore,
        ReadOnlyMemory<byte> currentProviderCatalogBytes)
    {
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.estimateStore = estimateStore ?? throw new ArgumentNullException(nameof(estimateStore));
        this.currentProviderCatalogBytes = currentProviderCatalogBytes.ToArray();
    }

    public CostAlertPresentationResolutionV1 Resolve(
        IReadOnlyList<AlertCostMemberV2> members,
        IReadOnlyList<AlertEvidenceReferenceV2> evidence)
    {
        if (!ValidInput(members, evidence))
        {
            return Unavailable();
        }

        try
        {
            var resolved = new List<CostAlertPresentationMemberV1>(members.Count);
            foreach (var member in members)
            {
                var sessionHref =
                    $"/costs?session_id={Uri.EscapeDataString(member.SessionId)}";
                PricingReadResult<CostSessionEstimateReadV1>? estimate = null;
                if (member.EstimateId is not null)
                {
                    estimate = estimateStore.ReadSessionEstimate(
                        member.SessionId,
                        member.EstimateId,
                        currentProviderCatalogBytes);
                    if (estimate.Status == PricingReadStatus.Busy)
                    {
                        return Busy();
                    }
                    if (estimate.Status != PricingReadStatus.NotFound
                        && (estimate.Status != PricingReadStatus.Success
                            || estimate.Value is null
                            || !ExactEstimate(member, estimate.Value)))
                    {
                        return Unavailable();
                    }
                }

                var detail = sessionStore.GetDetail(Guid.ParseExact(member.SessionId, "D"));
                if (detail is null)
                {
                    resolved.Add(new(
                        member.SessionId,
                        member.SessionEffectiveAtUtc,
                        "missing",
                        null,
                        null,
                        "unavailable",
                        sessionHref,
                        member.EstimateId,
                        member.EstimateId is null ? null : "missing",
                        null));
                    continue;
                }
                if (detail.Session.SessionId.ToString("D") != member.SessionId
                    || detail.Session.LastSeenAt.Offset != TimeSpan.Zero
                    || detail.Session.LastSeenAt != member.SessionEffectiveAtUtc)
                {
                    return Unavailable();
                }

                var sessionEvidenceState = detail.Session.RawRetentionState switch
                {
                    SessionRawRetentionState.Expiring => "available",
                    SessionRawRetentionState.NotCaptured => "available",
                    SessionRawRetentionState.ExpiredPendingDeletion => "expired",
                    _ => null,
                };
                if (sessionEvidenceState is null)
                {
                    return Unavailable();
                }

                var labelsAvailable =
                    AlertCenterLabelGuard.Accepts(detail.Session.Repository)
                    && AlertCenterLabelGuard.Accepts(detail.Session.Workspace);
                var repository = labelsAvailable ? detail.Session.Repository : null;
                var workspace = labelsAvailable ? detail.Session.Workspace : null;
                string? estimateEvidenceState = null;
                string? estimateHref = null;
                if (member.EstimateId is not null)
                {
                    if (estimate!.Status == PricingReadStatus.NotFound)
                    {
                        estimateEvidenceState = "missing";
                    }
                    else
                    {
                        estimateEvidenceState = "available";
                        estimateHref =
                            $"{sessionHref}&estimate_id={Uri.EscapeDataString(member.EstimateId)}";
                    }
                }

                resolved.Add(new(
                    member.SessionId,
                    member.SessionEffectiveAtUtc,
                    sessionEvidenceState,
                    repository,
                    workspace,
                    labelsAvailable ? "available" : "unavailable",
                    sessionHref,
                    member.EstimateId,
                    estimateEvidenceState,
                    estimateHref));
            }

            var result = new CostAlertPresentationResolutionV1(
                "success",
                resolved.AsReadOnly());
            return JsonSerializer.SerializeToUtf8Bytes(result, Json).Length <= MaximumResultBytes
                ? result
                : Unavailable();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Busy();
        }
        catch (Exception exception) when (exception is
            SqliteException or
            InvalidOperationException or
            FormatException or
            ArgumentException or
            OverflowException)
        {
            return Unavailable();
        }
    }

    private static bool ValidInput(
        IReadOnlyList<AlertCostMemberV2>? members,
        IReadOnlyList<AlertEvidenceReferenceV2>? evidence)
    {
        if (members is null
            || evidence is null
            || members.Count == 0
            || members.Count > MaximumMembers
            || evidence.Count > MaximumEvidence
            || members.Select(item => item.SessionId)
                .Distinct(StringComparer.Ordinal).Count() != members.Count
            || members.Where(item => item.EstimateId is not null)
                .Select(item => item.EstimateId)
                .Distinct(StringComparer.Ordinal).Count()
                != members.Count(item => item.EstimateId is not null))
        {
            return false;
        }

        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            if (!AlertValidationV2.ValidMember(member)
                || !CanonicalSessionId(member.SessionId)
                || member.SessionEffectiveAtUtc.Offset != TimeSpan.Zero
                || member.SessionUpdatedAtUtc.Offset != TimeSpan.Zero
                || (member.EstimateId is null)
                    != (member.EstimateCalculationTimeUtc is null)
                || member.EstimateId is not null
                    && (!PrefixedSha(member.EstimateId, "pricing-estimate-")
                        || member.EstimateCalculationTimeUtc!.Value.Offset != TimeSpan.Zero)
                || index > 0 && CompareMember(members[index - 1], member) >= 0)
            {
                return false;
            }
        }

        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            if (!CanonicalSessionId(item.SessionId)
                || item.ObservedAtUtc.Offset != TimeSpan.Zero
                || index > 0 && CompareEvidence(evidence[index - 1], item) >= 0)
            {
                return false;
            }
        }

        if (evidence.Count != members.Count + members.Count(item => item.EstimateId is not null))
        {
            return false;
        }
        foreach (var member in members)
        {
            if (evidence.Count(item =>
                    item.Kind == AlertEvidenceKindV2.Session
                    && item.SessionId == member.SessionId
                    && item.EvidenceId == member.SessionId
                    && item.ObservedAtUtc == member.SessionEffectiveAtUtc) != 1)
            {
                return false;
            }

            var estimateEvidence = evidence.Where(item =>
                item.Kind == AlertEvidenceKindV2.PricingEstimate
                && item.SessionId == member.SessionId).ToArray();
            if (member.EstimateId is null)
            {
                if (estimateEvidence.Length != 0) return false;
            }
            else if (estimateEvidence.Length != 1
                || estimateEvidence[0].EvidenceId != member.EstimateId
                || estimateEvidence[0].ObservedAtUtc != member.EstimateCalculationTimeUtc)
            {
                return false;
            }
        }
        return true;
    }

    private static bool ExactEstimate(
        AlertCostMemberV2 member,
        CostSessionEstimateReadV1 read)
    {
        var item = read.Item;
        return read.SessionId == member.SessionId
            && item.EstimateId == member.EstimateId
            && item.HeadRevision == member.HeadRevision
            && item.CalculationTimeUtc == member.EstimateCalculationTimeUtc
            && item.SessionEffectiveAtUtc == member.SessionEffectiveAtUtc
            && item.CatalogSha256 == member.CatalogSha256
            && item.Registry?.RegistryVersion == member.RegistryVersion
            && item.Provider == member.Provider
            && item.Model == member.Model
            && item.BillingMode == member.BillingMode
            && EstimateStatusMatches(member.State, item.EstimateStatus);
    }

    private static bool EstimateStatusMatches(
        AlertCostMemberStateV2 state,
        string estimateStatus) => state switch
    {
        AlertCostMemberStateV2.Estimated => estimateStatus == "estimated",
        AlertCostMemberStateV2.Partial => estimateStatus == "partial",
        AlertCostMemberStateV2.NotEstimable => estimateStatus == "not-estimable",
        AlertCostMemberStateV2.Stale =>
            estimateStatus is "estimated" or "partial" or "not-estimable",
        _ => false,
    };

    private static int CompareMember(AlertCostMemberV2 left, AlertCostMemberV2 right)
    {
        var time = left.SessionEffectiveAtUtc.CompareTo(right.SessionEffectiveAtUtc);
        return time != 0
            ? time
            : string.CompareOrdinal(left.SessionId, right.SessionId);
    }

    private static int CompareEvidence(
        AlertEvidenceReferenceV2 left,
        AlertEvidenceReferenceV2 right)
    {
        var kind = EvidenceRank(left.Kind).CompareTo(EvidenceRank(right.Kind));
        if (kind != 0) return kind;
        var session = string.CompareOrdinal(left.SessionId, right.SessionId);
        if (session != 0) return session;
        var evidence = string.CompareOrdinal(left.EvidenceId, right.EvidenceId);
        return evidence != 0
            ? evidence
            : left.ObservedAtUtc.CompareTo(right.ObservedAtUtc);
    }

    private static int EvidenceRank(AlertEvidenceKindV2 kind) => kind switch
    {
        AlertEvidenceKindV2.Session => 0,
        AlertEvidenceKindV2.PricingEstimate => 1,
        _ => int.MaxValue,
    };

    private static bool CanonicalSessionId(string? value) =>
        Guid.TryParseExact(value, "D", out var parsed)
        && parsed.ToString("D") == value;

    private static bool PrefixedSha(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length == prefix.Length + 64
        && value[prefix.Length..].All(
            character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static CostAlertPresentationResolutionV1 Busy() => new("busy", []);
    private static CostAlertPresentationResolutionV1 Unavailable() =>
        new("unavailable", []);
}
