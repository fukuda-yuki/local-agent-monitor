using System.Diagnostics;
using System.Xml.Linq;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class CopilotSdkTelemetryCompileTests
{
    [Fact]
    public async Task PinnedGuidanceSample_RestoresAndCompilesAgainstTheRepositorySdkProjectReference()
    {
        var repositoryRoot = FindRepositoryRoot();
        var localMonitorProject = Path.Combine(
            repositoryRoot,
            "src",
            "CopilotAgentObservability.LocalMonitor",
            "CopilotAgentObservability.LocalMonitor.csproj");
        var targetFramework = ReadTargetFramework(localMonitorProject);
        var temporaryDirectory = Path.Combine(
            repositoryRoot,
            "artifacts",
            $"sdk-guidance-compile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var projectPath = Path.Combine(temporaryDirectory, "PinnedTelemetrySample.csproj");
            var packageCache = Path.Combine(temporaryDirectory, "packages");
            File.WriteAllText(projectPath, CreateProbeProject(targetFramework, localMonitorProject));
            File.WriteAllText(Path.Combine(temporaryDirectory, "PinnedTelemetrySample.cs"), CreateCompileProbeSource());

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var result = await BuildWithFreshPackageCacheAsync(
                repositoryRoot,
                projectPath,
                packageCache,
                timeout.Token);

            Assert.Equal(0, result.ExitCode);
            Assert.True(Directory.Exists(Path.Combine(packageCache, "github.copilot.sdk")));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<DotNetBuildResult> BuildWithFreshPackageCacheAsync(
        string repositoryRoot,
        string projectPath,
        string packageCache,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(FindDotNetHost())
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["NUGET_PACKAGES"] = packageCache;
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-p:RestoreForceEvaluate=true");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet build did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            throw new TimeoutException("The repository SDK compile proof exceeded its bounded process lifetime.");
        }

        await Task.WhenAll(standardOutput, standardError);
        return new DotNetBuildResult(process.ExitCode);
    }

    private static string CreateProbeProject(string targetFramework, string localMonitorProject) =>
        new XDocument(
            new XElement(
                "Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement(
                    "PropertyGroup",
                    new XElement("TargetFramework", targetFramework),
                    new XElement("Nullable", "enable")),
                new XElement(
                    "ItemGroup",
                    new XElement("ProjectReference", new XAttribute("Include", localMonitorProject)))))
        .ToString();

    private static string CreateCompileProbeSource() => $$"""
        using GitHub.Copilot;

        internal static class PinnedTelemetrySample
        {
            private static readonly CopilotClientOptions Options = {{PinnedGuidanceSample()}};
        }
        """;

    private static string PinnedGuidanceSample() =>
        SetupContractValidator.RehydrateStatusGuidance(
            new SetupStatusGuidance("caller_managed_sample", "dotnet")).Sample;

    private static string FindRepositoryRoot()
    {
        foreach (var startingDirectory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startingDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
    }

    private static string ReadTargetFramework(string localMonitorProject)
    {
        var project = XDocument.Load(localMonitorProject);
        return project.Descendants("TargetFramework")
            .Single()
            .Value
            .Trim();
    }

    private static string FindDotNetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private sealed record DotNetBuildResult(int ExitCode);
}
