#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ValleyAgent.Testing;

/// <summary>
///     L3 复杂场景基础设施（2026-08-20 Phase 3）：
///     EventStreamRecorder（事件流 JSONL，设计稿 L5-B）、
///     HallucinationDetector（7 类幻觉判定）、
///     ExecutionSuccessTracker（4 类执行成功率统计）。
///     mock LLM 阶段：台词中的幻觉声明用 [H:类名] 标记注入，检测器据此验证判定链路；
///     切真 LLM 后同一检测器对照权威状态（L2 状态/回执/历史）工作。
/// </summary>
public static class ComplexInfrastructure
{
    // ── 事件流 ──────────────────────────────────────────────────────────

    /// <summary>事件流 JSONL 记录器：{tick, type, payload} 逐行追加。</summary>
    public sealed class EventStreamRecorder : IDisposable
    {
        private readonly StreamWriter _writer;

        public EventStreamRecorder(string path)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(path, append: false);
        }

        public void Record(int tick, string type, object payload)
        {
            try
            {
                var line = JsonSerializer.Serialize(new
                {
                    tick,
                    type,
                    payload
                });
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (IOException)
            {
                // 非关键：事件流写入失败不中断测试
            }
        }

        public void Dispose()
        {
            try
            {
                _writer.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }

    // ── 幻觉检测（7 类）──────────────────────────────────────────────────

    /// <summary>幻觉分类（对应讨论中的 7 类可测幻觉）。</summary>
    public enum HallucinationKind
    {
        State,      // 声称的状态 ≠ 权威状态
        Item,       // 声称的物品行为 ≠ 回执
        Memory,     // 引用的事件不在历史
        Promise,    // 承诺的动作 5s 内无状态变化
        Location,   // 声称的位置 ≠ 权威位置
        Character,  // 引用不存在的 NPC
        Time        // 声称的时间 ≠ 游戏时间
    }

    public sealed record HallucinationRecord(int Tick, string Npc, HallucinationKind Kind, string Speech, string Expected);

    /// <summary>
    ///     幻觉检测器。mock 阶段：台词含 "[H:State]" 等标记 → 必然判定为对应类幻觉
    ///     （验证判定链路）；真 LLM 阶段：调用 <c>Check</c> 对照权威值（真 LLM 阶段接口，mock 阶段未实现）。
    /// </summary>
    public sealed class HallucinationDetector
    {
        private readonly List<HallucinationRecord> _records = new();

        public IReadOnlyList<HallucinationRecord> Records => _records;

        /// <summary>按对话文本检测幻觉声明（mock 标记 + 关键词启发），返回检测到的分类列表。</summary>
        public List<HallucinationKind> Detect(int tick, string npc, string speech, string authoritativeState)
        {
            var kinds = new List<HallucinationKind>();
            if (string.IsNullOrEmpty(speech))
            {
                return kinds;
            }

            // mock 标记：台词内嵌 [H:State] 等，测试数据显式注入幻觉声明
            foreach (var kind in Enum.GetValues<HallucinationKind>())
            {
                if (speech.Contains($"[H:{kind}]", StringComparison.Ordinal))
                {
                    kinds.Add(kind);
                    _records.Add(new HallucinationRecord(tick, npc, kind, speech, authoritativeState));
                }
            }

            // 真 LLM 启发：声称跟随但快照不是 FOLLOW（状态幻觉——海莉事件原形）
            if (speech.Contains("跟着", StringComparison.Ordinal)
                && !authoritativeState.Contains("FOLLOW", StringComparison.OrdinalIgnoreCase)
                && !kinds.Contains(HallucinationKind.State))
            {
                kinds.Add(HallucinationKind.State);
                _records.Add(new HallucinationRecord(tick, npc, HallucinationKind.State, speech, authoritativeState));
            }

            return kinds;
        }

        /// <summary>显式记录一条承诺幻觉（玩家让 NPC 做事，N 轮后无状态变化）。</summary>
        public void RecordPromise(int tick, string npc, string promisedAction)
        {
            _records.Add(new HallucinationRecord(tick, npc, HallucinationKind.Promise, promisedAction, "no state change"));
        }

        /// <summary>显式记录一条人物幻觉（引用不存在的 NPC）。</summary>
        public void RecordCharacter(int tick, string npc, string referencedName)
        {
            _records.Add(new HallucinationRecord(tick, npc, HallucinationKind.Character, referencedName, "character does not exist"));
        }
    }

    // ── 执行成功率（4 类）───────────────────────────────────────────────

    /// <summary>执行成功率统计（4 类：tool call / set_goal / 对话反应 / 账本一致）。</summary>
    public sealed class ExecutionSuccessTracker
    {
        private readonly Dictionary<string, (int Success, int Total)> _counters = new()
        {
            ["tool_call"] = (0, 0),
            ["set_goal"] = (0, 0),
            ["dialogue_reaction"] = (0, 0),
            ["ledger_consistency"] = (0, 0)
        };

        public void Record(string kind, bool success)
        {
            var (s, t) = _counters[kind];
            _counters[kind] = (s + (success ? 1 : 0), t + 1);
        }

        public double Rate(string kind)
        {
            var (s, t) = _counters[kind];
            return t == 0 ? 1.0 : (double)s / t;
        }

        public IReadOnlyDictionary<string, (int Success, int Total)> Counters => _counters;

        /// <summary>生成报告段：每类成功率 + 硬门槛判定（tool_call/ledger ≥95%，dialogue_reaction = 100% 目标）。</summary>
        public object ReportSection()
        {
            var toolCall = Rate("tool_call");
            var goal = Rate("set_goal");
            var reaction = Rate("dialogue_reaction");
            var ledger = Rate("ledger_consistency");
            return new
            {
                tool_call_success_rate = toolCall,
                set_goal_success_rate = goal,
                dialogue_reaction_rate = reaction,
                ledger_consistency_rate = ledger,
                hard_gate = new
                {
                    tool_call_gate_95 = toolCall >= 0.95,
                    ledger_gate_95 = ledger >= 0.95,
                    reaction_gate_100 = reaction >= 1.0
                }
            };
        }
    }
}
