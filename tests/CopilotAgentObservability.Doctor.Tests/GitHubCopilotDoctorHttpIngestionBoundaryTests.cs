using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.Telemetry;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.Doctor.Tests;

public sealed class GitHubCopilotDoctorHttpIngestionBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task AcceptedHttpResponseHasExactPersistedRawRecordBeforeCopilotEvidenceObservation()
    {
        using var database = TemporaryDatabase.Create();
        var time = new FixedTimeProvider(Now);
        await using var host = await StartMonitorAsync(database.Path, time);
        var verification = StartVerification(database.Path, time);

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(ValidVscodeTraceJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(responseJson.RootElement.GetProperty("accepted").GetBoolean());
        var rawRecordId = responseJson.RootElement.GetProperty("rawRecordId").GetInt64();
        Assert.Equal(
            $"{rawRecordId}|11111111111111111111111111111111|2026-07-17T01:02:03.0000000+00:00",
            ReadRawRecordIdentity(database.Path, rawRecordId));

        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            time,
            new GitHubCopilotDoctorEvidenceSelection(
                verification.VerificationId,
                "vscode",
                rawRecordId,
                NativeSession: null));

        Assert.Equal(DoctorResultCode.VerificationActive, observed.ObservationResult.Code);
        Assert.Equal(
            [DoctorEvidenceKind.Ingest, DoctorEvidenceKind.RawPersistence, DoctorEvidenceKind.Projection],
            observed.ObservedKinds);
        Assert.Equal(LastIngestOutcome.Accepted, observed.Snapshot.LastIngest!.Outcome);
        Assert.Equal(RawPersistenceOutcome.Persisted, observed.Snapshot.RawPersistence!.Outcome);
        Assert.Equal(ProjectionOutcome.NotStarted, observed.Snapshot.Projection!.Outcome);
        Assert.Equal(3, CountCandidates(database.Path, verification.VerificationId));
    }

    [Fact]
    public async Task RejectedPayloadHasNoRawIdentityAndCannotFabricateCopilotRuntimeAttribution()
    {
        using var database = TemporaryDatabase.Create();
        var time = new FixedTimeProvider(Now);
        await using var host = await StartMonitorAsync(database.Path, time);
        var verification = StartVerification(database.Path, time);

        var response = await host.Client.PostAsync("/v1/traces", JsonContent("{\"resourceSpans\":["));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(responseJson.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("invalid_payload", responseJson.RootElement.GetProperty("error").GetString());
        Assert.False(responseJson.RootElement.TryGetProperty("rawRecordId", out _));
        Assert.Equal(0, CountRawRecords(database.Path));
        var rejectedObservation = Assert.Single(
            new SqliteSourceCompatibilityStore(database.Path).List(after: null, limit: 10));
        Assert.Equal(SourceCompatibilityState.AdapterFailure, rejectedObservation.CompatibilityState);
        Assert.Null(rejectedObservation.RawRecordId);
        Assert.Null(rejectedObservation.SourceSurface);
        Assert.Null(rejectedObservation.SourceAdapter);

        var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
            database.Path,
            time,
            new GitHubCopilotDoctorEvidenceSelection(
                verification.VerificationId,
                "vscode",
                RawRecordId: 1,
                NativeSession: null));

        Assert.Equal(DoctorResultCode.VerificationActive, observed.ObservationResult.Code);
        Assert.Empty(observed.ObservedKinds);
        Assert.Empty(observed.EvidenceRefs);
        Assert.False(observed.SessionUnbound);
        Assert.Equal(LastIngestOutcome.Unknown, observed.Snapshot.LastIngest!.Outcome);
        Assert.Equal(RawPersistenceOutcome.Unknown, observed.Snapshot.RawPersistence!.Outcome);
        Assert.Equal(ProjectionOutcome.Unknown, observed.Snapshot.Projection!.Outcome);
        Assert.Equal(ExactSessionBindingRequirement.Unknown, observed.Snapshot.ExactSessionBinding!.Requirement);
        Assert.Equal(ExactSessionBindingOutcome.Unknown, observed.Snapshot.ExactSessionBinding.Outcome);
        Assert.Equal(DoctorCompleteness.Unknown, observed.Snapshot.CompletenessAndContent!.Completeness);
        Assert.Equal(ContentCaptureStatus.Unknown, observed.Snapshot.CompletenessAndContent.ContentCapture);
        Assert.Equal(RawAccessStatus.Unknown, observed.Snapshot.CompletenessAndContent.RawAccess);
        Assert.Equal(0, CountCandidates(database.Path, verification.VerificationId));
    }

    private static DoctorVerification StartVerification(string databasePath, TimeProvider timeProvider)
    {
        var service = SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(databasePath, timeProvider));
        return Assert.IsType<DoctorVerification>(
            service.Start("github-copilot-vscode", "github-copilot-doctor", Now.AddMinutes(5)).Verification);
    }

    private static async Task<RunningMonitor> StartMonitorAsync(string databasePath, TimeProvider timeProvider)
    {
        var app = MonitorHost.Build(
            new MonitorOptions(
                databasePath,
                "http://127.0.0.1:0",
                SanitizedOnly: false,
                MonitorOptions.DefaultMaxRequestBodyBytes),
            new MonitorHostTestOptions
            {
                TimeProvider = timeProvider,
                StartProjectionWorker = false,
                UseUserSecrets = false,
            });
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .ToArray();
        var address = Assert.Single(addresses);
        return new RunningMonitor(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static int CountCandidates(string databasePath, string verificationId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM doctor_verification_evidence WHERE verification_id = $verification_id;";
        command.Parameters.AddWithValue("$verification_id", verificationId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int CountRawRecords(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_records;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadRawRecordIdentity(string databasePath, long rawRecordId)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CAST(id AS TEXT) || '|' || trace_id || '|' || received_at FROM raw_records WHERE id = $id;";
        command.Parameters.AddWithValue("$id", rawRecordId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static string ValidVscodeTraceJson() =>
        """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}]},"scopeSpans":[{"spans":[{"traceId":"11111111111111111111111111111111","spanId":"2222222222222222","name":"chat"}]}]}]}
        """;

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RunningMonitor(
        Microsoft.AspNetCore.Builder.WebApplication app,
        HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private TemporaryDatabase(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDatabase Create() =>
            new(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"issue103-http-boundary-{Guid.NewGuid():N}.db"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
