using CopilotAgentObservability.ConfigCli.HistoricalImport;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli;

internal static class HistoricalImportCli
{
    internal const int MaximumRequestBytes = 1_048_576;

    internal static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<string, IHistoricalImportApplication>? applicationFactory = null)
    {
        if (!TryParse(args, out var options) || options is null)
        {
            return Fail(error, "historical_import_invalid_arguments", 2);
        }

        try
        {
            var databasePath = HistoricalImportLocalFile.NormalizeSafePath(options.DatabasePath);
            using var databaseLease = HistoricalImportDatabaseLease.Open(databasePath);
            databaseLease.RequireLocalMonitorOwnership();
            IHistoricalImportApplication? application = null;
            try
            {
                application = InvokeApplication(() => applicationFactory is null
                    ? CreateApplication(databaseLease)
                    : applicationFactory(databasePath));
                object? request = options.Command switch
                {
                    "preview" => ReadRequest<HistoricalImportPreviewRequest>(options.RequestPath!),
                    "confirm" => ReadRequest<HistoricalImportConfirmationRequest>(options.RequestPath!),
                    "commit" => ReadRequest<HistoricalImportCommitRequest>(options.RequestPath!),
                    _ => null,
                };
                return options.Command switch
                {
                    "preview" => Succeed(
                        InvokeApplication(() => application.CreatePreview((HistoricalImportPreviewRequest)request!)),
                        output),
                    "confirm" => Succeed(
                        InvokeApplication(() => application.IssueConfirmation((HistoricalImportConfirmationRequest)request!)),
                        output),
                    "commit" => Succeed(
                        InvokeApplication(() => application.Commit((HistoricalImportCommitRequest)request!)),
                        output),
                    "status" => Succeed(InvokeApplication(() => application.ReadStatus(options.OperationId!)), output),
                    "result" => Succeed(InvokeApplication(() => application.ReadResult(options.OperationId!)), output),
                    "history" => Succeed(InvokeApplication(() => application.ListHistory(options.Limit)), output),
                    "observations" => Succeed(
                        InvokeApplication(() => application.ListObservations(options.Limit, options.Cursor)),
                        output),
                    _ => throw new InvalidOperationException(),
                };
            }
            finally
            {
                (application as IDisposable)?.Dispose();
            }
        }
        catch (HistoricalImportException exception)
        {
            return Fail(error, exception.Code, ExitCode(exception.Code));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Fail(error, HistoricalImportErrorCodes.StoreBusy, 5);
        }
        catch (Exception exception) when (exception is
            JsonException or
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            return Fail(error, HistoricalImportErrorCodes.RequestInvalid, 2);
        }
        catch
        {
            return Fail(error, HistoricalImportErrorCodes.StoreUnavailable, 5);
        }
    }

    private static IHistoricalImportApplication CreateApplication(HistoricalImportDatabaseLease databaseLease)
    {
        var store = SqliteHistoricalImportStore.OpenExistingDatabase(databaseLease.OpenVerifiedConnection);
        store.CreateSchema();
        return new HistoricalImportApplicationService(
            store,
            new HistoricalImportSourceGateway(),
            HistoricalAdmissionRegistry.Empty,
            TimeProvider.System);
    }

    private static T ReadRequest<T>(string path)
    {
        var normalizedPath = HistoricalImportLocalFile.NormalizeSafePath(path);
        var bytes = HistoricalImportLocalFile.ReadAtMostBytes(normalizedPath, MaximumRequestBytes);

        if (bytes.Length > MaximumRequestBytes)
        {
            throw new JsonException();
        }

        return HistoricalImportJson.Deserialize<T>(bytes);
    }

    private static T InvokeApplication<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (HistoricalImportException)
        {
            throw;
        }
        catch (SqliteException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }
    }

    private static int Succeed<T>(T result, TextWriter output)
    {
        output.WriteLine(HistoricalImportJson.SerializeString(result));
        return 0;
    }

    private static int ExitCode(string code) => code switch
    {
        HistoricalImportErrorCodes.RequestInvalid => 2,
        HistoricalImportErrorCodes.StoreBusy
            or HistoricalImportErrorCodes.StoreUnavailable
            or HistoricalImportErrorCodes.TransactionFailed => 5,
        _ => 4,
    };

    private static bool TryParse(string[] args, out HistoricalImportCliOptions? options)
    {
        options = null;
        if (args.Length == 0)
        {
            return false;
        }

        var command = args[0];
        var required = command switch
        {
            "preview" or "confirm" or "commit" => new[] { "--database", "--request" },
            "status" or "result" => new[] { "--database", "--operation-id" },
            "history" or "observations" => new[] { "--database" },
            _ => null,
        };
        if (required is null)
        {
            return false;
        }

        var allowed = required.ToHashSet(StringComparer.Ordinal);
        if (command is "history" or "observations")
        {
            allowed.Add("--limit");
        }
        if (command == "observations")
        {
            allowed.Add("--cursor");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length
                || !allowed.Contains(args[index])
                || args[index + 1].StartsWith("--", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(args[index + 1])
                || !values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
        }

        if (required.Any(name => !values.ContainsKey(name))
            || values.TryGetValue("--operation-id", out var operationIdText)
                && !IsOpaqueIdentifier(operationIdText, "hop_")
            || values.TryGetValue("--cursor", out var cursorText)
                && !IsOpaqueIdentifier(cursorText, "hoc_")
            || values.TryGetValue("--limit", out var limitText)
                && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
                    || limit is < 1 or > 100))
        {
            return false;
        }

        values.TryGetValue("--request", out var requestPath);
        values.TryGetValue("--operation-id", out var operationId);
        values.TryGetValue("--limit", out var parsedLimitText);
        values.TryGetValue("--cursor", out var cursor);
        options = new(
            command,
            values["--database"],
            requestPath,
            operationId,
            parsedLimitText is null ? 100 : int.Parse(parsedLimitText, NumberStyles.None, CultureInfo.InvariantCulture),
            cursor);
        return true;
    }

    private static bool IsOpaqueIdentifier(string value, string prefix) =>
        value.Length == prefix.Length + 32
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static int Fail(TextWriter error, string code, int exitCode)
    {
        error.WriteLine(code);
        return exitCode;
    }

    private sealed record HistoricalImportCliOptions(
        string Command,
        string DatabasePath,
        string? RequestPath,
        string? OperationId,
        int Limit,
        string? Cursor);
}
