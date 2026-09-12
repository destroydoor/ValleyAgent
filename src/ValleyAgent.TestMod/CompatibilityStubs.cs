using System;
using System.Threading.Tasks;
using ValleyAgent.Brain;
using ValleyAgent.StateMachine;

namespace ValleyAgent.LLM
{
    /// <summary>
    ///     v3 兼容桩。MockLLMProvider 已在 v4 中删除（LLM 移至 TS Agent Server），
    ///     但 Narrative/N1-N5 测试仍引用。所有方法均为空操作存根。
    /// </summary>
    public static class MockLLMProvider
    {
        public static void ForceDecision(string npcName, AgentState targetState, string reason = "",
            string thought = "")
        {
        }

        public static void ForceDecision(string npcName)
        {
        }

        public static void ForceDecision(string npcName, string targetState, string reason = "", string thought = "")
        {
        }

        public static void ClearForcedDecision()
        {
        }
    }

    /// <summary>v3 兼容：旧款 LLMResponse</summary>
    public class LLMResponse
    {
        public string Content { get; set; } = string.Empty;
        public bool IsSuccess { get; set; }

        public static LLMResponse Success(string content, string model = "", string finishReason = "",
            LLMUsage usage = null)
            => new() { Content = content, IsSuccess = true };

        public static LLMResponse Failure(string error)
            => new() { Content = error, IsSuccess = false };
    }

    /// <summary>v3 兼容：旧款 LLMUsage</summary>
    public abstract class LLMUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }

    /// <summary>v3 兼容：旧款 AIDecisionEngine（空壳）</summary>
    public class AIDecisionEngine
    {
        public AIDecisionEngine(object provider)
        {
        }
    }
}

namespace ValleyAgent.Gifts
{
    /// <summary>
    ///     v3 兼容桩。GiftSystem 已在 v4 重构。
    /// </summary>
    public class GiftEvaluationContext
    {
        public string NpcName { get; set; } = string.Empty;
        public string ItemId { get; set; } = string.Empty;
        public GiftItem Item { get; set; } = new();
        public int CurrentFriendshipPoints { get; set; }
        public int MaxFriendshipPoints { get; set; } = 2500;
        public string CurrentSeason { get; set; } = string.Empty;
        public int CurrentDay { get; set; }
        public int CurrentYear { get; set; }
        public string CurrentDateKey { get; set; } = string.Empty;
        public bool IsBirthday { get; set; }
        public bool IsFestival { get; set; }
    }

    public class GiftItem
    {
        public string ItemId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public int Quality { get; set; }
    }

    /// <summary>v3 兼容：旧款 GiftSystem</summary>
    public static class GiftSystem
    {
        public static string EvaluateGift(GiftEvaluationContext ctx, GiftItem item) => "neutral";

        public static async Task<GiftEvaluationResult> EvaluatePlayerGiftAsync(GiftEvaluationContext ctx)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(ctx);
            var taste = UniversalTaste(ctx);
            var evt = taste switch
            {
                "loved" => NpcEmotionEvent.GiftLoved,
                "liked" => NpcEmotionEvent.GiftLiked,
                "disliked" => NpcEmotionEvent.GiftDisliked,
                "hated" => NpcEmotionEvent.GiftHated,
                _ => NpcEmotionEvent.GiftLoved
            };

            // 2026-08-15 步骤 3：情绪推导已迁 TS（EmotionAnalyzer 删除），桩侧降级中性。
            var derivedEmotion = NpcEmotion.Neutral;
            var intensity = 1.0f;

            return new GiftEvaluationResult
            {
                Taste = taste,
                DerivedEmotion = derivedEmotion,
                Intensity = intensity,
                Event = evt
            };
        }

        private static string UniversalTaste(GiftEvaluationContext ctx)
        {
            return ctx.Item?.ItemId switch
            {
                "286" => "hated",
                _ => "neutral"
            };
        }
    }

    public class GiftEvaluationResult
    {
        public string Taste { get; set; } = "neutral";
        public NpcEmotion DerivedEmotion { get; set; } = NpcEmotion.Neutral;
        public float Intensity { get; set; }
        public NpcEmotionEvent Event { get; set; }
    }
}

namespace ValleyAgent.LLM
{
    /// <summary>
    ///     v3 兼容桩。AIDecision 类型在 v5 中删除（架构改为 EventHandlerInitializer._pendingDecisions 直驱）。
    ///     GoalCompletionAuditor.RecordDecision 使用此类型。
    /// </summary>
    public class AIDecision
    {
        public AgentState TargetState { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Thought { get; set; } = string.Empty;

        public AgentState GetAgentState() => TargetState;

        public static AIDecision Fallback(string reason) => new() { Reason = reason };
    }
}