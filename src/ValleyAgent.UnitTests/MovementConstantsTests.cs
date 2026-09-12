using ValleyAgent.Navigation;
using ValleyAgent.Utils;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E2-1 动态速度 MovementConstants 距离分段单元测试。
///     验证默认常量与边界语义：&gt;8 → 2.0×，[3,8] → 1.5×，&lt;3 → 1.0×；
///     走行动画帧间隔随倍率等比缩短；配置可覆盖且禁用后恒为近距倍率。
///     设计依据：docs/plan/2026-08-02-execution-plan.md E2-1。
/// </summary>
public class MovementConstantsTests
{
    [Fact]
    public void DefaultBandLookup_FarMidNearBoundaries()
    {
        // 远距（> 8）
        Assert.Equal(2.0f, MovementConstants.GetDynamicSpeedMultiplier(9f));
        Assert.Equal(2.0f, MovementConstants.GetDynamicSpeedMultiplier(100f));

        // 中距（含边界：恰在远距/近距边界上归中距）
        Assert.Equal(1.5f, MovementConstants.GetDynamicSpeedMultiplier(8f));
        Assert.Equal(1.5f, MovementConstants.GetDynamicSpeedMultiplier(5f));
        Assert.Equal(1.5f, MovementConstants.GetDynamicSpeedMultiplier(3f));

        // 近距（< 3）
        Assert.Equal(1.0f, MovementConstants.GetDynamicSpeedMultiplier(2.9f));
        Assert.Equal(1.0f, MovementConstants.GetDynamicSpeedMultiplier(0f));
    }

    [Fact]
    public void EffectiveSpeed_ScalesFromBasePathfindSpeed()
    {
        // GameSpeedMultiplier=1 → 基础寻路速度 2。显式固定并恢复，避免并行测试类干扰。
        var previous = DebugFlags.GameSpeedMultiplier;
        DebugFlags.GameSpeedMultiplier = 1;
        try
        {
            Assert.Equal(2, MovementConstants.GetEffectiveDynamicSpeed(2f)); // 近距 1.0× → 2
            Assert.Equal(3, MovementConstants.GetEffectiveDynamicSpeed(5f)); // 中距 1.5× → 3
            Assert.Equal(4, MovementConstants.GetEffectiveDynamicSpeed(10f)); // 远距 2.0× → 4
        }
        finally
        {
            DebugFlags.GameSpeedMultiplier = previous;
        }
    }

    [Fact]
    public void WalkAnimationInterval_ScalesInverselyWithMultiplier()
    {
        // 近距：不缩放 → 基础间隔
        Assert.Equal(MovementConstants.BaseWalkAnimationIntervalMs,
            MovementConstants.GetEffectiveWalkAnimationIntervalMs(2f));

        // 中距 1.5× → 100 / 1.5；远距 2.0× → 100 / 2.0（等比缩短，无滑步）
        Assert.Equal(100f / 1.5f, MovementConstants.GetEffectiveWalkAnimationIntervalMs(5f), 2);
        Assert.Equal(100f / 2.0f, MovementConstants.GetEffectiveWalkAnimationIntervalMs(10f), 2);
    }

    [Fact]
    public void ApplyConfig_OverridesDefaults()
    {
        MovementConstants.ApplyDynamicSpeedConfig(
            true,
            12f,
            4f,
            1.0f,
            2.0f,
            3.0f);
        try
        {
            Assert.Equal(3.0f, MovementConstants.GetDynamicSpeedMultiplier(13f)); // > 12 → 远距
            Assert.Equal(2.0f, MovementConstants.GetDynamicSpeedMultiplier(5f)); // [4,12] → 中距
            Assert.Equal(1.0f, MovementConstants.GetDynamicSpeedMultiplier(3f)); // < 4 → 近距
        }
        finally
        {
            RestoreDefaults();
        }
    }

    [Fact]
    public void Disabled_AlwaysReturnsNearMultiplier()
    {
        MovementConstants.ApplyDynamicSpeedConfig(false, 8f, 3f, 1f, 1.5f, 2f);
        try
        {
            // 即使距离很远，禁用后也按近距倍率移动
            Assert.Equal(1.0f, MovementConstants.GetDynamicSpeedMultiplier(50f));
            Assert.Equal(MovementConstants.BaseWalkAnimationIntervalMs,
                MovementConstants.GetEffectiveWalkAnimationIntervalMs(50f));
        }
        finally
        {
            RestoreDefaults();
        }
    }

    private static void RestoreDefaults()
    {
        MovementConstants.ApplyDynamicSpeedConfig(
            true,
            MovementConstants.DynamicSpeedFarThreshold,
            MovementConstants.DynamicSpeedMidThreshold,
            MovementConstants.DynamicSpeedNearMultiplier,
            MovementConstants.DynamicSpeedMidMultiplier,
            MovementConstants.DynamicSpeedFarMultiplier);
    }
}