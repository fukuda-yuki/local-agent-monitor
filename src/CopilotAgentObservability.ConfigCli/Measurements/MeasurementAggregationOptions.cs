namespace CopilotAgentObservability.ConfigCli;

internal sealed record MeasurementAggregationOptions(
    string InputPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static MeasurementAggregationOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new MeasurementAggregationOptionsParseResult(null, "aggregate-measurements requires an input JSON file path.");
        }

        var inputPath = args[1];
        string? csvOutputPath = null;
        string? jsonOutputPath = null;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--csv":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new MeasurementAggregationOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new MeasurementAggregationOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new MeasurementAggregationOptionsParseResult(null, $"unknown aggregate-measurements option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new MeasurementAggregationOptionsParseResult(null, "aggregate-measurements requires --csv, --json, or both.");
        }

        return new MeasurementAggregationOptionsParseResult(
            new MeasurementAggregationOptions(inputPath, csvOutputPath, jsonOutputPath),
            null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record MeasurementAggregationOptionsParseResult(
    MeasurementAggregationOptions? Options,
    string? Error);
