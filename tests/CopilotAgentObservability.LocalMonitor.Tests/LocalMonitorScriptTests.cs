using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

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
    public void SessionHookInstallAndUninstallAreIdempotentInTemporaryHome()
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
            var second = RunPowerShellScript(
                ScriptPath("install-session-hooks.ps1"),
                "-HomeDirectory", home,
                "-InstallRoot", installRoot);

            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);
            var configPath = Path.Combine(home, ".copilot", "hooks", "local-agent-monitor.json");
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(
                "CopilotAgentObservability.LocalMonitor",
                document.RootElement.GetProperty("managed_by").GetString());
            Assert.Equal(7, document.RootElement.GetProperty("hooks").EnumerateObject().Count());
            Assert.Contains("hook-forward", document.RootElement.GetProperty("hooks").GetProperty("SessionStart")[0].GetProperty("command").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("hooks").GetProperty("SessionStart")[0].GetProperty("timeoutSec").GetInt32());

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
    public void SessionHookScriptsNeverOverwriteOrDeleteUnmanagedFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var hooks = Directory.CreateDirectory(Path.Combine(home, ".copilot", "hooks")).FullName;
            var configPath = Path.Combine(hooks, "local-agent-monitor.json");
            const string unmanaged = "{\"version\":1,\"hooks\":{}}";
            File.WriteAllText(configPath, unmanaged);
            var installRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            File.WriteAllText(Path.Combine(installRoot, "CopilotAgentObservability.LocalMonitor.exe"), string.Empty);

            var install = RunPowerShellScript(
                ScriptPath("install-session-hooks.ps1"),
                "-HomeDirectory", home,
                "-InstallRoot", installRoot);
            var uninstall = RunPowerShellScript(
                ScriptPath("uninstall-session-hooks.ps1"),
                "-HomeDirectory", home);

            Assert.NotEqual(0, install.ExitCode);
            Assert.Contains("hook_config_exists_unmanaged", install.Error, StringComparison.Ordinal);
            Assert.NotEqual(0, uninstall.ExitCode);
            Assert.Contains("hook_config_exists_unmanaged", uninstall.Error, StringComparison.Ordinal);
            Assert.Equal(unmanaged, File.ReadAllText(configPath));
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
        Assert.Equal(2, stop.Split("$process.WaitForExit($TimeoutSeconds * 1000)", StringSplitOptions.None).Length - 1);
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

    private static void AssertScriptContains(string script, string expected)
    {
        Assert.Contains(expected, File.ReadAllText(ScriptPath(script)), StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output, string Error, bool ReadyProbeObserved) RunPublishedStartWithHealth(string healthStatus)
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
                function Test-LocalMonitorLoopbackUrl { param([string] $Url) return $true }
                function Initialize-LocalMonitorRuntime { param([string] $DbPath) }
                function Test-LocalMonitorHealth {
                    param([string] $Url, [string] $Path)
                    if ('{{healthStatus}}' -eq 'unreachable') { return $null }
                    if ('{{healthStatus}}'.StartsWith('existing_') -and $Path -eq '/health/live') {
                        return [pscustomobject]@{ StatusCode = 200; Content = '{}' }
                    }
                    if ($Path -eq '/health/live') {
                        $script:LiveProbeCount++
                        if ($script:LiveProbeCount -eq 1) { return $null }
                        return [pscustomobject]@{ StatusCode = 200; Content = '{}' }
                    }
                    [System.IO.File]::WriteAllText('{{readyProbeMarker.Replace("'", "''", StringComparison.Ordinal)}}', 'probed')
                    if ('{{readyStatus}}' -eq 'unreachable') { return $null }
                    return [pscustomobject]@{ StatusCode = 200; Content = '{{readyContent.Replace("'", "''", StringComparison.Ordinal)}}' }
                }
                function Test-LocalMonitorPortInUse { param([string] $Url) return $false }
                function Get-LocalMonitorDefaultInstallRoot { return '{{installRoot.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Get-LocalMonitorPublishedExePath { param([string] $InstallRoot) return '{{executable.Replace("'", "''", StringComparison.Ordinal)}}' }
                function Start-Process {
                    param(
                        [string] $FilePath,
                        [object[]] $ArgumentList,
                        [string] $WorkingDirectory,
                        [string] $WindowStyle,
                        [string] $RedirectStandardOutput,
                        [string] $RedirectStandardError,
                        [switch] $PassThru)
                    return [pscustomobject]@{ Id = 4242 }
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
                }
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
            return (result.ExitCode, result.Output, result.Error, File.Exists(readyProbeMarker));
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

    private sealed record ProcessResult(int ExitCode, byte[] StandardOutputBytes, byte[] StandardErrorBytes)
    {
        public string StandardOutputText => Encoding.UTF8.GetString(StandardOutputBytes);

        public string StandardErrorText => Encoding.UTF8.GetString(StandardErrorBytes);
    }

    private sealed record PackageFileSnapshot(string RelativePath, long Length, string Sha256);
}
