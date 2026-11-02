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

        /// <summary>串行化控制台设置的读取/切换/还原（备用缓冲区回调与退出收尾可能在不同时机）</summary>
        private static readonly object Gate = new();

        /// <summary>
        /// 写入字体用的结构体：FaceName 用内联定长缓冲。
        /// </summary>
        /// <remarks>
        /// 实测 conhost 对两种声明的接受程度不同：内联 <c>fixed char[32]</c> 能把「新宋体」
        /// 切成 Courier New，却切不回 MS Gothic；<see cref="ConsoleFontInfoText"/>（定长字符串）
        /// 则相反。两者都试一遍并用读回值校验，才能在任意起点上真正还原用户的字体。
        /// </remarks>
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

        /// <summary>读取/写入字体用的结构体：FaceName 用定长字符串声明（读取可靠）。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ConsoleFontInfoText
        {
            public uint Size;
            public uint Index;
            public short Width;
            public short Height;
            public int Family;
            public int Weight;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FaceName;
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

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCurrentConsoleFontEx(
            IntPtr consoleOutput,
            [MarshalAs(UnmanagedType.Bool)] bool maximumWindow,
            ref ConsoleFontInfoText fontInfo);

        /// <summary>
        /// 读取字体（GetCurrentConsoleFontEx）。
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCurrentConsoleFontEx(
            IntPtr consoleOutput,
            [MarshalAs(UnmanagedType.Bool)] bool maximumWindow,
            ref ConsoleFontInfoText fontInfo);

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleOutputCP();

        [DllImport("kernel32.dll")]
        private static extern uint GetConsoleCP();

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

        // ---------- 控制台设置的原值（退出时还原） ----------

        /// <summary>
        /// 进入前的代码页。共享终端里代码页是用户的设置，程序只是临时借用；
        /// 必须在任何 <c>Console.OutputEncoding</c> 赋值**之前**记录，否则记到的是自己设的值。
        /// </summary>
        private static uint originalOutputCodePage;
        private static uint originalInputCodePage;
        private static bool codePageCaptured;

        /// <summary>
        /// 进入前的字体，仅在第一次真正切换字体时记录（进入备用缓冲区后还会再切一次，
        /// 那时读到的已是缓冲区自己的默认字体，覆盖掉真正的原值就还不回去了）。
        /// 按字段保存而不是留着整个结构体：还原时走与切换完全相同的「按名字设置」路径，
        /// 避免结构体写回在某些宿主上只生效字号（实测：FaceName 未生效，屏幕字体仍是程序设的那个）。
        /// </summary>
        private static string? originalFontName;
        private static short originalFontWidth;
        private static short originalFontHeight;
        private static int originalFontFamily;
        private static int originalFontWeight;
        private static bool hasOriginalFont;
        private static bool fontSwitched;

        /// <summary>记录当前控制台代码页，供 <see cref="RestoreConsole"/> 还原。</summary>
        public static void CaptureConsoleCodePage()
        {
            lock (Gate)
            {
                if (codePageCaptured)
                {
                    return;
                }

                originalOutputCodePage = GetConsoleOutputCP();
                originalInputCodePage = GetConsoleCP();
                codePageCaptured = true;
            }
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

            lock (Gate)
            {
                // 只记录一次：进入备用缓冲区后还会调用本方法，那时读到的已经是缓冲区自己的
                // 默认字体，覆盖掉真正的原值就还不回去了。
                if (!hasOriginalFont
                    && TryReadConsoleFont(out string? name, out short width, out short height, out int family, out int weight)
                    && !string.IsNullOrWhiteSpace(name))
                {
                    originalFontName = name;
                    originalFontWidth = width;
                    originalFontHeight = height;
                    originalFontFamily = family;
                    originalFontWeight = weight;
                    hasOriginalFont = true;
                }

                foreach (string font in PreferredConsoleFonts)
                {
                    if (TrySetConsoleFont(font, fontSizeY, family: 54, weight: 400, width: 0)
                        && MeasureAmbiguousWidth() <= 1)
                    {
                        fontSwitched = true;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 把字体与代码页还给进入前的样子。
        /// </summary>
        /// <remarks>
        /// 从终端启动时控制台窗口与 shell 共享，字体和代码页是**用户的设置**，程序只是临时借用；
        /// 不还原的话退出后终端会一直停在我们换的字体上（实测：中文 Windows 上新宋体被换成
        /// Courier New 后，cmd/pwsh 的后续输出字体全变）。双击启动时窗口随进程关闭，
        /// 还原同样无害。任何一步失败都静默忽略——退出路径不该因为收尾而报错。
        /// </remarks>
        public static void RestoreConsole()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                lock (Gate)
                {
                    if (fontSwitched)
                    {
                        fontSwitched = false;

                        if (hasOriginalFont && originalFontName is { } name)
                        {
                            // 字宽固定传 0（由系统按字体自身比例计算）：读回来的是 8 这类具体值，
                            // 原样写回会让 conhost 认为「字体 + 该字宽」不合法而只应用字号
                            // （实测：名字不生效，终端仍停在程序换的字体上）。
                            TrySetConsoleFont(name, originalFontHeight, originalFontFamily, originalFontWeight, width: 0);
                        }
                    }

                    if (codePageCaptured)
                    {
                        codePageCaptured = false;
                        SetConsoleOutputCP(originalOutputCodePage);
                        SetConsoleCP(originalInputCodePage);
                    }
                }
            }
            catch (Exception)
            {
                // 控制台已关闭（双击启动的窗口先关）等情况：没有可还原的对象
            }
        }

        /// <summary>
        /// 读取当前控制台字体（GetCurrentConsoleFontEx）。名字读不到（空）时按「无法还原」处理，
        /// 不要拿着空名字去设置——那只会改字号，反而把用户的字体设置弄得更乱。
        /// </summary>
        private static bool TryReadConsoleFont(
            out string? name,
            out short width,
            out short height,
            out int family,
            out int weight)
        {
            name = null;
            width = 0;
            height = 0;
            family = 0;
            weight = 0;

            try
            {
                IntPtr handle = GetStdHandle(StdOutputHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return false;
                }

                var info = new ConsoleFontInfoText
                {
                    Size = (uint)Marshal.SizeOf<ConsoleFontInfoText>(),
                    FaceName = string.Empty,
                };

                if (!GetCurrentConsoleFontEx(handle, maximumWindow: false, ref info))
                {
                    return false;
                }

                name = info.FaceName;
                width = info.Width;
                height = info.Height;
                family = info.Family;
                weight = info.Weight;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>设置控制台字体（SetCurrentConsoleFontEx）。宿主不允许时返回 false。</summary>
        private static bool TrySetConsoleFont(string fontName, short fontSizeY)
        {
            return TrySetConsoleFont(fontName, fontSizeY, family: 54, weight: 400, width: 0);
        }

        /// <summary>
        /// 设置控制台字体并校验结果。宿主不允许时返回 false。
        /// </summary>
        /// <remarks>
        /// 两种结构都试：conhost 对「内联数组」与「定长字符串」两种声明的接受程度并不一致，
        /// 同一次调用返回 true 也可能只应用了字号（实测：新宋体→Courier New 只认内联数组，
        /// Courier New→MS Gothic 只认定长字符串）。因此写完必须读回比对名字，
        /// 没变就换另一种再写一次——只有读回一致才算成功。
        /// </remarks>
        private static bool TrySetConsoleFont(string fontName, short fontSizeY, int family, int weight, short width)
        {
            if (WriteConsoleFont(fontName, fontSizeY, family, weight, width, useInlineBuffer: true)
                && IsConsoleFontApplied(fontName))
            {
                return true;
            }

            return WriteConsoleFont(fontName, fontSizeY, family, weight, width, useInlineBuffer: false)
                && IsConsoleFontApplied(fontName);
        }

        /// <summary>写入一次字体（不校验），两种结构体二选一。</summary>
        private static unsafe bool WriteConsoleFont(
            string fontName,
            short fontSizeY,
            int family,
            int weight,
            short width,
            bool useInlineBuffer)
        {
            try
            {
                IntPtr handle = GetStdHandle(StdOutputHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return false;
                }

                if (!useInlineBuffer)
                {
                    var text = new ConsoleFontInfoText
                    {
                        Size = (uint)Marshal.SizeOf<ConsoleFontInfoText>(),
                        Width = width,
                        Height = fontSizeY,
                        Family = family,
                        Weight = weight,
                        FaceName = fontName,
                    };

                    return SetCurrentConsoleFontEx(handle, maximumWindow: false, ref text);
                }

                var info = new ConsoleFontInfo
                {
                    Size = (uint)sizeof(ConsoleFontInfo),
                    Width = width,
                    Height = fontSizeY,
                    Family = family,
                    Weight = weight,
                };

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

        /// <summary>读回当前字体名，确认上一次设置真的生效（大小写不敏感）。</summary>
        private static bool IsConsoleFontApplied(string fontName)
        {
            return TryReadConsoleFont(out string? current, out _, out _, out _, out _)
                && string.Equals(current, fontName, StringComparison.OrdinalIgnoreCase);
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
