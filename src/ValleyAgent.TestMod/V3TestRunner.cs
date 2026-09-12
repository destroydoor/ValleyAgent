#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Tests.Edge;
using ValleyAgent.TestMod.Tests.Focused;
using ValleyAgent.TestMod.Tests.Functional;
using ValleyAgent.TestMod.Tests.Fuzzy;
using ValleyAgent.TestMod.Tests.Integration;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod;

public class V3TestRunner
{
    private const int MaxTotalTicks = 240000;
    private const int MaxConsecutiveFailures = 5;
    private const int MaxTicksWithoutAdvance = 27000;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly IModHelper _helper;
    private readonly bool _mockMode;
    private readonly IMonitor _monitor;

    // 运行时间戳，用于创建子目录和 summary 文件
    private readonly string _runTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

    // 汇总数据：每个测试的 pass/fail 计数
    private readonly List<SummaryEntry> _summaryEntries = new();
    private readonly List<V3TestBase> _tests = new();
    private V3TestBase? _activeTest;
    private int _activeTick;
    private int _consecutiveSetupFailures;
    private int _consecutiveTestFailures;
    private bool _done;
    private int _farmWarpAttempts;
    private int _lastAdvanceTotalTick;
    private int _npcWaitTicks;
    private int _startDelayTicks = 120; // ~2s
    private bool _started;
    private int _testIndex;

    private int _totalTicksStarted;

    public V3TestRunner(IModHelper helper, IMonitor monitor, bool mockMode = false)
    {
        _helper = helper;
        _monitor = monitor;
        _mockMode = mockMode;
    }

    /// <summary>True when the runner has started and not yet finished all tests.</summary>
    public bool IsRunning
    {
        get => _started && !_done;
    }

    // 聊天框输出
    private static void ChatMsg(string msg, Color? color = null)
    {
        try
        {
            Game1.chatBox?.addMessage(msg, color ?? Color.White);
        }
        catch (InvalidOperationException)
        {
        }
        catch (NullReferenceException)
        {
        }
        catch (ArgumentException)
        {
        }
    }

    // 静音：每 tick 强制设置，防止游戏覆盖
    private static void MuteAudio()
    {
        try
        {
            Game1.options.musicVolumeLevel = 0f;
            Game1.options.soundVolumeLevel = 0f;
            Game1.options.ambientVolumeLevel = 0f;
            Game1.options.footstepVolumeLevel = 0f;
        }
        catch (InvalidOperationException)
        {
        }
        catch (NullReferenceException)
        {
        }
    }

    // 窗口非激活时强制继续运行（不暂停）
    private static void ForceUnpause()
    {
        try
        {
            if (Game1.paused)
            {
                Game1.paused = false;
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (NullReferenceException)
        {
        }
    }

    /// <summary>Call once per game tick from OnUpdateTicked.</summary>
    private void WriteWatchdogMarker(string reason, string detail)
    {
        try
        {
            var dir = Path.Combine(V3TestBase.FindProjectRoot(_helper), "logs", "test_results");
            _ = Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"_WATCHDOG_ABORT_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path,
                $"Watchdog Abort\n" +
                $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"Reason: {reason}\n" +
                $"Detail: {detail}\n" +
                $"TotalTicksStarted: {_totalTicksStarted}\n" +
                $"ActiveTest: {_activeTest?.TestName ?? "(null)"}\n" +
                $"TestIndex: {_testIndex}/{_tests.Count}\n" +
                $"ConsecutiveFailures: {_consecutiveTestFailures}\n");
        }
        catch (IOException)
        {
            /* non-critical */
        }
    }

    /// <summary>Call once per game tick from OnUpdateTicked.</summary>
    public void Update()
    {
        if (_done)
        {
            return;
        }

        // 窗口非激活时强制继续运行
        ForceUnpause();

        // 静音（每 tick 强制设置）
        MuteAudio();

        // ── 全局看门狗（只监控 _started 后的 tick） ──
        if (_started)
        {
            _totalTicksStarted++;

            // 看门狗 1：总 tick 超出绝对上限 → 强制中止全部测试
            if (_totalTicksStarted > MaxTotalTicks)
            {
                _monitor.Log($"[V3TestRunner] 看门狗触发：超过 {MaxTotalTicks} 总 tick（当前 {_totalTicksStarted}）。强制中止全部测试。",
                    LogLevel.Error);
                WriteWatchdogMarker("TotalTickLimit",
                    $"TotalTicks={_totalTicksStarted}, LastTest={_activeTest?.TestName}, Index={_testIndex}/{_tests.Count}");
                ChatMsg("⚠ 看门狗：超时上限，强制退出", Color.Red);
                _done = true;
                Game1.quit = true;
                return;
            }

            // 看门狗 2：长时间无测试完成（推进停滞）→ 强制中止。
            // 2026-08-20 Phase 5：阈值动态化——设计时长超过 27000 tick 的测试
            // （如 EXP012 完整流程 29000 tick）不被看门狗提前杀，异常卡死仍被抓。
            var stallTicks = _totalTicksStarted - _lastAdvanceTotalTick;
            var stallLimit = Math.Max(MaxTicksWithoutAdvance, (_activeTest?.TimeoutTicks ?? 0) + 2000);
            if (stallTicks > stallLimit)
            {
                _monitor.Log($"[V3TestRunner] 看门狗触发：{stallTicks} tick 无测试完成（limit={stallLimit}）。检测到卡死，强制中止。", LogLevel.Error);
                WriteWatchdogMarker("StallDetected",
                    $"StallTicks={stallTicks}, ActiveTest={_activeTest?.TestName}, Index={_testIndex}/{_tests.Count}");
                ChatMsg("⚠ 看门狗：卡死检测，强制退出", Color.Red);
                _done = true;
                Game1.quit = true;
                return;
            }
        }

        if (!_started)
        {
            // Wait for game init before starting tests
            if (_startDelayTicks > 0)
            {
                _startDelayTicks--;
                return;
            }

            // Warp player to Farm if indoors (NPCs are not fully loaded until player is outdoors)
            if (Game1.currentLocation == null || !Game1.currentLocation.IsOutdoors)
            {
                _farmWarpAttempts++;
                if (_farmWarpAttempts == 1)
                {
                    _monitor.Log("[V3TestRunner] Warping player to Farm...", LogLevel.Info);
                    // (30,30) 是农田硬隔离区（NPCBarrier+debris），SafeWarp 会修正到入口可达瓦片
                    _ = SafeWarp.Farmer(_monitor, "Farm", 30, 30, "V3TestRunner-initial");
                    _startDelayTicks = 60; // ~1s
                }
                else if (_farmWarpAttempts <= 5)
                {
                    _monitor.Log($"[V3TestRunner] Retry warp to Farm (attempt {_farmWarpAttempts})...", LogLevel.Info);
                    _ = SafeWarp.Farmer(_monitor, "Farm", 30, 30, "V3TestRunner-initial");
                    _startDelayTicks = 60;
                }
                else if (_farmWarpAttempts <= 8)
                {
                    _monitor.Log($"[V3TestRunner] Force teleport to Farm (attempt {_farmWarpAttempts})...",
                        LogLevel.Info);
                    var farm = Game1.getLocationFromName("Farm");
                    if (farm != null)
                    {
                        var tile = WarpTargetGuard.ResolveLandingTile(_monitor, "Farm", 30, 30,
                            "V3TestRunner-initial-force");
                        Game1.player.currentLocation = farm;
                        Game1.player.Position = new Vector2(tile.X * 64, tile.Y * 64);
                    }

                    _startDelayTicks = 60;
                }
                else if (_farmWarpAttempts <= 10)
                {
                    _monitor.Log($"[V3TestRunner] Raw warp to Farm (attempt {_farmWarpAttempts})...", LogLevel.Info);
                    var farm = Game1.getLocationFromName("Farm");
                    if (farm != null)
                    {
                        var tile = WarpTargetGuard.ResolveLandingTile(_monitor, "Farm", 30, 30,
                            "V3TestRunner-initial-raw");
                        Game1.currentLocation = farm;
                        Game1.player.currentLocation = farm;
                        Game1.player.Position = new Vector2(tile.X * 64, tile.Y * 64);
                    }

                    _startDelayTicks = 60;
                }
                else
                {
                    _monitor.Log("[V3TestRunner] Failed to warp to Farm after 10 attempts. Skipping all tests.",
                        LogLevel.Error);
                    _done = true;
                }

                return;
            }

            // Actively wait for Haley NPC to exist in the game world
            var haley = Game1.getCharacterFromName("Haley");
            if (haley == null)
            {
                _npcWaitTicks++;
                if (_npcWaitTicks > 300) // ~5s max wait
                {
                    _monitor.Log("[V3TestRunner] Haley not found after 5s — marking skip.", LogLevel.Error);
                    ChatMsg("❌ Haley 未找到，跳过测试", Color.Red);
                    _done = true;
                }

                return; // retry next tick
            }

            // Warp Haley to player location
            try
            {
                if (haley.currentLocation != Game1.currentLocation)
                {
                    var tile = TestScenes.FindWalkableTileNear(Game1.currentLocation, Game1.player.Tile,
                        Game1.player.Tile);
                    Game1.warpCharacter(haley, Game1.currentLocation!, tile);
                }

                var api = ModEntry.API;
                if (api != null)
                {
                    _ = api.TryAllocateAgent("Haley");
                    _ = api.TryRevive("Haley");
                }
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"[V3TestRunner] NPC setup error: {ex.Message}", LogLevel.Warn);
            }

            _started = true;
            _lastAdvanceTotalTick = 0; // 初始化推进看门狗
            _totalTicksStarted = 0;

            // Mock mode: LLM 由 TS Agent Server 驱动，MockLLMProvider 已移除
            if (_mockMode)
            {
                var api = ModEntry.API;
                if (api != null)
                {
                    _monitor.Log("[V3TestRunner] Mock mode active — LLM controlled by TS Agent Server.", LogLevel.Info);
                }
            }

            InitializeTests();
            if (_tests.Count == 0)
            {
                _monitor.Log("[V3TestRunner] No tests to run for active group.", LogLevel.Info);
                _done = true;
                return;
            }

            // 为所有测试设置运行时间戳（用于子目录）
            foreach (var t in _tests)
            {
                t.RunTimestamp = _runTimestamp;
            }

            _activeTest = _tests[0];
            _testIndex = 0;
            _monitor.Log($"[V3TestRunner] Starting {_tests.Count} tests for group {TestFilter.ActiveGroup}.",
                LogLevel.Info);
            ChatMsg($"▶ 开始测试 {_tests.Count} 项 ({TestFilter.ActiveGroup})", Color.Cyan);
        }

        if (_testIndex >= _tests.Count)
        {
            _monitor.Log("[V3TestRunner] All tests complete. Exiting.", LogLevel.Info);
            ChatMsg("✅ 全部测试完成，即将退出", Color.Green);

            // 写 _summary.json
            WriteSummaryFile();

            // Write completion marker for external auto-notification
            try
            {
                var markerDir = Path.Combine(V3TestBase.FindProjectRoot(_helper), "logs", "test_results",
                    _runTimestamp);
                _ = Directory.CreateDirectory(markerDir);
                var markerPath = Path.Combine(markerDir, "_TEST_COMPLETE.txt");
                File.WriteAllText(markerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                _monitor.Log($"[V3TestRunner] Completion marker written: {markerPath}", LogLevel.Info);
            }
            catch (IOException)
            {
                /* non-critical */
            }

            _done = true;
            Game1.quit = true;
            return;
        }

        _activeTest!.CurrentTick = _activeTick;
        _activeTick++;

        // First tick of this test → Setup
        if (_activeTick == 1)
        {
            _monitor.Log($"[V3TestRunner] Starting test {_testIndex + 1}/{_tests.Count}: {_activeTest.TestName}",
                LogLevel.Info);
            ChatMsg($"[{_testIndex + 1}/{_tests.Count}] ▶ {_activeTest.TestName}", Color.Yellow);
            _activeTest.Setup();

            if (_activeTest.WasSkipped)
            {
                _consecutiveSetupFailures++;
                _monitor.Log(
                    $"[V3TestRunner] '{_activeTest.TestName}' skipped ({_consecutiveSetupFailures} consecutive).",
                    LogLevel.Warn);
                ChatMsg($"  ⏭ 跳过: {_activeTest.TestName}", Color.Orange);

                if (_consecutiveSetupFailures >= 3)
                {
                    _monitor.Log(
                        $"[V3TestRunner] {_consecutiveSetupFailures} consecutive setup failures — skipping remaining {_tests.Count - _testIndex - 1} tests.",
                        LogLevel.Warn);
                    _testIndex = _tests.Count; // Flush remaining
                }
            }
            else
            {
                _consecutiveSetupFailures = 0;
            }

            return;
        }

        // Timeout check（2026-08-20 Phase 5：用 CurrentTick 而非递增后的 _activeTick——
        // 原逻辑在 CurrentTick==TimeoutTicks 时 _activeTick 已 +1，提前一拍触发超时，
        // 设计为"跑满时长在边界 tick 完成"的测试（F1-F6）被误杀）
        if (_activeTest.CurrentTick > _activeTest.TimeoutTicks)
        {
            // 2026-08-20 测试系统大改 Phase 1：超时默认 = 失败（防乐观判定）。
            // 设计为"跑满时长再断言"的测试需在 test_config.json timeoutExemptions 白名单登记。
            _activeTest.TimedOut = true;
            if (TestConfig.TimeoutAsFailure && !TestConfig.IsTimeoutExempt(_activeTest.TestName))
            {
                _activeTest.RecordRunnerAssertion("timeout_as_failure",
                    false,
                    $"test timed out after {_activeTick} ticks without timeoutExemptions entry");
            }

            _monitor.Log($"[V3TestRunner] Test '{_activeTest.TestName}' timed out after {_activeTick} ticks." +
                         (TestConfig.IsTimeoutExempt(_activeTest.TestName) ? " (exempted)" : ""),
                LogLevel.Warn);
            ChatMsg($"  ⏱ 超时: {_activeTest.TestName}", Color.Orange);
            _activeTest.Teardown();
            try
            {
                (_activeTest as dynamic)?.SaveResults();
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"[V3TestRunner] SaveResults failed (non-fatal): {ex.Message}", LogLevel.Warn);
            }

            AddSummaryEntry(_activeTest);
            AdvanceTest();
            return;
        }

        // Update test
        try
        {
            if (_activeTest.Update())
            {
                // Test completed
                _consecutiveTestFailures = 0;
                _activeTest.Teardown();
                try
                {
                    (_activeTest as dynamic)?.SaveResults();
                }
                catch (InvalidOperationException ex)
                {
                    _monitor.Log($"[V3TestRunner] SaveResults failed (non-fatal): {ex.Message}", LogLevel.Warn);
                }

                AddSummaryEntry(_activeTest);
                // 输出测试结果摘要
                var passed = _activeTest.TestResults.Count(r => r.Passed);
                var total = _activeTest.TestResults.Count;
                var summary = total > 0 ? $"{passed}/{total}" : "done";
                var color = total > 0 && passed == total ? Color.LightGreen : Color.OrangeRed;
                ChatMsg($"  ✔ {_activeTest.TestName}: {summary}", color);
                AdvanceTest();
            }
        }
        catch (InvalidOperationException ex)
        {
            HandleTestException(ex);
        }
        catch (NullReferenceException ex)
        {
            HandleTestException(ex);
        }
        catch (ArgumentException ex)
        {
            HandleTestException(ex);
        }
        catch (IndexOutOfRangeException ex)
        {
            HandleTestException(ex);
        }
        catch (KeyNotFoundException ex)
        {
            HandleTestException(ex);
        }
    }

    private void HandleTestException(Exception ex)
    {
        if (_activeTest == null)
        {
            return;
        }

        _consecutiveTestFailures++;
        _monitor.Log(
            $"[V3TestRunner] Test '{_activeTest.TestName}' threw: {ex.Message} ({_consecutiveTestFailures} consecutive failures)",
            LogLevel.Error);
        ChatMsg($"  ❌ 异常: {_activeTest.TestName}: {ex.Message}", Color.Red);
        _activeTest.RecordException(ex);
        _activeTest.Teardown();
        try
        {
            _activeTest.SaveResults();
        }
        catch (InvalidOperationException)
        {
        }

        AddSummaryEntry(_activeTest);

        if (_consecutiveTestFailures >= MaxConsecutiveFailures)
        {
            _monitor.Log(
                $"[V3TestRunner] {_consecutiveTestFailures} consecutive failures (catch) — 基础设施故障，跳过剩余 {_tests.Count - _testIndex - 1} 个测试。",
                LogLevel.Error);
            WriteWatchdogMarker("ConsecutiveFailures",
                $"Failures={_consecutiveTestFailures} (catch), LastTest={_activeTest?.TestName}, Index={_testIndex}/{_tests.Count}");
            _testIndex = _tests.Count;
            return;
        }

        AdvanceTest();
    }

    private void AdvanceTest()
    {
        _lastAdvanceTotalTick = _totalTicksStarted; // 记录推进时间戳（喂狗）

        // 保底：重置 SuppressDecisions，防止上一轮测试 Teardown 异常导致泄漏
        DebugFlags.SuppressDecisions = false;

        // Reset game state to baseline for next test
        // This prevents NPC loss, scripted event triggers, and state decay between tests
        try
        {
            // 清理残留事件（F6 在 Mine warp 中间态超时时 Game1.eventUp 可能残留 true）：
            // SDV warp 完成逻辑（Game1.cs:6239 if(!eventUp)）会跳过玩家 Position 设置，
            // 玩家 Tile 卡在旧值 → 后续测试的 dist/位置断言全部失败。必须清真实事件状态。
            // 统一走 EventCleanup（只置空字段，不碰 onEventFinished 的 NRE 坑）。
            _ = EventCleanup.ClearActiveEvents(_monitor, "V3TestRunner between-tests");

            // 落点经 WarpTargetGuard 验证（BFS 入口可达），不可达自动修正
            var farmTile = SafeWarp.Farmer(_monitor, "Farm", 54, 30, "V3TestRunner between-tests");

            // 玩家强制就位：warpFarmer 是异步队列，SafeWarp 已同步设置一次，
            // 这里用守卫修正后的落点再兜底一次，保证下一个测试 Setup 开始时玩家 Tile 已就位。
            var farm = Game1.getLocationFromName("Farm");
            if (farm != null)
            {
                Game1.player.currentLocation = farm;
                Game1.player.Position = new Vector2(farmTile.X, farmTile.Y) * 64f;
            }

            TestScenes.ClearAll(_helper, _monitor);

            // Re-allocate, revive, and re-warp NPC for next test
            var api = ModEntry.API;
            if (api != null)
            {
                _ = api.TryAllocateAgent("Haley");
                _ = api.TryRevive("Haley");
            }

            var haley = Game1.getCharacterFromName("Haley");
            if (haley != null && haley.currentLocation != Game1.currentLocation)
            {
                Game1.warpCharacter(haley, Game1.currentLocation!,
                    TestScenes.FindWalkableTileNear(Game1.currentLocation, Game1.player.Tile, Game1.player.Tile));
            }
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[V3TestRunner] Cleanup between tests failed: {ex.Message}", LogLevel.Warn);
        }
        catch (NullReferenceException ex)
        {
            _monitor.Log($"[V3TestRunner] Cleanup between tests failed: {ex.Message}", LogLevel.Warn);
        }
        catch (ArgumentException ex)
        {
            _monitor.Log($"[V3TestRunner] Cleanup between tests failed: {ex.Message}", LogLevel.Warn);
        }

        _testIndex++;
        _activeTick = 0;
        _activeTest = _testIndex < _tests.Count ? _tests[_testIndex] : null;
    }

    /// <summary>将单个测试的 pass/fail 记入汇总。</summary>
    private void AddSummaryEntry(V3TestBase test)
    {
        var passed = test.TestResults.Count(r => r.Passed);
        var failed = test.TestResults.Count(r => !r.Passed);
        string status;
        if (test.WasSkipped)
        {
            status = "SKIP";
        }
        else if (failed > 0)
        {
            status = "FAIL";
        }
        else
        {
            status = passed == 0 ? "SKIP" : "PASS";
        }

        _summaryEntries.Add(new SummaryEntry
        {
            Name = test.TestName,
            Group = test.Group.ToString(),
            Passed = passed,
            Failed = failed,
            Status = status,
            TimedOut = test.TimedOut
        });
    }

    /// <summary>所有测试结束后写 _summary.json 到运行子目录。</summary>
    private void WriteSummaryFile()
    {
        try
        {
            var dir = Path.Combine(V3TestBase.FindProjectRoot(_helper), "logs", "test_results", _runTimestamp);
            _ = Directory.CreateDirectory(dir);

            int totalPassed = 0, totalFailed = 0, totalSkipped = 0;
            foreach (var e in _summaryEntries)
            {
                totalPassed += e.Passed;
                totalFailed += e.Failed;
                if (e.Status == "SKIP")
                {
                    totalSkipped++;
                }
            }

            var summary = new
            {
                run_timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                total_tests = _summaryEntries.Count,
                total_passed = totalPassed,
                total_failed = totalFailed,
                total_skipped = totalSkipped,
                tests = _summaryEntries
            };

            var json = JsonSerializer.Serialize(summary, s_jsonOptions);
            File.WriteAllText(Path.Combine(dir, "_summary.json"), json);
            _monitor.Log($"[V3TestRunner] Summary written: {Path.Combine(dir, "_summary.json")}", LogLevel.Info);
        }
        catch (IOException ex)
        {
            _monitor.Log($"[V3TestRunner] WriteSummaryFile failed (non-critical): {ex.Message}", LogLevel.Warn);
        }
        catch (UnauthorizedAccessException ex)
        {
            _monitor.Log($"[V3TestRunner] WriteSummaryFile failed (non-critical): {ex.Message}", LogLevel.Warn);
        }
        catch (ArgumentException ex)
        {
            _monitor.Log($"[V3TestRunner] WriteSummaryFile failed (non-critical): {ex.Message}", LogLevel.Warn);
        }
        catch (JsonException ex)
        {
            _monitor.Log($"[V3TestRunner] WriteSummaryFile failed (non-critical): {ex.Message}", LogLevel.Warn);
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[V3TestRunner] WriteSummaryFile failed (non-critical): {ex.Message}", LogLevel.Warn);
        }
    }

    private void InitializeTests()
    {
        var g = TestFilter.ActiveGroup;

        // Fuzzy tests (includes focused cross-map test)
        if ((g & TestGroup.Fuzzy) != 0)
        {
            TryAdd(new F1_RandomWalk(_helper, _monitor));
            TryAdd(new F2_RandomMonsters(_helper, _monitor));
            TryAdd(new F3_RandomDrops(_helper, _monitor));
            TryAdd(new F4_RandomDecisions(_helper, _monitor));
            TryAdd(new F5_DualAgent(_helper, _monitor));
            TryAdd(new F6_SceneSwitch(_helper, _monitor));
            TryAdd(new F_FollowCrossMap(_helper, _monitor));
            TryAdd(new F_MineRealCombat(_helper, _monitor));
            TryAdd(new F7_ScanConsistencyStress(_helper, _monitor));
            TryAdd(new F8_StateDurationBypass(_helper, _monitor));
        }

        // Edge tests
        if ((g & TestGroup.Edge) != 0)
        {
            TryAdd(new E1_LongPathfind(_helper, _monitor));
            TryAdd(new E2_DoorLoop(_helper, _monitor));
            TryAdd(new E3_Unreachable(_helper, _monitor));
            TryAdd(new E4_FullInventoryFight(_helper, _monitor));
            TryAdd(new E5_DeathRespawn(_helper, _monitor));
            TryAdd(new E6_RainFestival(_helper, _monitor));
            TryAdd(new E8_FullDayCycle(_helper, _monitor));
            TryAdd(new E10_IllegalTransitionPermitted(_helper, _monitor));
            TryAdd(new E11_FightRadiusUnenforced(_helper, _monitor));
            TryAdd(new E12_ForageScanBlindIndoor(_helper, _monitor));
            TryAdd(new E13_FarmScanLocationCheck(_helper, _monitor));
            TryAdd(new E14_MineScanLocationCheck(_helper, _monitor));
        }

        // Functional tests
        if ((g & TestGroup.Functional) != 0)
        {
            TryAdd(new Func_DialogueActions(_helper, _monitor));
            TryAdd(new Func_IllegalTransitionAudit(_helper, _monitor));
            TryAdd(new Func_NpcInventoryFidelity(_helper, _monitor));
            TryAdd(new Func_ApiSanitySmoke(_helper, _monitor));
            TryAdd(new Func_MultiplayerSync(_helper, _monitor));
            TryAdd(new Func_VanillaReleaseReturnHome(_helper, _monitor));
        }

        // Integration tests (IT01-IT10): 程序化验证 P0 改动接线
        if ((g & TestGroup.Integration) != 0)
        {
            TryAdd(new IT01_SetState_HaleyEvent(_helper, _monitor));
            TryAdd(new IT02_StateChanged_ActualStateMirror(_helper, _monitor));
            TryAdd(new IT03_F3_TravelFailureRecovery(_helper, _monitor));
            TryAdd(new IT04_F5_EvictionNotification(_helper, _monitor));
            TryAdd(new IT05_TravelCircuitBreaker(_helper, _monitor));
            TryAdd(new IT06_GiveGift_ActionExecution(_helper, _monitor));
            TryAdd(new IT07_G1_ConsecutiveFailureFallback(_helper, _monitor));
            TryAdd(new IT08_G7_InventoryFullNotification(_helper, _monitor));
            TryAdd(new IT09_ChopTree_ActionExecution(_helper, _monitor));
            TryAdd(new IT10_E6_FollowToIdleNoFriendshipGain(_helper, _monitor));
            TryAdd(new IT11_Trade_Settlement(_helper, _monitor));
            TryAdd(new IT12_SetGoal_ChopTree(_helper, _monitor));
            TryAdd(new IT13_DirectorTools(_helper, _monitor));
            TryAdd(new IT14_MultiplayerAdjust(_helper, _monitor));
            TryAdd(new DIR_DirectorBehaviorRecord(_helper, _monitor));
        }

        // NOTE: Real scenario tests (Real_OneFullDay, Scene_*) were removed
        // because the Tests.Real namespace does not exist. Will be re-added in a future phase.
    }

    /// <summary>
    ///     按配置筛选后添加测试。如果 test_config.json 的 enable/disable 列表中有匹配项，
    ///     则根据过滤器决定是否添加。否则默认添加。
    /// </summary>
    private void TryAdd(V3TestBase test)
    {
        var className = test.GetType().Name;
        if (TestConfig.ShouldTestRun(className))
        {
            _tests.Add(test);
        }
        else
        {
            _monitor.Log($"[V3TestRunner] 跳过 '{className}' (test_config.json 过滤)", LogLevel.Debug);
        }
    }
}

/// <summary>汇总条目，用于 _summary.json。</summary>
public class SummaryEntry
{
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public int Passed { get; set; }
    public int Failed { get; set; }
    public string Status { get; set; } = "";
    public bool TimedOut { get; set; }
}