using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class MonitorSkillProjectionTests
{
    private const string TraceId = "11111111111111111111111111111111";
    private const string RecognisedVersion = "1.0.74";

    [Fact]
    public async Task ResolvedCliTrace_ProjectsInvokedSkillAndAvailableInventory()
    {
        const string payload =
            """
            {"resourceSpans":[{
              "resource":{"attributes":[
                {"key":"service.version","value":{"stringValue":"1.0.74"}},
                {"key":"client.kind","value":{"stringValue":"copilot-cli"}}
              ]},
              "scopeSpans":[{"spans":[{
                "traceId":"11111111111111111111111111111111",
                "spanId":"1111111111111111",
                "name":"execute_tool skill",
                "attributes":[
                  {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
                  {"key":"gen_ai.tool.name","value":{"stringValue":"skill"}},
                  {"key":"github.copilot.skill.name","value":{"stringValue":"probe-marker-skill"}},
                  {"key":"github.copilot.skill.source","value":{"stringValue":"project"}},
                  {"key":"github.copilot.skill.invocation_trigger","value":{"stringValue":"agent-invoked"}},
                  {"key":"github.copilot.tool.parameters.skill_name","value":{"stringValue":"probe-marker-skill"}},
                  {"key":"github.copilot.tool.parameters.file_path","value":{"stringValue":"C:\\Users\\fixture\\SKILL.md"}},
                  {"key":"github.copilot.context.skills","value":{"arrayValue":{"values":[
                    {"stringValue":"probe-marker-skill"},
                    {"stringValue":"other-skill"}
                  ]}}}
                ]
              }]}]
            }]}
            """;
        using var projected = await ProjectAsync(payload, "github-copilot-cli", [RecognisedVersion]);
        using var connection = Open(projected.DatabasePath);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT trace_id, span_id, skill_name, skill_source, invocation_trigger,
                       source_application_version, session_id
                FROM skill_projection_invocations;
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(TraceId, reader.GetString(0));
            Assert.Equal("1111111111111111", reader.GetString(1));
            Assert.Equal("probe-marker-skill", reader.GetString(2));
            Assert.Equal("project", reader.GetString(3));
            Assert.Equal("agent-invoked", reader.GetString(4));
            Assert.Equal(RecognisedVersion, reader.GetString(5));
            Assert.True(reader.IsDBNull(6));
            Assert.False(reader.Read());
        }

        Assert.Equal(
            ["probe-marker-skill", "other-skill"],
            ReadStrings(
                connection,
                "SELECT skill_name FROM skill_projection_inventory_names ORDER BY name_ordinal;"));
        Assert.Equal(
            "2|2|0|1.0.74",
            Scalar<string>(
                connection,
                """
                SELECT observed_name_count || '|' || retained_name_count || '|' ||
                       names_truncated || '|' || source_application_version
                FROM skill_projection_inventories;
                """));
        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                """
                SELECT COUNT(*) FROM skill_projection_invocations
                WHERE skill_name LIKE '%SKILL.md%' OR skill_source LIKE '%SKILL.md%' OR invocation_trigger LIKE '%SKILL.md%';
                """));
    }

    [Theory]
    [InlineData("chat", "skill")]
    [InlineData("execute_tool", "shell")]
    public async Task NonDedicatedToolSpanWithSkillName_ProjectsNoInvocation(
        string operation,
        string toolName)
    {
        var payload = SkillPayload(
            """{"key":"service.version","value":{"stringValue":"1.0.74"}},""",
            "safe-skill",
            ["safe-skill"],
            operation: operation,
            toolName: toolName);
        using var projected = await ProjectAsync(payload, "github-copilot-cli", [RecognisedVersion]);
        using var connection = Open(projected.DatabasePath);

        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM skill_projection_invocations;"));
    }

    [Fact]
    public async Task RedeliveredSpanAcrossRawRecords_ProjectsEachExactRawInput()
    {
        var payload = SkillPayload(
            """{"key":"service.version","value":{"stringValue":"1.0.74"}},""",
            "safe-skill",
            ["safe-skill"]);
        using var projected = await ProjectAsync(
            payload,
            "github-copilot-cli",
            [RecognisedVersion],
            deliveryCount: 2);
        using var connection = Open(projected.DatabasePath);

        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM skill_projection_invocations;"));
    }

    [Fact]
    public async Task SkillToolSpan_RequiresSpanIdToProjectInvocation()
    {
        var payloadWithSpanId = SkillPayload(
            """{"key":"service.version","value":{"stringValue":"1.0.74"}},""",
            "safe-skill",
            ["safe-skill"]);
        var payloadWithoutSpanId = payloadWithSpanId.Replace(
            """
                "spanId":"1111111111111111",

            """,
            string.Empty,
            StringComparison.Ordinal);
        var payloadWithEmptySpanId = payloadWithSpanId.Replace(
            "\"spanId\":\"1111111111111111\"",
            "\"spanId\":\"\"",
            StringComparison.Ordinal);

        using var withoutSpanId = await ProjectAsync(
            payloadWithoutSpanId,
            "github-copilot-cli",
            [RecognisedVersion]);
        using var withoutSpanIdConnection = Open(withoutSpanId.DatabasePath);
        Assert.Equal(
            0L,
            Scalar<long>(
                withoutSpanIdConnection,
                "SELECT COUNT(*) FROM skill_projection_invocations;"));

        using var withEmptySpanId = await ProjectAsync(
            payloadWithEmptySpanId,
            "github-copilot-cli",
            [RecognisedVersion]);
        using var withEmptySpanIdConnection = Open(withEmptySpanId.DatabasePath);
        Assert.Equal(
            0L,
            Scalar<long>(
                withEmptySpanIdConnection,
                "SELECT COUNT(*) FROM skill_projection_invocations;"));

        using var withSpanId = await ProjectAsync(
            payloadWithSpanId,
            "github-copilot-cli",
            [RecognisedVersion]);
        using var withSpanIdConnection = Open(withSpanId.DatabasePath);
        Assert.Equal(
            1L,
            Scalar<long>(
                withSpanIdConnection,
                "SELECT COUNT(*) FROM skill_projection_invocations;"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("conflicting")]
    [InlineData("unrecognised")]
    public async Task NonResolvedCliTrace_ProjectsNoSkillData(string resolution)
    {
        var resourceVersions = resolution switch
        {
            "missing" => string.Empty,
            "conflicting" =>
                """
                {"key":"service.version","value":{"stringValue":"1.0.74"}},
                {"key":"service.version","value":{"stringValue":"1.0.75"}},
                """,
            "unrecognised" =>
                """{"key":"service.version","value":{"stringValue":"9.9.9"}},""",
            _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
        };
        var recognisedVersions = resolution == "conflicting"
            ? new[] { "1.0.74", "1.0.75" }
            : new[] { RecognisedVersion };
        var payload = SkillPayload(resourceVersions, "safe-skill", ["safe-skill"]);
        using var projected = await ProjectAsync(payload, "github-copilot-cli", recognisedVersions);
        using var connection = Open(projected.DatabasePath);

        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM skill_projection_inventories;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM skill_projection_inventory_names;"));
    }

    [Fact]
    public async Task VscodeSourceWithSkillAttributes_ProjectsNoSkillData()
    {
        var payload = SkillPayload(
            """{"key":"service.version","value":{"stringValue":"1.0.74"}},""",
            "safe-skill",
            ["safe-skill"]);
        using var projected = await ProjectAsync(payload, "github-copilot-vscode", [RecognisedVersion]);
        using var connection = Open(projected.DatabasePath);

        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM skill_projection_inventories;"));
    }

    [Fact]
    public async Task OversizedInventory_IsBoundedAndRecordsTruncation()
    {
        var availableNames = Enumerable.Range(0, 105)
            .Select(index => index == 0 ? new string('a', 300) : $"skill-{index:D3}")
            .ToArray();
        var payload = SkillPayload(
            """{"key":"service.version","value":{"stringValue":"1.0.74"}},""",
            "invoked-skill",
            availableNames);
        using var projected = await ProjectAsync(payload, "github-copilot-cli", [RecognisedVersion]);
        using var connection = Open(projected.DatabasePath);

        Assert.Equal(
            "105|100|1",
            Scalar<string>(
                connection,
                """
                SELECT observed_name_count || '|' || retained_name_count || '|' || names_truncated
                FROM skill_projection_inventories;
                """));
        Assert.Equal(100L, Scalar<long>(connection, "SELECT COUNT(*) FROM skill_projection_inventory_names;"));
        Assert.Equal(256L, Scalar<long>(connection, "SELECT MAX(length(skill_name)) FROM skill_projection_inventory_names;"));
    }

    [Fact]
    public async Task UnsafeIdentifiers_AreDroppedInsteadOfStoredRaw()
    {
        const string unsafeIdentifier = "C:\\Users\\fixture\\secret-skill";
        var payload = SkillPayload(
            """{"key":"service.version","value":{"stringValue":"1.0.74"}},""",
            unsafeIdentifier,
            [unsafeIdentifier, "safe-skill"],
            skillSource: unsafeIdentifier,
            invocationTrigger: unsafeIdentifier);
        using var projected = await ProjectAsync(payload, "github-copilot-cli", [RecognisedVersion]);
        using var connection = Open(projected.DatabasePath);

        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal(["safe-skill"], ReadStrings(
            connection,
            "SELECT skill_name FROM skill_projection_inventory_names ORDER BY name_ordinal;"));
        Assert.DoesNotContain(
            unsafeIdentifier,
            string.Join('|', ReadStrings(
                connection,
                "SELECT skill_name FROM skill_projection_inventory_names ORDER BY name_ordinal;")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SanitizedOnlyMode_KeepsSanitizedSkillProjectionAvailable()
    {
        var payload = SkillPayload(
            """{"key":"service.version","value":{"stringValue":"1.0.74"}},""",
            "safe-skill",
            ["safe-skill"]);
        using var projected = await ProjectAsync(
            payload,
            "github-copilot-cli",
            [RecognisedVersion],
            sanitizedOnly: true);
        using var connection = Open(projected.DatabasePath);

        Assert.Equal(
            "safe-skill",
            Scalar<string>(
                connection,
                "SELECT skill_name FROM skill_projection_invocations;"));
        Assert.Equal(
            ["safe-skill"],
            ReadStrings(
                connection,
                "SELECT skill_name FROM skill_projection_inventory_names ORDER BY name_ordinal;"));
    }

    [Theory]
    [InlineData("copilot-cli", true)]
    [InlineData("vscode", false)]
    public async Task SessionBinding_RequiresExactCopilotCliNativeIdentity(
        string nativeSourceSurface,
        bool expectedBound)
    {
        const string sessionId = "0198f5b8-0c00-7000-8000-000000000001";
        const string nativeSessionId = "native-cli-session";
        var payload = SkillPayload(
            """{"key":"service.version","value":{"stringValue":"1.0.74"}},""",
            "safe-skill",
            ["safe-skill"],
            nativeSessionId: nativeSessionId);
        using var projected = await ProjectAsync(
            payload,
            "github-copilot-cli",
            [RecognisedVersion],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO sessions (
                        session_id, status, completeness, last_seen_at,
                        raw_retention_state, created_at, updated_at
                    ) VALUES (
                        $session_id, 'active', 'partial', '2026-07-29T00:00:00.0000000+00:00',
                        'not_captured', '2026-07-29T00:00:00.0000000+00:00',
                        '2026-07-29T00:00:00.0000000+00:00'
                    );
                    INSERT INTO session_native_ids (
                        session_id, source_surface, native_session_id, binding_kind, observed_at
                    ) VALUES (
                        $session_id, $source_surface, $native_session_id, 'native',
                        '2026-07-29T00:00:00.0000000+00:00'
                    );
                    """;
                command.Parameters.AddWithValue("$session_id", sessionId);
                command.Parameters.AddWithValue("$source_surface", nativeSourceSurface);
                command.Parameters.AddWithValue("$native_session_id", nativeSessionId);
                command.ExecuteNonQuery();
            });
        using var connection = Open(projected.DatabasePath);

        var storedSessionId = Scalar<string?>(
            connection,
            "SELECT session_id FROM skill_projection_invocations;");
        Assert.Equal(expectedBound ? sessionId : null, storedSessionId);
    }

    [Fact]
    public async Task SkillProjection_DoesNotChangeMonitorApiResponseBytes()
    {
        var payload = SkillPayload(
            """{"key":"service.version","value":{"stringValue":"1.0.74"}},""",
            "safe-skill",
            ["safe-skill"]);
        var registry = CreateRegistry(payload, "github-copilot-cli", [RecognisedVersion]);
        var metadata = OtlpTraceSourceMetadata.Create(
            "github-copilot-cli",
            sourceApplicationVersion: null,
            "raw-otlp",
            "1",
            SourceCaptureContentState.Unsupported);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceFingerprintRegistry = registry,
            SourceMetadataProvider = new FixedOtlpTraceSourceMetadataProvider(metadata),
        });
        using var response = await host.Client.PostAsync(
            "/v1/traces",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var worker = new ProjectionWorker(
            new RawTelemetryStoreProjectionStore(
                temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter)),
            health,
            new SqliteSourceCompatibilityStore(
                temp.DatabasePath,
                RawTelemetryStoreConnectionOptions.MonitorWriter));
        await worker.RunProjectionPassAsync();

        string[] paths =
        [
            "/api/monitor/ingestions",
            "/api/monitor/source-diagnostics",
            "/api/monitor/traces",
            $"/api/monitor/traces/{TraceId}/spans",
            $"/api/monitor/traces/{TraceId}/agent-graph",
            "/api/monitor/summary",
            "/api/monitor/overview",
            "/api/monitor/trace-list",
        ];
        var withoutSkills = await CaptureResponses(host.Client, paths);
        await RunSkillProjectionAsync(host.Services, temp.TimeProvider);
        var withSkills = await CaptureResponses(host.Client, paths);

        Assert.Equal(withSkills.Length, withoutSkills.Length);
        for (var index = 0; index < withSkills.Length; index++)
        {
            Assert.Equal(withSkills[index].StatusCode, withoutSkills[index].StatusCode);
            Assert.Equal(withSkills[index].Body, withoutSkills[index].Body);
        }
    }

    private static async Task<MonitorTempDirectory> ProjectAsync(
        string payload,
        string sourceSurface,
        IReadOnlyList<string> recognisedVersions,
        Action<SqliteConnection>? beforeProjection = null,
        bool sanitizedOnly = false,
        int deliveryCount = 1)
    {
        var registry = CreateRegistry(payload, sourceSurface, recognisedVersions);
        var metadata = OtlpTraceSourceMetadata.Create(
            sourceSurface,
            sourceApplicationVersion: null,
            "raw-otlp",
            "1",
            SourceCaptureContentState.Unsupported);
        var temp = new MonitorTempDirectory();
        try
        {
            await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: sanitizedOnly, testOptions: new MonitorHostTestOptions
            {
                StartProjectionWorker = false,
                StartSessionOtelEnrichment = false,
                UseUserSecrets = false,
                SourceFingerprintRegistry = registry,
                SourceMetadataProvider = new FixedOtlpTraceSourceMetadataProvider(metadata),
            });
            for (var delivery = 0; delivery < deliveryCount; delivery++)
            {
                using var response = await host.Client.PostAsync(
                    "/v1/traces",
                    new StringContent(payload, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            if (beforeProjection is not null)
            {
                using var connection = Open(temp.DatabasePath);
                beforeProjection(connection);
            }

            var health = new MonitorHealthState();
            health.MarkMigrationComplete();
            var worker = new ProjectionWorker(
                new RawTelemetryStoreProjectionStore(
                    temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter)),
                health,
                new SqliteSourceCompatibilityStore(
                    temp.DatabasePath,
                    RawTelemetryStoreConnectionOptions.MonitorWriter));
            await worker.RunProjectionPassAsync();
            await RunSkillProjectionAsync(host.Services, temp.TimeProvider);
            return temp;
        }
        catch
        {
            temp.Dispose();
            throw;
        }
    }

    private static async Task RunSkillProjectionAsync(
        IServiceProvider services,
        TimeProvider timeProvider)
    {
        var worker = new SkillProjectionWorker(
            services.GetRequiredService<SqliteSkillProjectionStore>(),
            timeProvider: timeProvider);
        var now = timeProvider.GetUtcNow();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await worker.RunNextAsync(now) is SkillProjectionWorkOutcome.NoWork)
                return;
            if (timeProvider is MutableTimeProvider mutable)
            {
                mutable.Advance(TimeSpan.FromMinutes(5));
                now = mutable.GetUtcNow();
            }
            else
            {
                now = now.AddMinutes(5);
            }
        }
        Assert.Fail("Skill projection did not reach a stable no-work state.");
    }

    private static string SkillPayload(
        string resourceVersions,
        string skillName,
        IReadOnlyList<string> availableNames,
        string skillSource = "project",
        string invocationTrigger = "agent-invoked",
        string? nativeSessionId = null,
        string operation = "execute_tool",
        string toolName = "skill")
    {
        var availableValues = string.Join(
            ',',
            availableNames.Select(name =>
                $$"""{"stringValue":{{JsonSerializer.Serialize(name)}}}"""));
        var nativeSessionAttribute = nativeSessionId is null
            ? string.Empty
            : ",{\"key\":\"gen_ai.conversation.id\",\"value\":{\"stringValue\":"
                + JsonSerializer.Serialize(nativeSessionId)
                + "}}";
        return """
            {"resourceSpans":[{
              "resource":{"attributes":[
                __RESOURCE_VERSIONS__
                {"key":"client.kind","value":{"stringValue":"copilot-cli"}}
              ]},
              "scopeSpans":[{"spans":[{
                "traceId":"__TRACE_ID__",
                "spanId":"1111111111111111",
                "name":"execute_tool skill",
                "attributes":[
                  {"key":"gen_ai.operation.name","value":{"stringValue":__OPERATION__}},
                  {"key":"gen_ai.tool.name","value":{"stringValue":__TOOL_NAME__}},
                  {"key":"github.copilot.skill.name","value":{"stringValue":__SKILL_NAME__}},
                  {"key":"github.copilot.skill.source","value":{"stringValue":__SKILL_SOURCE__}},
                  {"key":"github.copilot.skill.invocation_trigger","value":{"stringValue":__INVOCATION_TRIGGER__}},
                  {"key":"github.copilot.tool.parameters.skill_name","value":{"stringValue":__SKILL_NAME__}},
                  {"key":"github.copilot.context.skills","value":{"arrayValue":{"values":[__AVAILABLE_VALUES__]}}}__NATIVE_SESSION_ATTRIBUTE__
                ]
              }]}]
            }]}
            """
            .Replace("__RESOURCE_VERSIONS__", resourceVersions, StringComparison.Ordinal)
            .Replace("__TRACE_ID__", TraceId, StringComparison.Ordinal)
            .Replace("__SKILL_NAME__", JsonSerializer.Serialize(skillName), StringComparison.Ordinal)
            .Replace("__SKILL_SOURCE__", JsonSerializer.Serialize(skillSource), StringComparison.Ordinal)
            .Replace("__INVOCATION_TRIGGER__", JsonSerializer.Serialize(invocationTrigger), StringComparison.Ordinal)
            .Replace("__AVAILABLE_VALUES__", availableValues, StringComparison.Ordinal)
            .Replace("__NATIVE_SESSION_ATTRIBUTE__", nativeSessionAttribute, StringComparison.Ordinal)
            .Replace("__OPERATION__", JsonSerializer.Serialize(operation), StringComparison.Ordinal)
            .Replace("__TOOL_NAME__", JsonSerializer.Serialize(toolName), StringComparison.Ordinal);
    }

    private static VerifiedSourceFingerprintRegistry CreateRegistry(
        string payload,
        string sourceSurface,
        IReadOnlyList<string> recognisedVersions)
    {
        var inventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json",
            Encoding.UTF8.GetBytes(payload)).StructuralInventory;
        return VerifiedSourceFingerprintRegistry.Create(
            recognisedVersions
                .Select(version => VerifiedSourceFingerprintEvidence.Create(
                    sourceSurface,
                    version,
                    inventory.SchemaFingerprint))
                .ToArray(),
            [],
            []);
    }

    private static async Task<(HttpStatusCode StatusCode, byte[] Body)[]> CaptureResponses(
        HttpClient client,
        IEnumerable<string> paths)
    {
        var responses = new List<(HttpStatusCode StatusCode, byte[] Body)>();
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(path);
            responses.Add((response.StatusCode, await response.Content.ReadAsByteArrayAsync()));
        }
        return responses.ToArray();
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
        {
            return default!;
        }
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static string[] ReadStrings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }
        return values.ToArray();
    }
}
