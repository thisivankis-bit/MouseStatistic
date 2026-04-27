#define AppName      "Mouse Click Server"
#define ServiceName  "MouseClickServer"
#define AppVersion   "1.0"
#define AppExe       "MouseClickServer.exe"
#define PublishDir   "..\publish"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=MouseClickTracker
DefaultDirName={autopf}\MouseClickServer
DefaultGroupName={#AppName}
OutputDir=output
OutputBaseFilename=MouseClickServer-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
CloseApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Дашборд {#AppName}"; Filename: "http://localhost:5001"
Name: "{group}\Удалить {#AppName}"; Filename: "{uninstallexe}"

[Code]
var
  PortPage: TInputQueryWizardPage;

// ── Wizard pages ──────────────────────────────────────────────────────────────

procedure InitializeWizard;
begin
  PortPage := CreateInputQueryPage(
    wpSelectDir,
    'Настройка сервера',
    'Укажите порт для приёма данных от клиентов',
    'Клиенты будут отправлять статистику на этот порт.' + #13#10 +
    'Убедитесь, что порт открыт в брандмауэре (установщик сделает это автоматически).');
  PortPage.Add('Порт:', False);
  PortPage.Values[0] := '5001';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    Port := StrToIntDef(PortPage.Values[0], 0);
    if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('Введите корректный номер порта (1–65535).', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// ── Helpers ───────────────────────────────────────────────────────────────────

procedure WriteAppSettings(Port: String);
var
  Path, Json: String;
begin
  Path := ExpandConstant('{app}\appsettings.json');
  Json :=
    '{' + #13#10 +
    '  "Urls": "http://0.0.0.0:' + Port + '",' + #13#10 +
    '  "Logging": {' + #13#10 +
    '    "LogLevel": { "Default": "Warning" }' + #13#10 +
    '  }' + #13#10 +
    '}';
  SaveStringToFile(Path, Json, False);
end;

procedure InstallService;
var
  ResultCode: Integer;
  Cmd: String;
begin
  Cmd := ExpandConstant('{app}\{#AppExe}');

  // остановить и удалить старую версию если есть
  Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);

  // создать сервис с автозапуском
  Exec('sc.exe',
    'create {#ServiceName} binPath= "\"' + Cmd + '\"" DisplayName= "{#AppName}" start= auto',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // описание сервиса
  Exec('sc.exe',
    'description {#ServiceName} "Сервер сбора статистики кликов мышки"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // запустить
  Exec('sc.exe', 'start {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure AddFirewallRule(Port: String);
var
  ResultCode: Integer;
begin
  // удалить старое правило если есть
  Exec('netsh.exe',
    'advfirewall firewall delete rule name="{#AppName}"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // добавить новое
  Exec('netsh.exe',
    'advfirewall firewall add rule name="{#AppName}" dir=in action=allow protocol=TCP localport=' + Port,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// ── Installation steps ────────────────────────────────────────────────────────

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteAppSettings(PortPage.Values[0]);
    InstallService;
    AddFirewallRule(PortPage.Values[0]);
  end;
end;

// ── Uninstall ─────────────────────────────────────────────────────────────────

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec('sc.exe', 'stop {#ServiceName}',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
    Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('netsh.exe',
      'advfirewall firewall delete rule name="{#AppName}"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
