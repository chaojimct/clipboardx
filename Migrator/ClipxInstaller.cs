using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ClipboardX.Migrator;

/// <summary>
/// 下载并静默安装 clipx。
///
/// clipx 安装器（Inno，scripts/clipx.iss）是 <c>PrivilegesRequired=lowest</c>：
/// 装到 %LocalAppData%\clipx，**静默安装不弹 UAC**。这是能把「一键迁移」做成的原因。
/// </summary>
internal static class ClipxInstaller
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ClipboardX-Migrator/{MigratePaths.MigratorVersion}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>clipx 是否已装（安装目录里有主程序）。</summary>
    public static bool IsClipxInstalled() => File.Exists(MigratePaths.ClipxExe);

    /// <summary>找到的 setup 资产。</summary>
    internal sealed record SetupAsset(string Name, string Url, string Tag);

    /// <summary>
    /// 从 clipx 仓库的 latest release 里挑安装包。
    /// 优先 SelfContained（用户机器可能没有 .NET 桌面运行时），否则退 FDD 版。
    /// </summary>
    public static async Task<SetupAsset?> FindSetupAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{MigratePaths.ClipxRepo}/releases/latest";
        await using var s = await Http.GetStreamAsync(url, ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";

        if (!root.TryGetProperty("assets", out var assets)) return null;

        string? scUrl = null, scName = null, fddUrl = null, fddName = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var dl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dl)) continue;

            // clipx 的资产名形如 clipx-<v>-setup.exe / clipx-<v>-setup-self-contained.exe
            if (!name.StartsWith("clipx-", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith("-setup-self-contained.exe", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;

            if (name.EndsWith("-setup-self-contained.exe", StringComparison.OrdinalIgnoreCase))
            {
                (scUrl, scName) = (dl, name);
            }
            else
            {
                (fddUrl, fddName) = (dl, name);
            }
        }

        if (scUrl is not null) return new SetupAsset(scName!, scUrl, tag);
        if (fddUrl is not null) return new SetupAsset(fddName!, fddUrl, tag);
        return null;
    }

    /// <summary>下载到临时文件（带进度回调）。</summary>
    public static async Task<string> DownloadAsync(
        SetupAsset asset, IProgress<(long Got, long? Total)> progress, CancellationToken ct = default)
    {
        var dest = Path.Combine(Path.GetTempPath(), asset.Name);
        using var resp = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);

        var buf = new byte[81920];
        long got = 0;
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, read), ct);
            got += read;
            progress.Report((got, total));
        }
        return dest;
    }

    /// <summary>
    /// 静默安装 clipx。<c>/SILENT /SUPPRESSMSGBOXES</c> 是 Inno 的标准无人值守参数。
    /// 安装器 PrivilegesRequired=lowest，故不会弹 UAC。
    /// </summary>
    public static int RunSilentInstall(string setupPath)
    {
        var psi = new ProcessStartInfo(setupPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/SILENT");
        psi.ArgumentList.Add("/SUPPRESSMSGBOXES");
        psi.ArgumentList.Add("/NORESTART");

        using var p = Process.Start(psi);
        if (p is null) return -1;
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>启动 clipx（首启会自动导入老版历史）。</summary>
    public static void LaunchClipx()
    {
        if (!File.Exists(MigratePaths.ClipxExe)) return;
        try
        {
            var psi = new ProcessStartInfo(MigratePaths.ClipxExe)
            {
                WorkingDirectory = MigratePaths.ClipxInstallDir,
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动 clipx 失败：{ex.Message}");
        }
    }
}
