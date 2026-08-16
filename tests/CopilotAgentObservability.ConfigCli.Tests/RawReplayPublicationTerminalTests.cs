using CopilotAgentObservability.RawReplay;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RawReplayPublicationTerminalTests
{
    [Fact]
    public async Task TransientSealPermitsExactlyOnePublicationCommit()
    {
        var terminalCalls = 0;
        await using var lease = Lease(operation =>
        {
            Assert.Equal(RawReplaySnapshotTerminalOperation.SealTransientPublication, operation);
            terminalCalls++;
            return terminalCalls == 1
                ? RawReplaySnapshotTerminalResult.Sealed
                : RawReplaySnapshotTerminalResult.Lost;
        });

        Assert.True(lease.TrySealRawReplayTransientPublication(out var firstError));
        Assert.Null(firstError);
        Assert.False(lease.TrySealRawReplayTransientPublication(out var secondError));
        Assert.Equal("snapshot_read_denied", secondError);
        Assert.Equal(2, terminalCalls);
    }

    [Fact]
    public async Task FileSealReturnsASingleUseSameDirectoryNonOverwriteMoveTicket()
    {
        using var directory = new TempDirectory();
        var staged = Path.Combine(directory.Path, "raw-local-replay.zip.owned.partial");
        var output = Path.Combine(directory.Path, "raw-local-replay.zip");
        File.WriteAllBytes(staged, [1, 2, 3]);
        await using var lease = Lease(operation =>
        {
            Assert.Equal(RawReplaySnapshotTerminalOperation.SealFilePublication, operation);
            return RawReplaySnapshotTerminalResult.Sealed;
        });

        Assert.True(lease.TrySealRawReplayFilePublication(staged, output, out var ticket, out var error));
        Assert.Null(error);
        Assert.NotNull(ticket);

        ticket();
        Assert.False(File.Exists(staged));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
        ticket();
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task FileTicketNeverOverwritesAnExistingDestination()
    {
        using var directory = new TempDirectory();
        var staged = Path.Combine(directory.Path, "raw-local-replay.zip.owned.partial");
        var output = Path.Combine(directory.Path, "raw-local-replay.zip");
        File.WriteAllBytes(staged, [1, 2, 3]);
        await using var lease = Lease(_ => RawReplaySnapshotTerminalResult.Sealed);
        Assert.True(lease.TrySealRawReplayFilePublication(staged, output, out var ticket, out _));
        File.WriteAllBytes(output, [9]);

        Assert.Throws<IOException>(ticket!);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(output));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(staged));
        ticket!();
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(output));
    }

    private static RawReplaySnapshotLease Lease(
        Func<RawReplaySnapshotTerminalOperation, RawReplaySnapshotTerminalResult> terminal) =>
        new(Snapshot(), static () => ValueTask.CompletedTask, terminal);

    private static RawReplaySnapshot Snapshot() =>
        new("snapshot", DateTimeOffset.UnixEpoch, "monitor", [], [], []);

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"raw-replay-terminal-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
