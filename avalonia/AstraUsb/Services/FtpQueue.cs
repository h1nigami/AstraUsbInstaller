using Microsoft.Data.Sqlite;

namespace AstraUsb.Services;

/// <summary>Файл, ожидающий отправки на сервер.</summary>
/// <param name="Id">Место в очереди.</param>
/// <param name="Path">Путь к файлу в архиве.</param>
/// <param name="Attempts">Сколько раз отправка не удалась.</param>
public sealed record FtpItem(long Id, string Path, int Attempts);

/// <summary>
/// Очередь отправки на сервер.
///
/// Задание требует, чтобы при обрыве сети файлы оставались в локальном архиве,
/// а отправка возобновлялась после восстановления связи. Значит очередь должна
/// переживать и обрыв, и перезапуск станции, поэтому она лежит в той же базе,
/// а не в памяти.
/// </summary>
public sealed class FtpQueue
{
    /// <summary>После этого числа неудач файл откладывается: он мешает очереди.</summary>
    public const int MaxAttempts = 5;

    private readonly string _dbPath;

    public FtpQueue(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var db = Open();
        Run(db, """
            CREATE TABLE IF NOT EXISTS ftp_queue (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                path      TEXT NOT NULL UNIQUE,
                attempts  INTEGER NOT NULL DEFAULT 0,
                added_at  TEXT NOT NULL,
                last_error TEXT NOT NULL DEFAULT ''
            )
            """);
    }

    /// <summary>Ставит файл в очередь. Повторная постановка ничего не меняет.</summary>
    public void Add(string path)
    {
        using var db = Open();
        Run(db, """
            INSERT INTO ftp_queue (path, added_at) VALUES ($path, $now)
            ON CONFLICT(path) DO NOTHING
            """,
            ("$path", path), ("$now", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff")));
    }

    public void AddRange(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            Add(path);
    }

    /// <summary>Ближайшие файлы к отправке, начиная с самых давних.</summary>
    public IReadOnlyList<FtpItem> Next(int limit = 20)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, path, attempts FROM ftp_queue
            WHERE attempts < $max
            ORDER BY id
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$max", MaxAttempts);
        cmd.Parameters.AddWithValue("$limit", Math.Max(limit, 1));

        var list = new List<FtpItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new FtpItem(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));

        return list;
    }

    /// <summary>Файл ушёл на сервер: из очереди его убираем.</summary>
    public void Done(long id)
    {
        using var db = Open();
        Run(db, "DELETE FROM ftp_queue WHERE id = $id", ("$id", id));
    }

    /// <summary>Отправка не удалась: помним причину и число попыток.</summary>
    public void Failed(long id, string error)
    {
        using var db = Open();
        Run(db, """
            UPDATE ftp_queue
            SET attempts = attempts + 1, last_error = $error
            WHERE id = $id
            """,
            ("$error", error), ("$id", id));
    }

    /// <summary>Сколько файлов ждёт отправки.</summary>
    public int Count()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ftp_queue WHERE attempts < $max";
        cmd.Parameters.AddWithValue("$max", MaxAttempts);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>Сколько файлов отложено после исчерпания попыток.</summary>
    public int StuckCount()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ftp_queue WHERE attempts >= $max";
        cmd.Parameters.AddWithValue("$max", MaxAttempts);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>Возвращает отложенные файлы в работу: сеть или сервер починили.</summary>
    public int Retry()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE ftp_queue SET attempts = 0 WHERE attempts >= $max";
        cmd.Parameters.AddWithValue("$max", MaxAttempts);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Убирает из очереди файлы, которых больше нет в архиве.</summary>
    public int Prune()
    {
        var gone = Next(int.MaxValue).Where(item => !File.Exists(item.Path)).ToArray();
        foreach (var item in gone)
            Done(item.Id);
        return gone.Length;
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
