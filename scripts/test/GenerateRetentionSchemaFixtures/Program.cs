using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

var outputArgument = ParseOutputArgument(args);
if (Path.IsPathRooted(outputArgument))
    throw new ArgumentException("--output must be repository-relative so the recorded command remains repository-safe.");

var repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory);
var outputDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, outputArgument));
var repositoryPrefix = Path.GetFullPath(repositoryRoot) + Path.DirectorySeparatorChar;
if (!outputDirectory.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("--output must remain inside the repository.");

var normalizedOutput = outputArgument.Replace('\\', '/').TrimEnd('/');
var generationCommand = $"dotnet run --project scripts/test/GenerateRetentionSchemaFixtures/GenerateRetentionSchemaFixtures.csproj -- --output {normalizedOutput}";
var temporaryRoot = Path.Combine(Path.GetTempPath(), $"retention-schema-fixtures-{Guid.NewGuid():N}");
try
{
    var stagedOutput = Path.Combine(temporaryRoot, "output");
    Directory.CreateDirectory(stagedOutput);
    var fixtureFile = "retention-catalog-v1.sqlite";
    var fixturePath = Path.Combine(stagedOutput, fixtureFile);

    new RetentionCatalogStore(fixturePath).CreateSchema();
    using (var connection = new SqliteConnection($"Data Source={fixturePath};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE retention_store_instances SET store_instance_id='00000000000000000000000000000089' WHERE id=1;";
        command.ExecuteNonQuery();
    }
    CloseAndVacuum(fixturePath);

    var fixture = new FixtureEntry(
        fixtureFile,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath))).ToLowerInvariant(),
        new FileInfo(fixturePath).Length,
        ReadIntegrityCheck(fixturePath),
        new FixtureSentinels("00000000000000000000000000000089", 1, 0));
    var manifest = new FixtureManifest("retention", generationCommand, "git status --porcelain", [fixture]);
    var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    });

    Directory.CreateDirectory(outputDirectory);
    File.Copy(fixturePath, Path.Combine(outputDirectory, fixtureFile), overwrite: true);
    File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"), manifestJson.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));
}
finally
{
    if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
}

static string ParseOutputArgument(string[] arguments)
{
    if (arguments.Length != 2 || arguments[0] != "--output" || string.IsNullOrWhiteSpace(arguments[1]))
        throw new ArgumentException("Usage: GenerateRetentionSchemaFixtures --output <repository-relative-directory>");
    return arguments[1];
}

static string FindRepositoryRoot(string currentDirectory)
{
    var directory = new DirectoryInfo(currentDirectory);
    while (directory is not null)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
            return directory.FullName;
        directory = directory.Parent;
    }
    throw new InvalidOperationException("The repository root could not be located.");
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
    using var vacuum = connection.CreateCommand();
    vacuum.CommandText = "VACUUM;";
    vacuum.ExecuteNonQuery();
}

static string ReadIntegrityCheck(string fixturePath)
{
    using var connection = new SqliteConnection($"Data Source={fixturePath};Mode=ReadOnly;Pooling=False");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA integrity_check;";
    return (string)command.ExecuteScalar()!;
}

sealed record FixtureManifest(string Component, string GenerationCommand, string GitStatusCommand, IReadOnlyList<FixtureEntry> Fixtures);
sealed record FixtureEntry(string File, string Sha256, long ByteLength, string IntegrityCheck, FixtureSentinels Sentinels);
sealed record FixtureSentinels(string StoreInstanceId, int CatalogSchemaVersion, int ItemCount);
