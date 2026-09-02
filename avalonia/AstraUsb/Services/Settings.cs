using System.Text.Json;

namespace AstraUsb.Services;

/// <summary>
/// Настройки станции. Лежат рядом с базой в data/settings.json, чтобы
/// переустановка программы их не трогала — так же, как у Python-версии.
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

    /// <summary>Минуты бездействия до блокировки разделов; 0 — не блокировать.</summary>
    public int LockTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// Номер станции. Входит в номера, которые станция выдаёт камерам
    /// (BCU-01-0042), чтобы номера с разных станций не совпадали.
    /// </summary>
    public int StationNumber { get; set; } = 1;

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
            // Файла нет или он испорчен — берём значения по умолчанию.
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
