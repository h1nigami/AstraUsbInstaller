; Установщик для Windows. Станции работают на Linux, а Windows-сборка нужна
; для проверки и показа, поэтому здесь обычная установка в папку пользователя
; и ярлыки, без служб. Автозапуск это ярлык в общей папке «Автозагрузка»:
; программа стартует при входе любого пользователя.
; Сон и гибернация отключаются при установке.

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
; Установка в Program Files требует администратора, но сама программа
; запускается обычным пользователем, поэтому ниже в [Dirs] каталогу выдаётся
; запись для всех: иначе data и USB_Backups создать не удастся и окно не откроется.
PrivilegesRequired=admin
UninstallDisplayIcon={app}\AstraUsb.exe

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; \
    GroupDescription: "Дополнительно:"
Name: "autostart"; Description: "Запускать при входе в Windows"; \
    GroupDescription: "Дополнительно:"

[Dirs]
Name: "{app}"; Permissions: users-modify

[Files]
Source: "payload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\AstraUsb.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\AstraUsb.exe"; Tasks: desktopicon
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\AstraUsb.exe"; Tasks: autostart

[Run]
; Станция должна работать без перерыва: сон и гибернация отключаются сразу.
Filename: "{sys}\powercfg.exe"; Parameters: "/change standby-timeout-ac 0"; \
    Flags: runhidden
Filename: "{sys}\powercfg.exe"; Parameters: "/change standby-timeout-dc 0"; \
    Flags: runhidden
Filename: "{sys}\powercfg.exe"; Parameters: "/change hibernate-timeout-ac 0"; \
    Flags: runhidden
Filename: "{sys}\powercfg.exe"; Parameters: "/change hibernate-timeout-dc 0"; \
    Flags: runhidden
Filename: "{app}\AstraUsb.exe"; Description: "Запустить программу"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; Прежние значения не сохранялись, возвращаем типовые для Windows.
Filename: "{sys}\powercfg.exe"; Parameters: "/change standby-timeout-ac 30"; \
    Flags: runhidden
Filename: "{sys}\powercfg.exe"; Parameters: "/change standby-timeout-dc 15"; \
    Flags: runhidden

[UninstallDelete]
; База станции и собранные записи остаются: их удаляет только человек.
Type: filesandordirs; Name: "{app}\logs"
