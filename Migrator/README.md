# ClipboardX 迁移向导（Migrator）

把老版 ClipboardX（WPF）用户顺着**老版自己的更新通道**带到 [clipx](https://github.com/chaojimct/clipx) 的迁移版 launcher。

## 为什么需要它

老版的「检查更新」只查自家仓库的 `releases/latest`。只要在这个仓库发一个 tag 大于 `v1.9.9` 的版本、并把该版本的包换成这个 launcher，老用户点一次「检查更新 → 安装」就会落到迁移向导上——**零学习成本，不用自己找新软件**。

## 它做什么

1. 查 clipx 仓库的 latest release，下载并**静默安装** clipx（clipx 安装器 `PrivilegesRequired=lowest`，不弹 UAC）；
2. 停用老版的**开机自启项**（HKCU Run 值 + 登录计划任务），避免两套剪贴板监听并存、热键相撞、历史双写；
3. 启动 clipx —— 其首启会自动导入老版历史（含 OCR 结果）。

**它绝不做的事**：不删除老版的程序目录，不删除老版的数据目录（`%LocalAppData%\ClipboardX`，历史库在这里）。卸载这件事留给用户走老版卸载器，向导里明确提示**卸载时选「否」保留历史**。

## 老更新器的硬约束（改包前必读）

来自老版 `Services/GitHubUpdateService.cs`，不满足会被拒绝安装：

| 约束 | 说明 |
|---|---|
| 资产命名 | `ClipboardX-<版本>-win-x64-{self-contained,no-runtime}.zip`；主程序名为 `ClipboardX` 时会**排除** `ClipboardX-clipboard-` / `ClipboardX-filejump-` 开头的包 |
| 两种运行形态 | 老用户既可能装 self-contained 也可能装 no-runtime，**两个 zip 都要有**（选包按当前运行形态定优先级） |
| 包内根目录 | 必须有 `ClipboardX.exe`（安装后 `Start-Process <installDir>\ClipboardX.exe`） |
| tag 版本 | 必须 **大于 `1.9.9`**（`IsRemoteNewerThanCurrent` 用 `Version.TryParse` 比较） |
| 精简 flavor | 只用 `clipboard` / `filejump` 版的用户，还需对应前缀的包（包内 exe 需同名） |

## 打包

```powershell
# 在仓库根目录（pwsh 或 Windows PowerShell 5.1 均可）
powershell -File Migrator/build-migrator-package.ps1 -Version 1.9.10
# 演练时省时间（只打 Full 两形态）
powershell -File Migrator/build-migrator-package.ps1 -Version 1.9.10 -SkipFlavors
```

产物落 `Migrator/out/`，每个 zip 内**只有根目录一个 `ClipboardX.exe`**。

## 自检与演练

```powershell
# 只读现状（不改任何状态）
ClipboardX.exe --check

# 无交互跑完整迁移（供脚本演练）
ClipboardX.exe --migrate

# 以「迁移中」形态打开向导（进度条 + 日志可见），供无人值守截图核对排版
ClipboardX.exe --demo-busy

# 按 150% 缩放模拟排版 —— 在 100% 的开发机上重现高分屏效果（v1.9.10 事故的根因就是这个没测）
ClipboardX.exe --demo-scale 150
```

`--check` 输出示例：

```
=== ClipboardX 迁移版 launcher 现状 ===
launcher 版本      : 1.9.11
主屏 DPI           : 144（150%）
主屏工作区         : 1707x1115（逻辑像素）
自身目录（老版）   : C:\Users\<用户>\AppData\Local\Programs\ClipboardX
老版数据目录       : C:\Users\<用户>\AppData\Local\ClipboardX（存在）
老版是否在运行     : False
clipx 安装目录     : C:\Users\<用户>\AppData\Local\clipx
clipx 是否已安装   : True
剩余 Run 值        : []
剩余计划任务       : [ClipboardX_AutoStart, ClipboardX_AutoStart_Dev]
```

## 已知边界

- **提权任务删不掉**：老版在管理员模式下注册的自启任务 `RunLevel=HighestAvailable`，普通权限 `schtasks /Delete` 会返回「拒绝访问」。此时向导会如实报告并给出指引（以管理员身份重跑，或走老版卸载器），**不假装已解决**。`--migrate` 在这种情况下返回退出码 `4`。
- **单文件发布的路径陷阱**：本 exe 以 `PublishSingleFile + IncludeAllContentForSelfExtract` 发布，`AppContext.BaseDirectory` 会指向 `%Temp%\.net\...` 自解压目录。要拿真实安装目录必须用 `Environment.ProcessPath`（`MigratePaths.SelfDir` 已处理）。
- **窗口只夹高度、不夹宽度**：屏幕装不下时会把窗口高度收到工作区内（按钮贴底，永远可点；此时优先收起日志、再牺牲正文中段），但不会缩窄宽度——极窄屏（逻辑宽 < 约 600px）下窗口可能横向超出。
- **跨屏拖动不做像素级重排**：`FixedDialog` 一般不会被拖到别的缩放比例的屏上；真跨屏时靠 `DpiChanged` 事件重排一次，但窗口尺寸由系统给的建议矩形决定，可能与新 DPI 不完全吻合。

## 改动时容易踩的坑（已修，勿踩回去）

1. **不要在构造函数里读 `DemoBusy` / `SimulatedScalePercent`**：`new WizardForm { DemoBusy = x }` 的赋值在构造函数**返回之后**才执行，构造函数里读恒为默认值。任何依赖注入开关的初始化都必须放 `OnShown`。
2. **不要依赖 WinForms 的自动 DPI 缩放**：代码搭的 Form 若不声明 `AutoScaleDimensions`，默认值是 `(0,0)`，`PerformAutoScale` 会**直接跳过**——于是「控件坐标不缩放、字体却按 DPI 放大」，高分屏下文字换行量翻倍、把按钮挤出窗口（v1.9.10 发布事故）。本类一律关掉自动缩放（`AutoScaleMode.None`），所有坐标经 `S()`/`Sz()` 按 `DeviceDpi` 手算。
3. **不要写死窗口高度或控件尺寸**：对话框高度必须由内容算出来（`ApplyLayout()`），按钮尺寸必须按文字实测（`ButtonSize()`）。写死的话，换个 DPI 或换个系统字体就会裁字、溢出。
4. **不要在构造函数里做顺序布局**：`AutoSize` 的高度是**懒计算**的，那时读 `_body.Bottom` 拿到的是未重算的旧高度，会让下方控件与说明文字重叠。布局统一在 `ApplyLayout()` 里做，由 `OnLoad` / `OnShown` 调用。
5. **`ApplyLayout()` 会被调用多次**（OnLoad / OnShown / SetBusy / DpiChanged），其中的字体创建必须带幂等守卫（见 `ApplyFontsForScale`），否则每次重排都漏一个 GDI 字体句柄。
6. **日志区必须只吃剩余空间**：窗口被夹到工作区内时，若日志高度沿用旧值就会与按钮行重叠。现在的规则是「日志放不下就收起」，见 `ApplyLayout()` 里的 `logHeight` 计算。
7. **无控制台时 `Console.OutputEncoding` 可能抛异常**：`--check`/`--migrate` 取 OEM 代码页要用 `Encoding.GetEncoding((int)GetOEMCP())` 并 try/catch 兜底。

