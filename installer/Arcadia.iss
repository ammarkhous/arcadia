; Arcadia installer (Inno Setup 6). Build with installer\build-installer.ps1.
#define AppName "Arcadia"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "1.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif

[Setup]
AppId={{6DBE70BB-D089-4389-9C0E-AFC51A3BFEC5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Arcadia
AppCopyright=© 2026 Arcadia
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersionNumeric}
VersionInfoDescription={#AppName} {#AppVersion} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Upgrades install over the previous version in place (same AppId); user data is never in the install folder.
; Per-user install by default (no administrator prompt); the user may choose "all users" instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Arcadia-Setup-{#AppVersion}-x64
SetupIconFile=..\src\Arcadia.App\Assets\arcadia.ico
UninstallDisplayIcon={app}\Arcadia.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.DeleteData=Also delete your Arcadia data (library database, notes, projects, settings and saved keys)?%n%nYour own files and documents are never deleted. Choose No to keep the data for a later reinstall.
ar.DeleteData=هل تريد أيضًا حذف بيانات Arcadia (قاعدة بيانات المكتبة والملاحظات والمشاريع والإعدادات والمفاتيح المحفوظة)؟%n%nلا تُحذف ملفاتك ومستنداتك الخاصة أبدًا. اختر "لا" للاحتفاظ بالبيانات لإعادة التثبيت لاحقًا.
de.DeleteData=Auch Ihre Arcadia-Daten löschen (Bibliotheksdatenbank, Notizen, Projekte, Einstellungen und gespeicherte Schlüssel)?%n%nIhre eigenen Dateien und Dokumente werden nie gelöscht. Wählen Sie Nein, um die Daten für eine spätere Neuinstallation zu behalten.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Arcadia.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Arcadia.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Arcadia.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// User data lives outside the install folder: %LocalAppData%\Arcadia\Library (database, media, cache, logs)
// and %AppData%\Arcadia (settings.json, secrets.dat). It is kept unless the user explicitly asks to delete it.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{localappdata}\Arcadia\Library'), True, True, True);
      DelTree(ExpandConstant('{userappdata}\Arcadia'), True, True, True);
    end;
end;
