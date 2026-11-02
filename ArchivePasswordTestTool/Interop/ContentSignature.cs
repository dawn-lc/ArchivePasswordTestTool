using System.Text;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 明文首块的格式特征（用于「快速判错」确认解密结果是否像真实文件）。
    /// <para>
    /// 只检查首块（16 字节）能覆盖的签名。取值原则：宁可少数格式不支持（快速判错整体关闭），
    /// 也不要把弱特征放进来——弱特征会让错误密码大量"通过"筛选，反而退化成完整测密。
    /// </para>
    /// </summary>
    internal sealed class ContentSignature
    {
        /// <summary>格式名（展示给用户，如 "MP4"）</summary>
        public required string Format { get; init; }

        /// <summary>必须全部命中的特征组</summary>
        public required (int Offset, byte[] Bytes)[] All { get; init; }

        /// <summary>任一组全部命中即可（用于同类容器有多个合法魔数的场景）</summary>
        public (int Offset, byte[] Bytes)[][] Any { get; init; } = [];

        /// <summary>
        /// 额外要求首 4 字节（大端）是合理的盒子长度（MP4/ISO BMFF）。
        /// 与 ftyp 联合判定后误判率约 2^-32，2.5M 候选期望误报 &lt; 0.001 次。
        /// </summary>
        public bool RequirePlausibleBoxSize { get; init; }

        public bool Matches(ReadOnlySpan<byte> block)
        {
            if (RequirePlausibleBoxSize)
            {
                if (block.Length < 4)
                {
                    return false;
                }

                uint boxSize = (uint)(block[0] << 24 | block[1] << 16 | block[2] << 8 | block[3]);

                // size==1 表示 64 位 largesize 紧随其后；其余必须 >= 8
                if (boxSize != 1 && boxSize < 8)
                {
                    return false;
                }
            }

            foreach (var (offset, bytes) in All)
            {
                if (!MatchAt(block, offset, bytes))
                {
                    return false;
                }
            }

            if (Any.Length == 0)
            {
                return true;
            }

            foreach (var group in Any)
            {
                bool all = true;
                foreach (var (offset, bytes) in group)
                {
                    if (!MatchAt(block, offset, bytes))
                    {
                        all = false;
                        break;
                    }
                }

                if (all)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchAt(ReadOnlySpan<byte> block, int offset, byte[] expected)
        {
            return offset >= 0
                && offset + expected.Length <= block.Length
                && block.Slice(offset, expected.Length).SequenceEqual(expected);
        }

        // ---------- 特征表 ----------

        private static readonly Dictionary<string, ContentSignature> ByExtension =
            new(StringComparer.OrdinalIgnoreCase);

        static ContentSignature()
        {
            // MP4/ISO BMFF 家族：首盒长度 + 盒类型
            var mp4 = Mp4();
            foreach (string ext in new[] { ".mp4", ".m4v", ".m4a", ".m4b", ".mov", ".3gp", ".3g2", ".f4v" })
            {
                ByExtension[ext] = mp4;
            }

            // Matroska / WebM
            var mkv = Exact("Matroska", 0, 0x1A, 0x45, 0xDF, 0xA3);
            ByExtension[".mkv"] = mkv;
            ByExtension[".webm"] = mkv;

            // RIFF 家族：容器名在偏移 8
            ByExtension[".avi"] = Riff("AVI", "AVI ");
            ByExtension[".wav"] = Riff("WAV", "WAVE");
            ByExtension[".webp"] = Riff("WebP", "WEBP");

            ByExtension[".flv"] = Exact("FLV", 0, "FLV");
            ByExtension[".wmv"] = Exact("ASF", 0, 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11);
            ByExtension[".wma"] = ByExtension[".wmv"];
            ByExtension[".asf"] = ByExtension[".wmv"];

            // MPEG 音频帧同步（11 位全 1）与 ID3 标签
            ByExtension[".mp3"] = new ContentSignature
            {
                Format = "MP3",
                All = [],
                Any =
                [
                    [(0, Ascii("ID3"))],
                    [(0, [0xFF, 0xFB])],
                    [(0, [0xFF, 0xFA])],
                    [(0, [0xFF, 0xF3])],
                    [(0, [0xFF, 0xF2])],
                    [(0, [0xFF, 0xE3])],
                    [(0, [0xFF, 0xE2])],
                ],
            };

            ByExtension[".m4r"] = mp4;
            ByExtension[".mpg"] = Exact("MPEG-PS", 0, 0x00, 0x00, 0x01, 0xBA);
            ByExtension[".mpeg"] = ByExtension[".mpg"];
            ByExtension[".vob"] = ByExtension[".mpg"];

            ByExtension[".jpg"] = Exact("JPEG", 0, 0xFF, 0xD8, 0xFF);
            ByExtension[".jpeg"] = ByExtension[".jpg"];
            ByExtension[".png"] = Exact("PNG", 0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
            ByExtension[".gif"] = Exact("GIF", 0, "GIF8");
            ByExtension[".bmp"] = Exact("BMP", 0, "BM");
            ByExtension[".pdf"] = Exact("PDF", 0, "%PDF");

            // ZIP 家族（含 Office Open XML / APK / JAR）
            var zip = new ContentSignature
            {
                Format = "ZIP",
                All = [],
                Any =
                [
                    [(0, [0x50, 0x4B, 0x03, 0x04])],
                    [(0, [0x50, 0x4B, 0x05, 0x06])],
                    [(0, [0x50, 0x4B, 0x07, 0x08])],
                ],
            };
            foreach (string ext in new[] { ".zip", ".jar", ".apk", ".docx", ".xlsx", ".pptx", ".epub", ".odt" })
            {
                ByExtension[ext] = zip;
            }

            ByExtension[".rar"] = Exact("RAR", 0, "Rar!");
            ByExtension[".7z"] = Exact("7z", 0, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C);
            ByExtension[".exe"] = Exact("PE", 0, "MZ");
            ByExtension[".dll"] = ByExtension[".exe"];
            ByExtension[".sys"] = ByExtension[".exe"];
            ByExtension[".ocx"] = ByExtension[".exe"];
            ByExtension[".cpl"] = ByExtension[".exe"];
            ByExtension[".scr"] = ByExtension[".exe"];
            ByExtension[".msi"] = Exact("MSI", 0, "MSCF");
            ByExtension[".cab"] = ByExtension[".msi"];

            // OLE 复合文档（旧版 Office / msg 等）
            var ole = Exact("OLE", 0, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1);
            foreach (string ext in new[] { ".doc", ".xls", ".ppt", ".msg", ".vsd" })
            {
                ByExtension[ext] = ole;
            }

            ByExtension[".sqlite"] = Exact("SQLite", 0, "SQLite format 3\0");
            ByExtension[".db"] = ByExtension[".sqlite"];
            ByExtension[".db3"] = ByExtension[".sqlite"];

            ByExtension[".gz"] = Exact("gzip", 0, 0x1F, 0x8B);
            ByExtension[".tgz"] = ByExtension[".gz"];
            ByExtension[".bz2"] = Exact("bzip2", 0, "BZh");
            ByExtension[".xz"] = Exact("xz", 0, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00);
            ByExtension[".zst"] = Exact("zstd", 0, 0x28, 0xB5, 0x2F, 0xFD);
        }

        /// <summary>
        /// 按文件名取签名。未知扩展名返回 null —— 调用方据此关闭快速判错，
        /// 因为无法判断"解密结果像不像真实文件"，任何筛选都可能漏掉正确答案。
        /// </summary>
        public static ContentSignature? ForFileName(string fileName)
        {
            string extension = Path.GetExtension(fileName);
            return string.IsNullOrEmpty(extension) ? null : ByExtension.GetValueOrDefault(extension);
        }

        private static ContentSignature Mp4() => new()
        {
            Format = "MP4",
            All = [],
            RequirePlausibleBoxSize = true,
            Any =
            [
                [(4, Ascii("ftyp"))],
                [(4, Ascii("moov"))],
                [(4, Ascii("mdat"))],
                [(4, Ascii("free"))],
                [(4, Ascii("skip"))],
                [(4, Ascii("wide"))],
                [(4, Ascii("styp"))],
            ],
        };

        private static ContentSignature Exact(string format, int offset, params byte[] bytes) => new()
        {
            Format = format,
            All = [(offset, bytes)],
        };

        private static ContentSignature Exact(string format, int offset, string ascii) =>
            Exact(format, offset, Ascii(ascii));

        private static ContentSignature Riff(string format, string type) => new()
        {
            Format = format,
            All = [(0, Ascii("RIFF")), (8, Ascii(type))],
        };

        private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
    }
}
