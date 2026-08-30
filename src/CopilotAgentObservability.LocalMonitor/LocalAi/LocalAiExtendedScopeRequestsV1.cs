using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed record LocalAiRepositoryRunRequestV1(string SnapshotId, string PayloadSha256, int TimeoutSeconds);
internal sealed record LocalAiComparisonRunRequestV1(string RepositoryId, string ComparisonId, int TimeoutSeconds);
internal sealed record LocalAiRepositoryPreviewRequestV1(string RepositoryId,string Kind,string ArchiveScope,IReadOnlyList<string> SessionIds,LocalMonitorV1SessionSearchRequest? Filter);
internal enum LocalAiPreviewParseStatus { Success,InvalidRequest,ScopeTooLarge }

internal static class LocalAiExtendedScopeRequestParser
{
    internal static LocalAiPreviewParseStatus TryRepositoryPreview(ReadOnlyMemory<byte> bytes,out LocalAiRepositoryPreviewRequestV1? request)
    {
        request=null; try
        {
            using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=8});var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!ExactNames(root,["schema_version","repository_id","selection"])||!ExactString(root,"schema_version","local-ai-repository-preview.request.v1")||!TryUuid(root,"repository_id",out var repositoryId))return LocalAiPreviewParseStatus.InvalidRequest;
            var selection=root.GetProperty("selection");if(selection.ValueKind!=JsonValueKind.Object||!selection.TryGetProperty("kind",out var kindValue)||kindValue.ValueKind!=JsonValueKind.String)return LocalAiPreviewParseStatus.InvalidRequest;var kind=kindValue.GetString();
            if(kind=="explicit")
            {
                if(!ExactNames(selection,["kind","archive_scope","session_ids"])||selection.GetProperty("archive_scope").ValueKind!=JsonValueKind.String||selection.GetProperty("session_ids").ValueKind!=JsonValueKind.Array)return LocalAiPreviewParseStatus.InvalidRequest;
                var archive=selection.GetProperty("archive_scope").GetString();if(archive is not("active_only" or "include_archived"))return LocalAiPreviewParseStatus.InvalidRequest;
                if(selection.GetProperty("session_ids").GetArrayLength()>200)return LocalAiPreviewParseStatus.ScopeTooLarge;
                var ids=new List<string>();foreach(var item in selection.GetProperty("session_ids").EnumerateArray()){if(item.ValueKind!=JsonValueKind.String||!CanonicalUuid(item.GetString())||ids.Contains(item.GetString()!,StringComparer.Ordinal))return LocalAiPreviewParseStatus.InvalidRequest;ids.Add(item.GetString()!);}request=new(repositoryId!,kind,archive!,ids,null);return LocalAiPreviewParseStatus.Success;
            }
            if(kind=="filter")
            {
                if(!ExactNames(selection,["kind","request"]))return LocalAiPreviewParseStatus.InvalidRequest;var raw=System.Text.Encoding.UTF8.GetBytes(selection.GetProperty("request").GetRawText());
                if(LocalMonitorV1SessionSearchRequestParser.Parse(raw,out var filter)!=LocalMonitorV1SessionSearchParseStatus.Success||filter!.Scope!="repository"||filter.RepositoryId!=repositoryId||filter.Cursor is not null||filter.Limit is not null)return LocalAiPreviewParseStatus.InvalidRequest;
                request=new(repositoryId!,kind,filter.ArchiveScope,[],filter);return LocalAiPreviewParseStatus.Success;
            }
            return LocalAiPreviewParseStatus.InvalidRequest;
        }catch(JsonException){return LocalAiPreviewParseStatus.InvalidRequest;}
    }
    internal static bool TryRepositoryRun(ReadOnlyMemory<byte> bytes, out LocalAiRepositoryRunRequestV1? request)
    {
        request = null;
        if (!TryObject(bytes, ["schema_version", "snapshot_id", "payload_sha256", "timeout_seconds"], out var root)
            || !ExactString(root, "schema_version", "local-ai-repository-run.request.v1")
            || !TryUuid(root, "snapshot_id", out var snapshotId)
            || !TryHash(root, "payload_sha256", out var hash)
            || !TryTimeout(root, out var timeout)) return false;
        request = new(snapshotId!, hash!, timeout);
        return true;
    }

    internal static bool TryComparisonRun(ReadOnlyMemory<byte> bytes, out LocalAiComparisonRunRequestV1? request)
    {
        request = null;
        if (!TryObject(bytes, ["schema_version", "repository_id", "comparison_id", "timeout_seconds"], out var root)
            || !ExactString(root, "schema_version", "local-ai-comparison-run.request.v1")
            || !TryUuid(root, "repository_id", out var repositoryId)
            || !TryUuid(root, "comparison_id", out var comparisonId)
            || !TryTimeout(root, out var timeout)) return false;
        request = new(repositoryId!, comparisonId!, timeout);
        return true;
    }

    private static bool TryObject(ReadOnlyMemory<byte> bytes, string[] expected, out JsonElement root)
    {
        root = default;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object) return false;
            var names = root.EnumerateObject().Select(static item => item.Name).ToArray();
            return names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length
                && names.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        }
        catch (JsonException) { return false; }
    }
    private static bool ExactNames(JsonElement root,string[] expected){var names=root.EnumerateObject().Select(static x=>x.Name).ToArray();return names.Length==expected.Length&&names.Distinct(StringComparer.Ordinal).Count()==names.Length&&names.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal),StringComparer.Ordinal);}
    private static bool CanonicalUuid(string? value)=>value is {Length:36}&&Guid.TryParseExact(value,"D",out var parsed)&&parsed.Version==7&&value==value.ToLowerInvariant()&&value[19] is '8' or '9' or 'a' or 'b';

    private static bool ExactString(JsonElement root, string name, string expected) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String
        && string.Equals(root.GetProperty(name).GetString(), expected, StringComparison.Ordinal);

    private static bool TryUuid(JsonElement root, string name, out string? value)
    {
        value = root.GetProperty(name).ValueKind == JsonValueKind.String ? root.GetProperty(name).GetString() : null;
        return CanonicalUuid(value);
    }

    private static bool TryHash(JsonElement root, string name, out string? value)
    {
        value = root.GetProperty(name).ValueKind == JsonValueKind.String ? root.GetProperty(name).GetString() : null;
        return value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool TryTimeout(JsonElement root, out int timeout)
    {
        timeout = 0;
        return root.GetProperty("timeout_seconds").ValueKind == JsonValueKind.Number
            && root.GetProperty("timeout_seconds").TryGetInt32(out timeout) && timeout is >= 1 and <= 600;
    }
}
