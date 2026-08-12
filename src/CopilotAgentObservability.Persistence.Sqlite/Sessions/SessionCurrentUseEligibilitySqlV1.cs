using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

internal static class SessionCurrentUseEligibilitySqlV1
{
    internal const string EligibleSessionIdsCte = """
        WITH current_session_use_eligibility(session_id) AS (
            SELECT session.session_id
            FROM sessions session
            WHERE session.status IN ('completed','failed')
              AND session.completeness='full'
              AND EXISTS(
                  SELECT 1
                  FROM session_events terminal
                  WHERE terminal.session_id=session.session_id
                    AND terminal.terminal_outcome IS NOT NULL
              )
        )
        """;

    internal static bool Contains(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = EligibleSessionIdsCte + """
            SELECT 1
            FROM current_session_use_eligibility
            WHERE session_id=$session_id;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        return command.ExecuteScalar() is 1L;
    }
}
