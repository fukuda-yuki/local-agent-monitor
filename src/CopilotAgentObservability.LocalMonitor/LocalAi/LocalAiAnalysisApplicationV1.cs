using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed record LocalAiSessionStartRequestV1(string SessionId, int TimeoutSeconds = 60);
internal sealed record LocalAiPriorTurnV1(string Question, string Answer);
internal sealed record LocalAiNodeStartRequestV1(string SessionId, string NodeId, int TimeoutSeconds = 60,
    string? Question = null, IReadOnlyList<LocalAiPriorTurnV1>? PriorTurns = null);
internal sealed record LocalAiStartResponseV1(string? RunId, string? ErrorCode);
internal sealed record LocalAiRunStatusV1(string RunId, string State, string ScopeKind, string? SessionId,
    string? NodeId, string? ErrorCode, byte[]? ResultJson = null, string? RequestedAt = null,
    string? StartedAt = null, string? Model = null, string? ConfigurationSha256 = null,
    string? PromptTemplateVersion = null, string? RepositoryId = null, string? ComparisonId = null);
internal sealed record LocalAiReportItemResponseV1(string RunId, string State, byte[]? ResultJson, string ContentState, bool SnapshotChanged);
internal sealed record LocalAiReportPageResponseV1(IReadOnlyList<LocalAiReportItemResponseV1> Reports, string? NextCursor);

internal sealed record LocalAiProviderRequestV1(LocalAiSnapshotProjectionV1 Snapshot,
    LocalAiRunStatusV1 Run, LocalAiRawReadCapabilityV1 RawReads, string? Question, IReadOnlyList<LocalAiPriorTurnV1> PriorTurns);

internal enum LocalAiProviderOutcomeKindV1 { Complete, Partial, Failed }
internal sealed record LocalAiProviderOutcomeV1(LocalAiProviderOutcomeKindV1 Kind, byte[]? ResultJson)
{
    internal static LocalAiProviderOutcomeV1 Complete(byte[] result) => new(LocalAiProviderOutcomeKindV1.Complete, result);
    internal static LocalAiProviderOutcomeV1 Partial() => new(LocalAiProviderOutcomeKindV1.Partial, null);
    internal static LocalAiProviderOutcomeV1 Failed() => new(LocalAiProviderOutcomeKindV1.Failed, null);
}

internal interface ILocalAiProviderAdapterV1
{
    ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request, CancellationToken token);
}

internal interface ILocalAiRunRepositoryV1
{
    LocalAiRunStatusV1 Create(LocalAiSnapshotProjectionV1 snapshot, int timeout);
    void Start(string runId);
    LocalAiRunStatusV1 Complete(string runId, LocalAiProviderOutcomeV1 outcome, DateTimeOffset completedAt);
    LocalAiRunStatusV1 Fail(string runId, string errorCode);
    LocalAiRunStatusV1 Read(string runId);
    bool Cancel(string runId);
    LocalAiReportPageResponseV1 Reports(string sessionId, int? limit, string? cursor, string currentPayloadSha256);
}
internal interface ILocalAiAcceptedSnapshotRepositoryV1
{
    void StoreAccepted(LocalAiSnapshotProjectionV1 snapshot);
    LocalAiSnapshotProjectionV1? ReadAccepted(string snapshotId);
}

internal interface ILocalAiAnalysisApplicationV1
{
    ValueTask<LocalAiStartResponseV1> StartSessionAsync(LocalAiSessionStartRequestV1 request, CancellationToken token);
    ValueTask<LocalAiStartResponseV1> StartNodeAsync(LocalAiNodeStartRequestV1 request, CancellationToken token);
    ValueTask<LocalAiStartResponseV1> StartComparisonAsync(LocalAiComparisonRunRequestV1 request, CancellationToken token) =>
        ValueTask.FromResult(new LocalAiStartResponseV1(null, "provider_unavailable"));
    ValueTask<LocalAiRepositoryPreviewResultV1> PreviewRepositoryAsync(LocalAiRepositoryPreviewRequestV1 request,CancellationToken token)=>
        ValueTask.FromException<LocalAiRepositoryPreviewResultV1>(new InvalidOperationException("provider_unavailable"));
    ValueTask<LocalAiStartResponseV1> StartRepositoryAsync(LocalAiRepositoryRunRequestV1 request,CancellationToken token)=>ValueTask.FromResult(new LocalAiStartResponseV1(null,"provider_unavailable"));
    ValueTask<LocalAiRunStatusV1?> ReadRunAsync(string runId, CancellationToken token);
    ValueTask<bool> CancelAsync(string runId, CancellationToken token);
    ValueTask<LocalAiReportPageResponseV1> ReadReportsAsync(string sessionId, int? limit, string? cursor, CancellationToken token);
}

internal sealed class LocalAiAnalysisApplicationV1(
    Func<CancellationToken, ValueTask<bool>> providerReady,
    ILocalAiSnapshotProjectionServiceV1 snapshots,
    ILocalAiRunRepositoryV1 runs,
    ILocalAiProviderAdapterV1 provider,
    Func<string, LocalAiRawEvidenceV1, CancellationToken, ValueTask<byte[]>>? rawReader = null,
    TimeProvider? timeProvider = null,
    ILocalAiComparisonSnapshotAdapterV1? comparisons = null,
    ILocalAiRepositorySnapshotAdapterV1? repositories = null) : ILocalAiAnalysisApplicationV1, IHostedService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object lifecycleGate = new();
    private readonly Dictionary<string, Admission> active = new(StringComparer.Ordinal);
    private bool accepting = true;
    public ValueTask<LocalAiStartResponseV1> StartSessionAsync(LocalAiSessionStartRequestV1 request, CancellationToken token) =>
        StartAsync(request.SessionId, null, request.TimeoutSeconds, null, [], token);

    public ValueTask<LocalAiStartResponseV1> StartNodeAsync(LocalAiNodeStartRequestV1 request, CancellationToken token)
    {
        ValidateTranscript(request);
        return StartAsync(request.SessionId, request.NodeId, request.TimeoutSeconds, request.Question, request.PriorTurns ?? [], token);
    }

    public async ValueTask<LocalAiStartResponseV1> StartComparisonAsync(LocalAiComparisonRunRequestV1 request, CancellationToken token)
    {
        if (comparisons is null) return new(null, "provider_unavailable");
        return await StartProjectedAsync(request.TimeoutSeconds,
            cancellationToken => ValueTask.FromResult(comparisons.Read(request.RepositoryId, request.ComparisonId, cancellationToken)), token).ConfigureAwait(false);
    }
    public async ValueTask<LocalAiRepositoryPreviewResultV1> PreviewRepositoryAsync(LocalAiRepositoryPreviewRequestV1 request,CancellationToken token)
    {
        if(repositories is null||runs is not ILocalAiAcceptedSnapshotRepositoryV1 accepted)throw new InvalidOperationException("provider_unavailable");
        var preview=await repositories.PreviewAsync(request,token).ConfigureAwait(false);accepted.StoreAccepted(preview.Snapshot);return preview;
    }
    public async ValueTask<LocalAiStartResponseV1> StartRepositoryAsync(LocalAiRepositoryRunRequestV1 request,CancellationToken token)
    {
        if(repositories is null||runs is not ILocalAiAcceptedSnapshotRepositoryV1 accepted)return new(null,"provider_unavailable");
        LocalAiSnapshotProjectionV1? snapshot;try{snapshot=accepted.ReadAccepted(request.SnapshotId);}catch(SqliteException exception) when(exception.SqliteErrorCode is 5 or 6){return new(null,"persistence_busy");}catch(LocalRepositoryScopeSnapshotException exception){return new(null,exception.ErrorCode);}if(snapshot is null)return new(null,"snapshot_not_found");if(snapshot.ExpiresAt<=clock.GetUtcNow())return new(null,"snapshot_expired");if(snapshot.PayloadSha256!=request.PayloadSha256)return new(null,"stale_snapshot");
        LocalAiSnapshotProjectionV1? current;
        try{current=await repositories.RehydrateCurrentAsync(snapshot,token).ConfigureAwait(false);}
        catch(LocalRepositoryScopeSnapshotException exception){return new(null,exception.ErrorCode);}
        if(current is null)return new(null,"stale_snapshot");
        return await StartProjectedAsync(request.TimeoutSeconds,_=>ValueTask.FromResult(current),token).ConfigureAwait(false);
    }

    private async ValueTask<LocalAiStartResponseV1> StartProjectedAsync(int timeout,
        Func<CancellationToken, ValueTask<LocalAiSnapshotProjectionV1>> capture, CancellationToken token)
    {
        if (timeout is < 1 or > 600) return new(null, "invalid_request");
        if (!await providerReady(token).ConfigureAwait(false)) return new(null, "provider_unavailable");
        LocalAiSnapshotProjectionV1 snapshot;
        try { snapshot = await capture(token).ConfigureAwait(false); }
        catch (LocalAiScopeSnapshotException exception) { return new(null, exception.Error); }
        catch (LocalAiScopeTooLargeException) { return new(null, "scope_too_large"); }
        var run = runs.Create(snapshot, timeout); runs.Start(run.RunId);
        var admissionId = Guid.CreateVersion7().ToString();
        var admission = new Admission(CancellationTokenSource.CreateLinkedTokenSource(token)) { RunId=run.RunId };
        lock (lifecycleGate) { if (!accepting) { runs.Cancel(run.RunId); admission.Cancellation.Dispose(); return new(null,"provider_unavailable"); } active.Add(admissionId,admission); }
        admission.Cancellation.CancelAfter(TimeSpan.FromSeconds(timeout));
        _ = ExecuteAsync(admissionId,admission,run.RunId,snapshot,null,[]);
        return new(run.RunId,null);
    }

    private async ValueTask<LocalAiStartResponseV1> StartAsync(string sessionId, string? nodeId, int timeout,
        string? question, IReadOnlyList<LocalAiPriorTurnV1> priorTurns, CancellationToken token)
    {
        if (timeout is < 1 or > 600) return new(null, "invalid_request");
        var admissionId = Guid.CreateVersion7().ToString();
        var admission = new Admission(CancellationTokenSource.CreateLinkedTokenSource(token));
        lock (lifecycleGate)
        {
            if (!accepting) { admission.Cancellation.Dispose(); return new(null, "provider_unavailable"); }
            active.Add(admissionId, admission);
        }
        try
        {
            if (!await providerReady(admission.Cancellation.Token).ConfigureAwait(false))
            { CompleteAdmission(admissionId, admission); return new(null, "provider_unavailable"); }
        }
        catch (OperationCanceledException) when (admission.Cancellation.IsCancellationRequested)
        { CompleteAdmission(admissionId, admission); return new(null, "provider_unavailable"); }
        LocalAiSnapshotProjectionV1 snapshot;
        try
        {
            snapshot = nodeId is null
                ? await snapshots.ReadSessionAsync(sessionId, admission.Cancellation.Token).ConfigureAwait(false)
                : await snapshots.ReadNodeAsync(sessionId, nodeId, admission.Cancellation.Token).ConfigureAwait(false);
        }
        catch (LocalAiScopeTooLargeException) { CompleteAdmission(admissionId, admission); return new(null, "scope_too_large"); }
        catch (LocalWorkspaceSessionDetailException exception) when (exception.Error == "workspace_too_large")
        { CompleteAdmission(admissionId, admission); return new(null, "scope_too_large"); }
        catch (LocalWorkspaceSessionDetailException exception) when (exception.Error == "session_not_found")
        { CompleteAdmission(admissionId, admission); return new(null, nodeId is null ? "session_not_found" : "node_not_found"); }
        catch (LocalWorkspaceSessionDetailException exception) when (exception.Error == "local_monitor_ui_unavailable")
        { CompleteAdmission(admissionId, admission); return new(null, "projection_unavailable"); }
        catch (ArgumentException exception) when (nodeId is not null && exception.Message == "local_ai_node_anchor_not_found")
        { CompleteAdmission(admissionId, admission); return new(null, "node_not_found"); }
        catch (OperationCanceledException) when (admission.Cancellation.IsCancellationRequested)
        { CompleteAdmission(admissionId, admission); return new(null, "provider_unavailable"); }
        catch { CompleteAdmission(admissionId, admission); throw; }
        LocalAiRunStatusV1 run;
        try
        {
            lock (lifecycleGate)
            {
                if (!accepting || admission.Cancellation.IsCancellationRequested)
                { CompleteAdmission(admissionId, admission); return new(null, "provider_unavailable"); }
                run = runs.Create(snapshot, timeout);
                runs.Start(run.RunId);
                admission.RunId = run.RunId;
            }
        }
        catch (InvalidOperationException exception) when (exception.Message == "local_ai_snapshot_scope_too_large")
        { CompleteAdmission(admissionId, admission); return new(null, "scope_too_large"); }
        catch { CompleteAdmission(admissionId, admission); throw; }
        admission.Cancellation.CancelAfter(TimeSpan.FromSeconds(timeout));
        _ = ExecuteAsync(admissionId, admission, run.RunId, snapshot, question, priorTurns);
        return new(run.RunId, null);
    }

    private async Task ExecuteAsync(string admissionId, Admission admission, string runId, LocalAiSnapshotProjectionV1 snapshot,
        string? question, IReadOnlyList<LocalAiPriorTurnV1> priorTurns)
    {
        try
        {
            var raw = new LocalAiRawReadCapabilityV1(snapshot.RawEvidence?.Keys ?? [],
                (identifier, cancellationToken) => snapshot.RawEvidence?.TryGetValue(identifier, out var evidence) == true && rawReader is not null
                    ? rawReader(evidence.SessionId ?? snapshot.SessionId!, evidence, cancellationToken)
                    : ValueTask.FromException<byte[]>(new LocalAiRawReadException("raw_unavailable")));
            var startedRun = runs.Read(runId);
            var outcome = await provider.ExecuteAsync(new(snapshot, startedRun, raw, question, priorTurns), admission.Cancellation.Token).ConfigureAwait(false);
            if (snapshot.ScopeKind is "session" or "node" && !await snapshots.IsCurrentAsync(snapshot, CancellationToken.None).ConfigureAwait(false)
                || snapshot.ScopeKind=="repository_selection"&&repositories is not null&&!await repositories.IsCurrentAsync(snapshot,CancellationToken.None).ConfigureAwait(false))
                runs.Fail(runId, "stale_snapshot");
            else
            {
                var completedAt = clock.GetUtcNow();
                var normalized = outcome.Kind == LocalAiProviderOutcomeKindV1.Complete
                    ? outcome with { ResultJson = LocalAiResultEnvelopeV1.Compose(outcome.ResultJson, snapshot, startedRun, completedAt) }
                    : outcome;
                runs.Complete(runId, normalized, completedAt);
            }
        }
        catch (OperationCanceledException) when (admission.Cancellation.IsCancellationRequested)
        { if (runs.Read(runId).State == "canceled") return; runs.Fail(runId, "timed_out"); }
        catch (OperationCanceledException) { runs.Cancel(runId); }
        catch (LocalAiScopeTooLargeException) { runs.Fail(runId, "scope_too_large"); }
        catch { runs.Fail(runId, "provider_failed"); }
        finally { CompleteAdmission(admissionId, admission); }
    }

    public ValueTask<LocalAiRunStatusV1?> ReadRunAsync(string runId, CancellationToken token)
    {
        try { return ValueTask.FromResult<LocalAiRunStatusV1?>(runs.Read(runId)); }
        catch (InvalidOperationException) { return ValueTask.FromResult<LocalAiRunStatusV1?>(null); }
    }

    public ValueTask<bool> CancelAsync(string runId, CancellationToken token)
    {
        var canceled = runs.Cancel(runId);
        lock (lifecycleGate)
            if (canceled && active.Values.FirstOrDefault(item => item.RunId == runId)?.Cancellation is { } execution)
                try { execution.Cancel(); } catch (ObjectDisposedException) { }
        return ValueTask.FromResult(canceled);
    }

    public async ValueTask<LocalAiReportPageResponseV1> ReadReportsAsync(string sessionId, int? limit, string? cursor, CancellationToken token)
    {
        var current = await snapshots.ReadSessionAsync(sessionId, token).ConfigureAwait(false);
        return runs.Reports(sessionId, limit, cursor, current.PayloadSha256);
    }

    private static void ValidateTranscript(LocalAiNodeStartRequestV1 request)
    {
        var turns = request.PriorTurns ?? [];
        if (turns.Count > 16 || request.Question is { } question && System.Text.Encoding.UTF8.GetByteCount(question) > 4096
            || turns.Any(turn => System.Text.Encoding.UTF8.GetByteCount(turn.Question) > 4096
                || System.Text.Encoding.UTF8.GetByteCount(turn.Answer) > 32768))
            throw new ArgumentException("invalid_request");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<string, Admission>[] pending;
        lock (lifecycleGate) { accepting = false; pending = active.ToArray(); }
        foreach (var item in pending)
        {
            if (item.Value.RunId is { } runId) runs.Cancel(runId);
            try { item.Value.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        await Task.WhenAll(pending.Select(static item => item.Value.Completion.Task)).ConfigureAwait(false);
    }

    private void CompleteAdmission(string admissionId, Admission admission)
    {
        lock (lifecycleGate) active.Remove(admissionId);
        admission.Cancellation.Dispose(); admission.Completion.TrySetResult();
    }

    private sealed class Admission(CancellationTokenSource cancellation)
    {
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? RunId { get; set; }
    }
}

internal static class LocalAiResultEnvelopeV1
{
    internal static byte[] Compose(byte[]? providerJson, LocalAiSnapshotProjectionV1 snapshot,
        LocalAiRunStatusV1 run, DateTimeOffset completedAt)
    {
        try
        {
            if (providerJson is null || providerJson.Length > 1_048_576) return [];
            using var document = JsonDocument.Parse(providerJson, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var names = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(static property => property.Name).ToArray() : [];
            string[] expected = ["summary", "findings", "improvement_suggestions", "limitations"];
            if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || !names.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)) return [];
            var content = root;
            if (content.GetProperty("summary").ValueKind != JsonValueKind.String
                || content.GetProperty("findings").ValueKind != JsonValueKind.Array
                || content.GetProperty("improvement_suggestions").ValueKind != JsonValueKind.Array
                || content.GetProperty("limitations").ValueKind != JsonValueKind.Array) return [];
            object scope = snapshot.ScopeKind switch
            {
                "session" or "node" => new { kind=snapshot.ScopeKind, session_id=snapshot.SessionId, node_id=snapshot.NodeId, anchor_id=snapshot.AnchorId },
                "repository_selection" => new { kind=snapshot.ScopeKind, repository_id=snapshot.RepositoryId, anchor_id=snapshot.AnchorId },
                "comparison" => new { kind=snapshot.ScopeKind, repository_id=snapshot.RepositoryId, comparison_id=snapshot.ComparisonId, anchor_id=snapshot.AnchorId },
                _ => throw new InvalidOperationException("local_ai_scope_invalid"),
            };
            var entity = new
            {
                scope,
                snapshot = new { snapshot_id=snapshot.SnapshotId, payload_sha256=snapshot.PayloadSha256 },
                summary = content.GetProperty("summary").Clone(),
                findings = content.GetProperty("findings").Clone(),
                improvement_suggestions = content.GetProperty("improvement_suggestions").Clone(),
                limitations = content.GetProperty("limitations").Clone(),
                provenance = new { provider="github_copilot_sdk", model=run.Model, configuration_sha256=run.ConfigurationSha256,
                    prompt_template_version=run.PromptTemplateVersion, requested_at=run.RequestedAt, started_at=run.StartedAt,
                    completed_at=completedAt.ToUniversalTime().ToString("O"), snapshot_id=snapshot.SnapshotId,
                    snapshot_sha256=snapshot.PayloadSha256, coverage=new { included=snapshot.EvidenceIdentifiers.Count, excluded=0,
                        content_available=snapshot.RawEvidence?.Values.Any(static evidence =>
                            string.Equals(evidence.Locator.State,"available",StringComparison.Ordinal)) == true } },
            };
            return JsonSerializer.SerializeToUtf8Bytes(entity);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        { return []; }
    }
}
