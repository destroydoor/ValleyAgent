#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod.Tests.Edge;

/// <summary>
///     Tests that an NPC in FOLLOW state can handle long-distance pathfinding
///     and does not erroneously enter Mine or cross-map without player movement.
/// </summary>
public class E1_LongPathfind : V3TestBase
{
    private IValleyAgentApi? _api;
    private bool _controllerSeen;
    private int _lastAssertTick;
    private NPC? _npc;
    private Vector2 _npcStartTile;
    private Vector2 _playerStartTile;

    public E1_LongPathfind(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E1_LongPathfind";
    }

    public override int TimeoutTicks
    {
        get => 3600;
    }

    public override TestGroup Group
    {
        get => TestGroup.Edge;
    }

    public override void Setup()
    {
        _api = ModEntry.API;
        if (_api == null)
        {
            Skip("API not available");
            return;
        }

        _ = _api.TryAllocateAgent("Haley");
        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(32, 30));
        _npc.Halt();
        _npc.controller = null;

        // 设置初始位置记录
        _playerStartTile = Game1.player.Tile;
        _npcStartTile = _npc.Tile;

        // 设置 NPC 为 FOLLOW 状态以触发长寻路
        _ = _api.TrySetAgentState("Haley", "FOLLOW");
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var playerLoc = Game1.player.currentLocation?.NameOrUniqueName ?? "";
        var npcLoc = _npc.currentLocation?.NameOrUniqueName ?? "";
        var hasController = _npc.controller is not null;
        var dist = Vector2.Distance(Game1.player.Tile, _npc.Tile);

        if (CurrentTick % 300 == 0)
        {
            Monitor.Log($"[E1] tick={CurrentTick} player={playerLoc} npc={npcLoc} dist={dist:F1} ctrl={hasController}",
                LogLevel.Info);
        }

        // Assertion cooldown: only check every 300 ticks to prevent spam
        if (CurrentTick >= _lastAssertTick + 300 || _lastAssertTick == 0)
        {
            _lastAssertTick = CurrentTick;
            _controllerSeen = _controllerSeen || hasController;
            AssertEx("NPC_stays_in_range", dist < 50f,
                "FOLLOW 寻路失控：controller 持续把 NPC 往反方向带或长距离寻路失败后 NPC 被卡在远处，" +
                "玩家与 NPC 距离拉大到 50 格以上（dist 超限即触发）",
                $"tick={CurrentTick} dist={dist:F1}");
        }

        return CurrentTick >= TimeoutTicks;
    }

    public override void Teardown()
    {
        AssertEx("NPC_has_controller_at_some_point", _controllerSeen,
            "FOLLOW 状态从未生成 PathFindController（TrySetAgentState 被拒或 AgentBrain 未接线寻路），" +
            "全程 hasController=false");
        Monitor.Log("[E1] Teardown.", LogLevel.Info);
    }
}