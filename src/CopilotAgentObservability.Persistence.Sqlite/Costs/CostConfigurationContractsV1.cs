using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Persistence.Sqlite.Costs;

public enum CostConsumerStatus
{
    Success,
    Invalid,
    Unsupported,
    TooLarge,
}

public sealed record CostConsumerResult<T>(CostConsumerStatus Status, T? Value);

internal static class CostContractSchemaVersionV1
{
    internal static bool IsRecognizedFuture(string? schema, string prefix) =>
        schema is not null
        && Regex.IsMatch(
            schema,
            "^" + Regex.Escape(prefix) + "(?:[2-9]|[1-9][0-9]+)$",
            RegexOptions.CultureInvariant);
}

public sealed record CostSourceEntryV1(
    string SourceSurface,
    string ApplicationVersion,
    string AdapterCapabilityVersion,
    string Provider,
    string BillingMode,
    string PricingRoute);

public sealed record CostBudgetEntryV1(
    string RuleId,
    string RuleVersion,
    bool Enabled,
    string Currency,
    string WarningThreshold,
    string CriticalThreshold,
    int MinimumCoverageBasisPoints,
    string ScopeKind,
    int? WindowDays);

public sealed record CostConfigurationV1(
    string SchemaVersion,
    string ConfigurationId,
    string? PredecessorConfigurationId,
    string CatalogSha256,
    IReadOnlyList<CostSourceEntryV1> SourceEntries,
    IReadOnlyList<CostBudgetEntryV1> BudgetEntries,
    DateTimeOffset CreatedAtUtc);

public sealed record CostConfigurationPreviewV1(
    string SchemaVersion,
    CostConfigurationV1 Configuration,
    long ExpectedHeadRevision,
    string? ExpectedConfigurationId,
    string CatalogSha256,
    string SelectionDigest,
    int ProposedMatchCount,
    int CurrentMatchCount,
    string CurrentMatchCountState,
    int OverlapCount,
    string OverlapCountState,
    string PreviewDigest);

public sealed record CostConfigurationCommitResultV1(
    string SchemaVersion,
    string ConfigurationId,
    long HeadRevision,
    string CatalogSha256);

public static class CostConfigurationCanonicalJsonV1
{
    internal const int MaximumBytes = 1_048_576;
    internal const string SchemaVersion = "cost.configuration.v1";
    private static readonly Regex LowerToken = new("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeVersion = new("^[\\x21-\\x7e]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly string[] RuleOrder =
    [
        "session-estimated-cost-threshold",
        "daily-estimated-cost-threshold",
        "period-estimated-cost-threshold",
    ];

    public static CostConfigurationV1 Create(
        string? predecessorConfigurationId,
        string catalogSha256,
        IReadOnlyList<CostSourceEntryV1> sourceEntries,
        IReadOnlyList<CostBudgetEntryV1> budgetEntries,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(sourceEntries);
        ArgumentNullException.ThrowIfNull(budgetEntries);
        var sources = sourceEntries.Select(item => item with { }).ToArray();
        var budgets = budgetEntries.Select(item => item with { }).ToArray();
        Validate(predecessorConfigurationId, catalogSha256, sources, budgets, createdAtUtc);
        Array.Sort(sources, CompareSources);
        Array.Sort(budgets, CompareBudgets);
        RejectDuplicates(sources, budgets);
        var projection = SerializeProjection(
            predecessorConfigurationId,
            catalogSha256,
            sources,
            budgets,
            createdAtUtc);
        var configurationId = "cost-configuration-" + CostIdentityV1.Hash(
            "cost-configuration/v1",
            projection);
        return new(
            SchemaVersion,
            configurationId,
            predecessorConfigurationId,
            catalogSha256,
            Array.AsReadOnly(sources),
            Array.AsReadOnly(budgets),
            createdAtUtc);
    }

    public static byte[] Serialize(CostConfigurationV1 configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var created = Create(
            configuration.PredecessorConfigurationId,
            configuration.CatalogSha256,
            configuration.SourceEntries,
            configuration.BudgetEntries,
            configuration.CreatedAtUtc);
        if (configuration.SchemaVersion != SchemaVersion
            || configuration.ConfigurationId != created.ConfigurationId)
            throw new ArgumentException("Cost configuration identity is invalid.", nameof(configuration));
        var bytes = Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteString("configuration_id", created.ConfigurationId);
            WriteProjectionProperties(writer, created);
            writer.WriteEndObject();
        });
        if (bytes.Length > MaximumBytes)
            throw new ArgumentException("Cost configuration exceeds the v1 bound.", nameof(configuration));
        return bytes;
    }

    internal static void Validate(
        string? predecessor,
        string catalogSha,
        IReadOnlyList<CostSourceEntryV1> sources,
        IReadOnlyList<CostBudgetEntryV1> budgets,
        DateTimeOffset createdAt)
    {
        if (!Sha.IsMatch(catalogSha)
            || predecessor is not null
                && (!predecessor.StartsWith("cost-configuration-", StringComparison.Ordinal)
                    || predecessor.Length != 83
                    || !Sha.IsMatch(predecessor[19..]))
            || createdAt.Offset != TimeSpan.Zero
            || sources.Count > 32
            || budgets.Count > 3)
            throw new ArgumentException("Cost configuration fields are invalid.");

        foreach (var source in sources)
        {
            if (!LowerToken.IsMatch(source.SourceSurface)
                || !SafeVersion.IsMatch(source.ApplicationVersion)
                || !SafeVersion.IsMatch(source.AdapterCapabilityVersion)
                || !LowerToken.IsMatch(source.Provider)
                || !LowerToken.IsMatch(source.BillingMode)
                || !LowerToken.IsMatch(source.PricingRoute))
                throw new ArgumentException("Cost source entry is invalid.");
        }
        foreach (var budget in budgets)
        {
            var expectedScope = budget.RuleId switch
            {
                "session-estimated-cost-threshold" => "session",
                "daily-estimated-cost-threshold" => "utc_day",
                "period-estimated-cost-threshold" => "rolling_period",
                _ => null,
            };
            if (expectedScope is null
                || budget.RuleVersion != "1"
                || budget.Currency != "USD"
                || budget.ScopeKind != expectedScope
                || budget.MinimumCoverageBasisPoints is < 0 or > 10_000
                || !TryCanonicalDecimal(budget.WarningThreshold, out var warning)
                || !TryCanonicalDecimal(budget.CriticalThreshold, out var critical)
                || warning > critical
                || (expectedScope == "rolling_period"
                    ? budget.WindowDays is < 2 or > 366
                    : budget.WindowDays is not null))
                throw new ArgumentException("Cost budget entry is invalid.");
        }
    }

    private static byte[] SerializeProjection(
        string? predecessor,
        string catalogSha,
        IReadOnlyList<CostSourceEntryV1> sources,
        IReadOnlyList<CostBudgetEntryV1> budgets,
        DateTimeOffset createdAt) =>
        Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            if (predecessor is null) writer.WriteNull("predecessor_configuration_id");
            else writer.WriteString("predecessor_configuration_id", predecessor);
            writer.WriteString("catalog_sha256", catalogSha);
            WriteSources(writer, sources);
            WriteBudgets(writer, budgets);
            writer.WriteString("created_at_utc", CostJsonV1.Timestamp(createdAt));
            writer.WriteEndObject();
        });

    private static void WriteProjectionProperties(Utf8JsonWriter writer, CostConfigurationV1 value)
    {
        if (value.PredecessorConfigurationId is null) writer.WriteNull("predecessor_configuration_id");
        else writer.WriteString("predecessor_configuration_id", value.PredecessorConfigurationId);
        writer.WriteString("catalog_sha256", value.CatalogSha256);
        WriteSources(writer, value.SourceEntries);
        WriteBudgets(writer, value.BudgetEntries);
        writer.WriteString("created_at_utc", CostJsonV1.Timestamp(value.CreatedAtUtc));
    }

    private static void WriteSources(Utf8JsonWriter writer, IReadOnlyList<CostSourceEntryV1> values)
    {
        writer.WriteStartArray("source_entries");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("source_surface", value.SourceSurface);
            writer.WriteString("application_version", value.ApplicationVersion);
            writer.WriteString("adapter_capability_version", value.AdapterCapabilityVersion);
            writer.WriteString("provider", value.Provider);
            writer.WriteString("billing_mode", value.BillingMode);
            writer.WriteString("pricing_route", value.PricingRoute);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteBudgets(Utf8JsonWriter writer, IReadOnlyList<CostBudgetEntryV1> values)
    {
        writer.WriteStartArray("budget_entries");
        foreach (var value in values)
        {
            writer.WriteStartObject();
            writer.WriteString("rule_id", value.RuleId);
            writer.WriteString("rule_version", value.RuleVersion);
            writer.WriteBoolean("enabled", value.Enabled);
            writer.WriteString("currency", value.Currency);
            writer.WriteString("warning_threshold", value.WarningThreshold);
            writer.WriteString("critical_threshold", value.CriticalThreshold);
            writer.WriteNumber("minimum_coverage_basis_points", value.MinimumCoverageBasisPoints);
            writer.WriteString("scope_kind", value.ScopeKind);
            if (value.WindowDays is null) writer.WriteNull("window_days");
            else writer.WriteNumber("window_days", value.WindowDays.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static int CompareSources(CostSourceEntryV1 left, CostSourceEntryV1 right)
    {
        var surface = string.CompareOrdinal(left.SourceSurface, right.SourceSurface);
        return surface != 0 ? surface : string.CompareOrdinal(left.ApplicationVersion, right.ApplicationVersion);
    }

    private static int CompareBudgets(CostBudgetEntryV1 left, CostBudgetEntryV1 right) =>
        Array.IndexOf(RuleOrder, left.RuleId).CompareTo(Array.IndexOf(RuleOrder, right.RuleId));

    private static void RejectDuplicates(
        IReadOnlyList<CostSourceEntryV1> sources,
        IReadOnlyList<CostBudgetEntryV1> budgets)
    {
        if (sources.Select(item => (item.SourceSurface, item.ApplicationVersion)).Distinct().Count() != sources.Count
            || budgets.Select(item => item.RuleId).Distinct(StringComparer.Ordinal).Count() != budgets.Count)
            throw new ArgumentException("Cost configuration contains duplicate entries.");
    }

    private static bool TryCanonicalDecimal(string value, out decimal result)
    {
        result = 0;
        return Regex.IsMatch(value, "^(0|[1-9][0-9]*)(\\.[0-9]*[1-9])?$", RegexOptions.CultureInvariant)
            && decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result)
            && result >= 0;
    }

    internal static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            write(writer);
        return stream.ToArray();
    }
}

public static class CostConfigurationConsumerV1
{
    public static CostConsumerResult<CostConfigurationV1> Consume(ReadOnlyMemory<byte> canonicalBytes)
    {
        if (canonicalBytes.Length > CostConfigurationCanonicalJsonV1.MaximumBytes)
            return new(CostConsumerStatus.TooLarge, null);
        if (canonicalBytes.Length == 0) return new(CostConsumerStatus.Invalid, null);
        try
        {
            var bytes = canonicalBytes.ToArray();
            using var document = CostJsonV1.Parse(bytes, 16);
            var schema = document.RootElement.TryGetProperty("schema_version", out var schemaElement)
                ? schemaElement.GetString()
                : null;
            if (schema != CostConfigurationCanonicalJsonV1.SchemaVersion)
                return new(CostContractSchemaVersionV1.IsRecognizedFuture(
                    schema,
                    "cost.configuration.v")
                    ? CostConsumerStatus.Unsupported
                    : CostConsumerStatus.Invalid, null);
            var value = JsonSerializer.Deserialize<CostConfigurationV1>(bytes, CostJsonV1.Options);
            if (value is null) return new(CostConsumerStatus.Invalid, null);
            var frozen = CostConfigurationCanonicalJsonV1.Create(
                value.PredecessorConfigurationId,
                value.CatalogSha256,
                value.SourceEntries,
                value.BudgetEntries,
                value.CreatedAtUtc);
            var serialized = CostConfigurationCanonicalJsonV1.Serialize(frozen);
            return value.ConfigurationId == frozen.ConfigurationId
                && bytes.AsSpan().SequenceEqual(serialized)
                ? new(CostConsumerStatus.Success, frozen)
                : new(CostConsumerStatus.Invalid, null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return new(CostConsumerStatus.Invalid, null);
        }
    }
}

public static class CostConfigurationPreviewCanonicalJsonV1
{
    internal const string SchemaVersion = "cost.configuration-preview.v1";

    public static CostConfigurationPreviewV1 Create(
        CostConfigurationV1 configuration,
        long expectedHeadRevision,
        string? expectedConfigurationId,
        string catalogSha256,
        string selectionDigest,
        int proposedMatchCount,
        int currentMatchCount,
        string currentMatchCountState,
        int overlapCount,
        string overlapCountState)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (expectedHeadRevision < 0
            || (expectedHeadRevision == 0) != (expectedConfigurationId is null)
            || expectedConfigurationId != configuration.PredecessorConfigurationId
            || catalogSha256 != configuration.CatalogSha256
            || !Regex.IsMatch(selectionDigest, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || proposedMatchCount is < 0 or > 2000
            || currentMatchCount is < 0 or > 2001
            || overlapCount is < 0 or > 2001
            || currentMatchCountState is not ("exact" or "lower_bound")
            || overlapCountState is not ("exact" or "lower_bound")
            || currentMatchCountState == "exact" && currentMatchCount > 2000
            || currentMatchCountState == "lower_bound" && currentMatchCount != 2001
            || (currentMatchCountState == "exact") != (overlapCountState == "exact")
            || overlapCount > proposedMatchCount
            || overlapCount > currentMatchCount)
            throw new ArgumentException("Cost configuration preview fields are invalid.");
        var draft = new CostConfigurationPreviewV1(
            SchemaVersion,
            CostConfigurationCanonicalJsonV1.Create(
                configuration.PredecessorConfigurationId,
                configuration.CatalogSha256,
                configuration.SourceEntries,
                configuration.BudgetEntries,
                configuration.CreatedAtUtc),
            expectedHeadRevision,
            expectedConfigurationId,
            catalogSha256,
            selectionDigest,
            proposedMatchCount,
            currentMatchCount,
            currentMatchCountState,
            overlapCount,
            overlapCountState,
            string.Empty);
        var digest = CostIdentityV1.Hash("cost-configuration-preview/v1", SerializeProjection(draft));
        return draft with { PreviewDigest = digest };
    }

    public static byte[] Serialize(CostConfigurationPreviewV1 preview)
    {
        var created = Create(
            preview.Configuration,
            preview.ExpectedHeadRevision,
            preview.ExpectedConfigurationId,
            preview.CatalogSha256,
            preview.SelectionDigest,
            preview.ProposedMatchCount,
            preview.CurrentMatchCount,
            preview.CurrentMatchCountState,
            preview.OverlapCount,
            preview.OverlapCountState);
        if (preview.SchemaVersion != SchemaVersion || preview.PreviewDigest != created.PreviewDigest)
            throw new ArgumentException("Cost preview identity is invalid.", nameof(preview));
        return CostConfigurationCanonicalJsonV1.Write(writer =>
        {
            WriteProperties(writer, created, includeDigest: true);
        });
    }

    internal static byte[] SerializeProjection(CostConfigurationPreviewV1 preview) =>
        CostConfigurationCanonicalJsonV1.Write(writer => WriteProperties(writer, preview, includeDigest: false));

    private static void WriteProperties(Utf8JsonWriter writer, CostConfigurationPreviewV1 preview, bool includeDigest)
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", SchemaVersion);
        writer.WritePropertyName("configuration");
        using (var configuration = JsonDocument.Parse(CostConfigurationCanonicalJsonV1.Serialize(preview.Configuration)))
            configuration.RootElement.WriteTo(writer);
        writer.WriteNumber("expected_head_revision", preview.ExpectedHeadRevision);
        if (preview.ExpectedConfigurationId is null) writer.WriteNull("expected_configuration_id");
        else writer.WriteString("expected_configuration_id", preview.ExpectedConfigurationId);
        writer.WriteString("catalog_sha256", preview.CatalogSha256);
        writer.WriteString("selection_digest", preview.SelectionDigest);
        writer.WriteNumber("proposed_match_count", preview.ProposedMatchCount);
        writer.WriteNumber("current_match_count", preview.CurrentMatchCount);
        writer.WriteString("current_match_count_state", preview.CurrentMatchCountState);
        writer.WriteNumber("overlap_count", preview.OverlapCount);
        writer.WriteString("overlap_count_state", preview.OverlapCountState);
        if (includeDigest) writer.WriteString("preview_digest", preview.PreviewDigest);
        writer.WriteEndObject();
    }
}

public static class CostConfigurationPreviewConsumerV1
{
    public static CostConsumerResult<CostConfigurationPreviewV1> Consume(ReadOnlyMemory<byte> canonicalBytes)
    {
        if (canonicalBytes.Length > CostConfigurationCanonicalJsonV1.MaximumBytes)
            return new(CostConsumerStatus.TooLarge, null);
        if (canonicalBytes.Length == 0) return new(CostConsumerStatus.Invalid, null);
        try
        {
            var bytes = canonicalBytes.ToArray();
            using var document = CostJsonV1.Parse(bytes, 16);
            var schema = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema_version", out var schemaElement)
                && schemaElement.ValueKind == JsonValueKind.String
                    ? schemaElement.GetString()
                    : null;
            if (schema != CostConfigurationPreviewCanonicalJsonV1.SchemaVersion)
                return new(CostContractSchemaVersionV1.IsRecognizedFuture(
                    schema,
                    "cost.configuration-preview.v")
                    ? CostConsumerStatus.Unsupported
                    : CostConsumerStatus.Invalid, null);
            var value = JsonSerializer.Deserialize<CostConfigurationPreviewV1>(bytes, CostJsonV1.Options);
            if (value is null) return new(CostConsumerStatus.Invalid, null);
            var frozen = CostConfigurationPreviewCanonicalJsonV1.Create(
                value.Configuration,
                value.ExpectedHeadRevision,
                value.ExpectedConfigurationId,
                value.CatalogSha256,
                value.SelectionDigest,
                value.ProposedMatchCount,
                value.CurrentMatchCount,
                value.CurrentMatchCountState,
                value.OverlapCount,
                value.OverlapCountState);
            return value.PreviewDigest == frozen.PreviewDigest
                && bytes.AsSpan().SequenceEqual(CostConfigurationPreviewCanonicalJsonV1.Serialize(frozen))
                ? new(CostConsumerStatus.Success, frozen)
                : new(CostConsumerStatus.Invalid, null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return new(CostConsumerStatus.Invalid, null);
        }
    }
}

public static class CostConfigurationCommitConsumerV1
{
    private const string RequestSchema = "cost.configuration-commit.v1";
    private const string PreviewSchema = "cost.configuration-preview.v1";
    private const string ResultSchema = "cost.configuration-commit-result.v1";

    public static byte[] SerializeRequest(CostConfigurationPreviewV1 preview)
    {
        var previewBytes = CostConfigurationPreviewCanonicalJsonV1.Serialize(preview);
        var previewText = Encoding.UTF8.GetString(previewBytes);
        var previewPrefix = "{\"schema_version\":\"" + PreviewSchema + "\"";
        if (!previewText.StartsWith(previewPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Cost configuration preview schema is invalid.", nameof(preview));
        return Encoding.UTF8.GetBytes(
            "{\"schema_version\":\"" + RequestSchema + "\"" + previewText[previewPrefix.Length..]);
    }

    public static CostConsumerResult<CostConfigurationPreviewV1> ConsumeRequest(ReadOnlyMemory<byte> canonicalBytes)
    {
        if (canonicalBytes.Length > CostConfigurationCanonicalJsonV1.MaximumBytes)
            return new(CostConsumerStatus.TooLarge, null);
        try
        {
            var bytes = canonicalBytes.ToArray();
            using var document = CostJsonV1.Parse(bytes, 16);
            var schema = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema_version", out var schemaElement)
                && schemaElement.ValueKind == JsonValueKind.String
                    ? schemaElement.GetString()
                    : null;
            if (schema != RequestSchema)
                return new(CostContractSchemaVersionV1.IsRecognizedFuture(
                    schema,
                    "cost.configuration-commit.v")
                    ? CostConsumerStatus.Unsupported
                    : CostConsumerStatus.Invalid, null);
            var text = Encoding.UTF8.GetString(bytes);
            var requestPrefix = "{\"schema_version\":\"" + RequestSchema + "\"";
            if (!text.StartsWith(requestPrefix, StringComparison.Ordinal))
                return new(CostConsumerStatus.Invalid, null);
            var previewBytes = Encoding.UTF8.GetBytes(
                "{\"schema_version\":\"" + PreviewSchema + "\"" + text[requestPrefix.Length..]);
            var consumed = CostConfigurationPreviewConsumerV1.Consume(previewBytes);
            return consumed.Status == CostConsumerStatus.Success
                && bytes.AsSpan().SequenceEqual(SerializeRequest(consumed.Value!))
                ? consumed
                : new(CostConsumerStatus.Invalid, null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return new(CostConsumerStatus.Invalid, null);
        }
    }

    public static byte[] SerializeResult(CostConfigurationCommitResultV1 result)
    {
        if (result.SchemaVersion != ResultSchema
            || result.HeadRevision <= 0
            || !Regex.IsMatch(result.ConfigurationId, "^cost-configuration-[0-9a-f]{64}$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(result.CatalogSha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Cost configuration commit result is invalid.", nameof(result));
        return CostConfigurationCanonicalJsonV1.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", ResultSchema);
            writer.WriteString("configuration_id", result.ConfigurationId);
            writer.WriteNumber("head_revision", result.HeadRevision);
            writer.WriteString("catalog_sha256", result.CatalogSha256);
            writer.WriteEndObject();
        });
    }

    public static CostConsumerResult<CostConfigurationCommitResultV1> ConsumeResult(ReadOnlyMemory<byte> canonicalBytes)
    {
        if (canonicalBytes.Length > 65_536) return new(CostConsumerStatus.TooLarge, null);
        try
        {
            var bytes = canonicalBytes.ToArray();
            using var document = CostJsonV1.Parse(bytes, 16);
            var schema = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema_version", out var schemaElement)
                && schemaElement.ValueKind == JsonValueKind.String
                    ? schemaElement.GetString()
                    : null;
            if (schema != ResultSchema)
                return new(CostContractSchemaVersionV1.IsRecognizedFuture(
                    schema,
                    "cost.configuration-commit-result.v")
                    ? CostConsumerStatus.Unsupported
                    : CostConsumerStatus.Invalid, null);
            var value = JsonSerializer.Deserialize<CostConfigurationCommitResultV1>(bytes, CostJsonV1.Options);
            if (value is null || value.SchemaVersion != ResultSchema)
                return new(CostConsumerStatus.Invalid, null);
            return bytes.AsSpan().SequenceEqual(SerializeResult(value))
                ? new(CostConsumerStatus.Success, value with { })
                : new(CostConsumerStatus.Invalid, null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return new(CostConsumerStatus.Invalid, null);
        }
    }

    public static CostConfigurationCommitResultV1 CreateResult(
        string configurationId,
        long headRevision,
        string catalogSha256) =>
        new(ResultSchema, configurationId, headRevision, catalogSha256);

}

internal static class CostIdentityV1
{
    internal static string Hash(string domain, ReadOnlySpan<byte> payload)
    {
        var domainBytes = Encoding.UTF8.GetBytes(domain);
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, domainBytes.Length);
        stream.Write(length);
        stream.Write(domainBytes);
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}

internal static class CostJsonV1
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16,
        Converters = { new CanonicalUtcConverter() },
    };

    internal static JsonDocument Parse(byte[] bytes, int maxDepth)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = maxDepth,
        });
        RejectDuplicates(document.RootElement);
        return document;
    }

    internal static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException();
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private sealed class CanonicalUtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || !DateTimeOffset.TryParseExact(
                    reader.GetString(),
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var value))
                throw new JsonException();
            return value;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Timestamp(value));
    }
}
