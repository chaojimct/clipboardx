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
2. 若用 zip：解压到**固定目录**后运行 **`ClipboardX.exe`**（剪裁版为 `ClipboardX-clipboard.exe` / `ClipboardX-filejump.exe`）。配置与历史默认写在 **exe 同级 `Data\`**（绿色便携，可随文件夹备份或带走）。若希望装进用户目录并在「应用和功能」中卸载，可在托盘选 **「安装到当前用户…」**（非自动）；托盘 **关于** 可查看版本与主页。
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
- **快捷短语** — 列表项 **右键 → 设为快捷短语**，绑定关键词后固定参与列表；可切到 **⚡ 短语** 只显示短语项（仍支持搜索）；设/改短语时侧栏通过**低级键盘钩**输入关键词（与主列表搜索一致），**不抢占**原窗口输入焦点  
- **快捷键说明** — 面板底栏右侧 **more** 可打开气泡，查看**完整**快捷键（含面板修饰键 + 数字粘贴、翻页等组合）；底栏一行仍为浓缩提示  
- **图片** — 剪贴板图片在列表中 **右键**，可将图片 **粘贴为文件** 到当前资源管理器窗口（适用时）
- **普通文本** — 非 JSON 文本 **右键 → 作为文件粘贴（资源管理器）**，落盘为临时 `.txt`（流程与图片相同）
- **JSON 文本** — 列表中 **右键** 若内容为 **合法 JSON**（`System.Text.Json` 严格解析，无注释/尾逗号），菜单会出现 **粘贴为 JSON 文件**，写入临时 `.json` 后以文件列表方式粘贴到资源管理器（与图片「作为文件粘贴」流程相同）
- **右键菜单（键盘）** — 面板打开时 **单独按一下 Alt**（勿组合其它键）可打开当前选中项的右键菜单；**↑↓** 切换高亮，**Enter** 执行，**Esc** 关闭；菜单打开时再 **按 Alt 并松开**（无组合键）可关闭菜单；仍可用鼠标点选  
- **数字粘贴** — **面板主键**（默认 Ctrl，可改为 Alt / Win / CapsLock）+ **1～9** 粘贴对应可见条目（与设置中「面板主键」一致）  
- **全局热键** — 默认 **Ctrl+`**（反引号）呼出面板，**设置**中可改；勿与 **文件对话框跳转热键**重复  
- **其它操作** — ↑↓ 选择、Enter 粘贴、Esc、Backspace 删筛选字符、←→ 翻页；可拖动标题栏微调位置（不抢焦点策略下仍尽量跟光标/鼠标）  
- **去重** — 重复内容复制后提升到顶部  
- **预览** — **设置 → 预览行数** 控制每条历史的展示行数；面板打开时 **空格** 或 **鼠标中键点某条** 可在主面板 **左侧或右侧** 弹出气泡（自动选有空间的一侧，不挡列表）：**文本**为全文（可滚动），**图片**为大图预览；再按空格可关闭，**Esc**（先关预览）；**↑↓** 换条时预览内容随之更新；中键点其它条会切换选中并刷新预览  
- **不透明度 / 主题** — 弹窗不透明度滑块（默认 100%）；主题：跟随系统 / 亮 / 暗（Catppuccin Mocha）
- **替换系统 Win+V** — 设置中开启后，按 **Win+V** 打开 ClipboardX 而非系统剪贴板历史（通过注册表禁用系统剪贴板历史，退出时自动恢复）
- **FIFO / LIFO 批量队列** — 多选条目后 **Enter 入队**，在目标应用内每次 **Ctrl+V** / **Shift+Insert** **出队一条**；顶栏 Tag、列表角标、托盘图标随模式换色（普通青绿、FIFO 蓝、LIFO 金）；默认 **Alt+/** 切换模式
- **历史文本就地编辑** — 列表项 **右键 → 编辑文本**，修改后同步更新 SQLite 与检索索引；**Ctrl+Enter** 保存，**Esc** 取消
- **持久化** — 见下文「数据与日志」；首次运行若发现旧版 **ClipboardManager** 配置会自动迁移  

## 设置与托盘

### 设置（选项卡）

| 选项卡 | 内容 |
|--------|------|
| **剪贴板** | 最大记录数；**呼出快捷键**；外观**主题**、**弹出位置**（光标旁 / 鼠标旁）、**不透明度**；**预览行数**；**粘贴到目标窗口**（Ctrl+V / Shift+Insert）；**面板主键**；**开机自启动**；**以管理员身份运行**；**启动时检查更新**；**点击外部隐藏**剪贴板面板；**清空所有历史**（快捷短语仍保留在 `settings.json`） |
| **文件夹跳转** | **文件对话框跳转键**；跳转列表弹出**延时**（0～10000 ms；延时内再按一次跳转键会直接跳当前预选项）；**对话框打开时自动跳转**（合并原"到前台"+"首次点击"，对应 settings 里两项布尔，保存时拉齐相等）；**切回时自动同步路径**；**触发跳转时显示列表**（关则自动跳转静默无 UI；Ctrl+G 永远弹列表）；**跳转列表跟随**（仅在"对话框打开时自动跳转"关闭时显示并生效）；**Shell 注入跳转**；**everything 补充文件夹** |
| **实验性功能** | **当前文件夹内键入筛选**（系统资源管理器 + Everything，需本机 Everything；见该页说明） |
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
| **按 Ctrl+G**（手动入口，永远启用） | 弹跳转列表（含仅 1 条候选）；延时内再按一次直接跳当前预选项；位置由「跳转列表跟随」决定（在自动跳转开启时强制贴对话框） |
| **对话框打开时自动跳转**（默认开） | 对话框成为前台时自动跳转到最优路径；如宿主未发前台事件，则在框内第一次左键时兜底触发；同一对话框只自动跳一次；手动 Ctrl+G 不受影响。开启时所有列表（含 Ctrl+G）强制贴对话框 |
| **切回时自动同步路径**（默认开） | 从外部文件管理器切回**已打开**的文件对话框时刷新候选列表；若最近一次外部文件夹与对话框当前目录不同则自动跳转（仅当上一前台是"能采到路径的文件管理器"时才同步，避免误拉回旧路径） |
| **触发跳转时显示列表**（默认开） | 上述自动跳转触发时是否同时弹出列表（贴对话框）。关 = 静默跳转；Ctrl+G 永远弹列表 |

**路径来源（摘录）：** 资源管理器；**Total Commander / XYplorer / Directory Opus**（与 [QuickSwitch](https://github.com/gepruts/QuickSwitch) 同类专用通道）；以及 FreeCommander、Double Commander、Q-Dir、OneCommander 等**白名单进程**上的**浅层 UI 自动化**（无官方 API 时尽力而为，多栏/四格可能只取扫描到的一条路径）；另含**记忆的上次路径**、列表**收藏**。无任何路径则本次按键无效——可先在外部管理器进到目标目录再试。

### 跳转列表里的操作

与主剪贴板面板类似：**↑↓** 选择，**←→** 翻页，**单击行** 或 **Enter** 确认跳转，**Esc** 清搜索 / 关闭，**主键+1～9** 快速跳可见项，输入字符过滤（含收藏别名）。**右键** 可将路径 **加入收藏**（可设关键词）或管理已收藏项，数据在 **`settings.json`**。**点击外部**是否关列表与设置 **「点击外部隐藏」** 一致。  
当系统「打开 / 保存」等对话框处于前台且键盘焦点在**文件名等可编辑框**（经典 `Edit`、富文本 `RichEdit`）时，**不再拦截按键**，便于在列表仍贴靠显示的同时修改文件名；焦点回到跳转列表所在窗口时恢复键盘逻辑。

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
| 弹出后直接打字过滤 | 支持（低级键盘钩） | 常需先点进搜索框 | 支持 |
| **拼音**（全拼 / 首字母） | 支持 | 无内置 | 无内置 |

| 存储（可配置，默认值供参考） | ClipboardX | Ditto | CopyQ |
|------|------------|-------|-------|
| 引擎 | SQLite（WAL） | SQLite 等 | 自有格式 / SQLite |
| 默认条数规模（均可自定义） | 2000 | 约 500 级 | 约 200 级 |
| 去重置顶 | 支持 | 可配置 | 支持 |

| 界面 | ClipboardX | Ditto | CopyQ |
|------|------------|-------|-------|
| 主题 | 跟随系统 / 亮 / 暗（Catppuccin Mocha） | 传统界面为主 | 内置多套深色/浅色主题，不跟随系统 |
| 体量 | .NET 8，少量 NuGet | 体量因版而异 | 依赖 Qt 等，常驻偏高 |

| 交互 | ClipboardX | Ditto | CopyQ |
|------|------------|-------|-------|
| **替换系统 Win+V** | 支持（可选） | 不支持 | 不支持 |
| **FIFO/LIFO 批量队列** | 支持（多选入队，逐条出队） | 不支持 | 不支持 |
| **历史文本就地编辑** | 支持 | 不支持 | 需要脚本 |
| **类型筛选**（文本/图片/文件） | 支持 | 按组筛选 | 需要配置 |
| **数字快捷粘贴**（主键+1～9） | 支持 | 不支持 | 不支持 |

### 文件对话框跳转（Listary、QuickSwitch、逍遥 QuickJump）

| 特性 | ClipboardX | Listary | QuickSwitch | [逍遥 QuickJump](https://github.com/lch319/XiaoYao_QuickJump) |
|------|--------------|---------|-------------|----------------|
| 授权 | **开源** | 闭源（有 Pro） | 开源 | 开源 |
| 产品重心 | **剪贴板 + 跳转** | 全局搜索 + 跳转等 | 服务 **TC / XY / DO** 等 | **WPS** 等增强 |
| 文件管理器覆盖 | 专用协议 + **白名单 UIA**（十余种量级） | 多种 | **以 TC/XY/DO 为主** | 相对窄 |
| **WPS** 非 `#32770` 框 | **专门多策略** | 部分场景 | 基本不涉及 | **重点适配** |
| **Shell 注入**（公共对话框） | **支持**（`IShellBrowser`，可关） | 有类似深度能力 | **无** | **无** |
| **对话框打开时自动跳转**（到前台 + 首次点击兜底，默认开） | **支持** | 有类似能力 | **无** | **无** |
| **资源管理器 Everything 筛选** | **支持**（资源管理器内直接键入，需 Everything） | 不支持 | **无** | **无** |
| **自定义对话框规则**（向导探测 + 导入导出） | **支持** | 不支持 | **无** | **无** |
| **常用路径管理**（自动学习 + 右键移除） | **支持** | 部分支持 | **无** | **无** |

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

**默认（解压即用）**：数据根目录为 **`exe\Data\`**（`exe` 指**你实际启动的主程序路径**所在目录）。**单文件 zip**（`PublishSingleFile`）在运行时仍会把内嵌程序集解压到 `%TEMP%\.net\…`，但 **v1.2.8 起** 用户配置、SQLite 与日志**不会**再误写进该临时目录，避免因清理 Temp 丢失数据；首次升级时若曾在旧版 Temp 目录下生成过 `Data`，会在启动时**尽量合并**到 exe 旁的 `Data\`（若 Temp 已清空则无法找回）。

若通过托盘 **「安装到当前用户…」** 安装并自 **`%LocalAppData%\Programs\ClipboardX\`** 下主程序启动，则数据根改用 **`%LocalAppData%\ClipboardX\`**（多 Flavor 为 `ClipboardX-clipboard`、`ClipboardX-filejump`）。托盘菜单：未安装时显示 **安装**，已安装时显示 **卸载**；不再依赖 `ClipboardX.portable` 文件。

| 位置 | 说明 |
|------|------|
| `Data\settings.json`（默认）或 `%LocalAppData%\ClipboardX\settings.json`（安装后） | 热键、主题、自启动、**启动时检查更新**、最大条数、快捷短语、**文件夹收藏**、文件跳转相关开关等（含 `LastStartupUpdateNotifiedTag`，用于同一版本只气泡一次） |
| `Data\custom_file_dialogs.json` 或 `%LocalAppData%\ClipboardX\custom_file_dialogs.json` | **自定义文件对话框**规则（与设置中导入/导出格式一致） |
| `Data\clipboard_history.db`（…） | SQLite：**剪贴板历史**正文（WAL 模式） |
| `Data\shell_navigate.log`（…） | **Shell 注入**与相关跳转诊断（UTF-8） |

**迁移**：在 **安装布局**（`%LocalAppData%\ClipboardX\` 数据根）下，若曾使用 **`%AppData%\ClipboardX`** 或 **`%AppData%\ClipboardManager`**，首次启动会在目标文件不存在时尝试复制到当前数据根（详见 `AppPaths.MigrateLegacyPaths`）。**便携 / 单文件**：v1.2.8 起会把误写在单文件解压目录下的旧 **`Data\`** 合并到正确路径（exe 旁）。从解压目录执行 **安装到当前用户** 时，会将 **`exe\Data\`** 中尚不存在于目标目录的文件复制过去，减少配置与历史丢失。

**多 Flavor**：见上表与目录名；安装目录仍以 **`Programs\ClipboardX\`** 下的对应 **主 exe 文件名** 为准。

## 从源码运行

```bash
dotnet run
```

Debug 带控制台；正式发布用 **Release**。工程文件 **`ClipboardManager.csproj`**，输出名 **ClipboardX**，根命名空间仍为 **ClipboardManager**。

### macOS（第一版 · 仅剪贴板）

仓库内 **`ClipboardX.Mac`**（Avalonia 11）+ **`ClipboardX.Core`**（与 Windows 版 **同一 SQLite 表结构** 的历史存储与拼音检索）。在 Mac 上于仓库根目录执行：

```bash
dotnet run --project ClipboardX.Mac/ClipboardX.Mac.csproj
```

配置与数据库默认在 **`~/Library/Application Support/ClipboardX/`**（`mac-settings.json`、`clipboard_history.db`）。全局热键 **Ctrl+`（反引号）** 使用 **SharpHook**，一般需在 **系统设置 → 隐私与安全性 → 辅助功能** 中为终端或发布产物授权；亦可从托盘菜单打开面板。

发布示例：`dotnet publish ClipboardX.Mac/ClipboardX.Mac.csproj -c Release -r osx-arm64 --self-contained`（Intel 用 `osx-x64`）。当前不包含 Windows 上的文件对话框跳转等能力。

### 源码目录（简要）

| 目录 | 内容 |
|------|------|
| `ClipboardX.Core/` | 跨平台共享：剪贴板历史 SQLite、`HistoryEntry`、拼音检索（供 Mac 等宿主使用） |
| `ClipboardX.Mac/` | macOS / Avalonia 剪贴板第一版（轮询剪贴板、列表、托盘、Ctrl+`） |
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
$v = "1.6.0"   # 与 csproj 同步后改这里
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

单文件会把内嵌 DLL 解压到临时目录；运行时通过 `AppContext.BaseDirectory` 解析并加载 **`ClipboardXShellNavigate*.dll`**（与**用户数据根** `exe\Data\` 的路径规则无关，见上文「数据与日志」）。推送 **`v*`** 标签后，CI 会为 **Full / ClipboardOnly / FileJumpOnly** 各产出 **no-runtime** 与 **self-contained** zip，完整版另含 **两种 Inno 安装包**（`setup` 与 `setup-self-contained`，见上文「下载与安装」）。

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

结构化列表见 **[CHANGELOG.md](CHANGELOG.md)**（推送 `v*` 标签后，GitHub Release 会从中截取当前版本的 **更新内容**）。以下为 README 内便于浏览的摘录；安装包见 **[Releases](https://github.com/chaojimct/clipboardx/releases)**。

### v1.7.1

- **设置页布局修复**：「退出时自动清空历史」行不再被挤压为 10px 高度

### v1.9.9

- **修复 F2 重命名输入被快速搜索劫持**：桌面 / 资源管理器按 F2 重命名时输入字符误触发 Everything 快速搜索并吞掉全部按键。桌面路径补上焦点编辑框检查（此前缺失）、F2 后 1.5 秒内不启动会话（覆盖焦点未就绪竞态）、会话中焦点落入编辑框立即结束会话还键

### v1.9.8

- **文件跳转稳定性（重点修复）**：修复跳转数次后宿主程序崩溃/卡死——v1.9.7 内部改动误释放了对话框借用的 `IShellBrowser` 指针导致引用计数错乱（Edge 保存弹窗、微信等崩溃闪退），同时去掉钩子回调内的 Sleep 重试（曾堵死宿主消息泵）；busy 重试移至后台线程，宿主全程不被阻塞
- **防误关对话框**：自动同步等无人值守跳转改为仅注入导航，注入失败静默放弃；手动跳转的地址栏回退加焦点安全闸，焦点不在地址栏时中止键入，杜绝 Enter 误触「打开/保存」
- **WPS 兼容**：修复保存确认框/错误弹窗被误判为文件对话框而收到按键（Enter 直接点中「保存」）
- **开机自启**：修复登录时闪现命令行黑框（schtasks 引号写进任务 Command 被 cmd 解释执行）；同时修正任务 72 小时执行时限、电池模式禁启等隐藏问题，升级后自动重注册修复旧任务

### v1.9.7

- **管理员权限开机自启改用任务计划程序**：之前用 HKCU Run + PowerShell RunAs，登录阶段 UAC 阻塞导致自启失败。改用 `schtasks /Create /SC ONLOGON /RL HIGHEST`，由 Task Scheduler 提权启动，不弹 UAC

### v1.9.6

- **弹窗定位修复**：CodeBuddy / Trae Work 等 Electron 应用呼出 ClipboardX 时弹窗错位（落到主屏右上角或窗口左下角）。通过 UIA `ClassName` 为空 + rect 与窗口面积比 ≥ 0.95 双判据识别容器矩形并拒绝，落到鼠标兜底；不再依赖硬编码进程名白名单

### v1.9.5

- **内存优化**：修复 OCR 后台队列启动时把所有图片字节从数据库懒加载到内存（80-200MB），改为只在 Worker 处理该条时加载、处理完即释放；并修复缩略图在懒加载场景下不显示的回归

### v1.9.4

- **粘贴稳定性**：单条/批量/作为文件粘贴优先原生剪贴板 API，减少 OLE 争用导致的卡顿与 `CLIPBRD_E_CANT_OPEN` 失败；失败日志可查看占用进程

### v1.9.3

- **多图预览导航**：Files 条目含多张图片时，预览气泡可 ← → 或点击导航条切换查看

### v1.9.2

- **文本粘贴为文件**：普通文本右键「作为文件粘贴（资源管理器）」，在资源管理器窗口 Ctrl+V 落盘为 `.txt`

### v1.9.1

- **按键穿透**：实验性功能页可配置；面板打开时按住 CapsLock 等修饰键或已录制组合键将交给 AutoHotkey，不抢占搜索输入

### v1.9.0

- **图片 OCR**：复制图片后自动 Windows OCR 识别文字，列表/预览可查看、可搜索；右键或 Shift+Enter 粘贴识别文字；缺语言包时引导安装
- **面板置顶**：标题栏 📌 置顶，便于连续多次粘贴

### v1.8.0

- **排除应用列表**：设置 → 实验性功能 →「排除应用列表」；前台属于列表中的应用时，ClipboardX 全局快捷键不触发，解决与游戏、专业软件等的快捷键冲突
- **文件跳转修复**：抑制重复导航与自动同步冲突；Shell 注入失败时复查路径；粘性模式同路径去重；自动打开列表候选未就绪时重试；跳转列表默认尺寸略增

### v1.7.0

- **退出自动清空历史记录**：设置 → 剪贴板 →「退出时自动清空历史」（默认关闭）
- **全局 Ctrl+G 收藏跳转**：任意界面按 Ctrl+G 打开收藏/常用文件夹列表，选择后在资源管理器中打开
- **FIFO 首次粘贴修复**（#25）：从剪贴板监控器自动入队时同步推送到队列头部
- **Win+V 开始菜单修复**（#24）：拦截后注入 Escape + 合成 Win KeyUp 重置状态
- **XYplorer 冲突修复**（#23）：移除脚本消息 null terminator，移除不支持的 `i` 参数
- **更新卡住修复**（#20）：用 PID 轮询替代固定 Sleep

### v1.6.3

- **收藏按钮修复**：DLL 注入读取文件对话框路径，修复收藏错误路径问题；注入调用移至后台线程消除卡顿
- **Explorer 路径变化检测**：轮询监听资源管理器内导航，切回对话框时自动同步最新路径
- **Everything 选中操作配置**：新增「直接打开」模式，选中结果后导航前台文件对话框到目标路径
- **Everything Escape 修复**：弹窗获得焦点后 Escape 可正常关闭

### v1.6.1

- **Everything 快速筛选扩展**：在「此电脑」「库」等非常规文件夹以及桌面中键入时，支持全盘自动筛选并弹出结果

### v1.6.0

- **替换系统 Win+V**：设置 → 剪贴板 →「替换系统 Win+V」（默认关闭）；开启后按 Win+V 打开 ClipboardX 而不是系统剪贴板历史；退出时自动恢复系统设置
- **代码重构**：键盘钩子、鼠标钩子、托盘图标拆分为独立部分类文件，降低单文件复杂度

### v1.5.3

- **剪贴板 · 就地编辑文本**：右键「编辑文本」改内容并写回历史/数据库；编辑时临时允许主窗体激活以取得键盘焦点；弹窗靠主面板右侧定位；**Esc** 关闭、**Ctrl+Enter** 保存

### v1.5.2

- **批量粘贴 · 多图/多文件合并**：相邻多张图片落盘为 PNG 并入 **FileDropList**，相邻文件直接合并路径，相邻图+文件混合统一走 FileDropList；从「N 次写入 + N 次粘贴」降为 **1 次**，根治多图场景在 Word 中的「漏粘 / 重复粘 / 出现字符 v」问题
- **批量粘贴 · 段间稳定性**：以剪贴板序列号 + 「目标 OpenClipboard」为信号 `WaitForTargetClipboardConsumeAsync` 等待目标消费上一段（图片段上限 600ms、文本段 350ms），替代固定 22ms 让步；批量入口立即 Hide，避免每段反复抢前台
- **批量粘贴 · 防误触发 v**：`SendCtrlVPaste` / `SendShiftInsertPaste` 拆为「先释放修饰键 + 1ms 让步 → 再发组合键」两次 SendInput
- **剪贴板呼出 · Word 二次错位修复**：`PositionPopup` 优先走 **UIA TextPattern** 取文档光标（再退回 GUIThreadInfo / AttachThreadInput），UIA 超时放宽到 500ms 并启动时预热；首帧 `Opacity=0` 消除位置跳变
- **批量粘贴 · 入队卡顿**：`AutoBatchEnqueueIfNeeded` 来自剪贴板监控时跳过 `SchedulePushBatchQueueHeadIfChanged`，避免与源应用 OpenClipboard 互锁触发 8×55ms 重试

### v1.5.1

- **文件对话框跳转 · 设置**：「对话框打开时自动弹出列表」与「自动跳转到最佳路径」分开展示；两者同时开启时先直跳最佳路径再弹列表，仍可在列表中改选
- **文件对话框跳转 · 性能**：低阶鼠标钩仅在「仅自动跳转、不自动弹列表」时挂载，减轻全局鼠标移动/拖拽卡顿
- **文件对话框跳转 · 稳定性**：列表项高亮为 `Run` 时沿逻辑树向上查找，避免 `VisualTreeHelper.GetParent` 抛异常
- **其它**：设置与 `settings.json` 字段说明同步；`FileJumpAutoOnFirstClick` 语义为「自动跳转最佳」并调整默认

### v1.5.0

- **剪贴板 · 双击才粘贴**（默认关）：设置中可开启；开启后单击仅选中，双击才粘贴（`PasteRequiresDoubleClick`）
- **剪贴板 · 预览**：预览气泡位置上移微调；右侧展开时以列表项为锚点，改善中部选中时垂直对齐
- **文件对话框跳转 / Everything**：列表来源等文案统一为 **everything**；底栏移除推广链接
- **设置**：「双击才粘贴」等行修正 Grid 行号，避免控件重叠

### v1.4.0

- **文件对话框跳转 · 性能**：同轮采集中 **Shell.Windows** 只枚举一次；**COM 与 HWND 精确匹配** 时跳过资源管理器整窗 **relaxed UIA**；**跳转列表延时为 0** 时防抖 **80ms**
- **文件对话框跳转 · findx**：补充路径来源、底栏与设置等 **findx** 文案与 **GitHub** 链接；底栏快捷键单行展示
- **文件对话框跳转 · 焦点**：粘性贴靠模式下优化 **前台/焦点**（去 Owner、首帧允许激活、`SetForegroundWindowAggressive`），弹出后可直接键盘筛选
- **实验性功能**：**当前文件夹内键入筛选** 默认 **开启**（旧配置缺字段时视为开启；需本机 **Everything**）

### v1.3.6

- **剪贴板 · 粘贴模式**：设置中可选向目标窗口模拟 **Ctrl+V** 或 **Shift+Insert**（默认 Ctrl+V）；检测到**命令行/终端**目标且当前为 Ctrl+V 时，**临时**改用 Shift+Insert（不改保存项）
- **设置 · 管理员身份**：默认以管理员运行；切换该项并保存后**自动重启**；**设置窗口**打开时前置，避免被置顶剪贴板面板或其它窗口挡住

### v1.3.5

- **剪贴板 · 设置**：可自定义弹窗**宽度**、**最大高度**；**每次翻页条数**（与 PgUp/Dn、←→ 及翻页快捷键共用）
- **剪贴板 · 翻页**：翻页改为**完整组合键**（默认 **Ctrl+-** / **Ctrl+=**，须含修饰键）；`settings.json` 中增加上下翻页的 **modifiers + key** 字段，旧版仅单键时会按 **Ctrl+** 迁移
- **剪贴板 · 面板主键**：设置中**面板主键**（Ctrl / Alt / Win / CapsLock）恢复显示，便于与快贴、短语过滤等组合使用
- **设置 UI**：修复「清空所有历史记录」按钮行在 Grid 中行定义缺失导致的**整行红色拉满**（补充行定义与行号）

### v1.3.4

- **剪贴板 · 批量队列**：**FIFO / LIFO** 下队列为空后，可在**下一次**他处粘贴键时**自动切回普通**模式（默认开启，设置中可关）；兼容旧键 **`FifoAutoSwitchToNormalAfterQueueDone`**

### v1.3.3

- **剪贴板 · Alt 全局热键**：改善 **VS Code** 等宿主下 **Alt+`** 呼出/关闭后的菜单焦点与键序收尾；修复 **`BeginInvoke(HidePopup)`** 导致的 **`TargetParameterCountException`**
- **剪贴板 · 批量队列**：**FIFO/LIFO** 异步推剪贴板时若已切回**普通**或队首已变则中止写入，避免 **Ctrl+V 偶尔粘成别的内容**

### v1.3.1

- **文件对话框跳转**：微信（Weixin）等宿主下「打开文件」可依赖 **`GetLastActivePopup` + 全局焦点事件** 触发「对话框打开时自动跳转」；该项开启时同时挂鼠标钩，宿主未发前台事件也能在框内首次点击时兜底跳转
- **整理**：候选路径采集恢复为默认 **Z 序 +2** 的轻量逻辑，去掉多轮 Sleep 重试；前台是否仍在对话框内统一为 **`IsForegroundFocusOnFileDialogRoot`**

### v1.3.0

- **剪贴板 · 批量队列**：支持 **普通 / FIFO / LIFO** 三种模式；多选在 **FIFO/LIFO** 下 **Enter 入队**，在目标应用内每次 **Ctrl+V** / **Shift+Insert** 出队并推进剪贴板；顶栏模式 **Tag**、列表 **序号角标**、**托盘图标** 随模式换色（普通青绿 **#139493**、FIFO 蓝、LIFO 金），托盘叠 **F/L** 标记
- **剪贴板 · 快捷操作**：默认 **Alt+/**（可在设置中修改）在面板外 **循环切换** 批量模式，与面板顶栏左键顺序一致（普通 → LIFO → FIFO → 普通）
- **剪贴板 · 主题**：暗色面板底色与灰阶贴近 **VS Code Dark+**，强调色与列表选中/悬停在弹窗内 **随当前批量模式主色** 混色，与顶栏 Tag 视觉统一；亮色仍为浅灰窗口底 + 品牌强调色
- **剪贴板 · 交互**：底栏 **more** 与顶栏同样使用模式色 **pill**；快捷短语侧栏等延续不抢焦点输入；详见设置内帮助文案

### v1.2.9

- **剪贴板 · 快捷短语**：设/改短语侧栏改为与主列表相同的**键盘钩**输入关键词，不再使用可聚焦 `TextBox`，避免抢宿主应用输入焦点  
- **剪贴板 · 帮助**：主面板底栏右侧 **more** 打开气泡，展示**完整**快捷键说明（含修饰键组合）；**Esc** 可关闭该气泡  
- **文件对话框跳转**：根据前台线程 **`GetGUIThreadInfo`** 判断焦点；若在 **另存为** 等对话框的 **Edit / RichEdit** 文件名框中，**放行**按键，便于边开跳转列表边编辑文件名；焦点在跳转面板内时逻辑不变  

### v1.2.8

- **单文件 / 绿色 zip**：便携模式数据根改为 **`Environment.ProcessPath` 所在目录下的 `Data\`**，不再误用 `%TEMP%\.net\…` 单文件解压目录，避免清理 Temp 丢失设置与剪贴板历史；首次启动可合并旧版误写在 Temp 下的 `Data`（前提是该目录仍在）
- **安装**：「安装到当前用户…」在框架依赖多文件发布时，按当前主 exe 名检测主 DLL（如 `ClipboardX-clipboard.dll`），剪裁版整机复制不再漏文件

### v1.2.7

- **安装与数据**：默认数据根为 **`exe\Data\`**（解压即用）；仅当从 **`%LocalAppData%\Programs\ClipboardX`** 下主程序启动时使用 **`%LocalAppData%\ClipboardX`**（多 Flavor 目录名不变）；取消 **Release** 启动时自动复制到 Programs
- **托盘**：未安装时显示 **「安装到当前用户…」**，已安装时显示 **「卸载…」**；不再依赖 **`ClipboardX.portable`**
- **安装**：复制到用户 Programs 时，将 **`exe\Data\`** 中目标侧尚不存在的文件合并到用户配置目录，减少丢设置/历史
- **其它**：按用户安装目录中的主 exe 名与 **剪裁版** 一致；Debug 下安装菜单避免 CS0162 不可达代码警告

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

- **对话框打开时自动跳转**：到前台时自动跳到最优路径；宿主未发前台事件时由框内首次点击兜底；是否同时弹列表由「触发跳转时显示列表」决定。`settings.json` 仍为 `FileJumpPickerOpenWhenDialogForeground` 与 `FileJumpAutoOnFirstClick` 两个字段，保存时拉齐相等
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
