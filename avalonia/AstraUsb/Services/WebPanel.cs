using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AstraUsb.Services;

/// <summary>
/// Веб-панель станции: то же состояние и тот же архив, открытые с телефона.
///
/// Сервер живёт внутри того же процесса, что и киоск: второй процесс пришлось
/// бы отдельно ставить, отдельно перезапускать и отдельно чинить. Панель
/// выключена по умолчанию, потому что открытый порт на станции это то, за что
/// отвечает уже не программа, а тот, кто её ставит.
///
/// Данные панель берёт из снимка состояния и из той же базы, ничего не
/// дублируя. Сбор от неё не зависит: если панель не поднялась, станция
/// продолжает работать, а причина попадает в журнал падений.
/// </summary>
public sealed class WebPanel : IDisposable
{
    /// <summary>Сколько живёт вход без действий.</summary>
    private static readonly TimeSpan SessionLife = TimeSpan.FromMinutes(30);

    /// <summary>Столько неудачных попыток подряд закрывают вход на минуту.</summary>
    private const int MaxAttempts = 5;

    private readonly string _dbPath;
    private readonly Dictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private WebApplication? _app;
    private int _attempts;
    private DateTime _lockedUntil = DateTime.MinValue;

    public WebPanel(string dbPath) => _dbPath = dbPath;

    /// <summary>Поднимает панель. Ошибка не мешает станции работать.</summary>
    public bool Start(Settings settings)
    {
        if (_app is not null || !settings.WebEnabled)
            return false;

        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://0.0.0.0:{Port(settings)}");

            var app = builder.Build();
            Map(app, settings);
            app.Start();

            _app = app;
            return true;
        }
        catch (Exception e)
        {
            CrashLog.Write("веб-панель не поднялась", e);
            return false;
        }
    }

    public static int Port(Settings settings) =>
        settings.WebPort is > 0 and < 65536 ? settings.WebPort : 8080;

    private void Map(WebApplication app, Settings settings)
    {
        app.MapGet("/", () => Results.Content(Page(), "text/html; charset=utf-8"));

        app.MapPost("/api/login", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            var account = form["account"].ToString();
            var password = form["password"].ToString();

            lock (_lock)
            {
                if (DateTime.Now < _lockedUntil)
                    return Results.StatusCode(429);
            }

            var current = Settings.Load();
            if (!PasswordGate.AccountMatches(current.AdminAccount, account)
                || !PasswordGate.Matches(current.PasswordHash, password))
            {
                lock (_lock)
                {
                    // Панель висит в сети постоянно, поэтому подбор пароля
                    // закрывается задержкой, а не только отказом.
                    if (++_attempts >= MaxAttempts)
                    {
                        _lockedUntil = DateTime.Now.AddMinutes(1);
                        _attempts = 0;
                    }
                }

                new ActionLog(_dbPath).Write(ActionLog.Access,
                    $"веб-панель: отказ, учётная запись «{account}»");
                return Results.Unauthorized();
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            lock (_lock)
            {
                _attempts = 0;
                _sessions[token] = DateTime.Now + SessionLife;
            }

            new ActionLog(_dbPath).Write(ActionLog.Access, $"вход в веб-панель, {account}");
            return Results.Ok(new { token });
        });

        app.MapGet("/api/state", (HttpContext context) =>
            Authorized(context) ? Results.Json(StationSnapshot.Current) : Results.Unauthorized());

        app.MapGet("/api/log", (HttpContext context) =>
        {
            if (!Authorized(context))
                return Results.Unauthorized();

            var events = new ActionLog(_dbPath)
                .Between(DateTime.Now.AddDays(-1), DateTime.Now, 100)
                .Select(e => new { at = e.At, kind = e.Kind, text = e.Message });

            return Results.Json(events);
        });

        app.MapGet("/api/archive", (HttpContext context, string? name, string? kind) =>
        {
            if (!Authorized(context))
                return Results.Unauthorized();

            var filter = new ArchiveFilter
            {
                CollectedFrom = DateTime.Now.AddDays(-30),
                CollectedTo = DateTime.Now,
                FileName = name ?? "",
                Kind = Enum.TryParse<MediaKind>(kind, true, out var parsed) ? parsed : MediaKind.Any,
            };

            var root = Path.GetFullPath(Settings.Load().BackupRoot);
            var rows = new ArchiveSearch(_dbPath).Find(filter, 100).Select(r => new
            {
                file = Path.GetFileName(r.File.DestPath),
                path = RelativeTo(root, r.File.DestPath),
                kind = MediaKinds.Name(r.Kind),
                camera = r.CameraName,
                employee = r.EmployeeName,
                size = r.File.SizeBytes,
                collected = r.File.CollectedAt,
                shielded = r.File.Important,
            });

            return Results.Json(rows);
        });

        app.MapGet("/api/file", (HttpContext context, string p) =>
        {
            if (!Authorized(context))
                return Results.Unauthorized();

            // Путь приходит относительным и складывается с корнем архива, а
            // потом проверяется, что не вышел из него: иначе панель отдавала
            // бы любой файл станции по «..» в запросе.
            var root = Path.GetFullPath(Settings.Load().BackupRoot);
            var full = Path.GetFullPath(Path.Combine(root, p));

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(root, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("путь вне архива");

            if (!File.Exists(full))
                return Results.NotFound("записи больше нет в архиве");

            new ActionLog(_dbPath).Write(ActionLog.Export,
                $"веб-панель: скачана запись {Path.GetFileName(full)}");

            // Range нужен и для перемотки в браузере, и для докачки после
            // обрыва: записи весят гигабайты.
            return Results.File(full, enableRangeProcessing: true,
                fileDownloadName: Path.GetFileName(full));
        });

        app.MapPost("/api/bay/{slot:int}/{action}", (HttpContext context, int slot, string action) =>
        {
            if (!Authorized(context))
                return Results.Unauthorized();

            var wanted = action.ToLowerInvariant() switch
            {
                "priority" => StationAction.Prioritize,
                "charge" => StationAction.ChargeOnly,
                "resume" => StationAction.Resume,
                _ => (StationAction?)null,
            };

            if (wanted is not { } request)
                return Results.BadRequest("неизвестное действие");

            StationCommands.Request(request, slot);
            new ActionLog(_dbPath).Write(ActionLog.Backup,
                $"веб-панель: отсек {slot + 1}, {action}");

            return Results.Ok(new { queued = true });
        });

        app.MapPost("/api/restart", (HttpContext context) =>
        {
            if (!Authorized(context))
                return Results.Unauthorized();

            StationCommands.Request(StationAction.Restart, 0);
            new ActionLog(_dbPath).Write(ActionLog.Settings, "веб-панель: перезапуск станции");
            return Results.Ok(new { queued = true });
        });
    }

    /// <summary>Проверяет вход и продлевает его: панель открыта, пока ею пользуются.</summary>
    private bool Authorized(HttpContext context)
    {
        var token = context.Request.Headers["X-Token"].ToString();
        if (token.Length == 0)
            return false;

        lock (_lock)
        {
            if (!_sessions.TryGetValue(token, out var until))
                return false;

            if (DateTime.Now > until)
            {
                _sessions.Remove(token);
                return false;
            }

            _sessions[token] = DateTime.Now + SessionLife;
            return true;
        }
    }

    /// <summary>Путь записи внутри архива: наружу абсолютные пути не отдаём.</summary>
    private static string RelativeTo(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }
        catch (Exception)
        {
            return Path.GetFileName(path);
        }
    }

    /// <summary>Страница панели. Лежит рядом с кодом, чтобы не требовать сборщика.</summary>
    private static string Page() => WebPage.Html;

    public void Dispose()
    {
        try
        {
            _app?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            _app?.DisposeAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Станция закрывается: разбираться уже не с чем.
        }
        finally
        {
            _app = null;
        }
    }
}
