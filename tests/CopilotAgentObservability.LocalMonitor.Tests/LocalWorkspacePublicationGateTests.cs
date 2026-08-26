using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspacePublicationGateTests
{
    [Fact]
    public async Task Lease_can_be_released_from_a_different_thread_and_serializes_read_and_write()
    {
        var gate = new LocalWorkspacePublicationGate();
        var readLease = await gate.AcquireReadAsync(CancellationToken.None);
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writer = Task.Run(async () =>
        {
            await using var lease = await gate.AcquireWriteAsync(CancellationToken.None);
            writeEntered.SetResult();
        });

        await Task.Yield();
        Assert.False(writeEntered.Task.IsCompleted);

        await Task.Run(async () => await readLease.DisposeAsync());
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(writeEntered.Task.IsCompletedSuccessfully);
    }
}
