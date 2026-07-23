using System.Collections.Concurrent;
using System.Globalization;
using CopilotAgentObservability.InstructionFindings;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class HistoricalAnalysisCoordinatorV1 : IAsyncDisposable
{
    private const int MaximumEfficiencyRuns = 32;
    private static readonly TimeSpan DefaultEfficiencyTimeout = TimeSpan.FromSeconds(30);
    private readonly HistoricalEvidenceApplicationServiceV1 evidence;
    private readonly HistoricalInstructionAnalysisCompositionV1? instructionComposition;
    private readonly HistoricalInstructionAnalysisApplicationServiceV1? instructionRunner;
    private readonly IHistoricalEfficiencyExecutorV1 efficiencyExecutor;
    private readonly HistoricalAnalysisEvidenceResolverV1 evidenceResolver = new();
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan efficiencyTimeout;
    private readonly bool sanitizedOnly;
    private readonly CancellationTokenSource stopping;
    private readonly ConcurrentDictionary<long, Task> instructionTasks = new();
    private readonly ConcurrentDictionary<string, Task> efficiencyTasks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> efficiencyInvocations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EfficiencyRun> efficiencyRuns = new(StringComparer.Ordinal);
    private readonly object efficiencyGate = new();

    internal HistoricalAnalysisCoordinatorV1(HistoricalEvidenceApplicationServiceV1 evidence)
        : this(evidence, null, null, CancellationToken.None, null, null, null, false)
    {
    }

    internal HistoricalAnalysisCoordinatorV1(
        HistoricalEvidenceApplicationServiceV1 evidence,
        HistoricalInstructionAnalysisCompositionV1? instructionComposition,
        IHistoricalInstructionAnalysisProviderV1? instructionProvider,
        CancellationToken applicationStopping,
        IHistoricalEfficiencyExecutorV1? efficiencyExecutor = null,
        TimeProvider? timeProvider = null,
        TimeSpan? efficiencyTimeout = null,
        bool sanitizedOnly = false)
    {
        this.evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        this.instructionComposition = instructionComposition;
        this.efficiencyExecutor = efficiencyExecutor ?? DefaultHistoricalEfficiencyExecutorV1.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.efficiencyTimeout = efficiencyTimeout ?? DefaultEfficiencyTimeout;
        this.sanitizedOnly = sanitizedOnly;
        if (this.efficiencyTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(efficiencyTimeout));
        stopping = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        if (instructionComposition is not null && instructionProvider is not null)
        {
            try
            {
                instructionRunner = instructionComposition.CreateRunner(instructionProvider);
            }
            catch (HistoricalInstructionAnalysisValidationException)
            {
                instructionRunner = null;
            }
        }
    }

    internal async ValueTask<HistoricalAnalysisPreviewResponseV1> PreviewAsync(
        HistoricalAnalysisPreviewRequestV1 request,
        CancellationToken cancellationToken)
    {
        if (request is null
            || request.SchemaVersion != HistoricalAnalysisContractsV1.PreviewRequestSchemaVersion
            || request.Selection is null
            || request.Selection.ExplicitSessionIds is null
            || request.Selection.SourceSurfaces is null
            || sanitizedOnly && !request.Selection.SanitizedOnly)
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.InvalidRequest);

        HistoricalEvidenceExtractionV1 extraction;
        try
        {
            extraction = await evidence.CreateAsync(request.Selection, cancellationToken).ConfigureAwait(false);
        }
        catch (HistoricalEvidenceValidationException exception)
            when (exception.Code == HistoricalEvidenceValidationCodeV1.InvalidContract)
        {
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.InvalidRequest, exception);
        }
        catch (HistoricalEvidenceValidationException exception)
        {
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.StoreUnavailable, exception);
        }

        var safe = extraction.RepositorySafe;
        return new(
            HistoricalAnalysisContractsV1.PreviewResponseSchemaVersion,
            safe.ExtractionId,
            extraction.RawLocalSha256,
            extraction.RepositorySafeSha256,
            safe.Selection,
            safe.Sessions,
            safe.ExcludedSessions,
            safe.TruncatedBefore,
            safe.TruncatedSessionCount);
    }

    internal HistoricalAnalysisInstructionStartResponseV1 StartInstruction(
        HistoricalAnalysisInstructionStartRequestV1 request)
    {
        if (request is null
            || request.SchemaVersion != HistoricalAnalysisContractsV1.InstructionStartRequestSchemaVersion)
            throw InvalidRequest();

        var ownerRequest = new HistoricalInstructionAnalysisRequestV1(
            HistoricalInstructionAnalysisContractsV1.RequestSchemaVersion,
            request.ExtractionId,
            request.RawLocalSha256,
            request.Model,
            request.Provider,
            request.ConfigurationSha256,
            request.TimeoutMs,
            request.PromptTemplateVersion);
        try
        {
            HistoricalInstructionAnalysisJsonV1.ValidateRequest(ownerRequest);
        }
        catch (HistoricalInstructionAnalysisValidationException exception)
        {
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.InvalidRequest, exception);
        }

        HistoricalEvidenceExtractionV1? extraction;
        try
        {
            extraction = evidence.Get(request.ExtractionId);
        }
        catch (HistoricalEvidenceValidationException exception)
        {
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.StoreUnavailable, exception);
        }
        if (extraction is null)
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.ExtractionNotFound);
        if (!string.Equals(extraction.RawLocalSha256, request.RawLocalSha256, StringComparison.Ordinal))
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.StaleExtraction);
        if (instructionRunner is null)
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.ProviderUnavailable);

        long runId;
        try
        {
            runId = instructionRunner.Start(ownerRequest);
        }
        catch (HistoricalInstructionAnalysisValidationException exception)
        {
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.StoreUnavailable, exception);
        }
        ScheduleInstruction(runId);
        return new(
            HistoricalAnalysisContractsV1.InstructionStartResponseSchemaVersion,
            runId.ToString(CultureInfo.InvariantCulture),
            "queued");
    }

    internal HistoricalInstructionAnalysisReadV1 GetInstruction(long runId)
    {
        if (runId <= 0 || instructionComposition is null) throw InvalidRequest();
        try
        {
            var read = instructionComposition.Get(runId)
                ?? throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.RunNotFound);
            _ = HistoricalInstructionAnalysisReadConsumerV1.Validate(read);
            if (read.HandoffBytes.Length > 0
                && InstructionFindingHandoffConsumerV1.Validate(read.HandoffBytes) != read.RunId)
                throw new HistoricalInstructionAnalysisValidationException(
                    HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence);
            return read;
        }
        catch (HistoricalAnalysisException) { throw; }
        catch (Exception exception) when (exception is HistoricalInstructionAnalysisValidationException
            or InstructionFindingHandoffConsumerValidationException
            or InstructionFindingValidationException)
        {
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.StoreUnavailable, exception);
        }
    }

    internal HistoricalAnalysisEfficiencyStartResponseV1 StartEfficiency(
        HistoricalAnalysisEfficiencyStartRequestV1 request)
    {
        if (request is null
            || request.SchemaVersion != HistoricalAnalysisContractsV1.EfficiencyStartRequestSchemaVersion
            || !ValidOpaqueId(request.ExtractionId, "historical-extraction-")
            || !ValidSha256(request.RepositorySafeSha256))
            throw InvalidRequest();

        HistoricalEvidenceExtractionV1? extraction;
        try
        {
            extraction = evidence.Get(request.ExtractionId);
        }
        catch (HistoricalEvidenceValidationException exception)
        {
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.StoreUnavailable, exception);
        }
        if (extraction is null)
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.ExtractionNotFound);
        if (!string.Equals(
            extraction.RepositorySafeSha256,
            request.RepositorySafeSha256,
            StringComparison.Ordinal))
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.StaleExtraction);

        EfficiencyRun run;
        lock (efficiencyGate)
        {
            PruneEfficiencyRuns();
            if (efficiencyRuns.Count >= MaximumEfficiencyRuns)
                throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.PreconditionFailed);
            string runId;
            do
            {
                runId = $"historical-efficiency-run-{Guid.CreateVersion7():N}";
            } while (efficiencyRuns.ContainsKey(runId));
            run = new(
                runId,
                request.ExtractionId,
                request.RepositorySafeSha256,
                timeProvider.GetUtcNow());
            if (!efficiencyRuns.TryAdd(runId, run))
                throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.PreconditionFailed);
        }

        ScheduleEfficiency(run.RunId);
        return new(
            HistoricalAnalysisContractsV1.EfficiencyStartResponseSchemaVersion,
            run.RunId,
            "queued");
    }

    internal HistoricalAnalysisEfficiencyStatusResponseV1 GetEfficiency(string runId)
    {
        if (!ValidOpaqueId(runId, "historical-efficiency-run-")) throw InvalidRequest();
        if (!efficiencyRuns.TryGetValue(runId, out var run))
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.RunNotFound);
        return run.Snapshot();
    }

    internal HistoricalAnalysisEvidenceResolveResponseV1 ResolveEvidence(
        HistoricalAnalysisEvidenceResolveRequestV1 request)
    {
        if (request is null
            || request.SchemaVersion != HistoricalAnalysisContractsV1.EvidenceResolveRequestSchemaVersion
            || !ValidOpaqueId(request.ExtractionId, "historical-extraction-")
            || !ValidSha256(request.RepositorySafeSha256)
            || request.References is null
            || request.References.Count is < 1 or > HistoricalAnalysisContractsV1.MaximumEvidenceReferences
            || request.References.Distinct(StringComparer.Ordinal).Count() != request.References.Count
            || request.References.Any(reference => !ValidEvidenceReference(reference)))
            throw InvalidRequest();

        HistoricalEvidenceExtractionV1? extraction;
        try
        {
            extraction = evidence.Get(request.ExtractionId);
        }
        catch (HistoricalEvidenceValidationException exception)
        {
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.StoreUnavailable, exception);
        }
        if (extraction is null)
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.ExtractionNotFound);
        if (!string.Equals(
            extraction.RepositorySafeSha256,
            request.RepositorySafeSha256,
            StringComparison.Ordinal))
            throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.StaleExtraction);

        return evidenceResolver.Resolve(extraction, request.References);
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(instructionTasks.Values.Concat(efficiencyTasks.Values)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        try
        {
            await Task.WhenAll(efficiencyInvocations.Values).ConfigureAwait(false);
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
        }
        stopping.Dispose();
    }

    private void ScheduleInstruction(long runId)
    {
        var task = Task.Run(
            () => instructionRunner!.RunAsync(runId, stopping.Token),
            CancellationToken.None);
        instructionTasks[runId] = task;
        _ = task.ContinueWith(
            (completedTask, state) => ((ConcurrentDictionary<long, Task>)state!).TryRemove(runId, out _),
            instructionTasks,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ScheduleEfficiency(string runId)
    {
        var task = Task.Run(() => RunEfficiencyAsync(runId), CancellationToken.None);
        efficiencyTasks[runId] = task;
        _ = task.ContinueWith(
            (completedTask, state) =>
                ((ConcurrentDictionary<string, Task>)state!).TryRemove(runId, out _),
            efficiencyTasks,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunEfficiencyAsync(string runId)
    {
        if (!efficiencyRuns.TryGetValue(runId, out var run) || !run.MarkRunning(timeProvider.GetUtcNow()))
            return;

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        execution.CancelAfter(efficiencyTimeout);
        try
        {
            HistoricalEvidenceExtractionV1? extraction;
            try
            {
                extraction = evidence.Get(run.ExtractionId);
            }
            catch (HistoricalEvidenceValidationException)
            {
                run.CompleteFailure(HistoricalAnalysisEfficiencyStateV1.AnalysisFailed, timeProvider.GetUtcNow());
                return;
            }
            if (extraction is null
                || !string.Equals(
                    extraction.RepositorySafeSha256,
                    run.RepositorySafeSha256,
                    StringComparison.Ordinal))
            {
                run.CompleteFailure(HistoricalAnalysisEfficiencyStateV1.StaleExtraction, timeProvider.GetUtcNow());
                return;
            }

            var analysis = await InvokeEfficiencyAsync(run.RunId, extraction, execution.Token)
                .WaitAsync(execution.Token)
                .ConfigureAwait(false);
            execution.Token.ThrowIfCancellationRequested();
            var current = evidence.Get(run.ExtractionId);
            if (current is null
                || !string.Equals(
                    current.RepositorySafeSha256,
                    run.RepositorySafeSha256,
                    StringComparison.Ordinal))
            {
                run.CompleteFailure(HistoricalAnalysisEfficiencyStateV1.StaleExtraction, timeProvider.GetUtcNow());
                return;
            }
            var validated = ValidateEfficiencyResult(run, analysis);
            var state = validated.Receipt.State switch
            {
                HistoricalEfficiencyAnalysisStateV1.Succeeded =>
                    HistoricalAnalysisEfficiencyStateV1.Succeeded,
                HistoricalEfficiencyAnalysisStateV1.ZeroDrivers =>
                    HistoricalAnalysisEfficiencyStateV1.ZeroDrivers,
                _ => throw new HistoricalEfficiencyValidationException(
                    HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput),
            };
            run.CompleteSuccess(
                state,
                validated.Receipt,
                validated.PayloadSha256,
                timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            run.CompleteFailure(HistoricalAnalysisEfficiencyStateV1.Canceled, timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            run.CompleteFailure(HistoricalAnalysisEfficiencyStateV1.TimedOut, timeProvider.GetUtcNow());
        }
        catch (Exception)
        {
            run.CompleteFailure(HistoricalAnalysisEfficiencyStateV1.AnalysisFailed, timeProvider.GetUtcNow());
        }
    }

    private Task<HistoricalEfficiencyAnalysisV1> InvokeEfficiencyAsync(
        string runId,
        HistoricalEvidenceExtractionV1 extraction,
        CancellationToken cancellationToken)
    {
        var invocation = Task.Run(
            () => efficiencyExecutor.AnalyzeAsync(extraction, cancellationToken),
            CancellationToken.None);
        if (!efficiencyInvocations.TryAdd(runId, invocation))
            throw new InvalidOperationException("Efficiency invocation already exists.");
        _ = invocation.ContinueWith(
            (completedTask, state) =>
                ((ConcurrentDictionary<string, Task>)state!).TryRemove(runId, out _),
            efficiencyInvocations,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return invocation;
    }

    private static ValidatedEfficiencyResult ValidateEfficiencyResult(
        EfficiencyRun run,
        HistoricalEfficiencyAnalysisV1 analysis)
    {
        if (analysis is null
            || analysis.Receipt is null
            || analysis.CanonicalBytes is null
            || !ValidSha256(analysis.PayloadSha256))
            throw new HistoricalEfficiencyValidationException(
                HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput);
        var payloadSha256 = HistoricalEvidenceExtractorV1.Sha256(analysis.CanonicalBytes);
        var receipt = HistoricalEfficiencyJsonV1.Deserialize(analysis.CanonicalBytes);
        if (!analysis.CanonicalBytes.SequenceEqual(HistoricalEfficiencyJsonV1.Serialize(analysis.Receipt))
            || !string.Equals(payloadSha256, analysis.PayloadSha256, StringComparison.Ordinal)
            || receipt.ExtractionId != run.ExtractionId
            || receipt.ExtractionSha256 != run.RepositorySafeSha256)
            throw new HistoricalEfficiencyValidationException(
                HistoricalEfficiencyValidationCodeV1.InvalidHistoricalEfficiencyInput);
        return new(receipt, payloadSha256);
    }

    private void PruneEfficiencyRuns()
    {
        foreach (var run in efficiencyRuns.Values
            .Select(value => value.Snapshot())
            .Where(value => value.CompletedAt is not null
                && !efficiencyInvocations.ContainsKey(value.AnalysisRunId))
            .OrderBy(value => value.CompletedAt)
            .ThenBy(value => value.AnalysisRunId, StringComparer.Ordinal))
        {
            if (efficiencyRuns.Count < MaximumEfficiencyRuns) break;
            efficiencyRuns.TryRemove(run.AnalysisRunId, out _);
        }
    }

    private static bool ValidOpaqueId(string? value, string prefix) =>
        value is not null
        && value.Length == prefix.Length + 32
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).ToArray().All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9');

    private static bool ValidSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');

    private static bool ValidEvidenceReference(string? value) =>
        InstructionFindingReferenceTokenizationV1.IsSessionReference(value)
        || InstructionFindingReferenceTokenizationV1.IsTraceReference(value)
        || InstructionFindingReferenceTokenizationV1.IsSpanReference(value);

    private static HistoricalAnalysisException InvalidRequest() =>
        new(HistoricalAnalysisErrorCodesV1.InvalidRequest);

    private sealed record ValidatedEfficiencyResult(
        HistoricalEfficiencyReceiptV1 Receipt,
        string PayloadSha256);

    private sealed class EfficiencyRun(
        string runId,
        string extractionId,
        string repositorySafeSha256,
        DateTimeOffset requestedAt)
    {
        private readonly object gate = new();
        private HistoricalAnalysisEfficiencyStateV1 state = HistoricalAnalysisEfficiencyStateV1.Queued;
        private DateTimeOffset? startedAt;
        private DateTimeOffset? completedAt;
        private HistoricalEfficiencyReceiptV1? receipt;
        private string? receiptPayloadSha256;

        internal string RunId { get; } = runId;
        internal string ExtractionId { get; } = extractionId;
        internal string RepositorySafeSha256 { get; } = repositorySafeSha256;

        internal bool MarkRunning(DateTimeOffset timestamp)
        {
            lock (gate)
            {
                if (state != HistoricalAnalysisEfficiencyStateV1.Queued) return false;
                state = HistoricalAnalysisEfficiencyStateV1.Running;
                startedAt = timestamp;
                return true;
            }
        }

        internal void CompleteFailure(HistoricalAnalysisEfficiencyStateV1 terminal, DateTimeOffset timestamp)
        {
            lock (gate)
            {
                if (state != HistoricalAnalysisEfficiencyStateV1.Running
                    || terminal is HistoricalAnalysisEfficiencyStateV1.Queued
                        or HistoricalAnalysisEfficiencyStateV1.Running
                        or HistoricalAnalysisEfficiencyStateV1.Succeeded
                        or HistoricalAnalysisEfficiencyStateV1.ZeroDrivers)
                    return;
                state = terminal;
                completedAt = timestamp;
            }
        }

        internal void CompleteSuccess(
            HistoricalAnalysisEfficiencyStateV1 terminal,
            HistoricalEfficiencyReceiptV1 exactReceipt,
            string exactPayloadSha256,
            DateTimeOffset timestamp)
        {
            lock (gate)
            {
                if (state != HistoricalAnalysisEfficiencyStateV1.Running
                    || terminal is not (HistoricalAnalysisEfficiencyStateV1.Succeeded
                        or HistoricalAnalysisEfficiencyStateV1.ZeroDrivers))
                    return;
                state = terminal;
                receipt = exactReceipt;
                receiptPayloadSha256 = exactPayloadSha256;
                completedAt = timestamp;
            }
        }

        internal HistoricalAnalysisEfficiencyStatusResponseV1 Snapshot()
        {
            lock (gate)
            {
                return new(
                    HistoricalAnalysisContractsV1.EfficiencyStatusSchemaVersion,
                    RunId,
                    ExtractionId,
                    RepositorySafeSha256,
                    HistoricalAnalysisEfficiencyStateWireV1.ToWireValue(state),
                    requestedAt,
                    startedAt,
                    completedAt,
                    receipt,
                    receiptPayloadSha256);
            }
        }
    }

    private sealed class DefaultHistoricalEfficiencyExecutorV1 : IHistoricalEfficiencyExecutorV1
    {
        internal static DefaultHistoricalEfficiencyExecutorV1 Instance { get; } = new();

        public Task<HistoricalEfficiencyAnalysisV1> AnalyzeAsync(
            HistoricalEvidenceExtractionV1 extraction,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(HistoricalEfficiencyAnalyzerV1.Analyze(extraction));
        }
    }
}
