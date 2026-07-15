using System.Diagnostics;
using System.Xml.Linq;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class CopilotSdkTelemetryCompileTests
{
    [Fact]
    public void PinnedGuidanceSample_CompilesAgainstTheRepositorySdkPackage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageVersion = ReadRepositorySdkPackageVersion(repositoryRoot);
        var sdkAssembly = Path.Combine(
            GetNuGetPackagesRoot(),
            "github.copilot.sdk",
            packageVersion.ToLowerInvariant(),
            "lib",
            "net10.0",
            "GitHub.Copilot.SDK.dll");
        Assert.True(File.Exists(sdkAssembly), "The restored repository SDK assembly was not found.");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"cao-sdk-guidance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var sourcePath = Path.Combine(temporaryDirectory, "PinnedTelemetrySample.cs");
            File.WriteAllText(sourcePath, CreateCompileProbeSource());

            var compiler = FindCSharpCompiler();
            var startInfo = new ProcessStartInfo(FindDotNetHost())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(compiler);
            startInfo.ArgumentList.Add("/noconfig");
            startInfo.ArgumentList.Add("/target:library");
            startInfo.ArgumentList.Add("/langversion:latest");
            startInfo.ArgumentList.Add($"/out:{Path.Combine(temporaryDirectory, "PinnedTelemetrySample.dll")}");
            foreach (var assembly in TrustedPlatformAssemblies())
            {
                startInfo.ArgumentList.Add($"/r:{assembly}");
            }

            startInfo.ArgumentList.Add($"/r:{sdkAssembly}");
            startInfo.ArgumentList.Add(sourcePath);
            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            process.WaitForExit();
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            Assert.True(process.ExitCode == 0, output);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

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

    private static string ReadRepositorySdkPackageVersion(string repositoryRoot)
    {
        var project = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "CopilotAgentObservability.LocalMonitor",
            "CopilotAgentObservability.LocalMonitor.csproj"));
        var reference = project.Descendants("PackageReference")
            .Single(element => string.Equals(
                (string?)element.Attribute("Include"),
                "GitHub.Copilot.SDK",
                StringComparison.OrdinalIgnoreCase));
        return (string?)reference.Attribute("Version")
            ?? throw new InvalidOperationException("GitHub.Copilot.SDK must have an explicit version.");
    }

    private static string GetNuGetPackagesRoot() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    private static string FindDotNetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private static string FindCSharpCompiler()
    {
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new InvalidOperationException("Runtime directory was not found.");
        var dotnetRoot = Directory.GetParent(runtimeDirectory)?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("dotnet root was not found.");
        var compiler = Directory.EnumerateDirectories(Path.Combine(dotnetRoot, "sdk"))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Select(directory => Path.Combine(directory, "Roslyn", "bincore", "csc.dll"))
            .FirstOrDefault(File.Exists);
        return compiler ?? throw new InvalidOperationException("C# compiler was not found.");
    }

    private static IReadOnlyList<string> TrustedPlatformAssemblies() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Trusted platform assembly list was not found."))
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}
