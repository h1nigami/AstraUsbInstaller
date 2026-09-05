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

    public ArchiveSearch(string dbPath) => _dbPath = dbPath;

    public IReadOnlyList<ArchiveRow> Find(ArchiveFilter filter, int limit = Limit)
    {
        var log = new CollectionLog(_dbPath);
        var staff = new StaffDirectory(_dbPath);

        var from = filter.CollectedFrom ?? DateTime.MinValue;
        var to = filter.CollectedTo ?? DateTime.MaxValue;
        var found = log.CollectedBetween(from, to, filter.DeviceId);

        var cameras = Cameras();
        var people = People(staff);
        var departments = Departments(filter, staff);

        var rows = new List<ArchiveRow>();

        foreach (var file in found)
        {
            var camera = cameras.GetValueOrDefault(file.DeviceId);
            var person = camera?.EmployeeId is { } id ? people.GetValueOrDefault(id) : null;

            var name = camera?.Name is { Length: > 0 } given
                ? given
                : camera?.FirmwareId is { Length: > 0 } number
                    ? number
                    : file.DeviceId.ToString();

            var row = new ArchiveRow(
                file,
                name,
                person?.FullName ?? "",
                person?.PersonnelNo ?? "",
                person?.DepartmentId is { } dep ? staff.DepartmentPath(dep) : "");

            if (!Matches(row, filter, departments, person))
                continue;

            rows.Add(row);
            if (rows.Count >= limit)
                break;
        }

        return rows;
    }

    /// <summary>
    /// Удаляет отобранные записи. Защищённые пропускаются: их держат по
    /// случаю, и в пакетной операции они особенно легко ушли бы вместе с
    /// остальными.
    /// </summary>
    public DeleteResult Delete(IEnumerable<ArchiveRow> rows)
    {
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
                command.CommandText = "SELECT important FROM collected_files WHERE dest_path = $path";
                command.Parameters.AddWithValue("$path", row.File.DestPath);
                if (command.ExecuteScalar() is long important && important != 0)
                {
                    skipped++;
                    continue;
                }

                var file = new FileInfo(row.File.DestPath);
                var size = file.Exists ? file.Length : 0;
                if (file.Exists)
                    file.Delete();

                command.CommandText = "DELETE FROM collected_files WHERE dest_path = $path";
                command.ExecuteNonQuery();
                transaction.Commit();
                deleted++;
                deletedPaths.Add(row.File.DestPath);
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

    private Dictionary<long, DeviceRecord> Cameras()
    {
        try
        {
            using var registry = new DeviceRegistry(_dbPath);
            return registry.ListDevices().ToDictionary(d => d.Id);
        }
        catch (Exception)
        {
            return new Dictionary<long, DeviceRecord>();
        }
    }

    private static Dictionary<long, Employee> People(StaffDirectory staff)
    {
        try
        {
            return staff.Employees().ToDictionary(e => e.Id);
        }
        catch (Exception)
        {
            return new Dictionary<long, Employee>();
        }
    }

    /// <summary>Отдел и все его подчинённые, если отбор по отделу задан.</summary>
    private static HashSet<long>? Departments(ArchiveFilter filter, StaffDirectory staff)
    {
        if (filter.DepartmentId is not { } root)
            return null;

        var all = staff.Departments();
        var wanted = new HashSet<long> { root };

        // Дерево неглубокое, поэтому проходим по списку, пока он растёт.
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var department in all)
            {
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
