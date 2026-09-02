using System.Text.Json;

namespace AstraUsb.Services;

/// <summary>
/// Настройки станции. Лежат рядом с базой в data/settings.json, чтобы
/// переустановка программы их не трогала, так же, как у Python-версии.
/// </summary>
public sealed class Settings
{
    /// <summary>Куда складывать копии.</summary>
    public string BackupRoot { get; set; } = "";

    /// <summary>Что делать при нехватке места: предупреждать или перезаписывать.</summary>
    public StorageMode StorageMode { get; set; } = StorageMode.Warn;

    /// <summary>Порог свободного места в гигабайтах.</summary>
    public int MinFreeGb { get; set; } = 50;

    /// <summary>Удалять с камеры видео после успешной загрузки.</summary>
    public bool DeleteVideoAfterCopy { get; set; }

    /// <summary>Сколько дней держать собранные записи; 0 хранит их бессрочно.</summary>
    public int KeepDays { get; set; }

    /// <summary>
    /// Сколько секунд ждать, не смонтирует ли карту система, прежде чем
    /// монтировать её самим. Двойное монтирование одного FAT опаснее задержки.
    /// </summary>
    public int MountGraceSeconds { get; set; } = 4;

    /// <summary>Отправлять собранные записи на сервер.</summary>
    public bool FtpEnabled { get; set; }

    public string FtpHost { get; set; } = "";
    public int FtpPort { get; set; } = 21;
    public string FtpUser { get; set; } = "";

    /// <summary>
    /// Пароль сервера. Хранится как есть: клиент FTP должен предъявить его при
    /// каждой отправке, а расшифровать сохранённое можно тем же ключом, что
    /// лежал бы рядом. Поэтому защита здесь это права на файл настроек, а не
    /// шифрование, о чём сказано в разделе настроек.
    /// </summary>
    public string FtpPassword { get; set; } = "";

    /// <summary>Папка на сервере, куда складывать записи.</summary>
    public string FtpFolder { get; set; } = "";

    /// <summary>Отправлять по защищённому соединению.</summary>
    public bool FtpSsl { get; set; }

    /// <summary>
    /// Звуковая тревога при нехватке места и обрыве сети. Экран станции стоит
    /// в стороне, и цветная полоса внизу остаётся незамеченной до конца смены.
    /// </summary>
    public bool AlarmSound { get; set; } = true;

    /// <summary>Минуты бездействия до блокировки разделов; 0 отключает блокировку.</summary>
    public int LockTimeoutMinutes { get; set; } = 10;

    /// <summary>Имя учётной записи администратора. Пусто означает admin.</summary>
    public string AdminAccount { get; set; } = "";

    /// <summary>
    /// Хеш пароля станции, а не сам пароль. Пусто означает, что пароль ещё не
    /// меняли и подходит значение по умолчанию.
    /// </summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// Номер станции. Входит в номера, которые станция выдаёт камерам
    /// (BCU-01-0042), чтобы номера с разных станций не совпадали.
    /// </summary>
    public int StationNumber { get; set; } = 1;

    /// <summary>
    /// Сколько окон сбора показывать. У станций от шести до тридцати отсеков,
    /// и окна должны совпадать с железом. Применяется при следующем запуске:
    /// доска строится один раз.
    /// </summary>
    public int BayCount { get; set; } = 10;

    public long MinFreeBytes => (long)MinFreeGb * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    public static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            var text = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<Settings>(text);
            if (loaded is not null)
            {
                if (string.IsNullOrEmpty(loaded.BackupRoot))
                    loaded.BackupRoot = AppPaths.BackupsRoot;
                return loaded;
            }
        }
        catch (Exception)
        {
            // Файла нет или он испорчен, берём значения по умолчанию.
            // Из-за настроек станция запускаться не перестаёт.
        }

        return new Settings { BackupRoot = AppPaths.BackupsRoot };
    }

    public bool Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
