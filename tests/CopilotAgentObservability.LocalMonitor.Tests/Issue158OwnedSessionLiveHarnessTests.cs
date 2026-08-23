using System.Text.Json;
using System.Net.Http.Json;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class Issue158OwnedSessionLiveHarnessTests
{
    [WindowsOwnedSessionLiveFact]
    [Trait("Issue158Lane", "WindowsOwnedSession")]
    public async Task WindowsOwnedSession_TraversesTheProductionHost()
    {
        await Issue158WindowsOwnedSessionLane.RunAsync();
    }

    [Fact]
    public void DefaultDiscoveryIsSkipped() =>
        Assert.NotNull(new WindowsOwnedSessionLiveFactAttribute().Skip);

    [Fact]
    public async Task BodyGateRejectsBeforeFactoryConstruction()
    {
        var constructed = 0;
        var gate = Issue158WindowsLaneGate.Read(new Dictionary<string, string?>());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Issue158WindowsOwnedSessionLane.RunAfterGateAsync(() => gate, _ =>
            {
                constructed++;
                return Task.CompletedTask;
            }));
        Assert.Equal(0, constructed);
    }

    [Theory]
    [InlineData("COPILOT_CLI_PATH", "")]
    [InlineData("COPILOT_CLI_PATH", "synthetic")]
    [InlineData("copilot_cli_path", "")]
    [InlineData("CoPiLoT_Cli_PaTh", "synthetic")]
    [InlineData("CAO_ISSUE158_OPERATOR_AUTHORIZED", "wrong")]
    [InlineData("CAO_ISSUE158_CANDIDATE_SHA", "ABC")]
    [InlineData("CAO_ISSUE158_RUN_ID", "bad")]
    [InlineData("CAO_ISSUE158_RESULT_FILE", "other.json")]
    public void InvalidInputsCloseTheGate(string key, string value)
    {
        var values = ValidValues();
        values[key] = value;
        Assert.False(Issue158WindowsLaneGate.Read(values).IsAuthorized);
    }

    [Fact]
    public void ExactInputsOpenThePureGate() =>
        Assert.True(Issue158WindowsLaneGate.Read(ValidValues()).IsAuthorized);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PhysicalGateRejectsUnexpectedRootChild(bool runtime)
    {
        using var fixture = new OwnedGateFixture();
        File.WriteAllText(Path.Combine(runtime ? fixture.Runtime : fixture.Result, "unexpected"), "synthetic");
        Assert.False(fixture.Read().IsAuthorized);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PhysicalGateRejectsLeafJunction(bool runtime)
    {
        using var fixture = new OwnedGateFixture();
        fixture.ReplaceRootWithJunction(runtime);
        Assert.False(fixture.Read().IsAuthorized);
    }

    [Fact]
    public void PhysicalGateRejectsAncestorJunction()
    {
        using var fixture = new OwnedGateFixture();
        fixture.UseAncestorJunctionForRuntime();
        Assert.False(fixture.Read().IsAuthorized);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PhysicalGateRejectsLinkedOwner(bool hardLink)
    {
        using var fixture = new OwnedGateFixture();
        fixture.ReplaceRuntimeOwnerWithLink(hardLink);
        Assert.False(fixture.Read().IsAuthorized);
    }

    [Fact]
    public async Task BoundaryRereadRejectsRootReplacementBeforeConstruction()
    {
        using var fixture = new OwnedGateFixture();
        Assert.True(fixture.Read().IsAuthorized);
        File.WriteAllText(Path.Combine(fixture.Runtime, "replacement"), "synthetic");
        var constructed = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Issue158WindowsOwnedSessionLane.RunAfterGateAsync(fixture.Read, _ => { constructed++; return Task.CompletedTask; }));
        Assert.Equal(0, constructed);
    }

    [Fact]
    public void ResultSerializationIsStrictAndSanitized()
    {
        var json = Issue158WindowsResult.Serialize(ValidObservedResult());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(9, document.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("windows_owned_session", document.RootElement.GetProperty("lane").GetString());
    }

    [Fact]
    public void ResultSerializationRejectsAnUncertifiedSourceVersion()
    {
        var result = ValidObservedResult() with
        {
            Evidence = ValidEvidence() with { SourceApplicationVersion = "1.0.65" },
        };
        Assert.Throws<InvalidOperationException>(() => Issue158WindowsResult.Serialize(result));
    }

    [Fact]
    public void PreparedResultCarriesPendingCleanup()
    {
        var json = Issue158WindowsResult.Serialize(ValidObservedResult() with { CleanupComplete = false });
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("checks").GetProperty("cleanup_complete").GetBoolean());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PostCommitObserverIgnoresEveryNonCommitOutcome(int outcomeValue)
    {
        var calls = 0;
        SkillInvocationV2CommittedObservationV1.Notify(
            new SkillInvocationV2IngestResultV1((SkillInvocationV2IngestOutcomeV1)outcomeValue, false),
            _ => calls++);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PostCommitObserverReportsEachTrueCommitExactlyOnce()
    {
        var session = Guid.CreateVersion7();
        var observed = new List<SkillInvocationV2CommittedIdentityV1>();
        foreach (var snapshot in new[] { Guid.CreateVersion7(), Guid.CreateVersion7() })
            SkillInvocationV2CommittedObservationV1.Notify(
                new SkillInvocationV2IngestResultV1(
                    SkillInvocationV2IngestOutcomeV1.Committed,
                    true,
                    new(session, snapshot)),
                observed.Add);
        Assert.Equal(2, observed.Count);
        Assert.Single(observed.Select(static item => item.SessionId).Distinct());
        Assert.Equal(2, observed.Select(static item => item.SnapshotId).Distinct().Count());
    }

    [Fact]
    public void PostCommitObserverRequiresCommittedIdentityAndAllowsNullObserver()
    {
        var calls = 0;
        SkillInvocationV2CommittedObservationV1.Notify(
            new SkillInvocationV2IngestResultV1(SkillInvocationV2IngestOutcomeV1.Committed, true),
            _ => calls++);
        SkillInvocationV2CommittedObservationV1.Notify(
            new SkillInvocationV2IngestResultV1(
                SkillInvocationV2IngestOutcomeV1.Committed,
                true,
                new(Guid.CreateVersion7(), Guid.CreateVersion7())),
            null);
        Assert.Equal(0, calls);
    }

    private static Dictionary<string, string?> ValidValues() => new(StringComparer.Ordinal)
    {
        ["CAO_ISSUE158_OPERATOR_AUTHORIZED"] = Issue158WindowsLaneGate.Authorization,
        ["CAO_ISSUE158_CANDIDATE_SHA"] = new string('a', 40),
        ["CAO_ISSUE158_RUN_ID"] = new string('b', 32),
        ["CAO_ISSUE158_RUNTIME_DIRECTORY"] = Path.Combine(Path.GetTempPath(), "runtime"),
        ["CAO_ISSUE158_RESULT_DIRECTORY"] = Path.Combine(Path.GetTempPath(), "result"),
        ["CAO_ISSUE158_RESULT_FILE"] = "result.json",
        ["CAO_ISSUE158_RUNTIME_MARKER"] = "synthetic-marker",
    };

    private static OwnedSessionExecutionEvidenceV1 ValidEvidence() => new(
        "1.0.75", 3, 1, 1, 1, 1, 1, 1, 1, 1, 2,
        true, true, true, true, true, true);

    private static Issue158WindowsObservedResult ValidObservedResult() => new(
        new string('a', 40), ValidEvidence(), 1, 1, 1, 2, 2, 2,
        true, true, true, true, true, true, true);

    private sealed class OwnedGateFixture : IDisposable
    {
        private readonly List<string> links = [];
        private readonly List<string> extras = [];
        internal string RunId { get; } = Guid.NewGuid().ToString("N");
        internal string Candidate { get; } = new string('a', 40);
        internal string Runtime { get; private set; }
        internal string Result { get; }
        internal Dictionary<string, string?> Values { get; }

        internal OwnedGateFixture()
        {
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Runtime = Path.Combine(temp, $"cao-issue158-runtime-{RunId}");
            Result = Path.Combine(temp, $"cao-issue158-result-{RunId}");
            Directory.CreateDirectory(Runtime);
            Directory.CreateDirectory(Result);
            WriteOwners();
            Values = ValidValues();
            Values["CAO_ISSUE158_CANDIDATE_SHA"] = Candidate;
            Values["CAO_ISSUE158_RUN_ID"] = RunId;
            Values["CAO_ISSUE158_RUNTIME_DIRECTORY"] = Runtime;
            Values["CAO_ISSUE158_RESULT_DIRECTORY"] = Result;
        }

        internal Issue158WindowsLaneGate Read() => Issue158WindowsLaneGate.Read(Values, verifyOwnedRoots: true);

        internal void ReplaceRootWithJunction(bool runtime)
        {
            var path = runtime ? Runtime : Result;
            var target = path + "-target";
            Directory.Move(path, target);
            Directory.CreateSymbolicLink(path, target);
            links.Add(path);
            extras.Add(target);
        }

        internal void UseAncestorJunctionForRuntime()
        {
            var ancestor = Path.Combine(Path.GetTempPath(), $"cao-issue158-ancestor-{RunId}");
            var target = ancestor + "-target";
            Directory.CreateDirectory(target);
            var nested = Path.Combine(target, Path.GetFileName(Runtime));
            Directory.Move(Runtime, nested);
            Directory.CreateSymbolicLink(ancestor, target);
            Runtime = Path.Combine(ancestor, Path.GetFileName(nested));
            Values["CAO_ISSUE158_RUNTIME_DIRECTORY"] = Runtime;
            links.Add(ancestor);
            extras.Add(target);
        }

        internal void ReplaceRuntimeOwnerWithLink(bool hardLink)
        {
            var owner = Path.Combine(Runtime, ".cao-issue-158-owner.json");
            var target = Path.Combine(Path.GetTempPath(), $"cao-issue158-owner-target-{RunId}.json");
            File.Move(owner, target);
            if (hardLink)
            {
                if (!CreateHardLink(owner, target, IntPtr.Zero)) throw new InvalidOperationException("fixture");
            }
            else File.CreateSymbolicLink(owner, target);
            extras.Add(target);
        }

        private void WriteOwners()
        {
            static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            foreach (var pair in new[] { (Runtime, "runtime"), (Result, "result") })
                File.WriteAllText(Path.Combine(pair.Item1, ".cao-issue-158-owner.json"), JsonSerializer.Serialize(new
                {
                    schema_version = "issue-158-validation-owner.v1", run_id = RunId, candidate_sha = Candidate, kind = pair.Item2,
                    runtime_path_sha256 = Hash(Runtime), result_path_sha256 = Hash(Result),
                }));
        }

        public void Dispose()
        {
            foreach (var link in links.OrderByDescending(static path => path.Length)) if (Directory.Exists(link)) Directory.Delete(link);
            foreach (var path in new[] { Runtime, Result }.Concat(extras).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(static path => path.Length))
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    }
}

public sealed class WindowsOwnedSessionLiveFactAttribute : FactAttribute
{
    public WindowsOwnedSessionLiveFactAttribute()
    {
        if (!Issue158WindowsLaneGate.ReadProcess().IsAuthorized)
        {
            Skip = "operator_gate_required";
        }
    }
}

internal sealed record Issue158WindowsLaneGate(
    bool IsAuthorized,
    string Code,
    string CandidateSha,
    string RunId,
    string RuntimeDirectory,
    string ResultDirectory,
    string ResultFile,
    string RuntimeMarker)
{
    internal const string Authorization = "issue-158-windows-owned-session-v1";

    internal static Issue158WindowsLaneGate ReadProcess()
    {
        var values = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.Ordinal);
        return Read(values, verifyOwnedRoots: true);
    }

    internal static Issue158WindowsLaneGate Read(
        IReadOnlyDictionary<string, string?> values,
        bool verifyOwnedRoots = false)
    {
        static string Get(IReadOnlyDictionary<string, string?> source, string key) =>
            source.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;
        var candidate = Get(values, "CAO_ISSUE158_CANDIDATE_SHA");
        var runId = Get(values, "CAO_ISSUE158_RUN_ID");
        var runtime = Get(values, "CAO_ISSUE158_RUNTIME_DIRECTORY");
        var result = Get(values, "CAO_ISSUE158_RESULT_DIRECTORY");
        var file = Get(values, "CAO_ISSUE158_RESULT_FILE");
        var marker = Get(values, "CAO_ISSUE158_RUNTIME_MARKER");
        var valid = OperatingSystem.IsWindows()
            && Get(values, "CAO_ISSUE158_OPERATOR_AUTHORIZED") == Authorization
            && !values.Keys.Any(static key => string.Equals(key, "COPILOT_CLI_PATH", StringComparison.OrdinalIgnoreCase))
            && candidate.Length == 40 && candidate.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && runId.Length == 32 && runId.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && Path.IsPathFullyQualified(runtime) && Path.IsPathFullyQualified(result)
            && file == "result.json" && !string.IsNullOrWhiteSpace(marker)
            && (!verifyOwnedRoots || VerifyOwnedRoots(runtime, result, runId, candidate));
        return new(valid, valid ? "passed" : "operator_gate", candidate, runId, runtime, result, file, marker);
    }

    private static bool VerifyOwnedRoots(string runtime, string result, string runId, string candidate)
    {
        try
        {
            var temp = ResolveNoReparseDirectory(Path.GetTempPath());
            var runtimeLexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtime));
            var resultLexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(result));
            runtime = ResolveNoReparseDirectory(runtimeLexical);
            result = ResolveNoReparseDirectory(resultLexical);
            if (!string.Equals(runtimeLexical, runtime, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(resultLexical, result, StringComparison.OrdinalIgnoreCase)
                || string.Equals(runtime, result, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(runtime), temp, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(result), temp, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(runtime) != $"cao-issue158-runtime-{runId}"
                || Path.GetFileName(result) != $"cao-issue158-result-{runId}") return false;
            if (!HasOnlyOwner(runtime) || !HasOnlyOwner(result)) return false;
            return VerifyOwner(runtime, "runtime", runId, candidate, runtime, result)
                && VerifyOwner(result, "result", runId, candidate, runtime, result);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveNoReparseDirectory(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException("operator_gate");
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0 || rootInfo.LinkTarget is not null)
            throw new InvalidOperationException("operator_gate");
        var current = root;
        foreach (var component in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]))
        {
            if (component.Length == 0) continue;
            current = Path.Combine(current, component);
            var item = new DirectoryInfo(current);
            if (!item.Exists || (item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null)
                throw new InvalidOperationException("operator_gate");
            current = Path.TrimEndingDirectorySeparator(item.FullName);
        }
        return current;
    }

    private static bool HasOnlyOwner(string directory)
    {
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        return entries.Length == 1
            && string.Equals(Path.GetFileName(entries[0]), ".cao-issue-158-owner.json", StringComparison.Ordinal);
    }

    private static bool VerifyOwner(
        string directory, string kind, string runId, string candidate, string runtime, string result)
    {
        var ownerPath = Path.Combine(directory, ".cao-issue-158-owner.json");
        var attributes = File.GetAttributes(ownerPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
        var owner = new FileInfo(ownerPath);
        if (!owner.Exists || owner.LinkTarget is not null
            || !string.Equals(owner.FullName, Path.GetFullPath(ownerPath), StringComparison.OrdinalIgnoreCase)) return false;
        using (var handle = File.OpenHandle(ownerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (!GetFileInformationByHandle(handle, out var information) || information.NumberOfLinks != 1) return false;
        }
        using var document = JsonDocument.Parse(File.ReadAllBytes(ownerPath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 6) return false;
        static string Hash(string value) => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        return root.GetProperty("schema_version").GetString() == "issue-158-validation-owner.v1"
            && root.GetProperty("run_id").GetString() == runId
            && root.GetProperty("candidate_sha").GetString() == candidate
            && root.GetProperty("kind").GetString() == kind
            && root.GetProperty("runtime_path_sha256").GetString() == Hash(runtime)
            && root.GetProperty("result_path_sha256").GetString() == Hash(result);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }
}

internal static class Issue158WindowsOwnedSessionLane
{
    internal static Task RunAfterGateAsync(Func<Issue158WindowsLaneGate> readCurrentGate, Func<Issue158WindowsLaneGate, Task> factory)
    {
        var gate = readCurrentGate();
        if (!gate.IsAuthorized)
        {
            throw new InvalidOperationException("operator_gate");
        }
        return factory(gate);
    }

    internal static Task RunAsync() => RunAfterGateAsync(Issue158WindowsLaneGate.ReadProcess, ExecuteAsync);

    private static async Task ExecuteAsync(Issue158WindowsLaneGate gate)
    {
        const string skillName = "issue158-synthetic";
        const string traceId = "issue158-synthetic-trace";
        const string skillDocument = "---\nname: issue158-synthetic\ndescription: synthetic validation skill\n---\nReturn the synthetic validation completion.\n";
        var databasePath = Path.Combine(gate.RuntimeDirectory, "monitor.db");
        var analysisDirectory = Path.Combine(gate.RuntimeDirectory, "analysis");
        var skillRoot = Path.Combine(gate.RuntimeDirectory, "skills");
        var skillDirectory = Path.Combine(skillRoot, skillName);
        Directory.CreateDirectory(analysisDirectory);
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), skillDocument);

        var timeProvider = TimeProvider.System;
        var retention = RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath, timeProvider);
        var rawStore = new RawTelemetryStore(databasePath, retention, timeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        rawStore.CreateMonitorSchema();
        var record = new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, traceId, timeProvider.GetUtcNow(), null, "{}");
        var rawRecordId = rawStore.Insert(record);
        rawStore.ApplyProjection(rawRecordId, record.Source, record.ReceivedAt,
            new MonitorRecordProjection(traceId, "synthetic", 0,
                [new MonitorTraceContribution(traceId, "synthetic", null, null, null, null, null, 0, 0, 0, null, null, null)]),
            timeProvider.GetUtcNow());
        var identities = new List<SkillInvocationV2CommittedIdentityV1>();
        var evidence = new List<OwnedSessionExecutionEvidenceV1>();
        var identityLock = new object();
        var options = new MonitorOptions(databasePath, "http://127.0.0.1:0", false,
            MonitorOptions.DefaultMaxRequestBodyBytes, SkillDiscoveryDirectories: [skillRoot]);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions
        {
            TimeProvider = timeProvider,
            UseUserSecrets = false,
            ConfigurationValues = new Dictionary<string, string?>
            {
                ["CopilotAnalysis:Enabled"] = "true",
                ["CopilotAnalysis:BaseDirectory"] = analysisDirectory,
            },
            OwnedSessionExecutionDriver = new ExactSkillCommandExecutionDriverV1(skillName),
            SkillInvocationV2CommittedObserver = identity => { lock (identityLock) identities.Add(identity); },
            OwnedSessionExecutionEvidenceObserver = item => { lock (identityLock) evidence.Add(item); },
        });
        var analysisStore = app.Services.GetRequiredService<IMonitorAnalysisStore>();
        await app.StartAsync();
        Issue158WindowsObservedResult? observed = null;
        var shutdownComplete = false;
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/traces/{traceId}/analysis")
            {
                Content = JsonContent.Create(new { focus = "latency" }),
            };
            request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var start = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            var runId = start.RootElement.GetProperty("run_id").GetInt64();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            MonitorAnalysisRun? run;
            do
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
                run = analysisStore.GetRun(runId);
            } while (run?.Status is MonitorAnalysisStatus.Queued or MonitorAnalysisStatus.Running);
            if (run?.Status != MonitorAnalysisStatus.Succeeded) throw new InvalidOperationException("analysis_terminal");

            OwnedSessionExecutionEvidenceV1[] executionEvidence;
            lock (identityLock) executionEvidence = [.. evidence];
            if (executionEvidence.Length != 1 || !Issue158WindowsResult.IsCertified(executionEvidence[0]))
                throw new InvalidOperationException("execution_evidence");
            var certified = executionEvidence[0];

            SkillInvocationV2CommittedIdentityV1[] committed;
            lock (identityLock) committed = [.. identities];
            if (committed.Length != 2 || committed.Select(static item => item.SessionId).Distinct().Count() != 1
                || committed.Select(static item => item.SnapshotId).Distinct().Count() != 2)
                throw new InvalidOperationException("committed_identity_sequence");
            var identity = committed[0];
            var routeBase = $"/api/local-monitor/v1/sessions/{identity.SessionId:D}/skill-invocations/{identity.SnapshotId:D}";
            using var metadata = await client.GetAsync(routeBase, timeout.Token);
            using var historical = await client.GetAsync(routeBase + "/content", timeout.Token);
            using var currentRequest = new HttpRequestMessage(HttpMethod.Post, routeBase + "/current-file-read")
            {
                Content = JsonContent.Create(new { schema_version = "local-skill-current-file-read.request.v1" }),
            };
            currentRequest.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
            using var current = await client.SendAsync(currentRequest, timeout.Token);
            if (!metadata.IsSuccessStatusCode || !historical.IsSuccessStatusCode || !current.IsSuccessStatusCode)
                throw new InvalidOperationException("route_matrix");
            await Issue158RouteDocuments.ValidateMetadataAsync(metadata.Content, identity, certified.SourceApplicationVersion, timeout.Token);
            await Issue158RouteDocuments.ValidateHistoricalAsync(historical.Content, identity.SnapshotId, skillDocument, timeout.Token);
            await Issue158RouteDocuments.ValidateCurrentAsync(current.Content, identity.SnapshotId, skillDocument, timeout.Token);
            while (Count(databasePath, "skill_invocation_snapshots") != 2
                || Count(databasePath, "monitor_skill_invocations") != 2)
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            var snapshotRows = Count(databasePath, "skill_invocation_snapshots");
            var v1Imported = Count(databasePath, "monitor_skill_invocations");
            var userInvoked = CountEqual(databasePath, "skill_invocation_snapshots", "trigger", "user-invoked");
            var agentInvoked = CountEqual(databasePath, "skill_invocation_snapshots", "trigger", "agent-invoked");
            var taskComplete = CountEqual(databasePath, "session_events", "type", "session.task_complete");
            if (snapshotRows != 2 || v1Imported != 2 || userInvoked != 1 || agentInvoked != 1 || taskComplete != 1)
                throw new InvalidOperationException("aggregate_counts");
            observed = new(gate.CandidateSha, certified, userInvoked, agentInvoked, taskComplete,
                committed.LongLength, v1Imported, snapshotRows, gate.IsAuthorized, true, true, true, true, true, false);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            shutdownComplete = true;
        }
        observed = observed! with { ShutdownDrain = shutdownComplete };
        var resultPath = Path.Combine(gate.ResultDirectory, gate.ResultFile);
        await File.WriteAllTextAsync(resultPath, Issue158WindowsResult.Serialize(observed),
            new System.Text.UTF8Encoding(false));
    }

    private static long Count(string databasePath, string table)
    {
        using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    private static long CountEqual(string databasePath, string table, string column, string expected)
    {
        using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column}=$expected;";
        command.Parameters.AddWithValue("$expected", expected);
        return (long)command.ExecuteScalar()!;
    }
}

internal sealed record Issue158WindowsObservedResult(
    string CandidateSha, OwnedSessionExecutionEvidenceV1 Evidence,
    long UserInvoked, long AgentInvoked, long TaskComplete, long V2Imported, long V1Imported, long SnapshotRows,
    bool OperatorGate, bool CliOverrideAbsent, bool MetadataRoute, bool HistoricalRoute, bool CurrentFileRoute,
    bool ShutdownDrain, bool CleanupComplete);

internal static class Issue158RouteDocuments
{
    private const int MaxBytes = 1_048_576;

    internal static async Task ValidateMetadataAsync(HttpContent content, SkillInvocationV2CommittedIdentityV1 identity,
        string sourceVersion, CancellationToken cancellationToken)
    {
        using var document = await ReadAsync(content, cancellationToken);
        RequireExact(document.RootElement,
            "schema_version", "snapshot_id", "session_id", "claim_id", "event_id", "name", "source", "trigger",
            "invoked_at", "run_id", "trace_id", "span_id", "projection_validity", "snapshot_state", "snapshot_reason",
            "body_sha256", "body_utf8_bytes", "definition_path_sha256", "definition_path_utf8_bytes", "captured_at",
            "source_application_version", "adapter_version", "payload_schema");
        RequireString(document.RootElement, "schema_version", "local-skill-invocation-snapshot.metadata.v1");
        RequireString(document.RootElement, "session_id", identity.SessionId.ToString("D"));
        RequireString(document.RootElement, "snapshot_id", identity.SnapshotId.ToString("D"));
        RequireString(document.RootElement, "projection_validity", "current");
        RequireString(document.RootElement, "snapshot_state", "available");
        RequireString(document.RootElement, "source_application_version", sourceVersion);
    }

    internal static async Task ValidateHistoricalAsync(HttpContent content, Guid snapshotId, string expectedBody,
        CancellationToken cancellationToken)
    {
        using var document = await ReadAsync(content, cancellationToken);
        RequireExact(document.RootElement, "schema_version", "snapshot_id", "content_kind", "body", "definition_path",
            "body_sha256", "definition_path_sha256", "captured_at");
        RequireString(document.RootElement, "schema_version", "local-skill-invocation-snapshot.content.v1");
        RequireString(document.RootElement, "snapshot_id", snapshotId.ToString("D"));
        RequireString(document.RootElement, "content_kind", "historical_snapshot");
        RequireString(document.RootElement, "body", expectedBody);
        RequireHash(document.RootElement, "body_sha256");
        RequireHash(document.RootElement, "definition_path_sha256");
    }

    internal static async Task ValidateCurrentAsync(HttpContent content, Guid snapshotId, string expectedBody,
        CancellationToken cancellationToken)
    {
        using var document = await ReadAsync(content, cancellationToken);
        RequireExact(document.RootElement, "schema_version", "snapshot_id", "content_kind", "comparison",
            "historical_body_sha256", "current_body_sha256", "current_body_utf8_bytes", "body", "read_at");
        RequireString(document.RootElement, "schema_version", "local-skill-current-file-read.response.v1");
        RequireString(document.RootElement, "snapshot_id", snapshotId.ToString("D"));
        RequireString(document.RootElement, "content_kind", "current_file");
        RequireString(document.RootElement, "comparison", "same");
        RequireString(document.RootElement, "body", expectedBody);
        RequireHash(document.RootElement, "historical_body_sha256");
        RequireHash(document.RootElement, "current_body_sha256");
        if (!document.RootElement.GetProperty("current_body_utf8_bytes").TryGetInt64(out var bytes)
            || bytes != Encoding.UTF8.GetByteCount(expectedBody))
            throw new InvalidOperationException("route_document");
    }

    private static async Task<JsonDocument> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            await using var input = await content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (output.Length + read > MaxBytes) throw new InvalidOperationException("route_document");
                output.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("route_document"); }
    }

    private static void RequireExact(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("route_document");
        var actual = element.EnumerateObject().Select(static property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
            || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidOperationException("route_document");
    }

    private static void RequireString(JsonElement element, string property, string expected)
    {
        if (element.GetProperty(property).ValueKind != JsonValueKind.String
            || !string.Equals(element.GetProperty(property).GetString(), expected, StringComparison.Ordinal))
            throw new InvalidOperationException("route_document");
    }

    private static void RequireHash(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        if (value is null || value.Length != 64 || value.Any(static c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)))
            throw new InvalidOperationException("route_document");
    }
}

internal static class Issue158WindowsResult
{
    internal static bool IsCertified(OwnedSessionExecutionEvidenceV1 evidence) =>
        evidence is { SourceApplicationVersion: "1.0.75", ProtocolVersion: 3,
            ClientStartCount: 1, StatusObservationCount: 1, ProbeSessionCount: 1, ExecutionSessionCount: 1,
            RetainedRootCount: 1, RetainedSkillCount: 1, ProbeInventoryCount: 1, ExecutionInventoryCount: 1,
            PreparedInvocationCount: 2, SameClient: true, ExactToolUnion: true, RetainedOnlyInventory: true,
            ProbeNativeReproof: true, ExecutionNativeReproof: true, CallbackNativeReproof: true };

    internal static string Serialize(Issue158WindowsObservedResult result)
    {
        if (!IsCertified(result.Evidence) || result is not
            { UserInvoked: 1, AgentInvoked: 1, TaskComplete: 1, V2Imported: 2, V1Imported: 2, SnapshotRows: 2,
              OperatorGate: true, CliOverrideAbsent: true, MetadataRoute: true, HistoricalRoute: true,
              CurrentFileRoute: true, ShutdownDrain: true })
            throw new InvalidOperationException("result_evidence");
        return JsonSerializer.Serialize(new
        {
        schema_version = "issue-158-live-validation.v1",
        candidate_sha = result.CandidateSha,
        lane = "windows_owned_session",
        outcome = "passed",
        source_application_version = result.Evidence.SourceApplicationVersion,
        protocol_version = result.Evidence.ProtocolVersion,
        counts = new { retained_roots = result.Evidence.RetainedRootCount, retained_skills = result.Evidence.RetainedSkillCount, probe_sessions = result.Evidence.ProbeSessionCount, execution_sessions = result.Evidence.ExecutionSessionCount, user_invoked = result.UserInvoked, agent_invoked = result.AgentInvoked, task_complete = result.TaskComplete, v2_imported = result.V2Imported, v1_imported = result.V1Imported, snapshot_rows = result.SnapshotRows },
        checks = new { operator_gate = result.OperatorGate, cli_override_absent = result.CliOverrideAbsent, retained_only_inventory = result.Evidence.RetainedOnlyInventory, exact_tool_union = result.Evidence.ExactToolUnion, native_reproof = result.Evidence.ProbeNativeReproof && result.Evidence.ExecutionNativeReproof && result.Evidence.CallbackNativeReproof, current_generation = result.CurrentFileRoute, metadata_route = result.MetadataRoute, historical_route = result.HistoricalRoute, current_file_route = result.CurrentFileRoute, shutdown_drain = result.ShutdownDrain, cleanup_complete = result.CleanupComplete },
        exit_code = 0,
        });
    }
}
