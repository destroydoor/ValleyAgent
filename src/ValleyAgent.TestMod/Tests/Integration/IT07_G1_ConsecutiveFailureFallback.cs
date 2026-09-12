#nullable enable
using System;
using System.Collections.Concurrent;
using System.Reflection;
using StardewModdingAPI;
using ValleyAgent.Patches;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT07：验证 G1 连续礼物失败降级路径。
///     当 NPCGiftPatch._consecutiveFailures[npc] >= MaxConsecutiveFailures（=2）时，
///     Prefix 应调用 GetLocalGiftFallback 显示本地反应文本，不再静默消失。
///     限制：Prefix 是 Harmony 补丁，由 SDV 礼物交互触发，难以在测试中模拟完整送礼动作。
///     可自动化部分：
///     1. 反射设置 _consecutiveFailures[npc] = 2（模拟连续失败状态）
///     2. 反射调用 GetLocalGiftFallback(0, "Tulip") 验证返回非空文本（G1 修复路径调用的 helper）
///     3. 反射读 _consecutiveFailures 确认状态已设置
///     不可自动化部分：Prefix 完整流程需 Harmony 触发（需手动验证）
/// </summary>
public class IT07_G1_ConsecutiveFailureFallback : IntegrationTestBase
{
    private bool _asserted;
    private bool _fallbackInvoked;
    private bool _setupComplete;

    public IT07_G1_ConsecutiveFailureFallback(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT07_G1_ConsecutiveFailureFallback";
    }

    public override int TimeoutTicks
    {
        get => 600;
    }

    public override TestGroup Group
    {
        get => TestGroup.Integration;
    }

    public override void Setup()
    {
        if (!TryCommonSetup())
        {
            return;
        }

        // 反射设置 NPCGiftPatch._consecutiveFailures[NpcName] = 2
        // _consecutiveFailures 是 private static ConcurrentDictionary<string,int>
        var field = typeof(NPCGiftPatch).GetField("_consecutiveFailures",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (field == null)
        {
            Skip("cannot access NPCGiftPatch._consecutiveFailures via reflection");
            return;
        }

        var dict = field.GetValue(null) as ConcurrentDictionary<string, int>;
        if (dict == null)
        {
            Skip("_consecutiveFailures is null or wrong type");
            return;
        }

        try
        {
            dict[NpcName] = 2;
        }
        catch (Exception ex)
        {
            Skip($"failed to set _consecutiveFailures: {ex.Message}");
            return;
        }

        Monitor.Log($"[{TestName}] Setup complete. _consecutiveFailures[{NpcName}]=2", LogLevel.Info);
        _setupComplete = true;
    }

    public override bool Update()
    {
        if (!_setupComplete || Api == null)
        {
            return true;
        }

        // Phase 1: 反射调用 GetLocalGiftFallback 验证返回非空文本
        if (CurrentTick == 30 && !_fallbackInvoked)
        {
            _fallbackInvoked = true;

            // giftTaste: 0=Love, 2=Like, 4=Neutral, 6=Dislike, 8=Hate
            // 测试 0 (Love) 和 6 (Dislike) 两种场景
            string? fallbackLove = null;
            string? fallbackDislike = null;
            try
            {
                fallbackLove = InvokeStaticPrivate(typeof(NPCGiftPatch), "GetLocalGiftFallback", 0, "Tulip") as string;
            }
            catch (Exception ex)
            {
                Assert("fallback_love_no_throw", false, $"GetLocalGiftFallback(0, Tulip) threw: {ex.Message}");
            }

            try
            {
                fallbackDislike =
                    InvokeStaticPrivate(typeof(NPCGiftPatch), "GetLocalGiftFallback", 6, "Trash") as string;
            }
            catch (Exception ex)
            {
                Assert("fallback_dislike_no_throw", false, $"GetLocalGiftFallback(6, Trash) threw: {ex.Message}");
            }

            Assert("fallback_love_nonempty", !string.IsNullOrEmpty(fallbackLove),
                $"fallbackLove={fallbackLove ?? "(null)"}");
            Assert("fallback_dislike_nonempty", !string.IsNullOrEmpty(fallbackDislike),
                $"fallbackDislike={fallbackDislike ?? "(null)"}");

            // 验证文本包含礼物名（确认走的是有意义的回退路径，而非空字符串）
            if (fallbackLove != null)
            {
                Assert("fallback_love_mentions_item", fallbackLove.Contains("Tulip"),
                    $"fallbackLove={fallbackLove}");
            }

            if (fallbackDislike != null)
            {
                Assert("fallback_dislike_mentions_item", fallbackDislike.Contains("Trash"),
                    $"fallbackDislike={fallbackDislike}");
            }
        }

        // Phase 2: 验证 _consecutiveFailures 状态确实被设置
        if (CurrentTick == 90 && !_asserted)
        {
            _asserted = true;

            var field = typeof(NPCGiftPatch).GetField("_consecutiveFailures",
                BindingFlags.Static | BindingFlags.NonPublic);
            var dict = field?.GetValue(null) as ConcurrentDictionary<string, int>;

            var hasEntry = dict != null && dict.TryGetValue(NpcName, out var count) && count >= 2;
            Assert("consecutive_failures_state_set", hasEntry,
                hasEntry ? $"count={dict![NpcName]}" : "state not set or count < 2");
        }

        return CurrentTick >= 150;
    }

    public override void Teardown()
    {
        // 清理 _consecutiveFailures 状态，避免影响后续测试
        var field = typeof(NPCGiftPatch).GetField("_consecutiveFailures",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (field?.GetValue(null) is ConcurrentDictionary<string, int> dict)
        {
            try
            {
                dict.TryRemove(NpcName, out _);
            }
            catch (Exception)
            {
                /* ignore */
            }
        }

        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }
}