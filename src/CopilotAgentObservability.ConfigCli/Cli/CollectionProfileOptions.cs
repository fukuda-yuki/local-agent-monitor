namespace CopilotAgentObservability.ConfigCli;

internal sealed class CollectionProfileOptions
{
    public const string EnvironmentVariableName = "CAO_COLLECTION_PROFILE";
    public const string RawOnly = "raw-only";
    public const string DockerDesktopLangfuse = "docker-desktop-langfuse";
    public const string DockerDesktopCollectorLangfuse = "docker-desktop-collector-langfuse";
    public const string Wsl2DockerLangfuse = "wsl2-docker-langfuse";
    public const string Wsl2DockerCollectorLangfuse = "wsl2-docker-collector-langfuse";
    public const string RemoteManagedLangfuse = "remote-managed-langfuse";
    public const string RemoteManagedCollector = "remote-managed-collector";
    public const string RawLocalReceiver = "raw-local-receiver";

    public static readonly IReadOnlyList<string> SupportedValues =
    [
        RawOnly,
        DockerDesktopLangfuse,
        DockerDesktopCollectorLangfuse,
        Wsl2DockerLangfuse,
        Wsl2DockerCollectorLangfuse,
        RemoteManagedLangfuse,
        RemoteManagedCollector,
        RawLocalReceiver,
    ];

    private CollectionProfileOptions(string profile)
    {
        Profile = profile;
    }

    public string Profile { get; }

    public static ParseResult Parse(string[] args)
    {
        string? profile = null;

        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] != "--profile")
            {
                return ParseResult.Failure($"unknown collection profile option '{args[index]}'.");
            }

            if (profile is not null)
            {
                return ParseResult.Failure("profile command accepts --profile only once.");
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return ParseResult.Failure("--profile requires a collection profile value.");
            }

            profile = args[index + 1];
            index++;
        }

        profile ??= Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(profile))
        {
            return ParseResult.Failure($"collection profile is required. Pass --profile or set {EnvironmentVariableName}.");
        }

        if (!SupportedValues.Contains(profile, StringComparer.Ordinal))
        {
            return ParseResult.Failure($"unsupported collection profile '{profile}'.");
        }

        return ParseResult.Success(new CollectionProfileOptions(profile));
    }

    public sealed class ParseResult
    {
        private ParseResult(CollectionProfileOptions? options, string? error)
        {
            Options = options;
            Error = error;
        }

        public CollectionProfileOptions? Options { get; }

        public string? Error { get; }

        public static ParseResult Success(CollectionProfileOptions options)
        {
            return new ParseResult(options, null);
        }

        public static ParseResult Failure(string error)
        {
            return new ParseResult(null, error);
        }
    }
}
