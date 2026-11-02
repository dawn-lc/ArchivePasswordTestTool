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

        // ---------- 控制台（启动场景判定 / 焦点管理 / 代码页与字体） ----------

        /// <summary>
        /// 列出附加到当前控制台的进程。双击 console 子系统 exe 时 Windows 分配
        /// 专属控制台（返回 1 个进程）；从终端运行时与 shell 共享（≥2 个）。
        /// </summary>
        [DllImport("kernel32.dll")]
        public static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        /// <summary>UTF-8 代码页（SetConsoleCP / SetConsoleOutputCP 使用）</summary>
        private const uint Utf8CodePage = 65001;

        /// <summary>标准输出句柄编号（GetStdHandle 使用）</summary>
        private const int StdOutputHandle = -11;

        /// <summary>
        /// 控制台字体描述（CONSOLE_FONT_INFO_EX）。FaceName 是内联的 WCHAR[32]，
        /// 必须用定长缓冲而不是 string + ByValTStr：后者的封送依赖运行时元数据，
        /// Native AOT 发布时会引入动态封送路径（本项目对外发布的就是 AOT 产物）。
        /// </summary>
        private unsafe struct ConsoleFontInfo
        {
            public uint Size;
            public uint Index;
            public short Width;
            public short Height;
            public int Family;
            public int Weight;
            public fixed char FaceName[32];
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleOutputCP(uint codePage);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCP(uint codePage);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int stdHandle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCurrentConsoleFontEx(
            IntPtr consoleOutput,
            [MarshalAs(UnmanagedType.Bool)] bool maximumWindow,
            ref ConsoleFontInfo fontInfo);

        /// <summary>
        /// 把控制台输入/输出代码页切换为 UTF-8。控制台默认跟随系统 ANSI 代码页
        /// （中文 Windows = 936/GBK），界面里非该代码页的字符会被编成 '?'。
        /// 宿主不允许时返回 false，不是错误。
        /// </summary>
        public static bool TryEnableUtf8Console()
        {
            // 两个调用都执行：不能因为输出失败就跳过输入
            bool output = SetConsoleOutputCP(Utf8CodePage);
            bool input = SetConsoleCP(Utf8CodePage);
            return output && input;
        }

        /// <summary>
        /// 候选字体，按优先级排列。只取微软官方 Windows 字体清单主表（而非按需语言包）
        /// 中的等宽字体，保证不随系统语言变化。
        /// </summary>
        /// <remarks>
        /// 实测（简体中文 Windows，控制台 120x30）：
        /// <code>
        /// 字体              ± → … · °   中文   制表符
        /// Consolas          1 1 1 1 1    2     1
        /// Lucida Console    1 1 1 1 1    2     1
        /// Courier New       1 1 1 1 1    2     1
        /// MS Gothic         2 2 2 1 2    2     1   ← 歧义宽算两格，不可用
        /// 新宋体 (NSimSun)   2 2 2 2 2    2     1   ← 中文 Windows 默认字体，正是问题来源
        /// </code>
        /// 拉丁字体没有 CJK 字形，中文由 conhost 自行回退到系统 CJK 字体渲染，
        /// 因此换字体不会让中文变方块。Cascadia Mono 同样合格，但只在 Windows 11
        /// 随系统提供，故不作首选。
        /// </remarks>
        private static readonly string[] PreferredConsoleFonts = ["Consolas", "Lucida Console", "Courier New"];

        /// <summary>
        /// 确认终端把「歧义宽度」字符（<c>± → … · °</c>）当成一格，必要时换成拉丁等宽字体。
        /// </summary>
        /// <remarks>
        /// Spectre.Console 按 Unicode East Asian Width 计算列宽，把 Ambiguous 类字符算作一格；
        /// 而东亚语言的默认控制台字体（简体中文 Windows 上是新宋体 8x16）把它们画成两格。
        /// 这个差异会让 Spectre 认为「正好铺满一行」的内容在屏幕上多出一两格并折行，
        /// 备用缓冲区里 Live/Progress 的光标回退量随即与实际不符，画面逐帧错位。
        ///
        /// 程序无法改变字形宽度，只能换字体，所以这里用「写一个 ± 看光标前进几格」实测，
        /// 已经是一格就不动（不覆盖用户调好的字体）。
        /// </remarks>
        /// <returns>切换成功或本来就不需要切换时为 <see langword="true" />。</returns>
        public static bool EnsureNarrowAmbiguousWidth(short fontSizeY = 18)
        {
            // 输出被重定向时没有格子概念，也改不了字体。
            if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected)
            {
                return true;
            }

            if (MeasureAmbiguousWidth() <= 1)
            {
                return true;
            }

            foreach (string font in PreferredConsoleFonts)
            {
                if (TrySetConsoleFont(font, fontSizeY) && MeasureAmbiguousWidth() <= 1)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>设置控制台字体（SetCurrentConsoleFontEx）。宿主不允许时返回 false。</summary>
        private static unsafe bool TrySetConsoleFont(string fontName, short fontSizeY)
        {
            try
            {
                IntPtr handle = GetStdHandle(StdOutputHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return false;
                }

                var info = new ConsoleFontInfo
                {
                    Size = (uint)sizeof(ConsoleFontInfo),
                    Width = 0,
                    Height = fontSizeY,
                    // FF_MODERN | TMPF_VECTOR | TMPF_TRUETYPE：等宽 TrueType 字体
                    Family = 54,
                    Weight = 400,
                };

                // info 是局部结构体，固定缓冲区本身已在栈上，无需 fixed 语句
                Span<char> buffer = new(info.FaceName, 32);
                buffer.Clear();
                fontName.AsSpan(0, Math.Min(fontName.Length, 31)).CopyTo(buffer);

                return SetCurrentConsoleFontEx(handle, maximumWindow: false, ref info);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 在当前光标处写一个歧义宽度字符，返回它占用的字符格数；写完把光标与内容还原。
        /// 读不到光标位置或不能定位时按一格处理（视为无需修正）。
        /// </summary>
        private static int MeasureAmbiguousWidth()
        {
            try
            {
                int left = Console.CursorLeft;
                int top = Console.CursorTop;

                Console.Write('±');
                int advance = Console.CursorLeft - left;

                // 抹掉探针字符并回到原位：后面的绘制从当前光标开始，
                // 残留内容会顶到表格/面板边框外面。
                Console.SetCursorPosition(left, top);
                Console.Write(new string(' ', Math.Max(advance, 1)));
                Console.SetCursorPosition(left, top);

                return advance <= 0 ? 1 : advance;
            }
            catch (Exception)
            {
                return 1;
            }
        }

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
