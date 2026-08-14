using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace CopilotAgentObservability.LocalMonitor.Archive;

internal static class LocalArchiveWire
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static bool HasNoSemanticQuery(string? rawQuery) =>
        rawQuery is null or "" or "?";

    internal static bool HasSupportedPostMedia(
        StringValues contentTypes,
        StringValues contentEncodings)
    {
        if (contentTypes.Count != 1
            || contentEncodings.Count != 0
            || !MediaTypeHeaderValue.TryParse(contentTypes[0], out var parsed)
            || !string.Equals(parsed.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (parsed.Parameters.Count == 0)
            return true;
        if (parsed.Parameters.Count != 1)
            return false;
        var parameter = parsed.Parameters[0];
        return string.Equals(parameter.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)
            && parameter.Value.HasValue
            && string.Equals(parameter.Value.Value, "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParseDirectQuery(
        string? rawQuery,
        out LocalArchiveDirectQuery? query,
        out LocalArchiveWireError? error)
    {
        query = null;
        error = LocalArchiveWireError.InvalidRequest;
        if (!TryFields(rawQuery, out var fields)
            || fields.Count != 2
            || !fields.TryGetValue("target_kind", out var rawKind)
            || !fields.TryGetValue("target_id", out var targetId)
            || !TryKind(rawKind, out var targetKind)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(targetId))
        {
            return false;
        }

        query = new(targetKind, targetId);
        error = null;
        return true;
    }

    internal static bool TryParseListQuery(
        string? rawQuery,
        out LocalArchiveListQuery? query,
        out LocalArchiveWireError? error)
    {
        query = null;
        error = LocalArchiveWireError.InvalidRequest;
        if (!TryFields(rawQuery, out var fields)
            || fields.Count is < 1 or > 3
            || !fields.TryGetValue("target_kind", out var rawKind)
            || fields.Keys.Any(static key => key is not ("target_kind" or "after" or "limit"))
            || !TryKind(rawKind, out var targetKind))
        {
            return false;
        }

        var limit = 50;
        if (fields.TryGetValue("limit", out var rawLimit)
            && (!IsCanonicalLimit(rawLimit) || !int.TryParse(rawLimit, out limit) || limit > 200))
        {
            return false;
        }

        LocalArchiveCursor? cursor = null;
        if (fields.TryGetValue("after", out var after))
        {
            if (after.Length == 0 || after.Any(static character => character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-' and not '_'))
            {
                return false;
            }
            if (!LocalArchiveCursorCodec.TryDecode(after, targetKind, out cursor))
            {
                error = LocalArchiveWireError.InvalidCursor;
                return false;
            }
        }

        query = new(targetKind, cursor, limit);
        error = null;
        return true;
    }

    internal static bool TryParseActionBody(
        ReadOnlyMemory<byte> bytes,
        out LocalArchiveActionRequest? request)
    {
        request = null;
        if (bytes.Span.StartsWith("\uFEFF"u8))
            return false;
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            if (!TryClosedObject(document.RootElement,
                    ["schema_version", "action", "target_kind", "targets"], out var root)
                || !TryExactString(root["schema_version"], "local-archive-action.v1")
                || !TryAction(root["action"], out var action)
                || !TryKind(root["target_kind"], out var targetKind)
                || root["targets"].ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var count = root["targets"].GetArrayLength();
            if (count is < 1 or > 200
                || targetKind == LocalArchiveTargetKind.Repository && count != 1)
            {
                return false;
            }

            var targets = new LocalArchiveMutationTarget[count];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in root["targets"].EnumerateArray())
            {
                if (!TryClosedObject(item, ["target_id", "expected_revision"], out var target)
                    || !TryString(target["target_id"], out var id)
                    || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(id)
                    || !ids.Add(id)
                    || !TryRevision(target["expected_revision"], out var revision))
                {
                    return false;
                }
                targets[index++] = new(id, revision);
            }

            request = new(action, targetKind, targets);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static ReadOnlyMemory<byte> WriteDirect(
        LocalArchiveTargetKind targetKind,
        LocalArchiveMutationTargetSuccess target)
    {
        if (!IsDefined(targetKind) || target is null || !LocalArchiveValidation.IsValidTargetFact(target))
            throw new InvalidOperationException("local_archive_wire_snapshot_invalid");
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", "local-archive.response.v1");
            writer.WriteString("target_kind", Kind(targetKind));
            WriteTarget(writer, target);
            writer.WriteEndObject();
        });
    }

    internal static ReadOnlyMemory<byte> WriteAction(LocalArchiveMutationSuccess success)
    {
        ArgumentNullException.ThrowIfNull(success);
        if (!LocalArchiveValidation.TryFreezeMutationSuccess(
                success.Action, success.TargetKind, success.Targets, out var frozen))
        {
            throw new InvalidOperationException("local_archive_wire_snapshot_invalid");
        }
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", "local-archive-action.response.v1");
            writer.WriteString("action", Action(frozen!.Action));
            writer.WriteString("target_kind", Kind(frozen.TargetKind));
            writer.WriteStartArray("targets");
            foreach (var target in frozen.Targets)
            {
                writer.WriteStartObject();
                WriteTarget(writer, target);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    internal static ReadOnlyMemory<byte> WriteList(
        LocalArchiveTargetKind targetKind,
        IReadOnlyList<LocalArchiveMutationTargetSuccess> items,
        string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!IsDefined(targetKind) || items.Count > 200)
            throw new InvalidOperationException("local_archive_wire_snapshot_invalid");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        LocalArchiveMutationTargetSuccess? previous = null;
        foreach (var item in items)
        {
            if (item is null
                || item.State != LocalArchiveState.Archived
                || !LocalArchiveValidation.IsValidTargetFact(item)
                || !ids.Add(item.TargetId)
                || previous is not null && CompareListOrder(previous, item) >= 0)
            {
                throw new InvalidOperationException("local_archive_wire_snapshot_invalid");
            }
            previous = item;
        }
        if (nextCursor is not null
            && (previous is null
                || !string.Equals(nextCursor, LocalArchiveCursorCodec.Encode(
                    targetKind, new(previous.ArchivedAt!, previous.TargetId)), StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("local_archive_wire_snapshot_invalid");
        }

        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", "local-archived-items.response.v1");
            writer.WriteString("target_kind", Kind(targetKind));
            writer.WriteStartArray("items");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                WriteTarget(writer, item);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (nextCursor is null)
                writer.WriteNull("next_cursor");
            else
                writer.WriteString("next_cursor", nextCursor);
            writer.WriteEndObject();
        });
    }

    internal static ReadOnlyMemory<byte> ErrorBytes(LocalArchiveWireError error) => error switch
    {
        LocalArchiveWireError.InvalidHost => "{\"error\":\"invalid_host\"}"u8.ToArray(),
        LocalArchiveWireError.InvalidRequest => "{\"error\":\"invalid_request\"}"u8.ToArray(),
        LocalArchiveWireError.InvalidCursor => "{\"error\":\"invalid_cursor\"}"u8.ToArray(),
        LocalArchiveWireError.CsrfRejected => "{\"error\":\"csrf_rejected\"}"u8.ToArray(),
        LocalArchiveWireError.TargetNotFound => "{\"error\":\"target_not_found\"}"u8.ToArray(),
        LocalArchiveWireError.MethodNotAllowed => "{\"error\":\"method_not_allowed\"}"u8.ToArray(),
        LocalArchiveWireError.RevisionConflict => "{\"error\":\"revision_conflict\"}"u8.ToArray(),
        LocalArchiveWireError.RequestTooLarge => "{\"error\":\"request_too_large\"}"u8.ToArray(),
        LocalArchiveWireError.UnsupportedMediaType => "{\"error\":\"unsupported_media_type\"}"u8.ToArray(),
        LocalArchiveWireError.ArchiveStoreUnavailable => "{\"error\":\"archive_store_unavailable\"}"u8.ToArray(),
        LocalArchiveWireError.PersistenceBusy => "{\"error\":\"persistence_busy\"}"u8.ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    private static bool TryFields(string? rawQuery, out Dictionary<string, string> fields)
    {
        fields = new(StringComparer.Ordinal);
        if (rawQuery is null || rawQuery.Length <= 1 || rawQuery[0] != '?')
            return false;
        foreach (var component in rawQuery[1..].Split('&'))
        {
            var separator = component.IndexOf('=');
            if (separator <= 0 || separator != component.LastIndexOf('='))
                return false;
            if (!fields.TryAdd(component[..separator], component[(separator + 1)..]))
                return false;
        }
        return true;
    }

    private static bool IsCanonicalLimit(string value) =>
        value.Length is >= 1 and <= 3
        && value[0] is >= '1' and <= '9'
        && value.All(static character => character is >= '0' and <= '9');

    private static bool TryKind(string value, out LocalArchiveTargetKind targetKind)
    {
        targetKind = value switch
        {
            "session" => LocalArchiveTargetKind.Session,
            "repository" => LocalArchiveTargetKind.Repository,
            _ => default,
        };
        return value is "session" or "repository";
    }

    private static bool TryKind(JsonElement element, out LocalArchiveTargetKind targetKind)
    {
        targetKind = default;
        return TryString(element, out var value) && TryKind(value, out targetKind);
    }

    private static bool TryAction(JsonElement element, out LocalArchiveAction action)
    {
        action = default;
        if (!TryString(element, out var value))
            return false;
        action = value switch
        {
            "archive" => LocalArchiveAction.Archive,
            "restore" => LocalArchiveAction.Restore,
            _ => default,
        };
        return value is "archive" or "restore";
    }

    private static bool TryRevision(JsonElement element, out long revision)
    {
        revision = 0;
        var raw = element.GetRawText();
        return element.ValueKind == JsonValueKind.Number
            && raw.Length > 0
            && (raw == "0" || raw[0] is >= '1' and <= '9' && raw.All(static value => value is >= '0' and <= '9'))
            && long.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out revision);
    }

    private static bool TryExactString(JsonElement element, string expected) =>
        TryString(element, out var value) && string.Equals(value, expected, StringComparison.Ordinal);

    private static bool TryString(JsonElement element, out string value)
    {
        value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
        return element.ValueKind == JsonValueKind.String && element.GetString() is not null;
    }

    private static bool TryClosedObject(
        JsonElement element,
        string[] expected,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal)
                || !properties.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }
        return properties.Count == expected.Length;
    }

    private static ReadOnlyMemory<byte> Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            write(writer);
        return buffer.WrittenMemory.ToArray();
    }

    private static void WriteTarget(Utf8JsonWriter writer, LocalArchiveMutationTargetSuccess target)
    {
        writer.WriteString("target_id", target.TargetId);
        writer.WriteString("state", target.State == LocalArchiveState.Active ? "active" : "archived");
        writer.WriteNumber("revision", target.Revision);
        if (target.ArchivedAt is null) writer.WriteNull("archived_at"); else writer.WriteString("archived_at", target.ArchivedAt);
        if (target.UpdatedAt is null) writer.WriteNull("updated_at"); else writer.WriteString("updated_at", target.UpdatedAt);
    }

    private static int CompareListOrder(
        LocalArchiveMutationTargetSuccess left,
        LocalArchiveMutationTargetSuccess right)
    {
        var timestamp = string.CompareOrdinal(right.ArchivedAt, left.ArchivedAt);
        return timestamp != 0 ? timestamp : string.CompareOrdinal(right.TargetId, left.TargetId);
    }

    private static string Kind(LocalArchiveTargetKind targetKind) => targetKind switch
    {
        LocalArchiveTargetKind.Session => "session",
        LocalArchiveTargetKind.Repository => "repository",
        _ => throw new ArgumentOutOfRangeException(nameof(targetKind)),
    };

    private static string Action(LocalArchiveAction action) => action switch
    {
        LocalArchiveAction.Archive => "archive",
        LocalArchiveAction.Restore => "restore",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static bool IsDefined(LocalArchiveTargetKind targetKind) =>
        targetKind is LocalArchiveTargetKind.Session or LocalArchiveTargetKind.Repository;
}
