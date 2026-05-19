#define AppName "Mouse Click Tracker"
#define AppVersion "1.2.4"
#define AppExe "MouseClickTracker.exe"
#define PublishDir "..\publish"

[Setup]
AppId={{A8B6C2D4-9F4B-4D29-9C42-1A2B3C4D5E6F}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=MouseClickTracker
DefaultDirName={localappdata}\Programs\MouseClickTracker
DefaultGroupName={#AppName}
OutputDir=output
OutputBaseFilename=MouseClickTracker-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: desktopicon; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные параметры:"; Flags: unchecked
Name: startup;     Description: "Запускать автоматически при входе в Windows"; GroupDescription: "Дополнительные параметры:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";         Filename: "{app}\{#AppExe}"
Name: "{group}\Удалить {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}";   Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#AppName}"; ValueData: "{app}\{#AppExe}"; \
  Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; \
  Description: "Запустить {#AppName}"; \
  Flags: nowait postinstall

[Code]
var
  ServerUrlPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  ServerUrlPage := CreateInputQueryPage(
    wpSelectDir,
    'Настройка',
    'Укажите параметры подключения',
    'Введите адрес сервера (например: http://192.168.1.100:5001).' + #13#10 +
    'Оставьте пустым, если сервер не используется.');
  ServerUrlPage.Add('Адрес сервера:', False);
  ServerUrlPage.Add('Имя пользователя:', False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigPath, Json: String;
begin
  if (CurStep = ssPostInstall) and (not WizardSilent()) then
  begin
    // Interactive install — write whatever the user typed into the wizard.
    // Silent (auto-update) installs skip this so the existing config survives.
    ConfigPath := ExpandConstant('{app}\appsettings.json');
    Json :=
      '{' + #13#10 +
      '  "ServerUrl": "' + ServerUrlPage.Values[0] + '",' + #13#10 +
      '  "UserName": "' + ServerUrlPage.Values[1] + '",' + #13#10 +
      '  "MachineId": ""' + #13#10 +
      '}';
    SaveStringToFile(ConfigPath, Json, False);
  end;
end;
