using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SqliteEffectComparisonStoreTests
{
    [Fact]
    public void RecordEffectComparison_RejectsDuplicateSessionClassificationBeforePersisting()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var id = Guid.CreateVersion7();

        Assert.Throws<ArgumentException>(() => store.RecordEffectComparison(new(Guid.CreateVersion7(), 1, Guid.CreateVersion7(),
            [new(id, "pre", "case", null), new(id, "post", "case", null)]), DateTimeOffset.UtcNow));
    }
}
