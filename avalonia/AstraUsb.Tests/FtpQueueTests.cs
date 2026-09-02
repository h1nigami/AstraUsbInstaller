using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Очередь отправки на сервер. Задание требует, чтобы при обрыве связи файлы
/// оставались в архиве, а отправка возобновлялась потом, поэтому очередь
/// обязана переживать и обрыв, и перезапуск станции.
/// </summary>
public sealed class FtpQueueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-ftp-").FullName;
    private readonly string _db;

    public FtpQueueTests()
    {
        _db = Path.Combine(_dir, "devices.db");
    }

    private FtpQueue Queue() => new(_db);

    private string File_(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "запись");
        return path;
    }

    [Fact]
    public void A_queued_file_survives_a_restart()
    {
        var path = File_("VID_0001.MP4");
        Queue().Add(path);

        // Новый экземпляр это то же, что запуск станции заново.
        Assert.Equal(path, Assert.Single(Queue().Next()).Path);
    }

    [Fact]
    public void The_same_file_is_not_queued_twice()
    {
        var path = File_("VID_0001.MP4");
        var queue = Queue();

        queue.Add(path);
        queue.Add(path);

        Assert.Equal(1, queue.Count());
    }

    [Fact]
    public void The_earliest_file_goes_first()
    {
        var queue = Queue();
        queue.Add(File_("первый.mp4"));
        queue.Add(File_("второй.mp4"));

        Assert.Equal("первый.mp4", Path.GetFileName(queue.Next()[0].Path));
    }

    [Fact]
    public void A_sent_file_leaves_the_queue()
    {
        var queue = Queue();
        queue.Add(File_("VID_0001.MP4"));

        queue.Done(queue.Next()[0].Id);

        Assert.Equal(0, queue.Count());
    }

    [Fact]
    public void A_failed_file_stays_and_counts_its_attempts()
    {
        var queue = Queue();
        queue.Add(File_("VID_0001.MP4"));

        queue.Failed(queue.Next()[0].Id, "сервер не отвечает");

        var item = Assert.Single(queue.Next());
        Assert.Equal(1, item.Attempts);
    }

    [Fact]
    public void After_too_many_failures_the_file_is_set_aside()
    {
        var queue = Queue();
        queue.Add(File_("VID_0001.MP4"));
        var id = queue.Next()[0].Id;

        for (var i = 0; i < FtpQueue.MaxAttempts; i++)
            queue.Failed(id, "сервер не отвечает");

        // Иначе один негодный файл держал бы очередь вечно.
        Assert.Empty(queue.Next());
        Assert.Equal(0, queue.Count());
        Assert.Equal(1, queue.StuckCount());
    }

    [Fact]
    public void Set_aside_files_can_be_returned_to_work()
    {
        var queue = Queue();
        queue.Add(File_("VID_0001.MP4"));
        var id = queue.Next()[0].Id;
        for (var i = 0; i < FtpQueue.MaxAttempts; i++)
            queue.Failed(id, "сервер не отвечает");

        Assert.Equal(1, queue.Retry());
        Assert.Equal(1, queue.Count());
        Assert.Equal(0, queue.StuckCount());
    }

    [Fact]
    public void Files_that_left_the_archive_are_dropped_from_the_queue()
    {
        var queue = Queue();
        var path = File_("VID_0001.MP4");
        queue.Add(path);
        File.Delete(path);

        Assert.Equal(1, queue.Prune());
        Assert.Equal(0, queue.Count());
    }

    [Fact]
    public void Sending_a_missing_file_reports_it_instead_of_throwing()
    {
        var result = FtpSender.Send(new Settings { FtpHost = "127.0.0.1" },
            Path.Combine(_dir, "нет-такого.mp4"));

        Assert.False(result.Ok);
        Assert.Contains("архиве", result.Message);
    }

    [Fact]
    public void Testing_without_an_address_says_so()
    {
        var result = FtpSender.Test(new Settings());

        Assert.False(result.Ok);
        Assert.Contains("адрес", result.Message);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
