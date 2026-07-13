using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

if (args.Length == 4 && args[0] == "--generate-one")
{
    var generated = CreateHistoricalFixture(args[1], args[2], int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
    Console.Write(JsonSerializer.Serialize(generated.Sentinels));
    return;
}

if (args.Length == 4 && args[0] == "--upgrade-one")
{
    UpgradeHistoricalFixture(args[1], args[2], args[3]);
    return;
}

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
try
{
    var stagedOutput = Path.Combine(temporaryRoot, "output");
    Directory.CreateDirectory(stagedOutput);
    var approvedSourceDirectory = Path.Combine(temporaryRoot, "approved-sources");
    Directory.CreateDirectory(approvedSourceDirectory);

    const string versionTenUpgraderCommit = "cf2b15f6c9b18a68aea8dc22f48fcb3177a81346";
    var approvedManifestPath = Path.Combine(outputDirectory, "manifest.json");
    if (!File.Exists(approvedManifestPath))
    {
        throw new InvalidOperationException("The committed Session fixture manifest is required to preserve approved lineage inputs.");
    }
    var approvedManifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(approvedManifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("The committed Session fixture manifest could not be read.");
    var lineageSources = new List<ApprovedLineageSource>();
    foreach (var sourceVersion in new[] { 4, 5, 6 })
    {
        var sourceFile = $"session-v{sourceVersion}.sqlite";
        var sourceEntry = approvedManifest.Fixtures.Single(candidate => candidate.Version == sourceVersion && candidate.File == sourceFile);
        var sourcePath = Path.Combine(outputDirectory, sourceFile);
        if (!File.Exists(sourcePath)) throw new InvalidOperationException($"Approved lineage source is missing: {sourceFile}.");
        var sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))).ToLowerInvariant();
        if (!string.Equals(sourceEntry.Sha256, sourceSha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"Approved lineage source hash changed for {sourceFile}.");
        using (var sourceConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            sourceConnection.Open();
            using var command = sourceConnection.CreateCommand();
            command.CommandText = "SELECT version FROM schema_version WHERE component='session';";
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != sourceVersion)
                throw new InvalidOperationException($"Approved lineage source {sourceFile} is not schema v{sourceVersion}.");
        }
        var approvedCopyPath = Path.Combine(approvedSourceDirectory, sourceFile);
        File.Copy(sourcePath, approvedCopyPath);
        lineageSources.Add(new ApprovedLineageSource(sourceVersion, sourceFile, sourceEntry.SourceCommit, sourceSha256, approvedCopyPath, sourceEntry.Sentinels));
    }

    var specifications = new[]
    {
        new FixtureSpecification(1, "ab02e362f05798537e56c50a3048e0fbe3b9bf5a"),
        new FixtureSpecification(2, "b5e02e0f36705eb54881c288b83f875753e11de1"),
        new FixtureSpecification(3, "8d765ad07a46556b84ca32213e86fae28d5998b1"),
        new FixtureSpecification(4, "601c2beb5cb528d1e87aba0fef150b65e1dbccc0"),
        new FixtureSpecification(5, "30d5c8600d0d2abedecdb81944797d7213ef14c9"),
        new FixtureSpecification(6, "6048da1a50473fdf8701fdb2b787b5e565fec82a"),
        new FixtureSpecification(7, "5a28b87c05c81acecd9121ecf68f5afa2e82deae"),
        new FixtureSpecification(8, "87f4a000932481ac6240b5ec1240318c319efdb5"),
        new FixtureSpecification(9, "e55e2dfb0e306963065759716474385d337b17f6"),
        new FixtureSpecification(10, "cf2b15f6c9b18a68aea8dc22f48fcb3177a81346"),
    };

    var entries = new List<FixtureEntry>();
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
            var generatorAssembly = Assembly.GetExecutingAssembly().Location;
            var generatedJson = Run("dotnet", repositoryRoot, generatorAssembly, "--generate-one", historicalAssembly, fixturePath, specification.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var sentinels = JsonSerializer.Deserialize<FixtureSentinels>(generatedJson)
                ?? throw new InvalidOperationException($"Historical v{specification.Version} generator did not return sentinels.");
            if (specification.Version >= 9)
            {
                var deterministicComparisonId = FixtureGuid(12, specification.Version);
                NormalizeComparisonId(fixturePath, sentinels.ComparisonId!.Value, deterministicComparisonId);
                sentinels = sentinels with { ComparisonId = deterministicComparisonId };
            }
            if (specification.Version == 4)
            {
                InsertVersionFourProposalApplySentinels(fixturePath, sentinels);
            }
            CloseAndVacuum(fixturePath);

            var statusAfter = Run("git", repositoryRoot, "-C", historicalWorktree, "status", "--porcelain").Trim();
            EnsureClean(specification.Version, "after generation", statusAfter);

            var limitations = specification.Version switch
            {
                4 => new[] { "Commit 601c2beb5cb528d1e87aba0fef150b65e1dbccc0 exposes no public proposal-apply persistence API; parameterized INSERTs populate proposal-apply rows only after its public CreateSchema, Write, and CreateImprovementProposal APIs create the schema and parent sentinels." },
                >= 9 => new[] { $"Commit {specification.SourceCommit} exposes RecordEffectComparison but no public comparison-ID input; after that public API persists the complete comparison graph, parameterized UPDATEs replace only its generated opaque comparison ID with the deterministic fixture sentinel so SHA-256 reproduction remains exact." },
                _ => Array.Empty<string>(),
            };
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

    var upgraderWorktree = Path.Combine(temporaryRoot, "session-v10-upgrader");
    var upgraderArtifacts = Path.Combine(temporaryRoot, "artifacts-v10-upgrader");
    Run("git", repositoryRoot, "worktree", "add", "--detach", upgraderWorktree, versionTenUpgraderCommit);
    try
    {
        var actualCommit = Run("git", repositoryRoot, "-C", upgraderWorktree, "rev-parse", "HEAD").Trim();
        if (!string.Equals(versionTenUpgraderCommit, actualCommit, StringComparison.Ordinal))
            throw new InvalidOperationException($"Historical upgrader worktree resolved to {actualCommit}, expected {versionTenUpgraderCommit}.");

        var statusBefore = Run("git", repositoryRoot, "-C", upgraderWorktree, "status", "--porcelain").Trim();
        EnsureClean(10, "before lineage upgrade", statusBefore);
        var upgraderProject = Path.Combine(upgraderWorktree, "src", "CopilotAgentObservability.Persistence.Sqlite", "CopilotAgentObservability.Persistence.Sqlite.csproj");
        Run("dotnet", repositoryRoot, "build", upgraderProject, "--configuration", "Release", "--artifacts-path", upgraderArtifacts, "--nologo");
        var upgraderAssembly = FindHistoricalAssembly(upgraderArtifacts);
        var generatorAssembly = Assembly.GetExecutingAssembly().Location;

        foreach (var source in lineageSources)
        {
            var fixtureFile = $"session-v10-from-v{source.Version}.sqlite";
            var fixturePath = Path.Combine(stagedOutput, fixtureFile);
            Run("dotnet", repositoryRoot, generatorAssembly, "--upgrade-one", upgraderAssembly, source.ApprovedCopyPath, fixturePath);
            CloseAndVacuum(fixturePath);
            EnsureNoSqliteSidecars(fixturePath);

            var statusAfter = Run("git", repositoryRoot, "-C", upgraderWorktree, "status", "--porcelain").Trim();
            EnsureClean(10, $"after v{source.Version} lineage upgrade", statusAfter);
            entries.Add(new FixtureEntry(
                10,
                fixtureFile,
                source.SourceCommit,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant(),
                statusBefore,
                statusAfter,
                Array.Empty<string>(),
                source.Sentinels,
                new LineageProvenance(
                    source.Version,
                    source.File,
                    source.Sha256,
                    versionTenUpgraderCommit,
                    new[]
                    {
                        $"git worktree add --detach {{upgraderWorktree}} {versionTenUpgraderCommit}",
                        "dotnet build {upgraderWorktree}/src/CopilotAgentObservability.Persistence.Sqlite/CopilotAgentObservability.Persistence.Sqlite.csproj --configuration Release --artifacts-path {upgraderArtifacts} --nologo",
                        $"dotnet {{generatorAssembly}} --upgrade-one {{upgraderAssembly}} {source.File} {fixtureFile}",
                    })));
        }
    }
    finally
    {
        Run("git", repositoryRoot, "worktree", "remove", "--force", upgraderWorktree);
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
static void UpgradeHistoricalFixture(string assemblyPath, string sourceFixturePath, string fixturePath)
{
    File.Copy(sourceFixturePath, fixturePath, overwrite: true);
    var loadContext = new HistoricalLoadContext(assemblyPath);
    try
    {
        var persistenceAssembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var storeType = persistenceAssembly.GetType("CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore", throwOnError: true)!;
        var store = Activator.CreateInstance(storeType, fixturePath, null)!;
        Invoke(store, "CreateSchema");
    }
    finally
    {
        loadContext.Unload();
    }
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
        var objectiveEvaluationId = version >= 8 ? FixtureGuid(8, version) : (Guid?)null;
        var secondarySessionId = version >= 9 ? FixtureGuid(9, version) : (Guid?)null;
        var secondaryRunId = version >= 9 ? FixtureGuid(10, version) : (Guid?)null;
        var secondaryEventId = version >= 9 ? FixtureGuid(11, version) : (Guid?)null;
        Guid? comparisonId = null;
        var nativeSessionId = $"fixture-session-v{version}-native";
        var secondaryNativeSessionId = version >= 9 ? $"fixture-session-v{version}-secondary-native" : null;
        var projectorKey = $"fixture-session-v{version}-projector";
        var sourceEventId = $"fixture-session-v{version}-event";
        var secondarySourceEventId = version >= 9 ? $"fixture-session-v{version}-secondary-event" : null;

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

        if (version >= 9)
        {
            var secondaryAt = at.AddMinutes(10);
            var secondarySession = Domain("ObservedSession", secondarySessionId!.Value, completed, EnumValue("SessionCompleteness", "Full"), $"fixture/session-v{version}/secondary", $"workspace-v{version}-secondary", secondaryAt, secondaryAt.AddMinutes(1), secondaryAt.AddMinutes(1), EnumValue("SessionRawRetentionState", "Expiring"), secondaryAt, secondaryAt.AddMinutes(1));
            var secondaryNativeId = Domain("SessionNativeId", secondarySessionId.Value, sourceSurface, secondaryNativeSessionId!, EnumValue("SessionBindingKind", "Native"), secondaryAt);
            var secondaryRun = Domain("ObservedSessionRun", secondaryRunId!.Value, secondarySessionId.Value, sourceSurface, $"fixture-run-v{version}-secondary", $"fixture-trace-v{version}-secondary", null, "fixture-model", completed, secondaryAt, secondaryAt.AddMinutes(1), 101L + version, 201L + version, 302L + version * 2L);
            var secondaryEvent = Domain("ObservedSessionEvent", secondaryEventId!.Value, secondarySessionId.Value, secondaryRunId.Value, sourceSurface, null, $"fixture-trace-v{version}-secondary", "ok", "fixture-adapter", secondarySourceEventId!, "session.task_complete", secondaryAt.AddSeconds(30), EnumValue("SessionContentState", "Available"));
            var secondaryContent = Domain("SessionEventContent", secondaryEventId.Value, "fixture", $"{{\"fixture\":\"session-v{version}-secondary\"}}", secondaryAt.AddSeconds(30), secondaryAt.AddDays(90));
            var secondaryDetail = Domain("SessionDetail", secondarySession, DomainArray("SessionNativeId", secondaryNativeId), DomainArray("ObservedSessionRun", secondaryRun), DomainArray("ObservedSessionEvent", secondaryEvent));
            Invoke(store, "Write", Domain("SessionWriteBatch", secondaryDetail, DomainArray("SessionEventContent", secondaryContent)));
        }

        if (version >= 2)
        {
            Invoke(store, "UpsertHumanEvaluation", Domain("SessionHumanEvaluation", sessionId, "expected", at.AddMinutes(2)));
        }

        if (version >= 3)
        {
            var evidence = Domain("ImprovementProposalEvidenceReference", "event", eventId.ToString("D"));
            var sourceSessions = version >= 9 ? new[] { sessionId, secondarySessionId!.Value } : new[] { sessionId };
            var evidenceReferences = version >= 9
                ? DomainArray("ImprovementProposalEvidenceReference", evidence, Domain("ImprovementProposalEvidenceReference", "event", secondaryEventId!.Value.ToString("D")))
                : DomainArray("ImprovementProposalEvidenceReference", evidence);
            var proposal = version >= 7
                ? Domain("ImprovementProposal", proposalId!.Value, 1, EnumValue("ImprovementProposalStatus", "Candidate"), "skill", $"fixture-target-v{version}", $"Fixture proposal v{version}", "Synthetic migration sentinel", "Preserve fixture rows", "none", sourceSessions, evidenceReferences, at.AddMinutes(3), at.AddMinutes(3), null, null)
                : Domain("ImprovementProposal", proposalId!.Value, EnumValue("ImprovementProposalStatus", "Candidate"), "skill", $"fixture-target-v{version}", $"Fixture proposal v{version}", "Synthetic migration sentinel", "Preserve fixture rows", "none", sourceSessions, evidenceReferences, at.AddMinutes(3), at.AddMinutes(3), null, null);
            Invoke(store, "CreateImprovementProposal", proposal);
            if (version >= 9)
            {
                Invoke(store, "UpdateImprovementProposalStatus", proposalId.Value, EnumValue("ImprovementProposalStatus", "Recommended"), at.AddMinutes(4));
            }
        }

        if (version >= 5)
        {
            var draft = version >= 7
                ? Domain("ProposalApplyDraftMetadata", draftId!.Value, proposalId!.Value, version >= 9 ? 2 : 1, rootId!.Value, 1, $"fixture-digest-v{version}", EnumValue("ProposalApplyState", "Draft"), 1, at.AddMinutes(4), at.AddMinutes(4))
                : Domain("ProposalApplyDraftMetadata", draftId!.Value, proposalId!.Value, rootId!.Value, 1, $"fixture-digest-v{version}", EnumValue("ProposalApplyState", "Draft"), 1, at.AddMinutes(4), at.AddMinutes(4));
            var revision = Domain("ProposalApplyRevisionMetadata", draftId.Value, 1, $"fixture-digest-v{version}", null);
            Invoke(store, "SaveProposalApplyDraft", draft, new[] { ($"fixture-base-v{version}", $"fixture-replacement-v{version}") }, new[] { ($"fixture-hunk-v{version}", true, $"fixture-replacement-v{version}") }, revision);
            var approvedRevision = Domain("ProposalApplyRevisionMetadata", draftId.Value, 1, $"fixture-digest-v{version}", at.AddMinutes(4));
            Invoke(store, "SaveProposalApplyApproval", draftId.Value, approvedRevision);
            var outcome = Domain("ProposalApplyOutcome", applyId!.Value, draftId.Value, EnumValue("ProposalApplyState", "Applied"), at.AddMinutes(5));
            if (version >= 6)
            {
                var pending = Domain("ProposalApplyPendingOperation", applyId.Value, draftId.Value, proposalId.Value, rootId.Value, 1, "apply", at.AddMinutes(5));
                Invoke(store, "SaveProposalApplyPending", pending);
                Invoke(store, "CompleteProposalApplyPending", outcome, proposalId.Value, rootId.Value, 1, null);
            }
            else
            {
                Invoke(store, "SaveProposalApplyOutcome", outcome, proposalId.Value, rootId.Value, 1, null);
            }
        }

        if (version >= 8)
        {
            var objectiveEvidence = Domain("ObjectiveEvaluationEvidence", "event", eventId.ToString("D"));
            var objective = Domain("ObjectiveEvaluationReceipt", objectiveEvaluationId!.Value, sessionId, runId, $"fixture-trace-v{version}", EnumValue("ObjectiveResult", "Fail"), EnumValue("ObjectiveSeverity", "Severe"), "fixture-evaluator", "v1", "fixture-criterion", "fixture-case", DomainArray("ObjectiveEvaluationEvidence", objectiveEvidence), at.AddMinutes(2));
            Invoke(store, "CreateObjectiveEvaluation", objective);
        }

        if (version >= 9)
        {
            Invoke(store, "UpsertHumanEvaluation", Domain("SessionHumanEvaluation", secondarySessionId!.Value, "expected", at.AddMinutes(11)));
            var pre = Domain("EffectCohortSession", sessionId, "pre", "fixture-case", null);
            var post = Domain("EffectCohortSession", secondarySessionId.Value, "post", "fixture-case", null);
            var request = Domain("EffectComparisonRequest", proposalId!.Value, 2, applyId!.Value, DomainArray("EffectCohortSession", pre, post));
            var receipt = Invoke(store, "RecordEffectComparison", request, at.AddMinutes(12))!;
            comparisonId = (Guid)receipt.GetType().GetProperty("ComparisonId")!.GetValue(receipt)!;
        }

        return (new FixtureSentinels(sessionId, nativeSessionId, runId, eventId, sourceEventId, projectorKey, proposalId, draftId, applyId, rootId, objectiveEvaluationId, secondarySessionId, secondaryNativeSessionId, secondaryRunId, secondaryEventId, secondarySourceEventId, comparisonId), new WeakReference(loadContext));
    }
    finally
    {
        loadContext.Unload();
    }
}

static void NormalizeComparisonId(string fixturePath, Guid generatedId, Guid deterministicId)
{
    using var connection = new SqliteConnection($"Data Source={fixturePath};Pooling=False");
    connection.Open();
    using (var deferForeignKeys = connection.CreateCommand())
    {
        deferForeignKeys.CommandText = "PRAGMA defer_foreign_keys=ON;";
        deferForeignKeys.ExecuteNonQuery();
    }
    using var transaction = connection.BeginTransaction();
    foreach (var table in new[] { "effect_comparison_sessions", "effect_comparison_evidence", "effect_receipts", "effect_comparisons" })
        Execute(connection, transaction, $"UPDATE {table} SET comparison_id=$deterministic WHERE comparison_id=$generated;", ("$deterministic", deterministicId.ToString("D")), ("$generated", generatedId.ToString("D")));
    transaction.Commit();
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

static void EnsureNoSqliteSidecars(string fixturePath)
{
    foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        if (File.Exists(fixturePath + suffix)) throw new InvalidOperationException($"Unexpected SQLite sidecar: {Path.GetFileName(fixturePath)}{suffix}.");
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
sealed record FixtureEntry(
    int Version,
    string File,
    string SourceCommit,
    string Sha256,
    string GitStatusBefore,
    string GitStatusAfter,
    IReadOnlyList<string> Limitations,
    FixtureSentinels Sentinels,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LineageProvenance? Lineage = null);
sealed record LineageProvenance(int SourceVersion, string SourceFixture, string SourceFixtureSha256, string UpgraderCommit, IReadOnlyList<string> Commands);
sealed record ApprovedLineageSource(int Version, string File, string SourceCommit, string Sha256, string ApprovedCopyPath, FixtureSentinels Sentinels);
sealed record FixtureSentinels(
    Guid SessionId, string NativeSessionId, Guid RunId, Guid EventId, string SourceEventId, string ProjectorKey,
    Guid? ProposalId, Guid? DraftId, Guid? ApplyId, Guid? RootId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ObjectiveEvaluationId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? SecondarySessionId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SecondaryNativeSessionId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? SecondaryRunId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? SecondaryEventId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SecondarySourceEventId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ComparisonId = null);
