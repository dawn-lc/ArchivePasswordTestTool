using System.Globalization;
using ArchivePasswordTestTool.Properties;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 测密进度条上的实时速度列。
/// <para>
/// 速度值本身来自 Spectre 的 <see cref="ProgressTask.Speed" />（滑动窗口采样），这里锁两件事：
/// ① 没有可用采样时显示占位而不是伪造 0；② 有速度时用**与结果面板相同的资源模板**格式化，
/// 避免进度条和结果面板出现两套单位（曾各自硬编码）。
/// </para>
/// </summary>
public sealed class ProgressRateColumnTests
{
    /// <summary>无速度采样时的占位符，与实现里的分支保持一致</summary>
    private const string PendingPlaceholder = "-";

    [Fact]
    public void NotStartedTaskShowsPlaceholder()
    {
        var task = new ProgressTask(1, "Test", maxValue: 100, autoStart: false, new ManualTimeProvider());

        Assert.Null(task.Speed);
        Assert.Equal(PendingPlaceholder, RenderRate(task));
    }

    /// <summary>
    /// 推进 200 条、耗时 2 秒 → Spectre 报 100 条/秒，
    /// 列文本必须与该速度的格式化结果一致（证明它没走自己的硬编码格式）
    /// </summary>
    [Fact]
    public void RunningTaskShowsFormattedRate()
    {
        var time = new ManualTimeProvider();
        var task = new ProgressTask(1, "Test", maxValue: 1000, autoStart: true, time);

        task.Increment(100);
        time.Advance(TimeSpan.FromSeconds(2));
        task.Increment(100);

        Assert.NotNull(task.Speed);
        Assert.Equal(100, task.Speed!.Value, precision: 3);
        Assert.Equal(RateColumn.FormatRate(task.Speed.Value), RenderRate(task));
    }

    /// <summary>格式串来自 <c>Resources.ResultPwPerSec</c>，四舍五入后按当前 culture 加千分位</summary>
    [Fact]
    public void FormatRateUsesSharedResourceTemplate()
    {
        const double speed = 1234.4;
        string expectedNumber = Math.Round(speed).ToString("N0", CultureInfo.CurrentCulture);
        string expected = string.Format(Resources.ResultPwPerSec, expectedNumber);

        Assert.Equal(expected, RateColumn.FormatRate(speed));
        Assert.Contains(expectedNumber, RateColumn.FormatRate(speed), StringComparison.Ordinal);
    }

    /// <summary>把列的渲染结果落成纯文本（关闭颜色与 ANSI，避免断言里混入转义序列）</summary>
    private static string RenderRate(ProgressTask task)
    {
        using var writer = new StringWriter();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });

        var options = RenderOptions.Create(console, console.Profile.Capabilities);
        console.Write(new RateColumn().Render(options, task, TimeSpan.Zero));

        return writer.ToString().Trim();
    }

    /// <summary>手动推进的时间源：Spectre 的采样与耗时都取自 <c>TimeProvider</c></summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }
}
