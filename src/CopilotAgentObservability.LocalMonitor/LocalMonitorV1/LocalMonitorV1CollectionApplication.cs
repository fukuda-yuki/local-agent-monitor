using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal sealed record LocalMonitorV1RepositoryRequest(string ArchiveScope, string? After, int Limit);

internal static class LocalMonitorV1RepositoryRequestParser
{
    internal static bool TryParse(string queryString, out LocalMonitorV1RepositoryRequest? request)
    {
        request = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var raw = queryString.StartsWith("?", StringComparison.Ordinal) ? queryString[1..] : queryString;
        if (raw.Length != 0)
        {
            foreach (var part in raw.Split('&', StringSplitOptions.None))
            {
                var separator = part.IndexOf('=');
                if (separator <= 0) return false;
                var name = part[..separator]; var value = part[(separator + 1)..];
                if (name is not ("archive_scope" or "after" or "limit") || !values.TryAdd(name, value)
                    || name.IndexOfAny(['%', '+', ';']) >= 0
                    || name != "after" && (value.Length == 0 || part.IndexOf('=', separator + 1) >= 0 || value.IndexOfAny(['%', '+', ';']) >= 0)) return false;
            }
        }
        var archiveScope = values.TryGetValue("archive_scope", out var archive) ? archive : "active_only";
        var after = values.TryGetValue("after", out var cursor) ? cursor : null;
        var limit = 50;
        if (archiveScope is not ("active_only" or "include_archived")
            || values.TryGetValue("limit", out var rawLimit)
                && (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                    || limit is < 1 or > 200
                    || !string.Equals(limit.ToString(CultureInfo.InvariantCulture), rawLimit, StringComparison.Ordinal))) return false;
        request = new(archiveScope!, after, limit);
        return true;
    }
}

internal static class LocalMonitorV1CollectionApplication
{
    internal const int MaximumResponseBytes = 8_388_608;

    internal static byte[] SerializeSessions(LocalRepositoryScopeSnapshot snapshot, LocalMonitorV1SessionSearchRequest request, byte[] cursorKey, string? collectionRevisionOverride = null, string? itemRevisionOverride = null)
    {
        var rows = snapshot.Sessions
            .Where(row => InScope(row, request) && Matches(row, request))
            .Select(row => (Scope: row, Projection: (LocalWorkspaceProjectionRow)row.Session))
            .OrderBy(row => row.Projection.SortGroup)
            .ThenByDescending(row => row.Projection.SortEpochMilliseconds)
            .ThenByDescending(row => row.Projection.SessionId, StringComparer.Ordinal)
            .ToArray();
        LocalMonitorV1SessionCursorPosition? cursorPosition = null;
        if (request.Cursor is not null)
        {
            if (!LocalMonitorV1SessionCursorCodec.TryDecode(request.Cursor, cursorKey, request, out cursorPosition))
                throw new LocalMonitorV1CollectionException("invalid_cursor");
            rows = rows.Where(row => LocalMonitorV1SessionCursorKeyset.TryShouldResume(cursorPosition!,
                new((LocalMonitorV1SessionSortGroup)row.Projection.SortGroup, row.Projection.SortEpochMilliseconds, row.Projection.SessionId), out var resume) && resume).ToArray();
        }
        var page = rows.Take(request.EffectiveLimit + 1).ToArray();
        var emitted = page.Take(request.EffectiveLimit).ToArray();
        var revision = collectionRevisionOverride ?? (emitted.Length == 0 && snapshot.Sessions.Count == 0
            ? new string('0', 64)
            : Hash("local-monitor-session-collection\0v1\0", snapshot));
        string? next = page.Length > emitted.Length ? LocalMonitorV1SessionCursorCodec.Encode(cursorKey, request,
            new((LocalMonitorV1SessionSortGroup)emitted[^1].Projection.SortGroup, emitted[^1].Projection.SortEpochMilliseconds, emitted[^1].Projection.SessionId)) : null;
        return Write(writer =>
        {
            writer.WriteStartObject(); writer.WriteString("schema_version", "local-monitor-sessions.response.v1"); writer.WriteString("workspace_revision", revision);
            writer.WritePropertyName("items"); writer.WriteStartArray();
            foreach (var row in emitted) WriteSession(writer, row.Scope, row.Projection, itemRevisionOverride);
            writer.WriteEndArray(); if (next is null) writer.WriteNull("next_cursor"); else writer.WriteString("next_cursor", next); writer.WriteEndObject();
        });
    }

    internal static byte[] SerializeRepositories(LocalRepositoryScopeSnapshot snapshot, LocalMonitorV1RepositoryRequest request, byte[] cursorKey, string? collectionRevisionOverride = null, string? itemRevisionOverride = null)
    {
        var candidates = snapshot.Repositories.Where(r => request.ArchiveScope == "include_archived" || r.ArchiveState == LocalArchiveState.Active)
            .OrderBy(r => r.RepositoryId, StringComparer.Ordinal);
        if (request.After is not null)
        {
            if (!LocalMonitorV1RepositoryCursorCodec.TryDecode(request.After, cursorKey, request, out var position))
                throw new LocalMonitorV1CollectionException("invalid_cursor");
            candidates = candidates.Where(r => StringComparer.Ordinal.Compare(r.RepositoryId, position) > 0)
                .OrderBy(r => r.RepositoryId, StringComparer.Ordinal);
        }
        var page = candidates.Take(request.Limit + 1).ToArray(); var emitted = page.Take(request.Limit).ToArray();
        var collectionRevision = collectionRevisionOverride ?? (snapshot.Repositories.Count == 0 && snapshot.Sessions.Count == 0
            ? new string('0', 64)
            : Hash("local-monitor-repository-collection\0v1\0", snapshot));
        var assignedByRepository = snapshot.Sessions
            .Where(session => session.RepositoryId is not null && session.AssignmentState == LocalRepositoryScopeAssignmentState.Assigned)
            .GroupBy(session => session.RepositoryId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return Write(writer =>
        {
            writer.WriteStartObject(); writer.WriteString("schema_version", "local-monitor-repositories.response.v1"); writer.WriteString("workspace_revision", collectionRevision);
            writer.WritePropertyName("repositories"); writer.WriteStartArray();
            foreach (var repository in emitted)
            {
                var assigned = assignedByRepository.GetValueOrDefault(repository.RepositoryId) ?? [];
                writer.WriteStartObject(); writer.WriteString("repository_id", repository.RepositoryId); writer.WriteString("display_name", repository.DisplayName);
                writer.WriteString("archive_state", Name(repository.ArchiveState)); writer.WriteNumber("archive_revision", repository.ArchiveRevision);
                writer.WriteNumber("active_session_count", assigned.Count(s => s.IsEffectivelyEligible));
                var last = assigned.Where(s => s.IsEffectivelyEligible)
                    .Select(s => (LocalWorkspaceProjectionRow)s.Session)
                    .Select(p => DateTimeOffset.TryParseExact(p.LastSeenAt, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
                        ? (Valid: true, Instant: instant)
                        : default)
                    .Where(value => value.Valid)
                    .OrderByDescending(value => value.Instant)
                    .Select(value => (DateTimeOffset?)value.Instant)
                    .FirstOrDefault();
                if (last is null) writer.WriteNull("last_observed_at"); else writer.WriteString("last_observed_at", last.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                writer.WriteNumber("assignment_conflict_count", repository.AssignmentConflictCount); writer.WriteString("repository_revision", itemRevisionOverride ?? Hash("local-monitor-repository-item\0v1\0", repository, assigned)); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteNumber("all_session_count", snapshot.Sessions.Count(s => s.IsAllScopeMember));
            writer.WriteNumber("unassigned_active_session_count", snapshot.Sessions.Count(s => s.IsUnassignedScopeMember && s.IsEffectivelyEligible));
            writer.WriteNumber("archived_repository_count", snapshot.Repositories.Count(r => r.ArchiveState == LocalArchiveState.Archived));
            if (page.Length > emitted.Length) writer.WriteString("next_cursor", LocalMonitorV1RepositoryCursorCodec.Encode(cursorKey, request, emitted[^1].RepositoryId)); else writer.WriteNull("next_cursor"); writer.WriteEndObject();
        });
    }

    private static bool InScope(LocalRepositoryScopeSessionSnapshot row, LocalMonitorV1SessionSearchRequest request) =>
        (request.Scope switch { "all" => row.IsAllScopeMember, "unassigned" => row.IsUnassignedScopeMember, "repository" => row.IsRequestedScopeMember, _ => false })
        && (request.ArchiveScope == "include_archived" || row.IsEffectivelyEligible);

    private static bool Matches(LocalRepositoryScopeSessionSnapshot row, LocalMonitorV1SessionSearchRequest request)
    {
        var p = (LocalWorkspaceProjectionRow)row.Session;
        var acceptedAt = p.SortGroup == 0 ? DateTimeOffset.FromUnixTimeMilliseconds(p.SortEpochMilliseconds) : (DateTimeOffset?)null;
        return (request.From is null || acceptedAt is not null && acceptedAt.Value >= request.From.Value)
            && (request.To is null || acceptedAt is not null && acceptedAt.Value < request.To.Value)
            && (request.Sources.Count == 0 || p.Sources.Values.Any(request.Sources.Contains))
            && (request.Models.Count == 0 || p.Models.Values.Any(request.Models.Contains))
            && (request.Statuses.Count == 0 || request.Statuses.Contains(p.Status))
            && Fact(p.Activity.Skill, request.HasSkill) && Fact(p.Activity.Subagent, request.HasSubagent) && Fact(p.Activity.Error, request.HasError) && Fact(p.Activity.Retry, request.HasRetry)
            && (request.QueryNormalized is null || p.SearchTexts.Any(text => text.Contains(request.QueryNormalized, StringComparison.Ordinal)));
    }

    private static bool Fact(LocalWorkspaceFact<long> fact, bool? wanted) => wanted is null
        || wanted == true && fact.State == "recorded" && fact.Value > 0
        || wanted == false && fact.State == "recorded" && fact.Value == 0;

    private static void WriteSession(Utf8JsonWriter w, LocalRepositoryScopeSessionSnapshot s, LocalWorkspaceProjectionRow p, string? revisionOverride)
    {
        w.WriteStartObject(); w.WriteString("session_id", p.SessionId);
        w.WritePropertyName("assignment"); w.WriteStartObject(); w.WriteString("state", EnumName(s.AssignmentState)); w.WriteString("authority", EnumName(s.AssignmentAuthority)); w.WriteNumber("revision", s.AssignmentRevision); if (s.RepositoryId is null) w.WriteNull("repository_id"); else w.WriteString("repository_id", s.RepositoryId); w.WritePropertyName("candidate_repository_ids"); JsonSerializer.Serialize(w, s.CandidateRepositoryIds); w.WriteEndObject();
        w.WritePropertyName("archive"); w.WriteStartObject(); w.WriteString("state", Name(s.ArchiveState)); w.WriteNumber("revision", s.ArchiveRevision); w.WriteBoolean("effectively_eligible", s.IsEffectivelyEligible); if (s.ArchiveExclusionReason is null) w.WriteNull("exclusion_reason"); else w.WriteString("exclusion_reason", s.ArchiveExclusionReason); w.WriteEndObject();
        w.WritePropertyName("label"); Fact(w, p.LabelState, p.LabelText, "text"); w.WriteString("status", p.Status); w.WriteString("completeness", p.Completeness);
        Set(w, "source", p.Sources); Set(w, "model", p.Models); w.WritePropertyName("summary"); w.WriteStartObject(); Count(w,"skill",p.Activity.Skill); Count(w,"tool",p.Activity.Tool); Count(w,"subagent",p.Activity.Subagent); Count(w,"error",p.Activity.Error); Count(w,"retry",p.Activity.Retry); w.WriteEndObject();
        var t=p.Tokens; var inconsistent=new LocalWorkspaceFact<long>("inconsistent",null); var inconsistentCache=new LocalWorkspaceFact<long>("inconsistent",t.CacheRead.Value); w.WritePropertyName("tokens"); w.WriteStartObject(); w.WriteString("authority",t.Authority); w.WriteString("state",t.State); w.WriteNumber("available_execution_count",t.AvailableExecutionCount); w.WriteNumber("total_execution_count",t.TotalExecutionCount); Value(w,"input",t.Input); Value(w,"output",t.Output); Value(w,"total",t.Total); Value(w,"reasoning",t.Reasoning); Value(w,"cache_read",t.State=="inconsistent"?inconsistentCache:t.CacheRead); Value(w,"cache_creation",t.CacheCreation); Value(w,"new_input",t.State=="inconsistent"?inconsistent:t.NewInput); Value(w,"cache_read_ratio_basis_points",t.State=="inconsistent"?inconsistent:t.CacheReadRatioBasisPoints); w.WriteEndObject();
        w.WritePropertyName("timing"); w.WriteStartObject(); w.WriteString("state",p.TimingState); Nullable(w,"started_at",p.StartedAt); Nullable(w,"ended_at",p.EndedAt); if(p.DurationMilliseconds is null)w.WriteNull("duration_ms");else w.WriteNumber("duration_ms",p.DurationMilliseconds.Value); w.WriteEndObject(); w.WritePropertyName("capture_notes"); JsonSerializer.Serialize(w,p.CaptureNotes); w.WriteString("workspace_revision",revisionOverride??Hash("local-monitor-session-item\0v1\0",s,p)); w.WriteEndObject();
    }
    private static void Set(Utf8JsonWriter w,string n,LocalWorkspaceSetFact f){w.WritePropertyName(n);w.WriteStartObject();w.WriteString("state",f.State);w.WritePropertyName("values");JsonSerializer.Serialize(w,f.Values);w.WriteEndObject();}
    private static void Count(Utf8JsonWriter w,string n,LocalWorkspaceFact<long> f){w.WritePropertyName(n);Fact(w,f.State,f.Value,"count");}
    private static void Value(Utf8JsonWriter w,string n,LocalWorkspaceFact<long> f){w.WritePropertyName(n);Fact(w,f.State,f.Value,"value");}
    private static void Fact<T>(Utf8JsonWriter w,string state,T? value,string n){w.WriteStartObject();w.WriteString("state",state);w.WritePropertyName(n);if(value is null)w.WriteNullValue();else JsonSerializer.Serialize(w,value);w.WriteEndObject();}
    private static void Nullable(Utf8JsonWriter w,string n,string? v){if(v is null)w.WriteNull(n);else w.WriteString(n,v);}
    private static string Name(LocalArchiveState s)=>s==LocalArchiveState.Active?"active":"archived";
    private static string EnumName<T>(T value) where T:Enum => value.ToString().Replace("ExplicitlyUnassigned","explicitly_unassigned",StringComparison.Ordinal).ToLowerInvariant();
    private static byte[] Write(Action<Utf8JsonWriter> action){using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream,new(){Indented=false,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping})){action(writer);}var bytes=stream.ToArray();if(bytes.Length>MaximumResponseBytes)throw new LocalMonitorV1CollectionException("workspace_too_large");return bytes;}
    private static string Hash(string domain,params object[] values){using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);Span<byte> length=stackalloc byte[4];hash.AppendData(Encoding.UTF8.GetBytes(domain));foreach(var value in values){var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length,bytes.Length);hash.AppendData(length);hash.AppendData(bytes);}return Convert.ToHexStringLower(hash.GetHashAndReset());}
}

internal sealed class LocalMonitorV1CollectionException(string error) : Exception(error)
{
    internal string Error { get; } = error;
}
