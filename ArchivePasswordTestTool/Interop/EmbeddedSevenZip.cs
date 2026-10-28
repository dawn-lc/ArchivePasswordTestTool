using System.Reflection;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 内嵌 7z.dll 的落地与定位。
    /// <para>
    /// 构建期把官方 7z.dll 作为程序集资源嵌入（见 csproj 的 EnsureSevenZipDllAsset），
    /// 运行期解压到用户缓存目录再交给 <see cref="SevenZipLibrary"/> 按普通文件加载。
    /// 这样「下载单个 exe 即可用」，不再要求用户预装 7-Zip。
    /// </para>
    /// <para>
    /// 为什么不直接把 dll 展开成内存模块加载：7z.dll 有大量内部 DLL 导入与 TLS，
    /// 内存加载需要自实现 PE 映射与导入修复，风险远高于收益。落地成文件还能让用户
    /// 用自己的 7z.dll 覆盖（满足 7-Zip 的 LGPL 可替换要求，也可用 -lib 显式指定）。
    /// </para>
    /// <para>
    /// 落地位置选 %LOCALAPPDATA% 而非程序目录：程序可能装在 Program Files（只读），
    /// 也避免在便携场景往 U 盘/共享盘反复写文件。
    /// </para>
    /// </summary>
    internal static class EmbeddedSevenZip
    {
        internal const string DllResourceName = "ArchivePasswordTestTool.Native.7z.dll";
        internal const string LicenseResourceName = "ArchivePasswordTestTool.Native.7z-License.txt";

        private const string CacheFolderName = "ArchivePasswordTestTool";
        private const string CacheSubFolder = "native";
        private const string DllFileName = "7z.dll";
        private const string LicenseFileName = "7z-License.txt";

        /// <summary>缓存目录：%LOCALAPPDATA%\ArchivePasswordTestTool\native</summary>
        internal static string CacheDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            CacheFolderName,
            CacheSubFolder);

        /// <summary>程序集里是否真的带上了内嵌 7z.dll</summary>
        internal static bool IsAvailable => FindResource(DllResourceName) is not null;

        /// <summary>
        /// 把内嵌的 7z.dll 解压到缓存目录并返回路径；未内嵌、解压失败或缓存不可写时返回
        /// <see langword="null"/>（调用方回退到"要求用户安装 7-Zip"的原行为）。
        /// <para>
        /// 并发安全：多进程模式下一次启动会拉起 N 个工作进程，它们几乎同时走到这里。
        /// 先写唯一临时名再原子改名，并接受"别人已经写好"的结果，避免互相踩踏或读到半截文件。
        /// </para>
        /// </summary>
        internal static string? TryExtractDll()
        {
            string target = Path.Combine(CacheDirectory, DllFileName);

            try
            {
                using var resource = FindResource(DllResourceName);
                if (resource is null)
                {
                    return null;
                }

                long expectedLength = resource.Length;

                // 已落地且大小一致：直接复用（旧版本残留会被下面的替换流程纠正）
                var existing = new FileInfo(target);
                if (existing.Exists && existing.Length == expectedLength)
                {
                    EnsureLicenseExtracted();
                    return target;
                }

                Directory.CreateDirectory(CacheDirectory);

                string temporary = target + "." + Environment.ProcessId + ".tmp";
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    resource.CopyTo(output);
                }

                try
                {
                    File.Move(temporary, target, overwrite: true);
                }
                catch (IOException)
                {
                    // 另一进程刚写完并占住了目标文件：只要落地结果正确就视为成功
                    TryDelete(temporary);
                    var settled = new FileInfo(target);
                    if (!settled.Exists || settled.Length != expectedLength)
                    {
                        return null;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    TryDelete(temporary);
                    var settled = new FileInfo(target);
                    if (!settled.Exists || settled.Length != expectedLength)
                    {
                        return null;
                    }
                }

                EnsureLicenseExtracted();
                return target;
            }
            catch (Exception)
            {
                // 缓存目录只读、磁盘满、杀软拦截等：退回"需自行安装 7-Zip"
                return null;
            }
        }

        /// <summary>
        /// 许可证随 dll 一起落地：7-Zip 是 LGPL，官方 license.txt 要求二进制再分发时
        /// 必须附带许可证信息。失败不影响功能，因此静默忽略。
        /// </summary>
        private static void EnsureLicenseExtracted()
        {
            try
            {
                using var resource = FindResource(LicenseResourceName);
                if (resource is null)
                {
                    return;
                }

                string target = Path.Combine(CacheDirectory, LicenseFileName);
                var existing = new FileInfo(target);
                if (existing.Exists && existing.Length == resource.Length)
                {
                    return;
                }

                Directory.CreateDirectory(CacheDirectory);

                string temporary = target + "." + Environment.ProcessId + ".tmp";
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    resource.CopyTo(output);
                }

                try
                {
                    File.Move(temporary, target, overwrite: true);
                }
                catch (Exception)
                {
                    TryDelete(temporary);
                }
            }
            catch (Exception)
            {
                // 许可证落地失败不阻断启动
            }
        }

        private static Stream? FindResource(string name)
        {
            try
            {
                var assembly = typeof(EmbeddedSevenZip).Assembly;
                return assembly.GetManifestResourceStream(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 残留临时文件由下次解压覆盖
            }
        }
    }
}
