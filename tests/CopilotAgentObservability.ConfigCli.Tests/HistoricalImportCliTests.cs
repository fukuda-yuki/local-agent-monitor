using CopilotAgentObservability.ConfigCli.HistoricalImport;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class HistoricalImportCliTests
{
    [Theory]
    [MemberData(nameof(InvalidArgumentCases))]
    public void Run_InvalidArguments_ReturnsFixedNoLeakExit(string[] args)
    {
        var result = Run(args);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_invalid_arguments" + Environment.NewLine, result.Error);
    }

    public static IEnumerable<object[]> InvalidArgumentCases()
    {
        yield return [Array.Empty<string>()];
        yield return [new[] { "unknown" }];
        yield return [new[] { "preview", "--database", "monitor.db" }];
        yield return [new[] { "status", "--database", "monitor.db", "--operation-id", "hop_invalid" }];
        yield return [new[] { "history", "--database", "monitor.db", "--limit", "0" }];
        yield return [new[] { "observations", "--database", "monitor.db", "--limit", "101" }];
        yield return [new[] { "observations", "--database", "monitor.db", "--cursor", "hoc_invalid" }];
    }

    [Fact]
    public void CliApplication_DispatchesHistoricalImportAndHelpDocumentsEveryCommand()
    {
        var dispatch = RunApplication(["historical-import", "unknown"]);

        Assert.Equal(2, dispatch.ExitCode);
        Assert.Equal(string.Empty, dispatch.Output);
        Assert.Equal("historical_import_invalid_arguments" + Environment.NewLine, dispatch.Error);
        Assert.Contains("historical-import preview --database <monitor.db> --request <request.json>", CliHelpText.Text, StringComparison.Ordinal);
        Assert.Contains("historical-import confirm --database <monitor.db> --request <request.json>", CliHelpText.Text, StringComparison.Ordinal);
        Assert.Contains("historical-import commit --database <monitor.db> --request <request.json>", CliHelpText.Text, StringComparison.Ordinal);
        Assert.Contains("historical-import status --database <monitor.db> --operation-id <hop_...>", CliHelpText.Text, StringComparison.Ordinal);
        Assert.Contains("historical-import result --database <monitor.db> --operation-id <hop_...>", CliHelpText.Text, StringComparison.Ordinal);
        Assert.Contains("historical-import history --database <monitor.db> [--limit <1..100>]", CliHelpText.Text, StringComparison.Ordinal);
        Assert.Contains("historical-import observations --database <monitor.db> [--limit <1..100>] [--cursor <hoc_...>]", CliHelpText.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_MissingExistingDatabase_FailsBeforeOpeningSourceAndDoesNotEchoPath()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "missing-private-monitor.db");
        var request = Path.Combine(temp.Path, "request.json");
        File.WriteAllText(request, ValidSourceSelection(Path.Combine(temp.Path, "private-source")));

        var result = Run(["preview", "--database", database, "--request", request]);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_store_unavailable" + Environment.NewLine, result.Error);
        Assert.DoesNotContain(temp.Path, result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(database));
    }

    [Fact]
    public void Preview_RequestOverOneMiB_IsRejectedBeforeJsonOrSourceProcessing()
    {
        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var request = Path.Combine(temp.Path, "oversized-request.json");
        using (var stream = new FileStream(request, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(HistoricalImportCli.MaximumRequestBytes + 1L);

        var result = Run(["preview", "--database", database, "--request", request]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_request_invalid" + Environment.NewLine, result.Error);
    }

    [Fact]
    public void Preview_RequestSymlink_IsRejectedWithoutReadingTheTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var target = Path.Combine(temp.Path, "target-request.json");
        File.WriteAllText(target, ValidSourceSelection(Path.Combine(temp.Path, "private-source")));
        var request = Path.Combine(temp.Path, "request-link.json");
        File.CreateSymbolicLink(request, target);

        var result = Run(["preview", "--database", database, "--request", request]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_request_invalid" + Environment.NewLine, result.Error);
    }

    [Fact]
    public void Preview_RequestWithSymlinkAncestor_IsRejectedWithoutReadingTheTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var targetDirectory = Path.Combine(temp.Path, "request-target");
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, "request.json");
        File.WriteAllText(target, ValidSourceSelection(Path.Combine(temp.Path, "private-source")));
        var link = Path.Combine(temp.Path, "request-link");
        Directory.CreateSymbolicLink(link, targetDirectory);

        var result = Run([
            "preview", "--database", database, "--request", Path.Combine(link, "request.json")]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_request_invalid" + Environment.NewLine, result.Error);
    }

    [Fact]
    public async Task Preview_UnixFifoRequest_IsRejectedWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var request = Path.Combine(temp.Path, "request.pipe");
        Assert.Equal(0, CreateFifo(request));

        var invocation = Task.Run(() => Run(["preview", "--database", database, "--request", request]));

        var result = await invocation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_request_invalid" + Environment.NewLine, result.Error);
    }

    [Fact]
    public void History_NonDatabaseFile_ReturnsStoreUnavailableWithoutReplacingTheFile()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "not-a-database.db");
        File.WriteAllText(database, "SYNTHETIC_NOT_SQLITE");

        var result = Run(["history", "--database", database]);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_store_unavailable" + Environment.NewLine, result.Error);
        Assert.Equal("SYNTHETIC_NOT_SQLITE", File.ReadAllText(database));
    }

    [Fact]
    public void History_UnrelatedSqliteDatabase_ReturnsStoreUnavailableWithoutInstallingHistoricalSchema()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "unrelated.db");
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated_owner(value TEXT NOT NULL);";
            command.ExecuteNonQuery();
        }

        var result = Run(["history", "--database", database]);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_store_unavailable" + Environment.NewLine, result.Error);
        using var verification = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        verification.Open();
        using var objects = verification.CreateCommand();
        objects.CommandText =
            "SELECT group_concat(name, ',') FROM sqlite_schema WHERE type='table' ORDER BY name;";
        Assert.Equal("unrelated_owner", objects.ExecuteScalar());
    }

    [Fact]
    public void VerifiedDatabaseConnection_RejectsReplacementAfterSqliteOpen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var replacementDirectory = Path.Combine(temp.Path, "replacement");
        Directory.CreateDirectory(replacementDirectory);
        var replacement = CreateDatabaseFile(replacementDirectory);
        var original = database + ".original";
        using var lease = HistoricalImportDatabaseLease.Open(database);

        var exception = Assert.Throws<HistoricalImportException>(() =>
            lease.OpenVerifiedConnection(() =>
            {
                File.Move(database, original);
                File.Move(replacement, database);
            }));

        Assert.Equal(HistoricalImportErrorCodes.StoreUnavailable, exception.Code);
    }

    [Fact]
    public void VerifiedDatabaseConnection_RejectsMultiSwapWithoutMutatingEitherDatabase()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var replacementDirectory = Path.Combine(temp.Path, "replacement");
        Directory.CreateDirectory(replacementDirectory);
        var replacement = CreateDatabaseFile(replacementDirectory);
        var leasedAway = database + ".leased";
        var replacementAway = database + ".replacement";
        using var lease = HistoricalImportDatabaseLease.Open(database);
        File.Move(database, leasedAway);
        File.Move(replacement, database);

        var exception = Assert.Throws<HistoricalImportException>(() =>
            lease.OpenVerifiedConnection(
                afterConnectionOpened: () =>
                {
                    File.Move(database, replacementAway);
                    File.Move(leasedAway, database);
                },
                beforeUnixMovedCheck: () =>
                {
                    File.Move(database, leasedAway);
                    File.Move(replacementAway, database);
                }));

        Assert.Equal(HistoricalImportErrorCodes.StoreUnavailable, exception.Code);
        AssertHistoricalImportSchemaAbsent(database);
        AssertHistoricalImportSchemaAbsent(leasedAway);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inaccessible")]
    public void DescriptorSnapshotFailure_ReturnsStoreUnavailableExit5WithoutMutation(string failure)
    {
        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        Func<IReadOnlyDictionary<int, HistoricalImportFileIdentity>> descriptorSnapshotFactory = failure switch
        {
            "missing" => () => throw new DirectoryNotFoundException("synthetic descriptor directory"),
            "inaccessible" => () => throw new UnauthorizedAccessException("synthetic descriptor access"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = HistoricalImportCli.Run(
            ["history", "--database", database],
            output,
            error,
            applicationFactory: null,
            descriptorSnapshotFactory);

        Assert.Equal(5, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal("historical_import_store_unavailable" + Environment.NewLine, error.ToString());
        AssertHistoricalImportSchemaAbsent(database);
    }

    [Fact]
    public void History_DatabaseAccessFailure_ReturnsStoreUnavailable()
    {
        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);

        var result = Run(
            ["history", "--database", database],
            _ => throw new UnauthorizedAccessException("private database path"));

        Assert.Equal(5, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_store_unavailable" + Environment.NewLine, result.Error);
        Assert.DoesNotContain(temp.Path, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabasePath_IsNormalizedOnceBeforePreflightAndApplicationCreation()
    {
        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var intermediate = Path.Combine(temp.Path, "intermediate");
        Directory.CreateDirectory(intermediate);
        var lexicalDatabase = Path.Combine(intermediate, "..", Path.GetFileName(database));
        var application = new RecordingApplication();
        string? factoryPath = null;

        var result = Run(
            ["observations", "--database", lexicalDatabase],
            path =>
            {
                factoryPath = path;
                return application;
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Path.GetFullPath(database), factoryPath);
    }

    [Fact]
    public void DatabaseSymlinkHiddenByDotDot_IsNotTraversed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var targetDirectory = Path.Combine(temp.Path, "database-target");
        var targetChild = Path.Combine(targetDirectory, "child");
        Directory.CreateDirectory(targetChild);
        _ = CreateDatabaseFile(targetDirectory);
        var link = Path.Combine(temp.Path, "database-link");
        Directory.CreateSymbolicLink(link, targetChild);
        var lexicalDatabase = Path.Combine(link, "..", "monitor.db");
        var factoryInvoked = false;

        var result = Run(
            ["history", "--database", lexicalDatabase],
            _ =>
            {
                factoryInvoked = true;
                return new RecordingApplication();
            });

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("historical_import_store_unavailable" + Environment.NewLine, result.Error);
        Assert.False(factoryInvoked);
    }

    [Theory]
    [InlineData("{\"contract_version\":\"historical-import-workflow/v1\",\"contract_version\":\"historical-import-workflow/v1\"}")]
    [InlineData("{\"contract_version\":\"historical-import-workflow/v1\",\"schema_version\":\"historical-import-workflow-source-selection/v1\",\"source_surface\":\"github-copilot-cli\",\"reference_kind\":\"selected_root\",\"exact_reference\":\"C:/private/source\",\"session_id\":\"session-1\",\"source_application_version\":\"1.0.71\",\"requested_capture\":\"metadata_only\",\"consent_granted\":true,\"raw_content\":\"TOP_SECRET\"}")]
    [InlineData("{\"contract_version\":\"historical-import-workflow/v2\",\"schema_version\":\"historical-import-workflow-source-selection/v1\"}")]
    public void Preview_NonStrictRequest_ReturnsOnlyFixedSanitizedCode(string json)
    {
        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var request = Path.Combine(temp.Path, "private-request.json");
        File.WriteAllText(request, json);

        var result = Run(["preview", "--database", database, "--request", request]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_request_invalid" + Environment.NewLine, result.Error);
        Assert.DoesNotContain("TOP_SECRET", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("C:/private", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Path, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_RepeatedOrUnknownOptions_FailClosed()
    {
        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var request = Path.Combine(temp.Path, "request.json");
        File.WriteAllText(request, ValidSourceSelection(Path.Combine(temp.Path, "private-source")));

        var repeated = Run(["preview", "--database", database, "--database", database, "--request", request]);
        var unknown = Run(["preview", "--database", database, "--request", request, "--json"]);

        Assert.Equal(2, repeated.ExitCode);
        Assert.Equal(string.Empty, repeated.Output);
        Assert.Equal("historical_import_invalid_arguments" + Environment.NewLine, repeated.Error);
        Assert.Equal(2, unknown.ExitCode);
        Assert.Equal(string.Empty, unknown.Output);
        Assert.Equal("historical_import_invalid_arguments" + Environment.NewLine, unknown.Error);
    }

    [Fact]
    public void Preview_CurrentGitHubProfile_EmitsOneSafeNonActionableWorkflowObject()
    {
        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var selectedRoot = Path.GetFullPath(Path.Combine(temp.Path, "private-copilot-root"));
        var sessionDirectory = Path.Combine(selectedRoot, "session-state", "session-1");
        Directory.CreateDirectory(sessionDirectory);
        File.WriteAllText(Path.Combine(sessionDirectory, "events.jsonl"), "SENSITIVE_CONTENT_MUST_NOT_BE_READ");
        var request = Path.Combine(temp.Path, "preview-request.json");
        File.WriteAllText(request, ValidSourceSelection(selectedRoot));

        var preview = Run(["preview", "--database", database, "--request", request]);

        Assert.Equal(0, preview.ExitCode);
        Assert.Equal(string.Empty, preview.Error);
        using var json = System.Text.Json.JsonDocument.Parse(preview.Output);
        var root = json.RootElement;
        Assert.Equal("historical-import-workflow/v1", root.GetProperty("contract_version").GetString());
        Assert.Equal("historical-import-workflow-preview/v1", root.GetProperty("schema_version").GetString());
        Assert.Equal("github-copilot-cli", root.GetProperty("source_surface").GetString());
        Assert.Equal("github-copilot-cli-session-state", root.GetProperty("profile_id").GetString());
        Assert.Equal("github-copilot-cli-history-v1", root.GetProperty("adapter_id").GetString());
        Assert.Equal("unsupported", root.GetProperty("adapter_state").GetString());
        Assert.False(root.GetProperty("commit_allowed").GetBoolean());
        Assert.Equal("historical_import_no_eligible_candidates", root.GetProperty("rejection_code").GetString());
        Assert.Equal("not_read", root.GetProperty("content_risk").GetString());
        Assert.DoesNotContain(selectedRoot, preview.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("SENSITIVE_CONTENT_MUST_NOT_BE_READ", preview.Output, StringComparison.Ordinal);

        var confirmationRequest = Path.Combine(temp.Path, "confirmation-request.json");
        File.WriteAllText(confirmationRequest, $$"""
            {
              "contract_version": "historical-import-workflow/v1",
              "schema_version": "historical-import-workflow-confirmation-request/v1",
              "preview_id": {{System.Text.Json.JsonSerializer.Serialize(root.GetProperty("preview_id").GetString())}},
              "preview_digest": {{System.Text.Json.JsonSerializer.Serialize(root.GetProperty("preview_digest").GetString())}},
              "snapshot_version": {{System.Text.Json.JsonSerializer.Serialize(root.GetProperty("snapshot_version").GetString())}},
              "decision": "confirm"
            }
            """);

        var confirmation = Run(["confirm", "--database", database, "--request", confirmationRequest]);

        Assert.Equal(4, confirmation.ExitCode);
        Assert.Equal(string.Empty, confirmation.Output);
        Assert.Equal("historical_import_no_eligible_candidates" + Environment.NewLine, confirmation.Error);
        Assert.DoesNotContain(selectedRoot, confirmation.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadCommands_UseSharedWorkflowDtosAndFixedNotFoundMapping()
    {
        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);

        var history = Run(["history", "--database", database, "--limit", "1"]);
        var observations = Run(["observations", "--database", database]);
        var status = Run([
            "status", "--database", database,
            "--operation-id", "hop_0123456789abcdef0123456789abcdef"]);
        var result = Run([
            "result", "--database", database,
            "--operation-id", "hop_0123456789abcdef0123456789abcdef"]);

        Assert.Equal(0, history.ExitCode);
        Assert.Equal(string.Empty, history.Error);
        using (var json = System.Text.Json.JsonDocument.Parse(history.Output))
        {
            Assert.Equal("historical-import-workflow-import-history/v1", json.RootElement.GetProperty("schema_version").GetString());
            Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        }

        Assert.Equal(0, observations.ExitCode);
        Assert.Equal(string.Empty, observations.Error);
        using (var json = System.Text.Json.JsonDocument.Parse(observations.Output))
        {
            Assert.Equal("historical-import-workflow-observation-list/v1", json.RootElement.GetProperty("schema_version").GetString());
            Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());
        }

        Assert.Equal(4, status.ExitCode);
        Assert.Equal(string.Empty, status.Output);
        Assert.Equal("historical_import_operation_not_found" + Environment.NewLine, status.Error);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("historical_import_operation_not_found" + Environment.NewLine, result.Error);
    }

    [Fact]
    public void Observations_ForwardsValidatedLimitAndCursorToSharedApplication()
    {
        using var temp = new TempDirectory();
        var database = CreateDatabaseFile(temp.Path);
        var application = new RecordingApplication();
        const string Cursor = "hoc_0123456789abcdef0123456789abcdef";

        var result = Run(
            ["observations", "--database", database, "--limit", "17", "--cursor", Cursor],
            _ => application);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.Equal(17, application.ObservationLimit);
        Assert.Equal(Cursor, application.ObservationCursor);
        using var json = System.Text.Json.JsonDocument.Parse(result.Output);
        Assert.Equal("historical-import-workflow-observation-list/v1", json.RootElement.GetProperty("schema_version").GetString());
    }

    private static string ValidSourceSelection(string reference) => $$"""
        {
          "contract_version": "historical-import-workflow/v1",
          "schema_version": "historical-import-workflow-source-selection/v1",
          "source_surface": "github-copilot-cli",
          "reference_kind": "selected_root",
          "exact_reference": {{System.Text.Json.JsonSerializer.Serialize(Path.GetFullPath(reference))}},
          "session_id": "session-1",
          "source_application_version": "1.0.71",
          "requested_capture": "metadata_only",
          "consent_granted": true
        }
        """;

    private static string CreateDatabaseFile(string directory)
    {
        var path = Path.Combine(directory, "monitor.db");
        new RawTelemetryStore(path).CreateMonitorSchema();
        return path;
    }

    private static void AssertHistoricalImportSchemaAbsent(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_version WHERE component='historical_import';";
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static int CreateFifo(string path) => OperatingSystem.IsMacOS()
        ? MkFifoMacOs(path, 0x180)
        : MkFifoLinux(path, 0x180);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifoLinux(string path, uint mode);

    [DllImport("libSystem.B.dylib", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifoMacOs(string path, uint mode);

    private static (int ExitCode, string Output, string Error) Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return (HistoricalImportCli.Run(args, output, error), output.ToString(), error.ToString());
    }

    private static (int ExitCode, string Output, string Error) Run(
        string[] args,
        Func<string, IHistoricalImportApplication> factory)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return (HistoricalImportCli.Run(args, output, error, factory), output.ToString(), error.ToString());
    }

    private static (int ExitCode, string Output, string Error) RunApplication(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return (CliApplication.Run(args, output, error), output.ToString(), error.ToString());
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            var temporaryRoot = System.IO.Path.GetTempPath();
            if (OperatingSystem.IsMacOS() && temporaryRoot.StartsWith("/var/", StringComparison.Ordinal))
            {
                temporaryRoot = "/private" + temporaryRoot;
            }

            Path = System.IO.Path.Combine(
                temporaryRoot,
                $"historical-import-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class RecordingApplication : IHistoricalImportApplication
    {
        internal int ObservationLimit { get; private set; }
        internal string? ObservationCursor { get; private set; }

        public HistoricalImportPreview CreatePreview(HistoricalImportPreviewRequest request) => throw new NotSupportedException();
        public HistoricalImportPreview ReadPreview(string previewId) => throw new NotSupportedException();
        public HistoricalImportConfirmation IssueConfirmation(HistoricalImportConfirmationRequest request) => throw new NotSupportedException();
        public HistoricalImportResult Commit(HistoricalImportCommitRequest request) => throw new NotSupportedException();
        public HistoricalImportStatus ReadStatus(string operationId) => throw new NotSupportedException();
        public HistoricalImportResult ReadResult(string operationId) => throw new NotSupportedException();
        public HistoricalImportHistory ListHistory(int limit = 100) => throw new NotSupportedException();

        public HistoricalObservationList ListObservations(int limit = 100, string? cursor = null)
        {
            ObservationLimit = limit;
            ObservationCursor = cursor;
            return new(
                HistoricalImportContractVersions.Workflow,
                HistoricalImportContractVersions.ObservationList,
                "historical",
                [],
                null);
        }

        public HistoricalObservationDetail GetObservation(string observationId) => throw new NotSupportedException();
    }
}
