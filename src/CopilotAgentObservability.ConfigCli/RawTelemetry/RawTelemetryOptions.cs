namespace CopilotAgentObservability.ConfigCli;

internal sealed record RawIngestOptions(
    string InputPath,
    string DatabasePath)
{
    public static RawIngestOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new RawIngestOptionsParseResult(null, "ingest-raw requires a raw OTLP JSON file path.");
        }

        var inputPath = args[1];
        if (IsOption(inputPath))
        {
            return new RawIngestOptionsParseResult(null, "ingest-raw requires a raw OTLP JSON file path.");
        }

        string? databasePath = null;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--db":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new RawIngestOptionsParseResult(null, "--db requires a raw store database path.");
                    }

                    databasePath = args[++index];
                    break;

                default:
                    return new RawIngestOptionsParseResult(null, $"unknown ingest-raw option '{args[index]}'.");
            }
        }

        if (databasePath is null)
        {
            return new RawIngestOptionsParseResult(null, "ingest-raw requires --db <raw-store.db>.");
        }

        return new RawIngestOptionsParseResult(new RawIngestOptions(inputPath, databasePath), null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record RawIngestOptionsParseResult(
    RawIngestOptions? Options,
    string? Error);

internal sealed record RawNormalizationOptions(
    string InputPath,
    string? CsvOutputPath,
    string? JsonOutputPath)
{
    public static RawNormalizationOptionsParseResult Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return new RawNormalizationOptionsParseResult(null, "normalize-raw requires a raw store database or raw OTLP JSON file path.");
        }

        var inputPath = args[1];
        if (IsOption(inputPath))
        {
            return new RawNormalizationOptionsParseResult(null, "normalize-raw requires a raw store database or raw OTLP JSON file path.");
        }

        string? csvOutputPath = null;
        string? jsonOutputPath = null;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--csv":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new RawNormalizationOptionsParseResult(null, "--csv requires an output file path.");
                    }

                    csvOutputPath = args[++index];
                    break;

                case "--json":
                    if (index + 1 >= args.Length || IsOption(args[index + 1]))
                    {
                        return new RawNormalizationOptionsParseResult(null, "--json requires an output file path.");
                    }

                    jsonOutputPath = args[++index];
                    break;

                default:
                    return new RawNormalizationOptionsParseResult(null, $"unknown normalize-raw option '{args[index]}'.");
            }
        }

        if (csvOutputPath is null && jsonOutputPath is null)
        {
            return new RawNormalizationOptionsParseResult(null, "normalize-raw requires --csv, --json, or both.");
        }

        return new RawNormalizationOptionsParseResult(new RawNormalizationOptions(inputPath, csvOutputPath, jsonOutputPath), null);
    }

    private static bool IsOption(string value)
    {
        return value.StartsWith("--", StringComparison.Ordinal);
    }
}

internal sealed record RawNormalizationOptionsParseResult(
    RawNormalizationOptions? Options,
    string? Error);
