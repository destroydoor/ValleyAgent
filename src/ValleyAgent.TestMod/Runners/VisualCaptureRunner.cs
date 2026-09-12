#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.TestMod.Input;
using ValleyAgent.TestMod.Visual;

namespace ValleyAgent.TestMod.Runners;

/// <summary>
///     Visual-only capture scheduler. Used for VIS* tests that perform
///     screenshots / recordings without mock LLM. Drives Setup → Update →
///     Teardown per test and writes the same latch files as
///     <see cref="V3TestRunner" /> and <see cref="ExperienceTestRunner" />.
/// </summary>
/// <remarks>
///     Tests are discovered via <see cref="TestRegistry" /> filtered by
///     <see cref="TestGroup.Visual" /> (the existing enum value). Each test
///     receives <see cref="ScreenshotCapture" /> and <see cref="InputSimulator" />
///     hooks via the public properties on <see cref="V3TestBase" />. No mock
///     LLM stack is initialized — visual tests do not need it.
/// </remarks>
public sealed class VisualCaptureRunner : IDisposable
{
    private const int MaxTotalTicks = 240_000;
    private const int MaxTicksWithoutAdvance = 27_000;
    private const int MaxConsecutiveFailures = 5;
    private const int MaxLogLines = 200;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly bool _enabled;

    private readonly IModHelper _helper;
    private readonly List<string> _logLines = new();
    private readonly IMonitor _monitor;
    private readonly List<SummaryEntry> _summaryEntries = new();
    private readonly List<V3TestBase> _tests = new();
    private int _consecutiveSetupFailures;
    private int _consecutiveTestFailures;

    // Per-test state — mirrors V3TestRunner field set.
    private V3TestBase? _currentTest;
    private int _currentTestIndex;
    private int _currentTick;
    private bool _disposed;

    // Visual / input infrastructure (no mock LLM).
    private InputSimulator? _input;
    private int _lastAdvanceTotalTick;
    private string _runTimestamp = "";
    private ScreenshotCapture? _screenshot;
    private bool _started;
    private int _totalTicksStarted;
    private VisualAnalysisExporter? _visualExporter;

    public VisualCaptureRunner(IModHelper helper, IMonitor monitor, bool enabled)
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
    ///     Begin a visual capture run. Discovers tests via
    ///     <see cref="TestRegistry.ByGroup" />(<see cref="TestGroup.Visual" />),
    ///     initializes input + screenshot infrastructure, and prepares the
    ///     first test for Setup on the next <see cref="Update" /> tick.
    /// </summary>
    public void Start()
    {
        if (!_enabled)
        {
            Log(LogLevel.Warn, "[VisualCaptureRunner] Cannot start — runner is disabled.");
            return;
        }

        if (_started && !IsDone)
        {
            Log(LogLevel.Warn, "[VisualCaptureRunner] Already running; ignoring Start().");
            return;
        }

        if (IsDone)
        {
            Log(LogLevel.Warn, "[VisualCaptureRunner] Already done; create a new instance to re-run.");
            return;
        }

        _runTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        try
        {
            InitializeInfrastructure();
            DiscoverTests();
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Error, $"[VisualCaptureRunner] Initialization failed: {ex.Message}");
            TeardownInfrastructure();
            IsDone = true;
            return;
        }
        catch (IOException ex)
        {
            Log(LogLevel.Error, $"[VisualCaptureRunner] Initialization failed: {ex.Message}");
            TeardownInfrastructure();
            IsDone = true;
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log(LogLevel.Error, $"[VisualCaptureRunner] Initialization failed: {ex.Message}");
            TeardownInfrastructure();
            IsDone = true;
            return;
        }

        if (_tests.Count == 0)
        {
            Log(LogLevel.Warn, "[VisualCaptureRunner] No Visual tests discovered.");
            WriteSummaryFile();
            WriteCompletionLatch();
            TeardownInfrastructure();
            IsDone = true;
            return;
        }

        // Inject hooks + run timestamp on every test (Screenshot/Input only;
        // MockLLM intentionally left null for visual-only tests).
        foreach (var t in _tests)
        {
            t.RunTimestamp = _runTimestamp;
            t.Screenshot = _screenshot;
            t.Input = _input;
            // t.MockLLM left null — visual tests do not need it.
        }

        _currentTestIndex = 0;
        _currentTest = _tests[0];
        _currentTick = 0;
        _lastAdvanceTotalTick = 0;
        _totalTicksStarted = 0;
        _started = true;
        Log(LogLevel.Info, $"[VisualCaptureRunner] Started: {_tests.Count} Visual tests.");
    }

    /// <summary>Stop the current run immediately, tearing down capture infrastructure.</summary>
    public void Abort()
    {
        Log(LogLevel.Warn, "[VisualCaptureRunner] Abort requested.");
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
                $"[VisualCaptureRunner] Watchdog: total ticks {_totalTicksStarted} > {MaxTotalTicks}. Aborting.");
            WriteWatchdogMarker("TotalTickLimit", $"TotalTicks={_totalTicksStarted}");
            FinalizeRun(true);
            return;
        }

        // Watchdog 2: no test has advanced in MaxTicksWithoutAdvance ticks.
        var stallTicks = _totalTicksStarted - _lastAdvanceTotalTick;
        if (stallTicks > MaxTicksWithoutAdvance)
        {
            Log(LogLevel.Error,
                $"[VisualCaptureRunner] Watchdog: stall detected ({stallTicks} ticks without advance). Aborting.");
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

        // Defensive: should always be non-null after Start()/AdvanceTest().
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
                $"[VisualCaptureRunner] Starting test {_currentTestIndex + 1}/{_tests.Count}: {_currentTest.TestName}");
            try
            {
                _currentTest.Setup();
                if (_currentTest.WasSkipped)
                {
                    _consecutiveSetupFailures++;
                    Log(LogLevel.Warn,
                        $"[VisualCaptureRunner] '{_currentTest.TestName}' skipped ({_consecutiveSetupFailures} consecutive).");
                    if (_consecutiveSetupFailures >= 3)
                    {
                        Log(LogLevel.Warn,
                            $"[VisualCaptureRunner] {_consecutiveSetupFailures} consecutive setup skips — flushing remaining {_tests.Count - _currentTestIndex - 1} tests.");
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
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Input flush failed: {ex.Message}");
        }
        catch (NullReferenceException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Input flush failed: {ex.Message}");
        }

        try
        {
            _screenshot?.ProcessCaptures();
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Screenshot process failed: {ex.Message}");
        }
        catch (NullReferenceException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Screenshot process failed: {ex.Message}");
        }

        // Timeout check (matches V3TestRunner: timeout does not increment
        // _consecutiveTestFailures — many visual tests legitimately run to the limit).
        if (_currentTick > _currentTest.TimeoutTicks)
        {
            _currentTest.TimedOut = true;
            Log(LogLevel.Warn,
                $"[VisualCaptureRunner] Test '{_currentTest.TestName}' timed out after {_currentTick} ticks.");
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
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Teardown failed: {ex.Message}");
        }
        catch (NullReferenceException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Teardown failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Teardown failed: {ex.Message}");
        }

        try
        {
            _currentTest.SaveResults();
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] SaveResults failed: {ex.Message}");
        }
        catch (NullReferenceException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] SaveResults failed: {ex.Message}");
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
            $"[VisualCaptureRunner] Test '{_currentTest.TestName}' threw: {ex.Message} ({_consecutiveTestFailures} consecutive).");

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
                $"[VisualCaptureRunner] {_consecutiveTestFailures} consecutive failures — flushing remaining tests.");
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

        _currentTestIndex++;
        _currentTick = 0;
        _currentTest = _currentTestIndex < _tests.Count ? _tests[_currentTestIndex] : null;
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
        Log(LogLevel.Info, $"[VisualCaptureRunner] {label}. Finalizing run.");
        WriteSummaryFile();
        ExportVisualAnalysis();
        WriteCompletionLatch();
        TeardownInfrastructure();
        IsDone = true;
    }

    // ── Infrastructure lifecycle ────────────────────────────────────────

    private void InitializeInfrastructure()
    {
        _input = new InputSimulator(_monitor);
        _screenshot = new ScreenshotCapture(_helper, _monitor);
        _visualExporter = new VisualAnalysisExporter();
    }

    private void DiscoverTests()
    {
        TestRegistry.ScanAssembly(_helper, _monitor);
        foreach (var d in TestRegistry.ByGroup(TestGroup.Visual))
        {
            if (!TestConfig.ShouldTestRun(d.Name))
            {
                Log(LogLevel.Debug, $"[VisualCaptureRunner] Skipping '{d.Name}' (test_config.json filter).");
                continue;
            }

            try
            {
                var instance = d.CreateInstance(_helper, _monitor, _screenshot);
                _tests.Add(instance);
            }
            catch (InvalidOperationException ex)
            {
                Log(LogLevel.Warn, $"[VisualCaptureRunner] Failed to instantiate '{d.Name}': {ex.Message}");
            }
            catch (ArgumentException ex)
            {
                Log(LogLevel.Warn, $"[VisualCaptureRunner] Failed to instantiate '{d.Name}': {ex.Message}");
            }
        }
    }

    private void TeardownInfrastructure()
    {
        try
        {
            _screenshot?.Dispose();
        }
        catch (InvalidOperationException)
        {
        }

        _screenshot = null;

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
                runner = "VisualCaptureRunner",
                group = "Visual",
                total_tests = _summaryEntries.Count,
                total_passed = totalPassed,
                total_failed = totalFailed,
                total_skipped = totalSkipped,
                tests = _summaryEntries
            };
            var json = JsonSerializer.Serialize(summary, s_jsonOptions);
            File.WriteAllText(Path.Combine(dir, "_summary.json"), json);
            Log(LogLevel.Info, $"[VisualCaptureRunner] Summary written: {Path.Combine(dir, "_summary.json")}");
        }
        catch (IOException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] WriteSummaryFile failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] WriteSummaryFile failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] WriteSummaryFile failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] WriteSummaryFile failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] WriteSummaryFile failed: {ex.Message}");
        }
    }

    private void ExportVisualAnalysis()
    {
        if (_visualExporter == null)
        {
            Log(LogLevel.Info, "[VisualCaptureRunner] No visual exporter; skipping visual analysis export.");
            return;
        }

        if (_visualExporter.Count == 0)
        {
            Log(LogLevel.Info, "[VisualCaptureRunner] No visual assertions collected; skipping export.");
            return;
        }

        try
        {
            var dir = Path.Combine(V3TestBase.FindProjectRoot(_helper), "logs", "test_results", _runTimestamp);
            _ = Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "_visual_analysis_request.json");
            _visualExporter.Export(path, _runTimestamp);
            Log(LogLevel.Info,
                $"[VisualCaptureRunner] Visual analysis exported: {path} ({_visualExporter.Count} assertions).");
        }
        catch (IOException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Visual export failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Visual export failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Visual export failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Visual export failed: {ex.Message}");
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
            Log(LogLevel.Info, $"[VisualCaptureRunner] Completion latch written: {path}");
        }
        catch (IOException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Latch write failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Latch write failed: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            Log(LogLevel.Warn, $"[VisualCaptureRunner] Latch write failed: {ex.Message}");
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