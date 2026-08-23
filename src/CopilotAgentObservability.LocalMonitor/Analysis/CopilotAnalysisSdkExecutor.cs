using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class CopilotAnalysisSdkExecutor : ICopilotAnalysisSdkExecutor
{
    private readonly Func<CopilotClientOptions, SessionConfig, CopilotClient> createClient;
    private readonly Func<string, bool> environmentEntryPresent;

    internal CopilotAnalysisSdkExecutor()
        : this(static (options, _) => new CopilotClient(options)) { }

    internal CopilotAnalysisSdkExecutor(
        Func<CopilotClientOptions, SessionConfig, CopilotClient> createClient,
        Func<string, bool>? environmentEntryPresent = null)
    {
        ArgumentNullException.ThrowIfNull(createClient);
        this.createClient = createClient;
        this.environmentEntryPresent = environmentEntryPresent ?? IsProcessEnvironmentEntryPresent;
    }

    public async Task<CopilotAnalysisExecutionResult> ExecuteAsync(string childDirectory, CopilotAnalysisExecutionSettings settings, CopilotAnalysisToolRequest request, CancellationToken cancellationToken)
    {
        if (environmentEntryPresent("COPILOT_CLI_PATH"))
            throw new InvalidOperationException("The bundled Copilot runtime is unavailable.");
        var sessionConfig = CreateSessionConfig(
            settings,
            request,
            request.Data.InstructionFindingCollector,
            childDirectory);
        var clientOptions = new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            BaseDirectory = childDirectory,
            WorkingDirectory = childDirectory,
        };
        var client = createClient(clientOptions, sessionConfig);
        CopilotSession? session = null;
        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            await client.StartAsync(cancellationToken);
            session = await client.CreateSessionAsync(sessionConfig, cancellationToken);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var final = new StringBuilder();
            using var subscription = session.On<SessionEvent>(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta: final.Append(delta.Data.DeltaContent); break;
                    case AssistantMessageEvent message when final.Length == 0: final.Append(message.Data.Content); break;
                    case SessionIdleEvent: done.TrySetResult(); break;
                    case SessionErrorEvent error: done.TrySetException(new InvalidOperationException(error.Data.Message)); break;
                }
            });
            await session.SendAndWaitAsync(new MessageOptions { Prompt = request.Prompt }, TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken);
            done.TrySetResult();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new(final.Length == 0 ? "Copilot SDK analysis completed without a textual result." : final.ToString());
        }
        catch (Exception exception)
        {
            primaryFailure = ExceptionDispatchInfo.Capture(exception);
            throw;
        }
        finally
        {
            Exception? disposeFailure = null;
            try { if (session is not null) await session.DisposeAsync(); } catch (Exception exception) { disposeFailure = exception; }
            try { await client.DisposeAsync(); } catch (Exception exception) { disposeFailure ??= exception; }
            if (primaryFailure is null && disposeFailure is not null) ExceptionDispatchInfo.Capture(disposeFailure).Throw();
        }
    }

    public async Task<CopilotAnalysisExecutionResult> ExecuteAsync(
        string childDirectory,
        CopilotAnalysisExecutionSettings settings,
        CopilotAnalysisToolRequest request,
        CopilotAnalysisRootsExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(childDirectory, context.AnalysisScope.ChildDirectory, StringComparison.Ordinal))
            throw new InvalidOperationException("The analysis directory ownership is invalid.");
        var scopeOwnership = context.ScopeOwnership ?? new AnalysisSdkScopeOwnership(context.AnalysisScope);
        if (!scopeOwnership.TryTransferToExecutor())
            throw new InvalidOperationException("The analysis directory ownership is invalid.");

        CopilotRuntimeGenerationV1? candidate = null;
        CopilotRuntimeOperationCapabilityV1? lifecycle = null;
        SkillDiscoveryRootLeaseV1? rootLease = null;
        var succeeded = false;
        try
        {
            if (!context.RootGeneration.TryAcquireLease(out rootLease))
                throw new InvalidOperationException("Skill discovery roots are unavailable.");
            if (context.EnvironmentEntryPresent("COPILOT_CLI_PATH"))
                throw new InvalidOperationException("The bundled Copilot runtime is unavailable.");
            if (context.Bridge is null)
                throw new InvalidOperationException("The Skill invocation bridge is unavailable.");
            var ownedClient = context.OwnedClientFactory(childDirectory)
                ?? throw new InvalidOperationException("The bundled Copilot runtime is unavailable.");
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, context.AnalysisScope.LeaseLostToken, context.HostStoppingToken);
                var clientStartCount = 0;
                var statusObservationCount = 0;
                await ownedClient.StartAsync(linked.Token).ConfigureAwait(false);
                clientStartCount++;
                OwnedSessionExecutionCheckpointObservationV1.Notify(context.ExecutionCheckpointObserver, OwnedSessionExecutionCheckpointV1.ClientStarted);
                var status = await ownedClient.GetStatusAsync(linked.Token).ConfigureAwait(false);
                statusObservationCount++;
                if (!CopilotRuntimeIdentityCertifierV1.TryCertify(status, out var identity))
                    throw new InvalidOperationException("The bundled Copilot runtime could not be certified.");
                OwnedSessionExecutionCheckpointObservationV1.Notify(context.ExecutionCheckpointObserver, OwnedSessionExecutionCheckpointV1.IdentityCertified);

                if (!scopeOwnership.TryTransferToCandidate())
                    throw new InvalidOperationException("The analysis directory ownership is invalid.");
                candidate = context.Admission.CreateUnpublishedCandidate(
                    ownedClient.RuntimeClient, identity, context.AnalysisScope, scopeOwnership);
                OwnedSessionExecutionCheckpointObservationV1.Notify(context.ExecutionCheckpointObserver, OwnedSessionExecutionCheckpointV1.CandidateCreated);
                if (!candidate.TryAcquireOperationCapability(linked.Token, out lifecycle))
                    throw new InvalidOperationException("The candidate lifecycle is unavailable.");
                var workToken = lifecycle.WorkToken;

                var baseline = CreateSessionConfig(settings, request,
                    request.Data.InstructionFindingCollector, childDirectory);
                var roots = rootLease.RootSet.SkillDirectoryKeys;
                var proof = new RetainedRootOwnedSessionSkillProofProviderV1(
                    rootLease, context.NativeReader, workToken);
                var probe = await ProbeAsync(ownedClient, baseline, roots, identity, proof,
                    workToken, () => context.Admission.InvalidateCandidate(candidate)).ConfigureAwait(false);
                OwnedSessionExecutionCheckpointObservationV1.Notify(context.ExecutionCheckpointObserver, OwnedSessionExecutionCheckpointV1.ProbeCertified);

                var result = await ExecuteOwnedSessionAsync(ownedClient, baseline, roots, probe.Inventory,
                    proof, identity, candidate, request.Prompt, settings.TimeoutSeconds,
                    context, context.ExecutionDriver ?? DefaultOwnedSessionExecutionDriverV1.Instance,
                    clientStartCount, statusObservationCount, probe.SessionCount, probe.Client, workToken).ConfigureAwait(false);
                succeeded = true;
                return new(result.Markdown, candidate, result.Evidence);
            }
            catch
            {
                if (candidate is null)
                {
                    try { await ownedClient.DisposeAsync().ConfigureAwait(false); }
                    catch { }
                }
                throw;
            }
        }
        finally
        {
            lifecycle?.Release();
            rootLease?.Dispose();
            if (!succeeded && candidate is not null)
            {
                try { await context.Admission.DiscardCandidateAsync(candidate).ConfigureAwait(false); }
                catch { }
            }
            else if (candidate is null)
            {
                try { await scopeOwnership.DisposeByExecutorAsync().ConfigureAwait(false); }
                catch { }
            }
            if (succeeded && candidate is not null && !candidate.TryMarkReady())
            {
                await context.Admission.DiscardCandidateAsync(candidate).ConfigureAwait(false);
                throw new InvalidOperationException("The candidate was not ready for publication.");
            }
            if (succeeded && candidate is not null)
                OwnedSessionExecutionCheckpointObservationV1.Notify(context.ExecutionCheckpointObserver, OwnedSessionExecutionCheckpointV1.CandidateReady);
        }
    }

    private static async Task<(OwnedSessionFrozenSkillInventoryV1 Inventory, int SessionCount, IOwnedCopilotClientV1 Client)> ProbeAsync(
        IOwnedCopilotClientV1 client,
        SessionConfig baseline,
        IReadOnlyList<string> roots,
        CertifiedSkillProducerIdentityV1 identity,
        IOwnedSessionSkillProofProviderV1 proofProvider,
        CancellationToken cancellationToken,
        Action invalidateCandidate)
    {
        var callbackState = new ProbeCallbackStateV1(
            identity.SourceApplicationVersion, invalidateCandidate);
        var config = OwnedSessionSdkPolicyV1.CreateProbeConfig(
            baseline, roots, callbackState.OnEvent);
        IOwnedCopilotSessionV1? session = null;
        OwnedSessionFrozenSkillInventoryV1? inventory = null;
        var sessionCount = 0;
        try
        {
            session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false);
            sessionCount++;
            await session.EnsureSkillsLoadedAsync(cancellationToken).ConfigureAwait(false);
            var facts = await session.ListSkillsAsync(cancellationToken).ConfigureAwait(false);
            inventory = OwnedSessionSdkPolicyV1.TryFreezeProbeInventory(facts, roots, proofProvider);
        }
        finally
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
        if (session is null || !callbackState.TryClose(session.SessionId))
            throw new InvalidOperationException("The probe session identity is unavailable.");
        return (inventory ?? throw new InvalidOperationException("The probe inventory could not be certified."), sessionCount, client);
    }

    private sealed class ProbeCallbackStateV1(string sourceVersion, Action invalidateCandidate)
    {
        private readonly object sync = new();
        private string? observedSessionId;
        private bool poisoned;
        private bool closed;
        private bool invalidated;

        internal void OnEvent(SessionEvent sourceEvent)
        {
            var invalidate = false;
            lock (sync)
            {
                if (!IsRelevant(sourceEvent)) return;
                if (closed)
                {
                    poisoned = true;
                    if (!invalidated) invalidated = invalidate = true;
                }
                else if (sourceEvent is SessionStartEvent start
                    && observedSessionId is null
                    && start.Data is not null
                    && !string.IsNullOrEmpty(start.Data.SessionId)
                    && string.Equals(start.Data.CopilotVersion, sourceVersion, StringComparison.Ordinal))
                {
                    observedSessionId = start.Data.SessionId;
                }
                else
                {
                    poisoned = true;
                }
            }
            if (invalidate)
                try { invalidateCandidate(); } catch { }
        }

        internal bool TryClose(string expectedCreatedSessionId)
        {
            lock (sync)
            {
                if (closed) return false;
                closed = true;
                return !poisoned
                    && !string.IsNullOrEmpty(expectedCreatedSessionId)
                    && string.Equals(observedSessionId, expectedCreatedSessionId, StringComparison.Ordinal);
            }
        }

        private static bool IsRelevant(SessionEvent sourceEvent) =>
            sourceEvent is SessionStartEvent or SkillInvokedEvent or SessionTaskCompleteEvent
                or SessionErrorEvent or ModelCallFailureEvent or AbortEvent;
    }

    private static async Task<(string Markdown, OwnedSessionExecutionEvidenceV1 Evidence)> ExecuteOwnedSessionAsync(
        IOwnedCopilotClientV1 client,
        SessionConfig baseline,
        IReadOnlyList<string> roots,
        OwnedSessionFrozenSkillInventoryV1 inventory,
        IOwnedSessionSkillProofProviderV1 proofProvider,
        CertifiedSkillProducerIdentityV1 identity,
        CopilotRuntimeGenerationV1 candidate,
        string prompt,
        int timeoutSeconds,
        CopilotAnalysisRootsExecutionContext context,
        IOwnedSessionExecutionDriverV1 executionDriver,
        int clientStartCount,
        int statusObservationCount,
        int probeSessionCount,
        IOwnedCopilotClientV1 probeClient,
        CancellationToken cancellationToken)
    {
        var final = new StringBuilder();
        string? nativeSessionId = null;
        OwnedSessionCallbackStateV1? callbackState = null;
        byte[] PrepareStart(SessionStartEvent sourceEvent) => SerializeEnvelope(
            OwnedSessionEnvelopeMapperV1.TryMap(sourceEvent.Data?.SessionId ?? string.Empty, identity, sourceEvent));
        byte[] PrepareInvocation(SkillInvokedEvent sourceEvent)
        {
            if (!candidate.TryAcquireOperationCapability(cancellationToken, out var preparation))
                throw new InvalidOperationException("Invocation preparation is unavailable.");
            try
            {
                if (!SkillInvocationNormalizedJsonV1.TryWriteCancellable(
                    nativeSessionId, sourceEvent, preparation, preparation.WorkToken, out var body))
                    throw new InvalidOperationException("Invocation preparation failed.");
                return body;
            }
            finally { preparation.Release(); }
        }
        byte[] PrepareTerminal(SessionTaskCompleteEvent sourceEvent) => SerializeEnvelope(
            OwnedSessionEnvelopeMapperV1.TryMap(nativeSessionId ?? string.Empty, identity, sourceEvent));

        callbackState = new OwnedSessionCallbackStateV1(inventory, proofProvider,
            identity.SourceApplicationVersion, PrepareStart, PrepareInvocation, PrepareTerminal, cancellationToken,
            () => context.Admission.InvalidateCandidate(candidate));
        var config = OwnedSessionSdkPolicyV1.CreateExecutionConfig(
            baseline, roots, inventory.DisabledSkills, sourceEvent =>
            {
                try
                {
                    if (sourceEvent is SessionStartEvent start && start.Data is not null)
                    {
                        nativeSessionId = start.Data.SessionId;
                    }
                    lock (final)
                    {
                        if (sourceEvent is AssistantMessageDeltaEvent delta) final.Append(delta.Data.DeltaContent);
                        else if (sourceEvent is AssistantMessageEvent message && final.Length == 0) final.Append(message.Data.Content);
                    }
                    callbackState.OnEvent(sourceEvent);
                }
                catch { callbackState.Poison(); }
            });
        var baselineTools = baseline.AvailableTools?.ToList() ?? [];
        var executionTools = config.AvailableTools?.ToList() ?? [];
        var exactToolUnion = baselineTools.Count > 0
            && baselineTools.All(static tool => tool.StartsWith("custom:", StringComparison.Ordinal))
            && executionTools.Count == baselineTools.Count + 2
            && executionTools.Distinct(StringComparer.Ordinal).Count() == executionTools.Count
            && baselineTools.All(executionTools.Contains)
            && executionTools.Contains("builtin:skill", StringComparer.Ordinal)
            && executionTools.Contains("builtin:task_complete", StringComparer.Ordinal);

        OwnedSessionPreparedImportV1? prepared;
        var executionInventoryCount = 0;
        var executionSessionCount = 0;
        var executionInventoryCertified = false;
        await using (var session = await client.CreateSessionAsync(config, cancellationToken).ConfigureAwait(false))
        {
            executionSessionCount++;
            await session.EnsureSkillsLoadedAsync(cancellationToken).ConfigureAwait(false);
            var facts = await session.ListSkillsAsync(cancellationToken).ConfigureAwait(false);
            executionInventoryCount = facts?.Count ?? 0;
            if (!OwnedSessionSdkPolicyV1.ValidateExecutionInventory(inventory, facts, proofProvider))
                throw new InvalidOperationException("The execution inventory could not be certified.");
            executionInventoryCertified = true;
            OwnedSessionExecutionCheckpointObservationV1.Notify(context.ExecutionCheckpointObserver, OwnedSessionExecutionCheckpointV1.ExecutionInventoryCertified);
            if (!callbackState.TryBindCreatedSession(session.SessionId))
                throw new InvalidOperationException("The execution session identity is unavailable.");
            await executionDriver.ExecuteAsync(session, prompt, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
            OwnedSessionExecutionCheckpointObservationV1.Notify(context.ExecutionCheckpointObserver, OwnedSessionExecutionCheckpointV1.DriverCompleted);
        }
        prepared = callbackState.TryFreeze();
        if (prepared is null)
            throw new InvalidOperationException("The execution session did not complete successfully.");
        OwnedSessionExecutionCheckpointObservationV1.Notify(context.ExecutionCheckpointObserver, OwnedSessionExecutionCheckpointV1.CallbacksFrozen);
        if (!candidate.IsAdmitted)
            throw new InvalidOperationException("The candidate lifecycle was lost.");
        if (prepared.Bodies.Count != 0)
        {
            var importer = new OwnedSessionPostCompletionImporterV1(
                context.Bridge!, context.SessionEventQueue, context.CommitTimeout);
            if (!await importer.ImportAsync(candidate, prepared, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The completed session could not be imported.");
        }
        OwnedSessionExecutionCheckpointObservationV1.Notify(context.ExecutionCheckpointObserver, OwnedSessionExecutionCheckpointV1.ImportCompleted);
        var evidence = new OwnedSessionExecutionEvidenceV1(
            identity.SourceApplicationVersion,
            identity.ProtocolVersion,
            ClientStartCount: clientStartCount,
            StatusObservationCount: statusObservationCount,
            ProbeSessionCount: probeSessionCount,
            ExecutionSessionCount: executionSessionCount,
            RetainedRootCount: roots.Count,
            RetainedSkillCount: inventory.Retained.Count,
            ProbeInventoryCount: inventory.Probe.Count,
            ExecutionInventoryCount: executionInventoryCount,
            PreparedInvocationCount: prepared.Bodies.Count,
            SameClient: ReferenceEquals(probeClient, client),
            ExactToolUnion: exactToolUnion,
            RetainedOnlyInventory: executionInventoryCertified,
            ProbeNativeReproof: inventory.Retained.Count > 0,
            ExecutionNativeReproof: executionInventoryCertified,
            CallbackNativeReproof: prepared.Bodies.Count > 0);
        lock (final)
            return (final.Length == 0
                ? "Copilot SDK analysis completed without a textual result."
                : final.ToString(), evidence);
    }

    private static byte[] SerializeEnvelope(CopilotAgentObservability.LocalMonitor.Sessions.SessionIngestEnvelope? envelope) =>
        envelope is null
            ? throw new InvalidOperationException("The session envelope is invalid.")
            : JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static SessionConfig CreateSessionConfig(
        CopilotAnalysisExecutionSettings settings,
        CopilotAnalysisToolRequest request,
        InstructionFindingSubmissionCollectorV1? instructionFindingCollector,
        string childDirectory)
    {
        var tools = new List<AIFunctionDeclaration>
        {
            DefineTool("get_raw_trace", "Return the raw trace records for this Local Monitor analysis run.", () => Serialize(request.Data.RawTrace)),
            DefineTool("get_raw_record", "Return the selected raw record for this Local Monitor analysis run.", () => Serialize(request.Data.RawRecord)),
            DefineTool("get_raw_span_context", "Return the selected raw span context for this Local Monitor analysis run.", () => Serialize(request.Data.RawSpanContext)),
            DefineTool("get_trace_summary", "Return the sanitized trace summary for this Local Monitor analysis run.", () => Serialize(request.Data.TraceSummary)),
            DefineTool("get_trace_span_tree", "Return the sanitized span tree for this Local Monitor analysis run.", () => Serialize(request.Data.TraceSpanTree)),
            DefineTool("get_cache_summary", "Return the sanitized cache summary for this Local Monitor analysis run.", () => Serialize(request.Data.CacheSummary)),
            DefineTool("get_instruction_evidence", "Return deterministic instruction evidence for this Local Monitor analysis run.", () => Serialize(request.Data.InstructionEvidence)),
        };
        if (instructionFindingCollector is not null)
            tools.Add(DefineInstructionFindingSubmissionTool(instructionFindingCollector));
        var availableTools = new ToolSet();
        foreach (var tool in tools)
            availableTools.AddCustom(tool.Name);
        return new SessionConfig
        {
            Model = settings.Model,
            Streaming = true,
            EnableSkills = false,
            OnPermissionRequest = static (_, _) => Task.FromResult(DenyPermission()),
            Provider = settings.Provider,
            Tools = tools,
            AvailableTools = availableTools,
            WorkingDirectory = childDirectory,
            LargeOutput = new LargeToolOutputConfig { Enabled = true, OutputDirectory = childDirectory },
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = "You are analyzing a local Copilot/agent observability trace. Use the provided tools for raw data. Do not claim the response is repository-safe." },
        };
    }

#pragma warning disable GHCP001
    private static GitHub.Copilot.Rpc.PermissionDecision DenyPermission() =>
        GitHub.Copilot.Rpc.PermissionDecision.UserNotAvailable();
#pragma warning restore GHCP001

    private static AIFunction DefineTool(string name, string description, Func<string> tool) => CopilotTool.DefineTool((([Description("No input is required for this run-scoped Local Monitor tool.")] string? _ = null) => tool()), new CopilotToolOptions { SkipPermission = true }, new AIFunctionFactoryOptions { Name = name, Description = description });

    private static AIFunction DefineInstructionFindingSubmissionTool(InstructionFindingSubmissionCollectorV1 collector) =>
        CopilotTool.DefineTool(
            ([Description("Closed instruction-finding category id.")] string category,
             [Description("Finding verdict: supported, weak, or incomplete.")] string verdict,
             [Description("Extractor source: deterministic_prepass or prompt_only.")] string extractor_source,
             [Description("JSON array containing only exact evidence references returned by get_instruction_evidence.")] string evidence_refs_json) =>
                collector.SubmitWire(category, verdict, extractor_source, evidence_refs_json),
            new CopilotToolOptions { SkipPermission = true },
            new AIFunctionFactoryOptions
            {
                Name = "submit_instruction_finding",
                Description = "Validate and submit one instruction finding without free-form or raw content.",
            });

    private static string Serialize(object? value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static bool IsProcessEnvironmentEntryPresent(string name)
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process))
            if (entry.Key is string key && string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
