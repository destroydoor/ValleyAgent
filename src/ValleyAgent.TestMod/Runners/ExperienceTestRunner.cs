#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.TestMod.Input;
using ValleyAgent.TestMod.Mock;
using ValleyAgent.TestMod.Visual;

namespace ValleyAgent.TestMod.Runners;

/// <summary>
///     Player-experience test scheduler. Orchestrates Experience/Pipeline test
///     groups with mock LLM, input simulation, screenshot capture, and visual
///     assertion export. Mirrors <see cref="V3TestRunner" /> lifecycle and
///     output conventions (logs/test_results/{runTimestamp}/...).
/// </summary>
/// <remarks>
///     Tests are discovered via <see cref="TestRegistry" /> by tag (the
///     lowercase group name, e.g. "experience" / "pipeline"). Tests marked
///     with <c>[RegisteredTest]</c> and tagged accordingly will be picked
///     up. Each test receives <see cref="ScreenshotCapture" />,
///     <see cref="InputSimulator" />, and <see cref="MockLLMProvider" /> hooks
///     via the public properties on <see cref="V3TestBase" />.
/// </remarks>
public sealed class ExperienceTestRunner : IDisposable
{
    private const int DefaultMockPort = 8766;
    private const int MaxTotalTicks = 240_000;
    private const int MaxTicksWithoutAdvance = 27_000;
    private const int MaxConsecutiveFailures = 5;
    private const int MaxLogLines = 200;
    private const int MockServerStopTimeoutMs = 2_000;
    private const string MockResponsesDirectoryName = "Data";
    private const string MockResponsesFileName = "mock_llm_responses.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly bool _enabled;

    private readonly IModHelper _helper;
    private readonly List<string> _logLines = new();
    private readonly IMonitor _monitor;
    private readonly List<SummaryEntry> _summaryEntries = new();
    private readonly List<V3TestBase> _tests = new();
    private string _activeGroup = "Experience";
    private int _consecutiveSetupFailures;
    private int _consecutiveTestFailures;

    // Per-test state — mirrors V3TestRunner field set.
    private V3TestBase? _currentTest;
    private int _currentTestIndex;
    private int _currentTick;
    private bool _disposed;
    private InputSimulator? _input;
    private int _lastAdvanceTotalTick;
    private MockResponseLibrary? _mockLibrary;
    private MockLLMProvider? _mockLLM;

    // Mock / visual infrastructure — initialized in Start(), torn down on finalize/abort.
    private MockWebSocketServer? _mockServer;
    private string _runTimestamp = "";
    private ScreenshotCapture? _screenshot;
    private bool _started;
    private int _totalTicksStarted;
    private VisualAnalysisExporter? _visualExporter;

    public ExperienceTestRunner(IModHelper helper, IMonitor monitor, bool enabled)
    {
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _enabled = enabled;
    }

    /// <summary>True when the runner is mid-run (started but not yet finished).</summary>
    public bool IsRunning
    {
        get => _started && !IsDone;
    }

    /// <summary>True once the runner has finished all tests (or aborted).</summary>
    public bool IsDone { get; private set; }

    /// <summary>Test name currently executing, or empty if between tests / not started.</summary>
    public string CurrentTestName
    {
        get => _currentTest?.TestName ?? "";
    }

    /// <summary>Snapshot of recent log lines (newest at end, capped to MaxLogLines).</summary>
    public IReadOnlyList<string> LogLines
    {
        get => _logLines;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TeardownInfrastructure();
    }

    /// <summary>
    ///     Begin a test run for the given group. Validates <paramref name="group" />
    ///     is one of <c>"Experience"</c> or <c>"Pipeline"</c> (case-insensitive),
    ///     initializes the mock LLM stack, discovers tests, and prepares the first
    ///     test for Setup on the next <see cref="Update" /> tick.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     Thrown when <paramref name="group" /> is not "Experience" or "Pipeline".
    /// </exception>
    public void Start(string group = "Experience")
    {
        if (!_enabled)
        {
            Log(LogLevel.Warn, "[ExperienceTestRunner] Cannot start — runner is disabled.");
            return;
        }

        if (_started && !IsDone)
        {
            Log(LogLevel.Warn, "[ExperienceTestRunner] Already running; ignoring Start().");
            return;
        }

        if (IsDone)
        {
            Log(LogLevel.Warn, "[ExperienceTestRunner] Already done; create a new instance to re-run.");
            return;
        }

        if (!string.Equals(group, "Experience", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(group, "Pipeline", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(group, "Complex", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Invalid group '{group}'. Must be 'Experience', 'Pipeline' or 'Complex'.", nameof(group));
        }

        _activeGroup = group;
        _runTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        try
        {
            InitializeInfrastructure();
            DiscoverTests(group);
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Error, $"[ExperienceTestRunner] Initialization failed: {ex.Message}");
            TeardownInfrastructure();
            IsDone = true;
            return;
        }
        catch (IOException ex)
        {
            Log(LogLevel.Error, $"[ExperienceTestRunner] Initialization failed: {ex.Message}");
            TeardownInfrastructure();
            IsDone = true;
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log(LogLevel.Error, $"[ExperienceTestRunner] Initialization failed: {ex.Message}");
            TeardownInfrastructure();
            IsDone = true;
            return;
        }

        if (_tests.Count == 0)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] No tests discovered for group '{group}'.");
            WriteSummaryFile();
            WriteCompletionLatch();
            TeardownInfrastructure();
            IsDone = true;
            return;
        }

        // Inject hooks + run timestamp on every test, mirroring V3TestRunner.
        foreach (var t in _tests)
        {
            t.RunTimestamp = _runTimestamp;
            t.Screenshot = _screenshot;
            t.Input = _input;
            t.MockLLM = _mockLLM;
        }

        _currentTestIndex = 0;
        _currentTest = _tests[0];
        _currentTick = 0;
        _lastAdvanceTotalTick = 0;
        _totalTicksStarted = 0;
        _started = true;
        Log(LogLevel.Info,
            $"[ExperienceTestRunner] Started: {_tests.Count} tests, group='{group}', mock port={DefaultMockPort}.");
    }

    /// <summary>Stop the current run immediately, tearing down mock / capture infrastructure.</summary>
    public void Abort()
    {
        Log(LogLevel.Warn, "[ExperienceTestRunner] Abort requested.");
        TeardownInfrastructure();
        IsDone = true;
    }

    /// <summary>Per-tick driver. Called by ModEntry every game tick.</summary>
    public void Update()
    {
        if (!_enabled || IsDone || !_started)
        {
            return;
        }

        ForceUnpause();
        MuteAudio();

        _totalTicksStarted++;

        // Watchdog 1: absolute total-tick limit.
        if (_totalTicksStarted > MaxTotalTicks)
        {
            Log(LogLevel.Error,
                $"[ExperienceTestRunner] Watchdog: total ticks {_totalTicksStarted} > {MaxTotalTicks}. Aborting.");
            WriteWatchdogMarker("TotalTickLimit", $"TotalTicks={_totalTicksStarted}");
            FinalizeRun(true);
            return;
        }

        // Watchdog 2: no test has advanced in MaxTicksWithoutAdvance ticks.
        // 2026-08-20 Phase 5：阈值动态化（当前测试 TimeoutTicks + 余量）——
        // EXP012 完整流程设计 29000 tick，固定 27000 阈值会提前误杀。
        var stallTicks = _totalTicksStarted - _lastAdvanceTotalTick;
        var stallLimit = Math.Max(MaxTicksWithoutAdvance, (_currentTest?.TimeoutTicks ?? 0) + 2000);
        if (stallTicks > stallLimit)
        {
            Log(LogLevel.Error,
                $"[ExperienceTestRunner] Watchdog: stall detected ({stallTicks} ticks without advance, limit={stallLimit}). Aborting.");
            WriteWatchdogMarker("StallDetected", $"StallTicks={stallTicks}");
            FinalizeRun(true);
            return;
        }

        // All tests complete → finalize the run.
        if (_currentTestIndex >= _tests.Count)
        {
            FinalizeRun(false);
            return;
        }

        // Defensive: should always be non-null after Start()/AdvanceTest(),
        // but recover if state was disturbed.
        if (_currentTest == null)
        {
            _currentTest = _tests[_currentTestIndex];
            _currentTick = 0;
        }

        _currentTest.CurrentTick = _currentTick;
        _currentTick++;

        // First tick of this test → Setup (matches V3TestRunner).
        if (_currentTick == 1)
        {
            Log(LogLevel.Info,
                $"[ExperienceTestRunner] Starting test {_currentTestIndex + 1}/{_tests.Count}: {_currentTest.TestName}");
            try
            {
                _currentTest.Setup();
                if (_currentTest.WasSkipped)
                {
                    _consecutiveSetupFailures++;
                    Log(LogLevel.Warn,
                        $"[ExperienceTestRunner] '{_currentTest.TestName}' skipped ({_consecutiveSetupFailures} consecutive).");
                    if (_consecutiveSetupFailures >= 3)
                    {
                        Log(LogLevel.Warn,
                            $"[ExperienceTestRunner] {_consecutiveSetupFailures} consecutive setup skips — flushing remaining {_tests.Count - _currentTestIndex - 1} tests.");
                        _currentTestIndex = _tests.Count;
                        _currentTest = null;
                    }
                }
                else
                {
                    _consecutiveSetupFailures = 0;
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
            catch (KeyNotFoundException ex)
            {
                HandleTestException(ex);
            }
            catch (IndexOutOfRangeException ex)
            {
                HandleTestException(ex);
            }

            return;
        }

        // Process queued input + screenshots before driving the test.
        try
        {
            _input?.ProcessPendingInputs();
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Input flush failed: {ex.Message}");
        }
        catch (NullReferenceException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Input flush failed: {ex.Message}");
        }

        try
        {
            _screenshot?.ProcessCaptures();
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Screenshot process failed: {ex.Message}");
        }
        catch (NullReferenceException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Screenshot process failed: {ex.Message}");
        }

        // Timeout check. 2026-08-20 Phase 1：超时默认 = 失败（防乐观判定），
        // 白名单（timeoutExemptions）豁免。不再无差别跳过失败计数。
        // 2026-08-20 Phase 5：超时检查用 CurrentTick（_currentTick 在 Update 前递增，
        // 边界 tick 的完成型测试会被提前一拍误杀）
        if (_currentTick > _currentTest.TimeoutTicks)
        {
            _currentTest.TimedOut = true;
            if (TestConfig.TimeoutAsFailure && !TestConfig.IsTimeoutExempt(_currentTest.TestName))
            {
                _currentTest.RecordRunnerAssertion("timeout_as_failure",
                    false,
                    $"test timed out after {_currentTick} ticks without timeoutExemptions entry");
            }

            Log(LogLevel.Warn,
                $"[ExperienceTestRunner] Test '{_currentTest.TestName}' timed out after {_currentTick} ticks." +
                (TestConfig.IsTimeoutExempt(_currentTest.TestName) ? " (exempted)" : ""));
            FinalizeCurrentTest();
            return;
        }

        // Drive the test.
        try
        {
            if (_currentTest.Update())
            {
                _consecutiveTestFailures = 0;
                FinalizeCurrentTest();
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
        catch (KeyNotFoundException ex)
        {
            HandleTestException(ex);
        }
        catch (IndexOutOfRangeException ex)
        {
            HandleTestException(ex);
        }
    }

    private void FinalizeCurrentTest()
    {
        if (_currentTest == null)
        {
            return;
        }

        try
        {
            _currentTest.Teardown();
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Teardown failed: {ex.Message}");
        }
        catch (NullReferenceException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Teardown failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Teardown failed: {ex.Message}");
        }

        try
        {
            _currentTest.SaveResults();
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] SaveResults failed: {ex.Message}");
        }
        catch (NullReferenceException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] SaveResults failed: {ex.Message}");
        }

        ForwardVisualAssertions(_currentTest);
        AddSummaryEntry(_currentTest);
        AdvanceTest();
    }

    private void HandleTestException(Exception ex)
    {
        if (_currentTest == null)
        {
            return;
        }

        _consecutiveTestFailures++;
        Log(LogLevel.Error,
            $"[ExperienceTestRunner] Test '{_currentTest.TestName}' threw: {ex.Message} ({_consecutiveTestFailures} consecutive).");

        try
        {
            _currentTest.RecordException(ex);
        }
        catch (ArgumentNullException)
        {
            /* ignore */
        }

        try
        {
            _currentTest.Teardown();
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

        try
        {
            _currentTest.SaveResults();
        }
        catch (InvalidOperationException)
        {
        }
        catch (NullReferenceException)
        {
        }

        ForwardVisualAssertions(_currentTest);
        AddSummaryEntry(_currentTest);

        if (_consecutiveTestFailures >= MaxConsecutiveFailures)
        {
            Log(LogLevel.Error,
                $"[ExperienceTestRunner] {_consecutiveTestFailures} consecutive failures — flushing remaining tests.");
            WriteWatchdogMarker("ConsecutiveFailures",
                $"Failures={_consecutiveTestFailures}, LastTest={_currentTest?.TestName}");
            _currentTestIndex = _tests.Count;
            _currentTest = null;
            return;
        }

        AdvanceTest();
    }

    private void AdvanceTest()
    {
        _lastAdvanceTotalTick = _totalTicksStarted;

        // Reset per-test state on shared infrastructure so prior test's queued
        // inputs / recordings do not bleed into the next test.
        try
        {
            _input?.Reset();
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            if (_screenshot != null && _screenshot.IsRecording)
            {
                _ = _screenshot.StopRecording();
            }
        }
        catch (InvalidOperationException)
        {
        }

        // 2026-08-20 Phase 5：测试间世界状态重置（对齐 V3TestRunner.AdvanceTest 的关键部分）。
        // 此前 Experience/Pipeline 组测试间只清输入队列——NPC 状态/位置/玩家位置跨测试延续，
        // 是 EXP010（NPC 不掉血）/EXP011（菜单类型错）等失败的重要诱因。
        ResetWorldStateBetweenTests();

        _currentTestIndex++;
        _currentTick = 0;
        _currentTest = _currentTestIndex < _tests.Count ? _tests[_currentTestIndex] : null;
    }

    /// <summary>
    ///     重置玩家位置/NPC 状态到基线（幂等，失败不中断测试流）。
    ///     与 V3TestRunner.AdvanceTest 的清理策略一致但更轻量：
    ///     不做事件清理/场景清空（EXP 测试自身场景驱动），只保证状态基线。
    /// </summary>
    private void ResetWorldStateBetweenTests()
    {
        try
        {
            var api = ValleyAgent.ModEntry.Instance?.API;
            if (api == null)
            {
                return;
            }

            // NPC agent 状态回 IDLE（防止上一个测试残留 TALK/FOLLOW 等状态影响下一个测试）
            foreach (var name in new[] { "Haley", "Abigail", "Sebastian" })
            {
                try
                {
                    _ = api.TrySetAgentState(name, "IDLE");
                }
                catch (Exception)
                {
                    // 非关键：NPC 可能未分配或不存在
                }
            }

            // 战斗场景（EXP006）可能击杀/移除 NPC——重新分配 + 复活（对齐 V3 清理；
            // 2026-08-20 容器实证缺 Revive 时 EXP007+ 全部 "Haley not found"）。
            try
            {
                _ = api.TryAllocateAgent("Haley");
                _ = api.TryRevive("Haley");
            }
            catch (Exception)
            {
                // 非关键
            }

            // 玩家回 Farm 基线（与 V3 一致：54,30 附近）
            try
            {
                SafeWarp.Farmer(_monitor, "Farm", 54, 30, "ExperienceTestRunner between-tests");
            }
            catch (Exception)
            {
                // 非关键：warp 失败由测试自身 Setup 兜底
            }

            // NPC 位置重置：把主测试 NPC warp 回玩家附近（EXP006 战斗等测试会把 NPC
            // 带离场景，EXP007+ 的 Setup 假设 Haley 在场——2026-08-20 容器实证
            // "Haley not found" 连续 3 skip 刷掉剩余测试的根因）。
            try
            {
                var haley = Game1.getCharacterFromName("Haley");
                if (haley != null && haley.currentLocation != Game1.currentLocation && Game1.currentLocation != null)
                {
                    var tile = TestScenes.FindWalkableTileNear(Game1.currentLocation, Game1.player.Tile, Game1.player.Tile);
                    Game1.warpCharacter(haley, Game1.currentLocation, tile);
                }
            }
            catch (Exception)
            {
                // 非关键：NPC 可能不存在
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] ResetWorldStateBetweenTests failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Forward pending + resolved visual assertions from a test's queue to
    ///     the runner-level exporter. Called once per test after Teardown.
    /// </summary>
    private void ForwardVisualAssertions(V3TestBase test)
    {
        if (test.VisualAssertions == null || _visualExporter == null)
        {
            return;
        }

        foreach (var a in test.VisualAssertions.Pending)
        {
            _visualExporter.AddAssertion(
                a.AnalysisId, a.TestName, a.FileType,
                a.ScreenshotPath, a.Criteria, a.ExpectedResult, a.Context);
        }

        foreach (var a in test.VisualAssertions.Resolved)
        {
            _visualExporter.AddAssertion(
                a.AnalysisId, a.TestName, a.FileType,
                a.ScreenshotPath, a.Criteria, a.ExpectedResult, a.Context);
        }

        test.VisualAssertions.Clear();
    }

    private void FinalizeRun(bool aborted)
    {
        var label = aborted ? "Aborted" : "All tests complete";
        Log(LogLevel.Info, $"[ExperienceTestRunner] {label}. Finalizing run.");
        TeardownInfrastructure();
        WriteSummaryFile();
        ExportVisualAnalysis();
        WriteCompletionLatch();
        IsDone = true;
    }

    // ── Infrastructure lifecycle ────────────────────────────────────────

    private void InitializeInfrastructure()
    {
        // Mock LLM library + provider (LoadSafely falls back to defaults).
        var mockPath = Path.Combine(_helper.DirectoryPath, MockResponsesDirectoryName, MockResponsesFileName);
        _mockLibrary = MockResponseLibrary.LoadSafely(mockPath, msg => Log(LogLevel.Warn, msg));
        _mockLLM = new MockLLMProvider(_mockLibrary);

        // Mock WebSocket server (best-effort; visual tests can still run without it).
        try
        {
            _mockServer = new MockWebSocketServer(_mockLLM);
            _mockServer.Start();
            Log(LogLevel.Info,
                $"[ExperienceTestRunner] MockWebSocketServer listening on port {DefaultMockPort}.");
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Error,
                $"[ExperienceTestRunner] Mock server failed to start: {ex.Message}. Continuing without mock server.");
            _mockServer = null;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Log(LogLevel.Error,
                $"[ExperienceTestRunner] Invalid mock server port {DefaultMockPort}: {ex.Message}.");
            _mockServer = null;
        }
        catch (HttpListenerException ex)
        {
            Log(LogLevel.Error,
                $"[ExperienceTestRunner] Mock server listener error (port {DefaultMockPort}): {ex.Message}. Continuing without mock server.");
            _mockServer = null;
        }

        _input = new InputSimulator(_monitor);
        _screenshot = new ScreenshotCapture(_helper, _monitor);
        _visualExporter = new VisualAnalysisExporter();
    }

    private void DiscoverTests(string group)
    {
        TestRegistry.ScanAssembly(_helper, _monitor);

        // Primary discovery: tests tagged with the lowercase group name
        // (e.g. tag="experience" or tag="pipeline"). Mirrors how RegisteredTest
        // tags are intended to be used for cross-cutting groups.
        var tag = group.ToLowerInvariant();
        foreach (var d in TestRegistry.ByTag(tag))
        {
            if (!TestConfig.ShouldTestRun(d.Name))
            {
                Log(LogLevel.Debug, $"[ExperienceTestRunner] Skipping '{d.Name}' (test_config.json filter).");
                continue;
            }

            try
            {
                var instance = d.CreateInstance(_helper, _monitor, _screenshot);
                _tests.Add(instance);
            }
            catch (InvalidOperationException ex)
            {
                Log(LogLevel.Warn, $"[ExperienceTestRunner] Failed to instantiate '{d.Name}': {ex.Message}");
            }
            catch (ArgumentException ex)
            {
                Log(LogLevel.Warn, $"[ExperienceTestRunner] Failed to instantiate '{d.Name}': {ex.Message}");
            }
        }

        // Fallback: if the group string happens to match a TestGroup enum value
        // (e.g. "Visual"), include those tests too. Dedupe by class name.
        if (Enum.TryParse<TestGroup>(group, true, out var testGroup))
        {
            foreach (var d in TestRegistry.ByGroup(testGroup))
            {
                if (_tests.Any(t => t.GetType().Name == d.Name))
                {
                    continue;
                }

                if (!TestConfig.ShouldTestRun(d.Name))
                {
                    Log(LogLevel.Debug, $"[ExperienceTestRunner] Skipping '{d.Name}' (test_config.json filter).");
                    continue;
                }

                try
                {
                    var instance = d.CreateInstance(_helper, _monitor, _screenshot);
                    _tests.Add(instance);
                }
                catch (InvalidOperationException ex)
                {
                    Log(LogLevel.Warn, $"[ExperienceTestRunner] Failed to instantiate '{d.Name}': {ex.Message}");
                }
                catch (ArgumentException ex)
                {
                    Log(LogLevel.Warn, $"[ExperienceTestRunner] Failed to instantiate '{d.Name}': {ex.Message}");
                }
            }
        }
    }

    private void TeardownInfrastructure()
    {
        if (_mockServer != null)
        {
            try
            {
                var stopTask = _mockServer.StopAsync();
                if (!stopTask.IsCompleted)
                {
                    _ = stopTask.Wait(MockServerStopTimeoutMs);
                }

                Log(LogLevel.Info, "[ExperienceTestRunner] MockWebSocketServer stopped.");
            }
            catch (InvalidOperationException ex)
            {
                Log(LogLevel.Warn, $"[ExperienceTestRunner] Mock server stop failed: {ex.Message}");
            }
            catch (AggregateException ex)
            {
                Log(LogLevel.Warn, $"[ExperienceTestRunner] Mock server stop failed: {ex.Message}");
            }

            try
            {
                _mockServer.Dispose();
            }
            catch (InvalidOperationException)
            {
            }

            _mockServer = null;
        }

        try
        {
            _screenshot?.Dispose();
        }
        catch (InvalidOperationException)
        {
        }

        _screenshot = null;

        // Clear reference-only handles; mock library / provider can be GC'd.
        _mockLLM = null;
        _mockLibrary = null;
        _input = null;
        // _visualExporter intentionally retained so FinalizeRun can still export.
    }

    // ── Output writers (mirror V3TestRunner file naming / format) ───────

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
                runner = "ExperienceTestRunner",
                group = _activeGroup,
                total_tests = _summaryEntries.Count,
                total_passed = totalPassed,
                total_failed = totalFailed,
                total_skipped = totalSkipped,
                tests = _summaryEntries
            };
            var json = JsonSerializer.Serialize(summary, s_jsonOptions);
            File.WriteAllText(Path.Combine(dir, "_summary.json"), json);
            Log(LogLevel.Info, $"[ExperienceTestRunner] Summary written: {Path.Combine(dir, "_summary.json")}");
        }
        catch (IOException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] WriteSummaryFile failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] WriteSummaryFile failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] WriteSummaryFile failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] WriteSummaryFile failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] WriteSummaryFile failed: {ex.Message}");
        }
    }

    private void ExportVisualAnalysis()
    {
        if (_visualExporter == null)
        {
            Log(LogLevel.Info, "[ExperienceTestRunner] No visual exporter; skipping visual analysis export.");
            return;
        }

        if (_visualExporter.Count == 0)
        {
            Log(LogLevel.Info, "[ExperienceTestRunner] No visual assertions collected; skipping export.");
            return;
        }

        try
        {
            var dir = Path.Combine(V3TestBase.FindProjectRoot(_helper), "logs", "test_results", _runTimestamp);
            _ = Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "_visual_analysis_request.json");
            _visualExporter.Export(path, _runTimestamp);
            Log(LogLevel.Info,
                $"[ExperienceTestRunner] Visual analysis exported: {path} ({_visualExporter.Count} assertions).");
        }
        catch (IOException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Visual export failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Visual export failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Visual export failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Visual export failed: {ex.Message}");
        }
    }

    private void WriteCompletionLatch()
    {
        try
        {
            var dir = Path.Combine(V3TestBase.FindProjectRoot(_helper), "logs", "test_results", _runTimestamp);
            _ = Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "_TEST_COMPLETE.txt");
            File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Log(LogLevel.Info, $"[ExperienceTestRunner] Completion latch written: {path}");
        }
        catch (IOException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Latch write failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Latch write failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Log(LogLevel.Warn, $"[ExperienceTestRunner] Latch write failed: {ex.Message}");
        }
    }

    private void WriteWatchdogMarker(string reason, string detail)
    {
        try
        {
            var dir = Path.Combine(V3TestBase.FindProjectRoot(_helper), "logs", "test_results", _runTimestamp);
            _ = Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"_WATCHDOG_ABORT_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path,
                $"Watchdog Abort\n" +
                $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"Reason: {reason}\n" +
                $"Detail: {detail}\n" +
                $"TotalTicksStarted: {_totalTicksStarted}\n" +
                $"ActiveTest: {_currentTest?.TestName ?? "(null)"}\n" +
                $"TestIndex: {_currentTestIndex}/{_tests.Count}\n" +
                $"ConsecutiveFailures: {_consecutiveTestFailures}\n");
        }
        catch (IOException)
        {
            /* non-critical */
        }
        catch (UnauthorizedAccessException)
        {
            /* non-critical */
        }
        catch (ArgumentException)
        {
            /* non-critical */
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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

    private void Log(LogLevel level, string message)
    {
        _monitor.Log(message, level);
        _logLines.Add($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        while (_logLines.Count > MaxLogLines)
        {
            _logLines.RemoveAt(0);
        }
    }

    // Window inactive → force continue (do not pause). Matches V3TestRunner.
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

    // Mute audio every tick to prevent overrides. Matches V3TestRunner.
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
}