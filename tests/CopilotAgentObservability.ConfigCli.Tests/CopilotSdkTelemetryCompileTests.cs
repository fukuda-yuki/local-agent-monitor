using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class CopilotSdkTelemetryCompileTests
{
    private const int MaximumDiagnosticCharacters = 512;
    private static readonly Regex DiagnosticCode = new(
        @"\b(?:CS|MSB|NU|NETSDK)\d{4,5}\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    [Fact]
    public async Task PinnedGuidanceSample_CompilesWithoutModifyingReferencedProjectGeneratedState()
    {
        var repositoryRoot = FindRepositoryRoot();
        var before = SnapshotReferencedProjectGeneratedState(repositoryRoot);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await CompilePinnedSampleAsync(repositoryRoot, timeout.Token);

        Assert.True(result.ExitCode == 0, result.DiagnosticTail);
        Assert.Equal(before, SnapshotReferencedProjectGeneratedState(repositoryRoot));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(repositoryRoot, "artifacts"),
            "sdk-guidance-compile-*"));
    }

    [Fact]
    public async Task ProcessRunner_RetainsOnlyTheBoundedSanitizedDiagnosticTail()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("[Console]::Out.Write('CS1000 ' + ('x' * 4096) + ' CS9999'); [Console]::Error.Write('MSB1000 ' + ('y' * 4096) + ' MSB9999'); exit 7");

        var result = await RunProcessAsync(startInfo, timeout.Token);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("CS9999", result.DiagnosticTail, StringComparison.Ordinal);
        Assert.Contains("MSB9999", result.DiagnosticTail, StringComparison.Ordinal);
        Assert.DoesNotContain("CS1000", result.DiagnosticTail, StringComparison.Ordinal);
        Assert.DoesNotContain("MSB1000", result.DiagnosticTail, StringComparison.Ordinal);
        Assert.True(result.DiagnosticTail.Length <= MaximumDiagnosticCharacters);
        Assert.DoesNotContain(":\\", result.DiagnosticTail, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> CompilePinnedSampleAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var restoredSdkAsset = FindRestoredSdkAsset(repositoryRoot);
        var temporaryDirectory = Path.Combine(
            repositoryRoot,
            "artifacts",
            $"sdk-guidance-compile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var projectPath = Path.Combine(temporaryDirectory, "PinnedTelemetrySample.csproj");
            var configurationPath = Path.Combine(temporaryDirectory, "NuGet.Config");
            File.WriteAllText(projectPath, CreateProbeProject(restoredSdkAsset));
            File.WriteAllText(Path.Combine(temporaryDirectory, "PinnedTelemetrySample.cs"), CreateCompileProbeSource());
            File.WriteAllText(configurationPath, "<configuration><packageSources><clear /></packageSources></configuration>");

            var restore = await RunDotNetAsync(
                repositoryRoot,
                ["restore", projectPath, "--configfile", configurationPath, "--nologo"],
                cancellationToken);
            if (restore.ExitCode != 0)
            {
                return restore;
            }

            return await RunDotNetAsync(
                repositoryRoot,
                ["build", projectPath, "--no-restore", "--nologo"],
                cancellationToken);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunDotNetAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunProcessAsync(startInfo, cancellationToken);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The compiler process did not start.");
        var standardOutput = ReadSanitizedTailAsync(process.StandardOutput);
        var standardError = ReadSanitizedTailAsync(process.StandardError);
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
            var timeoutDiagnostic = CreateDiagnosticTail(await standardOutput, await standardError);
            throw new TimeoutException($"The SDK compile proof exceeded its bounded process lifetime. {timeoutDiagnostic}");
        }

        return new ProcessResult(
            process.ExitCode,
            CreateDiagnosticTail(await standardOutput, await standardError));
    }

    private static async Task<string> ReadSanitizedTailAsync(StreamReader reader)
    {
        var rawTail = new StringBuilder(MaximumDiagnosticCharacters);
        var buffer = new char[256];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0)
            {
                return SanitizeTail(rawTail.ToString());
            }

            AppendTail(rawTail, buffer.AsSpan(0, read));
        }
    }

    private static void AppendTail(StringBuilder destination, ReadOnlySpan<char> value)
    {
        if (value.Length >= MaximumDiagnosticCharacters)
        {
            destination.Clear();
            destination.Append(value[^MaximumDiagnosticCharacters..]);
            return;
        }

        var excess = destination.Length + value.Length - MaximumDiagnosticCharacters;
        if (excess > 0)
        {
            destination.Remove(0, excess);
        }

        destination.Append(value);
    }

    private static string SanitizeTail(string rawTail)
    {
        var safeTokens = new List<string>();
        foreach (Match match in DiagnosticCode.Matches(rawTail))
        {
            if (!safeTokens.Contains(match.Value, StringComparer.Ordinal))
            {
                safeTokens.Add(match.Value);
            }
        }

        if (rawTail.Contains("Build FAILED", StringComparison.Ordinal))
        {
            safeTokens.Add("build_failed");
        }

        if (rawTail.Contains("Restore failed", StringComparison.Ordinal))
        {
            safeTokens.Add("restore_failed");
        }

        return string.Join(';', safeTokens);
    }

    private static string CreateDiagnosticTail(string standardOutput, string standardError) =>
        AppendTail($"stdout:{standardOutput};stderr:{standardError}", MaximumDiagnosticCharacters);

    private static string AppendTail(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[^maximumLength..];

    private static RestoredSdkAsset FindRestoredSdkAsset(string repositoryRoot)
    {
        var assetsPath = Path.Combine(
            repositoryRoot,
            "src",
            "CopilotAgentObservability.LocalMonitor",
            "obj",
            "project.assets.json");
        if (!File.Exists(assetsPath))
        {
            throw MissingNormalBuildPrerequisite();
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var root = document.RootElement;
        var library = root.GetProperty("libraries")
            .EnumerateObject()
            .SingleOrDefault(property => property.Name.StartsWith("GitHub.Copilot.SDK/", StringComparison.Ordinal));
        if (library.Equals(default(JsonProperty)) ||
            !library.Value.TryGetProperty("path", out var libraryPathElement) ||
            libraryPathElement.GetString() is not { } libraryPath)
        {
            throw MissingNormalBuildPrerequisite();
        }

        foreach (var target in root.GetProperty("targets").EnumerateObject())
        {
            if (!target.Value.TryGetProperty(library.Name, out var targetLibrary) ||
                !targetLibrary.TryGetProperty("compile", out var compile))
            {
                continue;
            }

            foreach (var compileAsset in compile.EnumerateObject().Where(property =>
                         property.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var packageFolder in root.GetProperty("packageFolders").EnumerateObject())
                {
                    var assemblyPath = Path.Combine(
                        packageFolder.Name,
                        libraryPath.Replace('/', Path.DirectorySeparatorChar),
                        compileAsset.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(assemblyPath))
                    {
                        return new RestoredSdkAsset(target.Name, assemblyPath);
                    }
                }
            }
        }

        throw MissingNormalBuildPrerequisite();
    }

    private static InvalidOperationException MissingNormalBuildPrerequisite() => new(
        "The current GitHub.Copilot.SDK compile asset is unavailable. Run `dotnet build CopilotAgentObservability.slnx` before this focused test.");

    private static string CreateProbeProject(RestoredSdkAsset restoredSdkAsset) =>
        new XDocument(
            new XElement(
                "Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement(
                    "PropertyGroup",
                    new XElement("TargetFramework", restoredSdkAsset.TargetFramework),
                    new XElement("Nullable", "enable")),
                new XElement(
                    "ItemGroup",
                    new XElement(
                        "Reference",
                        new XAttribute("Include", "GitHub.Copilot.SDK"),
                        new XElement("HintPath", restoredSdkAsset.AssemblyPath)))))
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

    private static string FindDotNetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private static IReadOnlyList<string> SnapshotReferencedProjectGeneratedState(string repositoryRoot) =>
        new[]
        {
            "CopilotAgentObservability.LocalMonitor",
            "CopilotAgentObservability.Persistence.Sqlite",
            "CopilotAgentObservability.Telemetry",
        }
        .SelectMany(project => new[] { "bin", "obj" }.SelectMany(kind =>
        {
            var directory = Path.Combine(repositoryRoot, "src", project, kind);
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Select(path => $"{project}/{kind}/{Path.GetRelativePath(directory, path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
                : [];
        }))
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    private sealed record RestoredSdkAsset(string TargetFramework, string AssemblyPath);

    private sealed record ProcessResult(int ExitCode, string DiagnosticTail);
}
