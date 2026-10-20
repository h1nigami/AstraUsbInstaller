param([string]$Compiler = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe')
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Проверка установщика требует Windows' }
if (-not (Test-Path -LiteralPath $Compiler)) { throw "Компилятор не найден: $Compiler" }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$work = [IO.Directory]::CreateTempSubdirectory('astra-windows-install-').FullName
$name = 'BestCam Review ' + [Guid]::NewGuid().ToString('N')
$installed = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Programs/' + $name)
$source = (Join-Path $repo 'avalonia/AstraUsb/Services/AppPaths.cs').Replace('&', '&amp;')
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AssemblyName>AstraUsb</AssemblyName></PropertyGroup>
  <ItemGroup><Compile Include="$source" Link="AppPaths.cs" /></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $work 'Probe.csproj'), $project)
[IO.File]::WriteAllText((Join-Path $work 'Program.cs'), @'
using AstraUsb.Services;
try {
    AppPaths.EnsureCreated();
    File.WriteAllText(Path.Combine(AppPaths.DataDir, "probe.txt"), "settings writable");
    File.WriteAllText(Path.Combine(AppPaths.BackupsRoot, "probe.txt"), "archive retained");
    return 0;
} catch (UnauthorizedAccessException) { return 17; }
'@)
& dotnet publish (Join-Path $work 'Probe.csproj') -c Release -r win-x64 --self-contained false -o (Join-Path $work 'payload') --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать тестовый запуск приложения' }
$script = [IO.File]::ReadAllText((Join-Path $repo 'avalonia/windows/setup.iss'))
$script = $script.Replace('BestCam Station', $name)
$identity = [Guid]::NewGuid().ToString('B')
$script = [regex]::Replace($script, '(?m)^AppId=.*$', 'AppId={' + $identity)
$script = $script.Replace('Source: "payload\*"', 'Source: "' + $work + '\payload\*"')
if ($script.Contains('BestCam Station') -or -not $script.Contains($name)) { throw 'Не удалось изолировать тестовый установщик' }
[IO.File]::WriteAllText((Join-Path $work 'setup.iss'), $script)
& $Compiler '/Qp' ('/O' + $work) '/Fprobe-setup' (Join-Path $work 'setup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать тестовый установщик' }
try {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $work 'probe-setup.exe'))
    $start.UseShellExecute = $false
    foreach ($argument in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS')) { [void]$start.ArgumentList.Add($argument) }
    try { $setup = [Diagnostics.Process]::Start($start) }
    catch { throw "Обычный запуск установщика требует повышения прав: $($_.Exception.Message)" }
    if (-not $setup.WaitForExit(60000)) { $setup.Kill($true); throw 'Установщик не завершился' }
    if ($setup.ExitCode -ne 0) { throw "Установка завершилась с кодом $($setup.ExitCode)" }
    $actualInstalled = $installed
    if (-not [IO.File]::Exists((Join-Path $actualInstalled 'AstraUsb.exe'))) {
        foreach ($prefix in @('HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall', 'HKLM:/Software/Microsoft/Windows/CurrentVersion/Uninstall')) {
            $registration = Get-ItemProperty -LiteralPath (Join-Path $prefix ($identity + '_is1')) -ErrorAction SilentlyContinue
            if ($registration) { $actualInstalled = $registration.InstallLocation.TrimEnd('\') }
        }
    }
    if ([IO.Path]::GetFileName($actualInstalled) -ne $name) { throw 'Установщик вышел за отдельный тестовый каталог' }
    $application = Start-Process -FilePath (Join-Path $actualInstalled 'AstraUsb.exe') -PassThru -Wait -WindowStyle Hidden
    if ($application.ExitCode -ne 0) { throw "Обычный запуск приложения завершился с кодом $($application.ExitCode)" }
    if ($actualInstalled -ne $installed) { throw 'Установка использовала общий защищённый каталог вместо каталога пользователя' }
    foreach ($file in @('data/probe.txt', 'USB_Backups/probe.txt')) {
        if (-not [IO.File]::Exists((Join-Path $installed $file))) { throw "Приложение не смогло записать $file" }
    }
    $uninstaller = Start-Process -FilePath (Join-Path $installed 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -PassThru -Wait -WindowStyle Hidden
    if ($uninstaller.ExitCode -ne 0) { throw 'Не удалось удалить тестовое приложение' }
    foreach ($file in @('data/probe.txt', 'USB_Backups/probe.txt')) {
        if (-not [IO.File]::Exists((Join-Path $installed $file))) { throw "Удаление программы уничтожило $file" }
    }
    Write-Output 'PASS windows-install-guard: установка, запуск и сохранность данных без повышения прав'
}
finally {
    if ($actualInstalled -and [IO.Path]::GetFileName($actualInstalled) -eq $name) {
        $uninstall = Join-Path $actualInstalled 'unins000.exe'
        if (Test-Path -LiteralPath $uninstall) {
            Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -Wait | Out-Null
        }
    }
    Write-Output "Тестовые файлы: $work; $installed"
}
