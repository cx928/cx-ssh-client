; ============================================================
;  程星SSH客户端 · 安装包脚本 (Inno Setup 6)
;  生成: 单文件安装向导, 含开始菜单/桌面快捷方式与卸载程序
; ============================================================
#define MyAppName "程星SSH客户端"
#define MyAppVersion "1.0.0"
#define MyAppExeName "cx-ssh-client.exe"
#define DistDir "E:\Documents\deepseek-harness\default-workspace\cx-ssh-client\dist"
#define IconFile "E:\Documents\deepseek-harness\default-workspace\cx-ssh-client\winui\Assets\app.ico"
#define OutDir "E:\Documents\deepseek-harness\default-workspace\cx-ssh-client\installer"

[Setup]
AppId={{C1F4A0E2-9B7D-4E51-9A66-7D2C4B8F1A35}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=程星SSH客户端
AppComments=SSH / SFTP / FTP / RDP / VNC 多协议远程连接客户端
DefaultDirName={localappdata}\Programs\程星SSH客户端
DefaultGroupName=程星SSH客户端
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=cx-ssh-client-Setup-{#MyAppVersion}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
AllowNoIcons=yes
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupAppTitle=程星SSH客户端 安装程序
SetupWindowTitle=程星SSH客户端 安装向导
WelcomeLabel1=欢迎使用 程星SSH客户端 安装向导
WelcomeLabel2=即将在本机安装 程星SSH客户端。%n%n会话与密码保存在本机 AppData\Local\cx-ssh-client，使用 Windows 凭据保护加密；卸载时不会删除该目录。%n%n点击"下一步"继续。
SelectDirLabel3=安装程序将把 程星SSH客户端 安装到下面的文件夹。
SelectDirBrowseLabel=点击"下一步"继续；如需更改位置，请点击"浏览"。
SelectTasksLabel2=请选择安装程序要执行的附加任务，然后点击"下一步"。
ReadyLabel1=安装程序已准备好开始安装 程星SSH客户端。
ReadyLabel2a=点击"安装"开始安装；如需检查或更改设置，请点击"上一步"。
InstallingLabel=正在安装 程星SSH客户端，请稍候…
FinishedHeadingLabel=程星SSH客户端 安装完成
FinishedLabel=安装完成，可从开始菜单或桌面快捷方式启动 程星SSH客户端。
ClickFinish=点击"完成"结束安装向导。
ButtonNext=下一步(&N) >
ButtonBack=< 上一步(&B)
ButtonInstall=安装(&I)
ButtonCancel=取消
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&R)…
ButtonYes=是(&Y)
ButtonNo=否(&N)
ConfirmUninstall=确定要卸载 程星SSH客户端 吗？%n%n会话与密码数据仍会保留在本机 AppData\Local\cx-ssh-client。
UninstallAppFullTitle=卸载 程星SSH客户端

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 程星SSH客户端"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 程星SSH客户端"; Flags: nowait postinstall skipifsilent
