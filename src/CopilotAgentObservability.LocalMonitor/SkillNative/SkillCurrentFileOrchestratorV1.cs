using System.Globalization;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

internal enum SkillCurrentFileDispositionV1
{
    Respond,
    AbortWithoutResponse
}

// One fully determined current-file outcome. The route starts no HTTP response until it holds
// this, so an abort still carries no status, header, or entity.
internal sealed record SkillCurrentFileResultV1(
    SkillCurrentFileDispositionV1 Disposition,
    int StatusCode,
    byte[] BodyUtf8)
{
    internal static SkillCurrentFileResultV1 Abort() =>
        new(SkillCurrentFileDispositionV1.AbortWithoutResponse, 0, []);

    internal static SkillCurrentFileResultV1 Respond(int statusCode, byte[] bodyUtf8) =>
        new(SkillCurrentFileDispositionV1.Respond, statusCode, bodyUtf8);
}

// The Retention operation grant seen by the orchestrator. It is the exact subset of the
// historical-content lease the terminal ordering needs, so the interleaving matrix can be driven
// without standing up a real Retention catalog.
internal interface ISkillCurrentFileRetentionGrantV1 : IAsyncDisposable
{
    SkillInvocationSnapshotContentFacts Facts { get; }

    SkillInvocationSnapshotContentTerminalResult TrySealRawResponse();

    SkillInvocationSnapshotContentTerminalResult TryCompleteWithoutRaw();
}

internal sealed record SkillCurrentFileHistoricalAdmissionV1(
    SkillInvocationSnapshotContentOutcome Outcome,
    ISkillCurrentFileRetentionGrantV1? Grant);

internal interface ISkillCurrentFileHistoricalGateV1
{
    Task<SkillCurrentFileHistoricalAdmissionV1> AdmitAsync(
        Guid sessionId,
        Guid snapshotId,
        CancellationToken cancellationToken);
}

internal interface ISkillCurrentAuthorizationGateV1
{
    SkillProjectionCurrentSdkClaimAuthorizationResult TryAcquire(Guid sessionId, Guid snapshotId);
}

internal interface ISkillDiscoveryGatewayV1
{
    Task<CopilotSkillDiscoveryOutcome> DiscoverAsync(
        CopilotRuntimeOperationCapabilityV1 capability,
        DiscoveryRootSetV1 roots,
        CancellationToken cancellationToken);
}

// Gate 2 stages 8 through 12 for POST .../current-file-read, plus the terminal seal ordering.
//
// The route owns everything before this: method, required services and the max-body feature, the
// root generation-lease CAS, origin/CSRF, media, size, and the request parse. This type receives
// the already-won root lease and runs exact lookup -> historical state/Retention -> #154 current
// authorization -> runtime capability -> discovery -> native read.
//
// Two rules shape every exit. Pre-grant failures answer directly and call no terminal method.
// After the Retention operation grant is admitted, no response starts until the complete candidate
// exists and the terminal order for that candidate has been won: a safe error decided before the
// runtime capability needs Retention completion alone; a safe error decided after it needs
// Retention completion and then the runtime seal; and raw success needs the runtime seal first and
// the Retention raw seal second, so Retention raw is never sealed before the runtime authorized it.
internal sealed class SkillCurrentFileOrchestratorV1(
    ISkillCurrentFileHistoricalGateV1 historicalGate,
    ISkillCurrentAuthorizationGateV1 authorizationGate,
    CopilotRuntimeAdmissionV1 runtimeAdmission,
    ISkillDiscoveryGatewayV1 discoveryGateway,
    ICurrentSkillNativeFileReaderV1 nativeReader)
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal const string SnapshotNotFoundToken = "skill_snapshot_not_found";
    internal const string SnapshotExpiredToken = "skill_snapshot_expired";
    internal const string SnapshotContentUnavailableToken = "skill_snapshot_content_unavailable";
    internal const string PersistenceBusyToken = "persistence_busy";
    internal const string LocalMonitorUnavailableToken = "local_monitor_ui_unavailable";
    internal const string ProjectionNotCurrentToken = "skill_projection_not_current";
    internal const string DiscoveryUnavailableToken = "skill_current_file_discovery_unavailable";
    internal const string NotDiscoveredToken = "skill_current_file_not_discovered";
    internal const string UnsafeToken = "skill_current_file_unsafe";
    internal const string RacedToken = "skill_current_file_raced";
    internal const string MissingToken = "skill_current_file_missing";
    internal const string OversizedToken = "skill_current_file_oversized";
    internal const string BinaryToken = "skill_current_file_binary";

    internal async Task<SkillCurrentFileResultV1> ExecuteAsync(
        Guid sessionId,
        Guid snapshotId,
        SkillDiscoveryRootLeaseV1 rootLease,
        CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(rootLease);

        var historical = await historicalGate.AdmitAsync(sessionId, snapshotId, callerToken).ConfigureAwait(false);

        // Pre-grant outcomes answer directly: no grant was committed, so no terminal method exists
        // to call and none may be invented.
        switch (historical.Outcome)
        {
            case SkillInvocationSnapshotContentOutcome.NotFound:
                return Error(404, SnapshotNotFoundToken);
            case SkillInvocationSnapshotContentOutcome.Busy:
                return Error(503, PersistenceBusyToken);
            case SkillInvocationSnapshotContentOutcome.Unavailable:
                return Error(503, LocalMonitorUnavailableToken);
            case SkillInvocationSnapshotContentOutcome.Expired:
                return Error(410, SnapshotExpiredToken);
            case SkillInvocationSnapshotContentOutcome.ContentUnavailable:
                return Error(422, SnapshotContentUnavailableToken);
            case SkillInvocationSnapshotContentOutcome.Aborted:
                return SkillCurrentFileResultV1.Abort();
        }

        if (historical.Grant is null)
        {
            return Error(503, LocalMonitorUnavailableToken);
        }

        await using var grant = historical.Grant;

        var authorizationResult = authorizationGate.TryAcquire(sessionId, snapshotId);
        switch (authorizationResult.Outcome)
        {
            case SkillRegistryCurrentAuthorizationOutcome.NotCurrent:
                return PreRuntimeSafe(grant, 409, ProjectionNotCurrentToken, callerToken);
            case SkillRegistryCurrentAuthorizationOutcome.Busy:
                return PreRuntimeSafe(grant, 503, PersistenceBusyToken, callerToken);
            case SkillRegistryCurrentAuthorizationOutcome.Unavailable:
                return PreRuntimeSafe(grant, 503, LocalMonitorUnavailableToken, callerToken);
        }

        if (authorizationResult.Authorization is null)
        {
            return PreRuntimeSafe(grant, 503, LocalMonitorUnavailableToken, callerToken);
        }

        using var authorization = authorizationResult.Authorization;

        var acquisition = runtimeAdmission.AcquireCurrentFileCapability(callerToken, out var capability);
        if (acquisition == CopilotRuntimeAcquisitionDispositionV1.NormalShutdownClosed)
        {
            // Shutdown closure is not the discovery-unavailable error: it performs the Retention
            // cleanup and aborts with no response whatever that cleanup returns.
            grant.TryCompleteWithoutRaw();
            return SkillCurrentFileResultV1.Abort();
        }

        if (acquisition != CopilotRuntimeAcquisitionDispositionV1.Acquired || capability is null)
        {
            return PreRuntimeSafe(grant, 503, DiscoveryUnavailableToken, callerToken);
        }

        try
        {
            return await ExecuteWithRuntimeCapabilityAsync(
                grant, authorization, capability, rootLease, callerToken).ConfigureAwait(false);
        }
        finally
        {
            capability.Release();
        }
    }

    private async Task<SkillCurrentFileResultV1> ExecuteWithRuntimeCapabilityAsync(
        ISkillCurrentFileRetentionGrantV1 grant,
        SkillProjectionCurrentSdkClaimAuthorization authorization,
        CopilotRuntimeOperationCapabilityV1 capability,
        SkillDiscoveryRootLeaseV1 rootLease,
        CancellationToken callerToken)
    {
        // A claim with no source token cannot equal any of the six closed discovery source tokens,
        // so the single scan could only ever return not-discovered. Deciding it here keeps the
        // outcome identical while making no SDK call, exactly as an invalid historical path does.
        if (authorization.SkillSource is null)
        {
            return PostRuntimeSafe(grant, capability, 409, NotDiscoveredToken, callerToken);
        }

        var discovery = await discoveryGateway
            .DiscoverAsync(capability, rootLease.RootSet, callerToken)
            .ConfigureAwait(false);

        if (discovery is not CopilotSkillDiscoveryOutcome.Discovered discovered)
        {
            return PostRuntimeSafe(grant, capability, 503, DiscoveryUnavailableToken, callerToken);
        }

        var scan = SkillDiscoveryCandidateScannerV1.Scan(
            authorization.SkillName,
            authorization.SkillSource,
            grant.Facts.DefinitionPath,
            discovered.Facts,
            rootLease.RetainedRoots,
            rootLease.Revision);

        switch (scan.Outcome)
        {
            case SkillDiscoveryScanOutcome.NotDiscovered:
                return PostRuntimeSafe(grant, capability, 409, NotDiscoveredToken, callerToken);
            case SkillDiscoveryScanOutcome.Unsafe:
                return PostRuntimeSafe(grant, capability, 409, UnsafeToken, callerToken);
            case SkillDiscoveryScanOutcome.DiscoveryUnavailable:
                return PostRuntimeSafe(grant, capability, 503, DiscoveryUnavailableToken, callerToken);
        }

        var read = nativeReader.Read(scan.Target!, capability.WorkToken);
        return read.Outcome switch
        {
            CurrentSkillNativeOutcomeV1.Unsafe =>
                PostRuntimeSafe(grant, capability, 409, UnsafeToken, callerToken),
            CurrentSkillNativeOutcomeV1.Raced =>
                PostRuntimeSafe(grant, capability, 409, RacedToken, callerToken),
            CurrentSkillNativeOutcomeV1.Missing =>
                PostRuntimeSafe(grant, capability, 404, MissingToken, callerToken),
            CurrentSkillNativeOutcomeV1.OtherNativeFailure =>
                PostRuntimeSafe(grant, capability, 503, LocalMonitorUnavailableToken, callerToken),
            CurrentSkillNativeOutcomeV1.Oversized =>
                PostRuntimeSafe(grant, capability, 422, OversizedToken, callerToken),
            CurrentSkillNativeOutcomeV1.Binary =>
                PostRuntimeSafe(grant, capability, 422, BinaryToken, callerToken),
            _ => RawSuccess(grant, capability, read, callerToken)
        };
    }

    // Retention completion alone authorizes a safe result decided before any runtime capability
    // exists; no runtime capability is fabricated or reacquired to send it.
    private static SkillCurrentFileResultV1 PreRuntimeSafe(
        ISkillCurrentFileRetentionGrantV1 grant,
        int statusCode,
        string errorToken,
        CancellationToken callerToken)
    {
        var completion = grant.TryCompleteWithoutRaw();
        var cause = CurrentSkillRequestTerminationV1.ResolveCause(
            callerToken.IsCancellationRequested,
            IsLostOrBusy(completion),
            runtimeInvalidated: false);

        return cause == CurrentSkillTerminationCause.None
            ? Error(statusCode, errorToken)
            : SkillCurrentFileResultV1.Abort();
    }

    // A safe result decided after the runtime capability exists is two-staged: Retention completes
    // without raw first, then the runtime seal must win before the response starts. A lost seal
    // discards the candidate and substitutes the fixed discovery-unavailable 503, which Retention's
    // completion already authorizes.
    private static SkillCurrentFileResultV1 PostRuntimeSafe(
        ISkillCurrentFileRetentionGrantV1 grant,
        CopilotRuntimeOperationCapabilityV1 capability,
        int statusCode,
        string errorToken,
        CancellationToken callerToken)
    {
        var completion = grant.TryCompleteWithoutRaw();
        var sealWon = !IsLostOrBusy(completion) && capability.TrySealResponse();
        var cause = CurrentSkillRequestTerminationV1.ResolveCause(
            callerToken.IsCancellationRequested,
            IsLostOrBusy(completion),
            runtimeInvalidated: !sealWon);

        return cause switch
        {
            CurrentSkillTerminationCause.None => Error(statusCode, errorToken),
            CurrentSkillTerminationCause.RuntimeInvalidation => Error(503, DiscoveryUnavailableToken),
            _ => SkillCurrentFileResultV1.Abort()
        };
    }

    // Raw success uses the opposite order so Retention raw is never sealed before the runtime
    // authorized the send: the runtime seal wins first, then the Retention raw seal.
    private static SkillCurrentFileResultV1 RawSuccess(
        ISkillCurrentFileRetentionGrantV1 grant,
        CopilotRuntimeOperationCapabilityV1 capability,
        CurrentSkillNativeReadResultV1 read,
        CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            grant.TryCompleteWithoutRaw();
            return SkillCurrentFileResultV1.Abort();
        }

        var facts = grant.Facts;
        var body = SkillInvocationContentDocumentsV1.WriteCurrentFileResponse(
            facts.SnapshotId,
            StrictUtf8.GetBytes(facts.Body),
            facts.BodySha256,
            read.Body!,
            read.ReadAt!.Value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));

        if (!capability.TrySealResponse())
        {
            // The runtime lost, so the buffered raw is discarded. Only a Retention completion
            // without raw authorizes the substitute 503; loss or busy aborts with no response.
            return IsLostOrBusy(grant.TryCompleteWithoutRaw())
                ? SkillCurrentFileResultV1.Abort()
                : Error(503, DiscoveryUnavailableToken);
        }

        if (grant.TrySealRawResponse() == SkillInvocationSnapshotContentTerminalResult.Sealed)
        {
            return SkillCurrentFileResultV1.Respond(200, body);
        }

        // The runtime seal was won but Retention refused the raw seal: discard the raw, abandon the
        // won seal without sending, and abort with no response.
        capability.TryAbandonWonSeal();
        return SkillCurrentFileResultV1.Abort();
    }

    private static bool IsLostOrBusy(SkillInvocationSnapshotContentTerminalResult result) =>
        result is SkillInvocationSnapshotContentTerminalResult.Lost
            or SkillInvocationSnapshotContentTerminalResult.Busy;

    private static SkillCurrentFileResultV1 Error(int statusCode, string errorToken) =>
        SkillCurrentFileResultV1.Respond(statusCode, SkillInvocationJsonWriterV1.WriteErrorEntity(errorToken));
}
