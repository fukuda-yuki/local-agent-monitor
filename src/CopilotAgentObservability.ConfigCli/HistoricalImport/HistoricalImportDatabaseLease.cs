using System.Data;
using System.Runtime.InteropServices;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.HistoricalImport;

internal sealed class HistoricalImportDatabaseLease : IDisposable
{
    private const int SqliteOk = 0;
    private const int SqliteFileControlHasMoved = 20;
    private const int SqliteFileControlWindowsGetHandle = 29;
    private static readonly byte[] MainDatabaseName = "main\0"u8.ToArray();

    private readonly string databasePath;
    private readonly FileStream lease;
    private readonly HistoricalImportFileIdentity identity;
    private int disposed;

    private HistoricalImportDatabaseLease(string databasePath, FileStream lease)
    {
        this.databasePath = databasePath;
        this.lease = lease;
        identity = HistoricalImportLocalFile.ReadIdentity(lease);
    }

    internal static HistoricalImportDatabaseLease Open(string databasePath)
    {
        try
        {
            var lease = HistoricalImportLocalFile.OpenRegularRead(databasePath, allowWriteShare: true);
            try
            {
                return new HistoricalImportDatabaseLease(databasePath, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch (HistoricalImportException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or
            DirectoryNotFoundException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }
    }

    internal SqliteConnection OpenVerifiedConnection() => OpenVerifiedConnection(afterConnectionOpened: null);

    internal SqliteConnection OpenVerifiedConnection(Action? afterConnectionOpened)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            afterConnectionOpened?.Invoke();
            RequireExpectedFileIdentity(connection);
            RequireExpectedCanonicalPath(connection);
            return connection;
        }
        catch (HistoricalImportException)
        {
            connection?.Dispose();
            throw;
        }
        catch (SqliteException)
        {
            connection?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or
            DirectoryNotFoundException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            ObjectDisposedException or
            System.Security.SecurityException)
        {
            connection?.Dispose();
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }
        catch
        {
            connection?.Dispose();
            throw;
        }
    }

    internal void RequireLocalMonitorOwnership()
    {
        using var connection = OpenVerifiedConnection();
        try
        {
            using var version = connection.CreateCommand();
            version.CommandText = "SELECT version FROM schema_version WHERE component='monitor';";
            if (version.ExecuteScalar() is not long monitorVersion
                || monitorVersion != RawTelemetryStore.MonitorSchemaVersion)
            {
                throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
            }

            using var tables = connection.CreateCommand();
            tables.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_schema
                WHERE type='table'
                  AND name IN ('raw_records','monitor_ingestions','monitor_traces','monitor_spans');
                """;
            if (Convert.ToInt32(tables.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 4)
            {
                throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
            }
        }
        catch (HistoricalImportException)
        {
            throw;
        }
        catch (SqliteException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }
    }

    private void RequireExpectedCanonicalPath(SqliteConnection connection)
    {
        var filename = SqliteDatabaseFilename(
            connection.Handle!.DangerousGetHandle(),
            MainDatabaseName);
        var openedPath = filename == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(filename);
        if (string.IsNullOrWhiteSpace(openedPath))
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(NormalizeReportedPath(openedPath), databasePath, comparison))
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }
    }

    private void RequireExpectedFileIdentity(SqliteConnection connection)
    {
        if (connection.State != ConnectionState.Open || connection.Handle is null)
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }

        if (OperatingSystem.IsWindows())
        {
            var result = SqliteFileControlPointer(
                connection.Handle.DangerousGetHandle(),
                MainDatabaseName,
                SqliteFileControlWindowsGetHandle,
                out var sqliteFileHandle);
            if (result != SqliteOk
                || sqliteFileHandle == IntPtr.Zero
                || HistoricalImportLocalFile.ReadWindowsIdentity(sqliteFileHandle) != identity)
            {
                throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
            }

            return;
        }

        using var current = HistoricalImportLocalFile.OpenRegularRead(databasePath, allowWriteShare: true);
        if (HistoricalImportLocalFile.ReadIdentity(current) != identity)
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }

        var moved = 0;
        var movedResult = SqliteFileControlInteger(
            connection.Handle.DangerousGetHandle(),
            MainDatabaseName,
            SqliteFileControlHasMoved,
            ref moved);
        if (movedResult != SqliteOk || moved != 0)
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.StoreUnavailable);
        }
    }

    private static string NormalizeReportedPath(string path)
    {
        var normalized = OperatingSystem.IsWindows() && path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path[4..]
            : path;
        return Path.GetFullPath(normalized);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            lease.Dispose();
        }
    }

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_file_control", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteFileControlPointer(
        IntPtr database,
        byte[] databaseName,
        int operation,
        out IntPtr value);

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_file_control", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SqliteFileControlInteger(
        IntPtr database,
        byte[] databaseName,
        int operation,
        ref int value);

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_db_filename", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SqliteDatabaseFilename(
        IntPtr database,
        byte[] databaseName);
}
