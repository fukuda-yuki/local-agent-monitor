using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.ConfigCli;

internal static class RuntimeBackupCli
{
    internal static int Run(string[] args, TextWriter output, TextWriter error)
        => Run(args, output, error, FixedSkillRegistryGenerationAuthority.Load);

    internal static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<ISkillRegistryGenerationAuthority> authorityFactory)
    {
        ArgumentNullException.ThrowIfNull(authorityFactory);
        if (args.Length == 0) return Error(error, RuntimeBackupErrorCodes.InvalidArguments);
        try
        {
            return args[0] switch
            {
                "create" => Create(args, output, error, authorityFactory),
                "inspect" => Inspect(args, output, error),
                "preview" => Preview(args, output, error, authorityFactory),
                "restore" => Restore(args, output, error, authorityFactory),
                _ => Error(error, RuntimeBackupErrorCodes.InvalidArguments),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return Error(error, RuntimeBackupErrorCodes.RestoreFailed);
        }
    }

    private static int Create(string[] args, TextWriter output, TextWriter error, Func<ISkillRegistryGenerationAuthority> authorityFactory)
    {
        if (!TryOptions(args, ["--database", "--output"], [], out var values, out var flags) || flags.Count != 0)
            return Error(error, RuntimeBackupErrorCodes.InvalidArguments);
        if (!TryCreateOfflineService(authorityFactory, out var service, out var gate))
            return Error(error, RuntimeBackupErrorCodes.RestoreIncompatible);
        var result = service.CreateAndPublish(values["--database"], values["--output"]);
        return Result(result.Success, result.ErrorCode, result, output, error);
    }

    private static int Inspect(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryOptions(args, ["--bundle"], [], out var values, out var flags) || flags.Count != 0)
            return Error(error, RuntimeBackupErrorCodes.InvalidArguments);
        var result = new SqliteRuntimeBackupService().Inspect(values["--bundle"]);
        return Result(result.Success, result.ErrorCode, result, output, error);
    }

    private static int Preview(string[] args, TextWriter output, TextWriter error, Func<ISkillRegistryGenerationAuthority> authorityFactory)
    {
        if (!TryOptions(args, ["--bundle", "--database"], [], out var values, out var flags) || flags.Count != 0)
            return Error(error, RuntimeBackupErrorCodes.InvalidArguments);
        if (!TryCreateOfflineService(authorityFactory, out var service, out var gate))
            return Error(error, RuntimeBackupErrorCodes.RestoreIncompatible);
        var lease = gate.AcquireReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try
        {
            var result = service.Preview(values["--bundle"], values["--database"]);
            return Result(result.Success, result.ErrorCode, result, output, error);
        }
        finally
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int Restore(string[] args, TextWriter output, TextWriter error, Func<ISkillRegistryGenerationAuthority> authorityFactory)
    {
        if (!TryOptions(args, ["--bundle", "--database"], ["--pre-restore-output", "--confirmation"], out var values, out var flags)
            || flags.Except(["--allow-resurrection"], StringComparer.Ordinal).Any()
            || (flags.Contains("--allow-resurrection") != values.ContainsKey("--confirmation")))
            return Error(error, RuntimeBackupErrorCodes.InvalidArguments);
        var options = new RuntimeRestoreOptions(
            values.GetValueOrDefault("--pre-restore-output"),
            flags.Contains("--allow-resurrection"),
            values.GetValueOrDefault("--confirmation"));
        if (!TryCreateOfflineService(authorityFactory, out var service, out var gate))
            return Error(error, RuntimeBackupErrorCodes.RestoreIncompatible);
        var lease = gate.AcquireReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try
        {
            var result = service.Restore(values["--bundle"], values["--database"], options);
            return Result(result.Success, result.ErrorCode, result, output, error);
        }
        finally
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static bool TryCreateOfflineService(
        Func<ISkillRegistryGenerationAuthority> authorityFactory,
        out SqliteRuntimeBackupService service,
        out LocalWorkspacePublicationGate gate)
    {
        gate = new LocalWorkspacePublicationGate();
        try
        {
            service = new SqliteRuntimeBackupService(TimeProvider.System, authorityFactory(), gate);
            return true;
        }
        catch (InvalidOperationException)
        {
            service = null!;
            return false;
        }
    }

    private static int Result<T>(bool success, string? code, T value, TextWriter output, TextWriter error)
    {
        output.WriteLine(Encoding.UTF8.GetString(RuntimeBackupJson.SerializeResult(value)));
        return success ? 0 : Error(error, code ?? RuntimeBackupErrorCodes.RestoreFailed);
    }

    private static bool TryOptions(
        string[] args,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional,
        out Dictionary<string, string> values,
        out HashSet<string> flags)
    {
        values = new(StringComparer.Ordinal);
        flags = new(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];
            if (name == "--allow-resurrection")
            {
                if (!flags.Add(name)) return false;
                continue;
            }
            if ((!required.Contains(name) && !optional.Contains(name)) || index + 1 >= args.Length
                || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(name, args[++index])) return false;
        }
        return required.All(values.ContainsKey);
    }

    private static int Error(TextWriter error, string code)
    {
        error.WriteLine(code);
        return code switch
        {
            RuntimeBackupErrorCodes.InvalidArguments => 2,
            RuntimeBackupErrorCodes.RestoreIncompatible or RuntimeBackupErrorCodes.RestoreResurrectionBlocked
                or RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed => 3,
            RuntimeBackupErrorCodes.MonitorMustBeStopped or RuntimeBackupErrorCodes.ExternalRawStoreActive
                or RuntimeBackupErrorCodes.ExternalRuntimeStateActive or RuntimeBackupErrorCodes.ExternalRuntimeStateUnknown
                or RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe => 4,
            _ => 5,
        };
    }
}
