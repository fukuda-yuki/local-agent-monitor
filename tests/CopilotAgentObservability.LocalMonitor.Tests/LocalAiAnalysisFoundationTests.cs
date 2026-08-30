using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalAiAnalysisFoundationTests
{
    [Fact]
    public void Schema_InstallsVersionOneAndRejectsPartialOrNewerAuthority()
    {
        using var database = new Database();
        using var connection = database.Open();
        LocalAiAnalysisSchemaV1.Ensure(connection);
        LocalAiAnalysisSchemaV1.Ensure(connection);
        Assert.Equal(1L, Scalar(connection, "SELECT version FROM schema_version WHERE component='local_ai_analysis';"));

        using var partial = new Database();
        using var partialConnection = partial.Open();
        Execute(partialConnection, "CREATE TABLE local_ai_snapshots(id TEXT);");
        Assert.Throws<InvalidOperationException>(() => LocalAiAnalysisSchemaV1.Ensure(partialConnection));

        using var newer = new Database();
        using var newerConnection = newer.Open();
        Execute(newerConnection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('local_ai_analysis',2);");
        Assert.Throws<InvalidOperationException>(() => LocalAiAnalysisSchemaV1.Ensure(newerConnection));
    }

    [Fact]
    public void Snapshot_InsertSameIdentityRequiresByteIdenticalContent()
    {
        using var database = new Database();
        var store = database.Store();
        var snapshot = Snapshot();
        store.InsertSnapshot(snapshot);
        store.InsertSnapshot(snapshot);
        Assert.Throws<InvalidOperationException>(() => store.InsertSnapshot(snapshot with { PayloadCanonicalJson = "{\"changed\":true}"u8.ToArray() }));
    }

    [Fact]
    public void Run_EnforcesTimeoutTransitionsAndTerminalImmutability()
    {
        using var database = new Database();
        var store = database.Store();
        store.InsertSnapshot(Snapshot());
        var run = store.CreateRun(SnapshotId, "session", SessionId, null, null);
        Assert.Equal(60, run.TimeoutSeconds);
        Assert.Throws<InvalidOperationException>(() => store.CreateRun(SnapshotId, "node", SessionId, "node-1", 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.CreateRun(Guid.CreateVersion7().ToString(), "session", SessionId, null, 0));
        Assert.Throws<InvalidOperationException>(() => store.TransitionRun(run.RunId, LocalAiRunStateV1.Succeeded));
        store.TransitionRun(run.RunId, LocalAiRunStateV1.Running);
        store.TransitionRun(run.RunId, LocalAiRunStateV1.ProviderFailed);
        Assert.Throws<InvalidOperationException>(() => store.TransitionRun(run.RunId, LocalAiRunStateV1.Running));
    }

    [Fact]
    public void CompleteSuccess_RequiresValidatedImmutableResultAndResolvesEvidence()
    {
        using var database = new Database();
        var store = database.Store();
        store.InsertSnapshot(Snapshot());
        var run = store.CreateRun(SnapshotId, "session", SessionId, null, 60);
        store.TransitionRun(run.RunId, LocalAiRunStateV1.Running);

        Assert.Equal(LocalAiRunStateV1.InvalidEvidence, store.Complete(run.RunId, Result("missing")));
        var second = store.CreateRun(SnapshotId, "session", SessionId, null, 60);
        store.TransitionRun(second.RunId, LocalAiRunStateV1.Running);
        Assert.Equal(LocalAiRunStateV1.Succeeded, store.Complete(second.RunId, Result("ev-1")));
        Assert.Throws<InvalidOperationException>(() => store.Complete(second.RunId, Result("ev-1")));
    }

    [Fact]
    public void Validator_RejectsSizeDepthUnknownFieldsAndEvidenceRefCardinality()
    {
        Assert.Equal(LocalAiResultValidationCodeV1.TooLarge, LocalAiResultValidatorV1.Validate(new byte[1_048_577], ["ev-1"]).Code);
        var nested = Encoding.UTF8.GetBytes("{\"scope\":{},\"snapshot\":{},\"summary\":\"x\",\"findings\":[],\"improvement_suggestions\":[],\"limitations\":[],\"provenance\":" + new string('[', 17) + "0" + new string(']', 17) + "}");
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidResult, LocalAiResultValidatorV1.Validate(nested, ["ev-1"]).Code);
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidResult, LocalAiResultValidatorV1.Validate(Result("ev-1", rootExtra: ",\"extra\":true"), ["ev-1"]).Code);
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidEvidence, LocalAiResultValidatorV1.Validate(ResultWithRefs([]), ["ev-1"]).Code);
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidEvidence, LocalAiResultValidatorV1.Validate(ResultWithRefs(Enumerable.Repeat("ev-1", 17)), ["ev-1"]).Code);
    }

    [Fact]
    public void History_ReturnsOnlySuccessfulSessionReportsNewestFirstWithBoundedCursorPaging()
    {
        using var database = new Database();
        var store = database.Store();
        store.InsertSnapshot(Snapshot());
        for (var index = 0; index < 105; index++) Complete(store, index == 0 ? LocalAiRunStateV1.ProviderFailed : null);
        var first = store.GetSessionReports(SessionId, null, null);
        Assert.Equal(20, first.Items.Count);
        Assert.True(first.Items.Zip(first.Items.Skip(1), (left, right) => left.CreatedAt > right.CreatedAt).All(value => value));
        Assert.NotNull(first.NextCursor);
        Assert.Equal(100, store.GetSessionReports(SessionId, 1000, null).Items.Count);
        Assert.Equal(84, store.GetSessionReports(SessionId, 100, first.NextCursor).Items.Count);
    }

    [Fact]
    public void ZeroFindingsAndRegeneration_PreserveEverySuccessfulReport()
    {
        using var database = new Database();
        var store = database.Store();
        store.InsertSnapshot(Snapshot());
        Complete(store, null);
        Complete(store, null, zero: true);
        var reports = store.GetSessionReports(SessionId, null, null).Items;
        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, item => item.State == LocalAiRunStateV1.ZeroFindings);
        Assert.Equal(2, reports.Select(item => item.ResultId).Distinct().Count());
    }

    private static void Complete(LocalAiAnalysisStoreV1 store, LocalAiRunStateV1? failure, bool zero = false)
    {
        var run = store.CreateRun(SnapshotId, "session", SessionId, null, 60);
        store.TransitionRun(run.RunId, LocalAiRunStateV1.Running);
        if (failure is { } failed) store.TransitionRun(run.RunId, failed);
        else store.Complete(run.RunId, Result("ev-1", zero));
        Thread.Sleep(1);
    }

    private static LocalAiSnapshotV1 Snapshot() => new(
        SnapshotId, "session", SessionId, null, "anchor-1", "{\"value\":1}"u8.ToArray(), "{\"evidence_refs\":[\"ev-1\"]}"u8.ToArray());

    private static byte[] Result(string evidenceRef, bool zero = false, string rootExtra = "") => Encoding.UTF8.GetBytes(
        "{\"scope\":{},\"snapshot\":{},\"summary\":\"synthetic\",\"findings\":" + (zero ? "[]" : "[{\"finding_id\":\"f-1\",\"title\":\"title\",\"explanation\":\"explanation\",\"evidence_state\":\"supported\",\"evidence_refs\":[\"" + evidenceRef + "\"],\"limitation\":\"none\"}]") + ",\"improvement_suggestions\":[],\"limitations\":[],\"provenance\":{}" + rootExtra + "}");

    private static byte[] ResultWithRefs(IEnumerable<string> refs) => Encoding.UTF8.GetBytes(
        "{\"scope\":{},\"snapshot\":{},\"summary\":\"synthetic\",\"findings\":[{\"finding_id\":\"f-1\",\"title\":\"title\",\"explanation\":\"explanation\",\"evidence_state\":\"supported\",\"evidence_refs\":[" + string.Join(',', refs.Select(value => "\"" + value + "\"")) + "],\"limitation\":\"none\"}],\"improvement_suggestions\":[],\"limitations\":[],\"provenance\":{}}");

    private const string SnapshotId = "0198f5c0-1b89-7d41-8c2f-4ecba0b54410";
    private const string SessionId = "0198f5c0-1b89-7d41-8c2f-4ecba0b54411";

    private static long Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    private static void Execute(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }

    private sealed class Database : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "local-ai-" + Guid.NewGuid().ToString("N"));
        internal Database() { Directory.CreateDirectory(directory); Path = System.IO.Path.Combine(directory, "test.sqlite"); }
        internal string Path { get; }
        internal SqliteConnection Open() { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); connection.Open(); return connection; }
        internal LocalAiAnalysisStoreV1 Store() { using var connection = Open(); LocalAiAnalysisSchemaV1.Ensure(connection); return new(Path); }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
