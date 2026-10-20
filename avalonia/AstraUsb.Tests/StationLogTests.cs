using System.Text.RegularExpressions;
using AstraUsb.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class StationLogTests : IDisposable
{
    private readonly string _previous = AppPaths.Root;
    private readonly string _root = Directory.CreateTempSubdirectory("astra-station-log-").FullName;
    private string LocalLog => Path.Combine(AppPaths.DataDir, "app.log");

    public StationLogTests()
    {
        AppPaths.Root = _root;
        AppPaths.EnsureCreated();
        Assert.True(new Settings { BackupRoot = Path.Combine(_root, "missing") }.Save());
    }

    [Fact]
    public void Actions_are_written_to_the_local_log_without_an_archive()
    {
        new ActionLog(AppPaths.Database).Write(ActionLog.Backup, "сохранено Device7");

        Assert.True(File.Exists(LocalLog));
        Assert.Contains("сохранено Device7", File.ReadAllText(LocalLog));
    }

    [Fact]
    public void Errors_reach_the_station_log_with_their_cause()
    {
        CrashLog.Write("не удалось прочитать карту", new IOException("ошибка устройства 123"));

        Assert.True(File.Exists(LocalLog));
        var text = File.ReadAllText(LocalLog);
        Assert.Contains("не удалось прочитать карту", text);
        Assert.Contains("ошибка устройства 123", text);
    }

    [Fact]
    public void Every_nonempty_line_has_a_date_and_aligned_message()
    {
        new ActionLog(AppPaths.Database).Write(ActionLog.Backup, "   первая строка\n   вторая строка");

        Assert.True(File.Exists(LocalLog));
        foreach (var line in File.ReadAllLines(LocalLog).Where(line => line.Length > 0))
            Assert.Matches(@"^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}  \S", line);
    }

    [Fact]
    public void Parallel_actions_remain_whole_lines()
    {
        Parallel.For(0, 40, i =>
            new ActionLog(AppPaths.Database).Write(ActionLog.Backup, $"параллельная запись {i:D3}"));

        Assert.True(File.Exists(LocalLog));
        var lines = File.ReadAllLines(LocalLog).Where(line => line.Contains("параллельная запись")).ToArray();
        Assert.Equal(40, lines.Length);
        Assert.Equal(40, lines.Select(line => Regex.Match(line, @"параллельная запись \d{3}$").Value)
            .Where(value => value.Length > 0).Distinct().Count());
    }

    [Fact]
    public void The_log_follows_archive_changes_disconnection_and_return()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = SelectArchive("first");
        var actions = new ActionLog(AppPaths.Database);
        actions.Write(ActionLog.Backup, "первый архив");
        var firstLog = Path.Combine(first, "astra-usb-monitor.log");
        Assert.True(File.Exists(firstLog));
        var second = SelectArchive("second");
        actions.Write(ActionLog.Backup, "второй архив");
        Assert.DoesNotContain("второй архив", File.ReadAllText(firstLog));
        Assert.Contains("второй архив", File.ReadAllText(Path.Combine(second, "astra-usb-monitor.log")));

        Directory.Move(second, second + "-removed");
        actions.Write(ActionLog.Backup, "архив отключён");
        Assert.Contains("архив отключён", File.ReadAllText(LocalLog));
        Assert.False(Directory.Exists(second));
        Directory.Move(second + "-removed", second);
        actions.Write(ActionLog.Backup, "архив вернулся");
        Assert.Contains("архив вернулся", File.ReadAllText(Path.Combine(second, "astra-usb-monitor.log")));
    }

    [Fact]
    public void An_unwritable_archive_log_falls_back_without_interrupting_the_action()
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = SelectArchive("archive");
        Directory.CreateDirectory(Path.Combine(archive, "astra-usb-monitor.log"));

        new ActionLog(AppPaths.Database).Write(ActionLog.Backup, "запись при отказе файла");

        Assert.True(File.Exists(LocalLog));
        Assert.Contains("запись при отказе файла", File.ReadAllText(LocalLog));
    }

    [Fact]
    public void Archive_write_failure_is_reported_once_and_again_after_recovery()
    {
        var archive = SelectArchive("failure-reason");
        var path = Path.Combine(archive, StationLog.ArchiveName);
        Directory.CreateDirectory(path);

        StationLog.Write("первая запись");
        StationLog.Write("вторая запись");

        Assert.True(File.Exists(LocalLog));
        var text = File.ReadAllText(LocalLog);
        Assert.Contains(path, text);
        Assert.Contains("Exception", text);
        Assert.Single(File.ReadAllLines(LocalLog), line => line.Contains(path));
        Directory.Delete(path);
        StationLog.Write("архив восстановлен");
        Assert.Contains("архив восстановлен", File.ReadAllText(path));
        File.Delete(path);
        Directory.CreateDirectory(path);
        StationLog.Write("повторный отказ");
        Assert.Equal(2, File.ReadAllLines(LocalLog).Count(line => line.Contains(path)));
    }

    [Fact]
    public void Replacing_the_archive_during_message_formatting_cannot_create_a_shadow_log()
    {
        var archive = SelectArchive("replace");
        var replaced = false;
        var removed = archive + "-removed";
        var error = new ReplacingException(() =>
        {
            try { Directory.Move(archive, removed); }
            catch (IOException) { return; }
            Directory.CreateDirectory(archive);
            replaced = true;
        });

        StationLog.Write("сообщение при смене каталога", error);

        if (replaced)
        {
            Assert.False(File.Exists(Path.Combine(archive, StationLog.ArchiveName)));
            Assert.Contains("сообщение при смене каталога", File.ReadAllText(LocalLog));
        }
        else
            Assert.Contains("сообщение при смене каталога", File.ReadAllText(Path.Combine(archive, StationLog.ArchiveName)));
    }

    [Fact]
    public void An_ancestor_junction_swap_cannot_append_to_a_foreign_log()
    {
        var parent = Path.Combine(_root, "parent");
        var archive = SelectArchive(Path.Combine("parent", "archive"));
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var foreign = Directory.CreateDirectory(Path.Combine(outside, "archive")).FullName;
        var foreignLog = Path.Combine(foreign, StationLog.ArchiveName);
        File.WriteAllText(foreignLog, "чужая запись");
        var replaced = false;
        var error = new ReplacingException(() =>
        {
            try { Directory.Move(parent, parent + "-removed"); }
            catch (IOException) { return; }
            FileCopierTests.MakeDirectoryLink(parent, outside);
            replaced = true;
        });
        try
        {
            StationLog.Write("не должно попасть в чужой архив", error);

            Assert.Equal("чужая запись", File.ReadAllText(foreignLog));
        }
        finally
        {
            if (replaced)
                Directory.Delete(parent);
        }
    }

    private sealed class ReplacingException(Action replace) : Exception("проверка смены каталога")
    {
        private bool _replaced;

        public override string ToString()
        {
            if (!_replaced)
            {
                _replaced = true;
                replace();
            }
            return base.ToString();
        }
    }

    [Fact]
    public void Invalid_settings_can_report_their_error_while_a_writer_waits_for_the_settings_lock()
    {
        File.WriteAllText(Settings.FilePath, "{broken");
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var settingsLock = typeof(Settings).GetField("FileLock", flags)!.GetValue(null)!;
        var logLock = typeof(StationLog).GetField("Gate", flags)!.GetValue(null)!;
        using var started = new ManualResetEventSlim();
        var writer = new Thread(() =>
        {
            started.Set();
            StationLog.Write("запись при повреждённых настройках");
        }) { IsBackground = true };
        try
        {
            lock (settingsLock)
            {
                writer.Start();
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(SpinWait.SpinUntil(() => (writer.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)), "Писатель не дождался блокировки настроек");
                var available = Monitor.TryEnter(logLock);
                if (available) Monitor.Exit(logLock);
                Assert.True(available, "Писатель держит блокировку журнала, ожидая настройки");
                Assert.True(Settings.Load().Unreadable);
            }
        }
        finally { Assert.True(writer.Join(TimeSpan.FromSeconds(5)), "Писатель журнала не завершился"); }
        Assert.Contains("запись при повреждённых настройках", File.ReadAllText(LocalLog));
    }

    [Fact]
    public void Diagnostics_contains_the_existing_local_station_log()
    {
        File.WriteAllText(LocalLog, "событие перед запуском диагностики\npassword=hidden-secret");

        var bundle = Diagnostics.Export(Path.Combine(_root, "export"));

        var text = File.ReadAllText(Path.Combine(bundle, "app.log"));
        Assert.Contains("событие перед запуском диагностики", text);
        Assert.DoesNotContain("hidden-secret", text);
    }

    [Fact]
    public void A_failed_busy_marker_is_logged_once_and_again_after_recovery()
    {
        Directory.CreateDirectory(BusyMarker.FilePath);
        BusyMarker.Touch();
        BusyMarker.Touch();
        Assert.True(File.Exists(LocalLog));
        var first = File.ReadAllLines(LocalLog).Count(line => line.Contains("метку копирования"));
        Assert.Equal(1, first);
        Directory.Delete(BusyMarker.FilePath);
        BusyMarker.Touch();
        File.Delete(BusyMarker.FilePath);
        Directory.CreateDirectory(BusyMarker.FilePath);
        BusyMarker.Touch();
        Assert.Equal(2, File.ReadAllLines(LocalLog).Count(line => line.Contains("метку копирования")));
    }

    [Fact]
    public void A_broken_settings_file_reports_its_path_without_losing_the_error()
    {
        File.WriteAllText(Settings.FilePath, "{broken");

        Assert.True(Settings.Load().Unreadable);

        Assert.True(File.Exists(LocalLog));
        Assert.Contains(Settings.FilePath, File.ReadAllText(LocalLog));
    }

    private string SelectArchive(string name)
    {
        var path = Path.Combine(_root, name);
        Assert.True(ArchiveGuard.Mark(path));
        var settings = Settings.Load();
        Assert.True(settings.SelectBackupRoot(path));
        Assert.True(settings.Save());
        return path;
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
