using System.Diagnostics;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class LocalMonitorScriptTests
{
    private static readonly string[] RequiredScripts =
    [
        "common.ps1",
        "install.ps1",
        "package-release.ps1",
        "start.ps1",
        "stop.ps1",
        "status.ps1",
        "set-startup-task.ps1",
        "install-startup-task.ps1",
        "uninstall-startup-task.ps1",
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
        Assert.Contains("Compress-Archive", package, StringComparison.Ordinal);
        Assert.Contains("$LASTEXITCODE", package, StringComparison.Ordinal);
        Assert.Contains("dotnet_publish_failed", package, StringComparison.Ordinal);
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
        Assert.Contains("-RunLevel LeastPrivilege", install);
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
        AssertScriptContains("set-startup-task.ps1", "Disable-ScheduledTask");
        AssertScriptContains("set-startup-task.ps1", "Enable-ScheduledTask");
        AssertScriptContains("uninstall-startup-task.ps1", "$StopRunning");
        AssertScriptContains("uninstall-startup-task.ps1", "$RemoveData");
        AssertScriptContains("uninstall-startup-task.ps1", "$InstallRoot");
        AssertScriptContains("package-release.ps1", "$RuntimeIdentifier");
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

    private static string ScriptPath(string fileName)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "local-monitor", fileName));
    }

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
}
