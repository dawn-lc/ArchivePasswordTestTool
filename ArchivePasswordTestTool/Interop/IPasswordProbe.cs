namespace ArchivePasswordTestTool
{
    /// <summary>单条候选密码的快速判定结果</summary>
    internal enum PasswordProbeResult
    {
        /// <summary>解密首块与预期文件格式不符 → 密码错误</summary>
        NoMatch,

        /// <summary>解密首块与预期文件格式相符 → 交给 7-Zip 做权威确认</summary>
        Match,

        /// <summary>超出快速路径的适用范围（密码过长、含非 ASCII 等）→ 必须走完整测密</summary>
        Unsupported,
    }

    /// <summary>
    /// 快速判错器：用候选密码解密归档数据的首个块，比对文件格式签名。
    /// <para>
    /// 抽象成接口是为了让 <see cref="FastFilter"/> 同时支持 RAR4 与 ZipCrypto——
    /// 它们的共同点是「格式没有可靠的密码校验值，但存储模式让首块就是明文」。
    /// </para>
    /// <para>
    /// 实现必须是无共享可变状态的（<see cref="Probe"/> 会被多线程并发调用），
    /// 且 <see cref="Probe"/> 返回 <see cref="PasswordProbeResult.Match"/> 只表示
    /// 「很像」，权威结论永远来自 7-Zip 的完整解包。
    /// </para>
    /// </summary>
    internal interface IPasswordProbe
    {
        /// <summary>预期明文格式名（展示给用户，如 "MP4"）</summary>
        string ContentFormat { get; }

        /// <summary>用候选密码判定；不得抛异常（异常应视为 Unsupported）</summary>
        PasswordProbeResult Probe(string password);
    }
}
