using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Documents;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;

internal sealed record ClaudeObservedOwnedValue(
    string Key,
    string Value,
    bool IsUserOwned,
    SetupEffectiveSource? EffectiveSource);

internal sealed record ClaudeHigherPrecedenceResult(
    IReadOnlyList<ClaudeObservedOwnedValue> OwnedValues,
    string? FailureCode);

internal sealed class ClaudeHigherPrecedenceObserver
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ContentGates = new(StringComparer.Ordinal)
    {
        "OTEL_LOG_USER_PROMPTS",
        "OTEL_LOG_TOOL_DETAILS",
        "OTEL_LOG_TOOL_CONTENT",
    };
    private readonly ISetupPlatform platform;
    private readonly string invocationDirectory;
    private readonly string managedFilePath;

    public ClaudeHigherPrecedenceObserver(
        ISetupPlatform platform,
        string invocationDirectory,
        string managedFilePath)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.invocationDirectory = invocationDirectory ?? throw new ArgumentNullException(nameof(invocationDirectory));
        this.managedFilePath = managedFilePath ?? throw new ArgumentNullException(nameof(managedFilePath));
    }

    public ClaudeHigherPrecedenceResult Observe(
        IReadOnlyList<KeyValuePair<string, string>> desired,
        bool includeContentCapture)
    {
        ArgumentNullException.ThrowIfNull(desired);
        try
        {
            var selectedInvocationDirectory = RequireAbsolute(invocationDirectory, platform.PathStyle);
            var selectedManagedFilePath = RequireAbsolute(managedFilePath, platform.PathStyle);
            var resolved = new Dictionary<string, ClaudeObservedOwnedValue>(StringComparer.Ordinal);
            var unresolved = desired
                .Where(pair => includeContentCapture || !ContentGates.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var pair in desired)
            {
                if (!unresolved.ContainsKey(pair.Key))
                {
                    continue;
                }

                var processValue = platform.ProcessEnvironment.Get(pair.Key);
                if (processValue is null)
                {
                    continue;
                }

                if (processValue != pair.Value)
                {
                    return Failure(ConflictCode(pair.Key, ObservationTier.Process));
                }

                resolved.Add(pair.Key, new ClaudeObservedOwnedValue(
                    pair.Key,
                    pair.Value,
                    false,
                    SetupEffectiveSource.Environment));
                unresolved.Remove(pair.Key);
            }

            if (unresolved.Count > 0)
            {
                var failure = ResolveFromSource(ReadSelectedManaged(selectedManagedFilePath), unresolved, resolved);
                if (failure is not null)
                {
                    return Failure(failure);
                }
            }

            if (unresolved.Count > 0)
            {
                var failure = ResolveFromSource(
                    ReadFile(Path.Combine(selectedInvocationDirectory, ".claude", "settings.local.json"), ObservationTier.Local),
                    unresolved,
                    resolved);
                if (failure is not null)
                {
                    return Failure(failure);
                }
            }

            if (unresolved.Count > 0)
            {
                var failure = ResolveFromSource(
                    ReadFile(Path.Combine(selectedInvocationDirectory, ".claude", "settings.json"), ObservationTier.Project),
                    unresolved,
                    resolved);
                if (failure is not null)
                {
                    return Failure(failure);
                }
            }

            foreach (var pair in desired)
            {
                if (!includeContentCapture && ContentGates.Contains(pair.Key))
                {
                    continue;
                }

                if (!resolved.ContainsKey(pair.Key))
                {
                    resolved.Add(pair.Key, new ClaudeObservedOwnedValue(pair.Key, pair.Value, true, null));
                }
            }

            return new ClaudeHigherPrecedenceResult(
                desired.Where(pair => resolved.ContainsKey(pair.Key)).Select(pair => resolved[pair.Key]).ToArray(),
                null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or FormatException or DecoderFallbackException or ArgumentException)
        {
            return Failure(SetupCodes.MalformedSettings);
        }
    }

    private SourceObservation ReadSelectedManaged(string selectedManagedFilePath)
    {
        if (platform.OperatingSystem.Current == SetupPlanningOs.Windows)
        {
            var machine = ReadManaged(SetupManagedLocation.ClaudeCodeWindowsMachinePolicy);
            if (machine.Present || machine.FailureCode is not null)
            {
                return machine;
            }

            var file = ReadFile(selectedManagedFilePath, ObservationTier.Managed);
            if (file.Present || file.FailureCode is not null)
            {
                return file;
            }

            return ReadManaged(SetupManagedLocation.ClaudeCodeWindowsUserPolicy);
        }

        return platform.OperatingSystem.Current == SetupPlanningOs.Linux
            ? ReadFile(selectedManagedFilePath, ObservationTier.Managed)
            : SourceObservation.Absent(ObservationTier.Managed);
    }

    private SourceObservation ReadManaged(SetupManagedLocation location)
    {
        var observation = platform.ManagedSettings.Read(location);
        return observation.Outcome switch
        {
            SetupManagedOutcome.Absent => SourceObservation.Absent(ObservationTier.Managed),
            SetupManagedOutcome.Failed => SourceObservation.Failure(ObservationTier.Managed),
            SetupManagedOutcome.Present when observation.IsComplete => Parse(observation.Bytes, ObservationTier.Managed),
            _ => SourceObservation.Failure(ObservationTier.Managed),
        };
    }

    private SourceObservation ReadFile(string path, ObservationTier tier = ObservationTier.Project)
    {
        if (!platform.FileSystem.FileExists(path))
        {
            return SourceObservation.Absent(tier);
        }

        var read = platform.FileSystem.ReadAtMostBytes(path, MaximumBytes);
        return read.IsComplete ? Parse(read.Bytes, tier) : SourceObservation.Failure(tier);
    }

    private static SourceObservation Parse(byte[] bytes, ObservationTier tier)
    {
        var content = StrictUtf8.GetString(bytes);
        _ = ClaudeSettingsDocument.Parse(content);
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("env", out var env))
        {
            if (env.ValueKind != JsonValueKind.Object)
            {
                return SourceObservation.Failure(tier);
            }

            foreach (var property in env.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return SourceObservation.Failure(tier);
                }

                values.Add(property.Name, property.Value.GetString()!);
            }
        }

        return new SourceObservation(true, values, null, tier);
    }

    private static string? ResolveFromSource(
        SourceObservation source,
        Dictionary<string, string> unresolved,
        Dictionary<string, ClaudeObservedOwnedValue> resolved)
    {
        if (source.FailureCode is not null)
        {
            return source.FailureCode;
        }

        foreach (var pair in unresolved.ToArray())
        {
            if (!source.Values.TryGetValue(pair.Key, out var observed))
            {
                continue;
            }

            if (observed != pair.Value)
            {
                return ConflictCode(pair.Key, source.Tier);
            }

            resolved.Add(pair.Key, new ClaudeObservedOwnedValue(
                pair.Key,
                pair.Value,
                false,
                source.Tier == ObservationTier.Managed
                    ? SetupEffectiveSource.ManagedPolicy
                    : SetupEffectiveSource.UserSetting));
            unresolved.Remove(pair.Key);
        }

        return null;
    }

    private static string ConflictCode(string key, ObservationTier tier)
    {
        if (ContentGates.Contains(key))
        {
            return SetupCodes.ContentPolicyConflict;
        }

        return tier == ObservationTier.Managed
            ? SetupCodes.ManagedPolicyConflict
            : SetupCodes.EnvironmentOverrideConflict;
    }

    private static string RequireAbsolute(string path, SetupPathStyle pathStyle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var absolute = pathStyle == SetupPathStyle.Unix
            ? path.StartsWith("/", StringComparison.Ordinal)
            : Path.IsPathFullyQualified(path);
        if (!absolute)
        {
            throw new ArgumentException("Claude observation path must be absolute.", nameof(path));
        }

        return pathStyle == SetupPathStyle.Unix ? path : Path.GetFullPath(path);
    }

    private static ClaudeHigherPrecedenceResult Failure(string code) => new([], code);

    private enum ObservationTier
    {
        Process,
        Managed,
        Local,
        Project,
    }

    private sealed record SourceObservation(
        bool Present,
        IReadOnlyDictionary<string, string> Values,
        string? FailureCode,
        ObservationTier Tier)
    {
        public static SourceObservation Absent(ObservationTier tier) => new(false, new Dictionary<string, string>(), null, tier);

        public static SourceObservation Failure(ObservationTier tier) => new(false, new Dictionary<string, string>(), SetupCodes.MalformedSettings, tier);
    }

}
