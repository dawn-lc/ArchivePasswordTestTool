using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 压缩包上下文，管理压缩包的生命周期
    /// </summary>
    public class ArchiveContext : IDisposable
    {
        public readonly Guid Type;
        public readonly ArchiveInfo Info;

        private bool _disposed;
        private readonly IInArchive archive;
        private readonly IArchiveSourceStream? fileStream;
        private readonly SevenZipLibrary? library; // 每个实例独立的 7z.dll 库
        private readonly Lock actionLock = new();

        /// <summary>
        /// 打开压缩包的结果：S_OK(0) 表示成功。header 加密包依赖此值判断密码是否正确。
        /// </summary>
        private readonly int _openResult;

        /// <summary>
        /// 压缩数据签名偏移：普通包为 0；SFX（exe stub 封装）为扫描得到的偏移。
        /// 传入 Open 的 maxCheckStartPosition，避免处理器把 PE stub 当作归档数据。
        /// </summary>
        private readonly long _streamOffset;

        /// <summary>
        /// 收集分卷物理文件路径：传入 name.ext.001 时按此目录收集 .001..N 直至缺失。
        /// 仅当传入的是第一分卷且存在至少两个分卷时启用链接。
        /// internal 以便测试项目直接单元测试分卷收集规则。
        /// </summary>
        internal static bool TryGetVolumePaths(string firstPath, out List<string> paths)
        {
            paths = [];
            try
            {
                string dir = Path.GetDirectoryName(firstPath)!;
                string name = Path.GetFileName(firstPath);

                int dot = name.LastIndexOf('.');
                if (dot <= 0)
                {
                    return false;
                }

                string digits = name[(dot + 1)..];
                if (digits.Length is < 2 or > 3 || !digits.All(char.IsAsciiDigit))
                {
                    return false;
                }

                // 必须从第一分卷开始，从中间分卷无法拼出完整流
                if (int.Parse(digits) != 1)
                {
                    return false;
                }

                string stem = name[..dot];

                for (int i = 1; ; i++)
                {
                    string vol = Path.Combine(dir, $"{stem}.{i.ToString($"D{digits.Length}")}");
                    if (!File.Exists(vol))
                    {
                        break;
                    }
                    paths.Add(vol);
                }

                if (paths.Count > 1)
                {
                    return true;
                }

                // 契约：返回 false 时 out 列表必须为空，避免调用方误用部分匹配
                paths = [];
                return false;
            }
            catch
            {
                paths = [];
                return false;
            }
        }

        private static IInArchive OpenArchive(SevenZipLibrary library, Guid formatGuid, Guid iidInArchive, string archivePath, string? password, long streamOffset, out IArchiveSourceStream stream, out int openResult)
        {
            int hr = library.CreateObject(ref formatGuid, ref iidInArchive, out object archiveObj);

            if (hr < 0 || archiveObj == null)
            {
                Marshal.ThrowExceptionForHR(hr);
                throw new InvalidOperationException("创建 IInArchive 接口失败");
            }

            IInArchive archive = (IInArchive)archiveObj;

            // 分卷压缩包（secret.7z.001 等）：7-Zip 的分卷链接逻辑在 UI 层（7z.exe 的 OpenArchive.cpp），
            // 不在 7z.dll 内，因此需由调用方自行把全部分卷拼成连续虚拟流再交给 Open。
            stream = TryGetVolumePaths(archivePath, out var volumePaths)
                ? new MultiVolumeInStream(volumePaths)
                : new FileInStream(archivePath);

            // 传入真实签名位置：普通包为 0；SFX（exe stub 封装）为扫描得到的偏移
            ulong maxCheckStartPosition = unchecked((ulong)streamOffset);

            IArchiveOpenCallback openCallback = new PasswordOpenCallback(password, archivePath);

            hr = archive.Open((IInStream)stream, ref maxCheckStartPosition, openCallback);

            if (hr < 0)
            {
                Marshal.ReleaseComObject(archive);
                stream.Dispose();
                Marshal.ThrowExceptionForHR(hr);
            }

            openResult = hr;
            return archive;
        }
        /// <summary>
        /// 打开压缩包
        /// </summary>
        /// <param name="archivePath">压缩包路径</param>
        /// <param name="dllPath">7z.dll路径</param>
        /// <param name="password">打开压缩包时使用的密码（可选）</param>
        public ArchiveContext(string dllPath, string archivePath, string? password)
        {
            try
            {
                // 创建独立的 7z.dll 库实例
                var lib = new SevenZipLibrary(dllPath);
                library = lib;

                if (!File.Exists(archivePath))
                {
                    throw new FileNotFoundException($"Archive file not found: {archivePath}");
                }

                Guid iidInArchive = SevenZipGuids.IID_IInArchive;

                var (format, signatureOffset) = ArchiveFormatDetector.DetectArchiveFormat(archivePath);
                Type = format;
                _streamOffset = signatureOffset;

                if (Type == Guid.Empty)
                {
                    throw new InvalidOperationException("无法识别该文件的格式，请检查该文件是否是支持的压缩包！");
                }

                archive = OpenArchive(lib, Type, iidInArchive, archivePath, password, _streamOffset, out var fs, out int openResult);
                fileStream = fs;
                _openResult = openResult;

                Info = new ArchiveInfo() { Type = Type };

                PopulateArchiveInfo(archive, Info);
            }
            catch
            {
                // 半构造失败时释放已加载的 7z.dll，避免每次失败叠加一个 FreeLibrary 泄漏
                library?.Dispose();
                throw;
            }
        }

        public ArchiveContext(string dllPath, string archivePath, ArchiveInfo? info, string? password)
        {
            try
            {
                // 创建独立的 7z.dll 库实例
                var lib = new SevenZipLibrary(dllPath);
                library = lib;

                if (!File.Exists(archivePath))
                {
                    throw new FileNotFoundException($"Archive file not found: {archivePath}");
                }

                Guid iidInArchive = SevenZipGuids.IID_IInArchive;

                if (info != null)
                {
                    Info = info;
                    Type = info.Type;

                    // 共享 Info 只带格式，签名偏移仍需按文件解析（命中检测缓存，开销极小）
                    _streamOffset = ArchiveFormatDetector.DetectArchiveFormat(archivePath).Offset;
                }
                else
                {
                    var (format, signatureOffset) = ArchiveFormatDetector.DetectArchiveFormat(archivePath);
                    Type = format;
                    _streamOffset = signatureOffset;
                    if (Type == Guid.Empty)
                    {
                        throw new InvalidOperationException("无法识别该文件的格式，请检查该文件是否是支持的压缩包！");
                    }
                    Info = new ArchiveInfo { Type = Type };
                }

                // Open archive
                archive = OpenArchive(lib, Type, iidInArchive, archivePath, password, _streamOffset, out var fs, out int openResult2);
                fileStream = fs;
                _openResult = openResult2;

                // 注意：info 非空时绝不重新 Populate —— 共享 Info 是多线程只读对象，
                // 写共享对象会覆写 IsHeaderEncryption 导致后续迭代错误地走 Extract 分支并崩溃
            }
            catch
            {
                library?.Dispose();
                throw;
            }
        }

        private static void PopulateArchiveInfo(IInArchive archive, ArchiveInfo info)
        {
            archive.GetNumberOfItems(out uint fileCount);
            info.FileCount = fileCount;

            if (fileCount == 0)
            {
                info.IsEncrypted = true;
                info.IsHeaderEncryption = true;
                info.SupportsQuickTest = false;
                return;
            }

            bool foundEncrypted = false;
            bool allEncrypted = true;
            ulong minSize = ulong.MaxValue;
            uint minIndex = 0;
            bool foundQuickTarget = false;

            for (uint i = 0; i < fileCount; i++)
            {
                PropVariant enc = new();
                try
                {
                    archive.GetProperty(i, ItemPropId.Encrypted, ref enc);
                    bool encrypted = enc.VarType == VarEnum.VT_BOOL && enc.Bool;

                    if (encrypted)
                    {
                        foundEncrypted = true;
                        PropVariant size = new();
                        try
                        {
                            archive.GetProperty(i, ItemPropId.PackedSize, ref size);
                            ulong s = size.UInt64;
                            if (s > 32 && s < minSize)
                            {
                                minSize = s;
                                minIndex = i;
                                foundQuickTarget = true;
                            }
                        }
                        finally
                        {
                            size.Clear();
                        }
                    }
                    else
                    {
                        allEncrypted = false;
                    }
                }
                finally
                {
                    enc.Clear();
                }
            }

            info.IsEncrypted = foundEncrypted;

            // 判定 header 是否加密：7-Zip 在 header 加密时会通过
            // GetArchiveProperty(kpidEncrypted) 返回 VT_BOOL true；
            // zip 等其余格式无此 archive 级属性（VT_EMPTY）。
            // （原实现 fileCount < 1 && allEncrypted 是错的：正常包 fileCount>=1 恒为 false）
            bool headerEncrypted = false;
            PropVariant hdr = new();
            try
            {
                archive.GetArchiveProperty((uint)ItemPropId.Encrypted, ref hdr);
                headerEncrypted = hdr.VarType == VarEnum.VT_BOOL && hdr.Bool;
            }
            finally
            {
                hdr.Clear();
            }
            info.IsHeaderEncryption = headerEncrypted;

            // 仅当存在合格（压缩后 >32 字节）的加密文件时才建立快速测试目标：
            // 条件不成立时若还无条件赋值，minIndex 会停留在初值 0，
            // 一旦 0 号文件恰好未加密，密码测试将打到未加密文件上，
            // 任何错误密码都会被误判为正确（假阳性）——已实测复现并修复
            if (foundEncrypted && foundQuickTarget)
            {
                info.MinEncryptedFile = new KeyValuePair<uint, ulong>(minIndex, minSize);
            }
            info.SupportsQuickTest = !allEncrypted && foundQuickTarget;
        }

        /// <summary>
        /// 测试密码是否正确
        /// </summary>
        /// <param name="password">要测试的密码</param>
        /// <param name="fileIndices">要测试的文件索引数组，如果为null则自动选择一个加密文件</param>
        /// <returns>true 表示密码正确，false 表示密码错误或发生错误</returns>
        public bool TestPassword(string password, uint[]? fileIndices = null)
        {
            lock (actionLock)
            {
                if (Info.IsHeaderEncryption)
                {
                    // header 加密压缩包：Open 阶段即验证密码。
                    // S_OK = header 解密成功（密码正确）；S_FALSE 或其它 = 密码错误/解析失败。
                    // 不能用 FileCount 判定——错误密码打开时拿不到 items，FileCount 可能是 0。
                    return _openResult == 0;
                }

                uint[]? indices = null;

                // 如果没有指定文件，自动选择最小的加密文件
                if (fileIndices == null || fileIndices.Length == 0)
                {
                    if (Info.MinEncryptedFile.HasValue)
                    {
                        indices = [Info.MinEncryptedFile.Value.Key];
                    }
                }
                else
                {
                    indices = fileIndices;
                }

                uint numItems = indices is null ? 0xFFFFFFFF : (uint)indices.Length;

                var callback = new TestExtractCallback(password);
                try
                {
                    int hr = archive.Extract(indices, numItems, 1, callback); // 1 = test mode

                    if (hr != 0)
                    {
                        return false;
                    }

                    return callback.LastOperationResult == OperationResult.OK;
                }
                finally
                {
                    callback.Cleanup();
                }
            }
        }
        /// <summary>
        /// 清理资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                // 使用局部变量捕获_archive的值，避免编译器警告
                var archive = this.archive;
                if (archive != null)
                {
                    try
                    {
                        archive.Close();
                    }
                    catch
                    {
                        // 忽略关闭异常
                    }
                    Marshal.ReleaseComObject(archive);
                }

                fileStream?.Dispose();
                library?.Dispose();
            }
            catch
            {
                // 忽略清理异常
            }
            finally
            {
                _disposed = true;
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
        /// 压缩包格式对应的GUID
        /// </summary>
        public Guid Type { get; set; } = Guid.Empty;

        /// <summary>
        /// GUID对应的压缩包格式
        /// </summary>
        public string Format
        {
            get
            {
                return SevenZipFormats.GuidToName[Type];
            }
        }

        /// <summary>
        /// 文件数量
        /// </summary>
        public uint FileCount { get; set; }

        /// <summary>
        /// 是否走快速测试路径：非全量加密 且 存在压缩后大于 32 字节的加密文件
        /// （密码校验仅针对最小加密文件，避开逐密码全量提取）
        /// </summary>
        public bool SupportsQuickTest { get; set; }

        /// <summary>
        /// 是否加密头部
        /// </summary>
        public bool IsHeaderEncryption { get; set; }

        /// <summary>
        /// 最小的加密文件索引和大小（用于快速测试）
        /// </summary>
        public KeyValuePair<uint, ulong>? MinEncryptedFile { get; set; }

        /// <summary>
        /// 压缩包是否加密
        /// </summary>
        public bool IsEncrypted { get; set; }

    }
}
