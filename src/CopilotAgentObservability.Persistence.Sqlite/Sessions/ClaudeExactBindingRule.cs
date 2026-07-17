using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CopilotAgentObservability.Persistence.Sqlite.Sessions;

internal sealed class ClaudeExactBindingRule
{
    private readonly string databasePath;

    public ClaudeExactBindingRule(string databasePath)
    {
        this.databasePath = databasePath;
    }

    public Guid? Resolve(string? payloadJson, string traceId, string spanId)
    {
        if (payloadJson is null)
        {
            return null;
        }

        var nativeSessionId = ReadNativeSessionId(payloadJson, traceId, spanId);
        return nativeSessionId is null ? null : FindSession(nativeSessionId);
    }

    private Guid? FindSession(string nativeSessionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.session_id,n.binding_kind
            FROM session_native_ids n JOIN sessions s ON s.session_id=n.session_id
            WHERE n.source_surface='claude-code' AND n.native_session_id=$native_session_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$native_session_id", nativeSessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var sessionId = Guid.Parse(reader.GetString(0));
        var bindingKind = SessionWire.ParseBindingKind(reader.GetString(1));
        if (reader.Read() || bindingKind is not (SessionBindingKind.Native or SessionBindingKind.ExplicitResume or SessionBindingKind.ExplicitHandoff))
        {
            return null;
        }

        return sessionId;
    }

    private static string? ReadNativeSessionId(string payloadJson, string traceId, string spanId)
    {
        using var document = JsonDocument.Parse(payloadJson);
        string? result = null;
        var matchingSpanCount = 0;
        foreach (var resourceSpan in OtlpSpanReader.EnumerateArrayProperty(document.RootElement, "resourceSpans"))
        {
            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    if (!string.Equals(OtlpSpanReader.ReadString(span, "traceId"), traceId, StringComparison.Ordinal)
                        || !string.Equals(OtlpSpanReader.ReadString(span, "spanId"), spanId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matchingSpanCount++;
                    if (!span.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var attribute in attributes.EnumerateArray())
                    {
                        if (!string.Equals(OtlpSpanReader.ReadString(attribute, "key"), "session.id", StringComparison.Ordinal)
                            || !attribute.TryGetProperty("value", out var value)
                            || !value.TryGetProperty("stringValue", out var stringValue)
                            || stringValue.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        if (result is not null)
                        {
                            return null;
                        }

                        result = stringValue.GetString();
                    }
                }
            }
        }

        return matchingSpanCount == 1 ? result : null;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }
}
