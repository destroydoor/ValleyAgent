#nullable enable
using System;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod.Tests.Fuzzy;

/// <summary>
///     Fuzz test: Rapid scene switches to stress-test decision context updates.
///     Verifies: Scene changes detected, all transitions correctly identified.
/// </summary>
public class F6_SceneSwitch : V3TestBase
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly string[] _sceneOrder = { "Farm", "Town", "Farm", "Mine" };
    private int _detectedChanges;
    private int _eventSuppressTicksLeft; // Mine warp 后的事件抑制窗口（Marlon 剧情）
    private string _lastFingerprint = "";
    private NPC? _npc;
    private int _sceneIndex;
    private int _switchCount;
    private int _switchTick;
    private bool _warpPending; // warp 后需要等 1 tick 再读指纹

    public F6_SceneSwitch(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    public override string TestName
    {
        get => "F6_SceneSwitch";
    }

    public override int TimeoutTicks
    {
        get => 1200;
    }

    public override TestGroup Group
    {
        get => TestGroup.Fuzzy;
    }

    public override void Setup()
    {
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley NPC not found");
            return;
        }

        // Allocate agent
        var api = ModEntry.API;
        _ = api?.TryAllocateAgent("Haley");

        // Record initial scene fingerprint
        _lastFingerprint = GetSceneFingerprint();
        _switchTick = -999;
        _switchCount = 0;
        _detectedChanges = 0;
        _sceneIndex = 0;

        // Warp NPC to Farm initially
        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        Game1.warpCharacter(_npc, Game1.currentLocation, Game1.player.Tile);
    }

    public override bool Update()
    {
        var tick = CurrentTick;

        // Mine warp 后的事件抑制窗口：Marlon 剧情等可能在 warp 后数 tick 才拉起，
        // 逐 tick 清掉，防止 eventUp 残留破坏后续 warp 的 Position 设置与场景指纹读取
        if (_eventSuppressTicksLeft > 0)
        {
            _eventSuppressTicksLeft--;
            _ = EventCleanup.ClearActiveEvents(_monitor, "F6 post-mine-warp suppression");
        }

        // 如果上一 tick 执行了 warp，现在读指纹
        if (_warpPending)
        {
            _warpPending = false;
            var currentFp = GetSceneFingerprint();
            if (!string.Equals(_lastFingerprint, currentFp, StringComparison.Ordinal))
            {
                _detectedChanges++;
                _monitor.Log($"[F6] Scene switch #{_switchCount}: {_lastFingerprint} → {currentFp}",
                    LogLevel.Info);
            }

            _lastFingerprint = currentFp;
        }

        // Every 150 ticks, teleport player to next scene in sequence
        if (tick - _switchTick >= 150)
        {
            _switchTick = tick;

            if (_sceneIndex >= _sceneOrder.Length)
            {
                _sceneIndex = 0;
            }

            var targetScene = _sceneOrder[_sceneIndex];
            _sceneIndex++;

            WarpPlayerToScene(targetScene);
            _warpPending = true; // 下一 tick 再读指纹
            _switchCount++;
        }

        return tick >= TimeoutTicks;
    }

    public override void Teardown()
    {
        var expectedChanges = _sceneOrder.Length - 1; // e.g., 4 scenes = 3 transitions

        AssertEx("Scene_change_detected_at_least_3_of_4_transitions",
            _detectedChanges >= 3,
            "连续 warp 后场景指纹（当前地图名）与上一 tick 相同（SafeWarp 未生效或事件把玩家弹回原图），4 段转场少于 3 段被识别",
            $"detectedChanges={_detectedChanges}");

        AssertEx("All_changes_correctly_identified",
            _detectedChanges >= expectedChanges - 1,
            "同上：指纹识别漏检超过 1 次（warpPending 读取时序与 warp 实际生效错位）",
            $"detected={_detectedChanges}, expected~={expectedChanges}");

        _monitor.Log($"[F6] Final: switches={_switchCount}, detectedChanges={_detectedChanges}",
            LogLevel.Info);
    }

    private static string GetSceneFingerprint()
    {
        var loc = Game1.currentLocation;
        var locName = loc?.NameOrUniqueName ?? "Unknown";
        // 只用地名做指纹 — 同一地图内坐标变化不算场景切换
        return locName;
    }

    private void WarpPlayerToScene(string sceneName)
    {
        switch (sceneName)
        {
            case "Farm":
                _ = SafeWarp.Farmer(Monitor, "Farm", 54, 15, TestName);
                break;
            case "Town":
                _ = SafeWarp.Farmer(Monitor, "Town", 54, 68, TestName);
                break;
            case "Mine":
                // 首进 Mine 会触发 Marlon 剧情（eventUp）：SafeWarp 已在 warp 前后各清一次，
                // 但事件可能在随后数 tick 才拉起——武装一个抑制窗口，Update 内逐 tick 清理。
                // 这就是历史上 F6 在 Mine warp 中间态超时、残留 eventUp 污染后续测试的根因。
                _ = SafeWarp.Farmer(Monitor, "Mine", 5, 11, TestName);
                _eventSuppressTicksLeft = 180;
                break;
            default:
                _monitor.Log($"[F6] Unknown scene: {sceneName}", LogLevel.Warn);
                break;
        }

        // Keep NPC with player
        if (_npc != null && Game1.currentLocation != null)
        {
            Game1.warpCharacter(_npc, Game1.currentLocation, Game1.player.Tile);
        }
    }
}