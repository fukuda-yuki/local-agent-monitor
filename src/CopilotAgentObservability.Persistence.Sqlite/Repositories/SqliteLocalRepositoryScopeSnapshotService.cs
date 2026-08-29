using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class SqliteLocalRepositoryScopeSnapshotService : ILocalRepositoryScopeSnapshotService, ILocalRepositorySessionDetailSnapshotService, ILocalRepositoryComparisonInputSnapshotService
{
    private const int MaximumSessions = 10_000;
    private const int MaximumCandidatesPerSession = 128;
    private const int DefaultBusyTimeoutMilliseconds = 5_000;
    private readonly string databasePath;
    private readonly ILocalRepositorySessionSnapshotContributor sessionContributor;
    private readonly ILocalArchiveFactSnapshotContributor archiveContributor;
    private readonly ILocalWorkspaceSessionDetailSnapshotContributor detailContributor;
    private readonly int busyTimeoutMilliseconds;
    private readonly Action<int>? compositionObserver;
    private readonly Func<ValueTask>? capabilityEntryObserver;
    private readonly Action? contributorPhaseRevokedObserver;
    private readonly Action<SqliteConnection>? connectionOpenedObserver;
    private readonly Action? finalReturnObserver;
    private readonly Func<SqliteConnection>? connectionFactory;
    private readonly Action<string, int>? catalogRowObserver;
    private readonly ILocalWorkspacePublicationGate publicationGate;
    private readonly ISkillRegistryGenerationAuthority? skillRegistryAuthority;
    private readonly TimeProvider timeProvider;

    internal SqliteLocalRepositoryScopeSnapshotService(
        string databasePath,
        ILocalRepositorySessionSnapshotContributor sessionContributor,
        ILocalArchiveFactSnapshotContributor archiveContributor,
        ILocalWorkspaceSessionDetailSnapshotContributor? detailContributor = null,
        int busyTimeoutMilliseconds = DefaultBusyTimeoutMilliseconds,
        Action<int>? compositionObserver = null,
        Func<ValueTask>? capabilityEntryObserver = null,
        Action? contributorPhaseRevokedObserver = null,
        Action<SqliteConnection>? connectionOpenedObserver = null,
        Action? finalReturnObserver = null,
        Func<SqliteConnection>? connectionFactory = null,
        Action<string, int>? catalogRowObserver = null,
        ILocalWorkspacePublicationGate? publicationGate = null,
        ISkillRegistryGenerationAuthority? skillRegistryAuthority = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(sessionContributor);
        ArgumentNullException.ThrowIfNull(archiveContributor);
        if (busyTimeoutMilliseconds is < 1 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(busyTimeoutMilliseconds));
        var acceptedTimeProvider = timeProvider
            ?? (detailContributor as LocalWorkspaceSessionDetailSnapshotContributor)?.TimeProvider
            ?? (sessionContributor as LocalWorkspaceSessionSnapshotContributor)?.TimeProvider
            ?? TimeProvider.System;
        this.databasePath = Path.GetFullPath(databasePath);
        this.sessionContributor = sessionContributor;
        this.archiveContributor = archiveContributor;
        this.detailContributor = detailContributor ?? new LocalWorkspaceSessionDetailSnapshotContributor(
            registryAuthority: skillRegistryAuthority,
            timeProvider: acceptedTimeProvider);
        this.busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        this.compositionObserver = compositionObserver;
        this.capabilityEntryObserver = capabilityEntryObserver;
        this.contributorPhaseRevokedObserver = contributorPhaseRevokedObserver;
        this.connectionOpenedObserver = connectionOpenedObserver;
        this.finalReturnObserver = finalReturnObserver;
        this.connectionFactory = connectionFactory;
        this.catalogRowObserver = catalogRowObserver;
        this.publicationGate = publicationGate ?? new LocalWorkspacePublicationGate();
        this.skillRegistryAuthority = skillRegistryAuthority
            ?? (this.detailContributor as LocalWorkspaceSessionDetailSnapshotContributor)?.RegistryAuthority
            ?? (sessionContributor as LocalWorkspaceSessionSnapshotContributor)?.RegistryAuthority;
        this.timeProvider = acceptedTimeProvider;
    }

    public async ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(
        LocalRepositoryScopeRequest request,
        CancellationToken cancellationToken) =>
        (await ReadCoreAsync(request, detailRequest: null, comparisonBatch: false, cancellationToken).ConfigureAwait(false)).Scope;

    public async ValueTask<LocalRepositoryComparisonInputSnapshot> ReadComparisonInputAsync(
        LocalRepositoryScopeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ExactTargetSessionIds is null)
            throw new ArgumentException("invalid_local_repository_scope", nameof(request));
        var result = await ReadCoreAsync(request, detailRequest: null, comparisonBatch: true, cancellationToken).ConfigureAwait(false);
        return new(result.Scope, result.ComparisonSessions!);
    }

    public async ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(
        LocalRepositorySessionDetailRequest detailRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detailRequest);
        var sessionId = detailRequest.SessionId;
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(sessionId))
            throw new ArgumentException("invalid_session_id", nameof(sessionId));
        ValidateDetailRequest(detailRequest);
        var result = await ReadCoreAsync(new(LocalRepositoryScopeKind.All, null, sessionId), detailRequest, comparisonBatch: false, cancellationToken).ConfigureAwait(false);
        if (result.Scope.Sessions.Count == 0 || result.Detail is null)
            throw new LocalWorkspaceSessionDetailException("session_not_found");
        var session = result.Scope.Sessions[0];
        return new(session, result.Detail, ComputeRevision(session, result.Detail));
    }

    private async ValueTask<(LocalRepositoryScopeSnapshot Scope, LocalWorkspaceSessionDetailContribution? Detail, IReadOnlyList<LocalRepositoryComparisonSessionInput>? ComparisonSessions)> ReadCoreAsync(
        LocalRepositoryScopeRequest request,
        LocalRepositorySessionDetailRequest? detailRequest,
        bool comparisonBatch,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        await using var publicationLease = await publicationGate.AcquireReadAsync(cancellationToken).ConfigureAwait(false);
        var acceptedAt = timeProvider.GetUtcNow();
        using var pinnedRegistry = skillRegistryAuthority is null
            ? null
            : LocalWorkspaceSessionDetailSnapshotContributor.PinnedRegistryAuthority.TryCreate(skillRegistryAuthority);
        if (sessionContributor is LocalWorkspaceSessionSnapshotContributor && pinnedRegistry is null)
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        try
        {
            using var connection = Open();
            Execute(connection, null, $"PRAGMA busy_timeout={busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");
            Execute(connection, null, "PRAGMA query_only=ON;");
            using var transaction = connection.BeginTransaction(deferred: true);
            var capability = new ReadTransactionCapability(
                connection,
                transaction,
                capabilityEntryObserver,
                contributorPhaseRevokedObserver);
            try
            {
                var sessionContribution = await capability.RunContributorAsync(
                    ReadPhase.Session,
                    token => sessionContributor is LocalWorkspaceSessionSnapshotContributor workspaceSession && pinnedRegistry is not null
                        ? workspaceSession.ReadPinnedAsync(capability, request, acceptedAt, pinnedRegistry, token)
                        : sessionContributor.ReadAsync(capability, request, token),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var sessionRows = ValidateAndFreezeSessionRows(sessionContribution, cancellationToken);

                var catalogRead = await capability.RunCatalogAsync(
                    (currentConnection, currentTransaction, token) =>
                        ReadCatalogAsync(currentConnection, currentTransaction, sessionRows, catalogRowObserver, token),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var catalog = ValidateAndFreezeCatalog(catalogRead, cancellationToken);

                if (request.ScopeKind == LocalRepositoryScopeKind.Repository
                    && !catalog.RepositoryById.ContainsKey(request.RepositoryId!))
                {
                    throw new InvalidOperationException("local_repository_scope_repository_not_found");
                }

                var archiveInput = new LocalRepositoryArchiveInput(
                    Array.AsReadOnly(sessionRows.Select(item => item.SessionId).ToArray()),
                    Array.AsReadOnly(catalog.Repositories.Select(item => item.RepositoryId).ToArray()));
                var archiveContribution = await capability.RunContributorAsync(
                    ReadPhase.Archive,
                    token => archiveContributor.ReadAsync(capability, archiveInput, token),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var archive = ValidateAndFreezeArchive(
                    archiveContribution,
                    archiveInput.SessionIds,
                    catalog,
                    cancellationToken);

                var snapshot = Compose(request, sessionRows, catalog, archive, compositionObserver, cancellationToken);

                LocalWorkspaceSessionDetailContribution? detail = null;
                if (detailRequest is not null && sessionRows.Length != 0)
                {
                    var revisionSession = snapshot.Sessions.SingleOrDefault(item => item.SessionId == request.TargetSessionId);
                    detail = await capability.RunContributorAsync(
                        ReadPhase.Archive,
                        token => detailContributor is LocalWorkspaceSessionDetailSnapshotContributor workspaceDetail && pinnedRegistry is not null
                            ? workspaceDetail.ReadPinnedAsync(capability, detailRequest, acceptedAt, pinnedRegistry, revisionSession, token)
                            : detailContributor.ReadAsync(capability, detailRequest, token),
                        cancellationToken).ConfigureAwait(false);
                    ValidateDetail(request.TargetSessionId!, detailRequest, detail);
                }

                IReadOnlyList<LocalRepositoryComparisonSessionInput>? comparisonSessions = null;
                if (comparisonBatch)
                {
                    var inputs = new List<LocalRepositoryComparisonSessionInput>(snapshot.Sessions.Count);
                    foreach (var currentSession in snapshot.Sessions.OrderBy(static item => item.SessionId, StringComparer.Ordinal))
                    {
                        if (sessionContribution.ProjectionErrors?.TryGetValue(currentSession.SessionId, out var projectionError) == true)
                        {
                            inputs.Add(new(currentSession, null, ComputeUnavailableRevision(currentSession, projectionError), projectionError));
                            continue;
                        }
                        var summaryRequest = new LocalRepositorySessionDetailRequest(LocalRepositorySessionDetailRequestKind.Summary, currentSession.SessionId);
                        try
                        {
                            var currentDetail = await capability.RunContributorAsync(
                                ReadPhase.Archive,
                                token => detailContributor is LocalWorkspaceSessionDetailSnapshotContributor workspaceDetail && pinnedRegistry is not null
                                    ? workspaceDetail.ReadPinnedAsync(capability, summaryRequest, acceptedAt, pinnedRegistry, currentSession, token)
                                    : detailContributor.ReadAsync(capability, summaryRequest, token),
                                cancellationToken).ConfigureAwait(false);
                            ValidateDetail(currentSession.SessionId, summaryRequest, currentDetail);
                            LocalWorkspaceComparisonDetailContribution? comparisonDetail = null;
                            if (detailContributor is LocalWorkspaceSessionDetailSnapshotContributor workspaceComparison && pinnedRegistry is not null)
                            {
                                comparisonDetail = await capability.RunContributorAsync(
                                    ReadPhase.Archive,
                                    token => workspaceComparison.ReadComparisonPinnedAsync(
                                        capability, currentSession.SessionId, acceptedAt, pinnedRegistry, token),
                                    cancellationToken).ConfigureAwait(false);
                            }
                            inputs.Add(new(currentSession, currentDetail,
                                comparisonDetail is null ? ComputeRevision(currentSession, currentDetail) : ComputeRevision(currentSession, comparisonDetail),
                                ComparisonDetail: comparisonDetail));
                        }
                        catch (LocalWorkspaceSessionDetailException exception) when (exception.Error != "workspace_too_large")
                        {
                            inputs.Add(new(currentSession, null, ComputeUnavailableRevision(currentSession, exception.Error), exception.Error));
                        }
                    }
                    comparisonSessions = Array.AsReadOnly(inputs.ToArray());
                }

                finalReturnObserver?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                return (snapshot, detail, comparisonSessions);
            }
            finally
            {
                capability.Terminate();
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new LocalRepositoryScopeSnapshotException(
                LocalRepositoryScopeSnapshotError.PersistenceBusy,
                "persistence_busy",
                exception);
        }
    }

    private SqliteConnection Open()
    {
        var connection = connectionFactory?.Invoke() ?? new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeoutMilliseconds / 1000d)),
        }.ToString());
        try
        {
            connection.Open();
            connectionOpenedObserver?.Invoke(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ValidateRequest(LocalRepositoryScopeRequest request)
        => LocalRepositoryScopeRequestValidation.Validate(request);

    private static void ValidateDetailRequest(LocalRepositorySessionDetailRequest request)
    {
        if (!Enum.IsDefined(request.Kind) || request.Limit is < 1 or > 200)
            throw new ArgumentException("invalid_detail_request", nameof(request));
        if (request.ExpectedWorkspaceRevision is not null
            && (request.Kind is not (LocalRepositorySessionDetailRequestKind.Timeline
                    or LocalRepositorySessionDetailRequestKind.Node
                    or LocalRepositorySessionDetailRequestKind.Content)
                || request.ExpectedWorkspaceRevision.Length != 64
                || request.ExpectedWorkspaceRevision.Any(static value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new ArgumentException("invalid_detail_request", nameof(request));
        if (request.Kind == LocalRepositorySessionDetailRequestKind.Summary
            && (request.ExecutionId is not null || request.ParentNodeId is not null || request.After is not null || request.NodeId is not null)
            || request.Kind == LocalRepositorySessionDetailRequestKind.Timeline
            && request.NodeId is not null
            || request.Kind == LocalRepositorySessionDetailRequestKind.Node
            && (request.NodeId is null || request.ExecutionId is not null || request.ParentNodeId is not null || request.After is not null || request.ContentPart is not null)
            || request.Kind == LocalRepositorySessionDetailRequestKind.Content
            && (request.NodeId is null || request.ContentPart is null || request.ExecutionId is not null || request.ParentNodeId is not null || request.After is not null))
            throw new ArgumentException("invalid_detail_request", nameof(request));
        if (request.ParentNodeId is not null && request.ExecutionId is null)
            throw new ArgumentException("invalid_detail_request", nameof(request));
        if (request.After is { SourceOrdinal: > long.MaxValue })
            throw new ArgumentException("invalid_detail_request", nameof(request));
    }

    private static string ComputeRevision(LocalRepositoryScopeSessionSnapshot session, LocalWorkspaceSessionDetailContribution detail) =>
        ComputeRevisionForTest(
            session,
            detail.CanonicalRevisionInput ?? throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable"),
            detail.SkillRegistryGenerationIdentity ?? throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable"));

    private static string ComputeRevision(LocalRepositoryScopeSessionSnapshot session, LocalWorkspaceComparisonDetailContribution detail) =>
        ComputeRevisionForTest(session, detail.CanonicalRevisionInput, detail.SkillRegistryGenerationIdentity);

    private static string ComputeUnavailableRevision(LocalRepositoryScopeSessionSnapshot session, string error) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"local-monitor-comparison-unavailable\0v1\0{session.SessionId}\0{session.AssignmentRevision}\0{session.ArchiveRevision}\0{session.AssignedRepositoryArchiveRevision ?? -1}\0{JsonSerializer.Serialize(session.Session)}\0{error}")));

    internal static string ComputeRevisionForTest(
        LocalRepositoryScopeSessionSnapshot session,
        string canonicalRevisionInput,
        string skillRegistryGenerationIdentity)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.ASCII.GetBytes("local-monitor-session-detail-revision\0v1\0"));
        Append(session.SessionId); Append(session.AssignmentRevision); Append(session.ArchiveRevision);
        Append(session.AssignedRepositoryArchiveRevision ?? -1L); Append(session.Session);
        Append(canonicalRevisionInput);
        Append(skillRegistryGenerationIdentity);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
        void Append(object value)
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
    }

    internal static string ComputeRevisionForTest(
        LocalRepositoryScopeSessionSnapshot session,
        LocalWorkspaceSessionDetailContribution detail) => ComputeRevision(session, detail);

    private static void ValidateDetail(string sessionId, LocalRepositorySessionDetailRequest request, LocalWorkspaceSessionDetailContribution detail)
    {
        if (detail is null || detail.Executions is null || detail.Nodes is null || detail.Edges is null || detail.Content is null)
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        var executions = new Dictionary<string, LocalWorkspaceExecutionDetail>(StringComparer.Ordinal);
        foreach (var execution in detail.Executions)
        {
            if (execution.SessionId != sessionId || !executions.TryAdd(execution.ExecutionId, execution)
                || execution.ExecutionId != LocalWorkspaceProjectionStore.StableExecutionId(sessionId, execution.SourceKind, execution.SourceIdentity)
                || execution.SourceOrdinal < 0 || execution.SourceKind != "session_run" || string.IsNullOrWhiteSpace(execution.SourceIdentity)
                || execution.Lifecycle is not ("selected" or "started" or "completed" or "failed" or "deselected" or "unknown")
                || execution.Status is not ("active" or "completed" or "failed" or "unknown")
                || execution.ChildCount is < 0 or > 4096
                || !ValidTime(execution.Status, execution.TimeAuthority, execution.StartUtcTicks, execution.EndUtcTicks, execution.DurationMilliseconds)
                || !ValidActivity(execution.Activity) || !ValidTokens(execution.Tokens))
            {
                throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
            }
        }
        var nodes = new Dictionary<string, LocalWorkspaceNodeDetail>(StringComparer.Ordinal);
        var roots = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknownGroups = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in detail.Nodes)
        {
            if (node.SessionId != sessionId || !executions.ContainsKey(node.ExecutionId) || !nodes.TryAdd(node.NodeId, node)
                || node.NodeId != LocalWorkspaceProjectionStore.StableNodeId(node.SourceKind, node.SourceIdentity)
                || node.SourceOrdinal < 0 || !ValidNodeSource(node) || string.IsNullOrWhiteSpace(node.SourceIdentity)
                || node.RelationshipAuthority is not ("exact" or "explicit" or "unknown")
                || node.Kind is not ("execution" or "agent" or "skill" or "tool" or "subagent" or "event" or "error" or "retry" or "permission" or "unknown_relation_group")
                || node.NameState is not ("recorded" or "not_observed" or "invalid")
                || (node.NameState == "recorded") != (node.NameText is not null)
                || node.Lifecycle is not ("selected" or "started" or "completed" or "failed" or "deselected" or "unknown")
                || node.Status is not ("active" or "completed" or "failed" or "unknown")
                || node.ChildCount is < 0 or > 4096
                || !ValidTime(node.Status, node.TimeAuthority, node.StartUtcTicks, node.EndUtcTicks, node.DurationMilliseconds)
                || !ValidActivity(node.Activity) || !ValidTokens(node.Tokens)
                || !ValidKindMetadata(node) || !ValidReferences(node))
            {
                throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
            }
            if (node.SourceKind == "execution_root") roots[node.ExecutionId] = roots.GetValueOrDefault(node.ExecutionId) + 1;
            if (node.Kind == "unknown_relation_group") unknownGroups[node.ExecutionId] = unknownGroups.GetValueOrDefault(node.ExecutionId) + 1;
            if (node.SourceKind == "execution_root" && (node.Kind != "execution" || node.ParentNodeId is not null || node.RelationshipAuthority != "exact")
                || node.Kind == "unknown_relation_group" && (node.ParentNodeId is not null || node.RelationshipAuthority != "unknown"))
                throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        }
        if (!(request.Kind == LocalRepositorySessionDetailRequestKind.Timeline && request.ParentNodeId is not null)
            && executions.Keys.Any(id => roots.GetValueOrDefault(id) != 1))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        if (unknownGroups.Values.Any(static count => count > 1))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        foreach (var node in detail.Nodes)
        {
            if (node.ParentNodeId is not null
                && (!nodes.TryGetValue(node.ParentNodeId, out var parent) || parent.ExecutionId != node.ExecutionId)
                && !(request.Kind == LocalRepositorySessionDetailRequestKind.Timeline && node.NodeId == request.ParentNodeId))
                throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
            var seen = new HashSet<string>(StringComparer.Ordinal) { node.NodeId };
            var current = node;
            while (current.ParentNodeId is not null)
            {
                if (!seen.Add(current.ParentNodeId))
                    throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
                if (!nodes.TryGetValue(current.ParentNodeId, out current))
                {
                    if (request.Kind == LocalRepositorySessionDetailRequestKind.Timeline && node.NodeId == request.ParentNodeId) break;
                    throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
                }
            }
            if (current!.ParentNodeId is null && current.SourceKind != "execution_root" && current.Kind != "unknown_relation_group")
                throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        }
        foreach (var edge in detail.Edges)
        {
            if (!nodes.TryGetValue(edge.NodeId, out var node) || !nodes.TryGetValue(edge.RelatedNodeId, out var related)
                || node.ExecutionId != related.ExecutionId || edge.RelationKind is not ("parent" or "retry" or "recovery")
                || edge.RelationshipAuthority is not ("exact" or "explicit") || edge.SourceOrdinal < 0
                || edge.RelationKind == "parent" && node.ParentNodeId != edge.RelatedNodeId)
                throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        }

        if (!SortedUnique(detail.NativeSessionIds) || !SortedUnique(detail.Versions))
            throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");
        var contentKeys = new HashSet<(string NodeId, string Part)>();
        foreach (var content in detail.Content)
            if ((!nodes.ContainsKey(content.NodeId)
                    && !(request.Kind == LocalRepositorySessionDetailRequestKind.Summary
                        && content.Part == "instruction" && content.SourceItemId == detail.InstructionSourceIdentity))
                || !contentKeys.Add((content.NodeId, content.Part))
                || content.Part is not ("instruction" or "tool_input" or "tool_result" or "error_message" or "subagent_input" or "event_content")
                || content.State is not ("available" or "not_captured" or "expired" or "deleted" or "read_denied" or "oversized" or "invalid")
                || content.StoreKind != "session_event_content"
                || content.LocatorKind is not ("whole_event" or "json_pointer")
                || content.LocatorKind == "whole_event" && (content.Part != "event_content" || content.JsonPointer is not null)
                || content.LocatorKind == "json_pointer" && (content.Part == "event_content" || content.JsonPointer is null)
                || content.State == "available" && (content.SelectedUtf8Bytes is null or < 0 or > 1_048_576
                    || content.RetentionItemId is null || content.RetentionStoreInstanceId is null
                    || content.SourceCapturedAt is null || content.SourceExpiresAt is null || content.RetentionRevision is null or <= 0
                    || content.RetentionOwnershipReceipt is not { Length: 32 } || content.RetentionOwnerToken is not { Length: 32 }))
                throw new LocalWorkspaceSessionDetailException("local_monitor_ui_unavailable");

        static bool ValidTime(string status, string authority, long? start, long? end, long? duration) => authority switch
        {
            "recorded" => start is not null && status switch
            {
                "active" => end is null && duration is null,
                "completed" or "failed" => end >= start && duration == (end - start) / 10_000,
                "unknown" => end is null && duration is null || end >= start && duration == (end - start) / 10_000,
                _ => false,
            },
            "missing" or "invalid" => start is null && end is null && duration is null,
            _ => false,
        };
        static bool ValidActivity(LocalWorkspaceActivityFacts value) =>
            ValidActivityFact(value.Skill, true) && ValidActivityFact(value.Tool, false) && ValidActivityFact(value.Subagent, false) && ValidActivityFact(value.Error, false) && ValidActivityFact(value.Retry, false);
        static bool ValidActivityFact(LocalWorkspaceFact<long> value, bool skill) => value.State switch
        {
            "recorded" => value.Value is >= 0,
            "not_observed" or "capture_gap" or "source_unsupported" => value.Value is null,
            "certification_pending" or "projection_invalid" when skill => value.Value is null,
            _ => false,
        };
        static bool ValidTokenFact(LocalWorkspaceFact<long> value) => value.State switch
        {
            "recorded" => value.Value is >= 0,
            "not_observed" or "inconsistent" => value.Value is null,
            _ => false,
        };
        static bool ValidTokens(LocalWorkspaceTokenFacts value) =>
            value.AvailableExecutionCount >= 0 && value.TotalExecutionCount >= value.AvailableExecutionCount
            && value.Authority is "none" or "session_run" or "llm_span" or "mixed"
            && value.State is "recorded" or "not_observed" or "inconsistent"
            && ValidTokenFact(value.Input) && ValidTokenFact(value.Output) && ValidTokenFact(value.Total)
            && ValidTokenFact(value.Reasoning) && ValidTokenFact(value.CacheRead) && ValidTokenFact(value.CacheCreation)
            && ValidTokenFact(value.NewInput) && ValidTokenFact(value.CacheReadRatioBasisPoints)
            && (value.Total.Value is null || value.Input.Value is null || value.Output.Value is null
                || value.Total.Value >= value.Input.Value + value.Output.Value)
            && (value.CacheRead.Value is null || value.Input.Value is not null && value.CacheRead.Value <= value.Input.Value)
            && (value.NewInput.Value is null || value.Input.Value is not null && value.CacheRead.Value is not null
                && value.NewInput.Value == value.Input.Value - value.CacheRead.Value)
            && (value.CacheReadRatioBasisPoints.Value is null || value.CacheReadRatioBasisPoints.Value is >= 0 and <= 10_000);
        static bool ValidPermission(LocalWorkspacePermissionMetadataDetail value) =>
            value.DecisionState is "recorded" or "not_observed" or "source_unsupported"
            && (value.DecisionState == "recorded") == (value.Decision is not null)
            && value.WaitState is "recorded" or "not_observed" or "source_unsupported"
            && (value.WaitState == "recorded") == (value.WaitMilliseconds is not null)
            && value.WaitMilliseconds is null or >= 0;
        static bool ValidNodeSource(LocalWorkspaceNodeDetail node) => node.SourceKind switch
        {
            "execution_root" => node.Kind == "execution",
            "session_event" => node.Kind is "agent" or "event" or "error" or "retry" or "permission",
            "skill_invocation" => node.Kind == "skill",
            "semantic_tool" => node.Kind == "tool" && LowerHex(node.SourceIdentity, 64),
            "semantic_subagent" => node.Kind == "subagent" && LowerHex(node.SourceIdentity, 64),
            "unknown_relation_group" => node.Kind == "unknown_relation_group",
            _ => false,
        };
        static bool ValidKindMetadata(LocalWorkspaceNodeDetail node) =>
            (node.Kind == "tool") == (node.ToolMetadata is not null)
            && (node.Kind == "skill") == (node.SkillMetadata is not null)
            && (node.Kind == "subagent") == (node.SubagentLifecycle is not null)
            && (node.Kind == "permission") == (node.PermissionMetadata is not null)
            && (node.ToolMetadata is null || ValidTool(node.ToolMetadata))
            && (node.SkillMetadata is null || ValidSkill(node.SkillMetadata))
            && (node.SubagentLifecycle is null || ValidSubagent(node.SubagentLifecycle))
            && (node.PermissionMetadata is null || ValidPermission(node.PermissionMetadata))
            && (node.ToolMetadata is null || SameReferences(node.SourceReferences, node.ToolMetadata.SourceReferences))
            && (node.SubagentLifecycle is null || SameReferences(node.SourceReferences, node.SubagentLifecycle.SourceReferences));
        static bool ValidTool(LocalWorkspaceToolMetadataDetail value) =>
            StateValue(value.CallerState, value.CallerNodeId, "recorded", "not_observed", "source_unsupported", "projection_invalid")
            && LifecycleState(value.StartedState) && LifecycleState(value.CompletedState) && LifecycleState(value.FailedState)
            && StateValue(value.ExitState, value.ExitCode, "recorded", "not_observed", "source_unsupported")
            && StateValue(value.McpServerIdentityState, value.McpServerIdentity, "recorded", "not_observed", "source_unsupported")
            && (value.McpServerIdentityState != "recorded" || LowerHex(value.McpServerIdentity!, 64))
            && StateValue(value.McpServerNameState, value.McpServerName, "recorded", "not_observed", "source_unsupported")
            && StateValue(value.McpToolNameState, value.McpToolName, "recorded", "not_observed", "source_unsupported", "invalid")
            && value.RetryState is "recorded" or "not_observed" or "source_unsupported"
            && value.RecoveryState is "recorded" or "not_observed" or "source_unsupported"
            && StateValue(value.ChildActivityState, value.ChildActivityCount, "recorded", "not_observed", "source_unsupported")
            && value.ChildActivityCount is null or >= 0;
        static bool ValidSkill(LocalWorkspaceSkillMetadataDetail value) =>
            value.CurrentValidState is "current" or "stale" or "invalid" or "certification_pending" or "unavailable"
            && StateValue(value.SourceState, value.Source, "recorded", "not_observed", "unavailable")
            && StateValue(value.TriggerState, value.Trigger, "recorded", "not_observed", "unavailable")
            && value.InventoryReferenceState == "unavailable" && value.InventoryReference is null
            && StateValue(value.HistoricalSnapshotReferenceState, value.HistoricalSnapshotReference, "recorded", "not_observed", "unavailable");
        static bool ValidSubagent(LocalWorkspaceSubagentLifecycleDetail value) =>
            LifecycleState(value.SelectedState) && LifecycleState(value.StartedState)
            && LifecycleState(value.CompletedState) && LifecycleState(value.FailedState)
            && LifecycleState(value.DeselectedState)
            && value.InputState is "available" or "not_captured" or "expired" or "deleted" or "read_denied" or "oversized" or "invalid" or "source_unsupported";
        static bool LifecycleState(string value) => value is "recorded" or "not_observed" or "source_unsupported" or "inconsistent";
        static bool StateValue<T>(string state, T? value, params string[] states)
        {
            if (!states.Contains(state, StringComparer.Ordinal)) return false;
            if ((state == "recorded") != (value is not null)) return false;
            return value is not string text || !string.IsNullOrWhiteSpace(text);
        }
        static bool ValidReferences(LocalWorkspaceNodeDetail node)
        {
            var references = node.SourceReferences;
            if (references is null || references.Count > 16)
                return false;
            string? previous = null;
            foreach (var reference in references)
            {
                if (reference is null || !ValidReference(reference)) return false;
                var key = string.Join('\u001f', reference.SourceKind, reference.SourceIdentity ?? string.Empty,
                    reference.TraceId ?? string.Empty, reference.SpanId ?? string.Empty, reference.EventId ?? string.Empty);
                if (previous is not null && StringComparer.Ordinal.Compare(previous, key) >= 0) return false;
                previous = key;
            }
            return node.SourceKind switch
            {
                "execution_root" => references.Count == 1
                    && references[0].SourceKind == "session_run"
                    && references[0].SourceIdentity == node.SourceIdentity
                    && references[0].TraceId is null && references[0].SpanId is null && references[0].EventId is null,
                "session_event" => references.Count == 1
                    && references[0].SourceKind == "session_event"
                    && references[0].SourceIdentity == node.SourceIdentity
                    && references[0].EventId == node.EventId
                    && node.EventId == node.SourceIdentity
                    && references[0].TraceId == node.TraceId && references[0].SpanId == node.SpanId,
                "skill_invocation" => ValidSkillReferences(node, references),
                "semantic_tool" => ValidToolReferences(node, references),
                "semantic_subagent" => references.Count is >= 1 and <= 16
                    && references.All(static reference => reference.SourceKind == "session_event"),
                "unknown_relation_group" => references.Count == 0,
                _ => false,
            };
        }
        static bool ValidSkillReferences(
            LocalWorkspaceNodeDetail node,
            IReadOnlyList<LocalWorkspaceNodeSourceReferenceDetail> references)
        {
            if (references.Count is < 1 or > 2 || references.Any(static reference => reference.SourceKind != "skill_claim"))
                return false;
            var otel = references.Where(static reference => reference.SourceIdentity!.StartsWith("otel:", StringComparison.Ordinal)).ToArray();
            var sdk = references.Where(static reference => reference.SourceIdentity!.StartsWith("sdk:", StringComparison.Ordinal)).ToArray();
            if (otel.Length + sdk.Length != references.Count || otel.Length > 1 || sdk.Length > 1)
                return false;
            return otel.All(reference => reference.TraceId is not null && reference.SpanId is not null
                    && reference.EventId is not null && reference.TraceId == node.TraceId && reference.SpanId == node.SpanId)
                && sdk.All(static reference => reference.TraceId is null && reference.SpanId is null && reference.EventId is not null);
        }
        static bool ValidToolReferences(
            LocalWorkspaceNodeDetail node,
            IReadOnlyList<LocalWorkspaceNodeSourceReferenceDetail> references)
        {
            if (references.Count is < 1 or > 16) return false;
            if (references.All(static reference => reference.SourceKind == "session_event")) return true;
            if (references.Count != 1 || references[0].SourceKind != "otel_span") return false;
            var reference = references[0];
            return reference.TraceId is not null && reference.SpanId is not null && reference.EventId is not null
                && node.SourceIdentity == SemanticDigest("otel_tool", reference.TraceId, reference.SpanId);
        }
        static bool ValidReference(LocalWorkspaceNodeSourceReferenceDetail value)
        {
            if (!value.AuthorityValidated || string.IsNullOrEmpty(value.RevisionInput)
                || value.SourceIdentity is not null && string.IsNullOrWhiteSpace(value.SourceIdentity)
                || value.EventId is not null && string.IsNullOrWhiteSpace(value.EventId)
                || (value.TraceId is null) != (value.SpanId is null)
                || value.TraceId is not null && (!LowerHex(value.TraceId, 32) || !LowerHex(value.SpanId!, 16)))
                return false;
            return value.SourceKind switch
            {
                "session_run" => value.SourceIdentity is not null && value.TraceId is null && value.EventId is null,
                "session_event" => value.SourceIdentity is not null && value.EventId == value.SourceIdentity,
                "otel_span" => value.SourceIdentity is not null && value.TraceId is not null && value.EventId is not null,
                "skill_claim" => value.SourceIdentity is not null,
                _ => false,
            };
        }
        static bool LowerHex(string value, int length) =>
            value.Length == length && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        static bool SameReferences(
            IReadOnlyList<LocalWorkspaceNodeSourceReferenceDetail>? left,
            IReadOnlyList<LocalWorkspaceNodeSourceReferenceDetail>? right) =>
            left is not null && right is not null && left.SequenceEqual(right);
        static string SemanticDigest(string kind, string scope, string carrier)
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            hash.AppendData(System.Text.Encoding.ASCII.GetBytes("local-workspace-semantic-carrier\0v1\0"));
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(kind));
            hash.AppendData([0]);
            Append(scope);
            Append(carrier);
            return Convert.ToHexStringLower(hash.GetHashAndReset());

            void Append(string value)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(value);
                Span<byte> length = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
        }
        static bool SortedUnique(IReadOnlyList<string>? values)
        {
            if (values is null) return false;
            for (var index = 0; index < values.Count; index++)
                if (string.IsNullOrWhiteSpace(values[index]) || index > 0 && StringComparer.Ordinal.Compare(values[index - 1], values[index]) >= 0)
                    return false;
            return true;
        }
    }

    internal static void ValidateDetailForTest(
        string sessionId,
        LocalRepositorySessionDetailRequest request,
        LocalWorkspaceSessionDetailContribution detail) => ValidateDetail(sessionId, request, detail);

    private static FrozenSession[] ValidateAndFreezeSessionRows(
        LocalRepositorySessionContribution contribution,
        CancellationToken cancellationToken)
    {
        if (contribution?.Sessions is null)
            throw new InvalidOperationException("local_repository_session_contribution_invalid");
        if (contribution.Sessions.Count > MaximumSessions)
            throw new InvalidOperationException("local_repository_session_limit_exceeded");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var frozen = new FrozenSession[contribution.Sessions.Count];
        for (var index = 0; index < contribution.Sessions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = contribution.Sessions[index];
            var sessionId = row?.SessionId;
            if (row is null
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(sessionId)
                || !identities.Add(sessionId!))
            {
                throw new InvalidOperationException("local_repository_session_contribution_invalid");
            }
            frozen[index] = new(sessionId!, row);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return frozen.OrderBy(item => item.SessionId, StringComparer.Ordinal).ToArray();
    }

    private static FrozenCatalog ValidateAndFreezeCatalog(
        CatalogContribution contribution,
        CancellationToken cancellationToken)
    {
        var repositories = new FrozenRepository[contribution.Repositories.Count];
        var repositoryById = new Dictionary<string, FrozenRepository>(contribution.Repositories.Count, StringComparer.Ordinal);
        string? previousRepositoryId = null;
        for (var index = 0; index < contribution.Repositories.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = contribution.Repositories[index];
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(item.RepositoryId)
                || (previousRepositoryId is not null
                    && StringComparer.Ordinal.Compare(previousRepositoryId, item.RepositoryId) >= 0))
            {
                throw new InvalidOperationException("local_repository_catalog_snapshot_invalid");
            }
            var frozen = new FrozenRepository(item.RepositoryId, item.DisplayName, item.Revision, item.CurrentLocatorId);
            repositories[index] = frozen;
            repositoryById.Add(frozen.RepositoryId, frozen);
            previousRepositoryId = frozen.RepositoryId;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(
            contribution.Assignments,
            Array.AsReadOnly(repositories),
            repositoryById);
    }

    private static FrozenArchive ValidateAndFreezeArchive(
        LocalArchiveFactContribution contribution,
        IReadOnlyList<string> exactSessionIds,
        FrozenCatalog catalog,
        CancellationToken cancellationToken)
    {
        var contributedSessions = contribution?.Sessions;
        var contributedRepositories = contribution?.Repositories;
        if (contributedSessions is null || contributedRepositories is null)
        {
            throw new InvalidOperationException("local_archive_fact_contribution_invalid");
        }
        var contributedSessionCount = contributedSessions.Count;
        var contributedRepositoryCount = contributedRepositories.Count;
        if (contributedSessionCount != exactSessionIds.Count
            || contributedRepositoryCount != catalog.Repositories.Count)
        {
            throw new InvalidOperationException("local_archive_fact_contribution_invalid");
        }
        var expectedSessions = exactSessionIds.ToHashSet(StringComparer.Ordinal);
        var sessions = new Dictionary<string, LocalArchiveSessionFact>(contributedSessionCount, StringComparer.Ordinal);
        for (var index = 0; index < contributedSessionCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = contributedSessions[index];
            if (item is null)
            {
                throw new InvalidOperationException("local_archive_fact_contribution_invalid");
            }
            var sessionId = item.SessionId;
            var state = item.State;
            var revision = item.Revision;
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(sessionId)
                || !expectedSessions.Contains(sessionId)
                || !IsValidArchiveFact(state, revision)
                || !sessions.TryAdd(sessionId, new(sessionId, state, revision)))
            {
                throw new InvalidOperationException("local_archive_fact_contribution_invalid");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var repositories = new Dictionary<string, LocalArchiveRepositoryFact>(contributedRepositoryCount, StringComparer.Ordinal);
        for (var index = 0; index < contributedRepositoryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = contributedRepositories[index];
            if (item is null)
            {
                throw new InvalidOperationException("local_archive_fact_contribution_invalid");
            }
            var repositoryId = item.RepositoryId;
            var state = item.State;
            var revision = item.Revision;
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
                || !catalog.RepositoryById.ContainsKey(repositoryId)
                || !IsValidArchiveFact(state, revision)
                || !repositories.TryAdd(repositoryId, new(repositoryId, state, revision)))
            {
                throw new InvalidOperationException("local_archive_fact_contribution_invalid");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(sessions, repositories);
    }

    private static bool IsValidArchiveFact(LocalArchiveState state, long revision) =>
        state switch
        {
            LocalArchiveState.Active => revision >= 0 && revision % 2 == 0,
            LocalArchiveState.Archived => revision > 0 && revision % 2 == 1,
            _ => false,
        };

    private static async ValueTask<CatalogContribution> ReadCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<FrozenSession> sessionRows,
        Action<string, int>? rowObserver,
        CancellationToken cancellationToken)
    {
        var sessionIdsJson = JsonSerializer.Serialize(sessionRows.Select(item => item.SessionId));
        var assignments = new Dictionary<string, MutableAssignment>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                /* scope-catalog-query:assignments */
                WITH requested(session_id) AS (
                  SELECT CAST(value AS TEXT) FROM json_each($session_ids)
                )
                SELECT requested.session_id,
                       revisions.revision,
                       overrides.state,
                       overrides.repository_id,
                       overrides.revision
                FROM requested
                LEFT JOIN session_repository_assignment_revisions AS revisions
                  ON revisions.session_id=requested.session_id
                LEFT JOIN session_repository_manual_overrides AS overrides
                  ON overrides.session_id=requested.session_id
                ORDER BY requested.session_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$session_ids", sessionIdsJson);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rowIndex = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rowObserver?.Invoke("assignments", rowIndex++);
                cancellationToken.ThrowIfCancellationRequested();
                var sessionId = reader.GetString(0);
                assignments.Add(sessionId, new(
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4)));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                /* scope-catalog-query:candidates */
                WITH requested(session_id) AS (
                  SELECT CAST(value AS TEXT) FROM json_each($session_ids)
                )
                SELECT requested.session_id, contexts.repository_id
                FROM requested
                JOIN session_repository_observation_contexts AS contexts
                  ON contexts.session_id=requested.session_id
                 AND contexts.admission_state='admitted'
                GROUP BY requested.session_id, contexts.repository_id
                ORDER BY requested.session_id COLLATE BINARY, contexts.repository_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$session_ids", sessionIdsJson);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rowIndex = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rowObserver?.Invoke("candidates", rowIndex++);
                cancellationToken.ThrowIfCancellationRequested();
                var assignment = assignments[reader.GetString(0)];
                assignment.Candidates.Add(reader.GetString(1));
                if (assignment.Candidates.Count > MaximumCandidatesPerSession)
                    throw new InvalidOperationException("local_repository_candidate_limit_exceeded");
            }
        }

        var repositories = new List<MutableRepository>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                /* scope-catalog-query:repositories */
                SELECT repositories.repository_id,
                       repositories.display_name,
                       repositories.revision,
                       heads.locator_id
                FROM local_repositories AS repositories
                LEFT JOIN local_repository_locator_heads AS heads
                  ON heads.repository_id=repositories.repository_id
                 AND heads.kind='github_repository'
                ORDER BY repositories.repository_id COLLATE BINARY;
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rowIndex = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rowObserver?.Invoke("repositories", rowIndex++);
                cancellationToken.ThrowIfCancellationRequested();
                var repositoryId = reader.GetString(0);
                var displayName = reader.GetString(1);
                var revision = reader.GetInt64(2);
                var currentLocatorId = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
                    || !LocalRepositoryCatalogValidation.IsDisplayName(displayName)
                    || revision < 1
                    || (currentLocatorId is not null
                        && !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(currentLocatorId)))
                {
                    throw new InvalidOperationException("local_repository_catalog_snapshot_invalid");
                }
                repositories.Add(new(repositoryId, displayName, revision, currentLocatorId));
            }
        }

        return new(assignments, repositories);
    }

    private static LocalRepositoryScopeSnapshot Compose(
        LocalRepositoryScopeRequest request,
        IReadOnlyList<FrozenSession> sessionRows,
        FrozenCatalog catalog,
        FrozenArchive archive,
        Action<int>? compositionObserver,
        CancellationToken cancellationToken)
    {
        var conflictCounts = catalog.Repositories.ToDictionary(item => item.RepositoryId, _ => 0L, StringComparer.Ordinal);
        var sessions = new List<LocalRepositoryScopeSessionSnapshot>(sessionRows.Count);
        for (var index = 0; index < sessionRows.Count; index++)
        {
            compositionObserver?.Invoke(index);
            cancellationToken.ThrowIfCancellationRequested();
            var row = sessionRows[index];
            if (!catalog.Assignments.TryGetValue(row.SessionId, out var assignment)
                || assignment.AuthoritativeRevision is < 0
                || assignment.Candidates.Any(candidate => !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(candidate)
                    || !catalog.RepositoryById.ContainsKey(candidate)))
            {
                throw new InvalidOperationException("local_repository_catalog_snapshot_invalid");
            }

            var candidates = assignment.Candidates.ToArray();
            long revision;
            LocalRepositoryScopeAssignmentState state;
            LocalRepositoryScopeAssignmentAuthority authority;
            string? repositoryId;
            if (assignment.OverrideState == "assigned")
            {
                if (assignment.AuthoritativeRevision is null
                    || assignment.OverrideRevision is null
                    || assignment.AuthoritativeRevision != assignment.OverrideRevision
                    || assignment.AuthoritativeRevision < 1
                    || assignment.OverrideRepositoryId is null
                    || !catalog.RepositoryById.ContainsKey(assignment.OverrideRepositoryId))
                    throw new InvalidOperationException("local_repository_catalog_snapshot_invalid");
                revision = assignment.AuthoritativeRevision.Value;
                state = LocalRepositoryScopeAssignmentState.Assigned;
                authority = LocalRepositoryScopeAssignmentAuthority.Manual;
                repositoryId = assignment.OverrideRepositoryId;
            }
            else if (assignment.OverrideState == "explicitly_unassigned")
            {
                if (assignment.AuthoritativeRevision is null
                    || assignment.OverrideRevision is null
                    || assignment.AuthoritativeRevision != assignment.OverrideRevision
                    || assignment.AuthoritativeRevision < 1
                    || assignment.OverrideRepositoryId is not null)
                    throw new InvalidOperationException("local_repository_catalog_snapshot_invalid");
                revision = assignment.AuthoritativeRevision.Value;
                state = LocalRepositoryScopeAssignmentState.ExplicitlyUnassigned;
                authority = LocalRepositoryScopeAssignmentAuthority.Manual;
                repositoryId = null;
            }
            else if (assignment.OverrideState is not null)
            {
                throw new InvalidOperationException("local_repository_catalog_snapshot_invalid");
            }
            else if (candidates.Length == 0)
            {
                if (assignment.OverrideRevision is not null || assignment.OverrideRepositoryId is not null)
                    throw new InvalidOperationException("local_repository_catalog_snapshot_invalid");
                revision = assignment.AuthoritativeRevision ?? 0;
                state = LocalRepositoryScopeAssignmentState.Unassigned;
                authority = LocalRepositoryScopeAssignmentAuthority.None;
                repositoryId = null;
            }
            else if (candidates.Length == 1)
            {
                if (assignment.AuthoritativeRevision is null or < 1
                    || assignment.OverrideRevision is not null
                    || assignment.OverrideRepositoryId is not null)
                    throw new InvalidOperationException("local_repository_catalog_snapshot_invalid");
                revision = assignment.AuthoritativeRevision ?? 0;
                state = LocalRepositoryScopeAssignmentState.Assigned;
                authority = LocalRepositoryScopeAssignmentAuthority.Automatic;
                repositoryId = candidates[0];
            }
            else
            {
                if (assignment.AuthoritativeRevision is null or < 1
                    || assignment.OverrideRevision is not null
                    || assignment.OverrideRepositoryId is not null)
                    throw new InvalidOperationException("local_repository_catalog_snapshot_invalid");
                revision = assignment.AuthoritativeRevision ?? 0;
                state = LocalRepositoryScopeAssignmentState.Conflict;
                authority = LocalRepositoryScopeAssignmentAuthority.Automatic;
                repositoryId = null;
                foreach (var candidate in candidates)
                    conflictCounts[candidate]++;
            }

            var isUnassigned = state != LocalRepositoryScopeAssignmentState.Assigned;
            var isRequested = request.ScopeKind switch
            {
                LocalRepositoryScopeKind.All => true,
                LocalRepositoryScopeKind.Repository => repositoryId == request.RepositoryId,
                LocalRepositoryScopeKind.Unassigned => isUnassigned,
                _ => false,
            };
            var sessionArchiveFact = archive.Sessions[row.SessionId];
            var repositoryArchived = repositoryId is not null
                && archive.Repositories[repositoryId].State == LocalArchiveState.Archived;
            var isEffectivelyEligible = sessionArchiveFact.State != LocalArchiveState.Archived
                && !repositoryArchived;
            var exclusionReason = sessionArchiveFact.State == LocalArchiveState.Archived
                ? "session_archived"
                : repositoryArchived
                    ? "repository_archived"
                    : null;
            sessions.Add(new(
                row.SessionId,
                row.Row,
                revision,
                state,
                authority,
                repositoryId,
                Array.AsReadOnly(candidates),
                IsAllScopeMember: true,
                IsUnassignedScopeMember: isUnassigned,
                IsRequestedScopeMember: isRequested,
                ArchiveState: sessionArchiveFact.State,
                ArchiveRevision: sessionArchiveFact.Revision,
                IsEffectivelyEligible: isEffectivelyEligible,
                ArchiveExclusionReason: exclusionReason,
                AssignedRepositoryArchiveRevision: repositoryId is null ? null : archive.Repositories[repositoryId].Revision));
        }

        var repositories = new LocalRepositoryCatalogSnapshot[catalog.Repositories.Count];
        for (var index = 0; index < catalog.Repositories.Count; index++)
        {
            compositionObserver?.Invoke(sessionRows.Count + index);
            cancellationToken.ThrowIfCancellationRequested();
            var item = catalog.Repositories[index];
            var archiveFact = archive.Repositories[item.RepositoryId];
            repositories[index] = new(
                item.RepositoryId,
                item.DisplayName,
                item.Revision,
                item.CurrentLocatorId,
                conflictCounts[item.RepositoryId],
                archiveFact.State,
                archiveFact.Revision);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(request, Array.AsReadOnly(repositories), sessions.AsReadOnly());
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class ReadTransactionCapability(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Func<ValueTask>? entryObserver,
        Action? contributorPhaseRevokedObserver) : ILocalRepositoryReadTransaction
    {
        private readonly object gate = new();
        private readonly AsyncLocal<PhaseLease?> ambientLease = new();
        private readonly SQLitePCL.strdelegate_authorizer authorizer = Authorize;
        private ReadPhase phase;
        private long generation;
        private int activeCount;
        private ReadPhase activePhase;
        private int completedReads;
        private int tableReads;
        private bool terminal;
        private bool accepting;
        private TaskCompletionSource? idle;

        internal async ValueTask<T> RunContributorAsync<T>(
            ReadPhase contributorPhase,
            Func<CancellationToken, ValueTask<T>> contribute,
            CancellationToken cancellationToken)
        {
            if (contributorPhase is not (ReadPhase.Session or ReadPhase.Archive))
                throw new ArgumentOutOfRangeException(nameof(contributorPhase));
            var lease = BeginPhase(contributorPhase);
            var previous = ambientLease.Value;
            ambientLease.Value = lease;
            try
            {
                T result;
                try
                {
                    result = await contribute(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    ambientLease.Value = previous;
                    await EndPhaseAsync(lease, requireRead: false).ConfigureAwait(false);
                    ExceptionDispatchInfo.Capture(exception).Throw();
                    throw;
                }
                ambientLease.Value = previous;
                await EndPhaseAsync(lease, requireRead: true).ConfigureAwait(false);
                return result;
            }
            finally
            {
                ambientLease.Value = previous;
            }
        }

        internal async ValueTask<T> RunCatalogAsync<T>(
            Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> read,
            CancellationToken cancellationToken)
        {
            var lease = BeginPhase(ReadPhase.Catalog);
            try
            {
                return await ExecuteAsync(read, cancellationToken, lease).ConfigureAwait(false);
            }
            finally
            {
                await EndPhaseAsync(lease, requireRead: false).ConfigureAwait(false);
            }
        }

        public async ValueTask<T> ReadAsync<T>(
            Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> read,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(read);
            cancellationToken.ThrowIfCancellationRequested();
            var lease = ambientLease.Value;
            if (entryObserver is not null)
                await entryObserver().ConfigureAwait(false);
            return await ExecuteAsync(read, cancellationToken, lease, contributorOnly: true).ConfigureAwait(false);
        }

        internal void Terminate()
        {
            lock (gate)
            {
                terminal = true;
                accepting = false;
                phase = ReadPhase.None;
                generation++;
            }
            SQLitePCL.raw.sqlite3_set_authorizer(
                connection.Handle,
                (SQLitePCL.strdelegate_authorizer?)null,
                null);
        }

        private PhaseLease BeginPhase(ReadPhase nextPhase)
        {
            lock (gate)
            {
                if (terminal || phase != ReadPhase.None || activeCount != 0)
                    throw new InvalidOperationException("local_repository_snapshot_phase_invalid");
                phase = nextPhase;
                accepting = true;
                generation++;
                completedReads = 0;
                tableReads = 0;
                if (SQLitePCL.raw.sqlite3_set_authorizer(connection.Handle, authorizer, this) != SQLitePCL.raw.SQLITE_OK)
                    throw new InvalidOperationException("local_repository_snapshot_authorizer_unavailable");
                return new(this, nextPhase, generation);
            }
        }

        private async ValueTask EndPhaseAsync(PhaseLease lease, bool requireRead)
        {
            Task? wait = null;
            var incomplete = false;
            lock (gate)
            {
                if (lease.Generation != generation || lease.Phase != phase)
                    throw new InvalidOperationException("local_repository_snapshot_phase_invalid");
                accepting = false;
                generation++;
                if (activeCount != 0)
                {
                    incomplete = true;
                    wait = idle?.Task;
                }
            }
            if (lease.Phase is ReadPhase.Session or ReadPhase.Archive)
                contributorPhaseRevokedObserver?.Invoke();
            if (wait is not null)
                await wait.ConfigureAwait(false);
            lock (gate)
            {
                if (phase != lease.Phase || activeCount != 0)
                    throw new InvalidOperationException("local_repository_snapshot_phase_invalid");
                phase = ReadPhase.None;
            }
            if (incomplete || (requireRead && (Volatile.Read(ref completedReads) == 0 || Volatile.Read(ref tableReads) == 0)))
                throw new InvalidOperationException("local_repository_snapshot_contributor_read_required");
        }

        private async ValueTask<T> ExecuteAsync<T>(
            Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> read,
            CancellationToken cancellationToken,
            PhaseLease? lease,
            bool contributorOnly = false)
        {
            var currentIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (terminal
                    || !accepting
                    || lease is null
                    || !ReferenceEquals(lease.Owner, this)
                    || lease.Generation != generation
                    || lease.Phase != phase
                    || (contributorOnly && phase is not (ReadPhase.Session or ReadPhase.Archive)))
                {
                    throw new InvalidOperationException("local_repository_snapshot_phase_revoked");
                }
                if (activeCount != 0)
                    throw new InvalidOperationException("local_repository_snapshot_reader_overlap");
                activeCount = 1;
                activePhase = phase;
                idle = currentIdle;
            }
            try
            {
                var result = await read(connection, transaction, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref completedReads);
                return result;
            }
            finally
            {
                lock (gate)
                {
                    activeCount = 0;
                    activePhase = ReadPhase.None;
                    idle = null;
                }
                currentIdle.TrySetResult();
            }
        }

        private static int Authorize(
            object? userData,
            int action,
            string? table,
            string? column,
            string? database,
            string? trigger)
        {
            var self = (ReadTransactionCapability)userData!;
            if (action != SQLitePCL.raw.SQLITE_READ || table is null)
                return SQLitePCL.raw.SQLITE_OK;
            ReadPhase currentPhase;
            lock (self.gate)
                currentPhase = self.activeCount == 0 ? self.phase : self.activePhase;
            if (currentPhase is ReadPhase.Session or ReadPhase.Archive
                && (table.Equals("local_repositories", StringComparison.OrdinalIgnoreCase)
                    || table.StartsWith("local_repository_", StringComparison.OrdinalIgnoreCase)
                    || table.StartsWith("session_repository_", StringComparison.OrdinalIgnoreCase)))
            {
                return SQLitePCL.raw.SQLITE_DENY;
            }
            Interlocked.Increment(ref self.tableReads);
            return SQLitePCL.raw.SQLITE_OK;
        }
    }

    private sealed class MutableAssignment(
        long? authoritativeRevision,
        string? overrideState,
        string? overrideRepositoryId,
        long? overrideRevision)
    {
        internal long? AuthoritativeRevision { get; } = authoritativeRevision;
        internal string? OverrideState { get; } = overrideState;
        internal string? OverrideRepositoryId { get; } = overrideRepositoryId;
        internal long? OverrideRevision { get; } = overrideRevision;
        internal List<string> Candidates { get; } = [];
    }

    private sealed record FrozenSession(string SessionId, ILocalRepositorySessionSnapshotRow Row);

    private sealed record PhaseLease(ReadTransactionCapability Owner, ReadPhase Phase, long Generation);

    private enum ReadPhase
    {
        None,
        Session,
        Catalog,
        Archive,
    }

    private sealed record MutableRepository(
        string RepositoryId,
        string DisplayName,
        long Revision,
        string? CurrentLocatorId);

    private sealed record FrozenRepository(
        string RepositoryId,
        string DisplayName,
        long Revision,
        string? CurrentLocatorId);

    private sealed record CatalogContribution(
        IReadOnlyDictionary<string, MutableAssignment> Assignments,
        IReadOnlyList<MutableRepository> Repositories);

    private sealed record FrozenCatalog(
        IReadOnlyDictionary<string, MutableAssignment> Assignments,
        IReadOnlyList<FrozenRepository> Repositories,
        IReadOnlyDictionary<string, FrozenRepository> RepositoryById);

    private sealed record FrozenArchive(
        IReadOnlyDictionary<string, LocalArchiveSessionFact> Sessions,
        IReadOnlyDictionary<string, LocalArchiveRepositoryFact> Repositories);
}
