#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     NPC 农场动作可见。在 Farm 上生成 5 个成熟作物，FARM 决策，
///     录像验证 NPC 收菜动作可见，最终作物应被收获。
/// </summary>
[RegisteredTest(TestGroup.Experience, "NPC 农场动作可见", "experience")]
public class EXP005_NpcFarmActionVisible : V3TestBase
{
    private IValleyAgentApi? _api;
    private IReadOnlyList<Vector2> _cropTiles = Array.Empty<Vector2>();
    private int _initialCropCount;
    private NPC? _npc;

    public EXP005_NpcFarmActionVisible(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP005_NpcFarmActionVisible";
    }

    public override int TimeoutTicks
    {
        get => 8000;
    }

    public override TestGroup Group
    {
        get => TestGroup.Experience;
    }

    public override void Setup()
    {
        DebugFlags.SuppressDecisions = true;

        // 同 PIPE005：ModEntry.API 是 GameLaunched 时的 fallback（子 API 为 null），走惰性完整实例。
        _api = ValleyAgent.ModEntry.Instance?.API;
        if (_api == null)
        {
            Skip("API not available");
            return;
        }

        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _ = _api.TryAllocateAgent("Haley");
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(54, 16));
        _npc.Halt();
        _npc.controller = null;

        // 在玩家附近生成 5 个成熟作物
        _cropTiles = TestScenes.SpawnFarmScene(Helper, Monitor, 5);
        _initialCropCount = _cropTiles.Count;
        Monitor.Log($"[EXP005] Spawned {_initialCropCount} crops near NPC.", LogLevel.Info);

        // 设置 FARM 决策
        _ = _api.TrySetAgentState("Haley", "FARM");

        // 截图：场景初始状态
        CaptureScreenshot("farm_scene_setup");
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 120: 开始录像
        if (CurrentTick == 120)
        {
            Screenshot?.StartRecording(TestName, "farm_action", 5, 600);
            Monitor.Log("[EXP005] Recording started.", LogLevel.Info);
        }

        // 每 60 tick 截图（用于视觉分析）
        if (CurrentTick > 120 && CurrentTick < 6000 && CurrentTick % 60 == 0)
        {
            CaptureScreenshot($"farm_tick_{CurrentTick}");
        }

        // tick 6000: 停止录像 + 断言作物已收获
        if (CurrentTick == 6000)
        {
            var recordingPath = Screenshot?.StopRecording();
            Monitor.Log($"[EXP005] Recording stopped. path={recordingPath ?? "(null)"}", LogLevel.Info);

            var remaining = CountRemainingCrops();
            Assert("crops_harvested", remaining == 0,
                $"initial={_initialCropCount} remaining={remaining}");

            AssertVisual("farm_action_visible", recordingPath ?? "",
                "从玩家视角观察：NPC 的收菜动作是否自然可见？描述任何可能影响观感的问题（如动作僵硬、反馈不明显、动作缺失等）");
            AssertVisual("farm_closing_ritual", recordingPath ?? "",
                "从玩家视角观察：收菜结束后的收尾动作（如归位/待机）是否自然可见？描述任何可能影响收尾体验的问题（如动作突兀、无过渡、突然消失等）");
        }

        return CurrentTick >= 6120;
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP005] Teardown.", LogLevel.Info);
    }

    private int CountRemainingCrops()
    {
        var loc = Game1.player.currentLocation;
        if (loc == null)
        {
            return _initialCropCount;
        }

        var remaining = 0;
        foreach (var tile in _cropTiles)
        {
            if (!loc.terrainFeatures.TryGetValue(tile, out var tf))
            {
                continue;
            }

            if (tf is HoeDirt dirt && dirt.crop != null && !dirt.crop.dead.Value)
            {
                remaining++;
            }
        }

        return remaining;
    }
}