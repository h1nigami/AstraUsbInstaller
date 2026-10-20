param([string]$Stage = 'manual', [switch]$Linux, [switch]$BaselineFileEndpoint)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$fixture = Join-Path $repo ('.superpowers/review18/web/remount-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
foreach ($file in (git -C $repo ls-files -- avalonia/AstraUsb)) {
    $relative = $file.Substring('avalonia/AstraUsb/'.Length)
    $target = Join-Path $fixture ('app/' + $relative)
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $target
}
$guardPath = Join-Path $fixture 'app/Services/ArchiveGuard.cs'
$guard = Get-Content -LiteralPath $guardPath -Raw
# Подменено только обнаружение съёмного тома, проверки путей и native I/O остаются настоящими.
$describe = '(?s)    public static DestinationIdentity DescribeDestination\(string root\).*?(?=    public static string ResolveDestination)'
$resolve = '(?s)    public static string ResolveDestination\(Settings settings\).*?(?=    private static bool IdentityMatches)'
if ([regex]::Matches($guard, $describe).Count -ne 1 -or [regex]::Matches($guard, $resolve).Count -ne 1) { throw 'Не найдены точки подмены обнаружения тома' }
$guard = [regex]::Replace($guard, $describe, "    public static DestinationIdentity DescribeDestination(string root) => new(`"fixture`", `"`", `"archive`");`n`n")
$guard = [regex]::Replace($guard, $resolve, "    public static string ResolveDestination(Settings settings) => Environment.GetEnvironmentVariable(`"ASTRA_WEB_FIXTURE_LIVE`") ?? settings.BackupRoot;`n`n")
if ($Linux) {
    $policy = '(?s)    internal static void RequireDevice\(string device\).*?(?=    public sealed record DestinationIdentity)'
    $identity = '(?s)    private static bool IdentityMatches\(string root, Settings settings\).*?(?=    private static bool DeviceMatches)'
    if ([regex]::Matches($guard, $policy).Count -ne 1 -or [regex]::Matches($guard, $identity).Count -ne 1) { throw 'Не найдены границы физической политики тестового тома' }
    $guard = [regex]::Replace($guard, $policy, "    internal static void RequireDevice(string device) { }`n`n")
    $guard = [regex]::Replace($guard, $identity, "    private static bool IdentityMatches(string root, Settings settings) => true;`n`n")
}
Set-Content -LiteralPath $guardPath -Value $guard -Encoding utf8
if ($BaselineFileEndpoint) {
    $webPath = Join-Path $fixture 'app/Services/WebPanel.cs'
    $web = Get-Content -LiteralPath $webPath -Raw
    $baseline = git -C $repo show '52100a6:avalonia/AstraUsb/Services/WebPanel.cs' | Out-String
    $endpoint = '(?s)        app.MapGet\("/api/file",.*?(?=        app.MapPost\("/api/bay)'
    if ([regex]::Matches($web, $endpoint).Count -ne 1 -or [regex]::Matches($baseline, $endpoint).Count -ne 1) { throw 'Не найдена граница обработчика /api/file' }
    $web = [regex]::Replace($web, $endpoint, [System.Text.RegularExpressions.MatchEvaluator]{ param($match) [regex]::Match($baseline, $endpoint).Value })
    Set-Content -LiteralPath $webPath -Value $web -Encoding utf8
}
New-Item -ItemType Directory -Path (Join-Path $fixture 'runner') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WebRemountReviewIntegration.cs') -Destination (Join-Path $fixture 'runner/Program.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FtpReviewServer.cs') -Destination (Join-Path $fixture 'runner/FtpReviewServer.cs')
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <DefineConstants>WEB_REMOUNT_INTEGRATION</DefineConstants>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="../app/AstraUsb.csproj" />
    <Compile Include="Program.cs" />
    <Compile Include="FtpReviewServer.cs" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $fixture 'runner/Remount.csproj') -Encoding utf8
$mutex = [Threading.Mutex]::new($false, 'AstraUsbInstallerFix18Tests')
$mutex.WaitOne() | Out-Null
try {
    if ($Linux) {
        dotnet publish (Join-Path $fixture 'runner/Remount.csproj') -c Release -r linux-x64 --self-contained false -o (Join-Path $fixture 'linux') --nologo 2>&1 | Tee-Object -FilePath (Join-Path $repo ".superpowers/review18/web/p17-$Stage.log")
        if ($LASTEXITCODE -eq 0 -and $IsLinux) {
            dotnet (Join-Path $fixture 'linux/Remount.dll')
        }
        Write-Output "LINUX_FIXTURE=$fixture"
    } else {
        dotnet run --project (Join-Path $fixture 'runner/Remount.csproj') -c Release --nologo 2>&1 | Tee-Object -FilePath (Join-Path $repo ".superpowers/review18/web/p17-$Stage.log")
    }
    $result = $LASTEXITCODE
} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
exit $result
