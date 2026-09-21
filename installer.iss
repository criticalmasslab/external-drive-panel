; External Desk Panel 설치 프로그램 스크립트
; 사용법:
;   1) build_exe.bat 를 먼저 실행해서 dist\ExternalDeskPanel.exe 를 만들어두세요.
;   2) Inno Setup(무료, https://jrsoftware.org/isinfo.php) 을 설치하세요.
;   3) 이 파일(installer.iss)을 Inno Setup Compiler 로 열고 "Compile" 을 누르세요.
;   4) Output 폴더 안에 ExternalDeskPanel_Setup.exe 가 생성됩니다.
;      이 파일이 곧 "설치 프로그램"입니다. 관리자 권한 없이도 설치 가능합니다.

#define MyAppName "External Desk Panel"
#define MyAppVersion "1.0"
#define MyAppExeName "ExternalDeskPanel.exe"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\ExternalDeskPanel
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=ExternalDeskPanel_Setup
Compression=lzma
SolidCompression=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로가기 만들기"; GroupDescription: "추가 아이콘:"
Name: "startupicon"; Description: "윈도우 시작 시 자동 실행"; GroupDescription: "시작 옵션:"

[Files]
Source: "dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "설치 후 바로 실행"; Flags: nowait postinstall skipifsilent
