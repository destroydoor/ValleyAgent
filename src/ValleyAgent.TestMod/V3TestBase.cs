#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.TestMod.Input;
using ValleyAgent.TestMod.Mock;
using ValleyAgent.TestMod.Scoring;
using ValleyAgent.TestMod.Visual;

namespace ValleyAgent.TestMod;

/// <summary>
///     Base class for v3 tests. Handles timing, assertions, and structured JSON result output.
///     每个测试产出 3 个 JSON 文件（assertions / results / errors），存放在以运行时间戳命名的子目录中。
/// </summary>
public abstract partial class V3TestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly List<TestExceptionRecord> _exceptions = new();
    protected readonly List<AssertionResult> _results = new();

    private readonly List<VisualObservationRecord> _visualObservations = new();

    protected V3TestBase(IModHelper helper, IMonitor monitor)
    {
        Helper = helper;
        Monitor = monitor;
    }

    protected IModHelper Helper { get; }
    protected IMonitor Monitor { get; }

    protected virtual string ConfigNpcName
    {
        get => TestConfig.NpcName;
    }

    protected virtual int ConfigMonitorIntervalTicks
    {
        get => TestConfig.MonitorIntervalTicks;
    }

    protected virtual int ConfigLlmResponseTimeoutTicks
    {
        get => TestConfig.LlmResponseTimeoutTicks;
    }

    protected virtual int ConfigDefaultTestDurationTicks
    {
        get => TestConfig.DefaultTestDurationTicks;
    }

    protected virtual int ConfigAcceleratedSecondsPerTenMinutes
    {
        get => TestConfig.AcceleratedSecondsPerTenMinutes;
    }

    protected virtual int ConfigDefaultSecondsPerTenMinutes
    {
        get => TestConfig.DefaultSecondsPerTenMinutes;
    }

    protected virtual bool ConfigEnableTimeAcceleration
    {
        get => TestConfig.EnableTimeAcceleration;
    }

    /// <summary>Human-readable test name, used in result file.</summary>
    public abstract string TestName { get; }

    /// <summary>Maximum time (in game ticks) before test is force-stopped.</summary>
    public abstract int TimeoutTicks { get; }

    /// <summary>Which group this test belongs to.</summary>
    public abstract TestGroup Group { get; }

    /// <summary>Current tick counter, incremented every game tick by runner.</summary>
    public int CurrentTick { get; set; }

    /// <summary>True if test was skipped (e.g. setup failed). Runner checks this.</summary>
    public bool WasSkipped { get; private set; }

    /// <summary>True if test timed out.</summary>
    public bool TimedOut { get; set; }

    /// <summary>运行时间戳，由 V3TestRunner 在测试开始时设置，用于创建子目录。</summary>
    public string RunTimestamp { get; set; } = "";

    public IReadOnlyList<AssertionResult> TestResults
    {
        get => _results;
    }

    protected List<AssertionResult> Results
    {
        get => _results;
    }

    // ── Phase 5 additions: Visual / Input / Mock hooks ──

    /// <summary>Screenshot capture utility; null when not configured by the test.</summary>
    public ScreenshotCapture? Screenshot { get; set; }

    /// <summary>Input simulator; null when not configured by the test.</summary>
    public InputSimulator? Input { get; set; }

    /// <summary>Mock LLM provider; null when not configured by the test.</summary>
    public MockLLMProvider? MockLLM { get; set; }

    /// <summary>Deferred visual assertion queue; lazy-initialized via <see cref="EnsureVisualAssertions" />.</summary>
    public VisualAssertionQueue? VisualAssertions { get; private set; }

    /// <summary>
    ///     返回测试的评分结果（若测试支持评分）。默认返回 null，表示该测试无评分。
    ///     子类可覆盖此方法以提供实际的评分数据。
    /// </summary>
    public virtual ScoreReportResult? GetScoreResult() => null;

    /// <summary>Called every game tick. Return true when test is complete.</summary>
    public abstract bool Update();

    /// <summary>Called once before Update loop starts.</summary>
    public abstract void Setup();

    /// <summary>Called once after test completes (pass or timeout).</summary>
    public abstract void Teardown();

    // ── Assertions ──

    protected void Assert(string label, bool condition, string detail = "")
    {
        var r = new AssertionResult
        {
            Label = label,
            Passed = condition,
            Detail = detail,
            Tick = CurrentTick
        };
        Results.Add(r);

        var prefix = condition ? "[PASS]" : "[FAIL]";
        Monitor.Log($"{prefix} {TestName}: {label}{(string.IsNullOrEmpty(detail) ? "" : $" ({detail})")}",
            LogLevel.Info);
    }

    /// <summary>
    ///     Runner 层记录断言（2026-08-20 Phase 1）：供 V3/Experience runner 在超时等
    ///     基础设施事件上追加失败断言（超时默认=失败）。internal 仅限本程序集调用。
    /// </summary>
    internal void RecordRunnerAssertion(string label, bool condition, string detail)
    {
        Results.Add(new AssertionResult
        {
            Label = label,
            Passed = condition,
            Detail = detail,
            Tick = CurrentTick
        });
    }

    /// <summary>
    ///     带反例约束的断言（2026-08-20 Phase 1：防乐观判定）。
    ///     <paramref name="counterexample" /> 描述"什么情况这条断言应该失败"，
    ///     可引用 F1-F8/G1-G7/N1-N5 事故编号（如 "F1: NPC 声称在跟随但状态机是 IDLE"）。
    ///     反例约束随断言写入结果 JSON，供死断言检测脚本判断断言是否真实有效。
    /// </summary>
    protected void AssertEx(string label, bool condition, string counterexample, string detail = "")
    {
        var r = new AssertionResult
        {
            Label = label,
            Passed = condition,
            Detail = detail,
            Counterexample = counterexample,
            Tick = CurrentTick
        };
        Results.Add(r);

        var prefix = condition ? "[PASS]" : "[FAIL]";
        Monitor.Log($"{prefix} {TestName}: {label}{(string.IsNullOrEmpty(detail) ? "" : $" ({detail})")}" +
                    $" [反例: {counterexample}]",
            LogLevel.Info);
    }

    /// <summary>
    ///     记录一个 <see cref="TestResult" /> 断言，追加到结果列表并输出日志。
    ///     统一替代各测试类中重复的 Record(TestResult, string) 实现。
    /// </summary>
    protected void Record(TestResult result, string label)
    {
        var prefix = result.Passed ? "[PASS]" : "[FAIL]";
        Monitor.Log($"{prefix} {TestName}: {label} — {result.Message}", LogLevel.Info);
        Results.Add(new AssertionResult
        {
            Label = label,
            Passed = result.Passed,
            Detail = result.Message,
            Tick = CurrentTick
        });
    }

    /// <summary>
    ///     清除指定 NPC 的对话冷却和挂起请求，供测试在阶段切换时重置对话状态。
    ///     统一替代各测试类中重复的 ClearCooldown 实现。
    /// </summary>
    protected static void ClearDialogueCooldown(IValleyAgentApi? api, string npcName)
    {
        api?.ClearDialogueCooldown(npcName);
        api?.ClearDialogueState(npcName);
    }

    /// <summary>
    ///     截断字符串用于日志输出，超长部分以 "..." 表示。
    ///     统一替代各测试类中重复的 Truncate 实现。
    /// </summary>
    protected static string Truncate(string s, int maxLen = 60)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "(empty)";
        }

        return s.Length <= maxLen ? s : s[..maxLen] + "...";
    }

    /// <summary>记录运行时异常（不产生断言，仅追加到异常列表）。</summary>
    public void RecordException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        _exceptions.Add(new TestExceptionRecord
        {
            Tick = CurrentTick,
            Type = ex.GetType().Name,
            Message = ex.Message,
            StackTrace = ex.StackTrace ?? ""
        });
        Monitor.Log($"[EXCEPTION] {TestName}: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
    }

    protected void Skip(string reason)
    {
        WasSkipped = true;
        Monitor.Log($"[SKIP] {TestName}: {reason}", LogLevel.Info);
    }

    // ── Result file output ──

    /// <summary>
    ///     向上搜索项目根目录（含 AGENTS.md 或 .git 的目录），fallback 到 mod 目录。
    /// </summary>
    public static string FindProjectRoot(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        var dir = new DirectoryInfo(helper.DirectoryPath);
        for (var i = 0; i < 10 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return helper.DirectoryPath;
    }

    /// <summary>
    ///     将测试结果以 3 个 JSON 文件写入 logs/test_results/{RunTimestamp}/ 子目录。
    ///     - {TestName}_assertions.json — 所有断言点状态
    ///     - {TestName}_results.json    — 测试结果汇总
    ///     - {TestName}_errors.json     — 仅失败断言 + 异常（无失败则不写）
    /// </summary>
    public void SaveResults()
    {
        // 子目录：logs/test_results/{RunTimestamp}/
        var baseDir = Path.Combine(FindProjectRoot(Helper), "logs", "test_results");
        var runDir = string.IsNullOrEmpty(RunTimestamp)
            ? baseDir
            : Path.Combine(baseDir, RunTimestamp);
        _ = Directory.CreateDirectory(runDir);

        var safeName = TestName.Replace(" ", "_");
        var timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        // ── 1. assertions.json ──
        var assertionsData = new
        {
            test_name = TestName,
            group = Group.ToString(),
            timestamp,
            assertions = Results
        };
        WriteJson(Path.Combine(runDir, $"{safeName}_assertions.json"), assertionsData);

        // ── 2. results.json ──
        int passed = 0, failed = 0;
        foreach (var r in _results)
        {
            if (r.Passed)
            {
                passed++;
            }
            else
            {
                failed++;
            }
        }

        var resultsData = new
        {
            test_name = TestName,
            group = Group.ToString(),
            timestamp,
            total_ticks = CurrentTick,
            passed,
            failed,
            skipped = WasSkipped,
            timed_out = TimedOut,
            // Phase 5 additions: visual assertion counts and observations.
            // Omitted from JSON when null (existing tests produce identical output).
            visual_assertions = VisualAssertions != null
                ? new { pending = VisualAssertions.PendingCount, resolved = VisualAssertions.ResolvedCount }
                : null,
            visual_observations = _visualObservations.Count > 0 ? _visualObservations : null
        };
        WriteJson(Path.Combine(runDir, $"{safeName}_results.json"), resultsData);

        // ── 3. errors.json（仅有失败或异常时才写） ──
        if (failed > 0 || _exceptions.Count > 0)
        {
            var errorAssertions = new List<object>();
            foreach (var r in Results)
            {
                if (!r.Passed)
                {
                    errorAssertions.Add(new { tick = r.Tick, label = r.Label, detail = r.Detail });
                }
            }

            var errorsData = new
            {
                test_name = TestName,
                errors = errorAssertions,
                exceptions = _exceptions.Count > 0 ? _exceptions : null
            };
            WriteJson(Path.Combine(runDir, $"{safeName}_errors.json"), errorsData);
        }

        Monitor.Log($"[{TestName}] Results saved to: {runDir}", LogLevel.Info);
    }

    private static void WriteJson(string path, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOpts);
        File.WriteAllText(path, json);
    }

    /// <summary>Lazy-initialize the <see cref="VisualAssertions" /> queue and return it.</summary>
    protected VisualAssertionQueue EnsureVisualAssertions()
    {
        if (VisualAssertions == null)
        {
            VisualAssertions = new VisualAssertionQueue();
        }

        return VisualAssertions;
    }

    /// <summary>
    ///     Queue a deferred visual observation request for Kimi analysis.
    ///     Generates an analysis id from <see cref="TestName" /> + label
    ///     (snake_case), determines file type from the extension, and enqueues
    ///     a <see cref="VisualAssertion" />. If a
    ///     <see cref="VisualAnalysisExporter" /> is passed via
    ///     <c>context["exporter"]</c>, the assertion is also forwarded.
    ///     <para>
    ///         Experience-paradigm: <paramref name="criteria" /> is treated as
    ///         a player-experience focus question (also forwarded as
    ///         <c>focusQuestion</c> with <c>playerPerspective=true</c>). Kimi
    ///         is expected to respond with qualitative issues/observations
    ///         rather than a pass/fail verdict. <paramref name="expected" />
    ///         is retained for backward compatibility and defaults to ""
    ///         (no expected verdict).
    ///     </para>
    /// </summary>
    protected void AssertVisual(string label, string screenshotPath, string criteria,
        string? expected = "", Dictionary<string, object>? context = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(screenshotPath);
        ArgumentNullException.ThrowIfNull(criteria);

        var queue = EnsureVisualAssertions();
        var analysisId = ToSnakeCase(TestName + "_" + label);
        var fileType = DetermineFileType(screenshotPath);
        var assertion = new VisualAssertion(
            analysisId, TestName, label, screenshotPath, fileType,
            criteria, expected ?? "", context,
            criteria);
        queue.Enqueue(assertion);

        if (context != null
            && context.TryGetValue("exporter", out var exporterObj)
            && exporterObj is VisualAnalysisExporter exporter)
        {
            exporter.AddAssertion(analysisId, TestName, fileType,
                screenshotPath, criteria, expected ?? "",
                focusQuestion: criteria, playerPerspective: true, context: context);
        }

        Monitor.Log($"[VISUAL] {TestName}: queued '{label}' ({fileType}) -> {screenshotPath}",
            LogLevel.Info);
    }

    /// <summary>
    ///     提交一个玩家体验观察请求。Kimi 将从玩家视角分析截图/录像，
    ///     描述任何可能影响玩家体验的问题（而非简单的 PASS/FAIL）。
    ///     等价于以 <paramref name="focusQuestion" /> 作为 criteria 调用
    ///     <see cref="AssertVisual" />。
    /// </summary>
    protected void AssertExperience(string label, string screenshotPath, string focusQuestion,
        Dictionary<string, object>? context = null) =>
        AssertVisual(label, screenshotPath, focusQuestion, "", context);

    /// <summary>Convenience wrapper for <see cref="AssertVisual" /> with UI-visibility criteria.</summary>
    protected void AssertUIVisible(string label, string elementDescription, string? screenshotPath = null)
    {
        AssertVisual(label, screenshotPath ?? "",
            $"从玩家视角观察UI元素'{elementDescription}'是否可见，描述任何可能影响玩家体验的问题");
    }

    /// <summary>Convenience wrapper for <see cref="AssertVisual" /> with text-visibility criteria.</summary>
    protected void AssertTextVisible(string label, string expectedText, string? screenshotPath = null)
    {
        AssertVisual(label, screenshotPath ?? "",
            $"从玩家视角观察文本'{expectedText}'是否完整可读，描述任何可能影响阅读体验的问题");
    }

    /// <summary>Convenience wrapper for <see cref="AssertVisual" /> with button-position criteria.</summary>
    protected void AssertButtonPosition(string label, string buttonDescription, string? screenshotPath = null)
    {
        AssertVisual(label, screenshotPath ?? "",
            $"从玩家视角观察按钮'{buttonDescription}'的位置是否合理，描述任何可能影响操作的体验问题");
    }

    /// <summary>
    ///     Record a visual observation (goes into results JSON but not as a
    ///     pass/fail assertion).
    /// </summary>
    protected void RecordVisual(string label, string value)
    {
        _visualObservations.Add(new VisualObservationRecord
        {
            Label = label,
            Value = value,
            Tick = CurrentTick
        });
        Monitor.Log($"[VISUAL] {TestName}: {label} = {value}", LogLevel.Info);
    }

    /// <summary>
    ///     Request a screenshot via <see cref="Screenshot" /> if configured.
    ///     No-op when <see cref="Screenshot" /> is null.
    /// </summary>
    protected void CaptureScreenshot(string label)
    {
        if (Screenshot != null)
        {
            Screenshot.Request(TestName, label);
            Monitor.Log($"[VISUAL] {TestName}: screenshot requested '{label}'", LogLevel.Info);
        }
        else
        {
            Monitor.Log($"[VISUAL] {TestName}: CaptureScreenshot skipped (Screenshot not set)",
                LogLevel.Warn);
        }
    }

    private static string ToSnakeCase(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(s.Length * 2);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && sb.Length > 0 && sb[^1] != '_' && !char.IsDigit(sb[^1]))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else if (c == ' ' || c == '-')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string DetermineFileType(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "screenshot";
        }

        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".mp4", StringComparison.OrdinalIgnoreCase)
            ? "video"
            : "screenshot";
    }
}

public class AssertionResult
{
    public string Label { get; set; } = "";
    public bool Passed { get; set; }
    public string Detail { get; set; } = "";

    /// <summary>反例约束（2026-08-20 Phase 1）：描述"什么情况这条断言应该失败"，
    /// 可引用 F/G/N 事故编号。死断言检测脚本据此识别从无失败记录的断言。</summary>
    public string Counterexample { get; set; } = "";

    public int Tick { get; set; }
}

/// <summary>运行时异常记录，独立于断言。</summary>
public class TestExceptionRecord
{
    public int Tick { get; set; }
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public string StackTrace { get; set; } = "";
}

/// <summary>Visual observation record for the results JSON (not a pass/fail assertion).</summary>
public class VisualObservationRecord
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public int Tick { get; set; }
}