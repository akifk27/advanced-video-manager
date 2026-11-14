#ifndef AvmVersion
  #error AvmVersion is required.
#endif

[Setup]
AppId=AdvancedVideoManager.AVM
AppName=Advanced Video Manager (AVM)
AppVersion={#AvmVersion}
AppPublisher=M.Akif Kızılcık
DefaultDirName={localappdata}\Programs\AVM
UsePreviousAppDir=no
DisableDirPage=no
DefaultGroupName=Advanced Video Manager (AVM)
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ShowLanguageDialog=yes
OutputDir=..\dist
OutputBaseFilename=AVM-Setup-x64
SetupIconFile=..\avmsoftware\Assets\images\AVM.ico
Compression=lzma2/fast
SolidCompression=yes

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\avmsoftware\Tools\ffmpeg.exe"; DestDir: "{app}\Tools"; Flags: ignoreversion
Source: "..\avmsoftware\Tools\ffprobe.exe"; DestDir: "{app}\Tools"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\AVM"; ValueType: string; ValueName: "Language"; ValueData: "{language}"; Check: ShouldSaveLanguage

[Icons]
Name: "{group}\Advanced Video Manager (AVM)"; Filename: "{app}\avmsoftware.exe"

[Run]
Filename: "{app}\avmsoftware.exe"; Flags: nowait postinstall skipifsilent; Description: "{cm:LaunchProgram,Advanced Video Manager (AVM)}"
Filename: "{app}\avmsoftware.exe"; Parameters: "--updated"; Flags: nowait; Check: IsAvmUpdate

[Code]
function OpenProcess(Access: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(ProcessHandle: THandle; Timeout: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function IsAvmUpdate: Boolean;
begin
  Result := ExpandConstant('{param:AVMUPDATEPID|0}') <> '0';
end;

function ShouldSaveLanguage: Boolean;
begin
  Result := (not IsAvmUpdate) or (not RegValueExists(HKCU, 'Software\AVM', 'Language'));
end;

function InitializeSetup: Boolean;
var
  Pid: Integer;
  ProcessHandle: THandle;
begin
  Result := True;
  if not IsAvmUpdate then
    Exit;
  Result := ExpandConstant('{param:AVMUPDATEVERSION|}') = '{#AvmVersion}';
  Pid := StrToIntDef(ExpandConstant('{param:AVMUPDATEPID|0}'), 0);
  if (not Result) or (Pid <= 0) then
  begin
    Result := False;
    Exit;
  end;
  ProcessHandle := OpenProcess($00100000, False, Pid);
  if ProcessHandle <> 0 then
  begin
    Result := WaitForSingleObject(ProcessHandle, 20000) = 0;
    CloseHandle(ProcessHandle);
  end;
end;
