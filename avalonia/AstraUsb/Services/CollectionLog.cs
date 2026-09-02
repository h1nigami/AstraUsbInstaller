using Microsoft.Data.Sqlite;

namespace AstraUsb.Services;

/// <summary>Запись о собранном файле.</summary>
/// <param name="DeviceId">Камера, с которой он приехал.</param>
/// <param name="DestPath">Где файл лежит в хранилище.</param>
/// <param name="SizeBytes">Размер.</param>
/// <param name="ShotAt">
/// Время съёмки по часам камеры. Может врать: если часы не выставлены,
/// приезжают даты вроде 1970 года.
/// </param>
/// <param name="CollectedAt">
/// Время загрузки в станцию. Ставит станция, поэтому достоверно — по нему и
/// ищем.
/// </param>
public sealed record CollectedFile(
    long DeviceId,
    string DestPath,
    long SizeBytes,
    DateTime? ShotAt,
    DateTime CollectedAt)
{
    /// <summary>Раньше этой даты съёмка невозможна: часы камеры не выставлены.</summary>
    private static readonly DateTime Sane = new(2015, 1, 1);

    /// <summary>Можно ли доверять времени съёмки.</summary>
    public bool ShotAtTrusted =>
        ShotAt is { } shot && shot >= Sane && shot <= CollectedAt.AddDays(1);
}

/// <summary>
/// Журнал собранных файлов.
///
/// Время съёмки берётся с камеры и потому ненадёжно: часы на регистраторе
/// сбиваются, а выставить их станция не умеет. Поэтому каждая запись
/// получает ещё и время загрузки — его ставит сама станция, и именно оно
/// служит опорой для поиска и для чистки по давности.
/// </summary>
public sealed class CollectionLog
{
    private readonly string _dbPath;

    public CollectionLog(string dbPath)
    {
        _dbPath = dbPath;
        using var db = Open();
        Run(db, """
            CREATE TABLE IF NOT EXISTS collected_files (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id    INTEGER NOT NULL,
                dest_path    TEXT NOT NULL UNIQUE,
                size_bytes   INTEGER NOT NULL DEFAULT 0,
                shot_at      TEXT,
                collected_at TEXT NOT NULL
            )
            """);
        Run(db, "CREATE INDEX IF NOT EXISTS idx_collected_at ON collected_files (collected_at)");
        Run(db, "CREATE INDEX IF NOT EXISTS idx_collected_device ON collected_files (device_id)");
    }

    /// <summary>
    /// Записывает файлы одного сеанса. Повторная загрузка того же файла
    /// обновляет запись, а не плодит вторую: путь в хранилище уникален.
    /// </summary>
    public void Record(IEnumerable<CollectedFile> files)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();

        foreach (var file in files)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO collected_files (device_id, dest_path, size_bytes, shot_at, collected_at)
                VALUES ($device, $dest, $size, $shot, $collected)
                ON CONFLICT(dest_path) DO UPDATE SET
                    size_bytes = excluded.size_bytes,
                    shot_at = excluded.shot_at,
                    collected_at = excluded.collected_at
                """;
            cmd.Parameters.AddWithValue("$device", file.DeviceId);
            cmd.Parameters.AddWithValue("$dest", file.DestPath);
            cmd.Parameters.AddWithValue("$size", file.SizeBytes);
            cmd.Parameters.AddWithValue("$shot",
                file.ShotAt is { } shot ? Stamp(shot) : DBNull.Value);
            cmd.Parameters.AddWithValue("$collected", Stamp(file.CollectedAt));
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Файлы, загруженные в станцию за указанный промежуток. Поиск идёт по
    /// времени загрузки, а не съёмки: только оно достоверно.
    /// </summary>
    public IReadOnlyList<CollectedFile> CollectedBetween(DateTime from, DateTime to, long? deviceId = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, dest_path, size_bytes, shot_at, collected_at
            FROM collected_files
            WHERE collected_at >= $from AND collected_at <= $to
            """ + (deviceId is null ? "" : " AND device_id = $device")
              + " ORDER BY collected_at DESC";

        cmd.Parameters.AddWithValue("$from", Stamp(from));
        cmd.Parameters.AddWithValue("$to", Stamp(to));
        if (deviceId is { } id)
            cmd.Parameters.AddWithValue("$device", id);

        return Read(cmd);
    }

    /// <summary>Файлы, загруженные раньше указанной даты, — для чистки по давности.</summary>
    public IReadOnlyList<CollectedFile> CollectedBefore(DateTime moment)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, dest_path, size_bytes, shot_at, collected_at
            FROM collected_files
            WHERE collected_at < $moment
            ORDER BY collected_at
            """;
        cmd.Parameters.AddWithValue("$moment", Stamp(moment));
        return Read(cmd);
    }

    public void Forget(string destPath)
    {
        using var db = Open();
        Run(db, "DELETE FROM collected_files WHERE dest_path = $dest", ("$dest", destPath));
    }

    public int Count()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM collected_files";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>Тот же формат, что у Python-версии: ISO с разделителем T.</summary>
    private static string Stamp(DateTime moment) => moment.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");

    private static IReadOnlyList<CollectedFile> Read(SqliteCommand cmd)
    {
        var list = new List<CollectedFile>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CollectedFile(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
                DateTime.Parse(reader.GetString(4))));
        }
        return list;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection($"Data Source={_dbPath}");
        db.Open();
        return db;
    }

    private static void Run(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
