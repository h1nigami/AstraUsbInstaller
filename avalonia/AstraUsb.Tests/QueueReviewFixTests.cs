using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class QueueReviewFixTests : IDisposable
{
    private readonly string _previous = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-queue-review-").FullName;
    private readonly string _archive;
    private readonly FtpQueue _queue;
    private readonly string _path;
    public QueueReviewFixTests()
    {
        AppPaths.Root = _dir;
        _archive = Path.Combine(_dir, "archive");
        _path = Path.Combine(_archive, "Device1", "record.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        ArchiveGuard.Mark(_archive);
        File.WriteAllText(_path, "запись");
        new Settings { BackupRoot = _archive }.Save();
        _queue = new FtpQueue(Path.Combine(_dir, "queue.db"));
        _queue.Add(_path);
    }

    [Fact]
    public void Detaching_the_archive_does_not_remove_pending_files()
    {
        Directory.Move(_archive, _archive + "-detached");
        Assert.Equal(0, _queue.Prune());
        Directory.Move(_archive + "-detached", _archive);
        Assert.True(File.Exists(Assert.Single(_queue.Next()).Path));
    }

    [Fact]
    public void A_missing_file_is_kept_until_the_archive_is_confirmed()
    {
        File.Delete(_path);
        File.Delete(Path.Combine(_archive, Markers.Archive));
        Assert.Equal(0, _queue.Prune());
        Assert.Equal(1, _queue.Count());
        ArchiveGuard.Mark(_archive);
        Assert.Equal(1, _queue.Prune());
        Assert.Equal(0, _queue.Count());
    }

    public void Dispose()
    {
        AppPaths.Root = _previous;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }
}

