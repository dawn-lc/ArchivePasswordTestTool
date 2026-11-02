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
        : Exception($"Cannot open archive: wrong password or corrupted data (HRESULT 0x{hresult:X8}).", innerException);

    /// <summary>
    /// 压缩包上下文：持有一个已打开的 7-Zip 归档对象，负责它的生命周期与密码测试。
    /// <para>
    /// 职责边界：本类只管 COM 对象的打开/关闭/加锁复用；归档内部属性怎么读、
    /// 读出什么结论由 <see cref="ArchiveInspector"/> 负责，数据载体是 <see cref="ArchiveInfo"/>。
    /// </para>
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

        private static IInArchive OpenArchive(SevenZipLibrary library, Guid formatGuid, Guid iidInArchive, IArchiveSourceStream stream, string archivePath, string? password, long streamOffset, out int openResult)
        {
            int hr = library.CreateObject(ref formatGuid, ref iidInArchive, out IInArchive? archive);

            if (hr < 0 || archive is null)
            {
                Marshal.ThrowExceptionForHR(hr);
                throw new InvalidOperationException(Resources.CreateArchiveInterfaceFailed);
            }

            // 传入真实签名位置：普通包为 0；SFX（exe stub 封装）为扫描得到的偏移
            ulong maxCheckStartPosition = unchecked((ulong)streamOffset);

            IArchiveOpenCallback openCallback = new PasswordOpenCallback(password, archivePath);

            hr = archive.Open((IInStream)stream, ref maxCheckStartPosition, openCallback);

            if (hr < 0)
            {
                // 源生成 COM 无 Marshal.ReleaseComObject 等价物：RCW 由 GC 回收。
                // 这里主动 Close 以尽快释放 7z 内部句柄（这才是真正占资源的部分）
                try
                {
                    _ = archive.Close();
                }
                catch
                {
                    // 打开失败时 Close 可能也报错，忽略
                }

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

            // 共享 Info 只带格式；签名偏移仍需按文件解析（命中检测缓存，开销极小）。
            // 偏移只在这一处用一次（传给 Open 的 maxCheckStartPosition），不需存字段
            var (format, signatureOffset) = ArchiveFormatDetector.DetectArchiveFormat(archivePath);

            if (info is null)
            {
                Type = format;
                if (Type == Guid.Empty)
                {
                    // 面向用户的错误消息必须走资源：定死中文会让 en/ja/fr/ru 用户看到中文
                    throw new InvalidOperationException(Resources.UnsupportedArchiveFormat);
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

            // 分卷压缩包（secret.7z.001 等）：7-Zip 的分卷链接逻辑在 UI 层（7z.exe 的 OpenArchive.cpp），
            // 不在 7z.dll 内，因此需由调用方自行把全部分卷拼成连续虚拟流再交给 Open。
            // 顺手记下分卷数与磁盘占用——信息面板要展示，而这里正是唯一解析过路径的地方
            bool multiVolume = TryGetVolumePaths(archivePath, out var volumePaths);
            IArchiveSourceStream stream = multiVolume
                ? new MultiVolumeInStream(volumePaths)
                : new FileInStream(archivePath);
            fileStream = stream;

            archive = OpenArchive(library, Type, SevenZipGuids.IID_IInArchive, stream, archivePath, password, signatureOffset, out int openResult);
            _openResult = openResult;

            // 注意：info 非空时绝不重新 Populate —— 共享 Info 是多线程只读对象，
            // 写共享对象会覆写 IsHeaderEncryption 导致后续迭代错误地走 Extract 分支并崩溃
            if (info is null)
            {
                Info.SignatureOffset = signatureOffset;
                Info.VolumeCount = multiVolume ? volumePaths.Count : 1;
                Info.DiskSize = multiVolume
                    ? volumePaths.Sum(path => new FileInfo(path).Length)
                    : new FileInfo(archivePath).Length;

                ArchiveInspector.Populate(archive, Info);
            }
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
            {
                return;
            }

            try
            {
                try
                {
                    _ = archive.Close();
                }
                catch
                {
                    // 忽略关闭异常
                }

                // 源生成 COM 下没有 Marshal.ReleaseComObject 等价物：
                // RCW 的引用计数由 ComWrappers 管理，最终由 GC 回收。
                // Close() 已释放 7z 内部的原生资源，这里无需（也无法）手动减计数。
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
}
