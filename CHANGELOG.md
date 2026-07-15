# Changelog

与 README「更新记录」同步维护。推送 `v*` 标签时，GitHub Release 正文中的 **更新内容** 由本节中对应 `## [版本]` 段落自动截取。

格式依据 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/) 的常见写法；日期为发布日（与 tag 推送日一致即可）。

## [Unreleased]

## [1.9.5] - 2026-07-15

### 剪贴板 · 内存优化

- **修复 OCR 后台队列击穿懒加载**：启动时 `ImageOcrQueue.EnqueueBackfill` 的过滤条件调用 `TryGetImageData()`，触发每张图片从 SQLite 懒加载完整 PNG 字节到内存。40 张候选图片 × 2-5MB ≈ 80-200MB 全部读入内存，与降内存目标相反。改为仅用 `PersistedId` 元数据筛选，真正的字节加载延迟到 Worker 处理该条时
- **OCR 处理完即释放图片字节**：`ProcessOneAsync` 完成单条 OCR 后调用 `ReleaseImageData()`，处理 N 张图片时同时只持有 1 张的字节在内存（之前 N 张会累积不释放）
- **修复缩略图懒加载回归**：`Thumbnail` getter 之前用 `ImageData != null` 判断，懒加载场景下永远为 false，导致图片条目列表不显示缩略图。改为按 `Type == EntryType.Image` 判断，并在 `CreateThumbnail` 生成 64px 缩略图后立即释放原始 PNG 字节
- **`Enqueue` 早退检查不预加载**：改为「内存已有 OR 可懒加载」二选一判断，新捕获图片走内存路径，启动 backfill 走懒加载路径，两者都不预读字节

## [1.9.4] - 2026-07-07

### 剪贴板 · 粘贴稳定性

- **原生剪贴板写入**：文本/图片/文件优先走 user32 `CF_UNICODETEXT` / `CF_DIB` / `CF_HDROP`，绕开 WPF OLE 长占用，缓解外部进程抢剪贴板导致的卡顿与失败
- **批量与作为文件粘贴**：合并文本、合并 FileDrop、FIFO/LIFO 队首预推、资源管理器「作为文件粘贴」统一接入原生路径
- **诊断增强**：`CLIPBRD_E_CANT_OPEN` 时记录占用进程；附 `scripts/clip-holder-watch.ps1` 供排查剪贴板占用

## [1.9.3] - 2026-07-04

### 剪贴板 · 预览

- **多图预览导航**：复制多张图片文件时，空格/中键预览气泡可左右切换查看；底部显示 ‹ 1 / N › 导航条，← → 键同样可切换

## [1.9.2] - 2026-06-30

### 剪贴板 · 右键菜单

- **普通文本粘贴为文件**：文本历史项右键「作为文件粘贴（资源管理器）」，写入临时 `.txt` 后以文件列表粘贴到资源管理器（合法 JSON 仍使用「粘贴为 JSON 文件」）

## [1.9.1] - 2026-06-30

### 实验性功能 · 按键穿透

- **按键穿透**：设置 → 实验性功能 →「剪贴板 · 按键穿透」；面板打开时将指定修饰键或组合键交给 AutoHotkey 等外部低级钩子，不写入搜索框
- **修饰键锁存**：CapsLock / Ctrl / Shift / Alt / Win 物理按下锁存，兼容 AHK `CapsLock & k` 等组合
- **可配置**：穿透修饰键多选、组合键录制列表、「保留面板导航键」开关（Esc/方向键/Enter 仍由面板处理）

## [1.9.0] - 2026-06-30

### 剪贴板 · 图片 OCR

- **图片文字识别**：复制图片后使用 Windows 内置 OCR 后台识别，列表显示文字摘要、空格预览图文、支持搜索（含拼音）
- **OCR 语言包引导**：缺少系统 OCR 组件时提示打开设置或提权安装
- **粘贴识别文字**：右键「粘贴文字（OCR）」、单选图片 **Shift+Enter** 仅粘贴文字
- **CJK 空格修正**：去除中文等 CJK 字符间误插空格，保留中英文混排合理间距
- **设置开关**：剪贴板页「图片文字识别 (OCR)」，默认可关闭

### 剪贴板 · 面板

- **置顶按钮**：标题栏 📌 置顶后面板粘贴后不关闭、点击外部不自动隐藏，便于连续粘贴

## [1.8.0] - 2026-06-26

### 设置 · 排除应用列表

- **排除应用列表**：设置 → 实验性功能 →「排除应用列表」；添加进程名（不含 .exe，不区分大小写），当该应用处于前台时 ClipboardX 所有全局快捷键（呼出面板、文件跳转、批量切换、Win+V 替换）均不触发，解决与游戏、专业软件等的快捷键冲突

### 文件跳转 · 修复

- **重复导航抑制**：已在目标路径时跳过导航；导航后增加短暂抑制窗口，避免自动同步/自动打开与手动跳转互相触发导致重复跳转
- **Shell 注入容错**：注入返回失败后复查当前路径，实际已到达目标时仍视为成功
- **粘性模式去重**：同一路径 1.5 秒内重复点击不再重复导航
- **自动打开重试**：对话框前台自动打开跳转列表时，候选路径尚未就绪会短暂重试
- **跳转列表尺寸**：默认宽度 500、最大高度 620，列表区域可显示更多条目

## [1.7.1] - 2026-05-15

### 修复

- 修复设置页「退出时自动清空历史」行被挤压为 10px 高度、几乎不可见的问题（Row 定义错误导致元素落在 spacer 行上）

## [1.7.0] - 2026-05-15

### 剪贴板 · 退出清空历史

- **退出自动清空历史记录**：设置 → 剪贴板 →「退出时自动清空历史」（默认关闭）；开启后退出程序时自动清空剪贴板历史，快捷短语不受影响

### 文件夹跳转 · 全局收藏快捷键

- **全局 Ctrl+G 收藏跳转**：在任意界面按 Ctrl+G（或自定义跳转键）打开收藏/常用文件夹快速选择窗口，选择后在资源管理器中打开；文件对话框中的跳转行为不变

### Bug 修复

- **FIFO 模式首次粘贴修复**（#25）：从剪贴板监控器自动入队时同步推送到队列头部，修复 FIFO 模式首次粘贴内容错误的问题
- **Win+V 开始菜单弹出修复**（#24）：拦截 Win+V 后吞掉 Win KeyUp 并注入 Escape + 合成 Win KeyUp 重置状态，防止开始菜单意外弹出
- **XYplorer 冲突修复**（#23）：移除 XYplorer 脚本消息中的 null terminator（导致 scripting console 出现 `-` 错误）；移除不支持的 `get('path', i)` 调用；前台切换时跳过 XYplorer/TC 的路径采集
- **更新卡住修复**（#20）：用 PID 轮询循环替代固定 Sleep，等待旧进程退出后再替换文件

## [1.6.3] - 2026-05-12

### 文件跳转 · 常用路径修复

- **收藏按钮修复**：通过 DLL 注入（IShellBrowser COM 链）读取文件对话框当前路径，解决 UIA 读取失败导致收藏错误路径的问题
- **DLL 注入性能优化**：所有注入调用移至后台线程，解决自动同步路径时的界面卡顿

### 文件跳转 · 自动同步增强

- **Explorer 路径变化检测**：新增 600ms 轮询定时器，监听资源管理器窗口内导航变化，切回对话框时自动同步最新路径
- **未缓存路径读取**：新增 `fresh` 模式绕过 Shell COM 15 秒缓存，确保获取实时路径

### 资源管理器 · Everything 筛选增强

- **选中操作配置**：新增设置项「选中操作」，支持「从资源管理器打开」（默认，在资源管理器内就地导航选中文件）或「直接打开」（导航前台文件对话框到目标路径）
- **Escape 键修复**：Everything 弹窗获得焦点后，Escape 键可正常关闭弹窗

## [1.6.2] - 2026-05-11

### 文件跳转 · 常用路径增强

- **最大数量配置**：支持设置常用路径的最大保存数量
- **自动加入阈值**：路径确认次数达到阈值后自动加入常用路径列表
- **右键移除**：常用路径列表增加右键上下文菜单，可手动移除不需要的路径
- **路径截断显示**：超长路径自动截断并显示提示

### 修复

- 修复文件对话框跳转选择器窗口在所有者窗口销毁时未正确关闭的问题
- 优化应用设置保存机制，增加延迟保存避免频繁写入
- 文件对话框点击事件增加时间窗口限制，避免误触发

## [1.6.1] - 2026-05-08

### 资源管理器 · Everything 快速筛选

- **非常规文件夹支持**：在「此电脑」「库」等无法获取路径的文件夹中键入时，不再静默忽略，改为直接发起全盘筛选并弹出结果窗口
- **桌面支持**：在桌面（Progman / WorkerW）键入时自动以桌面目录作为搜索路径触发筛选

## [1.6.0] - 2026-05-06

### Win+V 替换系统剪贴板历史

- **替换系统 Win+V**（默认关闭）：设置 → 剪贴板 →「替换系统 Win+V」；开启后按 **Win+V** 打开 ClipboardX 而不是系统剪贴板历史
- **实现机制**：启用时通过注册表禁用系统剪贴板历史（`HKCU\Software\Microsoft\Clipboard\EnableClipboardHistory`），退出时自动恢复；WH_KEYBOARD_LL 钩子拦截 Win+V 按键并触发 ClipboardX 弹窗

### 代码重构

- **PopupWindow 拆分**：键盘钩子逻辑拆至 `PopupWindow.KeyboardHook.cs`，鼠标钩子逻辑拆至 `PopupWindow.MouseHook.cs`；`PopupWindow.xaml.cs` 从 6848 行减至约 5160 行
- **App 拆分**：托盘图标逻辑拆至 `App.TrayIcon.cs`；`App.xaml.cs` 从 849 行减至约 675 行

### 清理

- 删除调试日志 `debug-241056.log` 和补丁文件 `my_changes.patch`
- `.gitignore` 增加 `debug-*.log` 和 `*.patch` 规则

## [1.5.5] - 2026-04-26

### 文件对话框跳转

- **误触发修复**：排除 Sublime Text 等编辑器的 `Save Changes` / `Unsaved Changes` / `保存更改` 等保存确认框，避免其被误判为文件对话框后触发自动跳转或路径输入
- **标题识别收窄**：英文标题不再因为任意包含 `save` 就判定为文件对话框，仅保留 `Save As`、`Open File`、`Browse` 等文件对话框语义

## [1.5.4] - 2026-04-26

### 文件对话框跳转

- **首显优化**：调整跳转列表首显、焦点处理与资源管理器路径缓存逻辑，降低文件对话框打开和拖动时的阻塞风险
- **刷新节奏**：推迟完整候选刷新，减少刚打开文件对话框时与资源管理器 Shell 枚举争抢导致的卡顿

## [1.5.3] - 2026-04-24

### 剪贴板 · 历史文本就地编辑

- **编辑入口**：列表项右键「编辑文本」修改内容；写回列表预览与检索，普通历史项同步 **SQLite**（`TryUpdateText`）；快捷短语同步 `_quickPastes`
- **键盘焦点**：编辑期间临时去掉主窗 **`WS_EX_NOACTIVATE`**，并将 **`WM_MOUSEACTIVATE`** 改为可激活，配合 **`SetForegroundWindowAggressive`**，使光标进入多行编辑框；关闭编辑后恢复样式并把前台还回宿主
- **布局**：编辑弹窗改为 **`PlacementMode.Custom`**，相对 **`MainBorder`** 右侧留出间隙，减轻被主面板遮挡
- **快捷键**：**Esc** 取消、**Ctrl+Enter** 保存（`PreviewKeyDown` 与低级键盘钩双重处理，普通 Enter 仍换行）

## [1.5.2] - 2026-04-23

### 批量粘贴

- **多图/多文件合并**：`BuildAdjacentRuns` 升级为「Text vs 非 Text」二分聚合；相邻图片落盘 PNG、相邻文件直接取路径、图+文件混合统一进入同一个 `FileDropList`，由新增 `RunBatchImagesAndFilesAsFileDropAsync` 一次 SetClipboard + 一次粘贴交付，根治 Word 多图场景下「漏粘 / 重复粘 / 出现字符 v」
- **段间稳定性**：新增 `WaitForTargetClipboardConsumeAsync`，以剪贴板序列号 + 目标 `OpenClipboard` 为信号等待消费（图片段 600ms、文本段 350ms 上限），替代固定 22ms 让步；`RunOrderedPastesWithAdjacentTextMergeAsync` / `RunSequentialPastesAsync` 入口立即 `HidePopup`，每段 `hidePopupAfter:false`
- **防误发 v**：`SendCtrlVPaste` / `SendShiftInsertPaste` 拆分为「先释放修饰键 + 1ms 让步 → 再发组合键」两次 `SendInput`，避免目标先收到裸 V
- **入队卡顿**：`AutoBatchEnqueueIfNeeded(fromClipboardMonitor: true)` 跳过 `SchedulePushBatchQueueHeadIfChanged`，避免与源应用 `OpenClipboard` 抢锁触发 `CLIPBRD_E_CANT_OPEN` 多次重试

### 剪贴板呼出

- **Word 二次呼出错位修复**：`PositionPopup` 优先 `TryGetCaretByAutomation`（UIA TextPattern），再回退 `GetGUIThreadInfo` / `AttachThreadInput + GetCaretPos`；UIA 超时放宽到 500ms，启动时 `WarmUpUiaCaretProxy` 预热
- **首帧不闪**：`ShowPopup` 设 `Opacity=0` 后再 `Show()`，定位完成后再恢复显示；新增 H23 调试日志覆盖每条分支

## [1.5.1] - 2026-04-23

### 文件对话框跳转

- **设置 UI**：「对话框打开时自动弹出列表」与「自动跳转到最佳路径」分开展示；`FileJumpPickerAutoPopup` 与 `FileJumpPickerOpenWhenDialogForeground` 同义，保存时拉齐
- **行为**：两者同时开启时先直跳最佳路径再弹跳转列表，可在列表中改选；仅自动跳转、不弹列表时走静默直跳，并以低阶鼠标钩兜底无前台事件的宿主
- **性能**：低阶鼠标钩仅在「仅自动跳转、不自动弹列表」时挂载，避免长期挂钩导致系统级鼠标移动/拖拽卡顿
- **稳定性**：点击列表高亮内联文本（`Run`）时以逻辑/视觉树混合向上查找 `ListBoxItem`，修复 `GetParent` 抛错

## [1.5.0] - 2026-04-19

### 剪贴板 · 交互与预览

- **双击才粘贴**（默认关闭）：设置 → 剪贴板 →「双击才粘贴」；开启后单击列表仅选中、不粘贴，在 **PreviewMouseDown** 中根据 **ClickCount == 2** 触发粘贴，避免误触；保存项 `PasteRequiresDoubleClick`
- **空格预览**：预览气泡相对选中行上移微调；右侧展开时以 **ListBoxItem** 为锚点，减轻列表中部选中时垂直错位感

### 文件对话框跳转与 Everything 文案

- **列表来源** 等界面与设置说明中的品牌表述统一为 **everything**（voidtools Everything）；移除底栏 GitHub 推广链接

### 设置界面

- 「双击才粘贴」与「清空历史」等行修正 **Grid.Row** 与 **RowDefinitions** 对齐，避免控件挤在间隔行内重叠

## [1.4.0] - 2026-04-15

### 文件对话框跳转（Ctrl+G / 到前台自动弹出）

- **性能**：同一次 `CollectCandidates` 内 **Shell.Application.Windows** 仅枚举一次；**ExplorerComMatchScore ≥ 4**（COM 与 HWND 精确匹配）时跳过资源管理器整窗 **relaxed UIA**（原为主要耗时）；**延时为 0** 时合并前台防抖由 **180ms → 80ms**
- **findx 展示**：补充列表来源标签、底栏、设置说明等改为 **findx** 品牌与 **GitHub** 链接；底栏快捷键提示压缩为单行并去掉冗余说明
- **焦点**：粘性贴靠模式下去除 **Win32 Owner**、首帧 **`SetWindowPos` 允许激活**、**`SetForegroundWindowAggressive`**（`AttachThreadInput` + 前台），弹出后键盘可直接进入 **findx 筛选**

### 实验性功能 · 资源管理器与 Everything

- **当前文件夹内键入筛选** 默认 **开启**（`settings.json` 中无 `ExplorerEverythingQuickFindEnabled` 的既有配置会迁移为开启）；在资源管理器、焦点不在地址栏/搜索框时直接键入字符，通过 **Everything** IPC 将结果限定在当前文件夹（含子文件夹），小窗列出匹配项，**Enter** 在资源管理器中定位；**设置 → 实验性功能** 可关闭并设置「筛选最大条数」（1～2000）。需本机已安装并运行 Everything；`Everything64.dll` 随构建复制到程序目录

## [1.3.6] - 2026-04-16

### 剪贴板 · 设置与粘贴

- **粘贴到目标窗口**：可在设置中选择写入剪贴板后向目标窗口模拟 **Ctrl+V** 或 **Shift+Insert**（默认 Ctrl+V；`settings.json`：`PasteSimulationMode`）
- **命令行 / 终端**：当配置为 Ctrl+V 时，若检测到粘贴目标为常见控制台或终端（如 `ConsoleWindowClass`、Windows Terminal 宿主类名，或 cmd、PowerShell、Windows Terminal、WSL、mintty 等进程），**临时**改用 **Shift+Insert**（不修改已保存的设置）

### 设置与管理

- **以管理员身份运行**：默认开启；在设置中切换该项并保存后，**自动重启**以匹配目标权限（UAC 提升、从已提升实例降权启动普通实例，或同权限下重启）
- **设置窗口前置**：打开设置时指定 **Owner** 为剪贴板浮层（相对居中），并在加载后 **激活窗口 + 抢前台**，避免设置窗落在置顶剪贴板面板或其它应用之后

## [1.3.5] - 2026-04-15

### 剪贴板 · 设置与外观

- **面板尺寸**：可在设置中自定义剪贴板弹窗**宽度**与**最大高度**（DIP，带合理范围校验），写入 `settings.json`（`PopupPanelWidth` / `PopupPanelMaxHeight`）
- **翻页条数**：新增「每次翻页条数」，与 **PgUp / PgDn、←→** 以及下方翻页快捷键一致，控制列表每次滚动的条目数（1～50，默认 8）
- **翻页快捷键**：改为**完整组合键**录制（须含 **Ctrl / Shift / Alt / Win** 之一，与呼出快捷键相同交互）；默认仍为 **Ctrl+-** 向上、**Ctrl+=** 向下；配置为 `PanelPageScrollUpModifiers` / `PanelPageScrollUpKey` 与 `PanelPageScrollDownModifiers` / `PanelPageScrollDownKey`；从仅保存单键的旧配置加载时会自动补全为 **Ctrl+** 语义
- **面板主键**：设置项恢复显示，可在 **Ctrl / Alt / Win / CapsLock** 间切换（与 **1～9** 快贴、**Tab** 短语过滤、**Enter** 等面板内组合逻辑一致）；帮助气泡中同步说明可在设置中更换

### 设置界面

- **修复**：「清空所有历史记录」按钮原 `Grid.Row` 超出 `RowDefinitions` 时，WPF 会为超出行隐式使用 `*` 行高，导致危险色按钮被**纵向拉满**；已补充 `RowDefinition`、修正行号并设 `VerticalAlignment="Top"`，避免误占满滚动区域

## [1.3.4] - 2026-04-10

### 剪贴板

- **FIFO / LIFO**：队列在他处已全部贴完后，可在**下一次**他处 **Ctrl+V / Shift+Insert** 时**自动切回「普通」**批量模式（默认开启）；设置中可关闭，避免长期停留在队列模式；兼容旧配置键 **`FifoAutoSwitchToNormalAfterQueueDone`**

## [1.3.3] - 2026-04-09

### 剪贴板

- **Alt 全局热键（VS Code 等）**：呼出/关闭面板时吞物理 Alt KeyUp 并注入 **Ctrl Down → Ctrl Up → Alt Up**；热键关面板时清空 `_ctxAlt*`，避免收尾分支被误判；`BeginInvoke(() => HidePopup())` 修复 **TargetParameterCountException**
- **批量队列**：`TryPushClipboardQueueHeadAsync` 在 `TrySetClipboardAsync` 重试间隙校验 **模式仍为 FIFO/LIFO 且队首未变**，避免切回普通或外部复制后仍被异步写回旧队首导致「粘贴成别的内容」

## [1.3.2] - 2026-04-09

### 剪贴板

- **批量队列修复**：普通模式下 Enter 粘贴选中项而非队列头；队列粘贴失败时恢复队列状态

## [1.3.1] - 2026-04-09

### 文件对话框跳转

- **微信（Weixin）**：`ResolveFileDialogHwndFromWindowOrAncestor` 增加 **`GetLastActivePopup`**，前台仍为应用主窗时也能对齐模态 `#32770`
- **到前台自动执行**：增加 **`EVENT_OBJECT_FOCUS`** 钩 + **`QuickMayBeUnderFileDialog`** 轻量过滤（部分宿主打开对话框时不重复发前台切换事件）
- **首次点击自动跳转**：焦点触发的路径下同步调用 **`UpdateFileJumpClickToNavigateArm`**；前台事件优先对已解析的对话框句柄武装鼠标钩
- **整理**：**`CollectCandidates`** 恢复单一 **Z 序推测（默认 +2）**，移除 **`CollectCandidatesAfterDialogReady`**；多处前台根窗口判断合并为 **`IsForegroundFocusOnFileDialogRoot`**

## [1.3.0] - 2026-04-08

### 剪贴板

- **批量队列**：支持 **普通 / FIFO / LIFO**；多选在 **FIFO/LIFO** 下 **Enter 入队**，在目标应用内每次 **Ctrl+V** / **Shift+Insert** 出队并推进剪贴板；顶栏模式 **Tag**、列表 **序号角标**、**托盘图标** 随模式换色（普通青绿 **#139493**、FIFO 蓝、LIFO 金），托盘叠 **F/L** 标记
- **模式切换**：默认 **Alt+/**（可在设置中修改）在面板外 **循环切换** 批量模式；顺序为 **普通 → LIFO → FIFO → 普通**，与面板顶栏一致
- **主题**：暗色面板贴近 **VS Code Dark+**；弹窗内列表 **选中 / 悬停** 混合色 **随当前批量模式主色**，与顶栏 Tag 统一；亮色仍为浅灰底 + 品牌强调色
- **交互**：底栏 **more** 与顶栏使用相同模式色 **pill**；快捷短语侧栏等延续不抢焦点输入；设置内帮助文案同步

## [1.2.9]

### 剪贴板

- **快捷短语**：设/改短语侧栏改为与主列表相同的 **键盘钩** 输入关键词，不再使用可聚焦 `TextBox`，避免抢宿主输入焦点
- **帮助**：主面板底栏 **more** 打开气泡展示完整快捷键说明（含修饰键组合）；**Esc** 可关闭

### 文件对话框跳转

- 根据前台线程 **GetGUIThreadInfo** 判断焦点；在 **另存为** 等对话框的 **Edit / RichEdit** 文件名框中时 **放行** 按键，便于边开跳转列表边编辑文件名；焦点在跳转面板内时逻辑不变

## [1.2.8]

### 数据与安装

- **单文件 / 绿色 zip**：便携数据根固定为 **`Environment.ProcessPath` 所在目录下的 `Data\`**，不再误写 `%TEMP%\.net\…`；首次启动可合并旧版 Temp 下 `Data`（若仍存在）
- **安装**：「安装到当前用户…」在框架依赖多文件发布时按当前主 exe 名检测主 DLL，剪裁版整机复制不再漏文件

## [1.2.7]

### 安装与数据

- 默认数据根 **`exe\Data\`**；仅当从 **`%LocalAppData%\Programs\ClipboardX`** 启动时使用 **`%LocalAppData%\ClipboardX`**；取消 Release 启动时自动复制到 Programs
- **托盘**：未安装显示「安装到当前用户…」，已安装显示「卸载…」；不依赖 **`ClipboardX.portable`**
- 复制到用户 Programs 时将 **`exe\Data\`** 中目标侧不存在文件合并到用户配置目录
- 按用户安装目录主 exe 名与剪裁版一致；Debug 安装菜单避免 CS0162

## [1.2.6]

### 文件跳转与兼容

- 「切回时自动同步路径」仅在前次前台为可解析路径的外部文件管理器时才用资源管理器目录驱动自动跳转
- 排除 **Internet Download Manager**（`IDMan.exe`）主界面误判为 `#32770` 导致的误触发与卡顿

## [1.2.5]

### 安装包

- Inno 安装前 .NET 8 桌面运行时检测增加与 `dotnet --list-runtimes` 一致的目录与 arm64 注册表后备路径

## [1.2.4]

### 安装包 / CI

- Inno `[Code]` 中函数内不使用 `const` 段，改为 `var`，修复 ISCC 报错

## [1.2.3]

### 安装包 / CI

- Inno `[Tasks]` 移除非法标志 `checked`，修复 ISCC 6.7.x unknown flag

## [1.2.2]

### 安装包 / CI

- 简体中文 `ChineseSimplified.isl` 随仓库分发，避免 Actions 上 Inno 无语言文件导致失败

## [1.2.1]

### 构建与剪裁版

- 修复 **FileJumpOnly** 排除 Sqlite 导致 CI 失败；Release 矩阵关闭 **fail-fast**
- **FileJumpOnly**：不注册剪贴板全局热键、不处理剪贴板消息，避免误占热键

## [1.2.0]

### 文件跳转

- 切回对话框时路径同步与列表刷新优化；快照采用分层短等

### 发布与设置

- 「检查更新」按产品前缀匹配 zip；Inno **setup** 与 **setup-self-contained** 双安装包
- 设置：暗色 **TabControl** 模板，改善标签对比度

## [1.1.6]

### 文件跳转与兼容

- 对话框到前台自动执行；跳转列表贴靠对话框
- 粘贴默认 **Shift+Insert**
- WPS 识别排除误匹配；修复 WPS 粘性跳转死循环

## [1.1.5]

### 其它

- 自定义文件对话框规则（导入/导出/探测向导）
- 剪贴板写入重试与诊断
- 图片粘贴回退为文件列表
- Q-Dir 等路径采集

## 更早版本

见 **[GitHub Releases](https://github.com/chaojimct/clipboardx/releases)** 与仓库 git 历史。
