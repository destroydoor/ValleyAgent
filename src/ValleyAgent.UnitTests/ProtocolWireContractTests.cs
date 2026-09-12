using ValleyAgent.Protocol;
using ValleyAgent.WebSocket;
using Xunit;
using AdjustFailureCode = ValleyAgent.Protocol.ProtocolV2.AdjustFailureCode;
using AdjustOp = ValleyAgent.Protocol.ProtocolV2.AdjustOp;
using AdjustResultMessage = ValleyAgent.Protocol.ProtocolV2.AdjustResultMessage;
using ActionResultMessage = ValleyAgent.Protocol.ProtocolV2.ActionResultMessage;
using DialogueRequest = ValleyAgent.WebSocket.DialogueRequest;
using DialogueResponse = ValleyAgent.WebSocket.DialogueResponse;
using ExecuteAdjustMessage = ValleyAgent.Protocol.ProtocolV2.ExecuteAdjustMessage;

namespace ValleyAgent.UnitTests;

/// <summary>
///     Wire 层契约测试（2026-08-20 测试系统大改 Phase 2：L2 C#↔TS 数据流）。
///     验证 JSON wire 格式（对齐 ValleyAI protocol/messages.json 与 TS 端序列化）→
///     C# 反序列化字段映射正确：camelCase 字段、可选字段缺省、requestId 配对、
///     失败码枚举 camelCase、未知字段容错、类型错误拒绝路径。
///     对应设计稿 L1 契约测试的 C# 半环（TS 半环由 ValleyAI protocol-roundtrip.test.ts 覆盖）。
/// </summary>
public class ProtocolWireContractTests
{
    // ── 1. dialogue 请求（Abstractions record，camelCase）───────────────

    [Fact]
    public void DialogueRequest_FullJson_MapsAllFields()
    {
        var json = """
            {
              "type": "dialogue",
              "requestId": "req-1",
              "npcName": "Abigail",
              "playerInput": "今天天气不错",
              "playerId": "1234567890",
              "worldSnapshot": {
                "season": "summer",
                "day": 28,
                "time": "14:30",
                "weather": "sunny",
                "location": "Town",
                "npcTile": { "x": 32, "y": 30 },
                "nearbyObjects": "2 villagers",
                "friendship": 500,
                "npcState": "IDLE",
                "inventory": [{ "name": "Wood", "quantity": 10 }],
                "farmerName": "Farmer",
                "playerMoney": 5000,
                "npcLocation": "Town",
                "npcMoney": 1000,
                "npcMood": "happy",
                "npcRecentEvents": ["早上散步"],
                "npcWorkingOn": null
              }
            }
            """;

        var msg = MessageProtocol.Deserialize<DialogueRequest>(json);

        Assert.Equal("dialogue", msg.Type);
        Assert.Equal("req-1", msg.RequestId);
        Assert.Equal("Abigail", msg.NpcName);
        Assert.Equal("今天天气不错", msg.PlayerInput);
        Assert.Equal("1234567890", msg.PlayerId); // 联机字段透传
        Assert.NotNull(msg.WorldSnapshot);
        Assert.Equal("summer", msg.WorldSnapshot.Season);
        Assert.Equal(28, msg.WorldSnapshot.Day);
        Assert.Equal("14:30", msg.WorldSnapshot.Time);
        Assert.Equal("sunny", msg.WorldSnapshot.Weather);
        Assert.Equal("Town", msg.WorldSnapshot.Location);
        Assert.Equal(32, msg.WorldSnapshot.NpcTile.X);
        Assert.Equal(30, msg.WorldSnapshot.NpcTile.Y);
        Assert.Equal(500, msg.WorldSnapshot.Friendship);
        Assert.Equal("IDLE", msg.WorldSnapshot.NpcState);
        Assert.Equal(5000, msg.WorldSnapshot.PlayerMoney);
        Assert.Equal("Town", msg.WorldSnapshot.NpcLocation);
        Assert.Equal(1000, msg.WorldSnapshot.NpcMoney);
        Assert.Equal("happy", msg.WorldSnapshot.NpcMood);
        Assert.Single(msg.WorldSnapshot.NpcRecentEvents!);
        Assert.Equal("早上散步", msg.WorldSnapshot.NpcRecentEvents![0]);
    }

    [Fact]
    public void DialogueRequest_MissingOptionalFields_DefaultsWithoutThrowing()
    {
        var json = """
            { "type": "dialogue", "requestId": "req-2", "npcName": "Abigail",
              "playerInput": "hi", "worldSnapshot": { "season": "summer", "day": 1,
              "time": "09:00", "weather": "sunny", "location": "Farm",
              "npcTile": { "x": 1, "y": 1 }, "nearbyObjects": "", "friendship": 0,
              "npcState": "IDLE", "inventory": [], "farmerName": "Farmer" } }
            """;

        var msg = MessageProtocol.Deserialize<DialogueRequest>(json);

        Assert.Null(msg.PlayerId); // 旧客户端不携带 → null（回落 Game1.player）
        Assert.Null(msg.WorldSnapshot.PlayerMoney);
        Assert.Null(msg.WorldSnapshot.NpcMoney);
        Assert.Null(msg.WorldSnapshot.NpcMood);
        Assert.Null(msg.WorldSnapshot.NpcRecentEvents);
        Assert.Null(msg.WorldSnapshot.NpcWorkingOn);
    }

    // ── 2. dialogue_response（speech/actions/emotion/friendshipDelta/fallback）─

    [Fact]
    public void DialogueResponse_FullJson_MapsAllFields()
    {
        var json = """
            {
              "type": "dialogue_response",
              "requestId": "req-1",
              "npcName": "Abigail",
              "speech": "你好呀！",
              "emotion": "Happy",
              "actions": [
                { "tool": "speak", "args": { "text": "你好呀！" }, "callId": "call-1" },
                { "tool": "emote", "args": { "emote_id": 20 }, "callId": "call-2" }
              ],
              "friendshipDelta": 10,
              "friendshipReason": "他夸了我",
              "fallback": true,
              "playerId": "1234567890",
              "memorySideEffect": "recorded"
            }
            """;

        var msg = MessageProtocol.Deserialize<DialogueResponse>(json);

        Assert.Equal("dialogue_response", msg.Type);
        Assert.Equal("req-1", msg.RequestId);
        Assert.Equal("Abigail", msg.NpcName);
        Assert.Equal("你好呀！", msg.Speech);
        Assert.Equal("Happy", msg.Emotion);
        Assert.Equal(2, msg.Actions.Count);
        Assert.Equal("speak", msg.Actions[0].Tool);
        Assert.Equal("call-1", msg.Actions[0].CallId);
        Assert.Equal("call-2", msg.Actions[1].CallId);
        Assert.Equal(10, msg.FriendshipDelta);
        Assert.Equal("他夸了我", msg.FriendshipReason);
        Assert.True(msg.Fallback);
        Assert.Equal("1234567890", msg.PlayerId);
        Assert.Equal("recorded", msg.MemorySideEffect);
    }

    [Fact]
    public void DialogueResponse_NoOptionalFields_DefaultsToNull()
    {
        var json = """
            { "type": "dialogue_response", "requestId": "req-3", "npcName": "Abigail",
              "speech": "嗯...", "emotion": "Neutral", "actions": [] }
            """;

        var msg = MessageProtocol.Deserialize<DialogueResponse>(json);

        Assert.Equal("嗯...", msg.Speech);
        Assert.Empty(msg.Actions);
        Assert.Null(msg.FriendshipDelta); // LLM 未输出 → null（C# 按 0 处理）
        Assert.Null(msg.FriendshipReason);
        Assert.Null(msg.Fallback);
        Assert.Null(msg.PlayerId);
    }

    // ── 3. execute_adjust（TS→C# 原子批指令）──────────────────────────

    [Fact]
    public void ExecuteAdjust_FullJson_MapsAllFields()
    {
        var json = """
            {
              "type": "execute_adjust",
              "requestId": "adj-1",
              "instructionId": "trade-001",
              "npcName": "Abigail",
              "playerId": "1234567890",
              "ops": [
                { "kind": "money", "target": "player", "amount": 50, "reason": "trade" },
                { "kind": "item", "target": "npc", "itemId": "(O)388", "quantity": 2, "reason": "trade" }
              ]
            }
            """;

        var msg = MessageProtocol.Deserialize<ExecuteAdjustMessage>(json);

        Assert.Equal("execute_adjust", msg.Type);
        Assert.Equal("adj-1", msg.RequestId);
        Assert.Equal("trade-001", msg.InstructionId); // 幂等键
        Assert.Equal("Abigail", msg.NpcName);
        Assert.Equal("1234567890", msg.PlayerId);
        Assert.Equal(2, msg.Ops.Count);
        Assert.Equal("money", msg.Ops[0].Kind);
        Assert.Equal("player", msg.Ops[0].Target);
        Assert.Equal(50, msg.Ops[0].Amount);
        Assert.Equal("trade", msg.Ops[0].Reason);
        Assert.Equal("item", msg.Ops[1].Kind);
        Assert.Equal("npc", msg.Ops[1].Target);
        Assert.Equal("(O)388", msg.Ops[1].ItemId);
        Assert.Equal(2, msg.Ops[1].Quantity);
    }

    [Fact]
    public void ExecuteAdjust_NoPlayerId_DefaultsToNull()
    {
        var json = """
            { "type": "execute_adjust", "requestId": "adj-2", "instructionId": "t2",
              "npcName": "Abigail", "ops": [] }
            """;

        var msg = MessageProtocol.Deserialize<ExecuteAdjustMessage>(json);

        Assert.Null(msg.PlayerId); // 旧客户端缺省 → C# 回落 Game1.player
        Assert.Empty(msg.Ops);
    }

    // ── 4. adjust_result（回执，失败码枚举 camelCase）──────────────────

    [Fact]
    public void AdjustResult_FullJson_MapsStepsAndFailureCodes()
    {
        var json = """
            {
              "type": "adjust_result",
              "requestId": "adj-1",
              "instructionId": "trade-001",
              "npcName": "Abigail",
              "success": true,
              "steps": [
                { "index": 0, "kind": "money", "target": "player", "success": true,
                  "failureCode": "none", "detail": "ok" }
              ],
              "playerMoney": 5050,
              "npcMoney": 950
            }
            """;

        var msg = MessageProtocol.Deserialize<AdjustResultMessage>(json);

        Assert.True(msg.Success);
        Assert.Equal("trade-001", msg.InstructionId);
        Assert.Single(msg.Steps);
        Assert.True(msg.Steps[0].Success);
        Assert.Equal(AdjustFailureCode.None, msg.Steps[0].FailureCode); // "none" camelCase → None
        Assert.Equal(5050, msg.PlayerMoney);
        Assert.Equal(950, msg.NpcMoney);
    }

    [Fact]
    public void AdjustResult_FailureCodeCamelCase_MapsToEnum()
    {
        // 失败码 camelCase 字符串 ↔ 枚举（AGENTS.md 已知坑：大小写必须对齐）
        var json = """
            { "type": "adjust_result", "requestId": "r", "instructionId": "i",
              "npcName": "A", "success": false,
              "steps": [ { "index": 0, "kind": "item", "target": "player",
                           "success": false, "failureCode": "inventoryFull",
                           "detail": "背包满" } ] }
            """;

        var msg = MessageProtocol.Deserialize<AdjustResultMessage>(json);

        Assert.False(msg.Success);
        Assert.Equal(AdjustFailureCode.InventoryFull, msg.Steps[0].FailureCode);
    }

    // ── 5. action_result（C#→TS 工具执行回执）──────────────────────────

    [Fact]
    public void ActionResult_SerializesToCamelCaseWire()
    {
        var msg = new ActionResultMessage
        {
            RequestId = "ar-1",
            NpcName = "Abigail",
            Action = "give_gift",
            CallId = "call-9",
            Tool = "give_gift",
            Success = true
        };

        var json = MessageProtocol.Serialize(msg);
        var back = MessageProtocol.Deserialize<ActionResultMessage>(json);

        Assert.Equal("action_result", back.Type);
        Assert.Equal("ar-1", back.RequestId);
        Assert.Equal("Abigail", back.NpcName);
        Assert.Equal("give_gift", back.Action);
        Assert.Equal("call-9", back.CallId); // callId 透传（C3 反馈环匹配）
        Assert.Equal("give_gift", back.Tool);
        Assert.True(back.Success);
    }

    // ── 6. 字段容错（对应 decision/friendship_eval 死管道教训）─────────

    [Fact]
    public void UnknownFields_AreIgnoredWithoutThrowing()
    {
        // TS 端新增字段而 C# 未同步时，旧客户端必须静默忽略而非崩溃
        var json = """
            { "type": "dialogue", "requestId": "r", "npcName": "A", "playerInput": "hi",
              "futureField": { "nested": [1, 2, 3] },
              "worldSnapshot": { "season": "summer", "day": 1, "time": "09:00",
                "weather": "sunny", "location": "Farm", "npcTile": { "x": 1, "y": 1 },
                "nearbyObjects": "", "friendship": 0, "npcState": "IDLE",
                "inventory": [], "farmerName": "F", "futureSnapshotField": "x" } }
            """;

        var msg = MessageProtocol.Deserialize<DialogueRequest>(json);

        Assert.Equal("hi", msg.PlayerInput); // 未知字段不干扰已知字段
    }

    [Fact]
    public void WrongTypeField_ThrowsInvalidOperation()
    {
        // speech 应为 string，TS 发数字 → 拒绝路径（C# 端显式异常而非静默空）
        var json = """
            { "type": "dialogue_response", "requestId": "r", "npcName": "A",
              "speech": 123, "emotion": "Neutral", "actions": [] }
            """;

        Assert.Throws<InvalidOperationException>(
            () => MessageProtocol.Deserialize<DialogueResponse>(json));
    }

    [Fact]
    public void EmptyJson_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => MessageProtocol.Deserialize<DialogueRequest>(""));
        Assert.Throws<ArgumentNullException>(() => MessageProtocol.Deserialize<DialogueRequest>(null!));
    }

    // ── 7. ToolAction args（Dictionary<string, object> 反序列化行为）────

    [Fact]
    public void ToolAction_ArgsDict_ValuesAreJsonElements()
    {
        var json = """
            { "type": "dialogue_response", "requestId": "r", "npcName": "A",
              "speech": "好", "emotion": "Neutral",
              "actions": [ { "tool": "set_state", "args": { "state": "FOLLOW", "n": 3 },
                             "callId": "c1" } ] }
            """;

        var msg = MessageProtocol.Deserialize<DialogueResponse>(json);

        var action = Assert.Single(msg.Actions);
        Assert.Equal("set_state", action.Tool);
        Assert.True(action.Args.ContainsKey("state"));
        Assert.Equal("FOLLOW", action.Args["state"].ToString()); // STJ: object → JsonElement
    }
}
