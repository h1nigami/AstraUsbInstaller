using System.Text.RegularExpressions;

namespace AstraUsb.Services;

public static class Diagnostics
{
    public static string Export(string destination, string? dbPath = null)
    {
        using var operation = OperationGuard.Acquire(dbPath: dbPath);
        var settings = Settings.Load();
        var secrets = new[] { settings.FtpPassword, settings.SqlPassword, settings.PasswordHash }
            .Where(value => !string.IsNullOrEmpty(value)).ToArray();
        string Redact(string text)
        {
            foreach (var secret in secrets)
                text = text.Replace(secret, "[скрыто]", StringComparison.Ordinal);
            return Regex.Replace(text, @"(?im)^.*(?:password|пароль|token|secret|authorization|bearer).*$",
                "[строка с учётными данными скрыта]");
        }

        var now = DateTime.Now;
        var lines = new List<string>();
        var summary = $"Собрано: {now:yyyy-MM-dd HH:mm:ss}\n{VersionInfo.Label()}\n";
        try
        {
            var events = new ActionLog(dbPath ?? AppPaths.Database)
                .Between(DateTime.MinValue, DateTime.MaxValue);
            summary += $"В пакете событий: {events.Count}\n";
            lines.AddRange(events.Select(entry => $"{entry.At:yyyy-MM-dd HH:mm:ss} {entry.Kind}: {entry.Message}"));
        }
        catch (Exception)
        {
            summary += "База журнала недоступна\n";
        }
        try
        {
            if (File.Exists(CrashLog.FilePath))
                lines.AddRange(File.ReadLines(CrashLog.FilePath).TakeLast(2000));
        }
        catch (Exception)
        {
            lines.Add("Журнал ошибок недоступен");
        }
        summary += settings.Unreadable ? "Настройки не читаются\n" : "Настройки читаются\n";
        summary += ArchiveGuard.Available(settings.ResolveBackupRoot())
            ? "Архив доступен\n" : "Архив недоступен\n";
        using var target = ArchiveGuard.Open(destination, create: true, requireMarker: false);
        var name = $"Диагностика_{now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        using var folder = target.CreateDirectory(name);
        folder.WriteText("version.txt", VersionInfo.Label() + "\n");
        folder.WriteText("app.log", Redact(string.Join("\n", lines)) + "\n");
        folder.WriteText("summary.txt", summary);
        return Path.Combine(target.Root, name);
    }
}
