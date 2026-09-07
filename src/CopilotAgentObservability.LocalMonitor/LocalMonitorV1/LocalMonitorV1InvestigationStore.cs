using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal sealed class LocalMonitorV1InvestigationStore
{
    private sealed record Entry(DateTimeOffset CreatedAt, JsonElement Body, LocalMonitorV1SessionSearchRequest Search);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    internal LocalMonitorV1InvestigationStore(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;
    internal string? Save(JsonElement body)
    {
        if (!Keys(body, "schema_version", "search", "workspace_revision", "cohorts", "scroll_y")
            || body.GetProperty("schema_version").ValueKind != JsonValueKind.String
            || body.GetProperty("schema_version").GetString() != "local-monitor-investigation.request.v1"
            || !Hex(body.GetProperty("workspace_revision"))
            || body.GetProperty("scroll_y").ValueKind != JsonValueKind.Number || !body.GetProperty("scroll_y").TryGetInt32(out var scroll) || scroll is < 0 or > 10000000
            || !Keys(body.GetProperty("cohorts"), "a", "b")
            || !Ids(body.GetProperty("cohorts").GetProperty("a")) || !Ids(body.GetProperty("cohorts").GetProperty("b"))
            || LocalMonitorV1SessionSearchRequestParser.Parse(System.Text.Encoding.UTF8.GetBytes(body.GetProperty("search").GetRawText()), out var search)
                != LocalMonitorV1SessionSearchParseStatus.Success) return null;
        lock (gate)
        {
            Prune();
            if (entries.Count >= 32) entries.Remove(entries.MinBy(pair => pair.Value.CreatedAt).Key);
            var handle = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            entries.Add(handle, new(clock.GetUtcNow(), body.Clone(), search!));
            return handle;
        }
    }
    internal bool TryGet(string handle, out JsonElement body, out LocalMonitorV1SessionSearchRequest? search)
    {
        lock (gate)
        {
            Prune();
            if (entries.TryGetValue(handle, out var entry)) { body = entry.Body; search = entry.Search; return true; }
            body = default; search = null; return false;
        }
    }
    private void Prune()
    {
        foreach (var key in entries.Where(pair => clock.GetUtcNow() - pair.Value.CreatedAt >= TimeSpan.FromMinutes(30)).Select(pair => pair.Key).ToArray()) entries.Remove(key);
    }
    internal static bool Keys(JsonElement value, params string[] keys) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == keys.Length && value.EnumerateObject().All(property => keys.Contains(property.Name, StringComparer.Ordinal))
        && value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == keys.Length;
    internal static bool Hex(JsonElement value) => value.ValueKind == JsonValueKind.String && Regex.IsMatch(value.GetString()!, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    private static bool Ids(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 200
        && value.EnumerateArray().All(id => id.ValueKind == JsonValueKind.String && LocalMonitorV1Identity.TryParseUuidV7(id.GetString()!, out _))
        && value.EnumerateArray().Select(id => id.GetString()).Distinct(StringComparer.Ordinal).Count() == value.GetArrayLength();
}
