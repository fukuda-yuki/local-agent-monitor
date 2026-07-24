using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal sealed class AlertCenterV1AdapterReadModelV2(IAlertCenterReadModel inner)
    : IAlertCenterReadModelV2
{
    public AlertCenterReadResultV2 Read(AlertCenterQueryV2 query)
    {
        if (!TryOffset(query.Cursor, out var offset))
        {
            return new(AlertCenterReadStatusV2.InvalidQuery);
        }
        var result = inner.Read(new(
            query.AlertId,
            query.SessionId,
            query.TraceId,
            query.Severity,
            query.State,
            query.RuleId,
            query.SourceSurface,
            query.Repository,
            query.Workspace,
            query.Completeness,
            query.From,
            query.To,
            offset,
            query.Limit));
        if (result.Status != AlertCenterReadStatus.Success || result.Snapshot is null)
        {
            return new(result.Status == AlertCenterReadStatus.Busy
                ? AlertCenterReadStatusV2.Busy
                : AlertCenterReadStatusV2.Unavailable);
        }
        var source = result.Snapshot;
        var items = query.ReceiptKind == "cost_receipt_v2"
            || query.ScopeKind != "all"
            || query.Currency != "all"
            || query.CoverageState != "all"
                ? []
                : source.Alerts.Select(item =>
                    new AlertCenterItemV2("receipt_v1", item, null)).ToArray();
        var matched = query.ReceiptKind == "cost_receipt_v2"
            || query.ScopeKind != "all"
            || query.Currency != "all"
            || query.CoverageState != "all"
                ? 0
                : checked((int)source.TotalCount);
        var end = offset + items.Length;
        var snapshot = new AlertCenterSnapshotV2(
            AlertCenterContractVersions.CenterV2,
            "alert-center-snapshot-" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    string.Join(
                        "\0",
                        source.SchemaVersion,
                        source.GeneratedAt,
                        source.SnapshotState,
                        source.TotalCount.ToString(CultureInfo.InvariantCulture)))))
                .ToLowerInvariant(),
            source.SnapshotState,
            source.SnapshotState == "incomplete" ? "owner_more" : null,
            checked((int)source.TotalCount),
            source.SnapshotState == "complete" ? "exact" : "acquired_only",
            matched,
            new(
                query.AlertId,
                query.SessionId,
                query.TraceId,
                query.Severity,
                query.State,
                query.RuleId,
                query.SourceSurface,
                query.Repository,
                query.Workspace,
                query.Completeness,
                query.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                query.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                query.ReceiptKind,
                query.ScopeKind,
                query.Currency,
                query.CoverageState,
                query.Limit),
            items,
            items.Length == 0 ? 0 : offset + 1,
            items.Length == 0 ? 0 : end,
            offset > 0,
            offset <= query.Limit ? null : Cursor(offset - query.Limit),
            end < matched ? Cursor(end) : null,
            source.SnapshotState == "complete" ? "complete" : "incomplete_snapshot",
            source.SnapshotState == "complete" ? source.RecurringGroups : [],
            source.CoverageState,
            source.Coverage.Select((item, index) => new AlertCenterCoverageItemV2(
                "suppression_v1",
                new(
                    item.EvaluationId,
                    index,
                    item.RuleId,
                    item.RuleVersion,
                    item.Code,
                    item.MissingCapabilities,
                    item.ContextState,
                    item.SourceSurface,
                    item.SourceVersion,
                    item.SessionId,
                    item.TraceId,
                    item.ObservationDate),
                null)).ToArray(),
            source.OmittedCoverageFactCount);
        return new(AlertCenterReadStatusV2.Success, snapshot);
    }

    private static string Cursor(int offset) =>
        $"alert-center-cursor-v2.legacy-{offset.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryOffset(string? cursor, out int offset)
    {
        offset = 0;
        if (cursor is null) return true;
        const string prefix = "alert-center-cursor-v2.legacy-";
        return cursor.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(
                cursor[prefix.Length..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out offset)
            && offset is >= 0 and <= 1_000_000;
    }
}
