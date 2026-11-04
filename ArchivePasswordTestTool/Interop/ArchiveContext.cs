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
    /// 归档本身不完整或不可读：分卷缺失、文件被截断、结构损坏。
    /// <para>
    /// 单独立一类是因为它和「密码错误」的结论完全相反：密码错误要换字典，文件不全只能去补文件。
    /// 拿不到条目时若一律当成「header 加密」，每个密码试下来都是 false，最终会把
    /// 「你的文件不全」报成「未找到正确的解压密码」（而且是确定口吻、退出码 0）。
    /// </para>
    /// </summary>
    internal sealed class ArchiveIncompleteException(string message) : Exception(message);

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

        /// <summary>卷号的最大位数（<see cref="int"/> 解析上限；真实分卷不会超过这个量级）</summary>
        private const int MaxVolumeDigits = 9;

        /// <summary>
        /// 分卷命名：<c>&lt;目录&gt;&lt;前缀&gt;.NNN</c>，最后一个点之后是卷号。
        /// </summary>
        /// <param name="Directory">分卷所在目录</param>
        /// <param name="Stem">卷号之前的部分（含 <c>.7z</c> 这类真扩展名）</param>
        /// <param name="StartNumber">首卷编号（0 或 1）</param>
        /// <param name="Width">首卷卷号的位数，决定零填充宽度；数值超出宽度时自然变长</param>
        internal readonly record struct VolumeNaming(string Directory, string Stem, int StartNumber, int Width);

        /// <summary>
        /// 收集分卷物理文件路径：传入 <c>name.ext.001</c> 时按此目录收集后续卷直至缺失。
        /// 仅当传入的是第一分卷且存在至少两个分卷时启用链接。
        /// internal 以便测试项目直接单元测试分卷收集规则。
        /// </summary>
        internal static bool TryGetVolumePaths(string firstPath, out List<string> paths)
        {
            paths = [];

            if (!TryParseVolumeNaming(firstPath, out var naming))
            {
                return false;
            }

            try
            {
                // 上界用 number >= 0 而不是无限递增：到达 int.MaxValue 后自增变负即退出，
                // 不可能因目录里存在畸形大号而越界
                for (int number = naming.StartNumber; number >= 0; number++)
                {
                    string volume = VolumePath(naming, number);
                    if (!File.Exists(volume))
                    {
                        break;
                    }
                    paths.Add(volume);
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

        /// <summary>
        /// 卷号 → 路径。<c>D{width}</c> 在数值超出宽度时自然变长，与 7-Zip
        /// <c>CSeqName::GetNextName</c>（最高位进位时 <c>InsertAtFront('1')</c>）行为一致：
        /// <c>.999</c> 的下一个卷是 <c>.1000</c>。
        /// </summary>
        private static string VolumePath(VolumeNaming naming, int number) =>
            Path.Combine(naming.Directory, $"{naming.Stem}.{number.ToString($"D{naming.Width}")}");

        /// <summary>
        /// 已收集的分卷是否缺了中间的号：收集逻辑遇到第一个不存在的号就停，
        /// 因此缺 .004 时只会拿到 .001-.003 而看上去一切正常。
        /// <para>
        /// 这一步 7-Zip 没有：它的 <c>CSeqName</c> 遇到 <c>GetStream</c> 返回 <c>S_FALSE</c> 就静默停，
        /// 于是缺卷的归档会被当成「完整但内容不完整」，最终把「文件不全」报成「密码不对」。
        /// 保留它是有意的差异。
        /// </para>
        /// <para>
        /// <paramref name="collected"/> 为空也有效：那表示只确认了首卷本身存在（后续卷缺失导致
        /// 拼流没启用）。此时缺口从「首卷 + 1」开始找，于是 <c>.001</c> + <c>.003</c>（缺 <c>.002</c>）
        /// 也能报出具体缺哪一卷，而不是笼统地说「归档不完整」。
        /// </para>
        /// </summary>
        /// <param name="missing">缺失的第一卷完整路径（按编号顺序）</param>
        internal static bool TryFindMissingVolume(string firstPath, IReadOnlyList<string> collected, out string missing)
        {
            missing = string.Empty;

            if (!TryParseVolumeNaming(firstPath, out var naming))
            {
                return false;
            }

            try
            {
                // 已收集的是从首卷开始的连续前缀，所以缺口只可能从它的下一个号开始；
                // 目录里存在比它更大的号，就说明那里断过。collected 为空时表示只拿到了首卷
                int expected = naming.StartNumber + Math.Max(collected.Count, 1);

                foreach (string candidate in Directory.EnumerateFiles(naming.Directory, $"{naming.Stem}.*"))
                {
                    if (TryParseVolumeNumber(Path.GetFileName(candidate), naming.Stem, out int number) && number > expected)
                    {
                        missing = VolumePath(naming, expected);
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从文件名解析卷号。不比较位宽：<c>.999</c> 与 <c>.1000</c> 属于同一序列，
        /// 只按「前缀匹配 + 后缀全数字」认。
        /// </summary>
        private static bool TryParseVolumeNumber(string fileName, string stem, out int number)
        {
            number = 0;

            if (fileName.Length <= stem.Length + 1 || !fileName.StartsWith($"{stem}.", StringComparison.Ordinal))
            {
                return false;
            }

            string digits = fileName[(stem.Length + 1)..];

            return digits.Length <= MaxVolumeDigits
                && digits.All(char.IsAsciiDigit)
                && int.TryParse(digits, out number);
        }

        /// <summary>
        /// 解析 <c>name.ext.001</c> 这类分卷命名。
        /// <para>
        /// 判据对齐 7-Zip 的 <c>CSeqName</c>（<c>CPP/7zip/Archive/SplitHandler.cpp</c>）：最后一个点之后
        /// 必须<b>整段都是数字</b>且长度 ≥ 2，且数值为 0 或 1——7-Zip 的写法是「末两位是 <c>00</c>/<c>01</c>
        /// 且更前面的位全是 <c>0</c>」，与「数值 ∈ {0,1}」完全等价。位宽不设上限：7-Zip 也不限，
        /// <c>.0001</c> 是合法的四位卷号。允许从 0 开始也是对齐它的（Unix <c>split -d</c> 生成 <c>.00</c> 起）。
        /// </para>
        /// <para>
        /// 比 7-Zip 更严的一点：<b>首卷编号必须是 0 或 1</b>。它的 <c>CSeqName</c> 直接拿传入的文件名
        /// 当首卷往下递增，传入 <c>.005</c> 会从 <c>.006</c> 开始收集，得到的是错位的流；
        /// 从中间分卷拼不出完整归档，所以这里直接拒绝。
        /// </para>
        /// </summary>
        private static bool TryParseVolumeNaming(string firstPath, out VolumeNaming naming)
        {
            naming = default;

            try
            {
                string directory = Path.GetDirectoryName(firstPath)!;
                string name = Path.GetFileName(firstPath);

                int dot = name.LastIndexOf('.');
                if (dot <= 0)
                {
                    return false;
                }

                string digits = name[(dot + 1)..];

                // 长度 ≥ 2：7-Zip 同样拒绝单数字后缀，因为无法与普通扩展名区分
                if (digits.Length is < 2 or > MaxVolumeDigits || !digits.All(char.IsAsciiDigit))
                {
                    return false;
                }

                if (!int.TryParse(digits, out int number) || number > 1)
                {
                    return false;
                }

                naming = new VolumeNaming(directory, name[..dot], number, digits.Length);
                return true;
            }
            catch
            {
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

            // 分卷压缩包（secret.7z.001 等）：7-Zip 的分卷链接逻辑因格式而异——
            // 7z / zip 在 UI 层（7z.exe 的 OpenArchive.cpp 拼出 CMultiStream 再递归打开，7z.dll 的 handler
            // 收到流后不会去找下一卷），必须由调用方拼成可 Seek 的连续虚拟流；
            // RAR 在 handler 内部（它 QI IArchiveOpenVolumeCallback 并按 .partN / .rNN 自己推导）。
            // 但后者的标准命名不命中下面的「纯数字后缀」规则，天然不会被拼流，所以不必按格式分叉。
            // 唯一重叠的情形是「RAR 分卷被改名成 .001」：实测拼流与不拼流对密码判定的结果相同
            // （handler 都会按自己的规则去请求 X.r00 并失败，只读到首卷的条目，见 NumericSuffixRarVolumesAreAnUnsupportedNaming），
            // 而拼流能给出准确的分卷数与磁盘占用，所以这里统一拼流。
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

                // 非归档数据的范围只用于展示与分离：分卷时多个文件拼成一条流，「某文件末尾」没有意义；
                // 终点未知（头部加密的 RAR5 等）时只保留起始偏移，尾随量留空而不是猜一个数。
                // 同一份探测结果同时用于下面的 0 条目分类，不重复解析一遍
                ArchiveExtent? extent = null;
                if (!multiVolume)
                {
                    extent = ArchiveExtentDetector.Detect(archivePath, Type, signatureOffset);
                    Info.TrailingBytes = extent?.TrailingBytes(Info.DiskSize);
                }

                ArchiveInspector.Populate(archive, Info);

                // 拿不到条目不等于「header 加密」：分卷不全、文件截断同样得到 0 条目。
                // 必须在建上下文时就分清，否则后续每个密码都会被判错。
                // 只在无密码探测时判定：带候选密码重开时 0 条目本来就是「密码错」的表现，
                // 在那条路径上抛异常会把正常的错密码变成致命错误
                if (Info.FileCount == 0 && password is null)
                {
                    ReclassifyZeroItems(archivePath, multiVolume, volumePaths, extent);
                }
            }
        }

        /// <summary>
        /// 0 条目的三种真实原因只能靠证据区分：格式自证 header 加密 / 结构残缺 / 本来就没有条目。
        /// <para>
        /// <see cref="ArchiveInspector.Populate"/> 只能看到「没有条目」，看不到原因，于是它只能
        /// 按最常见的一种（header 加密）填结论。这个默认在残缺文件上会变成一条假结论：
        /// <see cref="TestPassword"/> 的 header 加密分支只回读 Open 的结果，于是每个密码都是 false，
        /// 最后报「未找到正确的解压密码」——把「文件不全」说成「密码不对」。
        /// </para>
        /// <para>
        /// 判据优先级从硬到软：缺卷（文件名层面确凿）→ 格式自证加密（结构层面确凿）→
        /// 结构走不通（说明文件不全）→ 无密码即可打开（说明真的没有条目）→ 剩下的只能承认是 header 加密。
        /// </para>
        /// </summary>
        private void ReclassifyZeroItems(string archivePath, bool multiVolume, List<string> volumePaths, ArchiveExtent? extent)
        {
            // 缺卷判定不要求拼流已启用：多卷时 collected 是连续前缀，只拿到首卷时它为空，
            // 两种情况都能指出缺的是哪一卷。单卷归档（后缀非数字）这里直接不命中
            if (TryFindMissingVolume(archivePath, volumePaths, out string missing))
            {
                throw new ArchiveIncompleteException(string.Format(Resources.ArchiveVolumeMissing, Path.GetFileName(missing)));
            }

            // 格式自己写明后面是密文（RAR4 主头的 MHD_PASSWORD / RAR5 的归档加密头）：这是 header 加密
            if (extent?.HeaderEncrypted == true)
            {
                return;
            }

            // 结构走不通：只给了一个分卷（7z 起始头指向分卷之外）、文件被截断、下载未完成
            if (!multiVolume && extent is null)
            {
                throw new ArchiveIncompleteException(Resources.ArchiveIncomplete);
            }

            // 结构完整却没条目：不带密码都能打开，说明归档里确实没有可测内容，而不是被加密藏起来了
            if (_openResult == 0)
            {
                throw new ArchiveIncompleteException(Resources.ArchiveHasNoEntries);
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
