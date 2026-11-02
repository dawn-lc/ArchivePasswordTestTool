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

        /// <summary>
        /// 便捷判定：扩展名推断的签名，或者任一已知容器签名命中，都算候选。
        /// <para>
        /// 为什么必须加上后半句：压缩包内的文件名是打包者定的，可能被改名或伪装
        /// （实测：PNG 内容命名为 <c>Sample.mp4</c>）。若只认扩展名推断出的签名，
        /// 正确密码解出的 PNG 首块会被判为 <see cref="PasswordProbeResult.NoMatch"/> 而丢弃，
        /// 用户得到「未找到密码」——静默漏测。已用真实样本复现并修复。
        /// </para>
        /// <para>
        /// 反过来也不会明显变宽：兜底表只收录魔数 ≥ 3 字节的格式，最弱的三字节签名
        /// 随机命中率 2⁻²⁴，2.5M 候选期望误报约 0.5 次，且命中的候选仍要 7-Zip 完整确认。
        /// </para>
        /// </summary>
        public bool MatchesFileOrAnyKnownContainer(ReadOnlySpan<byte> block) =>
            Matches(block) || MatchesAnyKnownContainer(block);

        /// <summary>明文首块是否匹配任一已知容器格式（用于兜住扩展名与内容不符）</summary>
        public static bool MatchesAnyKnownContainer(ReadOnlySpan<byte> block)
        {
            foreach (var signature in UniversalSignatures)
            {
                if (signature.Matches(block))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 兜底匹配要求的最短魔数长度。2 字节魔数（MZ / BM / gzip / MP3 帧同步）
        /// 的随机命中率是 2⁻¹⁶，2.5M 候选会有上百次误报，足以把候选上限打满并让扫描提前中断，
        /// 因此只把它们留给「扩展名明确」的精确匹配，不进兜底表。
        /// </summary>
        private const int UniversalMinimumMagicLength = 3;

        /// <summary>
        /// 该签名中最弱的匹配分支需要多少字节。取 All 与每个 Any 组的较小值——
        /// 宁可低估强度（少收录一个格式），也不要把弱特征放进兜底表。
        /// <c>RequirePlausibleBoxSize</c> 不计入：它只排除 6 个值，撑不起强度。
        /// </summary>
        private int MinimumMagicLength
        {
            get
            {
                int strength = int.MaxValue;

                if (All.Length > 0)
                {
                    strength = 0;
                    foreach (var (_, bytes) in All)
                    {
                        strength += bytes.Length;
                    }
                }

                foreach (var group in Any)
                {
                    int groupStrength = 0;
                    foreach (var (_, bytes) in group)
                    {
                        groupStrength += bytes.Length;
                    }

                    strength = Math.Min(strength, groupStrength);
                }

                return strength == int.MaxValue ? 0 : strength;
            }
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
            ByExtension[".lz4"] = Exact("lz4", 0, 0x04, 0x22, 0x4D, 0x18);

            // ---------- 音频 ----------

            // Ogg 家族（Vorbis / Theora / Opus / Speex）
            var ogg = Exact("Ogg", 0, "OggS");
            foreach (string ext in new[] { ".ogg", ".ogv", ".oga", ".ogx", ".opus", ".spx" })
            {
                ByExtension[ext] = ogg;
            }

            ByExtension[".flac"] = Exact("FLAC", 0, "fLaC");
            ByExtension[".wv"] = Exact("WavPack", 0, "wvpk");
            ByExtension[".ape"] = Exact("Monkey's Audio", 0, "MAC ");

            var midi = Exact("MIDI", 0, "MThd");
            ByExtension[".mid"] = midi;
            ByExtension[".midi"] = midi;
            ByExtension[".rmi"] = midi;

            // AIFF / AIFC：IFF 容器，类型名在偏移 8（与 RIFF 同构，但魔数不同）
            var aiff = new ContentSignature
            {
                Format = "AIFF",
                All = [],
                Any =
                [
                    [(0, Ascii("FORM")), (8, Ascii("AIFF"))],
                    [(0, Ascii("FORM")), (8, Ascii("AIFC"))],
                ],
            };
            foreach (string ext in new[] { ".aif", ".aiff", ".aifc" })
            {
                ByExtension[ext] = aiff;
            }

            // RealMedia：老片源/字幕组归档里仍常见
            var realMedia = Exact("RealMedia", 0, ".RMF");
            ByExtension[".rm"] = realMedia;
            ByExtension[".rmvb"] = realMedia;

            // ---------- 图像 ----------

            // TIFF 有两种字节序（Intel / Motorola）
            var tiff = new ContentSignature
            {
                Format = "TIFF",
                All = [],
                Any =
                [
                    [(0, [0x49, 0x49, 0x2A, 0x00])],
                    [(0, [0x4D, 0x4D, 0x00, 0x2A])],
                ],
            };
            foreach (string ext in new[] { ".tif", ".tiff" })
            {
                ByExtension[ext] = tiff;
            }

            // Photoshop（PSD 与大型文档 PSB 同一魔数）
            var psd = Exact("PSD", 0, "8BPS");
            ByExtension[".psd"] = psd;
            ByExtension[".psb"] = psd;

            ByExtension[".dds"] = Exact("DDS", 0, "DDS ");

            var djvu = Exact("DjVu", 0, "AT&TFORM");
            ByExtension[".djvu"] = djvu;
            ByExtension[".djv"] = djvu;

            // ---------- 文档 ----------

            ByExtension[".rtf"] = Exact("RTF", 0, "{\\rtf");
            ByExtension[".chm"] = Exact("CHM", 0, "ITSF");

            // ---------- 字体 / 3D / Flash ----------

            var swf = new ContentSignature
            {
                Format = "SWF",
                All = [],
                Any = [[(0, Ascii("FWS"))], [(0, Ascii("CWS"))], [(0, Ascii("ZWS"))]],
            };
            ByExtension[".swf"] = swf;

            ByExtension[".woff"] = Exact("WOFF", 0, "wOFF");
            ByExtension[".woff2"] = Exact("WOFF2", 0, "wOF2");
            ByExtension[".otf"] = Exact("OpenType", 0, "OTTO");
            ByExtension[".ttc"] = Exact("TrueType Collection", 0, "ttcf");
            ByExtension[".fbx"] = Exact("FBX", 0, "Kaydara FBX B");

            // ---------- 字节码 / 可执行 ----------

            var elf = Exact("ELF", 0, 0x7F, 0x45, 0x4C, 0x46);
            foreach (string ext in new[] { ".so", ".elf", ".o", ".axf" })
            {
                ByExtension[ext] = elf;
            }

            ByExtension[".class"] = Exact("Java class", 0, 0xCA, 0xFE, 0xBA, 0xBE);
            ByExtension[".dex"] = Exact("Android dex", 0, "dex\n");

            // ---------- 其它容器 ----------

            var wim = Exact("WIM", 0, "MSWIM\0\0\0");
            foreach (string ext in new[] { ".wim", ".esd", ".swm" })
            {
                ByExtension[ext] = wim;
            }

            ByExtension[".vpk"] = Exact("VPK", 0, 0x34, 0x12, 0xAA, 0x55);
            ByExtension[".unity3d"] = Exact("UnityFS", 0, "UnityFS");

            // 必须在这里（表填完之后）构建。写成字段初始化器是不行的：
            // 有显式静态构造函数的类型，字段初始化器会在构造函数体**之前**执行，
            // 那时 ByExtension 还是空的，兜底表会静默变成空数组。
            UniversalSignatures = BuildUniversalSignatures();
        }

        /// <summary>
        /// 兜底签名表：从扩展名表里去重并筛出「魔数足够强」的项。
        /// 与 <see cref="ByExtension"/> 同源，新增格式只改一处。
        /// </summary>
        private static readonly ContentSignature[] UniversalSignatures;

        private static ContentSignature[] BuildUniversalSignatures() =>
            [.. ByExtension.Values
                .Distinct()
                .Where(signature => signature.MinimumMagicLength >= UniversalMinimumMagicLength)];

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
