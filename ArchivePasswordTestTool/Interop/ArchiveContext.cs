using ArchivePasswordTestTool.Properties;
using System.Runtime.InteropServices;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 打开归档失败：绝大多数是密码不匹配——header 加密 / 全加密包在 Open 阶段
    /// 就校验密码，密码错误必然失败。单独异常类型让测试循环只吞掉这一类
    /// "预期内的密码失败"，不把 OOM、dll 加载失败等真实故障伪装成"未找到密码"。
    /// </summary>
    internal sealed class ArchivePasswordException(int hresult, Exception? innerException = null)
        : Exception($"Cannot open archive: wrong password or corrupted data (HRESULT 0x{hresult:X8}).", innerException)
    {
        /// <summary>7z.dll 返回的原始 HRESULT</summary>
        public int HResultCode { get; } = hresult;
    }

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
                throw new ArchivePasswordException(hr);
            }

            openResult = hr;
            return archive;
        }

        /// <summary>
        /// 打开压缩包。info 为共享的只读归档信息（多密码重开同一包时复用，
        /// 避免重复枚举条目）；为空时本实例负责检测格式并填充 Info。
        /// </summary>
        /// <param name="dllPath">7z.dll路径</param>
        /// <param name="archivePath">压缩包路径</param>
        /// <param name="password">打开压缩包时使用的密码（可选）</param>
        /// <param name="info">可选的共享归档信息（调用方已探测过时传入）</param>
        public ArchiveContext(string dllPath, string archivePath, string? password, ArchiveInfo? info = null)
        {
            if (!File.Exists(archivePath))
            {
                // 消息走资源本地化：这里原本硬编码英文，与项目其余错误提示不一致
                throw new FileNotFoundException(Resources.ArchiveFileNotFound);
            }

            // 共享 Info 只带格式；签名偏移仍需按文件解析（命中检测缓存，开销极小）
            var (format, signatureOffset) = ArchiveFormatDetector.DetectArchiveFormat(archivePath);
            _streamOffset = signatureOffset;

            if (info is null)
            {
                Type = format;
                if (Type == Guid.Empty)
                {
                    throw new InvalidOperationException("无法识别该文件的格式，请检查该文件是否是支持的压缩包！");
                }

                Info = new ArchiveInfo { Type = Type };
            }
            else
            {
                Info = info;
                Type = info.Type;
            }

            // 进程级共享库：同一路径只加载一次（详见 SevenZipLibrary）
            var library = SevenZipLibrary.GetShared(dllPath);

            archive = OpenArchive(library, Type, SevenZipGuids.IID_IInArchive, archivePath, password, _streamOffset, out var fs, out int openResult);
            fileStream = fs;
            _openResult = openResult;

            // 注意：info 非空时绝不重新 Populate —— 共享 Info 是多线程只读对象，
            // 写共享对象会覆写 IsHeaderEncryption 导致后续迭代错误地走 Extract 分支并崩溃
            if (info is null)
            {
                PopulateArchiveInfo(archive, Info);
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
            // 任何错误密码都会被误判为正确（假阳性）——已实测复现并修复。
            // 索引数组一次算好挂在 Info 上，供每个密码测试复用（原先每个密码都新建数组）
            if (foundEncrypted && foundQuickTarget)
            {
                info.QuickTestFileIndices = [minIndex];
            }
            info.SupportsQuickTest = !allEncrypted && foundQuickTarget;
        }

        /// <summary>
        /// 测试密码是否正确
        /// </summary>
        /// <param name="password">要测试的密码</param>
        /// <param name="fileIndices">要测试的文件索引数组；为空则用 Info 上缓存的快速测试目标</param>
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

                // 未显式指定时复用 Info 上预计算的索引（每次密码测试都新建数组纯属浪费）
                uint[]? indices = fileIndices is { Length: > 0 } ? fileIndices : Info.QuickTestFileIndices;

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
        /// 快速测试目标文件索引（最小的、压缩后大于 32 字节的加密文件）。
        /// 打开归档时算好一次，每个密码测试直接复用同一个数组，避免逐密码重复分配。
        /// 为空表示没有合格的快速测试目标（此时走全量测试）。
        /// </summary>
        public uint[]? QuickTestFileIndices { get; set; }

        /// <summary>
        /// 压缩包是否加密
        /// </summary>
        public bool IsEncrypted { get; set; }

    }
}
