using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var outputArgument = ParseOutputArgument(args);
if (Path.IsPathRooted(outputArgument))
{
    throw new ArgumentException("--output must be repository-relative so the recorded command remains repository-safe.");
}

var repositoryRoot = Run("git", Environment.CurrentDirectory, "rev-parse", "--show-toplevel").Trim();
var outputDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, outputArgument));
var repositoryPrefix = Path.GetFullPath(repositoryRoot) + Path.DirectorySeparatorChar;
if (!outputDirectory.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase))
{
    throw new ArgumentException("--output must remain inside the repository.");
}

var normalizedOutput = outputArgument.Replace('\\', '/').TrimEnd('/');
var generationCommand = $"dotnet run --project scripts/test/GenerateSessionSchemaFixtures/GenerateSessionSchemaFixtures.csproj -- --output {normalizedOutput}";
var temporaryRoot = Path.Combine(Path.GetTempPath(), $"session-schema-fixtures-{Guid.NewGuid():N}");
var stagedOutput = Path.Combine(temporaryRoot, "output");
Directory.CreateDirectory(stagedOutput);

var specifications = new[]
{
    new FixtureSpecification(1, "ab02e362f05798537e56c50a3048e0fbe3b9bf5a"),
    new FixtureSpecification(2, "b5e02e0f36705eb54881c288b83f875753e11de1"),
    new FixtureSpecification(3, "8d765ad07a46556b84ca32213e86fae28d5998b1"),
    new FixtureSpecification(4, "601c2beb5cb528d1e87aba0fef150b65e1dbccc0"),
    new FixtureSpecification(5, "30d5c8600d0d2abedecdb81944797d7213ef14c9"),
};

var entries = new List<FixtureEntry>();
try
{
    foreach (var specification in specifications)
    {
        var historicalWorktree = Path.Combine(temporaryRoot, $"session-v{specification.Version}");
        var artifactsPath = Path.Combine(temporaryRoot, $"artifacts-v{specification.Version}");
        var fixtureFile = $"session-v{specification.Version}.sqlite";
        var fixturePath = Path.Combine(stagedOutput, fixtureFile);

        Run("git", repositoryRoot, "worktree", "add", "--detach", historicalWorktree, specification.SourceCommit);
        try
        {
            var actualCommit = Run("git", repositoryRoot, "-C", historicalWorktree, "rev-parse", "HEAD").Trim();
            if (!string.Equals(specification.SourceCommit, actualCommit, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Historical worktree resolved to {actualCommit}, expected {specification.SourceCommit}.");
            }

            var statusBefore = Run("git", repositoryRoot, "-C", historicalWorktree, "status", "--porcelain").Trim();
            EnsureClean(specification.Version, "before generation", statusBefore);

            var projectPath = Path.Combine(historicalWorktree, "src", "CopilotAgentObservability.Persistence.Sqlite", "CopilotAgentObservability.Persistence.Sqlite.csproj");
            Run("dotnet", repositoryRoot, "build", projectPath, "--configuration", "Release", "--artifacts-path", artifactsPath, "--nologo");
            var historicalAssembly = FindHistoricalAssembly(artifactsPath);
            var generated = CreateHistoricalFixture(historicalAssembly, fixturePath, specification.Version);
            var sentinels = generated.Sentinels;
            WaitForUnload(generated.LoadContext);
            if (specification.Version == 4)
            {
                InsertVersionFourProposalApplySentinels(fixturePath, sentinels);
            }
            CloseAndVacuum(fixturePath);

            var statusAfter = Run("git", repositoryRoot, "-C", historicalWorktree, "status", "--porcelain").Trim();
            EnsureClean(specification.Version, "after generation", statusAfter);

            var limitations = specification.Version == 4
                ? new[] { "Commit 601c2beb5cb528d1e87aba0fef150b65e1dbccc0 exposes no public proposal-apply persistence API; parameterized INSERTs populate proposal-apply rows only after its public CreateSchema, Write, and CreateImprovementProposal APIs create the schema and parent sentinels." }
                : Array.Empty<string>();
            entries.Add(new FixtureEntry(
                specification.Version,
                fixtureFile,
                specification.SourceCommit,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant(),
                statusBefore,
                statusAfter,
                limitations,
                sentinels));
        }
        finally
        {
            Run("git", repositoryRoot, "worktree", "remove", "--force", historicalWorktree);
        }
    }

    Directory.CreateDirectory(outputDirectory);
    foreach (var entry in entries)
    {
        File.Copy(Path.Combine(stagedOutput, entry.File), Path.Combine(outputDirectory, entry.File), overwrite: true);
    }

    var manifest = new FixtureManifest("session", generationCommand, "git status --porcelain", entries);
    var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    });
    File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"), json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
finally
{
    if (Directory.Exists(temporaryRoot))
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }
}

static string ParseOutputArgument(string[] arguments)
{
    if (arguments.Length != 2 || arguments[0] != "--output" || string.IsNullOrWhiteSpace(arguments[1]))
    {
        throw new ArgumentException("Usage: GenerateSessionSchemaFixtures --output <repository-relative-directory>");
    }
    return arguments[1];
}

static string FindHistoricalAssembly(string artifactsPath)
{
    var candidates = Directory.GetFiles(artifactsPath, "CopilotAgentObservability.Persistence.Sqlite.dll", SearchOption.AllDirectories)
        .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("ref", StringComparer.OrdinalIgnoreCase))
        .Where(path => File.Exists(Path.ChangeExtension(path, ".deps.json")))
        .ToArray();
    return candidates.Length == 1
        ? Path.GetFullPath(candidates[0])
        : throw new InvalidOperationException($"Expected one loadable historical persistence assembly, found {candidates.Length}.");
}

[MethodImpl(MethodImplOptions.NoInlining)]
static (FixtureSentinels Sentinels, WeakReference LoadContext) CreateHistoricalFixture(string assemblyPath, string fixturePath, int version)
{
    var loadContext = new HistoricalLoadContext(assemblyPath);
    try
    {
        var persistenceAssembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var storeType = persistenceAssembly.GetType("CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore", throwOnError: true)!;
        var telemetryAssembly = loadContext.LoadFromAssemblyName(new AssemblyName("CopilotAgentObservability.Telemetry"));
        var store = Activator.CreateInstance(storeType, fixturePath, null)!;
        Invoke(store, "CreateSchema");

        var at = new DateTimeOffset(2026, 7, 12, 0, version, 0, TimeSpan.Zero);
        var sessionId = FixtureGuid(1, version);
        var runId = FixtureGuid(2, version);
        var eventId = FixtureGuid(3, version);
        var proposalId = version >= 3 ? FixtureGuid(4, version) : (Guid?)null;
        var draftId = version >= 4 ? FixtureGuid(5, version) : (Guid?)null;
        var applyId = version >= 4 ? FixtureGuid(6, version) : (Guid?)null;
        var rootId = version >= 4 ? FixtureGuid(7, version) : (Guid?)null;
        var nativeSessionId = $"fixture-session-v{version}-native";
        var projectorKey = $"fixture-session-v{version}-projector";
        var sourceEventId = $"fixture-session-v{version}-event";

        object EnumValue(string name, string value) => Enum.Parse(telemetryAssembly.GetType($"CopilotAgentObservability.Telemetry.Sessions.{name}", true)!, value);
        object Domain(string name, params object?[] values) => Create(telemetryAssembly.GetType($"CopilotAgentObservability.Telemetry.Sessions.{name}", true)!, values);
        Array DomainArray(string name, params object[] values)
        {
            var array = Array.CreateInstance(telemetryAssembly.GetType($"CopilotAgentObservability.Telemetry.Sessions.{name}", true)!, values.Length);
            for (var index = 0; index < values.Length; index++) array.SetValue(values[index], index);
            return array;
        }

        var sourceSurface = EnumValue("SessionSourceSurface", "CopilotSdk");
        var completed = EnumValue("ObservedSessionStatus", "Completed");
        var session = Domain("ObservedSession", sessionId, completed, EnumValue("SessionCompleteness", "Full"), $"fixture/session-v{version}", $"workspace-v{version}", at, at.AddMinutes(1), at.AddMinutes(1), EnumValue("SessionRawRetentionState", "Expiring"), at, at.AddMinutes(1));
        var nativeId = Domain("SessionNativeId", sessionId, sourceSurface, nativeSessionId, EnumValue("SessionBindingKind", "Native"), at);
        var run = Domain("ObservedSessionRun", runId, sessionId, sourceSurface, $"fixture-run-v{version}", $"fixture-trace-v{version}", null, "fixture-model", completed, at, at.AddMinutes(1), 11L + version, 21L + version, 32L + version * 2L);
        var sessionEvent = Domain("ObservedSessionEvent", eventId, sessionId, runId, sourceSurface, null, $"fixture-trace-v{version}", "ok", "fixture-adapter", sourceEventId, "session.task_complete", at.AddSeconds(30), EnumValue("SessionContentState", "Available"));
        var content = Domain("SessionEventContent", eventId, "fixture", $"{{\"fixture\":\"session-v{version}\"}}", at.AddSeconds(30), at.AddDays(90));
        var detail = Domain("SessionDetail", session, DomainArray("SessionNativeId", nativeId), DomainArray("ObservedSessionRun", run), DomainArray("ObservedSessionEvent", sessionEvent));
        var batch = Domain("SessionWriteBatch", detail, DomainArray("SessionEventContent", content));
        Invoke(store, "Write", batch);
        Invoke(store, "UpsertProjectionState", Domain("SessionProjectionState", projectorKey, 1000L + version, 10L + version, at.AddMinutes(1)));

        if (version >= 2)
        {
            Invoke(store, "UpsertHumanEvaluation", Domain("SessionHumanEvaluation", sessionId, "expected", at.AddMinutes(2)));
        }

        if (version >= 3)
        {
            var evidence = Domain("ImprovementProposalEvidenceReference", "event", eventId.ToString("D"));
            var proposal = Domain("ImprovementProposal", proposalId!.Value, EnumValue("ImprovementProposalStatus", "Candidate"), "skill", $"fixture-target-v{version}", $"Fixture proposal v{version}", "Synthetic migration sentinel", "Preserve fixture rows", "none", new[] { sessionId }, DomainArray("ImprovementProposalEvidenceReference", evidence), at.AddMinutes(3), at.AddMinutes(3), null, null);
            Invoke(store, "CreateImprovementProposal", proposal);
        }

        if (version >= 5)
        {
            var draft = Domain("ProposalApplyDraftMetadata", draftId!.Value, proposalId!.Value, rootId!.Value, 1, $"fixture-digest-v{version}", EnumValue("ProposalApplyState", "Draft"), 1, at.AddMinutes(4), at.AddMinutes(4));
            var revision = Domain("ProposalApplyRevisionMetadata", draftId.Value, 1, $"fixture-digest-v{version}", null);
            Invoke(store, "SaveProposalApplyDraft", draft, new[] { ($"fixture-base-v{version}", $"fixture-replacement-v{version}") }, new[] { ($"fixture-hunk-v{version}", true, $"fixture-replacement-v{version}") }, revision);
            var approvedRevision = Domain("ProposalApplyRevisionMetadata", draftId.Value, 1, $"fixture-digest-v{version}", at.AddMinutes(4));
            Invoke(store, "SaveProposalApplyApproval", draftId.Value, approvedRevision);
            var outcome = Domain("ProposalApplyOutcome", applyId!.Value, draftId.Value, EnumValue("ProposalApplyState", "Applied"), at.AddMinutes(5));
            Invoke(store, "SaveProposalApplyOutcome", outcome, proposalId.Value, rootId.Value, 1, null);
        }

        return (new FixtureSentinels(sessionId, nativeSessionId, runId, eventId, sourceEventId, projectorKey, proposalId, draftId, applyId, rootId), new WeakReference(loadContext));
    }
    finally
    {
        loadContext.Unload();
    }
}

static void WaitForUnload(WeakReference loadContext)
{
    for (var attempt = 0; loadContext.IsAlive && attempt < 10; attempt++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
    if (loadContext.IsAlive) throw new InvalidOperationException("Historical assembly load context did not unload.");
}

static object Create(Type type, object?[] values)
{
    var constructors = type.GetConstructors();
    var constructor = constructors.Single(candidate => candidate.GetParameters().Length == values.Length);
    return constructor.Invoke(values);
}

static object? Invoke(object target, string methodName, params object?[] arguments)
{
    var method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Single(candidate => candidate.Name == methodName && candidate.GetParameters().Length == arguments.Length);
    return method.Invoke(target, arguments);
}

static Guid FixtureGuid(int family, int version) => Guid.Parse($"{family:00000000}-0000-7000-8000-{version:000000000000}");

static void InsertVersionFourProposalApplySentinels(string fixturePath, FixtureSentinels sentinels)
{
    var at = new DateTimeOffset(2026, 7, 12, 0, 4, 0, TimeSpan.Zero);
    using var connection = new SqliteConnection($"Data Source={fixturePath};Pooling=False");
    connection.Open();
    using var transaction = connection.BeginTransaction();
    Execute(connection, transaction, "INSERT INTO proposal_apply_drafts(draft_id,proposal_id,root_id,selection_revision,approval_digest,state,created_at) VALUES($draft,$proposal,$root,1,$digest,'applied',$at);", ("$draft", sentinels.DraftId!.Value.ToString("D")), ("$proposal", sentinels.ProposalId!.Value.ToString("D")), ("$root", sentinels.RootId!.Value.ToString("D")), ("$digest", "fixture-digest-v4"), ("$at", at.AddMinutes(4).ToString("O")));
    Execute(connection, transaction, "INSERT INTO proposal_apply_files(draft_id,file_order,base_sha256,replacement_sha256) VALUES($draft,0,$base,$replacement);", ("$draft", sentinels.DraftId.Value.ToString("D")), ("$base", "fixture-base-v4"), ("$replacement", "fixture-replacement-v4"));
    Execute(connection, transaction, "INSERT INTO proposal_apply_hunks(draft_id,hunk_id,selected,replacement_sha256) VALUES($draft,$hunk,1,$replacement);", ("$draft", sentinels.DraftId.Value.ToString("D")), ("$hunk", "fixture-hunk-v4"), ("$replacement", "fixture-replacement-v4"));
    Execute(connection, transaction, "INSERT INTO proposal_apply_revisions(draft_id,selection_revision,approval_digest,approved_at) VALUES($draft,1,$digest,$at);", ("$draft", sentinels.DraftId.Value.ToString("D")), ("$digest", "fixture-digest-v4"), ("$at", at.AddMinutes(4).ToString("O")));
    Execute(connection, transaction, "INSERT INTO proposal_applies(apply_id,draft_id,state,created_at) VALUES($apply,$draft,'applied',$at);", ("$apply", sentinels.ApplyId!.Value.ToString("D")), ("$draft", sentinels.DraftId.Value.ToString("D")), ("$at", at.AddMinutes(5).ToString("O")));
    Execute(connection, transaction, "INSERT INTO proposal_apply_audit(audit_id,apply_id,draft_id,proposal_id,root_id,actor_kind,state,error_code,file_count,recorded_at) VALUES(1,$apply,$draft,$proposal,$root,'local_user','applied',NULL,1,$at);", ("$apply", sentinels.ApplyId.Value.ToString("D")), ("$draft", sentinels.DraftId.Value.ToString("D")), ("$proposal", sentinels.ProposalId.Value.ToString("D")), ("$root", sentinels.RootId.Value.ToString("D")), ("$at", at.AddMinutes(5).ToString("O")));
    transaction.Commit();
}

static void CloseAndVacuum(string fixturePath)
{
    SqliteConnection.ClearAllPools();
    using var connection = new SqliteConnection($"Data Source={fixturePath};Pooling=False");
    connection.Open();
    using (var journal = connection.CreateCommand())
    {
        journal.CommandText = "PRAGMA journal_mode=DELETE;";
        journal.ExecuteNonQuery();
    }
    using (var vacuum = connection.CreateCommand())
    {
        vacuum.CommandText = "VACUUM;";
        vacuum.ExecuteNonQuery();
    }
}

static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
{
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
    command.ExecuteNonQuery();
}

static string Run(string fileName, string workingDirectory, params string[] arguments)
{
    var startInfo = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
    var standardOutput = process.StandardOutput.ReadToEnd();
    var standardError = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{fileName} {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}{standardOutput}{standardError}");
    }
    return standardOutput;
}

static void EnsureClean(int version, string phase, string status)
{
    if (status.Length != 0) throw new InvalidOperationException($"Historical v{version} worktree was not clean {phase}:{Environment.NewLine}{status}");
}

sealed class HistoricalLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var path = resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}

sealed record FixtureSpecification(int Version, string SourceCommit);
sealed record FixtureManifest(string Component, string GenerationCommand, string GitStatusCommand, IReadOnlyList<FixtureEntry> Fixtures);
sealed record FixtureEntry(int Version, string File, string SourceCommit, string Sha256, string GitStatusBefore, string GitStatusAfter, IReadOnlyList<string> Limitations, FixtureSentinels Sentinels);
sealed record FixtureSentinels(Guid SessionId, string NativeSessionId, Guid RunId, Guid EventId, string SourceEventId, string ProjectorKey, Guid? ProposalId, Guid? DraftId, Guid? ApplyId, Guid? RootId);
