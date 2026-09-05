using System.Diagnostics;
using System.Text;
using CopilotAgentObservability.Telemetry.Repositories;

namespace CopilotAgentObservability.LocalMonitor.Repositories;

internal static class CopilotCliNativeRepositoryLocatorResolver
{
    private const int MaximumWorkspaceBytes = 4096;

    internal static ILocalRepositoryLocator? Resolve(string nativeSessionId)
        => Resolve(nativeSessionId, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ResolveGitRemotes);

    internal static ILocalRepositoryLocator? Resolve(
        string nativeSessionId,
        string userProfile,
        Func<string, GitRepositoryFacts?> gitResolver)
    {
        if (!Guid.TryParseExact(nativeSessionId, "D", out _) || nativeSessionId != nativeSessionId.ToLowerInvariant())
            return null;
        if (string.IsNullOrWhiteSpace(userProfile) || !Path.IsPathFullyQualified(userProfile))
            return null;
        var stateRoot = Path.Combine(userProfile, ".copilot", "session-state");
        var workspacePath = Path.Combine(stateRoot, nativeSessionId, "workspace.yaml");
        string text;
        try
        {
            using var stream = new FileStream(workspacePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var bytes = new byte[MaximumWorkspaceBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count is 0 or > MaximumWorkspaceBytes) return null;
            text = new UTF8Encoding(false, true).GetString(bytes, 0, count);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }

        if (!TryReadPlainTopLevelScalar(text, "id", out var storedId)
            || !string.Equals(storedId, nativeSessionId, StringComparison.Ordinal)
            || !TryReadPlainTopLevelScalar(text, "cwd", out var cwd)
            || !Path.IsPathFullyQualified(cwd))
            return null;

        var facts = gitResolver(cwd);
        if (facts is null) return null;
        var parsed = facts.RemoteUrls
            .Select(value => GitHubRepositoryLocatorParser.TryParse(value, out var locator) ? locator : null)
            .Where(locator => locator is not null)
            .GroupBy(locator => locator!.LocatorSha256, StringComparer.Ordinal)
            .Select(group => group.First()!)
            .Take(2)
            .ToArray();
        if (parsed.Length == 0)
        {
            var common = Path.TrimEndingDirectorySeparator(facts.CommonDirectory);
            var leaf = Path.GetFileName(common);
            var label = string.Equals(leaf, ".git", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(Path.GetDirectoryName(common))
                : leaf;
            return LocalGitRepositoryLocator.Create(facts.CommonDirectory, label ?? string.Empty);
        }
        return parsed.Length == 1 ? parsed[0] : null;
    }

    private static GitRepositoryFacts? ResolveGitRemotes(string cwd)
    {
        var commonOutput = RunGit(cwd, ["rev-parse", "--path-format=absolute", "--git-common-dir"]);
        if (commonOutput is null || !TryReadSingleOutputLine(commonOutput, out var commonDirectory)) return null;
        var remotes = RunGit(cwd, ["config", "--local", "--null", "--get-regexp", "^remote\\..*\\.url$"], allowNoMatch: true);
        if (remotes is null) return null;
        if (!TryReadNullTerminatedConfig(remotes, out var remoteUrls)) return null;
        return new(commonDirectory, remoteUrls);
    }

    private static string? RunGit(string cwd, IReadOnlyList<string> arguments, bool allowNoMatch = false)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-C");
            process.StartInfo.ArgumentList.Add(cwd);
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2000) || process.ExitCode != 0 && !(allowNoMatch && process.ExitCode == 1))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit(2000);
                return null;
            }
            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            return output;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal sealed record GitRepositoryFacts(string CommonDirectory, IReadOnlyList<string> RemoteUrls);

    private static bool TryReadSingleOutputLine(string output, out string value)
    {
        value = output.EndsWith("\r\n", StringComparison.Ordinal) ? output[..^2]
            : output.EndsWith('\n') ? output[..^1]
            : output;
        return value.Length > 0 && value.IndexOfAny(['\r', '\n', '\0']) < 0;
    }

    private static bool TryReadNullTerminatedConfig(string output, out IReadOnlyList<string> values)
    {
        values = [];
        if (output.Length == 0) return true;
        if (!output.EndsWith('\0')) return false;
        var parsed = new List<string>();
        foreach (var record in output.Split('\0')[..^1])
        {
            var separator = record.IndexOf('\n');
            if (separator <= 0 || separator == record.Length - 1 || record[..separator].IndexOfAny(['\r', '\n', '\0']) >= 0)
                return false;
            parsed.Add(record[(separator + 1)..]);
        }
        values = parsed.AsReadOnly();
        return true;
    }

    private static bool TryReadPlainTopLevelScalar(string text, string key, out string value)
    {
        value = string.Empty;
        var prefix = key + ":";
        var matches = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..].Trim())
            .ToArray();
        if (matches.Length != 1 || matches[0].Length == 0 || matches[0][0] is '\'' or '"' || matches[0].Contains(" #", StringComparison.Ordinal))
            return false;
        value = matches[0];
        return true;
    }
}
