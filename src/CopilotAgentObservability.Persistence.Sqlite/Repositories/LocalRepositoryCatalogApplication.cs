using System.Globalization;
using System.Text;
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

internal sealed class LocalRepositoryExactResponse
{
    internal const string SuccessContentType = "application/json; charset=utf-8";
    internal const string SuccessCacheControl = "no-store";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] entity;

    private LocalRepositoryExactResponse(int statusCode, byte[] entity) =>
        (StatusCode, this.entity) = (statusCode, entity);

    internal int StatusCode { get; }
    internal string ContentType => SuccessContentType;
    internal string CacheControl => SuccessCacheControl;

    internal byte[] CopyEntity() => entity.ToArray();

    internal static LocalRepositoryExactResponse CreateSuccess(int operationOwnedStatusCode, ReadOnlyMemory<byte> entity)
    {
        if (operationOwnedStatusCode is not (200 or 201))
            throw new ArgumentOutOfRangeException(nameof(operationOwnedStatusCode));
        var copy = entity.ToArray();
        ValidateEntity(copy);
        return new(operationOwnedStatusCode, copy);
    }

    internal static LocalRepositoryExactResponse FromStored(
        int expectedStatusCode,
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
        return CreateSuccess(statusCode, entity);
    }

    private static void ValidateEntity(ReadOnlySpan<byte> entity)
    {
        if (entity.IsEmpty
            || entity[^1] == (byte)'\n'
            || entity.Length >= 3 && entity[0] == 0xef && entity[1] == 0xbb && entity[2] == 0xbf)
        {
            throw new InvalidOperationException("local_repository_success_entity_invalid");
        }
        try
        {
            _ = StrictUtf8.GetCharCount(entity);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException("local_repository_success_entity_invalid", exception);
        }
    }
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
