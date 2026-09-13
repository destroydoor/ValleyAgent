using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Beats;
using ValleyAgent.Brain;
using ValleyAgent.Patches;
using ValleyAgent.Protocol;
using ValleyAgent.Services;
using ActionResultReason = ValleyAgent.Protocol.ProtocolV2.ActionResultReason;

namespace ValleyAgent.Commands;

/// <summary>
///     Director 工具集（阶段 3，3.3.1）。这是**元层**工具——只改 C# 状态数据
///     （位置/钱包/背包/心情/近期事件/工作标记/beat/记忆），绝不进入 NPC 的 LLM 上下文。
///     Director 没有 speak/emote/give_item/trade/set_goal——那些是 NPC Agent 的角色扮演工具。
///
///     消息入口：TS 发 director_command → EventHandlerInitializer 路由 → CommandExecutor.ExecuteDirectorCommand → 本类。
///     每个工具：校验参数 → 变更状态 → 返回 (true, None, "") 或 (false, reason, message)。
/// </summary>
public class DirectorTools
{
    private readonly IMonitor? _monitor;
    private readonly BeatStore? _beatStore;
    private readonly int _defaultBeatDurationMinutes;
    private readonly Func<string, AgentInstance?> _brainResolver;

    /// <summary>
    ///     行为类工具的"确保身体"接缝（PR2 B4，设计 §3.3）：入参 (npcName, tool)，
    ///     返回 true 表示该 NPC 已持有身体或已成功建身体。null = 未接线形态
    ///     （internal 构造器直建的单测路径），此时行为类工具跳过建身体、保持既有行为。
    /// </summary>
    private readonly Func<string, string, bool>? _ensureBody;

    public DirectorTools(IMonitor monitor, AgentService agentService, BeatStore? beatStore = null, int defaultBeatDurationMinutes = 120)
        : this(monitor, beatStore, defaultBeatDurationMinutes,
            npcName => agentService.TryGetBrain(npcName, out var agent) ? agent : null,
            (npcName, tool) => EnsureBodyForDirectorTool(agentService, npcName))
    {
    }

    /// <summary>
    ///     内部构造（单测接缝）：注入 brain 解析器与 beat 存储，不依赖 AgentService 实例。
    ///     ensureBody 供 spy 注入：验证行为类工具会调分配、数据类工具从不调分配。
    /// </summary>
    internal DirectorTools(
        IMonitor? monitor,
        BeatStore? beatStore,
        int defaultBeatDurationMinutes,
        Func<string, AgentInstance?> brainResolver,
        Func<string, string, bool>? ensureBody = null)
    {
        _monitor = monitor;
        _beatStore = beatStore;
        _defaultBeatDurationMinutes = defaultBeatDurationMinutes;
        _brainResolver = brainResolver ?? throw new ArgumentNullException(nameof(brainResolver));
        _ensureBody = ensureBody;
    }

    /// <summary>
    ///     行为类工具的默认建身体实现（B4）：已有身体直接放行；无身体时先做 NPC 存在性守卫
    ///     （防幽灵身体占名额，与房客中继路径同款守卫），再走 PromoteToAgent——
    ///     ForceAllocate + manual override 释放循环 + KeepUntil 豁免，与对话 promote 语义完全一致。
    ///     分配失败（满员无可替槽 / NPC 不存在 / 游戏状态未就绪）返回 false，
    ///     调用方按 AgentMissing 失败语义处理，不抛异常。
    /// </summary>
    private static bool EnsureBodyForDirectorTool(AgentService agentService, string npcName)
    {
        if (agentService.HasAgent(npcName))
        {
            return true;
        }

        NPC? npc;
        try
        {
            npc = Game1.getCharacterFromName<NPC>(npcName);
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            npc = null;
        }

        if (npc == null)
        {
            return false;
        }

        return DialogueBoxInputPatch.PromoteToAgent(npcName, "director");
    }

    /// <summary>
    ///     执行一个 Director 工具。返回 (成功?, 机器可读原因, 人类可读消息)。
    /// </summary>
    public (bool Success, ActionResultReason Reason, string Message) Execute(string tool, Dictionary<string, object>? args)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            return (false, ActionResultReason.InvalidState, "director tool is empty");
        }

        args ??= new Dictionary<string, object>();
        try
        {
            return tool switch
            {
                "set_npc_position" => SetNpcPosition(args),
                "set_npc_inventory" => SetNpcInventory(args),
                "set_npc_money" => SetNpcMoney(args),
                "set_npc_mood" => SetNpcMood(args),
                "set_npc_recent_events" => SetNpcRecentEvents(args),
                "set_npc_working_on" => SetNpcWorkingOn(args),
                "spawn_beat" => SpawnBeat(args),
                "spawn_group_beat" => SpawnGroupBeat(args),
                "inject_memory" => InjectMemory(args),
                _ => (false, ActionResultReason.InvalidState, $"unknown_director_tool: {tool}")
            };
        }
        catch (InvalidOperationException ex)
        {
            _monitor?.Log($"[DirectorTools] {tool} failed: {ex.Message}", LogLevel.Warn);
            return (false, ActionResultReason.InternalError, ex.Message);
        }
    }

    // ───────────────────────── 工具实现 ─────────────────────────

    /// <summary>set_npc_position(npc, location, tile:[x,y])：移动 NPC（跨图 warp / 同图 setTileLocation）。</summary>
    internal (bool Success, ActionResultReason Reason, string Message) SetNpcPosition(Dictionary<string, object> args)
    {
        var npcName = GetString(args, "npc");
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return (false, ActionResultReason.InvalidState, "set_npc_position: missing npc");
        }

        var locationName = GetString(args, "location");
        if (string.IsNullOrWhiteSpace(locationName))
        {
            return (false, ActionResultReason.InvalidState, "set_npc_position: missing location");
        }

        if (!TryGetTile(args, out var tile))
        {
            return (false, ActionResultReason.InvalidState, "set_npc_position: missing/invalid tile [x, y]");
        }

        // B4 行为类工具（设计 §3.3）：warp 前确保身体——无身体的 NPC 会被原版日程立即拉回，
        // warp 不持久（mood/working_on 也无行为表达）。纯数据类工具（mood/recent_events/working_on/
        // inventory/money/inject_memory/spawn_beat）不建身体——"改个心情不该占一个身体名额"。
        // 分配失败（满员无可替槽等）按既有失败语义返回 AgentMissing，不抛异常；
        // _ensureBody == null 为未接线形态（internal 构造器直建的旧单测路径），保持既有行为。
        if (_ensureBody != null && !_ensureBody(npcName, "set_npc_position"))
        {
            _monitor?.Log(
                $"[DirectorTools] set_npc_position: body allocation failed for '{npcName}' (pool full or npc missing)",
                LogLevel.Warn);
            return (false, ActionResultReason.AgentMissing,
                $"set_npc_position: body allocation failed for '{npcName}'");
        }

        NPC? npc;
        try
        {
            npc = Game1.getCharacterFromName<NPC>(npcName);
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            _monitor?.Log($"set_npc_position: game state not initialized for '{npcName}': {ex.Message}", LogLevel.Warn);
            return (false, ActionResultReason.AgentMissing, "set_npc_position: game state not initialized");
        }

        if (npc == null)
        {
            return (false, ActionResultReason.AgentMissing, $"set_npc_position: npc '{npcName}' not found");
        }

        var targetLocation = Game1.getLocationFromName(locationName);
        if (targetLocation == null)
        {
            return (false, ActionResultReason.LocationInvalid, $"set_npc_position: location '{locationName}' not found");
        }

        if (npc.currentLocation != targetLocation)
        {
            Game1.warpCharacter(npc, targetLocation, tile);
        }
        else
        {
            npc.setTileLocation(tile);
        }

        _monitor?.Log($"[DirectorTools] set_npc_position: {npcName} → {locationName} ({tile.X},{tile.Y})", LogLevel.Info);
        return (true, ActionResultReason.None, "");
    }

    /// <summary>set_npc_inventory(npc, add?, remove?)：增删 NPC 背包。add/remove 可为单个 itemId 或数组。</summary>
    internal (bool Success, ActionResultReason Reason, string Message) SetNpcInventory(Dictionary<string, object> args)
    {
        var npcName = GetString(args, "npc");
        var agent = ResolveAgent(npcName, "set_npc_inventory");
        if (agent == null)
        {
            return (false, ActionResultReason.AgentMissing, "set_npc_inventory: agent_missing");
        }

        var quantity = GetInt(args, "quantity", 1);
        if (quantity <= 0)
        {
            quantity = 1;
        }

        var addItems = GetStringList(args, "add");
        if (addItems.Count > 0)
        {
            foreach (var itemId in addItems)
            {
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    continue;
                }

                Item? item;
                try
                {
                    item = ItemRegistry.Create(itemId, quantity, allowNull: true);
                }
                catch (NullReferenceException)
                {
                    // 游戏数据未初始化/物品不存在 → 视为未知物品，避免拖垮 director 调用链
                    return (false, ActionResultReason.ItemNotFound, $"set_npc_inventory: unknown item_id '{itemId}'");
                }

                if (item == null)
                {
                    return (false, ActionResultReason.ItemNotFound, $"set_npc_inventory: unknown item_id '{itemId}'");
                }

                if (!agent.Inventory.TryAdd(item))
                {
                    return (false, ActionResultReason.InventoryFull, $"set_npc_inventory: inventory full for {npcName}");
                }
            }
        }

        var removeItems = GetStringList(args, "remove");
        if (removeItems.Count > 0)
        {
            foreach (var itemId in removeItems)
            {
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    continue;
                }

                if (!agent.Inventory.RemoveItem(itemId, quantity))
                {
                    return (false, ActionResultReason.ItemNotFound, $"set_npc_inventory: '{itemId}' not in {npcName}'s inventory");
                }
            }
        }

        _monitor?.Log($"[DirectorTools] set_npc_inventory: {npcName} +{addItems.Count} -{removeItems.Count}", LogLevel.Info);
        return (true, ActionResultReason.None, "");
    }

    /// <summary>set_npc_money(npc, delta)：改钱包。走 AddMoney/TrySpend 保证钱包事件留痕；余额不足拒绝。</summary>
    internal (bool Success, ActionResultReason Reason, string Message) SetNpcMoney(Dictionary<string, object> args)
    {
        var npcName = GetString(args, "npc");
        var agent = ResolveAgent(npcName, "set_npc_money");
        if (agent == null)
        {
            return (false, ActionResultReason.AgentMissing, "set_npc_money: agent_missing");
        }

        var delta = GetInt(args, "delta", 0);
        if (delta == 0)
        {
            return (false, ActionResultReason.InvalidState, "set_npc_money: delta must be non-zero");
        }

        if (delta > 0)
        {
            _ = agent.Inventory.AddMoney(delta, "director_grant");
        }
        else if (!agent.Inventory.TrySpend(-delta, "director_take"))
        {
            return (false, ActionResultReason.InvalidState, $"set_npc_money: {npcName} has insufficient funds ({agent.Inventory.Money}g)");
        }

        _monitor?.Log($"[DirectorTools] set_npc_money: {npcName} {delta:+0;-0}g → {agent.Inventory.Money}g", LogLevel.Info);
        return (true, ActionResultReason.None, "");
    }

    /// <summary>set_npc_mood(npc, moodTag)：写 L2 心情标签。</summary>
    internal (bool Success, ActionResultReason Reason, string Message) SetNpcMood(Dictionary<string, object> args)
    {
        var npcName = GetString(args, "npc");
        var agent = ResolveAgent(npcName, "set_npc_mood");
        if (agent == null)
        {
            return (false, ActionResultReason.AgentMissing, "set_npc_mood: agent_missing");
        }

        var moodTag = GetString(args, "moodTag") ?? GetString(args, "mood");
        agent.Brain.MoodTag = moodTag ?? "";
        _monitor?.Log($"[DirectorTools] set_npc_mood: {npcName} → '{agent.Brain.MoodTag}'", LogLevel.Info);
        return (true, ActionResultReason.None, "");
    }

    /// <summary>set_npc_recent_events(npc, events)：整体替换 L2 todayEvents（无日期前缀 → 视为当日）。</summary>
    internal (bool Success, ActionResultReason Reason, string Message) SetNpcRecentEvents(Dictionary<string, object> args)
    {
        var npcName = GetString(args, "npc");
        var agent = ResolveAgent(npcName, "set_npc_recent_events");
        if (agent == null)
        {
            return (false, ActionResultReason.AgentMissing, "set_npc_recent_events: agent_missing");
        }

        var events = GetStringList(args, "events");
        if (events.Count == 0)
        {
            return (false, ActionResultReason.InvalidState, "set_npc_recent_events: events must be a non-empty list");
        }

        agent.Brain.TodayEvents.Clear();
        foreach (var e in events)
        {
            if (!string.IsNullOrWhiteSpace(e))
            {
                agent.Brain.TodayEvents.Add(e);
            }

            if (agent.Brain.TodayEvents.Count >= AgentBrain.MaxTodayEvents)
            {
                break;
            }
        }

        _monitor?.Log($"[DirectorTools] set_npc_recent_events: {npcName} ← {agent.Brain.TodayEvents.Count} events", LogLevel.Info);
        return (true, ActionResultReason.None, "");
    }

    /// <summary>set_npc_working_on(npc, workingOn?)：设/清 L2 工作标记。</summary>
    internal (bool Success, ActionResultReason Reason, string Message) SetNpcWorkingOn(Dictionary<string, object> args)
    {
        var npcName = GetString(args, "npc");
        var agent = ResolveAgent(npcName, "set_npc_working_on");
        if (agent == null)
        {
            return (false, ActionResultReason.AgentMissing, "set_npc_working_on: agent_missing");
        }

        var workingOn = GetString(args, "workingOn");
        agent.Brain.WorkingOn = string.IsNullOrWhiteSpace(workingOn) ? null : workingOn;
        _monitor?.Log($"[DirectorTools] set_npc_working_on: {npcName} → '{agent.Brain.WorkingOn ?? "none"}'", LogLevel.Info);
        return (true, ActionResultReason.None, "");
    }

    /// <summary>spawn_beat(npc, sceneDesc, expectedInteraction?, durationMinutes?)：创建单 NPC beat（L3 剧本）。</summary>
    internal (bool Success, ActionResultReason Reason, string Message) SpawnBeat(Dictionary<string, object> args)
    {
        var npcName = GetString(args, "npc");
        var sceneDesc = GetString(args, "sceneDesc") ?? GetString(args, "sceneScript");
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(sceneDesc))
        {
            return (false, ActionResultReason.InvalidState, "spawn_beat: missing npc or sceneDesc");
        }

        if (_beatStore == null)
        {
            return (false, ActionResultReason.InternalError, "spawn_beat: BeatStore not wired");
        }

        var expected = GetString(args, "expectedInteraction");
        var duration = GetDuration(args, _defaultBeatDurationMinutes);
        var playerVisible = GetBool(args, "playerVisible", true);
        _beatStore.Create(npcName, sceneDesc, expected, duration, playerVisible);
        return (true, ActionResultReason.None, "");
    }

    /// <summary>spawn_group_beat(npcs, location, sceneScript, durationMinutes?)：创建多 NPC 群体 beat。</summary>
    internal (bool Success, ActionResultReason Reason, string Message) SpawnGroupBeat(Dictionary<string, object> args)
    {
        var npcs = GetStringList(args, "npcs");
        var sceneScript = GetString(args, "sceneScript");
        if (npcs.Count == 0 || string.IsNullOrWhiteSpace(sceneScript))
        {
            return (false, ActionResultReason.InvalidState, "spawn_group_beat: missing npcs or sceneScript");
        }

        if (_beatStore == null)
        {
            return (false, ActionResultReason.InternalError, "spawn_group_beat: BeatStore not wired");
        }

        var location = GetString(args, "location") ?? "Town";
        var duration = GetDuration(args, _defaultBeatDurationMinutes);
        var playerVisible = GetBool(args, "playerVisible", true);
        _beatStore.CreateGroup(npcs, location, sceneScript, duration, playerVisible);
        return (true, ActionResultReason.None, "");
    }

    /// <summary>inject_memory(npc, text, importance?, tags?)：注入 L1 长期记忆。</summary>
    internal (bool Success, ActionResultReason Reason, string Message) InjectMemory(Dictionary<string, object> args)
    {
        var npcName = GetString(args, "npc");
        var agent = ResolveAgent(npcName, "inject_memory");
        if (agent == null)
        {
            return (false, ActionResultReason.AgentMissing, "inject_memory: agent_missing");
        }

        var text = GetString(args, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return (false, ActionResultReason.InvalidState, "inject_memory: missing text");
        }

        var importance = GetDouble(args, "importance", 1.0);
        var tags = GetStringList(args, "tags");
        agent.Brain.AddMemory(text, Math.Clamp(importance, 0.0, 10.0), MemoryEntryType.Generic, "", tags);
        _monitor?.Log($"[DirectorTools] inject_memory: {npcName} (importance={importance:F1})", LogLevel.Info);
        return (true, ActionResultReason.None, "");
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private AgentInstance? ResolveAgent(string? npcName, string tool)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return null;
        }

        var agent = _brainResolver(npcName);
        if (agent == null)
        {
            _monitor?.Log($"[DirectorTools] {tool}: brain missing for '{npcName}'", LogLevel.Warn);
        }

        return agent;
    }

    /// <summary>取 tile 参数：支持 "tile":[x,y] 或 "tile":{"x":..,"y":..}。</summary>
    internal static bool TryGetTile(Dictionary<string, object> args, out Vector2 tile)
    {
        tile = Vector2.Zero;
        if (!args.TryGetValue("tile", out var tileObj) || tileObj == null)
        {
            return false;
        }

        if (tileObj is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("x", out var xProp) && xProp.TryGetInt32(out var x)
                && element.TryGetProperty("y", out var yProp) && yProp.TryGetInt32(out var y))
            {
                tile = new Vector2(x, y);
                return true;
            }

            return false;
        }

        if (TryGetIntArray(args, "tile", out var xy) && xy.Length >= 2)
        {
            tile = new Vector2(xy[0], xy[1]);
            return true;
        }

        return false;
    }

    /// <summary>取字符串参数（兼容 JsonElement / 原生 string）。</summary>
    internal static string? GetString(Dictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value is JsonElement element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()
            : value.ToString();
    }

    internal static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
    {
        var raw = GetString(args, key);
        return raw != null && int.TryParse(raw, out var result) ? result : defaultValue;
    }

    internal static double GetDouble(Dictionary<string, object> args, string key, double defaultValue)
    {
        var raw = GetString(args, key);
        return raw != null && double.TryParse(raw, out var result) ? result : defaultValue;
    }

    internal static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
    {
        var raw = GetString(args, key);
        return raw != null && bool.TryParse(raw, out var result) ? result : defaultValue;
    }

    /// <summary>取字符串数组参数：支持 "key":[a,b]（JsonElement 数组或 .NET 集合）或 "key":"single"。</summary>
    internal static List<string> GetStringList(Dictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value == null)
        {
            return new List<string>();
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList()!;
        }

        // .NET 集合（单测/非 JSON 路径）：逐个 ToString
        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            return enumerable.Cast<object?>()
                .Where(o => o != null && !string.IsNullOrWhiteSpace(o.ToString()))
                .Select(o => o!.ToString()!)
                .ToList();
        }

        var single = GetString(args, key);
        return string.IsNullOrWhiteSpace(single) ? new List<string>() : new List<string> { single };
    }

    internal static bool TryGetIntArray(Dictionary<string, object> args, string key, out int[] result)
    {
        result = Array.Empty<int>();
        if (!args.TryGetValue(key, out var value) || value == null)
        {
            return false;
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray()
                .Select(e => e.TryGetInt32(out var v) ? v : -1)
                .ToArray();
            if (items.Length == 0 || items.Any(v => v < 0))
            {
                return false;
            }

            result = items;
            return true;
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var parsed = enumerable.Cast<object?>()
                .Select(o => o != null && int.TryParse(o.ToString(), out var v) ? v : -1)
                .ToArray();
            if (parsed.Length == 0 || parsed.Any(v => v < 0))
            {
                return false;
            }

            result = parsed;
            return true;
        }

        return false;
    }

    /// <summary>beat 有效期：durationMinutes 显式则用之，否则用配置默认值。</summary>
    private static TimeSpan GetDuration(Dictionary<string, object> args, int defaultMinutes)
    {
        var minutes = GetInt(args, "durationMinutes", defaultMinutes);
        return TimeSpan.FromMinutes(Math.Max(1, minutes));
    }
}
