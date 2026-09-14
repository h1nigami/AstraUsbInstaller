#!/bin/sh
# Из корня репозитория, сценарии: busy, mismatch, restarts, success.
# docker run --rm --network none -e UPDATER_TEST_CONTAINER=1 -v "${PWD}:/src:ro" mcr.microsoft.com/dotnet/sdk:8.0 sh /src/avalonia/AstraUsb.Tests/updater-integration.sh busy
set -eu
[ "${UPDATER_TEST_CONTAINER:-}" = 1 ] && [ -f /.dockerenv ] || exit 2
case "$1" in busy|mismatch|restarts|success) ;; *) exit 2 ;; esac
mkdir -p /tmp/integration
cat > /tmp/integration/Integration.csproj <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <DefineConstants>UPDATER_INTEGRATION</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="/src/avalonia/AstraUsb.Tests/UpdaterIntegration.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/Updater.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/Release.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/AppPaths.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/BusyMarker.cs" />
    <Compile Include="/src/avalonia/AstraUsb/Services/VersionInfo.cs" />
  </ItemGroup>
</Project>
PROJECT
dotnet build /tmp/integration/Integration.csproj -o /tmp/test-app --verbosity quiet
cat > /bin/systemctl <<'MOCK'
#!/bin/sh
case "$1" in
    is-active) echo active ;;
    show) cat /tmp/update-restarts ;;
    reset-failed) echo 0 > /tmp/update-restarts ;;
    restart) : ;;
    *) exit 1 ;;
esac
MOCK
chmod +x /bin/systemctl
echo 0 > /tmp/update-restarts
dotnet /tmp/test-app/Integration.dll "$1"
