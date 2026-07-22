using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.SanitizedExport;

namespace CopilotAgentObservability.ConfigCli;

internal static class SanitizedExportCli
{
    internal static int Run(string[] args, TextWriter output, TextWriter error, ISanitizedExportSnapshotProvider? snapshotProvider = null)
    {
        if (args.Length < 1) return Error(error, "invalid_arguments");
        try
        {
            return args[0] switch
            {
                "preview" => Preview(args, snapshotProvider, output, error),
                "export" => Export(args, snapshotProvider, output, error),
                "result" => Result(args, new SanitizedExportBundleInspector(), output, error),
                _ => Error(error, "invalid_arguments"),
            };
        }
        catch (JsonException) { return Error(error, "request_invalid"); }
        catch (SanitizedExportSizeException exception) { return Error(error, exception.Code); }
        catch (IOException) { return Error(error, "io_failed"); }
        catch (UnauthorizedAccessException) { return Error(error, "io_failed"); }
        catch (ArgumentException) { return Error(error, "io_failed"); }
        catch (NotSupportedException) { return Error(error, "io_failed"); }
        catch (System.Security.SecurityException) { return Error(error, "io_failed"); }
    }

    private static int Preview(string[] args, ISanitizedExportSnapshotProvider? injected, TextWriter output, TextWriter error)
    {
        if (!TryOptions(args, ["--database", "--request"], out var values)) return Error(error, "invalid_arguments");
        var service = new SanitizedExportAuthorizedService(injected ?? new SqliteSanitizedExportSnapshotProvider(values["--database"]));
        var requestPath = values["--request"];
        var result = service.Preview(ReadRequest(requestPath));
        output.WriteLine(System.Text.Encoding.UTF8.GetString(SanitizedExportJson.Serialize(result)));
        return result.Success ? 0 : Error(error, result.ErrorCode!);
    }

    private static int Export(string[] args, ISanitizedExportSnapshotProvider? injected, TextWriter output, TextWriter error)
    {
        if (!TryOptions(args, ["--database", "--request", "--output"], out var values)) return Error(error, "invalid_arguments");
        var service = new SanitizedExportAuthorizedService(injected ?? new SqliteSanitizedExportSnapshotProvider(values["--database"]));
        var requestPath = values["--request"];
        var outputPath = values["--output"];
        var result = service.CreateAndPublish(ReadRequest(requestPath), outputPath);
        output.WriteLine(System.Text.Encoding.UTF8.GetString(SanitizedExportJson.Serialize(SanitizedExportResultView.From(result))));
        return result.Success ? 0 : Error(error, result.ErrorCode!);
    }

    private static int Result(string[] args, SanitizedExportBundleInspector service, TextWriter output, TextWriter error)
    {
        if (!TrySingleOption(args, "--bundle", out var bundlePath)) return Error(error, "invalid_arguments");
        var result = service.Inspect(ReadBounded(bundlePath, "bundle_too_large"));
        output.WriteLine(System.Text.Encoding.UTF8.GetString(SanitizedExportJson.Serialize(result)));
        return result.Success ? 0 : Error(error, result.ErrorCode!);
    }

    private static SanitizedExportControlRequest ReadRequest(string path) => SanitizedExportJson.DeserializeControlRequest(ReadBounded(path, "request_too_large", SanitizedExportLimits.MaximumControlRequestBytes));

    private static byte[] ReadBounded(string path, string errorCode, long maximumBytes = SanitizedExportLimits.MaximumUncompressedBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes) throw new SanitizedExportSizeException(errorCode);
        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[8192];
        var remaining = maximumBytes + 1;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        if (stream.ReadByte() != -1 || output.Length > maximumBytes) throw new SanitizedExportSizeException(errorCode);
        return output.ToArray();
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
        {
            if (index + 1 >= args.Length || !required.Contains(args[index], StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(args[index + 1]) || !values.TryAdd(args[index], args[index + 1])) return false;
        }
        return required.All(values.ContainsKey);
    }

    private static int Error(TextWriter error, string code)
    {
        error.WriteLine(code);
        return 2;
    }
}

internal sealed class SanitizedExportSizeException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
