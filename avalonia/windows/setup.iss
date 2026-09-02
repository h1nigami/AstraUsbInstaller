; Установщик для Windows. Станции работают на Linux, а Windows-сборка нужна
; для проверки и показа, поэтому здесь обычная установка в папку пользователя
; и ярлыки, без служб и автозапуска.

#define AppName "BestCam Station"
#define AppPublisher "Best Electronics"
#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif

[Setup]
AppId={{8F3B5A64-51D0-4B2C-9C4E-1B5C7C6E9A21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\BestCam Station
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
VersionInfoVersion={#AppVersion}
OutputBaseFilename=BestCamStationSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Программа пишет базу и записи рядом с собой, поэтому ставится с правами
; администратора: в Program Files иначе не записать.
PrivilegesRequired=admin
UninstallDisplayIcon={app}\AstraUsb.exe

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; \
    GroupDescription: "Дополнительно:"

[Files]
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\AstraUsb.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\AstraUsb.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AstraUsb.exe"; Description: "Запустить программу"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; База станции и собранные записи остаются: их удаляет только человек.
Type: filesandordirs; Name: "{app}\logs"
