using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AstraUsb.Services;

/// <summary>Удерживает каталог и проверяет устройство каждой открытой копии.</summary>
public sealed class ArchiveDirectory : IDisposable
{
    private const int DirectoryFlags = 0x10000 | 0x20000 | 0x80000;
    private readonly SafeFileHandle _handle;
    private readonly (string Device, ulong Inode) _identity;
    public string Root { get; }
    public string Path => OperatingSystem.IsLinux()
        ? $"/proc/self/fd/{_handle.DangerousGetHandle().ToInt32()}" : Root;

    private ArchiveDirectory(string root, SafeFileHandle handle)
    {
        Root = root;
        _handle = handle;
        _identity = Identity(handle);
    }

    internal static ArchiveDirectory Open(string root, bool create)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Защищённый архив поддерживается на Linux и Windows");
        root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        if (OperatingSystem.IsLinux())
        {
            var handle = OpenAt(-100, "/", DirectoryFlags);
            try
            {
                foreach (var name in root.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = OpenChild(handle, name, create);
                    handle.Dispose();
                    handle = next;
                }
                ArchiveGuard.RequireDevice(Identity(handle).Device);
                return new ArchiveDirectory(root, handle);
            }
            catch { handle.Dispose(); throw; }
        }
        var volume = System.IO.Path.GetPathRoot(root)!;
        var windowsHandle = OpenWindowsDirectory(volume);
        var currentPath = volume;
        try
        {
            var relative = System.IO.Path.GetRelativePath(volume, root);
            foreach (var part in relative == "." ? [] : Parts(relative))
            {
                currentPath = System.IO.Path.Combine(currentPath, part);
                RejectLinks(currentPath, checkParents: false);
                if (create)
                    Directory.CreateDirectory(currentPath);
                var child = OpenWindowsDirectory(currentPath);
                windowsHandle.Dispose();
                windowsHandle = child;
            }
            return new ArchiveDirectory(root, windowsHandle);
        }
        catch { windowsHandle.Dispose(); throw; }
    }

    public void Verify()
    {
        if (_handle.IsClosed || Identity(_handle) != _identity)
            throw new IOException("Диск назначения сменился");
        RejectLinks(Root);
        using var current = OperatingSystem.IsLinux()
            ? OpenAt(-100, Root, DirectoryFlags) : OpenWindowsDirectory(Root);
        if (Identity(current) != _identity)
            throw new IOException("Каталог назначения сменился");
        if (OperatingSystem.IsLinux())
            ArchiveGuard.RequireDevice(_identity.Device);
    }

    public ArchiveDirectory CreateDirectory(string relative) => DirectoryAt(relative, create: true);

    internal void RequireMarker()
    {
        using var marker = OperatingSystem.IsLinux()
            ? OpenAt(Fd, Markers.Archive, 0x800 | 0x20000 | 0x80000)
            : OpenWindowsHandle(System.IO.Path.Combine(Path, Markers.Archive), directory: false);
        if (Identity(marker).Device != _identity.Device || (OperatingSystem.IsLinux() && !Regular(marker)))
            throw new IOException("Не удалось подтвердить диск архива");
    }

    private ArchiveDirectory DirectoryAt(string relative, bool create)
    {
        Verify();
        var parts = Parts(relative);
        var current = OperatingSystem.IsLinux()
            ? OpenAt(Fd, ".", DirectoryFlags) : OpenWindowsDirectory(Root);
        var root = Root;
        try
        {
            foreach (var name in parts)
            {
                root = System.IO.Path.Combine(root, name);
                SafeFileHandle next;
                if (OperatingSystem.IsLinux())
                    next = OpenChild(current, name, create);
                else
                {
                    RejectLinks(root);
                    if (create)
                        Directory.CreateDirectory(root);
                    next = OpenWindowsDirectory(root);
                }
                current.Dispose();
                current = next;
                if (Identity(current).Device != _identity.Device)
                    throw new IOException("В каталоге назначения смонтирован другой диск");
            }
            Verify();
            return new ArchiveDirectory(root, current);
        }
        catch { current.Dispose(); throw; }
    }

    public long CopyFile(string source, string relative, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RejectLinks(source);
        using var sourceHandle = OpenSource(source);
        if (OperatingSystem.IsLinux() && !Regular(sourceHandle))
            throw new IOException("Источник не является обычным файлом");
        using var input = new FileStream(sourceHandle, FileAccess.Read);
        return CopyStream(input, relative, token, preserveMode: true);
    }

    public FileStream OpenRead(string relative)
    {
        var parts = DeviceFileParts(relative);
        using var parent = DirectoryAt(string.Join('/', parts[..^1]), create: false);
        var handle = OperatingSystem.IsLinux()
            ? OpenAt(parent.Fd, parts[^1], 0x800 | 0x20000 | 0x80000)
            : OpenSource(System.IO.Path.Combine(parent.Path, parts[^1]));
        try
        {
            if (Identity(handle).Device != _identity.Device || (OperatingSystem.IsLinux() && !Regular(handle)))
                throw new IOException("Источник оказался на другом устройстве");
            parent.Verify();
            Verify();
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    internal FileStream OpenRootRead(string name)
    {
        if (Parts(name).Length != 1)
            throw new IOException("Неверное имя файла");
        Verify();
        var handle = OperatingSystem.IsLinux()
            ? OpenAt(Fd, name, 0x800 | 0x20000 | 0x80000)
            : OpenSource(System.IO.Path.Combine(Path, name));
        try
        {
            VerifyRootFile(handle, name);
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    internal void AppendText(string name, string text, int? trimAtBytes = null)
    {
        if (Parts(name).Length != 1)
            throw new IOException("Неверное имя файла");
        Verify();
        using var handle = OperatingSystem.IsLinux()
            ? OpenAt(Fd, name, 2 | 0x40 | 0x400 | 0x800 | 0x20000 | 0x80000, 0x1B6)
            : OpenWindowsHandle(System.IO.Path.Combine(Path, name), directory: false, append: true);
        VerifyRootFile(handle, name);
        LockAppend(handle);
        using var output = new FileStream(handle, FileAccess.ReadWrite);
        VerifyRootFile(handle, name);
        AppendContents(output, text, trimAtBytes);
        VerifyRootFile(handle, name);
        if (OperatingSystem.IsLinux() && fsync(Fd) != 0)
            throw Error("Не удалось сохранить каталог журнала");
    }

    internal static void AppendLocalText(string text, int? trimAtBytes = null)
    {
        var path = System.IO.Path.Combine(AppPaths.DataDir, "app.log");
        using var handle = OpenAt(-100, path, 2 | 0x40 | 0x400 | 0x800 | 0x20000 | 0x80000, 0x1B6);
        if (!Regular(handle))
            throw new IOException("Локальный журнал не является обычным файлом");
        LockAppend(handle);
        using var output = new FileStream(handle, FileAccess.ReadWrite);
        AppendContents(output, text, trimAtBytes);
    }

    private static void LockAppend(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
            while (flock(handle.DangerousGetHandle().ToInt32(), 2) != 0)
                if (Marshal.GetLastPInvokeError() != 4)
                    throw Error("Не удалось заблокировать журнал");
    }

    private static void AppendContents(FileStream output, string text, int? trimAtBytes)
    {
        if (trimAtBytes is { } limit && output.Length > limit)
        {
            var tail = new byte[limit / 2];
            output.Seek(-tail.Length, SeekOrigin.End);
            output.ReadExactly(tail);
            output.SetLength(0);
            output.Position = 0;
            output.Write(tail);
        }
        output.Seek(0, SeekOrigin.End);
        output.Write(Encoding.UTF8.GetBytes(text));
        output.Flush(flushToDisk: true);
    }

    private void VerifyRootFile(SafeFileHandle handle, string name)
    {
        var identity = Identity(handle);
        if (identity.Device != _identity.Device || (OperatingSystem.IsLinux() && !Regular(handle)))
            throw new IOException("Файл журнала не является обычным файлом на диске архива");
        Verify();
        if (OperatingSystem.IsLinux() && EntryIdentity(name) != identity)
            throw new IOException("Файл журнала подменён");
    }

    public long CopyFile(ArchiveDirectory source, string sourceRelative, string relative,
        CancellationToken token = default)
    {
        using var input = source.OpenRead(sourceRelative);
        var length = CopyStream(input, relative, token, preserveMode: false);
        source.Verify();
        return length;
    }

    private long CopyStream(FileStream input, string relative, CancellationToken token, bool preserveMode)
    {
        token.ThrowIfCancellationRequested();
        var length = input.Length;
        var mtime = File.GetLastWriteTimeUtc(input.SafeFileHandle);
        WriteFile(relative, output =>
        {
            var buffer = new byte[1024 * 1024];
            int count;
            while ((count = ReadSource(input, buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            if (input.Length != length || output.Length != length)
                throw new IOException("Размер источника изменился во время копирования");
            if (preserveMode && OperatingSystem.IsLinux())
                File.SetUnixFileMode(output.SafeFileHandle, File.GetUnixFileMode(input.SafeFileHandle));
            File.SetLastAccessTimeUtc(output.SafeFileHandle, File.GetLastAccessTimeUtc(input.SafeFileHandle));
        }, mtime, creationMode: preserveMode ? 0x180u : 0x1B6u);
        return length;
    }

    internal bool IsRegularFile(string name)
    {
        if (Parts(name).Length != 1)
            throw new IOException("Неверное имя файла");
        Verify();
        return OperatingSystem.IsLinux()
            ? (BitConverter.ToUInt16(Stat(Fd, name, 0x100), 28) & 0xF000) == 0x8000
            : (File.GetAttributes(System.IO.Path.Combine(Path, name))
                & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
    }

    public bool SameFile(string source, string name)
    {
        if (Parts(name).Length != 1)
            throw new IOException("Неверное имя файла");
        Verify();
        var target = System.IO.Path.Combine(Path, name);
        RejectLinks(source);
        RejectLinks(target, checkParents: false);
        using var sourceHandle = OpenSource(source);
        using var targetHandle = OperatingSystem.IsLinux()
            ? OpenAt(Fd, name, 0x800 | 0x20000 | 0x80000)
            : File.OpenHandle(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var identity = Identity(targetHandle);
        if (identity.Device != _identity.Device
            || (OperatingSystem.IsLinux() && (!Regular(sourceHandle) || !Regular(targetHandle))))
            throw new IOException("Файл назначения оказался на другом устройстве");
        var identical = RandomAccess.GetLength(sourceHandle) == RandomAccess.GetLength(targetHandle)
            && (File.GetLastWriteTimeUtc(sourceHandle) - File.GetLastWriteTimeUtc(targetHandle)).Duration()
                < TimeSpan.FromSeconds(1);
        Verify();
        if (OperatingSystem.IsLinux() && EntryIdentity(name) != identity)
            throw new IOException("Файл назначения подменён во время проверки");
        return identical;
    }

    public void WriteText(string relative, string text) =>
        WriteFile(relative, output => output.Write(Encoding.UTF8.GetBytes(text)), null);

    public void ProduceFile(string relative, Action<string> produce) =>
        WriteFile(relative, output => produce(OperatingSystem.IsLinux()
            ? $"/proc/{Environment.ProcessId}/fd/{output.SafeFileHandle.DangerousGetHandle().ToInt32()}"
            : System.IO.Path.Combine(Path, relative)), null, producer: true);

    internal static SafeFileHandle OpenSourceDirectory(string source)
    {
        RejectLinks(source);
        return OperatingSystem.IsLinux()
            ? OpenAt(-100, source, DirectoryFlags) : OpenWindowsDirectory(source);
    }

    internal static SourceFileIdentity SourceIdentity(SafeFileHandle handle)
    {
        var (device, inode) = Identity(handle);
        if (OperatingSystem.IsLinux() && !Regular(handle))
            throw new IOException("Источник не является обычным файлом");
        return new SourceFileIdentity(device, inode, RandomAccess.GetLength(handle), File.GetLastWriteTimeUtc(handle));
    }

    internal static void DeleteSourceFile(SafeFileHandle root, string sourceRoot, string relative,
        SourceFileIdentity expected, CancellationToken token)
    {
        var parts = Parts(relative);
        if (parts.Length == 0) throw new IOException("Не задано имя исходного файла");
        var parent = OperatingSystem.IsLinux() ? OpenAt(root.DangerousGetHandle().ToInt32(), ".", DirectoryFlags)
            : OpenSourceDirectory(sourceRoot);
        var path = sourceRoot;
        try
        {
            foreach (var part in parts[..^1])
            {
                path = System.IO.Path.Combine(path, part);
                var child = OperatingSystem.IsLinux() ? OpenChild(parent, part, create: false)
                    : OpenSourceDirectory(path);
                parent.Dispose();
                parent = child;
            }
            var name = parts[^1];
            path = System.IO.Path.Combine(path, name);
            RejectLinks(path);
            using var file = OperatingSystem.IsLinux()
                ? OpenAt(parent.DangerousGetHandle().ToInt32(), name, 0x800 | 0x20000 | 0x80000)
                : OpenWindowsHandle(path, directory: false, deleting: true);
            if (SourceIdentity(file) != expected)
                throw new IOException("Исходный файл сменился после копирования");
            token.ThrowIfCancellationRequested();
            if (OperatingSystem.IsLinux())
            {
                var fd = parent.DangerousGetHandle().ToInt32();
                var quarantine = ".astra-clean-" + Guid.NewGuid().ToString("N");
                if (renameat2(fd, name, fd, quarantine, 1) != 0)
                    throw Error("Не удалось изолировать исходный файл перед удалением");
                try
                {
                    if (LinuxIdentity(Stat(fd, quarantine, 0x100)) != (expected.Device, expected.Inode)
                        || SourceIdentity(file) != expected)
                        throw new IOException("Исходный файл подменён перед удалением");
                    token.ThrowIfCancellationRequested();
                    if (unlinkat(fd, quarantine, 0) != 0) throw Error("Не удалось удалить исходный файл");
                }
                catch (Exception error)
                {
                    if (renameat2(fd, quarantine, fd, name, 1) != 0)
                    {
                        var message = $"Исходный файл сохранён под временным именем {System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, quarantine)}";
                        throw error is OperationCanceledException
                            ? new OperationCanceledException(message, error, token) : new IOException(message, error);
                    }
                    throw;
                }
            }
            else
            {
                var delete = new byte[] { 1 };
                if (!SetFileInformationByHandle(file, 4, delete, 1))
                    throw Error("Не удалось удалить исходный файл");
            }
        }
        finally { parent.Dispose(); }
    }

    internal static SafeFileHandle OpenSource(string source)
    {
        try
        {
            return OperatingSystem.IsLinux()
                ? OpenAt(-100, source, 0x800 | 0x20000 | 0x80000)
                : OpenWindowsHandle(source, directory: false);
        }
        catch (IOException error) when (DeviceLostException.IsRemoval(error))
        { throw new DeviceLostException(error); }
    }

    private static int ReadSource(FileStream input, byte[] buffer)
    {
        try { return input.Read(buffer); }
        catch (IOException error) when (DeviceLostException.IsRemoval(error))
        { throw new DeviceLostException(error); }
    }

    private void WriteFile(string relative, Action<FileStream> write, DateTime? mtime, bool producer = false,
        uint creationMode = 0x180)
    {
        var parts = Parts(relative);
        if (parts.Length == 0)
            throw new IOException("Не задано имя файла");
        using var parent = DirectoryAt(string.Join('/', parts[..^1]), create: false);
        var name = parts[^1];
        var target = System.IO.Path.Combine(parent.Path, name);
        using var handle = OperatingSystem.IsLinux()
            ? OpenAt(parent.Fd, name, 1 | 0x40 | 0x80 | 0x20000 | 0x80000, creationMode)
            : File.OpenHandle(target, FileMode.CreateNew, FileAccess.Write,
                producer ? FileShare.ReadWrite : FileShare.None);
        var identity = Identity(handle);
        using var output = new FileStream(handle, FileAccess.Write);
        try
        {
            if (identity.Device != _identity.Device || (OperatingSystem.IsLinux() && !Regular(handle)))
                throw new IOException("Файл назначения оказался на другом устройстве");
            write(output);
            if (mtime is { } modified)
                File.SetLastWriteTimeUtc(handle, modified);
            output.Flush(flushToDisk: true);
            parent.Verify();
            Verify();
            if (OperatingSystem.IsLinux() && fsync(parent.Fd) != 0)
                throw Error("Не удалось сохранить каталог");
            if (OperatingSystem.IsLinux() && parent.EntryIdentity(name) != identity)
                throw new IOException("Файл назначения подменён во время записи");
        }
        catch
        {
            try
            {
                if (!OperatingSystem.IsLinux())
                    output.Dispose();
                if (!OperatingSystem.IsLinux() || parent.EntryIdentity(name) == identity)
                    parent.Unlink(name, directory: false);
            }
            catch (IOException) { }
            throw;
        }
    }

    public long DeleteFile(string relative)
    {
        var parts = DeviceFileParts(relative);
        using var parent = DirectoryAt(string.Join('/', parts[..^1]), create: false);
        var name = parts[^1];
        var path = System.IO.Path.Combine(parent.Path, name);
        if (!System.IO.Path.Exists(path))
            return 0;
        RejectLinks(path, checkParents: false);
        using var file = OperatingSystem.IsLinux()
            ? OpenAt(parent.Fd, name, 0x800 | 0x20000 | 0x80000)
            : OpenWindowsHandle(path, directory: false, deleting: true);
        if (Identity(file).Device != _identity.Device || (OperatingSystem.IsLinux() && !Regular(file)))
            throw new IOException("Удаляемый файл оказался на другом устройстве");
        var size = RandomAccess.GetLength(file);
        parent.Verify();
        Verify();
        parent.Unlink(name, directory: false);
        return size;
    }

    public void DeleteDeviceFolder(string name)
    {
        if (!ArchiveGuard.IsDeviceFolderName(name) || Parts(name).Length != 1)
            throw new IOException("Неверная папка устройства");
        Verify();
        using (var device = DirectoryAt(name, create: false))
            device.DeleteContents();
        Verify();
        Unlink(name, directory: true);
    }

    internal void RemoveEmptyDeviceFolders()
    {
        foreach (var name in Directory.EnumerateDirectories(Path).Select(System.IO.Path.GetFileName))
        {
            if (!ArchiveGuard.IsDeviceFolderName(name))
                continue;
            using (var device = DirectoryAt(name!, create: false))
                device.RemoveEmptyContents();
            using var check = DirectoryAt(name!, create: false);
            if (!Directory.EnumerateFileSystemEntries(check.Path).Any())
            {
                check.Dispose();
                Verify();
                Unlink(name!, directory: true);
            }
        }
    }

    private void RemoveEmptyContents()
    {
        foreach (var path in Directory.EnumerateDirectories(Path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                continue;
            var name = System.IO.Path.GetFileName(path);
            using (var child = DirectoryAt(name, create: false))
            {
                child.RemoveEmptyContents();
                if (Directory.EnumerateFileSystemEntries(child.Path).Any())
                    continue;
            }
            Verify();
            Unlink(name, directory: true);
        }
    }

    private void DeleteContents()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(Path))
        {
            Verify();
            var name = System.IO.Path.GetFileName(path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("В папке устройства найдена ссылка");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                using (var child = DirectoryAt(name, create: false))
                    child.DeleteContents();
                Verify();
                Unlink(name, directory: true);
            }
            else
            {
                using var file = OperatingSystem.IsLinux()
                    ? OpenAt(Fd, name, 0x800 | 0x20000 | 0x80000)
                    : OpenWindowsHandle(path, directory: false, deleting: true);
                if (Identity(file).Device != _identity.Device || (OperatingSystem.IsLinux() && !Regular(file)))
                    throw new IOException("В папке устройства смонтирован другой диск");
                Unlink(name, directory: false);
            }
        }
    }

    private int Fd => _handle.DangerousGetHandle().ToInt32();
    private void Unlink(string name, bool directory)
    {
        if (OperatingSystem.IsLinux())
        {
            if (unlinkat(Fd, name, directory ? 0x200 : 0) != 0)
                throw Error("Не удалось удалить запись");
            if (fsync(Fd) != 0)
                throw Error("Не удалось сохранить каталог");
        }
        else if (directory)
            Directory.Delete(System.IO.Path.Combine(Path, name));
        else
            File.Delete(System.IO.Path.Combine(Path, name));
    }

    private static string[] Parts(string relative)
    {
        if (System.IO.Path.IsPathRooted(relative))
            throw new IOException("Путь выходит за каталог назначения");
        var parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.Contains(':')))
            throw new IOException("Путь выходит за каталог назначения");
        return parts;
    }

    private static string[] DeviceFileParts(string relative)
    {
        var parts = Parts(relative);
        if (parts.Length < 2 || !ArchiveGuard.IsDeviceFolderName(parts[0]) || Markers.IsService(parts[^1]))
            throw new IOException("Доступны только записи в папке DeviceN");
        return parts;
    }

    internal static void RejectLinks(string path, bool checkParents = true)
    {
        var current = System.IO.Path.GetFullPath(path);
        do
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Ссылки в пути назначения запрещены");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            if (!checkParents)
                break;
            current = System.IO.Path.GetDirectoryName(current);
        } while (!string.IsNullOrEmpty(current));
    }

    private static SafeFileHandle OpenChild(SafeFileHandle parent, string name, bool create)
    {
        var fd = parent.DangerousGetHandle().ToInt32();
        var child = openat(fd, name, DirectoryFlags, 0);
        if (child < 0 && Marshal.GetLastPInvokeError() == 2 && create)
        {
            ArchiveGuard.RequireDevice(Identity(parent).Device);
            if (mkdirat(fd, name, 0x1ED) != 0 && Marshal.GetLastPInvokeError() != 17)
                throw Error("Не удалось создать каталог");
            child = openat(fd, name, DirectoryFlags, 0);
        }
        if (child < 0)
            throw Error("Не удалось открыть каталог без ссылок");
        return new SafeFileHandle((IntPtr)child, ownsHandle: true);
    }

    private static SafeFileHandle OpenAt(int parent, string name, int flags, uint mode = 0)
    {
        var fd = openat(parent, name, flags, mode);
        if (fd < 0)
            throw Error("Не удалось открыть файл без ссылок");
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static SafeFileHandle OpenWindowsDirectory(string path) => OpenWindowsHandle(path, directory: true);

    private static SafeFileHandle OpenWindowsHandle(string path, bool directory, bool deleting = false, bool append = false)
    {
        var handle = CreateFileW(path, append ? 0xC0000000u : deleting ? 0x80010000u : 0x80000000u,
            directory ? 3u : deleting ? 5u : 1u, IntPtr.Zero, append ? 4u : 3u,
            (directory ? 0x02000000u : 0) | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Error("Не удалось открыть файл или каталог");
        }
        var info = new byte[52];
        if (!GetFileInformationByHandle(handle, info)
            || (BitConverter.ToUInt32(info, 0) & (uint)FileAttributes.ReparsePoint) != 0
            || ((BitConverter.ToUInt32(info, 0) & (uint)FileAttributes.Directory) != 0) != directory)
        {
            handle.Dispose();
            throw new IOException("Не удалось открыть каталог без ссылок");
        }
        return handle;
    }

    internal static (string Device, ulong Inode) Identity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
            return LinuxIdentity(Stat(handle.DangerousGetHandle().ToInt32(), "", 0x1000));
        var info = new byte[52];
        if (!GetFileInformationByHandle(handle, info))
            throw Error("Не удалось определить устройство назначения");
        return (BitConverter.ToUInt32(info, 28).ToString(),
            ((ulong)BitConverter.ToUInt32(info, 44) << 32) | BitConverter.ToUInt32(info, 48));
    }

    private (string Device, ulong Inode) EntryIdentity(string name) => LinuxIdentity(Stat(Fd, name, 0x100));

    private static (string Device, ulong Inode) LinuxIdentity(byte[] data) =>
        ($"{BitConverter.ToUInt32(data, 136)}:{BitConverter.ToUInt32(data, 140)}", BitConverter.ToUInt64(data, 32));

    internal static string DeviceAt(string path)
    {
        var data = Stat(-100, path, 0);
        return $"{BitConverter.ToUInt32(data, 136)}:{BitConverter.ToUInt32(data, 140)}";
    }

    private static bool Regular(SafeFileHandle handle) =>
        (BitConverter.ToUInt16(Stat(handle.DangerousGetHandle().ToInt32(), "", 0x1000), 28) & 0xF000) == 0x8000;

    private static byte[] Stat(int fd, string path, int flags)
    {
        var data = new byte[256];
        if (statx(fd, path, flags, 0x7FF, data) != 0)
            throw Error("Не удалось проверить устройство назначения");
        return data;
    }

    private static IOException Error(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));
    public void Dispose() => _handle.Dispose();

    [DllImport("libc", SetLastError = true)] private static extern int openat(int fd, string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int mkdirat(int fd, string path, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int fd, string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int renameat2(int oldFd, string oldPath, int newFd,
        string newPath, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int flock(int fd, int operation);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int fd, string path, int flags, uint mask, [Out] byte[] stat);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, [Out] byte[] info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        byte[] information, uint size);
}
