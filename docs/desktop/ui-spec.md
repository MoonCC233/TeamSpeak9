# PC 端 UI 还原规格

本文是 `client/desktop` 视觉与交互的唯一规范来源。主题字典、窗口模板和后续所有视图都以此为准；
改动样式前先改本文，避免各视图各自造一套尺寸。

- 实现位置：`client/desktop/src/TeamSpeak9.App/Themes/`（资源字典）、`Controls/`（自绘窗口与图标控件）
- 样式活体样例：`Views/ThemeGalleryView.xaml`（开发用预览页，覆盖全部控件样式）
- 参考对象：TeamSpeak 6 官方客户端（闭源，仅按截图还原外观，不复制任何代码或资源）

## 1. 范围

| 还原 | 不还原 |
|---|---|
| 三列主界面、顶栏、底栏 | 联系人（Contacts） |
| 频道树、成员列表、聊天面板 | 群组（Group chats） |
| 服务器/频道/权限/书签/身份的管理对话框 | myTeamSpeak 账号登录与云同步 |
| 深色主题、自绘窗口边框、图标体系 | 官方图标位图（用自绘矢量图标替代） |

去掉的三块在 UI 上不留占位入口。左栏因此只有「服务器」与「书签」两个分组。

## 2. 窗口外框

窗口一律继承 `Controls/ShellWindow.cs`，样式由 `Themes/Window.xaml` 的 `Window.Shell` /
`Window.Dialog` 提供。

| 项 | 主窗口 | 对话框 |
|---|---|---|
| 样式键 | `Window.Shell` | `Window.Dialog` |
| `WindowStyle` | `None` | `None` |
| `WindowChrome.CaptionHeight` | 48（= `Size.CaptionHeight`） | 40 |
| `ResizeBorderThickness` | 6 | 0 |
| 外边框 | 1px `Brush.BorderStrong`，最大化时归零 | 同 |
| 背景 | `Brush.Background` | `Brush.Surface` |

要点：

- `CaptionHeight` **故意非 0**。让 Windows 继续拥有那条 48px 的标题区，拖动、双击最大化、
  右键系统菜单、Aero Snap 与 Win11 贴靠布局都免费获得，无需自己处理 `WM_NCHITTEST`。
  该区域内的可交互元素用 `WindowChrome.IsHitTestVisibleInChrome="True"` 重新参与命中测试
  （`Button.Caption` 已内建该 Setter）。
- 自绘 `Window` 模板必须手工放回 `AdornerDecorator`，否则 `ToolTip` / `Popup` / 验证装饰无处渲染。
- 最大化边界由 `Controls/WindowChromeHelper.cs` 挂 `WM_GETMINMAXINFO` 修正，保证不盖住任务栏。
- 标题栏按钮：最小化 / 最大化 / 还原 / 关闭，尺寸 46×32（Win11 规格），方角，悬停整块着色，
  关闭键悬停为 `Brush.CloseHover`。最大化与还原按 `ShellWindow.IsMaximized` 互斥显示。
- 命令绑 `System.Windows.SystemCommands.*`。注意 `SystemCommands` 在 **`System.Windows`**
  命名空间（不是 `System.Windows.Shell`），XAML 里需要
  `xmlns:sw="clr-namespace:System.Windows;assembly=PresentationFramework"`。

## 3. 设计令牌

全部定义在 `Themes/Palette.xaml` 与 `Themes/Typography.xaml`。**视图中不允许出现字面颜色值**。

### 3.1 颜色

| 用途 | 键 | 值 |
|---|---|---|
| 窗口底色 | `Color.Background` | `#16181D` |
| 面板 | `Color.Surface` | `#1E2129` |
| 抬升面板 | `Color.SurfaceRaised` | `#252932` |
| 浮层（菜单/提示/下拉） | `Color.SurfaceOverlay` | `#2C313C` |
| 凹陷（输入框） | `Color.SurfaceSunken` | `#12141A` |
| 细分隔线 | `Color.BorderSubtle` | `#2A2F3A` |
| 强分隔线 / 窗口边框 | `Color.BorderStrong` | `#3A4150` |
| 主文字 | `Color.TextPrimary` | `#E6E9EF` |
| 次文字 | `Color.TextSecondary` | `#A0A7B4` |
| 三级文字 / 分组标题 | `Color.TextTertiary` | `#6E7683` |
| 禁用文字 | `Color.TextDisabled` | `#4E5561` |
| 强调色 | `Color.Accent` | `#3B82F6` |
| 成功 / 警告 / 危险 | `Color.Success` / `Warning` / `Danger` | `#34D399` / `#FBBF24` / `#F87171` |
| 关闭键悬停 | `Color.CloseHover` | `#C42B1C`（与 Windows 一致） |

叠加洗色（画在任何底色之上）：`Brush.Hover` `#14FFFFFF`、`Brush.Pressed` `#24FFFFFF`、
`Brush.Selected` `#1F3B82F6`。

昵称着色用 `Brush.Nick0`–`Nick7` 八色，由 `Converters/NickColorConverter.cs` 对 uid 做
**FNV-1a** 取模选色。不要用 `string.GetHashCode()`——它每进程随机化，会导致同一人换色。

### 3.2 圆角与尺寸

`Radius.Small` 4 / `Radius.Medium` 8 / `Radius.Large` 10 / `Radius.Pill` 999（`CornerRadius` 类型）。

`Size.*` 是 **`sys:Double`**：`CaptionHeight` 48、`IconSmall` 14、`IconMedium` 18、`IconLarge` 22、
`RowHeight` 28、`AvatarSmall` 24、`AvatarMedium` 32。

> ⚠️ `Size.*` 只能喂给 `Double` 类型的属性（`Width` / `Height` / `FontSize` / `StrokeThickness`）。
> `RowDefinition.Height` / `ColumnDefinition.Width` 需要 `GridLength`，XAML **不做隐式转换**，
> 写上去会在运行时抛「设置属性引发了异常」。正确做法是把行列设为 `Auto`，再把 `Size.*`
> 绑到容器自身的 `Height` / `Width`。

### 3.3 字体

`Font.Ui` = Segoe UI Variable Text → Segoe UI → Microsoft YaHei UI；
`Font.Display` 用于标题；`Font.Mono` = Cascadia Mono → Consolas。

字号阶梯：`Font.Size.Caption` 11、`Small` 12、`Body` 13（默认）、`BodyLarge` 14、
`Subtitle` 16、`Title` 20。对应文本样式 `Text.Caption` / `Text.Body` / `Text.BodySecondary` /
`Text.SectionHeader` / `Text.Subtitle` / `Text.Title` / `Text.Mono`。

## 4. 控件样式清单

命名规则 `控件族.变体`。未列出的控件靠隐式默认样式兜底，永远不会露出系统灰。

| 族 | 键 | 用途 |
|---|---|---|
| 按钮 | `Button.Primary` | 主操作（连接、创建、发送） |
| | `Button.Secondary` | 默认（`Button` 的隐式样式） |
| | `Button.Ghost` | 无底色，侧栏行与工具栏 |
| | `Button.Danger` | 删除、断开 |
| | `Button.Icon` / `Button.IconSmall` | 32×32 / 26×26 纯图标 |
| | `Button.Caption` / `Button.CaptionClose` | 标题栏 46×32 |
| 开关 | `Toggle.Pill` | 顶栏麦克风/扬声器/AFK，**选中 = 静音**（危险色） |
| | `Toggle.Tab` | 右栏 信息/聊天/文件 分段切换 |
| | `Toggle.Switch` | 设置页 38×20 滑动开关 |
| 输入 | `TextBox.Base` / `.Search` / `.Multiline` | `Tag` 即 placeholder（空且未聚焦时显示） |
| 列表 | `ListItem.Base` | 选中时左侧 3px 强调条 |
| | `ListItem.Message` | 聊天消息行，无选中态 |
| 树 | `TreeView.ExpandToggle` | 折叠箭头，选中旋转 90° |
| 容器 | `Card` / `Card.Raised` | 面板与横幅 |
| | `Badge` | 未读计数胶囊 |
| 图标 | `Icon.Small` / `Icon.Large` / `Icon.Filled` | `IconGlyph` 的尺寸变体 |
| 分隔 | `Separator.Base` | 同时注册到 `MenuItem.SeparatorStyleKey` |

隐式默认样式已覆盖：`TextBlock` `Button` `RepeatButton` `ToggleButton` `TextBox` `PasswordBox`
`CheckBox` `RadioButton` `Slider` `ProgressBar` `ListBox` `ListBoxItem` `TreeView` `TreeViewItem`
`GridSplitter` `ScrollBar` `ContextMenu` `MenuItem` `Separator` `ToolTip` `ComboBox` `ComboBoxItem`
`IconGlyph`。

两处刻意的例外：

- **不给 `ScrollViewer` 写隐式样式**。`HorizontalScrollBarVisibility` / `CanContentScroll` /
  `PanningMode` 是可继承的附加属性，样式 Setter 的优先级高于继承，会压掉宿主 `ListBox` /
  `TextBox` 自己声明的滚动策略。
- 菜单内的 `Separator` 只认 `{x:Static wpfControls:MenuItem.SeparatorStyleKey}`，不吃隐式样式，
  必须单独注册一份。

## 5. 图标

`Themes/Icons.xaml` 提供 63 个 `PathGeometry`，统一画在 24×24 网格上、以描边（stroke）表达，
因此一份几何图形能服务所有尺寸。渲染控件是 `Controls/IconGlyph.cs`，模板 `Stretch="Uniform"`，
`StrokeThickness` 以几何自身单位计，缩放后线重视觉一致。

分组：窗口按钮 4、顶栏 7、导航 10、侧栏 8、频道与成员 6、聊天 9、Markdown 工具栏 10、流媒体 6、其它 3。

新增图标的要求：24×24 边界、线条端点用 `Figures` 语法、只描边不填充（填充图标走
`Icon.Filled`）、键名 `Icon.<PascalCase>`。

## 6. 布局

### 6.1 整体

```
┌───────────────────────────────────────────────────────────────────┐
│ 顶栏 48px：铃铛 · teamspeak 字标 │ AFK · 麦克风 · 扬声器 │ ─ □ ✕ │
├──────────────┬──────────────────┬─────────────────────────────────┤
│ 左栏 280     │ 中栏 320         │ 右栏 *                          │
│ 搜索/连接    │ 服务器横幅卡片   │ 频道标题 + 信息/聊天/文件切换   │
│ ── 服务器 ── │ 频道树           │ 消息列表                        │
│ ── 书签 ──   │ （成员内嵌）     │                                 │
│              │                  │ ─────────────────────────────── │
│ 自己 + 设置  │ 创建频道/开始直播│ Markdown 工具栏 + 输入框 + 发送 │
└──────────────┴──────────────────┴─────────────────────────────────┘
```

列宽用 `GridSplitter` 可调：左栏 280（220–420）、中栏 320（260–520）、右栏 `*`（最小 360）。
窗口最小尺寸 960×600。

### 6.2 顶栏（48px）

- 左：`Icon.Bell` 通知按钮（有未读时叠 `Badge`）+ `teamspeak` 字标（`Text.Subtitle`）。
- 中：AFK 状态胶囊（`Toggle.Pill` + `Icon.Clock`）、麦克风、扬声器。后两者是「开关 + 下拉箭头」
  的组合按钮：主体切换静音，箭头弹出设备菜单。**选中态 = 已静音**，用危险色，与官方一致。
- 右：标题栏按钮组，紧贴右上角（`Margin="0,8,8,0"`）。

### 6.3 左栏

1. 搜索/连接输入框（`TextBox.Search`，左内嵌 `Icon.Search`，回车直连地址）。
2. 分组标题「服务器」（`Text.SectionHeader`）+ 已连接服务器列表：图标、名称、右侧断开按钮。
3. 「创建 TeamSpeak 社区」按钮（`Button.Secondary` + `Icon.Globe`）。
4. 分组标题「书签」+ 四个图标按钮（搜索 / 新建文件夹 / 新增 / 全部折叠），下方书签树。
5. 底部固定条：自己的头像（`Size.AvatarMedium`）、昵称、位置图标、设置齿轮。

### 6.4 中栏

- 顶部服务器横幅：`Card`，含服务器图标、名称、书签与聊天图标按钮。
- 频道树：`TreeView`，子频道缩进 14px；行内元素依次为折叠箭头、频道图标、名称、
  右侧人数/状态徽标。TS3 spacer 频道按其命名约定渲染为分隔线或居中/重复文本。
  当前所在频道整行用 `Brush.Selected` + 左侧 3px 强调条。
- 频道内成员：作为该频道节点的子项，头像 `Size.AvatarSmall`、昵称、麦克风/静音/国旗徽标。
- 底部：「创建频道」「开始直播」两个按钮，右下角布局切换与设备图标。

### 6.5 右栏

- 标题栏：频道图标 + 名称 + `Toggle.Tab` 三选一（信息 / 聊天 / 文件）。
- 三个页签的内容叠在同一网格行，各自用 `DataTrigger Binding="{Binding ActiveTab}"` 切
  `Visibility`；底部输入区只在「聊天」页签出现。

**聊天页签**

- 消息列表：`ListBox` + `ListItem.Message`。头像、着色昵称、时间戳、气泡；同一人 5 分钟内的
  连续消息合并（省略头像与昵称，仅悬停时显示时间）。消息正文按 Markdown 渲染（与官方 TS6 一致，
  不是 TS3 的 BBCode）：解析在 `Core/Model/Markdown.cs`，渲染在 `Controls/MarkdownPresenter.cs`。
- 底部：Markdown 工具栏（粗体 `**`、斜体 `*`、下划线 `__`、删除线 `~~`、链接、引用 `> `、
  列表 `- `、代码 `` ` ``、剧透 `||`、标题 `# `、更多）
  + `TextBox.Multiline` 输入框（40–160px 自增）+ 表情、附件、发送。

**信息页签**

竖向 `ScrollViewer` + 三张 `Card`，每张卡片标题为 `Icon.Small` + `Text.SectionHeader`。
键值行统一为 110px 标签列（`Text.BodySecondary`）+ 自适应值列（`Text.Body`，允许换行），
由 `InfoRowTemplate` 提供；数据来自 `ShellViewModel.BuildServerRows` / `BuildChannelRows`
两个纯函数（`internal`，便于单测）。**服务器没给的字段整行省略**，不显示空值。

| 卡片 | 图标 | 字段 |
|---|---|---|
| 服务器 | `Icon.Server` | 名称、语音提示名、地址、在线人数（`n / max`，无上限时只显示 n）、版本、平台、协议版本、许可类型、语音加密、创建时间 |
| 欢迎消息 | `Icon.Info` | `MarkdownPresenter` 渲染 `virtualserver_welcomemessage` |
| 当前频道 | `Icon.Channel` | 名称、主题、语音提示名、类型、人数（`n / 上限`）、编解码器、编码质量、所需发言权限（>0 才显示）、状态、频道描述 |

- 「状态」把默认频道 / 需要密码 / 强制静音 / 语音未加密四个布尔标记用「、」合并成一行，
  避免四行近乎空白的内容。
- 创建时间按本地时区显示；服务器未提供时（`default` 或 unix epoch）整行省略。
- 频道描述不在键值表里：它需要单独的 `channelinfo` 往返（`channellist` 不带该字段），
  并按 Markdown 渲染。切到信息页签或换频道时异步拉取一次，用「正在请求」标志防重入、
  「已取得的频道 id」做判重；请求期间换了频道则在请求结束后自动补拉。
- 未连接时只显示一句「连接服务器后显示信息」。

**文件页签**

浏览当前频道的文件区，三行结构：工具行（`Auto`）+ 列表（`*`）+ 未连接提示（`Auto`）。
数据层是 `Core/Management/FileService.cs`（`ftgetfilelist` / `ftcreatedir` / `ftrenamefile` /
`ftdeletefile` + 30033 端口的上传下载），列表行由 `ShellViewModel.BuildFileRows` 这个纯函数生成。

- 工具行：`Icon.ChevronLeft` 上一层（已在根目录时禁用）、`Icon.Refresh` 刷新、
  `Icon.FolderPlus` 新建文件夹、`Icon.Upload` 上传、`Icon.Download` 下载（仅选中文件时可用）、
  `Icon.Edit` 重命名、`Icon.Trash` 删除（仅有选中项时可用），均为 `Button.IconSmall`；
  中间是当前路径（`Text.BodySecondary`，根目录显示 `/`）。
- 列表行四列：图标（目录 `Icon.Folder` / 文件 `Icon.File`，由 `DataTrigger Binding="{Binding IsFile}"`
  切换）+ 名称 + 大小（右对齐，目录显示「文件夹」而非 `0 B`）+ 修改时间（右对齐，
  服务器未给时留空）。双击目录进入下一层；双击命中行靠 `ItemsControl.ContainerFromElement`
  定位，点在列表空白处不响应。
- 三种状态文案都来自 `ShellViewModel.FilesStatus`，叠在列表区上：加载中「正在加载…」、
  空目录「这个文件夹是空的。」、失败则显示 `CommandErrorText` 翻译后的服务器错误
  （拿不到具体错误时回落成「无法读取文件列表。」）。传输过程中同一处显示
  「正在下载 …」「正在上传…」。空目录的两个错误码
  （`database_empty_result` / `file_no_files_available`）在 `FileService` 里被折叠成空列表，
  不当成失败。
- 新建文件夹与重命名共用 `Views/TextPromptWindow.xaml` 这个轻量输入对话框：它没有 ViewModel、
  不进 DI 容器，由调用方直接 `new` 并传入标题、提示、按钮文案与校验委托
  （`FileService.ValidateName`）；重命名时预选扩展名之前的部分。
- 上传走 `OpenFileDialog`，下载走 `SaveFileDialog`，建议文件名先过
  `FileService.SanitizeLocalName`——名字来自服务器，必须剥掉分隔符与非法字符。
- 删除前用 `MessageBox` 二次确认，文件与文件夹两套文案。
- 传输与命令失败的详情写进聊天日志（`AppendSystemMessage`），避免用一行状态文字盖住整个列表。
- 切到本页签或换频道时懒加载一次根目录，靠「正在加载」标志防重入、「已列出的频道 id」判重；
  请求期间换了频道则丢弃回包并在结束后补拉。
- 未连接时列表为空，底部显示「连接服务器后可浏览频道文件」。
- 消息与文件列表都不做本地持久化，重启后重新从服务器拉取。

## 7. 功能面对照

| 功能 | 入口 | 协议依据 |
|---|---|---|
| 修改服务器信息 | 服务器横幅右键 → 编辑 | `serveredit`，见 [兼容性报告](tslib-ts6-compat.md) §5 |
| 创建 / 编辑 / 删除频道 | 频道树右键、底部「创建频道」 | `channelcreate` / `channeledit` / `channeldelete` |
| 频道图标增删改 | 频道编辑对话框 → 图标 | 文件传输 `/icon_<crc32>` + `channeladdperm i_icon_id` |
| 权限编辑 | 服务器右键 → 权限 | `*groupaddperm` / `*groupdelperm` |
| 书签管理 | 左栏书签分组 | 本地设置，不涉及服务端 |
| 身份管理 | 设置 → 身份 | 本地 identity 存储 |
| 频道文件浏览与传输 | 右栏「文件」页签 | `ftgetfilelist` / `ftcreatedir` / `ftrenamefile` / `ftdeletefile` + 30033 端口传输，见 [兼容性报告](tslib-ts6-compat.md) §4.7 |
| 屏幕共享 | 中栏「开始直播」 | [TSSP v1](../protocol/tssp-v1.md) |

图标写入路径有硬性限制（服务器属性只吃无符号 id、频道图标**只能**走权限而非
`channeledit`、客户端图标无有效路径），细节见兼容性报告 §4。

## 8. 样式回归验证

`Views/ThemeGalleryView.xaml` 是所有控件样式的活体样例：7 种按钮、8 种开关与勾选、
两列输入、频道树、带右键菜单的列表、55 个图标、8 个色块、7 级字号。改动主题字典后：

1. `dotnet build src\TeamSpeak9.App\TeamSpeak9.App.csproj` —— 期望 0 错误
   （`libopus.dll` 缺失的警告是预期的）。
2. 启动应用并截图，与上一次截图逐块比对。
3. 检查弹出层：`ContextMenu`、`ComboBox` 下拉、`ToolTip` 必须都能弹出且用深色浮层配色
   —— 这三项同时验证了自绘窗口模板里的 `AdornerDecorator` 补对了。
4. 最大化后窗口矩形应等于显示器工作区（`rcWork`）而非整屏，且外边框归零。

> WPF 启动阶段的未处理异常不会写控制台，而是表现为「进程活着 + 一个类名 `#32770` 的对话框」。
> 排查时枚举该进程的可见窗口，读 `#32770` 的子 `Static` 控件文本，即可拿到含行列号的完整异常消息。

图标按钮没有文字内容，UI Automation 无法自动推导名称，因此 `Button.Icon` 与 `Button.Caption`
把 `ToolTip` 同时绑给 `AutomationProperties.Name`。新增纯图标按钮时**必须写 `ToolTip`**，
否则该按钮在无障碍树与自动化测试里没有名字。
