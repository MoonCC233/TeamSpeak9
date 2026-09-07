; TeamSpeak9 PC 客户端安装包脚本
;
; 需要 Inno Setup 6.3 或更高版本（用到了 x64compatible 架构标识符）。
; GitHub Actions 的 windows runner 预装 Inno Setup 6.7.x，无需额外安装。
;
; 本脚本刻意不使用 ISPP 预处理指令（#define / #if / {#...}），
; 以便在只装了 Inno Setup 基础组件的机器上也能直接编译。
; 代价是 AppVersion 只能写死，因此 CI 里有一步「校验版本号一致性」
; 强制它与 client/desktop/Directory.Build.props 的 <Version> 保持相同。
;
; 编译前必须先发布自包含产物到 client/desktop/artifacts/publish：
;
;   dotnet publish client/desktop/src/TeamSpeak9.App/TeamSpeak9.App.csproj `
;     -c Release -r win-x64 --self-contained true `
;     -o client/desktop/artifacts/publish
;
; 然后编译（简体中文语言文件见下方 [Languages] 的说明）：
;
;   & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" client\desktop\packaging\TeamSpeak9.iss
;
; 产物落在 client/desktop/artifacts/installer（该目录已被 .gitignore 忽略）。

[Setup]
; 这个 GUID 是本产品的永久标识，升级安装与卸载项都依赖它，切勿修改。
AppId={{EA0FB628-D7A0-4478-B9E1-EE787AC9B631}
AppName=TeamSpeak9
AppVersion=0.1.0
AppPublisher=TeamSpeak9 contributors
AppPublisherURL=https://github.com/MoonCC233/TeamSpeak9
AppSupportURL=https://github.com/MoonCC233/TeamSpeak9/issues
AppUpdatesURL=https://github.com/MoonCC233/TeamSpeak9/releases
VersionInfoVersion=0.1.0
VersionInfoProductName=TeamSpeak9
DefaultDirName={autopf}\TeamSpeak9
DefaultGroupName=TeamSpeak9
DisableProgramGroupPage=yes
; 默认按管理员装到 Program Files，但允许用户在向导里改成仅为自己安装
; （落到 %LOCALAPPDATA%），这样没有管理员权限也能用。
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; 产物是 win-x64 自包含发布，没有 32 位版本。
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; TeamSpeak9.App 的目标框架是 net8.0-windows10.0.19041.0，
; 屏幕共享用的 Windows.Graphics.Capture 也要求 Win10 2004 以上。
MinVersion=10.0.19041
OutputDir=..\artifacts\installer
; 版本号不进文件名：CI 会在编译后重命名成 TeamSpeak9-<版本>-win-x64-setup.exe。
OutputBaseFilename=TeamSpeak9-setup-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=TeamSpeak9
UninstallDisplayIcon={app}\TeamSpeak9.exe
; 仓库里目前没有 .ico 素材（TeamSpeak9.App.csproj 的 ApplicationIcon 也是空的），
; 所以不设置 SetupIconFile，安装程序沿用 Inno 的默认图标。
;
; 用户数据（NLog 日志、图标缓存、设置）在 %APPDATA%\TeamSpeak9 下，
; 由 AppPaths.CreateDefault() 决定，卸载时**故意保留**，不在此处清理。

[Languages]
; 简体中文是 Inno Setup 的非官方翻译，不随安装程序附带，
; 需要事先把 issrc 仓库的 Files/Languages/Unofficial/ChineseSimplified.isl
; 拷到 Inno Setup 安装目录的 Languages 子目录下（CI 里有对应步骤）。
; 排在第一位 = 默认语言，与 Directory.Build.props 的 NeutralLanguage=zh-Hans 一致。
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; 这几条自己定义而不用 Default.isl 里的 {cm:CreateDesktopIcon} 等内置项，
; 是为了不依赖各 .isl 文件的 [CustomMessages] 是否同步。
chinesesimplified.AdditionalShortcuts=附加快捷方式：
chinesesimplified.CreateDesktopShortcut=创建桌面快捷方式(&D)
chinesesimplified.LaunchAfterInstall=立即运行 TeamSpeak9(&L)
english.AdditionalShortcuts=Additional shortcuts:
english.CreateDesktopShortcut=Create a &desktop shortcut
english.LaunchAfterInstall=&Launch TeamSpeak9

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopShortcut}"; GroupDescription: "{cm:AdditionalShortcuts}"; Flags: unchecked

[Files]
; 自包含发布产物整棵目录树，含 lib\x64\libopus.dll、zh-Hans 卫星程序集，
; 以及 CI 在 publish 之后拷进去的 NOTICE.txt（OSL-3.0 的 TSLib 要求随产物分发许可声明）。
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\TeamSpeak9"; Filename: "{app}\TeamSpeak9.exe"
Name: "{autodesktop}\TeamSpeak9"; Filename: "{app}\TeamSpeak9.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TeamSpeak9.exe"; Description: "{cm:LaunchAfterInstall}"; Flags: nowait postinstall skipifsilent
