using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AstraUsb.Services;

/// <summary>
/// Остальные разделы станции в веб-панели: архив с отбором и защитой,
/// камеры, сотрудники, журнал и настройки. Логика та же, что у экранов
/// киоска: здесь только разбор запроса и вызов тех же сервисов.
/// </summary>
public static class WebApi
{
    public sealed record Pick(string[]? Paths, bool On = true, string? Note = null, string? Format = null);
    public sealed record NameBody(string? Name);
    public sealed record AssignBody(long? EmployeeId);
    public sealed record StaffBody(long? Id, string? No, string? Name, string? Phone);
    public sealed record StorageBody(string? BackupRoot, int MinFreeGb, int StationNumber, string? StationPlace,
        int StorageMode, bool DeleteVideoAfterCopy, int KeepDays, bool AlarmSound, bool VoiceHints);
    public sealed record BaysBody(int BayCount, int BaysPerRow);
    public sealed record LockBody(int Minutes);
    public sealed record PasswordBody(string? Account, string? Current, string? Next, string? Repeat);
    public sealed record FtpBody(bool Enabled, string? Host, int Port, string? User, string? Password,
        string? Folder, bool Ssl);
    public sealed record SqlBody(bool Enabled, string? Host, int Port, string? Database, string? User,
        string? Password);
    public sealed record ResetBody(string? Password);

    private static readonly Dictionary<string, (string[] Paths, DateTime Until)> Packs = new();

    public static void Map(WebApplication app, Func<HttpContext, bool> authorized,
        Func<HttpContext, bool> ticketed, Func<string> issueTicket, string dbPath)
    {
        // Вся защита в одном месте: без входа не открывается ни один из адресов ниже.
        var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
            authorized(context.HttpContext) || IsDownload(context.HttpContext, ticketed)
                ? await next(context)
                : Results.Unauthorized());

        ActionLog Log() => new(dbPath);

        // --- Записи для скачивания по ссылке ---------------------------------
        // Заголовок с ключом на ссылке не передать, а запись весит гигабайты и
        // не должна целиком ложиться в память страницы. Поэтому страница
        // получает короткий билет только на чтение файлов.
        api.MapPost("/ticket", () => Results.Json(new { ticket = issueTicket() }));

        // --- Архив ------------------------------------------------------------
        api.MapGet("/departments", () =>
        {
            var staff = new StaffDirectory(dbPath);
            return Results.Json(staff.Departments()
                .Select(d => new { id = d.Id, name = staff.DepartmentPath(d.Id) }));
        });

        api.MapGet("/search", (HttpContext c) =>
        {
            var q = c.Request.Query;
            var from = Date(q["from"]) ?? DateTime.Now.AddDays(-7);
            var to = Date(q["to"]) ?? DateTime.Now;
            var camera = Camera(dbPath, q["camera"].ToString());

            var filter = new ArchiveFilter
            {
                CollectedFrom = from.Date,
                CollectedTo = to.Date.AddDays(1).AddTicks(-1),
                FileName = q["name"].ToString(),
                EmployeeName = q["employee"].ToString(),
                PersonnelNo = q["no"].ToString(),
                DeviceId = camera,
                DepartmentId = long.TryParse(q["department"], out var dep) ? dep : null,
                Kind = Enum.TryParse<MediaKind>(q["kind"], true, out var kind) ? kind : MediaKind.Any,
                ProtectedOnly = q["protected"] == "1",
            };

            var root = Root();
            var found = new ArchiveSearch(dbPath).Find(filter, 500);
            return Results.Json(new
            {
                limit = 500,
                converter = MediaTools.ConverterAvailable(),
                rows = found.Select(r => new
                {
                    path = Relative(root, r.File.DestPath),
                    file = Path.GetFileName(r.File.DestPath),
                    kind = MediaKinds.Name(r.Kind),
                    camera = r.CameraName,
                    employee = r.EmployeeName.Length > 0 ? r.EmployeeName : r.PersonnelNo,
                    department = r.Department,
                    size = r.File.SizeBytes,
                    collected = r.File.CollectedAt,
                    shot = r.File.ShotAt is null ? null : r.File.ShotAtTrusted ? "ok" : "off",
                    shotAt = r.File.ShotAt,
                    important = r.File.Important,
                    note = r.File.Note,
                    formats = MediaTools.FormatsFor(r.Kind),
                }),
            });
        });

        api.MapPost("/archive/protect", (Pick pick) =>
        {
            var rows = Chosen(dbPath, pick.Paths);
            var log = new CollectionLog(dbPath);
            foreach (var path in rows.SelectMany(r => r.LoggedPaths).Distinct())
                log.SetImportant(path, pick.On);
            Log().Write(ActionLog.Settings, pick.On
                ? $"веб-панель: защищено записей {rows.Count}"
                : $"веб-панель: снята защита с записей {rows.Count}");
            return Results.Json(new { count = rows.Count });
        });

        api.MapPost("/archive/note", (Pick pick) =>
        {
            var rows = Chosen(dbPath, pick.Paths);
            var log = new CollectionLog(dbPath);
            foreach (var path in rows.SelectMany(r => r.LoggedPaths).Distinct())
                log.SetNote(path, (pick.Note ?? "").Trim());
            return Results.Json(new { count = rows.Count });
        });

        api.MapPost("/archive/delete", (Pick pick) =>
        {
            var rows = Chosen(dbPath, pick.Paths);
            var result = new ArchiveSearch(dbPath).Delete(rows);
            Log().Write(ActionLog.Cleanup,
                $"веб-панель: удалено записей {result.Deleted}, пропущено защищённых {result.Skipped}");
            return Results.Json(new { deleted = result.Deleted, skipped = result.Skipped, failed = result.Failed });
        });

        api.MapPost("/archive/convert", (Pick pick) =>
        {
            var row = Chosen(dbPath, pick.Paths).FirstOrDefault();
            if (row is null)
                return Results.BadRequest(new { error = "записи больше нет в архиве" });

            var result = MediaTools.Convert(row.File.DestPath, pick.Format ?? "", null, dbPath);
            if (result.Ok)
                Log().Write(ActionLog.Export,
                    $"веб-панель: запись {Path.GetFileName(row.File.DestPath)} переведена в {pick.Format}");
            return Results.Json(new { ok = result.Ok, message = result.Message });
        });

        // Несколько записей одним архивом: пакуется на лету, без временного файла.
        // Список файлов длинный и в адрес не помещается: страница сначала
        // сдаёт его сюда, а скачивает уже по номеру.
        api.MapPost("/archive/pack", (Pick pick) =>
        {
            var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
            lock (Packs)
            {
                foreach (var old in Packs.Where(p => p.Value.Until < DateTime.Now).Select(p => p.Key).ToList())
                    Packs.Remove(old);
                Packs[id] = (pick.Paths ?? [], DateTime.Now.AddMinutes(10));
            }
            return Results.Json(new { id });
        });

        api.MapGet("/zip", async (HttpContext c, string id) =>
        {
            var root = Root();
            string[] wanted;
            lock (Packs)
                wanted = Packs.TryGetValue(id, out var pack) ? pack.Paths : [];
            var files = wanted.Select(p => Inside(root, p)).OfType<string>()
                .Where(File.Exists).ToList();
            if (files.Count == 0)
                return Results.NotFound(new { error = "записей больше нет в архиве" });

            Log().Write(ActionLog.Export, $"веб-панель: скачано записей {files.Count}");
            c.Response.ContentType = "application/zip";
            c.Response.Headers.ContentDisposition = $"attachment; filename=\"records-{DateTime.Now:yyyyMMdd-HHmm}.zip\"";

            c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>()!.AllowSynchronousIO = true;
            using var zip = new ZipArchive(c.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                for (var n = 2; !used.Add(name); n++)
                    name = $"{Path.GetFileNameWithoutExtension(file)} ({n}){Path.GetExtension(file)}";

                // Видео и фото уже сжаты: повторное сжатие жгло бы процессор станции зря.
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                await using var to = entry.Open();
                await using var from = File.OpenRead(file);
                await from.CopyToAsync(to, c.RequestAborted);
            }
            return Results.Empty;
        });

        // --- Камеры -----------------------------------------------------------
        api.MapGet("/devices", () =>
        {
            var staff = new StaffDirectory(dbPath);
            using var registry = new DeviceRegistry(dbPath);
            return Results.Json(new
            {
                devices = registry.ListDevices().Select(d => new
                {
                    id = d.Id,
                    name = d.Name,
                    number = d.FirmwareId,
                    label = d.Label,
                    employeeId = d.EmployeeId,
                    employee = d.EmployeeName,
                    department = staff.DepartmentPath(d.DepartmentId),
                    firstSeen = d.FirstSeen,
                    lastSeen = d.LastSeen,
                }),
                staff = staff.Employees(activeOnly: true)
                    .Select(e => new { id = e.Id, name = e.FullName, no = e.PersonnelNo }),
            });
        });

        api.MapPost("/devices/{id:long}/name", (long id, NameBody body) =>
        {
            var name = (body.Name ?? "").Trim();
            using (var registry = new DeviceRegistry(dbPath))
                registry.Rename(id, name);
            Log().Write(ActionLog.Settings, name.Length == 0
                ? $"веб-панель: имя камеры {id} снято"
                : $"веб-панель: камера {id} теперь «{name}»");
            return Results.Ok();
        });

        api.MapPost("/devices/{id:long}/employee", (long id, AssignBody body) =>
        {
            new StaffDirectory(dbPath).AssignDevice(id, body.EmployeeId);
            Log().Write(ActionLog.Settings, $"веб-панель: камера {id} закреплена за сотрудником {body.EmployeeId?.ToString() ?? "никем"}");
            return Results.Ok();
        });

        // --- Сотрудники -------------------------------------------------------
        api.MapGet("/staff", () => Results.Json(new StaffDirectory(dbPath).Employees()
            .Select(e => new { id = e.Id, no = e.PersonnelNo, name = e.FullName, phone = e.Phone, active = e.Active })));

        api.MapPost("/staff", (StaffBody body) =>
        {
            var no = (body.No ?? "").Trim();
            var name = (body.Name ?? "").Trim();
            if (no.Length == 0)
                return Results.BadRequest(new { error = "укажите номер сотрудника, он прописан в самой камере" });

            var staff = new StaffDirectory(dbPath);
            if (staff.FindByPersonnelNo(no) is { } same && same.Id != body.Id)
                return Results.BadRequest(new { error = $"номер {no} уже занят: {same.FullName}" });

            var phone = (body.Phone ?? "").Trim();
            if (body.Id is { } id && staff.Employees().FirstOrDefault(e => e.Id == id) is { } current)
                staff.UpdateEmployee(current with
                {
                    PersonnelNo = no,
                    FullName = name.Length == 0 ? no : name,
                    Phone = phone,
                });
            else
                staff.AddEmployee(name.Length == 0 ? no : name, no, phone);

            Log().Write(ActionLog.Settings, $"веб-панель: сохранена карточка {no}");
            return Results.Ok();
        });

        api.MapDelete("/staff/{id:long}", (long id) =>
        {
            new StaffDirectory(dbPath).DeleteEmployee(id);
            Log().Write(ActionLog.Settings, $"веб-панель: удалён сотрудник {id}");
            return Results.Ok();
        });

        // --- Журнал -----------------------------------------------------------
        api.MapGet("/events", (HttpContext c) =>
        {
            var from = Date(c.Request.Query["from"]) ?? DateTime.Now.Date;
            var to = Date(c.Request.Query["to"]) ?? DateTime.Now.Date;
            var events = Log().Between(from.Date, to.Date.AddDays(1).AddTicks(-1), 500);
            return Results.Json(events.Select(e => new { at = e.At, kind = e.Kind, text = e.Message }));
        });

        api.MapGet("/diagnostics", async (HttpContext c) =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "astra-diag-" + Guid.NewGuid().ToString("N"));
            try
            {
                var bundle = Diagnostics.Export(folder, dbPath);
                c.Response.ContentType = "application/zip";
                c.Response.Headers.ContentDisposition = "attachment; filename=\"diagnostics.zip\"";
                c.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>()!.AllowSynchronousIO = true;
            using var zip = new ZipArchive(c.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var file in Directory.GetFiles(bundle))
                {
                    await using var to = zip.CreateEntry(Path.GetFileName(file)).Open();
                    await using var from = File.OpenRead(file);
                    await from.CopyToAsync(to);
                }
                Log().Write(ActionLog.Export, "веб-панель: скачана диагностика");
                return Results.Empty;
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        });

        // --- Настройки --------------------------------------------------------
        api.MapGet("/settings", () =>
        {
            var s = Settings.Load();
            var queue = s.FtpEnabled ? Queue(dbPath) : "";
            return Results.Json(new
            {
                version = VersionInfo.Label(),
                storage = new
                {
                    backupRoot = s.BackupRoot,
                    minFreeGb = s.MinFreeGb,
                    stationNumber = s.StationNumber,
                    stationPlace = s.StationPlace,
                    storageMode = s.StorageMode == StorageMode.Overwrite ? 1 : 0,
                    deleteVideoAfterCopy = s.DeleteVideoAfterCopy,
                    keepDays = s.KeepDays,
                    alarmSound = s.AlarmSound,
                    voiceHints = s.VoiceHints,
                    onSystemDrive = ArchiveGuard.OnSystemDrive(s.BackupRoot),
                },
                bays = new { count = Math.Clamp(s.BayCount, 6, 30), perRow = Math.Clamp(s.BaysPerRow, 2, 6) },
                lockMinutes = s.LockTimeoutMinutes,
                account = new
                {
                    name = string.IsNullOrWhiteSpace(s.AdminAccount) ? PasswordGate.DefaultAccount : s.AdminAccount,
                    defaultPassword = string.IsNullOrEmpty(s.PasswordHash),
                },
                ftp = new { enabled = s.FtpEnabled, host = s.FtpHost, port = s.FtpPort, user = s.FtpUser,
                    folder = s.FtpFolder, ssl = s.FtpSsl, hasPassword = s.FtpPassword.Length > 0, queue },
                sql = new { enabled = s.SqlEnabled, host = s.SqlHost, port = s.SqlPort, database = s.SqlDatabase,
                    user = s.SqlUser, hasPassword = s.SqlPassword.Length > 0,
                    error = ExternalDatabase.ProviderError(s.SqlKind) },
                web = new { port = WebPanel.Port(s), ssl = s.WebSsl },
            });
        });

        api.MapPost("/settings/storage", (StorageBody b) =>
        {
            var s = Settings.Load();
            if (!s.SelectBackupRoot((b.BackupRoot ?? "").Trim()))
                return Fail("не удалось выбрать папку архива. Проверьте путь, подключение диска и права на запись");

            s.MinFreeGb = Math.Max(1, b.MinFreeGb);
            s.StationNumber = Math.Clamp(b.StationNumber, 0, 99);
            s.StationPlace = (b.StationPlace ?? "").Trim();
            s.StorageMode = b.StorageMode == 1 ? StorageMode.Overwrite : StorageMode.Warn;
            s.DeleteVideoAfterCopy = b.DeleteVideoAfterCopy;
            s.KeepDays = Math.Clamp(b.KeepDays, 0, 3650);
            s.AlarmSound = b.AlarmSound;
            s.VoiceHints = b.VoiceHints;
            return Saved(s, Log(), $"веб-панель: хранилище {s.BackupRoot}, порог {s.MinFreeGb} ГБ, "
                + $"срок {(s.KeepDays == 0 ? "бессрочно" : s.KeepDays + " дн")}", true, replace: true);
        });

        api.MapPost("/settings/bays", (BaysBody b) =>
        {
            var s = Settings.Load();
            s.BayCount = Math.Clamp(b.BayCount, 6, 30);
            s.BaysPerRow = Math.Clamp(b.BaysPerRow, 2, 6);
            // Доска строится при запуске, поэтому окна сменятся после перезапуска.
            return Saved(s, Log(), $"веб-панель: окон сбора {s.BayCount}", true);
        });

        api.MapPost("/settings/lock", (LockBody b) =>
        {
            var s = Settings.Load();
            s.LockTimeoutMinutes = Math.Clamp(b.Minutes, 0, 240);
            return Saved(s, Log(), $"веб-панель: блокировка разделов через {s.LockTimeoutMinutes} мин", true);
        });

        api.MapPost("/settings/password", (PasswordBody b) =>
        {
            var s = Settings.Load();
            if (!PasswordGate.Matches(s.PasswordHash, b.Current ?? ""))
                return Fail("текущий пароль не подошёл");
            if ((b.Next ?? "").Length < 4)
                return Fail("новый пароль короче четырёх знаков");
            if (string.IsNullOrWhiteSpace(b.Account))
                return Fail("укажите имя учётной записи");
            if (b.Next != b.Repeat)
                return Fail("новый пароль и повтор не совпали");

            s.AdminAccount = b.Account.Trim();
            s.PasswordHash = PasswordGate.Hash(b.Next!);
            return Saved(s, Log(), $"веб-панель: учётная запись администратора изменена: {s.AdminAccount}", true);
        });

        api.MapPost("/settings/ftp", (FtpBody b) =>
        {
            var s = Settings.Load();
            s.FtpEnabled = b.Enabled;
            s.FtpHost = (b.Host ?? "").Trim();
            s.FtpPort = b.Port is > 0 and < 65536 ? b.Port : 21;
            s.FtpUser = (b.User ?? "").Trim();
            // Пароль в ответах не отдаётся, поэтому пустое поле значит «не менять».
            if (!string.IsNullOrEmpty(b.Password))
                s.FtpPassword = b.Password;
            s.FtpFolder = (b.Folder ?? "").Trim();
            s.FtpSsl = b.Ssl;
            return Saved(s, Log(), s.FtpEnabled ? $"веб-панель: отправка на сервер включена {s.FtpHost}" : "веб-панель: отправка выключена", true);
        });

        api.MapPost("/ftp/test", async (FtpBody b) =>
        {
            var probe = Settings.Load();
            probe.FtpHost = (b.Host ?? "").Trim();
            probe.FtpPort = b.Port;
            probe.FtpUser = (b.User ?? "").Trim();
            if (!string.IsNullOrEmpty(b.Password))
                probe.FtpPassword = b.Password;
            probe.FtpFolder = (b.Folder ?? "").Trim();
            probe.FtpSsl = b.Ssl;
            var result = await Task.Run(() => FtpSender.Test(probe));
            return Results.Json(new { ok = result.Ok, message = result.Message });
        });

        api.MapPost("/ftp/retry", () =>
        {
            var queue = new FtpQueue(dbPath);
            var revived = queue.Retry();
            return Results.Json(new { ok = true, message = revived > 0
                ? $"возвращено в очередь: {revived}, всего ждёт {queue.Count()}"
                : $"отложенных нет, в очереди {queue.Count()}" });
        });

        api.MapPost("/settings/sql", (SqlBody b) =>
        {
            var s = Settings.Load();
            s.SqlEnabled = b.Enabled;
            s.SqlHost = (b.Host ?? "").Trim();
            s.SqlPort = b.Port is > 0 and < 65536 ? b.Port : SqlProbe.DefaultPort(s.SqlKind);
            s.SqlDatabase = (b.Database ?? "").Trim();
            s.SqlUser = (b.User ?? "").Trim();
            if (!string.IsNullOrEmpty(b.Password))
                s.SqlPassword = b.Password;
            return Saved(s, Log(), s.SqlEnabled ? $"веб-панель: внешняя база включена {s.SqlHost}" : "веб-панель: внешняя база выключена", true);
        });

        api.MapPost("/sql/test", async (SqlBody b) =>
        {
            var probe = Settings.Load();
            probe.SqlHost = (b.Host ?? "").Trim();
            probe.SqlPort = b.Port;
            probe.SqlDatabase = (b.Database ?? "").Trim();
            probe.SqlUser = (b.User ?? "").Trim();
            if (!string.IsNullOrEmpty(b.Password))
                probe.SqlPassword = b.Password;

            if (ExternalDatabase.ProviderError(probe.SqlKind) is { } missing)
                return Results.Json(new { ok = false, message = missing });

            var reachable = await SqlProbe.CheckAsync(probe.SqlHost, probe.SqlPort, TimeSpan.FromSeconds(5));
            if (!reachable.StartsWith("сервер отвечает", StringComparison.Ordinal))
                return Results.Json(new { ok = false, message = reachable });

            var result = await new ExternalDatabase(probe).CheckAsync();
            return Results.Json(new { ok = result.Ok, message = result.Ok ? $"{result.Message}, таблица учёта готова" : result.Message });
        });

        api.MapPost("/sql/sync", async () =>
        {
            var s = Settings.Load();
            var files = new CollectionLog(dbPath).CollectedBetween(DateTime.Now.AddDays(-30), DateTime.Now);
            var result = await new ExternalDatabase(s).SendAsync(files, $"BC-{Math.Clamp(s.StationNumber, 0, 99):00}");
            if (result.Ok && result.Sent > 0)
                Log().Write(ActionLog.Export, $"веб-панель: во внешнюю базу отправлено записей {result.Sent}");
            return Results.Json(new { ok = result.Ok, message = result.Message });
        });

        api.MapPost("/update/check", () =>
        {
            if (BusyMarker.Busy())
                return Results.Json(new { ok = false, message = "дождитесь конца сканирования или копирования" });
            return Results.Json(new { ok = true, message = Updater.StartService() });
        });

        // Сброс стирает архив, поэтому, кроме входа, снова спрашиваем пароль.
        api.MapPost("/factory-reset", (ResetBody b) =>
        {
            if (!PasswordGate.Matches(Settings.Load().PasswordHash, b.Password ?? ""))
                return Fail("пароль не подошёл");
            try
            {
                var r = FactoryReset.Run(dbPath, Settings.Load().BackupRoot);
                Log().Write(ActionLog.Settings, $"веб-панель: заводской сброс, устройств {r.Devices}, выгрузок {r.Backups}, записей {r.Entries}");
                StationCommands.Request(StationAction.Restart, 0);
                return Results.Json(new { ok = true, message = $"удалено: устройств {r.Devices}, выгрузок {r.Backups}, записей архива {r.Entries}. Станция перезапускается" });
            }
            catch (InvalidOperationException e)
            {
                return Fail(e.Message);
            }
        });
    }

    // --- Помощники ----------------------------------------------------------

    /// <summary>Файлы и архивы можно открыть по билету в адресе.</summary>
    private static bool IsDownload(HttpContext c, Func<HttpContext, bool> ticketed) =>
        HttpMethods.IsGet(c.Request.Method) && c.Request.Path.StartsWithSegments("/api/zip") && ticketed(c);

    private static IResult Fail(string message) => Results.BadRequest(new { error = message });

    private static IResult Saved(Settings s, ActionLog log, string what, bool ok, bool replace = false)
    {
        if (!s.Save(replace))
            return Fail("не удалось записать настройки, проверьте права на папку data");
        log.Write(ActionLog.Settings, what);
        return Results.Json(new { ok });
    }

    private static string Root() => Path.GetFullPath(Settings.Load().ResolveBackupRoot());

    private static string Relative(string root, string path)
    {
        try { return Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch (ArgumentException) { return Path.GetFileName(path); }
    }

    /// <summary>Путь внутри архива или null, если запрос вышел за его пределы.</summary>
    public static string? Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static DateTime? Date(string? text) =>
        DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Записи по относительным путям: те же строки, что видит поиск.</summary>
    private static List<ArchiveRow> Chosen(string dbPath, string[]? paths)
    {
        var root = Root();
        var wanted = (paths ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ArchiveSearch(dbPath).Find(new ArchiveFilter(), int.MaxValue)
            .Where(r => wanted.Contains(Relative(root, r.File.DestPath))).ToList();
    }

    /// <summary>Камера по номеру или имени; -1, если такой нет.</summary>
    private static long? Camera(string dbPath, string wanted)
    {
        wanted = wanted.Trim();
        if (wanted.Length == 0)
            return null;

        using var registry = new DeviceRegistry(dbPath);
        return registry.ListDevices().FirstOrDefault(d =>
            d.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            || d.FirmwareId.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            || d.Serial.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            || d.Id.ToString() == wanted)?.Id ?? -1;
    }

    private static string Queue(string dbPath)
    {
        var queue = new FtpQueue(dbPath);
        var stuck = queue.StuckCount();
        return stuck > 0 ? $"в очереди {queue.Count()}, отложено после неудач {stuck}" : $"в очереди {queue.Count()}";
    }
}
