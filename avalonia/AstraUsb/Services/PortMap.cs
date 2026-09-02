using Microsoft.Data.Sqlite;

namespace AstraUsb.Services;

/// <summary>
/// Закрепление гнёзд станции за окнами на экране.
///
/// Без него окна занимаются в порядке подключения: воткнули камеру во второй
/// разъём, а она встала в первое окно, потому что оказалась первой. Оператору
/// приходится искать глазами, какая плитка чья. Сопоставление привязывает окно
/// к адресу гнезда на шине, и порядок перестаёт зависеть от очерёдности.
/// </summary>
public sealed class PortMap
{
    private readonly string _dbPath;

    public PortMap(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS port_map (
                port_path TEXT PRIMARY KEY,
                slot      INTEGER NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Закрепляет гнездо за окном. Прежняя привязка этого окна снимается.</summary>
    public void Assign(string portPath, int slot)
    {
        if (string.IsNullOrEmpty(portPath))
            return;

        using var db = Open();
        using var tx = db.BeginTransaction();

        Run(db, tx, "DELETE FROM port_map WHERE slot = $slot", ("$slot", slot));
        Run(db, tx, """
            INSERT INTO port_map (port_path, slot) VALUES ($path, $slot)
            ON CONFLICT(port_path) DO UPDATE SET slot = excluded.slot
            """, ("$path", portPath), ("$slot", slot));

        tx.Commit();
    }

    public void Forget(string portPath)
    {
        using var db = Open();
        Run(db, null, "DELETE FROM port_map WHERE port_path = $path", ("$path", portPath));
    }

    public void Clear()
    {
        using var db = Open();
        Run(db, null, "DELETE FROM port_map");
    }

    /// <summary>Окно, закреплённое за гнездом, или null, если гнездо не размечено.</summary>
    public int? SlotOf(string? portPath)
    {
        if (string.IsNullOrEmpty(portPath))
            return null;

        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT slot FROM port_map WHERE port_path = $path";
        cmd.Parameters.AddWithValue("$path", portPath);
        return cmd.ExecuteScalar() is long slot ? (int)slot : null;
    }

    public IReadOnlyDictionary<string, int> All()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT port_path, slot FROM port_map";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = reader.GetInt32(1);
        return map;
    }

    /// <summary>
    /// Раскладывает носители по окнам: размеченные встают на свои места,
    /// остальные занимают свободные окна по порядку.
    /// </summary>
    public IReadOnlyList<UsbDevice?> Arrange(IReadOnlyList<UsbDevice> devices, int slots)
    {
        var placed = new UsbDevice?[slots];
        var rest = new List<UsbDevice>();

        foreach (var device in devices)
        {
            var slot = SlotOf(device.PortPath);
            if (slot is { } index && index >= 0 && index < slots && placed[index] is null)
                placed[index] = device;
            else
                rest.Add(device);
        }

        var next = 0;
        foreach (var device in rest)
        {
            while (next < slots && placed[next] is not null)
                next++;
            if (next >= slots)
                break;
            placed[next] = device;
        }

        return placed;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection($"Data Source={_dbPath}");
        db.Open();
        return db;
    }

    private static void Run(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null)
            cmd.Transaction = tx;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
