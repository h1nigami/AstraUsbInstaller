using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AstraUsb.Services;

public sealed class StationBusyException : IOException
{
    public StationBusyException() : base("Станция занята другой операцией") { }
}

public static class OperationGuard
{
    public static IDisposable Acquire(bool exclusive = false, string? dbPath = null)
    {
        var path = Path.GetFullPath((dbPath ?? AppPaths.Database) + ".operations.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                OperatingSystem.IsWindows() && exclusive ? FileShare.None : FileShare.ReadWrite);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33
                                      || !OperatingSystem.IsWindows() && error.HResult is 11 or 35)
        {
            throw new StationBusyException();
        }

        if (OperatingSystem.IsWindows())
            return stream;

        try
        {
            var fd = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
            while (Flock(fd, (exclusive ? 2 : 1) | 4) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 4)
                    continue;
                if (error is 11 or 35)
                    throw new StationBusyException();
                throw new IOException("Не удалось заблокировать операции", new Win32Exception(error));
            }
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static Process StartInstaller(ProcessStartInfo info, IDisposable operation)
    {
        var stream = (FileStream)operation;
        var fd = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
        info.Environment["USB_DB_PATH"] = stream.Name[..^".operations.lock".Length];
        info.Environment["ASTRA_OPERATIONS_LOCK_FD"] = fd.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var flags = Fcntl(fd, 1, 0);
        if (flags < 0 || Fcntl(fd, 2, flags & ~1) != 0)
            throw new IOException("Не удалось передать блокировку установщику",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            // Дескриптор наследует только установщик, последующие команды его не получают.
            return Process.Start(info) ?? throw new IOException("процесс не запустился");
        }
        finally
        {
            Fcntl(fd, 2, flags);
        }
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fd, int operation);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int command, int value);
}
