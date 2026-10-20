#if ARCHIVE_LEAF_INTEGRATION
using AstraUsb.Services;

namespace AstraUsb.Tests
{
    public static class ArchiveLeafIntegration
    {
        public static int Main()
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("ARCHIVE_LEAF_TEST_CONTAINER") != "1"
                || !File.Exists("/.dockerenv"))
                throw new InvalidOperationException("Нужен изолированный тестовый Linux-контейнер");
            var temp = Directory.CreateTempSubdirectory("astra-leaf-").FullName;
            var failures = new List<string>();
            var count = 0;
            void Assert(bool value, string message) { if (!value) throw new Exception(message); }
            void Refuses(Action action)
            {
                try { action(); }
                catch (IOException) { return; }
                throw new Exception("Подменённый файл ошибочно принят за сохранённый");
            }
            void Test(string name, Action<ArchiveDirectory> test)
            {
                var root = Directory.CreateDirectory(Path.Combine(temp, (++count).ToString())).FullName;
                using var archive = ArchiveGuard.Open(root, requireMarker: false);
                try { test(archive); Console.WriteLine("PASS " + name); }
                catch (Exception error) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + error.Message); }
            }
            try
            {
                Test("producer-identical", archive =>
                {
                    archive.ProduceFile("record.mp4", fd => File.WriteAllText(fd, "complete"));
                    var target = Path.Combine(archive.Root, "record.mp4");
                    Assert(File.ReadAllText(target) == "complete", "Нет завершённой записи");
                    var source = Path.Combine(temp, "same-source.mp4");
                    File.WriteAllText(source, "complete");
                    File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(target));
                    Assert(archive.SameFile(source, "record.mp4"), "Не распознан неизменившийся файл");
                });
                Test("backup-preserves-unix-mode", archive =>
                {
                    var sourceRoot = Directory.CreateDirectory(Path.Combine(temp, "mode-source")).FullName;
                    var source = Path.Combine(sourceRoot, "mode-source.mp4");
                    File.WriteAllText(source, "video");
                    File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

                    var result = FileCopier.Copy(sourceRoot, archive.Root, "stamp");

                    var target = Path.Combine(archive.Root, "mode-source.mp4");
                    Assert(result.CopiedFiles > 0 && File.Exists(target), "Нет резервной копии");
                    Assert(File.GetUnixFileMode(target) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead),
                        "Резервная копия потеряла исходные права");
                });
                Test("export-does-not-preserve-unix-mode", archive =>
                {
                    var device = Directory.CreateDirectory(Path.Combine(archive.Root, "Device1")).FullName;
                    var source = Path.Combine(device, "clip.mp4");
                    File.WriteAllText(source, "video");
                    File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    var mtime = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
                    File.SetLastWriteTimeUtc(source, mtime);

                    archive.CopyFile(archive, "Device1/clip.mp4", "export.mp4");

                    var target = Path.Combine(archive.Root, "export.mp4");
                    Assert(File.GetUnixFileMode(target) == (UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead | UnixFileMode.OtherRead),
                        "Экспорт не применил 0666 с umask 022");
                    Assert(File.GetLastWriteTimeUtc(target) == mtime, "Экспорт потерял mtime");
                });
                Test("backup-preserves-atime", archive =>
                {
                    var source = Path.Combine(temp, "atime-source.mp4");
                    File.WriteAllText(source, "video");
                    File.SetLastWriteTimeUtc(source, new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc));
                    File.SetLastAccessTimeUtc(source, new DateTime(2050, 2, 3, 4, 5, 6, DateTimeKind.Utc));

                    archive.CopyFile(source, "copy.mp4");

                    Assert(File.GetLastAccessTimeUtc(Path.Combine(archive.Root, "copy.mp4")) == File.GetLastAccessTimeUtc(source),
                        "Резервная копия потеряла atime после чтения источника");
                });
                Test("export-preserves-atime", archive =>
                {
                    var device = Directory.CreateDirectory(Path.Combine(archive.Root, "Device1")).FullName;
                    var source = Path.Combine(device, "clip.mp4");
                    File.WriteAllText(source, "video");
                    File.SetLastWriteTimeUtc(source, new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc));
                    File.SetLastAccessTimeUtc(source, new DateTime(2050, 2, 3, 4, 5, 6, DateTimeKind.Utc));

                    archive.CopyFile(archive, "Device1/clip.mp4", "export.mp4");

                    Assert(File.GetLastAccessTimeUtc(Path.Combine(archive.Root, "export.mp4")) == File.GetLastAccessTimeUtc(source),
                        "Экспорт потерял atime после чтения источника");
                });
                Test("timestamp-sibling-symlink-is-ignored", archive =>
                {
                    var sourceRoot = Directory.CreateDirectory(Path.Combine(temp, "linked-sibling-source")).FullName;
                    var source = Path.Combine(sourceRoot, "clip.mp4");
                    File.WriteAllText(source, "new");
                    File.WriteAllText(Path.Combine(archive.Root, "clip.mp4"), "old version");
                    var outside = Path.Combine(temp, "outside-sibling.mp4");
                    File.WriteAllText(outside, "new");
                    File.SetLastWriteTimeUtc(outside, File.GetLastWriteTimeUtc(source));
                    var sibling = Path.Combine(archive.Root, "clip_20261019_120000.mp4");
                    File.CreateSymbolicLink(sibling, outside);

                    var result = FileCopier.Copy(sourceRoot, archive.Root, "20261020_120000");

                    Assert(result.CopiedFiles == 1 && result.Failed == 0, "Ссылка помешала новой безопасной копии");
                    Assert(result.Destinations[source] == Path.Combine(archive.Root, "clip_20261020_120000.mp4"),
                        "Ссылка ошибочно принята за сохранённую копию");
                    Assert(new FileInfo(sibling).LinkTarget == outside && File.ReadAllText(outside) == "new",
                        "Изменена ссылка или чужой файл");
                });
                Test("timestamp-sibling-fifo-is-ignored", archive =>
                {
                    var sourceRoot = Directory.CreateDirectory(Path.Combine(temp, "fifo-sibling-source")).FullName;
                    var source = Path.Combine(sourceRoot, "clip.mp4");
                    File.WriteAllText(source, "new");
                    File.WriteAllText(Path.Combine(archive.Root, "clip.mp4"), "old version");
                    var sibling = Path.Combine(archive.Root, "clip_20261019_120000.mp4");
                    using var create = System.Diagnostics.Process.Start("mkfifo", sibling)!;
                    create.WaitForExit();
                    Assert(create.ExitCode == 0, "Не удалось создать тестовый FIFO");

                    var result = FileCopier.Copy(sourceRoot, archive.Root, "20261020_120000");

                    Assert(result.CopiedFiles == 1 && result.Failed == 0, "FIFO помешал новой безопасной копии");
                    Assert(result.Destinations[source] == Path.Combine(archive.Root, "clip_20261020_120000.mp4"),
                        "FIFO ошибочно принят за сохранённую копию");
                });
                Test("producer-rename", archive =>
                {
                    var target = Path.Combine(archive.Root, "record.mp4");
                    Refuses(() => archive.ProduceFile("record.mp4", fd =>
                    { File.WriteAllText(fd, "complete"); File.Move(target, target + "-moved"); }));
                });
                Test("producer-replaced", archive =>
                {
                    var target = Path.Combine(archive.Root, "record.mp4");
                    Refuses(() => archive.ProduceFile("record.mp4", fd =>
                    { File.WriteAllText(fd, "complete"); File.Move(target, target + "-moved"); File.WriteAllText(target, "foreign"); }));
                    Assert(File.ReadAllText(target) == "foreign", "Убрана чужая замена");
                });
                Test("producer-failed-replaced", archive =>
                {
                    var target = Path.Combine(archive.Root, "record.mp4");
                    Refuses(() => archive.ProduceFile("record.mp4", fd =>
                    { File.Move(target, target + "-moved"); File.WriteAllText(target, "foreign"); throw new IOException("Сбой producer"); }));
                    Assert(File.ReadAllText(target) == "foreign", "Убрана чужая замена");
                });
                Test("producer-unlinked", archive =>
                {
                    Refuses(() => archive.ProduceFile("record.mp4", fd =>
                    { File.WriteAllText(fd, "complete"); File.Delete(Path.Combine(archive.Root, "record.mp4")); }));
                });
                Test("producer-link-replaced", archive =>
                {
                    var target = Path.Combine(archive.Root, "record.mp4");
                    var outside = Path.Combine(temp, "outside.mp4");
                    File.WriteAllText(outside, "outside");
                    Refuses(() => archive.ProduceFile("record.mp4", fd =>
                    { File.Move(target, target + "-moved"); File.CreateSymbolicLink(target, outside); }));
                    Assert(new FileInfo(target).LinkTarget == outside, "Убрана чужая ссылка");
                    Assert(File.ReadAllText(outside) == "outside", "Изменён файл вне архива");
                });
                Test("producer-partial-cleanup", archive =>
                {
                    Refuses(() => archive.ProduceFile("record.mp4", fd =>
                    { File.WriteAllText(fd, "partial"); throw new IOException("Сбой producer"); }));
                    Assert(!File.Exists(Path.Combine(archive.Root, "record.mp4")), "Осталась неполная копия");
                });
                Test("stream-replaced", archive =>
                {
                    var source = Directory.CreateDirectory(Path.Combine(archive.Root, "source")).FullName;
                    var file = Path.Combine(source, "large.mp4");
                    using (var output = File.OpenWrite(file)) output.SetLength(512L * 1024 * 1024);
                    var destination = Directory.CreateDirectory(Path.Combine(archive.Root, "destination")).FullName;
                    var target = Path.Combine(destination, "large.mp4");
                    var copy = Task.Run(() => FileCopier.Copy(source, destination, "stamp"));
                    Assert(SpinWait.SpinUntil(() => File.Exists(target) || copy.IsCompleted, TimeSpan.FromSeconds(10)), "Копирование не началось");
                    Assert(!copy.IsCompleted, "Копирование завершилось до подмены");
                    File.Move(target, target + "-moved");
                    File.WriteAllText(target, "foreign");
                    var result = copy.GetAwaiter().GetResult();
                    Assert(result.Failed == 1 && result.CopiedFiles == 0 && result.BackedUp.Count == 0, "Подменённая запись попала в BackedUp");
                    Assert(File.Exists(file), "Пропал оригинал");
                    Assert(File.ReadAllText(target) == "foreign", "Убрана чужая замена");
                });
                Test("existing-target", archive =>
                {
                    var target = Path.Combine(archive.Root, "record.mp4");
                    File.WriteAllText(target, "existing");
                    Refuses(() => archive.ProduceFile("record.mp4", fd => File.WriteAllText(fd, "new")));
                    Assert(File.ReadAllText(target) == "existing", "Изменён существующий файл");
                });
                if (failures.Count > 0)
                    throw new Exception($"Не пройдено {failures.Count} из {count} проверок");
                Console.WriteLine($"Пройдено {count}/{count}. Проверены FD и inode; физическая политика отключена только в тестовой копии ArchiveGuard.");
                return 0;
            }
            finally { Directory.Delete(temp, recursive: true); }
        }
    }
}

namespace AstraUsb.Services
{
    public static class StationLog
    {
        public static void LocalOnce(string key, string? message)
        {
            if (message is not null)
                Console.Error.WriteLine(message);
        }
    }
    public static class CrashLog
    {
        public static void Write(string what, Exception? error) => Console.Error.WriteLine($"{what}: {error}");
    }
    public static class DeviceRegistry { public const string DeviceDirPrefix = "Device"; }
    public sealed class Settings
    {
        public bool Unreadable { get; set; }
        public string BackupRoot { get; set; } = "";
        public string BackupUuid { get; set; } = "";
        public string BackupSerial { get; set; } = "";
        public string? BackupRelativePath { get; set; }
    }
}
#endif
