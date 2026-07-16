using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Setup.Cli;

internal sealed record SetupOptions(
    SetupCommand Command,
    string? Adapter,
    string? Target,
    string? Endpoint,
    bool IncludeContentCapture,
    Guid? ChangeSetId,
    bool AllowWsl2Routing = false)
{
    private const string DefaultEndpoint = "http://127.0.0.1:4320";
    private static readonly Regex SafeSlug = new(
        "\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static SetupOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[0], "setup", StringComparison.Ordinal))
        {
            return Failure(SetupCodes.InvalidArguments);
        }

        return args[1] switch
        {
            "plan" => ParsePlan(args),
            "apply" => ParseChangeSetCommand(args, SetupCommand.Apply),
            "rollback" => ParseChangeSetCommand(args, SetupCommand.Rollback),
            "status" => ParseStatus(args),
            _ => Failure(SetupCodes.InvalidArguments),
        };
    }

    private static SetupOptionsParseResult ParsePlan(string[] args)
    {
        string? adapter = null;
        string? target = null;
        string? endpoint = null;
        var includeContentCapture = false;
        var allowWsl2Routing = false;
        var adapterSet = false;
        var targetSet = false;
        var endpointSet = false;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--adapter":
                    if (adapterSet || !TryReadValue(args, index, out adapter))
                    {
                        return Failure(SetupCodes.InvalidArguments);
                    }

                    adapterSet = true;
                    index++;
                    break;

                case "--target":
                    if (targetSet || !TryReadValue(args, index, out target))
                    {
                        return Failure(SetupCodes.InvalidArguments);
                    }

                    targetSet = true;
                    index++;
                    break;

                case "--endpoint":
                    if (endpointSet || !TryReadValue(args, index, out var endpointValue) || endpointValue is null ||
                        !TryNormalizeEndpoint(endpointValue, out endpoint))
                    {
                        return Failure(SetupCodes.InvalidArguments);
                    }

                    endpointSet = true;
                    index++;
                    break;

                case "--include-content-capture":
                    if (includeContentCapture)
                    {
                        return Failure(SetupCodes.InvalidArguments);
                    }

                    includeContentCapture = true;
                    break;

                case "--allow-wsl2-routing":
                    if (allowWsl2Routing)
                    {
                        return Failure(SetupCodes.InvalidArguments);
                    }

                    allowWsl2Routing = true;
                    break;

                default:
                    return Failure(SetupCodes.InvalidArguments);
            }
        }

        if (!adapterSet || !targetSet)
        {
            return Failure(SetupCodes.InvalidArguments);
        }

        if (!IsSafeSlug(adapter) || string.IsNullOrEmpty(target))
        {
            return Failure(SetupCodes.InvalidArguments);
        }

        if (allowWsl2Routing && !string.Equals(adapter, "claude-code", StringComparison.Ordinal))
        {
            return Failure(SetupCodes.InvalidArguments);
        }

        return Success(new SetupOptions(
            SetupCommand.Plan,
            adapter,
            target,
            endpoint ?? DefaultEndpoint,
            includeContentCapture,
            null,
            allowWsl2Routing));
    }

    private static SetupOptionsParseResult ParseChangeSetCommand(string[] args, SetupCommand command)
    {
        Guid? changeSetId = null;

        for (var index = 2; index < args.Length; index++)
        {
            if (args[index] != "--change-set" || changeSetId is not null ||
                !TryReadValue(args, index, out var changeSetValue) || changeSetValue is null ||
                !TryParseCanonicalUuidV7(changeSetValue, out var parsedChangeSetId))
            {
                return Failure(SetupCodes.InvalidArguments);
            }

            changeSetId = parsedChangeSetId;
            index++;
        }

        return changeSetId is null
            ? Failure(SetupCodes.InvalidArguments)
            : Success(new SetupOptions(command, null, null, null, false, changeSetId));
    }

    private static SetupOptionsParseResult ParseStatus(string[] args)
    {
        string? adapter = null;
        var adapterSet = false;

        for (var index = 2; index < args.Length; index++)
        {
            if (args[index] != "--adapter" || adapterSet || !TryReadValue(args, index, out adapter))
            {
                return Failure(SetupCodes.InvalidArguments);
            }

            adapterSet = true;
            index++;
        }

        if (adapter is not null && !IsSafeSlug(adapter))
        {
            return Failure(SetupCodes.InvalidArguments);
        }

        return Success(new SetupOptions(SetupCommand.Status, adapter, null, null, false, null));
    }

    private static bool TryReadValue(string[] args, int index, out string? value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = args[index + 1];
        return true;
    }

    private static bool TryNormalizeEndpoint(string value, out string? endpoint)
    {
        endpoint = null;
        if (!Regex.IsMatch(value, "\\Ahttp://(?:localhost|127\\.0\\.0\\.1|\\[::1\\]):[1-9][0-9]{0,4}/?\\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        var host = uri.DnsSafeHost switch
        {
            "localhost" => "localhost",
            "127.0.0.1" => "127.0.0.1",
            "::1" => "[::1]",
            _ => null,
        };
        if (host is null)
        {
            return false;
        }

        endpoint = $"http://{host}:{uri.Port}";
        return true;
    }

    private static bool IsSafeSlug(string? value) =>
        value is { Length: >= 1 and <= 128 } && SafeSlug.IsMatch(value);

    private static bool TryParseCanonicalUuidV7(string value, out Guid changeSetId)
    {
        changeSetId = Guid.Empty;
        return Guid.TryParseExact(value, "D", out changeSetId) &&
            string.Equals(value, changeSetId.ToString("D"), StringComparison.Ordinal) &&
            value[14] == '7' && value[19] is '8' or '9' or 'a' or 'b';
    }

    private static SetupOptionsParseResult Success(SetupOptions options) => new(options, null);

    private static SetupOptionsParseResult Failure(string code) => new(null, code);
}

internal sealed record SetupOptionsParseResult(
    SetupOptions? Options,
    string? Code);
