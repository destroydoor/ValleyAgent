using System.Collections.Generic;
using ValleyAgent.Testing;
using Xunit;
using ComplexInfrastructure = ValleyAgent.Testing.ComplexInfrastructure;

namespace ValleyAgent.UnitTests;

/// <summary>
///     L3 复杂场景基础设施单元测试（2026-08-20 Phase 3）：
///     幻觉检测器（7 类标记 + "跟着"启发式）、执行成功率统计（4 类 + 硬门槛）、
///     事件流记录器文件输出。纯逻辑部分秒级验证，游戏内回环由 Complex01 容器测试覆盖。
/// </summary>
public class ComplexInfrastructureTests
{
    // ── 幻觉检测器 ──────────────────────────────────────────────────────

    [Fact]
    public void Detect_MockMarker_ClassifiesHallucination()
    {
        var detector = new ComplexInfrastructure.HallucinationDetector();

        var kinds = detector.Detect(10, "Haley", "我刚从矿洞回来 [H:Location]", "IDLE");

        Assert.Contains(ComplexInfrastructure.HallucinationKind.Location, kinds);
        Assert.Single(detector.Records);
        Assert.Equal(10, detector.Records[0].Tick);
        Assert.Equal("Haley", detector.Records[0].Npc);
    }

    [Fact]
    public void Detect_MultipleMarkers_AllClassified()
    {
        var detector = new ComplexInfrastructure.HallucinationDetector();

        var kinds = detector.Detect(20, "Abigail", "[H:State][H:Memory]我在跟着你，还记得你说的话", "IDLE");

        Assert.Contains(ComplexInfrastructure.HallucinationKind.State, kinds);
        Assert.Contains(ComplexInfrastructure.HallucinationKind.Memory, kinds);
        Assert.Equal(2, detector.Records.Count);
    }

    [Fact]
    public void Detect_FollowKeyword_WithoutFollowState_FlagsStateHallucination()
    {
        // 海莉事件原形：台词声称跟随但权威状态不是 FOLLOW
        var detector = new ComplexInfrastructure.HallucinationDetector();

        var kinds = detector.Detect(30, "Haley", "我现在正跟着你走呢", "IDLE");

        Assert.Contains(ComplexInfrastructure.HallucinationKind.State, kinds);
    }

    [Fact]
    public void Detect_FollowKeyword_WhenActuallyFollowing_NoHallucination()
    {
        var detector = new ComplexInfrastructure.HallucinationDetector();

        var kinds = detector.Detect(30, "Haley", "我现在正跟着你走呢", "FOLLOW");

        Assert.Empty(kinds);
        Assert.Empty(detector.Records);
    }

    [Fact]
    public void Detect_NoMarkerOrKeyword_Empty()
    {
        var detector = new ComplexInfrastructure.HallucinationDetector();

        var kinds = detector.Detect(40, "Sebastian", "今天天气不错", "IDLE");

        Assert.Empty(kinds);
    }

    [Fact]
    public void RecordPromise_And_RecordCharacter_Append()
    {
        var detector = new ComplexInfrastructure.HallucinationDetector();

        detector.RecordPromise(50, "Haley", "我会去砍树");
        detector.RecordCharacter(60, "Abigail", "小镇理发师");

        Assert.Equal(2, detector.Records.Count);
        Assert.Equal(ComplexInfrastructure.HallucinationKind.Promise, detector.Records[0].Kind);
        Assert.Equal(ComplexInfrastructure.HallucinationKind.Character, detector.Records[1].Kind);
    }

    // ── 执行成功率统计器 ────────────────────────────────────────────────

    [Fact]
    public void Tracker_RecordsAndComputesRates()
    {
        var tracker = new ComplexInfrastructure.ExecutionSuccessTracker();

        tracker.Record("tool_call", true);
        tracker.Record("tool_call", true);
        tracker.Record("tool_call", false);
        tracker.Record("dialogue_reaction", true);
        tracker.Record("dialogue_reaction", false);

        Assert.Equal(2.0 / 3.0, tracker.Rate("tool_call"), 9);
        Assert.Equal(0.5, tracker.Rate("dialogue_reaction"), 9);
        Assert.Equal(1.0, tracker.Rate("set_goal"), 9); // 未记录 → 100%（无样本不惩罚）
    }

    [Fact]
    public void Tracker_ReportSection_ContainsHardGates()
    {
        var tracker = new ComplexInfrastructure.ExecutionSuccessTracker();
        for (var i = 0; i < 20; i++)
        {
            tracker.Record("tool_call", true);
        }

        tracker.Record("ledger_consistency", true);
        tracker.Record("ledger_consistency", false); // 50% → 硬门槛不过

        dynamic report = tracker.ReportSection();
        var gates = (dynamic)report.hard_gate;

        Assert.True((bool)gates.tool_call_gate_95);
        Assert.False((bool)gates.ledger_gate_95);
    }

    // ── 事件流记录器 ────────────────────────────────────────────────────

    [Fact]
    public void EventStreamRecorder_WritesJsonlLines()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"complex-events-{System.Guid.NewGuid():N}.jsonl");
        try
        {
            using (var recorder = new ComplexInfrastructure.EventStreamRecorder(path))
            {
                recorder.Record(10, "player_input", new { npc = "Haley", input = "你好" });
                recorder.Record(20, "dialogue_response", new { npc = "Haley", speech = "嗨" });
            }

            var lines = System.IO.File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"tick\":10", lines[0]);
            Assert.Contains("\"type\":\"player_input\"", lines[0]);
            Assert.Contains("\"npc\":\"Haley\"", lines[1]);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
