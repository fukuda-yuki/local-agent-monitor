using CopilotAgentObservability.LocalMonitor.SkillRuntime;

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
    Func<IOwnedCopilotClientV1?> clientFactory,
    SkillHostShutdownGateV1 shutdownGate,
    TimeSpan timeout)
{
    private const string EgressNotice = "selected_content_may_be_sent_to_github_copilot_only_after_explicit_ai_action";
    private readonly object sync = new();
    private SettingsAiReadinessSnapshot snapshot = new(
        provider, selectedModel, selectedConfiguration, "configured_not_checked", "not_checked", EgressNotice);
    private Task<SettingsAiReadinessSnapshot>? activeCheck;

    internal SettingsAiReadinessSnapshot GetSnapshot() => Volatile.Read(ref snapshot);

    internal async Task<SettingsAiReadinessSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        Task<SettingsAiReadinessSnapshot> check;
        lock (sync)
        {
            if (shutdownGate.IsNormalShutdownStarted)
                return Set("unavailable", "unavailable");
            check = activeCheck ??= Task.Run(RunCheckAsync);
        }
        return await check.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SettingsAiReadinessSnapshot> RunCheckAsync()
    {
        try
        {
            var client = clientFactory();
            if (client is null) return Set("unconfigured", "unconfigured");
            await using (client.ConfigureAwait(false))
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(shutdownGate.StoppingToken))
            {
                bounded.CancelAfter(timeout);
                await client.StartAsync(bounded.Token).ConfigureAwait(false);
                var status = await client.GetStatusAsync(bounded.Token).ConfigureAwait(false);
                if (status is null) return Set("authentication_required", "authentication_required");
                return CopilotRuntimeIdentityCertifierV1.TryCertify(status, out _)
                    ? Set("ready", "ready")
                    : Set("unavailable", "unavailable");
            }
        }
        catch (OperationCanceledException) when (shutdownGate.IsNormalShutdownStarted)
        {
            return Set("unavailable", "unavailable");
        }
        catch
        {
            return Set("check_failed", "check_failed");
        }
        finally
        {
            lock (sync) activeCheck = null;
        }
    }

    private SettingsAiReadinessSnapshot Set(string readiness, string result)
    {
        var value = Snapshot(readiness, result);
        Volatile.Write(ref snapshot, value);
        return value;
    }

    private SettingsAiReadinessSnapshot Snapshot(string readiness, string result) =>
        new(provider, selectedModel, selectedConfiguration, readiness, result, EgressNotice);
}
