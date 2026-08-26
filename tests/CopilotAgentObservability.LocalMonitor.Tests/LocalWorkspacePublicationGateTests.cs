using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspacePublicationGateTests
{
    [Fact]
    public void AuthorityAwareParticipantHasNoProcessGlobalDefault()
    {
        Assert.Null(typeof(LocalWorkspaceProjectionTransactionParticipant).GetProperty(
            "Instance",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic));
    }

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

    [Fact]
    public async Task FailedRegistryPublicationReleasesBarrierWithoutDeadlockAndKeepsOldGeneration()
    {
        var gate = new LocalWorkspacePublicationGate();
        var provider = new SkillInvocationV2RegistryProviderV1(SkillInvocationV2ArtifactRegistry.Load(), gate);
        var original = Assert.IsAssignableFrom<ISkillRegistryGenerationCapture>(provider.CaptureGeneration());
        provider.CurrentGenerationChanging += _ => throw new InvalidOperationException("projection_refresh_failed");

        var publication = Task.Run(() => Assert.Throws<InvalidOperationException>(() =>
            provider.PublishGeneration(SkillInvocationV2ArtifactRegistry.Load())));
        var exception = await publication.WaitAsync(TimeSpan.FromSeconds(5));
        await using var collectionLease = await gate.AcquireReadAsync(CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("projection_refresh_failed", exception.Message);
        Assert.True(provider.TryAcquireGenerationReadLease(original, out var originalLease));
        originalLease!.Dispose();
    }
}
