using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Services;

namespace ValleyAgent.AI;

/// <summary>
///     GameContext 快照构造器（2026-08-09 新增）。从 Game1 状态拼装 TS narrative-types.ts 的
///     GameContext JSON（Dictionary 形式，经 System.Text.Json 序列化后随 game_context_sync 消息发送）。
///     用途：TS 端 GameContextManager.update() 填充上下文，Director.morningPlan() 才能拿到
///     "今天是哪天/玩家在干嘛/谁在场" 等信息并真正调用 LLM 编排——此前 C# 从未发送该消息，
///     导演 LLM 在游戏内从未运行（morningPlan 恒走 no-game-context 分支）。
///     只读采集：不调用任何 NPC LLM、不改状态（职责隔离）。
/// </summary>
public static class GameContextSyncBuilder
{
    /// <summary>TS GameContext.time.season 枚举值映射（SDV 英文季节直接透传，中文/异常回落 spring）。</summary>
    private static string MapSeason(string? season)
    {
        return season?.ToLowerInvariant() switch
        {
            "spring" or "summer" or "fall" or "winter" => season.ToLowerInvariant(),
            "春" => "spring",
            "夏" => "summer",
            "秋" => "fall",
            "冬" => "winter",
            _ => "spring"
        };
    }

    /// <summary>TS GameContext.time.weather 枚举值映射（晴天/雨天/雪天/暴风雨）。</summary>
    private static string MapWeather()
    {
        try
        {
            if (Game1.isLightning)
            {
                return "stormy";
            }

            if (Game1.isRaining)
            {
                return "rainy";
            }

            if (Game1.isSnowing)
            {
                return "snowy";
            }
        }
        catch (NullReferenceException)
        {
            // 标题屏/测试环境无天气状态
        }

        return "sunny";
    }

    /// <summary>SDV 1.6 Season 枚举映射（string 季节 → 枚举，异常回落 Spring）。</summary>
    private static Season MapSeasonEnum(string? season)
    {
        return season?.ToLowerInvariant() switch
        {
            "summer" => Season.Summer,
            "fall" => Season.Fall,
            "winter" => Season.Winter,
            _ => Season.Spring
        };
    }

    /// <summary>
    ///     构造 GameContext 字典。游戏主线程调用（读取 Game1 全局状态）。
    ///     任何单项采集失败都不拖垮整表（降级为默认值并记录——导演日志静默 bug 的教训）。
    /// </summary>
    public static Dictionary<string, object> Build(IMonitor? monitor)
    {
        var context = new Dictionary<string, object>
        {
            ["time"] = BuildTime(),
            ["progress"] = BuildProgress(),
            ["seasonalResources"] = new Dictionary<string, object>
            {
                ["plantableCrops"] = new List<string>(),
                ["catchableFish"] = new List<string>(),
                ["forageItems"] = new List<string>(),
                ["activeFestivals"] = BuildActiveFestivals()
            },
            ["npcStates"] = BuildNpcStates(monitor),
            ["playerState"] = BuildPlayerState(),
            ["lastUpdated"] = DateTime.UtcNow.ToString("o")
        };

        return context;
    }

    private static Dictionary<string, object> BuildTime()
    {
        var season = MapSeason(Game1.currentSeason);
        var isFestival = false;
        var festivalName = "";
        try
        {
            isFestival = Utility.isFestivalDay(Game1.dayOfMonth, MapSeasonEnum(Game1.currentSeason));
            if (isFestival)
            {
                festivalName = Game1.CurrentEvent?.FestivalName ?? "";
            }
        }
        catch (NullReferenceException)
        {
            // 无节日/无事件
        }

        var time = new Dictionary<string, object>
        {
            ["year"] = Game1.year,
            ["season"] = season,
            ["day"] = Game1.dayOfMonth,
            ["dayOfWeek"] = Game1.Date.DayOfWeek.ToString(),
            ["weather"] = MapWeather(),
            ["isFestivalDay"] = isFestival
        };
        if (!string.IsNullOrEmpty(festivalName))
        {
            time["festivalName"] = festivalName;
        }

        return time;
    }

    private static Dictionary<string, object> BuildProgress()
    {
        var bundlesDone = new List<string>();
        var communityCenterComplete = false;
        try
        {
            if (Game1.MasterPlayer != null)
            {
                communityCenterComplete = Game1.MasterPlayer.hasCompletedCommunityCenter();
            }
        }
        catch (NullReferenceException)
        {
            // 存档未加载
        }

        return new Dictionary<string, object>
        {
            ["communityCenterComplete"] = communityCenterComplete,
            ["communityCenterBundlesDone"] = bundlesDone,
            ["jojaMartRoute"] = false,
            ["islandsUnlocked"] = new List<string>(),
            ["desertUnlocked"] = false,
            ["railroadUnlocked"] = false,
            ["sewersUnlocked"] = false,
            ["greenhouseRestored"] = false
        };
    }

    private static List<string> BuildActiveFestivals()
    {
        try
        {
            if (Utility.isFestivalDay(Game1.dayOfMonth, MapSeasonEnum(Game1.currentSeason)))
            {
                var festival = Game1.CurrentEvent?.FestivalName;
                if (!string.IsNullOrEmpty(festival) && !festival.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return new List<string> { festival };
                }
            }
        }
        catch (NullReferenceException)
        {
            // 无节日
        }

        return new List<string>();
    }

    private static List<object> BuildNpcStates(IMonitor? monitor)
    {
        var result = new List<object>();
        try
        {
            var agentService = AgentService.Current;
            foreach (var npc in Utility.getAllCharacters())
            {
                if (!npc.IsVillager || string.IsNullOrEmpty(npc.Name))
                {
                    continue;
                }

                var isAvailable = !npc.IsInvisible && !npc.isSleeping.Value;
                var currentState = "IDLE";
                try
                {
                    if (agentService != null && agentService.TryGetBrain(npc.Name, out var agent) && agent != null)
                    {
                        currentState = agent.StateMachine.CurrentStateFlag.ToString();
                    }
                }
                catch (NullReferenceException)
                {
                    // brain 查询失败 → 默认 IDLE
                }

                int friendshipPoints = 0;
                try
                {
                    if (Game1.player?.friendshipData != null &&
                        Game1.player.friendshipData.TryGetValue(npc.Name, out var fs))
                    {
                        friendshipPoints = fs.Points;
                    }
                }
                catch (NullReferenceException)
                {
                    // 无好感数据
                }

                result.Add(new Dictionary<string, object>
                {
                    ["name"] = npc.Name,
                    ["location"] = npc.currentLocation?.Name ?? "unknown",
                    ["tile"] = new Dictionary<string, object>
                    {
                        ["x"] = (int)npc.Tile.X,
                        ["y"] = (int)npc.Tile.Y
                    },
                    ["isAvailable"] = isAvailable,
                    ["currentState"] = currentState,
                    ["friendshipPoints"] = friendshipPoints
                });
            }
        }
        catch (NullReferenceException ex)
        {
            // 游戏状态未初始化（标题屏/测试启动早期）——导演上下文缺失，不影响主流程
            monitor?.Log($"[GameContextSync] npcStates build failed: {ex}", LogLevel.Debug);
        }

        return result;
    }

    private static Dictionary<string, object> BuildPlayerState()
    {
        var inventory = new List<object>();
        try
        {
            if (Game1.player?.Items != null)
            {
                foreach (var item in Game1.player.Items)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    inventory.Add(new Dictionary<string, object>
                    {
                        ["name"] = item.DisplayName ?? item.Name ?? item.ItemId,
                        ["quantity"] = item.Stack
                    });
                }
            }
        }
        catch (NullReferenceException)
        {
            // 玩家状态未初始化
        }

        return new Dictionary<string, object>
        {
            ["location"] = Game1.player?.currentLocation?.Name ?? "unknown",
            ["tile"] = new Dictionary<string, object>
            {
                ["x"] = Game1.player == null ? 0 : (int)Game1.player.Tile.X,
                ["y"] = Game1.player == null ? 0 : (int)Game1.player.Tile.Y
            },
            ["health"] = Game1.player?.health ?? 0,
            ["maxHealth"] = Game1.player?.maxHealth ?? 100,
            ["energy"] = Game1.player == null ? 0 : (int)Game1.player.Stamina,
            ["maxEnergy"] = Game1.player == null ? 270 : (int)Game1.player.MaxStamina,
            ["money"] = Game1.player?.Money ?? 0,
            ["inventory"] = inventory
        };
    }
}
