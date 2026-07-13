using System.Net;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SourceDiagnosticsApiTests
{
    [Fact]
    public async Task SourceDiagnostics_ReturnsSanitizedStableCursorPage()
    {
        using var temp = new MonitorTempDirectory();
        var compatibilityStore = new FakeCompatibilityStore(CompatibilityRows());
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = compatibilityStore,
                StartWriter = false,
                StartProjectionWorker = false,
            });

        var response = await host.Client.GetAsync("/api/monitor/source-diagnostics?limit=2");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var items = document.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("observation-1", items[0].GetProperty("observation_id").GetString());
        Assert.Equal("supported", items[0].GetProperty("compatibility_state").GetString());
        Assert.Equal("none", items[0].GetProperty("next_action").GetString());
        Assert.Equal("observation-2", items[1].GetProperty("observation_id").GetString());
        Assert.Equal("supported_with_unknown_fields", items[1].GetProperty("compatibility_state").GetString());
        Assert.Equal("review_unknown_fields", items[1].GetProperty("next_action").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("next_cursor").GetInt64());
        Assert.Equal(
            [
                "adapter_version",
                "compatibility_state",
                "ingest_batch_id",
                "inventory_hash",
                "next_action",
                "observation_id",
                "observed_at",
                "reason_codes",
                "schema_fingerprint",
                "source_adapter",
                "source_application_version",
                "source_surface",
                "unknown_attribute_count",
                "unknown_event_count",
                "unknown_span_count",
            ],
            items[0].EnumerateObject().Select(property => property.Name).Order().ToArray());

        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", body);
        Assert.DoesNotContain("leak-marker@example.com", body);
        Assert.DoesNotContain("sk-live-SECRET", body);
        Assert.DoesNotContain("C:\\Users\\victim\\secret.txt", body);
        Assert.DoesNotContain("raw_record_id", body);
        Assert.DoesNotContain("capture_content_state", body);
        Assert.DoesNotContain("unknown_observations", body);
    }

    [Theory]
    [InlineData("/api/monitor/source-diagnostics?after=-1")]
    [InlineData("/api/monitor/source-diagnostics?after=not-a-cursor")]
    [InlineData("/api/monitor/source-diagnostics?after=1.5")]
    [InlineData("/api/monitor/source-diagnostics?after=9223372036854775808")]
    [InlineData("/api/monitor/source-diagnostics?after=%20")]
    [InlineData("/api/monitor/source-diagnostics?limit=0")]
    [InlineData("/api/monitor/source-diagnostics?limit=-1")]
    [InlineData("/api/monitor/source-diagnostics?limit=1.5")]
    [InlineData("/api/monitor/source-diagnostics?limit=201")]
    [InlineData("/api/monitor/source-diagnostics?limit=2147483648")]
    [InlineData("/api/monitor/source-diagnostics?limit=1&limit=2")]
    public async Task SourceDiagnostics_RejectsInvalidCursorAndLimit(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var response = await host.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"error\":\"invalid_query\"", body);
    }

    [Fact]
    public async Task SourceDiagnostics_DefaultsToFiftyAndAcceptsValidAfterAndLimitBounds()
    {
        using var temp = new MonitorTempDirectory();
        var compatibilityStore = new FakeCompatibilityStore(Enumerable.Range(1, 201)
            .Select(id => Row(id, SourceCompatibilityState.Supported, [], SourceCompatibilityNextActions.None))
            .ToArray());
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = compatibilityStore,
                StartWriter = false,
                StartProjectionWorker = false,
            });

        using var defaultPage = JsonDocument.Parse(await host.Client.GetStringAsync("/api/monitor/source-diagnostics"));
        var defaultItems = defaultPage.RootElement.GetProperty("items");
        Assert.Equal(50, defaultItems.GetArrayLength());
        Assert.Equal("observation-1", defaultItems[0].GetProperty("observation_id").GetString());
        Assert.Equal("observation-50", defaultItems[49].GetProperty("observation_id").GetString());
        Assert.Equal(50, defaultPage.RootElement.GetProperty("next_cursor").GetInt64());

        using var afterPage = JsonDocument.Parse(await host.Client.GetStringAsync("/api/monitor/source-diagnostics?after=50&limit=1"));
        Assert.Equal("observation-51", afterPage.RootElement.GetProperty("items")[0].GetProperty("observation_id").GetString());
        Assert.Equal(51, afterPage.RootElement.GetProperty("next_cursor").GetInt64());

        using var maximumPage = JsonDocument.Parse(await host.Client.GetStringAsync("/api/monitor/source-diagnostics?limit=200"));
        Assert.Equal(200, maximumPage.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(200, maximumPage.RootElement.GetProperty("next_cursor").GetInt64());
    }

    [Fact]
    public async Task SourceDiagnostics_EmptyStoreReturnsNoItemsAndNoCursor()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = new FakeCompatibilityStore([]),
                StartWriter = false,
                StartProjectionWorker = false,
            });

        using var document = JsonDocument.Parse(await host.Client.GetStringAsync("/api/monitor/source-diagnostics"));

        Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task SourceDiagnostics_DoesNotChangeTheReadinessStatusOrBody()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var health = MonitorTestHealth.Ready(time);
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                Health = health,
                SourceCompatibilityStore = new FakeCompatibilityStore([]),
                StartWriter = false,
                StartProjectionWorker = false,
        });

        var before = await host.Client.GetAsync("/health/ready");
        var beforeBody = await before.Content.ReadAsStringAsync();
        var diagnostics = await host.Client.GetAsync("/api/monitor/source-diagnostics");
        var after = await host.Client.GetAsync("/health/ready");
        var afterBody = await after.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
        Assert.Equal(before.StatusCode, after.StatusCode);
        Assert.Equal(beforeBody, afterBody);
    }

    [Fact]
    public async Task SourceDiagnostics_EmitsEveryCompatibilityStateWithItsFixedNextAction()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = new FakeCompatibilityStore(CompatibilityRows()),
                StartWriter = false,
                StartProjectionWorker = false,
            });

        using var document = JsonDocument.Parse(await host.Client.GetStringAsync("/api/monitor/source-diagnostics?limit=7"));
        var items = document.RootElement.GetProperty("items");
        var actual = items
            .EnumerateArray()
            .Select(item => (
                State: item.GetProperty("compatibility_state").GetString(),
                NextAction: item.GetProperty("next_action").GetString()))
            .ToArray();

        Assert.Equal(
        [
            ("supported", "none"),
            ("supported_with_unknown_fields", "review_unknown_fields"),
            ("unsupported_source_version", "use_compatible_source_or_update_adapter"),
            ("schema_drift_detected", "capture_fixture_and_review_mapping"),
            ("recognized_record_drop_detected", "restore_mapping_or_update_versioned_golden"),
            ("adapter_failure", "validate_payload_and_protocol"),
            ("adapter_failure", "inspect_sanitized_adapter_failure"),
        ],
        actual);
        Assert.Equal(
        [
            Array.Empty<string>(),
            [SourceCompatibilityReasonCodes.UnknownFieldsObserved],
            [SourceCompatibilityReasonCodes.UnsupportedSourceVersion],
            [SourceCompatibilityReasonCodes.SchemaDriftDetected],
            [SourceCompatibilityReasonCodes.RecognizedRecordDropDetected],
            [SourceCompatibilityReasonCodes.AdapterParseFailure],
            [SourceCompatibilityReasonCodes.AdapterException],
        ],
        items.EnumerateArray()
            .Select(item => item.GetProperty("reason_codes").EnumerateArray().Select(reason => reason.GetString()).ToArray())
            .ToArray());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task SourceDiagnostics_MapsConcreteSqliteReadLockToSanitizedPersistenceBusy()
    {
        using var temp = new MonitorTempDirectory();
        var connectionOptions = new RawTelemetryStoreConnectionOptions(EnableWriteAheadLog: false, BusyTimeoutMilliseconds: 0);
        var compatibilityStore = new SqliteSourceCompatibilityStore(temp.DatabasePath, connectionOptions);
        compatibilityStore.CreateSchema();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = compatibilityStore,
                StartWriter = false,
                StartProjectionWorker = false,
            });
        using var lockConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temp.DatabasePath,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        lockConnection.Open();
        using var lockCommand = lockConnection.CreateCommand();
        lockCommand.CommandText = "PRAGMA locking_mode = EXCLUSIVE;";
        lockCommand.ExecuteNonQuery();
        lockCommand.CommandText = "BEGIN EXCLUSIVE;";
        lockCommand.ExecuteNonQuery();
        try
        {
            var response = await host.Client.GetAsync("/api/monitor/source-diagnostics");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("\"error\":\"persistence_busy\"", body);
            Assert.DoesNotContain("Sqlite", body);
            Assert.DoesNotContain(temp.DatabasePath, body);
        }
        finally
        {
            lockCommand.CommandText = "ROLLBACK;";
            lockCommand.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task SourceDiagnostics_MapsUnexpectedReadFailureToSanitizedInternalError()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = new ThrowingCompatibilityStore(new InvalidOperationException("SECRET_PROMPT_TEXT_MARKER")),
                StartWriter = false,
                StartProjectionWorker = false,
            });

        var response = await host.Client.GetAsync("/api/monitor/source-diagnostics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"error\":\"internal_error\"", body);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }

    [Fact]
    public async Task SourceDiagnostics_ActualPersistedRawAndUnknownMarkersNeverReachTheApi()
    {
        using var temp = new MonitorTempDirectory();
        var rawPayload =
            """
            {"producer_marker":"SECRET_PROMPT_TEXT_MARKER leak-marker@example.com sk-live-SECRET C:\\Users\\victim\\secret.txt","resourceSpans":[{"scopeSpans":[{"spans":[{}]}]}]}
            """;
        var compatibilityStore = new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        compatibilityStore.CreateSchema();
        var observedAt = DateTimeOffset.UnixEpoch;
        var inventory = OtlpJsonStructuralWalker.Build(rawPayload, observedAt);
        var observation = SourceObservationBatchDraft.Create(
            "raw-marker-batch",
            "claude-code",
            "1.0.0",
            "claude-code-otel",
            "1",
            inventory,
            SourceCompatibilityEvaluator.Assess(
                "claude-code",
                "1.0.0",
                inventory,
                observedRecognizedCount: 1,
                VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.NotCaptured,
            observedAt);
        new SqliteIngestionCommitStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).Commit(
            ValidatedIngestionBatch.Create(
                new RawTelemetryRecord(
                    Id: null,
                    Source: RawTelemetrySources.RawOtlp,
                    TraceId: null,
                    ReceivedAt: observedAt,
                    ResourceAttributesJson: null,
                    PayloadJson: rawPayload),
                observation));
        Assert.Contains("SECRET_PROMPT_TEXT_MARKER", Assert.Single(new RawTelemetryStore(temp.DatabasePath).ListRecords()).PayloadJson);

        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: new MonitorHostTestOptions
            {
                SourceCompatibilityStore = compatibilityStore,
                StartWriter = false,
                StartProjectionWorker = false,
            });

        var body = await host.Client.GetStringAsync("/api/monitor/source-diagnostics");

        Assert.Contains("raw-marker-batch", body);
        Assert.Contains("schema_drift_detected", body);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", body);
        Assert.DoesNotContain("leak-marker@example.com", body);
        Assert.DoesNotContain("sk-live-SECRET", body);
        Assert.DoesNotContain("C:\\Users\\victim\\secret.txt", body);
    }

    private static IReadOnlyList<SourceCompatibilityRow> CompatibilityRows() =>
    [
        Row(1, SourceCompatibilityState.Supported, [], SourceCompatibilityNextActions.None),
        Row(2, SourceCompatibilityState.SupportedWithUnknownFields, [SourceCompatibilityReasonCodes.UnknownFieldsObserved], SourceCompatibilityNextActions.ReviewUnknownFields),
        Row(3, SourceCompatibilityState.UnsupportedSourceVersion, [SourceCompatibilityReasonCodes.UnsupportedSourceVersion], SourceCompatibilityNextActions.UseCompatibleSourceOrUpdateAdapter),
        Row(4, SourceCompatibilityState.SchemaDriftDetected, [SourceCompatibilityReasonCodes.SchemaDriftDetected], SourceCompatibilityNextActions.CaptureFixtureAndReviewMapping),
        Row(5, SourceCompatibilityState.RecognizedRecordDropDetected, [SourceCompatibilityReasonCodes.RecognizedRecordDropDetected], SourceCompatibilityNextActions.RestoreMappingOrUpdateVersionedGolden),
        Row(6, SourceCompatibilityState.AdapterFailure, [SourceCompatibilityReasonCodes.AdapterParseFailure], SourceCompatibilityNextActions.ValidatePayloadAndProtocol),
        Row(7, SourceCompatibilityState.AdapterFailure, [SourceCompatibilityReasonCodes.AdapterException], SourceCompatibilityNextActions.InspectSanitizedAdapterFailure),
    ];

    private static SourceCompatibilityRow Row(
        long id,
        SourceCompatibilityState state,
        IReadOnlyList<string> reasons,
        string nextAction) => new(
            Id: id,
            ObservationId: $"observation-{id}",
            RawRecordId: id == 6 ? null : id,
            IngestBatchId: id == 6 ? null : $"batch-{id}",
            SourceSurface: "claude-code",
            SourceApplicationVersion: "1.0.0",
            SourceAdapter: "claude-code-otel",
            AdapterVersion: "1",
            SchemaFingerprint: id == 6 ? null : $"sha256:{id:x64}",
            InventoryHash: id == 6 ? null : $"sha256:{(id + 10):x64}",
            CompatibilityState: state,
            ReasonCodes: reasons,
            NextAction: nextAction,
            CaptureContentState: SourceCaptureContentState.NotCaptured,
            UnknownSpanCount: id,
            UnknownEventCount: id + 1,
            UnknownAttributeCount: id + 2,
            OverflowDistinctCount: 0,
            OverflowOccurrenceCount: 0,
            ObservedAt: DateTimeOffset.UnixEpoch.AddMinutes(id),
            UnknownObservations:
            [
                new SourceUnknownObservationRow(
                    Id: id,
                    SourceObservationId: id,
                    Kind: SourceUnknownKind.Attribute,
                    Name: "SECRET_PROMPT_TEXT_MARKER leak-marker@example.com sk-live-SECRET C:\\Users\\victim\\secret.txt",
                    Count: 1,
                    SourceVersionLabel: null,
                    FirstObservedAt: DateTimeOffset.UnixEpoch,
                    LastObservedAt: DateTimeOffset.UnixEpoch,
                    OpaqueSampleReference: "sample:v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
            ]);

    private sealed class FakeCompatibilityStore(IReadOnlyList<SourceCompatibilityRow> rows) : ISourceCompatibilityStore
    {
        public void CreateSchema()
        {
        }

        public long RecordAdapterFailure(SourceAdapterFailureDraft failure) => throw new NotSupportedException();

        public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId) =>
            rows.SingleOrDefault(row => row.RawRecordId == rawRecordId);

        public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit) =>
            rows.Where(row => row.Id > (after ?? 0)).Take(limit).ToArray();
    }

    private sealed class ThrowingCompatibilityStore(Exception exception) : ISourceCompatibilityStore
    {
        public void CreateSchema()
        {
        }

        public long RecordAdapterFailure(SourceAdapterFailureDraft failure) => throw exception;

        public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId) => throw exception;

        public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit) => throw exception;
    }
}
