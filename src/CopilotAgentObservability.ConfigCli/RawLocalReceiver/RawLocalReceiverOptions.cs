namespace CopilotAgentObservability.ConfigCli;

internal sealed record RawLocalReceiverOptions(
    string DatabasePath,
    string Url)
{
    public static RawLocalReceiverOptionsParseResult Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new RawLocalReceiverOptionsParseResult(null, "serve-raw-local-receiver requires a command name.");
        }

        string databasePath = RawStoreDefaults.DefaultDatabasePath;
        string url = "http://127.0.0.1:4319";
        var databasePathSet = false;
        var urlSet = false;

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--db":
                    if (databasePathSet)
                    {
                        return new RawLocalReceiverOptionsParseResult(null, "serve-raw-local-receiver accepts --db only once.");
                    }

                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new RawLocalReceiverOptionsParseResult(null, "--db requires a value.");
                    }

                    databasePath = args[++index];
                    databasePathSet = true;
                    break;

                case "--url":
                    if (urlSet)
                    {
                        return new RawLocalReceiverOptionsParseResult(null, "serve-raw-local-receiver accepts --url only once.");
                    }

                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new RawLocalReceiverOptionsParseResult(null, "--url requires a value.");
                    }

                    var candidateUrl = args[++index];
                    var validationError = ValidateUrl(candidateUrl);
                    if (validationError is not null)
                    {
                        return new RawLocalReceiverOptionsParseResult(null, validationError);
                    }

                    url = candidateUrl;
                    urlSet = true;
                    break;

                default:
                    return new RawLocalReceiverOptionsParseResult(null, $"unknown serve-raw-local-receiver option '{args[index]}'.");
            }
        }

        return new RawLocalReceiverOptionsParseResult(new RawLocalReceiverOptions(databasePath, url), null);
    }

    private static string? ValidateUrl(string candidateUrl)
    {
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var uri))
        {
            return "serve-raw-local-receiver requires an http or https URL.";
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return "serve-raw-local-receiver requires an http or https URL.";
        }

        if (!IsAllowedLoopbackHost(uri.Host))
        {
            return "serve-raw-local-receiver only allows localhost, 127.0.0.1, or ::1.";
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

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record RawLocalReceiverOptionsParseResult(
    RawLocalReceiverOptions? Options,
    string? Error);
