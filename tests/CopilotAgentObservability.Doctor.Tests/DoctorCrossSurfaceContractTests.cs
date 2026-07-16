using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using CopilotAgentObservability.Doctor.Tests.Persistence;

namespace CopilotAgentObservability.Doctor.Tests;

public sealed class DoctorCrossSurfaceContractTests
{
    [Fact]
    public async Task AllCatalogStatesUseOneCanonicalProductionResultAcrossDirectCliAndHttp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"doctor-cross-surface-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var monitor = await RunningDoctorMonitor.StartAsync();
            foreach (var (stateCode, snapshot) in DoctorEvaluatorTests.StateSnapshots())
            {
                var snapshotJson = SerializeSnapshot(snapshot);
                var fixturePath = Path.Combine(directory, $"{stateCode}.facts.json");
                await File.WriteAllTextAsync(fixturePath, snapshotJson);
                var directJson = DoctorJson.SerializeResult(DoctorEvaluator.Evaluate(snapshot));

                using var cliOutput = new StringWriter();
                using var cliError = new StringWriter();
                _ = CliApplication.Run(
                    ["doctor", "evaluate", "--input", fixturePath, "--json"],
                    cliOutput,
                    cliError);

                using var response = await monitor.Client.PostAsync(
                    "/api/doctor/evaluations",
                    new StringContent(snapshotJson, Encoding.UTF8, "application/json"));
                var httpJson = await response.Content.ReadAsStringAsync();

                Assert.Equal(string.Empty, cliError.ToString());
                Assert.Equal(directJson + Environment.NewLine, cliOutput.ToString());
                Assert.Equal(directJson, httpJson);
                Assert.Equal(directJson, DoctorJson.SerializeResult(DoctorJson.DeserializeResult(httpJson)));
                Assert.Contains(
                    Assert.IsType<DoctorEvaluation>(DoctorJson.DeserializeResult(httpJson).Evaluation).States,
                    state => state.StateCode == stateCode);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProductionCliLifecycleProjectsSharedStoreOutcomesWithoutMutationDrift()
    {
        using var database = new DoctorTestDatabase();
        var verification = StartCliVerification(database.Path);
        var store = new SqliteDoctorVerificationStore(database.Path, TimeProvider.System);
        var kinds = Enum.GetValues<DoctorEvidenceKind>();
        var candidates = kinds.Select((kind, index) => new DoctorEvidenceCandidate(
            $"01890abc-def0-7000-8000-{index + 1:x12}",
            verification.VerificationId,
            verification.ExpectedSourceSurface,
            verification.ExpectedSourceAdapter,
            DoctorEvidenceClass.RealSource,
            kind,
            $"receipt-{JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString())}",
            verification.StartedAt,
            verification.ExpiresAt)).ToArray();
        foreach (var candidate in candidates)
        {
            Assert.Equal(DoctorResultCode.VerificationActive, store.ObserveCandidate(candidate).Code);
        }
        var references = candidates.Select(candidate => candidate.EvidenceRef).ToArray();

        var nonReady = await CompleteCliAsync(
            database.Path,
            verification,
            expectedRevision: 1,
            CompletionContext(DoctorTestSnapshots.ReadyNoRealTrace(), verification),
            references,
            "non-ready");
        AssertCliResult(nonReady, 3, DoctorResultCode.EvaluationCompleted);
        Assert.Equal(DoctorStateCode.ReadyNoRealTrace, nonReady.Result.Evaluation?.PrimaryState?.StateCode);
        AssertActiveUnchanged(store, verification.VerificationId);

        var partial = await CompleteCliAsync(
            database.Path,
            verification,
            expectedRevision: 1,
            CompletionContext(DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true), verification) with
            {
                InstallAndSourceVersion = null,
            },
            references,
            "partial");
        AssertCliResult(partial, 3, DoctorResultCode.PartialFactSnapshot);
        AssertActiveUnchanged(store, verification.VerificationId);

        var stale = await CompleteCliAsync(
            database.Path,
            verification,
            expectedRevision: 2,
            CompletionContext(DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true), verification),
            references,
            "stale");
        AssertCliResult(stale, 4, DoctorResultCode.VerificationStale);
        AssertActiveUnchanged(store, verification.VerificationId);

        var ready = await CompleteCliAsync(
            database.Path,
            verification,
            expectedRevision: 1,
            CompletionContext(DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true), verification),
            references,
            "ready");
        AssertCliResult(ready, 0, DoctorResultCode.VerificationCompleted);
        Assert.Equal(DoctorStateCode.FirstTraceReady, ready.Result.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(references, ready.Result.Verification?.AcceptedEvidenceRefs);

        var terminal = await CompleteCliAsync(
            database.Path,
            verification,
            expectedRevision: 2,
            CompletionContext(DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true), verification),
            references,
            "terminal");
        AssertCliResult(terminal, 4, DoctorResultCode.VerificationAlreadyCompleted);
        var completed = Assert.IsType<DoctorVerification>(store.Get(verification.VerificationId).Verification);
        Assert.Equal(DoctorVerificationState.Completed, completed.State);
        Assert.Equal(2, completed.Revision);
        Assert.Equal(references, completed.AcceptedEvidenceRefs);

        var expiring = StartCliVerification(database.Path);
        using (var connection = database.Open())
        {
            DoctorTestDatabase.Execute(
                connection,
                "UPDATE doctor_verifications SET started_at=$started,expires_at=$expires WHERE verification_id=$id;",
                ("$started", CanonicalTimestamp(DateTimeOffset.UtcNow.AddMinutes(-10))),
                ("$expires", CanonicalTimestamp(DateTimeOffset.UtcNow.AddMinutes(-5))),
                ("$id", expiring.VerificationId));
        }
        var expired = RunCli(
            "doctor", "verification", "status", "--database", database.Path,
            "--verification-id", expiring.VerificationId, "--json");
        AssertCliResult(expired, 4, DoctorResultCode.VerificationExpired);
        Assert.Equal(DoctorVerificationState.Expired, expired.Result.Verification?.State);
        Assert.Empty(expired.Result.Verification?.AcceptedEvidenceRefs ?? []);

        var cancelling = StartCliVerification(database.Path);
        var staleCancel = RunCli(
            "doctor", "verification", "cancel", "--database", database.Path,
            "--verification-id", cancelling.VerificationId, "--expected-revision", "2", "--json");
        AssertCliResult(staleCancel, 4, DoctorResultCode.VerificationStale);
        AssertActiveUnchanged(store, cancelling.VerificationId);
        var cancelled = RunCli(
            "doctor", "verification", "cancel", "--database", database.Path,
            "--verification-id", cancelling.VerificationId, "--expected-revision", "1", "--json");
        AssertCliResult(cancelled, 0, DoctorResultCode.VerificationCancelled);
        Assert.Equal(DoctorVerificationState.Cancelled, cancelled.Result.Verification?.State);
        Assert.Equal(2, cancelled.Result.Verification?.Revision);
        Assert.Empty(cancelled.Result.Verification?.AcceptedEvidenceRefs ?? []);
        var cancelledConflict = RunCli(
            "doctor", "verification", "cancel", "--database", database.Path,
            "--verification-id", cancelling.VerificationId, "--expected-revision", "2", "--json");
        AssertCliResult(cancelledConflict, 4, DoctorResultCode.VerificationAlreadyCancelled);
    }

    [Fact]
    public async Task MonitorNotRunning_UsesOneCanonicalResultAcrossDirectCliAndHttp()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "monitor-not-running.facts.json");
        var fixtureJson = await File.ReadAllTextAsync(fixturePath);

        using var cliOutput = new StringWriter();
        using var cliError = new StringWriter();
        var cliExitCode = CliApplication.Run(
            ["doctor", "evaluate", "--input", fixturePath, "--json"],
            cliOutput,
            cliError);

        Assert.Equal(3, cliExitCode);
        Assert.Equal(string.Empty, cliError.ToString());
        var cliJson = cliOutput.ToString().TrimEnd();

        var snapshot = DoctorJson.DeserializeFactSnapshot(fixtureJson);
        var direct = DoctorEvaluator.Evaluate(snapshot);
        var directJson = DoctorJson.SerializeResult(direct);
        Assert.Equal(directJson, cliJson);
        Assert.Equal(
            Encoding.UTF8.GetBytes(directJson),
            Encoding.UTF8.GetBytes(DoctorJson.SerializeResult(direct)));

        var cliResult = DoctorJson.DeserializeResult(cliJson);
        Assert.Equivalent(direct, cliResult, strict: true);

        using var humanOutput = new StringWriter();
        using var humanError = new StringWriter();
        var humanExitCode = CliApplication.Run(
            ["doctor", "evaluate", "--input", fixturePath],
            humanOutput,
            humanError);

        Assert.Equal(3, humanExitCode);
        Assert.Equal(string.Empty, humanError.ToString());
        Assert.Equal(DoctorHumanProjector.Project(direct) + Environment.NewLine, humanOutput.ToString());
        Assert.InRange(humanOutput.ToString().Length, 1, 1024);

        await using var monitor = await RunningDoctorMonitor.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/doctor/evaluations")
        {
            Content = new StringContent(fixtureJson, Encoding.UTF8, "application/json")
        };
        using var response = await monitor.Client.SendAsync(request);
        var httpJson = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        var httpResult = DoctorJson.DeserializeResult(httpJson);
        Assert.Equivalent(direct, httpResult, strict: true);
        Assert.Equal(DoctorSchemaVersions.ResultV1, direct.SchemaVersion);
        Assert.Equal(DoctorResultCode.EvaluationCompleted, direct.Code);
        Assert.True(direct.Success);
        Assert.Null(direct.Verification);
        var evaluation = Assert.IsType<DoctorEvaluation>(direct.Evaluation);
        Assert.Equal("github-copilot-vscode", evaluation.SourceSurface);
        Assert.Empty(evaluation.MissingFactFamilies);
        var state = Assert.Single(evaluation.States);
        Assert.Equivalent(state, evaluation.PrimaryState, strict: true);
        Assert.Equal(DoctorSchemaVersions.ResultV1, state.SchemaVersion);
        Assert.Equal(DoctorStateCode.MonitorNotRunning, state.StateCode);
        Assert.Equal(DoctorSeverity.Error, state.Severity);
        Assert.Equal(DoctorRetryability.AfterAction, state.Retryability);
        Assert.Equal(DoctorNextAction.StartMonitor, state.NextAction);
        Assert.Equal(snapshot.ObservedAt, state.ObservedAt);
        Assert.Null(state.VerificationId);
        Assert.Empty(state.EvidenceRefs);
        Assert.Equal(
            [DoctorStateCode.MonitorNotRunning],
            state.ReasonCodes);
        Assert.Contains("\"success\":true", httpJson, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"evaluation_completed\"", httpJson, StringComparison.Ordinal);
        Assert.Contains("\"state_code\":\"monitor_not_running\"", httpJson, StringComparison.Ordinal);
        Assert.DoesNotContain(fixturePath, httpJson, StringComparison.Ordinal);
    }

    private sealed class RunningDoctorMonitor(
        Microsoft.AspNetCore.Builder.WebApplication app,
        HttpClient client,
        string directory) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async Task<RunningDoctorMonitor> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"doctor-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var options = new MonitorOptions(
                Path.Combine(directory, "monitor.db"),
                "http://127.0.0.1:0",
                SanitizedOnly: true,
                MonitorOptions.DefaultMaxRequestBodyBytes);
            Microsoft.AspNetCore.Builder.WebApplication? app = null;
            try
            {
                app = MonitorHost.Build(options);
                await app.StartAsync();
                var addresses = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()?
                    .Addresses;
                var address = Assert.Single(addresses!);
                return new RunningDoctorMonitor(
                    app,
                    new HttpClient { BaseAddress = new Uri(address) },
                    directory);
            }
            catch
            {
                try
                {
                    if (app is not null)
                    {
                        await app.DisposeAsync();
                    }
                }
                finally
                {
                    Directory.Delete(directory, recursive: true);
                }

                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await app.StopAsync();
            }
            finally
            {
                await app.DisposeAsync();
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    internal static string SerializeSnapshot(DoctorFactSnapshot snapshot)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new CanonicalTimestampConverter());
        return JsonSerializer.Serialize(snapshot, options);
    }

    private static DoctorVerification StartCliVerification(string databasePath)
    {
        var command = RunCli(
            "doctor", "verification", "start", "--database", databasePath,
            "--source-surface", "github-copilot-vscode", "--expires-at",
            CanonicalTimestamp(DateTimeOffset.UtcNow.AddMinutes(5)), "--json");
        AssertCliResult(command, 0, DoctorResultCode.VerificationStarted);
        return Assert.IsType<DoctorVerification>(command.Result.Verification);
    }

    private static async Task<CliCommandResult> CompleteCliAsync(
        string databasePath,
        DoctorVerification verification,
        int expectedRevision,
        DoctorFactSnapshot snapshot,
        IReadOnlyList<string> evidenceRefs,
        string name)
    {
        var inputPath = Path.Combine(Path.GetDirectoryName(databasePath)!, $"completion-{name}.json");
        await File.WriteAllTextAsync(
            inputPath,
            $$"""{"fact_snapshot":{{SerializeSnapshot(snapshot)}},"accepted_evidence_refs":{{JsonSerializer.Serialize(evidenceRefs)}}}""");
        return RunCli(
            "doctor", "verification", "complete", "--database", databasePath,
            "--verification-id", verification.VerificationId, "--expected-revision",
            expectedRevision.ToString(CultureInfo.InvariantCulture), "--input", inputPath, "--json");
    }

    private static DoctorFactSnapshot CompletionContext(
        DoctorFactSnapshot snapshot,
        DoctorVerification verification) => snapshot with
        {
            SourceSurface = verification.ExpectedSourceSurface,
            ExpectedSourceAdapter = verification.ExpectedSourceAdapter,
            VerificationId = verification.VerificationId,
            ObservedAt = verification.StartedAt,
            Observations = [],
        };

    private static CliCommandResult RunCli(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CliApplication.Run(args, output, error);
        return new(exitCode, DoctorJson.DeserializeResult(output.ToString()), error.ToString());
    }

    private static void AssertCliResult(
        CliCommandResult command,
        int expectedExitCode,
        DoctorResultCode expectedCode)
    {
        Assert.Equal(expectedExitCode, command.ExitCode);
        Assert.Equal(expectedCode, command.Result.Code);
        Assert.Equal(
            command.Result.Success ? string.Empty : Wire(expectedCode) + Environment.NewLine,
            command.Error);
    }

    private static void AssertActiveUnchanged(SqliteDoctorVerificationStore store, string verificationId)
    {
        var status = store.Get(verificationId);
        Assert.Equal(DoctorResultCode.VerificationActive, status.Code);
        var verification = Assert.IsType<DoctorVerification>(status.Verification);
        Assert.Equal(DoctorVerificationState.Active, verification.State);
        Assert.Equal(1, verification.Revision);
        Assert.Empty(verification.AcceptedEvidenceRefs);
    }

    private static string CanonicalTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string Wire<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private sealed record CliCommandResult(int ExitCode, DoctorResult Result, string Error);

    private sealed class CanonicalTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture));
    }
}
