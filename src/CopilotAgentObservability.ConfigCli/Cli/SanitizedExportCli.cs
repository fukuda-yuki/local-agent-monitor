using System.Text.Json;
using CopilotAgentObservability.SanitizedExport;

namespace CopilotAgentObservability.ConfigCli;

internal static class SanitizedExportCli
{
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 1) return Error(error, "invalid_arguments");
        var service = new SanitizedExportService();
        try
        {
            return args[0] switch
            {
                "preview" => Preview(args, service, output, error),
                "export" => Export(args, service, output, error),
                "result" => Result(args, service, output, error),
                _ => Error(error, "invalid_arguments"),
            };
        }
        catch (JsonException) { return Error(error, "request_invalid"); }
        catch (IOException) { return Error(error, "io_failed"); }
        catch (UnauthorizedAccessException) { return Error(error, "io_failed"); }
    }

    private static int Preview(string[] args, SanitizedExportService service, TextWriter output, TextWriter error)
    {
        if (!TrySingleOption(args, "--request", out var requestPath)) return Error(error, "invalid_arguments");
        var result = service.Preview(ReadRequest(requestPath));
        output.WriteLine(System.Text.Encoding.UTF8.GetString(SanitizedExportJson.Serialize(result)));
        return result.Success ? 0 : Error(error, result.ErrorCode!);
    }

    private static int Export(string[] args, SanitizedExportService service, TextWriter output, TextWriter error)
    {
        if (!TryOptionPair(args, "--request", "--output", out var requestPath, out var outputPath)) return Error(error, "invalid_arguments");
        var result = service.CreateAndPublish(ReadRequest(requestPath), outputPath);
        output.WriteLine(System.Text.Encoding.UTF8.GetString(SanitizedExportJson.Serialize(SanitizedExportResultView.From(result))));
        return result.Success ? 0 : Error(error, result.ErrorCode!);
    }

    private static int Result(string[] args, SanitizedExportService service, TextWriter output, TextWriter error)
    {
        if (!TrySingleOption(args, "--bundle", out var bundlePath)) return Error(error, "invalid_arguments");
        var result = service.Inspect(File.ReadAllBytes(bundlePath));
        output.WriteLine(System.Text.Encoding.UTF8.GetString(SanitizedExportJson.Serialize(result)));
        return result.Success ? 0 : Error(error, result.ErrorCode!);
    }

    private static SanitizedExportRequest ReadRequest(string path) => SanitizedExportJson.DeserializeRequest(File.ReadAllBytes(path));

    private static bool TrySingleOption(string[] args, string name, out string value)
    {
        value = string.Empty;
        return args.Length == 3 && args[1] == name && !string.IsNullOrWhiteSpace(value = args[2]);
    }

    private static bool TryOptionPair(string[] args, string firstName, string secondName, out string first, out string second)
    {
        first = second = string.Empty;
        if (args.Length != 5) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !values.TryAdd(args[index], args[index + 1])) return false;
        }
        return values.TryGetValue(firstName, out first!) && values.TryGetValue(secondName, out second!)
            && !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second);
    }

    private static int Error(TextWriter error, string code)
    {
        error.WriteLine(code);
        return 2;
    }
}
