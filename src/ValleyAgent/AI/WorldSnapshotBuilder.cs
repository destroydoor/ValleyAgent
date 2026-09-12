using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyAgent.Beats;
using ValleyAgent.Chat;
using ValleyAgent.Inventory;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.WebSocket;

namespace ValleyAgent.AI;

/// <summary>
///     WorldSnapshot 采集器。从 DialogueBoxInputPatch.SubmitInput 抽取为公共方法，
///     主机与 farmhand 端均可调用以构造场景上下文。
/// </summary>
public static class WorldSnapshotBuilder
{
    /// <summary>
    ///     从当前游戏状态构造 WorldSnapshot（场景上下文）。
    ///     调用方需保证 Game1.currentLocation / Game1.player 已初始化，
    ///     且 Game1.getCharacterFromName(npcName) 返回非空 NPC。
    /// </summary>
    /// <param name="npcName">目标 NPC 名称（用于读取 NPC 瓦片位置与好感度）。</param>
    /// <returns>WorldSnapshot 实例。</returns>
    /// <exception cref="ArgumentNullException">当 npcName 为空或 NPC 不存在时抛出。</exception>
    public static WorldSnapshot Build(string npcName) => Build(npcName, "IDLE");

    /// <summary>
    ///     从当前游戏状态构造 WorldSnapshot（场景上下文），并传入 NPC 真实 AgentState。
    ///     调用方需保证 Game1.currentLocation / Game1.player 已初始化，
    ///     且 Game1.getCharacterFromName(npcName) 返回非空 NPC。
    /// </summary>
    /// <param name="npcName">目标 NPC 名称（用于读取 NPC 瓦片位置与好感度）。</param>
    /// <param name="npcState">NPC 当前 AgentState（FOLLOW/IDLE/TALK 等），让 LLM 感知自身状态。</param>
    /// <returns>WorldSnapshot 实例。</returns>
    /// <exception cref="ArgumentNullException">当 npcName 为空或 NPC 不存在时抛出。</exception>
    public static WorldSnapshot Build(string npcName, string npcState)
    {
        if (string.IsNullOrEmpty(npcName))
        {
            throw new ArgumentNullException(nameof(npcName));
        }

        var npc = Game1.getCharacterFromName(npcName);
        if (npc == null)
        {
            throw new ArgumentNullException(nameof(npcName), $"NPC '{npcName}' not found in current game state");
        }

        // E0-6: NPC 自己的钱包/背包（认知对齐）。阶段3 起 TryGetBrain 覆盖活跃与休眠（默认创建）NPC，
        // 未找到时留 null（认知缺省而非编造）。
        AgentInventory? agentInventory = null;
        AgentInstance? agent = null;
        var agentService = AgentService.Current;
        if (agentService != null && agentService.TryGetBrain(npcName, out var found) && found != null)
        {
            agent = found;
            agentInventory = agent.Inventory;
        }

        return new WorldSnapshot(
            Game1.currentSeason,
            Game1.dayOfMonth,
            $"{Game1.timeOfDay / 100:D2}:{Game1.timeOfDay % 100:D2}",
            Game1.isRaining ? Game1.isLightning ? "Stormy" : "Rainy" : "Sunny",
            Game1.currentLocation?.Name ?? "Unknown",
            new TilePosition((int)npc.Tile.X, (int)npc.Tile.Y),
            GetNearbyObjectsSummary(npc),
            Game1.player.friendshipData.TryGetValue(npcName, out var fs) ? fs.Points : 0,
            npcState,
            Game1.player.Items.Where(i => i != null).Select(i => new InventoryItem(i.Name, i.Stack)).ToList(),
            Game1.player.Name,
            BuildPresentNpcs(),
            // E4-1: 玩家钱包（缺钱/有钱 NPC 差异化接单判定的 LLM 输入）。
            Game1.player.Money,
            // E0-6: NPC 自身位置/钱包/背包；既有 Location/Inventory 字段是玩家视角，不动。
            npc.currentLocation?.Name ?? "Unknown",
            agentInventory?.Money,
            // 物品/金币管理迁移：npcInventory 是 TS 决策层的账本键，必须发 QualifiedItemId
            // （如 (O)388），与 give_item/give_gift 工具的 item_id 参数及 AgentInventory.TryRemove
            // 的匹配键一致。旧值 Item.Name（显示名）导致 TS 校验永远匹配不上（-name vs (O)388-），
            // 决策层形同虚设。玩家背包（上方 inventory 字段）保持显示名——纯 prompt 展示用途。
            agentInventory?.GetAllItems()
                .Where(i => i != null)
                .Select(i => new InventoryItem(i!.QualifiedItemId, i!.Stack))
                .ToList(),
            // E3-6: 玩家手持物 + 市场参考价。仅当手持可赠送物（Object 且 canBeGivenAsGift）时注入，
            // 让 NPC 在玩家触发"送礼/交易"菜单时能以市场价为锚出价，防 LLM 臆想定价。
            BuildPlayerHeldItem(),
            // 阶段3 L2: NPC 自身状态摘要（心情/近期事件/工作标记/欠款）。无 brain 时为 null（认知缺省）。
            // 心情空串按 null 处理（tail-default-null 兼容约定）。
            agent?.Brain.MoodTag is { Length: > 0 } moodTag ? moodTag : null,
            agent?.Brain.RecentEventTexts,
            agent?.Brain.WorkingOn,
            agent?.Brain.OwedMoney,
            // 阶段3 L3: 当前活跃 beat 场景描述（BeatStore 当前实例；无 beat 时为 null）。
            BeatStore.Current?.GetActiveBeat(npcName)?.SceneDesc
        );
    }

    /// <summary>
    ///     E3-6: 构建玩家手持物摘要（含市场参考价）。仅当玩家手持 Object 且 canBeGivenAsGift 时返回非空。
    ///     MarketPrice 用 sellToStorePrice（卖给商店的公道价）；解析失败返回 null（认知缺省）。
    /// </summary>
    private static PlayerHeldItem? BuildPlayerHeldItem()
    {
        var held = Game1.player?.ActiveObject;
        if (held == null || !held.canBeGivenAsGift())
        {
            return null;
        }

        var itemId = held.QualifiedItemId ?? held.ItemId ?? "";
        if (string.IsNullOrEmpty(itemId))
        {
            return null;
        }

        var marketPrice = held.sellToStorePrice();
        if (marketPrice <= 0)
        {
            // sellToStorePrice 失败时回落到 salePrice（部分物品无销路但仍可定价）
            marketPrice = held.salePrice();
        }

        return new PlayerHeldItem(
            itemId,
            held.DisplayName ?? held.Name ?? itemId,
            held.Stack > 0 ? held.Stack : 1,
            marketPrice);
    }

    /// <summary>
    ///     E2-2: 构建玩家视角下的在场 NPC 清单（同地图村民），供 LLM 沉默权判定使用。
    ///     距玩家曼哈顿距离、AgentState 角色、是否跟随、是否处于会话模式。
    ///     无 Game1 状态或不在场时返回空列表（不会抛异常）。
    /// </summary>
    private static List<PresentNpcInfo> BuildPresentNpcs()
    {
        var location = Game1.currentLocation;
        var player = Game1.player;
        if (location == null || player == null || location.characters == null)
        {
            return new List<PresentNpcInfo>();
        }

        var result = new List<PresentNpcInfo>();
        foreach (var character in location.characters)
        {
            if (character is not NPC villager || !villager.IsVillager || villager.Tile == null)
            {
                continue;
            }

            var distance = (int)(Math.Abs(villager.Tile.X - player.Tile.X)
                                 + Math.Abs(villager.Tile.Y - player.Tile.Y));
            var state = GetAgentStateFlag(villager.Name);
            result.Add(new PresentNpcInfo(
                villager.Name,
                state,
                distance,
                state == AgentState.FOLLOW.ToString(),
                ChatSessionRegistry.Instance.IsInActiveSession(villager.Name)));
        }

        return result;
    }

    private static string GetAgentStateFlag(string npcName)
    {
        var service = AgentService.Current;
        if (service != null && service.TryGetAgent(npcName, out var agent) && agent != null)
        {
            return agent.StateMachine.CurrentStateFlag.ToString();
        }

        return AgentState.IDLE.ToString();
    }

    /// <summary>
    ///     构建 NPC 周围环境摘要（附近 NPC / 物品 / 出口），供 LLM 感知场景。
    ///     从 DialogueBoxInputPatch 抽取以共享给主机端代理链路。
    /// </summary>
    private static string GetNearbyObjectsSummary(NPC npc)
    {
        var location = npc.currentLocation;
        if (location == null)
        {
            return "unknown location";
        }

        var parts = new List<string>();

        var nearbyNpcs = location.characters
            .Where(c => c != npc && c.Tile != null && Vector2.Distance(c.Tile, npc.Tile) < 5f)
            .Select(c => c.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Take(5)
            .ToList();
        if (nearbyNpcs.Count > 0)
        {
            parts.Add($"{nearbyNpcs.Count} villagers ({string.Join(", ", nearbyNpcs)})");
        }

        var nearbyObjects = location.Objects.Values
            .Where(o => Vector2.Distance(o.TileLocation, npc.Tile) < 3f)
            .Select(o => o.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Take(5)
            .ToList();
        if (nearbyObjects.Count > 0)
        {
            parts.Add($"{nearbyObjects.Count} objects ({string.Join(", ", nearbyObjects)})");
        }

        var nearbyWarps = location.warps
            .Where(w => Vector2.Distance(new Vector2(w.X, w.Y), npc.Tile) < 5f)
            .Select(w => w.TargetName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Take(3)
            .ToList();
        if (nearbyWarps.Count > 0)
        {
            parts.Add($"{nearbyWarps.Count} exits to {string.Join(", ", nearbyWarps)}");
        }

        return parts.Count > 0 ? string.Join("; ", parts) : "empty area";
    }
}