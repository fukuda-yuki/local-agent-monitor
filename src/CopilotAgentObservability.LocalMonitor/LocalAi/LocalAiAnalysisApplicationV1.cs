using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed record LocalAiSessionStartRequestV1(string SessionId, int TimeoutSeconds = 60);
internal sealed record LocalAiPriorTurnV1(string Question, string Answer);
internal sealed record LocalAiNodeStartRequestV1(string SessionId, string NodeId, int TimeoutSeconds = 60,
    string? Question = null, IReadOnlyList<LocalAiPriorTurnV1>? PriorTurns = null);
internal sealed record LocalAiStartResponseV1(string? RunId, string? ErrorCode);
internal sealed record LocalAiRunStatusV1(string RunId, string State, string ScopeKind, string SessionId,
    string? NodeId, string? ErrorCode, byte[]? ResultJson = null, string? RequestedAt = null,
    string? StartedAt = null, string? Model = null, string? ConfigurationSha256 = null,
    string? PromptTemplateVersion = null);
internal sealed record LocalAiReportItemResponseV1(string RunId, string State, byte[] ResultJson, bool SnapshotChanged);
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
    LocalAiRunStatusV1 Complete(string runId, LocalAiProviderOutcomeV1 outcome);
    LocalAiRunStatusV1 Fail(string runId, string errorCode);
    LocalAiRunStatusV1 Read(string runId);
    bool Cancel(string runId);
    LocalAiReportPageResponseV1 Reports(string sessionId, int? limit, string? cursor, string currentRevision);
}

internal interface ILocalAiAnalysisApplicationV1
{
    ValueTask<LocalAiStartResponseV1> StartSessionAsync(LocalAiSessionStartRequestV1 request, CancellationToken token);
    ValueTask<LocalAiStartResponseV1> StartNodeAsync(LocalAiNodeStartRequestV1 request, CancellationToken token);
    ValueTask<LocalAiRunStatusV1?> ReadRunAsync(string runId, CancellationToken token);
    ValueTask<bool> CancelAsync(string runId, CancellationToken token);
    ValueTask<LocalAiReportPageResponseV1> ReadReportsAsync(string sessionId, int? limit, string? cursor, CancellationToken token);
}

internal sealed class LocalAiAnalysisApplicationV1(
    Func<CancellationToken, ValueTask<bool>> providerReady,
    ILocalAiSnapshotProjectionServiceV1 snapshots,
    ILocalAiRunRepositoryV1 runs,
    ILocalAiProviderAdapterV1 provider,
    Func<string, LocalAiRawEvidenceV1, CancellationToken, ValueTask<byte[]>>? rawReader = null) : ILocalAiAnalysisApplicationV1
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> active = new(StringComparer.Ordinal);
    public ValueTask<LocalAiStartResponseV1> StartSessionAsync(LocalAiSessionStartRequestV1 request, CancellationToken token) =>
        StartAsync(request.SessionId, null, request.TimeoutSeconds, null, [], token);

    public ValueTask<LocalAiStartResponseV1> StartNodeAsync(LocalAiNodeStartRequestV1 request, CancellationToken token)
    {
        ValidateTranscript(request);
        return StartAsync(request.SessionId, request.NodeId, request.TimeoutSeconds, request.Question, request.PriorTurns ?? [], token);
    }

    private async ValueTask<LocalAiStartResponseV1> StartAsync(string sessionId, string? nodeId, int timeout,
        string? question, IReadOnlyList<LocalAiPriorTurnV1> priorTurns, CancellationToken token)
    {
        if (timeout is < 1 or > 600) return new(null, "invalid_request");
        if (!await providerReady(token).ConfigureAwait(false)) return new(null, "provider_unavailable");
        LocalAiSnapshotProjectionV1 snapshot;
        try
        {
            snapshot = nodeId is null
                ? await snapshots.ReadSessionAsync(sessionId, token).ConfigureAwait(false)
                : await snapshots.ReadNodeAsync(sessionId, nodeId, token).ConfigureAwait(false);
        }
        catch (LocalAiScopeTooLargeException) { return new(null, "scope_too_large"); }
        var run = runs.Create(snapshot, timeout);
        runs.Start(run.RunId);
        var execution = new CancellationTokenSource(); execution.CancelAfter(TimeSpan.FromSeconds(timeout));
        if (!active.TryAdd(run.RunId, execution)) throw new InvalidOperationException("local_ai_run_duplicate");
        _ = ExecuteAsync(run.RunId, snapshot, question, priorTurns, execution);
        return new(run.RunId, null);
    }

    private async Task ExecuteAsync(string runId, LocalAiSnapshotProjectionV1 snapshot,
        string? question, IReadOnlyList<LocalAiPriorTurnV1> priorTurns, CancellationTokenSource execution)
    {
        try
        {
            var raw = new LocalAiRawReadCapabilityV1(snapshot.EvidenceIdentifiers,
                (identifier, cancellationToken) => snapshot.RawEvidence?.TryGetValue(identifier, out var evidence) == true && rawReader is not null
                    ? rawReader(snapshot.SessionId, evidence, cancellationToken)
                    : ValueTask.FromException<byte[]>(new LocalAiRawReadException("raw_unavailable")));
            var startedRun = runs.Read(runId);
            var outcome = await provider.ExecuteAsync(new(snapshot, startedRun, raw, question, priorTurns), execution.Token).ConfigureAwait(false);
            if (!await snapshots.IsCurrentAsync(snapshot, CancellationToken.None).ConfigureAwait(false))
                runs.Fail(runId, "stale_snapshot");
            else runs.Complete(runId, outcome);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        { if (runs.Read(runId).State == "canceled") return; runs.Fail(runId, "timed_out"); }
        catch (OperationCanceledException) { runs.Cancel(runId); }
        catch { runs.Fail(runId, "provider_failed"); }
        finally { active.TryRemove(runId, out _); execution.Dispose(); }
    }

    public ValueTask<LocalAiRunStatusV1?> ReadRunAsync(string runId, CancellationToken token)
    {
        try { return ValueTask.FromResult<LocalAiRunStatusV1?>(runs.Read(runId)); }
        catch (InvalidOperationException) { return ValueTask.FromResult<LocalAiRunStatusV1?>(null); }
    }

    public ValueTask<bool> CancelAsync(string runId, CancellationToken token)
    {
        var canceled = runs.Cancel(runId);
        if (canceled && active.TryGetValue(runId, out var execution)) execution.Cancel();
        return ValueTask.FromResult(canceled);
    }

    public async ValueTask<LocalAiReportPageResponseV1> ReadReportsAsync(string sessionId, int? limit, string? cursor, CancellationToken token)
    {
        var current = await snapshots.ReadSessionAsync(sessionId, token).ConfigureAwait(false);
        return runs.Reports(sessionId, limit, cursor, current.Revision);
    }

    private static void ValidateTranscript(LocalAiNodeStartRequestV1 request)
    {
        var turns = request.PriorTurns ?? [];
        if (turns.Count > 16 || request.Question is { } question && System.Text.Encoding.UTF8.GetByteCount(question) > 4096
            || turns.Any(turn => System.Text.Encoding.UTF8.GetByteCount(turn.Question) > 4096
                || System.Text.Encoding.UTF8.GetByteCount(turn.Answer) > 32768))
            throw new ArgumentException("invalid_request");
    }
}
