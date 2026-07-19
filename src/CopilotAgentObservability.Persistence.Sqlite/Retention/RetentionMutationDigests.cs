using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed record RetentionMutationDigestItem(string ItemId, RetentionStoreKind StoreKind);
public sealed record RetentionMutationExpectedStateItem(string ItemId, long Revision, RetentionPinState PinState, RetentionItemLifecycle State);
public sealed record RetentionMutationConflictItem(string ItemId, string ConflictCode, long LeaseGeneration);
public sealed record RetentionPreviewDigestInput(IReadOnlyDictionary<string, object?> PreviewFields, string ExpectedStateVersion, string TargetItemSetDigest, string? RejectionCode);

public static class RetentionMutationDigests
{
    public static string TargetItemSetDigest(IEnumerable<RetentionMutationDigestItem> items) =>
        HashPrefixed(TargetItemSetCanonicalJson(items), "sha256-");

    public static string TargetItemSetCanonicalJson(IEnumerable<RetentionMutationDigestItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var values = items
            .OrderBy(item => item.ItemId, StringComparer.Ordinal)
            .ThenBy(item => item.StoreKind)
            .Select(item => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["item_id"] = item.ItemId,
                ["store_kind"] = StoreKind(item.StoreKind)
            })
            .ToArray();
        return RetentionMutationJcs.Canonicalize(values);
    }

    public static string ExpectedStateVersion(IEnumerable<RetentionMutationExpectedStateItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var values = items
            .OrderBy(item => item.ItemId, StringComparer.Ordinal)
            .Select(item => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["item_id"] = item.ItemId,
                ["revision"] = item.Revision,
                ["pin_state"] = PinState(item.PinState),
                ["state"] = Lifecycle(item.State)
            })
            .ToArray();
        return HashPrefixed(RetentionMutationJcs.Canonicalize(values), "v1-");
    }

    public static string ConflictVersion(IEnumerable<RetentionMutationConflictItem> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        var registryOrder = RetentionMutationConflictCodes.All.Select((code, index) => (code, index)).ToDictionary(pair => pair.code, pair => pair.index, StringComparer.Ordinal);
        var values = conflicts
            .OrderBy(item => item.ItemId, StringComparer.Ordinal)
            .ThenBy(item => registryOrder.TryGetValue(item.ConflictCode, out var order) ? order : int.MaxValue)
            .ThenBy(item => item.ConflictCode, StringComparer.Ordinal)
            .Select(item => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["item_id"] = item.ItemId,
                ["conflict_code"] = item.ConflictCode,
                ["lease_generation"] = item.LeaseGeneration
            })
            .ToArray();
        return HashPrefixed(RetentionMutationJcs.Canonicalize(values), "v1-");
    }

    public static string PreviewDigest(RetentionPreviewDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.PreviewFields);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in input.PreviewFields)
            if (!IsExcludedPreviewField(pair.Key)) fields[pair.Key] = pair.Value;
        fields["expected_state_version"] = input.ExpectedStateVersion;
        fields["target_item_set_digest"] = input.TargetItemSetDigest;
        fields["rejection_code"] = input.RejectionCode;
        return HashPrefixed(RetentionMutationJcs.Canonicalize(fields), "sha256-");
    }

    public static string Canonicalize(IReadOnlyDictionary<string, object?> value) => RetentionMutationJcs.Canonicalize(value);

    internal static string StoreKind(RetentionStoreKind kind) => kind switch
    {
        RetentionStoreKind.SessionEventContent => "session_event_content",
        RetentionStoreKind.RawRecord => "raw_record",
        RetentionStoreKind.AnalysisRunRaw => "analysis_run_raw",
        RetentionStoreKind.SensitiveBundle => "sensitive_bundle",
        RetentionStoreKind.AnalysisSdkDirectory => "analysis_sdk_directory",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string Lifecycle(RetentionItemLifecycle state) => state switch
    {
        RetentionItemLifecycle.Expiring => "expiring",
        RetentionItemLifecycle.RetainedByPolicy => "retained_by_policy",
        RetentionItemLifecycle.ExpiredPendingDeletion => "expired_pending_deletion",
        RetentionItemLifecycle.DeletionQueued => "deletion_queued",
        RetentionItemLifecycle.Deleting => "deleting",
        RetentionItemLifecycle.Deleted => "deleted",
        RetentionItemLifecycle.DeletionFailed => "deletion_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    internal static string PinState(RetentionPinState state) => state switch
    {
        RetentionPinState.Pinned => "pinned",
        RetentionPinState.Unpinned => "unpinned",
        RetentionPinState.NotApplicable => "not_applicable",
        RetentionPinState.Mixed => "mixed",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static bool IsExcludedPreviewField(string key) => key is "preview_id" or "preview_digest" or "confirmation_expires_at" or "comment" or "comments";

    private static string HashPrefixed(string canonical, string prefix) => prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}

public static class RetentionMutationJcs
{
    public static string Canonicalize(object? value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case string text:
                WriteString(builder, text);
                return;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                return;
            case byte number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case sbyte number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case short number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case ushort number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case int number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case uint number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case long number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case ulong number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                return;
            case IReadOnlyDictionary<string, object?> dictionary:
                WriteObject(builder, dictionary);
                return;
            case IEnumerable<object?> sequence:
                WriteArray(builder, sequence);
                return;
            case DateTimeOffset timestamp:
                WriteString(builder, timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                return;
            default:
                throw new ArgumentException("JCS input contains an unsupported value.", nameof(value));
        }
    }

    private static void WriteObject(StringBuilder builder, IReadOnlyDictionary<string, object?> dictionary)
    {
        builder.Append('{');
        var first = true;
        foreach (var pair in dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first) builder.Append(',');
            first = false;
            WriteString(builder, pair.Key);
            builder.Append(':');
            Write(builder, pair.Value);
        }
        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, IEnumerable<object?> sequence)
    {
        builder.Append('[');
        var first = true;
        foreach (var item in sequence)
        {
            if (!first) builder.Append(',');
            first = false;
            Write(builder, item);
        }
        builder.Append(']');
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        if (value.Any(char.IsSurrogate))
        {
            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) { index++; continue; }
                if (char.IsSurrogate(value[index])) throw new ArgumentException("JCS input contains malformed UTF-16.", nameof(value));
            }
        }
        builder.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (rune.Value < 0x20) builder.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(rune.ToString());
                    break;
            }
        }
        builder.Append('"');
    }
}
