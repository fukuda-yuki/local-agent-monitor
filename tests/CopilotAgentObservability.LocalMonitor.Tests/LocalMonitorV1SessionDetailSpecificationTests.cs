using System.Diagnostics;
using System.Buffers.Binary;
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
            AssertEveryEnumIsKnown(schema.RootElement, "#");
            AssertEveryPatternIsKnown(schema.RootElement);
        }
    }

    [Fact]
    public void SchemasFreezeExactEnumsBoundsPatternsNullabilityAndStateInvariants()
    {
        using var summary = JsonDocument.Parse(File.ReadAllBytes(SchemaPath("session-summary")));
        var summaryDefs = summary.RootElement.GetProperty("$defs");
        Assert.Equal("^[0-9a-f]{64}$", summaryDefs.GetProperty("revision").GetProperty("pattern").GetString());
        Assert.Equal("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", summaryDefs.GetProperty("uuidv7").GetProperty("pattern").GetString());
        Assert.Equal("^node-[0-9a-f]{32}$", summaryDefs.GetProperty("nodeId").GetProperty("pattern").GetString());
        AssertEnum(summaryDefs.GetProperty("factState"), "recorded", "not_observed", "source_unsupported", "capture_gap", "certification_pending", "not_captured", "expired", "redacted", "malformed", "oversized", "inconsistent", "projection_invalid");
        AssertEnum(summaryDefs.GetProperty("session").GetProperty("properties").GetProperty("status"), "active", "completed", "failed", "unknown");
        AssertEnum(summaryDefs.GetProperty("session").GetProperty("properties").GetProperty("completeness"), "unbound", "partial", "rich", "full");
        AssertEnum(summaryDefs.GetProperty("assignment").GetProperty("properties").GetProperty("state"), "assigned", "unassigned", "explicitly_unassigned", "conflict");
        AssertEnum(summaryDefs.GetProperty("archive").GetProperty("properties").GetProperty("exclusion_reason"), null, "session_archived", "repository_archived");
        Assert.Equal(5, summaryDefs.GetProperty("sourceFact").GetProperty("properties").GetProperty("values").GetProperty("maxItems").GetInt32());
        Assert.Equal(16, summaryDefs.GetProperty("modelFact").GetProperty("properties").GetProperty("values").GetProperty("maxItems").GetInt32());
        Assert.False(summaryDefs.GetProperty("versionFact").GetProperty("properties").GetProperty("values").TryGetProperty("maxItems", out _));
        Assert.Equal(128, summaryDefs.GetProperty("assignment").GetProperty("properties").GetProperty("candidate_repository_ids").GetProperty("maxItems").GetInt32());
        Assert.Equal(160, summaryDefs.GetProperty("instruction").GetProperty("properties").GetProperty("label").GetProperty("maxLength").GetInt32());
        Assert.Equal(16, summaryDefs.GetProperty("capture").GetProperty("properties").GetProperty("notes").GetProperty("maxItems").GetInt32());
        Assert.Equal(10000, summaryDefs.GetProperty("ratioComponent").GetProperty("properties").GetProperty("value").GetProperty("maximum").GetInt32());
        Assert.True(summaryDefs.GetProperty("archive").TryGetProperty("allOf", out _));
        Assert.True(summaryDefs.GetProperty("instruction").TryGetProperty("allOf", out _));
        Assert.True(summaryDefs.GetProperty("countFact").TryGetProperty("allOf", out _));
        Assert.True(summaryDefs.GetProperty("sourceFact").TryGetProperty("allOf", out _));
        Assert.True(summaryDefs.GetProperty("executionTiming").TryGetProperty("oneOf", out _));
        Assert.Equal(256, summary.RootElement.GetProperty("properties").GetProperty("executions").GetProperty("maxItems").GetInt32());
        Assert.Equal(4096, summaryDefs.GetProperty("execution").GetProperty("properties").GetProperty("child_count").GetProperty("maximum").GetInt32());
        AssertTypes(summaryDefs.GetProperty("execution").GetProperty("properties").GetProperty("source"), "string", "null");
        AssertTypes(summaryDefs.GetProperty("instruction").GetProperty("properties").GetProperty("additional_count"), "integer", "null");
        AssertTypes(summaryDefs.GetProperty("assignment").GetProperty("properties").GetProperty("repository_id"), "$ref", "null");
        AssertTypes(summaryDefs.GetProperty("executionTiming").GetProperty("properties").GetProperty("started_at"), "$ref", "null");
        AssertTypes(summaryDefs.GetProperty("executionTiming").GetProperty("properties").GetProperty("ended_at"), "$ref", "null");

        using var timeline = JsonDocument.Parse(File.ReadAllBytes(SchemaPath("session-timeline")));
        var timelineDefs = timeline.RootElement.GetProperty("$defs");
        Assert.Equal(200, timeline.RootElement.GetProperty("properties").GetProperty("items").GetProperty("maxItems").GetInt32());
        Assert.Equal("^[A-Za-z0-9_-]{158}[AEIMQUYcgkosw048]$", timeline.RootElement.GetProperty("properties").GetProperty("next_cursor").GetProperty("pattern").GetString());
        AssertEnum(timelineDefs.GetProperty("item").GetProperty("properties").GetProperty("relationship_authority"), "exact", "explicit", "unknown");
        AssertEnum(timelineDefs.GetProperty("item").GetProperty("properties").GetProperty("kind"), "execution", "agent", "skill", "tool", "subagent", "event", "error", "retry", "permission", "unknown_relation_group");
        AssertEnum(timelineDefs.GetProperty("name").GetProperty("properties").GetProperty("state"), "recorded", "not_observed", "invalid");
        AssertEnum(timelineDefs.GetProperty("item").GetProperty("properties").GetProperty("lifecycle"), "selected", "started", "completed", "failed", "deselected", "unknown");
        AssertEnum(timelineDefs.GetProperty("item").GetProperty("properties").GetProperty("status"), "active", "completed", "failed", "unknown");
        AssertEnum(timelineDefs.GetProperty("contentPart"), ContentParts.Cast<object?>().ToArray());
        Assert.True(timelineDefs.GetProperty("timing").TryGetProperty("oneOf", out _));
        AssertTypes(timeline.RootElement.GetProperty("properties").GetProperty("execution_id"), "$ref", "null");
        AssertTypes(timeline.RootElement.GetProperty("properties").GetProperty("parent_node_id"), "$ref", "null");
        AssertTypes(timeline.RootElement.GetProperty("properties").GetProperty("next_cursor"), "string", "null");
        AssertTypes(timelineDefs.GetProperty("name").GetProperty("properties").GetProperty("text"), "string", "null");

        using var node = JsonDocument.Parse(File.ReadAllBytes(SchemaPath("session-node")));
        var nodeDefs = node.RootElement.GetProperty("$defs");
        Assert.Equal(4096, node.RootElement.GetProperty("properties").GetProperty("parent_path").GetProperty("maxItems").GetInt32());
        foreach (var relation in new[] { "retry", "recovery", "children" })
        {
            Assert.Equal(200, nodeDefs.GetProperty("related").GetProperty("properties").GetProperty(relation).GetProperty("maxItems").GetInt32());
        }
        AssertEnum(nodeDefs.GetProperty("contentState").GetProperty("properties").GetProperty("state"), "available", "not_captured", "expired", "deleted", "read_denied", "oversized", "invalid");
        Assert.True(nodeDefs.GetProperty("contentState").TryGetProperty("allOf", out _));
        Assert.True(nodeDefs.GetProperty("timing").TryGetProperty("oneOf", out _));
        AssertTypes(nodeDefs.GetProperty("technicalReferences").GetProperty("properties").GetProperty("trace_id"), "string", "null");
        foreach (var field in new[] { "source_kind", "source_identity", "span_id", "event_id" })
        {
            AssertTypes(nodeDefs.GetProperty("technicalReferences").GetProperty("properties").GetProperty(field), "string", "null");
        }
        Assert.Equal("^[0-9a-f]{32}$", nodeDefs.GetProperty("technicalReferences").GetProperty("properties").GetProperty("trace_id").GetProperty("pattern").GetString());
        Assert.Equal("^[0-9a-f]{16}$", nodeDefs.GetProperty("technicalReferences").GetProperty("properties").GetProperty("span_id").GetProperty("pattern").GetString());
    }

    [Fact]
    public void LiteralGoldenResponsesHaveExactBytesPropertyOrderAndSchemaTokens()
    {
        AssertFixture("summary-empty.json", SummaryToken, AssertSummaryOrder);
        AssertFixture("summary-full.json", SummaryToken, AssertSummaryOrder);
        AssertFixture("timeline-empty.json", TimelineToken, AssertTimelineOrder);
        AssertFixture("timeline-page.json", TimelineToken, AssertTimelineOrder);
        AssertFixture("node-full.json", NodeToken, AssertNodeOrder);
        AssertFixture("node-nested.json", NodeToken, AssertNodeOrder);
        using var nested = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "node-nested.json")));
        Assert.NotEmpty(nested.RootElement.GetProperty("parent_path").EnumerateArray());
        Assert.All(nested.RootElement.GetProperty("related").EnumerateObject(), property => Assert.NotEmpty(property.Value.EnumerateArray()));
    }

    [Fact]
    public void LiteralGoldenResponsesContainNoJsonWhitespaceOutsideStrings()
    {
        foreach (var name in new[] { "summary-empty.json", "summary-full.json", "timeline-empty.json", "timeline-page.json", "node-full.json", "node-nested.json", "transport-contract.json", "query-grammar.json" })
        {
            AssertNoJsonWhitespaceOutsideStrings(File.ReadAllBytes(Path.Combine(FixtureRoot, name)));
        }
    }

    [Fact]
    public void EveryLiteralGoldenValidatesAgainstItsSchema()
    {
        AssertSchemaValid("summary-empty.json", "session-summary");
        AssertSchemaValid("summary-full.json", "session-summary");
        AssertSchemaValid("timeline-empty.json", "session-timeline");
        AssertSchemaValid("timeline-page.json", "session-timeline");
        AssertSchemaValid("node-full.json", "session-node");
        AssertSchemaValid("node-nested.json", "session-node");
    }

    [Fact]
    public void TimelineCursorHasFrozen119ByteFrameAndLiteralFixtureBinding()
    {
        const string Golden = "AbaGQRRmeBKbzLi7H9QHg8-BuOwBfG68DPCCZpFe661GAAjfAw2j83eAAAAAAAAAAABub2RlLTAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAy4_ItzfbeUsqy3GVgSk-6q1x7eo3hEXJcKBHcmdugLm4";
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "timeline-page.json")));
        Assert.Equal(Golden, fixture.RootElement.GetProperty("next_cursor").GetString());
        var bytes = DecodeBase64Url(Golden);

        Assert.Equal(159, Golden.Length);
        Assert.Equal(119, bytes.Length);
        Assert.Equal(1, bytes[0]);
        Assert.Equal(0, bytes[33]);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero).Ticks, BinaryPrimitives.ReadInt64BigEndian(bytes[34..42]));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian(bytes[42..50]));
        Assert.Equal("node-00000000000000000000000000000002", Encoding.ASCII.GetString(bytes, 50, 37));

        var key = new byte[32];
        var filterFrame = TimelineFilterFrame(
            "018f0000-0000-7000-8000-000000000001",
            new string('1', 64),
            "018f0000-0000-7000-8000-000000000003",
            null,
            1);
        Assert.Equal(HMACSHA256.HashData(key, filterFrame), bytes[1..33]);
        var tagFrame = Encoding.ASCII.GetBytes("local-monitor-timeline-cursor\0v1\0").Concat(bytes[..87]).ToArray();
        Assert.Equal(HMACSHA256.HashData(key, tagFrame), bytes[87..119]);

        Assert.NotEqual(bytes[1..33], HMACSHA256.HashData(key, TimelineFilterFrame("018f0000-0000-7000-8000-000000000001", new string('2', 64), "018f0000-0000-7000-8000-000000000003", null, 1)));
        Assert.NotEqual(bytes[1..33], HMACSHA256.HashData(key, TimelineFilterFrame("018f0000-0000-7000-8000-000000000001", new string('1', 64), "018f0000-0000-7000-8000-000000000004", null, 1)));
        Assert.NotEqual(bytes[1..33], HMACSHA256.HashData(key, TimelineFilterFrame("018f0000-0000-7000-8000-000000000001", new string('1', 64), "018f0000-0000-7000-8000-000000000003", "node-00000000000000000000000000000001", 1)));
        Assert.NotEqual(bytes[1..33], HMACSHA256.HashData(key, TimelineFilterFrame("018f0000-0000-7000-8000-000000000001", new string('1', 64), "018f0000-0000-7000-8000-000000000003", null, 2)));
        Assert.Equal(new[] { 0, 1, 2 }, new[] { RecordedTimeGroup, MissingTimeGroup, InvalidTimeGroup });
        Assert.True(CompareCursorOrder((0, 1L, 0UL, "node-00000000000000000000000000000001"), (0, 1L, 1UL, "node-00000000000000000000000000000000")) < 0);
        Assert.True(CompareCursorOrder((0, 1L, 1UL, "node-00000000000000000000000000000001"), (1, 0L, 0UL, "node-00000000000000000000000000000000")) < 0);
    }

    [Fact]
    public void LiteralTransportTableFreezesRoutesQueriesPrecedenceHeadersAndErrors()
    {
        using var contract = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "transport-contract.json")));
        var root = contract.RootElement;
        AssertProperties(root, "schema_version", "routes", "query_fields", "error_precedence", "errors", "success_headers", "error_headers", "forbidden_headers", "json_max_bytes", "raw_max_bytes", "content_parts");
        Assert.Equal("local-monitor-session-detail.transport.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal(4, root.GetProperty("routes").GetArrayLength());
        Assert.All(root.GetProperty("routes").EnumerateArray(), route =>
        {
            AssertProperties(route, "name", "path", "query_order", "methods", "method_not_allowed_status");
            Assert.Equal(new[] { "GET", "HEAD" }, route.GetProperty("methods").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(405, route.GetProperty("method_not_allowed_status").GetInt32());
        });
        var routes = root.GetProperty("routes").EnumerateArray().ToDictionary(route => route.GetProperty("name").GetString()!, StringComparer.Ordinal);
        Assert.Empty(routes["summary"].GetProperty("query_order").EnumerateArray());
        Assert.Equal(new[] { "workspace_revision", "execution_id", "parent_node_id", "after", "limit" }, routes["timeline"].GetProperty("query_order").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "workspace_revision" }, routes["node"].GetProperty("query_order").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "workspace_revision", "part" }, routes["content"].GetProperty("query_order").EnumerateArray().Select(value => value.GetString()));
        var queries = root.GetProperty("query_fields").EnumerateArray().ToDictionary(field => field.GetProperty("name").GetString()!, StringComparer.Ordinal);
        Assert.Equal(new[] { "workspace_revision", "execution_id", "parent_node_id", "after", "limit", "part" }, queries.Keys);
        Assert.All(queries.Values, field => AssertProperties(field, "name", "required_on", "requires", "pattern", "minimum", "maximum", "default", "enum"));
        Assert.Equal(new[] { "timeline", "node", "content" }, queries["workspace_revision"].GetProperty("required_on").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("execution_id", queries["parent_node_id"].GetProperty("requires").GetString());
        Assert.Equal(1, queries["limit"].GetProperty("minimum").GetInt32());
        Assert.Equal(200, queries["limit"].GetProperty("maximum").GetInt32());
        Assert.Equal(100, queries["limit"].GetProperty("default").GetInt32());
        Assert.Equal(ContentParts, queries["part"].GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "method", "path_identifier", "closed_query", "session", "workspace_revision", "execution_or_node_membership", "cursor", "retention_lease" }, root.GetProperty("error_precedence").EnumerateArray().Select(value => value.GetString()));

        var expectedErrors = new (int Status, string Code, string Bytes)[]
        {
            (400, "invalid_host", "{\"error\":\"invalid_host\"}"), (405, "method_not_allowed", "{\"error\":\"method_not_allowed\"}"),
            (400, "invalid_request", "{\"error\":\"invalid_request\"}"), (400, "invalid_cursor", "{\"error\":\"invalid_cursor\"}"),
            (404, "session_not_found", "{\"error\":\"session_not_found\"}"), (404, "execution_not_found", "{\"error\":\"execution_not_found\"}"),
            (404, "node_not_found", "{\"error\":\"node_not_found\"}"), (409, "workspace_snapshot_stale", "{\"error\":\"workspace_snapshot_stale\"}"),
            (409, "workspace_too_large", "{\"error\":\"workspace_too_large\"}"), (404, "raw_content_not_captured", "{\"error\":\"raw_content_not_captured\"}"),
            (410, "raw_content_expired", "{\"error\":\"raw_content_expired\"}"), (410, "raw_content_deleted", "{\"error\":\"raw_content_deleted\"}"),
            (403, "raw_content_read_denied", "{\"error\":\"raw_content_read_denied\"}"), (413, "raw_content_too_large", "{\"error\":\"raw_content_too_large\"}"),
            (409, "raw_content_lease_lost", "{\"error\":\"raw_content_lease_lost\"}"), (503, "persistence_busy", "{\"error\":\"persistence_busy\"}"),
            (503, "local_monitor_ui_unavailable", "{\"error\":\"local_monitor_ui_unavailable\"}"),
        };
        Assert.Equal(expectedErrors, root.GetProperty("errors").EnumerateArray().Select(error => (
            error.GetProperty("status").GetInt32(), error.GetProperty("code").GetString()!, error.GetProperty("bytes").GetString()!)));
        Assert.Equal(8_388_608, root.GetProperty("json_max_bytes").GetInt32());
        Assert.Equal(1_048_576, root.GetProperty("raw_max_bytes").GetInt32());
        Assert.Equal(ContentParts, root.GetProperty("content_parts").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "Cache-Control", "Content-Type", "Content-Length" }, root.GetProperty("success_headers").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.True(root.GetProperty("success_headers").GetProperty("head_uses_get_content_length").GetBoolean());
        Assert.Equal(0, root.GetProperty("success_headers").GetProperty("head_entity_bytes").GetInt32());
        Assert.Equal("no-store", root.GetProperty("success_headers").GetProperty("cache_control").GetString());
        Assert.Equal("application/json; charset=utf-8", root.GetProperty("success_headers").GetProperty("json_content_type").GetString());
        Assert.Equal("text/plain; charset=utf-8", root.GetProperty("success_headers").GetProperty("raw_content_type").GetString());
        Assert.Equal("X-Local-Monitor-Schema-Version", root.GetProperty("success_headers").GetProperty("content_schema_header_name").GetString());
        Assert.Equal(ContentToken, root.GetProperty("success_headers").GetProperty("content_schema_header_value").GetString());
        Assert.Equal("GET, HEAD", root.GetProperty("error_headers").GetProperty("method_not_allowed_allow").GetString());
        Assert.Equal(new[] { "Cache-Control", "Content-Type", "Content-Length" }, root.GetProperty("error_headers").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("no-store", root.GetProperty("error_headers").GetProperty("cache_control").GetString());
        Assert.Equal("application/json; charset=utf-8", root.GetProperty("error_headers").GetProperty("content_type").GetString());
        Assert.True(root.GetProperty("error_headers").GetProperty("head_uses_get_content_length").GetBoolean());
        Assert.Equal(0, root.GetProperty("error_headers").GetProperty("head_entity_bytes").GetInt32());
        Assert.Equal(new[] { "Access-Control-Allow-Origin", "ETag", "Location", "Set-Cookie" }, root.GetProperty("forbidden_headers").EnumerateArray().Select(value => value.GetString()));

        using var grammar = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "query-grammar.json")));
        AssertProperties(grammar.RootElement, "schema_version", "case_sensitive", "reject", "generated_order");
        Assert.Equal("local-monitor-session-detail.query-grammar.v1", grammar.RootElement.GetProperty("schema_version").GetString());
        Assert.True(grammar.RootElement.GetProperty("case_sensitive").GetBoolean());
        Assert.Equal(new[] { "unknown_key", "empty_key", "empty_value", "duplicate_key", "percent_encoded_unreserved", "whitespace", "raw_plus", "noncanonical_identifier" }, grammar.RootElement.GetProperty("reject").EnumerateArray().Select(value => value.GetString()));
        AssertProperties(grammar.RootElement.GetProperty("generated_order"), "summary", "timeline", "node", "content");
    }

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

    private static void AssertEveryEnumIsKnown(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("enum", out var values))
            {
                var key = string.Join('|', values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Null ? "<null>" : value.GetString()));
                Assert.Contains(key, KnownEnumSets);
            }
            foreach (var property in element.EnumerateObject())
            {
                AssertEveryEnumIsKnown(property.Value, path + "/" + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                AssertEveryEnumIsKnown(item, path + "/" + index++);
            }
        }
    }

    private static readonly HashSet<string> KnownEnumSets = new(StringComparer.Ordinal)
    {
        "recorded|not_observed|source_unsupported|capture_gap|certification_pending|not_captured|expired|redacted|malformed|oversized|inconsistent|projection_invalid",
        "active|completed|failed|unknown", "unbound|partial|rich|full", "assigned|unassigned|explicitly_unassigned|conflict",
        "automatic|manual|none", "active|archived", "<null>|session_archived|repository_archived",
        "recorded|not_observed|not_captured|expired|invalid", "session_run|llm_span|mixed|none",
        "complete|partial|not_observed|invalid", "raw_content_not_captured|raw_content_expired|source_unsupported|capture_gap|certification_pending|projection_invalid|token_inconsistent|cache_inconsistent",
        "selected|started|completed|failed|deselected|unknown", "recorded|missing|invalid", "missing|invalid",
        "exact|explicit|unknown", "execution|agent|skill|tool|subagent|event|error|retry|permission|unknown_relation_group",
        "recorded|not_observed|invalid", "instruction|tool_input|tool_result|error_message|subagent_input|event_content",
        "available|not_captured|expired|deleted|read_denied|oversized|invalid",
    };

    private static void AssertEveryPatternIsKnown(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("pattern", out var pattern))
            {
                Assert.Contains(pattern.GetString()!, KnownPatterns);
            }
            foreach (var property in element.EnumerateObject())
            {
                AssertEveryPatternIsKnown(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertEveryPatternIsKnown(item);
            }
        }
    }

    private static readonly HashSet<string> KnownPatterns = new(StringComparer.Ordinal)
    {
        "^[0-9a-f]{64}$", "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        "^node-[0-9a-f]{32}$", "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{7}\\+00:00$",
        "^[0-9a-f]{32}$", "^[0-9a-f]{16}$", "^[A-Za-z0-9_-]{158}[AEIMQUYcgkosw048]$",
    };

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

    private static void AssertEnum(JsonElement schema, params object?[] expected)
    {
        var actual = schema.GetProperty("enum").EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Null ? null : value.GetString()).ToArray();
        Assert.Equal(expected.Select(value => value?.ToString()), actual);
    }

    private static void AssertTypes(JsonElement schema, params string[] expected)
    {
        string[] actual;
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array)
        {
            actual = type.EnumerateArray().Select(value => value.GetString()!).ToArray();
        }
        else
        {
            actual = schema.GetProperty("oneOf").EnumerateArray()
                .Select(value => value.TryGetProperty("$ref", out _) ? "$ref" : value.GetProperty("type").GetString()!)
                .ToArray();
        }
        Assert.Equal(expected, actual);
    }

    private static void AssertNoJsonWhitespaceOutsideStrings(byte[] bytes)
    {
        var insideString = false;
        var escaped = false;
        foreach (var value in bytes)
        {
            if (insideString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (value == (byte)'\\')
                {
                    escaped = true;
                }
                else if (value == (byte)'"')
                {
                    insideString = false;
                }
                continue;
            }
            if (value == (byte)'"')
            {
                insideString = true;
                continue;
            }
            Assert.DoesNotContain(value, new byte[] { (byte)' ', (byte)'\t', (byte)'\r', (byte)'\n' });
        }
        Assert.False(insideString);
        Assert.False(escaped);
    }

    private const int RecordedTimeGroup = 0;
    private const int MissingTimeGroup = 1;
    private const int InvalidTimeGroup = 2;

    private static byte[] TimelineFilterFrame(string sessionId, string revision, string? executionId, string? parentNodeId, int limit) =>
        Encoding.ASCII.GetBytes("local-monitor-timeline-filter\0v1\0" + sessionId + "\0" + revision + "\0" + (executionId ?? string.Empty) + "\0" + (parentNodeId ?? string.Empty) + "\0" + limit + "\0");

    private static int CompareCursorOrder((int Group, long Ticks, ulong Ordinal, string NodeId) left, (int Group, long Ticks, ulong Ordinal, string NodeId) right)
    {
        var group = left.Group.CompareTo(right.Group);
        if (group != 0) return group;
        var ticks = left.Ticks.CompareTo(right.Ticks);
        if (ticks != 0) return ticks;
        var ordinal = left.Ordinal.CompareTo(right.Ordinal);
        return ordinal != 0 ? ordinal : string.CompareOrdinal(left.NodeId, right.NodeId);
    }

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
