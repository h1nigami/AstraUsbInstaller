using System.Reflection;
using AstraUsb.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class SilentErrorParityTests : IDisposable
{
    private readonly string _previous = AppPaths.Root;
    private readonly string _root = Directory.CreateTempSubdirectory("astra-error-parity-").FullName;

    public SilentErrorParityTests()
    {
        AppPaths.Root = Path.Combine(_root, "app");
        AppPaths.EnsureCreated();
    }

    [Fact]
    public void A_failed_source_deletion_reports_the_file_and_keeps_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = Directory.CreateDirectory(Path.Combine(_root, "card")).FullName;
        var video = Path.Combine(source, "video.mp4");
        File.WriteAllText(video, "saved video");
        var copied = FileCopier.Copy(source, Path.Combine(_root, "copies"), "stamp");
        using var held = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(0, SourceCleaner.DeleteBackedUpVideos(source, copied));

        Assert.True(File.Exists(video));
        AssertLogged(video);
    }

    [Fact]
    public void A_failed_source_walk_is_reported_without_deleting_anything()
    {
        var missing = Path.Combine(_root, "removed-card");

        Assert.Equal(0, SourceCleaner.DeleteBackedUpVideos(missing, new CopyResult(0, 0, new Dictionary<string, string>(), 0)));

        AssertLogged(missing);
    }

    [Fact]
    public void A_failed_tag_marker_write_reports_the_tag_and_reason()
    {
        var failedFile = (string)typeof(Updater).GetProperty("FailedFile",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var previous = File.Exists(failedFile) ? File.ReadAllBytes(failedFile) : null;
        if (previous is not null) File.Delete(failedFile);
        Directory.CreateDirectory(failedFile);
        try
        {
            Updater.RememberFailed("v2.99");
            AssertLogged("v2.99");
        }
        finally
        {
            Directory.Delete(failedFile);
            if (previous is not null) File.WriteAllBytes(failedFile, previous);
        }
    }

    [Theory]
    [InlineData("missing-tag")]
    [InlineData("wrong-tag")]
    [InlineData("missing-archive")]
    public void An_existing_incomplete_offline_spool_explains_the_skip(string scenario)
    {
        var spool = Directory.CreateDirectory(Path.Combine(_root, "spool")).FullName;
        if (scenario != "missing-tag")
            File.WriteAllText(Path.Combine(spool, "tag"), scenario == "wrong-tag" ? "v1.99" : "v2.99");
        if (scenario != "missing-archive")
            File.WriteAllText(Path.Combine(spool, "release.tar.gz"), "archive");

        Assert.Null(Updater.TakeOfflineSpool(spool));

        AssertLogged("офлайн");
        AssertLogged(spool);
    }

    [Fact]
    public void A_missing_spool_is_normal_and_does_not_create_an_error_log()
    {
        Assert.Null(Updater.TakeOfflineSpool(Path.Combine(_root, "absent")));
        Assert.False(File.Exists(CrashLog.FilePath));
    }

    [Fact]
    public void Invalid_checksum_paths_are_not_misreported_as_a_missing_file()
    {
        Assert.Throws<ArgumentException>(() => Updater.VerifyChecksumFile("archive", "\0"));
    }

    [Fact]
    public void Invalid_offline_destination_is_not_hidden_as_an_io_failure()
    {
        var archive = Path.Combine(_root, "bestcam-station-v2.99-linux-x64.tar.gz");
        File.WriteAllText(archive, "package");
        File.WriteAllText(archive + ".sha256", Updater.Sha256(archive));

        Assert.Throws<ArgumentException>(() => Updater.StageOffline(archive, "linux-x64", "\0"));
    }

    [Theory]
    [InlineData("Ask")]
    [InlineData("Download")]
    public void Invalid_http_requests_are_not_swallowed_as_network_outages(string operation)
    {
        var method = typeof(Updater).GetMethod(operation, BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] arguments = operation == "Ask" ? ["relative-path"]
            : ["relative-path", Path.Combine(_root, "download"), null];

        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, arguments));

        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void A_failed_service_probe_reports_the_failed_command()
    {
        var command = Path.Combine(_root, "missing-command");
        var method = typeof(Updater).GetMethod("Output", BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Equal("", method.Invoke(null, [command, Array.Empty<string>()]));

        AssertLogged(command);
    }

    [Fact]
    public void Invalid_network_timeouts_are_not_misreported_as_offline()
    {
        var previous = Environment.GetEnvironmentVariable("ASTRA_UPDATE_API");
        Environment.SetEnvironmentVariable("ASTRA_UPDATE_API", "http://127.0.0.1:9/releases");
        try { Assert.Throws<ArgumentOutOfRangeException>(() => Updater.HasNetwork(TimeSpan.FromMilliseconds(-2))); }
        finally { Environment.SetEnvironmentVariable("ASTRA_UPDATE_API", previous); }
    }

    [Fact]
    public void A_missing_command_argument_is_not_misreported_as_a_service_failure()
    {
        var method = typeof(Updater).GetMethod("Shell", BindingFlags.Static | BindingFlags.NonPublic)!;

        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null,
            [null, "start", null, null, null]));

        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void A_real_collection_migration_error_is_not_treated_as_an_existing_column()
    {
        using var database = new SqliteConnection("Data Source=:memory:");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "CREATE TABLE collected_files (id INTEGER); PRAGMA query_only=1;";
        command.ExecuteNonQuery();
        var migrate = typeof(CollectionLog).GetMethod("EnsureColumn", BindingFlags.Static | BindingFlags.NonPublic)!;

        var error = Assert.Throws<TargetInvocationException>(() => migrate.Invoke(null,
            [database, "important", "ALTER TABLE collected_files ADD COLUMN important INTEGER NOT NULL DEFAULT 0"]));

        Assert.IsType<SqliteException>(error.InnerException);
    }

    [Fact]
    public void A_registry_migration_does_not_hide_duplicate_firmware_ids()
    {
        using (var registry = new DeviceRegistry(AppPaths.Database)) { }
        using (var database = new SqliteConnection($"Data Source={AppPaths.Database}"))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = """
                DROP INDEX idx_devices_firmware;
                INSERT INTO devices (id, serial, first_seen, last_seen, firmware_id)
                VALUES (1, 'one', '2026-10-20', '2026-10-20', 'same'),
                       (2, 'two', '2026-10-20', '2026-10-20', 'same');
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => { using var registry = new DeviceRegistry(AppPaths.Database); });
    }

    [Fact]
    public void Rejected_archive_marker_has_a_path_and_reason_in_the_log()
    {
        var blocked = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocked, "blocker");

        Assert.False(ArchiveGuard.Mark(blocked));

        AssertLogged(blocked);
    }

    [Fact]
    public void Device_labels_use_the_same_short_id_prefix_as_python()
    {
        Assert.Equal("ID 7", DeviceRegistry.FriendlyLabel(7, null));
        Assert.Equal("ID 7 · Патруль", DeviceRegistry.FriendlyLabel(7, "Патруль"));
    }

    private static void AssertLogged(string text)
    {
        var paths = new[] { CrashLog.FilePath, Path.Combine(AppPaths.DataDir, "app.log") };
        var logs = string.Join("\n", paths.Where(File.Exists).Select(File.ReadAllText));
        Assert.Contains(text, logs);
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
