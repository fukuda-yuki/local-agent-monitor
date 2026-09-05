using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed record LocalAiDiscoveredModelV1(string Id, string DisplayName);

internal sealed record LocalAiModelDiscoverySnapshotV1(
    string State,
    IReadOnlyList<LocalAiDiscoveredModelV1> Models,
    string? LegacyConfiguredModel,
    bool LegacyEligible,
    int Generation);

internal interface ILocalAiModelDiscoveryV1
{
    LocalAiModelDiscoverySnapshotV1 Current();
    ValueTask<LocalAiModelDiscoverySnapshotV1> RefreshAsync(CancellationToken token);
    bool IsSelectable(string model);
}

internal static class LocalAiModelIdentityV1
{
    internal const int MaximumLength = 200;

    internal static bool IsAuto(string value) => string.Equals(value, "auto", StringComparison.Ordinal);

    internal static bool IsSupportedId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength || IsAuto(value)) return false;
        foreach (var character in value)
            if (!char.IsLetterOrDigit(character) && character is not '_' and not '-' and not '.' and not '/')
                return false;
        return true;
    }

    internal static string SanitizeDisplayName(string? value, string fallback)
    {
        var candidate = MeasurementSanitizer.SanitizeFreeFormName(value);
        if (candidate is not null && !Uri.TryCreate(candidate, UriKind.Absolute, out _)) return candidate;
        var sanitizedFallback = MeasurementSanitizer.SanitizeFreeFormName(fallback);
        return sanitizedFallback is not null && !Uri.TryCreate(sanitizedFallback, UriKind.Absolute, out _)
            ? sanitizedFallback
            : fallback;
    }
}

internal sealed class LocalAiModelDiscoveryServiceV1(
    Func<IOwnedCopilotClientV1?> clientFactory,
    string? legacyConfiguredModel) : ILocalAiModelDiscoveryV1
{
    private readonly object gate = new();
    private LocalAiModelDiscoverySnapshotV1 snapshot = new("not_checked", [],
        LocalAiModelIdentityV1.IsSupportedId(legacyConfiguredModel) ? legacyConfiguredModel : null, false, 0);

    public LocalAiModelDiscoverySnapshotV1 Current()
    {
        lock (gate) return snapshot;
    }

    public bool IsSelectable(string model)
    {
        var current = Current();
        return current.State == "ready"
            && current.Models.Any(item => string.Equals(item.Id, model, StringComparison.Ordinal));
    }

    public async ValueTask<LocalAiModelDiscoverySnapshotV1> RefreshAsync(CancellationToken token)
    {
        IOwnedCopilotClientV1? client = null;
        try
        {
            client = clientFactory();
            if (client is null) return Publish("unavailable", []);
            await client.StartAsync(token).ConfigureAwait(false);
            var status = await client.GetStatusAsync(token).ConfigureAwait(false);
            if (status is null || !CopilotRuntimeIdentityCertifierV1.TryCertify(status, out _))
                return Publish("unavailable", []);
            if (!status.IsAuthenticated) return Publish("unauthenticated", []);
            var listed = await client.ListModelsAsync(token).ConfigureAwait(false);
            if (listed is null) return Publish("unavailable", []);
            var models = new List<LocalAiDiscoveredModelV1>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in listed)
            {
                if (!LocalAiModelIdentityV1.IsSupportedId(entry.Id) || !seen.Add(entry.Id)) continue;
                models.Add(new(entry.Id, LocalAiModelIdentityV1.SanitizeDisplayName(entry.DisplayName, entry.Id)));
            }
            return Publish(models.Count == 0 ? "empty" : "ready", models);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Publish("failed", []);
        }
        finally
        {
            if (client is not null)
            {
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private LocalAiModelDiscoverySnapshotV1 Publish(string state, IReadOnlyList<LocalAiDiscoveredModelV1> models)
    {
        lock (gate)
        {
            var legacy = snapshot.LegacyConfiguredModel;
            var eligible = state == "ready"
                && legacy is not null
                && models.Any(item => string.Equals(item.Id, legacy, StringComparison.Ordinal));
            snapshot = new(state, models, legacy, eligible, snapshot.Generation + 1);
            return snapshot;
        }
    }
}
