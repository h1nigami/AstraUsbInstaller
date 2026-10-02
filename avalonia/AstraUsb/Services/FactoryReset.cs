using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace AstraUsb.Services;

/// <summary>Что удалил заводской сброс.</summary>
public sealed record FactoryResetResult(int Devices, int Backups, int Entries);

/// <summary>
/// Сброс станции до заводского состояния, как factory_reset в Python-версии:
/// устройства, история выгрузок, файлы архива, пароль и настройки.
///
/// Журнал действий остаётся: по нему потом видно, кто и когда сбросил
/// станцию. Справочник сотрудников тоже сохраняется.
/// </summary>
public static class FactoryReset
{
    /// <summary>Таблицы с историей: устройства, сеансы, собранные файлы и очередь отправки.</summary>
    private static readonly string[] HistoryTables = ["collected_files", "ftp_queue", "backups"];

    public static FactoryResetResult Run(string dbPath, string? archiveRoot)
    {
        using var operation = OperationGuard.Acquire(exclusive: true, dbPath: dbPath);
        // Снос данных под идущей выгрузкой оставил бы архив и базу
        // в рассогласованном состоянии.
        if (BusyMarker.Busy())
            throw new InvalidOperationException("Сброс невозможен: идёт сканирование или копирование");

        using var archive = ArchiveGuard.Open(archiveRoot ?? Settings.Load().ResolveBackupRoot(), settings: Settings.Load());
        var entries = ClearArchive(archive);
        archive.Verify();
        var (devices, backups) = ClearDatabase(dbPath);
        Settings.Reset();

        try
        {
            File.Delete(BusyMarker.FilePath);
        }
        catch (Exception)
        {
            // Отметка занятости протухнет сама.
        }

        return new FactoryResetResult(devices, backups, entries);
    }

    private static (int Devices, int Backups) ClearDatabase(string dbPath)
    {
        if (!File.Exists(dbPath))
            return (0, 0);

        try
        {
            using var db = new SqliteConnection($"Data Source={dbPath};Default Timeout=30");
            db.Open();
            var tables = new HashSet<string>(StringComparer.Ordinal);
            using (var list = db.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var reader = list.ExecuteReader();
                while (reader.Read())
                    tables.Add(reader.GetString(0));
            }

            int Count(string table) => tables.Contains(table)
                ? Convert.ToInt32(Scalar(db, $"SELECT COUNT(*) FROM {table}"))
                : 0;

            var backups = Count("backups");
            var devices = Count("devices");

            using (var transaction = db.BeginTransaction())
            {
                // Сначала всё, что ссылается на устройства, затем сами устройства.
                foreach (var table in HistoryTables.Append("devices"))
                {
                    if (!tables.Contains(table))
                        continue;
                    using var delete = db.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM {table}";
                    delete.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            try
            {
                using var vacuum = db.CreateCommand();
                vacuum.CommandText = "VACUUM";
                vacuum.ExecuteNonQuery();
            }
            catch (SqliteException error)
            {
                // Данные уже удалены, место освободится позже: падать поздно.
                Debug.WriteLine($"VACUUM пропущен: {error.Message}");
            }

            return (devices, backups);
        }
        catch (SqliteException error)
        {
            throw new IOException($"Не удалось очистить базу: {error.Message}", error);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// Удаляет содержимое архива. Только там, где лежит метка тома: без неё
    /// нельзя поручиться, что это архив станции, а не, скажем, корень диска,
    /// оказавшийся в настройках по ошибке.
    /// </summary>
    private static int ClearArchive(ArchiveDirectory archive)
    {
        var entries = 0;
        foreach (var item in new DirectoryInfo(archive.Path).EnumerateDirectories())
        {
            if (!ArchiveGuard.IsDeviceFolderName(item.Name) || item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            archive.DeleteDeviceFolder(item.Name);
            entries++;
        }
        return entries;
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
