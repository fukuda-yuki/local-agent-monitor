namespace CopilotAgentObservability.LocalMonitor;

internal sealed record MonitorOptions(
    string DatabasePath,
    string Url,
    bool EnableRawView,
    int MaxRequestBodyBytes)
{
    public const string MaxRequestBodyBytesEnvironmentVariable = "CAO_MONITOR_MAX_REQUEST_BODY_BYTES";
    public const int DefaultMaxRequestBodyBytes = 31_457_280;

    public static MonitorOptionsParseResult Parse(
        string[] args,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        string databasePath = RawStoreDefaults.DefaultDatabasePath;
        string url = "http://127.0.0.1:4320";
        var databasePathSet = false;
        var urlSet = false;
        var portSet = false;
        var enableRawView = false;
        int? maxRequestBodyBytes = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--db":
                    if (databasePathSet)
                    {
                        return Failure("local-monitor accepts --db only once.");
                    }

                    if (!TryReadValue(args, index, out var dbValue))
                    {
                        return Failure("--db requires a value.");
                    }

                    databasePath = dbValue;
                    databasePathSet = true;
                    index++;
                    break;

                case "--url":
                    if (urlSet)
                    {
                        return Failure("local-monitor accepts --url only once.");
                    }

                    if (portSet)
                    {
                        return Failure("local-monitor accepts either --url or --port, not both.");
                    }

                    if (!TryReadValue(args, index, out var urlValue))
                    {
                        return Failure("--url requires a value.");
                    }

                    var urlValidationError = ValidateLoopbackHttpUrl(urlValue, "local-monitor");
                    if (urlValidationError is not null)
                    {
                        return Failure(urlValidationError);
                    }

                    url = urlValue;
                    urlSet = true;
                    index++;
                    break;

                case "--port":
                    if (portSet)
                    {
                        return Failure("local-monitor accepts --port only once.");
                    }

                    if (urlSet)
                    {
                        return Failure("local-monitor accepts either --url or --port, not both.");
                    }

                    if (!TryReadValue(args, index, out var portValue))
                    {
                        return Failure("--port requires a value.");
                    }

                    if (!int.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                        || port < IPEndPoint.MinPort
                        || port > IPEndPoint.MaxPort)
                    {
                        return Failure("--port requires a TCP port from 0 to 65535.");
                    }

                    url = $"http://127.0.0.1:{port}";
                    portSet = true;
                    index++;
                    break;

                case "--enable-raw-view":
                    enableRawView = true;
                    break;

                case "--max-request-body-bytes":
                    if (maxRequestBodyBytes is not null)
                    {
                        return Failure("local-monitor accepts --max-request-body-bytes only once.");
                    }

                    if (!TryReadValue(args, index, out var maxValue))
                    {
                        return Failure("--max-request-body-bytes requires a value.");
                    }

                    if (!TryParsePositiveInt(maxValue, out var parsedMax))
                    {
                        return Failure("--max-request-body-bytes requires a positive integer.");
                    }

                    maxRequestBodyBytes = parsedMax;
                    index++;
                    break;

                default:
                    return Failure($"unknown local-monitor option '{args[index]}'.");
            }
        }

        if (maxRequestBodyBytes is null)
        {
            var envValue = getEnvironmentVariable(MaxRequestBodyBytesEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                if (!TryParsePositiveInt(envValue, out var parsedMax))
                {
                    return Failure($"{MaxRequestBodyBytesEnvironmentVariable} requires a positive integer.");
                }

                maxRequestBodyBytes = parsedMax;
            }
        }

        return new MonitorOptionsParseResult(
            new MonitorOptions(databasePath, url, enableRawView, maxRequestBodyBytes ?? DefaultMaxRequestBodyBytes),
            null);
    }

    internal static string? ValidateLoopbackHttpUrl(string candidateUrl, string context)
    {
        if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            return $"{context} requires an http URL.";
        }

        if (!IsAllowedLoopbackHost(uri.Host))
        {
            return $"{context} only allows localhost, 127.0.0.1, or ::1.";
        }

        return null;
    }

    internal static bool IsAllowedLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.Ordinal)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal)
            || string.Equals(host, "[::1]", StringComparison.Ordinal);
    }

    private static bool TryParsePositiveInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result > 0;
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

    private static MonitorOptionsParseResult Failure(string error)
    {
        return new MonitorOptionsParseResult(null, error);
    }
}

internal sealed record MonitorOptionsParseResult(
    MonitorOptions? Options,
    string? Error);
