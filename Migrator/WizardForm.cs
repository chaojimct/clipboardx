using System.Drawing;
using System.Windows.Forms;

namespace ClipboardX.Migrator;

/// <summary>
/// 迁移向导窗口。
///
/// 刻意用代码搭 UI（不引 XAML）：这个 exe 只在「用户点了老版更新」后被打开一次，
/// 生命周期以秒计，不值得引入 WPF 依赖与一堆 XAML 文件。
///
/// 布局：所有几何数字一律按 96 dpi 书写，再由 <see cref="S"/>/<see cref="Sz"/> 换算到当前
/// 显示器 DPI；WinForms 自带的自动缩放（<see cref="AutoScaleMode"/>）刻意关掉，理由见构造函数。
/// </summary>
internal sealed class WizardForm : Form
{
    // ---- 布局基准（96 dpi 设计值；不要在这里写死设备像素）----
    private const int PadX = 26;            // 左右留白
    private const int PadTop = 20;          // 标题上边距
    private const int PadBottom = 16;       // 按钮行下边距
    private const int ContentWidth = 508;   // 正文 / 状态行 / 日志区宽度
    private const int Gap = 10;             // 段落间距
    private const int TightGap = 6;         // 行内间距
    private const int BarHeight = 14;
    private const int LogWantedHeight = 116;
    private const int LogMinHeight = 56;
    private const int ButtonMinHeight = 32;   // 低于此时按钮太扁；再高由文字实测决定
    private const int ButtonMinWidthPrimary = 96;
    private const int ButtonMinWidthCancel = 94;
    private const int ButtonPadX = 26;        // 按钮左右内边距（96 dpi 设计值）
    private const int ButtonPadY = 12;
    private const int ButtonGap = 8;
    private const int StatusMinHeight = 20;
    private const int MinClientHeight = 300;
    private const float BaseDpi = 96f;

    private readonly Label _title = new();
    private readonly Label _body = new();
    private readonly Label _status = new();
    private readonly ProgressBar _bar = new();
    private readonly Button _primary = new();
    private readonly Button _cancel = new();
    private readonly TextBox _log = new();

    /// <summary>本类自己创建的字体，随窗体一起释放。</summary>
    private readonly List<Font> _ownedFonts = [];

    private bool _busy;
    private float _appliedFontScale = float.NaN;

    /// <summary>流程是否要求显示日志区（<see cref="SetBusy"/> 置位）。
    /// 实际可见性由可用高度决定 —— 窗口装不下时宁可收起日志，也不能压到按钮。</summary>
    private bool _logRequested;

    /// <summary>自检注入口：由 <c>--demo-busy</c> 置位，见 <see cref="OnShown"/>。</summary>
    public bool DemoBusy { get; set; }

    /// <summary>
    /// 自检注入口：模拟指定 DPI 缩放百分比（如 <c>150</c>），用于在 100% 缩放的机器上
    /// 重现高分屏排版。0 / 负数 = 关闭（用真实显示器 DPI）。
    ///
    /// 之所以要它：真实高分屏才是暴露布局问题的地方，但开发机未必是高分屏 ——
    /// v1.9.10 的发布事故（150% 下按钮被挤出窗口）就因此没在自机被拍出来。
    /// 模拟时连字号一起按比例放大，等价于系统按该 DPI 渲染点值字体的结果。
    /// </summary>
    public int SimulatedScalePercent { get; set; }

    public WizardForm()
    {
        // 布局全部由本类按 DeviceDpi 手算（见 S/Sz），因此关掉 WinForms 的自动 DPI 缩放。
        //
        // 踩过的坑（v1.9.10 发布事故）：代码构建的 Form 若不声明 AutoScaleDimensions，
        // 它的默认值是 (0,0)，WinForms 的 PerformAutoScale 会直接跳过 —— 于是
        // 「控件坐标不缩放、字体却按 DPI 放大」。在 150% 缩放的屏上，正文换行量翻倍、
        // 高度暴涨，把状态行和按钮整个挤出客户区：用户只看到一个文字被裁半截、
        // 一个按钮都没有的窗口（连「稍后再说」都点不到，只能 Alt+F4）。
        //
        // 声明 AutoScaleDimensions=(96,96) + AutoScaleMode.Dpi 也能修，但那要额外确认
        // Min/MaximumSize 是否被一并缩放、字号会不会被二次放大；全部手算更好读、也能自测。
        AutoScaleMode = AutoScaleMode.None;

        Text = "ClipboardX 迁移向导";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;

        // 字体不在构造函数里建：字号要按真实 DPI（或 --demo-scale 模拟值）定，
        // 而那些值到 OnLoad 才可信。见 ApplyFontsForScale()。
        // 先给个近似尺寸，减少首帧跳动；OnLoad 里 ApplyLayout 会按真实 DPI 定稿。
        ClientSize = new Size(ContentWidth + PadX * 2, MinClientHeight);

        _title.Text = "迁移到 clipx";
        _title.AutoSize = true;
        _title.MaximumSize = new Size(ContentWidth, 0);

        _body.Text = BuildIntroText();
        _body.AutoSize = true;

        _status.Text = "准备就绪。";
        _status.AutoSize = false;
        _status.ForeColor = Color.FromArgb(70, 70, 70);

        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Visible = false;

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(248, 249, 250);
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Visible = false;

        _primary.Text = "开始迁移";
        _primary.Click += async (_, _) => await RunMigrationAsync();

        _cancel.Text = "稍后再说";
        _cancel.Click += (_, _) => Close();

        Controls.AddRange([_title, _body, _status, _bar, _log, _primary, _cancel]);

        // 跨不同缩放的显示器拖动时重排（FixedDialog 一般不跨屏拖，但别留破口）。
        DpiChanged += (_, _) => ApplyLayout();
    }

    /// <summary>当前显示器相对设计基准的缩放倍数。</summary>
    private float ScaleFactor
    {
        get
        {
            if (SimulatedScalePercent > 0) return SimulatedScalePercent / 100f;
            var dpi = DeviceDpi;
            return dpi > 0 ? dpi / BaseDpi : 1f;
        }
    }

    /// <summary>把 96 dpi 设计值换算成当前 DPI 的设备像素。</summary>
    private int S(int logical) => (int)Math.Round(logical * ScaleFactor);

    /// <summary>
    /// 按缩放倍数设定各级字号。
    ///
    /// 正常运行时字号写 9pt / 15pt / 8.5pt 不动 —— point 是物理单位，渲染时系统自会
    /// 按显示器 DPI 放大，跟 <see cref="S"/> 的坐标缩放天然同比例。
    /// 只有 <c>--demo-scale</c> 模拟时才把点值乘上倍数，好在 100% 屏上等价重现高分屏观感。
    ///
    /// 带幂等守卫：ApplyLayout 会被多次调用（OnLoad/OnShown/SetBusy/DpiChanged），
    /// 每次都 new Font 会漏 GDI 句柄。
    /// </summary>
    private void ApplyFontsForScale()
    {
        var f = SimulatedScalePercent > 0 ? SimulatedScalePercent / 100f : 1f;
        if (!float.IsNaN(_appliedFontScale) && Math.Abs(f - _appliedFontScale) < 0.001f) return;
        _appliedFontScale = f;

        var bodyFont = new Font("Microsoft YaHei UI", 9f * f);
        var titleFont = new Font("Microsoft YaHei UI", 15f * f, FontStyle.Bold);
        var logFont = new Font("Consolas", 8.5f * f);
        _ownedFonts.AddRange([bodyFont, titleFont, logFont]);

        Font = bodyFont;          // _body / _status / 按钮随继承
        _status.Font = bodyFont;
        _title.Font = titleFont;
        _log.Font = logFont;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var font in _ownedFonts) font.Dispose();
            _ownedFonts.Clear();
        }
        base.Dispose(disposing);
    }

    /// <summary>全部布局在此收口。
    ///
    /// 必须在句柄创建之后调用（<see cref="OnLoad"/> / <see cref="OnShown"/>）：句柄尚未创建时
    /// <see cref="Control.DeviceDpi"/> 拿不到真实屏幕 DPI，换算会退化成 1.0 倍。
    /// </summary>
    private void ApplyLayout()
    {
        ApplyFontsForScale();

        var padX = S(PadX);
        var contentW = S(ContentWidth);

        // 1) 按钮尺寸先定：窗口高度要靠它算，且宽高必须跟随 DPI 与字号。
        //    写死宽高会在高分屏下把按钮文字裁掉（v1.9.11 自检实测），所以按文字实测。
        _primary.Size = ButtonSize(_primary.Text, ButtonMinWidthPrimary);
        _cancel.Size = ButtonSize(_cancel.Text, ButtonMinWidthCancel);

        // 2) 标题 → 正文：正文宽度定死、高度随字体的真实测量值自适应，永远不会被裁。
        _title.MaximumSize = new Size(contentW, 0);
        _title.Location = new Point(padX, S(PadTop));

        _body.MaximumSize = new Size(contentW, 0);
        _body.Location = new Point(padX, _title.Bottom + S(Gap));

        // 3) 状态行贴正文下方。
        _status.Bounds = new Rectangle(
            padX,
            _body.Bottom + S(Gap),
            contentW,
            Math.Max(S(StatusMinHeight), _status.PreferredHeight));

        var y = _status.Bottom + S(TightGap);

        // 4) 进度条只在迁移中出现。
        if (_bar.Visible)
        {
            _bar.Bounds = new Rectangle(padX, y, contentW, S(BarHeight));
            y = _bar.Bottom + S(Gap);
        }

        // 5) 客户区尺寸：宽度固定，高度由内容决定 —— 不写死，任何字体/DPI 下都不会把按钮裁掉。
        var buttonRowH = _primary.Height;
        var heightWithoutLog = y + S(Gap) + buttonRowH + S(PadBottom);
        var wantedH = heightWithoutLog + (_logRequested ? S(Gap) + S(LogWantedHeight) : 0);

        // 上限：工作区的 92%（留出任务栏与窗口外框）。
        // 自检模拟时把可用高度一并放大 —— 模拟的是「物理更大、DPI 更高的屏」，
        // 否则物理屏没变大会误触发夹取，测不出真实高分屏的样子。
        var workHeight = (Screen.FromControl(this) ?? Screen.PrimaryScreen)?.WorkingArea.Height
                         ?? Screen.PrimaryScreen!.WorkingArea.Height;
        if (SimulatedScalePercent > 0) workHeight = (int)(workHeight * ScaleFactor);
        var maxH = Math.Max(S(MinClientHeight), (int)(workHeight * 0.92));

        var clientH = Math.Min(Math.Max(S(MinClientHeight), wantedH), maxH);

        // 日志区只吃「按钮行之上」的剩余空间：窗口被夹时先压日志，绝不与按钮重叠。
        var logHeight = 0;
        if (_logRequested)
        {
            var avail = clientH - S(Gap) - buttonRowH - S(PadBottom) - y;
            if (avail >= S(LogMinHeight)) logHeight = Math.Min(avail, S(LogWantedHeight));
        }

        ClientSize = new Size(contentW + padX * 2, clientH);

        _log.Visible = logHeight > 0;
        if (logHeight > 0)
        {
            _log.Bounds = new Rectangle(padX, y, contentW, logHeight);
        }

        // 6) 按钮行贴客户区右下（长高缩短都不会盖住内容）。
        var btnTop = ClientSize.Height - S(PadBottom) - buttonRowH;
        _cancel.Location = new Point(ClientSize.Width - padX - _cancel.Width, btnTop);
        _primary.Location = new Point(_cancel.Left - S(ButtonGap) - _primary.Width, btnTop);
    }

    /// <summary>按文字实测尺寸定按钮大小（含内边距），宽高都不写死。</summary>
    private Size ButtonSize(string text, int minWidth)
    {
        var measured = TextRenderer.MeasureText(text, Font);
        return new Size(
            Math.Max(S(minWidth), measured.Width + S(ButtonPadX)),
            Math.Max(S(ButtonMinHeight), measured.Height + S(ButtonPadY)));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyLayout();
    }

    /// <summary>
    /// 自检注入：<c>--demo-busy</c> 时以「迁移中」形态呈现（进度条 + 日志可见）。
    ///
    /// 必须放 OnShown 而不是构造函数 —— <c>new WizardForm { DemoBusy = x }</c> 的
    /// 对象初始化器在构造函数返回**之后**才赋值，构造函数里读 DemoBusy 恒为 false。
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 此时窗口已按 StartPosition 定位，Screen.FromControl 才是真实那块屏，重排一次。
        ApplyLayout();

        if (!DemoBusy) return;

        Text = "ClipboardX 迁移向导（自检：迁移中）";
        SetBusy(true);
        _status.Text = "正在停用老版开机自启…";
        _bar.Value = 42;
        Append("· clipx 已安装：C:\\Users\\…\\clipx\\clipx.exe");
        Append("· 已移除开机自启项 ClipboardX");
        Append("· 提示：老版当前仍在运行，自启停用后它不会被关闭 —— 可手动退出。");
        Refresh();
    }

    private static string BuildIntroText() =>
        "老版 ClipboardX（WPF）将由 clipx 接替 —— 界面交互一脉相承，运行更轻。\r\n\r\n" +
        "接下来会自动：\r\n" +
        "  1. 下载并安装 clipx（无需管理员权限）\r\n" +
        "  2. 停用老版的开机自启，避免两套剪贴板监听并存\r\n" +
        "  3. 启动 clipx —— 首次启动会自动导入您的老历史与 OCR 结果\r\n\r\n" +
        "老版的程序与数据都会原样保留，确认新数据无误后，\r\n" +
        "您可以自行运行老版卸载程序（卸载时请选「否」保留历史）。";

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _primary.Enabled = !busy;
        _cancel.Enabled = !busy;
        _bar.Visible = busy;
        _logRequested = true;
        ApplyLayout(); // 进度条 / 日志区显隐会改变所需高度，重排一次
    }

    private void Append(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private async Task RunMigrationAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            // 步骤 1：装 clipx（已装则跳过）
            if (ClipxInstaller.IsClipxInstalled())
            {
                _status.Text = "clipx 已安装，跳过下载。";
                Append("· clipx 已安装：" + MigratePaths.ClipxExe);
            }
            else
            {
                _status.Text = "正在获取 clipx 安装包…";
                Append("· 查询 clipx 最新版本…");
                var asset = await ClipxInstaller.FindSetupAsync();
                if (asset is null)
                {
                    Fail("没有在 clipx 仓库找到安装包（可能是网络问题，或尚未发布安装包）。");
                    return;
                }
                Append($"· 找到 {asset.Name}（{asset.Tag}）");

                var progress = new Progress<(long Got, long? Total)>(p =>
                {
                    if (p.Total is long tot && tot > 0)
                    {
                        var pct = (int)Math.Min(100, p.Got * 100 / tot);
                        _bar.Value = Math.Clamp(pct, 0, 100);
                        _status.Text = $"正在下载 clipx… {p.Got / 1048576.0:F1} / {tot / 1048576.0:F1} MB";
                    }
                    else
                    {
                        _status.Text = $"正在下载 clipx… {p.Got / 1048576.0:F1} MB";
                    }
                });

                _status.Text = "正在下载 clipx…";
                var setup = await ClipxInstaller.DownloadAsync(asset, progress);
                Append("· 下载完成：" + setup);

                _status.Text = "正在安装 clipx…";
                var code = ClipxInstaller.RunSilentInstall(setup);
                Append($"· 安装器退出码 = {code}");
                if (code != 0 || !ClipxInstaller.IsClipxInstalled())
                {
                    Fail("clipx 安装未成功完成。可稍后重试，或到仓库手动下载安装包。");
                    return;
                }
                Append("· clipx 安装完成");
            }

            // 步骤 2：停用老版自启
            _status.Text = "正在停用老版开机自启…";
            if (LegacyCleanup.IsLegacyRunning())
            {
                Append("· 提示：老版当前仍在运行，自启停用后它不会被关闭 —— 可手动退出。");
            }
            var outcome = LegacyCleanup.DisableAutostart();
            if (outcome.RunRemoved.Count > 0)
                Append("· 已移除开机自启项 " + string.Join("、", outcome.RunRemoved));
            if (outcome.TasksRemoved.Count > 0)
                Append("· 已移除登录计划任务 " + string.Join("、", outcome.TasksRemoved));
            foreach (var err in outcome.Errors)
                Append("· " + err);

            // 步骤 3：启动 clipx
            _status.Text = "正在启动 clipx…";
            ClipxInstaller.LaunchClipx();
            Append("· 已启动 clipx（首启会自动导入老历史）");

            _bar.Value = 100;
            if (outcome.TasksFailed.Count > 0)
            {
                // 部分失败：不假装成功，告诉用户怎么补
                _status.Text = "迁移基本完成，但有自启项未能移除。";
                MessageBox.Show(
                    this,
                    "clipx 已安装并启动，但老版的登录计划任务需要管理员权限才能删除。\r\n\r\n" +
                    "可以：以管理员身份打开 PowerShell，执行\r\n" +
                    "  schtasks /Delete /F /TN ClipboardX_AutoStart\r\n\r\n" +
                    "或直接运行老版卸载程序（卸载时选「否」保留历史）。",
                    "部分完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                _status.Text = "迁移完成。clipx 已启动，历史自动导入中。";
                _primary.Text = "完成";
                _primary.Click += (_, _) => Close();
            }
        }
        catch (Exception ex)
        {
            Fail("迁移过程中出错：" + ex.Message);
        }
        finally
        {
            _busy = false;
            _cancel.Enabled = true;
            _cancel.Text = "关闭";
        }
    }

    private void Fail(string message)
    {
        _status.Text = message;
        Append("× " + message);
        MessageBox.Show(this, message, "迁移失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
