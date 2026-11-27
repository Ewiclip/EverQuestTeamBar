; Build-Installer.ps1 supplies an exact, validated payload and compiled inventory.
#ifndef PayloadDir
  #error Run Build-Installer.ps1; do not compile this template directly.
#endif
#ifndef VerifiedFilesInclude
  #error Missing verified file list.
#endif
#ifndef VerifiedInventoryInclude
  #error Missing compiled inventory.
#endif
#ifndef InstallerFixtureRoot
  #ifndef SetupIconFile
    #error Missing verified Team Bar application icon.
  #endif
#endif
#define ProductName "Team Bar"

[Setup]
#ifdef InstallerFixtureRoot
AppId={{B9A2C5DE-6947-4D38-A58F-8B9B496DC96E}
#else
AppId={code:InstalledProductId}
#endif
AppName={#ProductName}
AppVersion={#AppVersion}
VersionInfoVersion={#FileVersion}
AppPublisher=Team Bar
#ifndef InstallerFixtureRoot
SetupIconFile={#SetupIconFile}
#endif
#ifdef InstallerFixtureRoot
DefaultDirName={#InstallerFixtureRoot}\app
UsePreviousAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
#else
DefaultDirName={code:DefaultProgramFolder}
#endif
DisableDirPage=yes
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
UsePreviousGroup=no
UsePreviousLanguage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=TeamBar-{#AppVersion}-win-x64-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
#ifndef InstallerFixtureRoot
SetupMutex=TeamBarInstaller-3DAAF327-DC3F-4A2C-A412-C901A53990B2,TeamBarInstaller-A6501F5B-F9D7-4FBB-B31B-EE384B6A7B49
#endif
UninstallDisplayIcon={app}\TeamBar.exe
#ifdef SignedSetup
SignTool=TeamBarRelease
SignedUninstaller=yes
#endif

[Files]
#include VerifiedFilesInclude

[Icons]
#ifndef InstallerFixtureRoot
Name: "{group}\{#ProductName}"; Filename: "{app}\TeamBar.exe"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\TeamBar.exe"; Tasks: desktopicon
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
#ifndef InstallerFixtureRoot
Filename: "{app}\TeamBar.exe"; Description: "Open {#ProductName}"; Flags: nowait postinstall skipifsilent; Check: InstallationHealthy
#endif

[Code]
const
  ReparsePointAttribute = $400;
  InvalidAttributes = $FFFFFFFF;
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\';
  StandardId = '{3DAAF327-DC3F-4A2C-A412-C901A53990B2}';
  LegacyId = '{A6501F5B-F9D7-4FBB-B31B-EE384B6A7B49}';
var
  ReleasePaths, ReleaseHashes, TransactionPaths, ExistingPaths, ExistingHashes: TStringList;
  BackupRoot, OldDisplayVersion, MigrationStaging: String;
  TransactionPrepared, TransactionStarted, TransactionCommitted, RollbackFailed, MigratedLegacyData: Boolean;

function NativeGetFileAttributes(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

#include VerifiedInventoryInclude

function InstalledProductId(Param: String): String;
begin
  { Reuse an existing uninstaller and its recorded directory. This also keeps a
    loaded, identical bridge at the same absolute path on an in-place upgrade. }
  Result := StandardId;
  if RegKeyExists(HKCU, UninstallKey + LegacyId + '_is1') and
     not RegKeyExists(HKCU, UninstallKey + StandardId + '_is1') then Result := LegacyId;
end;

function DefaultProgramFolder(Param: String): String;
var Folder, Key: String;
begin
  Key := UninstallKey + InstalledProductId('') + '_is1';
  if (RegQueryStringValue(HKCU, Key, 'Inno Setup: App Path', Folder) or
      RegQueryStringValue(HKCU, Key, 'InstallLocation', Folder)) and (Folder <> '') then
    Result := RemoveBackslashUnlessRoot(Folder)
  else Result := ExpandConstant('{localappdata}\Programs\{#ProductName}');
end;

function ProcessRunning(Name: String): Boolean;
var Locator, Services, Processes: Variant;
begin
#ifdef InstallerFixtureRoot
  Result := FileExists('{#InstallerFixtureRoot}\' + Name + '.running');
  Exit;
#endif
  { Fail closed when Windows process discovery is unavailable. Never terminate a game. }
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('', 'root\CIMV2');
  Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name="' + Name + '"');
  Result := Processes.Count > 0;
end;

function InstallBackupBase(): String;
begin
#ifdef InstallerFixtureRoot
  Result := '{#InstallerFixtureRoot}\install-backups';
#else
  Result := ExpandConstant('{localappdata}\TeamBar\install-backups');
#endif
end;

function UserDataDirectory(): String;
begin
#ifdef InstallerFixtureRoot
  Result := '{#InstallerFixtureRoot}\data';
#else
  Result := ExpandConstant('{localappdata}\TeamBar\team-bar');
#endif
end;

function IsSharedNativeFile(Name: String): Boolean;
begin
  Name := Lowercase(Name);
  Result := (Name = 'teambridge.dll') or (Name = 'teamcontrollerhost.dll') or
    ((Pos('\', Name) = 0) and (ExtractFileExt(Name) = '.dll') and
      ((Pos('vcruntime', Name) = 1) or (Pos('msvcp', Name) = 1) or
      (Pos('concrt', Name) = 1) or (Pos('vcomp', Name) = 1)));
end;

function ProcessIdRunning(ProcessId: Integer): Boolean;
var Locator, Services, Processes: Variant;
begin
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('', 'root\CIMV2');
  Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE ProcessId=' + IntToStr(ProcessId));
  Result := Processes.Count > 0;
end;

procedure RequireOrdinaryPath(Path: String);
var Attributes: LongWord; Parent: String;
begin
  Attributes := NativeGetFileAttributes(Path);
  if (Attributes <> InvalidAttributes) and ((Attributes and ReparsePointAttribute) <> 0) then
    RaiseException('Linked files or directories cannot be used by Setup: ' + Path);
  Parent := ExtractFileDir(Path);
  if (Parent <> '') and (CompareText(Parent, Path) <> 0) then RequireOrdinaryPath(Parent);
end;

function SafeRelativePath(Name: String): Boolean;
var Index: Integer;
begin
  Result := (Name <> '') and (Name[1] <> '\') and (Pos('..', Name) = 0) and
    (Pos(':', Name) = 0) and (Pos('/', Name) = 0);
  if not Result then Exit;
  for Index := 1 to Length(Name) do
    if not (((Name[Index] >= 'a') and (Name[Index] <= 'z')) or
      ((Name[Index] >= 'A') and (Name[Index] <= 'Z')) or
      ((Name[Index] >= '0') and (Name[Index] <= '9')) or
      (Pos(Name[Index], '\._ -') > 0)) then begin Result := False; Exit; end;
end;

function FileDiffers(Name, ExpectedHash: String): Boolean;
var Target: String;
begin
  Target := ExpandConstant('{app}\') + Name;
  RequireOrdinaryPath(Target);
  Result := not FileExists(Target);
  if not Result then Result := CompareText(GetSHA256OfFile(Target), ExpectedHash) <> 0;
end;

#ifndef InstallerFixtureRoot
procedure RetireOldShortcut(Path: String);
var Shell, Link: Variant;
begin
  try
    if not FileExists(Path) then Exit;
    RequireOrdinaryPath(Path);
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Path);
    if CompareText(String(Link.TargetPath), ExpandConstant('{app}\TeamBar.exe')) = 0 then
      if not DeleteFile(Path) then Log('An old shortcut could not be removed: ' + Path);
  except Log('Skipped old shortcut: ' + GetExceptionMessage); end;
end;
#endif

function InstallNativeFile(Name, ExpectedHash: String): Boolean;
begin
  { A loaded, identical native DLL stays at exactly the same full path and bytes. }
  Result := FileDiffers(Name, ExpectedHash);
  if Result and ProcessRunning('eqgame.exe') then
    RaiseException('EverQuest must be closed before either native library changes.');
end;

procedure CopyVerified(Source, Destination: String);
begin
  RequireOrdinaryPath(Source); RequireOrdinaryPath(Destination);
  if not ForceDirectories(ExtractFileDir(Destination)) then RaiseException('Cannot create backup directory.');
  if not FileCopy(Source, Destination, False) then RaiseException('Cannot copy ' + Source);
  if CompareText(GetSHA256OfFile(Source), GetSHA256OfFile(Destination)) <> 0 then RaiseException('Backup verification failed: ' + Source);
end;

procedure SaveTransactionState(State: String);
begin
  if not SaveStringToFile(BackupRoot + '\state.txt', State, False) then RaiseException('Cannot record installation transaction state.');
end;

procedure CheckInterruptedTransaction(Root: String);
var FindRec: TFindRec; State, InstallPath: String; Bytes: AnsiString;
begin
  RequireOrdinaryPath(Root);
  if not DirExists(Root) then Exit;
  if FindFirst(Root + '\*', FindRec) then begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          RequireOrdinaryPath(Root + '\' + FindRec.Name);
          if LoadStringFromFile(Root + '\' + FindRec.Name + '\state.txt', Bytes) then begin
            State := Trim(String(Bytes));
            if (State = 'installing') or (State = 'rollback-incomplete') then begin
              if not LoadStringFromFile(Root + '\' + FindRec.Name + '\install-path.txt', Bytes) then
                RaiseException('An interrupted update lacks its target-path record. Preserve the backup and repair it before retrying: ' + Root + '\' + FindRec.Name);
              InstallPath := Trim(String(Bytes));
              if CompareText(InstallPath, ExpandConstant('{app}')) = 0 then
                RaiseException('An interrupted update must be recovered before installing again. Previous binaries are preserved at ' + Root + '\' + FindRec.Name + '. Run the documented backup recovery procedure with Team Bar closed.');
            end;
          end;
        end;
      until not FindNext(FindRec);
    finally FindClose(FindRec); end;
  end;
end;

function IsCredentialFile(Name: String): Boolean;
var Extension: String;
begin
  Extension := ExtractFileExt(Name);
  Result := (CompareText(Name, 'session.dat') = 0) or
    (CompareText(Copy(Name, 1, 12), 'session.dat.') = 0) or
    (CompareText(Name, 'credentials.json') = 0) or (CompareText(Name, 'credentials.bin') = 0) or
    (CompareText(Extension, '.dpapi') = 0) or (CompareText(Extension, '.pem') = 0) or
    (CompareText(Extension, '.pfx') = 0) or (CompareText(Extension, '.p12') = 0) or
    (CompareText(Extension, '.key') = 0);
end;

procedure CopyProfileTree(Source, Destination: String; ContributionData: Boolean);
var FindRec: TFindRec; Child: String;
begin
  if not DirExists(Source) then Exit;
  RequireOrdinaryPath(Source); RequireOrdinaryPath(Destination);
  if not ForceDirectories(Destination) then RaiseException('Cannot create profile backup directory.');
  if FindFirst(AddBackslash(Source) + '*', FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          Child := AddBackslash(Source) + FindRec.Name;
          RequireOrdinaryPath(Child);
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
            if ContributionData or ((CompareText(FindRec.Name, 'sessions') <> 0) and (CompareText(FindRec.Name, 'history') <> 0) and
              (CompareText(FindRec.Name, 'contributions') <> 0) and (CompareText(FindRec.Name, 'account') <> 0)) then
              CopyProfileTree(Child, AddBackslash(Destination) + FindRec.Name, ContributionData);
          end
          else if (CompareText(ExtractFileExt(FindRec.Name), '.lock') <> 0) and
            (CompareText(ExtractFileExt(FindRec.Name), '.tmp') <> 0) and (ContributionData or not IsCredentialFile(FindRec.Name)) then
            CopyVerified(Child, AddBackslash(Destination) + FindRec.Name);
        end;
      until not FindNext(FindRec);
    finally FindClose(FindRec); end;
  end;
end;

procedure ImportLegacyData;
var Source, Destination: String;
begin
#ifdef InstallerFixtureRoot
  Source := '{#InstallerFixtureRoot}\legacy-data';
#else
  Source := ExpandConstant('{localappdata}\TeamBar-QA\team-bar');
#endif
  Destination := UserDataDirectory;
  { A populated canonical directory is used as a unit. Combining two different
    libraries by filename would break references between bars and buttons. }
  if DirExists(Destination) or not DirExists(Source) then Exit;
  if not (DirExists(Source + '\profiles') or DirExists(Source + '\Contributions') or
     FileExists(Source + '\spell-overlays.json') or FileExists(Source + '\user-settings.json')) then Exit;
  MigrationStaging := Destination + '.import-' + ExtractFileName(BackupRoot);
  RequireOrdinaryPath(Source); RequireOrdinaryPath(Destination); RequireOrdinaryPath(MigrationStaging);
  if DirExists(MigrationStaging) or not ForceDirectories(MigrationStaging) then RaiseException('Cannot stage the saved Team Bar data.');
  CopyProfileTree(Source + '\profiles', MigrationStaging + '\profiles', False);
  CopyProfileTree(Source + '\Contributions', MigrationStaging + '\Contributions', True);
  if FileExists(Source + '\spell-overlays.json') then CopyVerified(Source + '\spell-overlays.json', MigrationStaging + '\spell-overlays.json');
  if FileExists(Source + '\user-settings.json') then CopyVerified(Source + '\user-settings.json', MigrationStaging + '\user-settings.json');
  if not RenameFile(MigrationStaging, Destination) then RaiseException('Cannot publish the imported data beside the retained original.');
  MigratedLegacyData := True;
end;

procedure PrepareTransaction;
var Index: Integer; Source, Destination, DataRoot: String; PreviousPaths: TStringList;
begin
  if TransactionPrepared then Exit;
  TransactionPaths.Clear; ExistingPaths.Clear; ExistingHashes.Clear;
  RequireOrdinaryPath(ExpandConstant('{app}'));
  BackupRoot := AddBackslash(InstallBackupBase) +
    GetDateTimeString('yyyymmdd-hhnnss-zzz', '-', ':') + '-' + IntToStr(Random(1000000));
  RequireOrdinaryPath(BackupRoot);
  if DirExists(BackupRoot) then RaiseException('The proposed backup directory already exists.');
  if not ForceDirectories(BackupRoot + '\binaries') then RaiseException('Cannot create update backup.');
  if not SaveStringToFile(BackupRoot + '\install-path.txt', ExpandConstant('{app}'), False) then RaiseException('Cannot record installation path.');
  PreviousPaths := TStringList.Create;
  try
    Source := ExpandConstant('{app}\installed-files.txt');
    if FileExists(Source) then begin RequireOrdinaryPath(Source); PreviousPaths.LoadFromFile(Source); end;
    for Index := 0 to PreviousPaths.Count - 1 do begin
      if not SafeRelativePath(PreviousPaths[Index]) then RaiseException('The previous installation inventory contains an unsafe path.');
      if TransactionPaths.IndexOf(PreviousPaths[Index]) < 0 then TransactionPaths.Add(PreviousPaths[Index]);
    end;
  finally PreviousPaths.Free; end;
  for Index := 0 to ReleasePaths.Count - 1 do
    if TransactionPaths.IndexOf(ReleasePaths[Index]) < 0 then TransactionPaths.Add(ReleasePaths[Index]);
  { Preserve the existing uninstaller as part of binary rollback. }
  TransactionPaths.Add('unins000.exe'); TransactionPaths.Add('unins000.dat'); TransactionPaths.Add('unins000.msg');
  for Index := 0 to TransactionPaths.Count - 1 do begin
    Source := ExpandConstant('{app}\') + TransactionPaths[Index];
    RequireOrdinaryPath(Source);
    if FileExists(Source) then begin
      Destination := BackupRoot + '\binaries\' + TransactionPaths[Index];
      CopyVerified(Source, Destination); ExistingPaths.Add(TransactionPaths[Index]);
      ExistingHashes.Add(TransactionPaths[Index] + '|' + GetSHA256OfFile(Destination));
    end;
  end;
  ExistingPaths.SaveToFile(BackupRoot + '\previous-files.txt');
  ExistingHashes.SaveToFile(BackupRoot + '\previous-hashes.txt');
  TransactionPaths.SaveToFile(BackupRoot + '\transaction-files.txt');
  { Back up library/settings only. Credentials and contributions remain untouched. }
  DataRoot := UserDataDirectory;
  CopyProfileTree(DataRoot + '\profiles', BackupRoot + '\user-data\profiles', False);
  if FileExists(DataRoot + '\user-settings.json') then CopyVerified(DataRoot + '\user-settings.json', BackupRoot + '\user-data\user-settings.json');
  if FileExists(DataRoot + '\spell-overlays.json') then CopyVerified(DataRoot + '\spell-overlays.json', BackupRoot + '\user-data\spell-overlays.json');
#ifndef InstallerFixtureRoot
  RegQueryStringValue(HKCU, UninstallKey + InstalledProductId('') + '_is1', 'DisplayVersion', OldDisplayVersion);
#endif
  SaveTransactionState('prepared');
  TransactionPrepared := True;
end;

procedure RollBackBinaries;
var Index, PreviousIndex: Integer; Source, Destination, ExpectedHash: String;
begin
  if not TransactionStarted or TransactionCommitted then Exit;
  RollbackFailed := False;
  for Index := 0 to TransactionPaths.Count - 1 do begin
    try
      Destination := ExpandConstant('{app}\') + TransactionPaths[Index];
      RequireOrdinaryPath(Destination);
      if ExistingPaths.IndexOf(TransactionPaths[Index]) >= 0 then begin
        PreviousIndex := ExistingPaths.IndexOf(TransactionPaths[Index]);
        Source := BackupRoot + '\binaries\' + TransactionPaths[Index];
        ExpectedHash := Copy(ExistingHashes[PreviousIndex], Pos('|', ExistingHashes[PreviousIndex]) + 1, 64);
        if CompareText(GetSHA256OfFile(Source), ExpectedHash) <> 0 then RaiseException('The saved rollback file hash changed: ' + Source);
        if not FileExists(Destination) then CopyVerified(Source, Destination)
        else if CompareText(GetSHA256OfFile(Source), GetSHA256OfFile(Destination)) <> 0 then CopyVerified(Source, Destination);
      end else if FileExists(Destination) then begin
        if not DeleteFile(Destination) then RaiseException('Cannot remove new file: ' + Destination);
      end;
    except
      RollbackFailed := True;
      Log('ROLLBACK FAILED: ' + GetExceptionMessage);
    end;
  end;
#ifndef InstallerFixtureRoot
  if OldDisplayVersion <> '' then RegWriteStringValue(HKCU, UninstallKey + InstalledProductId('') + '_is1', 'DisplayVersion', OldDisplayVersion);
#endif
  if MigratedLegacyData then begin
    if RenameFile(UserDataDirectory, MigrationStaging) then MigratedLegacyData := False
    else begin Log('Could not move the uncommitted data copy back to its staging directory.'); RollbackFailed := True; end;
  end;
  if RollbackFailed then begin
    SaveStringToFile(BackupRoot + '\state.txt', 'rollback-incomplete', False);
    SuppressibleMsgBox('Setup could not restore every previous binary. Do not start Team Bar until repaired. The verified backup is retained at ' + BackupRoot, mbError, MB_OK, IDOK);
  end else SaveStringToFile(BackupRoot + '\state.txt', 'rolled-back; user data was not restored or changed', False);
end;

function InitializeSetup(): Boolean;
begin
  ReleasePaths := TStringList.Create; ReleaseHashes := TStringList.Create;
  TransactionPaths := TStringList.Create; ExistingPaths := TStringList.Create;
  ExistingHashes := TStringList.Create;
  LoadReleaseInventory;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var WaitPid, Attempt, Index: Integer; NativeChange: Boolean; PreviousPaths: TStringList;
begin
  Result := '';
  try
#ifdef InstallerFixtureRoot
    { Fail before process checks, inventory reads, backups, or install writes.
      A command-line override or remembered install path cannot escape a fixture. }
    if CompareText(ExpandFileName(ExpandConstant('{app}')), ExpandFileName('{#InstallerFixtureRoot}\app')) <> 0 then
      RaiseException('Fixture target must exactly match its compiled workspace app directory.');
#endif
    WaitPid := StrToIntDef(ExpandConstant('{param:WAITFORPID|0}'), 0);
    if WaitPid > 0 then begin
      for Attempt := 1 to 120 do begin
        if not ProcessIdRunning(WaitPid) then Break;
        Sleep(250);
      end;
      if ProcessIdRunning(WaitPid) then begin
        Result := 'Team Bar has not completed its safe shutdown. Nothing was installed; retry after it exits.'; Exit;
      end;
    end;
    if ProcessRunning('TeamBar.exe') then begin
      Result := 'Exit Team Bar from its tray menu, then try again. Your saved settings will be kept.'; Exit;
    end;
    CheckInterruptedTransaction(InstallBackupBase);
#ifndef InstallerFixtureRoot
    if RegKeyExists(HKCU, UninstallKey + StandardId + '_is1') and RegKeyExists(HKCU, UninstallKey + LegacyId + '_is1') then
      RaiseException('Two older Team Bar installations are registered. Uninstall Team Bar Internal QA through Windows Installed apps, then run Setup to upgrade Team Bar. The uninstaller preserves your saved profiles.');
    if RegKeyExists(HKCU, UninstallKey + InstalledProductId('') + '_is1') and
       (CompareText(ExpandFileName(ExpandConstant('{app}')), ExpandFileName(DefaultProgramFolder(''))) <> 0) then
      RaiseException('Run Setup using the registered Team Bar program directory: ' + DefaultProgramFolder(''));
    CheckInterruptedTransaction(ExpandConstant('{localappdata}\TeamBar-QA\install-backups'));
#endif
    NativeChange := False;
    for Index := 0 to ReleasePaths.Count - 1 do
      if IsSharedNativeFile(ReleasePaths[Index]) and FileDiffers(ReleasePaths[Index], ReleaseHashes[Index]) then NativeChange := True;
    PreviousPaths := TStringList.Create;
    try
      if FileExists(ExpandConstant('{app}\installed-files.txt')) then begin
        RequireOrdinaryPath(ExpandConstant('{app}\installed-files.txt'));
        PreviousPaths.LoadFromFile(ExpandConstant('{app}\installed-files.txt'));
        for Index := 0 to PreviousPaths.Count - 1 do
          if IsSharedNativeFile(PreviousPaths[Index]) and (ReleasePaths.IndexOf(PreviousPaths[Index]) < 0) then NativeChange := True;
      end;
    finally PreviousPaths.Free; end;
    if NativeChange and ProcessRunning('eqgame.exe') then begin
      Result := 'This installation changes the native input libraries or their location. Close EverQuest, then try again. Setup will not close game clients for you.'; Exit;
    end;
    PrepareTransaction;
  except
    Result := 'Setup stopped before installing: ' + GetExceptionMessage;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Index, ExitCode: Integer; Receipt, Json, Target: String; ReceiptBytes: AnsiString;
begin
#ifndef InstallerFixtureRoot
  if (CurStep = ssDone) and TransactionCommitted and (InstalledProductId('') = LegacyId) then begin
    RetireOldShortcut(ExpandConstant('{autodesktop}\Team Bar Internal QA.lnk'));
    RetireOldShortcut(ExpandConstant('{autoprograms}\Team Bar Internal QA\Team Bar Internal QA.lnk'));
    RemoveDir(ExpandConstant('{autoprograms}\Team Bar Internal QA'));
  end;
#endif
  if CurStep = ssInstall then begin
    TransactionStarted := True;
    SaveTransactionState('installing');
  end;
  if CurStep = ssPostInstall then begin
    try
      for Index := 0 to ReleasePaths.Count - 1 do
        if FileDiffers(ReleasePaths[Index], ReleaseHashes[Index]) then RaiseException('Installed file verification failed: ' + ReleasePaths[Index]);
      Receipt := BackupRoot + '\health.json';
      ImportLegacyData;
#ifdef InstallerFixtureRoot
      if FileExists('{#InstallerFixtureRoot}\fail-health') then RaiseException('Fixture requested a failed health check.');
      if not SaveStringToFile(Receipt, '{"healthy":true,"dataSchemaCompatible":true,"version":"{#AppVersion}"}', False) then RaiseException('Cannot save fixture health receipt.');
#else
      if not Exec(ExpandConstant('{app}\TeamBar.exe'), '--installation-health-check "' + Receipt + '"', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
        RaiseException('The new build failed its installation health check.');
#endif
      if not LoadStringFromFile(Receipt, ReceiptBytes) then RaiseException('The health check did not produce a receipt.');
      Json := String(ReceiptBytes);
      StringChangeEx(Json, ' ', '', True); StringChangeEx(Json, #13, '', True); StringChangeEx(Json, #10, '', True);
      if (Pos('"healthy":true', Json) = 0) or (Pos('"dataSchemaCompatible":true', Json) = 0) or
        (Pos('"version":"{#AppVersion}"', Json) = 0) then RaiseException('The new build reported an incompatible or unexpected installation.');
      { Retire only previously inventoried files, never arbitrary files or user data. }
      for Index := 0 to TransactionPaths.Count - 1 do
        if (ReleasePaths.IndexOf(TransactionPaths[Index]) < 0) and (Pos('unins', Lowercase(TransactionPaths[Index])) <> 1) then begin
          Target := ExpandConstant('{app}\') + TransactionPaths[Index];
          if IsSharedNativeFile(TransactionPaths[Index]) and ProcessRunning('eqgame.exe') then RaiseException('EverQuest opened during the update. Close it before changing native libraries.');
          if FileExists(Target) and not DeleteFile(Target) then RaiseException('Cannot retire obsolete release file: ' + Target);
        end;
      SaveTransactionState('committed');
      TransactionCommitted := True;
    except
      Log('Installation health failure: ' + GetExceptionMessage);
      RollBackBinaries;
      RaiseException('The new Team Bar build did not pass installation checks. Previous binaries were restored when possible; settings were kept. See ' + BackupRoot);
    end;
  end;
end;

function InstallationHealthy(): Boolean;
begin Result := TransactionCommitted; end;

procedure DeinitializeSetup;
begin
  RollBackBinaries;
  ReleasePaths.Free; ReleaseHashes.Free; TransactionPaths.Free; ExistingPaths.Free; ExistingHashes.Free;
end;

function GetCustomSetupExitCode(): Integer;
begin
  if TransactionStarted and not TransactionCommitted then Result := 23 else Result := 0;
end;

function InitializeUninstall(): Boolean;
begin
  Result := False;
  try
    if ProcessRunning('TeamBar.exe') or ProcessRunning('eqgame.exe') then begin
      SuppressibleMsgBox('Close Team Bar and EverQuest before uninstalling. Saved profiles and preferences will be kept.', mbInformation, MB_OK, IDOK); Exit;
    end;
    Result := True;
  except
    SuppressibleMsgBox('The uninstaller could not check for running apps. Nothing has been removed.', mbError, MB_OK, IDOK);
  end;
end;

{ No UninstallDelete entry for user data, credentials, profiles, or backups. }
