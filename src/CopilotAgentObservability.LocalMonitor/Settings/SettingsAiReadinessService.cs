using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.Settings;

internal sealed record SettingsAiReadinessSnapshot(
    string Provider,
    string SelectedModel,
    string SelectedConfiguration,
    string ReadinessState,
    string LastCheckResult,
    string ProviderEgressNotice);

internal sealed class SettingsAiReadinessService(
    string provider,
    string selectedModel,
    string selectedConfiguration,
    bool configured,
    Func<IOwnedCopilotClientV1?> clientFactory,
    TimeSpan timeout,
    TimeProvider timeProvider) : IHostedLifecycleService, IAsyncDisposable
{
    private const string EgressNotice = "selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action";
    private readonly object sync = new();
    private readonly CancellationTokenSource stopping = new();
    private SettingsAiReadinessSnapshot snapshot = new(
        provider, selectedModel, selectedConfiguration,
        configured ? "configured_not_checked" : "unconfigured", "not_checked", EgressNotice);
    private Task<SettingsAiReadinessSnapshot>? activeCheck;
    private bool closed;

    internal SettingsAiReadinessSnapshot GetSnapshot() => Volatile.Read(ref snapshot);

    internal async Task<SettingsAiReadinessSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        if (!configured) return GetSnapshot();
        Task<SettingsAiReadinessSnapshot> check;
        lock (sync)
        {
            if (closed) return Set("unavailable", "unavailable");
            if (activeCheck is null)
            {
                var completion = new TaskCompletionSource<SettingsAiReadinessSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                check = activeCheck = completion.Task;
                _ = CompleteCheckAsync(completion);
            }
            else check = activeCheck;
        }
        return await check.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task CloseAdmissionAndDrainAsync(CancellationToken cancellationToken)
    {
        Task? drain;
        lock (sync)
        {
            closed = true;
            stopping.Cancel();
            drain = activeCheck;
        }
        if (drain is not null) await drain.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteCheckAsync(TaskCompletionSource<SettingsAiReadinessSnapshot> completion)
    {
        var result = await RunCheckAsync().ConfigureAwait(false);
        completion.TrySetResult(result);
        lock (sync)
        {
            if (ReferenceEquals(activeCheck, completion.Task)) activeCheck = null;
        }
    }

    private async Task<SettingsAiReadinessSnapshot> RunCheckAsync()
    {
        IOwnedCopilotClientV1? client = null;
        try
        {
            client = clientFactory();
            if (client is null) return Set("unavailable", "unavailable");
            using var expiry = new CancellationTokenSource(timeout, timeProvider);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, expiry.Token);
            await client.StartAsync(bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
            var status = await client.GetStatusAsync(bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
            if (status is null) return Set("unavailable", "unavailable");
            if (!CopilotRuntimeIdentityCertifierV1.TryCertify(status, out _)) return Set("unavailable", "unavailable");
            return status.IsAuthenticated
                ? Set("ready", "ready")
                : Set("authentication_required", "authentication_required");
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return Set("unavailable", "unavailable");
        }
        catch
        {
            return Set("check_failed", "check_failed");
        }
        finally
        {
            if (client is not null)
            {
                using var expiry = new CancellationTokenSource(timeout, timeProvider);
                try { await client.DisposeAsync().AsTask().WaitAsync(expiry.Token).ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private SettingsAiReadinessSnapshot Set(string readiness, string result)
    {
        var value = new SettingsAiReadinessSnapshot(provider, selectedModel, selectedConfiguration, readiness, result, EgressNotice);
        Volatile.Write(ref snapshot, value);
        return value;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => CloseAdmissionAndDrainAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => CloseAdmissionAndDrainAsync(cancellationToken);
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => new(CloseAdmissionAndDrainAsync(CancellationToken.None));
}
