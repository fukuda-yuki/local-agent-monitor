using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal enum LocalMonitorV1SessionSearchParseStatus
{
    Success,
    InvalidRequest,
    RequestTooLarge,
}

internal sealed record LocalMonitorV1SessionSearchRequest(
    string Scope,
    string? RepositoryId,
    string ArchiveScope,
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Statuses,
    bool? HasSkill,
    bool? HasSubagent,
    bool? HasError,
    bool? HasRetry,
    string? QueryOriginal,
    string? QueryNormalized,
    string? Cursor,
    int? Limit)
{
    internal int EffectiveLimit => Limit ?? 50;
}

internal static class LocalMonitorV1UrlState
{
    internal static bool IsCursorEligible(LocalMonitorV1SessionSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.QueryOriginal is null
            && request.QueryNormalized is null
            && request.Models is { Count: 0 }
            && request.Limit is null;
    }
}

internal static class LocalMonitorV1SessionSearchRequestParser
{
    private const string SchemaVersion = "local-monitor-session-search.request.v1";
    private const int MaximumBodyBytes = 32_768;
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };

    private static readonly HashSet<string> ExpectedProperties = new(
        [
            "schema_version", "scope", "repository_id", "archive_scope", "from", "to",
            "source", "model", "status", "has_skill", "has_subagent", "has_error",
            "has_retry", "q", "cursor", "limit"
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> Sources = new(
        ["copilot-sdk", "copilot-cli", "vscode", "hook-unknown", "claude-code"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> Statuses = new(
        ["active", "completed", "failed", "unknown"],
        StringComparer.Ordinal);

    internal static LocalMonitorV1SessionSearchParseStatus Parse(
        ReadOnlyMemory<byte> bytes,
        out LocalMonitorV1SessionSearchRequest? request)
    {
        request = null;
        if (bytes.Length > MaximumBodyBytes) return LocalMonitorV1SessionSearchParseStatus.RequestTooLarge;
        if (bytes.Length >= 3
            && bytes.Span[0] == 0xef
            && bytes.Span[1] == 0xbb
            && bytes.Span[2] == 0xbf)
        {
            return LocalMonitorV1SessionSearchParseStatus.InvalidRequest;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            var root = document.RootElement;
            if (!TryProperties(root, out var properties)
                || !TryRequiredString(properties["schema_version"], out var schemaVersion)
                || !string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal)
                || !TryRequiredString(properties["scope"], out var scope)
                || scope is not ("all" or "unassigned" or "repository")
                || !TryNullableString(properties["repository_id"], out var repositoryId)
                || repositoryId is not null && !LocalMonitorV1Identity.TryParseUuidV7(repositoryId, out _)
                || scope == "repository" && repositoryId is null
                || scope != "repository" && repositoryId is not null
                || !TryRequiredString(properties["archive_scope"], out var archiveScope)
                || archiveScope is not ("active_only" or "include_archived")
                || !TryNullableString(properties["from"], out var fromText)
                || !TryNullableString(properties["to"], out var toText)
                || !TryExactUtc(fromText, out var from)
                || !TryExactUtc(toText, out var to)
                || from is not null && to is not null && from >= to
                || !TryClosedArray(properties["source"], Sources, out var sources)
                || !TryModelArray(properties["model"], out var models)
                || !TryClosedArray(properties["status"], Statuses, out var statuses)
                || !TryNullableBoolean(properties["has_skill"], out var hasSkill)
                || !TryNullableBoolean(properties["has_subagent"], out var hasSubagent)
                || !TryNullableBoolean(properties["has_error"], out var hasError)
                || !TryNullableBoolean(properties["has_retry"], out var hasRetry)
                || !TryNullableString(properties["q"], out var queryOriginal)
                || !TryNormalizeQuery(queryOriginal, out var queryNormalized)
                || !TryNullableString(properties["cursor"], out var cursor)
                || !TryLimit(properties["limit"], out var limit))
            {
                return LocalMonitorV1SessionSearchParseStatus.InvalidRequest;
            }

            request = new(
                scope!,
                repositoryId,
                archiveScope!,
                from,
                to,
                sources,
                models,
                statuses,
                hasSkill,
                hasSubagent,
                hasError,
                hasRetry,
                queryOriginal,
                queryNormalized,
                cursor,
                limit);
            return LocalMonitorV1SessionSearchParseStatus.Success;
        }
        catch (Exception exception) when (
            exception is JsonException
                or InvalidOperationException
                or ArgumentException
                or DecoderFallbackException)
        {
            return LocalMonitorV1SessionSearchParseStatus.InvalidRequest;
        }
    }

    private static bool TryProperties(JsonElement root, out Dictionary<string, JsonElement> properties)
    {
        properties = new(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
        {
            if (!ExpectedProperties.Contains(property.Name)
                || !properties.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }

        return properties.Count == ExpectedProperties.Count;
    }

    private static bool TryRequiredString(JsonElement element, out string? value)
    {
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    private static bool TryNullableString(JsonElement element, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        return TryRequiredString(element, out value);
    }

    private static bool TryNullableBoolean(JsonElement element, out bool? value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
        return element.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryExactUtc(string? value, out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (value is null) return true;
        if (value.Length != 33
            || !DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero
            || !string.Equals(
                parsed.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
                value,
                StringComparison.Ordinal))
        {
            return false;
        }

        timestamp = parsed;
        return true;
    }

    private static bool TryClosedArray(
        JsonElement element,
        IReadOnlySet<string> allowed,
        out IReadOnlyList<string> values)
    {
        values = [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 16) return false;
        var sorted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (!TryRequiredString(item, out var value)
                || !allowed.Contains(value!)
                || !sorted.Add(value!))
            {
                return false;
            }
        }

        values = Array.AsReadOnly(sorted.ToArray());
        return true;
    }

    private static bool TryModelArray(JsonElement element, out IReadOnlyList<string> values)
    {
        values = [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 16) return false;
        var sorted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (!TryRequiredString(item, out var value)
                || !TryMeasure(value!, out var scalarCount, out var utf8Bytes)
                || scalarCount is < 1 or > 128
                || utf8Bytes > 256
                || value!.EnumerateRunes().Any(IsForbiddenModelRune)
                || !sorted.Add(value))
            {
                return false;
            }
        }

        values = Array.AsReadOnly(sorted.ToArray());
        return true;
    }

    private static bool TryNormalizeQuery(string? original, out string? normalized)
    {
        normalized = null;
        if (original is null) return true;
        if (!TryMeasure(original, out var scalarCount, out var utf8Bytes)
            || scalarCount is < 1 or > 200
            || utf8Bytes > 800)
        {
            return false;
        }

        normalized = original.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return normalized.Length > 0
            && TryMeasure(normalized, out _, out var normalizedBytes)
            && normalizedBytes <= 800;
    }

    private static bool TryMeasure(string value, out int scalarCount, out int utf8Bytes)
    {
        scalarCount = 0;
        utf8Bytes = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done) return false;
            scalarCount++;
            utf8Bytes += rune.Utf8SequenceLength;
            remaining = remaining[consumed..];
        }

        return true;
    }

    private static bool IsForbiddenModelRune(Rune rune) =>
        rune.Value is >= 0x00 and <= 0x1f
            or >= 0x7f and <= 0x9f
            or 0x2028
            or 0x2029;

    private static bool TryLimit(JsonElement element, out int? limit)
    {
        limit = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Number) return false;
        var raw = element.GetRawText();
        if (raw.Length is < 1 or > 3
            || raw[0] is < '1' or > '9'
            || raw.Any(character => character is < '0' or > '9')
            || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 1 or > 200)
        {
            return false;
        }

        limit = parsed;
        return true;
    }
}
