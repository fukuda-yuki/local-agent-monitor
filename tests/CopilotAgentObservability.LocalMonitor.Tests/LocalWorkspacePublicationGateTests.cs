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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistryPublishGenerationCommitsOrRollsBackPointerAndV4RowsTogether(bool injectFailure)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            """);
        var gate = new LocalWorkspacePublicationGate();
        var provider = new SkillInvocationV2RegistryProviderV1(SkillInvocationV2ArtifactRegistry.Load(), gate);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
        var original = Assert.IsAssignableFrom<ISkillRegistryGenerationCapture>(provider.CaptureGeneration());
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "DELETE FROM local_workspace_execution_headers;");
        provider.CurrentGenerationChanging += authority =>
        {
            using var transaction = connection.BeginTransaction();
            LocalWorkspaceProjectionStore.Refresh(connection, transaction, DateTimeOffset.UnixEpoch, authority);
            if (injectFailure)
                throw new InvalidOperationException("injected_registry_projection_failure");
            transaction.Commit();
        };

        if (injectFailure)
            Assert.Throws<InvalidOperationException>(() => provider.PublishGeneration(SkillInvocationV2ArtifactRegistry.Load()));
        else
            provider.PublishGeneration(SkillInvocationV2ArtifactRegistry.Load());

        var current = Assert.IsAssignableFrom<ISkillRegistryGenerationCapture>(provider.CaptureGeneration());
        Assert.Equal(injectFailure, provider.TryAcquireGenerationReadLease(original, out var oldLease));
        oldLease?.Dispose();
        Assert.True(provider.TryAcquireGenerationReadLease(current, out var currentLease));
        currentLease?.Dispose();
        Assert.Equal(injectFailure ? ["0"] : ["1"], LocalWorkspaceProjectionSchemaTests.Strings(connection,
            "SELECT CAST(COUNT(*) AS TEXT) FROM local_workspace_execution_headers;"));
    }
}
