using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        Assert.True(summaryDefs.GetProperty("tokenComponent").TryGetProperty("allOf", out _));
        Assert.True(summaryDefs.GetProperty("ratioComponent").TryGetProperty("allOf", out _));
        Assert.True(summaryDefs.GetProperty("sessionTiming").TryGetProperty("oneOf", out _));
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
        Assert.True(timelineDefs.GetProperty("tokenComponent").TryGetProperty("allOf", out _));
        Assert.True(timelineDefs.GetProperty("ratioComponent").TryGetProperty("allOf", out _));
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
        Assert.True(nodeDefs.GetProperty("tokenComponent").TryGetProperty("allOf", out _));
        Assert.True(nodeDefs.GetProperty("ratioComponent").TryGetProperty("allOf", out _));
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
        AssertFixture("summary-nonrecorded-evidence.json", SummaryToken, AssertSummaryOrder);
        AssertFixture("timeline-empty.json", TimelineToken, AssertTimelineOrder);
        AssertFixture("timeline-page.json", TimelineToken, AssertTimelineOrder);
        AssertFixture("node-full.json", NodeToken, AssertNodeOrder);
        AssertFixture("node-nested.json", NodeToken, AssertNodeOrder);
        AssertFixture("node-related-serializer-only.json", NodeToken, AssertNodeOrder);
        using var nested = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "node-nested.json")));
        Assert.NotEmpty(nested.RootElement.GetProperty("parent_path").EnumerateArray());
        Assert.All(nested.RootElement.GetProperty("related").EnumerateObject(), property => Assert.Empty(property.Value.EnumerateArray()));
        using var related = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "node-related-serializer-only.json")));
        Assert.All(related.RootElement.GetProperty("related").EnumerateObject(), property => Assert.NotEmpty(property.Value.EnumerateArray()));
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "summary-nonrecorded-evidence.json")));
        foreach (var name in new[] { "model", "version" })
        {
            Assert.NotEqual("recorded", evidence.RootElement.GetProperty("session").GetProperty(name).GetProperty("state").GetString());
            Assert.Empty(evidence.RootElement.GetProperty("session").GetProperty(name).GetProperty("values").EnumerateArray());
        }
        Assert.Equal("native-nonrecorded-evidence", evidence.RootElement.GetProperty("technical_references").GetProperty("native_session_ids")[0].GetString());
    }

    [Fact]
    public void LiteralGoldenResponsesContainNoJsonWhitespaceOutsideStrings()
    {
        foreach (var name in new[] { "summary-empty.json", "summary-full.json", "summary-nonrecorded-evidence.json", "timeline-empty.json", "timeline-page.json", "node-full.json", "node-nested.json", "node-related-serializer-only.json", "transport-contract.json", "query-grammar.json" })
        {
            AssertNoJsonWhitespaceOutsideStrings(File.ReadAllBytes(Path.Combine(FixtureRoot, name)));
        }
    }

    [Fact]
    public void EveryLiteralGoldenValidatesAgainstItsSchema()
    {
        AssertSchemaValid("summary-empty.json", "session-summary");
        AssertSchemaValid("summary-full.json", "session-summary");
        AssertSchemaValid("summary-nonrecorded-evidence.json", "session-summary");
        AssertSchemaValid("timeline-empty.json", "session-timeline");
        AssertSchemaValid("timeline-page.json", "session-timeline");
        AssertSchemaValid("node-full.json", "session-node");
        AssertSchemaValid("node-nested.json", "session-node");
        AssertSchemaValid("node-related-serializer-only.json", "session-node");
    }

    [Fact]
    public void SchemasRejectLiteralStateInvariantMutants()
    {
        AssertSchemaRejectsMutations("summary-full.json", "session-summary",
            root => root["session"]!["source"]!["values"] = new JsonArray(),
            root => root["session"]!["model"]!["values"] = new JsonArray(),
            root => root["session"]!["version"]!["values"] = new JsonArray(),
            root => root["session"]!["assignment"]!["repository_id"] = "not-a-uuid",
            root => root["session"]!["assignment"]!["candidate_repository_ids"] = new JsonArray(Enumerable.Range(0, 129).Select(index => JsonValue.Create($"018f0000-0000-7000-8000-{index:000000000000}")).ToArray()),
            root => root["session"]!["archive"]!["effectively_eligible"] = false,
            root => root["session"]!["instruction"]!["state"] = "recorded",
            root => root["session"]!["tokens"]!["input"]!["state"] = "not_observed",
            root => root["session"]!["activity"]!["skill"]!["state"] = "recorded",
            root => { root["session"]!["timing"]!["state"] = "recorded"; root["session"]!["timing"]!["started_at"] = null; },
            root => root["executions"]![0]!["timing"]!["started_at"] = null,
            root => root["executions"]![0]!["timing"]!["ended_at"] = null,
            root => { root["executions"]![0]!["timing"]!["state"] = "missing"; });

        AssertSchemaRejectsMutations("timeline-page.json", "session-timeline",
            root => root["items"]![0]!["name"]!["state"] = "not_observed",
            root => root["items"]![0]!["tokens"]!["input"]!["state"] = "recorded",
            root => root["items"]![0]!["activity"]!["skill"]!["state"] = "recorded",
            root => root["items"]![0]!["timing"]!["started_at"] = null,
            root => root["items"]![0]!["timing"]!["ended_at"] = null,
            root => { root["items"]![0]!["timing"]!["state"] = "invalid"; });

        AssertSchemaRejectsMutations("node-full.json", "session-node",
            root => root["content"]!["instruction"]!["available"] = true,
            root => root["content"]!["tool_input"]!["available"] = true,
            root => root["node"]!["technical_references"]!["trace_id"] = "not-a-trace",
            root => root["node"]!["name"]!["state"] = "recorded",
            root => root["node"]!["tokens"]!["input"]!["state"] = "not_observed",
            root => { root["node"]!["timing"]!["state"] = "missing"; });
    }

    [Fact]
    public void TimelineCursorHasFrozen119ByteFrameAndLiteralFixtureBinding()
    {
        const string Golden = "AfuEzJkJsG4UwJEhS4gSoWXoBco3Yp1ktuIeLpliMnnoAAjfAw2j83eAAAAAAAAAAAFub2RlLWE4YTc3M2Q2NjE0ZDUwMzBmNTA1ZmYxOTViNDUyZGQ2sNQxzSz5hydOvIUgXLq3Lb8OCL9CkdxLT-KUQYhrjU0";
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, "timeline-page.json")));
        Assert.Equal(Golden, fixture.RootElement.GetProperty("next_cursor").GetString());
        var bytes = DecodeBase64Url(Golden);

        Assert.Equal(159, Golden.Length);
        Assert.Equal(119, bytes.Length);
        Assert.Equal(1, bytes[0]);
        Assert.Equal(0, bytes[33]);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero).Ticks, BinaryPrimitives.ReadInt64BigEndian(bytes[34..42]));
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64BigEndian(bytes[42..50]));
        Assert.Equal("node-a8a773d6614d5030f505ff195b452dd6", Encoding.ASCII.GetString(bytes, 50, 37));

        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var filterFrame = TimelineFilterFrame(
            "018f0000-0000-7000-8000-000000000001",
            "334bb00f10ac3c7527db3cbfaed6f89c7a33a92a749ecad70c7708f3bb08d24a",
            "9a5590c8-46e3-7069-af48-3844d2bf17a4",
            null,
            1);
        Assert.Equal(HMACSHA256.HashData(key, filterFrame), bytes[1..33]);
        var tagFrame = Encoding.ASCII.GetBytes("local-monitor-timeline-cursor\0v1\0").Concat(bytes[..87]).ToArray();
        Assert.Equal(HMACSHA256.HashData(key, tagFrame), bytes[87..119]);

        Assert.NotEqual(bytes[1..33], HMACSHA256.HashData(key, TimelineFilterFrame("018f0000-0000-7000-8000-000000000001", new string('2', 64), "018f0000-0000-7000-8000-000000000003", null, 1)));
        Assert.NotEqual(bytes[1..33], HMACSHA256.HashData(key, TimelineFilterFrame("018f0000-0000-7000-8000-000000000002", new string('1', 64), "018f0000-0000-7000-8000-000000000003", null, 1)));
        Assert.NotEqual(bytes[1..33], HMACSHA256.HashData(key, TimelineFilterFrame("018f0000-0000-7000-8000-000000000001", new string('1', 64), "018f0000-0000-7000-8000-000000000004", null, 1)));
        Assert.NotEqual(bytes[1..33], HMACSHA256.HashData(key, TimelineFilterFrame("018f0000-0000-7000-8000-000000000001", new string('1', 64), "018f0000-0000-7000-8000-000000000003", "node-00000000000000000000000000000001", 1)));
        Assert.NotEqual(bytes[1..33], HMACSHA256.HashData(key, TimelineFilterFrame("018f0000-0000-7000-8000-000000000001", new string('1', 64), "018f0000-0000-7000-8000-000000000003", null, 2)));
        Assert.Equal(new[] { 0, 1, 2 }, new[] { RecordedTimeGroup, MissingTimeGroup, InvalidTimeGroup });
        Assert.True(CompareCursorOrder((0, 1L, 0UL, "node-00000000000000000000000000000009"), (0, 2L, 0UL, "node-00000000000000000000000000000000")) < 0);
        Assert.True(CompareCursorOrder((0, 1L, 0UL, "node-00000000000000000000000000000001"), (0, 1L, 1UL, "node-00000000000000000000000000000000")) < 0);
        Assert.True(CompareCursorOrder((0, 1L, 1UL, "node-00000000000000000000000000000001"), (0, 1L, 1UL, "node-00000000000000000000000000000002")) < 0);
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
        var expectedRoutes = new Dictionary<string, (string Path, string MediaType, string SchemaToken, (string Name, string Value)[] Headers)>(StringComparer.Ordinal)
        {
            ["summary"] = ("/api/local-monitor/v1/sessions/{sessionId}/summary", "application/json; charset=utf-8", SummaryToken, [("Cache-Control", "no-store"), ("Content-Type", "application/json; charset=utf-8"), ("Content-Length", "exact_representation_utf8_byte_length")]),
            ["timeline"] = ("/api/local-monitor/v1/sessions/{sessionId}/timeline", "application/json; charset=utf-8", TimelineToken, [("Cache-Control", "no-store"), ("Content-Type", "application/json; charset=utf-8"), ("Content-Length", "exact_representation_utf8_byte_length")]),
            ["node"] = ("/api/local-monitor/v1/sessions/{sessionId}/nodes/{nodeId}", "application/json; charset=utf-8", NodeToken, [("Cache-Control", "no-store"), ("Content-Type", "application/json; charset=utf-8"), ("Content-Length", "exact_representation_utf8_byte_length")]),
            ["content"] = ("/api/local-monitor/v1/sessions/{sessionId}/nodes/{nodeId}/content", "text/plain; charset=utf-8", ContentToken, [("Cache-Control", "no-store"), ("Content-Type", "text/plain; charset=utf-8"), ("Content-Length", "exact_representation_utf8_byte_length"), ("X-Local-Monitor-Schema-Version", ContentToken)]),
        };
        Assert.All(root.GetProperty("routes").EnumerateArray(), route =>
        {
            AssertProperties(route, "name", "path", "query_order", "methods", "success_status", "success_media_type", "schema_token", "method_not_allowed_status", "required_headers");
            var expected = expectedRoutes[route.GetProperty("name").GetString()!];
            Assert.Equal(expected.Path, route.GetProperty("path").GetString());
            Assert.Equal(new[] { "GET", "HEAD" }, route.GetProperty("methods").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(200, route.GetProperty("success_status").GetInt32());
            Assert.Equal(expected.MediaType, route.GetProperty("success_media_type").GetString());
            Assert.Equal(expected.SchemaToken, route.GetProperty("schema_token").GetString());
            Assert.Equal(405, route.GetProperty("method_not_allowed_status").GetInt32());
            Assert.Equal(expected.Headers, route.GetProperty("required_headers").EnumerateArray().Select(header =>
            {
                AssertProperties(header, "name", "value");
                return (header.GetProperty("name").GetString()!, header.GetProperty("value").GetString()!);
            }));
        });
        var routes = root.GetProperty("routes").EnumerateArray().ToDictionary(route => route.GetProperty("name").GetString()!, StringComparer.Ordinal);
        Assert.Empty(routes["summary"].GetProperty("query_order").EnumerateArray());
        Assert.Equal(new[] { "workspace_revision", "execution_id", "parent_node_id", "after", "limit" }, routes["timeline"].GetProperty("query_order").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "workspace_revision" }, routes["node"].GetProperty("query_order").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "workspace_revision", "part" }, routes["content"].GetProperty("query_order").EnumerateArray().Select(value => value.GetString()));
        var queries = root.GetProperty("query_fields").EnumerateArray().ToDictionary(field => field.GetProperty("name").GetString()!, StringComparer.Ordinal);
        Assert.Equal(new[] { "workspace_revision", "execution_id", "parent_node_id", "after", "limit", "part" }, queries.Keys);
        Assert.All(queries.Values, field => AssertProperties(field, "name", "required_on", "requires", "pattern", "minimum", "maximum", "default", "enum"));
        AssertQueryField(queries["workspace_revision"], "workspace_revision", ["timeline", "node", "content"], null, "^[0-9a-f]{64}$", null, null, null, null);
        AssertQueryField(queries["execution_id"], "execution_id", [], null, "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", null, null, null, null);
        AssertQueryField(queries["parent_node_id"], "parent_node_id", [], "execution_id", "^node-[0-9a-f]{32}$", null, null, null, null);
        AssertQueryField(queries["after"], "after", [], null, "^[A-Za-z0-9_-]{158}[AEIMQUYcgkosw048]$", null, null, null, null);
        AssertQueryField(queries["limit"], "limit", [], null, "^(?:[1-9]|[1-9][0-9]|1[0-9]{2}|200)$", 1, 200, 100, null);
        AssertQueryField(queries["part"], "part", ["content"], null, null, null, null, null, ContentParts);
        Assert.Equal(new[] { "host", "method", "path_identifier", "closed_query", "session", "workspace_revision", "execution_or_node_membership", "cursor", "retention_lease" }, root.GetProperty("error_precedence").EnumerateArray().Select(value => value.GetString()));

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
        var generatedOrder = grammar.RootElement.GetProperty("generated_order");
        AssertProperties(generatedOrder, "summary", "timeline", "node", "content");
        var expectedQueryOrder = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["summary"] = [],
            ["timeline"] = ["workspace_revision", "execution_id", "parent_node_id", "after", "limit"],
            ["node"] = ["workspace_revision"],
            ["content"] = ["workspace_revision", "part"],
        };
        foreach (var (routeName, expectedOrder) in expectedQueryOrder)
        {
            Assert.Equal(expectedOrder, generatedOrder.GetProperty(routeName).EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(expectedOrder, routes[routeName].GetProperty("query_order").EnumerateArray().Select(value => value.GetString()));
        }
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

    private static void AssertSchemaRejectsMutations(string fixtureName, string schemaName, params Action<JsonNode>[] mutations)
    {
        var temporaryFiles = new List<(string Path, int Index)>();
        try
        {
            for (var index = 0; index < mutations.Length; index++)
            {
                var root = JsonNode.Parse(File.ReadAllBytes(Path.Combine(FixtureRoot, fixtureName)))!;
                mutations[index](root);
                var path = Path.Combine(Path.GetTempPath(), "local-monitor-detail-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(path, root.ToJsonString(), new UTF8Encoding(false));
                temporaryFiles.Add((path, index));
            }
            foreach (var (path, index) in temporaryFiles)
            {
                Assert.False(ValidateWithPowerShellJsonSchema(path, SchemaPath(schemaName)), $"{fixtureName} mutation {index}");
            }
        }
        finally
        {
            foreach (var (path, _) in temporaryFiles)
            {
                File.Delete(path);
            }
        }
    }

    private static bool ValidateWithPowerShellJsonSchema(string instancePath, string schemaPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh",
            ArgumentList =
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-CommandWithArgs",
                "$instance = Get-Content -Raw -LiteralPath $args[0]; " +
                "if ($instance | Test-Json -SchemaFile $args[1] -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }",
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

    private static void AssertQueryField(
        JsonElement field,
        string name,
        string[] requiredOn,
        string? requires,
        string? pattern,
        int? minimum,
        int? maximum,
        int? defaultValue,
        string[]? values)
    {
        Assert.Equal(name, field.GetProperty("name").GetString());
        Assert.Equal(requiredOn, field.GetProperty("required_on").EnumerateArray().Select(value => value.GetString()));
        AssertNullableString(field.GetProperty("requires"), requires);
        AssertNullableString(field.GetProperty("pattern"), pattern);
        AssertNullableInt32(field.GetProperty("minimum"), minimum);
        AssertNullableInt32(field.GetProperty("maximum"), maximum);
        AssertNullableInt32(field.GetProperty("default"), defaultValue);
        var enumValue = field.GetProperty("enum");
        if (values is null)
        {
            Assert.Equal(JsonValueKind.Null, enumValue.ValueKind);
        }
        else
        {
            Assert.Equal(values, enumValue.EnumerateArray().Select(value => value.GetString()));
        }
    }

    private static void AssertNullableString(JsonElement value, string? expected)
    {
        if (expected is null) Assert.Equal(JsonValueKind.Null, value.ValueKind);
        else Assert.Equal(expected, value.GetString());
    }

    private static void AssertNullableInt32(JsonElement value, int? expected)
    {
        if (expected is null) Assert.Equal(JsonValueKind.Null, value.ValueKind);
        else Assert.Equal(expected.Value, value.GetInt32());
    }

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
