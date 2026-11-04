namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 把 <c>[前缀][归档][尾随]</c> 结构里的归档部分单独拷出来，得到一个只含归档数据的干净文件。
    /// <para>
    /// 为什么需要它：7-Zip 本身能跳过前置 stub 与尾随垃圾（<c>maxCheckStartPosition</c> 加上格式自身的容错），
    /// 所以测密不需要这一步。但用户在其它工具里打开同一个文件时未必这么宽容，而且
    /// 「文件 5MB、包内只有 2MB」这种差异需要一个能落地的解释——分离就是把解释变成文件。
    /// </para>
    /// <para>
    /// 只在单卷上可用：分卷是多个文件拼成的一条流，切成单文件既不符合格式也不符合用户预期。
    /// </para>
    /// </summary>
    internal static class ArchiveSeparator
    {
        /// <summary>干净副本的文件名后缀，与结果文件（<c>&lt;压缩包&gt;[results].txt</c>）保持同一种括号风格</summary>
        public const string CleanSuffix = "[clean]";

        /// <summary>
        /// 干净副本的目标路径：<c>Secret.7z</c> → <c>Secret[clean].7z</c>（后缀插在扩展名之前）。
        /// 保留原扩展名——它决定了用户的其它工具会怎么打开这个文件；对自解压包会得到
        /// <c>RarSfx[clean].exe</c>，需要按普通归档用时由用户自己改扩展名，这比猜一个更准。
        /// </summary>
        public static string BuildTargetPath(string archivePath)
        {
            string directory = Path.GetDirectoryName(archivePath) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(archivePath);
            string extension = Path.GetExtension(archivePath);

            return Path.Combine(directory, $"{stem}{CleanSuffix}{extension}");
        }

        /// <summary>
        /// 把 <paramref name="start"/> 到 <paramref name="end"/> 的字节拷到目标文件。
        /// 分块复制，不把整个归档读进内存。
        /// </summary>
        /// <param name="sourcePath">原文件</param>
        /// <param name="start">归档起始偏移</param>
        /// <param name="end">归档结束偏移（不含）</param>
        /// <param name="targetPath">输出路径</param>
        public static void Write(string sourcePath, long start, long end, string targetPath)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(start);

            if (end <= start)
            {
                throw new ArgumentOutOfRangeException(nameof(end), end, "归档区间为空");
            }

            using var source = File.OpenRead(sourcePath);
            using var target = File.Create(targetPath);

            _ = source.Seek(start, SeekOrigin.Begin);

            byte[] buffer = new byte[1024 * 1024];
            long remaining = end - start;

            while (remaining > 0)
            {
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    throw new InvalidDataException("归档区间超出了文件实际长度");
                }

                target.Write(buffer, 0, read);
                remaining -= read;
            }
        }
    }
}
