namespace CopilotAgentObservability.LocalMonitor.Tests;

internal readonly record struct BlockingActor(Task Completion, Task Entered);

internal readonly record struct BlockingActor<TResult>(Task<TResult> Completion, Task Entered);

internal static class BlockingTestActor
{
    public static BlockingActor Start(Action actor)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Factory.StartNew(
            () =>
            {
                entered.SetResult();
                actor();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        return new BlockingActor(completion, entered.Task);
    }

    public static BlockingActor<TResult> Start<TResult>(Func<TResult> actor)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Factory.StartNew(
            () =>
            {
                entered.SetResult();
                return actor();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        return new BlockingActor<TResult>(completion, entered.Task);
    }
}
