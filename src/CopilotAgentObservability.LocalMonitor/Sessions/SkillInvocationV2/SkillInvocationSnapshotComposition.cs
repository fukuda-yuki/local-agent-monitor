using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

// Host startup refused the configured discovery roots. The reason is one of the two sanitized
// Gate 8 tokens and never carries a configured value or a native fact.
internal sealed class SkillDiscoveryStartupAbortException(string reason)
    : InvalidOperationException(reason)
{
    internal string Reason { get; } = reason;
}

// The production adapters that bind the metadata, historical-content, and current-file readers to
// the route surface. Each one is the single call the route makes; nothing here re-decides an
// outcome an owner already made.
internal static class SkillInvocationSnapshotComposition
{
    internal static async Task<SkillInvocationMetadataDocumentV1Response> ReadMetadataAsync(
        string databasePath,
        SkillProjectionReadService readService,
        TimeProvider timeProvider,
        Guid sessionId,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        var read = await Task.Run(
            () => SkillInvocationSnapshotMetadataReader.ReadOwnedTransaction(
                databasePath, sessionId, snapshotId, timeProvider),
            cancellationToken).ConfigureAwait(false);

        if (read.Outcome == SkillInvocationSnapshotMetadataOutcome.NotFound)
        {
            return Metadata(SkillInvocationMetadataDerivedStateV1.NotFound);
        }

        if (read.Outcome != SkillInvocationSnapshotMetadataOutcome.Found || read.Facts is null)
        {
            return Metadata(SkillInvocationMetadataDerivedStateV1.Unavailable);
        }

        var facts = read.Facts;
        var persistedSnapshot = ToPersistedSnapshot(facts);

        // Only an available snapshot has a claim for the point diagnostic to run against; a fault
        // row's projection_validity is the closed invalid token with no claim lookup at all.
        string? diagnosticToken = null;
        if (persistedSnapshot is SkillInvocationMetadataPersistedSnapshotV1.Available)
        {
            var proof = await Task.Run(
                () => readService.ProveCurrentSdkClaim(sessionId, snapshotId, timeProvider),
                cancellationToken).ConfigureAwait(false);

            if (proof.Outcome != SkillProjectionSdkClaimProofOutcome.Proved || proof.Tuple is null)
            {
                return Metadata(SkillInvocationMetadataDerivedStateV1.Unavailable);
            }

            diagnosticToken = ToDiagnosticToken(SkillProjectionDiagnosticV1.Diagnose(
                isSnapshotAvailable: true,
                new SkillInvocationV2CompatibilityTuple(
                    proof.Tuple.SourceApplicationVersion,
                    proof.Tuple.AdapterVersion,
                    proof.Tuple.NormalizationVersion,
                    proof.Tuple.PayloadSchema,
                    proof.Tuple.SchemaFingerprint)));

            if (diagnosticToken is null)
            {
                return Metadata(SkillInvocationMetadataDerivedStateV1.Unavailable);
            }
        }

        return SkillInvocationMetadataDocumentV1.Write(new SkillInvocationMetadataDocumentV1Input(
            facts.SnapshotId,
            facts.SessionId,
            facts.EventId,
            facts.InvokedAt,
            persistedSnapshot,
            ToRetentionProjection(facts.RetentionProjection),
            diagnosticToken,
            facts.CapturedAt,
            facts.SourceApplicationVersion,
            facts.AdapterVersion,
            facts.PayloadSchema));
    }

    internal static async Task<SkillHistoricalContentRouteResultV1> ReadHistoricalContentAsync(
        string databasePath,
        RetentionCatalogStore retentionStore,
        TimeProvider timeProvider,
        Guid sessionId,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        var read = await SkillInvocationSnapshotContentReader.ReadAsync(
            databasePath, retentionStore, timeProvider, sessionId, snapshotId, cancellationToken)
            .ConfigureAwait(false);

        switch (read.Outcome)
        {
            case SkillInvocationSnapshotContentOutcome.NotFound:
                return new(SkillHistoricalContentRouteOutcomeV1.NotFound, []);
            case SkillInvocationSnapshotContentOutcome.Expired:
                return new(SkillHistoricalContentRouteOutcomeV1.Expired, []);
            case SkillInvocationSnapshotContentOutcome.ContentUnavailable:
                return new(SkillHistoricalContentRouteOutcomeV1.ContentUnavailable, []);
            case SkillInvocationSnapshotContentOutcome.Busy:
                return new(SkillHistoricalContentRouteOutcomeV1.Busy, []);
            case SkillInvocationSnapshotContentOutcome.Unavailable:
                return new(SkillHistoricalContentRouteOutcomeV1.Unavailable, []);
            case SkillInvocationSnapshotContentOutcome.Aborted:
                return new(SkillHistoricalContentRouteOutcomeV1.AbortWithoutResponse, []);
        }

        await using var lease = read.Lease!;
        var facts = read.Facts!;

        byte[] document;
        try
        {
            document = SkillInvocationContentDocumentsV1.WriteHistoricalContent(
                new SkillInvocationHistoricalContentV1Input(
                    facts.SnapshotId,
                    facts.Body,
                    facts.DefinitionPath,
                    facts.BodySha256,
                    facts.DefinitionPathSha256,
                    FormatTimestamp(facts.CapturedAt)));
        }
        catch (ArgumentException)
        {
            // A post-grant mapper fault: discard the buffer, keep the handle, and send the fixed
            // safe error only after Retention completes without raw.
            return lease.TryCompleteWithoutRaw() == SkillInvocationSnapshotContentTerminalResult.CompletedWithoutRaw
                ? new(SkillHistoricalContentRouteOutcomeV1.Unavailable, [])
                : new(SkillHistoricalContentRouteOutcomeV1.AbortWithoutResponse, []);
        }

        if (lease.TrySealRawResponse() != SkillInvocationSnapshotContentTerminalResult.Sealed)
        {
            Array.Clear(document);
            return new(SkillHistoricalContentRouteOutcomeV1.AbortWithoutResponse, []);
        }

        return new(SkillHistoricalContentRouteOutcomeV1.Document, document);
    }

    internal static ICurrentSkillNativeFileReaderV1? CreateNativeReader(
        SkillProducerPathKeyPlatform platform,
        TimeProvider timeProvider) => platform switch
        {
            SkillProducerPathKeyPlatform.Windows =>
                new WindowsCurrentSkillFileReaderV1(timeProvider.GetUtcNow),
            SkillProducerPathKeyPlatform.Linux =>
                new LinuxCurrentSkillFileReaderV1(timeProvider.GetUtcNow),
            _ => null
        };

    private static SkillInvocationMetadataDocumentV1Response Metadata(SkillInvocationMetadataDerivedStateV1 derived) =>
        derived.Outcome == SkillInvocationMetadataDocumentOutcomeV1.NotFound
            ? new(404, SkillInvocationJsonWriterV1.WriteErrorEntity(SkillInvocationMetadataDocumentV1.NotFoundErrorToken))
            : new(503, SkillInvocationJsonWriterV1.WriteErrorEntity(SkillInvocationMetadataDocumentV1.UnavailableErrorToken));

    private static SkillInvocationMetadataPersistedSnapshotV1? ToPersistedSnapshot(
        SkillInvocationSnapshotMetadataFacts facts)
    {
        if (facts.IsAvailable)
        {
            return new SkillInvocationMetadataPersistedSnapshotV1.Available(
                facts.ClaimId!.Value,
                facts.Name!,
                facts.Source,
                facts.Trigger,
                facts.RunId,
                facts.BodySha256!,
                facts.BodyUtf8Bytes!.Value,
                facts.DefinitionPathSha256!,
                facts.DefinitionPathUtf8Bytes!.Value);
        }

        return SkillInvocationMetadataDocumentV1.TryParsePersistedFault(
            facts.FaultState, facts.FaultReason, out var fault)
            ? fault
            : null;
    }

    private static SkillInvocationRetentionProjectionV1 ToRetentionProjection(
        SkillInvocationSnapshotMetadataRetentionProjection projection) => projection switch
        {
            SkillInvocationSnapshotMetadataRetentionProjection.Readable =>
                SkillInvocationRetentionProjectionV1.Readable,
            _ => SkillInvocationRetentionProjectionV1.RetainedDeletedOrTombstoned
        };

    private static string? ToDiagnosticToken(SkillProjectionDiagnosticV1Outcome outcome) => outcome switch
    {
        SkillProjectionDiagnosticV1Outcome.Current => SkillInvocationMetadataDocumentV1.DiagnosticCurrent,
        SkillProjectionDiagnosticV1Outcome.Stale => SkillInvocationMetadataDocumentV1.DiagnosticStale,
        SkillProjectionDiagnosticV1Outcome.Invalid => SkillInvocationMetadataDocumentV1.DiagnosticInvalid,
        _ => null
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'",
            System.Globalization.CultureInfo.InvariantCulture);
}

// Production adapter over the historical-content reader for the current-file route: the same
// reader, but taking the Retention operation lease kind rather than the access kind.
internal sealed class SkillCurrentFileHistoricalGateV1(
    string databasePath,
    RetentionCatalogStore retentionStore,
    TimeProvider timeProvider) : ISkillCurrentFileHistoricalGateV1
{
    public async Task<SkillCurrentFileHistoricalAdmissionV1> AdmitAsync(
        Guid sessionId,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        var read = await SkillInvocationSnapshotContentReader.ReadAsync(
            databasePath,
            retentionStore,
            timeProvider,
            sessionId,
            snapshotId,
            cancellationToken,
            RetentionReadKind.Operation).ConfigureAwait(false);

        return new SkillCurrentFileHistoricalAdmissionV1(
            read.Outcome,
            read.Lease is null ? null : new SkillCurrentFileRetentionGrantV1(read.Lease));
    }
}

internal sealed class SkillCurrentFileRetentionGrantV1(SkillInvocationSnapshotContentLease lease)
    : ISkillCurrentFileRetentionGrantV1
{
    public SkillInvocationSnapshotContentFacts Facts => lease.Facts;

    public SkillInvocationSnapshotContentTerminalResult TrySealRawResponse() => lease.TrySealRawResponse();

    public SkillInvocationSnapshotContentTerminalResult TryCompleteWithoutRaw() => lease.TryCompleteWithoutRaw();

    public ValueTask DisposeAsync() => lease.DisposeAsync();
}

internal sealed class SkillCurrentAuthorizationGateV1(
    SkillProjectionReadService readService,
    TimeProvider timeProvider) : ISkillCurrentAuthorizationGateV1
{
    public SkillProjectionCurrentSdkClaimAuthorizationResult TryAcquire(Guid sessionId, Guid snapshotId) =>
        readService.TryAcquireCurrentSdkClaimAuthorization(sessionId, snapshotId, timeProvider);
}

internal sealed class SkillDiscoveryGatewayAdapterV1(CopilotSdkSkillDiscoveryGateway gateway)
    : ISkillDiscoveryGatewayV1
{
    public Task<CopilotSkillDiscoveryOutcome> DiscoverAsync(
        CopilotRuntimeOperationCapabilityV1 capability,
        DiscoveryRootSetV1 roots,
        CancellationToken cancellationToken) =>
        gateway.DiscoverAsync(capability, roots, cancellationToken);
}
