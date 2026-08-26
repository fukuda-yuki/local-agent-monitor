using System.Threading;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class SqliteCommandExecutionObserver
{
    private static readonly AsyncLocal<Action?> Current = new();

    internal static IDisposable Begin(Action observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        var previous = Current.Value;
        Current.Value = observe;
        return new Scope(previous);
    }

    internal static void Executing() => Current.Value?.Invoke();

    private sealed class Scope(Action? previous) : IDisposable
    {
        private Action? restore = previous;
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                Current.Value = Interlocked.Exchange(ref restore, null);
        }
    }
}
