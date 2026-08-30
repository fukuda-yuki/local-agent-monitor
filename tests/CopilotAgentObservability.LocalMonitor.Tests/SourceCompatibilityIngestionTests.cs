using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class SourceCompatibilityIngestionTests
{
    [Fact]
    public async Task Projection_CrossRecordSourceConflictClearsTraceAndContributingIngestions()
    {
        const string traceId = "11111111111111111111111111111111";
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });
        var rawStore = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        var projectionStore = new RawTelemetryStoreProjectionStore(rawStore);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var sourceStore = new SqliteSourceCompatibilityStore(
            temp.DatabasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        var worker = new ProjectionWorker(projectionStore, health, sourceStore);

        var first = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent(SourcePayload(traceId, "1111111111111111", "github-copilot")));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await worker.RunProjectionPassAsync();

        var second = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent(SourcePayload(traceId, "2222222222222222", "copilot-chat")));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        await worker.RunProjectionPassAsync();
        var duplicate = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent(SourcePayload(traceId, "1111111111111111", "github-copilot")));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        await worker.RunProjectionPassAsync();

        Assert.Equal(
            new TraceSourceResolutionRow(traceId, TraceSourceResolutionState.Conflicting, null),
            sourceStore.GetTraceSourceResolution(traceId));
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using (var trace = connection.CreateCommand())
        {
            trace.CommandText = "SELECT client_kind FROM monitor_traces WHERE trace_id=$trace_id;";
            trace.Parameters.AddWithValue("$trace_id", traceId);
            Assert.Equal(DBNull.Value, trace.ExecuteScalar());
        }
        using (var ingestions = connection.CreateCommand())
        {
            ingestions.CommandText = "SELECT COUNT(*) FROM monitor_ingestions WHERE client_kind IS NULL;";
            Assert.Equal(3L, ingestions.ExecuteScalar());
        }
    }

    [Fact]
    public async Task Projection_SourceConflictClearsOnlyExactOtelSessionSurfacesAndPreservesIdentity()
    {
        const string traceId = "11111111111111111111111111111111";
        const string nativeSessionId = "synthetic-conversation-1";
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            StartSessionOtelEnrichment = false,
            StartLocalRepositoryCatalogHostedService = false,
            UseUserSecrets = false,
            TimeProvider = new FixedTimeProvider(now),
        });
        var sessionStore = host.Services.GetRequiredService<ISessionStore>();
        var sessionId = Guid.CreateVersion7();
        sessionStore.Write(new(new(
            new ObservedSession(
                sessionId,
                ObservedSessionStatus.Unknown,
                SessionCompleteness.Unbound,
                Repository: null,
                Workspace: null,
                StartedAt: null,
                EndedAt: null,
                LastSeenAt: now,
                SessionRawRetentionState.NotCaptured,
                CreatedAt: now,
                UpdatedAt: now),
            [new SessionNativeId(
                sessionId,
                SessionSourceSurface.CopilotCli,
                nativeSessionId,
                SessionBindingKind.Native,
                now)],
            [],
            []), []));
        var rawStore = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        var projectionStore = new RawTelemetryStoreProjectionStore(rawStore);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var sourceStore = new SqliteSourceCompatibilityStore(
            temp.DatabasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        var worker = new ProjectionWorker(projectionStore, health, sourceStore);
        var enricher = new SqliteSessionOtelEnricher(
            temp.DatabasePath,
            sessionStore,
            temp.RetentionContext,
            new FixedTimeProvider(now));

        var first = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent(SourcePayloadWithConversation(
                traceId,
                "1111111111111111",
                "github-copilot",
                nativeSessionId)));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await worker.RunProjectionPassAsync();
        Assert.Equal(1, enricher.ProcessNextBatch(100));

        var beforeConflict = Assert.IsType<SessionDetail>(
            sessionStore.GetDetail(sessionId));
        var beforeSession = beforeConflict.Session;
        var beforeNative = Assert.Single(beforeConflict.NativeIds);
        var beforeEvent = Assert.Single(
            beforeConflict.Events,
            item => item.SourceAdapter == "otel-exact");
        var beforeRun = Assert.Single(
            beforeConflict.Runs,
            item => item.RunId == beforeEvent.RunId);
        Assert.Equal(SessionSourceSurface.CopilotCli, beforeEvent.SourceSurface);
        Assert.Equal(SessionSourceSurface.CopilotCli, beforeRun.SourceSurface);
        Assert.Equal(SessionMatchKind.ConversationId, beforeEvent.MatchKind);

        var second = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent(SourcePayloadWithConversation(
                traceId,
                "2222222222222222",
                "copilot-chat",
                nativeSessionId)));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(sourceStore.ReconcileProjectedTraceSourceAttribution());

        var afterReconciliation = Assert.IsType<SessionDetail>(
            sessionStore.GetDetail(sessionId));
        Assert.Equal(beforeSession, afterReconciliation.Session);
        Assert.Equal(beforeNative, Assert.Single(afterReconciliation.NativeIds));
        var reconciledEvent = Assert.Single(
            afterReconciliation.Events,
            item => item.EventId == beforeEvent.EventId);
        var reconciledRun = Assert.Single(
            afterReconciliation.Runs,
            item => item.RunId == beforeRun.RunId);
        Assert.Null(reconciledEvent.SourceSurface);
        Assert.Null(reconciledRun.SourceSurface);
        Assert.Equal(
            beforeEvent with { SourceSurface = null },
            reconciledEvent);
        Assert.Equal(
            beforeRun with { SourceSurface = null },
            reconciledRun);

        await worker.RunProjectionPassAsync();
        Assert.Equal(1, enricher.ProcessNextBatch(100));

        var completed = Assert.IsType<SessionDetail>(
            sessionStore.GetDetail(sessionId));
        Assert.Equal(beforeNative, Assert.Single(completed.NativeIds));
        Assert.All(
            completed.Events.Where(item => item.SourceAdapter == "otel-exact"),
            item => Assert.Null(item.SourceSurface));
        Assert.All(
            completed.Runs.Where(item => item.TraceId == traceId),
            item => Assert.Null(item.SourceSurface));
        Assert.Equal(
            2,
            completed.Events.Count(item => item.SourceAdapter == "otel-exact"));
        Assert.Contains(
            completed.Events,
            item => item.EventId == beforeEvent.EventId
                && item.MatchKind == beforeEvent.MatchKind
                && item.SourceEventId == beforeEvent.SourceEventId);
    }

    [Fact]
    public async Task PostTraces_CurrentMarkerlessCliPersistsUnrecognisedProjectsNullAndSelectsNoManifest()
    {
        const string traceId = "11111111111111111111111111111111";
        const string payload =
            """
            {"resourceSpans":[{
              "resource":{"attributes":[
                {"key":"service.name","value":{"stringValue":""}},
                {"key":"service.version","value":{"stringValue":"1.0.75"}}
              ]},
              "scopeSpans":[{"spans":[{
                "traceId":"11111111111111111111111111111111",
                "spanId":"1111111111111111",
                "name":"chat gpt-4o"
              }]}]
            }]}
            """;
        var inventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(payload)).StructuralInventory;
        var registry = VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("github-copilot-cli", "1.0.75", inventory.SchemaFingerprint)],
            [],
            []);
        var metadata = OtlpTraceSourceMetadata.Create(
            "github-copilot-cli",
            "1.0.75",
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

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sourceStore = new SqliteSourceCompatibilityStore(
            temp.DatabasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        Assert.Equal(
            new TraceSourceResolutionRow(traceId, TraceSourceResolutionState.Unrecognised, null),
            sourceStore.GetTraceSourceResolution(traceId));
        Assert.Equal(
            new TraceSourceVersionResolutionRow(traceId, TraceSourceVersionResolutionState.Resolved, "1.0.75"),
            sourceStore.GetTraceSourceVersionResolution(traceId));
        var sourceResolution = Assert.Single(OtlpTraceSourceResolver.Resolve(payload));
        Assert.Null(SourceCapabilityManifestLoader.LoadForTraceSourceResolution(sourceResolution));

        var rawStore = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        var projectionStore = new RawTelemetryStoreProjectionStore(rawStore);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var worker = new ProjectionWorker(projectionStore, health, sourceStore);
        await worker.RunProjectionPassAsync();

        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using (var trace = connection.CreateCommand())
        {
            trace.CommandText = "SELECT client_kind FROM monitor_traces WHERE trace_id=$trace_id;";
            trace.Parameters.AddWithValue("$trace_id", traceId);
            Assert.Equal(DBNull.Value, trace.ExecuteScalar());
        }
        using (var ingestion = connection.CreateCommand())
        {
            ingestion.CommandText = "SELECT client_kind FROM monitor_ingestions WHERE trace_id=$trace_id;";
            ingestion.Parameters.AddWithValue("$trace_id", traceId);
            Assert.Equal(DBNull.Value, ingestion.ExecuteScalar());
        }
    }

    [Fact]
    public async Task PostTraces_ResolvesResourceScopedSourceVersionIndependentlyForEachTrace()
    {
        const string firstTraceId = "11111111111111111111111111111111";
        const string secondTraceId = "22222222222222222222222222222222";
        const string payload =
            """
            {"resourceSpans":[{
              "resource":{"attributes":[{"key":"service.version","value":{"stringValue":"1.0.74"}}]},
              "scopeSpans":[{"spans":[{"traceId":"11111111111111111111111111111111","spanId":"1111111111111111"}]}]
            },{
              "resource":{"attributes":[{"key":"service.version","value":{"stringValue":"1.0.75"}}]},
              "scopeSpans":[{"spans":[{"traceId":"22222222222222222222222222222222","spanId":"2222222222222222"}]}]
            }]}
            """;
        var inventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(payload)).StructuralInventory;
        var registry = VerifiedSourceFingerprintRegistry.Create(
            [
                VerifiedSourceFingerprintEvidence.Create("github-copilot-cli", "1.0.74", inventory.SchemaFingerprint),
                VerifiedSourceFingerprintEvidence.Create("github-copilot-cli", "1.0.75", inventory.SchemaFingerprint),
            ],
            [],
            []);
        var metadata = OtlpTraceSourceMetadata.Create(
            "github-copilot-cli",
            "batch-metadata-version",
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

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        Assert.Equal(
            new TraceSourceVersionResolutionRow(firstTraceId, TraceSourceVersionResolutionState.Resolved, "1.0.74"),
            store.GetTraceSourceVersionResolution(firstTraceId));
        Assert.Equal(
            new TraceSourceVersionResolutionRow(secondTraceId, TraceSourceVersionResolutionState.Resolved, "1.0.75"),
            store.GetTraceSourceVersionResolution(secondTraceId));
        Assert.Equal(
            "batch-metadata-version",
            Assert.Single(store.List(after: null, limit: 200)).SourceApplicationVersion);
    }

    [Fact]
    public async Task PostTraces_SameTraceWithMissingAndRecognisedResourceVersionsResolvesMissing()
    {
        const string traceId = "11111111111111111111111111111111";
        const string payload =
            """
            {"resourceSpans":[{
              "resource":{"attributes":[]},
              "scopeSpans":[{"spans":[{"traceId":"11111111111111111111111111111111","spanId":"1111111111111111"}]}]
            },{
              "resource":{"attributes":[{"key":"service.version","value":{"stringValue":"1.0.74"}}]},
              "scopeSpans":[{"spans":[{"traceId":"11111111111111111111111111111111","spanId":"2222222222222222"}]}]
            }]}
            """;
        var inventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(payload)).StructuralInventory;
        var registry = VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("github-copilot-cli", "1.0.74", inventory.SchemaFingerprint)],
            [],
            []);
        var metadata = OtlpTraceSourceMetadata.Create(
            "github-copilot-cli",
            "1.0.74",
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

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new TraceSourceVersionResolutionRow(traceId, TraceSourceVersionResolutionState.Missing, null),
            new SqliteSourceCompatibilityStore(temp.DatabasePath).GetTraceSourceVersionResolution(traceId));
    }

    [Fact]
    public async Task PostTraces_SameTraceWithMissingAndUnrecognisedResourceVersionsResolvesUnrecognised()
    {
        const string traceId = "11111111111111111111111111111111";
        const string payload =
            """
            {"resourceSpans":[{
              "resource":{"attributes":[]},
              "scopeSpans":[{"spans":[{"traceId":"11111111111111111111111111111111","spanId":"1111111111111111"}]}]
            },{
              "resource":{"attributes":[{"key":"service.version","value":{"stringValue":"9.9.9"}}]},
              "scopeSpans":[{"spans":[{"traceId":"11111111111111111111111111111111","spanId":"2222222222222222"}]}]
            }]}
            """;
        var inventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(payload)).StructuralInventory;
        var registry = VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("github-copilot-cli", "1.0.74", inventory.SchemaFingerprint)],
            [],
            []);
        var metadata = OtlpTraceSourceMetadata.Create(
            "github-copilot-cli",
            "9.9.9",
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

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new TraceSourceVersionResolutionRow(traceId, TraceSourceVersionResolutionState.Unrecognised, "9.9.9"),
            new SqliteSourceCompatibilityStore(temp.DatabasePath).GetTraceSourceVersionResolution(traceId));
    }

    [Fact]
    public async Task PostTraces_PersistsMissingConflictingAndUnrecognisedAsDistinctFailClosedStates()
    {
        const string missingTraceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string conflictingTraceId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string unrecognisedTraceId = "cccccccccccccccccccccccccccccccc";
        const string invalidTraceId = "dddddddddddddddddddddddddddddddd";
        var invalidVersion = new string('v', 257);
        var payload =
            $$$"""
            {"resourceSpans":[{
              "resource":{"attributes":[]},
              "scopeSpans":[{"spans":[{"traceId":"{{{missingTraceId}}}","spanId":"1111111111111111"}]}]
            },{
              "resource":{"attributes":[
                {"key":"service.version","value":{"stringValue":"1.0.74"}},
                {"key":"service.version","value":{"stringValue":"1.0.75"}}
              ]},
              "scopeSpans":[{"spans":[{"traceId":"{{{conflictingTraceId}}}","spanId":"2222222222222222"}]}]
            },{
              "resource":{"attributes":[{"key":"service.version","value":{"stringValue":"9.9.9"}}]},
              "scopeSpans":[{"spans":[{"traceId":"{{{unrecognisedTraceId}}}","spanId":"4444444444444444"}]}]
            },{
              "resource":{"attributes":[{"key":"service.version","value":{"stringValue":"{{{invalidVersion}}}"}}]},
              "scopeSpans":[{"spans":[{"traceId":"{{{invalidTraceId}}}","spanId":"5555555555555555"}]}]
            }]}
            """;
        var inventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(payload)).StructuralInventory;
        var registry = VerifiedSourceFingerprintRegistry.Create(
            [
                VerifiedSourceFingerprintEvidence.Create("github-copilot-cli", "1.0.74", inventory.SchemaFingerprint),
                VerifiedSourceFingerprintEvidence.Create("github-copilot-cli", "1.0.75", inventory.SchemaFingerprint),
            ],
            [],
            []);
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

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var store = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        Assert.Equal(
            new TraceSourceVersionResolutionRow(missingTraceId, TraceSourceVersionResolutionState.Missing, null),
            store.GetTraceSourceVersionResolution(missingTraceId));
        Assert.Equal(
            new TraceSourceVersionResolutionRow(conflictingTraceId, TraceSourceVersionResolutionState.Conflicting, null),
            store.GetTraceSourceVersionResolution(conflictingTraceId));
        Assert.Equal(
            new TraceSourceVersionResolutionRow(unrecognisedTraceId, TraceSourceVersionResolutionState.Unrecognised, "9.9.9"),
            store.GetTraceSourceVersionResolution(unrecognisedTraceId));
        Assert.Equal(
            new TraceSourceVersionResolutionRow(invalidTraceId, TraceSourceVersionResolutionState.Unrecognised, null),
            store.GetTraceSourceVersionResolution(invalidTraceId));
    }

    [Fact]
    public async Task TraceSourceAuthorities_DoNotChangeFrozenPublicResponseBytes()
    {
        const string traceId = "11111111111111111111111111111111";
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            StartSessionOtelEnrichment = false,
            UseUserSecrets = false,
            TimeProvider = time,
        });
        var response = await host.Client.PostAsync("/v1/traces", JsonContent(EquivalentJson()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawStore = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        var projectionStore = new RawTelemetryStoreProjectionStore(rawStore);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var worker = new ProjectionWorker(
            projectionStore,
            health,
            new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter));
        await worker.RunProjectionPassAsync();
        string[] paths =
        [
            "/api/monitor/ingestions",
            "/api/monitor/source-diagnostics",
            "/api/monitor/traces",
            $"/api/monitor/traces/{traceId}/spans",
            $"/api/monitor/traces/{traceId}/agent-graph",
            "/api/monitor/summary",
            "/api/monitor/overview",
            "/api/monitor/trace-list",
            "/api/session-workspace/sessions",
            "/api/session-workspace/status",
            "/health/ready",
        ];
        var before = await CaptureResponses(host.Client, paths);
        var beforeSse = await CaptureSseConnectBytes(host.Client);

        long sourceObservationId;
        string retainedPayload;
        TraceSourceVersionResolutionState currentState;
        string? currentVersion;
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT version.source_observation_id,raw.payload_json,
                       version.resolution_state,version.source_application_version
                FROM source_trace_version_observations AS version
                JOIN source_schema_observations AS source
                  ON source.id=version.source_observation_id
                JOIN raw_records AS raw
                  ON raw.id=source.raw_record_id
                WHERE version.trace_id=$trace_id
                """;
            command.Parameters.AddWithValue("$trace_id", traceId);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            sourceObservationId = reader.GetInt64(0);
            retainedPayload = reader.GetString(1);
            currentState = reader.GetString(2) switch
            {
                "resolved" => TraceSourceVersionResolutionState.Resolved,
                "missing" => TraceSourceVersionResolutionState.Missing,
                "unrecognised" => TraceSourceVersionResolutionState.Unrecognised,
                "conflicting" => TraceSourceVersionResolutionState.Conflicting,
                _ => throw new InvalidOperationException("Unexpected trace source state."),
            };
            currentVersion = reader.IsDBNull(3) ? null : reader.GetString(3);
            Assert.False(reader.Read());
        }
        var reconciliationRegistry = currentState == TraceSourceVersionResolutionState.Resolved
            ? VerifiedSourceFingerprintRegistry.Create(
                [
                    VerifiedSourceFingerprintEvidence.Create(
                        "github-copilot-cli",
                        currentVersion!,
                        new string('a', 64)),
                ],
                [],
                [])
            : VerifiedSourceFingerprintRegistry.Create([], [], []);
        var reconciliation = new SourceCompatibilityReconciler(
                temp.DatabasePath,
                SourceCompatibilityReconciliationAuthority.Create(
                [
                    new(
                        "resolver-frozen-byte-1",
                        "registry-frozen-byte-1",
                        reconciliationRegistry),
                ]),
                time)
            .Reconcile(SourceCompatibilityReconciliationRequest.Create(
                "frozen-byte-no-op",
                sourceObservationId,
                traceId,
                0,
                currentState == TraceSourceVersionResolutionState.Unrecognised
                    ? SourceCompatibilityReconciliationTrigger.RegistryRevision
                    : SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-frozen-byte-1",
                "registry-frozen-byte-1",
                SkillProjectionGenerationParticipant.CurrentProjectorVersion));
        Assert.Equal(SourceCompatibilityReconciliationOutcome.NoChange, reconciliation.Outcome);

        var after = await CaptureResponses(host.Client, paths);
        var afterSse = await CaptureSseConnectBytes(host.Client);
        for (var index = 0; index < paths.Length; index++)
        {
            Assert.Equal(before[index].StatusCode, after[index].StatusCode);
            Assert.True(
                before[index].Body.AsSpan().SequenceEqual(after[index].Body),
                $"Frozen response bytes changed for {paths[index]}."
                + $"\nBefore: {Encoding.UTF8.GetString(before[index].Body)}"
                + $"\nAfter: {Encoding.UTF8.GetString(after[index].Body)}");
        }
        Assert.Equal(": connected\n\n"u8.ToArray(), beforeSse);
        Assert.Equal(beforeSse, afterSse);

        static async Task<(HttpStatusCode StatusCode, byte[] Body)[]> CaptureResponses(
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

        static async Task<byte[]> CaptureSseConnectBytes(HttpClient client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/events");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            var bytes = new byte[": connected\n\n"u8.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await stream.ReadExactlyAsync(bytes, timeout.Token);
            return bytes;
        }
    }

    [Fact]
    public async Task PostTraces_DefaultReceiverCommitsRawAndObservationBeforeAcknowledging()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(EquivalentJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rawRecordId = responseJson.RootElement.GetProperty("rawRecordId").GetInt64();
        var observationId = responseJson.RootElement.GetProperty("observationId").GetInt64();
        Assert.True(rawRecordId > 0);
        Assert.True(observationId > 0);

        var raw = Assert.Single(temp.CreateRawStore().ListRecords());
        var compatibilityStore = new SqliteSourceCompatibilityStore(temp.DatabasePath);
        var observation = Assert.Single(compatibilityStore.List(after: null, limit: 200));
        Assert.Equal(rawRecordId, raw.Id);
        Assert.Equal(observationId, observation.Id);
        Assert.Equal(rawRecordId, observation.RawRecordId);
        Assert.Equal("raw-otlp", observation.SourceSurface);
        Assert.Null(observation.SourceApplicationVersion);
        Assert.Equal("raw-otlp", observation.SourceAdapter);
        Assert.Equal("1", observation.AdapterVersion);
        Assert.Equal(SourceCompatibilityState.SchemaDriftDetected, observation.CompatibilityState);
        var lookedUpObservation = Assert.IsType<SourceCompatibilityRow>(compatibilityStore.GetByRawRecordId(rawRecordId));
        Assert.Equal(observation.Id, lookedUpObservation.Id);
        Assert.Equal(observation.CompatibilityState, lookedUpObservation.CompatibilityState);
        Assert.Null(compatibilityStore.GetByRawRecordId(rawRecordId + 1));
    }

    [Fact]
    public async Task PostTraces_EquivalentJsonAndProtobufUseOneKnownFingerprint()
    {
        var jsonInventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(EquivalentJson())).StructuralInventory;
        var protobufInventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/x-protobuf", OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest()).StructuralInventory;
        Assert.Equal(jsonInventory.SchemaFingerprint, protobufInventory.SchemaFingerprint);

        var registry = VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("raw-otlp", "fixture-v1", jsonInventory.SchemaFingerprint)],
            [],
            []);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceFingerprintRegistry = registry,
        });

        var jsonResponse = await host.Client.PostAsync("/v1/traces", JsonContent(EquivalentJson()));
        using var protobufContent = new ByteArrayContent(OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest());
        protobufContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        var protobufResponse = await host.Client.PostAsync("/v1/traces", protobufContent);

        Assert.Equal(HttpStatusCode.OK, jsonResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, protobufResponse.StatusCode);
        var observations = new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200);
        Assert.Equal(2, observations.Count);
        Assert.All(observations, item =>
        {
            Assert.Equal(jsonInventory.SchemaFingerprint, item.SchemaFingerprint);
            Assert.Equal(SourceCompatibilityState.Supported, item.CompatibilityState);
        });
    }

    [Fact]
    public async Task PostTraces_UnknownProtobufFieldDoesNotPoisonRecognizedProjection()
    {
        const string marker = "unknown-protobuf-value-marker";
        var payload = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.VscodeCopilotChatTraceRequest(),
            OtlpProtobufTestPayload.StringField(100, marker));
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        var response = await host.Client.PostAsync("/v1/traces", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawStore = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        var raw = Assert.Single(rawStore.ListRecords());
        Assert.DoesNotContain(marker, raw.PayloadJson, StringComparison.Ordinal);
        var observation = Assert.Single(
            new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.True(observation.UnknownAttributeCount > 0);

        var projectionStore = new RawTelemetryStoreProjectionStore(rawStore);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var worker = new ProjectionWorker(
            projectionStore,
            health,
            new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter));

        await worker.RunProjectionPassAsync();

        Assert.Single(projectionStore.GetSpansForTrace("11111111111111111111111111111111"));
        Assert.Equal(0, projectionStore.GetProjectionStatus().Backlog);
        Assert.Equal(0, projectionStore.GetSpanProjectionStatus().Backlog);
    }

    [Theory]
    [InlineData(ClaudeInteractionWithUserPrompt, "available")]
    [InlineData(ClaudeInteractionWithRedactedUserPrompt, "not_captured")]
    [InlineData(ClaudeInteractionWithoutGatedField, "not_captured")]
    [InlineData(ForeignSpanOnly, "unsupported")]
    public async Task PostTraces_DerivesTraceContentStateFromClaudeSpanEvidence(string payload, string expectedContentState)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rawStore = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        var projectionStore = new RawTelemetryStoreProjectionStore(rawStore);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var worker = new ProjectionWorker(
            projectionStore,
            health,
            new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter));
        await worker.RunProjectionPassAsync();

        var tracesResponse = await host.Client.GetAsync("/api/monitor/traces");
        Assert.Equal(HttpStatusCode.OK, tracesResponse.StatusCode);
        using var tracesJson = JsonDocument.Parse(await tracesResponse.Content.ReadAsStringAsync());
        var item = Assert.Single(tracesJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(expectedContentState, item.GetProperty("content_state").GetString());
    }

    private const string ClaudeInteractionWithUserPrompt = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[{
          "traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "spanId":"1111111111111111",
          "name":"claude_code.interaction",
          "startTimeUnixNano":"1000000000",
          "endTimeUnixNano":"1500000000",
          "attributes":[{"key":"user_prompt","value":{"stringValue":"synthetic-marker"}}]
        }]}]}]}
        """;

    private const string ClaudeInteractionWithRedactedUserPrompt = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[{
          "traceId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "spanId":"1111111111111111",
          "name":"claude_code.interaction",
          "startTimeUnixNano":"1000000000",
          "endTimeUnixNano":"1500000000",
          "attributes":[
            {"key":"user_prompt","value":{"stringValue":"<REDACTED>"}},
            {"key":"user_prompt_length","value":{"intValue":"16"}}
          ]
        }]}]}]}
        """;

    private const string ClaudeInteractionWithoutGatedField = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[{
          "traceId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
          "spanId":"2222222222222222",
          "name":"claude_code.interaction",
          "startTimeUnixNano":"1000000000",
          "endTimeUnixNano":"1500000000",
          "attributes":[{"key":"session.id","value":{"stringValue":"synthetic-marker"}}]
        }]}]}]}
        """;

    private const string ForeignSpanOnly = """
        {"resourceSpans":[{"scopeSpans":[{"spans":[{
          "traceId":"cccccccccccccccccccccccccccccccc",
          "spanId":"3333333333333333",
          "name":"chat gpt-4o",
          "startTimeUnixNano":"1000000000",
          "endTimeUnixNano":"1500000000",
          "attributes":[]
        }]}]}]}
        """;

    [Fact]
    public async Task PostTraces_NewFingerprintIsCommittedAsDrift()
    {
        var known = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(EquivalentJson())).StructuralInventory;
        var registry = VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("raw-otlp", "fixture-v1", known.SchemaFingerprint)],
            [],
            []);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceFingerprintRegistry = registry,
        });

        var response = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent(EquivalentJson().Replace("\"resourceSpans\"", "\"futureEnvelope\":{},\"resourceSpans\"", StringComparison.Ordinal)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var observation = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal(SourceCompatibilityState.SchemaDriftDetected, observation.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.SchemaDriftDetected], observation.ReasonCodes);
    }

    [Fact]
    public async Task PostTraces_RecognitionProfileDeficitIsPersistedAsRecognizedRecordDrop()
    {
        const string sourceVersion = "fixture-v1";
        var inventory = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/json", Encoding.UTF8.GetBytes(EquivalentJson())).StructuralInventory;
        var registry = VerifiedSourceFingerprintRegistry.Create(
            [VerifiedSourceFingerprintEvidence.Create("raw-otlp", sourceVersion, inventory.SchemaFingerprint)],
            [],
            [SourceRecognitionProfileEvidence.Create(
                "raw-otlp",
                sourceVersion,
                inventory.SchemaFingerprint,
                SourceOccurrenceCount.Create(2))]);
        var metadata = OtlpTraceSourceMetadata.Create(
            "raw-otlp",
            sourceVersion,
            "raw-otlp",
            "1",
            SourceCaptureContentState.NotCaptured);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceFingerprintRegistry = registry,
            SourceMetadataProvider = new FixedOtlpTraceSourceMetadataProvider(metadata),
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(EquivalentJson()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var observation = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal(SourceCompatibilityState.RecognizedRecordDropDetected, observation.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.RecognizedRecordDropDetected], observation.ReasonCodes);
    }

    [Fact]
    public async Task Projection_WrongRepresentationsStayRawOnlyAndBatchCompletes()
    {
        const string marker = "wrong-representation-raw-marker";
        var payload = """
        {
          "resourceSpans": [{
            "scopeSpans": [{
              "spans": [{
                "traceId":"11111111111111111111111111111111",
                "spanId":{"marker":"wrong-representation-raw-marker"},
                "parentSpanId":["wrong-representation-raw-marker"],
                "attributes":[{
                  "key":"gen_ai.request.model",
                  "value":{"stringValue":{"marker":"wrong-representation-raw-marker"}}
                }]
              },{
                "traceId":{"marker":"wrong-representation-raw-marker"},
                "spanId":"valid-shape-id",
                "attributes":[]
              }]
            }]
          }]
        }
        """;
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });
        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawStore = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        var projectionStore = new RawTelemetryStoreProjectionStore(rawStore);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var worker = new ProjectionWorker(
            projectionStore,
            health,
            new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter));

        await worker.RunProjectionPassAsync();

        var span = Assert.Single(projectionStore.GetSpansForTrace("11111111111111111111111111111111"));
        Assert.Null(span.SpanId);
        Assert.Null(span.ParentSpanId);
        Assert.Null(span.RequestModel);
        Assert.Single(projectionStore.ListMonitorSpans("11111111111111111111111111111111", 0, 200).Items);
        Assert.Equal(0, projectionStore.GetProjectionStatus().Backlog);
        Assert.Equal(0, projectionStore.GetSpanProjectionStatus().Backlog);
        Assert.Contains(marker, Assert.Single(rawStore.ListRecords()).PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTraces_UsesOnlyTrustedInjectedSourceMetadata()
    {
        using var temp = new MonitorTempDirectory();
        var metadata = OtlpTraceSourceMetadata.Create(
            "trusted-fixture-source",
            "2.1.207",
            "trusted-fixture-adapter",
            "7",
            SourceCaptureContentState.NotCaptured);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceMetadataProvider = new FixedOtlpTraceSourceMetadataProvider(metadata),
        });

        var response = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent(EquivalentJson().Replace(
                "\"client.kind\"",
                "\"untrusted.source_surface\"",
                StringComparison.Ordinal)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var observation = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal("trusted-fixture-source", observation.SourceSurface);
        Assert.Equal("2.1.207", observation.SourceApplicationVersion);
        Assert.Equal("trusted-fixture-adapter", observation.SourceAdapter);
        Assert.Equal("7", observation.AdapterVersion);
        Assert.Equal(SourceCaptureContentState.NotCaptured, observation.CaptureContentState);
    }

    [Fact]
    public async Task PostTraces_MissingRequiredSpanSignalIsCommittedAsUnsupported()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent("""{"resourceSpans":[]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(temp.CreateRawStore().ListRecords());
        var observation = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal(SourceCompatibilityState.UnsupportedSourceVersion, observation.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.UnsupportedSourceVersion], observation.ReasonCodes);
    }

    [Fact]
    public async Task PostTraces_WrongHierarchyCommitsOriginalRawAndUnsupportedObservation()
    {
        const string marker = "wrong-hierarchy-raw-only-marker";
        var payload = $$"""
        {
          "resourceSpans": {
            "marker": "{{marker}}",
            "scopeSpans": [{"spans": [{"traceId":"must-not-project"}]}]
          }
        }
        """;
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = Assert.Single(temp.CreateRawStore().ListRecords());
        Assert.Equal(payload, raw.PayloadJson);
        Assert.Contains(marker, raw.PayloadJson, StringComparison.Ordinal);
        Assert.Null(raw.TraceId);
        Assert.Null(raw.ResourceAttributesJson);
        var observation = Assert.Single(
            new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal(raw.Id, observation.RawRecordId);
        Assert.Equal(SourceCompatibilityState.UnsupportedSourceVersion, observation.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.UnsupportedSourceVersion], observation.ReasonCodes);
    }

    [Fact]
    public async Task PostTraces_NonObjectRootRecordsParseFailureWithoutRaw()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent("[]"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(temp.CreateRawStore().ListRecords());
        var failure = Assert.Single(
            new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Null(failure.RawRecordId);
        Assert.Equal(SourceCompatibilityState.AdapterFailure, failure.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.AdapterParseFailure], failure.ReasonCodes);
    }

    [Fact]
    public async Task PostTraces_ParseFailureRecordsSanitizedNullableDiagnosticWithoutRaw()
    {
        const string marker = "RAW_PARSE_FAILURE_MARKER_7cefb";
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        });

        var response = await host.Client.PostAsync(
            "/v1/traces",
            JsonContent($$"""{"resourceSpans":[{"marker":"{{marker}}"}"""));

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_payload", responseBody);
        Assert.DoesNotContain(marker, responseBody, StringComparison.Ordinal);
        Assert.Empty(temp.CreateRawStore().ListRecords());
        var failure = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Null(failure.RawRecordId);
        Assert.Null(failure.IngestBatchId);
        Assert.Null(failure.SourceSurface);
        Assert.Null(failure.SourceApplicationVersion);
        Assert.Null(failure.SourceAdapter);
        Assert.Null(failure.AdapterVersion);
        Assert.Null(failure.SchemaFingerprint);
        Assert.Null(failure.InventoryHash);
        Assert.Null(failure.CaptureContentState);
        Assert.Equal(SourceCompatibilityState.AdapterFailure, failure.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.AdapterParseFailure], failure.ReasonCodes);
        Assert.DoesNotContain(marker, ReadSharedDatabaseText(temp.DatabasePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTraces_MalformedProtobufRecordsSanitizedParseFailureWithoutRaw()
    {
        const string marker = "MALFORMED_PROTOBUF_EXCEPTION_BYTES_9fd31";
        var payload = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.StringField(100, marker),
            [0x0a, 0x80]);
        using var temp = new MonitorTempDirectory();
        await using (var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
        }))
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

            var response = await host.Client.PostAsync("/v1/traces", content);

            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("invalid_payload", responseBody);
            Assert.DoesNotContain(marker, responseBody, StringComparison.Ordinal);
            Assert.Empty(temp.CreateRawStore().ListRecords());
            var failure = Assert.Single(
                new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
            Assert.Null(failure.RawRecordId);
            Assert.Null(failure.IngestBatchId);
            Assert.Null(failure.SchemaFingerprint);
            Assert.Null(failure.InventoryHash);
            Assert.Equal(SourceCompatibilityState.AdapterFailure, failure.CompatibilityState);
            Assert.Equal([SourceCompatibilityReasonCodes.AdapterParseFailure], failure.ReasonCodes);
        }

        Assert.DoesNotContain(marker, ReadSharedDatabaseText(temp.DatabasePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTraces_AdapterExceptionRecordsSanitizedNullableDiagnosticWithoutRaw()
    {
        const string marker = "ADAPTER_EXCEPTION_MARKER_931fd";
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartProjectionWorker = false,
            UseUserSecrets = false,
            SourceMetadataProvider = new ThrowingSourceMetadataProvider(marker),
        });

        var response = await host.Client.PostAsync("/v1/traces", JsonContent(EquivalentJson()));

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("internal_error", responseBody);
        Assert.DoesNotContain(marker, responseBody, StringComparison.Ordinal);
        Assert.Empty(temp.CreateRawStore().ListRecords());
        var failure = Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Null(failure.RawRecordId);
        Assert.Null(failure.IngestBatchId);
        Assert.Null(failure.SourceSurface);
        Assert.Null(failure.SourceApplicationVersion);
        Assert.Null(failure.SourceAdapter);
        Assert.Null(failure.AdapterVersion);
        Assert.Null(failure.SchemaFingerprint);
        Assert.Null(failure.InventoryHash);
        Assert.Null(failure.CaptureContentState);
        Assert.Equal(SourceCompatibilityState.AdapterFailure, failure.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.AdapterException], failure.ReasonCodes);
        Assert.DoesNotContain(marker, ReadSharedDatabaseText(temp.DatabasePath), StringComparison.Ordinal);
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static string SourcePayload(string traceId, string spanId, string serviceName) =>
        """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"service.name","value":{"stringValue":"SERVICE_NAME"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"TRACE_ID","spanId":"SPAN_ID","name":"chat gpt-4o"}
        ]}]}]}
        """
        .Replace("TRACE_ID", traceId, StringComparison.Ordinal)
        .Replace("SPAN_ID", spanId, StringComparison.Ordinal)
        .Replace("SERVICE_NAME", serviceName, StringComparison.Ordinal);

    private static string SourcePayloadWithConversation(
        string traceId,
        string spanId,
        string serviceName,
        string conversationId) =>
        SourcePayload(traceId, spanId, serviceName).Replace(
            "\"name\":\"chat gpt-4o\"",
            """
            "name":"chat gpt-4o","attributes":[
              {"key":"gen_ai.conversation.id","value":{"stringValue":"CONVERSATION_ID"}}
            ]
            """.Replace(
                "CONVERSATION_ID",
                conversationId,
                StringComparison.Ordinal),
            StringComparison.Ordinal);

    private static string ReadSharedDatabaseText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Encoding.UTF8.GetString(copy.ToArray());
    }

    private sealed class ThrowingSourceMetadataProvider(string marker) : IOtlpTraceSourceMetadataProvider
    {
        public OtlpTraceSourceMetadata GetMetadata() => throw new InvalidOperationException(marker);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static string EquivalentJson() =>
        """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"experiment.id","value":{"stringValue":"baseline"}}
        ]},"scopeSpans":[{"spans":[{
          "traceId":"11111111111111111111111111111111",
          "spanId":"2222222222222222",
          "name":"chat gpt-4o",
          "startTimeUnixNano":"1000000000",
          "endTimeUnixNano":"1500000000",
          "attributes":[
            {"key":"gen_ai.usage.input_tokens","value":{"intValue":"10"}},
            {"key":"gen_ai.usage.output_tokens","value":{"intValue":"5"}}
          ]
        }]}]}]}
        """;
}
