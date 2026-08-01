using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CopilotAgentObservability.Telemetry.Repositories;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalRepositorySessionAction
{
    Assign,
    ExplicitlyUnassign,
    ResumeAutomatic,
}

internal enum LocalRepositoryMutationFailure
{
    InvalidRequest,
    InvalidLocator,
    RepositoryNotFound,
    SessionNotFound,
    RevisionConflict,
    LocatorConflict,
    LocatorLimitReached,
    IdempotencyConflict,
}

internal delegate ReadOnlyMemory<byte> LocalRepositorySuccessEntityWriter<in T>(T snapshot);

internal enum LocalRepositoryMutationEntityKind
{
    Repository,
    Assignment,
}

internal sealed record LocalRepositoryDecodedMutationEntity(
    LocalRepositoryMutationEntityKind Kind,
    string TargetId,
    long Revision,
    string? State);

internal sealed class LocalRepositoryExactResponse
{
    internal const string SuccessContentType = "application/json; charset=utf-8";
    internal const string SuccessCacheControl = "no-store";
    internal const int MaximumEntityBytes = 16_384;

    internal static class RepositoryV1
    {
        internal const string SchemaVersionProperty = "schema_version";
        internal const string SchemaVersion = "local-repository.v1";
        internal const string RepositoryId = "repository_id";
        internal const string DisplayName = "display_name";
        internal const string Revision = "revision";
        internal const string CreatedAt = "created_at";
        internal const string UpdatedAt = "updated_at";
    }

    internal static class AssignmentV1
    {
        internal const string SchemaVersionProperty = "schema_version";
        internal const string SchemaVersion = "local-session-repository-assignment.v1";
        internal const string SessionId = "session_id";
        internal const string AssignmentRevision = "assignment_revision";
        internal const string State = "state";
        internal const string Authority = "authority";
        internal const string RepositoryId = "repository_id";
        internal const string ConflictingRepositoryIds = "conflicting_repository_ids";
        internal const string ObservedLabelCandidates = "observed_label_candidates";
        internal const string UpdatedAt = "updated_at";
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
        Encoder = JavaScriptEncoder.Default,
    };
    private readonly byte[] entity;

    private LocalRepositoryExactResponse(
        int statusCode,
        byte[] entity,
        LocalRepositoryDecodedMutationEntity decoded) =>
        (StatusCode, this.entity, Decoded) = (statusCode, entity, decoded);

    internal int StatusCode { get; }
    internal string ContentType => SuccessContentType;
    internal string CacheControl => SuccessCacheControl;
    internal LocalRepositoryDecodedMutationEntity Decoded { get; }

    internal byte[] CopyEntity() => entity.ToArray();

    internal static LocalRepositoryExactResponse CreateSuccess(
        int operationOwnedStatusCode,
        LocalRepositoryMutationEntityKind expectedKind,
        ReadOnlyMemory<byte> entity)
    {
        var decoded = ValidateMutationEntity(operationOwnedStatusCode, entity.Span);
        if (decoded.Kind != expectedKind)
            throw InvalidEntity();
        return new(operationOwnedStatusCode, entity.ToArray(), decoded);
    }

    internal static LocalRepositoryExactResponse FromStored(
        int expectedStatusCode,
        LocalRepositoryMutationEntityKind expectedKind,
        int statusCode,
        string contentType,
        string cacheControl,
        byte[] entity)
    {
        if (statusCode != expectedStatusCode
            || !string.Equals(contentType, SuccessContentType, StringComparison.Ordinal)
            || !string.Equals(cacheControl, SuccessCacheControl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("local_repository_receipt_envelope_corrupt");
        }
        ArgumentNullException.ThrowIfNull(entity);
        return CreateSuccess(statusCode, expectedKind, entity);
    }

    internal static LocalRepositoryDecodedMutationEntity ValidateMutationEntity(
        int statusCode,
        ReadOnlySpan<byte> entity)
    {
        if (statusCode is not (200 or 201)
            || entity.IsEmpty
            || entity.Length > MaximumEntityBytes
            || entity[^1] == (byte)'\n'
            || entity.Length >= 3 && entity[0] == 0xef && entity[1] == 0xbb && entity[2] == 0xbf)
        {
            throw InvalidEntity();
        }
        try
        {
            _ = StrictUtf8.GetCharCount(entity);
            using var document = JsonDocument.Parse(entity.ToArray(), DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidEntity();

            var enumerator = root.EnumerateObject();
            if (!enumerator.MoveNext()
                || !string.Equals(enumerator.Current.Name, RepositoryV1.SchemaVersionProperty, StringComparison.Ordinal)
                || enumerator.Current.Value.ValueKind != JsonValueKind.String)
            {
                throw InvalidEntity();
            }

            var schemaVersion = enumerator.Current.Value.GetString();
            var decoded = schemaVersion switch
            {
                RepositoryV1.SchemaVersion when statusCode is 200 or 201 => DecodeRepository(root),
                AssignmentV1.SchemaVersion when statusCode == 200 => DecodeAssignment(root),
                _ => throw InvalidEntity(),
            };
            var canonical = EncodeCanonical(root, decoded.Kind);
            if (!entity.SequenceEqual(canonical))
                throw InvalidEntity();
            return decoded;
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidEntity(exception);
        }
        catch (JsonException exception)
        {
            throw InvalidEntity(exception);
        }
    }

    private static LocalRepositoryDecodedMutationEntity DecodeRepository(JsonElement root)
    {
        if (!TryGetExactValues(
                root,
                [
                    RepositoryV1.SchemaVersionProperty,
                    RepositoryV1.RepositoryId,
                    RepositoryV1.DisplayName,
                    RepositoryV1.Revision,
                    RepositoryV1.CreatedAt,
                    RepositoryV1.UpdatedAt,
                ],
                out var values)
            || !TryExactString(values[0], RepositoryV1.SchemaVersion, out _)
            || !TryString(values[1], out var repositoryId)
            || !TryString(values[2], out var displayName)
            || !TryPositiveInt64(values[3], out var revision)
            || !TryString(values[4], out var createdAt)
            || !TryString(values[5], out var updatedAt)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
            || !LocalRepositoryCatalogValidation.IsDisplayName(displayName!)
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(createdAt)
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(updatedAt))
        {
            throw InvalidEntity();
        }
        return new(LocalRepositoryMutationEntityKind.Repository, repositoryId!, revision, null);
    }

    private static LocalRepositoryDecodedMutationEntity DecodeAssignment(JsonElement root)
    {
        if (!TryGetExactValues(
                root,
                [
                    AssignmentV1.SchemaVersionProperty,
                    AssignmentV1.SessionId,
                    AssignmentV1.AssignmentRevision,
                    AssignmentV1.State,
                    AssignmentV1.Authority,
                    AssignmentV1.RepositoryId,
                    AssignmentV1.ConflictingRepositoryIds,
                    AssignmentV1.ObservedLabelCandidates,
                    AssignmentV1.UpdatedAt,
                ],
                out var values)
            || !TryExactString(values[0], AssignmentV1.SchemaVersion, out _)
            || !TryString(values[1], out var sessionId)
            || !TryNonNegativeInt64(values[2], out var revision)
            || !TryString(values[3], out var state)
            || !TryString(values[4], out var authority)
            || !TryNullableString(values[5], out var repositoryId)
            || !TryUuidArray(values[6], out var conflicts)
            || !IsEmptyArray(values[7])
            || !TryNullableString(values[8], out var updatedAt)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(sessionId)
            || repositoryId is not null && !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
            || updatedAt is not null && !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(updatedAt)
            || !IsValidAssignment(revision, state!, authority!, repositoryId, conflicts, updatedAt))
        {
            throw InvalidEntity();
        }
        return new(LocalRepositoryMutationEntityKind.Assignment, sessionId!, revision, state);
    }

    private static byte[] EncodeCanonical(JsonElement root, LocalRepositoryMutationEntityKind kind)
    {
        var values = kind == LocalRepositoryMutationEntityKind.Repository
            ? GetExactValues(root, 6)
            : GetExactValues(root, 9);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            if (kind == LocalRepositoryMutationEntityKind.Repository)
            {
                writer.WriteString(RepositoryV1.SchemaVersionProperty, RepositoryV1.SchemaVersion);
                writer.WriteString(RepositoryV1.RepositoryId, values[1].GetString());
                writer.WriteString(RepositoryV1.DisplayName, values[2].GetString());
                writer.WriteNumber(RepositoryV1.Revision, values[3].GetInt64());
                writer.WriteString(RepositoryV1.CreatedAt, values[4].GetString());
                writer.WriteString(RepositoryV1.UpdatedAt, values[5].GetString());
            }
            else
            {
                writer.WriteString(AssignmentV1.SchemaVersionProperty, AssignmentV1.SchemaVersion);
                writer.WriteString(AssignmentV1.SessionId, values[1].GetString());
                writer.WriteNumber(AssignmentV1.AssignmentRevision, values[2].GetInt64());
                writer.WriteString(AssignmentV1.State, values[3].GetString());
                writer.WriteString(AssignmentV1.Authority, values[4].GetString());
                WriteNullableString(writer, AssignmentV1.RepositoryId, values[5].GetString());
                writer.WriteStartArray(AssignmentV1.ConflictingRepositoryIds);
                foreach (var value in values[6].EnumerateArray())
                    writer.WriteStringValue(value.GetString());
                writer.WriteEndArray();
                writer.WriteStartArray(AssignmentV1.ObservedLabelCandidates);
                writer.WriteEndArray();
                WriteNullableString(writer, AssignmentV1.UpdatedAt, values[8].GetString());
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static bool IsValidAssignment(
        long revision,
        string state,
        string authority,
        string? repositoryId,
        IReadOnlyList<string> conflicts,
        string? updatedAt)
    {
        var validState = (state, authority, repositoryId) switch
        {
            ("assigned", "automatic" or "manual", not null) => conflicts.Count == 0,
            ("unassigned", "none", null) => conflicts.Count == 0,
            ("explicitly_unassigned", "manual", null) => conflicts.Count == 0,
            ("conflict", "automatic", null) => conflicts.Count is >= 2 and <= 128,
            _ => false,
        };
        return validState
            && (revision == 0
                ? state == "unassigned" && authority == "none" && repositoryId is null && conflicts.Count == 0 && updatedAt is null
                : updatedAt is not null);
    }

    private static bool TryUuidArray(JsonElement value, out IReadOnlyList<string> result)
    {
        result = [];
        if (value.ValueKind != JsonValueKind.Array)
            return false;
        var ids = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (!TryString(item, out var id)
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(id)
                || ids.Count == 128
                || ids.Count > 0 && CompareUuidRfcBytes(ids[^1], id!) >= 0)
            {
                return false;
            }
            ids.Add(id!);
        }
        result = ids;
        return true;
    }

    private static int CompareUuidRfcBytes(string left, string right)
    {
        Span<byte> leftBytes = stackalloc byte[16];
        Span<byte> rightBytes = stackalloc byte[16];
        _ = Guid.Parse(left).TryWriteBytes(leftBytes, bigEndian: true, out _);
        _ = Guid.Parse(right).TryWriteBytes(rightBytes, bigEndian: true, out _);
        return leftBytes.SequenceCompareTo(rightBytes);
    }

    private static bool IsEmptyArray(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && !value.EnumerateArray().MoveNext();

    private static bool TryGetExactValues(JsonElement root, string[] expected, out JsonElement[] values)
    {
        values = new JsonElement[expected.Length];
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        var index = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (index == expected.Length || !string.Equals(property.Name, expected[index], StringComparison.Ordinal))
                return false;
            values[index++] = property.Value;
        }
        return index == expected.Length;
    }

    private static JsonElement[] GetExactValues(JsonElement root, int count)
    {
        var values = new JsonElement[count];
        var index = 0;
        foreach (var property in root.EnumerateObject())
            values[index++] = property.Value;
        return values;
    }

    private static bool TryExactString(JsonElement value, string expected, out string? actual) =>
        TryString(value, out actual) && string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool TryString(JsonElement value, out string? result)
    {
        result = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return result is not null;
    }

    private static bool TryNullableString(JsonElement value, out string? result)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            result = null;
            return true;
        }
        return TryString(value, out result);
    }

    private static bool TryPositiveInt64(JsonElement value, out long result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result) && result >= 1;
    }

    private static bool TryNonNegativeInt64(JsonElement value, out long result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result) && result >= 0;
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string property, string? value)
    {
        if (value is null)
            writer.WriteNull(property);
        else
            writer.WriteString(property, value);
    }

    private static InvalidOperationException InvalidEntity(Exception? inner = null) =>
        new("local_repository_success_entity_invalid", inner);
}

internal abstract record LocalRepositoryMutationResult;
internal sealed record LocalRepositoryMutationSucceeded(LocalRepositoryExactResponse Response, bool IsReplay) : LocalRepositoryMutationResult;
internal sealed record LocalRepositoryMutationRejected(LocalRepositoryMutationFailure Failure) : LocalRepositoryMutationResult;
internal sealed record LocalRepositoryMutationBusy : LocalRepositoryMutationResult;

internal sealed record LocalRepositoryCreateInput(string DisplayName, string? GitHubLocator);
internal sealed record LocalRepositoryRenameInput(string RepositoryId, long ExpectedRevision, string DisplayName);
internal sealed record LocalRepositorySetLocatorInput(string RepositoryId, long ExpectedRevision, string GitHubLocator);
internal sealed record LocalRepositorySessionActionInput(string SessionId, long ExpectedRevision, string Action, string? RepositoryId);

internal enum LocalRepositoryPreparationFailure
{
    InvalidRequest,
    InvalidLocator,
    InvalidRepositoryTarget,
    InvalidSessionTarget,
}

internal abstract record LocalRepositoryPreparationResult<TPrepared>;
internal sealed record LocalRepositoryPreparationSucceeded<TPrepared>(TPrepared Prepared) : LocalRepositoryPreparationResult<TPrepared>;
internal sealed record LocalRepositoryPreparationRejected<TPrepared>(LocalRepositoryPreparationFailure Failure) : LocalRepositoryPreparationResult<TPrepared>;

internal sealed record LocalRepositoryMutationRepository(
    string RepositoryId,
    string DisplayName,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record LocalRepositoryMutationAssignment(
    string SessionId,
    long Revision,
    string State,
    string Authority,
    string? RepositoryId,
    IReadOnlyList<string> ConflictingRepositoryIds,
    DateTimeOffset? UpdatedAt);

internal sealed record LocalRepositoryLocatorSnapshot(
    string RepositoryId,
    long RepositoryRevision,
    IReadOnlyList<LocalRepositoryLocatorItem> Locators);

internal sealed record LocalRepositoryLocatorItem(
    string LocatorId,
    string Kind,
    string CanonicalLocator,
    string DisplayOwner,
    string DisplayRepository,
    string Source,
    bool IsCurrent,
    DateTimeOffset CreatedAt,
    LocalRepositoryObservedLocatorProvenance? Provenance);

internal sealed record LocalRepositoryObservedLocatorProvenance(
    string SourceSurface,
    string? SourceApplicationVersion,
    string TraceId,
    string SpanId,
    DateTimeOffset ObservedAt,
    string SourceContentAvailability);

internal sealed record LocalRepositoryAssignmentSnapshot(
    string SessionId,
    long AssignmentRevision,
    string State,
    string Authority,
    string? RepositoryId,
    IReadOnlyList<string> ConflictingRepositoryIds,
    DateTimeOffset? UpdatedAt);

internal abstract record LocalRepositoryLocatorReadResult;
internal sealed record LocalRepositoryLocatorsFound(LocalRepositoryLocatorSnapshot Value) : LocalRepositoryLocatorReadResult;
internal sealed record LocalRepositoryLocatorRepositoryNotFound : LocalRepositoryLocatorReadResult;
internal sealed record LocalRepositoryLocatorReadBusy : LocalRepositoryLocatorReadResult;
internal sealed record LocalRepositoryLocatorReadCorrupt : LocalRepositoryLocatorReadResult;

internal abstract record LocalRepositoryAssignmentReadResult;
internal sealed record LocalRepositoryAssignmentFound(LocalRepositoryAssignmentSnapshot Value) : LocalRepositoryAssignmentReadResult;
internal sealed record LocalRepositoryAssignmentSessionNotFound : LocalRepositoryAssignmentReadResult;
internal sealed record LocalRepositoryAssignmentReadBusy : LocalRepositoryAssignmentReadResult;
internal sealed record LocalRepositoryAssignmentReadCorrupt : LocalRepositoryAssignmentReadResult;

internal sealed class LocalRepositoryCatalogApplication
{
    private const string CollectionRoute = "/api/local-monitor/v1/repositories";
    private const string ItemRoute = "/api/local-monitor/v1/repositories/{repositoryId}";
    private const string SessionActionRoute = "/api/local-monitor/v1/session-repository-actions";
    private readonly SqliteLocalRepositoryCatalogStore store;
    private readonly object preparedSeal = new();
    private readonly object storeInputSeal = new();

    internal LocalRepositoryCatalogApplication(SqliteLocalRepositoryCatalogStore store) =>
        this.store = store ?? throw new ArgumentNullException(nameof(store));

    private sealed record PreparedCreateState(
        string Method,
        string RouteTemplate,
        string Operation,
        int ExpectedSuccessStatus,
        string DisplayName,
        GitHubRepositoryLocator? Locator);

    private sealed record PreparedRenameState(
        string Method,
        string RouteTemplate,
        string Operation,
        int ExpectedSuccessStatus,
        string RepositoryId,
        long ExpectedRevision,
        string DisplayName);

    private sealed record PreparedSetLocatorState(
        string Method,
        string RouteTemplate,
        string Operation,
        int ExpectedSuccessStatus,
        string RepositoryId,
        long ExpectedRevision,
        GitHubRepositoryLocator Locator);

    private sealed record PreparedSessionActionState(
        string Method,
        string RouteTemplate,
        string Operation,
        int ExpectedSuccessStatus,
        string SessionId,
        long ExpectedRevision,
        LocalRepositorySessionAction Action,
        string ActionValue,
        string? RepositoryId);

    internal sealed class PreparedCreate
    {
        private readonly LocalRepositoryCatalogApplication owner;
        private readonly object seal;
        private readonly object state;
        private PreparedCreate(LocalRepositoryCatalogApplication owner, object seal, object state) =>
            (this.owner, this.seal, this.state) = (owner, seal, state);
        internal static PreparedCreate Create(LocalRepositoryCatalogApplication owner, object seal, object state) =>
            owner is not null && owner.OwnsPreparedSeal(seal) && state is PreparedCreateState
                ? new(owner, seal, state)
                : throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        internal object Unseal(LocalRepositoryCatalogApplication expectedOwner, object expectedSeal) =>
            ReferenceEquals(owner, expectedOwner) && ReferenceEquals(seal, expectedSeal) && expectedOwner.OwnsPreparedSeal(seal) && state is PreparedCreateState exact
                ? expectedOwner.ValidatePreparedState(exact)
                : throw new InvalidOperationException("local_repository_prepared_capability_invalid");
    }

    internal sealed class PreparedRename
    {
        private readonly LocalRepositoryCatalogApplication owner;
        private readonly object seal;
        private readonly object state;
        private PreparedRename(LocalRepositoryCatalogApplication owner, object seal, object state) =>
            (this.owner, this.seal, this.state) = (owner, seal, state);
        internal static PreparedRename Create(LocalRepositoryCatalogApplication owner, object seal, object state) =>
            owner is not null && owner.OwnsPreparedSeal(seal) && state is PreparedRenameState
                ? new(owner, seal, state)
                : throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        internal object Unseal(LocalRepositoryCatalogApplication expectedOwner, object expectedSeal) =>
            ReferenceEquals(owner, expectedOwner) && ReferenceEquals(seal, expectedSeal) && expectedOwner.OwnsPreparedSeal(seal) && state is PreparedRenameState exact
                ? expectedOwner.ValidatePreparedState(exact)
                : throw new InvalidOperationException("local_repository_prepared_capability_invalid");
    }

    internal sealed class PreparedSetLocator
    {
        private readonly LocalRepositoryCatalogApplication owner;
        private readonly object seal;
        private readonly object state;
        private PreparedSetLocator(LocalRepositoryCatalogApplication owner, object seal, object state) =>
            (this.owner, this.seal, this.state) = (owner, seal, state);
        internal static PreparedSetLocator Create(LocalRepositoryCatalogApplication owner, object seal, object state) =>
            owner is not null && owner.OwnsPreparedSeal(seal) && state is PreparedSetLocatorState
                ? new(owner, seal, state)
                : throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        internal object Unseal(LocalRepositoryCatalogApplication expectedOwner, object expectedSeal) =>
            ReferenceEquals(owner, expectedOwner) && ReferenceEquals(seal, expectedSeal) && expectedOwner.OwnsPreparedSeal(seal) && state is PreparedSetLocatorState exact
                ? expectedOwner.ValidatePreparedState(exact)
                : throw new InvalidOperationException("local_repository_prepared_capability_invalid");
    }

    internal sealed class PreparedSessionAction
    {
        private readonly LocalRepositoryCatalogApplication owner;
        private readonly object seal;
        private readonly object state;
        private PreparedSessionAction(LocalRepositoryCatalogApplication owner, object seal, object state) =>
            (this.owner, this.seal, this.state) = (owner, seal, state);
        internal static PreparedSessionAction Create(LocalRepositoryCatalogApplication owner, object seal, object state) =>
            owner is not null && owner.OwnsPreparedSeal(seal) && state is PreparedSessionActionState
                ? new(owner, seal, state)
                : throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        internal object Unseal(LocalRepositoryCatalogApplication expectedOwner, object expectedSeal) =>
            ReferenceEquals(owner, expectedOwner) && ReferenceEquals(seal, expectedSeal) && expectedOwner.OwnsPreparedSeal(seal) && state is PreparedSessionActionState exact
                ? expectedOwner.ValidatePreparedState(exact)
                : throw new InvalidOperationException("local_repository_prepared_capability_invalid");
    }

    internal LocalRepositoryPreparationResult<PreparedCreate> PrepareCreate(LocalRepositoryCreateInput input)
    {
        if (input is null || !TryNormalizeDisplayName(input.DisplayName, out var displayName))
            return new LocalRepositoryPreparationRejected<PreparedCreate>(LocalRepositoryPreparationFailure.InvalidRequest);
        GitHubRepositoryLocator? locator = null;
        if (input.GitHubLocator is not null && !GitHubRepositoryLocatorParser.TryParse(input.GitHubLocator, out locator))
            return new LocalRepositoryPreparationRejected<PreparedCreate>(LocalRepositoryPreparationFailure.InvalidLocator);
        return new LocalRepositoryPreparationSucceeded<PreparedCreate>(PreparedCreate.Create(this, preparedSeal, new PreparedCreateState(
            "POST", CollectionRoute, "create", 201, displayName, locator)));
    }

    internal LocalRepositoryPreparationResult<PreparedRename> PrepareRename(LocalRepositoryRenameInput input)
    {
        if (input is null || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(input.RepositoryId))
            return new LocalRepositoryPreparationRejected<PreparedRename>(LocalRepositoryPreparationFailure.InvalidRepositoryTarget);
        if (input.ExpectedRevision < 1 || !TryNormalizeDisplayName(input.DisplayName, out var displayName))
            return new LocalRepositoryPreparationRejected<PreparedRename>(LocalRepositoryPreparationFailure.InvalidRequest);
        return new LocalRepositoryPreparationSucceeded<PreparedRename>(PreparedRename.Create(this, preparedSeal, new PreparedRenameState(
            "PATCH", ItemRoute, "rename", 200, input.RepositoryId, input.ExpectedRevision, displayName)));
    }

    internal LocalRepositoryPreparationResult<PreparedSetLocator> PrepareSetGitHubLocator(LocalRepositorySetLocatorInput input)
    {
        if (input is null || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(input.RepositoryId))
            return new LocalRepositoryPreparationRejected<PreparedSetLocator>(LocalRepositoryPreparationFailure.InvalidRepositoryTarget);
        if (input.ExpectedRevision < 1)
            return new LocalRepositoryPreparationRejected<PreparedSetLocator>(LocalRepositoryPreparationFailure.InvalidRequest);
        if (!GitHubRepositoryLocatorParser.TryParse(input.GitHubLocator, out var locator))
            return new LocalRepositoryPreparationRejected<PreparedSetLocator>(LocalRepositoryPreparationFailure.InvalidLocator);
        return new LocalRepositoryPreparationSucceeded<PreparedSetLocator>(PreparedSetLocator.Create(this, preparedSeal, new PreparedSetLocatorState(
            "PATCH", ItemRoute, "set_github_locator", 200, input.RepositoryId, input.ExpectedRevision, locator!)));
    }

    internal LocalRepositoryPreparationResult<PreparedSessionAction> PrepareSessionAction(LocalRepositorySessionActionInput input)
    {
        if (input is null || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(input.SessionId))
            return new LocalRepositoryPreparationRejected<PreparedSessionAction>(LocalRepositoryPreparationFailure.InvalidSessionTarget);
        if (input.ExpectedRevision < 0)
            return new LocalRepositoryPreparationRejected<PreparedSessionAction>(LocalRepositoryPreparationFailure.InvalidRequest);
        var action = input.Action switch
        {
            "assign" => LocalRepositorySessionAction.Assign,
            "explicitly_unassign" => LocalRepositorySessionAction.ExplicitlyUnassign,
            "resume_automatic" => LocalRepositorySessionAction.ResumeAutomatic,
            _ => (LocalRepositorySessionAction?)null,
        };
        if (action is null)
            return new LocalRepositoryPreparationRejected<PreparedSessionAction>(LocalRepositoryPreparationFailure.InvalidRequest);
        if (action == LocalRepositorySessionAction.Assign)
        {
            if (input.RepositoryId is null)
                return new LocalRepositoryPreparationRejected<PreparedSessionAction>(LocalRepositoryPreparationFailure.InvalidRequest);
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(input.RepositoryId))
                return new LocalRepositoryPreparationRejected<PreparedSessionAction>(LocalRepositoryPreparationFailure.InvalidRepositoryTarget);
        }
        else if (input.RepositoryId is not null)
        {
            return new LocalRepositoryPreparationRejected<PreparedSessionAction>(LocalRepositoryPreparationFailure.InvalidRequest);
        }
        return new LocalRepositoryPreparationSucceeded<PreparedSessionAction>(PreparedSessionAction.Create(this, preparedSeal, new PreparedSessionActionState(
            "POST", SessionActionRoute, "session_action", 200, input.SessionId, input.ExpectedRevision,
            action.Value, input.Action, input.RepositoryId)));
    }

    internal ValueTask<LocalRepositoryMutationResult> ExecutePreparedAsync(
        PreparedCreate prepared,
        string validOperationKey,
        LocalRepositorySuccessEntityWriter<LocalRepositoryMutationRepository> writeEntity,
        CancellationToken cancellationToken)
    {
        var state = prepared?.Unseal(this, preparedSeal)
            ?? throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        if (!LocalRepositoryCatalogValidation.IsOperationKey(validOperationKey))
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.InvalidRequest));
        ArgumentNullException.ThrowIfNull(writeEntity);
        var mutation = CreateStoreInput((PreparedCreateState)state);
        return store.ExecutePreparedAsync(mutation, validOperationKey, writeEntity, cancellationToken);
    }

    internal ValueTask<LocalRepositoryMutationResult> ExecutePreparedAsync(
        PreparedRename prepared,
        string validOperationKey,
        LocalRepositorySuccessEntityWriter<LocalRepositoryMutationRepository> writeEntity,
        CancellationToken cancellationToken)
    {
        var state = prepared?.Unseal(this, preparedSeal)
            ?? throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        if (!LocalRepositoryCatalogValidation.IsOperationKey(validOperationKey))
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.InvalidRequest));
        ArgumentNullException.ThrowIfNull(writeEntity);
        var mutation = CreateStoreInput((PreparedRenameState)state);
        return store.ExecutePreparedAsync(mutation, validOperationKey, writeEntity, cancellationToken);
    }

    internal ValueTask<LocalRepositoryMutationResult> ExecutePreparedAsync(
        PreparedSetLocator prepared,
        string validOperationKey,
        LocalRepositorySuccessEntityWriter<LocalRepositoryMutationRepository> writeEntity,
        CancellationToken cancellationToken)
    {
        var state = prepared?.Unseal(this, preparedSeal)
            ?? throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        if (!LocalRepositoryCatalogValidation.IsOperationKey(validOperationKey))
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.InvalidRequest));
        ArgumentNullException.ThrowIfNull(writeEntity);
        var mutation = CreateStoreInput((PreparedSetLocatorState)state);
        return store.ExecutePreparedAsync(mutation, validOperationKey, writeEntity, cancellationToken);
    }

    internal ValueTask<LocalRepositoryMutationResult> ExecutePreparedAsync(
        PreparedSessionAction prepared,
        string validOperationKey,
        LocalRepositorySuccessEntityWriter<LocalRepositoryMutationAssignment> writeEntity,
        CancellationToken cancellationToken)
    {
        var state = prepared?.Unseal(this, preparedSeal)
            ?? throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        if (!LocalRepositoryCatalogValidation.IsOperationKey(validOperationKey))
            return ValueTask.FromResult<LocalRepositoryMutationResult>(new LocalRepositoryMutationRejected(LocalRepositoryMutationFailure.InvalidRequest));
        ArgumentNullException.ThrowIfNull(writeEntity);
        var mutation = CreateStoreInput((PreparedSessionActionState)state);
        return store.ExecutePreparedAsync(mutation, validOperationKey, writeEntity, cancellationToken);
    }

    internal ValueTask<LocalRepositoryLocatorReadResult> ReadLocatorsAsync(string repositoryId, CancellationToken cancellationToken) =>
        LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
            ? store.ReadLocatorsAsync(repositoryId, cancellationToken)
            : ValueTask.FromResult<LocalRepositoryLocatorReadResult>(new LocalRepositoryLocatorRepositoryNotFound());

    internal ValueTask<LocalRepositoryAssignmentReadResult> ReadAssignmentAsync(string sessionId, CancellationToken cancellationToken) =>
        LocalRepositoryCatalogValidation.IsCanonicalUuidV7(sessionId)
            ? store.ReadAssignmentAsync(sessionId, cancellationToken)
            : ValueTask.FromResult<LocalRepositoryAssignmentReadResult>(new LocalRepositoryAssignmentSessionNotFound());

    internal bool OwnsStoreInputSeal(object seal) => ReferenceEquals(storeInputSeal, seal);
    private bool OwnsPreparedSeal(object seal) => ReferenceEquals(preparedSeal, seal);

    private object ValidatePreparedState(PreparedCreateState state)
    {
        if (state is not { Method: "POST", RouteTemplate: CollectionRoute, Operation: "create", ExpectedSuccessStatus: 201 }
            || !LocalRepositoryCatalogValidation.IsDisplayName(state.DisplayName)
            || (state.Locator is not null && !GitHubRepositoryLocatorParser.IsExact(state.Locator)))
            throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        return state;
    }

    private object ValidatePreparedState(PreparedRenameState state)
    {
        if (state is not { Method: "PATCH", RouteTemplate: ItemRoute, Operation: "rename", ExpectedSuccessStatus: 200 }
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(state.RepositoryId)
            || state.ExpectedRevision < 1
            || !LocalRepositoryCatalogValidation.IsDisplayName(state.DisplayName))
            throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        return state;
    }

    private object ValidatePreparedState(PreparedSetLocatorState state)
    {
        if (state is not { Method: "PATCH", RouteTemplate: ItemRoute, Operation: "set_github_locator", ExpectedSuccessStatus: 200 }
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(state.RepositoryId)
            || state.ExpectedRevision < 1
            || !GitHubRepositoryLocatorParser.IsExact(state.Locator))
            throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        return state;
    }

    private object ValidatePreparedState(PreparedSessionActionState state)
    {
        if (state is not { Method: "POST", RouteTemplate: SessionActionRoute, Operation: "session_action", ExpectedSuccessStatus: 200 }
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(state.SessionId)
            || state.ExpectedRevision < 0
            || !ValidSessionActionState(state))
            throw new InvalidOperationException("local_repository_prepared_capability_invalid");
        return state;
    }

    private LocalRepositoryCreateStoreInput CreateStoreInput(PreparedCreateState state)
    {
        _ = ValidatePreparedState(state);
        var fingerprint = Fingerprint(state.Method, state.RouteTemplate, state.Operation, null, null, state.DisplayName, state.Locator?.CanonicalLocator, null, null);
        return LocalRepositoryCreateStoreInput.Create(this, storeInputSeal, state.ExpectedSuccessStatus, state.DisplayName, state.Locator, fingerprint);
    }

    private LocalRepositoryRenameStoreInput CreateStoreInput(PreparedRenameState state)
    {
        _ = ValidatePreparedState(state);
        var revision = state.ExpectedRevision.ToString(CultureInfo.InvariantCulture);
        var fingerprint = Fingerprint(state.Method, state.RouteTemplate, state.Operation, state.RepositoryId, revision, state.DisplayName, null, null, null);
        return LocalRepositoryRenameStoreInput.Create(this, storeInputSeal, state.ExpectedSuccessStatus, state.RepositoryId, state.ExpectedRevision, state.DisplayName, fingerprint);
    }

    private LocalRepositorySetLocatorStoreInput CreateStoreInput(PreparedSetLocatorState state)
    {
        _ = ValidatePreparedState(state);
        var revision = state.ExpectedRevision.ToString(CultureInfo.InvariantCulture);
        var fingerprint = Fingerprint(state.Method, state.RouteTemplate, state.Operation, state.RepositoryId, revision, null, state.Locator.CanonicalLocator, null, null);
        return LocalRepositorySetLocatorStoreInput.Create(this, storeInputSeal, state.ExpectedSuccessStatus, state.RepositoryId, state.ExpectedRevision, state.Locator, fingerprint);
    }

    private LocalRepositorySessionActionStoreInput CreateStoreInput(PreparedSessionActionState state)
    {
        _ = ValidatePreparedState(state);
        var revision = state.ExpectedRevision.ToString(CultureInfo.InvariantCulture);
        var fingerprint = Fingerprint(state.Method, state.RouteTemplate, state.Operation, state.SessionId, revision, null, null, state.ActionValue, state.RepositoryId);
        return LocalRepositorySessionActionStoreInput.Create(this, storeInputSeal, state.ExpectedSuccessStatus, state.SessionId, state.ExpectedRevision, state.Action, state.ActionValue, state.RepositoryId, fingerprint);
    }

    private static bool ValidSessionActionState(PreparedSessionActionState state) => state switch
    {
        { Action: LocalRepositorySessionAction.Assign, ActionValue: "assign", RepositoryId: not null } => LocalRepositoryCatalogValidation.IsCanonicalUuidV7(state.RepositoryId),
        { Action: LocalRepositorySessionAction.ExplicitlyUnassign, ActionValue: "explicitly_unassign", RepositoryId: null } => true,
        { Action: LocalRepositorySessionAction.ResumeAutomatic, ActionValue: "resume_automatic", RepositoryId: null } => true,
        _ => false,
    };

    private static bool TryNormalizeDisplayName(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null)
            return false;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC);
            return LocalRepositoryCatalogValidation.IsDisplayName(normalized);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Fingerprint(
        string method,
        string routeTemplate,
        string operation,
        string? targetId,
        string? expectedRevision,
        string? displayName,
        string? canonicalLocator,
        string? sessionAction,
        string? repositoryId) =>
        LocalRepositoryIdentityHashing.OperationFingerprint(new(
            method,
            routeTemplate,
            operation,
            targetId,
            expectedRevision,
            displayName,
            canonicalLocator,
            sessionAction,
            repositoryId));
}

internal abstract class LocalRepositoryStoreInput
{
    private readonly LocalRepositoryCatalogApplication owner;
    private readonly object seal;

    protected LocalRepositoryStoreInput(LocalRepositoryCatalogApplication owner, object seal, int expectedSuccessStatus, string requestFingerprint)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.seal = seal ?? throw new ArgumentNullException(nameof(seal));
        if (!owner.OwnsStoreInputSeal(seal))
            throw new InvalidOperationException("local_repository_store_input_invalid");
        ExpectedSuccessStatus = expectedSuccessStatus;
        RequestFingerprint = requestFingerprint;
    }

    internal int ExpectedSuccessStatus { get; }
    internal string RequestFingerprint { get; }

    internal void Validate(int expectedStatus)
    {
        if (!owner.OwnsStoreInputSeal(seal)
            || ExpectedSuccessStatus != expectedStatus
            || !LocalRepositoryCatalogValidation.IsLowerSha256(RequestFingerprint))
        {
            throw new InvalidOperationException("local_repository_store_input_invalid");
        }
    }
}

internal sealed class LocalRepositoryCreateStoreInput : LocalRepositoryStoreInput
{
    private LocalRepositoryCreateStoreInput(LocalRepositoryCatalogApplication owner, object seal, int status, string displayName, GitHubRepositoryLocator? locator, string fingerprint)
        : base(owner, seal, status, fingerprint) => (DisplayName, Locator) = (displayName, locator);
    internal string DisplayName { get; }
    internal GitHubRepositoryLocator? Locator { get; }
    internal static LocalRepositoryCreateStoreInput Create(LocalRepositoryCatalogApplication owner, object seal, int status, string displayName, GitHubRepositoryLocator? locator, string fingerprint) => new(owner, seal, status, displayName, locator, fingerprint);
}

internal sealed class LocalRepositoryRenameStoreInput : LocalRepositoryStoreInput
{
    private LocalRepositoryRenameStoreInput(LocalRepositoryCatalogApplication owner, object seal, int status, string repositoryId, long expectedRevision, string displayName, string fingerprint)
        : base(owner, seal, status, fingerprint) => (RepositoryId, ExpectedRevision, DisplayName) = (repositoryId, expectedRevision, displayName);
    internal string RepositoryId { get; }
    internal long ExpectedRevision { get; }
    internal string DisplayName { get; }
    internal static LocalRepositoryRenameStoreInput Create(LocalRepositoryCatalogApplication owner, object seal, int status, string repositoryId, long expectedRevision, string displayName, string fingerprint) => new(owner, seal, status, repositoryId, expectedRevision, displayName, fingerprint);
}

internal sealed class LocalRepositorySetLocatorStoreInput : LocalRepositoryStoreInput
{
    private LocalRepositorySetLocatorStoreInput(LocalRepositoryCatalogApplication owner, object seal, int status, string repositoryId, long expectedRevision, GitHubRepositoryLocator locator, string fingerprint)
        : base(owner, seal, status, fingerprint) => (RepositoryId, ExpectedRevision, Locator) = (repositoryId, expectedRevision, locator);
    internal string RepositoryId { get; }
    internal long ExpectedRevision { get; }
    internal GitHubRepositoryLocator Locator { get; }
    internal static LocalRepositorySetLocatorStoreInput Create(LocalRepositoryCatalogApplication owner, object seal, int status, string repositoryId, long expectedRevision, GitHubRepositoryLocator locator, string fingerprint) => new(owner, seal, status, repositoryId, expectedRevision, locator, fingerprint);
}

internal sealed class LocalRepositorySessionActionStoreInput : LocalRepositoryStoreInput
{
    private LocalRepositorySessionActionStoreInput(LocalRepositoryCatalogApplication owner, object seal, int status, string sessionId, long expectedRevision, LocalRepositorySessionAction action, string actionValue, string? repositoryId, string fingerprint)
        : base(owner, seal, status, fingerprint) => (SessionId, ExpectedRevision, Action, ActionValue, RepositoryId) = (sessionId, expectedRevision, action, actionValue, repositoryId);
    internal string SessionId { get; }
    internal long ExpectedRevision { get; }
    internal LocalRepositorySessionAction Action { get; }
    internal string ActionValue { get; }
    internal string? RepositoryId { get; }
    internal static LocalRepositorySessionActionStoreInput Create(LocalRepositoryCatalogApplication owner, object seal, int status, string sessionId, long expectedRevision, LocalRepositorySessionAction action, string actionValue, string? repositoryId, string fingerprint) => new(owner, seal, status, sessionId, expectedRevision, action, actionValue, repositoryId, fingerprint);
}
