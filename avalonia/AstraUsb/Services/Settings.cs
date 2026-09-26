using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// Голосовые подсказки: оператор ставит регистратор и уходит, а фраза
    /// «отсек три, можно забирать» слышна от двери.
    /// </summary>
    public bool VoiceHints { get; set; }

    /// <summary>
    /// Веб-панель для удалённого просмотра. Выключена по умолчанию: открытый
    /// порт на станции это решение того, кто её ставит, а не программы.
    /// </summary>
    public bool WebEnabled { get; set; }

    public int WebPort { get; set; } = 8080;

    /// <summary>
    /// Защищённое соединение с панелью. Сертификат станция выпускает себе
    /// сама, поэтому браузер при первом входе предупредит о недоверии, зато
    /// пароль и записи не пойдут по сети открытым текстом.
    /// </summary>
    public bool WebSsl { get; set; }

    /// <summary>
    /// Внешний сервер базы. Станция работает на своей базе рядом с программой,
    /// и эти параметры нужны только там, где сервер есть.
    /// </summary>
    public bool SqlEnabled { get; set; }

    public string SqlKind { get; set; } = "MySQL";
    public string SqlHost { get; set; } = "";
    public int SqlPort { get; set; } = 3306;
    public string SqlDatabase { get; set; } = "";
    public string SqlUser { get; set; } = "";
    public string SqlPassword { get; set; } = "";

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
    /// Название точки: улица, объект, помещение. Идёт в шапку веб-панели
    /// рядом с моделью, потому что на объекте станций несколько и по одной
    /// модели их не различить.
    /// </summary>
    public string StationPlace { get; set; } = "";

    /// <summary>
    /// Сколько окон сбора показывать. У станций от шести до тридцати отсеков,
    /// и окна должны совпадать с железом. Применяется при следующем запуске:
    /// доска строится один раз.
    /// </summary>
    public int BayCount { get; set; } = 10;

    /// <summary>
    /// Сколько окон помещать в строку. При ширине станции в 800 точек читаемо
    /// не больше трёх, но на широком экране веб-режима бывает нужно иначе.
    /// </summary>
    public int BaysPerRow { get; set; } = 3;

    public long MinFreeBytes => (long)MinFreeGb * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    public static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    /// <summary>
    /// Файл настроек есть, но не читается. Принять такие настройки за
    /// отсутствующие опаснее всего для архива: запись пошла бы в папку по
    /// умолчанию на системном разделе, а оригиналы удалились бы с карты.
    /// Поэтому выгрузка ждёт починки, а сохранение не затирает файл молча.
    /// </summary>
    [JsonIgnore]
    public bool Unreadable { get; private set; }

    /// <summary>Чтение и запись файла идут по очереди: иначе позднее сохранение затирает чужие ключи.</summary>
    private static readonly object FileLock = new();

    public static Settings Load()
    {
        lock (FileLock)
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
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // Файла ещё нет: станция не настраивалась, берём значения по умолчанию.
                return new Settings { BackupRoot = AppPaths.BackupsRoot };
            }
            catch (Exception)
            {
                // Испорчен или не читается: см. Unreadable.
            }

            // Из-за настроек станция запускаться не перестаёт, но и писать
            // в архив по умолчанию не начинает.
            return new Settings { BackupRoot = AppPaths.BackupsRoot, Unreadable = true };
        }
    }

    /// <summary>
    /// Записывает файл целиком через временный и замену: обрыв питания
    /// посреди записи оставляет прежний файл, а не обрезанный.
    /// </summary>
    /// <param name="replaceUnreadable">
    /// Разрешить перезаписать нечитаемый файл. Только при явном выборе папки
    /// архива оператором: он сам задаёт потерянное главное значение.
    /// </param>
    public bool Save(bool replaceUnreadable = false)
    {
        if (Unreadable && !replaceUnreadable)
            return false;

        lock (FileLock)
        {
            var temp = FilePath + ".tmp";
            try
            {
                AppPaths.EnsureCreated();
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, this, Json);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, FilePath, overwrite: true);
                Unreadable = false;
                return true;
            }
            catch (Exception)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // Остаток временного файла не мешает следующей записи.
                }
                return false;
            }
        }
    }

    /// <summary>Удаляет файл настроек при заводском сбросе, под тем же замком.</summary>
    public static void Reset()
    {
        lock (FileLock)
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (Exception)
            {
                // Нет файла, значит и сбрасывать нечего.
            }
        }
    }
}
