using System.Globalization;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;
using CopilotAgentObservability.SanitizedExport;
using CopilotAgentObservability.SanitizedImport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli;

internal static class SanitizedImportCli
{
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 1) return WriteError(error, "invalid_arguments");
        try
        {
            return args[0] switch
            {
                "preview" => Preview(args, output, error),
                "import" => Import(args, output, error),
                "history" => History(args, output, error),
                _ => WriteError(error, "invalid_arguments"),
            };
        }
        catch (SanitizedImportSizeException exception) { return WriteError(error, exception.Code); }
        catch (IOException) { return WriteError(error, "io_failed"); }
        catch (UnauthorizedAccessException) { return WriteError(error, "io_failed"); }
        catch (ArgumentException) { return WriteError(error, "io_failed"); }
        catch (NotSupportedException) { return WriteError(error, "io_failed"); }
        catch (System.Security.SecurityException) { return WriteError(error, "io_failed"); }
        catch (SqliteException) { return WriteError(error, "import_store_unavailable"); }
        catch (InvalidOperationException) { return WriteError(error, "import_store_unavailable"); }
        catch (OverflowException) { return WriteError(error, "import_store_unavailable"); }
    }

    private static int Preview(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryOptions(args, ["--database", "--bundle"], [], out var values))
            return WriteError(error, "invalid_arguments");
        var archiveBytes = ReadBundle(values["--bundle"]);
        var validation = SanitizedImportBundleReader.Read(archiveBytes);
        if (!validation.Success)
        {
            WriteJson(output, SqliteSanitizedImportStore.Failure(validation.ErrorCode!));
            return WriteError(error, validation.ErrorCode!);
        }
        var store = Open(values["--database"]);
        var result = store.Preview(archiveBytes);
        WriteJson(output, result);
        return result.Success ? 0 : WriteError(error, result.ErrorCode!);
    }

    private static int Import(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryOptions(args, ["--database", "--bundle", "--preview-digest"], [], out var values))
            return WriteError(error, "invalid_arguments");
        if (!SqliteSanitizedImportStore.IsHash(values["--preview-digest"]))
        {
            WriteJson(output, SqliteSanitizedImportStore.ResultFailure("preview_digest_invalid"));
            return WriteError(error, "preview_digest_invalid");
        }
        var archiveBytes = ReadBundle(values["--bundle"]);
        var validation = SanitizedImportBundleReader.Read(archiveBytes);
        if (!validation.Success)
        {
            WriteJson(output, SqliteSanitizedImportStore.ResultFailure(validation.ErrorCode!));
            return WriteError(error, validation.ErrorCode!);
        }
        var store = Open(values["--database"]);
        var result = store.Commit(archiveBytes, values["--preview-digest"]);
        WriteJson(output, result);
        return result.Success ? 0 : WriteError(error, result.ErrorCode!);
    }

    private static int History(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryOptions(args, ["--database"], ["--limit"], out var values))
            return WriteError(error, "invalid_arguments");
        var limit = SanitizedImportLimits.DefaultHistoryItems;
        if (values.TryGetValue("--limit", out var text)
            && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > SanitizedImportLimits.MaximumHistoryItems))
            return WriteError(error, "invalid_arguments");
        var store = Open(values["--database"]);
        WriteJson(output, store.ListHistory(limit));
        return 0;
    }

    private static SqliteSanitizedImportStore Open(string databasePath)
    {
        var store = new SqliteSanitizedImportStore(databasePath);
        store.CreateSchema();
        return store;
    }

    private static byte[] ReadBundle(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > SanitizedExportLimits.MaximumUncompressedBytes)
            throw new SanitizedImportSizeException("bundle_too_large");
        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[8192];
        var remaining = SanitizedExportLimits.MaximumUncompressedBytes + 1L;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        if (stream.ReadByte() != -1 || output.Length > SanitizedExportLimits.MaximumUncompressedBytes)
            throw new SanitizedImportSizeException("bundle_too_large");
        return output.ToArray();
    }

    private static bool TryOptions(
        string[] args,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional,
        out Dictionary<string, string> values)
    {
        values = new(StringComparer.Ordinal);
        if ((args.Length - 1) % 2 != 0) return false;
        for (var index = 1; index < args.Length; index += 2)
        {
            var name = args[index];
            if (index + 1 >= args.Length
                || !required.Contains(name, StringComparer.Ordinal) && !optional.Contains(name, StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(args[index + 1])
                || !values.TryAdd(name, args[index + 1])) return false;
        }
        return required.All(values.ContainsKey);
    }

    private static void WriteJson<T>(TextWriter output, T value) =>
        output.WriteLine(Encoding.UTF8.GetString(SanitizedImportJson.Serialize(value)));

    private static int WriteError(TextWriter error, string code)
    {
        error.WriteLine(code);
        return code is "io_failed" or "import_store_busy" or "import_store_unavailable" or "import_transaction_failed" ? 3 : 2;
    }
}

internal sealed class SanitizedImportSizeException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
