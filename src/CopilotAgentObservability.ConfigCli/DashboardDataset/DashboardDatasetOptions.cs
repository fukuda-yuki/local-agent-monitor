namespace CopilotAgentObservability.ConfigCli;

internal sealed record DashboardDatasetOptions(
    string MeasurementsPath,
    string? RawInputPath,
    string? DiagnosisCandidatesPath,
    string? ImprovementCandidatesPath,
    string? AutoDecisionsPath,
    string? CsvOutputDirectory,
    string? JsonOutputPath,
    string TimeBucketGranularity)
{
    public static DashboardDatasetOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2 || IsOption(args[1]))
        {
            return new DashboardDatasetOptionsParseResult(null, "generate-dashboard-dataset requires a normalized measurements CSV or JSON file path.");
        }

        var measurementsPath = args[1];
        string? rawInputPath = null;
        string? diagnosisCandidatesPath = null;
        string? improvementCandidatesPath = null;
        string? autoDecisionsPath = null;
        string? csvOutputDirectory = null;
        string? jsonOutputPath = null;
        var timeBucketGranularity = "day";

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--raw":
                    if (!TryReadOptionValue(args, ref index, out rawInputPath))
                    {
                        return new DashboardDatasetOptionsParseResult(null, "--raw requires a raw store database or raw OTLP JSON file path.");
                    }

                    break;

                case "--diagnosis-candidates":
                    if (!TryReadOptionValue(args, ref index, out diagnosisCandidatesPath))
                    {
                        return new DashboardDatasetOptionsParseResult(null, "--diagnosis-candidates requires an input file path.");
                    }

                    break;

                case "--improvement-candidates":
                    if (!TryReadOptionValue(args, ref index, out improvementCandidatesPath))
                    {
                        return new DashboardDatasetOptionsParseResult(null, "--improvement-candidates requires an input file path.");
                    }

                    break;

                case "--auto-decisions":
                    if (!TryReadOptionValue(args, ref index, out autoDecisionsPath))
                    {
                        return new DashboardDatasetOptionsParseResult(null, "--auto-decisions requires an input file path.");
                    }

                    break;

                case "--csv-dir":
                    if (!TryReadOptionValue(args, ref index, out csvOutputDirectory))
                    {
                        return new DashboardDatasetOptionsParseResult(null, "--csv-dir requires an output directory path.");
                    }

                    break;

                case "--json":
                    if (!TryReadOptionValue(args, ref index, out jsonOutputPath))
                    {
                        return new DashboardDatasetOptionsParseResult(null, "--json requires an output file path.");
                    }

                    break;

                case "--time-bucket":
                    if (!TryReadOptionValue(args, ref index, out timeBucketGranularity))
                    {
                        return new DashboardDatasetOptionsParseResult(null, "--time-bucket requires day, hour, or week.");
                    }

                    if (timeBucketGranularity is not ("day" or "hour" or "week"))
                    {
                        return new DashboardDatasetOptionsParseResult(null, "--time-bucket must be day, hour, or week.");
                    }

                    break;

                default:
                    return new DashboardDatasetOptionsParseResult(null, $"unknown generate-dashboard-dataset option '{args[index]}'.");
            }
        }

        if (csvOutputDirectory is null && jsonOutputPath is null)
        {
            return new DashboardDatasetOptionsParseResult(null, "generate-dashboard-dataset requires --csv-dir, --json, or both.");
        }

        return new DashboardDatasetOptionsParseResult(
            new DashboardDatasetOptions(
                measurementsPath,
                rawInputPath,
                diagnosisCandidatesPath,
                improvementCandidatesPath,
                autoDecisionsPath,
                csvOutputDirectory,
                jsonOutputPath,
                timeBucketGranularity),
            null);
    }

    private static bool TryReadOptionValue(string[] args, ref int index, out string? value)
    {
        value = null;
        if (index + 1 >= args.Length || IsOption(args[index + 1]))
        {
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record DashboardDatasetOptionsParseResult(
    DashboardDatasetOptions? Options,
    string? Error);

