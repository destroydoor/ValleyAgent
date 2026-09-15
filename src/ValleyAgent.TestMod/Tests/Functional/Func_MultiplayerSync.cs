using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Multiplayer;

namespace ValleyAgent.TestMod.Tests.Functional;

/// <summary>
///     联机模块单元测试。
///     验证 MultiplayerHelper 逻辑、消息序列化、AgentSyncBroadcaster/AgentRemoteRenderer 数据流。
///     这些测试在单机模式下运行，不依赖实际联机连接。
/// </summary>
public class Func_MultiplayerSync : V3TestBase
{
    private int _phase;

    public Func_MultiplayerSync(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "Func_MultiplayerSync";
    }

    public override int TimeoutTicks
    {
        get => 600;
    }

    public override TestGroup Group
    {
        get => TestGroup.Functional;
    }

    public override void Setup() => _phase = 0;

    public override bool Update()
    {
        switch (_phase)
        {
            case 0:
                TestMultiplayerHelperLogic();
                _phase++;
                break;

            case 1:
                TestMessageSerialization();
                _phase++;
                break;

            case 2:
                TestAgentStateSnapshotMapping();
                _phase++;
                break;

            case 3:
                TestAgentRemoteRendererFlow();
                _phase++;
                break;

            case 4:
                TestFullSyncMessage();
                _phase++;
                break;

            case 5:
                TestPositionInterpolation();
                _phase++;
                break;

            default:
                return true;
        }

        return false;
    }

    public override void Teardown()
    {
    }

    /// <summary>
    ///     测试 MultiplayerHelper 的静态逻辑。
    ///     在单机模式下，ShouldRunAgentLogic 应为 true，IsFarmhand 应为 false。
    /// </summary>
    private void TestMultiplayerHelperLogic()
    {
        // 单机模式断言
        var isSinglePlayer = !StardewModdingAPI.Context.IsMultiplayer;
        AssertEx("SinglePlayer_ShouldRunAgentLogic",
            MultiplayerHelper.ShouldRunAgentLogic,
            "模式判定反转：单机（isSinglePlayer=true）时 ShouldRunAgentLogic 仍为 false——ShouldRunAgentLogic 的取反/联机分支写反",
            $"ShouldRunAgentLogic={MultiplayerHelper.ShouldRunAgentLogic}, IsMultiplayer={MultiplayerHelper.IsMultiplayer}");

        AssertEx("SinglePlayer_NotFarmhand",
            !MultiplayerHelper.IsFarmhand,
            "单机会话被误判为房客（IsFarmhand=true）——Game1.IsClient/IsMaster 判定分支写反",
            $"IsFarmhand={MultiplayerHelper.IsFarmhand}");

        AssertEx("SinglePlayer_NotSplitScreenFarmhand",
            !MultiplayerHelper.IsSplitScreenFarmhand,
            "单机无分屏却被判为分屏房客（Game1.player.IsSplitScreen 判定失效）",
            $"IsSplitScreenFarmhand={MultiplayerHelper.IsSplitScreenFarmhand}");

        AssertEx("SinglePlayer_NotRemoteFarmhand",
            !MultiplayerHelper.IsRemoteFarmhand,
            "单机被判为远程房客（IsRemoteFarmhand 的 active/远端实例判定失效）",
            $"IsRemoteFarmhand={MultiplayerHelper.IsRemoteFarmhand}");

        // ScreenId 在单机模式下应为 0
        AssertEx("SinglePlayer_ScreenIdIsZero",
            MultiplayerHelper.ScreenId == 0,
            "单机 ScreenId 非 0（Game1.game1.IsSplitScreen 或 Game1.GetCurrentScreenId 语义变化）",
            $"ScreenId={MultiplayerHelper.ScreenId}");

        // 逻辑一致性：IsFarmhand 意味着不应该运行 Agent 逻辑
        if (MultiplayerHelper.IsFarmhand)
        {
            Assert("Farmhand_CannotRunAgentLogic",
                !MultiplayerHelper.ShouldRunAgentLogic,
                "IsFarmhand=true but ShouldRunAgentLogic also true — logic error");
        }
    }

    /// <summary>
    ///     测试消息类型的序列化/反序列化。
    ///     SMAPI 的 SendMessage 内部使用 JSON 序列化，验证消息类可以被正确创建和读取。
    /// </summary>
    private void TestMessageSerialization()
    {
        // AgentStateMessage
        var stateMsg = new AgentStateMessage
        {
            Agents = new List<AgentStateSnapshot>
            {
                new()
                {
                    NpcName = "Haley",
                    State = "FARM",
                    Health = 80,
                    MaxHealth = 100,
                    Emotion = "Happy",
                    Location = "Farm",
                    PosX = 15.5f,
                    PosY = 20.3f,
                    IsDead = false,
                    FacingDirection = 1,
                    IsMoving = true
                },
                new()
                {
                    NpcName = "Abigail",
                    State = "FIGHT",
                    Health = 50,
                    MaxHealth = 100,
                    Emotion = "Angry",
                    Location = "Mine",
                    PosX = 30f,
                    PosY = 40f,
                    IsDead = false
                }
            }
        };

        // 原 AgentStateMessage_TwoAgents / AgentStateMessage_HaleyState 断言已删
        //（2026-09-14 死断言清理）：对象初始化器刚写入的字段立即读回断言，构造性恒真。

        Assert("AgentStateMessage_HaleyFacingMoving",
            stateMsg.Agents[0].FacingDirection == 1 && stateMsg.Agents[0].IsMoving,
            $"FacingDirection={stateMsg.Agents[0].FacingDirection}, IsMoving={stateMsg.Agents[0].IsMoving}");

        // DialogueRequestMessage
        var dlgReq = new DialogueRequestMessage
        {
            NpcName = "Haley",
            PlayerMessage = "你好！",
            PlayerId = 12345L,
            WorldSnapshotJson = "{\"season\":\"spring\"}"
        };
        // 原 DialogueRequestMessage_Fields 断言已删（2026-09-14 死断言清理）：
        // 初始化器回声，构造性恒真。
        Assert("DialogueRequestMessage_WorldSnapshot",
            dlgReq.WorldSnapshotJson == "{\"season\":\"spring\"}",
            $"WorldSnapshotJson={dlgReq.WorldSnapshotJson}");

        // DialogueResponseMessage
        var dlgResp = new DialogueResponseMessage
        {
            NpcName = "Haley",
            Text = "你好呀，今天天气真好！",
            Emotion = "Happy",
            Action = "FOLLOW",
            ActionsJson = "[{\"tool\":\"emote\"}]",
            TargetPlayerId = 12345L
        };
        // 原 DialogueResponseMessage_Fields 断言已删（2026-09-14 死断言清理）：
        // 初始化器回声，构造性恒真。
        Assert("DialogueResponseMessage_ActionsJson",
            dlgResp.ActionsJson == "[{\"tool\":\"emote\"}]",
            $"ActionsJson={dlgResp.ActionsJson}");

        // GiftRequestMessage
        var giftReq = new GiftRequestMessage
        {
            NpcName = "Haley",
            ItemId = "(O)16",
            Quantity = 1,
            PlayerId = 12345L
        };
        // 原 GiftRequestMessage_Fields 断言已删（2026-09-14 死断言清理）：
        // 初始化器回声，构造性恒真。

        // NpcActionMessage
        var actionMsg = new NpcActionMessage
        {
            NpcName = "Haley",
            ActionType = "emote",
            EmoteId = 24,
            Text = "太棒了！",
            DurationMs = 1000
        };
        // 原 NpcActionMessage_Fields 断言已删（2026-09-14 死断言清理）：
        // 初始化器回声，构造性恒真。

        // MessageTypes 常量（原为 Assert(true) 死断言，2026-09-14 清理改为真实相等断言）
        AssertEx("MessageTypes_AgentState",
            MessageTypes.AgentState == "AgentState",
            "MessageTypes.AgentState 常量被改名（≠'AgentState'）——主机/房客 SMAPI 消息按字符串路由，改名即互收不到",
            $"Expected 'AgentState', got '{MessageTypes.AgentState}'");
        AssertEx("MessageTypes_DialogueRequest",
            MessageTypes.DialogueRequest == "DialogueRequest",
            "MessageTypes.DialogueRequest 常量被改名（≠'DialogueRequest'）——对话请求消息路由将断裂",
            $"Expected 'DialogueRequest', got '{MessageTypes.DialogueRequest}'");
    }

    /// <summary>
    ///     测试 AgentStateSnapshot 的字段映射完整性。
    ///     原 Snapshot_AllFieldsSet / Snapshot_DeadAgent 初始化器回声断言已删（2026-09-14 死断言清理）；
    ///     快照真实字段映射由 TestAgentRemoteRendererFlow 的 HandleAgentStateMessage 链路覆盖。
    /// </summary>
    private void TestAgentStateSnapshotMapping()
    {
        // 快照构造本身不再断言字段回声（构造性恒真）。
    }

    /// <summary>
    ///     测试 AgentRemoteRenderer 的数据流：接收消息 → 缓存状态 → 查询。
    /// </summary>
    private void TestAgentRemoteRendererFlow()
    {
        var renderer = new AgentRemoteRenderer(Monitor);

        // 原 "Renderer_InitialStateEmpty" 断言已删（2026-09-14 死断言清理）：
        // 刚 new 出的实例其状态字典必为空（实例字段，无静态共享），构造性恒真。

        // 接收 AgentStateMessage
        var stateMsg = new AgentStateMessage
        {
            Agents = new List<AgentStateSnapshot>
            {
                new()
                {
                    NpcName = "Haley",
                    State = "FARM",
                    Health = 80,
                    MaxHealth = 100,
                    Emotion = "Happy",
                    Location = "Farm",
                    PosX = 15f,
                    PosY = 20f,
                    IsDead = false
                }
            }
        };
        renderer.HandleAgentStateMessage(stateMsg);

        // 查询缓存
        var haleyState = renderer.GetRemoteState("Haley");
        AssertEx("Renderer_HaleyCached",
            haleyState != null,
            "HandleAgentStateMessage 未缓存消息中的快照（解析或键写入分支断链），GetRemoteState('Haley') 返回 null",
            "Haley state should be cached after HandleAgentStateMessage");

        AssertEx("Renderer_HaleyStateCorrect",
            haleyState!.State == "FARM" && haleyState.Health == 80,
            "缓存写入时字段映射错位（State/Health 拷贝错或被后续覆写）",
            $"State={haleyState.State}, Health={haleyState.Health}");

        // 不存在的 NPC
        var samState = renderer.GetRemoteState("Sam");
        AssertEx("Renderer_UnknownNpcNull",
            samState == null,
            "GetRemoteState 对未缓存键返回了非 null 对象（字典 miss 分支被改为返回默认实例）",
            "Unknown NPC should return null");

        // 接收对话响应
        var dlgResp = new DialogueResponseMessage
        {
            NpcName = "Haley",
            Text = "你好！",
            Emotion = "Happy",
            TargetPlayerId = 1L
        };
        renderer.HandleDialogueResponse(dlgResp);

        var dequeued = renderer.DequeueDialogueResponse("Haley");
        AssertEx("Renderer_DialogueResponseDequeued",
            dequeued != null && dequeued.Text == "你好！",
            "HandleDialogueResponse 未入队或入队时 Text 丢失，DequeueDialogueResponse 取不到该回复",
            $"Dequeued response: {dequeued?.Text ?? "null"}");

        // 二次 Dequeue 应为 null
        var dequeued2 = renderer.DequeueDialogueResponse("Haley");
        AssertEx("Renderer_DialogueResponseConsumed",
            dequeued2 == null,
            "Dequeue 后消息未从 pending 队列移除（重复消费），二次 Dequeue 仍返回同一条",
            "Second dequeue should return null");

        // NPC 动作
        var actionMsg = new NpcActionMessage
        {
            NpcName = "Haley",
            ActionType = "emote",
            EmoteId = 16
        };
        renderer.HandleNpcAction(actionMsg);

        var dequeuedAction = renderer.DequeueAction();
        AssertEx("Renderer_ActionDequeued",
            dequeuedAction != null && dequeuedAction.ActionType == "emote",
            "HandleNpcAction 未入动作队列或 ActionType 映射丢失，DequeueAction 取不到动作",
            $"ActionType={dequeuedAction?.ActionType ?? "null"}");

        // Clear
        renderer.Clear();
        AssertEx("Renderer_ClearWorks",
            renderer.GetAllRemoteStates().Count == 0,
            "Clear 未清空状态字典（只清了部分集合），Clear 后仍有残留远程状态",
            $"After clear, expected 0 states, got {renderer.GetAllRemoteStates().Count}");
    }

    /// <summary>
    ///     测试 FullSyncMessage 的完整数据流。
    /// </summary>
    private void TestFullSyncMessage()
    {
        var renderer = new AgentRemoteRenderer(Monitor);

        // 先添加一些旧状态
        var oldMsg = new AgentStateMessage
        {
            Agents = new List<AgentStateSnapshot>
            {
                new() { NpcName = "OldNPC", State = "IDLE", Health = 50, MaxHealth = 100 }
            }
        };
        renderer.HandleAgentStateMessage(oldMsg);
        AssertEx("FullSync_OldStateExists",
            renderer.GetRemoteState("OldNPC") != null,
            "HandleAgentStateMessage 基线写入失效（全量同步前置状态不存在），OldNPC 未被缓存",
            "Old NPC should exist before full sync");

        // 发送 FullSync（会清除旧状态）
        var fullSync = new FullSyncMessage
        {
            HostPlayerId = 999L,
            Agents = new List<AgentFullState>
            {
                new()
                {
                    NpcName = "Haley",
                    State = "FARM",
                    Health = 80,
                    MaxHealth = 100,
                    Emotion = "Happy",
                    Location = "Farm",
                    Friendship = 500,
                    FarmerNickname = "小农",
                    RecentMemory = new List<string> { "玩家送了花", "一起收菜" }
                },
                new()
                {
                    NpcName = "Abigail",
                    State = "MINE",
                    Health = 60,
                    MaxHealth = 100,
                    Emotion = "Excited",
                    Location = "Mine",
                    Friendship = 1200,
                    FarmerNickname = "新来的农夫"
                }
            }
        };
        renderer.HandleFullSyncMessage(fullSync);

        // 旧状态应被清除
        AssertEx("FullSync_OldStateCleared",
            renderer.GetRemoteState("OldNPC") == null,
            "HandleFullSyncMessage 未执行'清旧再写新'（增量合并语义回归），OldNPC 残留",
            "Old NPC should be cleared after full sync");

        // 新状态应存在
        var haley = renderer.GetRemoteState("Haley");
        AssertEx("FullSync_HaleyExists",
            haley != null,
            "全量同步未写入 Agents 列表（列表遍历或键路由断链），Haley 不存在",
            "Haley should exist after full sync");

        AssertEx("FullSync_HaleyFriendship",
            haley!.Friendship == 500,
            "AgentFullState → 缓存映射丢失 Friendship 字段（赋值遗漏或被默认值覆盖）",
            $"Expected Friendship=500, got {haley.Friendship}");

        AssertEx("FullSync_HaleyNickname",
            haley.FarmerNickname == "小农",
            "FarmerNickname 字段映射丢失（per-player 昵称字段被改名/漏拷）",
            $"Expected FarmerNickname='小农', got '{haley.FarmerNickname}'");

        AssertEx("FullSync_HaleyMemory",
            haley.RecentMemory.Count == 2,
            "RecentMemory 列表映射丢失（深拷贝缺失或引用被清空）",
            $"Expected 2 memories, got {haley.RecentMemory.Count}");

        // Abigail 也应存在
        var abigail = renderer.GetRemoteState("Abigail");
        AssertEx("FullSync_AbigailExists",
            abigail != null && abigail.State == "MINE",
            "多 Agent 全量同步只写入首个 Agent（遍历中断/覆盖），Abigail 缺失或状态错",
            $"Abigail state: {abigail?.State ?? "null"}");
    }

    /// <summary>
    ///     测试 AgentRemoteRenderer 的位置插值引擎。
    ///     单机模式下直接调用 internal UpdatePositionInterpolation（绕过 IsFarmhand 守卫）。
    ///     场景 A：3 格步进 — 多 tick 插值后距离应持续减小
    ///     场景 B：10 格吸附 — 距离 > 8 tiles，单次 update 即吸附到目标像素位置
    ///     场景 C：跨图跳过 — 目标图 != 当前图，NPC 位置/图不变
    /// </summary>
    private void TestPositionInterpolation()
    {
        var renderer = new AgentRemoteRenderer(Monitor);

        // 前置条件：需要可用 NPC 和已加载的当前图
        var npc = Game1.getCharacterFromName("Abigail") ?? Game1.getCharacterFromName("Haley");
        var currentLoc = Game1.currentLocation;
        if (npc == null || currentLoc == null)
        {
            Assert("PositionInterpolation_Preconditions", false,
                $"Preconditions not met: npc={(npc == null ? "null" : npc.Name)}, currentLoc={(currentLoc == null ? "null" : currentLoc.NameOrUniqueName)}");
            return;
        }

        AssertEx("PositionInterpolation_Preconditions", true,
            "NPC 或当前图在守卫检查后被销毁（E5 死亡重生路径的防御记录）",
            $"npc={npc.Name}, loc={currentLoc.NameOrUniqueName}, speed={npc.Speed}");

        // ───── 场景 A：3 格步进 ─────
        try
        {
            renderer.Clear();
            EnsureNpcAtCurrentLocation(npc, currentLoc);
            npc.controller = null;
            npc.Speed = 2;

            var P0 = npc.Position;

            // 找一个 3 格外的可行走目标 tile
            var targetTile = TestScenes.FindWalkableTileNear(currentLoc,
                npc.Tile + new Vector2(3, 0), npc.Tile);
            var targetPos = targetTile * 64f;
            var dist0 = Vector2.Distance(P0, targetPos);

            var msg = new AgentStateMessage
            {
                Agents = new List<AgentStateSnapshot>
                {
                    new()
                    {
                        NpcName = npc.Name,
                        State = "IDLE",
                        Health = 100, MaxHealth = 100,
                        Location = currentLoc.NameOrUniqueName,
                        PosX = targetPos.X, PosY = targetPos.Y,
                        FacingDirection = 1,
                        IsMoving = true,
                        IsDead = false
                    }
                }
            };
            renderer.HandleAgentStateMessage(msg);

            // 驱动 60 次 update
            for (var i = 0; i < 60; i++)
            {
                renderer.UpdatePositionInterpolation();
            }

            var dist1 = Vector2.Distance(npc.Position, targetPos);
            Assert("PositionInterpolation_ScenarioA_MovedCloser",
                dist1 < dist0,
                $"P0={P0}, target={targetPos}, dist0={dist0:F1}, dist1={dist1:F1}, npc.Pos={npc.Position}");
        }
        catch (Exception ex)
        {
            Assert("PositionInterpolation_ScenarioA_Exception", false, ex.ToString());
        }

        // ───── 场景 B：10 格吸附 ─────
        try
        {
            renderer.Clear();
            EnsureNpcAtCurrentLocation(npc, currentLoc);
            npc.controller = null;
            npc.Speed = 2;

            var P0 = npc.Position;

            // 10 格外的目标 tile（无需可行走，因为直接吸附）
            var targetTile = npc.Tile + new Vector2(10, 0);
            var targetPos = targetTile * 64f;
            var dist0 = Vector2.Distance(P0, targetPos);

            Assert("PositionInterpolation_ScenarioB_DistanceExceedsSnapThreshold",
                dist0 > 8 * 64f,
                $"dist0={dist0:F1} should be > {8 * 64f:F1} (8 tiles) to trigger snap");

            var msg = new AgentStateMessage
            {
                Agents = new List<AgentStateSnapshot>
                {
                    new()
                    {
                        NpcName = npc.Name,
                        State = "IDLE",
                        Health = 100, MaxHealth = 100,
                        Location = currentLoc.NameOrUniqueName,
                        PosX = targetPos.X, PosY = targetPos.Y,
                        FacingDirection = 1,
                        IsMoving = true,
                        IsDead = false
                    }
                }
            };
            renderer.HandleAgentStateMessage(msg);

            // 驱动 1 次 update — 应触发吸附
            renderer.UpdatePositionInterpolation();

            var dist1 = Vector2.Distance(npc.Position, targetPos);
            Assert("PositionInterpolation_ScenarioB_SnapedToTarget",
                dist1 < 1f,
                $"After 1 update with dist>8tiles, NPC should snap. dist1={dist1:F3}, npc.Pos={npc.Position}, target={targetPos}");
        }
        catch (Exception ex)
        {
            Assert("PositionInterpolation_ScenarioB_Exception", false, ex.ToString());
        }

        // ───── 场景 C：跨图跳过 ─────
        try
        {
            renderer.Clear();
            EnsureNpcAtCurrentLocation(npc, currentLoc);
            npc.controller = null;
            npc.Speed = 2;

            var P0 = npc.Position;
            var locBefore = npc.currentLocation;

            // 构造一个跨图消息：目标 Location 是一个保证不等于当前图的名字
            var fakeTargetLocation = "ZZZ_NonExistent_TestLocation_ForScenarioC";
            var msg = new AgentStateMessage
            {
                Agents = new List<AgentStateSnapshot>
                {
                    new()
                    {
                        NpcName = npc.Name,
                        State = "IDLE",
                        Health = 100, MaxHealth = 100,
                        Location = fakeTargetLocation,
                        PosX = 9999f, PosY = 9999f, // 不应被应用到 NPC
                        FacingDirection = 1,
                        IsMoving = true,
                        IsDead = false
                    }
                }
            };
            renderer.HandleAgentStateMessage(msg);

            renderer.UpdatePositionInterpolation();

            var locAfter = npc.currentLocation;
            var posAfter = npc.Position;

            Assert("PositionInterpolation_ScenarioC_LocationUnchanged",
                locAfter == locBefore,
                $"locBefore={locBefore?.NameOrUniqueName}, locAfter={locAfter?.NameOrUniqueName}");
            Assert("PositionInterpolation_ScenarioC_PositionUnchanged",
                posAfter == P0,
                $"P0={P0}, posAfter={posAfter} (cross-map should skip interpolation)");
        }
        catch (Exception ex)
        {
            Assert("PositionInterpolation_ScenarioC_Exception", false, ex.ToString());
        }
    }

    /// <summary>
    ///     确保指定 NPC 在当前激活图内；若不在则 warp 到玩家附近的可行走 tile。
    /// </summary>
    private static void EnsureNpcAtCurrentLocation(NPC npc, GameLocation currentLoc)
    {
        if (npc.currentLocation != currentLoc)
        {
            var tile = TestScenes.FindWalkableTileNear(currentLoc, Game1.player.Tile, Game1.player.Tile);
            Game1.warpCharacter(npc, currentLoc, tile);
        }
        else
        {
            // 已在当前图：再校正到玩家附近可行走 tile，避免出生在墙里
            var tile = TestScenes.FindWalkableTileNear(currentLoc, Game1.player.Tile, Game1.player.Tile);
            npc.setTileLocation(tile);
        }

        npc.Halt();
    }
}