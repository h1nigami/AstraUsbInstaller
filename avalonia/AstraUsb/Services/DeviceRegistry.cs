using Microsoft.Data.Sqlite;

namespace AstraUsb.Services;

/// <summary>
/// Учёт устройств и сеансов копирования в общей базе Python и Avalonia.
/// Новые строки используют ID регистратора; старые остаются доступными.
/// </summary>
public sealed class DeviceRegistry : IDisposable
{
    /// <summary>Папка бэкапа всегда называется так и при переименовании не меняется.</summary>
    public const string DeviceDirPrefix = "Device";

    private readonly SqliteConnection _db;

    public DeviceRegistry(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS devices (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                serial      TEXT UNIQUE NOT NULL,
                label       TEXT DEFAULT '',
                person      TEXT DEFAULT '',
                name        TEXT DEFAULT '',
                first_seen  TEXT NOT NULL,
                last_seen   TEXT NOT NULL
            )
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS backups (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id   INTEGER NOT NULL REFERENCES devices(id),
                dest_path   TEXT NOT NULL,
                total_files INTEGER DEFAULT 0,
                total_bytes INTEGER DEFAULT 0,
                started_at  TEXT NOT NULL,
                finished_at TEXT NOT NULL
            )
            """);

        // Миграции старых баз: колонки добавлялись со временем.
        TryExecute("ALTER TABLE devices ADD COLUMN person TEXT DEFAULT ''");
        TryExecute("ALTER TABLE devices ADD COLUMN name TEXT DEFAULT ''");
        if (Scalar("SELECT 1 FROM pragma_table_info('devices') WHERE name = 'id_source'") is null)
            Execute("ALTER TABLE devices ADD COLUMN id_source TEXT DEFAULT ''");

        // Колонка остаётся для чтения старых записей и карточек сотрудников.
        TryExecute("ALTER TABLE devices ADD COLUMN firmware_id TEXT");
        TryExecute("CREATE UNIQUE INDEX IF NOT EXISTS idx_devices_firmware"
                   + " ON devices (firmware_id) WHERE firmware_id IS NOT NULL");

        // Устройства, потерянные прежней ошибкой регистрации: бэкапы на них
        // есть, а строки нет. Без неё устройство не видно в списке, ему нельзя
        // задать имя, и поиск не находит его файлы: он связывает файлы с
        // устройствами джойном.
        TryExecute("""
            INSERT INTO devices (id, serial, label, first_seen, last_seen)
            SELECT b.device_id, 'RECOVERED_' || b.device_id, '',
                   MIN(b.started_at), MAX(b.finished_at)
            FROM backups b
            LEFT JOIN devices d ON d.id = b.device_id
            WHERE d.id IS NULL
            GROUP BY b.device_id
            """);
    }

    /// <summary>Регистрирует номер, заданный оператором в регистраторе.</summary>
    public long ResolveDeviceId(string? mountPoint, string? serial, string? label, string? devName)
    {
        var now = Timestamp();
        var id = DeviceIdentifier.Read(mountPoint
            ?? throw new InvalidDataException("Носитель регистратора не указан"));
        if (DeviceExists(id))
        {
            if (IdSourceOf(id) != "device")
                throw new InvalidDataException($"ID {id} занят прежней записью");
        }
        else
        {
            RegisterDeviceId(id, serial, label ?? devName ?? "", now);
        }

        Execute("UPDATE devices SET last_seen = $now, label = $label WHERE id = $id",
            ("$now", now), ("$label", label ?? devName ?? ""), ("$id", id));
        return id;
    }

    private string? IdSourceOf(long id) =>
        Scalar("SELECT id_source FROM devices WHERE id = $id", ("$id", id)) as string;

    private void RegisterDeviceId(long deviceId, string? serial, string label, string now)
    {
        var baseSerial = string.IsNullOrEmpty(serial) ? $"DEVICE_ID_{deviceId}" : serial;
        string[] candidates =
        [
            baseSerial,
            $"{baseSerial}#{deviceId}",
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                Execute("""
                    INSERT INTO devices (id, serial, label, first_seen, last_seen, id_source)
                    VALUES ($id, $serial, $label, $now, $now, 'device')
                    """,
                    ("$id", deviceId), ("$serial", candidate), ("$label", label), ("$now", now));
                return;
            }
            catch (SqliteException error) when (error.SqliteExtendedErrorCode == 2067)
            {
                // Общий USB-серийник не объединяет разные регистраторы.
            }
        }
        throw new InvalidDataException($"Не удалось зарегистрировать ID {deviceId}");
    }

    public bool DeviceExists(long id) =>
        Scalar("SELECT 1 FROM devices WHERE id = $id", ("$id", id)) is not null;

    public string? GetDeviceName(long id) =>
        Scalar("SELECT name FROM devices WHERE id = $id", ("$id", id)) as string;

    /// <summary>
    /// Подпись связывает номер на экране с папкой Device{номер}. Заданное имя
    /// остаётся дополнительным описанием устройства.
    /// </summary>
    public static string FriendlyLabel(long deviceId, string? name)
    {
        var astraId = $"Astra ID {deviceId}";
        return string.IsNullOrEmpty(name) ? astraId : $"{astraId} · {name}";
    }

    /// <summary>Тот же формат, что пишет Python-версия: ISO с разделителем T.</summary>
    public static string Timestamp() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");

    private void Execute(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private void TryExecute(string sql)
    {
        try
        {
            Execute(sql);
        }
        catch (SqliteException)
        {
            // Колонка уже есть или вставлять нечего, обычное дело при миграции.
        }
    }

    private object? Scalar(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    /// <summary>Определяет ID по данным регистратора, не меняя носитель.</summary>
    public long ResolveByCard(string? mountPoint, int stationNumber,
        string? label, string? devName)
        => ResolveDeviceId(mountPoint, null, label, devName);

    /// <summary>Номер камеры из прошивки, если он известен.</summary>
    public string? FirmwareIdOf(long deviceId) =>
        Scalar("SELECT firmware_id FROM devices WHERE id = $id", ("$id", deviceId)) as string;

    /// <summary>Все известные камеры для вкладки «Устройства».</summary>
    public IReadOnlyList<DeviceRecord> ListDevices()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT d.id, d.serial, d.label, COALESCE(d.name, ''),
                   d.first_seen, d.last_seen,
                   COALESCE(e.full_name, ''), e.department_id,
                   COALESCE(d.firmware_id, ''), d.employee_id
            FROM devices d
            LEFT JOIN employees e ON e.id = d.employee_id
            ORDER BY d.id
            """;

        var list = new List<DeviceRecord>();
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new DeviceRecord(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9)));
            }
        }
        catch (SqliteException)
        {
            // Справочник сотрудников ещё не создан, читаем без него.
            return ListDevicesWithoutStaff();
        }
        return list;
    }

    private IReadOnlyList<DeviceRecord> ListDevicesWithoutStaff()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT id, serial, label, COALESCE(name, ''), first_seen, last_seen,
                   COALESCE(firmware_id, '')
            FROM devices ORDER BY id
            """;

        var list = new List<DeviceRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new DeviceRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), "", null,
                reader.GetString(6), null));
        }
        return list;
    }

    /// <summary>Задаёт камере человекочитаемое имя. Папка бэкапа не меняется.</summary>
    public void Rename(long deviceId, string name)
    {
        Execute("UPDATE devices SET name = $name WHERE id = $id",
            ("$name", name), ("$id", deviceId));
    }

    public void Dispose() => _db.Dispose();
}

/// <summary>Строка вкладки «Устройства».</summary>
public sealed record DeviceRecord(
    long Id,
    string Serial,
    string Label,
    string Name,
    string FirstSeen,
    string LastSeen,
    string EmployeeName,
    long? DepartmentId,
    string FirmwareId,
    long? EmployeeId);

