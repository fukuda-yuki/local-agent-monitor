namespace CopilotAgentObservability.ConfigCli;

internal sealed record StaticDashboardOptions(
    string DatasetPath,
    string OutputDirectory,
    string SnapshotDate,
    string Title)
{
    public static StaticDashboardOptionsParseResult Parse(string[] args, DateTimeOffset nowUtc)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return new StaticDashboardOptionsParseResult(null, "generate-static-dashboard requires a dashboard dataset JSON file path.");
        }

        string? outputDirectory = null;
        var snapshotDate = nowUtc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var title = "Agent Workflow Observability";

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--out-dir":
                    if (++index >= args.Length)
                    {
                        return new StaticDashboardOptionsParseResult(null, "--out-dir requires an output directory path.");
                    }

                    outputDirectory = args[index];
                    break;

                case "--snapshot-date":
                    if (++index >= args.Length)
                    {
                        return new StaticDashboardOptionsParseResult(null, "--snapshot-date requires a YYYY-MM-DD value.");
                    }

                    if (!DateOnly.TryParseExact(args[index], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    {
                        return new StaticDashboardOptionsParseResult(null, "--snapshot-date must be in YYYY-MM-DD format.");
                    }

                    snapshotDate = args[index];
                    break;

                case "--title":
                    if (++index >= args.Length)
                    {
                        return new StaticDashboardOptionsParseResult(null, "--title requires a non-empty title.");
                    }

                    if (string.IsNullOrWhiteSpace(args[index]))
                    {
                        return new StaticDashboardOptionsParseResult(null, "--title requires a non-empty title.");
                    }

                    title = args[index];
                    break;

                default:
                    return new StaticDashboardOptionsParseResult(null, $"unknown generate-static-dashboard option '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return new StaticDashboardOptionsParseResult(null, "generate-static-dashboard requires --out-dir.");
        }

        return new StaticDashboardOptionsParseResult(
            new StaticDashboardOptions(args[1], outputDirectory, snapshotDate, title),
            null);
    }
}

internal sealed record StaticDashboardOptionsParseResult(
    StaticDashboardOptions? Options,
    string? Error);
