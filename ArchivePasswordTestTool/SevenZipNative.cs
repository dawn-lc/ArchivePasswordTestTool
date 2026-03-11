using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 7-Zip SDK 的 GUID 定义
    /// </summary>
    internal static class SevenZipGuids
    {
        // 7z 格式的 CLSID
        public static Guid CLSID_CFormat7z = new("23170F69-40C1-278A-1000-000110070000");
        public static Guid CLSID_CFormatZip = new("23170F69-40C1-278A-1000-000110010000");
        public static Guid CLSID_CFormatRar = new("23170F69-40C1-278A-1000-000110030000");
        public static Guid CLSID_CFormatRar5 = new("23170F69-40C1-278A-1000-000110CC0000");

        // 接口 GUID
        public static Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        public static Guid IID_IInArchive = new("23170F69-40C1-278A-0000-000600600000");
        public static Guid IID_IOutArchive = new("23170F69-40C1-278A-0000-000600A00000");
        public static Guid IID_ISetProperties = new("23170F69-40C1-278A-0000-000600030000");
    }

    /// <summary>
    /// Windows API 方法
    /// </summary>
    internal static class WindowsNativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
    }

    /// <summary>
    /// 7z.dll 原生方法
    /// </summary>
    internal static class NativeMethods
    {
        private static IntPtr _7zModule = IntPtr.Zero;
        private static CreateObjectDelegate? _createObjectDelegate;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateObjectDelegate(
            [In] ref Guid classID,
            [In] ref Guid interfaceID,
            [MarshalAs(UnmanagedType.Interface)] out object outObject);

        /// <summary>
        /// 初始化7z.dll，从指定路径加载
        /// </summary>
        /// <param name="dllPath">7z.dll的完整路径</param>
        /// <returns>是否成功加载</returns>
        public static bool Initialize(string dllPath)
        {
            // DebugHelper.Log($"NativeMethods.Initialize开始: dllPath={dllPath}");

            if (_7zModule != IntPtr.Zero)
            {
                // DebugHelper.Log("7z.dll已经初始化，跳过");
                return true; // 已经初始化
            }

            if (!File.Exists(dllPath))
            {
                // DebugHelper.LogError($"7z.dll文件不存在: {dllPath}");
                throw new FileNotFoundException($"7z.dll not found at: {dllPath}");
            }

            // 设置DLL搜索目录为7z.dll所在目录
            string dllDirectory = Path.GetDirectoryName(dllPath)!;
            // DebugHelper.Log($"设置DLL搜索目录: {dllDirectory}");
            WindowsNativeMethods.SetDllDirectory(dllDirectory);

            // 加载7z.dll
            // DebugHelper.Log($"加载7z.dll: {dllPath}");
            _7zModule = WindowsNativeMethods.LoadLibrary(dllPath);
            if (_7zModule == IntPtr.Zero)
            {
                int errorCode = Marshal.GetLastWin32Error();
                // DebugHelper.LogError($"加载7z.dll失败: {dllPath}, 错误代码: {errorCode}");
                throw new DllNotFoundException($"Failed to load 7z.dll from {dllPath}. Error code: {errorCode}");
            }

            // DebugHelper.Log($"7z.dll加载成功，模块句柄: 0x{_7zModule:X}");

            // 获取CreateObject函数地址
            // DebugHelper.Log("获取CreateObject函数地址");
            IntPtr createObjectPtr = WindowsNativeMethods.GetProcAddress(_7zModule, "CreateObject");
            if (createObjectPtr == IntPtr.Zero)
            {
                // DebugHelper.LogError("CreateObject函数未找到");
                WindowsNativeMethods.FreeLibrary(_7zModule);
                _7zModule = IntPtr.Zero;
                throw new EntryPointNotFoundException("CreateObject function not found in 7z.dll");
            }

            // DebugHelper.Log($"CreateObject函数地址: 0x{createObjectPtr:X}");

            // 创建委托
            _createObjectDelegate = Marshal.GetDelegateForFunctionPointer<CreateObjectDelegate>(createObjectPtr);
            // DebugHelper.Log("NativeMethods.Initialize完成");
            return true;
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        public static void Cleanup()
        {
            if (_7zModule != IntPtr.Zero)
            {
                WindowsNativeMethods.FreeLibrary(_7zModule);
                _7zModule = IntPtr.Zero;
                _createObjectDelegate = null;
            }
        }

        public static int CreateObject(
            ref Guid clsID,
            ref Guid interfaceID,
            out object outObject)
        {
            if (_createObjectDelegate == null)
            {
                throw new InvalidOperationException("7z.dll not initialized. Call Initialize() first.");
            }

            // DebugHelper.Log($"NativeMethods.CreateObject调用: clsID={clsID}, interfaceID={interfaceID}");

            int result = _createObjectDelegate(ref clsID, ref interfaceID, out outObject);
            // DebugHelper.Log($"NativeMethods.CreateObject结果: result=0x{result:X8}, outObject={(outObject == null ? "null" : outObject.GetType().Name)}");
            return result;
        }
    }

    /// <summary>
    /// 7-Zip 接口定义（完整版本，与SevenZipWrapper保持一致）
    /// </summary>

    internal enum AskMode : int
    {
        Extract = 0,
        Test,
        Skip
    }

    internal enum OperationResult : int
    {
        OK = 0,
        UnsupportedMethod,
        DataError,
        CRCError
    }

    internal enum ItemPropId : uint
    {
        NoProperty = 0,
        HandlerItemIndex = 2,
        Path,
        Name,
        Extension,
        IsFolder,
        Size,
        PackedSize,
        Attributes,
        CreationTime,
        LastAccessTime,
        LastWriteTime,
        Solid,
        Commented,
        Encrypted,
        SplitBefore,
        SplitAfter,
        DictionarySize,
        CRC,
        Type,
        IsAnti,
        Method,
        HostOS,
        FileSystem,
        User,
        Group,
        Block,
        Comment,
        Position,
        Prefix,
        TotalSize = 0x1100,
        FreeSpace,
        ClusterSize,
        VolumeName,
        LocalName = 0x1200,
        Provider,
        UserDefined = 0x10000
    }

    internal enum ArchivePropId : uint
    {
        Name = 0,
        ClassID,
        Extension,
        AddExtension,
        Update,
        KeepName,
        StartSignature,
        FinishSignature,
        Associate
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropArray
    {
        internal uint Length;
        internal IntPtr PointerValues;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
        [FieldOffset(8)] public byte byteValue;
        [FieldOffset(8)] public long longValue;
        [FieldOffset(8)] public FILETIME filetime;
        [FieldOffset(8)] public PropArray propArray;

        public readonly VarEnum VarType => (VarEnum)vt;

        public void Clear()
        {
            switch (VarType)
            {
                case VarEnum.VT_EMPTY:
                    break;

                case VarEnum.VT_NULL:
                case VarEnum.VT_I2:
                case VarEnum.VT_I4:
                case VarEnum.VT_R4:
                case VarEnum.VT_R8:
                case VarEnum.VT_CY:
                case VarEnum.VT_DATE:
                case VarEnum.VT_ERROR:
                case VarEnum.VT_BOOL:
                case VarEnum.VT_I1:
                case VarEnum.VT_UI1:
                case VarEnum.VT_UI2:
                case VarEnum.VT_UI4:
                case VarEnum.VT_I8:
                case VarEnum.VT_UI8:
                case VarEnum.VT_INT:
                case VarEnum.VT_UINT:
                case VarEnum.VT_HRESULT:
                case VarEnum.VT_FILETIME:
                    vt = 0;
                    break;

                default:
                    PropVariantClear(ref this);
                    break;
            }
        }

        public readonly object? GetObject()
        {
            return VarType switch
            {
                VarEnum.VT_EMPTY => null,
                VarEnum.VT_FILETIME => DateTime.FromFileTime(longValue),
                _ => MarshalVariant()
            };
        }

        private readonly object? MarshalVariant()
        {
            GCHandle handle = GCHandle.Alloc(this, GCHandleType.Pinned);

            try
            {
                return Marshal.GetObjectForNativeVariant(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000600600000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInArchive
    {
        [PreserveSig]
        int Open(
            IInStream stream,
            [In] ref ulong maxCheckStartPosition,
            [MarshalAs(UnmanagedType.Interface)] IArchiveOpenCallback? openCallback);

        void Close();

        uint GetNumberOfItems();

        void GetProperty(uint index, ItemPropId propID, ref PropVariant value);

        [PreserveSig]
        int Extract(
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[]? indices,
            uint numItems,
            int testMode,
            [MarshalAs(UnmanagedType.Interface)] IArchiveExtractCallback extractCallback);

        void GetArchiveProperty(uint propID, ref PropVariant value);

        uint GetNumberOfProperties();

        void GetPropertyInfo(
            uint index,
            [MarshalAs(UnmanagedType.BStr)] out string name,
            out ItemPropId propID,
            out ushort varType);

        uint GetNumberOfArchiveProperties();

        void GetArchivePropertyInfo(
            uint index,
            [MarshalAs(UnmanagedType.BStr)] string name,
            ref uint propID,
            ref ushort varType);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-1000-000600030000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInStream
    {
        uint Read(
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data,
            uint size);

        void Seek(
            long offset,
            uint seekOrigin,
            IntPtr newPosition);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-1000-000600200000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveOpenCallback
    {
        int SetTotal(IntPtr files, IntPtr bytes);
        int SetCompleted(IntPtr files, IntPtr bytes);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000600200000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveExtractCallback
    {
        void SetTotal(ulong total);
        void SetCompleted([In] ref ulong completeValue);

        [PreserveSig]
        int GetStream(
            uint index,
            [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream? outStream,
            AskMode askExtractMode);

        void PrepareOperation(AskMode askExtractMode);
        void SetOperationResult(OperationResult resultEOperationResult);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-1000-000600500000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISequentialOutStream
    {
        int Write(byte[] data, uint size, IntPtr processedSize);
    }

    [ComImport]
    [Guid("23170F69-40C1-278A-0000-000500100000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICryptoGetTextPassword
    {
        [PreserveSig]
        int CryptoGetTextPassword(
            [MarshalAs(UnmanagedType.BStr)] out string password);
    }

    /// <summary>
    /// 文件流包装器，实现 IInStream 接口
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal class FileInStream : IInStream, IDisposable
    {
        private readonly Stream _stream;
        private readonly string? _filePath;
        private GCHandle _gcHandle;

        public FileInStream(string filePath)
        {
            // DebugHelper.Log($"FileInStream构造函数: filePath={filePath}");
            _stream = File.OpenRead(filePath);
            _filePath = filePath;
            // 固定对象，防止在COM调用期间被垃圾回收
            _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        }

        public FileInStream(Stream stream)
        {
            // DebugHelper.Log($"FileInStream构造函数: 使用现有Stream");
            _stream = stream;
            _filePath = null;
            // 固定对象，防止在COM调用期间被垃圾回收
            _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        }

        public uint Read(byte[] data, uint size)
        {
            // DebugHelper.Log($"FileInStream.Read: size={size}");
            int read = _stream.Read(data, 0, (int)size);
            // DebugHelper.Log($"FileInStream.Read: 实际读取 {read} 字节");

            return (uint)read;
        }

        public void Seek(long offset, uint seekOrigin, IntPtr newPosition)
        {
            // 减少日志输出以避免在COM调用期间可能的内存访问冲突
            // DebugHelper.Log($"FileInStream.Seek: offset={offset}, seekOrigin={seekOrigin}, newPosition={(newPosition == IntPtr.Zero ? "null" : "有效")}");

            SeekOrigin origin = (SeekOrigin)seekOrigin;
            _stream.Seek(offset, origin);

            if (newPosition != IntPtr.Zero)
            {
                Marshal.WriteInt64(newPosition, _stream.Position);
                // DebugHelper.Log($"FileInStream.Seek: 写入newPosition={_stream.Position}");
            }
        }

        public void Dispose()
        {
            // DebugHelper.Log($"FileInStream.Dispose: 文件={_filePath ?? "Stream"}");
            _stream?.Dispose();
            if (_gcHandle.IsAllocated)
            {
                _gcHandle.Free();
            }
        }

        ~FileInStream()
        {
            if (_gcHandle.IsAllocated)
            {
                _gcHandle.Free();
            }
        }
    }

    /// <summary>
    /// 密码提供者，实现 ICryptoGetTextPassword 接口
    /// </summary>
    internal class PasswordProvider(string password) : ICryptoGetTextPassword
    {
        private readonly string _password = password;

        public int CryptoGetTextPassword(
            [MarshalAs(UnmanagedType.BStr)] out string password)
        {
            password = _password;
            return 0; // 返回 0 表示成功
        }
    }

    /// <summary>
    /// 虚拟输出流，用于测试模式
    /// </summary>
    internal class DummyOutStream : ISequentialOutStream
    {
        public int Write(byte[] data, uint size, IntPtr processedSize)
        {
            // DebugHelper.Log($"DummyOutStream.Write: size={size}");
            // 返回成功，但不实际写入任何内容
            if (processedSize != IntPtr.Zero)
            {
                Marshal.WriteInt32(processedSize, (int)size);
            }
            return 0; // S_OK
        }
    }

    /// <summary>
    /// 简单的提取回调，用于测试模式
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal class TestExtractCallback : IArchiveExtractCallback, ICryptoGetTextPassword
    {
        private readonly string _password;
        private GCHandle _gcHandle;
        private OperationResult _lastOperationResult;

        public TestExtractCallback(string password)
        {
            _password = password;
            _lastOperationResult = OperationResult.OK;
            // 固定对象，防止在COM调用期间被垃圾回收
            _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        }

        ~TestExtractCallback()
        {
            if (_gcHandle.IsAllocated)
            {
                _gcHandle.Free();
            }
        }

        public OperationResult LastOperationResult => _lastOperationResult;

        public void SetTotal(ulong total)
        {
            // DebugHelper.Log($"TestExtractCallback.SetTotal: total={total}");
        }

        public void SetCompleted([In] ref ulong completeValue)
        {
            // DebugHelper.Log($"TestExtractCallback.SetCompleted: completeValue={completeValue}");
        }

        public int GetStream(uint index, out ISequentialOutStream? outStream, AskMode askExtractMode)
        {
            // DebugHelper.Log($"TestExtractCallback.GetStream: index={index}, askExtractMode={askExtractModeStr}");

            // 根据SevenZipWrapper的实现，当askExtractMode不是Extract时返回null
            // 在测试模式下，askExtractMode应该是Test，所以返回null
            if (askExtractMode != AskMode.Extract)
            {
                outStream = null;
                // DebugHelper.Log($"TestExtractCallback.GetStream: askExtractMode不是Extract，返回null");
                return 0; // S_OK
            }

            // 在测试模式下，askExtractMode应该是Test，所以不会执行到这里
            // 但如果执行到这里，返回一个虚拟输出流
            outStream = new DummyOutStream();
            // DebugHelper.Log($"TestExtractCallback.GetStream: 返回虚拟输出流");
            return 0; // S_OK
        }

        public void PrepareOperation(AskMode askExtractMode)
        {
            // DebugHelper.Log($"TestExtractCallback.PrepareOperation: askExtractMode={askExtractModeStr}");
        }

        public void SetOperationResult(OperationResult operationResult)
        {
            // DebugHelper.Log($"TestExtractCallback.SetOperationResult: operationResult={operationResultStr} (值={(int)operationResult})");

            // 根据7z SDK文档，OperationResult应该只有0-3的值
            // 但实际测试中发现有时会返回9，这可能是密码错误或其他错误
            // 我们将非OK的结果都视为密码错误
            if (operationResult == OperationResult.OK)
            {
                _lastOperationResult = operationResult;
            }
            else
            {
                // 非OK结果视为密码错误
                _lastOperationResult = OperationResult.DataError; // 使用DataError表示密码错误
                // DebugHelper.Log($"TestExtractCallback.SetOperationResult: 非OK结果({(int)operationResult})，视为密码错误");
            }
        }

        public int CryptoGetTextPassword(
            [MarshalAs(UnmanagedType.BStr)] out string password)
        {
            // DebugHelper.Log($"TestExtractCallback.CryptoGetTextPassword: 返回密码={(string.IsNullOrEmpty(_password) ? "(空密码)" : "***")}");
            password = _password;
            return 0;
        }
    }

    /// <summary>
    /// 支持密码的打开回调
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal class PasswordOpenCallback : IArchiveOpenCallback, ICryptoGetTextPassword
    {
        private readonly string _password;
        private GCHandle _gcHandle;

        public PasswordOpenCallback(string password)
        {
            _password = password;
            // 固定对象，防止在COM调用期间被垃圾回收
            _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        }

        ~PasswordOpenCallback()
        {
            if (_gcHandle.IsAllocated)
            {
                _gcHandle.Free();
            }
        }

        public int SetTotal(IntPtr files, IntPtr bytes)
        {
            // 减少日志输出以避免在COM调用期间可能的内存访问冲突
            // DebugHelper.Log($"PasswordOpenCallback.SetTotal");
            return 0; // S_OK
        }

        public int SetCompleted(IntPtr files, IntPtr bytes)
        {
            // 减少日志输出以避免在COM调用期间可能的内存访问冲突
            // DebugHelper.Log($"PasswordOpenCallback.SetCompleted");
            return 0; // S_OK
        }

        public int CryptoGetTextPassword(
            [MarshalAs(UnmanagedType.BStr)] out string password)
        {
            // 减少日志输出以避免在COM调用期间可能的内存访问冲突
            // DebugHelper.Log($"PasswordOpenCallback.CryptoGetTextPassword: 返回密码={(string.IsNullOrEmpty(_password) ? "(空密码)" : "***")}");
            password = _password;
            return 0; // S_OK
        }
    }

    /// <summary>
    /// 压缩包上下文，管理压缩包的生命周期（支持并行测试）
    /// </summary>
    public class ArchiveContext : IDisposable
    {
        private readonly IInArchive _archive;
        private readonly FileInStream? _stream;
        private bool _disposed;
        private readonly Lock _testLock = new(); // 用于同步TestPassword调用

        /// <summary>
        /// 压缩包中的文件数量
        /// </summary>
        public uint FileCount { get; private set; }

        /// <summary>
        /// 压缩包格式
        /// </summary>
        public string Format { get; private set; } = "Unknown";

        /// <summary>
        /// 打开压缩包
        /// </summary>
        /// <param name="archivePath">压缩包路径</param>
        /// <param name="dllPath">7z.dll路径</param>
        /// <param name="password">打开压缩包时使用的密码（可选）</param>
        /// <param name="preferredFormat">优先尝试的格式（如果已知）</param>
        public ArchiveContext(string archivePath, string dllPath, string? password = null, string? preferredFormat = null)
        {
            // DebugHelper.Log($"ArchiveContext构造函数: archivePath={archivePath}, dllPath={dllPath}, password={(string.IsNullOrEmpty(password) ? "(空密码)" : "***")}, preferredFormat={preferredFormat ?? "未指定"}");

            if (!File.Exists(archivePath))
            {
                throw new FileNotFoundException($"Archive file not found: {archivePath}");
            }

            // 初始化7z.dll
            if (!NativeMethods.Initialize(dllPath))
            {
                throw new InvalidOperationException($"Failed to initialize 7z.dll from: {dllPath}");
            }

            // 定义支持的格式
            var formatList = new List<(Guid Guid, string Name)>
            {
                (SevenZipGuids.CLSID_CFormat7z, "7z"),
                (SevenZipGuids.CLSID_CFormatZip, "Zip"),
                (SevenZipGuids.CLSID_CFormatRar, "Rar"),
                (SevenZipGuids.CLSID_CFormatRar5, "Rar5")
            };

            // 如果指定了格式，只尝试这个格式
            if (!string.IsNullOrEmpty(preferredFormat))
            {
                var specifiedFormat = formatList.FirstOrDefault(f => f.Name.Equals(preferredFormat, StringComparison.OrdinalIgnoreCase));
                if (specifiedFormat.Name != null)
                {
                    // 只保留指定的格式
                    formatList = [specifiedFormat];
                    // DebugHelper.Log($"指定格式: {specifiedFormat.Name}, 只尝试此格式");
                }
                else
                {
                    // DebugHelper.LogWarning($"指定的格式 '{preferredFormat}' 不在支持列表中，将尝试所有格式");
                }
            }

            bool opened = false;
            IInArchive? tempArchive = null;
            FileInStream? tempStream = null;

            for (int i = 0; i < formatList.Count; i++)
            {
                var formatItem = formatList[i];
                Guid clsID = formatItem.Guid;
                string formatName = formatItem.Name;

                // DebugHelper.Log($"尝试格式: {formatName}, GUID: {clsID}");

                // 直接请求IInArchive接口
                Guid iidInArchive = SevenZipGuids.IID_IInArchive;
                int hr = NativeMethods.CreateObject(ref clsID, ref iidInArchive, out object archiveObj);
                // DebugHelper.LogHResult($"CreateObject({formatName}) - IInArchive", hr);

                if (hr == 0 && archiveObj != null)
                {
                    // DebugHelper.Log($"CreateObject成功, 获取到IInArchive接口");
                    tempArchive = (IInArchive)archiveObj;

                    // DebugHelper.Log($"创建FileInStream: {archivePath}");
                    tempStream = new FileInStream(archivePath);
                    ulong maxCheckStartPosition = 0;

                    // 创建打开回调（如果需要密码）
                    IArchiveOpenCallback? openCallback = null;
                    if (!string.IsNullOrEmpty(password))
                    {
                        // DebugHelper.Log($"创建密码打开回调");
                        openCallback = new PasswordOpenCallback(password);
                    }

                    // DebugHelper.Log($"调用archive.Open()");
                    hr = tempArchive.Open(tempStream, ref maxCheckStartPosition, openCallback);
                    // DebugHelper.LogHResult($"archive.Open({formatName})", hr);

                    if (hr == 0)
                    {
                        // DebugHelper.Log($"archive.Open成功, 格式: {formatName}");
                        _archive = tempArchive;
                        _stream = tempStream;
                        Format = formatName;
                        opened = true;
                        break;
                    }
                    else
                    {
                        // DebugHelper.LogWarning($"archive.Open失败, 格式: {formatName}, 尝试下一个格式");
                    }

                    // 清理当前尝试
                    // DebugHelper.Log($"清理当前尝试: {formatName}");
                    if (tempArchive != null)
                    {
                        Marshal.ReleaseComObject(tempArchive);
                        tempArchive = null;
                    }
                    tempStream?.Dispose();
                    tempStream = null;
                }
                else
                {
                    // DebugHelper.LogWarning($"CreateObject失败, 格式: {formatName}");
                }
            }

            if (!opened)
            {
                // 清理临时资源
                if (tempArchive != null)
                {
                    Marshal.ReleaseComObject(tempArchive);
                }
                tempStream?.Dispose();
                throw new InvalidOperationException("所有格式尝试失败，无法打开压缩包");
            }

            // 获取文件数量（只有在成功打开压缩包后才执行）
            // 注意：这里_archive已经被正确初始化，因为opened为true
            if (_archive == null)
            {
                throw new InvalidOperationException("Archive not properly initialized");
            }
            FileCount = _archive.GetNumberOfItems();
            // DebugHelper.Log($"压缩包包含 {FileCount} 个文件, 格式: {Format}");
        }

        /// <summary>
        /// 测试密码是否正确（线程安全版本）
        /// </summary>
        /// <param name="password">要测试的密码</param>
        /// <param name="fileIndices">要测试的文件索引数组，如果为null则测试所有文件</param>
        /// <returns>true 表示密码正确，false 表示密码错误或发生错误</returns>
        public bool TestPassword(string password, uint[]? fileIndices = null)
        {
            // 使用锁确保线程安全，因为IInArchive接口可能不支持并发调用
            lock (_testLock)
            {
                // DebugHelper.Log($"ArchiveContext.TestPassword: password={(string.IsNullOrEmpty(password) ? "(空密码)" : "***")}, fileIndices={(fileIndices == null ? "null" : $"长度={fileIndices.Length}")}");

                if (FileCount == 0)
                {
                    // DebugHelper.LogError($"文件数量为0: FileCount={FileCount}");
                    return false;
                }

                // 根据SevenZipWrapper的实现，使用null表示提取所有文件，0xFFFFFFFF作为numItems
                uint[]? indices = fileIndices;
                uint numItems = 0xFFFFFFFF; // 特殊值，表示提取所有文件

                if (indices != null && indices.Length > 0)
                {
                    // 只测试指定的文件
                    // DebugHelper.Log($"使用指定文件索引: {string.Join(", ", indices)}");
                    numItems = (uint)indices.Length;
                }
                else
                {
                    // 测试所有文件，使用null作为indices参数
                    indices = null;
                    // DebugHelper.Log($"测试所有 {FileCount} 个文件");
                }

                // 使用测试模式（testMode = 1），根据7z SDK文档，1表示测试模式
                // DebugHelper.Log($"调用archive.Extract(testMode=1)");
                var callback = new TestExtractCallback(password);
                int extractResult = _archive.Extract(indices, numItems, 1, callback);
                // DebugHelper.LogHResult("archive.Extract", extractResult);

                // 检查操作结果
                OperationResult operationResult = callback.LastOperationResult;
                // DebugHelper.Log($"TestPassword操作结果: {operationResult} (值={(int)operationResult})");

                // 返回码说明：
                // 0 = S_OK (成功)
                // 1 = S_FALSE (密码错误或其他错误)
                // 同时需要检查操作结果是否为OK
                bool result = extractResult == 0 && operationResult == OperationResult.OK;
                // DebugHelper.Log($"TestPassword结果: {result} (extractResult={extractResult}, operationResult={operationResult})");
                return result;
            }
        }

        /// <summary>
        /// 获取文件信息
        /// </summary>
        public ArchiveFileInfo[] GetFileInfo()
        {
            // DebugHelper.Log($"ArchiveContext.GetFileInfo开始");
            var result = new List<ArchiveFileInfo>();

            for (uint i = 0; i < FileCount; i++)
            {
                // 获取文件属性
                PropVariant propVariant = new();
                try
                {
                    // 获取文件是否加密
                    _archive.GetProperty(i, ItemPropId.Encrypted, ref propVariant);
                    bool isEncrypted = false;

                    // 尝试解析属性值
                    var propValue = propVariant.GetObject();
                    if (propValue is bool boolValue)
                    {
                        isEncrypted = boolValue;
                    }
                    else if (propValue != null)
                    {
                        // 尝试转换为bool
                        isEncrypted = Convert.ToBoolean(propValue);
                    }

                    // 获取文件大小
                    propVariant.Clear();
                    _archive.GetProperty(i, ItemPropId.Size, ref propVariant);
                    long fileSize = 0;

                    var sizeValue = propVariant.GetObject();
                    if (sizeValue is long longSize)
                    {
                        fileSize = longSize;
                    }
                    else if (sizeValue is ulong ulongSize)
                    {
                        fileSize = (long)ulongSize;
                    }
                    else if (sizeValue != null)
                    {
                        fileSize = Convert.ToInt64(sizeValue);
                    }

                    // 获取文件名
                    propVariant.Clear();
                    _archive.GetProperty(i, ItemPropId.Path, ref propVariant);
                    string fileName = $"File_{i}";

                    var nameValue = propVariant.GetObject();
                    if (nameValue is string nameStr)
                    {
                        fileName = nameStr;
                    }

                    result.Add(new ArchiveFileInfo
                    {
                        Index = (int)i,
                        FileName = fileName,
                        Size = fileSize,
                        Encrypted = isEncrypted
                    });

                    propVariant.Clear();
                }
                catch
                {

                    // DebugHelper.LogError($"获取文件{i}信息失败", ex);
                    // 如果获取属性失败，使用默认值
                    result.Add(new ArchiveFileInfo
                    {
                        Index = (int)i,
                        FileName = $"File_{i}",
                        Size = 0,
                        Encrypted = true // 假设加密，避免误判
                    });
                    propVariant.Clear();
                }
            }

            // DebugHelper.Log($"ArchiveContext.GetFileInfo: 返回 {result.Count} 个文件信息");
            return [.. result];
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            // DebugHelper.Log("ArchiveContext.Dispose开始");
            try
            {
                // 使用局部变量捕获_archive的值，避免编译器警告
                var archive = _archive;
                if (archive != null)
                {
                    try
                    {
                        // DebugHelper.Log("调用archive.Close()");
                        archive.Close();
                    }
                    catch
                    {
                        // DebugHelper.LogError("archive.Close失败", ex);
                    }
                    Marshal.ReleaseComObject(archive);
                }

                _stream?.Dispose();
            }
            catch
            {
                // DebugHelper.LogError("ArchiveContext.Dispose异常", ex);
            }
            finally
            {
                _disposed = true;
                // DebugHelper.Log("ArchiveContext.Dispose完成");
            }
        }
    }

    /// <summary>
    /// 压缩包中文件信息
    /// </summary>
    public class ArchiveFileInfo
    {
        public int Index { get; set; }
        public string? FileName { get; set; }
        public long Size { get; set; }
        public bool Encrypted { get; set; }
    }

    /// <summary>
    /// 压缩包信息，包含压缩包的基本信息和检测结果
    /// </summary>
    public class ArchiveInfo
    {
        /// <summary>
        /// 压缩包格式（7z、Zip、Rar等）
        /// </summary>
        public string Format { get; set; } = "Unknown";

        /// <summary>
        /// 文件数量
        /// </summary>
        public uint FileCount { get; set; }

        /// <summary>
        /// 是否支持快速测试（是否有未加密的文件）
        /// </summary>
        public bool SupportsQuickTest { get; set; }

        /// <summary>
        /// 是否完全加密（需要密码才能打开）
        /// </summary>
        public bool IsFullyEncrypted { get; set; }

        /// <summary>
        /// 最小的加密文件索引和大小（用于快速测试）
        /// </summary>
        public KeyValuePair<uint, ulong>? MinEncryptedFile { get; set; }

        /// <summary>
        /// 压缩包是否加密
        /// </summary>
        public bool IsEncrypted { get; set; }

        /// <summary>
        /// 压缩包上下文（如果成功打开）
        /// </summary>
        public ArchiveContext? Context { get; set; }

        /// <summary>
        /// 已知的压缩包格式（用于缓存，避免重复检测）
        /// </summary>
        public string? KnownFormat { get; set; }

        /// <summary>
        /// 是否已经检测过格式
        /// </summary>
        public bool FormatDetected { get; set; }

        /// <summary>
        /// 设置已知格式
        /// </summary>
        /// <param name="format">检测到的格式</param>
        public void SetKnownFormat(string format)
        {
            KnownFormat = format;
            FormatDetected = true;
            Format = format;
        }

        /// <summary>
        /// 设置通过文件头检测到的格式
        /// </summary>
        /// <param name="format">通过文件头检测到的格式</param>
        public void SetHeaderDetectedFormat(string format)
        {
            KnownFormat = format;
            FormatDetected = true;
            Format = format;
            // DebugHelper.Log($"ArchiveInfo.SetHeaderDetectedFormat: 通过文件头检测到格式: {format}");
        }

        /// <summary>
        /// 获取优先尝试的格式（如果已知）
        /// </summary>
        public string? GetPreferredFormat()
        {
            return FormatDetected ? KnownFormat : null;
        }
    }

    /// <summary>
    /// 7-Zip 原生包装器，用于测试压缩包密码
    /// </summary>
    public static class SevenZipNative
    {
        /// <summary>
        /// 打开压缩包并返回上下文
        /// </summary>
        /// <param name="archivePath">压缩包路径</param>
        /// <param name="dllPath">7z.dll路径</param>
        /// <param name="password">打开压缩包时使用的密码（可选）</param>
        /// <returns>ArchiveContext实例</returns>
        public static ArchiveContext OpenArchive(string archivePath, string dllPath, string? password = null)
        {
            return new ArchiveContext(archivePath, dllPath, password);
        }

        /// <summary>
        /// 打开压缩包并返回上下文（支持优先格式）
        /// </summary>
        /// <param name="archivePath">压缩包路径</param>
        /// <param name="dllPath">7z.dll路径</param>
        /// <param name="password">打开压缩包时使用的密码（可选）</param>
        /// <param name="preferredFormat">优先尝试的格式（如果已知）</param>
        /// <returns>ArchiveContext实例</returns>
        public static ArchiveContext OpenArchive(string archivePath, string dllPath, string? password, string? preferredFormat)
        {
            return new ArchiveContext(archivePath, dllPath, password, preferredFormat);
        }

        /// <summary>
        /// 检测压缩包信息（格式、加密状态、文件信息等）
        /// </summary>
        /// <param name="archivePath">压缩包路径</param>
        /// <param name="dllPath">7z.dll路径</param>
        /// <param name="archiveInfo">可选的ArchiveInfo实例，用于缓存格式信息</param>
        /// <returns>ArchiveInfo实例</returns>
        public static ArchiveInfo GetArchiveInfo(string archivePath, string dllPath, ArchiveInfo? archiveInfo = null)
        {
            // DebugHelper.Log($"SevenZipNative.GetArchiveInfo开始: archivePath={archivePath}");

            archiveInfo ??= new ArchiveInfo();

            try
            {
                // 如果已经检测过格式，使用缓存的格式
                string? preferredFormat = archiveInfo.GetPreferredFormat();
                // DebugHelper.Log($"GetArchiveInfo: 优先格式={preferredFormat ?? "未指定"}");

                // 尝试使用空密码打开压缩包，使用缓存的格式（如果已知）
                using var context = OpenArchive(archivePath, dllPath, "", preferredFormat);

                // 设置已知格式到ArchiveInfo中
                archiveInfo.SetKnownFormat(context.Format);
                archiveInfo.FileCount = context.FileCount;
                archiveInfo.Context = null; // 不保留上下文，只获取信息
                archiveInfo.IsFullyEncrypted = false; // 能使用空密码打开，说明不是完全加密
                archiveInfo.SupportsQuickTest = context.FileCount > 0;

                // 获取文件信息
                var fileInfos = context.GetFileInfo();
                if (fileInfos.Length > 0)
                {
                    // 检查是否有加密的文件
                    var encryptedFiles = fileInfos.Where(f => f.Encrypted).ToList();
                    archiveInfo.IsEncrypted = encryptedFiles.Count > 0;

                    // 支持快速测试的条件：有加密文件且不是完全加密
                    archiveInfo.SupportsQuickTest = encryptedFiles.Count > 0 && !archiveInfo.IsFullyEncrypted;

                    // 找到最小的加密文件（用于快速测试）
                    if (encryptedFiles.Count > 0)
                    {
                        var minEncryptedFile = encryptedFiles.OrderBy(f => f.Size).FirstOrDefault();
                        if (minEncryptedFile != null)
                        {
                            archiveInfo.MinEncryptedFile = new KeyValuePair<uint, ulong>(
                                (uint)minEncryptedFile.Index,
                                (ulong)minEncryptedFile.Size);
                        }
                    }
                }

                // DebugHelper.Log($"GetArchiveInfo成功: 格式={archiveInfo.Format}, 文件数={archiveInfo.FileCount}, 支持快速测试={archiveInfo.SupportsQuickTest}");
            }
            catch
            {
                // DebugHelper.LogError($"使用空密码打开压缩包失败: {ex.Message}", ex);

                // 如果无法使用空密码打开，可能是完全加密的压缩包
                archiveInfo.IsFullyEncrypted = true;
                archiveInfo.IsEncrypted = true;
                archiveInfo.SupportsQuickTest = false;

                // 尝试通过文件头检测格式
                string? headerDetectedFormat = Utils.Util.DetectArchiveFormatByHeader(archivePath);
                if (headerDetectedFormat != null)
                {
                    // DebugHelper.Log($"通过文件头检测到压缩包格式: {headerDetectedFormat}");
                    archiveInfo.SetHeaderDetectedFormat(headerDetectedFormat);
                }
                else
                {
                    // DebugHelper.Log($"无法通过文件头检测压缩包格式");
                }

                // DebugHelper.Log($"压缩包可能是完全加密的，需要密码才能打开");
            }

            return archiveInfo;
        }

        /// <summary>
        /// 测试压缩包密码是否正确（使用已打开的ArchiveContext）
        /// </summary>
        /// <param name="context">已打开的ArchiveContext</param>
        /// <param name="password">要测试的密码</param>
        /// <param name="fileIndices">要测试的文件索引数组，如果为null则测试所有文件</param>
        /// <returns>true 表示密码正确，false 表示密码错误或发生错误</returns>
        public static bool TestPassword(ArchiveContext context, string password, uint[]? fileIndices = null)
        {
            // DebugHelper.Log($"SevenZipNative.TestPassword开始: password={(string.IsNullOrEmpty(password) ? "(空密码)" : "***")}, fileIndices={(fileIndices == null ? "null" : $"长度={fileIndices.Length}")}");

            return context.TestPassword(password, fileIndices);
        }

        /// <summary>
        /// 获取压缩包中的文件信息
        /// </summary>
        public static ArchiveFileInfo[] GetArchiveFileInfo(string archivePath)
        {
            // DebugHelper.Log($"SevenZipNative.GetArchiveFileInfo开始: archivePath={archivePath}");

            // 使用Program中找到的7z.dll路径
            if (Program.Current7zPath == null)
            {
                // DebugHelper.LogError("未找到7z.dll路径，请确保Program已初始化");
                return [];
            }

            try
            {
                // 对于完全加密的压缩包，我们需要在打开时提供空密码来尝试获取信息
                using var context = OpenArchive(archivePath, Program.Current7zPath, "");
                return context.GetFileInfo();
            }
            catch
            {
                // DebugHelper.LogError($"获取压缩包文件信息失败: {ex.Message}", ex);
                // 对于完全加密的压缩包，可能无法使用空密码打开，返回空数组
                return [];
            }
        }
    }
}
