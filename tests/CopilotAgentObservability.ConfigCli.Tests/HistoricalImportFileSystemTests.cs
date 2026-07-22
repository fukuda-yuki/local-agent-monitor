using CopilotAgentObservability.ConfigCli.HistoricalImport.ClaudeCode;
using CopilotAgentObservability.ConfigCli.HistoricalImport.GitHubCopilot;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using System.Runtime.InteropServices;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class HistoricalImportFileSystemTests
{
    [Fact]
    public void WindowsNetworkVolumeReportedByHost_IsRejectedBeforeFileSystemIo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string path = @"C:\private\historical-import\request.json";

        Assert.False(HistoricalSourcePathPolicy.IsCanonicalNativeAbsolute(
            path,
            windowsDriveType: _ => DriveType.Network));
        Assert.False(HistoricalSourcePathPolicy.IsSafeLocalFileSyntax(
            path,
            _ => DriveType.Network));
    }

    [Fact]
    public void WindowsFixedVolumeReportedByHost_AcceptsCanonicalNativeLocalPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string path = @"C:\private\historical-import\request.json";

        Assert.True(HistoricalSourcePathPolicy.IsCanonicalNativeAbsolute(
            path,
            windowsDriveType: _ => DriveType.Fixed));
        Assert.True(HistoricalSourcePathPolicy.IsSafeLocalFileSyntax(
            path,
            _ => DriveType.Fixed));
        Assert.False(HistoricalSourcePathPolicy.IsSafeLocalFileSyntax(
            "//server/share/request.json",
            _ => DriveType.Fixed));
    }

    [Fact]
    public void RegularFile_IsClassifiedWithoutReadingItsBody()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "transcript.jsonl");
        File.WriteAllText(path, "SENSITIVE_BODY_MUST_NOT_BE_READ");

        var claude = new SystemClaudeHistoricalFileSystem().InspectExactReference(path);
        var github = new SystemGitHubCopilotHistoricalMetadataFileSystem().InspectPath(path);

        Assert.Equal(ClaudeTranscriptReferenceInspection.RegularFile, claude);
        Assert.Equal(GitHubCopilotHistoricalPathKind.RegularFile, github);
    }

    [Fact]
    public void Symlink_IsNeverClassifiedAsARegularHistoricalSourceFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "target.jsonl");
        var link = Path.Combine(temp.Path, "transcript.jsonl");
        File.WriteAllText(target, "SENSITIVE_BODY_MUST_NOT_BE_READ");
        File.CreateSymbolicLink(link, target);

        var claude = new SystemClaudeHistoricalFileSystem().InspectExactReference(link);
        var github = new SystemGitHubCopilotHistoricalMetadataFileSystem().InspectPath(link);

        Assert.Equal(ClaudeTranscriptReferenceInspection.NotRegularFile, claude);
        Assert.Equal(GitHubCopilotHistoricalPathKind.Other, github);
    }

    [Fact]
    public void SymlinkAncestor_IsRejectedBeforeTheFinalHistoricalSourceEntry()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var realDirectory = Path.Combine(temp.Path, "real");
        Directory.CreateDirectory(realDirectory);
        File.WriteAllText(Path.Combine(realDirectory, "transcript.jsonl"), "SENSITIVE_BODY_MUST_NOT_BE_READ");
        var linkedDirectory = Path.Combine(temp.Path, "linked");
        Directory.CreateSymbolicLink(linkedDirectory, realDirectory);
        var reference = Path.Combine(linkedDirectory, "transcript.jsonl");

        var claude = new SystemClaudeHistoricalFileSystem().InspectExactReference(reference);
        var github = new SystemGitHubCopilotHistoricalMetadataFileSystem().InspectPath(reference);

        Assert.Equal(ClaudeTranscriptReferenceInspection.NotRegularFile, claude);
        Assert.Equal(GitHubCopilotHistoricalPathKind.Other, github);
    }

    [Fact]
    public void SymlinkHiddenByDotDotNormalization_IsNotTraversedDuringInspection()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var targetDirectory = Path.Combine(temp.Path, "target");
        var targetChild = Path.Combine(targetDirectory, "child");
        Directory.CreateDirectory(targetChild);
        File.WriteAllText(
            Path.Combine(targetDirectory, "transcript.jsonl"),
            "SENSITIVE_BODY_MUST_NOT_BE_READ");
        var link = Path.Combine(temp.Path, "linked-child");
        Directory.CreateSymbolicLink(link, targetChild);
        var lexicalReference = Path.Combine(link, "..", "transcript.jsonl");

        var claude = new SystemClaudeHistoricalFileSystem().InspectExactReference(lexicalReference);
        var github = new SystemGitHubCopilotHistoricalMetadataFileSystem().InspectPath(lexicalReference);

        Assert.Equal(ClaudeTranscriptReferenceInspection.Missing, claude);
        Assert.Equal(GitHubCopilotHistoricalPathKind.Missing, github);
    }

    [Fact]
    public async Task UnixFifo_IsClassifiedAsSpecialWithoutBlocking()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var temp = new TempDirectory();
        var fifo = Path.Combine(temp.Path, "transcript.pipe");
        Assert.Equal(0, CreateFifo(fifo));

        var inspection = Task.Run(() => (
            Claude: new SystemClaudeHistoricalFileSystem().InspectExactReference(fifo),
            GitHub: new SystemGitHubCopilotHistoricalMetadataFileSystem().InspectPath(fifo)));

        var result = await inspection.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ClaudeTranscriptReferenceInspection.NotRegularFile, result.Claude);
        Assert.Equal(GitHubCopilotHistoricalPathKind.Other, result.GitHub);
    }

    private static int CreateFifo(string path) => OperatingSystem.IsMacOS()
        ? MkFifoMacOs(path, 0x180)
        : MkFifoLinux(path, 0x180);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifoLinux(string path, uint mode);

    [DllImport("libSystem.B.dylib", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifoMacOs(string path, uint mode);

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            var temporaryRoot = System.IO.Path.GetTempPath();
            if (OperatingSystem.IsMacOS() && temporaryRoot.StartsWith("/var/", StringComparison.Ordinal))
            {
                temporaryRoot = "/private" + temporaryRoot;
            }

            Path = System.IO.Path.Combine(
                temporaryRoot,
                $"historical-import-filesystem-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
