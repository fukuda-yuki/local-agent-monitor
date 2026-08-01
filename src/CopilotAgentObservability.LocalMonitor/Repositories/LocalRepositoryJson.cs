using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CopilotAgentObservability.Telemetry.Repositories;

namespace CopilotAgentObservability.LocalMonitor.Repositories;

internal static class LocalRepositoryJson
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
        Encoder = JavaScriptEncoder.Default,
    };

    internal static bool TryParseCreate(ReadOnlyMemory<byte> bytes, out LocalRepositoryCreateRequest? request)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            var root = document.RootElement;
            if (!TryGetExactValues(root, ["schema_version", "display_name", "github_locator"], out var values)
                || !TryString(values[0], out var schema)
                || !TryString(values[1], out var displayName)
                || !TryNullableString(values[2], out var locator)
                || !string.Equals(schema, "local-repository-create.v1", StringComparison.Ordinal))
            {
                return false;
            }
            request = new(schema!, displayName!, locator);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static bool TryParseUpdate(ReadOnlyMemory<byte> bytes, out LocalRepositoryUpdateRequest? request)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            var root = document.RootElement;
            if (!TryGetExactValues(root, ["schema_version", "expected_revision", "operation", "display_name", "github_locator"], out var values)
                || !TryString(values[0], out var schema)
                || !TryPositiveInt64(values[1], out var revision)
                || !TryString(values[2], out var operation)
                || !TryNullableString(values[3], out var displayName)
                || !TryNullableString(values[4], out var locator)
                || !string.Equals(schema, "local-repository-update.v1", StringComparison.Ordinal)
                || operation switch
                {
                    "rename" => displayName is null || locator is not null,
                    "set_github_locator" => displayName is not null || locator is null,
                    _ => true,
                })
            {
                return false;
            }
            request = new(schema!, revision, operation!, displayName, locator);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static bool TryParseSessionAction(ReadOnlyMemory<byte> bytes, out LocalRepositorySessionActionRequest? request)
    {
        request = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, DocumentOptions);
            var root = document.RootElement;
            if (!TryGetExactValues(root, ["schema_version", "session_id", "expected_revision", "action", "repository_id"], out var values)
                || !TryString(values[0], out var schema)
                || !TryString(values[1], out var sessionId)
                || !TryNonNegativeInt64(values[2], out var revision)
                || !TryString(values[3], out var action)
                || !TryNullableString(values[4], out var repositoryId)
                || !string.Equals(schema, "local-session-repository-action.v1", StringComparison.Ordinal)
                || action switch
                {
                    "assign" => repositoryId is null,
                    "explicitly_unassign" or "resume_automatic" => repositoryId is not null,
                    _ => true,
                })
            {
                return false;
            }
            request = new(schema!, sessionId!, revision, action!, repositoryId);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static ReadOnlyMemory<byte> WriteRepository(int expectedStatusCode, LocalRepositoryMutationRepository value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString(LocalRepositoryExactResponse.RepositoryV1.SchemaVersionProperty, LocalRepositoryExactResponse.RepositoryV1.SchemaVersion);
            writer.WriteString(LocalRepositoryExactResponse.RepositoryV1.RepositoryId, value.RepositoryId);
            writer.WriteString(LocalRepositoryExactResponse.RepositoryV1.DisplayName, value.DisplayName);
            writer.WriteNumber(LocalRepositoryExactResponse.RepositoryV1.Revision, value.Revision);
            writer.WriteString(LocalRepositoryExactResponse.RepositoryV1.CreatedAt, Timestamp(value.CreatedAt));
            writer.WriteString(LocalRepositoryExactResponse.RepositoryV1.UpdatedAt, Timestamp(value.UpdatedAt));
            writer.WriteEndObject();
        });
        ValidateWrittenEntity(expectedStatusCode, LocalRepositoryMutationEntityKind.Repository, value.RepositoryId, value.Revision, null, bytes.Span);
        return bytes;
    }

    internal static ReadOnlyMemory<byte> WriteLocators(LocalRepositoryLocatorSnapshot value)
    {
        ValidateLocators(value);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", "local-repository-locators.v1");
            writer.WriteString("repository_id", value.RepositoryId);
            writer.WriteNumber("repository_revision", value.RepositoryRevision);
            writer.WriteStartArray("locators");
            foreach (var item in value.Locators)
            {
                writer.WriteStartObject();
                writer.WriteString("locator_id", item.LocatorId);
                writer.WriteString("kind", item.Kind);
                writer.WriteString("canonical_locator", item.CanonicalLocator);
                writer.WriteString("display_owner", item.DisplayOwner);
                writer.WriteString("display_repository", item.DisplayRepository);
                writer.WriteString("source", item.Source);
                writer.WriteBoolean("is_current", item.IsCurrent);
                writer.WriteString("created_at", Timestamp(item.CreatedAt));
                if (item.Provenance is null)
                    writer.WriteNull("provenance");
                else
                {
                    writer.WriteStartObject("provenance");
                    writer.WriteString("source_surface", item.Provenance.SourceSurface);
                    WriteNullableString(writer, "source_application_version", item.Provenance.SourceApplicationVersion);
                    writer.WriteString("trace_id", item.Provenance.TraceId);
                    writer.WriteString("span_id", item.Provenance.SpanId);
                    writer.WriteString("observed_at", Timestamp(item.Provenance.ObservedAt));
                    writer.WriteString("source_content_availability", item.Provenance.SourceContentAvailability);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    internal static ReadOnlyMemory<byte> WriteAssignment(LocalRepositoryAssignmentSnapshot value) =>
        WriteAssignment(value.SessionId, value.AssignmentRevision, value.State, value.Authority, value.RepositoryId, value.ConflictingRepositoryIds, value.UpdatedAt);

    internal static ReadOnlyMemory<byte> WriteAssignment(LocalRepositoryMutationAssignment value) =>
        WriteAssignment(value.SessionId, value.Revision, value.State, value.Authority, value.RepositoryId, value.ConflictingRepositoryIds, value.UpdatedAt);

    internal static ReadOnlyMemory<byte> ErrorBytes(LocalRepositoryError error) => error switch
    {
        LocalRepositoryError.InvalidRequest => "{\"error\":\"invalid_request\"}"u8.ToArray(),
        LocalRepositoryError.InvalidLocator => "{\"error\":\"invalid_locator\"}"u8.ToArray(),
        LocalRepositoryError.RepositoryNotFound => "{\"error\":\"repository_not_found\"}"u8.ToArray(),
        LocalRepositoryError.SessionNotFound => "{\"error\":\"session_not_found\"}"u8.ToArray(),
        LocalRepositoryError.RevisionConflict => "{\"error\":\"revision_conflict\"}"u8.ToArray(),
        LocalRepositoryError.LocatorConflict => "{\"error\":\"locator_conflict\"}"u8.ToArray(),
        LocalRepositoryError.LocatorLimitReached => "{\"error\":\"locator_limit_reached\"}"u8.ToArray(),
        LocalRepositoryError.IdempotencyConflict => "{\"error\":\"idempotency_conflict\"}"u8.ToArray(),
        LocalRepositoryError.CsrfRejected => "{\"error\":\"csrf_rejected\"}"u8.ToArray(),
        LocalRepositoryError.RequestTooLarge => "{\"error\":\"request_too_large\"}"u8.ToArray(),
        LocalRepositoryError.UnsupportedMediaType => "{\"error\":\"unsupported_media_type\"}"u8.ToArray(),
        LocalRepositoryError.MethodNotAllowed => "{\"error\":\"method_not_allowed\"}"u8.ToArray(),
        LocalRepositoryError.PersistenceBusy => "{\"error\":\"persistence_busy\"}"u8.ToArray(),
        LocalRepositoryError.LocalMonitorUiUnavailable => "{\"error\":\"local_monitor_ui_unavailable\"}"u8.ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    private static ReadOnlyMemory<byte> WriteAssignment(
        string sessionId,
        long revision,
        string state,
        string authority,
        string? repositoryId,
        IReadOnlyList<string> conflicts,
        DateTimeOffset? updatedAt)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        var bytes = Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString(LocalRepositoryExactResponse.AssignmentV1.SchemaVersionProperty, LocalRepositoryExactResponse.AssignmentV1.SchemaVersion);
            writer.WriteString(LocalRepositoryExactResponse.AssignmentV1.SessionId, sessionId);
            writer.WriteNumber(LocalRepositoryExactResponse.AssignmentV1.AssignmentRevision, revision);
            writer.WriteString(LocalRepositoryExactResponse.AssignmentV1.State, state);
            writer.WriteString(LocalRepositoryExactResponse.AssignmentV1.Authority, authority);
            WriteNullableString(writer, LocalRepositoryExactResponse.AssignmentV1.RepositoryId, repositoryId);
            writer.WriteStartArray(LocalRepositoryExactResponse.AssignmentV1.ConflictingRepositoryIds);
            foreach (var id in conflicts) writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WriteStartArray(LocalRepositoryExactResponse.AssignmentV1.ObservedLabelCandidates);
            writer.WriteEndArray();
            if (updatedAt is null)
                writer.WriteNull(LocalRepositoryExactResponse.AssignmentV1.UpdatedAt);
            else
                writer.WriteString(LocalRepositoryExactResponse.AssignmentV1.UpdatedAt, Timestamp(updatedAt.Value));
            writer.WriteEndObject();
        });
        ValidateWrittenEntity(200, LocalRepositoryMutationEntityKind.Assignment, sessionId, revision, state, bytes.Span);
        return bytes;
    }

    private static ReadOnlyMemory<byte> Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) write(writer);
        return buffer.WrittenMemory.ToArray();
    }

    private static bool TryGetExactValues(JsonElement root, string[] expected, out JsonElement[] values)
    {
        values = new JsonElement[expected.Length];
        if (root.ValueKind != JsonValueKind.Object) return false;
        var index = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (index == expected.Length || !string.Equals(property.Name, expected[index], StringComparison.Ordinal)) return false;
            values[index] = property.Value;
            index++;
        }
        return index == expected.Length;
    }

    private static bool TryString(JsonElement element, out string? value)
    {
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return value is not null;
    }

    private static bool TryNullableString(JsonElement element, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Null) { value = null; return true; }
        return TryString(element, out value);
    }

    private static bool TryPositiveInt64(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value) && value >= 1;
    }

    private static bool TryNonNegativeInt64(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value) && value >= 0;
    }

    private static void ValidateLocators(LocalRepositoryLocatorSnapshot value)
    {
        if (value is null
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(value.RepositoryId)
            || value.RepositoryRevision < 1
            || value.Locators is null
            || value.Locators.Count > 128)
        {
            throw new InvalidOperationException("local_repository_wire_snapshot_invalid");
        }

        var locatorIds = new HashSet<string>(StringComparer.Ordinal);
        var canonicalLocators = new HashSet<string>(StringComparer.Ordinal);
        var currentCount = 0;
        LocalRepositoryLocatorItem? previousHistorical = null;
        for (var index = 0; index < value.Locators.Count; index++)
        {
            var item = value.Locators[index];
            ValidateLocator(item);
            if (!locatorIds.Add(item.LocatorId) || !canonicalLocators.Add(item.CanonicalLocator))
                throw new InvalidOperationException("local_repository_wire_snapshot_invalid");
            if (item.IsCurrent)
            {
                if (index != 0 || ++currentCount != 1)
                    throw new InvalidOperationException("local_repository_wire_snapshot_invalid");
                continue;
            }

            if (previousHistorical is not null && CompareLocatorOrder(previousHistorical, item) >= 0)
                throw new InvalidOperationException("local_repository_wire_snapshot_invalid");
            previousHistorical = item;
        }

        if (value.Locators.Count > 0 && currentCount != 1)
            throw new InvalidOperationException("local_repository_wire_snapshot_invalid");
    }

    private static void ValidateLocator(LocalRepositoryLocatorItem item)
    {
        if (item is null
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(item.LocatorId)
            || item.Kind != "github_repository"
            || item.Source is not ("manual" or "observed")
            || item.Source == "manual" && item.Provenance is not null
            || item.Source == "observed" && item.Provenance is null
            || !IsUtcTimestamp(item.CreatedAt))
            throw new InvalidOperationException("local_repository_wire_snapshot_invalid");

        if (!GitHubRepositoryLocatorParser.IsExact(item.CanonicalLocator, item.DisplayOwner, item.DisplayRepository))
            throw new InvalidOperationException("local_repository_wire_snapshot_invalid");
        if (item.Provenance is { } provenance
            && (provenance.SourceSurface is not ("github-copilot-cli" or "github-copilot-vscode")
                || provenance.SourceApplicationVersion is not null && !IsVisibleApplicationVersion(provenance.SourceApplicationVersion)
                || provenance.TraceId.Length != 32 || provenance.TraceId.Any(static value => !IsLowerHex(value))
                || provenance.SpanId.Length != 16 || provenance.SpanId.Any(static value => !IsLowerHex(value))
                || provenance.SourceContentAvailability is not ("available" or "expired" or "not_retained" or "unknown")
                || !IsUtcTimestamp(provenance.ObservedAt)))
            throw new InvalidOperationException("local_repository_wire_snapshot_invalid");
    }

    private static void ValidateWrittenEntity(
        int statusCode,
        LocalRepositoryMutationEntityKind expectedKind,
        string expectedTargetId,
        long expectedRevision,
        string? expectedState,
        ReadOnlySpan<byte> bytes)
    {
        var decoded = LocalRepositoryExactResponse.ValidateMutationEntity(statusCode, bytes);
        if (decoded.Kind != expectedKind
            || !string.Equals(decoded.TargetId, expectedTargetId, StringComparison.Ordinal)
            || decoded.Revision != expectedRevision
            || !string.Equals(decoded.State, expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("local_repository_wire_snapshot_invalid");
        }
    }

    private static int CompareLocatorOrder(LocalRepositoryLocatorItem left, LocalRepositoryLocatorItem right)
    {
        var timestamp = left.CreatedAt.CompareTo(right.CreatedAt);
        return timestamp != 0 ? timestamp : CompareUuidRfcBytes(left.LocatorId, right.LocatorId);
    }

    private static int CompareUuidRfcBytes(string left, string right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        _ = Guid.Parse(left).TryWriteBytes(leftBytes, bigEndian: true, out _);
        _ = Guid.Parse(right).TryWriteBytes(rightBytes, bigEndian: true, out _);
        return leftBytes.SequenceCompareTo(rightBytes);
    }

    private static bool IsVisibleApplicationVersion(string value) => value.Length is >= 1 and <= 64
        && value.All(character => character is >= '!' and <= '~' && character is not '/' and not '\\');

    private static bool IsUtcTimestamp(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static string Timestamp(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new InvalidOperationException("local_repository_wire_timestamp_invalid");
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }
}
