#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Config;
using ValleyAgent.Initialization;
using ValleyAgent.WebSocket;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     DIR_DirectorBehaviorRecord：导演 AI 行为记录（2026-08-09 新增）。
///     导演的 AI 行为（上下文感知 → LLM 思考 → 编排决策 → 行动）不是可量化打分的功能，
///     因此本测试**不打 pass/fail**——只触发一次完整导演流程并全量记录行为，供人工评分。
///
///     链路：day_started → C# 发送 game_context_sync（GameContext 快照）+ day_started →
///     TS handleDayStarted roll（TriggerProbability=1.0 必命中）→ Director.morningPlan()
///     → 导演 LLM 思考（prompt 输入 / llm 输出全量入 TS 日志）→ validateAndFilter（保留/丢弃）
///     → 产出的 beat 经 allocate_agent 回 C#（ForceAllocate + KeepUntil 豁免）。
///
///     行为素材来源：SMAPI 日志（ServerConsoleWindow=false 时 TS server stdout 经
///     ServerProcessManager.Log → SMAPI Monitor 落盘）。Teardown 提取相关日志段写入
///     {logs/test_results/RunTimestamp}/DIR_DirectorBehaviorRecord_behavior.txt 报告文件。
/// </summary>
public class DIR_DirectorBehaviorRecord : IntegrationTestBase
{
    private const int TriggerTick = 60;
    private const int MaxWaitTicks = 10800; // 180s：导演 LLM 慢时可达 60-90s，留足余量

    private bool _triggered;
    private bool _behaviorDetected;
    private bool _reportWritten;
    private ServerProcessManager? _serverManager;
    private EventHandlerInitializer? _eventHandlerInitializer;
    private readonly List<string> _behaviorLogLines = new();
    private int _logStartLine;

    public DIR_DirectorBehaviorRecord(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "DIR_DirectorBehaviorRecord";
    }

    public override int TimeoutTicks
    {
        get => MaxWaitTicks + 300;
    }

    public override TestGroup Group
    {
        get => TestGroup.Integration;
    }

    private static string SmapiLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StardewValley", "ErrorLogs", "SMAPI-latest.txt");

    public override void Setup()
    {
        if (!TryCommonSetup())
        {
            return;
        }

        // ── 1. 强制导演触发 + TS 日志落盘 ──
        // 内存级改配置（本次会话生效），重启 server 使 --director-probability 1.0 与
        // ConsoleWindow=false（TS stdout → SMAPI 日志）生效。
        var config = Container!.GetService<ModConfig>();
        if (config == null)
        {
            Skip("ModConfig not registered in container");
            return;
        }

        config.Director.TriggerProbability = 1.0;
        config.ServerConsoleWindow = false;
        // 注意：改容器内的 ModConfig 单例即可——ServerProcessManager 已在 SaveLoaded 时
        // RebindConfig 到该实例，重启时直接读它，无需写盘（Helper.WriteConfig 写的是 TestMod 的配置文件）。
        Monitor.Log($"[{TestName}] TriggerProbability={config.Director.TriggerProbability}, ConsoleWindow={config.ServerConsoleWindow}",
            LogLevel.Info);

        _serverManager = Container.GetService<ServerProcessManager>();
        if (_serverManager == null)
        {
            Skip("ServerProcessManager not registered in container");
            return;
        }

        // 关键：ServerProcessManager.ConsoleWindow 只在初始化时从 config 复制一次，
        // 改 config.ServerConsoleWindow 不会同步到它。重启时若它仍是 true 会走 cmd 窗口
        // 分支（stdout 不重定向），TS 日志全部丢失——必须直接改属性。
        _serverManager.ConsoleWindow = false;

        // 注意：必须全限定 ValleyAgent.ModEntry —— 本文件命名空间是 ValleyAgent.TestMod.Tests.Integration，
        // 裸 ModEntry 会解析到 TestMod 自己的 ModEntry（其 Instance 恒 null），导致反射永远失败。
        _eventHandlerInitializer = ValleyAgent.ModEntry.Instance == null
            ? null
            : ReadField<EventHandlerInitializer>(ValleyAgent.ModEntry.Instance, "_eventHandlerInitializer");

        // ── 2. 重启 server（新参数生效）──
        try
        {
            var restarted = _serverManager.RestartServerAsync().GetAwaiter().GetResult();
            Monitor.Log($"[{TestName}] Server restarted (new director probability + log redirection): {restarted}",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            Monitor.Log($"[{TestName}] Server restart failed: {ex.Message}", LogLevel.Warn);
            // 重启失败不致命：server 可能仍在跑旧参数（概率 0.1，不可控），仍记录行为
        }

        // ── 3. 记录日志起点（只采集本次触发的行为）──
        _logStartLine = CountLogLines();
        Monitor.Log($"[{TestName}] Setup complete. logStartLine={_logStartLine}, logPath={SmapiLogPath}",
            LogLevel.Info);
    }

    public override bool Update()
    {
        // ── Phase 1: 触发 day_started（含 game_context_sync）──
        if (CurrentTick == TriggerTick && !_triggered)
        {
            _triggered = true;
            if (_eventHandlerInitializer == null)
            {
                Monitor.Log($"[{TestName}] EventHandlerInitializer not found — cannot trigger day_started",
                    LogLevel.Warn);
                return true;
            }

            try
            {
                // NotifyDayStartedAsync 是 private async：反射调用（fire-and-forget）
                var task = InvokePrivate(_eventHandlerInitializer, "NotifyDayStartedAsync");
                _ = task as Task;
                Monitor.Log($"[{TestName}] day_started + game_context_sync sent (async)", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[{TestName}] Trigger day_started failed: {ex.Message}", LogLevel.Warn);
                return true;
            }
        }

        if (!_triggered)
        {
            return false;
        }

        // ── Phase 2: 轮询导演行为（不判 pass/fail，只记录）──
        if (CurrentTick % 30 == 0)
        {
            var lines = ReadNewLogLines();
            foreach (var line in lines)
            {
                CaptureDirectorFlowLine(line);

                // 导演流程完成信号：TS 产出 beat 并发 allocate_agent，或 morningPlan 明确结束
                if (line.Contains("[send] allocate_agent", StringComparison.OrdinalIgnoreCase)
                    || (line.Contains("[director]", StringComparison.OrdinalIgnoreCase)
                        && line.Contains("morningPlan end", StringComparison.OrdinalIgnoreCase)))
                {
                    _behaviorDetected = true;
                }
            }
        }

        if (_behaviorDetected || CurrentTick >= MaxWaitTicks)
        {
            if (!_reportWritten)
            {
                _reportWritten = true;
                WriteBehaviorReport();
            }

            return true;
        }

        return false;
    }

    public override void Teardown()
    {
        // 兜底：未在 Update 完成时也写报告
        if (!_reportWritten)
        {
            // 再采集一次日志（LLM 可能刚完成）
            foreach (var line in ReadNewLogLines())
            {
                CaptureDirectorFlowLine(line);
            }

            WriteBehaviorReport();
        }

        // 恢复配置（避免影响后续测试）
        var config = Container?.GetService<ModConfig>();
        if (config != null)
        {
            config.Director.TriggerProbability = 0.1;
            config.ServerConsoleWindow = true;
        }

        if (_serverManager != null)
        {
            _serverManager.ConsoleWindow = true;
        }

        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown. captured director lines: {_behaviorLogLines.Count}", LogLevel.Info);
    }

    // ── 行为采集 ──

    private int CountLogLines()
    {
        try
        {
            if (!File.Exists(SmapiLogPath))
            {
                return 0;
            }

            return ReadAllLinesShared(SmapiLogPath).Count;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private List<string> ReadNewLogLines()
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(SmapiLogPath))
            {
                return result;
            }

            var lines = ReadAllLinesShared(SmapiLogPath);
            for (var i = _logStartLine; i < lines.Count; i++)
            {
                result.Add(lines[i]);
            }

            _logStartLine = lines.Count;
        }
        catch (IOException)
        {
            // 非致命：SMAPI 日志被独占句柄占用时跳过本轮，返回已读部分
        }
        catch (UnauthorizedAccessException)
        {
            // 非致命：无读权限时跳过本轮，返回已读部分
        }

        return result;
    }

    /// <summary>
    ///     以 FileShare.ReadWrite 读取日志文件。SMAPI 持续持有该文件句柄，
    ///     File.ReadAllLines 默认 FileShare.Read 会被独占句柄挡掉（实测 logStartLine=0 的根因）。
    /// </summary>
    private static List<string> ReadAllLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            lines.Add(line);
        }

        return lines;
    }

    private bool _inDirectorFlow;

    /// <summary>
    ///     采集导演流程窗口内的日志。窗口从 "[director] triggered/morningPlan start" 打开，
    ///     到 "morningPlan end" 关闭。窗口内**全量捕获**（LLM 的多行思考/输出文本不带
    ///     [director] 标签，按标签过滤会丢掉最有价值的素材），并跳过与上一行完全相同的行
    ///     （ServerProcessManager.LogCallback 被订阅两次，TS 每行日志会重复出现）。
    /// </summary>
    private void CaptureDirectorFlowLine(string line)
    {
        if (line.Contains("[director] triggered", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[director] morningPlan start", StringComparison.OrdinalIgnoreCase))
        {
            _inDirectorFlow = true;
        }

        if (_inDirectorFlow)
        {
            if (_behaviorLogLines.Count == 0 || _behaviorLogLines[^1] != line)
            {
                _behaviorLogLines.Add(line);
            }
        }

        if (_inDirectorFlow
            && (line.Contains("morningPlan end", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[send] allocate_agent", StringComparison.OrdinalIgnoreCase)))
        {
            _inDirectorFlow = false;
        }
    }

    /// <summary>把导演行为写成报告文件（logs/test_results/{RunTimestamp}/DIR_DirectorBehaviorRecord_behavior.txt）。</summary>
    private void WriteBehaviorReport()
    {
        try
        {
            var baseDir = Path.Combine(FindProjectRoot(Helper), "logs", "test_results");
            var runDir = string.IsNullOrEmpty(RunTimestamp)
                ? baseDir
                : Path.Combine(baseDir, RunTimestamp);
            _ = Directory.CreateDirectory(runDir);
            var path = Path.Combine(runDir, "DIR_DirectorBehaviorRecord_behavior.txt");

            var sb = new StringBuilder();
            sb.AppendLine("导演 AI 行为记录（非通过/未通过判定，供人工评分）");
            sb.AppendLine($"触发时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"行为完成: {_behaviorDetected} | 采集导演日志行数: {_behaviorLogLines.Count}");
            sb.AppendLine($"说明: 链路 = game_context_sync(上下文先行) → day_started → TS roll(概率1.0) → Director.morningPlan() → LLM 思考 → beat 校验/丢弃 → allocate_agent → C# 分配。");
            sb.AppendLine("=" .PadRight(80, '='));
            if (_behaviorLogLines.Count == 0)
            {
                sb.AppendLine("（未采集到导演行为日志——检查 ServerConsoleWindow 配置 / TS 日志落盘）");
            }
            else
            {
                foreach (var line in _behaviorLogLines)
                {
                    sb.AppendLine(line);
                }
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Monitor.Log($"[{TestName}] Behavior report written: {path}", LogLevel.Info);

            // 行为记录进 results（不打 pass/fail，仅观测）
            RecordVisual("director_behavior_report", $"行为报告已写入 {path}，采集 {_behaviorLogLines.Count} 行导演日志");
        }
        catch (IOException ex)
        {
            Monitor.Log($"[{TestName}] WriteBehaviorReport failed: {ex.Message}", LogLevel.Warn);
        }
        catch (UnauthorizedAccessException ex)
        {
            Monitor.Log($"[{TestName}] WriteBehaviorReport failed: {ex.Message}", LogLevel.Warn);
        }
    }
}
