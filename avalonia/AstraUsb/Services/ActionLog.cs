using Microsoft.Data.Sqlite;

namespace AstraUsb.Services;

/// <summary>Запись журнала действий.</summary>
/// <param name="At">Когда это случилось.</param>
/// <param name="Kind">Род события: доступ, выгрузка, настройки, уборка.</param>
/// <param name="Message">Что именно произошло, словами для человека.</param>
public sealed record ActionEntry(long Id, DateTime At, string Kind, string Message);

/// <summary>
/// Журнал действий станции.
///
/// Станция стоит в общем помещении и работает без присмотра, поэтому важно
/// видеть, кто входил в закрытые разделы, что менял и что уносил с собой.
/// Записи копятся годами, поэтому у журнала есть предел: самые старые
/// вытесняются, иначе база растёт без конца.
/// </summary>
public sealed class ActionLog
{
    /// <summary>Род события, по нему журнал фильтруют.</summary>
    public const string Access = "доступ";
    public const string Exit = "выход";
    public const string Settings = "настройки";
    public const string Export = "выгрузка";
    public const string Cleanup = "уборка";
    public const string Backup = "загрузка";

    private readonly string _dbPath;

    public ActionLog(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var db = Open();
        Run(db, """
            CREATE TABLE IF NOT EXISTS action_log (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                at      TEXT NOT NULL,
                kind    TEXT NOT NULL DEFAULT '',
                message TEXT NOT NULL DEFAULT ''
            )
            """);
        Run(db, "CREATE INDEX IF NOT EXISTS idx_action_at ON action_log (at)");
    }

    /// <summary>
    /// Заносит событие. Журнал ведётся ради разбора потом, поэтому сбой записи
    /// не должен мешать тому, что происходит сейчас.
    /// </summary>
    public void Write(string kind, string message)
    {
        try
        {
            using var db = Open();
            Run(db, "INSERT INTO action_log (at, kind, message) VALUES ($at, $kind, $message)",
                ("$at", Stamp(DateTime.Now)), ("$kind", kind), ("$message", message));
        }
        catch (SqliteException)
        {
            // База занята другим действием: событие потеряно, работа идёт дальше.
        }
    }

    /// <summary>События за период, от свежих к старым.</summary>
    public IReadOnlyList<ActionEntry> Between(DateTime from, DateTime to, int limit = 500)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, at, kind, message FROM action_log
            WHERE at >= $from AND at <= $to
            ORDER BY at DESC, id DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$from", Stamp(from));
        cmd.Parameters.AddWithValue("$to", Stamp(to));
        cmd.Parameters.AddWithValue("$limit", Math.Max(limit, 1));

        var list = new List<ActionEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ActionEntry(
                reader.GetInt64(0),
                DateTime.TryParse(reader.GetString(1), out var at) ? at : default,
                reader.GetString(2),
                reader.GetString(3)));
        }
        return list;
    }

    public int Count()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM action_log";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Оставляет только последние записи. Вызывается при запуске: журнал за
    /// годы работы иначе разрастается вместе с базой.
    /// </summary>
    /// <returns>Сколько записей вытеснено.</returns>
    public int Trim(int keepEntries)
    {
        if (keepEntries < 0)
            return 0;

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM action_log
            WHERE id NOT IN (
                SELECT id FROM action_log ORDER BY id DESC LIMIT $keep)
            """;
        cmd.Parameters.AddWithValue("$keep", keepEntries);
        return cmd.ExecuteNonQuery();
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

    /// <summary>Тот же формат, что у остальных таблиц: ISO с разделителем T.</summary>
    private static string Stamp(DateTime moment) => moment.ToString("yyyy-MM-ddTHH:mm:ss.ffffff");
}
