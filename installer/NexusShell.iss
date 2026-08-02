#define MyAppName "Nexus Shell"
#ifndef MyAppVersion
  #define MyAppVersion "0.8.0"
#endif
#define MyAppPublisher "Nexus"
#define MyAppExeName "Nexus.App.exe"
#define MyPayloadDirectory "app-" + MyAppVersion
#define MyPayloadMarker ".nexus-shell-payload"
#define MyPayloadMarkerMagic "NEXUS_SHELL_PAYLOAD_V1"
#define MyPayloadProductId "F20C84DD-1DF1-4F1C-95B9-EA4340F6B6C2"

[Setup]
AppId={{F20C84DD-1DF1-4F1C-95B9-EA4340F6B6C2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=Файловый менеджер и органайзер для Windows 11
DefaultDirName={localappdata}\Programs\Nexus Shell
DefaultGroupName=Nexus Shell
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\outputs
OutputBaseFilename=NexusShell-Setup-{#MyAppVersion}-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
SetupLogging=yes
SetupIconFile=..\src\Nexus.App\Assets\Nexus.ico
CloseApplications=yes
CloseApplicationsFilter=Nexus.App.exe
RestartApplications=no
Uninstallable=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyPayloadDirectory}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Nexus Shell Installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные значки:"
Name: "startupicon"; Description: "Запускать Nexus Shell при входе в Windows"; GroupDescription: "Автозапуск:"; Flags: unchecked

[Files]
Source: "..\work\installer\publish\*"; DestDir: "{app}\{#MyPayloadDirectory}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Nexus Shell"; Filename: "{app}\{#MyPayloadDirectory}\{#MyAppExeName}"; WorkingDir: "{app}\{#MyPayloadDirectory}"
Name: "{autodesktop}\Nexus Shell"; Filename: "{app}\{#MyPayloadDirectory}\{#MyAppExeName}"; WorkingDir: "{app}\{#MyPayloadDirectory}"; Tasks: desktopicon
Name: "{userstartup}\Nexus Shell"; Filename: "{app}\{#MyPayloadDirectory}\{#MyAppExeName}"; WorkingDir: "{app}\{#MyPayloadDirectory}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyPayloadDirectory}\{#MyAppExeName}"; Description: "Запустить Nexus Shell"; WorkingDir: "{app}\{#MyPayloadDirectory}"; Flags: nowait postinstall skipifsilent

[Code]
const
  ReparsePointAttribute = $400;

function IsDotEntry(const Name: String): Boolean;
begin
  Result := (Name = '.') or (Name = '..');
end;

function IsStrictVersionedPayloadName(const Name: String): Boolean;
var
  VersionPart: String;
  Character: String;
  Index: Integer;
  DotCount: Integer;
  PreviousWasDot: Boolean;
begin
  Result := False;
  if (Length(Name) <= 4) or (CompareText(Copy(Name, 1, 4), 'app-') <> 0) then
    Exit;

  VersionPart := Copy(Name, 5, Length(Name) - 4);
  DotCount := 0;
  PreviousWasDot := True;

  for Index := 1 to Length(VersionPart) do
  begin
    Character := Copy(VersionPart, Index, 1);
    if Character = '.' then
    begin
      if PreviousWasDot then
        Exit;

      DotCount := DotCount + 1;
      PreviousWasDot := True;
    end
    else
    begin
      if Pos(Character, '0123456789') = 0 then
        Exit;

      PreviousWasDot := False;
    end;
  end;

  Result := (not PreviousWasDot) and (DotCount = 2);
end;

function MarkerHasLine(
  const Lines: TArrayOfString;
  const ExpectedLine: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Lines[Index] = ExpectedLine then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function IsOwnedPayloadDirectory(
  const RootPath: String;
  const DirectoryName: String): Boolean;
var
  PayloadPath: String;
  MarkerPath: String;
  PayloadVersion: String;
  MarkerLines: TArrayOfString;
begin
  Result := False;
  if not IsStrictVersionedPayloadName(DirectoryName) then
    Exit;

  PayloadPath := AddBackslash(RootPath) + DirectoryName;
  MarkerPath := AddBackslash(PayloadPath) + '{#MyPayloadMarker}';
  if (not DirExists(PayloadPath))
    or (not FileExists(AddBackslash(PayloadPath) + '{#MyAppExeName}'))
    or (not FileExists(MarkerPath)) then
    Exit;

  if not LoadStringsFromFile(MarkerPath, MarkerLines) then
    Exit;

  PayloadVersion := Copy(DirectoryName, 5, Length(DirectoryName) - 4);
  Result :=
    MarkerHasLine(MarkerLines, 'Magic={#MyPayloadMarkerMagic}') and
    MarkerHasLine(MarkerLines, 'ProductId={#MyPayloadProductId}') and
    MarkerHasLine(MarkerLines, 'Version=' + PayloadVersion) and
    MarkerHasLine(MarkerLines, 'Executable={#MyAppExeName}');
end;

function IsRecognizedLegacyRootPayload(const RootPath: String): Boolean;
begin
  Result :=
    FileExists(AddBackslash(RootPath) + 'Nexus.App.exe') and
    FileExists(AddBackslash(RootPath) + 'Nexus.App.dll') and
    FileExists(AddBackslash(RootPath) + 'Nexus.App.deps.json') and
    FileExists(AddBackslash(RootPath) + 'Nexus.App.runtimeconfig.json') and
    FileExists(AddBackslash(RootPath) + 'Nexus.App.pri') and
    FileExists(AddBackslash(RootPath) + 'Nexus.Core.dll') and
    FileExists(AddBackslash(RootPath) + 'App.xbf') and
    FileExists(AddBackslash(RootPath) + 'MainWindow.xbf');
end;

procedure NoteLegacyRootPayload(const RootPath: String);
begin
  if IsRecognizedLegacyRootPayload(RootPath) then
  begin
    Log(
      'Recognized legacy flat Nexus payload at "' + RootPath +
      '". It is preserved because it has no ownership marker; shortcuts now target the versioned payload.');
  end;
end;

procedure RemoveOldVersionDirectories(const RootPath: String);
var
  Entry: TFindRec;
  EntryPath: String;
  LowerName: String;
begin
  if FindFirst(AddBackslash(RootPath) + 'app-*', Entry) then
  begin
    try
      repeat
        if ((Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0)
          and ((Entry.Attributes and ReparsePointAttribute) = 0)
          and (CompareText(Entry.Name, '{#MyPayloadDirectory}') <> 0) then
        begin
          LowerName := Lowercase(Entry.Name);
          if (Pos('app-', LowerName) = 1)
            and IsOwnedPayloadDirectory(RootPath, Entry.Name) then
          begin
            EntryPath := AddBackslash(RootPath) + Entry.Name;
            if DelTree(EntryPath, True, True, True) then
              Log('Removed owned old Nexus payload: "' + EntryPath + '"')
            else
              Log('Could not remove owned old Nexus payload: "' + EntryPath + '"');
          end
          else if Pos('app-', LowerName) = 1 then
          begin
            Log(
              'Skipped unowned or invalid version directory: "' +
              AddBackslash(RootPath) + Entry.Name + '"');
          end;
        end;
        if ((Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0)
          and ((Entry.Attributes and ReparsePointAttribute) <> 0)
          and (Pos('app-', Lowercase(Entry.Name)) = 1) then
        begin
          Log(
            'Skipped reparse-point version directory: "' +
            AddBackslash(RootPath) + Entry.Name + '"');
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

procedure ReconcileOptionalShortcuts;
var
  ShortcutPath: String;
begin
  if not WizardIsTaskSelected('desktopicon') then
  begin
    ShortcutPath := ExpandConstant('{autodesktop}\Nexus Shell.lnk');
    if FileExists(ShortcutPath) then
      DeleteFile(ShortcutPath);
  end;

  if not WizardIsTaskSelected('startupicon') then
  begin
    ShortcutPath := ExpandConstant('{userstartup}\Nexus Shell.lnk');
    if FileExists(ShortcutPath) then
      DeleteFile(ShortcutPath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  InstallRoot: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  InstallRoot := ExpandConstant('{app}');
  if not IsOwnedPayloadDirectory(InstallRoot, '{#MyPayloadDirectory}') then
  begin
    Log(
      'Current Nexus payload marker validation failed; old payload cleanup and shortcut reconciliation were skipped.');
    Exit;
  end;

  NoteLegacyRootPayload(InstallRoot);
  RemoveOldVersionDirectories(InstallRoot);
  ReconcileOptionalShortcuts;
end;
