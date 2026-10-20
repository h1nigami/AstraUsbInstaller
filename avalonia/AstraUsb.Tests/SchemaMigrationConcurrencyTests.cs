using AstraUsb.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AstraUsb.Tests;

public sealed class SchemaMigrationConcurrencyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("astra-schema-race-").FullName;

    [Fact]
    public void A_late_collection_migration_error_rolls_back_the_whole_schema_change()
    {
        var path = Path.Combine(_root, "collection-rollback.db");
        int maximum;
        using (var database = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            database.Open();
            maximum = SQLitePCL.raw.sqlite3_limit(database.Handle!, SQLitePCL.raw.SQLITE_LIMIT_COLUMN, -1);
            using var command = database.CreateCommand();
            command.CommandText = """
                CREATE TABLE collected_files (
                    id INTEGER PRIMARY KEY, device_id INTEGER, dest_path TEXT UNIQUE,
                    size_bytes INTEGER, shot_at TEXT, collected_at TEXT,
                """ + string.Join(",", Enumerable.Range(0, maximum - 7).Select(index => $"filler{index} INTEGER")) + ")";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<SqliteException>(() => new CollectionLog(path));

        Assert.Contains("too many columns", error.Message);
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM pragma_table_info('collected_files') WHERE name = 'important'"));
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'idx_collected_at'"));
        Execute(path, "ALTER TABLE collected_files DROP COLUMN filler0");
        Assert.Equal(0, new CollectionLog(path).Count());
    }

    [Fact]
    public void A_late_registry_migration_error_rolls_back_and_remains_visible()
    {
        var path = Path.Combine(_root, "registry-rollback.db");
        CreateOldDatabase(path);
        Execute(path, """
            ALTER TABLE devices ADD COLUMN firmware_id TEXT;
            UPDATE devices SET firmware_id = 'duplicate';
            INSERT INTO devices (id, serial, first_seen, last_seen, firmware_id)
            VALUES (9, 'OLD9', '2026-09-01T12:00:00', '2026-09-01T12:00:00', 'duplicate');
            """);

        var error = Assert.Throws<SqliteException>(() => { using var registry = new DeviceRegistry(path); });

        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Contains("firmware_id", error.Message);
        Assert.Equal(0L, Scalar(path, "SELECT COUNT(*) FROM pragma_table_info('devices') WHERE name = 'person'"));
        Assert.Equal(2L, Scalar(path, "SELECT COUNT(*) FROM devices"));
        Execute(path, "UPDATE devices SET firmware_id = NULL WHERE id = 9");
        using var recovered = new DeviceRegistry(path);
        Assert.Equal(3, recovered.ListDevices().Count);
    }

    [Theory]
    [InlineData("CollectionLog")]
    [InlineData("DeviceRegistry")]
    public async Task Concurrent_initializers_migrate_an_old_database_without_losing_rows(string service)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var path = Path.Combine(_root, $"{service}-{attempt}.db");
            CreateOldDatabase(path);
            using var start = new Barrier(13);
            var initializers = Enumerable.Range(0, 12).Select(_ => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                return Record.Exception(() => Initialize(service, path));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            start.SignalAndWait();
            var errors = await Task.WhenAll(initializers);

            Assert.True(errors.All(error => error is null), string.Join(Environment.NewLine,
                errors.Where(error => error is not null).Select(error => error!.ToString())));
            if (service == "CollectionLog")
            {
                var row = Assert.Single(new CollectionLog(path).CollectedBetween(DateTime.MinValue, DateTime.MaxValue));
                Assert.Equal("/archive/Device7/old.mp4", row.DestPath);
                Assert.False(row.Important);
                Assert.Equal("", row.Note);
            }
            else
            {
                using var registry = new DeviceRegistry(path);
                var rows = registry.ListDevices();
                Assert.Equal(2, rows.Count);
                Assert.Equal("OLD7", Assert.Single(rows, row => row.Id == 7).Serial);
                Assert.Equal("RECOVERED_8", Assert.Single(rows, row => row.Id == 8).Serial);
            }
        }
    }

    private static void Initialize(string service, string path)
    {
        if (service == "CollectionLog")
            _ = new CollectionLog(path);
        else
        {
            using var registry = new DeviceRegistry(path);
        }
    }

    private static void CreateOldDatabase(string path)
    {
        using var database = new SqliteConnection($"Data Source={path};Pooling=False;Foreign Keys=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            CREATE TABLE collected_files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id INTEGER NOT NULL,
                dest_path TEXT NOT NULL UNIQUE,
                size_bytes INTEGER NOT NULL DEFAULT 0,
                shot_at TEXT,
                collected_at TEXT NOT NULL
            );
            CREATE INDEX idx_collected_at ON collected_files (collected_at);
            CREATE INDEX idx_collected_device ON collected_files (device_id);
            INSERT INTO collected_files (device_id, dest_path, size_bytes, collected_at)
            VALUES (7, '/archive/Device7/old.mp4', 42, '2026-09-01T12:00:00');
            CREATE TABLE devices (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                serial TEXT UNIQUE NOT NULL,
                label TEXT DEFAULT '',
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL
            );
            INSERT INTO devices (id, serial, first_seen, last_seen)
            VALUES (7, 'OLD7', '2026-09-01T12:00:00', '2026-09-01T12:00:00');
            CREATE TABLE backups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id INTEGER NOT NULL REFERENCES devices(id),
                dest_path TEXT NOT NULL,
                total_files INTEGER DEFAULT 0,
                total_bytes INTEGER DEFAULT 0,
                started_at TEXT NOT NULL,
                finished_at TEXT NOT NULL
            );
            INSERT INTO backups (device_id, dest_path, started_at, finished_at)
            VALUES (8, '/archive/Device8', '2026-09-01T12:00:00', '2026-09-01T12:05:00');
            """;
        command.ExecuteNonQuery();
    }

    private static void Execute(string path, string sql)
    {
        using var database = new SqliteConnection($"Data Source={path};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(string path, string sql)
    {
        using var database = new SqliteConnection($"Data Source={path};Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
