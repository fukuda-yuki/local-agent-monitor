using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.FirstTrace;
using CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.Doctor.Tests.Persistence;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Doctor.ClaudeCode;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.Doctor.Tests.ClaudeCode;

public sealed class ClaudeFirstTraceCrossSurfaceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 1, 2, 3, TimeSpan.Zero);

    private const string NativeSessionMarker = "SYNTHETIC_NATIVE_SESSION_X";
    private const string PromptMarker = "SYNTHETIC_PROMPT_MARKER";
    private const string PathMarker = "SYNTHETIC_PATH_MARKER";
    private const string TraceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BaselineTraceId = "11111111111111111111111111111111";
    private const string SpanId = "bbbbbbbbbbbbbbbb";
    private const string InteractionSpanId = "cccccccccccccccc";
    private const string ToolSpanId = "dddddddddddddddd";

    [Fact]
    public async Task SetupBeginRealMonitorStatusAndCompleteProveClaudeFirstTraceAcrossSurfaces()
    {
        var directory = CreateDirectory("claude-first-trace-positive");
        var databasePath = Path.Combine(directory, "monitor.db");
        var origin = ReserveLoopbackOrigin();
        var time = new DoctorTestTimeProvider(Now);
        try
        {
            PrepareBaselineDatabase(databasePath, time);

            var setupPlatform = CreatePlatform(databasePath, endpoint: origin);
            AssertChangedSetupHandoff(setupPlatform, origin);

            var beginPlatform = CreatePlatform(databasePath, endpoint: origin);
            var beginOrchestrator = CreateOrchestrator(beginPlatform, time);
            using var begin = RunFirstTrace(
                beginOrchestrator,
                [
                    "begin", "--adapter", "claude-code", "--database", databasePath,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            var verificationId = begin.RootElement.GetProperty("verification_id").GetString();
            Assert.NotNull(verificationId);
            Assert.Equal("first_trace_verification_started", begin.RootElement.GetProperty("code").GetString());
            Assert.Equal("doctor.v1", begin.RootElement.GetProperty("doctor").GetProperty("schema_version").GetString());
            Assert.NotEmpty(begin.RootElement.GetProperty("guidance").EnumerateArray());
            AssertNoSensitiveMarkers(begin.RootElement.GetRawText());

            await using var monitor = await RunningMonitor.StartAsync(databasePath, origin, time);
            await monitor.PostSessionStartAsync(NativeSessionMarker);
            await monitor.PostOtlpAsync(Payload(TraceId, NativeSessionMarker));
            await monitor.DrainAsync();

            var observer = new ClaudeDoctorCandidateObserver(databasePath, time);
            observer.RunOnce();

            using var status = RunFirstTrace(
                beginOrchestrator,
                [
                    "status", "--database", databasePath, "--verification-id", verificationId!,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            var candidates = status.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
            Assert.Equal(
                Enum.GetValues<DoctorEvidenceKind>(),
                candidates.Select(candidate => ParseEvidenceKind(candidate.GetProperty("evidence_kind").GetString()!)).Distinct().OrderBy(kind => kind));
            Assert.All(candidates, candidate =>
            {
                var evidenceRef = candidate.GetProperty("evidence_ref").GetString()!;
                Assert.True(DoctorValidation.IsValidEvidenceReference(evidenceRef));
                Assert.Matches(
                    "^claude-otel-(ingest|raw|projection)-[0-9a-f]{32}-[0-9a-f]{16}$|^claude-otel-binding-[0-9a-f]{32}-[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$|^claude-otel-completeness-[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                    evidenceRef);
            });
            AssertNoSensitiveMarkers(status.RootElement.GetRawText());

            var completionPlatform = CreatePlatform(databasePath, endpoint: "http://127.0.0.1:4320");
            var completionOrchestrator = CreateOrchestrator(completionPlatform, time);
            using var complete = RunFirstTrace(
                completionOrchestrator,
                [
                    "complete", "--database", databasePath, "--verification-id", verificationId!,
                    "--expected-revision", "1", "--json",
                ],
                expectedExitCode: 0);
            Assert.Equal("first_trace_completed", complete.RootElement.GetProperty("code").GetString());
            Assert.Equal("doctor.v1", complete.RootElement.GetProperty("doctor").GetProperty("schema_version").GetString());
            Assert.Equal("verification_completed", complete.RootElement.GetProperty("doctor").GetProperty("code").GetString());
            Assert.Equal(
                "first_trace_ready",
                complete.RootElement.GetProperty("doctor").GetProperty("evaluation").GetProperty("primary_state").GetProperty("state_code").GetString());
            AssertNoSensitiveMarkers(complete.RootElement.GetRawText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SharedTraceIdWithoutSessionIdCannotPromoteClaudeFirstTrace()
    {
        var directory = CreateDirectory("claude-first-trace-negative");
        var databasePath = Path.Combine(directory, "monitor.db");
        var origin = ReserveLoopbackOrigin();
        var time = new DoctorTestTimeProvider(Now);
        try
        {
            PrepareBaselineDatabase(databasePath, time);
            var setupPlatform = CreatePlatform(databasePath, endpoint: origin);
            AssertChangedSetupHandoff(setupPlatform, origin);

            var beginPlatform = CreatePlatform(databasePath, endpoint: origin);
            var beginOrchestrator = CreateOrchestrator(beginPlatform, time);
            using var begin = RunFirstTrace(
                beginOrchestrator,
                [
                    "begin", "--adapter", "claude-code", "--database", databasePath,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            var verificationId = begin.RootElement.GetProperty("verification_id").GetString();
            Assert.NotNull(verificationId);

            await using var monitor = await RunningMonitor.StartAsync(databasePath, origin, time);
            await monitor.PostSessionStartAsync(NativeSessionMarker);
            await monitor.PostOtlpAsync(Payload(TraceId, sessionId: null));
            await monitor.DrainAsync();
            new ClaudeDoctorCandidateObserver(databasePath, time).RunOnce();

            var completionPlatform = CreatePlatform(databasePath, endpoint: "http://127.0.0.1:4320");
            using var complete = RunFirstTrace(
                CreateOrchestrator(completionPlatform, time),
                [
                    "complete", "--database", databasePath, "--verification-id", verificationId!,
                    "--expected-revision", "1", "--json",
                ],
                expectedExitCode: 3);
            Assert.Equal("first_trace_not_ready", complete.RootElement.GetProperty("code").GetString());
            Assert.Equal("evaluation_completed", complete.RootElement.GetProperty("doctor").GetProperty("code").GetString());
            var states = complete.RootElement.GetProperty("doctor").GetProperty("evaluation").GetProperty("states").EnumerateArray();
            Assert.Contains(states, state => state.GetProperty("state_code").GetString() == "session_unbound");
            Assert.DoesNotContain(states, state => state.GetProperty("state_code").GetString() == "first_trace_ready");
            AssertNoSensitiveMarkers(complete.RootElement.GetRawText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertChangedSetupHandoff(TestSetupPlatform platform, string origin)
    {
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(
            platform,
            new FixedClaudeHookCommandProvider(
                new ClaudeHookCommand(
                    "dotnet",
                    ["run", "--no-build", "--project", "synthetic-monitor.csproj", "--"],
                    ClaudeHookCommandMode.Repository)));

        using var planOutput = new StringWriter();
        using var planError = new StringWriter();
        var planExitCode = CliApplication.Run(
                [
                    "setup", "plan", "--adapter", "claude-code", "--target", "cli",
                    "--endpoint", origin,
                ],
                planOutput,
                planError,
                dispatch);
        Assert.True(planExitCode == 0, $"exit={planExitCode}; stdout={planOutput}; stderr={planError}");
        Assert.Equal(string.Empty, planError.ToString());
        using var plan = JsonDocument.Parse(planOutput.ToString());
        var changeSetId = plan.RootElement.GetProperty("change_set_id").GetString();
        Assert.NotNull(changeSetId);

        using var applyOutput = new StringWriter();
        using var applyError = new StringWriter();
        var applyExitCode = CliApplication.Run(
                ["setup", "apply", "--change-set", changeSetId!],
                applyOutput,
                applyError,
                dispatch);
        Assert.True(applyExitCode == 0, $"exit={applyExitCode}; stdout={applyOutput}; stderr={applyError}");
        Assert.Equal(string.Empty, applyError.ToString());
        using var apply = JsonDocument.Parse(applyOutput.ToString());
            Assert.Equal(
                [SetupCodes.RestartClaudeProcess, SetupCodes.RunFirstTraceDoctor],
                apply.RootElement.GetProperty("next_actions").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    private static JsonDocument RunFirstTrace(
        FirstTraceOrchestrator orchestrator,
        string[] args,
        int expectedExitCode)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = FirstTraceCli.Run(args, output, error, orchestrator);
        Assert.Equal(expectedExitCode, exitCode);
        if (expectedExitCode == 0)
        {
            Assert.Equal(string.Empty, error.ToString());
        }
        else
        {
            Assert.NotEqual(string.Empty, error.ToString());
        }

        return JsonDocument.Parse(output.ToString());
    }

    private static FirstTraceOrchestrator CreateOrchestrator(TestSetupPlatform platform, TimeProvider time) =>
        new(
            [new ClaudeCodeFirstTraceAdapter(
                platform,
                platform.HttpProbe,
                platform.Clock,
                platform.InvocationDirectory)],
            time);

    private static TestSetupPlatform CreatePlatform(string databasePath, string endpoint)
    {
            var platform = new TestSetupPlatform(Now);
        platform.SeedFile(databasePath, []);
        platform.SeedProcessEnvironment("CLAUDE_CODE_ENABLE_TELEMETRY", "1");
        platform.SeedProcessEnvironment("CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "1");
        platform.SeedProcessEnvironment("OTEL_TRACES_EXPORTER", "otlp");
        platform.SeedProcessEnvironment("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "http/protobuf");
        platform.SeedProcessEnvironment("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", endpoint + "/v1/traces");
        return platform;
    }

    private static void PrepareBaselineDatabase(string databasePath, TimeProvider time)
    {
        new RawTelemetryStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        new SqliteSourceCompatibilityStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        new SqliteMonitorRuntimeStateStore(databasePath, time, RawTelemetryStoreConnectionOptions.MonitorWriter).Upsert(false);

        var payload = Payload(BaselineTraceId, sessionId: "SYNTHETIC_BASELINE_SESSION");
        var decoded = OtlpTracePayloadDecoder.DecodeTracePayload("application/json", Encoding.UTF8.GetBytes(payload));
        var recognizedPayload = OtlpJsonRecognizedPayloadBuilder.Build(decoded.PayloadJson);
        var receivedAt = Now.AddSeconds(-1);
        var record = RawOtlpIngestor.CreateRecordFromPayloadJson(decoded.PayloadJson, recognizedPayload, receivedAt);
        var observation = SourceObservationBatchDraft.Create(
            Guid.CreateVersion7().ToString("D"),
            "claude-code",
            "2.1.207",
            "claude-code-otel",
            "claude-test-adapter-v1",
            decoded.StructuralInventory,
            SourceCompatibilityEvaluator.Assess(
                "claude-code",
                "2.1.207",
                decoded.StructuralInventory,
                observedRecognizedCount: 1,
                VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.Available,
            receivedAt);
        new SqliteIngestionCommitStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Commit(ValidatedIngestionBatch.Create(record, observation));
    }

    private static string Payload(string traceId, string? sessionId)
    {
        var spans = new JsonArray
        {
            Span(InteractionSpanId, "claude_code.interaction", traceId, sessionId, parentSpanId: null),
            Span(SpanId, "claude_code.llm_request", traceId, sessionId, InteractionSpanId),
            Span(ToolSpanId, "claude_code.tool", traceId, sessionId, InteractionSpanId),
        };
        return new JsonObject
        {
            ["resourceSpans"] = new JsonArray
            {
                new JsonObject
                {
                    ["resource"] = new JsonObject
                    {
                        ["attributes"] = new JsonArray
                        {
                            Attribute("service.name", "claude-code"),
                            Attribute("service.version", "2.1.207"),
                        },
                    },
                    ["scopeSpans"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["scope"] = new JsonObject { ["name"] = "com.anthropic.claude_code" },
                            ["spans"] = spans,
                        },
                    },
                },
            },
        }.ToJsonString();
    }

    private static JsonObject Span(
        string spanId,
        string name,
        string traceId,
        string? sessionId,
        string? parentSpanId)
    {
        var attributes = new JsonArray
        {
            Attribute("user_prompt", PromptMarker),
            Attribute("path", PathMarker),
        };
        if (sessionId is not null)
        {
            attributes.Add(Attribute("session.id", sessionId));
        }
        if (name == "claude_code.llm_request")
        {
            attributes.Add(Attribute("gen_ai.request.model", "claude-synthetic-model"));
            attributes.Add(Attribute("input_tokens", 11));
            attributes.Add(Attribute("output_tokens", 7));
        }
        if (name == "claude_code.tool")
        {
            attributes.Add(Attribute("tool_name", "synthetic_tool"));
        }

        var span = new JsonObject
        {
            ["traceId"] = traceId,
            ["spanId"] = spanId,
            ["name"] = name,
            ["startTimeUnixNano"] = "1784336523000000000",
            ["endTimeUnixNano"] = "1784336524000000000",
            ["status"] = new JsonObject { ["code"] = 1 },
            ["attributes"] = attributes,
        };
        if (parentSpanId is not null)
        {
            span["parentSpanId"] = parentSpanId;
        }
        return span;
    }

    private static JsonObject Attribute(string key, string value) => new()
    {
        ["key"] = key,
        ["value"] = new JsonObject { ["stringValue"] = value },
    };

    private static JsonObject Attribute(string key, int value) => new()
    {
        ["key"] = key,
        ["value"] = new JsonObject { ["intValue"] = value.ToString() },
    };

    private static DoctorEvidenceKind ParseEvidenceKind(string value) => value switch
    {
        "ingest" => DoctorEvidenceKind.Ingest,
        "raw_persistence" => DoctorEvidenceKind.RawPersistence,
        "projection" => DoctorEvidenceKind.Projection,
        "exact_session_binding" => DoctorEvidenceKind.ExactSessionBinding,
        "completeness_content" => DoctorEvidenceKind.CompletenessContent,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    private static void AssertNoSensitiveMarkers(string envelopeJson)
    {
        Assert.DoesNotContain(NativeSessionMarker, envelopeJson, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptMarker, envelopeJson, StringComparison.Ordinal);
        Assert.DoesNotContain(PathMarker, envelopeJson, StringComparison.Ordinal);
    }

    private static string CreateDirectory(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ReserveLoopbackOrigin()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    private sealed class RunningMonitor : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly IngestionWriterWorker ingestionWorker;
        private readonly SessionEventWriterWorker sessionWorker;
        private readonly ProjectionWorker projectionWorker;
        private readonly SqliteSessionOtelEnricher enricher;

        private RunningMonitor(
            WebApplication app,
            HttpClient client,
            IngestionWriterWorker ingestionWorker,
            SessionEventWriterWorker sessionWorker,
            ProjectionWorker projectionWorker,
            SqliteSessionOtelEnricher enricher)
        {
            this.app = app;
            Client = client;
            this.ingestionWorker = ingestionWorker;
            this.sessionWorker = sessionWorker;
            this.projectionWorker = projectionWorker;
            this.enricher = enricher;
        }

        public HttpClient Client { get; }

        public static async Task<RunningMonitor> StartAsync(
            string databasePath,
            string origin,
            TimeProvider time)
        {
            var queue = new IngestionQueue(time);
            var sessionQueue = new SessionEventQueue();
            var compatibility = new SqliteSourceCompatibilityStore(
                databasePath,
                RawTelemetryStoreConnectionOptions.MonitorWriter);
            var sessionStore = new SqliteSessionStore(databasePath, time);
            var health = new MonitorHealthState();
            var app = MonitorHost.Build(
                new MonitorOptions(databasePath, origin, false, MonitorOptions.DefaultMaxRequestBodyBytes),
                new MonitorHostTestOptions
                {
                    Queue = queue,
                    SourceCompatibilityStore = compatibility,
                    SourceMetadataProvider = new FixedOtlpTraceSourceMetadataProvider(
                        OtlpTraceSourceMetadata.Create(
                            "claude-code",
                            "2.1.207",
                            "claude-code-otel",
                            "claude-test-adapter-v1",
                            SourceCaptureContentState.Available)),
                    Health = health,
                    StartWriter = false,
                    StartProjectionWorker = false,
                    SessionStore = sessionStore,
                    SessionEventQueue = sessionQueue,
                    StartSessionWriter = false,
                    StartSessionOtelEnrichment = false,
                    TimeProvider = time,
                    UseUserSecrets = false,
                });
            try
            {
                await app.StartAsync();
                var ingestionWorker = new IngestionWriterWorker(
                    queue,
                    new SqliteIngestionCommitStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter),
                    compatibility,
                    health);
                await ingestionWorker.StartAsync(CancellationToken.None);
                var normalizer = new SessionEventNormalizer(sessionStore, time);
                var sessionWorker = new SessionEventWriterWorker(sessionQueue, normalizer);
                await sessionWorker.StartAsync(CancellationToken.None);
                var projectionWorker = new ProjectionWorker(
                    app.Services.GetRequiredService<IMonitorProjectionStore>(),
                    health,
                    compatibility,
                    time);
                var enricher = new SqliteSessionOtelEnricher(databasePath, sessionStore, time);
                return new(
                    app,
                    new HttpClient { BaseAddress = new Uri(origin) },
                    ingestionWorker,
                    sessionWorker,
                    projectionWorker,
                    enricher);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public async Task PostSessionStartAsync(string nativeSessionId)
        {
            var envelope = new JsonObject
            {
                ["schema_version"] = 1,
                ["source_adapter"] = "claude-code-hook",
                ["source_surface"] = "claude-code",
                ["native_session_id"] = nativeSessionId,
                ["source_application_version"] = "2.1.207",
                ["adapter_version"] = "claude-hook-v1",
                ["normalization_version"] = "session-normalization-v1",
                ["events"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["source_event_id"] = "synthetic-session-start-event",
                        ["type"] = "SessionStart",
                        ["occurred_at"] = Now.ToString("O"),
                        ["payload"] = new JsonObject
                        {
                            ["session_id"] = nativeSessionId,
                            ["transcript_path"] = "SYNTHETIC_TRANSCRIPT_PATH",
                            ["cwd"] = "SYNTHETIC_WORKING_DIRECTORY",
                            ["hook_event_name"] = "SessionStart",
                            ["source"] = "startup",
                        },
                    },
                },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v1/events")
            {
                Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-CAO-Session-Event-Version", "1");
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public async Task PostOtlpAsync(string payload)
        {
            using var response = await Client.PostAsync(
                "/v1/traces",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task DrainAsync()
        {
            await projectionWorker.RunProjectionPassAsync();
            enricher.ProcessNextBatch();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await sessionWorker.StopAsync(CancellationToken.None);
            await ingestionWorker.StopAsync(CancellationToken.None);
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class TestSetupPlatform : ISetupPlatform
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SetupPathMetadata> paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> processEnvironment = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> userEnvironment = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> locks = new(StringComparer.OrdinalIgnoreCase);
        private long identifierSequence = 1;

        public TestSetupPlatform(DateTimeOffset utcNow)
        {
            InvocationDirectory = "C:\\Users\\first-trace";
            LocalApplicationData = "C:\\first-trace-local-app-data";
            PathStyle = SetupPathStyle.Windows;
            Clock = new TestClock(utcNow);
            Identifiers = new TestIdentifiers(this);
            FileSystem = new TestFileSystem(this);
            UserEnvironment = new TestUserEnvironment(this);
            ProcessEnvironment = new TestProcessEnvironment(this);
            Execution = new TestExecution();
            OperatingSystem = new TestOperatingSystem();
            ProcessRunner = new TestProcessRunner();
            ManagedSettings = new TestManagedSettings();
            HttpProbe = new TestHttpProbe();
            SeedDirectoryChain(InvocationDirectory);
            SeedDirectoryChain(OperatingSystem.UserProfile);
            SeedDirectoryChain(Path.Combine(OperatingSystem.UserProfile, ".claude"));
            SeedFile(
                Path.Combine(OperatingSystem.UserProfile, ".claude", "settings.json"),
                Encoding.UTF8.GetBytes("{}\n"));
        }

        public string InvocationDirectory { get; }
        public SetupPathStyle PathStyle { get; }
        public string LocalApplicationData { get; }
        public ISetupFileSystem FileSystem { get; }
        public ISetupUserEnvironment UserEnvironment { get; }
        public ISetupProcessEnvironment ProcessEnvironment { get; }
        public ISetupClock Clock { get; }
        public ISetupIdentifierGenerator Identifiers { get; }
        public ISetupExecution Execution { get; }
        public ISetupOperatingSystem OperatingSystem { get; }
        public ISetupProcessRunner ProcessRunner { get; }
        public ISetupManagedSettingsSource ManagedSettings { get; }
        public ISetupHttpProbe HttpProbe { get; }

        public void SeedFile(string path, byte[] bytes)
        {
            files[path] = bytes.ToArray();
            paths[path] = new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal);
        }

        public void SeedProcessEnvironment(string name, string value) => processEnvironment[name] = value;

        private void SeedDirectoryChain(string directory)
        {
            var current = Path.GetPathRoot(directory)!;
            paths[current] = new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory);
            foreach (var segment in directory[current.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                paths[current] = new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory);
            }
        }

        private sealed class TestFileSystem(TestSetupPlatform platform) : ISetupFileSystem
        {
            public void CreateDirectory(string path) => platform.paths[path] = new(true, SetupPathKind.Directory, FileAttributes.Directory);
            public bool FileExists(string path) => platform.files.ContainsKey(path);
            public byte[] ReadAllBytes(string path) => platform.files[path].ToArray();
            public SetupBoundedFileRead ReadAtMostBytes(string path, int maximumBytes)
            {
                var bytes = platform.files[path];
                var length = Math.Min(bytes.Length, maximumBytes + 1);
                return new(bytes[..length], bytes.Length <= maximumBytes);
            }
            public bool HasDirectories(string path) => platform.paths.Keys.Any(candidate =>
                candidate.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                && platform.paths[candidate].Kind == SetupPathKind.Directory);
            public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => platform.SeedFile(path, bytes.ToArray());
            public void WriteNewAllBytes(string path, ReadOnlySpan<byte> bytes)
            {
                if (platform.paths.ContainsKey(path)) throw new IOException("Destination exists.");
                platform.SeedFile(path, bytes.ToArray());
            }
            public bool TryWriteNewAllBytesAndFlush(string path, ReadOnlySpan<byte> bytes)
            {
                if (platform.paths.ContainsKey(path)) return false;
                platform.SeedFile(path, bytes.ToArray());
                return true;
            }
            public void FlushFile(string path) { }
            public void ReplaceFile(string sourcePath, string destinationPath)
            {
                platform.SeedFile(destinationPath, platform.files[sourcePath]);
                platform.DeletePath(sourcePath);
            }
            public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
            {
                if (!overwrite && platform.paths.ContainsKey(destinationPath)) throw new IOException("Destination exists.");
                platform.SeedFile(destinationPath, platform.files[sourcePath]);
                platform.DeletePath(sourcePath);
            }
            public void DeleteFile(string path) => platform.DeletePath(path);
            public SetupPathMetadata GetPathMetadata(string path) => platform.paths.TryGetValue(path, out var metadata) ? metadata : SetupPathMetadata.Missing;
            public ISetupExclusiveFileLock? TryAcquireExclusiveFileLock(string path) =>
                platform.locks.Add(path) ? new TestLock(platform, path) : null;
        }

        private void DeletePath(string path)
        {
            files.Remove(path);
            paths.Remove(path);
        }

        private sealed class TestLock(TestSetupPlatform platform, string path) : ISetupExclusiveFileLock
        {
            public void Dispose() => platform.locks.Remove(path);
        }

        private sealed class TestUserEnvironment(TestSetupPlatform platform) : ISetupUserEnvironment
        {
            public string? Get(string name) => platform.userEnvironment.GetValueOrDefault(name);
            public void Set(string name, string? value) => platform.userEnvironment[name] = value;
            public void NotifyChange() { }
        }

        private sealed class TestProcessEnvironment(TestSetupPlatform platform) : ISetupProcessEnvironment
        {
            public string? Get(string name) => platform.processEnvironment.GetValueOrDefault(name);
        }

        private sealed class TestClock(DateTimeOffset utcNow) : ISetupClock
        {
            public DateTimeOffset UtcNow { get; } = utcNow;
        }

        private sealed class TestIdentifiers(TestSetupPlatform platform) : ISetupIdentifierGenerator
        {
            public Guid CreateUuidV7() => Guid.Parse($"00000000-0000-7000-8000-{platform.identifierSequence++:D12}");
        }

        private sealed class TestExecution : ISetupExecution
        {
            public void Checkpoint(string operation) { }
        }

        private sealed class TestOperatingSystem : ISetupOperatingSystem
        {
            public SetupPlanningOs Current => SetupPlanningOs.Windows;
            public string ApplicationData => "C:\\Users\\first-trace\\AppData\\Roaming";
            public string UserProfile => "C:\\Users\\first-trace";
        }

        private sealed class TestProcessRunner : ISetupProcessRunner
        {
            public SetupProcessObservation Run(string fileName, IReadOnlyList<string> arguments) =>
                new(SetupProcessOutcome.Completed, 0, "2.1.207 (Claude Code)");
        }

        private sealed class TestManagedSettings : ISetupManagedSettingsSource
        {
            public SetupManagedObservation Read(SetupManagedLocation location) => SetupManagedObservation.Absent;
        }

        private sealed class TestHttpProbe : ISetupHttpProbe
        {
            private static readonly byte[] ReadyBody = Encoding.UTF8.GetBytes(
                "{\"status\":\"ready\",\"checks\":{\"loopback_bound\":true,\"db_open\":true,\"migration_complete\":true,\"writer_running\":true,\"projection_worker_running\":true,\"ingestion_accepting\":true,\"projection_lag_seconds\":0,\"projection_backlog\":0,\"span_projection_lag_seconds\":0,\"span_projection_backlog\":0,\"projection_failure_count\":0},\"degraded_reasons\":[]}");

            public SetupHttpProbeObservation Get(string origin, string path, int totalBudgetMilliseconds, int maxBodyBytes) =>
                path == "/health/live"
                    ? new(SetupHttpProbeOutcome.Response, 200, 18, Encoding.UTF8.GetBytes("{\"status\":\"live\"}"), true)
                    : new(SetupHttpProbeOutcome.Response, 200, ReadyBody.Length, ReadyBody.ToArray(), true);
        }
    }
}
