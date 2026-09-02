using Microsoft.Data.Sqlite;

namespace AstraUsb;

/// <summary>
/// Учёт устройств и сеансов копирования. Схема и правила идентификации
/// перенесены из Python-версии (usb_monitor.py) без изменений: база у точек
/// уже накоплена, и новая версия обязана читать её как есть.
/// </summary>
public sealed class DeviceRegistry : IDisposable
{
    /// <summary>Файл-маркер на носителе, хранящий его номер.</summary>
    public const string DeviceIdFile = ".astra_id";

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

        // Устройства, потерянные прежней ошибкой регистрации: бэкапы на них
        // есть, а строки нет. Без неё устройство не видно в списке, ему нельзя
        // задать имя, и поиск не находит его файлы — он связывает файлы с
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

    /// <summary>
    /// Определяет номер устройства: сначала по маркеру на носителе, затем по
    /// серийнику, и только потом заводит новое.
    /// </summary>
    public long ResolveDeviceId(string? mountPoint, string? serial, string? label, string? devName)
    {
        var now = Timestamp();
        var idFromUsb = ReadDeviceIdFromUsb(mountPoint);

        if (idFromUsb is { } known)
        {
            if (!DeviceExists(known))
                RegisterIdFromUsb(known, serial, label ?? devName ?? "", now);

            Execute("UPDATE devices SET last_seen = $now, label = $label WHERE id = $id",
                ("$now", now), ("$label", label ?? devName ?? ""), ("$id", known));
            return known;
        }

        if (!string.IsNullOrEmpty(serial))
        {
            var bySerial = FindIdBySerial(serial);
            if (bySerial is { } existing)
            {
                WriteDeviceIdToUsb(mountPoint, existing);
                return existing;
            }
        }

        var created = CreateDevice(serial, label, devName, now);
        WriteDeviceIdToUsb(mountPoint, created);
        return created;
    }

    /// <summary>
    /// Заводит устройство под номером, который принёс носитель.
    /// Серийник может быть уже занят: USB-эмуляторы отдают один и тот же на все
    /// экземпляры. Тогда берём серийник, уникализированный номером устройства —
    /// номер и так первичный ключ, столкнуться он не может.
    /// </summary>
    private void RegisterIdFromUsb(long deviceId, string? serial, string label, string now)
    {
        string[] candidates =
        [
            serial ?? "",
            $"{(string.IsNullOrEmpty(serial) ? "NOSERIAL" : serial)}#{deviceId}",
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                Execute("""
                    INSERT INTO devices (id, serial, label, first_seen, last_seen)
                    VALUES ($id, $serial, $label, $now, $now)
                    """,
                    ("$id", deviceId), ("$serial", candidate), ("$label", label), ("$now", now));
                return;
            }
            catch (SqliteException)
            {
                // Серийник занят другим устройством — пробуем следующий вариант.
            }
        }
    }

    private long CreateDevice(string? serial, string? label, string? devName, string now)
    {
        // serial объявлен UNIQUE NOT NULL. Реальный серийник и так принадлежит
        // одному устройству, но когда железо не отдаёт его вовсе, одинаковая
        // пустая строка столкнулась бы на втором таком носителе.
        var effective = string.IsNullOrEmpty(serial)
            ? $"NOSERIAL_{(string.IsNullOrEmpty(devName) ? "usb" : devName)}_{now}"
            : serial;

        Execute("""
            INSERT INTO devices (serial, label, first_seen, last_seen)
            VALUES ($serial, $label, $now, $now)
            """,
            ("$serial", effective),
            ("$label", label ?? devName ?? "USB"),
            ("$now", now));

        return (long)(Scalar("SELECT last_insert_rowid()") ?? 0L);
    }

    public bool DeviceExists(long id) =>
        Scalar("SELECT 1 FROM devices WHERE id = $id", ("$id", id)) is not null;

    public long? FindIdBySerial(string? serial)
    {
        if (string.IsNullOrEmpty(serial))
            return null;
        return Scalar("SELECT id FROM devices WHERE serial = $serial", ("$serial", serial)) as long?;
    }

    public string? GetDeviceName(long id) =>
        Scalar("SELECT name FROM devices WHERE id = $id", ("$id", id)) as string;

    /// <summary>
    /// Подпись устройства для человека: заданное имя, иначе голый номер.
    /// Папка бэкапа при этом всегда Device{номер} и не переименовывается.
    /// </summary>
    public static string FriendlyLabel(long deviceId, string? name) =>
        string.IsNullOrEmpty(name) ? deviceId.ToString() : name;

    public static long? ReadDeviceIdFromUsb(string? mountPoint)
    {
        if (string.IsNullOrEmpty(mountPoint))
            return null;
        try
        {
            var text = File.ReadAllText(Path.Combine(mountPoint, DeviceIdFile)).Trim();
            return long.TryParse(text, out var id) ? id : null;
        }
        catch (Exception)
        {
            // Маркера нет, носитель только что извлекли, файл нечитаем —
            // всё это штатно: номер просто будет определён другим путём.
            return null;
        }
    }

    public static void WriteDeviceIdToUsb(string? mountPoint, long deviceId)
    {
        if (string.IsNullOrEmpty(mountPoint))
            return;
        try
        {
            File.WriteAllText(Path.Combine(mountPoint, DeviceIdFile), $"{deviceId}\n");
        }
        catch (Exception)
        {
            // Носитель может быть только для чтения — это не повод прерывать копирование.
        }
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
            // Колонка уже есть или вставлять нечего — обычное дело при миграции.
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

    public void Dispose() => _db.Dispose();
}
