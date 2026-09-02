using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClipboardManager;

/// <summary>
/// 将原生 DLL 注入文件对话框<strong>宿主进程</strong>，在目标进程内调用
/// <c>SendMessage(WM_USER+7)</c> 取得 IShellBrowser 并 <c>BrowseObject</c>，
/// 实现 Shell 命名空间级目录切换（不模拟键盘）。失败时由 <see cref="FileDialogJumpHelper"/> 回退。
/// 支持 64→32 跨架构注入（通过远程 PE 导出表解析）。
/// </summary>
internal static class ShellDialogDeepNavigate
{
    private const string ExportName = "ClipboardX_RemoteNavigate";
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint ListModulesAll = 0x03;

    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;

    /// <summary>路径 WCHAR[520]，与 native NavigatePayload.path 一致。</summary>
    private const int MaxPathChars = 520;

    private const uint ExitCodeBusy = 0x800700AA;
    private const uint ExitCodeEFail = 0x80004005;

    public static bool TryBrowseObjectInject(IntPtr dialogHwnd, string folderPath)
    {
        if (dialogHwnd == IntPtr.Zero)
        {
            ShellNavigateLog.WriteInjector("aborted: dialog HWND zero");
            return false;
        }
        if (!Directory.Exists(folderPath))
        {
            ShellNavigateLog.WriteInjector($"aborted: folder not exists: {folderPath}");
            return false;
        }
        if (!Win32.IsWindow(dialogHwnd))
        {
            ShellNavigateLog.WriteInjector($"aborted: invalid window 0x{dialogHwnd.ToInt64():X}");
            return false;
        }

        Win32.GetWindowThreadProcessId(dialogHwnd, out var pid);
        if (pid == 0)
        {
            ShellNavigateLog.WriteInjector("aborted: pid=0");
            return false;
        }

        var access = ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite;
        var hProcess = OpenProcess(access, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            ShellNavigateLog.WriteInjectorWin32($"OpenProcess pid={pid} failed", Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            var targetIs64 = IsTargetProcess64Bit(hProcess);

            // 32→64 跨架构注入不支持（需要 Heaven's Gate 技术）
            if (!Environment.Is64BitProcess && targetIs64)
            {
                ShellNavigateLog.WriteInjector(
                    $"32→64 injection not supported (ClipboardX x86, target pid={pid} x64)");
                return false;
            }

            var crossArch = Environment.Is64BitProcess != targetIs64;
            var dllFileName = targetIs64 ? "ClipboardXShellNavigate.dll" : "ClipboardXShellNavigate32.dll";
            var dllFullPath = Path.Combine(AppContext.BaseDirectory, dllFileName);
            if (!File.Exists(dllFullPath))
            {
                ShellNavigateLog.WriteInjector($"missing DLL: {dllFullPath}");
                return false;
            }

            ShellNavigateLog.WriteInjector(
                $"start pid={pid} hwnd=0x{dialogHwnd.ToInt64():X} dll={dllFileName} cross={crossArch} path={folderPath}");

            if (!EnsureRemoteDllLoaded(hProcess, dllFullPath, dllFileName, crossArch, out var remoteBase))
            {
                ShellNavigateLog.WriteInjectorWin32(
                    "EnsureRemoteDllLoaded failed", Marshal.GetLastWin32Error());
                return false;
            }

            ShellNavigateLog.WriteInjector($"remote module 0x{remoteBase.ToInt64():X}");

            var remoteFn = crossArch
                ? FindExportInRemoteModule(hProcess, remoteBase, ExportName)
                : GetRemoteExportAddress(hProcess, remoteBase, dllFullPath, ExportName);

            // x86 __stdcall 修饰名回退：_FuncName@参数字节数
            if (remoteFn == IntPtr.Zero && crossArch)
                remoteFn = FindExportInRemoteModule(hProcess, remoteBase, $"_{ExportName}@4");

            if (remoteFn == IntPtr.Zero)
            {
                ShellNavigateLog.WriteInjectorWin32("GetRemoteExportAddress failed", Marshal.GetLastWin32Error());
                return false;
            }

            ShellNavigateLog.WriteInjector($"remote {ExportName} 0x{remoteFn.ToInt64():X}");

            var payload = BuildPayload(dialogHwnd, Path.GetFullPath(folderPath), targetIs64);
            var pPayload = VirtualAllocEx(hProcess, IntPtr.Zero, (nuint)payload.Length, MemCommit | MemReserve, PageReadWrite);
            if (pPayload == IntPtr.Zero)
            {
                ShellNavigateLog.WriteInjectorWin32("VirtualAllocEx payload failed", Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                if (!WriteProcessMemory(hProcess, pPayload, payload, (nuint)payload.Length, out _))
                {
                    ShellNavigateLog.WriteInjectorWin32("WriteProcessMemory payload failed", Marshal.GetLastWin32Error());
                    return false;
                }

                var ok = InvokeRemoteNavigateWithRetry(hProcess, remoteFn, pPayload);
                if (ok)
                    ShellNavigateLog.WriteInjector("remote thread OK (see native lines in same log file)");
                return ok;
            }
            finally
            {
                VirtualFreeEx(hProcess, pPayload, 0, MemRelease);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// 注入式导航的 busy/E_FAIL 重试：native 侧每次只做单次 BrowseObject（不能在宿主
    /// UI 线程上 Sleep），视图未就绪时由本方法在调用方后台线程退避后重新拉起远程线程。
    /// 远程线程等待超时说明宿主 UI 线程未泵消息（可能卡死），此时直接放弃注入。
    /// </summary>
    private static bool InvokeRemoteNavigateWithRetry(IntPtr hProcess, IntPtr remoteFn, IntPtr pPayload)
    {
        uint[] retryDelaysMs = { 0, 150, 300, 500, 800, 1200 };

        for (var attempt = 0; attempt < retryDelaysMs.Length; attempt++)
        {
            if (retryDelaysMs[attempt] > 0)
                Thread.Sleep((int)retryDelaysMs[attempt]);

            var t = CreateRemoteThread(hProcess, IntPtr.Zero, 0, remoteFn, pPayload, 0, IntPtr.Zero);
            if (t == IntPtr.Zero)
            {
                ShellNavigateLog.WriteInjectorWin32("CreateRemoteThread failed", Marshal.GetLastWin32Error());
                return false;
            }
            try
            {
                var wr = WaitForSingleObject(t, 8000);
                if (wr != 0)
                {
                    ShellNavigateLog.WriteInjector(
                        $"remote navigate wait not signaled (0x{wr:X}); host UI thread likely stuck, giving up");
                    return false;
                }
                GetExitCodeThread(t, out var code);
                if (code == 0)
                    return true;

                ShellNavigateLog.WriteInjector($"remote navigate attempt {attempt} exit code={code} (0x{code:X})");
                var retryable = code == ExitCodeBusy || code == ExitCodeEFail;
                if (!retryable) return false;
            }
            finally
            {
                CloseHandle(t);
            }
        }
        return false;
    }

    /// <summary>
    /// 通过 DLL 注入在对话框宿主进程内调用 IShellBrowser 读取当前文件夹路径。
    /// 这是读取文件对话框路径最可靠的方式，不受 UIA/CDM 限制。
    /// </summary>
    public static bool TryReadCurrentFolderInject(IntPtr dialogHwnd, out string folder)
    {
        folder = "";
        if (dialogHwnd == IntPtr.Zero || !Win32.IsWindow(dialogHwnd)) return false;

        Win32.GetWindowThreadProcessId(dialogHwnd, out var pid);
        if (pid == 0) return false;

        const string readExportName = "ClipboardX_RemoteReadCurrentFolder";
        var access = ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite;
        var hProcess = OpenProcess(access, false, pid);
        if (hProcess == IntPtr.Zero) return false;

        try
        {
            var targetIs64 = IsTargetProcess64Bit(hProcess);
            if (!Environment.Is64BitProcess && targetIs64) return false;

            var crossArch = Environment.Is64BitProcess != targetIs64;
            var dllFileName = targetIs64 ? "ClipboardXShellNavigate.dll" : "ClipboardXShellNavigate32.dll";
            var dllFullPath = Path.Combine(AppContext.BaseDirectory, dllFileName);
            if (!File.Exists(dllFullPath)) return false;

            if (!EnsureRemoteDllLoaded(hProcess, dllFullPath, dllFileName, crossArch, out var remoteBase))
                return false;

            var remoteFn = crossArch
                ? FindExportInRemoteModule(hProcess, remoteBase, readExportName)
                : GetRemoteExportAddress(hProcess, remoteBase, dllFullPath, readExportName);
            if (remoteFn == IntPtr.Zero) return false;

            // ReadPayload: HWND + WCHAR[520]
            var payload = BuildReadPayload(dialogHwnd, targetIs64);
            var pPayload = VirtualAllocEx(hProcess, IntPtr.Zero, (nuint)payload.Length, MemCommit | MemReserve, PageReadWrite);
            if (pPayload == IntPtr.Zero) return false;

            try
            {
                if (!WriteProcessMemory(hProcess, pPayload, payload, (nuint)payload.Length, out _))
                    return false;

                var t = CreateRemoteThread(hProcess, IntPtr.Zero, 0, remoteFn, pPayload, 0, IntPtr.Zero);
                if (t == IntPtr.Zero) return false;
                try
                {
                    WaitForSingleObject(t, 10000);
                    GetExitCodeThread(t, out var code);
                    if (code != 0) return false;

                    // 读回 payload 中的 path 字段
                    var result = new byte[payload.Length];
                    UIntPtr read = 0;
                    if (!ReadProcessMemory(hProcess, pPayload, result, (nuint)result.Length, out read))
                        return false;

                    // path 偏移：64位下 8 字节 HWND，32位下 4 字节 HWND
                    var pathOffset = targetIs64 ? 8 : 4;
                    folder = Encoding.Unicode.GetString(result, pathOffset, (MaxPathChars - 1) * 2).TrimEnd('\0');
                    return !string.IsNullOrEmpty(folder);
                }
                finally
                {
                    CloseHandle(t);
                }
            }
            finally
            {
                VirtualFreeEx(hProcess, pPayload, 0, MemRelease);
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static byte[] BuildReadPayload(IntPtr hwnd, bool ptr64)
    {
        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        if (ptr64)
            bw.Write(hwnd.ToInt64());
        else
            bw.Write(hwnd.ToInt32());

        // WCHAR[520] 零初始化
        var total = ptr64 ? 8 + MaxPathChars * 2 : 4 + MaxPathChars * 2;
        while (ms.Length < total)
            bw.Write((byte)0);

        return ms.ToArray();
    }

    private static bool IsTargetProcess64Bit(IntPtr hProcess)
    {
        if (!Environment.Is64BitOperatingSystem)
            return false;
        if (!IsWow64Process(hProcess, out var targetWow))
            return Environment.Is64BitProcess;
        return !targetWow;
    }

    private static byte[] BuildPayload(IntPtr hwnd, string fullPath, bool ptr64)
    {
        var pathUtf16 = Encoding.Unicode.GetBytes(fullPath);
        if (pathUtf16.Length > (MaxPathChars - 1) * 2)
            Array.Resize(ref pathUtf16, (MaxPathChars - 1) * 2);

        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        if (ptr64)
            bw.Write(hwnd.ToInt64());
        else
            bw.Write(hwnd.ToInt32());

        bw.Write(pathUtf16);
        bw.Write((ushort)0);

        var total = ptr64 ? 8 + MaxPathChars * 2 : 4 + MaxPathChars * 2;
        while (ms.Length < total)
            bw.Write((byte)0);

        return ms.ToArray();
    }

    private static bool EnsureRemoteDllLoaded(
        IntPtr hProcess, string dllFullPath, string dllFileName, bool crossArch, out IntPtr remoteBase)
    {
        remoteBase = FindRemoteModule(hProcess, dllFileName);
        if (remoteBase != IntPtr.Zero)
        {
            ShellNavigateLog.WriteInjector($"DLL already loaded in target: 0x{remoteBase.ToInt64():X}");
            return true;
        }

        var pathBytes = Encoding.Unicode.GetBytes(dllFullPath + "\0");
        var pPath = VirtualAllocEx(hProcess, IntPtr.Zero, (nuint)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);
        if (pPath == IntPtr.Zero)
        {
            ShellNavigateLog.WriteInjectorWin32("VirtualAllocEx(dll path) failed", Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            if (!WriteProcessMemory(hProcess, pPath, pathBytes, (nuint)pathBytes.Length, out _))
            {
                ShellNavigateLog.WriteInjectorWin32("WriteProcessMemory(dll path) failed", Marshal.GetLastWin32Error());
                return false;
            }

            var loadLib = crossArch
                ? FindExportInRemoteModule(hProcess, FindRemoteModule(hProcess, "kernel32.dll"), "LoadLibraryW")
                : GetRemoteKernelProcAddress(hProcess, "LoadLibraryW");
            if (loadLib == IntPtr.Zero)
            {
                ShellNavigateLog.WriteInjector("Cannot find LoadLibraryW in remote process");
                return false;
            }

            var t = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLib, pPath, 0, IntPtr.Zero);
            if (t == IntPtr.Zero)
            {
                ShellNavigateLog.WriteInjectorWin32("CreateRemoteThread(LoadLibraryW) failed", Marshal.GetLastWin32Error());
                return false;
            }
            try
            {
                WaitForSingleObject(t, 20000);
                GetExitCodeThread(t, out var exit);
                if (exit == 0)
                {
                    ShellNavigateLog.WriteInjector($"remote LoadLibraryW returned NULL for: {dllFullPath}");
                    return false;
                }
                ShellNavigateLog.WriteInjector($"remote LoadLibraryW exit/HMODULE=0x{exit:X}");
            }
            finally
            {
                CloseHandle(t);
            }
        }
        finally
        {
            VirtualFreeEx(hProcess, pPath, 0, MemRelease);
        }

        remoteBase = FindRemoteModule(hProcess, dllFileName);
        if (remoteBase == IntPtr.Zero)
            ShellNavigateLog.WriteInjector($"FindRemoteModule after load: not found {dllFileName}");
        return remoteBase != IntPtr.Zero;
    }

    /// <summary>
    /// 从远程进程模块的 PE 导出表中查找函数地址（支持跨架构，即 64 位进程读 32 位模块）。
    /// </summary>
    private static IntPtr FindExportInRemoteModule(IntPtr hProcess, IntPtr moduleBase, string exportName)
    {
        if (moduleBase == IntPtr.Zero) return IntPtr.Zero;

        var dosHeader = new byte[64];
        if (!ReadProcessMemory(hProcess, moduleBase, dosHeader, (nuint)dosHeader.Length, out _))
            return IntPtr.Zero;
        if (dosHeader[0] != 0x4D || dosHeader[1] != 0x5A)
            return IntPtr.Zero;

        var e_lfanew = BitConverter.ToInt32(dosHeader, 60);
        var peAddr = (IntPtr)(moduleBase.ToInt64() + e_lfanew);

        // PE sig(4) + COFF(20) + OptionalHeader 足够覆盖 DataDirectory[0]
        var peHeader = new byte[264];
        if (!ReadProcessMemory(hProcess, peAddr, peHeader, (nuint)peHeader.Length, out _))
            return IntPtr.Zero;
        if (peHeader[0] != 0x50 || peHeader[1] != 0x45)
            return IntPtr.Zero;

        var magic = BitConverter.ToUInt16(peHeader, 24);
        int exportDirOff = magic switch
        {
            0x10B => 24 + 96,   // PE32
            0x20B => 24 + 112,  // PE32+
            _ => -1
        };
        if (exportDirOff < 0) return IntPtr.Zero;

        var exportRva = BitConverter.ToUInt32(peHeader, exportDirOff);
        if (exportRva == 0) return IntPtr.Zero;

        var exportDir = new byte[40]; // IMAGE_EXPORT_DIRECTORY
        if (!ReadProcessMemory(hProcess, (IntPtr)(moduleBase.ToInt64() + exportRva), exportDir, (nuint)exportDir.Length, out _))
            return IntPtr.Zero;

        var numberOfNames = BitConverter.ToInt32(exportDir, 24);
        var addrOfFunctions = BitConverter.ToUInt32(exportDir, 28);
        var addrOfNames = BitConverter.ToUInt32(exportDir, 32);
        var addrOfOrdinals = BitConverter.ToUInt32(exportDir, 36);
        if (numberOfNames == 0) return IntPtr.Zero;

        var nameRvas = new byte[numberOfNames * 4];
        if (!ReadProcessMemory(hProcess, (IntPtr)(moduleBase.ToInt64() + addrOfNames), nameRvas, (nuint)nameRvas.Length, out _))
            return IntPtr.Zero;

        var ordinals = new byte[numberOfNames * 2];
        if (!ReadProcessMemory(hProcess, (IntPtr)(moduleBase.ToInt64() + addrOfOrdinals), ordinals, (nuint)ordinals.Length, out _))
            return IntPtr.Zero;

        var wantBytes = Encoding.ASCII.GetBytes(exportName);
        var nameBuf = new byte[256];
        for (var i = 0; i < numberOfNames; i++)
        {
            var nameRva = BitConverter.ToUInt32(nameRvas, i * 4);
            if (!ReadProcessMemory(hProcess, (IntPtr)(moduleBase.ToInt64() + nameRva), nameBuf, (nuint)nameBuf.Length, out _))
                continue;
            var nullIdx = Array.IndexOf(nameBuf, (byte)0);
            if (nullIdx < 0) nullIdx = nameBuf.Length;
            if (nullIdx != wantBytes.Length || !nameBuf.AsSpan(0, nullIdx).SequenceEqual(wantBytes))
                continue;

            var ordinal = BitConverter.ToUInt16(ordinals, i * 2);
            var funcRvaBytes = new byte[4];
            if (!ReadProcessMemory(hProcess, (IntPtr)(moduleBase.ToInt64() + addrOfFunctions + ordinal * 4), funcRvaBytes, 4, out _))
                return IntPtr.Zero;
            var funcRva = BitConverter.ToUInt32(funcRvaBytes, 0);
            return (IntPtr)(moduleBase.ToInt64() + funcRva);
        }

        return IntPtr.Zero;
    }

    /// <summary>同架构快速路径：本地加载 DLL 计算 RVA。</summary>
    private static IntPtr GetRemoteExportAddress(IntPtr hProcess, IntPtr remoteModule, string localDllPath, string exportName)
    {
        var localMod = LoadLibraryW(localDllPath);
        if (localMod == IntPtr.Zero)
            return IntPtr.Zero;
        try
        {
            var localFn = GetProcAddress(localMod, exportName);
            if (localFn == IntPtr.Zero)
                return IntPtr.Zero;
            var rva = localFn.ToInt64() - localMod.ToInt64();
            return (IntPtr)(remoteModule.ToInt64() + rva);
        }
        finally
        {
            FreeLibrary(localMod);
        }
    }

    /// <summary>同架构快速路径：本地 kernel32 RVA 映射到远程。</summary>
    private static IntPtr GetRemoteKernelProcAddress(IntPtr hProcess, string procName)
    {
        var localK = GetModuleHandleW("kernel32.dll");
        var remoteK = FindRemoteModule(hProcess, "kernel32.dll");
        if (localK == IntPtr.Zero || remoteK == IntPtr.Zero) return IntPtr.Zero;
        var localFn = GetProcAddress(localK, procName);
        if (localFn == IntPtr.Zero) return IntPtr.Zero;
        var rva = localFn.ToInt64() - localK.ToInt64();
        return (IntPtr)(remoteK.ToInt64() + rva);
    }

    private static IntPtr FindRemoteModule(IntPtr hProcess, string fileName)
    {
        var want = fileName.ToLowerInvariant();
        var mods = new IntPtr[4096];
        if (!EnumProcessModulesEx(hProcess, mods, mods.Length * IntPtr.Size, out var written, ListModulesAll))
            return IntPtr.Zero;

        var n = written / IntPtr.Size;
        var sb = new StringBuilder(2048);
        for (var i = 0; i < n; i++)
        {
            if (mods[i] == IntPtr.Zero) continue;
            sb.Clear();
            if (Win32.GetModuleFileNameEx(hProcess, mods[i], sb, sb.Capacity) == 0)
                continue;
            var fn = Path.GetFileName(sb.ToString()).ToLowerInvariant();
            if (fn == want)
                return mods[i];
        }
        return IntPtr.Zero;
    }

    #region Native

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, nuint nSize, out UIntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, nuint nSize, out UIntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, nuint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, int cb, out int lpcbNeeded, uint dwFilterFlag);

    #endregion
}
