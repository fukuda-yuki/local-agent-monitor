using CopilotAgentObservability.LocalMonitor.Analysis;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalInstructionAnalysisPersistenceTests
{
    [Fact]
    public void CreateSchema_CreatesStandaloneV1_AndRestartIsNoOp()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);

        store.CreateSchema();
        var definition = Text(temp.DatabasePath,
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name='historical_instruction_analysis_runs';");
        store.CreateSchema();

        Assert.Equal(1L, Number(temp.DatabasePath,
            "SELECT version FROM schema_version WHERE component='historical_instruction_analysis';"));
        Assert.Equal(definition, Text(temp.DatabasePath,
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name='historical_instruction_analysis_runs';"));
    }

    [Fact]
    public void CreateSchema_RejectsFutureComponentVersionWithoutMutation()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        store.CreateSchema();
        Execute(temp.DatabasePath,
            "UPDATE schema_version SET version=2 WHERE component='historical_instruction_analysis';");
        var before = Text(temp.DatabasePath,
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name='historical_instruction_analysis_runs';");

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(store.CreateSchema);

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
        Assert.Equal(2L, Number(temp.DatabasePath,
            "SELECT version FROM schema_version WHERE component='historical_instruction_analysis';"));
        Assert.Equal(before, Text(temp.DatabasePath,
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name='historical_instruction_analysis_runs';"));
    }

    [Fact]
    public void CreateSchema_RejectsOwnedObjectWithoutOwnerRow_AndRollsBackOwnerStamp()
    {
        using var temp = new MonitorTempDirectory();
        const string malformed = "CREATE TABLE historical_instruction_analysis_runs(run_id INTEGER PRIMARY KEY)";
        Execute(temp.DatabasePath, malformed);
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(store.CreateSchema);

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
        Assert.Equal(0L, Number(temp.DatabasePath,
            "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='schema_version';"));
        Assert.Equal(malformed, Text(temp.DatabasePath,
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name='historical_instruction_analysis_runs';"));
    }

    [Fact]
    public void CreateSchema_RejectsOwnedMalformedTableWithoutMutation()
    {
        using var temp = new MonitorTempDirectory();
        const string malformed = "CREATE TABLE historical_instruction_analysis_runs(run_id INTEGER PRIMARY KEY)";
        Execute(temp.DatabasePath,
            "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);" +
            "INSERT INTO schema_version(component,version) VALUES('historical_instruction_analysis',1);" +
            malformed + ";");
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(store.CreateSchema);

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
        Assert.Equal(1L, Number(temp.DatabasePath,
            "SELECT version FROM schema_version WHERE component='historical_instruction_analysis';"));
        Assert.Equal(malformed, Text(temp.DatabasePath,
            "SELECT sql FROM sqlite_schema WHERE type='table' AND name='historical_instruction_analysis_runs';"));
    }

    [Theory]
    [InlineData("../model")]
    [InlineData("https://provider")]
    [InlineData("model api-key")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("github_pat_abcdefghijklmnopqrstuvwxyz")]
    [InlineData("glpat-abcdefghijklmnopqrstuvwx")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwx")]
    [InlineData("api-key")]
    public void Start_RejectsPathUriOrCredentialLikeIdentifierWithoutInsert(string unsafeIdentifier)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        store.CreateSchema();
        var modelRequest = Request() with { Model = unsafeIdentifier };
        var providerRequest = Request() with { Provider = unsafeIdentifier };

        var modelException = Assert.Throws<HistoricalInstructionAnalysisValidationException>(
            () => store.Start(modelRequest, Projection(), DateTimeOffset.UnixEpoch));
        var providerException = Assert.Throws<HistoricalInstructionAnalysisValidationException>(
            () => store.Start(providerRequest, Projection(), DateTimeOffset.UnixEpoch));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidContract, modelException.Code);
        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidContract, providerException.Code);
        Assert.Equal(0L, Number(temp.DatabasePath,
            "SELECT count(*) FROM historical_instruction_analysis_runs;"));
    }

    [Fact]
    public void Start_AcceptsExactOneHourTimeout_AndRejectsLargerValue()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        store.CreateSchema();

        var accepted = store.Start(Request() with { TimeoutMilliseconds = 3_600_000 }, Projection(), DateTimeOffset.UnixEpoch);
        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
            store.Start(Request() with { TimeoutMilliseconds = 3_600_001 }, Projection(), DateTimeOffset.UnixEpoch));

        Assert.True(accepted > 0);
        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidContract, exception.Code);
        Assert.Equal(1L, Number(temp.DatabasePath, "SELECT count(*) FROM historical_instruction_analysis_runs;"));
    }

    [Fact]
    public void ReadConsumer_AcceptsQueuedRunningAndEveryNonSuccessTerminalState()
    {
        var terminalStates = Enum.GetValues<HistoricalInstructionAnalysisStateV1>()
            .Where(state => state is not (HistoricalInstructionAnalysisStateV1.Queued
                or HistoricalInstructionAnalysisStateV1.Running
                or HistoricalInstructionAnalysisStateV1.Succeeded
                or HistoricalInstructionAnalysisStateV1.ZeroFindings))
            .ToArray();
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        store.CreateSchema();

        var queuedId = store.Start(Request(), Projection(), DateTimeOffset.UnixEpoch);
        Assert.Equal(queuedId, HistoricalInstructionAnalysisReadConsumerV1.Validate(store.Get(queuedId)!.ToRead()));
        store.MarkRunning(queuedId, DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.Equal(queuedId, HistoricalInstructionAnalysisReadConsumerV1.Validate(store.Get(queuedId)!.ToRead()));

        foreach (var state in terminalStates)
        {
            var expectedProjection = ProjectionForState(state);
            var runId = store.Start(Request(), expectedProjection, DateTimeOffset.UnixEpoch);
            store.MarkRunning(runId, DateTimeOffset.UnixEpoch.AddSeconds(1));
            store.Complete(runId, state, null, null, DateTimeOffset.UnixEpoch.AddSeconds(2));
            var read = store.Get(runId)!.ToRead();
            Assert.Equal(runId, HistoricalInstructionAnalysisReadConsumerV1.Validate(read));
            Assert.Equal(expectedProjection.TruncatedBefore, read.DatasetProjection.TruncatedBefore);
            Assert.Equal(expectedProjection.SanitizedOnly, read.DatasetProjection.SanitizedOnly);
            Assert.Equal(expectedProjection.ContentAvailable, read.DatasetProjection.ContentAvailable);
            Assert.Equal(expectedProjection.DatasetDistribution.Completeness, read.DatasetProjection.DatasetDistribution.Completeness);
            Assert.Equal(expectedProjection.DatasetDistribution.SourceKinds, read.DatasetProjection.DatasetDistribution.SourceKinds);
            Assert.Equal(expectedProjection.DatasetDistribution.Capabilities, read.DatasetProjection.DatasetDistribution.Capabilities);
        }
    }

    [Theory]
    [InlineData("content_unavailable")]
    [InlineData("no_eligible_sessions")]
    public void ReadConsumer_RejectsImpossibleTerminalStateProjectionPair(
        string wireState)
    {
        var state = HistoricalInstructionAnalysisStateWireV1.Parse(wireState);
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        store.CreateSchema();
        var runId = store.Start(Request(), Projection(), DateTimeOffset.UnixEpoch);
        store.MarkRunning(runId, DateTimeOffset.UnixEpoch.AddSeconds(1));
        store.Complete(runId, state, null, null, DateTimeOffset.UnixEpoch.AddSeconds(2));

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() => store.Get(runId));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence, exception.Code);
    }

    [Fact]
    public void MarkRunning_RejectsClockRegressionWithoutMutation()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch.AddSeconds(2);
        var runId = store.Start(Request(), Projection(), requestedAt);

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
            store.MarkRunning(runId, requestedAt.AddTicks(-1)));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidTransition, exception.Code);
        var run = store.Get(runId)!;
        Assert.Equal(HistoricalInstructionAnalysisStateV1.Queued, run.State);
        Assert.Null(run.StartedAt);
    }

    [Fact]
    public void Complete_RejectsClockRegressionWithoutMutation()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        store.CreateSchema();
        var requestedAt = DateTimeOffset.UnixEpoch;
        var startedAt = requestedAt.AddSeconds(2);
        var runId = store.Start(Request(), Projection(), requestedAt);
        store.MarkRunning(runId, startedAt);

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(() =>
            store.Complete(runId, HistoricalInstructionAnalysisStateV1.ProviderFailed, null, null,
                startedAt.AddTicks(-1)));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidTransition, exception.Code);
        var run = store.Get(runId)!;
        Assert.Equal(HistoricalInstructionAnalysisStateV1.Running, run.State);
        Assert.Null(run.CompletedAt);
    }

    [Fact]
    public void TerminalRunCannotBeRewritten()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        store.CreateSchema();
        var runId = store.Start(Request(), Projection(), DateTimeOffset.UnixEpoch);
        store.MarkRunning(runId, DateTimeOffset.UnixEpoch.AddSeconds(1));
        store.Complete(runId, HistoricalInstructionAnalysisStateV1.ProviderFailed, null, null,
            DateTimeOffset.UnixEpoch.AddSeconds(2));

        var rerun = Assert.Throws<HistoricalInstructionAnalysisValidationException>(
            () => store.MarkRunning(runId, DateTimeOffset.UnixEpoch.AddSeconds(3)));
        var rewrite = Assert.Throws<HistoricalInstructionAnalysisValidationException>(
            () => store.Complete(runId, HistoricalInstructionAnalysisStateV1.TimedOut, null, null,
                DateTimeOffset.UnixEpoch.AddSeconds(4)));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidTransition, rerun.Code);
        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidTransition, rewrite.Code);
        Assert.Equal(HistoricalInstructionAnalysisStateV1.ProviderFailed, store.Get(runId)!.State);
    }

    private static HistoricalInstructionAnalysisRequestV1 Request() =>
        new(
            HistoricalInstructionAnalysisContractsV1.RequestSchemaVersion,
            "historical-extraction-00000000000000000000000000000000",
            new string('a', 64),
            "gpt-5",
            "copilot",
            new string('b', 64),
            30_000,
            HistoricalInstructionAnalysisContractsV1.PromptTemplateVersion);

    private static HistoricalInstructionAnalysisDatasetProjectionV1 Projection() =>
        new(
            TruncatedBefore: true,
            SanitizedOnly: false,
            ContentAvailable: true,
            new HistoricalEvidenceDistributionV1(
                [new HistoricalDistributionCountV1("full", 1)],
                [new HistoricalDistributionCountV1("live_otel", 1)],
                [new HistoricalDistributionCountV1("turn_rollup", 1)]));

    private static HistoricalInstructionAnalysisDatasetProjectionV1 ProjectionForState(
        HistoricalInstructionAnalysisStateV1 state) => state switch
    {
        HistoricalInstructionAnalysisStateV1.ContentUnavailable =>
            Projection() with { SanitizedOnly = true, ContentAvailable = false },
        HistoricalInstructionAnalysisStateV1.NoEligibleSessions =>
            Projection() with { DatasetDistribution = new HistoricalEvidenceDistributionV1([], [], []) },
        _ => Projection(),
    };

    private static void Execute(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Number(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Text(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        return connection;
    }
}
