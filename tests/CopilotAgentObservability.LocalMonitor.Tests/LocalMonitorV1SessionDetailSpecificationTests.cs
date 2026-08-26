using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionDetailSpecificationTests
{
    private const string SummaryToken = "local-monitor-session-summary.response.v1";
    private const string TimelineToken = "local-monitor-session-timeline.response.v1";
    private const string NodeToken = "local-monitor-session-node.response.v1";
    private const string ContentToken = "local-monitor-node-content.response.v1";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ContractRoot = Path.Combine(
        RepositoryRoot, "docs", "specifications", "contracts", "local-monitor-v1");
    private static readonly string FixtureRoot = Path.Combine(
        RepositoryRoot, "tests", "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "LocalMonitorV1SessionDetail");

    [Fact]
    public void SchemasAreClosedDraft202012Documents()
    {
        var schemas = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["session-summary"] = SummaryToken,
            ["session-timeline"] = TimelineToken,
            ["session-node"] = NodeToken,
        };
        foreach (var (name, token) in schemas)
        {
            using var schema = JsonDocument.Parse(File.ReadAllBytes(SchemaPath(name)));
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
            Assert.Equal(token, schema.RootElement.GetProperty("title").GetString());
            Assert.Equal(token, schema.RootElement.GetProperty("properties").GetProperty("schema_version").GetProperty("const").GetString());
            AssertClosedObjects(schema.RootElement, "#");
        }
    }

    [Fact]
    public void LiteralGoldenResponsesHaveExactBytesPropertyOrderAndSchemaTokens()
    {
        AssertFixture("summary-empty.json", SummaryToken, AssertSummaryOrder);
        AssertFixture("summary-full.json", SummaryToken, AssertSummaryOrder);
        AssertFixture("timeline-empty.json", TimelineToken, AssertTimelineOrder);
        AssertFixture("timeline-page.json", TimelineToken, AssertTimelineOrder);
        AssertFixture("node-full.json", NodeToken, AssertNodeOrder);
    }

    [Fact]
    public void EveryLiteralGoldenValidatesAgainstItsSchema()
    {
        AssertSchemaValid("summary-empty.json", "session-summary");
        AssertSchemaValid("summary-full.json", "session-summary");
        AssertSchemaValid("timeline-empty.json", "session-timeline");
        AssertSchemaValid("timeline-page.json", "session-timeline");
        AssertSchemaValid("node-full.json", "session-node");
    }

    [Fact]
    public void TimelineCursorHasFrozen119ByteFrameAndLiteralZeroKeyGolden()
    {
        const string Golden = "AUvBmFRvW910FmGP6PsgMKWaZpJ-_RruPTvLjZWo_9S5AQAAAAAAAAAAAAAAAAAAAABub2RlLTAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwoMQ6BPSDxGoZq79-Xxr6E1mlGJLDEojG5z_uZrh8jn0";
        var bytes = DecodeBase64Url(Golden);

        Assert.Equal(159, Golden.Length);
        Assert.Equal(119, bytes.Length);
        Assert.Equal(1, bytes[0]);
        Assert.Equal(1, bytes[33]);
        Assert.Equal(new byte[8], bytes[34..42]);
        Assert.Equal(new byte[8], bytes[42..50]);
        Assert.Equal("node-00000000000000000000000000000000", Encoding.ASCII.GetString(bytes, 50, 37));

        var key = new byte[32];
        var filterFrame = Encoding.ASCII.GetBytes(
            "local-monitor-timeline-filter\0v1\0" +
            "018f0000-0000-7000-8000-000000000001\0" +
            new string('0', 64) + "\0\0\0" +
            "100\0");
        Assert.Equal(HMACSHA256.HashData(key, filterFrame), bytes[1..33]);
        var tagFrame = Encoding.ASCII.GetBytes("local-monitor-timeline-cursor\0v1\0").Concat(bytes[..87]).ToArray();
        Assert.Equal(HMACSHA256.HashData(key, tagFrame), bytes[87..119]);
    }

    [Fact]
    public void ErrorsMethodsAndRawContentContractAreExact()
    {
        var specification = File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "specifications", "interfaces", "local-monitor-v1-session-detail.md"));
        var errors = new[]
        {
            "invalid_host", "method_not_allowed", "invalid_request", "invalid_cursor",
            "session_not_found", "execution_not_found", "node_not_found",
            "workspace_snapshot_stale", "workspace_too_large", "raw_content_not_captured",
            "raw_content_expired", "raw_content_deleted", "raw_content_read_denied",
            "raw_content_too_large", "raw_content_lease_lost", "persistence_busy",
            "local_monitor_ui_unavailable",
        };
        Assert.All(errors, error => Assert.Equal(
            Encoding.UTF8.GetBytes($"{{\"error\":\"{error}\"}}"),
            Encoding.UTF8.GetBytes(ErrorBytes[error])));
        Assert.All(errors, error => Assert.Contains(error, specification, StringComparison.Ordinal));

        Assert.Equal(new[] { "GET", "HEAD" }, AcceptedMethods);
        Assert.Equal("GET, HEAD", MethodNotAllowedHeader);
        Assert.Contains("Only GET and HEAD are accepted", specification, StringComparison.Ordinal);
        Assert.Contains("Every other method is exact `405`", specification, StringComparison.Ordinal);
        Assert.Contains("HEAD selects the exact GET status, headers", specification, StringComparison.Ordinal);
        Assert.Equal("X-Local-Monitor-Schema-Version", ContentSchemaHeaderName);
        Assert.Equal(ContentToken, ContentSchemaHeaderValue);
        Assert.Equal("text/plain; charset=utf-8", ContentType);
        Assert.Equal("no-store", CacheControl);
        Assert.Equal(1_048_576, MaximumRawContentBytes);
        Assert.Equal(new[] { "instruction", "tool_input", "tool_result", "error_message", "subagent_input", "event_content" }, ContentParts);
        Assert.Contains("`X-Local-Monitor-Schema-Version`", specification, StringComparison.Ordinal);
        Assert.Contains("`Content-Type: text/plain; charset=utf-8`", specification, StringComparison.Ordinal);
    }

    private static readonly IReadOnlyDictionary<string, string> ErrorBytes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["invalid_host"] = "{\"error\":\"invalid_host\"}",
        ["method_not_allowed"] = "{\"error\":\"method_not_allowed\"}",
        ["invalid_request"] = "{\"error\":\"invalid_request\"}",
        ["invalid_cursor"] = "{\"error\":\"invalid_cursor\"}",
        ["session_not_found"] = "{\"error\":\"session_not_found\"}",
        ["execution_not_found"] = "{\"error\":\"execution_not_found\"}",
        ["node_not_found"] = "{\"error\":\"node_not_found\"}",
        ["workspace_snapshot_stale"] = "{\"error\":\"workspace_snapshot_stale\"}",
        ["workspace_too_large"] = "{\"error\":\"workspace_too_large\"}",
        ["raw_content_not_captured"] = "{\"error\":\"raw_content_not_captured\"}",
        ["raw_content_expired"] = "{\"error\":\"raw_content_expired\"}",
        ["raw_content_deleted"] = "{\"error\":\"raw_content_deleted\"}",
        ["raw_content_read_denied"] = "{\"error\":\"raw_content_read_denied\"}",
        ["raw_content_too_large"] = "{\"error\":\"raw_content_too_large\"}",
        ["raw_content_lease_lost"] = "{\"error\":\"raw_content_lease_lost\"}",
        ["persistence_busy"] = "{\"error\":\"persistence_busy\"}",
        ["local_monitor_ui_unavailable"] = "{\"error\":\"local_monitor_ui_unavailable\"}",
    };

    private static readonly string[] AcceptedMethods = ["GET", "HEAD"];
    private const string MethodNotAllowedHeader = "GET, HEAD";
    private const string ContentSchemaHeaderName = "X-Local-Monitor-Schema-Version";
    private const string ContentSchemaHeaderValue = ContentToken;
    private const string ContentType = "text/plain; charset=utf-8";
    private const string CacheControl = "no-store";
    private const int MaximumRawContentBytes = 1_048_576;
    private static readonly string[] ContentParts = ["instruction", "tool_input", "tool_result", "error_message", "subagent_input", "event_content"];

    private static void AssertFixture(string name, string token, Action<JsonElement> assertOrder)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureRoot, name));
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.NotEmpty(bytes);
        Assert.NotEqual((byte)'\n', bytes[^1]);
        Assert.NotEqual((byte)'\r', bytes[^1]);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(token, document.RootElement.GetProperty("schema_version").GetString());
        assertOrder(document.RootElement);
    }

    private static void AssertSummaryOrder(JsonElement root)
    {
        AssertProperties(root, "schema_version", "workspace_revision", "session", "executions", "technical_references");
        var session = root.GetProperty("session");
        AssertProperties(session, "session_id", "status", "completeness", "assignment", "archive", "instruction", "source", "model", "version", "timing", "tokens", "activity", "capture");
        AssertProperties(session.GetProperty("assignment"), "state", "authority", "revision", "repository_id", "candidate_repository_ids");
        AssertProperties(session.GetProperty("archive"), "state", "revision", "effectively_eligible", "exclusion_reason");
        AssertProperties(session.GetProperty("instruction"), "state", "label", "additional_count", "content_available");
        AssertProperties(session.GetProperty("source"), "state", "values");
        AssertProperties(session.GetProperty("model"), "state", "values");
        AssertProperties(session.GetProperty("version"), "state", "values");
        AssertProperties(session.GetProperty("timing"), "state", "started_at", "ended_at", "last_seen_at", "duration_ms");
        AssertTokenOrder(session.GetProperty("tokens"));
        AssertActivityOrder(session.GetProperty("activity"));
        AssertProperties(session.GetProperty("capture"), "state", "notes");
        foreach (var execution in root.GetProperty("executions").EnumerateArray())
        {
            AssertExecutionOrder(execution);
        }
        AssertProperties(root.GetProperty("technical_references"), "native_session_ids", "trace_ids");
    }

    private static void AssertTimelineOrder(JsonElement root)
    {
        AssertProperties(root, "schema_version", "workspace_revision", "session_id", "execution_id", "parent_node_id", "items", "next_cursor");
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            AssertTimelineItemOrder(item, technicalReferences: false);
        }
    }

    private static void AssertNodeOrder(JsonElement root)
    {
        AssertProperties(root, "schema_version", "workspace_revision", "session_id", "execution", "node", "parent_path", "related", "content");
        AssertExecutionOrder(root.GetProperty("execution"));
        AssertTimelineItemOrder(root.GetProperty("node"), technicalReferences: true);
        Assert.All(root.GetProperty("parent_path").EnumerateArray(), item => AssertTimelineItemOrder(item, technicalReferences: false));
        var related = root.GetProperty("related");
        AssertProperties(related, "retry", "recovery", "children");
        Assert.All(related.EnumerateObject().SelectMany(property => property.Value.EnumerateArray()), item => AssertTimelineItemOrder(item, technicalReferences: false));
        var content = root.GetProperty("content");
        AssertProperties(content, ContentParts);
        Assert.All(content.EnumerateObject(), property => AssertProperties(property.Value, "state", "available"));
    }

    private static void AssertExecutionOrder(JsonElement execution)
    {
        AssertProperties(execution, "execution_id", "node_id", "source", "model", "lifecycle", "status", "timing", "tokens", "activity", "child_count");
        AssertProperties(execution.GetProperty("timing"), "state", "started_at", "ended_at", "duration_ms");
        AssertTokenOrder(execution.GetProperty("tokens"));
        AssertActivityOrder(execution.GetProperty("activity"));
    }

    private static void AssertTimelineItemOrder(JsonElement item, bool technicalReferences)
    {
        var expected = new List<string> { "node_id", "execution_id", "parent_node_id", "relationship_authority", "kind", "name", "lifecycle", "status", "timing", "activity", "tokens", "child_count", "content_parts" };
        if (technicalReferences)
        {
            expected.Add("technical_references");
        }
        AssertProperties(item, expected.ToArray());
        AssertProperties(item.GetProperty("name"), "state", "text");
        AssertProperties(item.GetProperty("timing"), "state", "started_at", "ended_at", "duration_ms");
        AssertActivityOrder(item.GetProperty("activity"));
        AssertTokenOrder(item.GetProperty("tokens"));
        if (technicalReferences)
        {
            AssertProperties(item.GetProperty("technical_references"), "source_kind", "source_identity", "trace_id", "span_id", "event_id");
        }
    }

    private static void AssertTokenOrder(JsonElement tokens)
    {
        AssertProperties(tokens, "authority", "state", "available_execution_count", "total_execution_count", "input", "output", "total", "reasoning", "cache_read", "cache_creation", "new_input", "cache_read_ratio_basis_points");
        Assert.All(tokens.EnumerateObject().Skip(4), component => AssertProperties(component.Value, "state", "value"));
    }

    private static void AssertActivityOrder(JsonElement activity)
    {
        AssertProperties(activity, "skill", "tool", "subagent", "error", "retry");
        Assert.All(activity.EnumerateObject(), fact => AssertProperties(fact.Value, "state", "count"));
    }

    private static void AssertClosedObjects(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String && type.GetString() == "object")
            {
                Assert.True(element.TryGetProperty("additionalProperties", out var additional), path);
                Assert.Equal(JsonValueKind.False, additional.ValueKind);
                Assert.True(element.TryGetProperty("required", out _), path);
                Assert.True(element.TryGetProperty("properties", out _), path);
            }
            foreach (var property in element.EnumerateObject())
            {
                AssertClosedObjects(property.Value, path + "/" + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                AssertClosedObjects(item, path + "/" + index++);
            }
        }
    }

    private static void AssertSchemaValid(string fixtureName, string schemaName) =>
        Assert.True(ValidateWithPowerShellJsonSchema(
            Path.Combine(FixtureRoot, fixtureName), SchemaPath(schemaName)), fixtureName);

    private static bool ValidateWithPowerShellJsonSchema(string instancePath, string schemaPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh",
            ArgumentList =
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                "$instance = Get-Content -Raw -LiteralPath $args[0]; " +
                "if ($instance | Test-Json -SchemaFile $args[1]) { exit 0 } else { exit 1 }",
                instancePath, schemaPath,
            },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static void AssertProperties(JsonElement value, params string[] expected) =>
        Assert.Equal(expected, value.EnumerateObject().Select(property => property.Name));

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string SchemaPath(string name) => Path.Combine(ContractRoot, name + ".response.schema.json");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
