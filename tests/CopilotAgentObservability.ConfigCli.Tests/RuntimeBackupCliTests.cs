using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class RuntimeBackupCliTests
{
    [Fact]
    public void Help_lists_the_closed_runtime_backup_command_family()
    {
        var output = new StringWriter();

        var exit = CliApplication.Run(["help"], output, new StringWriter());

        Assert.Equal(0, exit);
        Assert.Contains("runtime-backup create --database <monitor.db> --output <bundle.zip>", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("runtime-backup inspect --bundle <bundle.zip>", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("runtime-backup preview --bundle <bundle.zip> --database <monitor.db>", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("runtime-backup restore --bundle <bundle.zip> --database <monitor.db>", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_runtime_backup_arguments_return_fixed_code_without_echoing_values()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = CliApplication.Run(["runtime-backup", "restore", "--bundle", "C:\\private\\secret.zip"], output, error);

        Assert.Equal(2, exit);
        Assert.Equal("invalid_arguments" + Environment.NewLine, error.ToString());
        Assert.DoesNotContain("C:\\private\\secret.zip", output.ToString() + error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.zip", output.ToString() + error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("inspect")]
    [InlineData("preview")]
    public void Non_restore_commands_reject_restore_only_flag(string command)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"runtime-backup-flag-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "monitor.db");
        var bundle = Path.Combine(directory, "backup.zip");
        File.WriteAllBytes(database, []);
        File.WriteAllBytes(bundle, []);
        var arguments = command switch
        {
            "create" => new[] { "runtime-backup", command, "--database", database, "--output", Path.Combine(directory, "created.zip"), "--allow-resurrection" },
            "inspect" => new[] { "runtime-backup", command, "--bundle", bundle, "--allow-resurrection" },
            _ => new[] { "runtime-backup", command, "--bundle", bundle, "--database", database, "--allow-resurrection" },
        };
        try
        {
            var result = Run(arguments);

            Assert.Equal(2, result.Exit);
            Assert.Equal("invalid_arguments" + Environment.NewLine, result.Error);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Create_inspect_and_preview_emit_json_without_paths()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"runtime-backup-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "monitor.db");
            var bundle = Path.Combine(directory, "backup.zip");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
                    transaction.Commit();
                }
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE cli_probe(value TEXT NOT NULL); INSERT INTO cli_probe(value) VALUES('opaque');";
                command.ExecuteNonQuery();
            }

            var created = Run(["runtime-backup", "create", "--database", database, "--output", bundle]);
            var inspected = Run(["runtime-backup", "inspect", "--bundle", bundle]);
            var previewed = Run(["runtime-backup", "preview", "--bundle", bundle, "--database", database]);

            Assert.Equal(0, created.Exit);
            Assert.Equal(0, inspected.Exit);
            Assert.Equal(0, previewed.Exit);
            Assert.All(new[] { created, inspected, previewed }, result =>
            {
                Assert.Equal(string.Empty, result.Error);
                using var json = JsonDocument.Parse(result.Output);
                Assert.True(json.RootElement.GetProperty("success").GetBoolean());
                Assert.DoesNotContain(JsonStrings(json.RootElement), value =>
                    value.Contains(directory, StringComparison.OrdinalIgnoreCase));
            });
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Restore_executes_offline_and_emits_fixed_path_free_result()
    {
        var root = Path.Combine(Path.GetTempPath(), $"runtime-backup-cli-restore-{Guid.NewGuid():N}");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "source-private-root")).FullName;
        var targetDirectory = Directory.CreateDirectory(Path.Combine(root, "target-private-root")).FullName;
        var source = Path.Combine(sourceDirectory, "monitor.db");
        var target = Path.Combine(targetDirectory, "monitor.db");
        var bundle = Path.Combine(sourceDirectory, "backup.zip");
        try
        {
            CreateDatabase(source, "source-value");
            CreateDatabase(target, "old-value");
            var created = Run(["runtime-backup", "create", "--database", source, "--output", bundle]);
            Assert.Equal(0, created.Exit);

            var restored = Run(["runtime-backup", "restore", "--bundle", bundle, "--database", target]);

            Assert.True(restored.Exit == 0, $"exit={restored.Exit}; error={restored.Error}");
            Assert.Equal(string.Empty, restored.Error);
            using var json = JsonDocument.Parse(restored.Output);
            var result = json.RootElement;
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.Equal("local-runtime-restore-result.v1", result.GetProperty("schema_version").GetString());
            Assert.True(result.GetProperty("pre_restore_backup_created").GetBoolean());
            Assert.Equal(64, result.GetProperty("pre_restore_backup_sha256").GetString()?.Length);
            Assert.Equal("database_ready", result.GetProperty("readiness_check").GetString());
            Assert.Equal("doctor_store_ready", result.GetProperty("doctor_check").GetString());
            Assert.DoesNotContain(JsonStrings(result), value =>
                value.Contains(sourceDirectory, StringComparison.OrdinalIgnoreCase)
                || value.Contains(targetDirectory, StringComparison.OrdinalIgnoreCase));
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM cli_probe;";
            Assert.Equal("source-value", command.ExecuteScalar());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Restore_honors_a_caller_selected_pre_restore_output_without_disclosing_its_path()
    {
        var root = Path.Combine(Path.GetTempPath(), $"runtime-backup-cli-pre-restore-{Guid.NewGuid():N}");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var targetDirectory = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        var source = Path.Combine(sourceDirectory, "source.db");
        var target = Path.Combine(targetDirectory, "target.db");
        var bundle = Path.Combine(sourceDirectory, "source.zip");
        var preRestore = Path.Combine(targetDirectory, "operator-selected-pre-restore.zip");
        try
        {
            CreateDatabase(source, "source-value");
            CreateDatabase(target, "target-value");
            Assert.Equal(0, Run(["runtime-backup", "create", "--database", source, "--output", bundle]).Exit);

            var restored = Run([
                "runtime-backup", "restore",
                "--bundle", bundle,
                "--database", target,
                "--pre-restore-output", preRestore,
            ]);

            Assert.Equal(0, restored.Exit);
            Assert.Equal(string.Empty, restored.Error);
            Assert.True(File.Exists(preRestore));
            using var json = JsonDocument.Parse(restored.Output);
            Assert.True(json.RootElement.GetProperty("pre_restore_backup_created").GetBoolean());
            Assert.DoesNotContain(JsonStrings(json.RootElement), value =>
                value.Contains(root, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Restore_incompatible_and_external_state_failures_use_fixed_exit_classes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"runtime-backup-cli-exits-{Guid.NewGuid():N}");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var targetDirectory = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        var source = Path.Combine(sourceDirectory, "source.db");
        var target = Path.Combine(targetDirectory, "target.db");
        var bundle = Path.Combine(sourceDirectory, "source.zip");
        try
        {
            CreateDatabase(source, "source-value");
            CreateDatabase(target, "target-value");
            Assert.Equal(0, Run(["runtime-backup", "create", "--database", source, "--output", bundle]).Exit);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE schema_version SET version=999 WHERE component='monitor';";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var incompatible = Run(["runtime-backup", "preview", "--bundle", bundle, "--database", target]);

            Assert.Equal(3, incompatible.Exit);
            Assert.Equal("restore_incompatible" + Environment.NewLine, incompatible.Error);
            Assert.DoesNotContain(root, incompatible.Output + incompatible.Error, StringComparison.OrdinalIgnoreCase);

            var proposalDrafts = Directory.CreateDirectory(Path.Combine(sourceDirectory, "proposal-apply", "drafts"));
            File.WriteAllText(Path.Combine(proposalDrafts.FullName, "private-draft.json"), "private");
            var blockedOutput = Path.Combine(sourceDirectory, "blocked.zip");

            var external = Run(["runtime-backup", "create", "--database", source, "--output", blockedOutput]);

            Assert.Equal(4, external.Exit);
            Assert.Equal("external_runtime_state_active" + Environment.NewLine, external.Error);
            Assert.DoesNotContain(root, external.Output + external.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(blockedOutput));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Restore_requires_resurrection_flag_and_confirmation_as_a_pair(bool allow, bool confirmation)
    {
        var arguments = new List<string> { "runtime-backup", "restore", "--bundle", Path.GetFullPath("bundle.zip"), "--database", Path.GetFullPath("monitor.db") };
        if (allow) arguments.Add("--allow-resurrection");
        if (confirmation) { arguments.Add("--confirmation"); arguments.Add(new string('a', 64)); }

        var result = Run(arguments.ToArray());

        Assert.Equal(2, result.Exit);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("invalid_arguments" + Environment.NewLine, result.Error);
    }

    [Fact]
    public void Restore_classifies_empty_archive_without_echoing_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"runtime-backup-cli-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var bundle = Path.Combine(root, "private-empty.zip");
        var target = Path.Combine(root, "monitor.db");
        try
        {
            File.WriteAllBytes(bundle, []);
            CreateDatabase(target, "old-value");

            var result = Run(["runtime-backup", "restore", "--bundle", bundle, "--database", target]);

            Assert.Equal(5, result.Exit);
            Assert.Equal("archive_invalid" + Environment.NewLine, result.Error);
            using var json = JsonDocument.Parse(result.Output);
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("archive_invalid", json.RootElement.GetProperty("error_code").GetString());
            Assert.DoesNotContain(JsonStrings(json.RootElement), value =>
                value.Contains(root, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CreateDatabase(string path, string value)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using (var transaction = connection.BeginTransaction())
        {
            MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
            transaction.Commit();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE cli_probe(value TEXT NOT NULL); INSERT INTO cli_probe(value) VALUES($value);";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static IEnumerable<string> JsonStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                foreach (var value in JsonStrings(item))
                    yield return value;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                foreach (var value in JsonStrings(property.Value))
                    yield return value;
                break;
        }
    }

    private static (int Exit, string Output, string Error) Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return (CliApplication.Run(args, output, error), output.ToString(), error.ToString());
    }
}
