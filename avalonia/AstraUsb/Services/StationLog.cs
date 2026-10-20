using System.Text;

namespace AstraUsb.Services;

public static class StationLog
{
    public const string ArchiveName = "astra-usb-monitor.log";
    public static string LocalPath => Path.Combine(AppPaths.DataDir, "app.log");
    private const int MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> LastErrors = new();
    private static string? _activePath;

    public static string FilePath
    {
        get
        {
            var settings = Settings.Load();
            var root = settings.ResolveBackupRoot();
            try
            {
                using var archive = ArchiveGuard.Open(root, settings: settings);
                lock (Gate)
                    return LastErrors.ContainsKey("archive-log:" + root) ? LocalPath : Path.Combine(root, ArchiveName);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return LocalPath;
            }
        }
    }

    public static void Write(string message, Exception? error = null)
    {
        var settings = Settings.Load();
        var root = settings.ResolveBackupRoot();
        lock (Gate)
        {
            var path = Path.Combine(root, ArchiveName);
            var key = "archive-log:" + root;
            try
            {
                using var archive = ArchiveGuard.Open(root, settings: settings);
                var text = Format(message, error);
                archive.RequireMarker();
                archive.AppendText(ArchiveName, text, _activePath == path ? null : MaxBytes);
                archive.RequireMarker();
                archive.Verify();
                _activePath = path;
                LastErrors.Remove(key);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
            {
                var cause = $"Не удалось записать журнал архива {path}: {failure.GetType().Name}: {failure.Message}";
                if (failure.InnerException is not null)
                    cause += $" ({failure.GetBaseException().Message})";
                if (Changed(key, cause))
                {
                    WriteLocal(cause);
                    Console.Error.WriteLine(cause);
                }
                WriteLocal(message, error);
            }
        }
    }

    public static void Once(string key, string? message)
    {
        if (Changed(key, message)) Write(message!);
    }

    // Настройки могут быть повреждены: их ошибку пишем без повторного чтения настроек.
    public static void LocalOnce(string key, string? message)
    {
        if (!Changed(key, message)) return;
        lock (Gate) WriteLocal(message!);
    }

    private static bool Changed(string key, string? message)
    {
        lock (Gate)
        {
            if (message is null)
            {
                LastErrors.Remove(key);
                return false;
            }
            if (LastErrors.GetValueOrDefault(key) == message) return false;
            LastErrors[key] = message;
            return true;
        }
    }

    private static string Format(string message, Exception? error)
    {
        var text = error is null ? message : $"{message}: {error}";
        var stamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss  ");
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')
            .Select(line => string.IsNullOrWhiteSpace(line) ? "" : stamp + line.TrimStart(' '));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void WriteLocal(string message, Exception? error = null)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            if (new FileInfo(LocalPath).LinkTarget is not null)
                throw new IOException($"Файл журнала оказался ссылкой: {LocalPath}");
            if (OperatingSystem.IsLinux())
                ArchiveDirectory.AppendLocalText(Format(message, error), _activePath == LocalPath ? null : MaxBytes);
            else
            {
                if (_activePath != LocalPath && File.Exists(LocalPath) && new FileInfo(LocalPath).Length > MaxBytes)
                {
                    using var file = new FileStream(LocalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    file.Seek(-MaxBytes / 2, SeekOrigin.End);
                    var tail = new byte[MaxBytes / 2];
                    file.ReadExactly(tail);
                    file.Position = 0;
                    file.Write(tail);
                    file.SetLength(tail.Length);
                }
                File.AppendAllText(LocalPath, Format(message, error), Encoding.UTF8);
            }
            _activePath = LocalPath;
            LastErrors.Remove("local-log:" + LocalPath);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var cause = $"Не удалось записать локальный журнал {LocalPath}: {failure.Message}";
            if (Changed("local-log:" + LocalPath, cause)) Console.Error.WriteLine(cause);
        }
    }
}
