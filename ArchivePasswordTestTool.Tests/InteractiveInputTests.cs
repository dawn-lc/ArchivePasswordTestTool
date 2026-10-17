namespace ArchivePasswordTestTool.Tests;

/// <summary>
/// 交互路径输入的突发（拖放/粘贴）判定单测：
/// 守护"手动打字不会被误判为粘贴而自动验收"这一体验修复
/// </summary>
public class InteractiveInputTests
{
    [Fact]
    public void FastSuccessiveCharacters_AreBurst()
    {
        // 拖放/粘贴：字符间隔 1~5ms，应识别为突发输入
        Assert.True(InteractivePrompts.UpdateBurstState(false, 1));
        Assert.True(InteractivePrompts.UpdateBurstState(false, 5));
        Assert.True(InteractivePrompts.UpdateBurstState(false, 60));
    }

    [Fact]
    public void SlowTypingAfterIdle_IsNotBurst()
    {
        // 粘贴结束后停顿再打字：间隔超过粘贴空闲阈值 → 复位为手动打字
        Assert.False(InteractivePrompts.UpdateBurstState(true, 300));
        Assert.False(InteractivePrompts.UpdateBurstState(true, 1000));
    }

    [Fact]
    public void MediumGap_KeepsPreviousState()
    {
        // 60~250ms 之间的间隔不足以判定切换：维持原状态
        Assert.True(InteractivePrompts.UpdateBurstState(true, 100));
        Assert.False(InteractivePrompts.UpdateBurstState(false, 100));
    }

    [Fact]
    public void BurstIsStickyAcrossBriefHiccups()
    {
        // 粘贴中途偶发调度抖动（间隔略大）不应让整段识别失败
        bool burst = InteractivePrompts.UpdateBurstState(false, 5);   // 字符 2
        burst = InteractivePrompts.UpdateBurstState(burst, 120);      // 抖动
        burst = InteractivePrompts.UpdateBurstState(burst, 5);        // 继续粘贴
        Assert.True(burst);
    }

    [Fact]
    public void ManualTypingNeverStartsBurst()
    {
        // 手动打字典型节奏 100~200ms：从头到尾都不应进入突发模式
        bool burst = InteractivePrompts.UpdateBurstState(false, long.MaxValue);
        for (int i = 0; i < 20; i++)
        {
            burst = InteractivePrompts.UpdateBurstState(burst, 150);
            Assert.False(burst);
        }
    }
}
