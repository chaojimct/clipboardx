using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ClipboardX.Migrator;

/// <summary>
/// 迁移向导窗口。
///
/// 刻意用代码搭 UI（不引 XAML）：这个 exe 只在「用户点了老版更新」后被打开一次，
/// 生命周期以秒计，不值得引入 WPF 依赖与一堆 XAML 文件。
/// </summary>
internal sealed class WizardForm : Form
{
    private readonly Label _title = new();
    private readonly Label _body = new();
    private readonly Label _status = new();
    private readonly ProgressBar _bar = new();
    private readonly Button _primary = new();
    private readonly Button _cancel = new();
    private readonly TextBox _log = new();

    private bool _busy;

    /// <summary>自检注入口：由 <c>--demo-busy</c> 置位，见 <see cref="OnShown"/>。</summary>
    public bool DemoBusy { get; set; }

    public WizardForm()
    {
        Text = "ClipboardX 迁移向导";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 420);
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.White;

        _title.Text = "迁移到 clipx";
        _title.Font = new Font(Font.FontFamily, 15f, FontStyle.Bold);
        _title.Location = new Point(24, 20);
        _title.AutoSize = true;

        _body.Text = BuildIntroText();
        _body.Location = new Point(26, 62);
        _body.MaximumSize = new Size(508, 0); // 固定宽度、高度自适应，避免文案被裁
        _body.AutoSize = true;

        _status.Text = "准备就绪。";
        _status.Size = new Size(508, 20);
        _status.ForeColor = Color.FromArgb(70, 70, 70);

        _bar.Size = new Size(508, 14);
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Visible = false;

        _log.Size = new Size(508, 116);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(248, 249, 250);
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 8.5f);
        _log.Visible = false;

        _primary.Text = "开始迁移";
        _primary.Location = new Point(338, 374);
        _primary.Size = new Size(96, 32);
        _primary.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _primary.Click += async (_, _) => await RunMigrationAsync();

        _cancel.Text = "稍后再说";
        _cancel.Location = new Point(440, 374);
        _cancel.Size = new Size(94, 32);
        _cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _cancel.Click += (_, _) => Close();

        Controls.AddRange([_title, _body, _status, _bar, _log, _primary, _cancel]);
    }

    /// <summary>
    /// 把状态行 / 进度条 / 日志区按说明文字的实际高度顺排。
    ///
    /// 必须在 autosize 落定之后调用（OnShown 时机），构造函数里读 <c>_body.Bottom</c>
    /// 拿到的是尚未重算的旧高度，会导致下方控件与说明文字重叠。
    /// </summary>
    private void LayoutBelow()
    {
        var y = _body.Bottom + 8;
        _status.Location = new Point(26, y);
        _bar.Location = new Point(26, _status.Bottom + 4);
        _log.Location = new Point(26, _bar.Bottom + 8);

        // 日志区占满剩余空间，但不越过按钮行；窗体系 FixedDialog，高度必须留在客户区内。
        var avail = _primary.Top - 12 - _log.Top;
        _log.Height = Math.Max(60, Math.Min(avail, ClientSize.Height - _log.Top - 12));
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

        LayoutBelow();

        if (!DemoBusy) return;

        Text = "ClipboardX 迁移向导（自检：迁移中）";
        SetBusy(true);
        _status.Text = "正在停用老版开机自启…";
        _bar.Value = 42;
        Append("· clipx 已安装：C:\\Users\\…\\clipx\\clipx.exe");
        Append("· 已移除开机自启项 ClipboardX");
        Append("· 提示：老版当前仍在运行，自启停用后它不会被关闭 —— 可手动退出。");
        PerformLayout();
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
        _log.Visible = true;
        LayoutBelow(); // 日志区显隐会改变可用高度，重排一次
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
