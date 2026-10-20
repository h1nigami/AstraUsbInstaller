#!/bin/sh
set -eu
umask 022
[ "${ARCHIVE_LEAF_TEST_CONTAINER:-}" = 1 ] && [ -f /.dockerenv ] || exit 2
mkdir -p /tmp/station-log
awk '
    /    internal static void RequireDevice\(string device\)/ {
        skipped = 1; count++;
        print "    internal static void RequireDevice(string device) { }";
        next;
    }
    skipped && /    public sealed record DestinationIdentity/ { skipped = 0; }
    !skipped { print; }
    END { if (count != 1 || skipped) exit 2; }
' /src/avalonia/AstraUsb/Services/ArchiveGuard.cs > /tmp/station-log/ArchiveGuard.Fixture.cs
cat > /tmp/station-log/StationLog.csproj <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <DefineConstants>STATION_LOG_INTEGRATION</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="/src/avalonia/AstraUsb.Tests/StationLogIntegration.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/StationLog.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/Diagnostics.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/ArchiveDirectory.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/FileCopier.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/Markers.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/AppPaths.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/CrashLog.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/OperationGuard.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/VersionInfo.cs" />
    <Compile Include="/tmp/station-log/ArchiveGuard.Fixture.cs" />
  </ItemGroup>
</Project>
PROJECT
dotnet build /tmp/station-log/StationLog.csproj -o /tmp/station-log/bin --verbosity quiet
dotnet /tmp/station-log/bin/StationLog.dll
