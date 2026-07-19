using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class LegacySensitiveBundleDeletionE2ETests
{
    [Fact]
    public async Task AdoptedLegacyChild_ReopenClaimAndDelete_PreservesRequestedParentAndSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), $"legacy-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "catalog.sqlite");
            var now = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var parent = Path.Combine(root, "requested-output");
            Directory.CreateDirectory(Path.Combine(parent, "evidence"));
            var rawSourcePath = Path.Combine(root, "private-source.json");
            File.WriteAllText(Path.Combine(parent, "evidence", "one.json"), $$"""{"schema_version":1,"evidence_ref":"bundle:requested-output:diagcand-0001","diagnosis_candidate_id":"diagcand-0001","trace_id":"trace-private","source_locator":"private-locator","fragments":[{"fragment_id":"fragment-0001","content_kind":"prompt","source_path":"{{rawSourcePath.Replace("\\", "\\\\")}}","sequence":1,"value":"synthetic","sha256":"00"}]}""");
            File.WriteAllText(Path.Combine(parent, "manifest.json"), $$"""{"schema_version":1,"bundle_id":"requested-output","created_at_utc":"{{now:O}}","expires_at_utc":"{{now.AddDays(7):O}}","generated_by_command":"generate-diagnosis-candidates","source_inputs":[{"path":"{{rawSourcePath.Replace("\\", "\\\\")}}","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","kind":"raw-otlp"}],"content_included":true,"delete_target_paths":["{{parent.Replace("\\", "\\\\")}}"],"evidence_index":[{"evidence_ref":"bundle:requested-output:diagcand-0001","diagnosis_candidate_id":"diagcand-0001","trace_id":"trace-private","source_locator":"private-locator","evidence_file":"evidence/one.json","content_kinds":["prompt"],"fragment_count":1}]}""");
            var sibling = Path.Combine(root, "unrelated"); Directory.CreateDirectory(sibling); File.WriteAllText(Path.Combine(sibling, "keep"), "keep");
            var catalog = new RetentionCatalogStore(RetentionCatalogContext.InitializeNewOwnedDatabase(database, new MutableTimeProvider(now)), new MutableTimeProvider(now));
            new RetentionSensitiveBundleStore(catalog).AdoptLegacyBundles(parent);
            var generated = Assert.Single(Directory.EnumerateDirectories(parent));
            Assert.DoesNotContain(rawSourcePath, File.ReadAllText(Path.Combine(generated, "manifest.json")), StringComparison.Ordinal);
            var item = Scalar<string>(database, "SELECT item_id FROM retention_items WHERE store_kind='sensitive_bundle'");
            Execute(database, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            Execute(database, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$id", ("$now", now.ToString("O")), ("$id", item));
            var reopened = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(database), new MutableTimeProvider(now));
            var work = Assert.Single((await reopened.PrepareCleanupBatchAsync(now, 1, 1, TimeSpan.FromSeconds(1), CancellationToken.None)).Work);
            var claim = (await reopened.TryClaimDeletionAsync(work, "e2e", now, CancellationToken.None)).Claim!;
            var intent = await reopened.EnsureDeleteIntentAsync(claim.Fence, 0, now, CancellationToken.None);
            var context = new RetentionDeleteContext(claim.Fence.ItemId, claim.StoreInstanceId, claim.StoreKind, claim.Fence.ExpectedRevision, claim.Fence.LeaseOwner, claim.Fence.LeaseGeneration, claim.SourceIdentity, claim.PrivateLocator, intent.IntentCursor, CancellationToken.None);
            Assert.Same(RetentionAdapterResult.Deleted, await new SensitiveBundleRetentionAdapter(reopened, new MutableTimeProvider(now)).DeleteAsync(context));
            Assert.Equal(RetentionMutationDisposition.Applied, await reopened.TryCompleteDeletionAsync(claim.Fence, now, CancellationToken.None));
            Assert.True(Directory.Exists(parent)); Assert.False(File.Exists(Path.Combine(parent, "manifest.json"))); Assert.Empty(Directory.EnumerateDirectories(parent)); Assert.True(File.Exists(Path.Combine(sibling, "keep")));
            var diagnostics = reopened.LoadSensitiveBundleDeletionPlan(context, now).ToString();
            Assert.DoesNotContain(root, diagnostics, StringComparison.Ordinal); Assert.DoesNotContain("synthetic", diagnostics, StringComparison.Ordinal);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Execute(string db, string sql, params (string Name, object Value)[] values) { using var c = new SqliteConnection($"Data Source={db};Pooling=False"); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var value in values) q.Parameters.AddWithValue(value.Name, value.Value); q.ExecuteNonQuery(); }
    private static T Scalar<T>(string db, string sql) { using var c = new SqliteConnection($"Data Source={db};Pooling=False"); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return (T)q.ExecuteScalar()!; }
}
