using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SQLitePCL;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorAnalysisStoreTests
{
    [Fact]
    public void CopilotAnalysisSettings_ReadsByokProviderConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopilotAnalysis:Model"] = "glm-5.2",
                ["CopilotAnalysis:BaseDirectory"] = @"C:\tmp\cao-copilot-sdk",
                ["CopilotAnalysis:Provider:Type"] = "openai",
                ["CopilotAnalysis:Provider:BaseUrl"] = "https://example.test/v1/",
                ["CopilotAnalysis:Provider:WireApi"] = "completions",
                ["CopilotAnalysis:Provider:ApiKey"] = "secret-value",
            })
            .Build();

        var settings = CopilotAnalysisSettings.From(configuration);

        Assert.True(settings.Enabled);
        Assert.Equal("glm-5.2", settings.Model);
        Assert.Equal(@"C:\tmp\cao-copilot-sdk", settings.BaseDirectory);
        Assert.NotNull(settings.Provider);
        Assert.Equal("openai", settings.Provider.Type);
        Assert.Equal("https://example.test/v1", settings.Provider.BaseUrl);
        Assert.Equal("completions", settings.Provider.WireApi);
    }

    [Fact]
    public void CopilotAnalysisSettings_ReadsDisabledFlag()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopilotAnalysis:Enabled"] = "false",
            })
            .Build();

        var settings = CopilotAnalysisSettings.From(configuration);

        Assert.False(settings.Enabled);
        Assert.Equal("gpt-5", settings.Model);
        Assert.Null(settings.Provider);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("600", 600)]
    [InlineData("0", 60)]
    [InlineData("-5", 60)]
    [InlineData("not-a-number", 60)]
    public void CopilotAnalysisSettings_ReadsTimeoutSeconds(string? configured, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopilotAnalysis:TimeoutSeconds"] = configured,
            })
            .Build();

        var settings = CopilotAnalysisSettings.From(configuration);

        Assert.Equal(expected, settings.TimeoutSeconds);
    }

    [Fact]
    public void CopilotAnalysisSettings_RejectsUnsupportedWireApi()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CopilotAnalysis:Provider:Type"] = "openai",
                ["CopilotAnalysis:Provider:BaseUrl"] = "https://example.test/v1",
                ["CopilotAnalysis:Provider:WireApi"] = "invalid",
                ["CopilotAnalysis:Provider:ApiKey"] = "secret-value",
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => CopilotAnalysisSettings.From(configuration));

        Assert.Contains("WireApi", exception.Message);
        Assert.DoesNotContain("secret-value", exception.Message);
    }

    [Fact]
    public void StartRun_PersistsQueuedRun()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();

        var result = store.StartRun(
            traceId: "trace-analysis",
            rawRecordId: 42,
            spanId: "span-1",
            focus: MonitorAnalysisFocus.ToolUsage,
            requestedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

        var run = store.GetRun(result.RunId);

        Assert.NotNull(run);
        Assert.Equal("trace-analysis", run.TraceId);
        Assert.Equal(42, run.RawRecordId);
        Assert.Equal("span-1", run.SpanId);
        Assert.Equal(MonitorAnalysisFocus.ToolUsage, run.Focus);
        Assert.Equal(MonitorAnalysisStatus.Queued, run.Status);
        Assert.Equal(0, CountRetentionItems(temp.DatabasePath, result.RunId));
    }

    [Fact]
    public void CompleteInstructionDiagnosisRun_PersistsResultAndHandoffAtomically()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.InstructionDiagnosis, requestedAt);
        var fence = store.AppendEvent(run.RunId, run.OperationToken, null, "running", "started", requestedAt.AddMinutes(1));
        var handoff = InstructionFindingPipelineV1.Generate(
            run.RunId,
            new InstructionFindingEvidenceIndexV1("trace-analysis", []),
            []);

        store.CompleteInstructionDiagnosisRun(run.RunId, run.OperationToken, fence, "result", handoff, requestedAt.AddMinutes(2));

        Assert.Equal(MonitorAnalysisStatus.Succeeded, store.GetRun(run.RunId)!.Status);
        Assert.Equal(
            InstructionFindingJsonV1.Serialize(handoff),
            InstructionFindingJsonV1.Serialize(new SqliteInstructionFindingHandoffStore(temp.DatabasePath).Get(run.RunId)!));
    }

    [Fact]
    public void CompleteInstructionDiagnosisRun_FailureBeforeCommitRollsBackResultAndHandoff()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider,
            phase =>
            {
                if (phase == MonitorAnalysisStoreWritePhase.BeforeCommit) throw new InvalidOperationException("injected");
            });
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.InstructionDiagnosis, requestedAt);
        var handoff = InstructionFindingPipelineV1.Generate(
            run.RunId,
            new InstructionFindingEvidenceIndexV1("trace-analysis", []),
            []);

        Assert.Throws<InvalidOperationException>(() =>
            store.CompleteInstructionDiagnosisRun(run.RunId, run.OperationToken, null, "result", handoff, requestedAt.AddMinutes(2)));

        Assert.Equal(MonitorAnalysisStatus.Queued, store.GetRun(run.RunId)!.Status);
        Assert.Null(new SqliteInstructionFindingHandoffStore(temp.DatabasePath).Get(run.RunId));
    }

    [Fact]
    public void CompleteInstructionDiagnosisRun_NonInstructionFocusRejectsHandoff()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, requestedAt);
        var handoff = InstructionFindingPipelineV1.Generate(
            run.RunId,
            new InstructionFindingEvidenceIndexV1("trace-analysis", []),
            []);

        var exception = Assert.Throws<InstructionFindingValidationException>(() =>
            store.CompleteInstructionDiagnosisRun(run.RunId, run.OperationToken, null, "result", handoff, requestedAt.AddMinutes(2)));

        Assert.Equal(InstructionFindingValidationCodeV1.InvalidContract, exception.Code);
        Assert.Equal(MonitorAnalysisStatus.Queued, store.GetRun(run.RunId)!.Status);
        Assert.Null(new SqliteInstructionFindingHandoffStore(temp.DatabasePath).Get(run.RunId));
    }

    [Fact]
    public void StartRun_DerivesOperationTokenWithoutExposingTheRetentionOwnerToken()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch;
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, requestedAt);
        var sourceToken = ReadRetentionOwnerToken(temp.DatabasePath, run.RunId);

        Assert.NotEqual(sourceToken, run.OperationToken.Copy());

        var rejection = Assert.Throws<RetentionRevisionFenceRejectedException>(() =>
            store.AppendEvent(run.RunId, new MonitorAnalysisOperationToken(sourceToken), null, "progress", "raw", requestedAt.AddMinutes(1)));

        AssertBoundedSafeRejection(rejection);
        Assert.Equal(0, CountEvents(temp.DatabasePath, run.RunId));
        Assert.Equal(0, CountRetentionItems(temp.DatabasePath, run.RunId));

        var fence = store.AppendEvent(run.RunId, run.OperationToken, null, "progress", "raw", requestedAt.AddMinutes(1));
        Assert.NotNull(fence);
    }

    [Fact]
    public void RetentionRevisionFence_DoesNotRetainTheRawSourceToken()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, DateTimeOffset.UnixEpoch);
        var sourceToken = ReadRetentionOwnerToken(temp.DatabasePath, run.RunId);
        var fence = store.AppendEvent(run.RunId, run.OperationToken, null, "progress", "raw", DateTimeOffset.UnixEpoch.AddMinutes(1));

        var fields = typeof(RetentionRevisionFence).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        Assert.DoesNotContain(fields, field => string.Equals(field.Name, "sourceToken", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields.Select(field => field.GetValue(fence)).OfType<byte[]>(), value => value.AsSpan().SequenceEqual(sourceToken));
        Assert.DoesNotContain(typeof(RetentionRevisionFence).GetProperties(), property => property.PropertyType == typeof(byte[]) && property.Name.Contains("source", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(RetentionRevisionFence).GetMethods(), method => method.ReturnType == typeof(byte[]) && method.Name.Contains("source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateSchema_MissingCatalogFailsBeforeCreatingDatabaseOrAnalysisSchema()
    {
        using var target = new MonitorTempDirectory();
        using var unrelated = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(target.DatabasePath, unrelated.RetentionContext, target.TimeProvider);

        Assert.Throws<RetentionCatalogUnavailableException>(() => store.CreateSchema());

        Assert.False(File.Exists(target.DatabasePath));
    }

    [Fact]
    public void CreateSchema_UnsupportedCatalogFailsBeforeAnalysisSchemaMutation()
    {
        using var temp = new MonitorTempDirectory();
        var context = temp.RetentionContext;
        ExecuteWithoutParameters(temp.DatabasePath, "DELETE FROM retention_component_versions WHERE component = 'retention';");
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, context, temp.TimeProvider);

        Assert.Throws<RetentionCatalogUnavailableException>(() => store.CreateSchema());

        Assert.False(TableExists(temp.DatabasePath, "monitor_analysis_runs"));
        Assert.False(TableExists(temp.DatabasePath, "monitor_analysis_events"));
    }

    [Fact]
    public void AppendEvent_AtomicallyCreatesAnalysisRawRetentionItem()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UtcNow;
        var result = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.ToolUsage, requestedAt);

        var fence = store.AppendEvent(result.RunId, result.OperationToken, null, "progress", "raw local event", requestedAt.AddMinutes(1));

        Assert.Equal(1, CountRetentionItems(temp.DatabasePath, result.RunId));
        Assert.NotNull(fence);
    }

    [Fact]
    public async Task AppendEvent_ConcurrentFirstRawWritesCreateOneItemAndRejectTheLosingBootstrap()
    {
        using var temp = new MonitorTempDirectory();
        var setup = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        setup.CreateSchema();
        var requestedAt = DateTimeOffset.UtcNow;
        var run = setup.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.ToolUsage, requestedAt);

        using var start = new Barrier(3);
        var first = Task.Run(() =>
        {
            start.SignalAndWait();
            return new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider).AppendEvent(run.RunId, run.OperationToken, null, "progress", "first raw event", requestedAt.AddMinutes(1));
        });
        var second = Task.Run(() =>
        {
            start.SignalAndWait();
            return new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider).AppendEvent(run.RunId, run.OperationToken, null, "progress", "second raw event", requestedAt.AddMinutes(2));
        });

        start.SignalAndWait();
        var outcomes = await Task.WhenAll(
            first.ContinueWith(task => task.Exception?.GetBaseException()),
            second.ContinueWith(task => task.Exception?.GetBaseException()));

        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is RetentionRevisionFenceRejectedException);
        Assert.Equal(1, CountEvents(temp.DatabasePath, run.RunId));
        Assert.Equal(1, CountRetentionItems(temp.DatabasePath, run.RunId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AppendEvent_InjectedWriteFailureRollsBackEventAndCatalog(int failingPhase)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider, phase =>
        {
            if ((int)phase == failingPhase) throw new InvalidOperationException("injected");
        });
        store.CreateSchema();
        var result = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.ToolUsage, DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => store.AppendEvent(result.RunId, result.OperationToken, null, "progress", "raw local event", DateTimeOffset.UtcNow));

        Assert.Equal(0, CountRetentionItems(temp.DatabasePath, result.RunId));
        Assert.Equal(0, CountEvents(temp.DatabasePath, result.RunId));
    }

    [Fact]
    public void RawWriters_CarryTheirFenceAcrossAppendCompleteAndRawFinish()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch;

        var appendRun = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, requestedAt);
        var appendFence = store.AppendEvent(appendRun.RunId, appendRun.OperationToken, null, "progress", "first", requestedAt.AddMinutes(1));
        appendFence = store.AppendEvent(appendRun.RunId, appendRun.OperationToken, appendFence, "progress", "second", requestedAt.AddMinutes(2));
        _ = store.CompleteRun(appendRun.RunId, appendRun.OperationToken, appendFence, "result", requestedAt.AddMinutes(3));

        var finishRun = store.StartRun("trace-analysis", 43, "span-2", MonitorAnalysisFocus.Errors, requestedAt);
        var finishFence = store.AppendEvent(finishRun.RunId, finishRun.OperationToken, null, "progress", "first", requestedAt.AddMinutes(1));
        var returnedFence = store.FinishRun(finishRun.RunId, finishRun.OperationToken, finishFence, MonitorAnalysisStatus.Failed, "raw error", requestedAt.AddMinutes(2));

        Assert.NotNull(returnedFence);
        Assert.Equal(1, CountRetentionItems(temp.DatabasePath, appendRun.RunId));
        Assert.Equal(1, CountRetentionItems(temp.DatabasePath, finishRun.RunId));
    }

    [Fact]
    public void FinishRun_MetadataOnlyDoesNotCreateRawItem()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, DateTimeOffset.UnixEpoch);

        var fence = store.FinishRun(run.RunId, run.OperationToken, null, MonitorAnalysisStatus.Canceled, null, DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.Null(fence);
        Assert.Equal(0, CountRetentionItems(temp.DatabasePath, run.RunId));
        Assert.Equal(MonitorAnalysisStatus.Canceled, store.GetRun(run.RunId)!.Status);
    }

    [Fact]
    public void RawWriters_RejectWrongRunTokenAndNullAfterRawExistsWithoutMutatingRawState()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch;
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, requestedAt);
        var otherRun = store.StartRun("trace-analysis", 43, "span-2", MonitorAnalysisFocus.Errors, requestedAt);
        var fence = store.AppendEvent(run.RunId, run.OperationToken, null, "progress", "original", requestedAt.AddMinutes(1));
        var before = ReadRawState(temp.DatabasePath, run.RunId);

        var wrongRun = Assert.Throws<RetentionRevisionFenceRejectedException>(() => store.AppendEvent(run.RunId, otherRun.OperationToken, fence, "progress", "RAW_PAYLOAD_MARKER TOKEN_MARKER C:\\secret-path", requestedAt.AddMinutes(2)));
        var wrongToken = Assert.Throws<RetentionRevisionFenceRejectedException>(() => store.FinishRun(run.RunId, new MonitorAnalysisOperationToken(new byte[32]), fence, MonitorAnalysisStatus.Failed, "RAW_PAYLOAD_MARKER TOKEN_MARKER C:\\secret-path", requestedAt.AddMinutes(2)));
        var nullAfterRaw = Assert.Throws<RetentionRevisionFenceRejectedException>(() => store.CompleteRun(run.RunId, run.OperationToken, null, "RAW_PAYLOAD_MARKER TOKEN_MARKER C:\\secret-path", requestedAt.AddMinutes(3)));

        AssertBoundedSafeRejection(wrongRun);
        AssertBoundedSafeRejection(wrongToken);
        AssertBoundedSafeRejection(nullAfterRaw);
        Assert.Equal(before, ReadRawState(temp.DatabasePath, run.RunId));
    }

    [Fact]
    public void StaleFence_RejectsAppendCompleteAndFinishWithoutMutatingRawState()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch;

        var append = BootstrapRawRun(store, requestedAt);
        var complete = BootstrapRawRun(store, requestedAt.AddHours(1));
        var finish = BootstrapRawRun(store, requestedAt.AddHours(2));
        AdvanceCatalogRevision(temp.DatabasePath, append.RunId);
        AdvanceCatalogRevision(temp.DatabasePath, complete.RunId);
        AdvanceCatalogRevision(temp.DatabasePath, finish.RunId);
        var appendBefore = ReadRawState(temp.DatabasePath, append.RunId);
        var completeBefore = ReadRawState(temp.DatabasePath, complete.RunId);
        var finishBefore = ReadRawState(temp.DatabasePath, finish.RunId);

        var appendError = Assert.Throws<RetentionRevisionFenceRejectedException>(() => store.AppendEvent(append.RunId, append.OperationToken, append.Fence, "progress", "RAW_PAYLOAD_MARKER TOKEN_MARKER C:\\secret-path", requestedAt.AddMinutes(2)));
        var completeError = Assert.Throws<RetentionRevisionFenceRejectedException>(() => store.CompleteRun(complete.RunId, complete.OperationToken, complete.Fence, "RAW_PAYLOAD_MARKER TOKEN_MARKER C:\\secret-path", requestedAt.AddHours(1).AddMinutes(2)));
        var finishError = Assert.Throws<RetentionRevisionFenceRejectedException>(() => store.FinishRun(finish.RunId, finish.OperationToken, finish.Fence, MonitorAnalysisStatus.Failed, "RAW_PAYLOAD_MARKER TOKEN_MARKER C:\\secret-path", requestedAt.AddHours(2).AddMinutes(2)));

        AssertBoundedSafeRejection(appendError);
        AssertBoundedSafeRejection(completeError);
        AssertBoundedSafeRejection(finishError);
        Assert.Equal(appendBefore, ReadRawState(temp.DatabasePath, append.RunId));
        Assert.Equal(completeBefore, ReadRawState(temp.DatabasePath, complete.RunId));
        Assert.Equal(finishBefore, ReadRawState(temp.DatabasePath, finish.RunId));
    }

    [Fact]
    public void AppendEvent_MissingOrZeroSourceRowRollsBackBootstrapCatalogItem()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var missing = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, DateTimeOffset.UnixEpoch);
        Execute(temp.DatabasePath, "DELETE FROM monitor_analysis_runs WHERE id = $run_id;", missing.RunId);

        Assert.Throws<InvalidOperationException>(() => store.AppendEvent(missing.RunId, missing.OperationToken, null, "progress", "raw", DateTimeOffset.UnixEpoch));
        Assert.Equal(0, CountRetentionItems(temp.DatabasePath, missing.RunId));

        var zero = store.StartRun("trace-analysis", 43, "span-2", MonitorAnalysisFocus.Errors, DateTimeOffset.UnixEpoch);
        Execute(temp.DatabasePath, "CREATE TRIGGER reject_analysis_event BEFORE INSERT ON monitor_analysis_events WHEN NEW.run_id = " + zero.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " BEGIN SELECT RAISE(IGNORE); END;", zero.RunId);

        Assert.Throws<RetentionRevisionFenceRejectedException>(() => store.AppendEvent(zero.RunId, zero.OperationToken, null, "progress", "raw", DateTimeOffset.UnixEpoch));
        Assert.Equal(0, CountRetentionItems(temp.DatabasePath, zero.RunId));
        Assert.Equal(0, CountEvents(temp.DatabasePath, zero.RunId));
    }

    [Fact]
    public void CompleteRun_PersistsLocalRawResultAndRepositorySafeSummary()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UtcNow;
        var result = store.StartRun(
            traceId: "trace-safe",
            rawRecordId: 7,
            spanId: null,
            focus: MonitorAnalysisFocus.Errors,
            requestedAt: requestedAt);

        var fence = store.AppendEvent(result.RunId, result.OperationToken, null, "progress", "Copilot read raw prompt SECRET_PROMPT_TEXT_MARKER", requestedAt.AddMinutes(1));
        store.CompleteRun(
            result.RunId, result.OperationToken, fence,
            "Copilot raw result mentions SECRET_PROMPT_TEXT_MARKER and leak-marker@example.com",
            requestedAt.AddMinutes(2));

        var run = store.GetRun(result.RunId);
        Assert.NotNull(run);
        var summary = store.GenerateRepositorySafeSummary(result.RunId, requestedAt.AddMinutes(3));

        Assert.Equal(MonitorAnalysisStatus.Succeeded, run.Status);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", System.Text.Json.JsonSerializer.Serialize(run));
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", summary.Markdown);
        Assert.DoesNotContain("leak-marker@example.com", summary.Markdown);
        Assert.Contains("trace-safe", summary.Markdown);
        Assert.Contains("raw record 7", summary.Markdown);
        Assert.Contains("errors", summary.Markdown);
        Assert.Equal(1, CountRetentionItems(temp.DatabasePath, result.RunId));
    }

    [Fact]
    public async Task AnalysisRawReader_ResultErrorAndEventsUseOneConsistentSnapshot()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, requestedAt);
        var fence = store.AppendEvent(run.RunId, run.OperationToken, null, "progress", "first raw event", requestedAt.AddMinutes(1));
        store.CompleteRun(run.RunId, run.OperationToken, fence, "raw result", requestedAt.AddMinutes(2));

        var read = await store.ReadRawSnapshotAsync(run.RunId, CancellationToken.None);

        Assert.Equal(RetentionReadDisposition.Granted, read.Disposition);
        await using var lease = Assert.IsType<RetentionReadLease<AnalysisRunRawSnapshot>>(read.Lease);
        Assert.Equal("raw result", lease.Value.ResultMarkdown);
        Assert.Null(lease.Value.ErrorMessage);
        var entry = Assert.Single(lease.Value.Events);
        Assert.Equal("first raw event", entry.Message);
    }

    [Fact]
    public async Task AnalysisRawReader_EventsAreNotConsumerMutable()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, requestedAt);
        _ = store.AppendEvent(run.RunId, run.OperationToken, null, "progress", "first raw event", requestedAt.AddMinutes(1));

        var read = await store.ReadRawSnapshotAsync(run.RunId, CancellationToken.None);

        Assert.Equal(RetentionReadDisposition.Granted, read.Disposition);
        await using var lease = Assert.IsType<RetentionReadLease<AnalysisRunRawSnapshot>>(read.Lease);
        Assert.False(lease.Value.Events is IList<AnalysisRunRawEvent> { IsReadOnly: false });
        Assert.Equal(new[] { "first raw event" }, lease.Value.Events.Select(@event => @event.Message));
    }

    [Fact]
    public void AnalysisStore_MetadataReadersNeverSelectRawFields()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "CopilotAgentObservability.LocalMonitor", "Analysis", "SqliteMonitorAnalysisStore.cs"));

        var metadataReader = source[source.IndexOf("public MonitorAnalysisRun? GetRun", StringComparison.Ordinal)..source.IndexOf("public void MarkRunning", StringComparison.Ordinal)];
        Assert.DoesNotContain("result_markdown", metadataReader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error_message", metadataReader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("monitor_analysis_events", metadataReader, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalysisRawReader_DeniedMissingOrMismatchedRawPreservesSafeMetadata()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, DateTimeOffset.UnixEpoch.AddMinutes(1));
        _ = store.AppendEvent(run.RunId, run.OperationToken, null, "progress", "raw event", DateTimeOffset.UnixEpoch.AddMinutes(2));

        Execute(temp.DatabasePath, "DELETE FROM retention_items WHERE store_kind = 'analysis_run_raw' AND source_item_id = $run_id;", run.RunId);
        var missing = await store.ReadRawSnapshotAsync(run.RunId, CancellationToken.None);

        Assert.Equal(RetentionReadDisposition.NotFound, missing.Disposition);
        Assert.NotNull(store.GetRun(run.RunId));
        var summaryAfterDenial = store.GenerateRepositorySafeSummary(run.RunId, DateTimeOffset.UnixEpoch.AddMinutes(3));
        Assert.Contains("trace-analysis", summaryAfterDenial.Markdown);
        Assert.DoesNotContain("raw event", summaryAfterDenial.Markdown);

        var mismatchedRun = store.StartRun("trace-analysis", 42, "span-2", MonitorAnalysisFocus.Errors, DateTimeOffset.UnixEpoch.AddMinutes(3));
        _ = store.CompleteRun(mismatchedRun.RunId, mismatchedRun.OperationToken, null, "raw result", DateTimeOffset.UnixEpoch.AddMinutes(4));
        Execute(temp.DatabasePath, "UPDATE retention_items SET ownership_receipt = randomblob(32) WHERE store_kind = 'analysis_run_raw' AND source_item_id = $run_id;", mismatchedRun.RunId);
        var mismatched = await store.ReadRawSnapshotAsync(mismatchedRun.RunId, CancellationToken.None);

        Assert.Equal(RetentionReadDisposition.Denied, mismatched.Disposition);
        Assert.NotNull(store.GetRun(mismatchedRun.RunId));
    }

    [Fact]
    public async Task AnalysisRawReader_ConcurrentAppendOrCompletionCannotProduceMixedSnapshot()
    {
        using var temp = new MonitorTempDirectory();
        var selectorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSelector = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SqliteMonitorAnalysisStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider,
            rawSnapshotSelectorBarrier: async cancellationToken =>
            {
                selectorEntered.SetResult();
                await releaseSelector.Task.WaitAsync(cancellationToken);
            });
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, requestedAt);
        var fence = store.AppendEvent(run.RunId, run.OperationToken, null, "progress", "pre-event", requestedAt.AddMinutes(1));
        fence = store.CompleteRun(run.RunId, run.OperationToken, fence, "pre-result", requestedAt.AddMinutes(2));

        var snapshotTask = store.ReadRawSnapshotAsync(run.RunId, CancellationToken.None).AsTask();
        await selectorEntered.Task;
        var appendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(() =>
        {
            appendStarted.SetResult();
            new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider)
                .AppendEvent(run.RunId, run.OperationToken, fence, "progress", "post-event", requestedAt.AddMinutes(3));
        });
        await appendStarted.Task;
        Assert.False(writer.IsCompleted);
        releaseSelector.SetResult();
        var read = await snapshotTask;
        new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider)
            .AppendEvent(run.RunId, run.OperationToken, fence, "progress", "post-event", requestedAt.AddMinutes(3));
        await writer;

        Assert.Equal(RetentionReadDisposition.Granted, read.Disposition);
        await using var lease = Assert.IsType<RetentionReadLease<AnalysisRunRawSnapshot>>(read.Lease);
        Assert.Equal("pre-result", lease.Value.ResultMarkdown);
        Assert.Null(lease.Value.ErrorMessage);
        Assert.Equal(new[] { "pre-event" }, lease.Value.Events.Select(@event => @event.Message));
    }

    [Fact]
    public async Task AnalysisRawReader_ConcurrentWriterNativeProbeProvesImmediateReadLockAndKeepsSnapshotExact()
    {
        using var temp = new MonitorTempDirectory();
        var selectorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSelector = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new SqliteMonitorAnalysisStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider,
            rawSnapshotSelectorBarrier: async cancellationToken =>
            {
                if (selectorEntered.TrySetResult()) await releaseSelector.Task.WaitAsync(cancellationToken);
            });
        reader.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        var run = reader.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, requestedAt);
        var fence = reader.AppendEvent(run.RunId, run.OperationToken, null, "progress", "pre-event", requestedAt.AddMinutes(1));
        fence = reader.CompleteRun(run.RunId, run.OperationToken, fence, "pre-result", requestedAt.AddMinutes(2));

        var snapshotTask = reader.ReadRawSnapshotAsync(run.RunId, CancellationToken.None).AsTask();
        await selectorEntered.Task;
        var probe = new NativeWriterProbeSentinel();
        var writer = new SqliteMonitorAnalysisStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider,
            beforeRawWriterBegin: connection =>
            {
                Assert.Equal(0, raw.sqlite3_busy_timeout(connection.Handle, 0));
                Assert.Equal((int)raw.SQLITE_BUSY, raw.sqlite3_exec(connection.Handle, "BEGIN IMMEDIATE"));
                Assert.Equal(1, raw.sqlite3_get_autocommit(connection.Handle));
                throw probe;
            });

        Assert.Same(probe, Assert.Throws<NativeWriterProbeSentinel>(() => writer.AppendEvent(run.RunId, run.OperationToken, fence, "progress", "post-event", requestedAt.AddMinutes(3))));
        releaseSelector.SetResult();
        var snapshot = await snapshotTask;

        Assert.Equal(RetentionReadDisposition.Granted, snapshot.Disposition);
        {
            await using var lease = Assert.IsType<RetentionReadLease<AnalysisRunRawSnapshot>>(snapshot.Lease);
            Assert.Equal("pre-result", lease.Value.ResultMarkdown);
            Assert.Equal(new[] { "pre-event" }, lease.Value.Events.Select(@event => @event.Message));
        }

        new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider)
            .AppendEvent(run.RunId, run.OperationToken, fence, "progress", "post-event", requestedAt.AddMinutes(3));
        var later = await reader.ReadRawSnapshotAsync(run.RunId, CancellationToken.None);
        await using var laterLease = Assert.IsType<RetentionReadLease<AnalysisRunRawSnapshot>>(later.Lease);
        Assert.Equal(new[] { "pre-event", "post-event" }, laterLease.Value.Events.Select(@event => @event.Message));
    }

    private static int CountRetentionItems(string databasePath, long runId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_run_raw' AND source_item_id=$run_id;";
        command.Parameters.AddWithValue("$run_id", runId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (long RunId, MonitorAnalysisOperationToken OperationToken, RetentionRevisionFence Fence) BootstrapRawRun(SqliteMonitorAnalysisStore store, DateTimeOffset requestedAt)
    {
        var run = store.StartRun("trace-analysis", 42, "span-1", MonitorAnalysisFocus.Errors, requestedAt);
        var fence = store.AppendEvent(run.RunId, run.OperationToken, null, "progress", "original", requestedAt.AddMinutes(1));
        return (run.RunId, run.OperationToken, fence);
    }

    private static void AdvanceCatalogRevision(string databasePath, long runId) =>
        Execute(databasePath, "UPDATE retention_items SET revision = revision + 1 WHERE store_kind = 'analysis_run_raw' AND source_item_id = $run_id;", runId);

    private static byte[] ReadRetentionOwnerToken(string databasePath, long runId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT retention_owner_token FROM monitor_analysis_runs WHERE id=$run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        return command.ExecuteScalar() as byte[] ?? throw new InvalidOperationException("Retention owner token was not persisted.");
    }

    private static (int EventCount, string? Result, string? Error) ReadRawState(string databasePath, long runId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT COUNT(*) FROM monitor_analysis_events WHERE run_id=$run_id), result_markdown, error_message FROM monitor_analysis_runs WHERE id=$run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static void AssertBoundedSafeRejection(RetentionRevisionFenceRejectedException exception)
    {
        Assert.Equal("The requested analysis raw write is no longer authorized.", exception.Message);
        Assert.True(exception.Message.Length < 128);
        Assert.DoesNotContain("RAW_PAYLOAD_MARKER", exception.Message);
        Assert.DoesNotContain("TOKEN_MARKER", exception.Message);
        Assert.DoesNotContain("C:\\secret-path", exception.Message);
    }

    private static void Execute(string databasePath, string sql, long runId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$run_id", runId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void ExecuteWithoutParameters(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool TableExists(string databasePath, string tableName)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table_name);";
        command.Parameters.AddWithValue("$table_name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }


    private static int CountEvents(string databasePath, long runId)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM monitor_analysis_events WHERE run_id=$run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class NativeWriterProbeSentinel : Exception;
}
