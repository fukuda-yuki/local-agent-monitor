using System.Diagnostics.CodeAnalysis;

namespace CopilotAgentObservability.ConfigCli;

internal sealed record ProfileVsCodeEnvOptions(
    string Profile,
    string? RawLocalReceiverEndpoint)
{
    public static ProfileVsCodeEnvOptionsParseResult Parse(string[] args)
    {
        string? profile = null;
        string target = "receiver";
        string? endpoint = null;
        var targetSet = false;
        var endpointSet = false;

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--profile":
                    if (profile is not null)
                    {
                        return Failure("profile command accepts --profile only once.");
                    }

                    if (!TryReadValue(args, index, out var profileValue))
                    {
                        return Failure("--profile requires a collection profile value.");
                    }

                    profile = profileValue;
                    index++;
                    break;

                case "--target":
                    if (targetSet)
                    {
                        return Failure("profile-vscode-env accepts --target only once.");
                    }

                    if (!TryReadValue(args, index, out var targetValue))
                    {
                        return Failure("--target requires receiver or monitor.");
                    }

                    if (targetValue is not "receiver" and not "monitor")
                    {
                        return Failure("--target must be receiver or monitor.");
                    }

                    target = targetValue;
                    targetSet = true;
                    index++;
                    break;

                case "--endpoint":
                    if (endpointSet)
                    {
                        return Failure("profile-vscode-env accepts --endpoint only once.");
                    }

                    if (!TryReadValue(args, index, out var endpointValue))
                    {
                        return Failure("--endpoint requires a loopback http URL.");
                    }

                    var endpointValidationError = ValidateLoopbackHttpUrl(endpointValue);
                    if (endpointValidationError is not null)
                    {
                        return Failure(endpointValidationError);
                    }

                    endpoint = endpointValue;
                    endpointSet = true;
                    index++;
                    break;

                default:
                    return Failure($"unknown collection profile option '{args[index]}'.");
            }
        }

        profile ??= Environment.GetEnvironmentVariable(CollectionProfileOptions.EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(profile))
        {
            return Failure($"collection profile is required. Pass --profile or set {CollectionProfileOptions.EnvironmentVariableName}.");
        }

        if (!CollectionProfileOptions.SupportedValues.Contains(profile, StringComparer.Ordinal))
        {
            return Failure($"unsupported collection profile '{profile}'.");
        }

        if ((targetSet || endpointSet) && !string.Equals(profile, CollectionProfileOptions.RawLocalReceiver, StringComparison.Ordinal))
        {
            return Failure("--target and --endpoint apply only to raw-local-receiver.");
        }

        var resolvedEndpoint = profile == CollectionProfileOptions.RawLocalReceiver
            ? endpoint ?? (target == "monitor" ? ConfigSamples.LocalMonitorOtlpHttpEndpoint : ConfigSamples.RawLocalReceiverOtlpHttpEndpoint)
            : null;
        return new ProfileVsCodeEnvOptionsParseResult(new ProfileVsCodeEnvOptions(profile, resolvedEndpoint), null);
    }

    private static string? ValidateLoopbackHttpUrl(string candidateUrl)
    {
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            return "profile-vscode-env --endpoint requires an http URL.";
        }

        if (!IsAllowedLoopbackHost(uri.Host))
        {
            return "profile-vscode-env --endpoint only allows localhost, 127.0.0.1, or ::1.";
        }

        return null;
    }

    private static bool IsAllowedLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.Ordinal)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal)
            || string.Equals(host, "[::1]", StringComparison.Ordinal);
    }

    private static bool TryReadValue(string[] args, int index, [NotNullWhen(true)] out string? value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = args[index + 1];
        return true;
    }

    private static ProfileVsCodeEnvOptionsParseResult Failure(string error)
    {
        return new ProfileVsCodeEnvOptionsParseResult(null, error);
    }
}

internal sealed record ProfileVsCodeEnvOptionsParseResult(
    ProfileVsCodeEnvOptions? Options,
    string? Error);
