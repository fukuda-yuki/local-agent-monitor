using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.Doctor.Tests;

public sealed class DoctorCrossSurfaceContractTests
{
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
}
