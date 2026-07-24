using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Persistence.Sqlite.Costs;

public sealed record CostBudgetScopeV1(
    string ScopeKind,
    string? SessionId,
    string? UtcDate,
    DateTimeOffset? CutoffUtc,
    int? WindowDays);

public sealed record CostRecalculationRequestV1(
    string SchemaVersion,
    string ConfigurationId,
    long ExpectedHeadRevision,
    string CatalogSha256,
    IReadOnlyList<string> SessionIds,
    IReadOnlyList<CostBudgetScopeV1> BudgetScopes,
    string IdempotencyKey);

public static class CostRecalculationRequestCanonicalJsonV1
{
    internal const string SchemaVersion = "cost.recalculation-request.v1";

    public static CostRecalculationRequestV1 Create(
        string configurationId,
        long expectedHeadRevision,
        string catalogSha256,
        IReadOnlyList<string> sessionIds,
        IReadOnlyList<CostBudgetScopeV1> budgetScopes,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        ArgumentNullException.ThrowIfNull(budgetScopes);
        var sessions = sessionIds.ToArray();
        var scopes = budgetScopes.Select(scope => scope with { }).ToArray();
        if (!Regex.IsMatch(configurationId, "^cost-configuration-[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || expectedHeadRevision <= 0
            || !Regex.IsMatch(catalogSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || sessions.Length is < 1 or > 100
            || sessions.Distinct(StringComparer.Ordinal).Count() != sessions.Length
            || sessions.Any(session => !Guid.TryParseExact(session, "D", out var parsed)
                || parsed.ToString("D") != session)
            || scopes.Length > 8
            || scopes.Distinct().Count() != scopes.Length
            || !Regex.IsMatch(idempotencyKey, "^[A-Za-z0-9][A-Za-z0-9._-]{15,127}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Cost recalculation request fields are invalid.");
        foreach (var scope in scopes) ValidateScope(scope);
        return new(
            SchemaVersion,
            configurationId,
            expectedHeadRevision,
            catalogSha256,
            Array.AsReadOnly(sessions),
            Array.AsReadOnly(scopes),
            idempotencyKey);
    }

    public static byte[] Serialize(CostRecalculationRequestV1 request)
    {
        var frozen = Create(
            request.ConfigurationId,
            request.ExpectedHeadRevision,
            request.CatalogSha256,
            request.SessionIds,
            request.BudgetScopes,
            request.IdempotencyKey);
        if (request.SchemaVersion != SchemaVersion)
            throw new ArgumentException("Cost recalculation request version is invalid.", nameof(request));
        return CostConfigurationCanonicalJsonV1.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString("configuration_id", frozen.ConfigurationId);
            writer.WriteNumber("expected_head_revision", frozen.ExpectedHeadRevision);
            writer.WriteString("catalog_sha256", frozen.CatalogSha256);
            writer.WriteStartArray("session_ids");
            foreach (var session in frozen.SessionIds) writer.WriteStringValue(session);
            writer.WriteEndArray();
            writer.WriteStartArray("budget_scopes");
            foreach (var scope in frozen.BudgetScopes)
            {
                writer.WriteStartObject();
                writer.WriteString("scope_kind", scope.ScopeKind);
                switch (scope.ScopeKind)
                {
                    case "session":
                        writer.WriteString("session_id", scope.SessionId);
                        break;
                    case "utc_day":
                        writer.WriteString("utc_date", scope.UtcDate);
                        break;
                    case "rolling_period":
                        writer.WriteString("cutoff_utc", CostJsonV1.Timestamp(scope.CutoffUtc!.Value));
                        writer.WriteNumber("window_days", scope.WindowDays!.Value);
                        break;
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("idempotency_key", frozen.IdempotencyKey);
            writer.WriteEndObject();
        });
    }

    public static CostConsumerResult<CostRecalculationRequestV1> Consume(ReadOnlyMemory<byte> canonicalBytes)
    {
        if (canonicalBytes.Length > 1_048_576) return new(CostConsumerStatus.TooLarge, null);
        try
        {
            var bytes = canonicalBytes.ToArray();
            using var document = CostJsonV1.Parse(bytes, 16);
            if (document.RootElement.GetProperty("schema_version").GetString() != SchemaVersion)
                return new(CostConsumerStatus.Unsupported, null);
            var value = JsonSerializer.Deserialize<CostRecalculationRequestV1>(bytes, CostJsonV1.Options);
            if (value is null) return new(CostConsumerStatus.Invalid, null);
            var frozen = Create(
                value.ConfigurationId,
                value.ExpectedHeadRevision,
                value.CatalogSha256,
                value.SessionIds,
                value.BudgetScopes,
                value.IdempotencyKey);
            return bytes.AsSpan().SequenceEqual(Serialize(frozen))
                ? new(CostConsumerStatus.Success, frozen)
                : new(CostConsumerStatus.Invalid, null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return new(CostConsumerStatus.Invalid, null);
        }
    }

    private static void ValidateScope(CostBudgetScopeV1 scope)
    {
        var valid = scope.ScopeKind switch
        {
            "session" => scope.SessionId is not null
                && Guid.TryParseExact(scope.SessionId, "D", out var parsed)
                && parsed.ToString("D") == scope.SessionId
                && scope.UtcDate is null && scope.CutoffUtc is null && scope.WindowDays is null,
            "utc_day" => scope.SessionId is null
                && scope.UtcDate is not null
                && DateOnly.TryParseExact(scope.UtcDate, "yyyy-MM-dd", out _)
                && scope.CutoffUtc is null && scope.WindowDays is null,
            "rolling_period" => scope.SessionId is null && scope.UtcDate is null
                && scope.CutoffUtc is { Offset.Ticks: 0 } cutoff
                && cutoff.TimeOfDay == TimeSpan.Zero
                && scope.WindowDays is >= 2 and <= 366,
            _ => false,
        };
        if (!valid) throw new ArgumentException("Cost budget scope is invalid.");
    }
}
