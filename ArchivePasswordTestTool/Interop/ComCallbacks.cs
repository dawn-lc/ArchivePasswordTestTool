using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

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
    /// <para>
    /// [GeneratedComClass] 是 Native AOT 的前提：运行时 CCW（[ComVisible]+[ClassInterface]）
    /// 依赖内置 COM 封送，AOT 下直接报 "COM Interop requires ComWrapper instance registered
    /// for marshalling"。源生成版本编译期产出 CCW stub，AOT 可用。
    /// </para>
    /// <para>
    /// 也不需要 GCHandle 手动固定：CCW 存活期间 ComWrappers 持有托管对象的强引用。
    /// </para>
    /// </summary>
    [GeneratedComClass]
    internal sealed unsafe partial class FileInStream : IInStream, IArchiveSourceStream
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

        public FileInStream(string filePath)
        {
            _stream = File.OpenRead(filePath);
            _filePath = filePath;
        }

        public FileInStream(Stream stream)
        {
            _stream = stream;
            _filePath = null;
        }

        public int Read(byte* data, uint size, uint* processedSize)
        {
            ComTrace($"FileInStream.Read size={size}");

            // native 缓冲区由调用方保证至少可写 size 字节
            int request = size > int.MaxValue ? int.MaxValue : (int)size;
            int read = 0;

            if (request > 0 && data is not null)
            {
                read = _stream.Read(new Span<byte>(data, request));
            }

            // 必须回写实际读取字节数，否则 7z 内部会读到未初始化的栈值导致内存越界
            if (processedSize is not null)
            {
                *processedSize = (uint)read;
            }

            return 0; // S_OK
        }

        public int Seek(long offset, uint seekOrigin, ulong* newPosition)
        {
            ComTrace($"FileInStream.Seek offset={offset} origin={seekOrigin}");

            SeekOrigin origin = (SeekOrigin)seekOrigin;
            long position = _stream.Seek(offset, origin);

            if (newPosition is not null)
            {
                *newPosition = (ulong)position;
            }

            return 0; // S_OK
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }

    /// <summary>
    /// 多分卷连续流包装器：把 name.ext.001/.002/... 拼成单个虚拟 IInStream。
    /// 7-Zip 对 .7z/.zip 分卷的链接逻辑位于 UI 层（7z.exe 的 OpenArchive.cpp，拼装后递归打开内部归档），
    /// 不在 7z.dll 内，因此直接调用 IInArchive.Open 时必须由调用方自行拼接分卷流。
    /// </summary>
    [GeneratedComClass]
    internal sealed unsafe partial class MultiVolumeInStream : IInStream, IArchiveSourceStream
    {
        private readonly FileStream[] _streams;
        private readonly long[] _starts;    // 每个分卷在虚拟流中的起始偏移
        private readonly long _total;
        private long _position;
        private bool _disposed;

        public MultiVolumeInStream(IReadOnlyList<string> paths)
        {
            _streams = new FileStream[paths.Count];
            _starts = new long[paths.Count];

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

        public int Read(byte* data, uint size, uint* processedSize)
        {
            FileInStream.ComTrace($"MultiVolumeInStream.Read size={size} pos={_position}");

            int totalRead = 0;
            int request = size > int.MaxValue ? int.MaxValue : (int)size;

            while (totalRead < request && data is not null)
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

                // 直接写进 native 缓冲区，省掉一次 ArrayPool 租借 + Marshal.Copy
                var span = new Span<byte>(data + totalRead, chunk);
                int read = _streams[vol].Read(span);

                if (read <= 0)
                {
                    break;
                }

                _position += read;
                totalRead += read;

                if (read < chunk)
                {
                    break; // 部分读取（可能到分卷文件末尾）
                }
            }

            // 必须回写实际读取字节数，否则 7z 内部会当作错误处理
            if (processedSize is not null)
            {
                *processedSize = (uint)totalRead;
            }

            return 0; // S_OK
        }

        public int Seek(long offset, uint seekOrigin, ulong* newPosition)
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

            if (newPosition is not null)
            {
                *newPosition = (ulong)_position;
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
        }
    }

    /// <summary>
    /// 密码提供者，实现 ICryptoGetTextPassword 接口
    /// </summary>
    [GeneratedComClass]
    internal sealed partial class PasswordProvider(string password) : ICryptoGetTextPassword
    {
        private readonly string _password = password;

        public int CryptoGetTextPassword(out string password)
        {
            password = _password;
            return 0; // 返回 0 表示成功
        }
    }

    [GeneratedComClass]
    internal sealed unsafe partial class DummyOutStream : ISequentialOutStream, IDisposable
    {
        public int Write(byte* data, uint size, uint* processedSize)
        {
            FileInStream.ComTrace($"DummyOutStream.Write size={size}");

            if (processedSize is not null)
            {
                *processedSize = size;
            }

            return 0; // S_OK
        }

        public void Dispose()
        {
        }
    }
    /// <summary>
    /// 简单的提取回调，用于测试模式
    /// </summary>
    [GeneratedComClass]
    internal sealed unsafe partial class TestExtractCallback : IArchiveExtractCallback, ICryptoGetTextPassword
    {
        private readonly string _password;
        private OperationResult _lastOperationResult;
        private readonly List<DummyOutStream> _outStreams;

        public int ProcessedFiles { get; private set; }

        public TestExtractCallback(string password)
        {
            _password = password;
            _lastOperationResult = OperationResult.OK;
            _outStreams = [];
        }

        public OperationResult LastOperationResult => _lastOperationResult;

        public int SetTotal(ulong total)
        {
            FileInStream.ComTrace($"TestExtractCallback.SetTotal total={total}");
            return 0; // S_OK
        }

        public int SetCompleted(ulong* completeValue)
        {
            FileInStream.ComTrace($"TestExtractCallback.SetCompleted value={(completeValue is null ? 0 : *completeValue)}");
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

        public int CryptoGetTextPassword(out string password)
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
    [GeneratedComClass]
    internal sealed unsafe partial class PasswordOpenCallback : IArchiveOpenCallback, ICryptoGetTextPassword, IArchiveOpenVolumeCallback
    {
        private readonly string _password;
        private readonly string _archivePath;

        public PasswordOpenCallback(string? password, string archivePath)
        {
            _password = password ?? string.Empty;
            _archivePath = archivePath;
        }

        public int SetTotal(ulong* files, ulong* bytes)
        {
            FileInStream.ComTrace("PasswordOpenCallback.SetTotal");
            return 0; // S_OK
        }

        public int SetCompleted(ulong* files, ulong* bytes)
        {
            FileInStream.ComTrace("PasswordOpenCallback.SetCompleted");
            return 0; // S_OK
        }

        public int CryptoGetTextPassword(out string password)
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
