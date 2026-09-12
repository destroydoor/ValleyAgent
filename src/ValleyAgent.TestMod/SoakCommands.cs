#nullable enable
using System;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod;

/// <summary>
///     3 人联机主机卡死复刻（2026-09-10 soak，docs/plan/2026-09-10-host-freeze-repro-plan.md）的测试驱动命令。
///     只布置场景（玩家位置 / Agent 状态），不改 ValleyAgent 生产逻辑：
///     - va_mp_goto：本机玩家 SafeWarp 到指定地图（三房客分赴不同图的场景布置）；
///     - va_soak_follow：把已分配 Agent 强制置 FOLLOW（TrySetAgentState→ForceTransition，
///       与 E1/EXP 测试同款 API；真实场景里 FOLLOW 由 LLM 对话动作触发，测试驱动省 token 且确定）；
///     - va_soak_diag：dump 在线玩家（id/所在图/瓦片）+ 各 Agent（状态/NPC 所在图/LastDialoguePlayerId），
///       供 soak 脚本断言场景是否就位。
/// </summary>
public static class SoakCommands
{
    public static void Register(IModHelper helper, IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(monitor);

        _ = helper.ConsoleCommands.Add("va_mp_goto",
            "SafeWarp the local player (host or farmhand) to a map.\n" +
            "Usage: va_mp_goto <map_name> [x y]  (default 20 20; landing auto-corrected by WarpTargetGuard)",
            (_, args) => RunGoto(args, monitor));

        _ = helper.ConsoleCommands.Add("va_soak_follow",
            "Force an allocated agent NPC into FOLLOW state (soak scenario staging).\n" +
            "Usage: va_soak_follow <npc_name>",
            (_, args) => RunFollow(args, monitor));

        _ = helper.ConsoleCommands.Add("va_soak_diag",
            "Dump online players and agent states (soak scenario verification).\n" +
            "Usage: va_soak_diag",
            (_, _) => RunDiag(monitor));
    }

    private static void RunGoto(string[] args, IMonitor monitor)
    {
        if (args.Length < 1)
        {
            monitor.Log("[Soak] Usage: va_mp_goto <map_name> [x y]", LogLevel.Error);
            return;
        }

        var mapName = args[0];
        var x = args.Length > 1 && int.TryParse(args[1], out var px) ? px : 20;
        var y = args.Length > 2 && int.TryParse(args[2], out var py) ? py : 20;

        var player = Game1.player;
        if (player == null || !Game1.hasLoadedGame)
        {
            monitor.Log("[Soak] Goto FAIL: game not loaded.", LogLevel.Error);
            return;
        }

        try
        {
            var landed = SafeWarp.Farmer(monitor, mapName, x, y, "va_mp_goto");
            monitor.Log(
                $"[Soak] Goto PASS: player '{player.Name}' (id={player.UniqueMultiplayerID}) → " +
                $"{mapName} @ ({landed.X},{landed.Y})",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            monitor.Log($"[Soak] Goto FAIL: warp to {mapName} threw {ex.Message}", LogLevel.Error);
        }
    }

    private static void RunFollow(string[] args, IMonitor monitor)
    {
        var npcName = args.Length > 0 ? args[0] : TestConfig.NpcName;
        var api = ValleyAgent.ModEntry.Instance?.API;
        if (api == null)
        {
            monitor.Log("[Soak] Follow FAIL: ValleyAgent API 未获取到。", LogLevel.Error);
            return;
        }

        var ok = api.TrySetAgentState(npcName, "FOLLOW");
        var stateAfter = "unknown";
        if (TryGetAgentForDiag(npcName, out var agent))
        {
            stateAfter = agent!.StateMachine.CurrentStateFlag.ToString();
        }

        monitor.Log(
            $"[Soak] Follow {(ok ? "PASS" : "FAIL")}: {npcName} → FOLLOW (api={ok}, stateAfter={stateAfter})",
            ok ? LogLevel.Info : LogLevel.Error);
    }

    private static void RunDiag(IMonitor monitor)
    {
        if (!Game1.hasLoadedGame)
        {
            monitor.Log("[Soak] Diag: game not loaded.", LogLevel.Warn);
            return;
        }

        foreach (var farmer in Game1.getOnlineFarmers())
        {
            if (farmer == null)
            {
                continue;
            }

            monitor.Log(
                $"[Soak] Player name='{farmer.Name}' id={farmer.UniqueMultiplayerID} " +
                $"isLocal={farmer.IsLocalPlayer} loc='{farmer.currentLocation?.Name ?? "?"}' " +
                $"tile=({farmer.TilePoint.X},{farmer.TilePoint.Y})",
                LogLevel.Info);
        }

        var api = ValleyAgent.ModEntry.Instance?.API;
        var names = api?.GetActiveAgentNames() ?? Array.Empty<string>();
        foreach (var name in names)
        {
            if (!TryGetAgentForDiag(name, out var agent) || agent == null)
            {
                monitor.Log($"[Soak] Agent {name}: <instance not found>", LogLevel.Warn);
                continue;
            }

            var npc = Game1.getCharacterFromName(name);
            monitor.Log(
                $"[Soak] Agent {name}: state={agent.StateMachine.CurrentStateFlag} " +
                $"npcLoc='{npc?.currentLocation?.Name ?? "?"}' npcTile=({npc?.TilePoint.X},{npc?.TilePoint.Y}) " +
                $"lastDialoguePlayerId={agent.Brain.LastDialoguePlayerId ?? "<null>"}",
                LogLevel.Info);
        }

        monitor.Log(
            $"[Soak] Diag: time={Game1.timeOfDay} day={Game1.dayOfMonth} " +
            $"season={Game1.season} onlinePlayers={Game1.getOnlineFarmers().Count()}",
            LogLevel.Info);
        monitor.Log($"[Soak] Diag done. agents={names.Length}", LogLevel.Info);
    }

    private static bool TryGetAgentForDiag(string npcName, out ValleyAgent.Services.AgentInstance? agent)
    {
        agent = ValleyAgent.ModEntry.Instance?.Container?
            .GetService<ValleyAgent.Services.AgentService>() is { } service
            && service.TryGetAgent(npcName, out var found)
            ? found
            : null;
        return agent != null;
    }
}
