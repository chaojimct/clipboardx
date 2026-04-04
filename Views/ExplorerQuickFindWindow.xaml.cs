using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Brush = System.Windows.Media.Brush;

namespace ClipboardManager;

/// <summary>
/// 资源管理器上下文中 Everything 筛选结果浮层（不抢焦点；键盘由低级钩下发）。
/// 视觉与「文件对话框跳转」浮层共用 SharedPopupStyles。
/// 窗口采用 Hide/Show 复用，避免每次会话重建开销。
/// </summary>
public partial class ExplorerQuickFindWindow : Window
{
    private Brush? _primaryBrush;
    private Brush? _secondaryBrush;
    private Brush? _mutedBrush;

    private const string DefaultHint = "↑↓ 选择 · ←→ 翻页 · Ctrl+N 快选 · Enter 定位 · Esc 关闭";

    public ExplorerQuickFindWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CacheBrushes();
        Closed += (_, _) => UserClosed?.Invoke(this, EventArgs.Empty);
    }

    private void CacheBrushes()
    {
        _primaryBrush ??= (Brush)FindResource("PrimaryText");
        _secondaryBrush ??= (Brush)FindResource("SecondaryText");
        _mutedBrush ??= (Brush)FindResource("MutedText");
    }

    public event EventHandler? UserClosed;

    /// <summary>用户在列表中点击了某项，携带完整路径。</summary>
    public event Action<string>? ItemActivated;

    public void SetQueryText(string folderLabel, string typing)
    {
        FolderLabel.Text = folderLabel;
        TypingLabel.Text = string.IsNullOrEmpty(typing) ? " " : typing;
    }

    public void SetResults(IReadOnlyList<QuickFindResultItem> items, string? status)
    {
        CacheBrushes();
        var primary = _primaryBrush!;
        var secondary = _secondaryBrush!;
        var muted = _mutedBrush!;

        ResultsList.Items.Clear();

        for (int idx = 0; idx < items.Count; idx++)
        {
            var item = items[idx];

            var tb = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (idx < 9)
                tb.Inlines.Add(new Run($"{idx + 1} ") { Foreground = muted, FontSize = 10 });

            tb.Inlines.Add(new Run(item.IsDirectory ? "\uD83D\uDCC1 " : "\uD83D\uDCC4 ") { FontSize = 12 });
            tb.Inlines.Add(new Run(item.FileName) { Foreground = primary, FontSize = 13 });

            if (!string.IsNullOrEmpty(item.RelativePath)
                && !string.Equals(item.RelativePath, item.FileName, StringComparison.OrdinalIgnoreCase))
            {
                tb.Inlines.Add(new Run($"  {item.RelativePath}")
                {
                    Foreground = secondary,
                    FontSize = 11,
                });
            }

            ResultsList.Items.Add(new ListBoxItem
            {
                Content = tb,
                ToolTip = item.FullPath,
                Tag = item.FullPath,
            });
        }

        if (ResultsList.Items.Count > 0)
            ResultsList.SelectedIndex = 0;

        CountLabel.Text = items.Count > 0 ? $"{items.Count} 项" : "";
        HintLabel.Text = string.IsNullOrEmpty(status) ? DefaultHint : status;
    }

    public void MoveSelection(int delta)
    {
        if (ResultsList.Items.Count == 0) return;
        var i = ResultsList.SelectedIndex;
        if (i < 0) i = 0;
        i = Math.Clamp(i + delta, 0, ResultsList.Items.Count - 1);
        ResultsList.SelectedIndex = i;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    public void MoveSelectionPage(int direction)
    {
        if (ResultsList.Items.Count == 0) return;
        const int pageSize = 8;
        MoveSelection(direction * pageSize);
    }

    public void MoveSelectionToEnd(bool last)
    {
        if (ResultsList.Items.Count == 0) return;
        ResultsList.SelectedIndex = last ? ResultsList.Items.Count - 1 : 0;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    public string? GetSelectedFullPath()
    {
        if (ResultsList.SelectedItem is ListBoxItem { Tag: string s } && !string.IsNullOrEmpty(s))
            return s;
        return null;
    }

    public string? GetFullPathByIndex(int index)
    {
        if (index < 0 || index >= ResultsList.Items.Count) return null;
        if (ResultsList.Items[index] is ListBoxItem { Tag: string s } && !string.IsNullOrEmpty(s))
            return s;
        return null;
    }

    public void PositionNearExplorer(IntPtr explorerHwnd)
    {
        if (explorerHwnd == IntPtr.Zero || !Win32.IsWindow(explorerHwnd))
        {
            PositionNearCursor();
            return;
        }

        if (!Win32.GetWindowRect(explorerHwnd, out var rc))
        {
            PositionNearCursor();
            return;
        }

        var hMon = Win32.MonitorFromWindow(explorerHwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(hMon, ref mi);

        var src = PresentationSource.FromVisual(this);
        double dipScale = 1;
        if (src?.CompositionTarget != null)
            dipScale = src.CompositionTarget.TransformFromDevice.M11;
        if (dipScale <= 0) dipScale = 1;

        double winW = ActualWidth > 0 ? ActualWidth : Width;
        double winH = ActualHeight > 0 ? ActualHeight : MaxHeight;

        double explorerRight = rc.Right * dipScale;
        double explorerLeft = rc.Left * dipScale;
        double explorerTop = rc.Top * dipScale;
        double explorerCenterY = (rc.Top + rc.Bottom) / 2.0 * dipScale;
        double screenRight = mi.rcWork.Right * dipScale;
        double screenLeft = mi.rcWork.Left * dipScale;

        double x;
        if (explorerRight + winW + 8 <= screenRight)
            x = explorerRight + 4;
        else if (explorerLeft - winW - 8 >= screenLeft)
            x = explorerLeft - winW - 4;
        else
            x = Math.Max(screenLeft, screenRight - winW - 16);

        double y = Math.Max(explorerTop, explorerCenterY - winH / 2);

        Left = x;
        Top = y;
    }

    private void PositionNearCursor()
    {
        Win32.GetCursorPos(out var pt);
        var src = PresentationSource.FromVisual(this);
        double dipScale = 1;
        if (src?.CompositionTarget != null)
            dipScale = src.CompositionTarget.TransformFromDevice.M11;
        if (dipScale <= 0) dipScale = 1;

        Left = pt.X * dipScale;
        Top = (pt.Y + 24) * dipScale;
    }

    private void ResultsList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var path = GetSelectedFullPath();
        if (!string.IsNullOrEmpty(path))
            ItemActivated?.Invoke(path!);
    }
}

public sealed class QuickFindResultItem
{
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public bool IsDirectory { get; init; }

    public static List<QuickFindResultItem> FromFullPaths(IReadOnlyList<string> paths, string baseFolder)
    {
        var baseTrimmed = baseFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var list = new List<QuickFindResultItem>(paths.Count);
        foreach (var p in paths)
        {
            var trimmed = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = trimmed;

            string rel;
            if (trimmed.StartsWith(baseTrimmed + "\\", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(baseTrimmed + "/", StringComparison.OrdinalIgnoreCase))
                rel = trimmed[(baseTrimmed.Length + 1)..];
            else
                rel = name;

            var isDir = Directory.Exists(p);

            list.Add(new QuickFindResultItem
            {
                FullPath = p,
                FileName = name,
                RelativePath = rel,
                IsDirectory = isDir,
            });
        }

        // 当前目录直属文件排最前，子目录文件按路径深度排后
        list.Sort((a, b) =>
        {
            int depthA = a.RelativePath.Count(c => c is '\\' or '/');
            int depthB = b.RelativePath.Count(c => c is '\\' or '/');
            if (depthA != depthB) return depthA.CompareTo(depthB);
            return string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
        });

        return list;
    }
}
