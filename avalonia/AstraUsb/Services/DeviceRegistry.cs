using Microsoft.Data.Sqlite;

namespace AstraUsb.Services;

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

        // Номер, который камера пишет в свой журнал. Он живёт в аппарате и
        // переживает замену карты, поэтому опознание идёт прежде всего по нему.
        TryExecute("ALTER TABLE devices ADD COLUMN firmware_id TEXT");
        TryExecute("CREATE UNIQUE INDEX IF NOT EXISTS idx_devices_firmware"
                   + " ON devices (firmware_id) WHERE firmware_id IS NOT NULL");

        // Номер, которым камера подписывает имена своих записей. Он живёт в
        // аппарате, поэтому позволяет узнать камеру с заменённой картой.
        // Без UNIQUE: оператор может прописать один номер двум аппаратам, и
        // отказ вставки был бы хуже неоднозначности.
        TryExecute("ALTER TABLE devices ADD COLUMN camera_no TEXT");

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
    /// экземпляры. Тогда берём серийник, уникализированный номером устройства:
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
                // Серийник занят другим устройством, пробуем следующий вариант.
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
            // Маркера нет, носитель только что извлекли, файл нечитаем,
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
            // Носитель может быть только для чтения, это не повод прерывать копирование.
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

    /// <summary>
    /// Находит или заводит камеру по номеру с её карты.
    ///
    /// Файл на карте и есть единственный источник истины. Если файла нет, станция
    /// выдаёт номер и обязательно записывает его: без записи камера при
    /// следующем подключении будет опознана как новая. Чужой номер нашего
    /// формата не перезаписывается: камера могла приехать с другой станции.
    /// </summary>
    /// <param name="recording">
    /// Что удалось прочитать из имени последней записи. Запасной признак:
    /// карту могли заменить, а номер аппарата остаётся в именах файлов.
    /// </param>
    public long ResolveByCard(string? mountPoint, int stationNumber,
        string? label, string? devName, RecordingInfo? recording = null)
    {
        var now = Timestamp();
        var card = CardIdentity.Read(mountPoint);

        if (!string.IsNullOrEmpty(card))
            return Remember(Upsert(card, label ?? devName ?? "", now), recording);

        // Номера на карте нет. Прежде чем счесть камеру новой, проверяем её
        // собственный номер из имён записей: так узнаётся аппарат, которому
        // поставили другую карту.
        if (recording?.HasDeviceNo == true
            && FindByCameraNo(recording.DeviceNo) is { } known)
        {
            // Прежний номер возвращаем на новую карту, иначе при следующем
            // подключении камера опять станет новой.
            if (FirmwareIdOf(known) is { } previous)
                CardIdentity.Write(mountPoint, previous);

            Execute("UPDATE devices SET last_seen = $now, label = $label WHERE id = $id",
                ("$now", now), ("$label", label ?? devName ?? ""), ("$id", known));
            return Remember(known, recording);
        }

        // Камера незнакомая, выдаём свой номер и кладём на карту.
        var issued = CardIdentity.Format(stationNumber, NextSequence(stationNumber));
        CardIdentity.Write(mountPoint, issued);
        return Remember(Upsert(issued, label ?? devName ?? "", now), recording);
    }

    /// <summary>
    /// Запоминает номер, которым камера подписывает записи. Он понадобится,
    /// когда карту заменят и опознавать придётся по нему.
    /// </summary>
    private long Remember(long deviceId, RecordingInfo? recording)
    {
        if (recording?.HasDeviceNo == true)
            Execute("UPDATE devices SET camera_no = $no WHERE id = $id",
                ("$no", recording.DeviceNo), ("$id", deviceId));
        return deviceId;
    }

    /// <summary>Камера с таким номером в именах записей, если она известна.</summary>
    public long? FindByCameraNo(string? cameraNo)
    {
        if (string.IsNullOrEmpty(cameraNo))
            return null;
        return Scalar("SELECT id FROM devices WHERE camera_no = $no ORDER BY id LIMIT 1",
            ("$no", cameraNo)) as long?;
    }

    /// <summary>Номер, которым камера подписывает записи, если он известен.</summary>
    public string? CameraNoOf(long deviceId) =>
        Scalar("SELECT camera_no FROM devices WHERE id = $id", ("$id", deviceId)) as string;

    /// <summary>Следующий свободный порядковый номер этой станции.</summary>
    private int NextSequence(int stationNumber)
    {
        var prefix = $"BCU-{Math.Clamp(stationNumber, 0, 99):00}-";
        var taken = Scalar(
            "SELECT MAX(CAST(SUBSTR(firmware_id, LENGTH($prefix) + 1) AS INTEGER))"
            + " FROM devices WHERE firmware_id LIKE $like",
            ("$prefix", prefix), ("$like", prefix + "%"));

        return taken is long max ? (int)max + 1 : 1;
    }

    /// <summary>Заводит камеру под этим номером или обновляет уже известную.</summary>
    private long Upsert(string cardId, string label, string now)
    {
        if (Scalar("SELECT id FROM devices WHERE firmware_id = $fw", ("$fw", cardId)) is long known)
        {
            Execute("UPDATE devices SET last_seen = $now, label = $label WHERE id = $id",
                ("$now", now), ("$label", label), ("$id", known));
            return known;
        }

        Execute("""
            INSERT INTO devices (serial, label, first_seen, last_seen, firmware_id)
            VALUES ($serial, $label, $now, $now, $fw)
            """,
            ("$serial", $"CARD_{cardId}"), ("$label", label), ("$now", now), ("$fw", cardId));

        return (long)(Scalar("SELECT last_insert_rowid()") ?? 0L);
    }

    /// <summary>
    /// Находит или заводит камеру по её удостоверению.
    ///
    /// Номер из прошивки служит главным ключом: он принадлежит аппарату, а не карте.
    /// Маркер на карте остаётся запасным путём для камер, которым номер не
    /// прописали. Серийник USB не используется вовсе: у этой модели он зашит
    /// одинаковым для всех экземпляров.
    /// </summary>
    public long ResolveByIdentity(DeviceIdentity identity, string? mountPoint,
        string? label, string? devName)
    {
        var now = Timestamp();

        if (identity.Kind == IdentityKind.FirmwareId)
        {
            if (Scalar("SELECT id FROM devices WHERE firmware_id = $fw",
                    ("$fw", identity.Value)) is long known)
            {
                Execute("UPDATE devices SET last_seen = $now, label = $label WHERE id = $id",
                    ("$now", now), ("$label", label ?? devName ?? ""), ("$id", known));
                return known;
            }

            Execute("""
                INSERT INTO devices (serial, label, first_seen, last_seen, firmware_id)
                VALUES ($serial, $label, $now, $now, $fw)
                """,
                ("$serial", $"CAM_{identity.Value}"),
                ("$label", label ?? devName ?? ""),
                ("$now", now),
                ("$fw", identity.Value));

            return (long)(Scalar("SELECT last_insert_rowid()") ?? 0L);
        }

        // Номера у камеры нет, работаем по прежней схеме с маркером на карте.
        return ResolveDeviceId(mountPoint, null, label, devName);
    }

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
                   COALESCE(e.full_name, ''), e.department_id
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
                    reader.IsDBNull(7) ? null : reader.GetInt64(7)));
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
            SELECT id, serial, label, COALESCE(name, ''), first_seen, last_seen
            FROM devices ORDER BY id
            """;

        var list = new List<DeviceRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new DeviceRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), "", null));
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
    long? DepartmentId);

