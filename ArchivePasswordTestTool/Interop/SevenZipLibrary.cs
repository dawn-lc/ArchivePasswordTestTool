using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 7z.dll 原生方法（静态版本，向后兼容）
    /// </summary>
    internal static class NativeMethods
    {
        private static SevenZipLibrary? _library;

        /// <summary>
        /// 初始化7z.dll，从指定路径加载
        /// </summary>
        /// <param name="dllPath">7z.dll的完整路径</param>
        /// <returns>是否成功加载</returns>
        public static bool Initialize(string dllPath)
        {
            if (_library != null)
            {
                return true; // 已经初始化
            }

            _library = new SevenZipLibrary(dllPath);
            return true;
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        public static void Cleanup()
        {
            _library?.Dispose();
            _library = null;
        }

        public static int CreateObject(
            ref Guid clsID,
            ref Guid interfaceID,
            out object outObject)
        {
            if (_library == null)
            {
                throw new InvalidOperationException("7z.dll not initialized. Call Initialize() first.");
            }

            return _library.CreateObject(ref clsID, ref interfaceID, out outObject);
        }
    }

    /// <summary>
    /// 7-Zip 库实例，每个实例独立加载 7z.dll
    /// </summary>
    /// <remarks>
    /// CreateObject 用 LibraryImport 源生成 stub（编译期生成，裁剪安全），
    /// 不使用 Marshal.GetDelegateForFunctionPointer——后者依赖运行时封送元数据，
    /// 在 IL 裁剪后会 TypeLoadException（已实测）。
    /// Windows 加载器按 base name 匹配已加载模块，Initialize 中 LoadLibrary 后
    /// 此处 DllImport("7z.dll") 命中同一模块（含 -lib 指定路径的场景）。
    /// </remarks>
    internal partial class SevenZipLibrary : IDisposable
    {
        private IntPtr _7zModule = IntPtr.Zero;
        private bool _disposed;

        /// <summary>
        /// 7z.dll 导出的 CreateObject(const GUID*, const GUID*, void**)
        /// </summary>
        [LibraryImport("7z.dll", EntryPoint = "CreateObject")]
        private static partial int CreateObjectNative(ref Guid classID, ref Guid interfaceID, out IntPtr outObject);

        /// <summary>
        /// 初始化7z.dll，从指定路径加载
        /// </summary>
        /// <param name="dllPath">7z.dll的完整路径</param>
        public SevenZipLibrary(string dllPath)
        {
            Initialize(dllPath);
        }

        /// <summary>
        /// 初始化7z.dll，从指定路径加载
        /// </summary>
        /// <param name="dllPath">7z.dll的完整路径</param>
        /// <returns>是否成功加载</returns>
        private void Initialize(string dllPath)
        {
            if (_7zModule != IntPtr.Zero)
            {
                return; // 已经初始化
            }

            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException($"7z.dll not found at: {dllPath}");
            }

            // 设置DLL搜索目录为7z.dll所在目录
            string dllDirectory = Path.GetDirectoryName(dllPath)!;
            WindowsNativeMethods.SetDllDirectory(dllDirectory);

            // 加载7z.dll
            _7zModule = WindowsNativeMethods.LoadLibrary(dllPath);
            if (_7zModule == IntPtr.Zero)
            {
                int errorCode = Marshal.GetLastWin32Error();
                throw new DllNotFoundException($"Failed to load 7z.dll from {dllPath}. Error code: {errorCode}");
            }
        }

        /// <summary>
        /// 创建 COM 对象
        /// </summary>
        public int CreateObject(
            ref Guid clsID,
            ref Guid interfaceID,
            out object outObject)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(SevenZipLibrary));

            int hr = CreateObjectNative(ref clsID, ref interfaceID, out IntPtr unknownPtr);
            if (hr < 0 || unknownPtr == IntPtr.Zero)
            {
                outObject = null!;
                return hr;
            }

            // IUnknown → RCW（内部会 AddRef），这里释放原生引用以平衡调用约定的一次计数；
            // 后续生命周期由 Marshal.ReleaseComObject 管理，与原 out object 封送语义一致
            outObject = Marshal.GetObjectForIUnknown(unknownPtr);
            Marshal.Release(unknownPtr);
            return hr;
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            if (_7zModule != IntPtr.Zero)
            {
                WindowsNativeMethods.FreeLibrary(_7zModule);
                _7zModule = IntPtr.Zero;
            }

            _disposed = true;
        }

        ~SevenZipLibrary()
        {
            Dispose();
        }
    }
}
