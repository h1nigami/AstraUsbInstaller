#if STATION_LOG_INTEGRATION
using System.Diagnostics;
using System.Reflection;
using AstraUsb.Services;

namespace AstraUsb.Tests
{
    public static class StationLogIntegration
    {
        public static int Main(string[] args)
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("ARCHIVE_LEAF_TEST_CONTAINER") != "1"
                || !File.Exists("/.dockerenv"))
                throw new InvalidOperationException("Нужен изолированный тестовый Linux-контейнер");
            if (args.Length > 0)
            {
                Settings.Current = new Settings { BackupRoot = args[1] };
                AppPaths.Root = args[2];
                for (var i = 0; i < int.Parse(args[3]); i++)
                    StationLog.Write($"запись процесса {args[4]}:{i}");
                return 0;
            }
            var root = Directory.CreateTempSubdirectory("astra-log-native-").FullName;
            var failures = new List<string>();
            var count = 0;
            void Assert(bool value, string message) { if (!value) throw new Exception(message); }
            void Test(string name, Action<string> test)
            {
                var folder = Directory.CreateDirectory(Path.Combine(root, (++count).ToString())).FullName;
                AppPaths.Root = Path.Combine(folder, "app");
                AppPaths.EnsureCreated();
                var archive = Path.Combine(folder, "archive");
                Assert(ArchiveGuard.Mark(archive), "Не удалось подготовить архив");
                Settings.Current = new Settings { BackupRoot = archive };
                try { test(archive); Console.WriteLine("PASS " + name); }
                catch (Exception error) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + error.Message); }
            }
            Process Child(string archive, int writes, string label)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, "append", archive,
                             AppPaths.Root, writes.ToString(), label })
                    start.ArgumentList.Add(argument);
                return Process.Start(start)!;
            }
            try
            {
                Test("identity-mismatch-falls-back", archive =>
                {
                    Settings.Current.BackupUuid = "different-disk";
                    StationLog.Write("сообщение другого диска");
                    Assert(!File.Exists(Path.Combine(archive, StationLog.ArchiveName)), "Журнал записан на другой диск");
                    Assert(StationLog.FilePath == StationLog.LocalPath, "Статус не учитывает identity настроек");
                    Assert(File.ReadAllText(StationLog.LocalPath).Contains("сообщение другого диска"), "Нет fallback записи");
                });
                Test("archive-replacement-does-not-create-shadow", archive =>
                {
                    StationLog.Write("смена каталога", new ReplacingException(() =>
                    {
                        Directory.Move(archive, archive + "-removed");
                        Directory.CreateDirectory(archive);
                    }));
                    Assert(!File.Exists(Path.Combine(archive, StationLog.ArchiveName)), "Создан shadow log");
                    Assert(File.ReadAllText(StationLog.LocalPath).Contains("смена каталога"), "Нет fallback записи");
                });
                Test("missing-marker-during-format-falls-back", archive =>
                {
                    StationLog.Write("маркер исчез", new ReplacingException(() => File.Delete(Path.Combine(archive, Markers.Archive))));
                    Assert(!File.Exists(Path.Combine(archive, StationLog.ArchiveName)), "Запись без маркера архива");
                    Assert(File.ReadAllText(StationLog.LocalPath).Contains("маркер исчез"), "Нет fallback записи");
                });
                Test("leaf-symlink-replacement-is-not-followed", archive =>
                {
                    var outside = Path.Combine(root, "outside-append.log");
                    File.WriteAllText(outside, "чужой файл");
                    StationLog.Write("чужое сообщение", new ReplacingException(() =>
                        File.CreateSymbolicLink(Path.Combine(archive, StationLog.ArchiveName), outside)));
                    Assert(File.ReadAllText(outside) == "чужой файл", "Запись ушла по ссылке");
                    Assert(File.ReadAllText(StationLog.LocalPath).Contains("чужое сообщение"), "Нет fallback записи");
                });
                Test("diagnostics-does-not-read-a-linked-log", archive =>
                {
                    var outside = Path.Combine(root, "outside-read.log");
                    File.WriteAllText(outside, "PRIVATE-OUTSIDE-LOG");
                    File.CreateSymbolicLink(Path.Combine(archive, StationLog.ArchiveName), outside);
                    var bundle = Diagnostics.Export(Path.Combine(root, "diagnostics-link"));
                    Assert(!File.ReadAllText(Path.Combine(bundle, "app.log")).Contains("PRIVATE-OUTSIDE-LOG"),
                        "Диагностика прочитала журнал за пределами архива");
                });
                Test("diagnostics-rejects-another-archive-identity", archive =>
                {
                    File.WriteAllText(Path.Combine(archive, StationLog.ArchiveName), "PRIVATE-OTHER-DISK");
                    Settings.Current.BackupUuid = "different-disk";
                    var bundle = Diagnostics.Export(Path.Combine(root, "diagnostics-identity"));
                    Assert(!File.ReadAllText(Path.Combine(bundle, "app.log")).Contains("PRIVATE-OTHER-DISK"), "Прочитан другой диск");
                    Assert(File.ReadAllText(Path.Combine(bundle, "summary.txt")).Contains("Архив недоступен"), "Неверный статус архива");
                });
                Test("fifo-log-does-not-block", archive =>
                {
                    using var create = Process.Start("mkfifo", Path.Combine(archive, StationLog.ArchiveName))!;
                    create.WaitForExit();
                    Assert(create.ExitCode == 0, "Не удалось создать FIFO");
                    using var child = Child(archive, 1, "fifo");
                    if (!child.WaitForExit(3000))
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit();
                        throw new Exception("Запись зависла на FIFO");
                    }
                    Assert(child.ExitCode == 0 && File.Exists(StationLog.LocalPath), "Нет безопасной fallback записи FIFO");
                });
                Test("parallel-processes-keep-all-lines", archive =>
                {
                    var children = Enumerable.Range(0, 4).Select(i => Child(archive, 100, i.ToString())).ToArray();
                    foreach (var child in children)
                    {
                        Assert(child.WaitForExit(20000) && child.ExitCode == 0, "Процесс журнала не завершился");
                        child.Dispose();
                    }
                    var lines = File.ReadAllLines(Path.Combine(archive, StationLog.ArchiveName));
                    Assert(lines.Length == 400 && lines.Distinct().Count() == 400, "Потеряны или перемешаны строки процессов");
                    Assert(File.GetUnixFileMode(Path.Combine(archive, StationLog.ArchiveName))
                        == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead),
                        "Журнал не сохранил обычные права 0666 с umask 022");
                });
                Test("rotation-retains-the-tail-and-new-message", archive =>
                {
                    var path = Path.Combine(archive, StationLog.ArchiveName);
                    File.WriteAllText(path, new string('o', 1_200_000) + "KEEP-END\n");
                    StationLog.Write("новая строка после сокращения");
                    var text = File.ReadAllText(path);
                    Assert(new FileInfo(path).Length < 700_000 && text.Contains("KEEP-END")
                        && text.Contains("новая строка после сокращения"), "Неверное сокращение журнала");
                });
                Test("parallel-fallback-processes-keep-all-lines", archive =>
                {
                    File.Delete(Path.Combine(archive, Markers.Archive));
                    var children = Enumerable.Range(0, 4).Select(i => Child(archive, 100, i.ToString())).ToArray();
                    foreach (var child in children)
                    {
                        Assert(child.WaitForExit(20000) && child.ExitCode == 0, "Процесс fallback не завершился");
                        child.Dispose();
                    }
                    var lines = File.ReadAllLines(StationLog.LocalPath).Where(line => line.Contains("запись процесса")).ToArray();
                    Assert(lines.Length == 400 && lines.Distinct().Count() == 400, "Потеряны или перемешаны fallback строки процессов");
                });
                if (failures.Count > 0)
                    throw new Exception($"Не пройдено {failures.Count} из {count} проверок журнала");
                Console.WriteLine($"Пройдено {count}/{count} проверок журнала");
                return 0;
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        private sealed class ReplacingException(Action replace) : Exception("смена записи")
        {
            private bool _replaced;
            public override string ToString()
            {
                if (!_replaced) { _replaced = true; replace(); }
                return base.ToString();
            }
        }
    }
}

namespace AstraUsb.Services
{
    public static class DeviceRegistry { public const string DeviceDirPrefix = "Device"; }
    public sealed class Settings
    {
        public static Settings Current { get; set; } = new();
        public static Settings Load() => Current;
        public string ResolveBackupRoot() => BackupRoot;
        public bool Unreadable { get; set; }
        public string BackupRoot { get; set; } = "";
        public string BackupUuid { get; set; } = "";
        public string BackupSerial { get; set; } = "";
        public string? BackupRelativePath { get; set; }
        public string FtpPassword { get; set; } = "";
        public string SqlPassword { get; set; } = "";
        public string PasswordHash { get; set; } = "";
    }
    public sealed record ActionEntry(DateTime At, string Kind, string Message);
    public sealed class ActionLog
    {
        public ActionLog(string path) { }
        public IReadOnlyList<ActionEntry> Between(DateTime from, DateTime to) => [];
    }
}
#endif
