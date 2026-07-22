using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalEvidenceProductionTests
{
    [Fact]
    public async Task HostApplication_CreatesPersistsAndReopensConsumerCompleteDatasetFromSessionStore()
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        const string traceId = "0123456789abcdef0123456789abcdef";
        const string spanId = "0123456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            "owner/repository", "workspace", now, now.AddSeconds(3), now.AddSeconds(3), SessionRawRetentionState.NotCaptured, now, now);
        var run = new ObservedSessionRun(runId, sessionId, SessionSourceSurface.ClaudeCode, null, traceId, null, "gpt-5.4",
            ObservedSessionStatus.Completed, now, now.AddSeconds(3), 10, 20, 30);
        var @event = new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, runId, SessionSourceSurface.ClaudeCode,
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "otel.span", now,
            SessionContentState.NotCaptured, "2.1.215", "adapter.v1");
        store.Write(new(new(session, [], [run], [@event]), []));

        var service = app.Services.GetRequiredService<HistoricalEvidenceApplicationServiceV1>();
        var created = await service.CreateAsync(HistoricalEvidenceSelectionV1.Create(
            repository: "owner/repository", sourceSurfaces: [SessionSourceSurface.ClaudeCode], sanitizedOnly: true), CancellationToken.None);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET repository='mutated-after-extraction' WHERE session_id=$id;";
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var reopened = service.Get(created.RawLocal.ExtractionId);

        Assert.NotNull(reopened);
        var raw = Assert.Single(reopened.RawLocal.Sessions);
        Assert.Equal([SessionSourceSurface.ClaudeCode], raw.Metadata.SourceSurfaces);
        Assert.Equal("2.1.215", Assert.Single(raw.Metadata.SourceProvenance).SourceApplicationVersion);
        Assert.Equal("gpt-5.4", Assert.Single(raw.Metadata.ModelObservations).Model);
        Assert.Equal(3000, Assert.Single(raw.Metadata.DurationObservations).DurationMs);
        Assert.Contains(reopened.RawLocal.EvidenceGroups, group => group.Kind == HistoricalEvidenceGroupKindV1.TokenRollup);
        Assert.StartsWith("repository-ref-", reopened.RepositorySafe.Selection.Repository);
        Assert.StartsWith("repository-ref-", Assert.Single(reopened.RepositorySafe.Sessions).Metadata.Repository);
        Assert.Equal("owner/repository", raw.Metadata.Repository);
    }

    [Fact]
    public async Task SnapshotSource_ReadsDescriptorOnlyAfterRawCapabilityAndGrantedRetentionLease()
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        const string traceId = "1123456789abcdef0123456789abcdef";
        const string spanId = "1123456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            "owner/repository", "workspace", now, now.AddSeconds(1), now.AddSeconds(1), SessionRawRetentionState.Expiring, now, now);
        var @event = new ObservedSessionEvent(eventId, sessionId, null, SessionSourceSurface.ClaudeCode,
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "user.message", now,
            SessionContentState.Available, "2.1.215", "adapter.v1");
        store.Write(new(new(session, [], [], [@event]), []));
        var reader = new RecordingContentReader(new(
            SessionContentReadDisposition.Granted,
            new SessionContentReadLease(
                new SessionEventContent(eventId, "application/json", "{\"text\":\"Use focused tests\"}", now, now.AddDays(1)),
                () => ValueTask.CompletedTask)));
        var source = new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath, store, reader);

        var sanitized = await HistoricalEvidenceExtractorV1.ExtractAsync(
            HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId], sanitizedOnly: true), source, CancellationToken.None);
        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(HistoricalDescriptorStateV1.NotRequested, Assert.Single(sanitized.RawLocal.Sessions).DescriptorState);

        var raw = await HistoricalEvidenceExtractorV1.ExtractAsync(
            HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), source, CancellationToken.None);
        Assert.Equal(1, reader.ReadCount);
        Assert.Equal("Use focused tests", Assert.Single(raw.RawLocal.Sessions).RawLocalDescriptor);
        Assert.Null(Assert.Single(raw.RepositorySafe.Sessions).RawLocalDescriptor);
    }

    [Theory]
    [InlineData((int)SessionContentState.Redacted, (int)SessionRawRetentionState.Expiring)]
    [InlineData((int)SessionContentState.Available, (int)SessionRawRetentionState.NotCaptured)]
    public async Task SnapshotSource_DoesNotTouchContentReaderWithoutAvailableRetainedRawPosture(int contentValue, int retentionValue)
    {
        using var temp = new MonitorTempDirectory();
        using var app = MonitorHost.Build(
            new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", true, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, UseUserSecrets = false });
        var store = app.Services.GetRequiredService<ISessionStore>();
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.CreateVersion7();
        const string traceId = "2123456789abcdef0123456789abcdef";
        const string spanId = "2123456789abcdef";
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full,
            null, null, now, now, now, (SessionRawRetentionState)retentionValue, now, now);
        var @event = new ObservedSessionEvent(Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode,
            null, traceId, "ok", "claude-code-otel", $"{traceId}/{spanId}", "user.message", now,
            (SessionContentState)contentValue);
        store.Write(new(new(session, [], [], [@event]), []));
        var reader = new RecordingContentReader(new(SessionContentReadDisposition.Denied, null));
        var source = new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath, store, reader);

        var extraction = await HistoricalEvidenceExtractorV1.ExtractAsync(
            HistoricalEvidenceSelectionV1.Create(explicitSessionIds: [sessionId]), source, CancellationToken.None);

        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(HistoricalDescriptorStateV1.Unavailable, Assert.Single(extraction.RawLocal.Sessions).DescriptorState);
    }

    private sealed class RecordingContentReader(SessionContentReadResult result) : IHistoricalSessionContentReaderV1
    {
        internal int ReadCount { get; private set; }

        public ValueTask<SessionContentReadResult> ReadContentAsync(Guid sessionId, Guid eventId, CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(result);
        }
    }
}
