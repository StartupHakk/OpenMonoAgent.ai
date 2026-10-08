; OpenMono for Windows: per-user Inno Setup installer.
; Lean installer: self-contained .NET app plus CPU llama-server and rg.exe.
; GPU flavor and models download on first run. Signing runs only when the
; WINDOWS_CERT_PATH (PFX) and WINDOWS_CERT_PASSWORD env vars are set.

#define MyAppName "OpenMono"
#define MyAppVersion GetEnv("OPENMONO_VERSION")
#if MyAppVersion == ""
  #define MyAppVersion "1.0.0-preview.1"
#endif
#define MyAppPublisher "OpenMono"
#define MyAppURL "https://openmono.ai"
#define MyAppExeName "OpenMono.exe"

[Setup]
AppId={{3F2E1D4C-7A6B-4C9D-8E5F-0A1B2C3D4E5F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\..\dist
OutputBaseFilename=OpenMonoSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
#if FileExists(SourcePath + "..\assets\icon.ico")
SetupIconFile=..\assets\icon.ico
#endif
#if FileExists(SourcePath + "..\assets\license.rtf")
LicenseFile=..\assets\license.rtf
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; VC++ redist bootstrapper (downloaded by build.ps1 into installer\deps; optional at compile time)
#if FileExists(SourcePath + "..\deps\VC_redist.x64.exe")
Source: "..\deps\VC_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsVCRedist
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\VC_redist.x64.exe"; Parameters: "/quiet /norestart"; StatusMsg: "Installing Visual C++ runtime..."; Flags: waituntilterminated; Check: NeedsVCRedist
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Never delete user data silently. The prompt below offers keep or delete.

[Code]
var
  DataPage: TInputOptionWizardPage;

function NeedsVCRedist(): Boolean;
var
  Version: String;
begin
  // VC++ 2015-2022 runtime check via the redist registry key.
  Result := not RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Version', Version);
end;

procedure InitializeWizard();
begin
  DataPage := CreateInputOptionPage(wpUninstall,
    'Uninstall data',
    'Models and sessions are kept by default.',
    'Delete large downloaded data as well?',
    False, False);
  DataPage.Add('Delete models (%LOCALAPPDATA%\OpenMono\models)');
  DataPage.Add('Delete sessions and settings (%USERPROFILE%\.openmono)');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Stop any running llama-server left behind by the app.
    Exec('taskkill.exe', '/F /IM llama-server.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if DataPage.Values[0] then
      DelTree(ExpandConstant('{localappdata}\OpenMono\models'), True, True, True);
    if DataPage.Values[1] then
      DelTree(ExpandConstant('{userprofile}\.openmono'), True, True, True);
  end;
end;
