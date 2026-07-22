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
    public void Start_RejectsPathUriOrCredentialLikeIdentifierWithoutInsert(string unsafeModel)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        store.CreateSchema();
        var request = Request() with { Model = unsafeModel };

        var exception = Assert.Throws<HistoricalInstructionAnalysisValidationException>(
            () => store.Start(request, DateTimeOffset.UnixEpoch));

        Assert.Equal(HistoricalInstructionAnalysisValidationCodeV1.InvalidContract, exception.Code);
        Assert.Equal(0L, Number(temp.DatabasePath,
            "SELECT count(*) FROM historical_instruction_analysis_runs;"));
    }

    [Fact]
    public void TerminalRunCannotBeRewritten()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteHistoricalInstructionAnalysisStoreV1(temp.DatabasePath);
        store.CreateSchema();
        var runId = store.Start(Request(), DateTimeOffset.UnixEpoch);
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
