using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 7-Zip 库（进程级共享）。同一路径在一个进程内只加载一次。
    /// Windows 加载器对同一模块本就返回同一句柄并只做引用计数，因此每次密码测试
    /// 都新建实例、LoadLibrary/FreeLibrary 一轮不会有任何隔离收益，纯属额外开销；
    /// 真正绕开 7z.dll 内 KDF 全局锁的是多进程模式（见 MultiProcess），
    /// 那里每个进程各自加载一份，天然隔离。
    /// CreateObject 用 LibraryImport 源生成 stub（编译期生成，裁剪安全），
    /// 不使用 Marshal.GetDelegateForFunctionPointer——后者依赖运行时封送元数据，
    /// 在 IL 裁剪后会 TypeLoadException（已实测）。
    /// </summary>
    internal sealed unsafe partial class SevenZipLibrary
    {
        private static readonly Lock Gate = new();
        private static readonly Dictionary<string, SevenZipLibrary> Shared = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 取得指定路径的进程级共享实例：首次调用时加载 7z.dll（并按需设置一次
        /// DLL 搜索目录），之后直接命中缓存。加载失败抛异常（含路径与错误码）
        /// </summary>
        public static SevenZipLibrary GetShared(string dllPath)
        {
            string fullPath = Path.GetFullPath(dllPath);

            lock (Gate)
            {
                if (Shared.TryGetValue(fullPath, out var existing))
                {
                    return existing;
                }

                var library = new SevenZipLibrary(fullPath);
                Shared.Add(fullPath, library);
                return library;
            }
        }

        /// <summary>
        /// 7z.dll 导出的 CreateObject(const GUID*, const GUID*, void**)
        /// </summary>
        [LibraryImport("7z.dll", EntryPoint = "CreateObject")]
        private static partial int CreateObjectNative(ref Guid classID, ref Guid interfaceID, out IntPtr outObject);

        private SevenZipLibrary(string dllPath)
        {
            if (!File.Exists(dllPath))
            {
                throw new FileNotFoundException($"7z.dll not found at: {dllPath}");
            }

            // 仅在此处（每路径一次）调整 DLL 搜索目录：原实现每次密码测试创建上下文时
            // 都会写一次进程级全局搜索路径，既多余又让并发创建上下文时行为不干净。
            // 7z.dll 常驻进程直到退出（不再 FreeLibrary），因此这一次设置足够
            WindowsNativeMethods.SetDllDirectory(Path.GetDirectoryName(dllPath)!);

            IntPtr module = WindowsNativeMethods.LoadLibrary(dllPath);
            if (module == IntPtr.Zero)
            {
                int errorCode = Marshal.GetLastWin32Error();
                throw new DllNotFoundException($"Failed to load 7z.dll from {dllPath}. Error code: {errorCode}");
            }
        }

        /// <summary>
        /// 创建 COM 对象并转成源生成托管接口。
        /// <para>
        /// 取代原先的 <c>Marshal.GetObjectForIUnknown</c>：那个 API 依赖运行时内置 COM 封送，
        /// Native AOT 下会抛 "COM Interop requires ComWrapper instance registered for marshalling"。
        /// <see cref="ComInterfaceMarshaller{T}.ConvertToManaged"/> 走编译期生成的 marshaller，AOT 可用。
        /// </para>
        /// <para>
        /// 注意转换后的 RCW 生命周期由 GC 决定（没有 <c>Marshal.ReleaseComObject</c> 的等价物），
        /// 及时释放原生资源必须靠显式 <c>Close()</c> —— 调用方已这样做。
        /// </para>
        /// </summary>
        public int CreateObject<T>(ref Guid clsID, ref Guid interfaceID, out T? outObject)
            where T : class
        {
            int hr = CreateObjectNative(ref clsID, ref interfaceID, out IntPtr unknownPtr);
            if (hr < 0 || unknownPtr == IntPtr.Zero)
            {
                outObject = null;
                return hr;
            }

            try
            {
                outObject = ComInterfaceMarshaller<T>.ConvertToManaged((void*)unknownPtr);
            }
            finally
            {
                // 释放 CreateObjectNative 带来的那一份原生引用
                Marshal.Release(unknownPtr);
            }

            return hr;
        }
    }
}
