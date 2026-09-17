#define MyAppName "مكتبة الجامعة - المبيعات"
#define MyAppVersion "1.0.0"
#define MyAppExeName "UniversityLibraryPOS.exe"
[Setup]
AppId={{B6379B03-257B-4ED5-894B-202609170001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\University Library POS
DefaultGroupName={#MyAppName}
OutputDir=installer-output
OutputBaseFilename=UniversityLibraryPOS-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
[Run]
Filename: "{sys}\netsh.exe"; Parameters: "http add urlacl url=http://+:5088/ user=Everyone"; Flags: runhidden; Check: IsAdmin
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""University Library POS"" dir=in action=allow protocol=TCP localport=5088"; Flags: runhidden; Check: IsAdmin
Filename: "{app}\{#MyAppExeName}"; Description: "تشغيل البرنامج"; Flags: nowait postinstall skipifsilent
