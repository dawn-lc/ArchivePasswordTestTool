using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 压缩包源流标记：同时具备 IInStream(COM) 与 IDisposable 能力。
    /// 受 ComImport 接口派生限制无法用单个托管接口同时继承两者，
    /// 调用需要 IInStream 参数处需显式转型。
    /// </summary>
    internal interface IArchiveSourceStream : IDisposable
    {
    }

    /// <summary>
    /// 文件流包装器，实现 IInStream 接口
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal class FileInStream : IInStream, IArchiveSourceStream
    {
        // APTT_TRACE=1 时输出 COM 回调跟踪到 stderr（仅诊断用）
        internal static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("APTT_TRACE") == "1";

        internal static void ComTrace(string message)
        {
            if (TraceEnabled)
            {
                Console.Error.WriteLine($"[TRACE:{Environment.CurrentManagedThreadId:00}] {message}");
            }
        }

        private readonly Stream _stream;
        private readonly string? _filePath;
        private GCHandle _gcHandle;

        public FileInStream(string filePath)
        {
            _stream = File.OpenRead(filePath);
            _filePath = filePath;
            _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        }

        public FileInStream(Stream stream)
        {
            _stream = stream;
            _filePath = null;
            _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        }

        public int Read(IntPtr data, uint size, IntPtr processedSize)
        {
            ComTrace($"FileInStream.Read size={size}");

            // native 缓冲区由调用方保证至少可写 size 字节
            int request = size > int.MaxValue ? int.MaxValue : (int)size;
            int read = 0;

            if (request > 0)
            {
                var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(request);
                try
                {
                    read = _stream.Read(buf, 0, request);
                    if (read > 0)
                    {
                        Marshal.Copy(buf, 0, data, read);
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buf);
                }
            }

            // 必须回写实际读取字节数，否则 7z 内部会读到未初始化的栈值导致内存越界
            if (processedSize != IntPtr.Zero)
            {
                Marshal.WriteInt32(processedSize, read);
            }

            return 0; // S_OK
        }

        public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
        {
            ComTrace($"FileInStream.Seek offset={offset} origin={seekOrigin}");

            SeekOrigin origin = (SeekOrigin)seekOrigin;
            long position = _stream.Seek(offset, origin);

            if (newPosition != IntPtr.Zero)
            {
                Marshal.WriteInt64(newPosition, position);
            }

            return 0; // S_OK
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
    /// 多分卷连续流包装器：把 name.ext.001/.002/... 拼成单个虚拟 IInStream。
    /// 7-Zip 对 .7z/.zip 分卷的链接逻辑位于 UI 层（7z.exe 的 OpenArchive.cpp，拼装后递归打开内部归档），
    /// 不在 7z.dll 内，因此直接调用 IInArchive.Open 时必须由调用方自行拼接分卷流。
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal class MultiVolumeInStream : IInStream, IArchiveSourceStream
    {
        private readonly FileStream[] _streams;
        private readonly long[] _starts;    // 每个分卷在虚拟流中的起始偏移
        private readonly long _total;
        private long _position;
        private GCHandle _gcHandle;
        private bool _disposed;

        public MultiVolumeInStream(IReadOnlyList<string> paths)
        {
            _streams = new FileStream[paths.Count];
            _starts = new long[paths.Count];
            _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);

            long pos = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                _starts[i] = pos;
                _streams[i] = File.OpenRead(paths[i]);
                pos += _streams[i].Length;
            }

            _total = pos;
        }

        private int FindVolume(long pos)
        {
            for (int i = _streams.Length - 1; i >= 0; i--)
            {
                if (pos >= _starts[i])
                {
                    return i;
                }
            }

            return -1;
        }

        public int Read(IntPtr data, uint size, IntPtr processedSize)
        {
            FileInStream.ComTrace($"MultiVolumeInStream.Read size={size} pos={_position}");

            int totalRead = 0;
            int request = size > int.MaxValue ? int.MaxValue : (int)size;

            while (totalRead < request && data != IntPtr.Zero)
            {
                int vol = FindVolume(_position);

                if (vol < 0)
                {
                    break; // 已到流末尾
                }

                long within = _position - _starts[vol];
                long avail = _streams[vol].Length - within;

                if (avail <= 0)
                {
                    _position = _starts[vol] + _streams[vol].Length;
                    continue;
                }

                int chunk = (int)Math.Min(avail, request - totalRead);
                _streams[vol].Seek(within, SeekOrigin.Begin);

                var buf = new byte[chunk];
                int read = _streams[vol].Read(buf, 0, chunk);

                if (read <= 0)
                {
                    break;
                }

                Marshal.Copy(buf, 0, data + totalRead, read);
                _position += read;
                totalRead += read;

                if (read < chunk)
                {
                    break; // 部分读取（可能到分卷文件末尾）
                }
            }

            // 必须回写实际读取字节数，否则 7z 内部会当作错误处理
            if (processedSize != IntPtr.Zero)
            {
                Marshal.WriteInt32(processedSize, totalRead);
            }

            return 0; // S_OK
        }

        public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
        {
            // STREAM_SEEK_SET=0 / STREAM_SEEK_CUR=1 / STREAM_SEEK_END=2，与 .NET SeekOrigin 数值一致
            long target = seekOrigin switch
            {
                0 => offset,
                1 => _position + offset,
                2 => _total + offset,
                _ => throw new InvalidOperationException($"未知的 seekOrigin: {seekOrigin}")
            };

            _position = Math.Clamp(target, 0, _total);

            if (newPosition != IntPtr.Zero)
            {
                Marshal.WriteInt64(newPosition, _position);
            }

            return 0; // S_OK
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var stream in _streams)
            {
                stream.Dispose();
            }

            if (_gcHandle.IsAllocated)
            {
                _gcHandle.Free();
            }
        }

        ~MultiVolumeInStream()
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

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal class DummyOutStream : ISequentialOutStream, IDisposable
    {
        private GCHandle _gcHandle;
        private bool _disposed;

        public DummyOutStream()
        {
            // 固定对象，防止在COM调用期间被垃圾回收
            _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        }

        public int Write(IntPtr data, uint size, IntPtr processedSize)
        {
            FileInStream.ComTrace($"DummyOutStream.Write size={size}");

            if (processedSize != IntPtr.Zero)
                Marshal.WriteInt32(processedSize, (int)size);

            return 0; // S_OK
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_gcHandle.IsAllocated)
            {
                _gcHandle.Free();
            }

            _disposed = true;
        }

        ~DummyOutStream()
        {
            if (_gcHandle.IsAllocated)
            {
                _gcHandle.Free();
            }
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
        private readonly List<DummyOutStream> _outStreams;

        public int ProcessedFiles { get; private set; }

        public TestExtractCallback(string password)
        {
            _password = password;
            _lastOperationResult = OperationResult.OK;
            _outStreams = [];
            _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
        }

        ~TestExtractCallback()
        {
            if (_gcHandle.IsAllocated)
                _gcHandle.Free();
        }

        public OperationResult LastOperationResult => _lastOperationResult;

        public int SetTotal(ulong total)
        {
            FileInStream.ComTrace($"TestExtractCallback.SetTotal total={total}");
            return 0; // S_OK
        }

        public int SetCompleted([In] ref ulong completeValue)
        {
            FileInStream.ComTrace($"TestExtractCallback.SetCompleted value={completeValue}");
            return 0; // S_OK
        }

        public int GetStream(uint index, out ISequentialOutStream? outStream, AskMode askExtractMode)
        {
            FileInStream.ComTrace($"TestExtractCallback.GetStream index={index} mode={askExtractMode}");

            // Skip 模式直接跳过
            if (askExtractMode == AskMode.Skip)
            {
                outStream = null;
                return 0;
            }

            // Extract 和 Test 都需要流
            ProcessedFiles++;
            var dummyStream = new DummyOutStream();
            _outStreams.Add(dummyStream);
            outStream = dummyStream;
            return 0;
        }

        public int PrepareOperation(AskMode askExtractMode)
        {
            FileInStream.ComTrace($"TestExtractCallback.PrepareOperation mode={askExtractMode}");
            return 0; // S_OK
        }

        public int SetOperationResult(OperationResult operationResult)
        {
            FileInStream.ComTrace($"TestExtractCallback.SetOperationResult result={operationResult}");
            _lastOperationResult = operationResult;
            return 0; // S_OK
        }

        public int CryptoGetTextPassword(
            [MarshalAs(UnmanagedType.BStr)] out string password)
        {
            password = _password;
            return 0;
        }

        public void Cleanup()
        {
            foreach (var stream in _outStreams)
            {
                stream.Dispose();
            }
            _outStreams.Clear();
        }
    }

    /// <summary>
    /// 支持密码与多分卷的打开回调。
    /// 分卷压缩包的 7z 流头部引用的尾头部在最后一个分卷中，
    /// 7-Zip 必须通过 IArchiveOpenVolumeCallback 把全部分卷链接成连续虚拟流才能解析。
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal class PasswordOpenCallback : IArchiveOpenCallback, ICryptoGetTextPassword, IArchiveOpenVolumeCallback
    {
        private readonly string _password;
        private readonly string _archivePath;

        public PasswordOpenCallback(string? password, string archivePath)
        {
            _password = password ?? string.Empty;
            _archivePath = archivePath;
        }

        public int SetTotal(IntPtr files, IntPtr bytes)
        {
            FileInStream.ComTrace("PasswordOpenCallback.SetTotal");
            return 0; // S_OK
        }

        public int SetCompleted(IntPtr files, IntPtr bytes)
        {
            FileInStream.ComTrace("PasswordOpenCallback.SetCompleted");
            return 0; // S_OK
        }

        public int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password)
        {
            FileInStream.ComTrace("PasswordOpenCallback.CryptoGetTextPassword");
            password = _password;
            return 0; // S_OK
        }

        // ---------- IArchiveOpenVolumeCallback ----------

        public int GetProperty(uint propID, ref PropVariant value)
        {
            // 7-Zip 请求 kpidName 获取当前分卷的文件名，
            // 用于推导后续分卷名（如 secret_split.7z.001 → secret_split.7z.002）
            if (propID == (uint)ItemPropId.Name)
            {
                value.vt = (ushort)VarEnum.VT_BSTR;
                value.pointerValue = Marshal.StringToBSTR(Path.GetFileName(_archivePath));
            }
            else
            {
                value.vt = 0; // VT_EMPTY
            }

            return 0; // S_OK
        }

        public int GetStream([MarshalAs(UnmanagedType.LPWStr)] string name, out IInStream? inStream)
        {
            FileInStream.ComTrace($"PasswordOpenCallback.GetStream {name}");
            inStream = null;

            // 7-Zip 传入的是分卷文件名（相对名），按第一个分卷所在目录解析
            string path = name;
            if (!Path.IsPathRooted(path))
            {
                string? dir = Path.GetDirectoryName(_archivePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    path = Path.Combine(dir, name);
                }
            }

            if (!File.Exists(path))
            {
                return 1; // S_FALSE：分卷缺失，按 7-Zip 约定返回 S_FALSE
            }

            inStream = new FileInStream(path);
            return 0; // S_OK
        }
    }
}
