using ArchivePasswordTestTool.Properties;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ArchivePasswordTestTool
{
    /// <summary>
    /// 进度条里的实时速度列（个/秒）。
    /// <para>
    /// 数据取自 <see cref="ProgressTask.Speed" />——Spectre 自己维护的滑动窗口采样（默认丢弃 30 秒前的旧样本），
    /// 比手写「累计值 ÷ 总耗时」更能反映**当前**速度：后者在并行度波动、快筛命中候选等场景下会严重滞后。
    /// </para>
    /// <para>
    /// 只用于测密进度条。哈希进度是按字节推进的，「个/秒」没有语义，见 <c>Runner.CreateProgress</c>。
    /// 非交互终端（重定向输出）下 Spectre 走 <c>FallbackProgressRenderer</c>，只打印「描述: 百分比」，
    /// 本列不参与渲染——这是库的行为，日志不会被速度文本污染。
    /// </para>
    /// </summary>
    internal sealed class RateColumn : ProgressColumn
    {
        /// <summary>有速度时的样式</summary>
        private static readonly Style RateStyle = new(foreground: Color.Green);

        /// <summary>还没攒够采样、无法给出速度时的样式</summary>
        private static readonly Style PendingStyle = new(foreground: Color.Grey);

        /// <inheritdoc />
        public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
        {
            double? speed = task.Speed;

            // 刚启动还没攒够样本（或任务根本没开始）时显示占位，而不是伪造一个 0；
            // 完成后的行被 HideCompleted 隐藏，不需要额外区分
            return speed is null or <= 0
                ? new Text("-", PendingStyle)
                : new Text(FormatRate(speed.Value), RateStyle);
        }

        /// <summary>
        /// 速度文本，如 <c>1,017 个/秒</c>。抽成单独方法便于直接断言，不必走渲染管线。
        /// </summary>
        /// <param name="speed">每秒测试的密码条数</param>
        internal static string FormatRate(double speed) =>
            string.Format(Resources.ResultPwPerSec, Math.Round(speed).ToString("N0", CultureInfo.CurrentCulture));
    }
}
