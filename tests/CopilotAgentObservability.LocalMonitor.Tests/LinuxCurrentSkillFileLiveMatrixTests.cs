using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LinuxCurrentSkillFileLiveMatrixTests
{
    [LinuxExt4CurrentFileLiveFact]
    [Trait("Issue158Lane", "LinuxExt4CurrentFile")]
    public Task LinuxExt4CurrentFile_ExercisesNativeMatrix() => Issue158LinuxLane.RunAsync();

    [Fact]
    public void DefaultDiscoveryIsSkipped() => Assert.NotNull(new LinuxExt4CurrentFileLiveFactAttribute().Skip);

    [Fact]
    public async Task BodyGateRejectsBeforeFactory()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Issue158LinuxLane.RunAfterGateAsync(
            () => Issue158LinuxGate.Rejected,
            _ => { calls++; return Task.CompletedTask; }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false, true, true, true, true, true, true)]
    [InlineData(true, false, true, true, true, true, true)]
    [InlineData(true, true, false, true, true, true, true)]
    [InlineData(true, true, true, false, true, true, true)]
    [InlineData(true, true, true, true, false, true, true)]
    [InlineData(true, true, true, true, true, false, true)]
    [InlineData(true, true, true, true, true, true, false)]
    public void PureGateRejectsEveryClosedObservation(bool linux, bool kernel, bool candidate, bool detachedClean, bool checkoutExt4, bool workExt4, bool nativePath)
    {
        Assert.False(Issue158LinuxGate.FromObservations(linux, kernel, candidate, detachedClean, checkoutExt4, workExt4, nativePath).IsAuthorized);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PureGateRequiresCheckoutAndWorkExt4Independently(bool checkoutExt4, bool workExt4) =>
        Assert.False(Issue158LinuxGate.FromObservations(true, true, true, true, checkoutExt4, workExt4, true).IsAuthorized);

    [Fact]
    public void LinuxResultIsStrictAndCarriesPendingCleanup()
    {
        var json = Issue158LinuxResult.Serialize(new(new string('a', 40), 1, 1, 7,
            true, true, true, true, true, true, true, true, true, true, true, true, true, false));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(8, document.RootElement.EnumerateObject().Count());
        Assert.Equal("ext4", document.RootElement.GetProperty("filesystem").GetString());
        Assert.False(document.RootElement.GetProperty("checks").GetProperty("cleanup_complete").GetBoolean());
        Assert.DoesNotContain("/mnt/", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapperUsesArgumentListAndNeverInterpolatesAShellCommand()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "validation", "issue-158", "run-linux-current-file-matrix.ps1"));
        Assert.Contains("$psi.ArgumentList.Add($argument)", script, StringComparison.Ordinal);
        Assert.Contains("@('--distribution','Ubuntu','--exec','bash',$helper,$CandidateSha,$source,$runId,$marker)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("bash -c", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", script, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository_root");
    }
}

public sealed class LinuxExt4CurrentFileLiveFactAttribute : FactAttribute
{
    public LinuxExt4CurrentFileLiveFactAttribute()
    {
        if (!Issue158LinuxGate.ReadProcess().IsAuthorized) Skip = "operator_gate_required";
    }
}

internal sealed record Issue158LinuxGate(bool IsAuthorized, string CandidateSha, string WorkRoot, string ResultFile)
{
    internal const string Authorization = "issue-158-linux-ext4-current-file-v1";
    internal static readonly Issue158LinuxGate Rejected = new(false, string.Empty, string.Empty, string.Empty);

    internal static Issue158LinuxGate FromObservations(bool linux, bool kernel, bool candidate, bool detachedClean,
        bool checkoutExt4, bool workExt4, bool nativePath) =>
        new(linux && kernel && candidate && detachedClean && checkoutExt4 && workExt4 && nativePath,
            string.Empty, string.Empty, string.Empty);

    internal static Issue158LinuxGate ReadProcess()
    {
        static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;
        var candidate = Env("CAO_ISSUE158_CANDIDATE_SHA");
        var repository = Env("CAO_ISSUE158_LINUX_REPOSITORY");
        var workRoot = Env("CAO_ISSUE158_LINUX_WORK_ROOT");
        var resultFile = Env("CAO_ISSUE158_LINUX_RESULT_FILE");
        var runId = Env("CAO_ISSUE158_RUN_ID");
        if (!OperatingSystem.IsLinux() || Env("CAO_ISSUE158_LINUX_AUTHORIZED") != Authorization
            || candidate.Length != 40 || candidate.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || runId.Length != 32 || runId.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || !Path.IsPathFullyQualified(repository) || !Path.IsPathFullyQualified(workRoot)
            || !Path.IsPathFullyQualified(resultFile) || !LinuxNativeFileApisV1.IsSupportedKernel()) return Rejected;
        try
        {
            var repo = ResolveNoAlias(repository);
            var work = ResolveNoAlias(workRoot);
            var top = Directory.GetParent(work)?.FullName ?? string.Empty;
            if (!string.Equals(Path.GetFileName(work), "work", StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(top), $"cao-issue158-linux-{runId}", StringComparison.Ordinal)
                || !string.Equals(repo, Path.Combine(top, "checkout"), StringComparison.Ordinal)
                || !string.Equals(resultFile, Path.Combine(top, "prepared.json"), StringComparison.Ordinal)
                || !VerifyOwner(top, runId, candidate)
                || IsMntC(repo) || IsMntC(work) || !string.Equals(Run("git", ["-C", repo, "rev-parse", "HEAD"]), candidate, StringComparison.Ordinal)
                || RunExit("git", ["-C", repo, "symbolic-ref", "-q", "HEAD"]) == 0
                || Run("git", ["-C", repo, "status", "--porcelain=v1", "--untracked-files=normal"]).Length != 0
                || !string.Equals(Run("findmnt", ["-T", repo, "-n", "-o", "FSTYPE"]), "ext4", StringComparison.Ordinal)
                || !string.Equals(Run("findmnt", ["-T", work, "-n", "-o", "FSTYPE"]), "ext4", StringComparison.Ordinal)) return Rejected;
            return new(true, candidate, work, resultFile);
        }
        catch { return Rejected; }
    }

    private static bool VerifyOwner(string top, string runId, string candidate)
    {
        var path = Path.Combine(top, ".cao-issue-158-linux-owner.json");
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 3
            && root.GetProperty("schema_version").GetString() == "issue-158-linux-owner.v1"
            && root.GetProperty("run_id").GetString() == runId
            && root.GetProperty("candidate_sha").GetString() == candidate;
    }

    private static string ResolveNoAlias(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var current = Path.GetPathRoot(full) ?? throw new InvalidOperationException("operator_gate");
        foreach (var component in full[current.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var info = new DirectoryInfo(current);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("operator_gate");
            current = info.FullName;
        }
        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(current), StringComparison.Ordinal))
            throw new InvalidOperationException("operator_gate");
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static bool IsMntC(string path) => path.Equals("/mnt/c", StringComparison.Ordinal) || path.StartsWith("/mnt/c/", StringComparison.Ordinal);

    private static string Run(string file, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("operator_gate");
        var output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("operator_gate");
        return output.Trim();
    }

    private static int RunExit(string file, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("operator_gate");
        _ = process.StandardOutput.ReadToEnd(); _ = process.StandardError.ReadToEnd(); process.WaitForExit(); return process.ExitCode;
    }
}

internal static class Issue158LinuxLane
{
    internal static Task RunAsync() => RunAfterGateAsync(Issue158LinuxGate.ReadProcess, ExecuteAsync);

    internal static Task RunAfterGateAsync(Func<Issue158LinuxGate> readGate, Func<Issue158LinuxGate, Task> factory)
    {
        var gate = readGate();
        if (!gate.IsAuthorized) throw new InvalidOperationException("operator_gate");
        return factory(gate);
    }

    private static async Task ExecuteAsync(Issue158LinuxGate gate)
    {
        const string body = "---\nname: issue158-linux\ndescription: synthetic\n---\nsynthetic body\n";
        var root = Path.Combine(gate.WorkRoot, "skills");
        var skill = Path.Combine(root, "issue158-linux");
        Directory.CreateDirectory(skill);
        var file = Path.Combine(skill, "SKILL.md");
        await File.WriteAllTextAsync(file, body, new UTF8Encoding(false));
        using var preflight = SkillDiscoveryRootPreflightV1.Run([], [root]);
        if (preflight.Outcome != SkillDiscoveryRootPreflightOutcomeV1.Certified || preflight.RetainedRoots.Count != 1)
            throw new InvalidOperationException("native_preflight");
        var target = new CurrentSkillReadTargetV1(preflight.RetainedRoots[0], ["issue158-linux", "SKILL.md"], "synthetic-revision");
        var reader = new LinuxCurrentSkillFileReaderV1();
        var cases = 0;
        if (reader.Read(target, CancellationToken.None).Outcome != CurrentSkillNativeOutcomeV1.Success) throw new InvalidOperationException("native_matrix"); cases++;
        File.Delete(file); if (reader.Read(target, CancellationToken.None).Outcome != CurrentSkillNativeOutcomeV1.Missing) throw new InvalidOperationException("native_matrix"); cases++;
        await File.WriteAllBytesAsync(file, [0xff, 0xfe]); if (reader.Read(target, CancellationToken.None).Outcome != CurrentSkillNativeOutcomeV1.Binary) throw new InvalidOperationException("native_matrix"); cases++;
        await File.WriteAllBytesAsync(file, new byte[LinuxCurrentSkillFileReaderV1.MaximumBodyBytes + 1]); if (reader.Read(target, CancellationToken.None).Outcome != CurrentSkillNativeOutcomeV1.Oversized) throw new InvalidOperationException("native_matrix"); cases++;
        File.Delete(file); File.CreateSymbolicLink(file, "/dev/null"); if (reader.Read(target, CancellationToken.None).Outcome != CurrentSkillNativeOutcomeV1.Unsafe) throw new InvalidOperationException("native_matrix"); cases++;
        File.Delete(file); await File.WriteAllTextAsync(file, body, new UTF8Encoding(false));
        var raced = new LinuxCurrentSkillFileReaderV1(hooks: new CurrentSkillFileReaderHooksV1 { AfterFinalMetadataCaptured = _ => File.AppendAllText(file, "x") });
        if (raced.Read(target, CancellationToken.None).Outcome != CurrentSkillNativeOutcomeV1.Raced) throw new InvalidOperationException("native_matrix"); cases++;
        await File.WriteAllTextAsync(file, body, new UTF8Encoding(false));
        var routes = await ExerciseRoutesAsync(gate, root, file, body);
        var result = new Issue158LinuxObservedResult(gate.CandidateSha, 1, 1, cases,
            true, true, true, true, true, true, true, true, true, true, routes.Metadata, routes.Historical, routes.Current, false);
        await File.WriteAllTextAsync(gate.ResultFile, Issue158LinuxResult.Serialize(result), new UTF8Encoding(false));
    }

    private static async Task<(bool Metadata, bool Historical, bool Current)> ExerciseRoutesAsync(
        Issue158LinuxGate gate, string skillRoot, string definitionPath, string body)
    {
        var databasePath = Path.Combine(gate.WorkRoot, "monitor.db");
        var time = TimeProvider.System;
        var retention = RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath, time);
        var raw = new RawTelemetryStore(databasePath, retention, time, RawTelemetryStoreConnectionOptions.MonitorWriter);
        raw.CreateMonitorSchema();
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 2,
            source_adapter = "copilot-sdk-stream",
            source_surface = "copilot-sdk",
            native_session_id = "issue158-linux-native",
            source_application_version = "1.0.75",
            adapter_version = SkillInvocationV2TestIdentity.V1075.AdapterVersion,
            normalization_version = SkillInvocationV2TestIdentity.V1075.NormalizationVersion,
            payload_schema = SkillInvocationV2TestIdentity.V1075.PayloadSchema,
            schema_fingerprint = SkillInvocationV2TestIdentity.V1075.SchemaFingerprint,
            events = new[] { new {
                source_event_id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                source_parent_event_id = "bbbbbbbb-bbbb-4bbb-9bbb-bbbbbbbbbbbb",
                type = "skill.invoked", occurred_at = "2026-08-24T00:00:00.0000000+00:00",
                run_native_id = "issue158-linux-run", source_ephemeral = true,
                trace_id = (string?)null, span_id = (string?)null,
                payload = new { name = "issue158-linux", path = definitionPath, content = body, source = "custom", trigger = "user-invoked" }
            } }
        });
        var options = new MonitorOptions(databasePath, "http://127.0.0.1:0", false,
            MonitorOptions.DefaultMaxRequestBodyBytes, SkillDiscoveryDirectories: [skillRoot]);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions { UseUserSecrets = false });
        var registry = app.Services.GetRequiredService<SkillInvocationV2RegistryProviderV1>();
        var facts = SkillInvocationV2IngestRequestFactsV1.Derive(
            SkillInvocationV2Parser.Parse(request, new LinuxRuntimeCapability()));
        var ingest = SkillInvocationV2IngestTransactionV1.Execute(databasePath, facts,
            registry, time, () => true, () => true, CancellationToken.None);
        var identity = ingest.CommittedIdentity ?? throw new InvalidOperationException("route_seed");
        var admission = app.Services.GetRequiredService<CopilotRuntimeAdmissionV1>();
        var client = new LinuxDiscoveryClient(new("issue158-linux", "custom", definitionPath, null, "synthetic", null, true, true));
        _ = admission.PublishReadyTestCandidate(client, SkillInvocationV2TestIdentity.V1075, out _);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
            var route = $"/api/local-monitor/v1/sessions/{identity.SessionId:D}/skill-invocations/{identity.SnapshotId:D}";
            using var metadata = await http.GetAsync(route);
            using var historical = await http.GetAsync(route + "/content");
            using var currentRequest = new HttpRequestMessage(HttpMethod.Post, route + "/current-file-read")
            {
                Content = new StringContent("{\"schema_version\":\"local-skill-current-file-read.request.v1\"}", Encoding.UTF8, "application/json")
            };
            currentRequest.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
            using var current = await http.SendAsync(currentRequest);
            if (!metadata.IsSuccessStatusCode || !historical.IsSuccessStatusCode || !current.IsSuccessStatusCode)
                throw new InvalidOperationException("route_matrix");
            await Issue158RouteDocuments.ValidateMetadataAsync(metadata.Content, identity, "1.0.75", CancellationToken.None);
            await Issue158RouteDocuments.ValidateHistoricalAsync(historical.Content, identity.SnapshotId, body, CancellationToken.None);
            await Issue158RouteDocuments.ValidateCurrentAsync(current.Content, identity.SnapshotId, body, CancellationToken.None);
            return (true, true, true);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class LinuxRuntimeCapability : ISkillInvocationV2RuntimeCapability
    {
        public CertifiedSkillProducerIdentityV1 CertifiedIdentity => SkillInvocationV2TestIdentity.V1075;
    }

    private sealed class LinuxDiscoveryClient(CopilotDiscoveredSkillFactV1 fact) : ICopilotSkillRuntimeClient
    {
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(
            IReadOnlyList<string> projectPaths, IReadOnlyList<string> skillDirectories, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>([fact]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed record Issue158LinuxObservedResult(string CandidateSha, int RetainedRoots, int RetainedSkills, int MatrixCases,
    bool OperatorGate, bool DetachedCleanCandidate, bool KernelSupported, bool NativeExt4, bool RetainedRootReproof,
    bool StrictUtf8Read, bool UnsafePathRejected, bool MissingRejected, bool OversizedRejected, bool BinaryRejected,
    bool MetadataRoute, bool HistoricalRoute, bool CurrentFileRoute, bool CleanupComplete);

internal static class Issue158LinuxResult
{
    internal static string Serialize(Issue158LinuxObservedResult result)
    {
        if (result.RetainedRoots != 1 || result.RetainedSkills != 1 || result.MatrixCases < 6
            || !result.OperatorGate || !result.DetachedCleanCandidate || !result.KernelSupported || !result.NativeExt4
            || !result.RetainedRootReproof || !result.StrictUtf8Read || !result.UnsafePathRejected || !result.MissingRejected
            || !result.OversizedRejected || !result.BinaryRejected || !result.MetadataRoute || !result.HistoricalRoute || !result.CurrentFileRoute)
            throw new InvalidOperationException("result_evidence");
        return JsonSerializer.Serialize(new
        {
            schema_version = "issue-158-live-validation.v1", candidate_sha = result.CandidateSha,
            lane = "linux_ext4_current_file", outcome = "passed", filesystem = "ext4",
            counts = new { retained_roots = result.RetainedRoots, retained_skills = result.RetainedSkills, matrix_cases = result.MatrixCases },
            checks = new { operator_gate = result.OperatorGate, detached_clean_candidate = result.DetachedCleanCandidate, kernel_supported = result.KernelSupported, native_ext4 = result.NativeExt4, retained_root_reproof = result.RetainedRootReproof, strict_utf8_read = result.StrictUtf8Read, unsafe_path_rejected = result.UnsafePathRejected, missing_rejected = result.MissingRejected, oversized_rejected = result.OversizedRejected, binary_rejected = result.BinaryRejected, metadata_route = result.MetadataRoute, historical_route = result.HistoricalRoute, current_file_route = result.CurrentFileRoute, cleanup_complete = result.CleanupComplete },
            exit_code = 0,
        });
    }
}
