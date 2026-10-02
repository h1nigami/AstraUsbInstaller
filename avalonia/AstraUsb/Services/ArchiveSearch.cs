using Microsoft.Data.Sqlite;

namespace AstraUsb.Services;

/// <summary>Чем закончилось удаление отобранных записей.</summary>
/// <param name="Deleted">Сколько записей убрано.</param>
/// <param name="Skipped">Сколько пропущено из-за защиты.</param>
/// <param name="Failed">Сколько не удалось удалить.</param>
/// <param name="Bytes">Сколько места освободилось.</param>
public sealed record DeleteResult(int Deleted, int Skipped, int Failed, long Bytes)
{
    public IReadOnlyList<string> DeletedPaths { get; init; } = [];
}

/// <summary>
/// Поиск по архиву и удаление найденного.
///
/// Записи лежат в журнале сбора, а имена сотрудников и отделы в справочнике,
/// поэтому отбор идёт в два шага: журнал отдаёт записи за период, а остальные
/// условия проверяются по справочнику. Так проще, чем джойн через две базы
/// сущностей, и достаточно быстро: потолок выборки всё равно 500 записей.
/// </summary>
public sealed class ArchiveSearch
{
    /// <summary>Столько записей отдаётся оператору, как требует задание.</summary>
    public const int Limit = 500;

    private readonly string _dbPath;
    private readonly string? _archiveRoot;
    private readonly Func<string, string>? _resolvePath;

    public ArchiveSearch(string dbPath, string? archiveRoot = null)
    {
        _dbPath = dbPath;
        _archiveRoot = archiveRoot;
    }

    internal ArchiveSearch(string dbPath, string archiveRoot, Func<string, string> resolvePath)
        : this(dbPath, archiveRoot) => _resolvePath = resolvePath;

    public IReadOnlyList<ArchiveRow> Find(ArchiveFilter filter, int limit = Limit,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!DatabaseExists())
            return [];
        var settings = Settings.Load();
        var root = _archiveRoot ?? settings.ResolveBackupRoot();
        var resolve = _resolvePath ?? settings.ArchivePathResolver(root);
        var log = new CollectionLog(_dbPath, initialize: false);

        var from = filter.CollectedFrom ?? DateTime.MinValue;
        var to = filter.CollectedTo ?? DateTime.MaxValue;
        // ponytail: для важных alias читаем весь журнал; большой архив потребует постоянного идентификатора и индекса.
        var found = log.CollectedBetween(DateTime.MinValue, DateTime.MaxValue, token: token);
        if (found.Count == 0)
            return [];

        var staff = new StaffDirectory(_dbPath, initialize: false);
        var cameras = Cameras(token);
        var people = People(staff, token);
        var departments = Departments(filter, staff, token);

        var rows = new List<ArchiveRow>();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var aliases = new Dictionary<string, List<CollectedFile>>(comparer);
        foreach (var file in found)
        {
            token.ThrowIfCancellationRequested();
            if (Resolve(resolve, file.DestPath) is not { } path)
                continue;
            if (!aliases.TryGetValue(path, out var entries))
                aliases[path] = entries = [];
            entries.Add(file);
        }
        var shown = new HashSet<string>(comparer);

        foreach (var file in found)
        {
            token.ThrowIfCancellationRequested();
            if (file.CollectedAt < from || file.CollectedAt > to
                || filter.DeviceId is { } deviceId && file.DeviceId != deviceId
                || Resolve(resolve, file.DestPath) is not { } path || shown.Contains(path))
                continue;
            var entries = aliases[path];
            var camera = cameras.GetValueOrDefault(file.DeviceId);
            var person = camera?.EmployeeId is { } id ? people.GetValueOrDefault(id) : null;

            var name = camera?.Name is { Length: > 0 } given
                ? given
                : camera?.FirmwareId is { Length: > 0 } number
                    ? number
                    : file.DeviceId.ToString();

            var row = new ArchiveRow(
                file with { Important = entries.Any(entry => entry.Important) },
                name,
                person?.FullName ?? "",
                person?.PersonnelNo ?? "",
                person?.DepartmentId is { } dep ? staff.DepartmentPath(dep, token) : "")
            { Path = path, LoggedPaths = entries.Select(entry => entry.DestPath).ToArray() };

            if (!Matches(row, filter, departments, person))
                continue;

            rows.Add(row);
            shown.Add(path);
            if (rows.Count >= limit)
                break;
        }

        token.ThrowIfCancellationRequested();
        return rows;
    }

    private bool DatabaseExists()
    {
        var database = Path.GetFullPath(_dbPath);
        for (string? path = database; path is not null; path = Path.GetDirectoryName(path))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if (path != database && (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Родительский путь базы не является каталогом");
                return path == database;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return false;
    }

    public void ValidatePath(string path)
    {
        var settings = _archiveRoot is null ? Settings.Load() : null;
        using var archive = ArchiveGuard.Open(_archiveRoot ?? settings!.ResolveBackupRoot(), settings: settings);
        _ = (_resolvePath ?? (settings ?? Settings.Load()).ArchivePathResolver(archive.Root))(path);
    }

    private static string? Resolve(Func<string, string> resolve, string path)
    {
        try
        {
            return resolve(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    /// <summary>
    /// Удаляет отобранные записи. Защищённые пропускаются: их держат по
    /// случаю, и в пакетной операции они особенно легко ушли бы вместе с
    /// остальными.
    /// </summary>
    public DeleteResult Delete(IEnumerable<ArchiveRow> rows)
    {
        using var operation = OperationGuard.Acquire(exclusive: true, dbPath: _dbPath);
        var settings = _archiveRoot is null ? Settings.Load() : null;
        using var archive = ArchiveGuard.Open(_archiveRoot ?? settings!.ResolveBackupRoot(), settings: settings);
        var resolve = _resolvePath ?? (settings ?? Settings.Load()).ArchivePathResolver(archive.Root);
        _ = new CollectionLog(_dbPath);
        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        var bytes = 0L;
        var deletedPaths = new List<string>();
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        db.Open();

        foreach (var row in rows)
        {
            try
            {
                using var transaction = db.BeginTransaction();
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                var path = resolve(row.File.DestPath);
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var aliases = new List<string>();
                var protectedFile = false;
                command.CommandText = "SELECT dest_path, important FROM collected_files";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        if (Resolve(resolve, reader.GetString(0)) is { } alias && comparer.Equals(alias, path))
                        {
                            aliases.Add(reader.GetString(0));
                            protectedFile |= reader.GetInt64(1) != 0;
                        }
                }
                if (protectedFile) { skipped++; continue; }

                var size = archive.DeleteFile(Path.GetRelativePath(archive.Root, path));

                command.CommandText = "DELETE FROM collected_files WHERE dest_path = $path";
                command.Parameters.AddWithValue("$path", row.File.DestPath);
                foreach (var alias in aliases)
                {
                    command.Parameters["$path"].Value = alias;
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
                deleted++;
                deletedPaths.AddRange(aliases.Append(row.File.DestPath).Distinct());
                bytes += size;
            }
            catch (Exception)
            {
                // Файл занят или недоступен: запись остаётся на месте.
                failed++;
            }
        }

        return new DeleteResult(deleted, skipped, failed, bytes) { DeletedPaths = deletedPaths };
    }

    private bool Matches(ArchiveRow row, ArchiveFilter filter,
        HashSet<long>? departments, Employee? person)
    {
        if (filter.Kind != MediaKind.Any && row.Kind != filter.Kind)
            return false;

        if (filter.ProtectedOnly && !row.File.Important)
            return false;

        if (filter.ShotFrom is { } shotFrom
            && (row.File.ShotAt is null || row.File.ShotAt < shotFrom))
            return false;

        if (filter.ShotTo is { } shotTo
            && (row.File.ShotAt is null || row.File.ShotAt > shotTo))
            return false;

        if (filter.FileName.Length > 0
            && !Path.GetFileName(row.File.DestPath)
                .Contains(filter.FileName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (filter.PersonnelNo.Length > 0
            && !row.PersonnelNo.Contains(filter.PersonnelNo, StringComparison.OrdinalIgnoreCase))
            return false;

        if (filter.EmployeeName.Length > 0
            && !row.EmployeeName.Contains(filter.EmployeeName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Отдел ищется вместе с подчинёнными: спрашивают про управление, а
        // записи закреплены за его взводами.
        if (departments is not null
            && (person?.DepartmentId is not { } dep || !departments.Contains(dep)))
            return false;

        return true;
    }

    private Dictionary<long, DeviceRecord> Cameras(CancellationToken token)
    {
        try
        {
            using var registry = new DeviceRegistry(_dbPath, initialize: false);
            return registry.ListDevices(token).ToDictionary(d => d.Id);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new Dictionary<long, DeviceRecord>();
        }
    }

    private static Dictionary<long, Employee> People(StaffDirectory staff, CancellationToken token)
    {
        try
        {
            return staff.Employees(token: token).ToDictionary(e => e.Id);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new Dictionary<long, Employee>();
        }
    }

    /// <summary>Отдел и все его подчинённые, если отбор по отделу задан.</summary>
    private static HashSet<long>? Departments(ArchiveFilter filter, StaffDirectory staff, CancellationToken token)
    {
        if (filter.DepartmentId is not { } root)
            return null;

        var all = staff.Departments(token);
        var wanted = new HashSet<long> { root };

        // Дерево неглубокое, поэтому проходим по списку, пока он растёт.
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var department in all)
            {
                token.ThrowIfCancellationRequested();
                if (department.ParentId is { } parent
                    && wanted.Contains(parent)
                    && wanted.Add(department.Id))
                {
                    grew = true;
                }
            }
        }

        return wanted;
    }
}
