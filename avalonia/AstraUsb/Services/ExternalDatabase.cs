using MySqlConnector;

namespace AstraUsb.Services;

/// <summary>Итог обмена с внешней базой.</summary>
public sealed record SyncResult(bool Ok, int Sent, string Message);

/// <summary>
/// Учёт собранного во внешней базе.
///
/// Станция работает на своей базе рядом с программой и от внешней не зависит:
/// сеть пропадает, сервер обслуживают, а сбор останавливаться не должен.
/// Поэтому наружу уходит не работа, а сведения о собранном, и уходят они
/// поверх уже записанного локально.
///
/// Таблица создаётся станцией сама, чтобы на сервере не требовалось ручной
/// подготовки. Ключ это имя станции и путь записи: повторная отправка того же
/// обновляет строку, а не плодит вторую.
/// </summary>
public sealed class ExternalDatabase
{
    private readonly Settings _settings;

    public ExternalDatabase(Settings settings) => _settings = settings;

    /// <summary>Строка подключения из настроек станции.</summary>
    private string Connection()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = _settings.SqlHost.Trim(),
            Port = (uint)(_settings.SqlPort is > 0 and < 65536 ? _settings.SqlPort : 3306),
            Database = _settings.SqlDatabase.Trim(),
            UserID = _settings.SqlUser.Trim(),
            Password = _settings.SqlPassword,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 30,
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Проверяет, отвечает ли база и хватает ли прав: создаёт таблицу учёта,
    /// если её ещё нет. Проверить право на запись иначе нельзя, а узнать о его
    /// нехватке лучше при настройке, чем в первую же смену.
    /// </summary>
    public async Task<SyncResult> CheckAsync(CancellationToken token = default)
    {
        if (_settings.SqlHost.Trim().Length == 0)
            return new SyncResult(false, 0, "не указан адрес сервера");

        if (_settings.SqlDatabase.Trim().Length == 0)
            return new SyncResult(false, 0, "не указано имя базы");

        try
        {
            await using var db = new MySqlConnection(Connection());
            await db.OpenAsync(token);
            await EnsureTableAsync(db, token);

            return new SyncResult(true, 0, $"база отвечает: {db.ServerVersion}");
        }
        catch (MySqlException e)
        {
            return new SyncResult(false, 0, Explain(e));
        }
        catch (OperationCanceledException)
        {
            return new SyncResult(false, 0, "сервер не ответил вовремя");
        }
        catch (Exception e)
        {
            return new SyncResult(false, 0, e.Message);
        }
    }

    /// <summary>
    /// Отправляет сведения о собранных записях. Локальный журнал остаётся
    /// источником истины: наружу уходит его отражение.
    /// </summary>
    public async Task<SyncResult> SendAsync(IReadOnlyList<CollectedFile> files,
        string station, CancellationToken token = default)
    {
        if (files.Count == 0)
            return new SyncResult(true, 0, "отправлять нечего");

        try
        {
            await using var db = new MySqlConnection(Connection());
            await db.OpenAsync(token);
            await EnsureTableAsync(db, token);

            var sent = 0;

            foreach (var file in files)
            {
                await using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO bestcam_collected
                        (station, path, device_id, size_bytes, shot_at, collected_at, protected_file, note)
                    VALUES (@station, @path, @device, @size, @shot, @collected, @protected, @note)
                    ON DUPLICATE KEY UPDATE
                        size_bytes = VALUES(size_bytes),
                        shot_at = VALUES(shot_at),
                        collected_at = VALUES(collected_at),
                        protected_file = VALUES(protected_file),
                        note = VALUES(note)
                    """;

                cmd.Parameters.AddWithValue("@station", station);
                cmd.Parameters.AddWithValue("@path", file.DestPath);
                cmd.Parameters.AddWithValue("@device", file.DeviceId);
                cmd.Parameters.AddWithValue("@size", file.SizeBytes);
                cmd.Parameters.AddWithValue("@shot", file.ShotAt);
                cmd.Parameters.AddWithValue("@collected", file.CollectedAt);
                cmd.Parameters.AddWithValue("@protected", file.Important ? 1 : 0);
                cmd.Parameters.AddWithValue("@note", file.Note);

                await cmd.ExecuteNonQueryAsync(token);
                sent++;
            }

            return new SyncResult(true, sent, $"отправлено записей: {sent}");
        }
        catch (MySqlException e)
        {
            return new SyncResult(false, 0, Explain(e));
        }
        catch (OperationCanceledException)
        {
            return new SyncResult(false, 0, "сервер не ответил вовремя");
        }
        catch (Exception e)
        {
            return new SyncResult(false, 0, e.Message);
        }
    }

    /// <summary>
    /// Создаёт таблицу учёта, если её нет. Ключ это станция и путь: одна и та
    /// же запись с одной станции остаётся одной строкой.
    /// </summary>
    private static async Task EnsureTableAsync(MySqlConnection db, CancellationToken token)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS bestcam_collected (
                station        VARCHAR(64)  NOT NULL,
                path           VARCHAR(512) NOT NULL,
                device_id      BIGINT       NOT NULL,
                size_bytes     BIGINT       NOT NULL DEFAULT 0,
                shot_at        DATETIME     NULL,
                collected_at   DATETIME     NOT NULL,
                protected_file TINYINT      NOT NULL DEFAULT 0,
                note           TEXT         NULL,
                PRIMARY KEY (station, path)
            ) CHARACTER SET utf8mb4
            """;
        await cmd.ExecuteNonQueryAsync(token);
    }

    /// <summary>Переводит ошибку сервера в то, что понятно оператору.</summary>
    private static string Explain(MySqlException error) => error.ErrorCode switch
    {
        MySqlErrorCode.AccessDenied =>
            "учётная запись или пароль не подошли",
        MySqlErrorCode.UnableToConnectToHost => "сервер не отвечает",
        MySqlErrorCode.UnknownDatabase => "такой базы на сервере нет",
        MySqlErrorCode.TableAccessDenied or MySqlErrorCode.ColumnAccessDenied =>
            "учётной записи не хватает прав на таблицу учёта",
        _ => error.Message,
    };
}
