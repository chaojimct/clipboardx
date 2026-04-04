# ClipboardX

**[下载最新版 (Releases)](https://github.com/chaojimct/clipboardx/releases)** · [官网](https://chaojimct.github.io/clipboardx/) · [源码](https://github.com/chaojimct/clipboardx) · [功能介绍与同类对比](docs/ClipboardX-Introduction.md)

轻量级 Windows **剪贴板历史 + 文件对话框跳转**二合一工具：剪贴板弹窗**不抢焦点**（`WS_EX_NOACTIVATE`），并支持在「打开 / 保存」等窗口中一键跳转到资源管理器或常用目录（默认 **Ctrl+G**）。

## 演示

<p align="center">
  <b>剪贴板历史</b>（不抢焦点 · 实时 / 拼音搜索）<br/>
  <img src="static/clipx.gif" alt="剪贴板弹窗演示" width="720"/>
</p>

<p align="center">
  <b>文件对话框跳转</b>（热键与延时在设置中可调）<br/>
  <img src="static/jumpx.gif" alt="文件对话框跳转演示" width="720"/>
</p>

## 下载与安装

1. 打开 **[Releases](https://github.com/chaojimct/clipboardx/releases)**，在 Assets 中选安装包或 zip（版本号以发布页为准）：
   - **`ClipboardX-*-setup.exe`** — **Inno（完整版·框架依赖）**：比自包含安装包小；须已安装或先安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（x64）。未检测到时安装程序会提示并可**打开官方下载页**，装好后再运行 setup。
   - **`ClipboardX-*-setup-self-contained.exe`** — **Inno（完整版·自包含）**：体积较大，**无需**单独装 .NET，与自包含 zip 等价但带安装向导、开始菜单与卸载项。
   - **`ClipboardX-*-win-x64-no-runtime.zip`** — 解压即用，需已安装上述桌面运行时
   - **`ClipboardX-*-win-x64-self-contained.zip`** — 自带运行时，无需单独装 .NET
2. 若用 zip：解压后运行 **`ClipboardX.exe`**。从临时目录启动时，程序会复制到 `%LocalAppData%\Programs\ClipboardX` 并可在「应用和功能」中卸载；托盘 **关于** 可查看版本与主页。
3. **检查更新**：托盘右键 → **检查更新…**，从 GitHub Releases 按**当前程序名（完整版 / 剪裁版）**匹配对应 zip，并按本机是否使用共享运行时优选 **no-runtime** 或 **self-contained**；关闭程序后覆盖并重启。需可访问 GitHub（含 `api.github.com`）。**设置 → 剪贴板 → 启动时检查更新**（默认开）会在启动约 45 秒后静默查询；若有新版仅**托盘气泡**提示，同一发行版只提示一次，仍须手动「检查更新…」下载安装。

### SmartScreen「Windows 已保护你的电脑」

自解压/互联网下载的 exe 若未做 **Authenticode 签名**，SmartScreen 可能拦截，属信誉策略而非「报毒」。

**若你相信本仓库发布包：** 点 **更多信息** → **仍要运行**；或对 exe **右键 → 属性 → 解除锁定** 后再开。对公网分发减少提示需购买受信证书并持续积累信誉；个人使用按上法即可。

## 特性（剪贴板）

- **不抢焦点** — 原窗口保持输入焦点，适合 IDE / 聊天等场景  
- **实时搜索** — 弹出后直接输入过滤；热键走低级键盘钩，不必先点搜索框  
- **拼音检索** — 全拼 / 首字母匹配中文历史（依赖 NPinyin）  
- **多格式** — 文本、图片、文件路径列表；图片可显示缩略图  
- **类型筛选** — 面板顶栏在「全部 / 文本 / 图片 / 文件」间切换  
- **快捷短语** — 列表项 **右键 → 设为快捷短语**，绑定关键词后固定参与列表；可切到 **⚡ 短语** 只显示短语项（仍支持搜索）  
- **图片** — 剪贴板图片在列表中 **右键**，可将图片 **粘贴为文件** 到当前资源管理器窗口（适用时）  
- **数字粘贴** — **面板主键**（默认 Ctrl，可改为 Alt / Win / CapsLock）+ **1～9** 粘贴对应可见条目（与设置中「面板主键」一致）  
- **全局热键** — 默认 **Ctrl+`**（反引号）呼出面板，**设置**中可改；勿与 **文件对话框跳转热键**重复  
- **其它操作** — ↑↓ 选择、Enter 粘贴、Esc、Backspace 删筛选字符、←→ 翻页；可拖动标题栏微调位置（不抢焦点策略下仍尽量跟光标/鼠标）  
- **去重** — 重复内容复制后提升到顶部  
- **预览** — **设置 → 预览行数** 控制每条历史的展示行数  
- **透明度 / 主题** — 弹窗透明度滑块；主题：跟随系统 / 亮 / 暗（Catppuccin Mocha）  
- **持久化** — 见下文「数据与日志」；首次运行若发现旧版 **ClipboardManager** 配置会自动迁移  

## 设置与托盘

### 设置（选项卡）

| 选项卡 | 内容 |
|--------|------|
| **剪贴板** | 最大记录数；**呼出快捷键**；外观**主题**、**弹出位置**（光标旁 / 鼠标旁）、**透明度**；**预览行数**；**面板主键**；**开机自启动**；**启动时检查更新**；**点击外部隐藏**剪贴板面板；**清空所有历史**（快捷短语仍保留在 `settings.json`） |
| **文件夹跳转** | **文件对话框跳转键**；跳转列表弹出**延时**（0～10000 ms；延时内再按一次跳转键会直接跳当前预选项）；**跳转列表跟随**；**多候选时弹出列表**；**对话框到前台自动执行**；**Shell 注入跳转**；**点击后自动跳转**；**切回时自动同步路径**（从资源管理器等切回已打开对话框时刷新候选并按设置自动跳转）；**当前文件夹内键入筛选**（系统资源管理器 + Everything，见下文） |
| **自定义文件对话框** | 针对内置识别为「无」的窗口：规则列表、删除、**运行探测向导**、**导入（合并 / 替换）**、**导出**；底部显示规则文件路径 |

首次关闭设置时若点 **保存**，上述常规项写入 **`settings.json`**；自定义规则在导入/删除/向导成功时已写入 **`custom_file_dialogs.json`**，与是否点「保存」无关。

### 托盘

右键菜单：**显示**（含当前呼出热键提示）、**设置**、**关于**、**检查更新…**、**添加自定义文件对话框…**（与设置中探测向导相同流程）、**卸载…**、**退出**。**双击托盘图标**等同于打开剪贴板面板。（以 **`dotnet run` / Debug** 运行时，托盘会多一项 **采集窗口信息**，用于开发调试。）

## 文件对话框跳转

在以下窗口**处于前台**时，按 **文件对话框跳转键**（默认 **Ctrl+G**，**设置**里可改）使用：

| 类型 | 说明 |
|------|------|
| 系统公共对话框 | Win32 **`#32770`** 类「打开 / 保存」等 |
| WPS 等 | 套件自带「打开文件」「另存为」等 **非** 标准公共对话框（文字 / 表格 / 演示等） |

- **WPS**：用界面自动化、面包屑、**ReBar + F4** 地址栏等策略（思路参考 [XiaoYao_QuickJump](https://github.com/lch319/XiaoYao_QuickJump)），**不**做 Shell DLL 注入。  
- **系统公共对话框**：可注入宿主走 `IShellBrowser::BrowseObject`；失败则回退地址栏模拟。可在 **设置** 中关闭 **「Shell 注入跳转」**（默认开启），遇杀软拦截时只用模拟方式，兼容更好。

### 资源管理器内 Everything 筛选

**完整版 / FileJumpOnly**，且设置里已开启「当前文件夹内键入筛选」：需本机 **Everything（64 位）** 已运行；本功能通过 voidtools 提供的 **Everything64.dll**（[SDK](https://www.voidtools.com/support/everything/sdk/)）做进程间查询。仓库 **`native/Everything64.dll`** 为随源码分发的 SDK 文件（与安装版目录里的一般为同一套）；**构建时会复制到 `ClipboardX.exe` 同级**，运行时优先从该处加载。若自行编译时未放置该文件，也可从官方 SDK 或 Everything 安装目录拷贝 **`Everything64.dll`** 到 exe 旁，或将含该 DLL 的目录加入 **PATH**。在资源管理器中聚焦文件区（非地址栏/搜索框）直接键入即可；若 **剪贴板浮层仍显示** 但已用 **Alt+Tab** 或任务栏切到资源管理器，也会正常触发（剪贴板钩在前台为资源管理器时会放行键盘链）。

**排障**：每次启动会在数据目录写入 **`explorer_quickfind.log`**（与 `settings.json` 同目录：`%LocalAppData%\ClipboardX\` 或便携模式下的 `Data\`），首行可区分「键盘钩已安装」与安装失败及 Win32 错误码。另可设环境变量 **`CLIPBOARDX_DEBUG_EXPLORER_QF=1`**（或 **Debug** 配置），用 [Sysinternals DebugView](https://learn.microsoft.com/sysinternals/downloads/debugview) 查看 **`[ExplorerQF]`** 的详细原因（如 COM 取路径失败、焦点类名等）。

### 自定义文件对话框

若某窗口**未被内置识别**为文件对话框，可在 **设置 → 自定义文件对话框** 中维护规则（按**窗口类名 + 进程名**等匹配，可选 **标题包含**）。JSON 与 **`%AppData%\ClipboardX\custom_file_dialogs.json`** 一致。**运行探测向导**（或托盘 **添加自定义文件对话框…**）会按内置顺序依次尝试多种策略（如 `shell_inject`、`sys_listview`、`address_bar`、WPS 综合链、`qt_alt_n`、`alt_d_value_enter`、`ctrl_l_type_enter` 等），用宽松 UI 自动化读取当前路径做校验；命中后会把**优先策略**记入规则。**探测前**请保证对话框当前**不在**用于校验的目标文件夹内，并准备好有效目标路径（如 **上次在对话框里记录的路径** 或剪贴板中的目录路径）。

向导与 **导入/导出** 的说明亦见 **设置** 选项卡内文案。

### 行为概要

| 情况 | 表现 |
|------|------|
| 没有可用路径 | **静默**（无提示） |
| 仅 1 条候选 | **直接跳转**，不弹列表 |
| 多条候选、延时大于 0 ms | 先延时再弹列表；**延时内再按一次同一快捷键** → 直接跳当前预选项 |
| 多条候选、延时为 0 | **立即**弹列表 |
| **点击后自动跳转**（设置默认开） | 对话框成为前台后，**第一次**在框内点左键即按列表**首条**路径跳转；**同一对话框窗口存活期内只自动跳一次**（关掉再开才再来），手动 **Ctrl+G** 不受影响 |
| **对话框到前台自动执行** | 开启后，检测到「打开 / 保存」对话框成为前台时，无需按快捷键即自动采集路径并弹出跳转列表（多候选）或直跳（单候选）。跳转列表紧贴对话框并随窗口移动，同一对话框顶层窗口只自动处理一次。可在设置中关闭 |
| **切回时自动同步路径**（设置项，默认可关） | 从外部文件管理器切回**已打开**的文件对话框时刷新候选列表；开启后若最近一次外部文件夹与对话框当前目录不同则自动跳转（与「到前台自动执行」分工：后者偏首次到前台，本项偏反复切回） |

**路径来源（摘录）：** 资源管理器；**Total Commander / XYplorer / Directory Opus**（与 [QuickSwitch](https://github.com/gepruts/QuickSwitch) 同类专用通道）；以及 FreeCommander、Double Commander、Q-Dir、OneCommander 等**白名单进程**上的**浅层 UI 自动化**（无官方 API 时尽力而为，多栏/四格可能只取扫描到的一条路径）；另含**记忆的上次路径**、列表**收藏**。无任何路径则本次按键无效——可先在外部管理器进到目标目录再试。

### 跳转列表里的操作

与主剪贴板面板类似：**↑↓** 选择，**←→** 翻页，**单击行** 或 **Enter** 确认跳转，**Esc** 清搜索 / 关闭，**主键+1～9** 快速跳可见项，输入字符过滤（含收藏别名）。**右键** 可将路径 **加入收藏**（可设关键词）或管理已收藏项，数据在 **`settings.json`**。**点击外部**是否关列表与设置 **「点击外部隐藏」** 一致。

## 与其它工具对比

以下为与常见同类软件的**差异对照**，便于选型；各工具随版本迭代，具体以官方说明为准。

### 剪贴板历史（Ditto、CopyQ）

| 特性 | ClipboardX | Ditto | CopyQ |
|------|------------|-------|-------|
| 弹窗是否抢焦点 | **不抢**（`WS_EX_NOACTIVATE`） | 抢焦点 | 抢焦点 |
| 写代码 / 聊天时 | 光标可留在原窗口 | 焦点常切到工具窗 | 焦点常切到工具窗 |
| 跟**文本光标** | 支持（`GetGUIThreadInfo` 等多级回退） | 一般不支持 | 一般不支持 |
| 跟**鼠标** | 支持（可配置） | 支持 | 部分支持 |

| 搜索 | ClipboardX | Ditto | CopyQ |
|------|------------|-------|-------|
| 弹出后直接打字过滤 | 支持（低级键盘钩） | 常需先点进搜索框 | 常需先聚焦搜索框 |
| **拼音**（全拼 / 首字母） | 支持 | 无内置 | 无内置 |

| 存储（可配置，默认值供参考） | ClipboardX | Ditto | CopyQ |
|------|------------|-------|-------|
| 引擎 | SQLite（WAL） | SQLite 等 | 自有格式 / SQLite |
| 默认条数规模 | 2000 | 约 500 级 | 约 200 级 |
| 去重置顶 | 支持 | 可配置 | 视版本而定 |

| 界面 | ClipboardX | Ditto | CopyQ |
|------|------------|-------|-------|
| 主题 | 跟随系统 / 亮 / 暗（Catppuccin Mocha） | 传统界面为主 | 可定制，成本较高 |
| 体量 | .NET 8，少量 NuGet | 体量因版而异 | 依赖 Qt 等，常驻偏高 |

### 文件对话框跳转（Listary、QuickSwitch、逍遥 QuickJump）

| 特性 | ClipboardX | Listary | QuickSwitch | [逍遥 QuickJump](https://github.com/lch319/XiaoYao_QuickJump) |
|------|--------------|---------|-------------|----------------|
| 授权 | **开源** | 闭源（有 Pro） | 开源 | 开源 |
| 产品重心 | **剪贴板 + 跳转** | 全局搜索 + 跳转等 | 服务 **TC / XY / DO** 等 | **WPS** 等增强 |
| 文件管理器覆盖 | 专用协议 + **白名单 UIA**（十余种量级） | 多种 | **以 TC/XY/DO 为主** | 相对窄 |
| **WPS** 非 `#32770` 框 | **专门多策略** | 部分场景 | 基本不涉及 | **重点适配** |
| **Shell 注入**（公共对话框） | **支持**（`IShellBrowser`，可关） | 有类似深度能力 | **无** | **无** |
| **首次点击自动跳** | **支持**（可关） | 有类似能力 | **无** | **无** |
| **对话框到前台自动执行** | **支持**（自动弹列表/直跳，可关） | 有类似能力 | **无** | **无** |

**路径采集分层（摘录）：** 第一档为资源管理器 COM、Total Commander 消息、XYplorer `WM_COPYDATA`、Directory Opus CLI 等；第二档为 FreeCommander、Double Commander、Q-Dir、OneCommander 等在**进程白名单**下的浅层 UI Automation。**ClipboardX** 与 QuickSwitch / 逍遥在覆盖面上侧重点不同：前者在「广谱管理器 + WPS + 二合一」上更均衡；QuickSwitch 在 TC/XY/DO 上极专；逍遥在 **WPS 地址栏** 等路线上与 ClipboardX 有思路交集（如 ReBar + F4）。

### 「二合一」在常驻上的差异

| 若分开装 | ClipboardX |
|----------|------------|
| 剪贴板（Ditto / CopyQ…）+ 跳转（Listary / 专精工具…）各一托盘、各一套习惯 | **单进程**：共享主题、设置、热键栈与 UI 基底，托盘只占一格 |

### 小结

| 维度 | ClipboardX |
|------|------------|
| 剪贴板弹窗 | **不抢焦点**、可跟光标、**拼音**检索，路径与 Ditto/CopyQ 不同 |
| 粘贴方式 | 使用 **Shift+Insert** 系统级粘贴，兼容性优于 Ctrl+V（不被应用层快捷键映射拦截） |
| 公共「打开/保存」 | 可选 **Shell 深度跳转**，亦可全模拟 |
| **WPS** | **多策略回退**（自动化、ComboBox、ReBar+F4、快捷键等） |
| 管理器路径 | **协议 + UIA 白名单**，覆盖面广于「只做两三款管理器」的专用工具 |
| 开源 | 代码可审（许可以仓库声明为准） |

## 数据与日志

默认数据根目录为 **`%LocalAppData%\ClipboardX\`**（与 `AppPaths` 一致）。**便携模式**：在 exe 同目录放置空文件 **`ClipboardX.portable`**，则配置与数据库落在 **`exe\Data\`** 下。

| 位置 | 说明 |
|------|------|
| `%LocalAppData%\ClipboardX\settings.json` | 热键、主题、自启动、**启动时检查更新**、最大条数、快捷短语、**文件夹收藏**、文件跳转相关开关等（含 `LastStartupUpdateNotifiedTag`，用于同一版本只气泡一次） |
| `%LocalAppData%\ClipboardX\custom_file_dialogs.json` | **自定义文件对话框**规则（与设置中导入/导出格式一致） |
| `%LocalAppData%\ClipboardX\clipboard_history.db` | SQLite：**剪贴板历史**正文（WAL 模式） |
| `%LocalAppData%\ClipboardX\shell_navigate.log` | **Shell 注入**与相关跳转诊断（UTF-8） |

**迁移**：若曾使用 **`%AppData%\ClipboardX`** 或旧名 **`%AppData%\ClipboardManager`** 的配置，首次启动会在目标文件不存在时尝试复制到当前数据根（详见 `AppPaths.MigrateLegacyPaths`）。

**多 Flavor**：仅剪贴板 / 仅文件跳转版本的目录名为 `ClipboardX-clipboard`、`ClipboardX-filejump`（同样在 LocalAppData 下，或为便携 `Data\`）。

## 从源码运行

```bash
dotnet run
```

Debug 带控制台；正式发布用 **Release**。工程文件 **`ClipboardManager.csproj`**，输出名 **ClipboardX**，根命名空间仍为 **ClipboardManager**。

### 源码目录（简要）

| 目录 | 内容 |
|------|------|
| `Views/` | 主弹窗、设置、跳转选择器、`SharedPopupStyles.xaml` |
| `Models/` | `AppSettings`、自定义对话框规则、剪贴板项、收藏等 |
| `Services/` | 主题、更新、剪贴板门禁、自定义规则存储、自启动、`AppInfo` |
| `FileJump/` | 对话框分类、路径收集、Shell 跳转与日志 |
| `Interop/` | Win32 P/Invoke |
| `Install/` | 按用户安装 / 卸载 |
| `Search/` | 拼音检索 |
| `Media/` | 托盘 SVG 栅格化 |
| `static/` | 演示动图 |
| 根目录 | `App.xaml`、`ClipboardManager.csproj`（版本见 csproj） |

## 自行编译发布

需 **.NET 8 SDK**。与 CI 类似示例：

```bash
# 可选：先编 Shell 导航原生 DLL（跳过则深度跳转能力受限）
powershell -ExecutionPolicy Bypass -File native/ShellNavigate/build.ps1

# 框架依赖 + 单文件（本机需 .NET 8）
dotnet publish ClipboardManager.csproj -c Release -r win-x64 \
  -p:SelfContained=false -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o ./out/fdd

# 自带运行时 + 单文件
dotnet publish ClipboardManager.csproj -c Release -r win-x64 \
  -p:SelfContained=true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o ./out/sc
```

### Windows：完整版 + Inno 安装包（与 CI / `release.yml` 一致）

需 **Inno Setup 6**（例如 `winget install JRSoftware.InnoSetup`）。若装到当前用户目录，编译器常见路径为 **`%LocalAppData%\Programs\Inno Setup 6\ISCC.exe`**；否则会落在 **`Program Files (x86)\Inno Setup 6\ISCC.exe`**。版本号 **`$v`** 请与 **`ClipboardManager.csproj`** 里 `<Version>` 一致。

```powershell
# 在仓库根目录执行
$v = "1.2.6"   # 与 csproj 同步后改这里
Set-ExecutionPolicy -Scope Process -Bypass -Force
.\native\ShellNavigate\build.ps1

dotnet publish ClipboardManager.csproj -c Release -r win-x64 `
  -p:ClipboardXProduct=Full -p:SelfContained=false -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
  -p:DebugType=None -p:Version=$v -o publish/fdd

dotnet publish ClipboardManager.csproj -c Release -r win-x64 `
  -p:ClipboardXProduct=Full -p:SelfContained=true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:Version=$v -o publish/sc

$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
& $iscc /DAppVersion=$v /DPublishDir=..\publish\fdd installer\clipboardx.iss
& $iscc /DAppVersion=$v /DPublishDir=..\publish\sc /DSETUP_SKIP_DOTNET /DSetupOutputSuffix=-setup-self-contained installer\clipboardx.iss
```

产物：**`installer\Output\ClipboardX-{v}-setup.exe`**（框架依赖，含 .NET 检测）与 **`ClipboardX-{v}-setup-self-contained.exe`**。可选：`Compress-Archive publish\fdd\* ClipboardX-$v-win-x64-no-runtime.zip`，以及对 `publish\sc` 做 **self-contained** zip，命名与 Releases 附件一致。

**多 Flavor（与 CI 一致，可选）：** 默认 `ClipboardXProduct=Full`；仅剪贴板或仅文件跳转时需显式传入，输出 exe 名与 GitHub 上 zip 前缀才会一致（「检查更新」靠此前缀匹配附件）。

```bash
# 仅剪贴板
dotnet publish ClipboardManager.csproj -c Release -r win-x64 \
  -p:ClipboardXProduct=ClipboardOnly \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true -o ./out/clip

# 仅文件对话框跳转（建议先编 ShellNavigate DLL）
dotnet publish ClipboardManager.csproj -c Release -r win-x64 \
  -p:ClipboardXProduct=FileJumpOnly \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true -o ./out/jump
```

单文件会把内嵌 DLL 解压到临时目录；运行时通过 `AppContext.BaseDirectory` 加载 **`ClipboardXShellNavigate*.dll`**。推送 **`v*`** 标签后，CI 会为 **Full / ClipboardOnly / FileJumpOnly** 各产出 **no-runtime** 与 **self-contained** zip，完整版另含 **两种 Inno 安装包**（`setup` 与 `setup-self-contained`，见上文「下载与安装」）。

## 环境与要求

| 场景 | 要求 |
|------|------|
| no-runtime 包 | Windows 10/11 + **.NET 8** 桌面运行时 |
| self-contained 包 | Windows 10/11 |
| 开发 | .NET 8 SDK |

## Shell 深度跳转与日志（可选）

仅影响 **Win32 类名为 `#32770` 的系统公共对话框**：主程序在输出目录同时提供 **`ClipboardXShellNavigate.dll`（64 位）** 与 **`ClipboardXShellNavigate32.dll`（32 位）** 时，可按**目标进程架构**选择注入模块，从 **64 位 ClipboardX** 向 **32 位**宿主（如 32 位 WPS 内的「浏览」公共对话框）亦可注入；**关闭「Shell 注入跳转」** 后仅走地址栏/键入模拟。WPS **自有 Qt / 非 `#32770`** 对话框**从不**注入。

**设置 → Shell 注入跳转** 可关闭注入（遇杀软拦截时建议仅用模拟路径）。

- **编译 DLL**：`powershell -ExecutionPolicy Bypass -File native\ShellNavigate\build.ps1`（需 MSVC + Windows SDK；可加 **`-InstallBuildTools`** 用 winget 装 VS Build Tools，较慢）。产物会随 `ClipboardManager.csproj` 条件复制到输出根目录。若工具集报错，可改 `native\ShellNavigate\ClipboardXShellNavigate.vcxproj` 中 `PlatformToolset`（**v143** / **v142**）与本地一致。
- **日志**：**`%LocalAppData%\ClipboardX\shell_navigate.log`**（UTF-8）。**inject** 为托管端写入；**native** 为注入 DLL 在宿主内写入。调试识别与 WPS 等回退时也可关注 **wps**、**custom_fd** 等前缀行。

## 更新记录

完整历史见 **[Releases](https://github.com/chaojimct/clipboardx/releases)**，以下摘录主要变更。

### v1.2.6

- **文件跳转**：「切回时自动同步路径」仅在前一次前台为可解析路径的外部文件管理器时才用资源管理器侧目录驱动自动跳转，避免在对话框内手动改路径后到其它程序再切回被误拉回旧目录
- **兼容**：排除 Internet Download Manager（`IDMan.exe`）主界面被误判为 `#32770` 公共对话框（减少误触发跳转与整窗控件树枚举带来的卡顿）

### v1.2.5

- **安装包**：Inno 安装前 .NET 8 桌面运行时检测增加与 `dotnet --list-runtimes` 一致的目录后备判断（`Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.*`），并补充 arm64 注册表路径，避免本机已装运行时仍误提示未安装

### v1.2.4

- **安装包 / CI**：Inno `[Code]` 中函数内不可使用 `const` 段，改为 `var` 赋值，修复 ISCC 报 `'BEGIN' expected`

### v1.2.3

- **安装包 / CI**：Inno `[Tasks]` 移除非法标志 `checked`（Inno 6 仅支持 `unchecked` 等；默认即为勾选），修复 ISCC 6.7.x 报 unknown flag 导致安装包步骤失败

### v1.2.2

- **安装包 / CI**：Inno 简体中文语言文件改为与 `installer\clipboardx.iss` 同目录随仓库分发（`ChineseSimplified.isl`，来源 jrsoftware/issrc），避免 GitHub Actions 上自带 Inno 未附带 `Languages\ChineseSimplified.isl` 导致编译失败

### v1.2.1

- **构建**：修复 `FileJumpOnly` 剪裁版因排除 Sqlite 包导致 CI 发布失败；Release 矩阵关闭 **`fail-fast`**，避免某一 Flavor 先失败时正在执行的其他矩阵任务被一并取消
- **FileJumpOnly 运行时**：无剪贴板 Flavor 下不再注册剪贴板全局热键、不处理 `WM_CLIPBOARDUPDATE` / 剪贴板呼出热键，避免误占热键

### v1.2.0

- **文件跳转**：切回对话框时路径同步与列表刷新优化；快照 / 自动同步采用**分层短等**（先快试再补等），响应更敏捷  
- **发布物**：GitHub「检查更新」按当前产品前缀匹配 zip，避免多 Flavor 附件误选；Inno 提供 **setup**（框架依赖，缺 .NET 8 桌面运行时时可打开下载页）与 **setup-self-contained**（自包含）两种安装包；CI 产物说明同步  
- **设置界面**：暗色主题下自定义 `TabControl` / 标签页模板，告别「外深内白」与标签对比度不佳  
- 其它修复与改进见提交记录

### v1.1.6

- **对话框到前台自动执行**：检测到打开/保存对话框成为前台时自动采集路径并弹出跳转列表或直跳，无需按快捷键；跳转列表紧贴对话框跟随移动
- **粘贴改用 Shift+Insert**：系统级粘贴快捷键，不会被应用层快捷键映射拦截（如 VSCode 等 Electron 应用），兼容性优于 Ctrl+V
- **WPS 对话框识别优化**：排除 WPS 新建页等非对话框 Qt 窗口的误匹配（检查窗口 owner）
- **修复 WPS 粘性跳转死循环**：粘性自动模式下导航期间抑制键盘钩子，防止 Enter 键被拦截导致无限循环

### v1.1.5

- 自定义文件对话框规则管理（导入/导出/探测向导）
- 剪贴板写入重试与诊断日志
- 图片粘贴回退为文件列表
- Q-Dir 等更多文件管理器路径采集

## 许可证

本项目采用 [MIT 许可证](LICENSE)（版权所有 © 2026 mact）。第三方依赖（如 NuGet 包、原生组件）受其各自许可证约束。
