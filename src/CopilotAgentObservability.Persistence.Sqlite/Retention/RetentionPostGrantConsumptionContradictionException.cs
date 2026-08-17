using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed class RetentionPostGrantConsumptionContradictionException(Exception innerException)
    : Exception("Persisted data contradicted the post-grant query or mapper shape.", innerException);

internal static class RetentionPostGrantConsumptionContradiction
{
    internal static async ValueTask<T> NormalizeAsync<T>(Func<ValueTask<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is not (5 or 6))
        {
            throw new RetentionPostGrantConsumptionContradictionException(exception);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException)
        {
            throw new RetentionPostGrantConsumptionContradictionException(exception);
        }
    }
}
