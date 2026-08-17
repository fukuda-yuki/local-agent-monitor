using System.Globalization;
using System.Reflection;
using System.Text;
using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.RawReplay;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RetentionRawReplayStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public async Task ReplayAsync_UsesExistingSensitiveBundleCaptureAndOperationReadLease()
    {
        using var fixture = new Fixture();
        var archive = Archive(1, "trace-one");
        var store = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent, fixture.TimeProvider);

        var first = await store.ReplayAsync("replay-one", archive, CancellationToken.None);

        Assert.True(first.Success, first.ErrorCode);
        Assert.False(first.IdempotentReplay);
        Assert.Equal(0, first.Result!.ExternalModelInvocations);
        Assert.Equal(1, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle' AND policy_id='sensitive-bundle-7d' AND policy_version=1;"));
        Assert.Equal("complete", fixture.Scalar<string>("SELECT phase FROM retention_file_capture_reservations;"));
        Assert.Single(Directory.EnumerateDirectories(fixture.BundleParent));

        var retained = await store.ReadAsync("replay-one", CancellationToken.None);
        Assert.Equal(RetainedRawReplayReadDisposition.Granted, retained.Disposition);
        await using var lease = Assert.IsType<RetainedRawReplayLease>(retained.Lease);
        using (var reference = lease.AcquireReceiptReference())
        {
            Assert.Equal(first.Result.ArchiveSha256, reference.Receipt.ArchiveSha256);
            reference.Dispose();
            Assert.Throws<ObjectDisposedException>(() => reference.Receipt);
        }
        Assert.Equal(1, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        await lease.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => lease.AcquireReceiptReference());
        Assert.Equal(0, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Fact]
    public async Task ReadAsync_PinnedCapturePastHistoricalExpiryMaterializesExactReceiptWithinLeaseWithoutLeaksOrMutation()
    {
        using var fixture = new Fixture();
        const string replayId = "replay-pinned-materializer";
        const string rawValue = "raw-pinned-materializer-value";
        var archive = Archive(1, rawValue);
        var writer = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent, fixture.TimeProvider);
        var captured = await writer.ReplayAsync(replayId, archive, CancellationToken.None);
        Assert.True(captured.Success, captured.ErrorCode);
        var captureId = RetentionRawReplayStore.CaptureId(replayId);
        Assert.Equal(1, fixture.Execute(
            "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE store_kind='sensitive_bundle' AND source_item_id=$capture AND state='expiring';",
            ("$capture", captureId)));
        Assert.Equal(Now.AddDays(7).ToString("O", CultureInfo.InvariantCulture), fixture.Scalar<string>(
            "SELECT expires_at FROM retention_items WHERE store_kind='sensitive_bundle' AND source_item_id=$capture;",
            ("$capture", captureId)));
        var beforeCatalog = fixture.BundleCatalogState(captureId);
        var beforeSource = fixture.BundleSourceState(captureId);
        var privateLocator = fixture.Scalar<string>(
            "SELECT private_locator FROM retention_items WHERE store_kind='sensitive_bundle' AND source_item_id=$capture;",
            ("$capture", captureId));
        var sourceToken = fixture.Scalar<byte[]>(
            "SELECT owner_token FROM retention_file_capture_reservations WHERE capture_id=$capture;",
            ("$capture", captureId));
        var readTime = new FixedTimeProvider(Now.AddDays(8));
        var reader = new RetentionRawReplayStore(
            new RetentionCatalogStore(fixture.Context, readTime),
            fixture.BundleParent,
            readTime);

        var retained = await reader.ReadAsync(replayId, CancellationToken.None);

        Assert.Equal(RetainedRawReplayReadDisposition.Granted, retained.Disposition);
        var lease = Assert.IsType<RetainedRawReplayLease>(retained.Lease);
        using (var reference = lease.AcquireReceiptReference())
        {
            Assert.Equal(
                RawReplayJson.SerializeCanonical(captured.Result!),
                RawReplayJson.SerializeCanonical(reference.Receipt));
        Assert.Equal(1, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        AssertExactPinnedRawReplayReadPublicSurface();

        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance;
            var surfaces = new List<string>
            {
                retained.ToString(),
                lease.ToString() ?? string.Empty,
                reference.Receipt.ToString(),
                RawReplayJson.Text(reference.Receipt),
            };
            surfaces.AddRange(typeof(RetainedRawReplayReadResult).GetProperties(publicInstance)
                .Select(property => property.GetValue(retained)?.ToString() ?? string.Empty));
            surfaces.AddRange(typeof(RetainedRawReplayLease).GetProperties(publicInstance)
                .Select(property => property.GetValue(lease)?.ToString() ?? string.Empty));
            var forbidden = ForbiddenRepresentations(
                sourceToken,
                rawValue,
                captureId,
                fixture.Root,
                fixture.BundleParent,
                privateLocator,
                Path.Combine(privateLocator, "manifest.json"),
                Path.Combine(privateLocator, "input", "archive.zip"));
            foreach (var surface in surfaces)
                foreach (var value in forbidden)
                    Assert.DoesNotContain(value, surface, StringComparison.OrdinalIgnoreCase);
        }

        await lease.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => lease.AcquireReceiptReference());

        Assert.Equal(0, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases;"));
        Assert.Equal(beforeCatalog, fixture.BundleCatalogState(captureId));
        var afterSource = fixture.BundleSourceState(captureId);
        Assert.Equal(beforeSource.Keys.ToArray(), afterSource.Keys.ToArray());
        foreach (var member in beforeSource)
            Assert.Equal(member.Value, afterSource[member.Key]);
    }

    [Fact]
    public async Task ReceiptMaterialization_EachAdmissionCapabilityParameterSemanticallyConstrainsTheRow()
    {
        using var fixture = new Fixture();
        const string replayId = "replay-selector-capability";
        var writer = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent, fixture.TimeProvider);
        var captured = await writer.ReplayAsync(replayId, Archive(1, "selector-capability"), CancellationToken.None);
        Assert.True(captured.Success, captured.ErrorCode);
        var captureId = RetentionRawReplayStore.CaptureId(replayId);
        var key = new RetentionOwnershipKey(
            fixture.Catalog.StoreInstanceId,
            RetentionStoreKind.SensitiveBundle,
            captureId);

        var result = await fixture.Catalog.ReadAsync(
            new RetentionReadRequest(key, RetentionReadKind.Operation, Now, ExpectedRevision: null),
            (connection, transaction, grant, _) =>
            {
                using var baselineCommand = connection.CreateCommand();
                RetentionRawReplayStore.ConfigureReceiptMaterializationCommand(
                    baselineCommand,
                    transaction,
                    fixture.Catalog.StoreInstanceId,
                    captureId,
                    grant);
                string baselineLocator;
                using (var baselineReader = baselineCommand.ExecuteReader())
                {
                    Assert.True(baselineReader.Read());
                    baselineLocator = baselineReader.GetString(0);
                    Assert.False(baselineReader.Read());
                }

                var perturbations = new (string ParameterName, Func<object, object> Perturb)[]
                {
                    ("$retention_read_source_token", value =>
                    {
                        var token = Assert.IsType<byte[]>(value).ToArray();
                        token[0] ^= byte.MaxValue;
                        return token;
                    }),
                    ("$retention_read_item_id", value => Assert.IsType<string>(value) + "-other"),
                    ("$retention_read_revision", value => Assert.IsType<long>(value) + 1L),
                    ("$retention_read_lease_kind", _ => "access"),
                    ("$retention_read_lease_owner", value => Assert.IsType<string>(value) + "-other"),
                    ("$retention_read_lease_generation", value => Assert.IsType<long>(value) + 1L),
                    ("$retention_read_lease_expires_at", value => DateTimeOffset.ParseExact(
                        Assert.IsType<string>(value),
                        "O",
                        CultureInfo.InvariantCulture).AddTicks(1).ToString("O", CultureInfo.InvariantCulture)),
                };

                foreach (var (parameterName, perturb) in perturbations)
                {
                    using var perturbedCommand = connection.CreateCommand();
                    RetentionRawReplayStore.ConfigureReceiptMaterializationCommand(
                        perturbedCommand,
                        transaction,
                        fixture.Catalog.StoreInstanceId,
                        captureId,
                        grant);
                    var parameter = Assert.IsType<SqliteParameter>(perturbedCommand.Parameters[parameterName]);
                    Assert.NotNull(parameter.Value);
                    parameter.Value = perturb(parameter.Value!);
                    using var perturbedReader = perturbedCommand.ExecuteReader();
                    Assert.False(perturbedReader.Read(), parameterName);
                }

                return ValueTask.FromResult<string?>(baselineLocator);
            },
            CancellationToken.None);

        Assert.Null(result.Disposition);
        await using (var lease = Assert.IsType<RetentionReadLease<string>>(result.Lease))
        using (var reference = lease.AcquireValueReference())
            Assert.Equal(Path.Combine(fixture.BundleParent, captureId), reference.Value);
        Assert.Equal(0, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases;"));
    }

    [Fact]
    public async Task ReplayAsync_IsDurablyIdempotentAndRejectsSameIdWithDifferentArchive()
    {
        using var fixture = new Fixture();
        var firstStore = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent, fixture.TimeProvider);
        var archive = Archive(1, "trace-one");
        Assert.True((await firstStore.ReplayAsync("replay-stable", archive, CancellationToken.None)).Success);

        var reopened = new RetentionRawReplayStore(fixture.Reopen(), fixture.BundleParent, fixture.TimeProvider);
        var retry = await reopened.ReplayAsync("replay-stable", archive, CancellationToken.None);
        var conflict = await reopened.ReplayAsync("replay-stable", Archive(2, "trace-two"), CancellationToken.None);

        Assert.True(retry.Success, retry.ErrorCode);
        Assert.True(retry.IdempotentReplay);
        Assert.False(conflict.Success);
        Assert.Equal("replay_id_conflict", conflict.ErrorCode);
        Assert.Equal(1, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle';"));
        Assert.Single(Directory.EnumerateDirectories(fixture.BundleParent));
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("input/archive.zip")]
    public async Task ReadAsync_TreatsTransientMemberContentionAsBusyWithoutCatalogMutation(string member)
    {
        using var fixture = new Fixture();
        const string replayId = "replay-locked";
        var store = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent, fixture.TimeProvider);
        Assert.True((await store.ReplayAsync(replayId, Archive(1, "trace-one"), CancellationToken.None)).Success);
        var before = fixture.CatalogState();
        var memberPath = Path.Combine(
            fixture.BundleParent,
            RetentionRawReplayStore.CaptureId(replayId),
            member.Replace('/', Path.DirectorySeparatorChar));

        RetainedRawReplayReadResult contended;
        using (new FileStream(memberPath, FileMode.Open, FileAccess.Read, FileShare.None))
            contended = await store.ReadAsync(replayId, CancellationToken.None);

        Assert.Equal(RetainedRawReplayReadDisposition.Busy, contended.Disposition);
        Assert.Null(contended.Lease);
        Assert.Equal(0, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases;"));
        Assert.Equal(before, fixture.CatalogState());

        var retry = await store.ReadAsync(replayId, CancellationToken.None);
        Assert.Equal(RetainedRawReplayReadDisposition.Granted, retry.Disposition);
        await Assert.IsType<RetainedRawReplayLease>(retry.Lease).DisposeAsync();
        Assert.Equal(0, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases;"));
    }

    [Fact]
    public async Task ReadAsyncZeroesEveryOwnedRawBufferAfterGrantedReceiptMaterialization()
    {
        using var fixture = new Fixture();
        const string replayId = "replay-buffer-lifetime";
        var writer = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent, fixture.TimeProvider);
        Assert.True((await writer.ReplayAsync(replayId, Archive(1, "trace-one"), CancellationToken.None)).Success);
        var ownedBuffers = new List<byte[]>();
        var reader = new RetentionRawReplayStore(
            fixture.Catalog,
            fixture.BundleParent,
            fixture.TimeProvider,
            bytes => ownedBuffers.Add(bytes));

        var result = await reader.ReadAsync(replayId, CancellationToken.None);

        Assert.Equal(RetainedRawReplayReadDisposition.Granted, result.Disposition);
        Assert.NotEmpty(ownedBuffers);
        Assert.All(ownedBuffers, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        await Assert.IsType<RetainedRawReplayLease>(result.Lease).DisposeAsync();
    }

    [Fact]
    public async Task ReadAsyncZeroesOwnedRawBuffersWhenMaterializationThrowsMidPath()
    {
        using var fixture = new Fixture();
        const string replayId = "replay-buffer-exception";
        var writer = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent, fixture.TimeProvider);
        Assert.True((await writer.ReplayAsync(replayId, Archive(1, "trace-one"), CancellationToken.None)).Success);
        var ownedBuffers = new List<byte[]>();
        var reader = new RetentionRawReplayStore(
            fixture.Catalog,
            fixture.BundleParent,
            fixture.TimeProvider,
            bytes =>
            {
                ownedBuffers.Add(bytes);
                throw new IOException("synthetic materialization failure");
            });

        var result = await reader.ReadAsync(replayId, CancellationToken.None);

        Assert.Equal(RetainedRawReplayReadDisposition.Busy, result.Disposition);
        Assert.Null(result.Lease);
        Assert.NotEmpty(ownedBuffers);
        Assert.All(ownedBuffers, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        Assert.Equal(0, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases;"));
    }

    [Fact]
    public async Task ReplayAsyncZeroesEveryOwnedExecutionBufferBeforeReturningTheSafeReceipt()
    {
        using var fixture = new Fixture();
        var ownedBuffers = new List<byte[]>();
        var store = new RetentionRawReplayStore(
            fixture.Catalog,
            fixture.BundleParent,
            fixture.TimeProvider,
            bytes => ownedBuffers.Add(bytes));

        var result = await store.ReplayAsync(
            "replay-execution-buffers",
            Archive(1, "trace-one"),
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Null(result.ResultBytes);
        Assert.Null(result.NormalizedBytes);
        Assert.Null(result.ProjectionBytes);
        Assert.Null(result.DashboardBytes);
        Assert.NotEmpty(ownedBuffers);
        Assert.All(ownedBuffers, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task ReadAsync_TreatsSqliteContentionAsBusyWithoutCatalogMutation()
    {
        using var fixture = new Fixture();
        const string replayId = "replay-db-locked";
        var store = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent, fixture.TimeProvider);
        Assert.True((await store.ReplayAsync(replayId, Archive(1, "trace-one"), CancellationToken.None)).Success);
        var before = fixture.CatalogState();
        using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Pooling = false,
        }.ToString());
        blocker.Open();
        using var command = blocker.CreateCommand();
        command.CommandText = "BEGIN EXCLUSIVE;";
        command.ExecuteNonQuery();
        RetainedRawReplayReadResult contended;
        try
        {
            contended = await store.ReadAsync(replayId, CancellationToken.None);
        }
        finally
        {
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
        }

        Assert.Equal(RetainedRawReplayReadDisposition.Busy, contended.Disposition);
        Assert.Null(contended.Lease);
        Assert.Equal(0, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases;"));
        Assert.Equal(before, fixture.CatalogState());
    }

    [Fact]
    public async Task ReadAsync_ConcurrentDeletionTransitionUsesBusyThenLifecycleDeniedWithoutAuthority()
    {
        using var fixture = new Fixture();
        const string replayId = "replay-delete-race";
        var store = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent, fixture.TimeProvider);
        Assert.True((await store.ReplayAsync(replayId, Archive(1, "trace-one"), CancellationToken.None)).Success);
        using var deletion = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Pooling = false,
        }.ToString());
        deletion.Open();
        using var command = deletion.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        command.ExecuteNonQuery();
        command.CommandText = """
            UPDATE retention_items
            SET state='deletion_queued',read_denied_at=$now,queued_at=$now,revision=revision+1
            WHERE store_kind='sensitive_bundle' AND source_item_id=$capture;
            """;
        command.Parameters.AddWithValue("$now", Now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$capture", RetentionRawReplayStore.CaptureId(replayId));
        Assert.Equal(1, command.ExecuteNonQuery());

        RetainedRawReplayReadResult racing;
        try
        {
            racing = await store.ReadAsync(replayId, CancellationToken.None);
        }
        finally
        {
            command.CommandText = "COMMIT;";
            command.Parameters.Clear();
            command.ExecuteNonQuery();
        }

        Assert.Equal(RetainedRawReplayReadDisposition.Busy, racing.Disposition);
        Assert.Null(racing.Lease);
        var afterCommit = await store.ReadAsync(replayId, CancellationToken.None);
        Assert.Equal(RetainedRawReplayReadDisposition.Denied, afterCommit.Disposition);
        Assert.Null(afterCommit.Lease);
        Assert.Equal(0, fixture.Scalar<long>("SELECT COUNT(*) FROM retention_leases;"));
    }

    [Fact]
    public async Task ReadAsync_MissingCatalogReturnsDeniedWithoutRecreatingIt()
    {
        using var fixture = new Fixture();
        var store = new RetentionRawReplayStore(fixture.Catalog, fixture.BundleParent, fixture.TimeProvider);
        SqliteConnection.ClearAllPools();
        File.Delete(fixture.DatabasePath);

        var result = await store.ReadAsync("replay-missing-db", CancellationToken.None);

        Assert.Equal(RetainedRawReplayReadDisposition.Denied, result.Disposition);
        Assert.Null(result.Lease);
        Assert.False(File.Exists(fixture.DatabasePath));
    }

    private static void AssertExactPinnedRawReplayReadPublicSurface()
    {
        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance;
        const BindingFlags declaredPublicInstance = publicInstance | BindingFlags.DeclaredOnly;
        var resultType = typeof(RetainedRawReplayReadResult);
        var leaseType = typeof(RetainedRawReplayLease);

        Assert.False(resultType.IsVisible);
        Assert.True(resultType.IsSealed);
        var resultProperties = resultType.GetProperties(publicInstance).OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        Assert.Collection(
            resultProperties,
            property =>
            {
                Assert.Equal("Disposition", property.Name);
                Assert.Equal(typeof(RetainedRawReplayReadDisposition), property.PropertyType);
                Assert.True(property.GetMethod!.IsPublic);
                Assert.True(property.SetMethod!.IsPublic);
            },
            property =>
            {
                Assert.Equal("Lease", property.Name);
                Assert.Equal(leaseType, property.PropertyType);
                Assert.True(property.GetMethod!.IsPublic);
                Assert.True(property.SetMethod!.IsPublic);
            });
        Assert.Empty(resultType.GetFields(publicInstance));
        Assert.Empty(resultType.GetEvents(publicInstance));
        Assert.Equal(
            new[] { "<Clone>$", "Deconstruct", "Equals", "Equals", "GetHashCode", "ToString" },
            resultType.GetMethods(declaredPublicInstance)
                .Where(method => !method.IsSpecialName)
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
        var resultConstructor = Assert.Single(resultType.GetConstructors(publicInstance));
        Assert.Equal(
            new[] { typeof(RetainedRawReplayReadDisposition), leaseType },
            resultConstructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(
            new[] { "Disposition", "Lease" },
            resultConstructor.GetParameters().Select(parameter => parameter.Name));

        Assert.False(leaseType.IsVisible);
        Assert.True(leaseType.IsSealed);
        Assert.Empty(leaseType.GetProperties(publicInstance));
        Assert.Empty(leaseType.GetFields(publicInstance));
        Assert.Empty(leaseType.GetEvents(publicInstance));
        var leaseMethod = Assert.Single(
            leaseType.GetMethods(declaredPublicInstance),
            method => !method.IsSpecialName);
        Assert.Equal(nameof(IAsyncDisposable.DisposeAsync), leaseMethod.Name);
        Assert.Equal(typeof(ValueTask), leaseMethod.ReturnType);
        Assert.Empty(leaseMethod.GetParameters());
        Assert.Empty(leaseType.GetConstructors(publicInstance));
    }

    private static IReadOnlyCollection<string> ForbiddenRepresentations(byte[] sourceToken, params string[] sensitiveValues)
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRepresentations(forbidden, sourceToken);
        foreach (var sensitiveValue in sensitiveValues)
        {
            AddRepresentations(forbidden, Encoding.UTF8.GetBytes(sensitiveValue));
            forbidden.Add(sensitiveValue);
            forbidden.Add(sensitiveValue.Replace(Path.DirectorySeparatorChar, '/'));
        }
        return forbidden;
    }

    private static void AddRepresentations(ISet<string> forbidden, byte[] value)
    {
        forbidden.Add(Convert.ToHexString(value));
        var base64 = Convert.ToBase64String(value);
        forbidden.Add(base64);
        forbidden.Add(base64.TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    }

    private static byte[] Archive(long id, string trace)
    {
        var service = new RawReplayArchiveService();
        var snapshot = new RawReplaySnapshot("snapshot", Now, "monitor-v1",
            [new RawReplayRecord(id, "raw-otlp", trace, Now, null,
                $"{{\"resourceSpans\":[{{\"scopeSpans\":[{{\"spans\":[{{\"traceId\":\"{trace}\",\"spanId\":\"span\"}}]}}]}}]}}", 1,
                new("copilot-cli", "1", "otlp-json", "adapter-v1", "schema-v1", new string('a', 64), "supported", "available", "not_applied_raw_capture", RawReplayContractVersions.CredentialScanner))],
            [], ["session_content_not_requested"]);
        var control = new RawReplayExportControl(RawReplayContractVersions.ExportControl, RawReplayContractVersions.BundleProfile, Now,
            new(RawRecordIds: [id]), false, false, null, null);
        var preview = service.Preview(snapshot, control);
        var created = service.Create(snapshot, control with
        {
            PreviewDigest = preview.PreviewDigest,
            Consent = new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase),
        });
        Assert.True(created.Success, created.ErrorCode);
        return created.ArchiveBytes!;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"retained-raw-replay-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            DatabasePath = Path.Combine(Root, "retention.db");
            BundleParent = Path.Combine(Root, "bundles");
            TimeProvider = new FixedTimeProvider(Now);
            Context = RetentionCatalogContext.InitializeNewOwnedDatabase(DatabasePath, TimeProvider);
            Catalog = new RetentionCatalogStore(Context, TimeProvider);
        }

        public string Root { get; }
        public string DatabasePath { get; }
        public string BundleParent { get; }
        public TimeProvider TimeProvider { get; }
        public RetentionCatalogContext Context { get; }
        public RetentionCatalogStore Catalog { get; }
        public RetentionCatalogStore Reopen() => new(RetentionCatalogContext.AdoptExistingCatalogV1(DatabasePath), TimeProvider);

        public IReadOnlyList<string> CatalogState()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT item_id,state,revision,COALESCE(read_denied_at,''),COALESCE(queued_at,''),COALESCE(error_code,'')
                FROM retention_items ORDER BY item_id COLLATE BINARY;
                """;
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
            return rows;
        }

        public IReadOnlyList<string> BundleCatalogState(string captureId)
        {
            var item = "(SELECT item_id FROM retention_items WHERE store_kind='sensitive_bundle' AND source_item_id=$capture)";
            var queries = new[]
            {
                (Table: "retention_items", Sql: "SELECT * FROM retention_items WHERE store_kind='sensitive_bundle' AND source_item_id=$capture ORDER BY rowid;"),
                (Table: "retention_file_capture_reservations", Sql: "SELECT * FROM retention_file_capture_reservations WHERE capture_id=$capture ORDER BY rowid;"),
                (Table: "retention_file_capture_members", Sql: "SELECT * FROM retention_file_capture_members WHERE capture_id=$capture ORDER BY rowid;"),
                (Table: "retention_capture_journal", Sql: $"SELECT * FROM retention_capture_journal WHERE item_id={item} ORDER BY rowid;"),
                (Table: "retention_leases", Sql: $"SELECT * FROM retention_leases WHERE item_id={item} ORDER BY rowid;"),
                (Table: "retention_delete_journal", Sql: $"SELECT * FROM retention_delete_journal WHERE item_id={item} ORDER BY rowid;"),
                (Table: "retention_tombstones", Sql: $"SELECT * FROM retention_tombstones WHERE item_id={item} ORDER BY rowid;"),
            };
            var rows = new List<string>();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open();
            foreach (var query in queries)
            {
                using var command = connection.CreateCommand();
                command.CommandText = query.Sql;
                command.Parameters.AddWithValue("$capture", captureId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add(query.Table + "|" + string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index => SnapshotCell(reader.GetValue(index)))));
            }
            return rows;
        }

        public IReadOnlyDictionary<string, byte[]> BundleSourceState(string captureId)
        {
            var root = Path.Combine(BundleParent, captureId);
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToDictionary(
                    path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                    File.ReadAllBytes,
                    StringComparer.Ordinal);
        }

        public int Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            return command.ExecuteNonQuery();
        }

        public T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            var value = command.ExecuteScalar()!;
            return value is T exact ? exact : (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }

        private static string SnapshotCell(object value) => value switch
        {
            DBNull => "null",
            byte[] bytes => "blob:" + Convert.ToHexString(bytes),
            string text => "text:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)),
            _ => value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture),
        };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
