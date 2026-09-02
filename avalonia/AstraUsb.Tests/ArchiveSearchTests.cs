using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

/// <summary>
/// Поиск по архиву и удаление найденного. Это то, ради чего записи собирают,
/// поэтому проверяется и отбор по каждому условию, и то, что защищённая запись
/// не уходит вместе с остальными при пакетном удалении.
/// </summary>
public sealed class ArchiveSearchTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0);

    private readonly string _dir = Directory.CreateTempSubdirectory("astra-archive-").FullName;
    private readonly string _db;
    private readonly string _store;

    public ArchiveSearchTests()
    {
        _db = Path.Combine(_dir, "devices.db");
        _store = Path.Combine(_dir, "store");
        Directory.CreateDirectory(_store);
        using var registry = new DeviceRegistry(_db);
    }

    private ArchiveSearch Search() => new(_db);

    /// <summary>Кладёт файл в архив и заносит его в журнал сбора.</summary>
    private string Collected(long deviceId, string name, DateTime? shotAt = null,
        DateTime? collectedAt = null, bool important = false)
    {
        var path = Path.Combine(_store, $"Device{deviceId}", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "запись");

        var log = new CollectionLog(_db);
        log.Record([new CollectedFile(deviceId, path, 1000, shotAt, collectedAt ?? Now)]);
        if (important)
            log.SetImportant(path, true);

        return path;
    }

    /// <summary>Камера, закреплённая за сотрудником в отделе.</summary>
    private long Camera(string cameraName, string person, string personnelNo, long? department)
    {
        using var registry = new DeviceRegistry(_db);
        var id = registry.ResolveByCard(null, 1, cameraName, cameraName);
        registry.Rename(id, cameraName);

        var staff = new StaffDirectory(_db);
        var employee = staff.AddEmployee(person, personnelNo, departmentId: department);
        staff.AssignDevice(id, employee);
        return id;
    }

    [Fact]
    public void An_empty_filter_returns_everything()
    {
        var camera = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        Collected(camera, "VID_0001.MP4");
        Collected(camera, "AUD_0001.WAV");

        var found = Search().Find(new ArchiveFilter());

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Kind_narrows_the_result_to_one_sort_of_file()
    {
        var camera = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        Collected(camera, "VID_0001.MP4");
        Collected(camera, "AUD_0001.WAV");
        Collected(camera, "IMG_0001.JPG");
        Collected(camera, "LOG_0001.TXT");

        Assert.Equal("VID_0001.MP4", Path.GetFileName(
            Assert.Single(Search().Find(new ArchiveFilter { Kind = MediaKind.Video })).File.DestPath));
        Assert.Single(Search().Find(new ArchiveFilter { Kind = MediaKind.Audio }));
        Assert.Single(Search().Find(new ArchiveFilter { Kind = MediaKind.Photo }));
        Assert.Single(Search().Find(new ArchiveFilter { Kind = MediaKind.Log }));
    }

    [Fact]
    public void An_unknown_extension_counts_as_a_log_not_as_video()
    {
        // Служебная выгрузка регистратора не должна попадать в видео.
        Assert.Equal(MediaKind.Log, MediaKinds.Of("SETTINGS.BIN"));
        Assert.Equal(MediaKind.Video, MediaKinds.Of("VID_0001.MOV"));
    }

    [Fact]
    public void File_name_is_matched_by_part_and_ignores_case()
    {
        var camera = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        Collected(camera, "VID_00231.MP4");
        Collected(camera, "VID_00987.MP4");

        var found = Search().Find(new ArchiveFilter { FileName = "00231" });

        Assert.Single(found);
        Assert.Single(Search().Find(new ArchiveFilter { FileName = "vid_00987" }));
    }

    [Fact]
    public void Personnel_number_and_name_find_the_owners_records()
    {
        var first = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        var second = Camera("КАМ-2", "Петров П.П.", "333333", null);
        Collected(first, "VID_0001.MP4");
        Collected(second, "VID_0002.MP4");

        Assert.Equal("Смирнов С.С.",
            Assert.Single(Search().Find(new ArchiveFilter { PersonnelNo = "222222" })).EmployeeName);
        Assert.Equal("333333",
            Assert.Single(Search().Find(new ArchiveFilter { EmployeeName = "Петров" })).PersonnelNo);
    }

    [Fact]
    public void A_department_search_includes_its_subordinate_departments()
    {
        var staff = new StaffDirectory(_db);
        var head = staff.AddDepartment("Охрана");
        var shift = staff.AddDepartment("Смена 1", parentId: head);

        var inner = Camera("КАМ-1", "Смирнов С.С.", "222222", shift);
        var outside = Camera("КАМ-2", "Петров П.П.", "333333", null);
        Collected(inner, "VID_0001.MP4");
        Collected(outside, "VID_0002.MP4");

        var found = Search().Find(new ArchiveFilter { DepartmentId = head });

        Assert.Equal("Смирнов С.С.", Assert.Single(found).EmployeeName);
    }

    [Fact]
    public void Shot_time_filters_out_records_without_a_known_time()
    {
        var camera = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        Collected(camera, "VID_0001.MP4", shotAt: Now.AddHours(-2));
        Collected(camera, "VID_0002.MP4", shotAt: null);

        var found = Search().Find(new ArchiveFilter { ShotFrom = Now.AddHours(-3) });

        // Запись без времени съёмки в отбор по съёмке не попадает: иначе
        // оператор считал бы, что она снята в этот период.
        Assert.Equal("VID_0001.MP4", Path.GetFileName(Assert.Single(found).File.DestPath));
    }

    [Fact]
    public void Collected_period_bounds_the_result()
    {
        var camera = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        Collected(camera, "VID_0001.MP4", collectedAt: Now.AddDays(-10));
        Collected(camera, "VID_0002.MP4", collectedAt: Now);

        var found = Search().Find(new ArchiveFilter
        {
            CollectedFrom = Now.AddDays(-1),
            CollectedTo = Now.AddDays(1),
        });

        Assert.Equal("VID_0002.MP4", Path.GetFileName(Assert.Single(found).File.DestPath));
    }

    [Fact]
    public void Protected_only_shows_what_is_kept_on_purpose()
    {
        var camera = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        Collected(camera, "VID_0001.MP4", important: true);
        Collected(camera, "VID_0002.MP4");

        var found = Search().Find(new ArchiveFilter { ProtectedOnly = true });

        Assert.True(Assert.Single(found).File.Important);
    }

    [Fact]
    public void The_limit_caps_the_result()
    {
        var camera = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        for (var i = 0; i < 8; i++)
            Collected(camera, $"VID_{i:0000}.MP4");

        Assert.Equal(3, Search().Find(new ArchiveFilter(), limit: 3).Count);
    }

    [Fact]
    public void Batch_delete_removes_files_and_forgets_them()
    {
        var camera = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        var first = Collected(camera, "VID_0001.MP4");
        var second = Collected(camera, "VID_0002.MP4");

        // Освобождённое место считается по файлам на диске, а не по журналу:
        // журнал мог отстать от того, что там лежит на самом деле.
        var expected = new FileInfo(first).Length + new FileInfo(second).Length;

        var rows = Search().Find(new ArchiveFilter());
        var result = Search().Delete(rows);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(expected, result.Bytes);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Equal(0, new CollectionLog(_db).Count());
    }

    [Fact]
    public void Batch_delete_skips_protected_records_and_counts_them()
    {
        var camera = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        var kept = Collected(camera, "по-случаю.mp4", important: true);
        var ordinary = Collected(camera, "обычное.mp4");

        var result = Search().Delete(Search().Find(new ArchiveFilter()));

        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(ordinary));
        Assert.Equal(1, new CollectionLog(_db).Count());
    }

    [Fact]
    public void Deleting_a_vanished_file_still_forgets_the_record()
    {
        var camera = Camera("КАМ-1", "Смирнов С.С.", "222222", null);
        var path = Collected(camera, "пропало.mp4");
        var rows = Search().Find(new ArchiveFilter());
        File.Delete(path);

        var result = Search().Delete(rows);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, new CollectionLog(_db).Count());
    }

    [Fact]
    public void The_camera_is_named_by_its_number_until_it_gets_a_name()
    {
        using (var registry = new DeviceRegistry(_db))
        {
            var id = registry.ResolveByCard(null, 1, "BESTCAM", "sdb1");
            Collected(id, "VID_0001.MP4");
        }

        var row = Assert.Single(Search().Find(new ArchiveFilter()));

        Assert.Equal("BCU-01-0001", row.CameraName);
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
