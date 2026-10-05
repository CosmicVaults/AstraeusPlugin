; Astraeus N.I.N.A. plugin installer. Needs Inno Setup 6.3 or later: https://jrsoftware.org/isinfo.php
;
; Built by the Installer target in Astraeus.csproj, not by hand:
;
;     dotnet msbuild -t:Installer -p:Configuration=Release
;
; which passes in the version and folders: /DAppVersion=1.0.0.271 /DStageDir=... /DOutputDir=...
; AppVersion is the plugin's four-part file version, the same number N.I.N.A. shows in its plugin list.
;
; N.I.N.A. loads any plugin DLL one folder below %LOCALAPPDATA%\NINA\Plugins\3.0.0\ when it starts,
; with no manifest or registry key. So this installer just copies the staged files into
; %LOCALAPPDATA%\NINA\Plugins\3.0.0\Astraeus and adds an entry under Settings > Apps to remove it.
; That's the folder N.I.N.A.'s own plugin installer uses, so a later listing in the official plugin
; repository lands in the same place. The 3.0.0 is N.I.N.A.'s plugin-API version
; (PluginMinimumApplicationVersion in NINA.Plugin.dll), the same for every 3.x release.
;
; A running N.I.N.A. holds the plugin's files open and may be in the middle of a night's imaging, so
; the installer asks the user to close it and waits. It never closes N.I.N.A. itself, which is why
; Restart Manager support is off below.

#ifndef AppVersion
  #error AppVersion is not defined. Build with: dotnet msbuild -t:Installer -p:Configuration=Release
#endif
#ifndef StageDir
  #define StageDir "..\stage"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "Astraeus"
#define AppPublisher "Cosmic Vaults"
#define AppURL "https://CosmicVaults.com"
#define PluginAssembly "CosmicVaults.NINA.Astraeus.dll"
; N.I.N.A.'s plugin-API version folder. See the header comment.
#define NinaPluginFolderVersion "3.0.0"

[Setup]
; The plugin's GUID (Properties\AssemblyInfo.cs). It never changes, so a newer installer upgrades the
; old install instead of adding a second entry in Settings > Apps.
AppId={{fd1225f8-142d-4cab-9262-8f423822d905}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} plugin installer for N.I.N.A.

DefaultDirName={localappdata}\NINA\Plugins\{#NinaPluginFolderVersion}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
; LOCALAPPDATA belongs to the user, so no elevation or UAC prompt.
PrivilegesRequired=lowest
; N.I.N.A. 3 is x64, and the bundled libvlc tree is win-x64 only.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}

; The uninstaller lives outside the plugin folder because N.I.N.A.'s plugin manager removes a plugin
; by emptying its folder, which would leave Settings > Apps pointing at a missing file.
UninstallFilesDir={userpf}\{#AppName} NINA Plugin
UninstallDisplayName={#AppName} (N.I.N.A. plugin)
; The uninstaller carries SetupIconFile's icon and lives in UninstallFilesDir, so Settings > Apps
; keeps its icon after N.I.N.A. empties the plugin folder. An .ico in {app} wouldn't survive that.
UninstallDisplayIcon={uninstallexe}
; Restart Manager would offer to close N.I.N.A. for the user, which we never do. See the header comment.
CloseApplications=no
RestartApplications=no
SetupLogging=yes
WizardStyle=modern
; Both made from cosmic-vaults-mark-512.png beside this script. The icon holds 16 to 256 px. The
; wizard images are one per DPI step (100, 150, 200 and 250%) and Setup picks the closest.
SetupIconFile=Astraeus.ico
WizardSmallImageFile=WizardSmallImage-58.png,WizardSmallImage-87.png,WizardSmallImage-116.png,WizardSmallImage-159.png

[Files]
; The Package target's stage folder, the same set the N.I.N.A. manifest zip is built from, so the
; two can't drift. ignoreversion always replaces, because the libvlc binaries keep the same version
; across our releases.
Source: "{#StageDir}\{#PluginAssembly}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\CosmicVaults.NINA.Astraeus.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#StageDir}\LibVLCSharp.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\3rd-party-licenses.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\licenses\*.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
; Must sit at libvlc\win-x64 beside the DLL, since the plugin hands LibVLC that path.
Source: "{#StageDir}\libvlc\win-x64\*"; DestDir: "{app}\libvlc\win-x64"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Start from an empty folder, so an upgrade cannot leave behind a libvlc module or an old DLL
; that N.I.N.A.'s loader would still pick up.
Type: filesandordirs; Name: "{app}"

[UninstallDelete]
; libvlc writes a plugins.dat cache into its own tree at runtime. Inno only knows the files it
; installed, so take the whole folder.
Type: filesandordirs; Name: "{app}"

[Messages]
WelcomeLabel2=This will install [name/ver] into N.I.N.A.'s plugin folder for the current Windows user.%n%nClose N.I.N.A. before continuing: it holds a plugin's files open while it runs, and Setup will wait until it has closed.
FinishedLabel=Setup has finished installing [name] on your computer.%n%nStart N.I.N.A. and you will find Astraeus under Plugins > Installed.

[Code]
const
  NinaProcessName = 'NINA.exe';

function NinaBaseFolder: String;
begin
  Result := ExpandConstant('{localappdata}\NINA');
end;

function NinaPluginsRoot: String;
begin
  Result := NinaBaseFolder + '\Plugins';
end;

function NinaPluginsFolder: String;
begin
  Result := NinaPluginsRoot + '\{#NinaPluginFolderVersion}';
end;

{ Asks WMI whether NINA.exe is running. If WMI is broken we say no. A wrong yes would block the
  install for good, and a wrong no just means a file-in-use error they can retry. }
function IsNinaRunning: Boolean;
var
  Locator, Service, Processes: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('localhost', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT Name FROM Win32_Process WHERE Name = ''' + NinaProcessName + '''');
    Result := Processes.Count > 0;
  except
    Log('WMI process query failed; assuming N.I.N.A. is not running. ' + GetExceptionMessage);
  end;
end;

{ Keeps asking until N.I.N.A. is closed or the user gives up. We never close it ourselves, since
  it may be in the middle of a night's imaging. }
function WaitForNinaToClose(const Action: String): Boolean;
begin
  Result := True;
  while IsNinaRunning do
  begin
    if MsgBox('N.I.N.A. is running, and it keeps a plugin''s files open while it runs.' + #13#10#13#10 +
              'Finish or stop what it is doing, close N.I.N.A., then click Retry to ' + Action + '.',
              mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

procedure OfferToRemove(const Path: String; const IsFolder: Boolean);
var
  Removed: Boolean;
begin
  if MsgBox('Another copy of Astraeus was found:' + #13#10#13#10 + Path + #13#10#13#10 +
            'N.I.N.A. would load both. Remove it?', mbConfirmation, MB_YESNO) <> IDYES then
    Exit;
  if IsFolder then
    Removed := DelTree(Path, True, True, True)
  else
    Removed := DeleteFile(Path);
  if not Removed then
    MsgBox('Could not remove ' + Path + '. Please delete it yourself before starting N.I.N.A.',
           mbError, MB_OK);
end;

{ Another copy of the DLL anywhere under the plugins folder (a manual unzip, a dev build) would
  make N.I.N.A. load the plugin twice. Offer to remove each one. }
procedure RemoveOtherCopies;
var
  Root, Candidate: String;
  FindRec: TFindRec;
begin
  Root := NinaPluginsFolder;
  if FileExists(Root + '\{#PluginAssembly}') then
    OfferToRemove(Root + '\{#PluginAssembly}', False);
  if not FindFirst(Root + '\*', FindRec) then
    Exit;
  try
    repeat
      if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
         (FindRec.Name <> '.') and (FindRec.Name <> '..') then
      begin
        Candidate := Root + '\' + FindRec.Name;
        if (CompareText(Candidate, ExpandConstant('{app}')) <> 0) and
           FileExists(Candidate + '\{#PluginAssembly}') then
          OfferToRemove(Candidate, True);
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not DirExists(NinaBaseFolder) then
  begin
    MsgBox('N.I.N.A. does not seem to have been run by this Windows user yet: there is no ' +
           NinaBaseFolder + ' folder.' + #13#10#13#10 +
           'Astraeus will be installed anyway, and N.I.N.A. picks it up the first time it starts. ' +
           'If you use N.I.N.A. under a different Windows account, run this Setup from that account instead.',
           mbInformation, MB_OK);
  end
  else if DirExists(NinaPluginsRoot) and not DirExists(NinaPluginsFolder) then
  begin
    { On its first run N.I.N.A. 3 moves older plugins into the 3.0.0 folder, but only if that
      folder doesn't exist yet. Creating it here first would skip the move. }
    if MsgBox('N.I.N.A. 3 has not been started on this account since it was installed or upgraded. ' +
              'The first time it runs it moves your existing plugins into their new folder, and if ' +
              'Astraeus creates that folder first, that move is skipped.' + #13#10#13#10 +
              'Start N.I.N.A. once, close it, then run this Setup again.' + #13#10#13#10 +
              'Install anyway?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
      Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not WaitForNinaToClose('continue') then
  begin
    Result := 'Setup cannot continue while N.I.N.A. is running.';
    Exit;
  end;
  RemoveOtherCopies;
end;

function InitializeUninstall: Boolean;
begin
  Result := WaitForNinaToClose('remove Astraeus');
end;
