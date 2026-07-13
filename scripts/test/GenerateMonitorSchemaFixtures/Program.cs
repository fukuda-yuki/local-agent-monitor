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
var generationCommand = $"dotnet run --project scripts/test/GenerateMonitorSchemaFixtures/GenerateMonitorSchemaFixtures.csproj -- --output {normalizedOutput}";
var temporaryRoot = Path.Combine(Path.GetTempPath(), $"monitor-schema-fixtures-{Guid.NewGuid():N}");
var stagedOutput = Path.Combine(temporaryRoot, "output");
Directory.CreateDirectory(stagedOutput);

var specifications = new[]
{
    new FixtureSpecification(1, "655e00243df9e07b9abb3392d6e4daf747064a77"),
    new FixtureSpecification(2, "f91e195b549fa2bbfc51b3245dd3fb19fcc8759c"),
    new FixtureSpecification(3, "9ca613a97fd0611ccff1d84b35261b7346112eab"),
    new FixtureSpecification(4, "65ec872eb541b2023f55c32d32edebb9cf83818b"),
};

var entries = new List<FixtureEntry>();
try
{
    foreach (var specification in specifications)
    {
        var historicalWorktree = Path.Combine(temporaryRoot, $"monitor-v{specification.Version}");
        var artifactsPath = Path.Combine(temporaryRoot, $"artifacts-v{specification.Version}");
        var fixtureFile = $"monitor-v{specification.Version}.sqlite";
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
            var loadContext = InvokeHistoricalCreateMonitorSchema(historicalAssembly, fixturePath);
            WaitForUnload(loadContext);

            var sentinels = InsertSentinels(fixturePath, specification.Version);
            CloseAndVacuum(fixturePath);

            var statusAfter = Run("git", repositoryRoot, "-C", historicalWorktree, "status", "--porcelain").Trim();
            EnsureClean(specification.Version, "after generation", statusAfter);

            entries.Add(new FixtureEntry(
                specification.Version,
                fixtureFile,
                specification.SourceCommit,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant(),
                statusBefore,
                statusAfter,
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

    var manifest = new FixtureManifest("monitor", generationCommand, "git status --porcelain", entries);
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
        throw new ArgumentException("Usage: GenerateMonitorSchemaFixtures --output <repository-relative-directory>");
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
static WeakReference InvokeHistoricalCreateMonitorSchema(string assemblyPath, string fixturePath)
{
    var loadContext = new HistoricalLoadContext(assemblyPath);
    try
    {
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var storeType = assembly.GetType("CopilotAgentObservability.Persistence.Sqlite.RawTelemetryStore", throwOnError: true)!;
        var constructor = AssertSingle(storeType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), candidate => candidate.GetParameters().Length == 2);
        var store = constructor.Invoke(new object?[] { fixturePath, null });
        storeType.GetMethod("CreateMonitorSchema", BindingFlags.Instance | BindingFlags.Public)!.Invoke(store, null);
    }
    finally
    {
        loadContext.Unload();
    }
    return new WeakReference(loadContext);
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

static FixtureSentinels InsertSentinels(string fixturePath, int version)
{
    var sentinels = new FixtureSentinels(
        RawRecordId: version * 100L + 1,
        IngestionId: version * 100L + 2,
        TraceRowId: version * 100L + 3,
        TraceId: $"fixture-monitor-v{version}-trace",
        SpanRowId: version >= 2 ? version * 100L + 4 : null,
        SpanId: version >= 2 ? $"fixture-monitor-v{version}-span" : null);

    using var connection = new SqliteConnection($"Data Source={fixturePath};Pooling=False");
    connection.Open();
    using var transaction = connection.BeginTransaction();
    Execute(connection, transaction,
        "INSERT INTO raw_records(id,source,trace_id,received_at,payload_json,schema_version) VALUES($id,'raw-otlp',$trace,'2026-07-12T00:00:00.0000000+00:00','{\"fixture\":true}',1);",
        ("$id", sentinels.RawRecordId), ("$trace", sentinels.TraceId));
    Execute(connection, transaction,
        "INSERT INTO monitor_ingestions(id,raw_record_id,received_at,source,trace_id,projected_at) VALUES($id,$raw,'2026-07-12T00:00:00.0000000+00:00','raw-otlp',$trace,'2026-07-12T00:00:01.0000000+00:00');",
        ("$id", sentinels.IngestionId), ("$raw", sentinels.RawRecordId), ("$trace", sentinels.TraceId));
    Execute(connection, transaction,
        "INSERT INTO monitor_traces(id,trace_id,projected_at) VALUES($id,$trace,'2026-07-12T00:00:01.0000000+00:00');",
        ("$id", sentinels.TraceRowId), ("$trace", sentinels.TraceId));
    if (sentinels.SpanRowId is not null && sentinels.SpanId is not null)
    {
        Execute(connection, transaction,
            "INSERT INTO monitor_spans(id,raw_record_id,trace_id,span_id,span_ordinal,projected_at) VALUES($id,$raw,$trace,$span,0,'2026-07-12T00:00:01.0000000+00:00');",
            ("$id", sentinels.SpanRowId.Value), ("$raw", sentinels.RawRecordId), ("$trace", sentinels.TraceId), ("$span", sentinels.SpanId));
    }
    transaction.Commit();
    return sentinels;
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

static T AssertSingle<T>(IEnumerable<T> values, Func<T, bool> predicate)
{
    var matches = values.Where(predicate).ToArray();
    return matches.Length == 1 ? matches[0] : throw new InvalidOperationException($"Expected one match, found {matches.Length}.");
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
sealed record FixtureEntry(int Version, string File, string SourceCommit, string Sha256, string GitStatusBefore, string GitStatusAfter, FixtureSentinels Sentinels);
sealed record FixtureSentinels(long RawRecordId, long IngestionId, long TraceRowId, string TraceId, long? SpanRowId, string? SpanId);
