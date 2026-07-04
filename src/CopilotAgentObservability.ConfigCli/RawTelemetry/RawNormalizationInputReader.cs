namespace CopilotAgentObservability.ConfigCli;

internal static class RawNormalizationInputReader
{
    public static IReadOnlyList<MeasurementRow> Read(string inputPath)
    {
        if (IsRawStorePath(inputPath))
        {
            var records = new RawTelemetryStore(inputPath).ListRecords();
            return RawMeasurementNormalizer.Normalize(records);
        }

        var payloadJson = File.ReadAllText(inputPath, Encoding.UTF8);
        return RawMeasurementNormalizer.Normalize(payloadJson);
    }

    private static bool IsRawStorePath(string inputPath)
    {
        var extension = Path.GetExtension(inputPath);
        if (extension is ".db" or ".sqlite" or ".sqlite3")
        {
            return true;
        }

        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(inputPath);
        var bytesRead = stream.Read(header);
        return bytesRead >= 16 && Encoding.ASCII.GetString(header) == "SQLite format 3\0";
    }
}
