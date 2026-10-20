#if ARCHIVE_LEAF_INTEGRATION
using AstraUsb.Services;

namespace AstraUsb.Tests;

public static class SourceCleanerRaceIntegration
{
    public static int Run(string scenario, string root)
    {
        if (!OperatingSystem.IsLinux() || !Path.GetFullPath(root).StartsWith("/tmp/astra-source-race-", StringComparison.Ordinal)
            || !Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Нужен новый пустой временный каталог теста");
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var video = Path.Combine(source, "clip.mp4");
        File.WriteAllText(video, "OLD");
        var copied = FileCopier.Copy(source, Path.Combine(root, "copies"), "20261020_120000");

        var deleted = SourceCleaner.DeleteBackedUpVideos(source, copied);

        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        Require(File.ReadAllText(Path.Combine(root, "copies", "clip.mp4")) == "OLD", "Изменена сохранённая копия");
        if (scenario == "normal")
            Require(deleted == 1 && !File.Exists(video), "Подтверждённый оригинал не удалён");
        else
        {
            Require(deleted == 0, "Очистка ошибочно удалила подменённый файл");
            Require(File.ReadAllText(video + ".previous") == "OLD", "Повреждён прежний оригинал");
            if (scenario == "replace")
            {
                Require(File.Exists(video) && File.ReadAllText(video) == "NEW", "Потерян файл, подменённый перед syscall");
                Require(Directory.GetFiles(source).Length == 2, "Безопасный возврат оставил временный файл");
            }
            else if (scenario == "recreate")
            {
                Require(File.ReadAllText(video) == "LATEST", "Безопасный возврат перезаписал новый файл");
                Require(Directory.GetFiles(source, ".astra-clean-*")
                    .Any(path => File.ReadAllText(path) == "NEW"), "Потерян файл, который нельзя вернуть на занятое имя");
            }
            else throw new ArgumentException("Неизвестный сценарий", nameof(scenario));
        }
        Console.WriteLine("PASS source-cleaner-" + scenario);
        return 0;
    }
}
#endif
