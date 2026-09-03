using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public class LocalMonitorScriptTests
{
    private static readonly string[] RequiredScripts =
    [
        "common.ps1",
        "install.ps1",
        "package-release.ps1",
        "first-trace.ps1",
        "setup.ps1",
        "start.ps1",
        "stop.ps1",
        "status.ps1",
        "set-startup-task.ps1",
        "install-user-env.ps1",
        "install-startup-task.ps1",
        "uninstall-user-env.ps1",
        "uninstall-startup-task.ps1",
        "install-session-hooks.ps1",
        "uninstall-session-hooks.ps1",
    ];

    private static readonly string[] RequiredTestScripts =
    [
        "install-playwright-chromium.ps1",
    ];

    private static readonly string[] RequiredWorkflows =
    [
        "local-monitor-release.yml",
    ];

    [Fact]
    public void RequiredScriptsExist()
    {
        foreach (var script in RequiredScripts)
        {
            Assert.True(File.Exists(ScriptPath(script)), $"{script} is missing.");
        }

        Assert.True(File.Exists(ScriptPath("README.md")), "README.md is missing.");
    }

    [Fact]
    public void ScriptsParseSuccessfully()
    {
        foreach (var script in RequiredScripts)
        {
            var result = RunPowerShellParser(ScriptPath(script));

            Assert.True(result.ExitCode == 0, $"{script} failed to parse: {result.Output}{result.Error}");
        }

        foreach (var script in RequiredTestScripts)
        {
            var result = RunPowerShellParser(TestScriptPath(script));

            Assert.True(result.ExitCode == 0, $"{script} failed to parse: {result.Output}{result.Error}");
        }
    }

    [Fact]
    public void RequiredWorkflowsExist()
    {
        foreach (var workflow in RequiredWorkflows)
        {
            Assert.True(File.Exists(WorkflowPath(workflow)), $"{workflow} is missing.");
        }
    }

    [Fact]
    public void PlaywrightBootstrapScriptUsesRepositoryLocalBrowserCache()
    {
        var script = File.ReadAllText(TestScriptPath("install-playwright-chromium.ps1"));

        Assert.Contains("PLAYWRIGHT_BROWSERS_PATH", script, StringComparison.Ordinal);
        Assert.Contains("artifacts", script, StringComparison.Ordinal);
        Assert.Contains("playwright-browsers", script, StringComparison.Ordinal);
        Assert.Contains("playwright.ps1", script, StringComparison.Ordinal);
        Assert.Contains("WithDeps", script, StringComparison.Ordinal);
        Assert.Contains("--with-deps", script, StringComparison.Ordinal);
        Assert.Contains("install", script, StringComparison.Ordinal);
        Assert.Contains("chromium", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CommonScriptDefinesStableDefaults()
    {
        var common = File.ReadAllText(ScriptPath("common.ps1"));

        Assert.Contains("CopilotAgentObservability LocalMonitor", common);
        Assert.Contains("http://127.0.0.1:4320", common);
        Assert.Contains("[Environment]::GetFolderPath('LocalApplicationData')", common);
        Assert.Contains("app", common);
        Assert.Contains("raw-store.db", common);
        Assert.Contains("local-monitor.state.json", common);
        Assert.Contains("CopilotAgentObservability.LocalMonitor.exe", common);
    }

    [Fact]
    public async Task LifecycleScriptsShareExplicitRuntimeRootWithoutChangingNormalUserRuntimeFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimeRoot = CreateTemporaryDirectory("cao-runtime-root-tests");
        var normalRuntimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopilotAgentObservability",
            "LocalMonitor");
        var normalRuntimeFingerprint = RuntimeFileFingerprint(normalRuntimeRoot);
        Process? firstWrapper = null;
        Process? firstChild = null;
        Process? secondWrapper = null;
        Process? secondChild = null;
        try
        {
            var port = GetFreeTcpPort();
            var url = $"http://127.0.0.1:{port}";
            var start = StartPowerShellScript(
                ScriptPath("start.ps1"),
                "-RuntimeRoot", runtimeRoot,
                "-Url", url,
                "-Mode", "DotnetRun",
                "-NoBrowser",
                "-WaitReady",
                "-TimeoutSeconds", "60");
            firstWrapper = start.Process;

            await WaitForRuntimeReady(runtimeRoot, url, TimeSpan.FromSeconds(60));
            firstChild = CaptureOwnedRuntimeProcess(runtimeRoot, firstWrapper, url);
            Assert.True(Directory.Exists(Path.Combine(runtimeRoot, "logs")), "Start did not use the disposable log root.");
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(runtimeRoot, "logs"), "wrapper-*.log"));
            Assert.True(File.Exists(Path.Combine(runtimeRoot, "local-monitor.state.json")));
            Assert.True(File.Exists(Path.Combine(runtimeRoot, "local-monitor.pid")));

            var status = RunPowerShellScript(ScriptPath("status.ps1"), "-RuntimeRoot", runtimeRoot);

            Assert.True(status.ExitCode == 0, status.Output + status.Error);
            Assert.Contains($"DB path: {Path.Combine(runtimeRoot, "raw-store.db")}", status.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"log path: {Path.Combine(runtimeRoot, "logs")}", status.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"install root: {Path.Combine(runtimeRoot, "app")}", status.Output, StringComparison.OrdinalIgnoreCase);

            var stop = RunPowerShellScript(ScriptPath("stop.ps1"), "-RuntimeRoot", runtimeRoot, "-Force");

            Assert.Equal(0, stop.ExitCode);
            Assert.Equal("stopped\n", stop.Output.Replace("\r\n", "\n", StringComparison.Ordinal));
            var firstStart = await CompletePowerShellScript(start, TimeSpan.FromSeconds(15));
            Assert.Equal(0, firstStart.ExitCode);
            Assert.StartsWith("started", firstStart.Output, StringComparison.Ordinal);
            Assert.Empty(firstStart.Error);
            Assert.False(File.Exists(Path.Combine(runtimeRoot, "local-monitor.state.json")));
            Assert.False(File.Exists(Path.Combine(runtimeRoot, "local-monitor.pid")));

            var restart = StartPowerShellScript(
                ScriptPath("start.ps1"),
                "-RuntimeRoot", runtimeRoot,
                "-Url", url,
                "-Mode", "DotnetRun",
                "-NoBrowser",
                "-WaitReady",
                "-TimeoutSeconds", "60");
            secondWrapper = restart.Process;
            await WaitForRuntimeReady(runtimeRoot, url, TimeSpan.FromSeconds(60));
            secondChild = CaptureOwnedRuntimeProcess(runtimeRoot, secondWrapper, url);
            var finalStop = RunPowerShellScript(ScriptPath("stop.ps1"), "-RuntimeRoot", runtimeRoot, "-Force");
            Assert.Equal(0, finalStop.ExitCode);
            var secondStart = await CompletePowerShellScript(restart, TimeSpan.FromSeconds(15));
            Assert.Equal(0, secondStart.ExitCode);
            Assert.StartsWith("started", secondStart.Output, StringComparison.Ordinal);
            Assert.Empty(secondStart.Error);
            Assert.Equal(normalRuntimeFingerprint, RuntimeFileFingerprint(normalRuntimeRoot));
        }
        finally
        {
            KillKnownProcessTree(secondChild);
            KillKnownProcessTree(secondWrapper);
            KillKnownProcessTree(firstChild);
            KillKnownProcessTree(firstWrapper);
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitRuntimeRootRejectsDifferentRequestedIdentityBeforeSideEffects()
    {
        if (!OperatingSystem.IsWindows()) return;

        var runtimeRoot = CreateTemporaryDirectory("cao-runtime-identity-tests");
        Process? wrapperA = null;
        Process? monitorA = null;
        Process? wrapperB = null;
        Process? monitorB = null;
        try
        {
            var portA = GetFreeTcpPort();
            var portB = GetFreeTcpPort();
            while (portB == portA) portB = GetFreeTcpPort();
            var urlA = $"http://127.0.0.1:{portA}";
            var urlB = $"http://127.0.0.1:{portB}";
            var dbA = Path.Combine(runtimeRoot, "a", "monitor.db");
            var dbB = Path.Combine(runtimeRoot, "b", "monitor.db");
            var startA = StartPowerShellScript(
                ScriptPath("start.ps1"), "-RuntimeRoot", runtimeRoot, "-Url", urlA,
                "-DbPath", dbA, "-Mode", "DotnetRun", "-NoBrowser", "-WaitReady", "-TimeoutSeconds", "60");
            wrapperA = startA.Process;
            await WaitForRuntimeReady(runtimeRoot, urlA, TimeSpan.FromSeconds(60));
            monitorA = CaptureOwnedRuntimeProcess(runtimeRoot, wrapperA, urlA, dbA);
            var stateBefore = File.ReadAllBytes(Path.Combine(runtimeRoot, "local-monitor.state.json"));
            var pidBefore = File.ReadAllBytes(Path.Combine(runtimeRoot, "local-monitor.pid"));
            var runtimeBefore = RuntimeFileFingerprint(runtimeRoot);

            var startB = StartPowerShellScript(
                ScriptPath("start.ps1"), "-RuntimeRoot", runtimeRoot, "-Url", urlB,
                "-DbPath", dbB, "-Mode", "DotnetRun", "-NoBrowser", "-WaitReady", "-TimeoutSeconds", "60");
            wrapperB = startB.Process;
            var exited = await WaitForExit(startB.Process, TimeSpan.FromSeconds(10));
            if (!exited)
            {
                await WaitForRuntimeReady(runtimeRoot, urlB, TimeSpan.FromSeconds(60));
                await WaitForRuntimeProcessChange(runtimeRoot, monitorA.Id, TimeSpan.FromSeconds(15));
                monitorB = CaptureOwnedRuntimeProcess(runtimeRoot, wrapperB, urlB, dbB);
            }

            Assert.True(exited, "The conflicting start launched a second monitor instead of failing closed.");
            var resultB = await CompletePowerShellScript(startB, TimeSpan.FromSeconds(15));
            Assert.Equal(1, resultB.ExitCode);
            Assert.Contains("runtime_state_mismatch", resultB.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(dbB));
            Assert.False(Directory.Exists(Path.GetDirectoryName(dbB)!));
            Assert.False(await CanConnect(portB));
            Assert.False(monitorA.HasExited);
            Assert.True(await CanConnect(portA));
            Assert.Equal(stateBefore, File.ReadAllBytes(Path.Combine(runtimeRoot, "local-monitor.state.json")));
            Assert.Equal(pidBefore, File.ReadAllBytes(Path.Combine(runtimeRoot, "local-monitor.pid")));
            Assert.Equal(runtimeBefore, RuntimeFileFingerprint(runtimeRoot));
        }
        finally
        {
            KillKnownProcessTree(monitorB);
            KillKnownProcessTree(wrapperB);
            if (monitorA is not null && !monitorA.HasExited)
                RunPowerShellScript(ScriptPath("stop.ps1"), "-RuntimeRoot", runtimeRoot, "-Force");
            KillKnownProcessTree(monitorA);
            KillKnownProcessTree(wrapperA);
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public void LifecycleScriptsPreserveExistingPositionalParameterBindings()
    {
        var start = RunPowerShellScript(ScriptPath("start.ps1"), "https://example.invalid");
        Assert.Equal(1, start.ExitCode);
        Assert.Contains("non_loopback_url", start.Error, StringComparison.Ordinal);

        var taskName = $"Issue233 Missing Task {Guid.NewGuid():N}";
        var status = RunPowerShellScript(ScriptPath("status.ps1"), taskName);
        Assert.Contains($"task name: {taskName}", status.Output, StringComparison.Ordinal);

        var stop = RunPowerShellScript(ScriptPath("stop.ps1"), "not-an-integer");
        Assert.NotEqual(0, stop.ExitCode);
        Assert.Contains("TimeoutSeconds", stop.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status.ps1")]
    [InlineData("stop.ps1")]
    public void ExplicitRuntimeRootRejectsMalformedStateWithoutDeletingIt(string script)
    {
        var runtimeRoot = CreateTemporaryDirectory("cao-runtime-root-malformed");
        var statePath = Path.Combine(runtimeRoot, "local-monitor.state.json");
        var pidPath = Path.Combine(runtimeRoot, "local-monitor.pid");
        try
        {
            File.WriteAllText(statePath, "{");
            File.WriteAllText(pidPath, "123");

            var result = RunPowerShellScript(ScriptPath(script), "-RuntimeRoot", runtimeRoot);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Contains("runtime_state_mismatch", result.Error, StringComparison.Ordinal);
            Assert.True(File.Exists(statePath));
            Assert.True(File.Exists(pidPath));
        }
        finally
        {
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("status.ps1")]
    [InlineData("stop.ps1")]
    public void ExplicitRuntimeRootRejectsPidMismatchWithoutKillingOrDeleting(string script)
    {
        var runtimeRoot = CreateTemporaryDirectory("cao-runtime-root-pid-mismatch");
        var statePath = Path.Combine(runtimeRoot, "local-monitor.state.json");
        var pidPath = Path.Combine(runtimeRoot, "local-monitor.pid");
        try
        {
            var state = new
            {
                process_id = Environment.ProcessId,
                url = "http://127.0.0.1:4320",
                db_path = Path.Combine(runtimeRoot, "raw-store.db"),
                mode = "dotnet-run",
                repo_root = RepositoryRoot,
                install_root = Path.Combine(runtimeRoot, "app"),
                executable_path = "dotnet",
            };
            File.WriteAllText(statePath, JsonSerializer.Serialize(state));
            File.WriteAllText(pidPath, (Environment.ProcessId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

            var result = RunPowerShellScript(ScriptPath(script), "-RuntimeRoot", runtimeRoot);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Contains("runtime_state_mismatch", result.Error, StringComparison.Ordinal);
            Assert.False(Process.GetCurrentProcess().HasExited);
            Assert.True(File.Exists(statePath));
            Assert.True(File.Exists(pidPath));
        }
        finally
        {
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("status.ps1")]
    [InlineData("stop.ps1")]
    public void ExplicitRuntimeRootRejectsStatePidPresenceAsymmetry(string script)
    {
        var runtimeRoot = CreateTemporaryDirectory("cao-runtime-root-asymmetry");
        try
        {
            File.WriteAllText(Path.Combine(runtimeRoot, "local-monitor.pid"), "123");
            var result = RunPowerShellScript(ScriptPath(script), "-RuntimeRoot", runtimeRoot);

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Contains("runtime_state_mismatch", result.Error, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(runtimeRoot, "local-monitor.pid")));
        }
        finally
        {
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public void ExplicitRuntimeRootRejectsForeignSuperficialProcessWithoutKillingOrDeleting()
    {
        var runtimeRoot = CreateTemporaryDirectory("cao-runtime-root-foreign-process");
        using var foreign = Process.Start(new ProcessStartInfo
        {
            FileName = PowerShellExecutablePath(),
            ArgumentList = { "-NoProfile", "-Command", "$name='CopilotAgentObservability.LocalMonitor'; Start-Sleep -Seconds 300" },
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start foreign test process.");
        try
        {
            WriteRuntimeState(runtimeRoot, foreign.Id, "http://127.0.0.1:4320");
            foreach (var script in new[] { "start.ps1", "status.ps1", "stop.ps1" })
            {
                var arguments = new List<string> { "-RuntimeRoot", runtimeRoot };
                if (script == "start.ps1")
                {
                    arguments.AddRange(["-Mode", "DotnetRun", "-TimeoutSeconds", "1"]);
                }
                var result = RunPowerShellScript(ScriptPath(script), [.. arguments]);
                Assert.Equal(1, result.ExitCode);
                Assert.Contains("runtime_state_mismatch", result.Error, StringComparison.Ordinal);
                Assert.False(foreign.HasExited);
                Assert.True(File.Exists(Path.Combine(runtimeRoot, "local-monitor.state.json")));
                Assert.True(File.Exists(Path.Combine(runtimeRoot, "local-monitor.pid")));
            }
        }
        finally
        {
            if (!foreign.HasExited)
            {
                foreign.Kill(entireProcessTree: true);
                foreign.WaitForExit(10_000);
            }
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitRuntimeRootStatusDoesNotProbeStaleStateUrl()
    {
        var runtimeRoot = CreateTemporaryDirectory("cao-runtime-root-stale");
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(url + "/");
        listener.Start();
        var requests = 0;
        using var cancellation = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellation.Token);
                Interlocked.Increment(ref requests);
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
            catch (OperationCanceledException)
            {
            }
        });
        try
        {
            WriteRuntimeState(runtimeRoot, int.MaxValue, url);
            var result = RunPowerShellScript(ScriptPath("status.ps1"), "-RuntimeRoot", runtimeRoot);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("running: no", result.Output, StringComparison.Ordinal);
            Assert.Contains("health/live HTTP status: unreachable", result.Output, StringComparison.Ordinal);
            Assert.Equal(0, Volatile.Read(ref requests));
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
            try { await server; } catch (System.Net.HttpListenerException) { }
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public void ExplicitRuntimeRootStatusDoesNotProbeDefaultUrlWhenStateIsAbsent()
    {
        var runtimeRoot = CreateTemporaryDirectory("cao-runtime-root-absent");
        try
        {
            var result = RunPowerShellScript(ScriptPath("status.ps1"), "-RuntimeRoot", runtimeRoot);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("running: no", result.Output, StringComparison.Ordinal);
            Assert.Contains("health/live HTTP status: unreachable", result.Output, StringComparison.Ordinal);
            Assert.Contains("health/ready HTTP status: unreachable", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitRuntimeRootStartRejectsForeignHealthyEndpointWithoutTouchingIt()
    {
        var runtimeRoot = CreateTemporaryDirectory("cao-runtime-root-foreign");
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(url + "/");
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        var server = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    var context = await listener.GetContextAsync().WaitAsync(cancellation.Token);
                    Interlocked.Increment(ref requests);
                    var body = Encoding.UTF8.GetBytes("{\"status\":\"ready\"}");
                    context.Response.StatusCode = 200;
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body, cancellation.Token);
                    context.Response.Close();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, cancellation.Token);
        try
        {
            var result = RunPowerShellScript(
                ScriptPath("start.ps1"),
                "-RuntimeRoot", runtimeRoot,
                "-Url", url,
                "-Mode", "DotnetRun",
                "-NoBrowser",
                "-WaitReady",
                "-TimeoutSeconds", "5");

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Contains("runtime_state_mismatch", result.Error, StringComparison.Ordinal);
            Assert.True(Volatile.Read(ref requests) > 0);
            Assert.True(listener.IsListening, "The foreign endpoint was stopped.");
            Assert.False(File.Exists(Path.Combine(runtimeRoot, "local-monitor.state.json")));
            Assert.False(File.Exists(Path.Combine(runtimeRoot, "local-monitor.pid")));
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
            try { await server; } catch (System.Net.HttpListenerException) { }
            Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("start.ps1", "")]
    [InlineData("stop.ps1", "   ")]
    [InlineData("status.ps1", "relative-root")]
    public void LifecycleScriptsRejectInvalidExplicitRuntimeRootWithoutDisclosingIt(string script, string runtimeRoot)
    {
        var arguments = new List<string> { "-RuntimeRoot", runtimeRoot };
        if (script == "start.ps1")
        {
            arguments.AddRange(["-InstallRoot", Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cao-runtime-root-invalid-app"))]);
        }
        var result = RunPowerShellScript(ScriptPath(script), [.. arguments]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("runtime_root_invalid", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("relative-root", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageReleaseScriptDefinesSelfContainedWindowsZipLayout()
    {
        var package = File.ReadAllText(ScriptPath("package-release.ps1"));

        Assert.Contains("win-x64", package, StringComparison.Ordinal);
        Assert.Contains("SelfContained", package, StringComparison.Ordinal);
        Assert.Contains("PublishSingleFile=false", package, StringComparison.Ordinal);
        Assert.Contains("local-monitor-win-x64.zip", package, StringComparison.Ordinal);
        Assert.Contains("manifest.json", package, StringComparison.Ordinal);
        Assert.Contains("app", package, StringComparison.Ordinal);
        Assert.Contains("scripts", package, StringComparison.Ordinal);
        Assert.Contains("install-user-env.ps1", package, StringComparison.Ordinal);
        Assert.Contains("uninstall-user-env.ps1", package, StringComparison.Ordinal);
        Assert.Contains("install-session-hooks.ps1", package, StringComparison.Ordinal);
        Assert.Contains("uninstall-session-hooks.ps1", package, StringComparison.Ordinal);
        Assert.Contains("first-trace.ps1", package, StringComparison.Ordinal);
        Assert.Contains("'start.ps1'", package, StringComparison.Ordinal);
        Assert.Contains("'stop.ps1'", package, StringComparison.Ordinal);
        Assert.Contains("scripts\\local-monitor\\README.md", package, StringComparison.Ordinal);
        Assert.Contains("Compress-Archive", package, StringComparison.Ordinal);
        Assert.Contains("$LASTEXITCODE", package, StringComparison.Ordinal);
        Assert.Contains("dotnet_publish_failed", package, StringComparison.Ordinal);
        Assert.Contains("Join-Path $OutputDirectory 'artifacts'", package, StringComparison.Ordinal);
        Assert.Equal(2, package.Split("--artifacts-path $artifactsDirectory", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, package.Split("--disable-build-servers", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("scripts", "local-monitor", "README.md")]
    [InlineData("docs", "user-guide", "local-monitor.md")]
    public void RuntimeRestoreDocumentationUsesPackagedConditionalRestartSequence(
        string directory,
        string subdirectory,
        string fileName)
    {
        var documentation = File.ReadAllText(Path.Combine(RepositoryRoot, directory, subdirectory, fileName))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("app\\config-cli\\CopilotAgentObservability.ConfigCli.exe", documentation, StringComparison.Ordinal);
        Assert.Contains("Mode = 'Published'", documentation, StringComparison.Ordinal);
        Assert.Contains("Url = $monitorUrl", documentation, StringComparison.Ordinal);
        Assert.Contains("DbPath = $db", documentation, StringComparison.Ordinal);
        Assert.Contains("InstallRoot = $installRoot", documentation, StringComparison.Ordinal);
        Assert.Contains("SanitizedOnly = $sanitizedOnly", documentation, StringComparison.Ordinal);
        Assert.Contains("WaitReady = $true", documentation, StringComparison.Ordinal);
        Assert.Contains(
            "& $stopScript -Force\n" +
            "$stopExitCode = $LASTEXITCODE\n" +
            "if ($stopExitCode -ne 0) {\n" +
            "    exit $stopExitCode\n" +
            "}",
            documentation,
            StringComparison.Ordinal);
        Assert.Contains(
            "& $cli runtime-backup restore --bundle C:\\private\\local-monitor-backup.zip --database $db\n" +
            "$restoreExitCode = $LASTEXITCODE\n" +
            "if ($restoreExitCode -ne 0) {\n" +
            "    exit $restoreExitCode\n" +
            "}",
            documentation,
            StringComparison.Ordinal);
        Assert.Contains(
            "& $startScript @startParameters\n" +
            "$startExitCode = $LASTEXITCODE\n" +
            "if ($startExitCode -ne 0) {\n" +
            "    exit $startExitCode\n" +
            "}",
            documentation,
            StringComparison.Ordinal);

        var stopIndex = documentation.IndexOf("& $stopScript -Force", StringComparison.Ordinal);
        var stopExitCaptureIndex = documentation.IndexOf("$stopExitCode = $LASTEXITCODE", StringComparison.Ordinal);
        var stopGuardIndex = documentation.IndexOf("if ($stopExitCode -ne 0)", StringComparison.Ordinal);
        var stopExitIndex = documentation.IndexOf("exit $stopExitCode", StringComparison.Ordinal);
        var restoreIndex = documentation.IndexOf("& $cli runtime-backup restore", StringComparison.Ordinal);
        var restoreExitCaptureIndex = documentation.IndexOf("$restoreExitCode = $LASTEXITCODE", StringComparison.Ordinal);
        var restoreGuardIndex = documentation.IndexOf("if ($restoreExitCode -ne 0)", StringComparison.Ordinal);
        var restoreExitIndex = documentation.IndexOf("exit $restoreExitCode", StringComparison.Ordinal);
        var startIndex = documentation.IndexOf("& $startScript @startParameters", StringComparison.Ordinal);

        Assert.True(stopIndex >= 0, "The documented sequence must stop Local Monitor first.");
        Assert.True(stopIndex < stopExitCaptureIndex && stopExitCaptureIndex < stopGuardIndex && stopGuardIndex < stopExitIndex);
        Assert.True(stopExitIndex < restoreIndex, "Restore must not run when stop fails.");
        Assert.True(restoreIndex < restoreExitCaptureIndex && restoreExitCaptureIndex < restoreGuardIndex);
        Assert.True(restoreGuardIndex < restoreExitIndex && restoreExitIndex < startIndex, "Published restart must occur only after restore exit 0.");
    }

    [Fact]
    public void FirstTraceWrapperUsesRuntimeDatabaseAndPreservesPackagedCliTransport()
    {
        var wrapper = File.ReadAllText(ScriptPath("first-trace.ps1"));

        Assert.Contains("common.ps1", wrapper, StringComparison.Ordinal);
        Assert.Contains("$script:DefaultDbPath", wrapper, StringComparison.Ordinal);
        Assert.Contains("CopilotAgentObservability.ConfigCli.exe", wrapper, StringComparison.Ordinal);
        Assert.Contains("@('first-trace')", wrapper, StringComparison.Ordinal);
        Assert.Contains("'--database'", wrapper, StringComparison.Ordinal);
        Assert.Contains("$LASTEXITCODE", wrapper, StringComparison.Ordinal);
        Assert.Contains("internal_error", wrapper, StringComparison.Ordinal);
        Assert.Contains("runtime_database_not_found", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet", wrapper, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'begin', 'status', 'complete', 'cancel'", wrapper, StringComparison.Ordinal);
        Assert.Contains("$_ -eq '--database'", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("[string] $DatabasePath", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-LocalMonitorLog", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstTraceWrapperRejectsCallerDatabaseWithoutDisclosingItsValue()
    {
        var callerDatabase = Path.Combine(Path.GetTempPath(), "ISSUE105_PRIVATE_DATABASE", "raw-store.db");

        var result = RunPowerShellScript(
            ScriptPath("first-trace.ps1"),
            "status",
            "--verification-id",
            "01999999-9999-7999-8999-999999999999",
            "--database",
            callerDatabase,
            "--json");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Equal("invalid_arguments\n", result.Error.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain(callerDatabase, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ISSUE105_PRIVATE_DATABASE", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstTraceWrapperFailsClosedWhenRuntimeDatabaseIsMissing()
    {
        var root = CreateTemporaryDirectory("cao-first-trace-missing-database");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var wrapper = Path.Combine(scripts, "first-trace.ps1");
            File.Copy(ScriptPath("first-trace.ps1"), wrapper);
            File.WriteAllText(
                Path.Combine(scripts, "common.ps1"),
                "$script:DefaultDbPath = Join-Path '" + root.Replace("'", "''", StringComparison.Ordinal) + "' 'ISSUE105_PRIVATE_DATABASE\\raw-store.db'\n");

            var result = RunPowerShellScript(
                wrapper,
                "status",
                "--verification-id",
                "01999999-9999-7999-8999-999999999999",
                "--json");

            Assert.Equal(5, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Equal("runtime_database_not_found\n", result.Error.Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.DoesNotContain(root, result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ISSUE105_PRIVATE_DATABASE", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReleasePackageContainsSelfContainedConfigCliAndSetupWrapperPreservesInvalidArgumentParityWithoutDotnet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory("cao-release-tests");
        try
        {
            var outputDirectory = Path.Combine(root, "release");
            var package = RunBoundedProcess(
                PowerShellExecutablePath(),
                [
                    "-NoProfile",
                    "-File",
                    ScriptPath("package-release.ps1"),
                    "-OutputDirectory",
                    outputDirectory,
                    "-Version",
                    "0.0.0-test",
                ],
                environment: null,
                timeout: TimeSpan.FromMinutes(10));

            Assert.True(package.ExitCode == 0, $"Package failed with exit code {package.ExitCode}: {package.StandardOutputText}{package.StandardErrorText}");

            var staging = Path.Combine(outputDirectory, "staging");
            var packagedSetup = Path.Combine(staging, "scripts", "setup.ps1");
            var packagedFirstTrace = Path.Combine(staging, "scripts", "first-trace.ps1");
            var packagedStart = Path.Combine(staging, "scripts", "start.ps1");
            var packagedStop = Path.Combine(staging, "scripts", "stop.ps1");
            var packagedCli = Path.Combine(staging, "app", "config-cli", "CopilotAgentObservability.ConfigCli.exe");
            Assert.True(File.Exists(packagedSetup), "The release layout is missing scripts/setup.ps1.");
            Assert.True(File.Exists(packagedFirstTrace), "The release layout is missing scripts/first-trace.ps1.");
            Assert.True(File.Exists(packagedStart), "The release layout is missing scripts/start.ps1.");
            Assert.True(File.Exists(packagedStop), "The release layout is missing scripts/stop.ps1.");
            Assert.True(File.Exists(Path.Combine(staging, "README.md")), "The release layout is missing its operator README.");
            Assert.True(File.Exists(packagedCli), "The release layout is missing the self-contained Config CLI executable.");
            Assert.True(File.Exists(Path.ChangeExtension(packagedCli, ".runtimeconfig.json")), "The Config CLI runtime configuration is missing.");

            var zipPath = Path.Combine(outputDirectory, "local-monitor-win-x64.zip");
            Assert.True(File.Exists(zipPath), "The release ZIP was not created.");
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                Assert.Contains(archive.Entries, entry => entry.FullName == "scripts/setup.ps1");
                Assert.Contains(archive.Entries, entry => entry.FullName == "scripts/first-trace.ps1");
                Assert.Contains(archive.Entries, entry => entry.FullName == "scripts/start.ps1");
                Assert.Contains(archive.Entries, entry => entry.FullName == "scripts/stop.ps1");
                Assert.Contains(archive.Entries, entry => entry.FullName == "README.md");
                Assert.Contains(archive.Entries, entry => entry.FullName == "app/config-cli/CopilotAgentObservability.ConfigCli.exe");
            }

            var hiddenPath = Directory.CreateDirectory(Path.Combine(root, "path-without-dotnet")).FullName;
            var packagedEnvironment = new Dictionary<string, string?>
            {
                ["PATH"] = hiddenPath,
                ["DOTNET_ROOT"] = null,
                ["DOTNET_HOST_PATH"] = null,
            };

            var releaseFailure = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-File", packagedSetup, "status", "--unexpected"],
                packagedEnvironment,
                TimeSpan.FromMinutes(2));

            Assert.Equal(2, releaseFailure.ExitCode);
            Assert.Equal("invalid_arguments\n", releaseFailure.StandardErrorText);
            using var failureDocument = JsonDocument.Parse(releaseFailure.StandardOutputBytes);
            var failure = failureDocument.RootElement;
            Assert.Equal(
                ["contract_version", "command", "success", "code", "change_set_id", "recovered_change_set_id", "recovery_operation", "adapter", "targets", "change_sets", "warnings", "next_actions", "truncated"],
                failure.EnumerateObject().Select(property => property.Name));
            Assert.Equal("setup.v1", failure.GetProperty("contract_version").GetString());
            Assert.Equal("status", failure.GetProperty("command").GetString());
            Assert.False(failure.GetProperty("success").GetBoolean());
            Assert.Equal("invalid_arguments", failure.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, failure.GetProperty("change_set_id").ValueKind);
            Assert.Equal(JsonValueKind.Null, failure.GetProperty("recovered_change_set_id").ValueKind);
            Assert.Equal(JsonValueKind.Null, failure.GetProperty("recovery_operation").ValueKind);
            Assert.Equal(JsonValueKind.Null, failure.GetProperty("adapter").ValueKind);
            Assert.Empty(failure.GetProperty("targets").EnumerateArray());
            Assert.Empty(failure.GetProperty("change_sets").EnumerateArray());
            Assert.Empty(failure.GetProperty("warnings").EnumerateArray());
            Assert.Empty(failure.GetProperty("next_actions").EnumerateArray());
            Assert.False(failure.GetProperty("truncated").GetBoolean());

            var privateDatabase = Path.Combine(root, "ISSUE105_PRIVATE_DATABASE", "raw-store.db");
            var firstTraceFailure = RunBoundedProcess(
                PowerShellExecutablePath(),
                [
                    "-NoProfile", "-File", packagedFirstTrace, "status",
                    "--verification-id", "01999999-9999-7999-8999-999999999999",
                    "--database", privateDatabase, "--json",
                ],
                packagedEnvironment,
                TimeSpan.FromMinutes(2));

            Assert.Equal(2, firstTraceFailure.ExitCode);
            Assert.Empty(firstTraceFailure.StandardOutputBytes);
            Assert.Equal("invalid_arguments\n", firstTraceFailure.StandardErrorText);
            Assert.DoesNotContain(privateDatabase, firstTraceFailure.StandardErrorText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ISSUE105_PRIVATE_DATABASE", firstTraceFailure.StandardErrorText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ClaudeSetup_RepositoryAndReleaseWrappersPreserveTransportParityWithoutDotnetAndIsolatedUserState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory("cao-claude-setup-release-tests");
        try
        {
            var outputDirectory = Path.Combine(root, "release");
            var package = RunBoundedProcess(
                PowerShellExecutablePath(),
                [
                    "-NoProfile",
                    "-File",
                    ScriptPath("package-release.ps1"),
                    "-OutputDirectory",
                    outputDirectory,
                    "-Version",
                    "0.0.0-test",
                ],
                environment: null,
                timeout: TimeSpan.FromMinutes(10));

            Assert.True(package.ExitCode == 0, $"Package failed with exit code {package.ExitCode}: {package.StandardOutputText}{package.StandardErrorText}");

            string[] actionArguments =
            [
                "plan",
                "--adapter",
                "claude-code",
                "--target",
                "cli",
                "--endpoint",
                "http://127.0.0.1:4320",
                "--allow-wsl2-routing",
                "--allow-wsl2-routing",
            ];
            var direct = RunBoundedProcess(
                "dotnet",
                [
                    "run",
                    "--verbosity",
                    "quiet",
                    "--project",
                    ConfigCliProjectPath,
                    "--",
                    "setup",
                    .. actionArguments,
                ],
                environment: null,
                timeout: TimeSpan.FromMinutes(2));
            var repositorySetup = ScriptPath("setup.ps1");
            var repository = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-File", repositorySetup, .. actionArguments],
                environment: null,
                timeout: TimeSpan.FromMinutes(2));

            var zipPath = Path.Combine(outputDirectory, "local-monitor-win-x64.zip");
            var extractedRelease = Path.Combine(root, "extracted-release");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractedRelease);
            var extractedReleaseBefore = SnapshotPackageTree(extractedRelease);
            Assert.NotEmpty(extractedReleaseBefore);
            var packagedSetup = Path.Combine(extractedRelease, "scripts", "setup.ps1");
            var hiddenPath = Directory.CreateDirectory(Path.Combine(root, "path-without-dotnet")).FullName;
            var release = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-File", packagedSetup, .. actionArguments],
                new Dictionary<string, string?>
                {
                    ["PATH"] = hiddenPath,
                    ["DOTNET_ROOT"] = null,
                    ["DOTNET_HOST_PATH"] = null,
                },
                TimeSpan.FromMinutes(2));

            Assert.Equal(2, direct.ExitCode);
            Assert.Equal(Encoding.UTF8.GetBytes("invalid_arguments\n"), direct.StandardErrorBytes);
            Assert.Equal(direct.ExitCode, repository.ExitCode);
            Assert.Equal(direct.StandardOutputBytes, repository.StandardOutputBytes);
            Assert.Equal(direct.StandardErrorBytes, repository.StandardErrorBytes);
            Assert.Equal(direct.ExitCode, release.ExitCode);
            Assert.Equal(direct.StandardOutputBytes, release.StandardOutputBytes);
            Assert.Equal(direct.StandardErrorBytes, release.StandardErrorBytes);
            using var document = JsonDocument.Parse(release.StandardOutputBytes);
            var result = document.RootElement;
            Assert.Equal("setup.v1", result.GetProperty("contract_version").GetString());
            Assert.Equal("invalid_arguments", result.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("adapter").ValueKind);

            var isolatedUser = Directory.CreateDirectory(Path.Combine(root, "isolated-user")).FullName;
            var isolatedLocalAppData = Directory.CreateDirectory(Path.Combine(isolatedUser, "local-app-data")).FullName;
            var isolatedAppData = Directory.CreateDirectory(Path.Combine(isolatedUser, "app-data")).FullName;
            var isolatedClaudeConfig = Directory.CreateDirectory(Path.Combine(isolatedUser, ".claude")).FullName;
            var dotnetRoot = Path.GetDirectoryName(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")) ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            Assert.True(File.Exists(Path.Combine(dotnetRoot, "dotnet.exe")), "The isolated repository PATH is missing dotnet.exe.");
            var isolatedEnvironment = new Dictionary<string, string?>
            {
                ["HOME"] = isolatedUser,
                ["USERPROFILE"] = isolatedUser,
                ["LOCALAPPDATA"] = isolatedLocalAppData,
                ["APPDATA"] = isolatedAppData,
                ["CLAUDE_CONFIG_DIR"] = isolatedClaudeConfig,
                ["OTEL_EXPORTER_OTLP_HEADERS"] = "authorization=ISSUE68_PHYSICAL_SECRET_MARKER",
                ["DOTNET_ROOT"] = dotnetRoot,
                ["DOTNET_HOST_PATH"] = null,
                ["PATH"] = dotnetRoot,
            };
            string[] validPlanArguments =
            [
                "plan",
                "--adapter",
                "claude-code",
                "--target",
                "cli",
                "--endpoint",
                "http://127.0.0.1:43199",
            ];
            var isolatedRepository = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-File", repositorySetup, .. validPlanArguments],
                isolatedEnvironment,
                TimeSpan.FromMinutes(2));
            isolatedEnvironment["PATH"] = hiddenPath;
            isolatedEnvironment["DOTNET_ROOT"] = null;
            var isolatedRelease = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-File", packagedSetup, .. validPlanArguments],
                isolatedEnvironment,
                TimeSpan.FromMinutes(2));
            var extractedReleaseAfter = SnapshotPackageTree(extractedRelease);

            Assert.Equal(4, isolatedRepository.ExitCode);
            Assert.Equal(Encoding.UTF8.GetBytes("target_not_installed\n"), isolatedRepository.StandardErrorBytes);
            Assert.Equal(isolatedRepository.ExitCode, isolatedRelease.ExitCode);
            Assert.Equal(isolatedRepository.StandardOutputBytes, isolatedRelease.StandardOutputBytes);
            Assert.Equal(isolatedRepository.StandardErrorBytes, isolatedRelease.StandardErrorBytes);
            using var validDocument = JsonDocument.Parse(isolatedRelease.StandardOutputBytes);
            var validResult = validDocument.RootElement;
            Assert.Equal("setup.v1", validResult.GetProperty("contract_version").GetString());
            Assert.Equal("plan", validResult.GetProperty("command").GetString());
            Assert.False(validResult.GetProperty("success").GetBoolean());
            Assert.Equal("target_not_installed", validResult.GetProperty("code").GetString());
            Assert.Equal("claude-code", validResult.GetProperty("adapter").GetString());
            Assert.DoesNotContain("ISSUE68_PHYSICAL_SECRET_MARKER", isolatedRelease.StandardOutputText, StringComparison.Ordinal);
            Assert.DoesNotContain("ISSUE68_PHYSICAL_SECRET_MARKER", isolatedRelease.StandardErrorText, StringComparison.Ordinal);
            Assert.DoesNotContain(isolatedUser, isolatedRelease.StandardOutputText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(isolatedUser, isolatedRelease.StandardErrorText, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(isolatedClaudeConfig, "*", SearchOption.AllDirectories));
            var isolatedSetupRoot = Path.Combine(
                isolatedLocalAppData,
                "CopilotAgentObservability",
                "LocalMonitor",
                "setup");
            if (Directory.Exists(isolatedSetupRoot))
            {
                Assert.Empty(Directory.EnumerateFiles(isolatedSetupRoot, "*", SearchOption.AllDirectories));
            }

            Assert.Equal(extractedReleaseBefore, extractedReleaseAfter);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackagedSetupFailsClosedWhenConfigCliExecutableIsNotAFile(bool createDirectoryAtExecutablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory("cao-incomplete-release-tests");
        try
        {
            var scriptsDirectory = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var configCliDirectory = Directory.CreateDirectory(Path.Combine(root, "app", "config-cli")).FullName;
            var packagedSetup = Path.Combine(scriptsDirectory, "setup.ps1");
            File.Copy(ScriptPath("setup.ps1"), packagedSetup);
            if (createDirectoryAtExecutablePath)
            {
                Directory.CreateDirectory(Path.Combine(configCliDirectory, "CopilotAgentObservability.ConfigCli.exe"));
            }

            var result = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-File", packagedSetup, "status"],
                environment: null,
                timeout: TimeSpan.FromMinutes(1));

            Assert.Equal(5, result.ExitCode);
            Assert.Empty(result.StandardOutputBytes);
            Assert.Equal(Encoding.UTF8.GetBytes("internal_error\n"), result.StandardErrorBytes);
            Assert.DoesNotContain(root, result.StandardErrorText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PowerShell", result.StandardErrorText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PackagedSetupFailsClosedWhenConfigCliExecutableCannotStart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory("cao-invalid-release-tests");
        try
        {
            var scriptsDirectory = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var configCliDirectory = Directory.CreateDirectory(Path.Combine(root, "app", "config-cli")).FullName;
            var packagedSetup = Path.Combine(scriptsDirectory, "setup.ps1");
            File.Copy(ScriptPath("setup.ps1"), packagedSetup);
            File.WriteAllText(Path.Combine(configCliDirectory, "CopilotAgentObservability.ConfigCli.exe"), "not-an-executable");

            var result = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-File", packagedSetup, "status"],
                environment: null,
                timeout: TimeSpan.FromMinutes(1));

            Assert.Equal(5, result.ExitCode);
            Assert.Empty(result.StandardOutputBytes);
            Assert.Equal(Encoding.UTF8.GetBytes("internal_error\n"), result.StandardErrorBytes);
            Assert.DoesNotContain(root, result.StandardErrorText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PowerShell", result.StandardErrorText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InstallScriptCopiesAppWithoutRegisteringStartupOrStartingProcess()
    {
        var install = File.ReadAllText(ScriptPath("install.ps1"));

        Assert.Contains("InstallRoot", install, StringComparison.Ordinal);
        Assert.Contains("SourceRoot", install, StringComparison.Ordinal);
        Assert.Contains("Get-LocalMonitorDefaultInstallRoot", install, StringComparison.Ordinal);
        Assert.DoesNotContain("Register-ScheduledTask", install, StringComparison.Ordinal);
        Assert.DoesNotContain("New-ScheduledTask", install, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", install, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallScriptUsesUserLogonTaskWithSafeDefaults()
    {
        var install = File.ReadAllText(ScriptPath("install-startup-task.ps1"));

        Assert.Contains("New-ScheduledTaskTrigger -AtLogOn", install);
        Assert.Contains("New-ScheduledTaskPrincipal", install);
        Assert.Contains("-RunLevel Limited", install);
        Assert.DoesNotContain("-RunLevel LeastPrivilege", install);
        Assert.Contains("New-ScheduledTaskSettingsSet", install);
        Assert.Contains("-MultipleInstances IgnoreNew", install);
        Assert.Contains("Register-ScheduledTask", install);
        Assert.Contains("DryRun", install);
    }

    [Fact]
    public void StartupWrappersExposeBoundedPricingRegistryOverrideArrays()
    {
        var start = File.ReadAllText(ScriptPath("start.ps1"));
        var install = File.ReadAllText(ScriptPath("install-startup-task.ps1"));

        Assert.Contains("[string[]] $PricingRegistryOverride", start, StringComparison.Ordinal);
        Assert.Contains("[string[]] $PricingRegistryOverride", install, StringComparison.Ordinal);
        Assert.Contains("pricing_registry_override_count_invalid", start, StringComparison.Ordinal);
        Assert.Contains("pricing_registry_override_count_invalid", install, StringComparison.Ordinal);
        var common = File.ReadAllText(ScriptPath("common.ps1"));
        Assert.Contains("ConvertTo-LocalMonitorWindowsCommandLine", common, StringComparison.Ordinal);
        Assert.Contains("Start-Process", common, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardOutput", common, StringComparison.Ordinal);
    }

    [Fact]
    public void StartWrapperForwardsEachPricingOverrideAsAnOrderedHostFlagValuePair()
    {
        var start = File.ReadAllText(ScriptPath("start.ps1"));

        Assert.Contains("$arguments += '--pricing-registry-override'", start, StringComparison.Ordinal);
        Assert.Contains("$arguments += $override", start, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", start, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupTaskUsesUtf16EncodedPricingOverrideLiteralsWithoutReflectingLocators()
    {
        var root = CreateTemporaryDirectory("cao-startup-override-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var capturePath = Path.Combine(root, "task-action.txt");
            var startCapturePath = Path.Combine(root, "start-overrides.json");
            var locatorOne = @"C:\private registry\one; $(not-a-command).json";
            var locatorTwo = @"C:\private registry\two's value.json";
            File.Copy(ScriptPath("install-startup-task.ps1"), Path.Combine(scripts, "install-startup-task.ps1"));
            File.WriteAllText(
                Path.Combine(scripts, "start.ps1"),
                $$"""
                param([string[]] $PricingRegistryOverride)
                [System.IO.File]::WriteAllText('{{startCapturePath.Replace("'", "''", StringComparison.Ordinal)}}', ($PricingRegistryOverride | ConvertTo-Json -Compress))
                exit 0
                """);
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            File.AppendAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                $script:DefaultDbPath = 'C:\safe\raw-store.db'
                $script:TaskWasRegistered = $false
                function Get-LocalMonitorDefaultInstallRoot { 'C:\safe\app' }
                function Test-LocalMonitorLoopbackUrl { param([string] $Url) $true }
                function Get-LocalMonitorTask {
                    param([string] $TaskName)
                    if ($script:TaskWasRegistered) { [pscustomobject]@{ State = 'Ready' } }
                }
                function Get-LocalMonitorRepoRoot { 'C:\safe\repo' }
                function Get-LocalMonitorPowerShellPath { 'C:\safe\pwsh.exe' }
                function New-ScheduledTaskAction {
                    param([string] $Execute, [string] $Argument, [string] $WorkingDirectory)
                    [System.IO.File]::WriteAllText('{{capturePath.Replace("'", "''", StringComparison.Ordinal)}}', $Argument)
                    [pscustomobject]@{}
                }
                function New-ScheduledTaskTrigger { [pscustomobject]@{} }
                function New-ScheduledTaskPrincipal { [pscustomobject]@{} }
                function New-ScheduledTaskSettingsSet { [pscustomobject]@{} }
                function Register-ScheduledTask { $script:TaskWasRegistered = $true; [pscustomobject]@{} }
                """);

            var installScript = Path.Combine(scripts, "install-startup-task.ps1").Replace("'", "''", StringComparison.Ordinal);
            var result = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-Command", $"& '{installScript}' -StartNow -PricingRegistryOverride @('{locatorOne.Replace("'", "''", StringComparison.Ordinal)}','{locatorTwo.Replace("'", "''", StringComparison.Ordinal)}')"],
                environment: null,
                timeout: TimeSpan.FromMinutes(1));

            Assert.True(result.ExitCode == 0, $"{result.StandardOutputText}{result.StandardErrorText}");
            Assert.DoesNotContain(locatorOne, result.StandardOutputText, StringComparison.Ordinal);
            Assert.DoesNotContain(locatorTwo, result.StandardOutputText, StringComparison.Ordinal);
            Assert.DoesNotContain(locatorOne, result.StandardErrorText, StringComparison.Ordinal);
            Assert.DoesNotContain(locatorTwo, result.StandardErrorText, StringComparison.Ordinal);

            using (var startNowOverrides = JsonDocument.Parse(File.ReadAllText(startCapturePath)))
            {
                Assert.Equal([locatorOne, locatorTwo], startNowOverrides.RootElement.EnumerateArray().Select(item => item.GetString()));
            }
            File.Delete(startCapturePath);

            var action = File.ReadAllText(capturePath);
            Assert.DoesNotContain(locatorOne, action, StringComparison.Ordinal);
            Assert.DoesNotContain(locatorTwo, action, StringComparison.Ordinal);
            var encoded = action.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
            var command = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
            Assert.Contains("-PricingRegistryOverride", command, StringComparison.Ordinal);
            Assert.Contains("@('C:\\private registry\\one; $(not-a-command).json','C:\\private registry\\two''s value.json')", command, StringComparison.Ordinal);
            Assert.Contains("-NoBrowser", command, StringComparison.Ordinal);
            Assert.Contains("-WaitReady", command, StringComparison.Ordinal);

            var decoded = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
                environment: null,
                timeout: TimeSpan.FromMinutes(1));
            Assert.True(decoded.ExitCode == 0, $"{decoded.StandardOutputText}{decoded.StandardErrorText}");
            using var decodedOverrides = JsonDocument.Parse(File.ReadAllText(startCapturePath));
            Assert.Equal([locatorOne, locatorTwo], decodedOverrides.RootElement.EnumerateArray().Select(item => item.GetString()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartScriptRejectsMoreThanEightPricingOverridesWithoutReflectingLocators()
    {
        var locators = Enumerable.Range(1, 9)
            .Select(index => $@"C:\private registry\locator-{index}.json")
            .ToArray();

        var startScript = ScriptPath("start.ps1").Replace("'", "''", StringComparison.Ordinal);
        var literals = string.Join(",", locators.Select(locator => $"'{locator.Replace("'", "''", StringComparison.Ordinal)}'"));
        var result = RunBoundedProcess(
            PowerShellExecutablePath(),
            ["-NoProfile", "-Command", $"& '{startScript}' -PricingRegistryOverride @({literals})"],
            environment: null,
            timeout: TimeSpan.FromMinutes(1));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("pricing_registry_override_count_invalid", result.StandardErrorText, StringComparison.Ordinal);
        foreach (var locator in locators)
        {
            Assert.DoesNotContain(locator, result.StandardOutputText, StringComparison.Ordinal);
            Assert.DoesNotContain(locator, result.StandardErrorText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StartupTaskDryRunReportsOnlyPricingOverridePresenceAndCount()
    {
        var locatorOne = @"C:\private registry\dry-run one; $(not-a-command).json";
        var locatorTwo = @"C:\private registry\dry-run two's value.json";
        var installScript = ScriptPath("install-startup-task.ps1").Replace("'", "''", StringComparison.Ordinal);
        var result = RunBoundedProcess(
            PowerShellExecutablePath(),
            ["-NoProfile", "-Command", $"& '{installScript}' -DryRun -PricingRegistryOverride @('{locatorOne.Replace("'", "''", StringComparison.Ordinal)}','{locatorTwo.Replace("'", "''", StringComparison.Ordinal)}')"],
            environment: null,
            timeout: TimeSpan.FromMinutes(1));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("pricing registry overrides: present (count: 2)", result.StandardOutputText, StringComparison.Ordinal);
        Assert.DoesNotContain(locatorOne, result.StandardOutputText, StringComparison.Ordinal);
        Assert.DoesNotContain(locatorTwo, result.StandardOutputText, StringComparison.Ordinal);
        Assert.DoesNotContain(locatorOne, result.StandardErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain(locatorTwo, result.StandardErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellFreeProcessLaunchPreservesExactOverrideArgumentsAndDurableRedirectsOnInstalledPowerShellHosts()
    {
        var root = CreateTemporaryDirectory("cao-startup-argv-tests");
        try
        {
            var captureScript = Path.Combine(root, "capture-argv.ps1");
            File.WriteAllText(captureScript, "Write-Output ($args | ConvertTo-Json -Compress)\n");
            var logicalArguments = new[]
            {
                "-NoProfile",
                "-File",
                captureScript,
                "C:\\private registry\\space value; $(not-a-command).json",
                "C:\\private registry\\single'quote and \\ trailing.json",
                "C:\\private registry\\double\"quote & pipe|.json",
            };

            foreach (var hostPath in InstalledPowerShellHosts())
            {
                var outputPath = Path.Combine(root, $"{Path.GetFileNameWithoutExtension(hostPath)}.stdout.json");
                var errorPath = Path.Combine(root, $"{Path.GetFileNameWithoutExtension(hostPath)}.stderr.log");
                var commonPath = ScriptPath("common.ps1");
                var command = $". {PowerShellLiteral(commonPath)}; $p = Start-LocalMonitorProcess -FilePath {PowerShellLiteral(hostPath)} -WorkingDirectory {PowerShellLiteral(root)} -ArgumentList @({string.Join(",", logicalArguments.Select(PowerShellLiteral))}) -StandardOutputPath {PowerShellLiteral(outputPath)} -StandardErrorPath {PowerShellLiteral(errorPath)}; $p.WaitForExit(); exit $p.ExitCode";
                var result = RunBoundedProcess(hostPath, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command], environment: null, timeout: TimeSpan.FromMinutes(1));

                Assert.True(result.ExitCode == 0, $"{hostPath}: {result.StandardOutputText}{result.StandardErrorText}");
                Assert.True(
                    SpinWait.SpinUntil(() => File.Exists(outputPath) && File.Exists(errorPath), TimeSpan.FromSeconds(2)),
                    $"{hostPath} did not preserve stdout/stderr redirection.");
                using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
                Assert.Equal(logicalArguments.Skip(3), document.RootElement.EnumerateArray().Select(item => item.GetString()));
                Assert.Empty(File.ReadAllText(errorPath));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartScriptLaunchesPublishedChildWithExactOverridesAndDurableDelayedLogs()
    {
        var root = CreateTemporaryDirectory("cao-startup-end-to-end-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var childProject = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
            var publishDirectory = Directory.CreateDirectory(Path.Combine(root, "published")).FullName;
            var logDirectory = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            var capturePath = Path.Combine(root, "child-argv.json");
            var statePath = Path.Combine(root, "local-monitor.state.json");
            var dbPath = Path.Combine(root, "raw store.db");
            var locatorOne = "C:\\private registry\\space value; $(not-a-command).json";
            var locatorTwo = "C:\\private registry\\quote's value.json";
            var projectPath = Path.Combine(childProject, "Child.csproj");
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(childProject, "Program.cs"),
                """
                using System;
                using System.IO;
                using System.Threading.Tasks;
                using System.Text.Json;

                await Task.Delay(1500);
                await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("CAO_CHILD_CAPTURE")!, JsonSerializer.Serialize(args));
                Console.WriteLine("delayed stdout");
                Console.Error.WriteLine("delayed stderr");
                """);
            var publish = RunBoundedProcess(
                "dotnet",
                ["publish", projectPath, "--configuration", "Release", "--output", publishDirectory, "--nologo"],
                environment: null,
                timeout: TimeSpan.FromMinutes(2));
            Assert.True(publish.ExitCode == 0, $"{publish.StandardOutputText}{publish.StandardErrorText}");

            File.Copy(ScriptPath("start.ps1"), Path.Combine(scripts, "start.ps1"));
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            var executablePath = Path.Combine(publishDirectory, "Child.exe");
            File.AppendAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                $script:LogDirectory = '{{logDirectory.Replace("'", "''", StringComparison.Ordinal)}}'
                $script:StatePath = '{{statePath.Replace("'", "''", StringComparison.Ordinal)}}'
                $script:HealthCalls = 0
                function Initialize-LocalMonitorRuntime { }
                function Get-LocalMonitorPublishedExePath { param([string] $InstallRoot) '{{executablePath.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Test-LocalMonitorPortInUse { param([string] $Url) $false }
                function Test-LocalMonitorHealth {
                    param([string] $Url, [string] $Path)
                    $script:HealthCalls++
                    if ($script:HealthCalls -gt 1) { [pscustomobject]@{ StatusCode = 200; Content = '{"status":"ready"}' } }
                }
                """);

            var startScript = Path.Combine(scripts, "start.ps1").Replace("'", "''", StringComparison.Ordinal);
            var result = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $"& '{startScript}' -Mode Published -InstallRoot '{root.Replace("'", "''", StringComparison.Ordinal)}' -DbPath '{dbPath.Replace("'", "''", StringComparison.Ordinal)}' -WaitReady:$false -PricingRegistryOverride @('{locatorOne.Replace("'", "''", StringComparison.Ordinal)}','{locatorTwo.Replace("'", "''", StringComparison.Ordinal)}')"],
                environment: new Dictionary<string, string?> { ["CAO_CHILD_CAPTURE"] = capturePath },
                timeout: TimeSpan.FromMinutes(1));

            Assert.Equal(0, result.ExitCode);
            Assert.True(
                SpinWait.SpinUntil(
                    () => File.Exists(capturePath)
                        && File.Exists(Path.Combine(logDirectory, "local-monitor.stdout.log"))
                        && File.Exists(Path.Combine(logDirectory, "local-monitor.stderr.log"))
                        && File.Exists(statePath),
                    TimeSpan.FromSeconds(5)),
                "The child did not write captured arguments, logs, and state after the wrapper exited.");
            using var arguments = JsonDocument.Parse(File.ReadAllText(capturePath));
            Assert.Equal(
                ["--db", dbPath, "--url", "http://127.0.0.1:4320", "--pricing-registry-override", locatorOne, "--pricing-registry-override", locatorTwo],
                arguments.RootElement.EnumerateArray().Select(item => item.GetString()));
            Assert.Contains("delayed stdout", File.ReadAllText(Path.Combine(logDirectory, "local-monitor.stdout.log")), StringComparison.Ordinal);
            Assert.Contains("delayed stderr", File.ReadAllText(Path.Combine(logDirectory, "local-monitor.stderr.log")), StringComparison.Ordinal);
            foreach (var path in Directory.EnumerateFiles(logDirectory).Append(statePath))
            {
                var text = File.ReadAllText(path);
                Assert.DoesNotContain(locatorOne, text, StringComparison.Ordinal);
                Assert.DoesNotContain(locatorTwo, text, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupTaskFailureAndEnableDisablePreserveEncodedOverrideActionWithoutLocatorLeakage()
    {
        var root = CreateTemporaryDirectory("cao-startup-task-lifecycle-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var actionPath = Path.Combine(root, "task-action.txt");
            var lifecyclePath = Path.Combine(root, "lifecycle-action.txt");
            var locatorOne = "C:\\private registry\\failure one; $(not-a-command).json";
            var locatorTwo = "C:\\private registry\\failure two's value.json";
            File.Copy(ScriptPath("install-startup-task.ps1"), Path.Combine(scripts, "install-startup-task.ps1"));
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            File.AppendAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                $script:DefaultDbPath = 'C:\safe\raw-store.db'
                function Get-LocalMonitorDefaultInstallRoot { 'C:\safe\app' }
                function Test-LocalMonitorLoopbackUrl { param([string] $Url) $true }
                function Get-LocalMonitorTask { param([string] $TaskName) $null }
                function Get-LocalMonitorRepoRoot { 'C:\safe\repo' }
                function Get-LocalMonitorPowerShellPath { 'C:\safe\pwsh.exe' }
                function New-ScheduledTaskAction {
                    param([string] $Execute, [string] $Argument, [string] $WorkingDirectory)
                    [System.IO.File]::WriteAllText('{{actionPath.Replace("'", "''", StringComparison.Ordinal)}}', $Argument)
                    [pscustomobject]@{}
                }
                function New-ScheduledTaskTrigger { [pscustomobject]@{} }
                function New-ScheduledTaskPrincipal { [pscustomobject]@{} }
                function New-ScheduledTaskSettingsSet { [pscustomobject]@{} }
                function Register-ScheduledTask { [pscustomobject]@{} }
                """);

            var installScript = Path.Combine(scripts, "install-startup-task.ps1").Replace("'", "''", StringComparison.Ordinal);
            var install = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $"& '{installScript}' -PricingRegistryOverride @('{locatorOne.Replace("'", "''", StringComparison.Ordinal)}','{locatorTwo.Replace("'", "''", StringComparison.Ordinal)}')"],
                environment: null,
                timeout: TimeSpan.FromMinutes(1));

            Assert.NotEqual(0, install.ExitCode);
            Assert.Contains("task_registration_failed", install.StandardErrorText, StringComparison.Ordinal);
            foreach (var locator in new[] { locatorOne, locatorTwo })
            {
                Assert.DoesNotContain(locator, install.StandardOutputText, StringComparison.Ordinal);
                Assert.DoesNotContain(locator, install.StandardErrorText, StringComparison.Ordinal);
                Assert.DoesNotContain(locator, File.ReadAllText(actionPath), StringComparison.Ordinal);
            }

            var taskAction = File.ReadAllText(actionPath);
            File.Copy(ScriptPath("set-startup-task.ps1"), Path.Combine(scripts, "set-startup-task.ps1"));
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "set-common.ps1"));
            File.AppendAllText(
                Path.Combine(scripts, "set-common.ps1"),
                $$"""
                function Get-LocalMonitorTask { param([string] $TaskName) [pscustomobject]@{ State = 'Ready'; Actions = @([pscustomobject]@{ Arguments = '{{taskAction}}' }) } }
                function Enable-ScheduledTask { param([string] $TaskName) [System.IO.File]::WriteAllText('{{lifecyclePath.Replace("'", "''", StringComparison.Ordinal)}}', (Get-LocalMonitorTask).Actions[0].Arguments) }
                function Disable-ScheduledTask { param([string] $TaskName) [System.IO.File]::WriteAllText('{{lifecyclePath.Replace("'", "''", StringComparison.Ordinal)}}', (Get-LocalMonitorTask).Actions[0].Arguments) }
                """);
            File.Move(Path.Combine(scripts, "common.ps1"), Path.Combine(scripts, "install-common.ps1"));
            File.Move(Path.Combine(scripts, "set-common.ps1"), Path.Combine(scripts, "common.ps1"));

            var setScript = Path.Combine(scripts, "set-startup-task.ps1");
            foreach (var operation in new[] { "Enable", "Disable" })
            {
                var result = RunPowerShellScript(setScript, "-Action", operation);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal(taskAction, File.ReadAllText(lifecyclePath));
                Assert.DoesNotContain(locatorOne, result.Output, StringComparison.Ordinal);
                Assert.DoesNotContain(locatorTwo, result.Output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StatusReportsOnlyDecodedPricingOverrideStateAndCount()
    {
        var root = CreateTemporaryDirectory("cao-startup-status-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var locatorOne = @"C:\private registry\status one; $(not-a-command).json";
            var locatorTwo = @"C:\private registry\status two's value.json";
            var command = $"& 'C:\\safe\\start.ps1' -Url 'http://127.0.0.1:4320' -DbPath 'C:\\safe\\raw-store.db' -Mode 'Published' -InstallRoot 'C:\\safe\\app' -NoBrowser -WaitReady -PricingRegistryOverride @('{locatorOne}','{locatorTwo.Replace("'", "''", StringComparison.Ordinal)}')";
            var action = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            File.Copy(ScriptPath("status.ps1"), Path.Combine(scripts, "status.ps1"));
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            File.AppendAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                function Get-LocalMonitorState { $null }
                function Get-LocalMonitorTask { [pscustomobject]@{ State = 'Ready'; Actions = @([pscustomobject]@{ Arguments = '{{action}}' }) } }
                function Test-LocalMonitorProcess { $false }
                function Test-LocalMonitorHealth { $null }
                function Get-LocalMonitorPublishedExePath { 'C:\safe\missing.exe' }
                function Get-LocalMonitorAppVersion { '' }
                """);

            var result = RunPowerShellScript(Path.Combine(scripts, "status.ps1"));

            Assert.Contains("pricing registry overrides: present (count: 2)", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(locatorOne, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(locatorTwo, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(locatorOne, result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(locatorTwo, result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StatusReportsMalformedTaskActionAsUnknownWithoutReflectingLocators()
    {
        var root = CreateTemporaryDirectory("cao-startup-status-malformed-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var locator = @"C:\private registry\malformed; $(not-a-command).json";
            File.Copy(ScriptPath("status.ps1"), Path.Combine(scripts, "status.ps1"));
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            File.AppendAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                function Get-LocalMonitorState { $null }
                function Get-LocalMonitorTask { [pscustomobject]@{ State = 'Ready'; Actions = @([pscustomobject]@{ Arguments = 'not-an-encoded-action {{locator.Replace("'", "''", StringComparison.Ordinal)}}' }) } }
                function Test-LocalMonitorProcess { $false }
                function Test-LocalMonitorHealth { $null }
                function Get-LocalMonitorPublishedExePath { 'C:\safe\missing.exe' }
                function Get-LocalMonitorAppVersion { '' }
                """);

            var result = RunPowerShellScript(Path.Combine(scripts, "status.ps1"));

            Assert.Contains("pricing registry overrides: unknown", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(locator, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(locator, result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StatusProbesOnlyLiveAndReadyWithoutUiFallback()
    {
        var root = CreateTemporaryDirectory("cao-status-health-only-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var observedPaths = Path.Combine(root, "health-paths.txt");
            File.Copy(ScriptPath("status.ps1"), Path.Combine(scripts, "status.ps1"));
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            File.AppendAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                function Get-LocalMonitorState { $null }
                function Get-LocalMonitorTask { [pscustomobject]@{ State = 'Ready'; Actions = @() } }
                function Test-LocalMonitorProcess { $false }
                function Test-LocalMonitorHealth {
                    param([string] $Url, [string] $Path)
                    if ($Path -notin @('/health/live', '/health/ready')) { throw "unexpected_health_path:$Path" }
                    [System.IO.File]::AppendAllText('{{observedPaths.Replace("'", "''", StringComparison.Ordinal)}}', "$Path`n")
                    if ($Path -eq '/health/live') { return [pscustomobject]@{ StatusCode = 200; Content = '{}' } }
                    return [pscustomobject]@{
                        StatusCode = 200
                        Content = '{"status":"ready","checks":{"projection_lag_seconds":0,"projection_backlog":0},"degraded_reasons":[]}'
                    }
                }
                function Get-LocalMonitorPublishedExePath { 'C:\safe\missing.exe' }
                function Get-LocalMonitorAppVersion { '' }
                """);

            var result = RunPowerShellScript(Path.Combine(scripts, "status.ps1"));

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Error);
            Assert.Equal(
                ["/health/live", "/health/ready"],
                File.ReadAllLines(observedPaths));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StatusReportsRecognizedAbsentActionWithZeroCount()
    {
        var root = CreateTemporaryDirectory("cao-startup-status-absent-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var command = "& 'C:\\safe\\start.ps1' -Url 'http://127.0.0.1:4320' -DbPath 'C:\\safe\\raw-store.db' -Mode 'Published' -InstallRoot 'C:\\safe\\app' -NoBrowser -WaitReady";
            var action = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            File.Copy(ScriptPath("status.ps1"), Path.Combine(scripts, "status.ps1"));
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            File.AppendAllText(Path.Combine(scripts, "common.ps1"), StatusTestCommonOverrides(action));

            var result = RunPowerShellScript(Path.Combine(scripts, "status.ps1"));

            Assert.Contains("pricing registry overrides: absent (count: 0)", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("@('C:\\private registry\\unterminated.json)")]
    [InlineData("@('C:\\private registry\\trailing.json',)")]
    public void StatusRejectsMalformedEncodedOverrideArrayWithoutReflectingLocator(string arrayLiteral)
    {
        var root = CreateTemporaryDirectory("cao-startup-status-array-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var locator = "C:\\private registry\\";
            var command = "& 'C:\\safe\\start.ps1' -Url 'http://127.0.0.1:4320' -DbPath 'C:\\safe\\raw-store.db' -Mode 'Published' -InstallRoot 'C:\\safe\\app' -NoBrowser -WaitReady -PricingRegistryOverride " + arrayLiteral;
            var action = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            File.Copy(ScriptPath("status.ps1"), Path.Combine(scripts, "status.ps1"));
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            File.AppendAllText(Path.Combine(scripts, "common.ps1"), StatusTestCommonOverrides(action));

            var result = RunPowerShellScript(Path.Combine(scripts, "status.ps1"));

            Assert.Contains("pricing registry overrides: unknown", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(locator, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(locator, result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScriptsExposeRequiredParameters()
    {
        AssertScriptContains("start.ps1", "ValidateSet('DotnetRun', 'Published')");
        AssertScriptContains("start.ps1", "$SanitizedOnly");
        AssertScriptContains("start.ps1", "$WaitReady");
        AssertScriptContains("start.ps1", "$InstallRoot");
        AssertScriptContains("stop.ps1", "$Force");
        AssertScriptContains("status.ps1", "installed:");
        AssertScriptContains("status.ps1", "startup registered:");
        AssertScriptContains("status.ps1", "startup enabled:");
        AssertScriptContains("status.ps1", "install root:");
        AssertScriptContains("status.ps1", "app version:");
        AssertScriptContains("status.ps1", "log path:");
        AssertScriptContains("status.ps1", "task name:");
        AssertScriptContains("status.ps1", "sanitized-only mode:");
        AssertScriptContains("install-startup-task.ps1", "$StartNow");
        AssertScriptContains("install-startup-task.ps1", "$Force");
        AssertScriptContains("install-startup-task.ps1", "$InstallRoot");
        AssertScriptContains("install-user-env.ps1", "$Force");
        AssertScriptContains("install-user-env.ps1", "$Url");
        AssertScriptContains("uninstall-user-env.ps1", "$Force");
        AssertScriptContains("set-startup-task.ps1", "Disable-ScheduledTask");
        AssertScriptContains("set-startup-task.ps1", "Enable-ScheduledTask");
        AssertScriptContains("uninstall-startup-task.ps1", "$StopRunning");
        AssertScriptContains("uninstall-startup-task.ps1", "$RemoveData");
        AssertScriptContains("uninstall-startup-task.ps1", "$InstallRoot");
        AssertScriptContains("package-release.ps1", "$RuntimeIdentifier");
        AssertScriptContains("install-session-hooks.ps1", "local-agent-monitor.json");
        AssertScriptContains("uninstall-session-hooks.ps1", "local-agent-monitor.json");
    }

    [Fact]
    public void SessionHookScriptsAreOptInAndProtectUnmanagedConfiguration()
    {
        var install = File.ReadAllText(ScriptPath("install-session-hooks.ps1"));
        var uninstall = File.ReadAllText(ScriptPath("uninstall-session-hooks.ps1"));
        var normalInstall = File.ReadAllText(ScriptPath("install.ps1"));

        Assert.Contains("managed_by", install, StringComparison.Ordinal);
        Assert.Contains("CopilotAgentObservability.LocalMonitor", install, StringComparison.Ordinal);
        Assert.Contains("hook-forward", install, StringComparison.Ordinal);
        Assert.Contains("SessionStart", install, StringComparison.Ordinal);
        Assert.Contains("UserPromptSubmit", install, StringComparison.Ordinal);
        Assert.Contains("PreToolUse", install, StringComparison.Ordinal);
        Assert.Contains("PostToolUse", install, StringComparison.Ordinal);
        Assert.Contains("SubagentStart", install, StringComparison.Ordinal);
        Assert.Contains("SubagentStop", install, StringComparison.Ordinal);
        Assert.Contains("Stop", install, StringComparison.Ordinal);
        Assert.Contains("hook_config_exists_unmanaged", install, StringComparison.Ordinal);
        Assert.Contains("managed_by", uninstall, StringComparison.Ordinal);
        Assert.Contains("hook_config_exists_unmanaged", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("install-session-hooks", normalInstall, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionHookInstallWritesCanonicalDeterministicConfigurationAndUninstallsIdempotently()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            File.WriteAllText(Path.Combine(installRoot, "CopilotAgentObservability.LocalMonitor.exe"), string.Empty);

            var first = RunPowerShellScript(
                ScriptPath("install-session-hooks.ps1"),
                "-HomeDirectory", home,
                "-InstallRoot", installRoot);
            Assert.Equal(0, first.ExitCode);
            var configPath = Path.Combine(home, ".copilot", "hooks", "local-agent-monitor.json");
            var firstConfigurationBytes = File.ReadAllBytes(configPath);

            var second = RunPowerShellScript(
                ScriptPath("install-session-hooks.ps1"),
                "-HomeDirectory", home,
                "-InstallRoot", installRoot);

            Assert.Equal(0, second.ExitCode);
            var secondConfigurationBytes = File.ReadAllBytes(configPath);
            Assert.Equal(firstConfigurationBytes, secondConfigurationBytes);
            using var document = JsonDocument.Parse(secondConfigurationBytes);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(
                "CopilotAgentObservability.LocalMonitor",
                document.RootElement.GetProperty("managed_by").GetString());
            var hooks = document.RootElement.GetProperty("hooks");
            Assert.Equal(
                [
                    "SessionStart",
                    "UserPromptSubmit",
                    "PreToolUse",
                    "PostToolUse",
                    "PostToolUseFailure",
                    "PermissionRequest",
                    "SubagentStart",
                    "SubagentStop",
                    "Stop",
                    "SessionEnd",
                ],
                hooks.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.False(hooks.TryGetProperty("StopFailure", out _));
            foreach (var hook in hooks.EnumerateObject())
            {
                var registration = Assert.Single(hook.Value.EnumerateArray());
                Assert.Equal("command", registration.GetProperty("type").GetString());
                Assert.Contains("hook-forward", registration.GetProperty("command").GetString());
                Assert.Equal(1, registration.GetProperty("timeoutSec").GetInt32());
            }

            var uninstall = RunPowerShellScript(
                ScriptPath("uninstall-session-hooks.ps1"),
                "-HomeDirectory", home);
            var repeatedUninstall = RunPowerShellScript(
                ScriptPath("uninstall-session-hooks.ps1"),
                "-HomeDirectory", home);

            Assert.Equal(0, uninstall.ExitCode);
            Assert.Equal(0, repeatedUninstall.ExitCode);
            Assert.False(File.Exists(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SessionHookInstallUpgradesManagedSevenEventConfigurationToCanonicalBytes()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var managedHome = Directory.CreateDirectory(Path.Combine(root, "managed-home")).FullName;
            var managedHooks = Directory.CreateDirectory(Path.Combine(managedHome, ".copilot", "hooks")).FullName;
            var managedConfigPath = Path.Combine(managedHooks, "local-agent-monitor.json");
            File.WriteAllText(
                managedConfigPath,
                """
                {
                  "version": 1,
                  "managed_by": "CopilotAgentObservability.LocalMonitor",
                  "hooks": {
                    "SessionStart": [],
                    "UserPromptSubmit": [],
                    "PreToolUse": [],
                    "PostToolUse": [],
                    "SubagentStart": [],
                    "SubagentStop": [],
                    "Stop": []
                  }
                }
                """);
            var freshHome = Directory.CreateDirectory(Path.Combine(root, "fresh-home")).FullName;
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            File.WriteAllText(Path.Combine(installRoot, "CopilotAgentObservability.LocalMonitor.exe"), string.Empty);

            var upgrade = RunPowerShellScript(
                ScriptPath("install-session-hooks.ps1"),
                "-HomeDirectory", managedHome,
                "-InstallRoot", installRoot);
            var freshInstall = RunPowerShellScript(
                ScriptPath("install-session-hooks.ps1"),
                "-HomeDirectory", freshHome,
                "-InstallRoot", installRoot);

            Assert.Equal(0, upgrade.ExitCode);
            Assert.Equal(0, freshInstall.ExitCode);
            var freshConfigPath = Path.Combine(freshHome, ".copilot", "hooks", "local-agent-monitor.json");
            Assert.Equal(File.ReadAllBytes(freshConfigPath), File.ReadAllBytes(managedConfigPath));
            using var upgraded = JsonDocument.Parse(File.ReadAllBytes(managedConfigPath));
            Assert.Equal(
                [
                    "SessionStart",
                    "UserPromptSubmit",
                    "PreToolUse",
                    "PostToolUse",
                    "PostToolUseFailure",
                    "PermissionRequest",
                    "SubagentStart",
                    "SubagentStop",
                    "Stop",
                    "SessionEnd",
                ],
                upgraded.RootElement.GetProperty("hooks").EnumerateObject().Select(property => property.Name).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"copilotAgentObservability.LocalMonitor\"")]
    [InlineData("[\"CopilotAgentObservability.LocalMonitor\"]")]
    [InlineData("\"CopilotAgentObservability.Local\\u0000Monitor\"")]
    public void SessionHookInstallRejectsInvalidOwnershipMarkerWithoutChangingBytes(string markerJson)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var hooks = Directory.CreateDirectory(Path.Combine(home, ".copilot", "hooks")).FullName;
            var configPath = Path.Combine(hooks, "local-agent-monitor.json");
            var unmanaged = Encoding.UTF8.GetBytes(
                $$"""
                {
                  "version": 1,
                  "managed_by": {{markerJson}},
                  "hooks": {}
                }
                """);
            using var fixture = JsonDocument.Parse(unmanaged);
            if (markerJson.Contains("\\u0000", StringComparison.Ordinal))
            {
                Assert.Contains("\\u0000", Encoding.UTF8.GetString(unmanaged), StringComparison.Ordinal);
                Assert.Equal(
                    "CopilotAgentObservability.Local\0Monitor",
                    fixture.RootElement.GetProperty("managed_by").GetString());
            }
            File.WriteAllBytes(configPath, unmanaged);
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            File.WriteAllText(Path.Combine(installRoot, "CopilotAgentObservability.LocalMonitor.exe"), string.Empty);

            var install = RunPowerShellScript(
                ScriptPath("install-session-hooks.ps1"),
                "-HomeDirectory", home,
                "-InstallRoot", installRoot);

            Assert.NotEqual(0, install.ExitCode);
            Assert.Contains("hook_config_exists_unmanaged", install.Error, StringComparison.Ordinal);
            Assert.Equal(unmanaged, File.ReadAllBytes(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"copilotAgentObservability.LocalMonitor\"")]
    [InlineData("[\"CopilotAgentObservability.LocalMonitor\"]")]
    [InlineData("\"CopilotAgentObservability.Local\\u0000Monitor\"")]
    public void SessionHookUninstallRejectsInvalidOwnershipMarkerWithoutChangingBytes(string markerJson)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var hooks = Directory.CreateDirectory(Path.Combine(home, ".copilot", "hooks")).FullName;
            var configPath = Path.Combine(hooks, "local-agent-monitor.json");
            var unmanaged = Encoding.UTF8.GetBytes(
                $$"""
                {
                  "version": 1,
                  "managed_by": {{markerJson}},
                  "hooks": {}
                }
                """);
            using var fixture = JsonDocument.Parse(unmanaged);
            if (markerJson.Contains("\\u0000", StringComparison.Ordinal))
            {
                Assert.Contains("\\u0000", Encoding.UTF8.GetString(unmanaged), StringComparison.Ordinal);
                Assert.Equal(
                    "CopilotAgentObservability.Local\0Monitor",
                    fixture.RootElement.GetProperty("managed_by").GetString());
            }
            File.WriteAllBytes(configPath, unmanaged);

            var uninstall = RunPowerShellScript(
                ScriptPath("uninstall-session-hooks.ps1"),
                "-HomeDirectory", home);

            Assert.NotEqual(0, uninstall.ExitCode);
            Assert.Contains("hook_config_exists_unmanaged", uninstall.Error, StringComparison.Ordinal);
            Assert.Equal(unmanaged, File.ReadAllBytes(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SessionHookScriptsNeverOverwriteOrDeleteUnmanagedFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var hooks = Directory.CreateDirectory(Path.Combine(home, ".copilot", "hooks")).FullName;
            var configPath = Path.Combine(hooks, "local-agent-monitor.json");
            var unmanaged = Encoding.UTF8.GetBytes("{\r\n  \"version\": 1,\r\n  \"hooks\": {}\r\n}\r\n");
            File.WriteAllBytes(configPath, unmanaged);
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            File.WriteAllText(Path.Combine(installRoot, "CopilotAgentObservability.LocalMonitor.exe"), string.Empty);

            var install = RunPowerShellScript(
                ScriptPath("install-session-hooks.ps1"),
                "-HomeDirectory", home,
                "-InstallRoot", installRoot);
            Assert.NotEqual(0, install.ExitCode);
            Assert.Contains("hook_config_exists_unmanaged", install.Error, StringComparison.Ordinal);
            Assert.Equal(unmanaged, File.ReadAllBytes(configPath));

            var uninstall = RunPowerShellScript(
                ScriptPath("uninstall-session-hooks.ps1"),
                "-HomeDirectory", home);

            Assert.NotEqual(0, uninstall.ExitCode);
            Assert.Contains("hook_config_exists_unmanaged", uninstall.Error, StringComparison.Ordinal);
            Assert.Equal(unmanaged, File.ReadAllBytes(configPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://user:secret@127.0.0.1:4320")]
    [InlineData("http://127.0.0.1:4320/other")]
    [InlineData("http://127.0.0.1:4320?token=secret")]
    [InlineData("http://127.0.0.1:4320#fragment")]
    public void SessionHookInstallerRejectsEndpointShapesRejectedByForwarder(string endpoint)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            File.WriteAllText(Path.Combine(installRoot, "CopilotAgentObservability.LocalMonitor.exe"), string.Empty);

            var result = RunPowerShellScript(
                ScriptPath("install-session-hooks.ps1"),
                "-HomeDirectory", home,
                "-InstallRoot", installRoot,
                "-Endpoint", endpoint);

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(Path.Combine(home, ".copilot", "hooks", "local-agent-monitor.json")));
            Assert.DoesNotContain("secret", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:4320/api/session-ingest/v1/events")]
    [InlineData("http://[::1]:4320")]
    public void SessionHookInstallerAcceptsQualifiedAndIpv6LoopbackEndpoints(string endpoint)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            File.WriteAllText(Path.Combine(installRoot, "CopilotAgentObservability.LocalMonitor.exe"), string.Empty);

            var result = RunPowerShellScript(
                ScriptPath("install-session-hooks.ps1"),
                "-HomeDirectory", home,
                "-InstallRoot", installRoot,
                "-Endpoint", endpoint);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(home, ".copilot", "hooks", "local-agent-monitor.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PublishedModeStartsPublishedExecutable()
    {
        var start = File.ReadAllText(ScriptPath("start.ps1"));

        Assert.DoesNotContain("published_mode_not_implemented", start, StringComparison.Ordinal);
        Assert.Contains("Get-LocalMonitorPublishedExePath", start, StringComparison.Ordinal);
        Assert.Contains("Save-LocalMonitorState", start, StringComparison.Ordinal);
        Assert.Contains("Mode 'published'", start, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("degraded")]
    public void PublishedStartWaitReadyAcceptsOnlyDocumentedSuccessStates(string healthStatus)
    {
        var result = RunPublishedStartWithHealth(healthStatus);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"started {healthStatus}", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("not_ready", 2, "health_ready_not_ready")]
    [InlineData("unreachable", 1, "monitor_start_timeout")]
    public void PublishedStartWaitReadyFailsWhenReadinessIsNotAcceptedOrUnreachable(
        string healthStatus,
        int expectedExitCode,
        string expectedError)
    {
        var result = RunPublishedStartWithHealth(healthStatus);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.DoesNotContain("started", result.Output, StringComparison.Ordinal);
        Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishedStartWaitReadyDoesNotTreatLiveOnlyExistingProcessAsReady()
    {
        var result = RunPublishedStartWithHealth("existing_not_ready");

        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("already_running", result.Output, StringComparison.Ordinal);
        Assert.Contains("health_ready_not_ready", result.Error, StringComparison.Ordinal);
        Assert.True(result.ReadyProbeObserved, "An already-live process must still be readiness-probed.");
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("degraded")]
    public void PublishedStartWaitReadyAcceptsReadyExistingProcessAfterReadinessProbe(string healthStatus)
    {
        var result = RunPublishedStartWithHealth($"existing_{healthStatus}");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("already_running", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
        Assert.True(result.ReadyProbeObserved, "An already-live process must still be readiness-probed.");
    }

    [Fact]
    public void PublishedStartWaitReadyFailsWhenExistingProcessReadinessIsUnreachable()
    {
        var result = RunPublishedStartWithHealth("existing_unreachable");

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("already_running", result.Output, StringComparison.Ordinal);
        Assert.Contains("monitor_start_timeout", result.Error, StringComparison.Ordinal);
        Assert.True(result.ReadyProbeObserved, "An already-live process must still be readiness-probed.");
    }

    [Fact]
    public void PublishedStartPublishesStateOnlyAfterLivenessSucceeds()
    {
        var result = RunPublishedStartWithHealth("ready");

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.StatePublishedBeforeLive, "The child PID must not become live-monitor state before its HTTP host is live.");
        Assert.True(result.StateExists, "A live child must publish durable monitor state.");
    }

    [Fact]
    public void PublishedStartReportsChildExitWithoutTimeoutOrRunningState()
    {
        var result = RunPublishedStartWithHealth("child_exit");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("monitor_start_failed", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("monitor_start_timeout", result.Error, StringComparison.Ordinal);
        Assert.False(result.StateExists, "An exited child must not leave running monitor state.");
    }

    [Fact]
    public void PublishedStartReportsChildExitObservedByTheFinalHealthProbe()
    {
        var result = RunPublishedStartWithHealth("child_exit_after_probe");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("monitor_start_failed", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("monitor_start_timeout", result.Error, StringComparison.Ordinal);
        Assert.False(result.StateExists, "An exited child must not leave running monitor state.");
    }

    [Fact]
    public void PublishedStartPreservesForeignStateWhenItsChildExits()
    {
        var result = RunPublishedStartWithHealth("child_exit_foreign_state");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("monitor_start_failed", result.Error, StringComparison.Ordinal);
        Assert.Equal("foreign-state", result.StateContent);
    }

    [Fact]
    public void PublishedStartRejectsAChildThatExitsDuringTheSuccessfulLiveProbe()
    {
        var result = RunPublishedStartWithHealth("child_exit_after_live");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("monitor_start_failed", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("started", result.Output, StringComparison.Ordinal);
        Assert.False(result.StateExists);
    }

    [Fact]
    public void PublishedStartRemovesItsOwnedStateWhenTheChildExitsDuringReadiness()
    {
        var result = RunPublishedStartWithHealth("child_exit_after_state");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("monitor_start_failed", result.Error, StringComparison.Ordinal);
        Assert.False(result.StateExists, "State published after liveness must be removed when that exact child exits.");
    }

    [Fact]
    public void PublishedStartTimeoutTerminatesTheUntrackedChild()
    {
        var result = RunPublishedStartWithHealth("unreachable");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("monitor_start_timeout", result.Error, StringComparison.Ordinal);
        Assert.True(result.ProcessTerminated, "A child that never becomes live must not remain untracked after timeout.");
        Assert.False(result.StateExists);
    }

    [Fact]
    public void PublishedStartTimeoutPreservesOwnedStateWhenChildCleanupFails()
    {
        var result = RunPublishedStartWithHealth("timeout_cleanup_failure_after_state");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("monitor_start_timeout", result.Error, StringComparison.Ordinal);
        Assert.True(result.StateExists, "A child that could not be stopped must remain discoverable through its owned state.");
    }

    [Fact]
    public void UninstallKeepsDataByDefaultAndRemovesRuntimeOnlyWithRemoveData()
    {
        var uninstall = File.ReadAllText(ScriptPath("uninstall-startup-task.ps1"));

        Assert.Contains("RemoveData", uninstall, StringComparison.Ordinal);
        Assert.Contains("Remove-LocalMonitorInstall", uninstall, StringComparison.Ordinal);
        Assert.Contains("Remove-LocalMonitorState", uninstall, StringComparison.Ordinal);
        Assert.Contains("$script:RuntimeRoot", uninstall, StringComparison.Ordinal);
        Assert.Contains("data_not_removed", uninstall, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallFailsClosedWhenStopFails()
    {
        var root = CreateTemporaryDirectory("cao-uninstall-stop-failure");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var installedFile = Path.Combine(installRoot, "installed.txt");
            File.WriteAllText(installedFile, "test-owned");
            File.Copy(ScriptPath("uninstall-startup-task.ps1"), Path.Combine(scripts, "uninstall-startup-task.ps1"));
            File.WriteAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                $script:RuntimeRoot = '{{root.Replace("'", "''", StringComparison.Ordinal)}}'
                function Get-LocalMonitorDefaultInstallRoot { '{{installRoot.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Get-LocalMonitorTask { $null }
                function Remove-LocalMonitorState { }
                function Remove-LocalMonitorInstall {
                    param([string] $InstallRoot, [switch] $AllowExternal)
                    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
                }
                """);
            File.WriteAllText(
                Path.Combine(scripts, "stop.ps1"),
                "Write-Error 'stop_timeout'\nexit 1\n");

            var result = RunPowerShellScript(
                Path.Combine(scripts, "uninstall-startup-task.ps1"),
                "-StopRunning",
                "-InstallRoot", installRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain("uninstalled", result.Output, StringComparison.Ordinal);
            Assert.Contains("stop_timeout", result.Error, StringComparison.Ordinal);
            Assert.True(File.Exists(installedFile), "Install removal must not begin after stop fails.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StopTerminatesAHeadlessPublishedProcessWithoutForce()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory("cao-headless-stop");
        Process? monitor = null;
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            File.Copy(ScriptPath("stop.ps1"), Path.Combine(scripts, "stop.ps1"));
            monitor = Process.Start(new ProcessStartInfo
            {
                FileName = PowerShellExecutablePath(),
                ArgumentList =
                {
                    "-NoProfile",
                    "-Command",
                    "while ($true) { Start-Sleep -Seconds 60 }",
                },
                CreateNoWindow = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Failed to start test-owned headless process.");
            File.WriteAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                function Get-LocalMonitorState { [pscustomobject]@{ process_id = {{monitor.Id}} } }
                function Test-LocalMonitorProcess { param([int] $ProcessId) $ProcessId -eq {{monitor.Id}} }
                function Remove-LocalMonitorState { }
                function Write-LocalMonitorLog { param([string] $Message) }
                """);

            var result = RunPowerShellScript(
                Path.Combine(scripts, "stop.ps1"),
                "-TimeoutSeconds", "1");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("stopped", result.Output, StringComparison.Ordinal);
            Assert.True(monitor.WaitForExit(5_000), "The test-owned headless process remained running.");
        }
        finally
        {
            if (monitor is { HasExited: false })
            {
                monitor.Kill(entireProcessTree: true);
                monitor.WaitForExit();
            }

            monitor?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StopUsesOnlyBoundedProcessExitWaits()
    {
        var stop = File.ReadAllText(ScriptPath("stop.ps1"));

        Assert.DoesNotContain("$process.WaitForExit()", stop, StringComparison.Ordinal);
        Assert.Equal(3, stop.Split("$process.WaitForExit($TimeoutSeconds * 1000)", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void UninstallFailsClosedWhenInstalledFilesRemain()
    {
        var root = CreateTemporaryDirectory("cao-uninstall-removal-failure");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var installedFile = Path.Combine(installRoot, "installed.txt");
            File.WriteAllText(installedFile, "test-owned");
            File.Copy(ScriptPath("uninstall-startup-task.ps1"), Path.Combine(scripts, "uninstall-startup-task.ps1"));
            File.WriteAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                $script:RuntimeRoot = '{{root.Replace("'", "''", StringComparison.Ordinal)}}'
                function Get-LocalMonitorDefaultInstallRoot { '{{installRoot.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Get-LocalMonitorTask { $null }
                function Remove-LocalMonitorState { }
                function Remove-LocalMonitorInstall { param([string] $InstallRoot, [switch] $AllowExternal) }
                """);

            var result = RunPowerShellScript(
                Path.Combine(scripts, "uninstall-startup-task.ps1"),
                "-InstallRoot", installRoot);

            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain("uninstalled", result.Output, StringComparison.Ordinal);
            Assert.Contains("uninstall_incomplete", result.Error, StringComparison.Ordinal);
            Assert.True(File.Exists(installedFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UserEnvironmentScriptsPersistRawLocalMonitorOtelSettingsForCurrentUser()
    {
        var install = File.ReadAllText(ScriptPath("install-user-env.ps1"));
        var uninstall = File.ReadAllText(ScriptPath("uninstall-user-env.ps1"));
        var common = File.ReadAllText(ScriptPath("common.ps1"));

        Assert.Contains("Set-LocalMonitorUserEnvironmentVariable", common, StringComparison.Ordinal);
        Assert.Contains("Clear-LocalMonitorUserEnvironmentVariable", common, StringComparison.Ordinal);
        Assert.Contains("Send-LocalMonitorEnvironmentChanged", common, StringComparison.Ordinal);
        Assert.Contains("SetEnvironmentVariable($Name, $Value, 'User')", common, StringComparison.Ordinal);
        Assert.Contains("SetEnvironmentVariable($Name, $null, 'User')", common, StringComparison.Ordinal);
        Assert.Contains("WM_SETTINGCHANGE", common, StringComparison.Ordinal);
        Assert.Contains("Environment", common, StringComparison.Ordinal);

        Assert.Contains("CAO_COLLECTION_PROFILE", install, StringComparison.Ordinal);
        Assert.Contains("raw-local-receiver", install, StringComparison.Ordinal);
        Assert.Contains("COPILOT_OTEL_ENABLED", install, StringComparison.Ordinal);
        Assert.Contains("COPILOT_OTEL_CAPTURE_CONTENT", install, StringComparison.Ordinal);
        Assert.Contains("COPILOT_OTEL_ENDPOINT", install, StringComparison.Ordinal);
        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", install, StringComparison.Ordinal);
        Assert.Contains("OTEL_EXPORTER_OTLP_PROTOCOL", install, StringComparison.Ordinal);
        Assert.Contains("http/protobuf", install, StringComparison.Ordinal);
        Assert.Contains("OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT", install, StringComparison.Ordinal);
        Assert.Contains("OTEL_RESOURCE_ATTRIBUTES", install, StringComparison.Ordinal);
        Assert.Contains("experiment.id=baseline", install, StringComparison.Ordinal);
        Assert.DoesNotContain("client.kind", install, StringComparison.Ordinal);
        Assert.DoesNotContain("setx", install, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Clear-LocalMonitorUserEnvironmentVariable", uninstall, StringComparison.Ordinal);
        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", uninstall, StringComparison.Ordinal);
        Assert.Contains("Send-LocalMonitorEnvironmentChanged", uninstall, StringComparison.Ordinal);
        Assert.DoesNotContain("setx", uninstall, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleaseWorkflowBuildsTestsPackagesAndUploadsZipArtifact()
    {
        var workflow = File.ReadAllText(WorkflowPath("local-monitor-release.yml"));

        Assert.Contains("workflow_dispatch", workflow, StringComparison.Ordinal);
        Assert.Contains("windows-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet build CopilotAgentObservability.slnx", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts\\test\\install-playwright-chromium.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test CopilotAgentObservability.slnx", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts\\local-monitor\\package-release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact", workflow, StringComparison.Ordinal);
        Assert.Contains("local-monitor-win-x64.zip", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptsDoNotLogRawPayloadOrPiiFields()
    {
        foreach (var script in RequiredScripts)
        {
            var text = File.ReadAllText(ScriptPath(script));

            Assert.DoesNotContain("PayloadJson", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("request body", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("user.email", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tool arguments", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tool results", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    public void SkillDiscoveryProjectPathAcceptsUpToSixteenMembers(int count)
    {
        var projectPaths = Enumerable.Range(1, count).Select(index => $@"C:\skills\project-{index}").ToArray();

        var message = InvokeSkillDiscoveryValidation(projectPaths, Array.Empty<string>(), sanitizedOnly: false);

        Assert.Null(message);
    }

    [Fact]
    public void SkillDiscoveryProjectPathRejectsSeventeenthMember()
    {
        var projectPaths = Enumerable.Range(1, 17).Select(index => $@"C:\skills\project-{index}").ToArray();

        var message = InvokeSkillDiscoveryValidation(projectPaths, Array.Empty<string>(), sanitizedOnly: false);

        Assert.Equal("local-monitor accepts at most 16 --skill-discovery-project-path values.", message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    public void SkillDiscoveryDirectoryAcceptsUpToThirtyTwoMembers(int count)
    {
        var directories = Enumerable.Range(1, count).Select(index => $@"C:\skills\dir-{index}").ToArray();

        var message = InvokeSkillDiscoveryValidation(Array.Empty<string>(), directories, sanitizedOnly: false);

        Assert.Null(message);
    }

    [Fact]
    public void SkillDiscoveryDirectoryRejectsThirtyThirdMember()
    {
        var directories = Enumerable.Range(1, 33).Select(index => $@"C:\skills\dir-{index}").ToArray();

        var message = InvokeSkillDiscoveryValidation(Array.Empty<string>(), directories, sanitizedOnly: false);

        Assert.Equal("local-monitor accepts at most 32 --skill-discovery-directory values.", message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SkillDiscoveryProjectPathRejectsEmptyOrWhitespaceOrNullMember(string? blankMember)
    {
        var message = InvokeSkillDiscoveryValidation(
            [@"C:\skills\ok", blankMember],
            Array.Empty<string>(),
            sanitizedOnly: false);

        Assert.Equal("--skill-discovery-project-path requires a value.", message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SkillDiscoveryDirectoryRejectsEmptyOrWhitespaceOrNullMember(string? blankMember)
    {
        var message = InvokeSkillDiscoveryValidation(
            Array.Empty<string>(),
            [@"C:\skills\ok", blankMember],
            sanitizedOnly: false);

        Assert.Equal("--skill-discovery-directory requires a value.", message);
    }

    [Fact]
    public void SkillDiscoveryOptionsConflictWithSanitizedOnlyOnBothScripts()
    {
        var message = InvokeSkillDiscoveryValidation([@"C:\skills\one"], Array.Empty<string>(), sanitizedOnly: true);
        Assert.Equal("skill discovery options cannot be used with --sanitized-only.", message);

        var startScript = ScriptPath("start.ps1").Replace("'", "''", StringComparison.Ordinal);
        var startResult = RunBoundedProcess(
            PowerShellExecutablePath(),
            ["-NoProfile", "-Command", $"& '{startScript}' -SanitizedOnly -SkillDiscoveryDirectory @('C:\\skills\\dir')"],
            environment: null,
            timeout: TimeSpan.FromMinutes(1));
        Assert.NotEqual(0, startResult.ExitCode);
        Assert.Contains("skill discovery options cannot be used with --sanitized-only.", startResult.StandardErrorText, StringComparison.Ordinal);

        var installScript = ScriptPath("install-startup-task.ps1").Replace("'", "''", StringComparison.Ordinal);
        var installResult = RunBoundedProcess(
            PowerShellExecutablePath(),
            ["-NoProfile", "-Command", $"& '{installScript}' -SanitizedOnly -SkillDiscoveryDirectory @('C:\\skills\\dir')"],
            environment: null,
            timeout: TimeSpan.FromMinutes(1));
        Assert.NotEqual(0, installResult.ExitCode);
        Assert.Contains("skill discovery options cannot be used with --sanitized-only.", installResult.StandardErrorText, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> SkillDiscoveryPrecedenceCases()
    {
        yield return new object[]
        {
            new[] { @"C:\skills\ok", "" },
            new[] { @"C:\skills\ok", "   " },
            false,
            "--skill-discovery-project-path requires a value.",
        };
        yield return new object[]
        {
            Enumerable.Range(1, 17).Select(index => $@"C:\skills\project-{index}").ToArray(),
            new[] { "" },
            false,
            "--skill-discovery-directory requires a value.",
        };
        yield return new object[]
        {
            Enumerable.Range(1, 17).Select(index => $@"C:\skills\project-{index}").ToArray(),
            Enumerable.Range(1, 33).Select(index => $@"C:\skills\dir-{index}").ToArray(),
            false,
            "local-monitor accepts at most 16 --skill-discovery-project-path values.",
        };
        yield return new object[]
        {
            new[] { @"C:\skills\project-1" },
            Enumerable.Range(1, 33).Select(index => $@"C:\skills\dir-{index}").ToArray(),
            true,
            "local-monitor accepts at most 32 --skill-discovery-directory values.",
        };
        yield return new object[]
        {
            new[] { @"C:\skills\project-1" },
            Array.Empty<string>(),
            true,
            "skill discovery options cannot be used with --sanitized-only.",
        };
    }

    [Theory]
    [MemberData(nameof(SkillDiscoveryPrecedenceCases))]
    public void SkillDiscoveryValidationPrecedenceIsFixedAndOrderIndependent(
        string[] projectPaths,
        string[] directories,
        bool sanitizedOnly,
        string expectedMessage)
    {
        var forward = InvokeSkillDiscoveryValidation(projectPaths, directories, sanitizedOnly, reverseParameterOrder: false);
        var reversed = InvokeSkillDiscoveryValidation(projectPaths, directories, sanitizedOnly, reverseParameterOrder: true);

        Assert.Equal(expectedMessage, forward);
        Assert.Equal(expectedMessage, reversed);
    }

    [Fact]
    public void StartWrapperSerializesSkillDiscoveryArraysAsRepeatedPairsAfterPricingOverrides()
    {
        var root = CreateTemporaryDirectory("cao-skill-discovery-argv-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var logs = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            var database = Path.Combine(root, "raw-store.db");
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var executable = Path.Combine(installRoot, "CopilotAgentObservability.LocalMonitor.exe");
            File.WriteAllText(executable, string.Empty);
            var capturePath = Path.Combine(root, "argv.json");
            var start = Path.Combine(scripts, "start.ps1");
            File.Copy(ScriptPath("start.ps1"), start);
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            File.AppendAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                $script:DefaultDbPath = '{{database.Replace("'", "''", StringComparison.Ordinal)}}'
                $script:LogDirectory = '{{logs.Replace("'", "''", StringComparison.Ordinal)}}'
                $script:LiveProbeCount = 0
                function Test-LocalMonitorLoopbackUrl { param([string] $Url) return $true }
                function Initialize-LocalMonitorRuntime { param([string] $DbPath) }
                function Test-LocalMonitorPortInUse { param([string] $Url) return $false }
                function Test-LocalMonitorHealth {
                    param([string] $Url, [string] $Path)
                    if ($Path -eq '/health/live') {
                        $script:LiveProbeCount++
                        if ($script:LiveProbeCount -eq 1) { return $null }
                        return [pscustomobject]@{ StatusCode = 200; Content = '{}' }
                    }
                    return [pscustomobject]@{ StatusCode = 200; Content = '{"status":"ready"}' }
                }
                function Get-LocalMonitorDefaultInstallRoot { return '{{installRoot.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Get-LocalMonitorPublishedExePath { param([string] $InstallRoot) return '{{executable.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Start-LocalMonitorProcess {
                    param(
                        [string] $FilePath,
                        [object[]] $ArgumentList,
                        [string] $WorkingDirectory,
                        [string] $StandardOutputPath,
                        [string] $StandardErrorPath)
                    [System.IO.File]::WriteAllText('{{capturePath.Replace("'", "''", StringComparison.Ordinal)}}', ($ArgumentList | ConvertTo-Json -Compress))
                    return [pscustomobject]@{ Id = 4242 }
                }
                function Save-LocalMonitorState {
                    param(
                        [int] $ProcessId, [string] $Url, [string] $DbPath, [string] $Mode,
                        [string] $RepoRoot, [string] $InstallRoot, [string] $ExecutablePath, [switch] $SanitizedOnly)
                }
                function Write-LocalMonitorLog { param([string] $Message) }
                """);

            var projectPathOne = @"C:\private skills\project one; $(not-a-command)";
            var projectPathTwo = @"C:\private skills\project two's value";
            var directoryOne = @"C:\private skills\directory one; $(not-a-command)";
            var pricingOverride = @"C:\pricing\registry.json";

            var startCommand = "& " + PowerShellLiteral(start) +
                " -Mode Published -Url http://127.0.0.1:4320 -DbPath " + PowerShellLiteral(database) +
                " -InstallRoot " + PowerShellLiteral(installRoot) +
                " -NoBrowser -WaitReady:$false" +
                " -PricingRegistryOverride @(" + PowerShellLiteral(pricingOverride) + ")" +
                " -SkillDiscoveryProjectPath @(" + PowerShellLiteral(projectPathOne) + "," + PowerShellLiteral(projectPathTwo) + ")" +
                " -SkillDiscoveryDirectory @(" + PowerShellLiteral(directoryOne) + ")";

            var result = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-Command", startCommand],
                environment: null,
                timeout: TimeSpan.FromMinutes(1));

            Assert.True(result.ExitCode == 0, $"{result.StandardOutputText}{result.StandardErrorText}");
            using var argv = JsonDocument.Parse(File.ReadAllText(capturePath));
            Assert.Equal(
                [
                    "--db", database, "--url", "http://127.0.0.1:4320",
                    "--pricing-registry-override", pricingOverride,
                    "--skill-discovery-project-path", projectPathOne,
                    "--skill-discovery-project-path", projectPathTwo,
                    "--skill-discovery-directory", directoryOne,
                ],
                argv.RootElement.EnumerateArray().Select(item => item.GetString()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupTaskEncodesSkillDiscoveryArraysAndCountOnlyReaderDecodesThem()
    {
        var projectPathOne = @"C:\private skills\project one; $(not-a-command)";
        var projectPathTwo = @"C:\private skills\project two's value";
        var directoryOne = @"C:\private skills\directory one; $(not-a-command)";

        var command = ". " + PowerShellLiteral(ScriptPath("common.ps1")) + "; " +
            "$taskArgument = New-LocalMonitorStartupTaskArgument -StartScript 'C:\\safe\\start.ps1' -Url 'http://127.0.0.1:4320' -DbPath 'C:\\safe\\raw-store.db' -Mode 'Published' -InstallRoot 'C:\\safe\\app' " +
            "-SkillDiscoveryProjectPath @(" + PowerShellLiteral(projectPathOne) + "," + PowerShellLiteral(projectPathTwo) + ") " +
            "-SkillDiscoveryDirectory @(" + PowerShellLiteral(directoryOne) + "); " +
            "$encoded = $taskArgument.Split(' ')[-1]; " +
            "$decodedCommand = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($encoded)); " +
            "$task = [pscustomobject]@{ Actions = @([pscustomobject]@{ Arguments = $taskArgument }) }; " +
            "$projectState = Get-LocalMonitorTaskSkillDiscoveryProjectPathState -Task $task; " +
            "$directoryState = Get-LocalMonitorTaskSkillDiscoveryDirectoryState -Task $task; " +
            "[pscustomobject]@{ DecodedCommand = $decodedCommand; ProjectState = $projectState.State; ProjectCount = $projectState.Count; DirectoryState = $directoryState.State; DirectoryCount = $directoryState.Count } | ConvertTo-Json -Compress";

        var result = RunBoundedProcess(
            PowerShellExecutablePath(),
            ["-NoProfile", "-Command", command],
            environment: null,
            timeout: TimeSpan.FromMinutes(1));

        Assert.True(result.ExitCode == 0, $"{result.StandardOutputText}{result.StandardErrorText}");
        using var document = JsonDocument.Parse(result.StandardOutputText);
        var decodedCommand = document.RootElement.GetProperty("DecodedCommand").GetString();
        Assert.NotNull(decodedCommand);
        var expectedProjectPathFragment = "-SkillDiscoveryProjectPath @(" +
            PowerShellLiteral(projectPathOne) + "," + PowerShellLiteral(projectPathTwo) + ")";
        var expectedDirectoryFragment = "-SkillDiscoveryDirectory @(" + PowerShellLiteral(directoryOne) + ")";
        Assert.Contains(expectedProjectPathFragment, decodedCommand, StringComparison.Ordinal);
        Assert.Contains(expectedDirectoryFragment, decodedCommand, StringComparison.Ordinal);
        Assert.Equal("present", document.RootElement.GetProperty("ProjectState").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("ProjectCount").GetInt32());
        Assert.Equal("present", document.RootElement.GetProperty("DirectoryState").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("DirectoryCount").GetInt32());
    }

    [Fact]
    public void SkillDiscoveryValidationFailureAndDryRunNeverLeakSuppliedRoots()
    {
        const string sentinel = "SENTINEL";
        var sentinelProjectPath = @"C:\SENTINEL_ROOT\project";
        var sentinelDirectory = @"C:\SENTINEL_ROOT\skills";

        var tooManyProjectPaths = Enumerable.Range(1, 17)
            .Select(index => index == 1 ? sentinelProjectPath : $@"C:\skills\project-{index}")
            .ToArray();
        var startScript = ScriptPath("start.ps1").Replace("'", "''", StringComparison.Ordinal);
        var literals = string.Join(",", tooManyProjectPaths.Select(PowerShellLiteral));
        var failure = RunBoundedProcess(
            PowerShellExecutablePath(),
            ["-NoProfile", "-Command", $"& '{startScript}' -SkillDiscoveryProjectPath @({literals})"],
            environment: null,
            timeout: TimeSpan.FromMinutes(1));

        Assert.NotEqual(0, failure.ExitCode);
        Assert.Contains("local-monitor accepts at most 16 --skill-discovery-project-path values.", failure.StandardErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, failure.StandardOutputText, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, failure.StandardErrorText, StringComparison.Ordinal);

        var installScript = ScriptPath("install-startup-task.ps1").Replace("'", "''", StringComparison.Ordinal);
        var dryRun = RunBoundedProcess(
            PowerShellExecutablePath(),
            [
                "-NoProfile", "-Command",
                $"& '{installScript}' -DryRun -SkillDiscoveryProjectPath @({PowerShellLiteral(sentinelProjectPath)}) -SkillDiscoveryDirectory @({PowerShellLiteral(sentinelDirectory)})",
            ],
            environment: null,
            timeout: TimeSpan.FromMinutes(1));

        Assert.Equal(0, dryRun.ExitCode);
        Assert.Contains("skill discovery project paths: present (count: 1)", dryRun.StandardOutputText, StringComparison.Ordinal);
        Assert.Contains("skill discovery directories: present (count: 1)", dryRun.StandardOutputText, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, dryRun.StandardOutputText, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, dryRun.StandardErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillDiscoveryStartFlowNeverLeaksSuppliedRootsThroughLogsOrState()
    {
        const string sentinel = "SENTINEL";
        var sentinelProjectPath = @"C:\SENTINEL_ROOT\project";
        var sentinelDirectory = @"C:\SENTINEL_ROOT\skills";
        var root = CreateTemporaryDirectory("cao-skill-discovery-leak-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var logs = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            var database = Path.Combine(root, "raw-store.db");
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var executable = Path.Combine(installRoot, "CopilotAgentObservability.LocalMonitor.exe");
            File.WriteAllText(executable, string.Empty);
            var statePath = Path.Combine(root, "local-monitor.state.json");
            var start = Path.Combine(scripts, "start.ps1");
            File.Copy(ScriptPath("start.ps1"), start);
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            File.AppendAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                $script:DefaultDbPath = '{{database.Replace("'", "''", StringComparison.Ordinal)}}'
                $script:LogDirectory = '{{logs.Replace("'", "''", StringComparison.Ordinal)}}'
                $script:StatePath = '{{statePath.Replace("'", "''", StringComparison.Ordinal)}}'
                $script:LiveProbeCount = 0
                function Test-LocalMonitorLoopbackUrl { param([string] $Url) return $true }
                function Initialize-LocalMonitorRuntime { param([string] $DbPath) }
                function Test-LocalMonitorPortInUse { param([string] $Url) return $false }
                function Test-LocalMonitorHealth {
                    param([string] $Url, [string] $Path)
                    if ($Path -eq '/health/live') {
                        $script:LiveProbeCount++
                        if ($script:LiveProbeCount -eq 1) { return $null }
                        return [pscustomobject]@{ StatusCode = 200; Content = '{}' }
                    }
                    return [pscustomobject]@{ StatusCode = 200; Content = '{"status":"ready"}' }
                }
                function Get-LocalMonitorDefaultInstallRoot { return '{{installRoot.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Get-LocalMonitorPublishedExePath { param([string] $InstallRoot) return '{{executable.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Start-LocalMonitorProcess {
                    param(
                        [string] $FilePath, [object[]] $ArgumentList, [string] $WorkingDirectory,
                        [string] $StandardOutputPath, [string] $StandardErrorPath)
                    return [pscustomobject]@{ Id = 4242 }
                }
                function Get-LocalMonitorAppVersion { param([string] $InstallRoot) return '' }
                """);

            var startCommand = "& " + PowerShellLiteral(start) +
                " -Mode Published -Url http://127.0.0.1:4320 -DbPath " + PowerShellLiteral(database) +
                " -InstallRoot " + PowerShellLiteral(installRoot) +
                " -NoBrowser -WaitReady:$false" +
                " -SkillDiscoveryProjectPath @(" + PowerShellLiteral(sentinelProjectPath) + ")" +
                " -SkillDiscoveryDirectory @(" + PowerShellLiteral(sentinelDirectory) + ")";
            var startResult = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-Command", startCommand],
                environment: null,
                timeout: TimeSpan.FromMinutes(1));

            Assert.True(startResult.ExitCode == 0, $"{startResult.StandardOutputText}{startResult.StandardErrorText}");
            Assert.DoesNotContain(sentinel, startResult.StandardOutputText, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, startResult.StandardErrorText, StringComparison.Ordinal);
            Assert.True(File.Exists(statePath), "The wrapper must still write its state file.");
            Assert.DoesNotContain(sentinel, File.ReadAllText(statePath), StringComparison.Ordinal);
            var logFiles = Directory.EnumerateFiles(logs).ToArray();
            Assert.NotEmpty(logFiles);
            foreach (var logFile in logFiles)
            {
                Assert.DoesNotContain(sentinel, File.ReadAllText(logFile), StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SkillDiscoveryTaskStateReaderNeverLeaksSuppliedRoots()
    {
        const string sentinel = "SENTINEL";
        var sentinelProjectPath = @"C:\SENTINEL_ROOT\project";
        var sentinelDirectory = @"C:\SENTINEL_ROOT\skills";
        var taskCommand = "& 'C:\\safe\\start.ps1' -Url 'http://127.0.0.1:4320' -DbPath 'C:\\safe\\raw-store.db' -Mode 'Published' -InstallRoot 'C:\\safe\\app' -NoBrowser -WaitReady" +
            " -SkillDiscoveryProjectPath @(" + PowerShellLiteral(sentinelProjectPath) + ")" +
            " -SkillDiscoveryDirectory @(" + PowerShellLiteral(sentinelDirectory) + ")";
        var encodedTaskAction = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(taskCommand));

        var command = ". " + PowerShellLiteral(ScriptPath("common.ps1")) + "; " +
            "$task = [pscustomobject]@{ Actions = @([pscustomobject]@{ Arguments = " + PowerShellLiteral(encodedTaskAction) + " }) }; " +
            "$projectState = Get-LocalMonitorTaskSkillDiscoveryProjectPathState -Task $task; " +
            "$directoryState = Get-LocalMonitorTaskSkillDiscoveryDirectoryState -Task $task; " +
            "[pscustomobject]@{ ProjectState = $projectState.State; ProjectCount = $projectState.Count; DirectoryState = $directoryState.State; DirectoryCount = $directoryState.Count } | ConvertTo-Json -Compress";

        var result = RunBoundedProcess(
            PowerShellExecutablePath(),
            ["-NoProfile", "-Command", command],
            environment: null,
            timeout: TimeSpan.FromMinutes(1));

        Assert.True(result.ExitCode == 0, $"{result.StandardOutputText}{result.StandardErrorText}");
        Assert.DoesNotContain(sentinel, result.StandardOutputText, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, result.StandardErrorText, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.StandardOutputText);
        Assert.Equal("present", document.RootElement.GetProperty("ProjectState").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("ProjectCount").GetInt32());
        Assert.Equal("present", document.RootElement.GetProperty("DirectoryState").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("DirectoryCount").GetInt32());
    }

    [Fact]
    public void InstallStartupTaskValidationFailureLeavesExistingTaskUnchangedAndRegistersNothing()
    {
        var root = CreateTemporaryDirectory("cao-skill-discovery-partial-effect-tests");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var unregisterMarker = Path.Combine(root, "unregister-called.txt");
            var registerMarker = Path.Combine(root, "register-called.txt");
            File.Copy(ScriptPath("install-startup-task.ps1"), Path.Combine(scripts, "install-startup-task.ps1"));
            File.Copy(ScriptPath("common.ps1"), Path.Combine(scripts, "common.ps1"));
            File.AppendAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                $script:DefaultDbPath = 'C:\safe\raw-store.db'
                function Get-LocalMonitorDefaultInstallRoot { 'C:\safe\app' }
                function Test-LocalMonitorLoopbackUrl { param([string] $Url) $true }
                function Get-LocalMonitorTask { param([string] $TaskName) [pscustomobject]@{ State = 'Ready' } }
                function Get-LocalMonitorRepoRoot { 'C:\safe\repo' }
                function Get-LocalMonitorPowerShellPath { 'C:\safe\pwsh.exe' }
                function Unregister-ScheduledTask { param([string] $TaskName, [switch] $Confirm) [System.IO.File]::WriteAllText('{{unregisterMarker.Replace("'", "''", StringComparison.Ordinal)}}', 'called') }
                function New-ScheduledTaskAction { param([string] $Execute, [string] $Argument, [string] $WorkingDirectory) [pscustomobject]@{} }
                function New-ScheduledTaskTrigger { [pscustomobject]@{} }
                function New-ScheduledTaskPrincipal { [pscustomobject]@{} }
                function New-ScheduledTaskSettingsSet { [pscustomobject]@{} }
                function Register-ScheduledTask { [System.IO.File]::WriteAllText('{{registerMarker.Replace("'", "''", StringComparison.Ordinal)}}', 'called'); [pscustomobject]@{} }
                """);

            var installScript = Path.Combine(scripts, "install-startup-task.ps1").Replace("'", "''", StringComparison.Ordinal);
            var result = RunBoundedProcess(
                PowerShellExecutablePath(),
                ["-NoProfile", "-Command", $"& '{installScript}' -Force -SanitizedOnly -SkillDiscoveryProjectPath @('C:\\skills\\one')"],
                environment: null,
                timeout: TimeSpan.FromMinutes(1));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("skill discovery options cannot be used with --sanitized-only.", result.StandardErrorText, StringComparison.Ordinal);
            Assert.False(File.Exists(unregisterMarker), "Validation failure must not unregister the existing task.");
            Assert.False(File.Exists(registerMarker), "Validation failure must not register a new task.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string? InvokeSkillDiscoveryValidation(
        IEnumerable<string?> projectPaths,
        IEnumerable<string?> directories,
        bool sanitizedOnly,
        bool reverseParameterOrder = false)
    {
        var projectPathLiteral = BuildPowerShellArrayLiteral(projectPaths);
        var directoryLiteral = BuildPowerShellArrayLiteral(directories);
        var sanitizedArgument = sanitizedOnly ? " -SanitizedOnly" : string.Empty;
        var callArguments = reverseParameterOrder
            ? $"-SkillDiscoveryDirectory {directoryLiteral} -SkillDiscoveryProjectPath {projectPathLiteral}{sanitizedArgument}"
            : $"-SkillDiscoveryProjectPath {projectPathLiteral} -SkillDiscoveryDirectory {directoryLiteral}{sanitizedArgument}";
        var command = $". {PowerShellLiteral(ScriptPath("common.ps1"))}; $result = Test-LocalMonitorSkillDiscoveryArguments {callArguments}; if ($null -eq $result) {{ '<null>' }} else {{ $result }}";

        var result = RunBoundedProcess(
            PowerShellExecutablePath(),
            ["-NoProfile", "-Command", command],
            environment: null,
            timeout: TimeSpan.FromMinutes(1));

        Assert.True(result.ExitCode == 0, $"validation probe failed: {result.StandardOutputText}{result.StandardErrorText}");
        var output = result.StandardOutputText.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        return output == "<null>" ? null : output;
    }

    private static string BuildPowerShellArrayLiteral(IEnumerable<string?> values)
    {
        var items = values.Select(value => value is null ? "$null" : PowerShellLiteral(value));
        return "@(" + string.Join(",", items) + ")";
    }

    private static void AssertScriptContains(string script, string expected)
    {
        Assert.Contains(expected, File.ReadAllText(ScriptPath(script)), StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output, string Error, bool ReadyProbeObserved, bool StatePublishedBeforeLive, bool StateExists, bool ProcessTerminated, string? StateContent) RunPublishedStartWithHealth(string healthStatus)
    {
        var root = CreateTemporaryDirectory("cao-published-readiness");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;
            var logs = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
            var database = Path.Combine(root, "raw-store.db");
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            var executable = Path.Combine(installRoot, "CopilotAgentObservability.LocalMonitor.exe");
            File.WriteAllText(executable, string.Empty);
            var readyProbeMarker = Path.Combine(root, "ready-probed");
            var statePath = Path.Combine(root, "local-monitor.state.json");
            var prematureStateMarker = Path.Combine(root, "state-before-live");
            var processTerminatedMarker = Path.Combine(root, "process-terminated");
            if (healthStatus == "child_exit_foreign_state") File.WriteAllText(statePath, "foreign-state");
            var start = Path.Combine(scripts, "start.ps1");
            File.Copy(ScriptPath("start.ps1"), start);
            var readyStatus = healthStatus.StartsWith("existing_", StringComparison.Ordinal)
                ? healthStatus["existing_".Length..]
                : healthStatus;
            var timeoutSeconds = readyStatus is "ready" or "degraded" ? "2" : "0";
            var readyContent = JsonSerializer.Serialize(new { status = readyStatus });
            File.WriteAllText(
                Path.Combine(scripts, "common.ps1"),
                $$"""
                $script:DefaultDbPath = '{{database.Replace("'", "''", StringComparison.Ordinal)}}'
                $script:LogDirectory = '{{logs.Replace("'", "''", StringComparison.Ordinal)}}'
                $script:LiveProbeCount = 0
                function Test-LocalMonitorPricingRegistryOverrideCount { param([string[]] $PricingRegistryOverride) return $true }
                function Test-LocalMonitorSkillDiscoveryArguments { param([string[]] $SkillDiscoveryProjectPath, [string[]] $SkillDiscoveryDirectory, [switch] $SanitizedOnly) return $null }
                function Test-LocalMonitorLoopbackUrl { param([string] $Url) return $true }
                function Initialize-LocalMonitorRuntime { param([string] $DbPath) }
                function Test-LocalMonitorHealth {
                    param([string] $Url, [string] $Path)
                    if ('{{healthStatus}}' -eq 'unreachable' -or '{{healthStatus}}' -eq 'child_exit') { return $null }
                    if ('{{healthStatus}}' -eq 'child_exit_after_probe') {
                        if ($null -ne $script:StartedProcess) { $script:StartedProcess.HasExited = $true }
                        return $null
                    }
                    if ('{{healthStatus}}' -eq 'child_exit_after_live' -and $null -ne $script:StartedProcess -and $Path -eq '/health/live') {
                        $script:StartedProcess.HasExited = $true
                        return [pscustomobject]@{ StatusCode = 200; Content = '{}' }
                    }
                    if ('{{healthStatus}}' -eq 'child_exit_after_state' -and $null -ne $script:StartedProcess -and $Path -eq '/health/ready') {
                        $script:StartedProcess.HasExited = $true
                        return $null
                    }
                    if ('{{healthStatus}}' -eq 'timeout_cleanup_failure_after_state' -and $Path -eq '/health/ready') { return $null }
                    if ('{{healthStatus}}'.StartsWith('existing_') -and $Path -eq '/health/live') {
                        return [pscustomobject]@{ StatusCode = 200; Content = '{}' }
                    }
                    if ($Path -eq '/health/live') {
                        $script:LiveProbeCount++
                        if ($script:LiveProbeCount -eq 1) { return $null }
                        if (Test-Path -LiteralPath '{{statePath.Replace("'", "''", StringComparison.Ordinal)}}') {
                            [System.IO.File]::WriteAllText('{{prematureStateMarker.Replace("'", "''", StringComparison.Ordinal)}}', 'published')
                        }
                        return [pscustomobject]@{ StatusCode = 200; Content = '{}' }
                    }
                    if ($Path -ne '/health/ready') { throw "unexpected_health_path:$Path" }
                    [System.IO.File]::WriteAllText('{{readyProbeMarker.Replace("'", "''", StringComparison.Ordinal)}}', 'probed')
                    if ('{{readyStatus}}' -eq 'unreachable') { return $null }
                    return [pscustomobject]@{ StatusCode = 200; Content = '{{readyContent.Replace("'", "''", StringComparison.Ordinal)}}' }
                }
                function Test-LocalMonitorPortInUse { param([string] $Url) return $false }
                function Get-LocalMonitorDefaultInstallRoot { return '{{installRoot.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Get-LocalMonitorPublishedExePath { param([string] $InstallRoot) return '{{executable.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Start-LocalMonitorProcess {
                    param(
                        [string] $FilePath,
                        [object[]] $ArgumentList,
                        [string] $WorkingDirectory,
                        [string] $StandardOutputPath,
                        [string] $StandardErrorPath)
                    $script:StartedProcess = [pscustomobject]@{
                        Id = 4242
                        HasExited = ('{{healthStatus}}' -eq 'child_exit' -or '{{healthStatus}}' -eq 'child_exit_foreign_state')
                        ExitCode = if ('{{healthStatus}}' -eq 'child_exit' -or '{{healthStatus}}' -eq 'child_exit_foreign_state') { 17 } else { 0 }
                    }
                    $script:StartedProcess | Add-Member -MemberType ScriptMethod -Name Kill -Value { param([bool] $EntireProcessTree) if ('{{healthStatus}}' -ne 'timeout_cleanup_failure_after_state') { $this.HasExited = $true; [System.IO.File]::WriteAllText('{{processTerminatedMarker.Replace("'", "''", StringComparison.Ordinal)}}', 'terminated') } }
                    $script:StartedProcess | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param([int] $Milliseconds) return '{{healthStatus}}' -ne 'timeout_cleanup_failure_after_state' }
                    return $script:StartedProcess
                }
                function Save-LocalMonitorState {
                    param(
                        [int] $ProcessId,
                        [string] $Url,
                        [string] $DbPath,
                        [string] $Mode,
                        [string] $RepoRoot,
                        [string] $InstallRoot,
                        [string] $ExecutablePath,
                        [switch] $SanitizedOnly)
                    [System.IO.File]::WriteAllText(
                        '{{statePath.Replace("'", "''", StringComparison.Ordinal)}}',
                        (@{ process_id = $ProcessId } | ConvertTo-Json -Compress))
                }
                function Get-LocalMonitorState {
                    if (-not (Test-Path -LiteralPath '{{statePath.Replace("'", "''", StringComparison.Ordinal)}}')) { return $null }
                    $content = Get-Content -Raw -LiteralPath '{{statePath.Replace("'", "''", StringComparison.Ordinal)}}'
                    try { return $content | ConvertFrom-Json } catch { return $null }
                }
                function Remove-LocalMonitorState { Remove-Item -LiteralPath '{{statePath.Replace("'", "''", StringComparison.Ordinal)}}' -ErrorAction SilentlyContinue }
                function Write-LocalMonitorLog { param([string] $Message) }
                """);

            var result = RunPowerShellScript(
                start,
                "-Mode", "Published",
                "-Url", "http://127.0.0.1:4320",
                "-DbPath", database,
                "-InstallRoot", installRoot,
                "-NoBrowser",
                "-WaitReady",
                "-TimeoutSeconds", timeoutSeconds);
            return (
                result.ExitCode,
                result.Output,
                result.Error,
                File.Exists(readyProbeMarker),
                File.Exists(prematureStateMarker),
                File.Exists(statePath),
                File.Exists(processTerminatedMarker),
                File.Exists(statePath) ? File.ReadAllText(statePath) : null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ScriptPath(string fileName)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "local-monitor", fileName));
    }

    private static string ConfigCliProjectPath => Path.Combine(
        RepositoryRoot,
        "src",
        "CopilotAgentObservability.ConfigCli",
        "CopilotAgentObservability.ConfigCli.csproj");

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));

    private static string TestScriptPath(string fileName)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "test", fileName));
    }

    private static string WorkflowPath(string fileName)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".github", "workflows", fileName));
    }

    private static (int ExitCode, string Output, string Error) RunPowerShellParser(string scriptPath)
    {
        var escapedPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh",
            ArgumentList =
            {
                "-NoProfile",
                "-Command",
                "$tokens=$null; $errors=$null; [System.Management.Automation.Language.Parser]::ParseFile('" + escapedPath + "', [ref]$tokens, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start pwsh.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static (int ExitCode, string Output, string Error) RunPowerShellScript(string scriptPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pwsh.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static string PowerShellExecutablePath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, "pwsh.exe");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new InvalidOperationException("pwsh.exe was not found on PATH.");
    }

    private static IEnumerable<string> InstalledPowerShellHosts()
    {
        var hosts = new List<string>();
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (File.Exists(windowsPowerShell))
        {
            hosts.Add(windowsPowerShell);
        }

        var powerShell = PowerShellExecutablePath();
        if (!hosts.Contains(powerShell, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(powerShell);
        }

        return hosts;
    }

    private static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string StatusTestCommonOverrides(string action)
    {
        return $$"""
            function Get-LocalMonitorState { $null }
            function Get-LocalMonitorTask { [pscustomobject]@{ State = 'Ready'; Actions = @([pscustomobject]@{ Arguments = '{{action}}' }) } }
            function Test-LocalMonitorProcess { $false }
            function Test-LocalMonitorHealth { $null }
            function Get-LocalMonitorPublishedExePath { 'C:\safe\missing.exe' }
            function Get-LocalMonitorAppVersion { '' }
            """;
    }

    private static PackageFileSnapshot[] SnapshotPackageTree(string packageRoot)
    {
        return Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                return new PackageFileSnapshot(
                    Path.GetRelativePath(packageRoot, path).Replace('\\', '/'),
                    stream.Length,
                    Convert.ToHexString(SHA256.HashData(stream)));
            })
            .OrderBy(snapshot => snapshot.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static ProcessResult RunBoundedProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        using var timeoutSource = new CancellationTokenSource(timeout);
        var outputCopy = process.StandardOutput.BaseStream.CopyToAsync(standardOutput, timeoutSource.Token);
        var errorCopy = process.StandardError.BaseStream.CopyToAsync(standardError, timeoutSource.Token);
        try
        {
            process.WaitForExitAsync(timeoutSource.Token).GetAwaiter().GetResult();
            Task.WhenAll(outputCopy, errorCopy).WaitAsync(timeoutSource.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            throw new TimeoutException($"{fileName} exceeded the {timeout} process and output bound.");
        }

        return new ProcessResult(process.ExitCode, standardOutput.ToArray(), standardError.ToArray());
    }

    private static string CreateTemporaryDirectory(string prefix = "cao-hook-tests")
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RuntimeFileFingerprint(string runtimeRoot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in new[]
                 {
                     Path.Combine(runtimeRoot, "local-monitor.state.json"),
                     Path.Combine(runtimeRoot, "local-monitor.pid"),
                 }.Concat(Directory.Exists(Path.Combine(runtimeRoot, "logs"))
                     ? Directory.EnumerateFiles(Path.Combine(runtimeRoot, "logs"), "*", SearchOption.AllDirectories)
                     : []))
        {
            var relativePath = Path.GetRelativePath(runtimeRoot, path);
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hash.AppendData(SHA256.HashData(stream));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static (Process Process, Task<string> Output, Task<string> Error) StartPowerShellScript(
        string scriptPath,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellExecutablePath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pwsh.");
        return (process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
    }

    private static async Task WaitForRuntimeReady(string runtimeRoot, string url, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (!timeoutSource.IsCancellationRequested)
        {
            if (File.Exists(Path.Combine(runtimeRoot, "local-monitor.state.json"))
                && File.Exists(Path.Combine(runtimeRoot, "local-monitor.pid")))
            {
                try
                {
                    using var response = await client.GetAsync(url + "/health/ready", timeoutSource.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException) when (!timeoutSource.IsCancellationRequested)
                {
                }
            }
            await Task.Delay(200, timeoutSource.Token);
        }
        throw new TimeoutException("The disposable Local Monitor did not become ready.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> CompletePowerShellScript(
        (Process Process, Task<string> Output, Task<string> Error) invocation,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        await invocation.Process.WaitForExitAsync(timeoutSource.Token);
        var output = await invocation.Output.WaitAsync(timeoutSource.Token);
        var error = await invocation.Error.WaitAsync(timeoutSource.Token);
        var exitCode = invocation.Process.ExitCode;
        return (exitCode, output, error);
    }

    private static Process CaptureOwnedRuntimeProcess(string runtimeRoot, Process wrapper, string url, string? dbPath = null)
    {
        var statePath = Path.Combine(runtimeRoot, "local-monitor.state.json");
        using var state = JsonDocument.Parse(File.ReadAllText(statePath));
        var processId = state.RootElement.GetProperty("process_id").GetInt32();
        var command = $"Get-CimInstance Win32_Process -Filter 'ProcessId = {processId}' | Select-Object ProcessId,ParentProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Compress";
        var result = RunBoundedProcess(PowerShellExecutablePath(), ["-NoProfile", "-Command", command], environment: null, timeout: TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        using var cim = JsonDocument.Parse(result.StandardOutputText);
        Assert.Equal(wrapper.Id, cim.RootElement.GetProperty("ParentProcessId").GetInt32());
        var executable = Path.GetFullPath(cim.RootElement.GetProperty("ExecutablePath").GetString()!);
        Assert.Equal(DotnetExecutablePath(), executable, ignoreCase: true);
        var arguments = SplitWindowsCommandLine(cim.RootElement.GetProperty("CommandLine").GetString()!);
        var native = arguments.Skip(1).ToArray();
        Assert.Equal("run", native[0]);
        Assert.Equal("--project", native[1]);
        Assert.Equal(LocalMonitorProjectPath, Path.GetFullPath(native[2]), ignoreCase: true);
        Assert.Equal("--", native[3]);
        Assert.Single(native, value => value == "--db");
        Assert.Equal(Path.GetFullPath(dbPath ?? Path.Combine(runtimeRoot, "raw-store.db")), Path.GetFullPath(native[Array.IndexOf(native, "--db") + 1]), ignoreCase: true);
        Assert.Single(native, value => value == "--url");
        Assert.Equal(url, native[Array.IndexOf(native, "--url") + 1]);
        return Process.GetProcessById(processId);
    }

    private static void KillKnownProcessTree(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private static async Task<bool> WaitForExit(Process process, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(timeoutSource.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static async Task WaitForRuntimeProcessChange(string runtimeRoot, int previousProcessId, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (!timeoutSource.IsCancellationRequested)
        {
            try
            {
                using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtimeRoot, "local-monitor.state.json")));
                if (state.RootElement.GetProperty("process_id").GetInt32() != previousProcessId) return;
            }
            catch (IOException) { }
            catch (JsonException) { }
            await Task.Delay(100, timeoutSource.Token);
        }
        throw new TimeoutException("The conflicting monitor did not publish its test-owned process identity.");
    }

    private static async Task<bool> CanConnect(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await client.ConnectAsync("127.0.0.1", port, timeoutSource.Token); return true; }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or OperationCanceledException) { return false; }
    }

    private static string[] SplitWindowsCommandLine(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try
        {
            return Enumerable.Range(0, count)
                .Select(index => System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!)
                .ToArray();
        }
        finally { LocalFree(pointer); }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static string LocalMonitorProjectPath => Path.Combine(RepositoryRoot, "src", "CopilotAgentObservability.LocalMonitor", "CopilotAgentObservability.LocalMonitor.csproj");

    private static string DotnetExecutablePath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, "dotnet.exe"))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .First();
    }

    private static void WriteRuntimeState(string runtimeRoot, int processId, string url)
    {
        var state = new
        {
            process_id = processId,
            url,
            db_path = Path.Combine(runtimeRoot, "raw-store.db"),
            mode = "dotnet-run",
            repo_root = RepositoryRoot,
            install_root = Path.Combine(runtimeRoot, "app"),
            executable_path = "dotnet",
        };
        File.WriteAllText(Path.Combine(runtimeRoot, "local-monitor.state.json"), JsonSerializer.Serialize(state));
        File.WriteAllText(Path.Combine(runtimeRoot, "local-monitor.pid"), processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed record ProcessResult(int ExitCode, byte[] StandardOutputBytes, byte[] StandardErrorBytes)
    {
        public string StandardOutputText => Encoding.UTF8.GetString(StandardOutputBytes);

        public string StandardErrorText => Encoding.UTF8.GetString(StandardErrorBytes);
    }

    private sealed record PackageFileSnapshot(string RelativePath, long Length, string Sha256);
}
