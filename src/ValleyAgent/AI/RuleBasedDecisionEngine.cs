using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using ValleyAgent.Brain;
using ValleyAgent.StateMachine;

namespace ValleyAgent.AI;

/// <summary>
///     基于规则的决策上下文，供 RuleBasedDecisionEngine 使用。
///     包含 NPC 当前状态、环境、情绪等决策所需信息。
/// </summary>
public class RuleDecisionContext
{
    /// <summary>默认血量值，与 ModConfig.DefaultMaxHealth 对齐。用于属性初始值和 null 兜底。</summary>
    public const int DefaultHealth = 100;

    public string Location { get; set; } = "Town";
    public int Health { get; set; } = DefaultHealth;
    public int MaxHealth { get; set; } = DefaultHealth;
    public int Friendship { get; set; }
    public float PlayerDistance { get; set; } = float.MaxValue;
    public bool IsRaining { get; set; }
    public List<string> NearbyObjects { get; set; } = new();
    public EmotionState Emotion { get; set; } = EmotionState.Neutral();
    public int ConsecutiveIdleCount { get; set; }
    public AgentState CurrentState { get; set; } = AgentState.IDLE;

    /// <summary>
    ///     主动跟随触发概率 [0,1]（来自 ModConfig.ProactiveFollowProbability，默认 1.0）。
    ///     规则4社交互动中，高友谊 FOLLOW 决策的概率门控：
    ///     1.0 = 满足友谊条件必然跟随；0.2 = 20% 概率跟随；0.0 = 永不主动跟随。
    ///     仅门控社交跟随（规则4），不影响生存模式跟随（规则2）。
    /// </summary>
    public float FollowProbability { get; set; } = 1.0f;

    /// <summary>血量百分比（0.0~1.0），MaxHealth 为 0 时返回 0</summary>
    public float HealthPct
    {
        get => MaxHealth > 0 ? (float)Health / MaxHealth : 0f;
    }
}

/// <summary>
///     规则引擎决策结果。
/// </summary>
public class RuleDecisionResult
{
    public AgentState TargetState { get; set; } = AgentState.IDLE;
    public string Reason { get; set; } = string.Empty;
    public string Thought { get; set; } = string.Empty;
}

/// <summary>
///     情绪阈值查找表。根据情绪类型和强度返回决策修正参数。
///     强度 >= 0.5 时应用强度修正，低于 0.5 仅使用基础值。
/// </summary>
/// <summary>
///     生存反射决策引擎（2026-08-15 步骤 3 瘦身）— LLM 不可用时的本地回退。
///     性格决策（机会工作/社交互动）已删除——"不做 NPC 自主动机循环"（AGENTS.md §2.1）。
///     按严格优先级短路求值（生存反射专用）：
///     1. 紧急战斗：附近有怪物且血量 ≥ 50% → FIGHT
///     2. 生存模式：血量 < 35% + 有怪物 → FOLLOW(友谊 ≥ 1000) 或 IDLE(躲藏)
///     3. 默认空闲：IDLE + 地点相关的思考
/// </summary>
public static class RuleBasedDecisionEngine
{
    /// <summary>生存反射常量（2026-08-15 步骤 3：性格决策删除后固定阈值，原 Neutral 默认值）。</summary>
    private const float SurvivalFightHealthPct = 0.5f;
    private const float SurvivalHideHealthPct = 0.35f;
    private const int SurvivalFollowFriendship = 1000;

    private static readonly HashSet<string> MineLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "UndergroundMine", "Mine", "SkullCave"
    };

    private static readonly HashSet<string> FarmLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Farm", "FarmHouse", "FarmCave"
    };

    private static readonly HashSet<string> OutdoorLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Farm", "Town", "Forest", "Mountain", "Beach", "BusStop",
        "Railroad", "Desert", "Woods", "Backwoods"
    };

    private static readonly string[] MonsterKeywords =
        { "monster", "slime", "bat", "bug", "skeleton", "serpent", "怪" };

    public static RuleDecisionResult Decide(RuleDecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.MaxHealth <= 0)
        {
            return new RuleDecisionResult
            {
                TargetState = AgentState.IDLE,
                Reason = "生命值数据异常，暂时休息",
                Thought = "感觉不太对劲，先休息一下"
            };
        }

        // 2026-08-15 步骤 3：性格决策（机会工作/社交互动）已删除——"不做 NPC 自主动机循环"。
        // 本引擎只保留生存反射（紧急战斗/生存模式），其余交 spark/对话/Director beat 驱动。
        // 规则1: 紧急战斗（血量 ≥ 50% 才迎战，原 Neutral 默认阈值）
        var result = TryEmergencyFight(ctx);
        if (result != null)
        {
            return result;
        }

        // 规则2: 生存模式（低血量 + 有怪物 → FOLLOW(友谊 ≥ 1000) 或 IDLE 躲藏）
        result = TrySurvivalMode(ctx);
        if (result != null)
        {
            return result;
        }

        // 规则3: 默认空闲
        return DefaultIdle(ctx);
    }

    /// <summary>规则1: 附近有怪物且血量足够（≥ 50%）→ FIGHT（生存反射）。</summary>
    private static RuleDecisionResult? TryEmergencyFight(RuleDecisionContext ctx)
    {
        if (!HasMonsters(ctx.NearbyObjects))
        {
            return null;
        }

        if (ctx.HealthPct < SurvivalFightHealthPct)
        {
            return null;
        }

        return new RuleDecisionResult
        {
            TargetState = AgentState.FIGHT,
            Reason = "附近有怪物出现，必须迎战保护农场主",
            Thought = ThoughtForState("FIGHT", ctx)
        };
    }

    /// <summary>规则2: 低血量（< 35%）+ 有怪物 → FOLLOW(友谊 ≥ 1000) 或 IDLE(躲藏)。</summary>
    private static RuleDecisionResult? TrySurvivalMode(RuleDecisionContext ctx)
    {
        if (ctx.HealthPct >= SurvivalHideHealthPct || !HasMonsters(ctx.NearbyObjects))
        {
            return null;
        }

        if (ctx.Friendship >= SurvivalFollowFriendship)
        {
            return new RuleDecisionResult
            {
                TargetState = AgentState.FOLLOW,
                Reason = "受伤了，需要农场主的保护",
                Thought = "我受伤太重了，得跟上农场主才安全"
            };
        }

        return new RuleDecisionResult
        {
            TargetState = AgentState.IDLE,
            Reason = "受伤太重，需要休息恢复",
            Thought = "浑身是伤，我得休息一下"
        };
    }

    /// <summary>规则5: 默认空闲，带地点相关的思考</summary>
    private static RuleDecisionResult DefaultIdle(RuleDecisionContext ctx)
    {
        var thought = IsMine(ctx.Location)
            ? "矿洞里有点暗，先看看周围情况"
            : IsFarm(ctx.Location)
                ? "农场真安静，放松一下"
                : ctx.IsRaining && IsOutdoor(ctx.Location)
                    ? "下雨天还是待着吧"
                    : ctx.Friendship >= 1000
                        ? "农场主就在附近呢"
                        : "看看接下来做什么好";

        return new RuleDecisionResult
        {
            TargetState = AgentState.IDLE,
            Reason = "周围没有需要处理的事情，暂时休息",
            Thought = thought
        };
    }

    private static bool HasMonsters(List<string> objects)
    {
        return objects.Any(obj =>
            MonsterKeywords.Any(kw => obj.Contains(kw, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsMine(string location) =>
        MineLocations.Contains(location);

    private static bool IsFarm(string location) =>
        FarmLocations.Contains(location);

    private static bool IsOutdoor(string location) =>
        OutdoorLocations.Contains(location);

    private static string ThoughtForState(string state, RuleDecisionContext ctx)
    {
        var options = state switch
        {
            "FIGHT" => new[] { "有怪物！准备战斗", "不能让它伤害农场主", "来吧，让我看看你的本事" },
            _ => new[] { "休息一下" }
        };
        return options[RandomNumberGenerator.GetInt32(options.Length)];
    }
}