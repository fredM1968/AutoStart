; Installeur du plugin NINA "AutoStart" (Inno Setup 6, gratuit : https://jrsoftware.org/isinfo.php)
;
; Mode d'emploi :
;   1. Compiler le projet en Release (Générer > Régénérer la solution, configuration Release, NINA fermé).
;   2. Ouvrir ce fichier dans Inno Setup, puis Build > Compile (ou F9).
;   3. L'installeur est créé dans le dossier "output" : AutoStart-Setup-<version>.exe
;
; Ce fichier peut se trouver dans le dossier du projet ou dans son sous-dossier "installer" : la DLL est cherchée dans les deux cas.

#define MyAppName "AutoStart (plugin NINA)"
#define MyAppPublisher "fredM1968"
#define MyAppUrl "https://github.com/fredM1968/nina-autostart"
#define DllName "NINA.Plugin.AutoStart.dll"
; Emplacements possibles de la DLL (plateforme x64 ou non, ce fichier dans le projet ou dans "installer").
; C'est la DLL la PLUS RÉCENTE qui est utilisée.
#define Cand1 "bin\x64\Release\net8.0-windows\" + DllName
#define Cand2 "..\bin\x64\Release\net8.0-windows\" + DllName
#define Cand3 "bin\Release\net8.0-windows\" + DllName
#define Cand4 "..\bin\Release\net8.0-windows\" + DllName
#define Stamp1 FileExists(SourcePath + Cand1) ? GetFileDateTimeString(SourcePath + Cand1, 'yyyymmddhhnnss', '', '') : "0"
#define Stamp2 FileExists(SourcePath + Cand2) ? GetFileDateTimeString(SourcePath + Cand2, 'yyyymmddhhnnss', '', '') : "0"
#define Stamp3 FileExists(SourcePath + Cand3) ? GetFileDateTimeString(SourcePath + Cand3, 'yyyymmddhhnnss', '', '') : "0"
#define Stamp4 FileExists(SourcePath + Cand4) ? GetFileDateTimeString(SourcePath + Cand4, 'yyyymmddhhnnss', '', '') : "0"
#define DllPath Cand4
#define BestStamp Stamp4
#if Stamp3 > BestStamp
  #undef DllPath
  #undef BestStamp
  #define DllPath Cand3
  #define BestStamp Stamp3
#endif
#if Stamp2 > BestStamp
  #undef DllPath
  #undef BestStamp
  #define DllPath Cand2
  #define BestStamp Stamp2
#endif
#if Stamp1 > BestStamp
  #undef DllPath
  #undef BestStamp
  #define DllPath Cand1
  #define BestStamp Stamp1
#endif
#if BestStamp == "0"
  #error DLL introuvable : compilez d'abord le projet en Release (bin\x64\Release\net8.0-windows\NINA.Plugin.AutoStart.dll).
#endif
; La version est lue dans la DLL : augmentez AssemblyVersion / AssemblyFileVersion (AssemblyInfo.cs) avant chaque diffusion.
#define MyAppVersion GetVersionNumbersString(DllPath)
#pragma message "DLL utilisée : " + SourcePath + DllPath + "  (version " + MyAppVersion + ", date " + BestStamp + ")"

[Setup]
; Identifiant de l'installeur : ne jamais le changer d'une version à l'autre (sinon les mises à jour ne remplacent pas l'ancienne).
AppId={{9A68B240-894C-4C1E-A29C-7DB64651229D}
AppName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
; Le plugin va dans le dossier des plugins de l'utilisateur : pas besoin de droits administrateur
DefaultDirName={localappdata}\NINA\Plugins\3.0.0\AutoStart
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=AutoStart-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
; NINA charge la DLL : si NINA est ouvert, l'installeur propose de le fermer
CloseApplications=yes
RestartApplications=no
UninstallDisplayName={#MyAppName}
InfoBeforeFile=installeur-info.txt

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Files]
Source: "{#DllPath}"; DestDir: "{app}"; Flags: ignoreversion

[UninstallDelete]
; Les réglages (%LOCALAPPDATA%\NINA\AutoStart\autostart.json) sont conservés.
Type: dirifempty; Name: "{app}"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'NINA AutoStart';

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    MsgBox('Le plugin est installé.' + #13#10 +
           'Démarrez (ou redémarrez) NINA, puis Options > Plugins > AutoStart > "Ouvrir la fenêtre AutoStart".',
           mbInformation, MB_OK);
end;

// À la désinstallation, NINA ne doit plus se lancer tout seul au démarrage de Windows.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if RegValueExists(HKEY_CURRENT_USER, RunKey, RunValue) then
      RegDeleteValue(HKEY_CURRENT_USER, RunKey, RunValue);
end;
