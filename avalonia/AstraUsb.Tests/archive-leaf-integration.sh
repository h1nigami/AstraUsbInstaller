#!/bin/sh
# docker run --rm --network none -e ARCHIVE_LEAF_TEST_CONTAINER=1 -v "${PWD}:/src:ro" mcr.microsoft.com/dotnet/sdk:8.0 sh /src/avalonia/AstraUsb.Tests/archive-leaf-integration.sh
set -eu
[ "${ARCHIVE_LEAF_TEST_CONTAINER:-}" = 1 ] && [ -f /.dockerenv ] || exit 2
mkdir -p /tmp/archive-leaf
# Меняем только физическую политику в тестовой копии. Native I/O остаётся настоящим.
awk '
    /    internal static void RequireDevice\(string device\)/ {
        skipped = 1; count++;
        print "    internal static void RequireDevice(string device) { }";
        next;
    }
    skipped && /    public sealed record DestinationIdentity/ { skipped = 0; }
    !skipped { print; }
    END { if (count != 1 || skipped) exit 2; }
' /src/avalonia/AstraUsb/Services/ArchiveGuard.cs > /tmp/archive-leaf/ArchiveGuard.LeafFixture.cs
cat > /tmp/archive-leaf/ArchiveLeaf.csproj <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <DefineConstants>ARCHIVE_LEAF_INTEGRATION</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="/src/avalonia/AstraUsb.Tests/ArchiveLeafIntegration.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/ArchiveDirectory.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/FileCopier.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/Markers.cs" />
    <Compile Include="/tmp/archive-leaf/ArchiveGuard.LeafFixture.cs" />
  </ItemGroup>
</Project>
PROJECT
dotnet build /tmp/archive-leaf/ArchiveLeaf.csproj -o /tmp/archive-leaf/bin --verbosity quiet
dotnet /tmp/archive-leaf/bin/ArchiveLeaf.dll
