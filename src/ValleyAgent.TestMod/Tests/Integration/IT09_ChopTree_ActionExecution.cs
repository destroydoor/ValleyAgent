#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using ValleyAgent.WebSocket;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT09：验证 chop_tree action 在 C# 端的执行行为（CommandExecutor.ExecuteChopTree）。
///     ExecuteChopTree 在 NPC 附近 MaxRadius=3 格内查找最近的非 stump Tree，
///     移除该 terrainFeature 并掉落木 debris。
///     验证点：
///     1. 在 NPC 附近放一棵成熟 Tree，ExecuteAction(chop_tree) 后该 tile 不在 terrainFeatures
///     2. ExecuteAction 不抛异常
///     3. NPC 状态可读（未崩溃）
/// </summary>
public class IT09_ChopTree_ActionExecution : IntegrationTestBase
{
    private bool _actionExecuted;
    private bool _asserted;
    private CommandExecutor? _executor;
    private Vector2 _treeTile;

    public IT09_ChopTree_ActionExecution(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT09_ChopTree_ActionExecution";
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

        _executor = Container!.GetService<CommandExecutor>();
        if (_executor == null)
        {
            Skip("CommandExecutor not registered in container");
            return;
        }

        // 拦截 OnSendActionResult 避免实际发到 WS
        _executor.OnSendActionResult = (_, _, _, _) => Task.FromResult(true);

        // 在 NPC 附近 1 格放一棵成熟 Tree（growthStage=5）
        var npc = Game1.getCharacterFromName(NpcName);
        if (npc == null || npc.currentLocation == null)
        {
            Skip("NPC or currentLocation is null");
            return;
        }

        var npcTile = npc.Tile;
        _treeTile = new Vector2((int)npcTile.X + 1, (int)npcTile.Y);

        // 移除 NPC 周围 3 格内所有现有 Tree（ExecuteChopTree 的 MaxRadius=3）
        // 地图原生树会干扰 no_leftover_tree_nearby 断言，必须预先清除。
        var loc = npc.currentLocation;
        var tilesToClean = new List<Vector2>();
        foreach (var entry in loc.terrainFeatures.Pairs)
        {
            if (entry.Value is Tree && Vector2.Distance(entry.Key, npcTile) <= 3f)
            {
                tilesToClean.Add(entry.Key);
            }
        }

        foreach (var tile in tilesToClean)
        {
            _ = loc.terrainFeatures.Remove(tile);
        }

        if (tilesToClean.Count > 0)
        {
            Monitor.Log($"[{TestName}] Pre-cleared {tilesToClean.Count} native tree(s) within 3 tiles of NPC",
                LogLevel.Debug);
        }

        // 移除目标 tile 上已有的 terrainFeature（避免冲突）
        if (loc.terrainFeatures.ContainsKey(_treeTile))
        {
            _ = loc.terrainFeatures.Remove(_treeTile);
        }

        try
        {
            // SDV 1.6: Tree 构造接受 treeType (string) + growthStage (int)
            // treeType: "1"=Oak, "2"=Maple, "3"=Pine, "6"=Palm
            // growthStage: 5=fully grown
            var tree = new Tree("1", 5);
            loc.terrainFeatures[_treeTile] = tree;
            Monitor.Log($"[{TestName}] Placed Tree at {_treeTile} in {loc.Name}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Skip($"failed to place Tree: {ex.Message}");
            return;
        }

        Monitor.Log($"[{TestName}] Setup complete. treeTile={_treeTile}", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_executor == null || Api == null)
        {
            return true;
        }

        // Phase 1: 触发 chop_tree action
        if (CurrentTick == 30 && !_actionExecuted)
        {
            _actionExecuted = true;
            var args = new Dictionary<string, object>();
            var action = new ToolAction("chop_tree", args, "it09-call-1");
            try
            {
                _executor.ExecuteAction(action, NpcName);
                Monitor.Log($"[{TestName}] ExecuteAction(chop_tree) returned", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Assert("action_executed_no_throw", false, $"ExecuteAction threw: {ex.Message}");
                return true;
            }
        }

        // Phase 2: 验证树被移除
        if (CurrentTick == 90 && !_asserted)
        {
            _asserted = true;
            var npc = Game1.getCharacterFromName(NpcName);
            var loc = npc?.currentLocation;

            AssertEx("location_available", loc != null,
                "chop_tree 执行链把 NPC 移出地图（死亡清理/传送事故），getCharacterFromName 或 currentLocation 变 null");

            if (loc != null)
            {
                var treeRemoved = !loc.terrainFeatures.ContainsKey(_treeTile);
                AssertEx("tree_removed_from_terrain", treeRemoved,
                    "GoalExecutor 砍树未生效：_treeTile 仍在 terrainFeatures（ChopTreeGoal.TickCore 未调用或树木血量未扣完）",
                    treeRemoved
                        ? $"tile {_treeTile} no longer in terrainFeatures"
                        : $"tile {_treeTile} still in terrainFeatures (tree not chopped)");

                // 验证附近没有其他 stump 残留（ExecuteChopTree 直接 Remove 而非 set stump）
                // 注意：NetVector2Dictionary 直接 foreach 会 yield SerializableDictionary 而非 KeyValuePair，
                // 必须用 .Pairs 属性获取 KeyValuePair<Vector2, TerrainFeature> 枚举。
                var leftoverTile = Vector2.Zero;
                var hasLeftover = false;
                foreach (var entry in loc.terrainFeatures.Pairs)
                {
                    if (entry.Value is Tree && Vector2.Distance(entry.Key, _treeTile) <= 3f)
                    {
                        leftoverTile = entry.Key;
                        hasLeftover = true;
                        break;
                    }
                }

                Assert("no_leftover_tree_nearby", !hasLeftover,
                    hasLeftover ? $"leftover tree at {leftoverTile}" : "clean");
            }

            // NPC 状态可读
            var state = Api.GetAgentState(NpcName);
            Assert("state_still_readable", !string.IsNullOrEmpty(state), $"state={state}");
        }

        return CurrentTick >= 150;
    }

    public override void Teardown()
    {
        // 清理可能残留的树
        var npc = Game1.getCharacterFromName(NpcName);
        var loc = npc?.currentLocation;
        if (loc != null && loc.terrainFeatures.ContainsKey(_treeTile))
        {
            _ = loc.terrainFeatures.Remove(_treeTile);
        }

        if (_executor != null)
        {
            _executor.OnSendActionResult = null;
        }

        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }
}