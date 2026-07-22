using System.Globalization;
using System.Text.Json;

namespace CopilotAgentObservability.SanitizedExport;

internal static class RepositoryMetadataProjectionV1
{
    internal static byte[] Serialize(
        string recordId,
        string? sessionId,
        string? traceId,
        string? sourceSurface,
        string? repositoryName,
        string? workspaceLabel,
        string? repoSnapshot,
        DateTimeOffset observedAt,
        string completeness,
        string contentState,
        string retentionState)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", "repository-metadata-projection.v1");
            writer.WriteString("record_id", recordId);
            WriteNullable(writer, "session_id", sessionId);
            WriteNullable(writer, "trace_id", traceId);
            WriteNullable(writer, "source_surface", sourceSurface);
            WriteNullable(writer, "repository_name", repositoryName);
            WriteNullable(writer, "workspace_label", workspaceLabel);
            WriteNullable(writer, "repo_snapshot", repoSnapshot);
            writer.WriteString("observed_at", observedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("completeness", completeness);
            writer.WriteString("content_state", contentState);
            writer.WriteString("retention_state", retentionState);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }
}
