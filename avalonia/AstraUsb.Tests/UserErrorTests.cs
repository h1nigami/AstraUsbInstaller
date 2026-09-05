using AstraUsb.Services;
using Xunit;

namespace AstraUsb.Tests;

[Collection("Каталог данных")]
public sealed class UserErrorTests : IDisposable
{
    private readonly string _root = AppPaths.Root;
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-user-error-").FullName;

    public UserErrorTests() => AppPaths.Root = _dir;

    [Fact]
    public void Technical_details_are_logged_but_not_shown_to_the_operator()
    {
        var message = UserError.Report("Не удалось выполнить действие", new IOException("SYSTEM_DETAILS_123"));

        Assert.DoesNotContain("SYSTEM_DETAILS_123", message);
        Assert.Contains("носителя", message);
        Assert.Contains("SYSTEM_DETAILS_123", File.ReadAllText(CrashLog.FilePath));
    }

    [Fact]
    public void An_unwritable_log_does_not_hide_the_friendly_message()
    {
        File.WriteAllText(AppPaths.DataDir, "занято файлом");

        var message = UserError.Report("Не удалось сохранить запись", new UnauthorizedAccessException("SYSTEM_DETAILS_123"));

        Assert.Contains("Нет доступа", message);
        Assert.DoesNotContain("SYSTEM_DETAILS_123", message);
    }

    public void Dispose()
    {
        AppPaths.Root = _root;
        Directory.Delete(_dir, true);
    }
}
