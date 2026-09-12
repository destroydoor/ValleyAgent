#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using StardewModdingAPI;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.TestMod.Mock;
using ValleyAgent.WebSocket;
using ValleyAgent.Testing;

namespace ValleyAgent.TestMod.Tests.ComplexScenario;

/// <summary>
///     L3 复杂场景测试（2026-08-20 Phase 3，mock LLM 先跑通框架）：
///     3 NPC 同场 + 玩家乱操作序列 + 矛盾指令 + 长上下文轮次，
///     驱动 MockWebSocketServer 对话回环，统计：
///       - 幻觉率（7 类，台词 [H:xxx] 标记注入验证判定链路）
///       - 执行成功率（4 类：tool call / set_goal / 对话反应 / 账本一致）
///     产出：_complex_scenario_report.json + 事件流 JSONL + 回放（WS 流量）。
///     真 LLM 阶段：同一检测器对照权威状态工作（test_config 开关）。
/// </summary>
[RegisteredTest(TestGroup.ComplexScenario, "多 NPC 随机场景 + 幻觉率/执行成功率指标", "complex")]
public class Complex01_MultiNpcRandomPlay : V3TestBase
{
    private const int Port = 8809;
    private const int ConnectTimeoutMs = 3000;
    private const int SendTimeoutMs = 3000;
    private const int Rounds = 12;

    // 乱序玩家操作脚本（确定性 seed 洗牌）：(NPC, 玩家输入, 期望工具)
    private static readonly (string Npc, string Input, string ExpectedTool)[] Script = {
        ("Haley", "你好，今天过得怎么样？", "speak"),
        ("Abigail", "我想卖给你 5 个木头", "trade"),
        ("Sebastian", "你跟着我去矿洞吧", "set_state"),
        ("Haley", "这个礼物送给你", "give_gift"),
        ("Abigail", "去帮我砍 3 棵树", "set_goal"),
        ("Sebastian", "你刚才说要跟着我的，现在停下来", "set_state"),
        ("Haley", "你记得我昨天说过的话吗？", "speak"),
        ("Abigail", "再卖给你 2 个石头", "trade"),
        ("Sebastian", "你待在农场别动", "set_state"),
        ("Haley", "给你这个郁金香", "give_gift"),
        ("Abigail", "停止砍树，回来吧", "set_goal"),
        ("Sebastian", "我们去镇上逛逛", "set_state")
    };

    private MockWebSocketServer? _server;
    private MockLLMProvider? _provider;
    private ClientWebSocket? _client;
    private readonly List<string> _npcNames = new() { "Haley", "Abigail", "Sebastian" };
    private readonly Dictionary<string, string> _simState = new(); // NPC 当前权威状态（测试自维护）
    private int _round;
    private int _connectAttempts; // 连接重试计数（mock server listener 绑定偶发慢）
    private bool _setupComplete;
    private ComplexInfrastructure.HallucinationDetector? _detector;
    private ComplexInfrastructure.ExecutionSuccessTracker? _tracker;
    private ComplexInfrastructure.EventStreamRecorder? _events;
    private readonly List<string> _replayLines = new(); // WS 双向流量（回放）
    private string _reportDir = "";

    public Complex01_MultiNpcRandomPlay(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName => "Complex01_MultiNpcRandomPlay";

    public override int TimeoutTicks => 6000; // ~100s，12 轮 × 每轮 500 tick

    public override TestGroup Group => TestGroup.ComplexScenario;

    public override void Setup()
    {
        try
        {
            var library = MockResponseLibrary.CreateDefault();
            _provider = new MockLLMProvider(library);
            _server = new MockWebSocketServer(_provider);
            _server.Start(Port);
        }
        catch (Exception ex)
        {
            Skip($"Failed to start mock server on {Port}: {ex.Message}");
            return;
        }

        _detector = new ComplexInfrastructure.HallucinationDetector();
        _tracker = new ComplexInfrastructure.ExecutionSuccessTracker();
        _reportDir = Path.Combine(V3TestBase.FindProjectRoot(Helper), "logs", "complex_scenario",
            DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        _events = new ComplexInfrastructure.EventStreamRecorder(Path.Combine(_reportDir, "events.jsonl"));

        // 分配 3 个 NPC agent
        var api = ValleyAgent.ModEntry.Instance?.API;
        if (api == null)
        {
            Skip("API not available");
            return;
        }

        foreach (var name in _npcNames)
        {
            _ = api.TryAllocateAgent(name);
            _simState[name] = "IDLE";
        }

        _setupComplete = true;
        Monitor.Log($"[Complex01] Setup complete. NPCs=[{string.Join(",", _npcNames)}], rounds={Rounds}",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (!_setupComplete || _server == null || _provider == null || _detector == null || _tracker == null)
        {
            return true;
        }

        // Phase 0: 连接客户端 + 发一次 hello（收 ack）。
        // 注意：hello 只在连接时发一次——每轮重发会与 dialogue 响应形成 2:1 堆积错位
        //（round N 会收到 round N-2 的响应，日志实证 RESPONSE MISMATCH）。
        // 连接失败每 120 tick 重试。起点 tick 120（listener 绑定有延迟，PIPE 测试实证 tick 60 起连才稳）。
        if (CurrentTick >= 120 && (CurrentTick - 120) % 120 == 0 && _client == null && _connectAttempts < 5)
        {
            _connectAttempts++;
            try
            {
                _client = new ClientWebSocket();
                using var cts = new CancellationTokenSource(ConnectTimeoutMs);
                _client.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/"), cts.Token).GetAwaiter().GetResult();
                var hello = JsonSerializer.Serialize(new { type = "hello", requestId = "cx-hello", npcName = "Haley", modVersion = "1.0.0" });
                _client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(hello)), WebSocketMessageType.Text, true, cts.Token)
                    .GetAwaiter().GetResult();
                _replayLines.Add("> hello (once, fire-and-forget)");
                // 注意：mock server 对 hello 不回 ack（PIPE001 实证只记录不回复）——
                // 等待 ack 会永久阻塞导致 ConnectAsync 超时取消（"The operation was canceled" 根因）。
            }
            catch (Exception ex)
            {
                _client?.Dispose();
                _client = null;
                Monitor.Log($"[Complex01] Connect attempt {_connectAttempts}/5 failed: {ex.Message}", LogLevel.Warn);
            }

            return false;
        }

        // 每轮节奏：tick 120 + round*400 触发一轮
        var roundStart = 120 + _round * 400;
        if (_round < Rounds && CurrentTick >= roundStart && _client?.State == WebSocketState.Open)
        {
            RunRound(_round, CurrentTick);
            _round++;
            _events?.Record(CurrentTick, "round_completed", new { round = _round - 1, total = Rounds });
        }

        if (_round >= Rounds)
        {
            // 全部轮次完成 → 收尾断言 + 报告
            FinalizeAndReport();
            return true;
        }

        return false;
    }

    private void RunRound(int round, int tick)
    {
        if (_provider == null || _client == null || _detector == null || _tracker == null)
        {
            return;
        }

        var (npc, input, expectedTool) = Script[round];
        var requestId = $"cx-{round}-{Guid.NewGuid():N}"[..20];
        _events?.Record(tick, "player_input", new { npc, input, round });

        // 注入本轮 mock 响应：台词按轮次模式埋幻觉标记 + 预期工具调用
        var (speech, toolName) = BuildMockRoundResponse(round, npc, expectedTool);
        _provider.SetOverrideResponse(new DialogueResponse(
            speech,
            new List<ToolAction>
            {
                new(toolName, new Dictionary<string, object> { ["target"] = npc, ["quantity"] = 1 })
            },
            "Happy",
            requestId,
            "dialogue",
            PlayerId: "complex-test-player"));

        // 发 dialogue（连接已在 Phase 0 建立，hello 只发一次）
        try
        {
            using var cts = new CancellationTokenSource(SendTimeoutMs);
            var dialogue = JsonSerializer.Serialize(new
            {
                type = "dialogue",
                requestId,
                npcName = npc,
                playerInput = input,
                worldSnapshot = new
                {
                    season = "summer", day = 28, time = "14:30", weather = "sunny",
                    location = "Farm", npcTile = new { x = 32, y = 30 }, nearbyObjects = "3 villagers",
                    friendship = 500, npcState = _simState[npc], inventory = new[] { new { name = "Wood", quantity = 10 } },
                    farmerName = "Farmer", npcLocation = "Farm"
                }
            });
            _client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(dialogue)), WebSocketMessageType.Text, true, cts.Token)
                .GetAwaiter().GetResult();
            _replayLines.Add($"> dialogue {npc}: {input}");

            // 收响应（mock server 立即返回）
            var buffer = new byte[8192];
            var result = _client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).GetAwaiter().GetResult();
            var respJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
            _replayLines.Add($"< {respJson}");
            var resp = MessageProtocol.Deserialize<DialogueResponse>(respJson);
            // 防御：mock server 响应缺 actions 字段时反序列化为 null（record 无默认值），
            // 统一空列表兜底避免 LINQ source null（round 1/4/6/9 曾因此失败）。
            var respActions = resp.Actions ?? new List<ToolAction>();
            if (resp.RequestId != requestId)
            {
                Monitor.Log($"[Complex01] round {round}: RESPONSE MISMATCH req={requestId} got={resp.RequestId}", LogLevel.Warn);
            }

            _events?.Record(tick, "dialogue_response", new { npc, speech = resp.Speech, actions = respActions.Select(a => a.Tool) });

            // 对话反应率：收到非空 speech = 反应成功
            var reacted = !string.IsNullOrEmpty(resp.Speech);
            _tracker.Record("dialogue_reaction", reacted);
            if (!reacted)
            {
                Monitor.Log($"[Complex01] round {round}: NO REACTION from {npc}", LogLevel.Warn);
            }

            // 幻觉检测：台词标记 + "跟着"启发（对照模拟权威状态）
            var hallucinations = _detector.Detect(tick, npc, resp.Speech, _simState[npc]);
            foreach (var h in hallucinations)
            {
                Monitor.Log($"[Complex01] HALLUCINATION {h} by {npc}: \"{resp.Speech}\"", LogLevel.Warn);
            }

            // 模拟执行工具调用：随机成败（deterministic：round 奇数成功/偶数失败），发 action_result
            var success = round % 2 == 0;
            if (respActions.Count > 0)
            {
                var action = respActions[0];
                _tracker.Record("tool_call", success);
                _tracker.Record(action.Tool == "set_goal" ? "set_goal" : "tool_call", success); // set_goal 单独计一类
                var callId = string.IsNullOrEmpty(action.CallId) ? $"cx-{round}-call" : action.CallId;
                var ack = JsonSerializer.Serialize(new
                {
                    type = "action_result", requestId = $"{requestId}-ack", npcName = npc,
                    action = action.Tool, callId, tool = action.Tool, success
                });
                _client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(ack)), WebSocketMessageType.Text, true, cts.Token)
                    .GetAwaiter().GetResult();
                _replayLines.Add($"> action_result {action.Tool} success={success}");

                // 模拟状态变化：set_state 成功 → 更新权威状态
                if (success && action.Tool == "set_state" && action.Args.TryGetValue("state", out var st))
                {
                    _simState[npc] = st.ToString() ?? "IDLE";
                }
            }

            // 承诺检测：玩家要求做事（卖/跟/砍）但响应无对应工具 → 承诺幻觉
            if (expectedTool == "set_state" && respActions.All(a => a.Tool != "set_state"))
            {
                _detector.RecordPromise(tick, npc, input);
            }

            _events?.Record(tick, "round_done", new { npc, success, simState = _simState[npc] });
        }
        catch (Exception ex)
        {
            Monitor.Log($"[Complex01] round {round} failed: {ex.Message}", LogLevel.Error);
            _tracker.Record("tool_call", false);
            _tracker.Record("dialogue_reaction", false);
        }
    }

    /// <summary>构造本轮 mock 台词：按轮次注入幻觉标记（[H:State]/[H:Location]/[H:Memory] 等）。</summary>
    private static (string Speech, string Tool) BuildMockRoundResponse(int round, string npc, string expectedTool)
    {
        var hallucinationTag = round switch
        {
            3 => "[H:Item]",
            5 => "[H:State]",
            7 => "[H:Location]",
            9 => "[H:Memory]",
            _ => ""
        };

        var speech = round switch
        {
            5 => $"{hallucinationTag}我其实正在跟着你走呢",
            7 => $"{hallucinationTag}我刚从矿洞回来，收获不错",
            9 => $"{hallucinationTag}我记得你说过要给我买钻石",
            _ => $"{hallucinationTag}（{npc} 的回应：收到你的话啦）"
        };

        // 期望工具缺失的轮次（6/10：矛盾指令后无对应工具 → 承诺幻觉检测）
        var tool = round is 6 or 10 ? "speak" : expectedTool;
        return (speech, tool);
    }

    private void FinalizeAndReport()
    {
        if (_detector == null || _tracker == null)
        {
            return;
        }

        // 幻觉率 = 幻觉记录数 / 可验证声明轮数（12 轮中埋了 4 轮标记 + 承诺检测）
        var hallucinationCount = _detector.Records.Count;
        var verifiableClaims = 12;
        var hallucinationRate = (double)hallucinationCount / verifiableClaims;

        var report = new
        {
            test = TestName,
            timestamp = DateTime.Now.ToString("s"),
            llm_mode = "mock",
            rounds = Rounds,
            npcs = _npcNames,
            hallucination_rate = hallucinationRate,
            hallucination_count = hallucinationCount,
            hallucinations_by_kind = _detector.Records
                .GroupBy(r => r.Kind)
                .ToDictionary(g => g.Key.ToString(), g => g.Count()),
            execution_success = _tracker.ReportSection(),
            replay = new
            {
                ws_traffic_lines = _replayLines.Count,
                path = Path.Combine(_reportDir, "replay.txt")
            }
        };

        _ = Directory.CreateDirectory(_reportDir);
        File.WriteAllText(Path.Combine(_reportDir, "_complex_scenario_report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(_reportDir, "replay.txt"), string.Join("\n", _replayLines));
        _events?.Record(0, "report_written", new { path = _reportDir });

        Assert("report_written", File.Exists(Path.Combine(_reportDir, "_complex_scenario_report.json")),
            _reportDir);
        Assert("hallucination_detector_fired", hallucinationCount >= 4,
            $"detected={hallucinationCount} (expected >= 4 injected markers)");
        Assert("tool_call_tracked", _tracker.Counters["tool_call"].Total >= Rounds,
            $"total={_tracker.Counters["tool_call"].Total}");

        Monitor.Log($"[Complex01] DONE: hallucinationRate={hallucinationRate:F2} ({hallucinationCount}/12), " +
                    $"report={Path.Combine(_reportDir, "_complex_scenario_report.json")}", LogLevel.Info);
    }

    public override void Teardown()
    {
        try
        {
            _client?.Dispose();
        }
        catch (Exception)
        {
            // 非致命：Teardown 容错，Dispose 异常不应掩盖测试真实结论
        }

        try
        {
            _server?.Dispose();
        }
        catch (Exception)
        {
            // 非致命：Teardown 容错，Dispose 异常不应掩盖测试真实结论
        }

        _events?.Dispose();
        Monitor.Log($"[Complex01] Teardown. report={_reportDir}", LogLevel.Info);
    }
}
