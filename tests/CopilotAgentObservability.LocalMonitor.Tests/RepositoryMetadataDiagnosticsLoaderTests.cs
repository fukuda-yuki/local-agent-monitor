using CopilotAgentObservability.LocalMonitor.Diagnostics;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RepositoryMetadataDiagnosticsLoaderTests
{
    [Fact]
    public async Task LoadAsync_UsesBoundedAccessReadAndAggregatesOnlySafeDiagnosticFields()
    {
        var store = new DiagnosticStore(
        [
            Raw(2, """
                {"resourceSpans":[{"resource":{"attributes":[
                  {"key":"vcs.repository.name","value":{"stringValue":"private-repository-value"}},
                  {"key":"vcs.owner.name","value":{"stringValue":"private-owner-value"}}
                ]},"scopeSpans":[{"spans":[]}]}]}
                """),
            Raw(1, """
                {"resourceSpans":[{"resource":{"attributes":[
                  {"key":"vcs.repository.url.full","value":{"stringValue":"https://github.com/private-owner/private-fallback-value"}}
                ]},"scopeSpans":[{"spans":[]}]}]}
                """),
        ]);
        var loader = new RepositoryMetadataDiagnosticsLoader(store);

        var snapshot = await loader.LoadAsync(CancellationToken.None);

        Assert.Equal(50, store.CandidateLimit);
        Assert.Equal(1_048_576, store.MaximumPayloadBytes);
        Assert.Equal(4_194_304, store.MaximumTotalPayloadBytes);
        Assert.Equal(RetentionReadKind.Access, store.ReadKind);
        Assert.Equal(new long[] { 2, 1 }, store.ReadIds);
        Assert.Equal(2, snapshot.AnalyzedRecordCount);
        Assert.False(snapshot.Unavailable);
        Assert.Contains(snapshot.StatusRows, row =>
            row.Status == "metadata_present"
            && row.RecordCount == 1
            && row.RepositoryLabelPresent
            && !row.UrlFallbackUsed);
        Assert.Contains(snapshot.StatusRows, row =>
            row.Status == "url_fallback_used"
            && row.RecordCount == 1
            && row.RepositoryLabelPresent
            && row.UrlFallbackUsed);
        Assert.Contains(snapshot.InventoryRows, row =>
            row.Key == "vcs.repository.name"
            && row.Count == 1
            && row.Scope == "resource"
            && row.Classification == "repository");
        Assert.DoesNotContain(
            snapshot.StatusRows.SelectMany(row => new[] { row.Status }),
            value => value.Contains("private", StringComparison.Ordinal));
        Assert.DoesNotContain(
            snapshot.InventoryRows.SelectMany(row => new[] { row.Key, row.Scope, row.Classification }),
            value => value.Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_WhenRetentionDeniesBatch_FailsClosedWithoutAnalyzingBodies()
    {
        var store = new DiagnosticStore([Raw(1, "{}")] )
        {
            ReadDisposition = RetentionReadDisposition.Denied,
        };
        var loader = new RepositoryMetadataDiagnosticsLoader(store);

        var snapshot = await loader.LoadAsync(CancellationToken.None);

        Assert.True(snapshot.Unavailable);
        Assert.Equal(0, snapshot.AnalyzedRecordCount);
        Assert.Empty(snapshot.StatusRows);
        Assert.Empty(snapshot.InventoryRows);
    }

    [Fact]
    public void DiagnosticViewModels_ExposeOnlyKeyInventoryAndFixedStatusFields()
    {
        Assert.Equal(
            new HashSet<string> { "Status", "RecordCount", "RepositoryLabelPresent", "UrlFallbackUsed" },
            typeof(RepositoryMetadataStatusSummary).GetProperties().Select(property => property.Name).ToHashSet());
        Assert.Equal(
            new HashSet<string> { "Key", "Count", "Scope", "Classification" },
            typeof(RepositoryMetadataInventorySummary).GetProperties().Select(property => property.Name).ToHashSet());
        Assert.Equal(
            new HashSet<string> { "AnalyzedRecordCount", "Unavailable", "StatusRows", "InventoryRows" },
            typeof(RepositoryMetadataDiagnosticsSnapshot).GetProperties().Select(property => property.Name).ToHashSet());
    }

    private static RawTelemetryRecord Raw(long id, string payloadJson) =>
        new(
            Id: id,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: null,
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);

    private sealed class DiagnosticStore(IReadOnlyList<RawTelemetryRecord> records) : ProjectionStoreTestDouble
    {
        public int CandidateLimit { get; private set; }
        public int MaximumPayloadBytes { get; private set; }
        public int MaximumTotalPayloadBytes { get; private set; }
        public IReadOnlyList<long>? ReadIds { get; private set; }
        public RetentionReadKind? ReadKind { get; private set; }
        public RetentionReadDisposition ReadDisposition { get; init; } = RetentionReadDisposition.Granted;

        public override IReadOnlyList<long> ListRecentRawRecordIdsForRepositoryMetadataDiagnostics(
            int limit,
            int maxPayloadBytes,
            int maxTotalPayloadBytes)
        {
            CandidateLimit = limit;
            MaximumPayloadBytes = maxPayloadBytes;
            MaximumTotalPayloadBytes = maxTotalPayloadBytes;
            return records.Select(record => record.Id!.Value).ToArray();
        }

        public override ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadRawRecordsAsync(
            IReadOnlyList<long> ids,
            RetentionReadKind readKind,
            CancellationToken cancellationToken)
        {
            ReadIds = ids;
            ReadKind = readKind;
            return ValueTask.FromResult(ReadDisposition == RetentionReadDisposition.Granted
                ? Granted<IReadOnlyList<RawTelemetryRecord>>(records)
                : new RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>(ReadDisposition, null));
        }
    }
}
