using System.Runtime.InteropServices;

namespace Plugin.Engine;

/// <summary>
/// 业务侧独占的 Win32 声明：挂窗口过程、定时器、模块导出查找、原生内存探测。
/// 接入层（GoldSrc.Avalonia）只保留渲染/输入需要的部分，不提供这类"改游戏"原语。
/// </summary>
internal static class Win32
{
    public const int GWL_WNDPROC = -4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongA(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLongA(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CallWindowProcA(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessageA(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

    const uint MEM_COMMIT = 0x1000;
    const uint PAGE_GUARD = 0x100;
    const uint PAGE_NOACCESS = 0x01;
    const uint PAGE_EXECUTE = 0x10;

    /// <summary>整个 [addr, addr+size) 是否已提交且可读（原生指针解引用前的守卫）</summary>
    public static bool IsRangeReadable(IntPtr addr, int size) =>
        CheckRangeProtection(addr, size, requireWrite: false);

    /// <summary>整个 [addr, addr+size) 是否已提交且可写</summary>
    public static bool IsRangeWritable(IntPtr addr, int size) =>
        CheckRangeProtection(addr, size, requireWrite: true);

    static bool CheckRangeProtection(IntPtr addr, int size, bool requireWrite)
    {
        if (addr == IntPtr.Zero || size <= 0)
            return false;
        long pos = addr.ToInt64();
        long end = pos + size;
        int mbiSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        while (pos < end)
        {
            if (VirtualQuery((IntPtr)pos, out var mbi, (IntPtr)mbiSize) == IntPtr.Zero)
                return false;
            if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0)
                return false;
            // 0x10 PAGE_EXECUTE 只可执行不可读写；读写权限页的可写位为 0x04/0x08
            if (mbi.Protect == PAGE_EXECUTE)
                return false;
            if (requireWrite && (mbi.Protect & 0x0C) == 0)
                return false;
            long regionEnd = mbi.BaseAddress.ToInt64() + mbi.RegionSize.ToInt64();
            if (regionEnd <= pos)
                return false;
            pos = Math.Min(regionEnd, end);
        }
        return true;
    }
}
