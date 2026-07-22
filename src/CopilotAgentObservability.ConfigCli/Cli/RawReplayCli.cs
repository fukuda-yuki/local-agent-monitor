using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.RawReplay;

namespace CopilotAgentObservability.ConfigCli;

internal static class RawReplayCli
{
    internal static int Run(string[] args, TextWriter output, TextWriter error, IRawReplaySnapshotProvider? snapshotProvider = null)
    {
        if (args.Length < 1) return Error(error, "invalid_arguments");
        try
        {
            return args[0] switch
            {
                "preview" => Preview(args, snapshotProvider, output, error),
                "export" => Export(args, snapshotProvider, output, error),
                "result" => Result(args, output, error),
                _ => Error(error, "invalid_arguments"),
            };
        }
        catch (JsonException) { return Error(error, "request_invalid"); }
        catch (RawReplaySizeException exception) { return Error(error, exception.Code); }
        catch (Persistence.Sqlite.Retention.RetentionCatalogUnavailableException) { return Error(error, "snapshot_store_unavailable"); }
        catch (Persistence.Sqlite.Retention.RetentionMigrationBlockedException) { return Error(error, "snapshot_store_unavailable"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { return Error(error, "snapshot_store_unavailable"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException) { return Error(error, "io_failed"); }
    }

    private static int Preview(string[] args, IRawReplaySnapshotProvider? injected, TextWriter output, TextWriter error)
    {
        if (!TryOptions(args, ["--database", "--request"], out var values)) return Error(error, "invalid_arguments");
        var control = ReadRequest(values["--request"]);
        var provider = injected ?? CreateProvider(values["--database"]);
        var result = new RawReplayAuthorizedService(provider).PreviewAsync(control).AsTask().GetAwaiter().GetResult();
        output.WriteLine(Encoding.UTF8.GetString(RawReplayJson.Serialize(result)));
        return result.Success ? 0 : Error(error, result.ErrorCode!);
    }

    private static int Export(string[] args, IRawReplaySnapshotProvider? injected, TextWriter output, TextWriter error)
    {
        if (!TryOptions(args, ["--database", "--request", "--output"], out var values)) return Error(error, "invalid_arguments");
        var control = ReadRequest(values["--request"]);
        var provider = injected ?? CreateProvider(values["--database"]);
        var result = new RawReplayAuthorizedService(provider)
            .CreateAndPublishAsync(control, values["--output"]).AsTask().GetAwaiter().GetResult();
        output.WriteLine(Encoding.UTF8.GetString(RawReplayJson.Serialize(new RawReplayResultView(
            result.Success, result.ErrorCode, result.Preview, result.ArchiveSha256))));
        return result.Success ? 0 : Error(error, result.ErrorCode!);
    }

    private static int Result(string[] args, TextWriter output, TextWriter error)
    {
        if (!TrySingleOption(args, "--bundle", out var path)) return Error(error, "invalid_arguments");
        var inspection = new RawReplayArchiveService().Inspect(ReadBounded(path, RawReplayLimits.MaximumArchiveBytes, "archive_too_large"));
        output.WriteLine(Encoding.UTF8.GetString(RawReplayJson.Serialize(inspection)));
        return inspection.Success ? 0 : Error(error, inspection.ErrorCode!);
    }

    private static IRawReplaySnapshotProvider CreateProvider(string databasePath)
    {
        var context = Persistence.Sqlite.Retention.RetentionCatalogContext.AdoptExistingCatalogV1(databasePath);
        return new SqliteRawReplaySnapshotProvider(databasePath, context);
    }

    private static RawReplayExportControl ReadRequest(string path) =>
        RawReplayJson.DeserializeExact<RawReplayExportControl>(ReadBounded(path, RawReplayLimits.MaximumControlBytes, "request_too_large"));

    private static byte[] ReadBounded(string path, long maximum, string code)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 0 || stream.Length > maximum || stream.Length > int.MaxValue) throw new RawReplaySizeException(code);
        var bytes = new byte[(int)stream.Length]; var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new IOException();
            offset += read;
        }
        return stream.ReadByte() == -1 ? bytes : throw new RawReplaySizeException(code);
    }

    private static bool TrySingleOption(string[] args, string name, out string value)
    {
        value = string.Empty;
        return args.Length == 3 && args[1] == name && !string.IsNullOrWhiteSpace(value = args[2]);
    }

    private static bool TryOptions(string[] args, IReadOnlyList<string> required, out Dictionary<string, string> values)
    {
        values = new(StringComparer.Ordinal);
        if (args.Length != 1 + required.Count * 2) return false;
        for (var index = 1; index < args.Length; index += 2)
            if (index + 1 >= args.Length || !required.Contains(args[index], StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(args[index + 1]) || !values.TryAdd(args[index], args[index + 1])) return false;
        return required.All(values.ContainsKey);
    }

    private static int Error(TextWriter error, string code) { error.WriteLine(code); return 2; }
    private sealed record RawReplayResultView(bool Success, string? ErrorCode, RawReplayPreview Preview, string? ArchiveSha256);
}

internal sealed class RawReplaySizeException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
