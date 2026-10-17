using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// Windows API P/Invoke 汇集点：kernel32 / user32 / ole32。
    /// 除 7z.dll 自身导出（SevenZipLibrary.CreateObject，属第三方库绑定）外，
    /// 全部操作系统原生调用都收敛在此，避免散落各文件。
    /// </summary>
    internal static class WindowsNativeMethods
    {
        // ---------- DLL 加载（7z.dll 定位与装载） ----------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeLibrary(IntPtr hModule);

        // 注意：GetProcAddress 的 lpProcName 是 ANSI (LPCSTR)，
        // 必须用 Ansi marshal，否则 UTF-16 字符串会在第一个 \0 处被截断成单字符
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetModuleFileName(IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);

        // ---------- 控制台（启动场景判定 / 焦点管理） ----------

        /// <summary>
        /// 列出附加到当前控制台的进程。双击 console 子系统 exe 时 Windows 分配
        /// 专属控制台（返回 1 个进程）；从终端运行时与 shell 共享（≥2 个）。
        /// </summary>
        [DllImport("kernel32.dll")]
        public static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        // ---------- 窗口 ----------

        /// <summary>把指定窗口带到前台（拖放后焦点留在资源管理器，需主动还回）</summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        // ---------- COM（PropVariant 生命周期） ----------

        /// <summary>
        /// 释放 PropVariant 持有的原生资源（BSTR、接口指针等）。
        /// 签名与 PropVariant 结构体耦合，属 COM 结构体协议的一部分
        /// </summary>
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant pvar);
    }
}
