import { test, expect } from "bun:test";
import type {
  DialogueRequest,
  DialogueResponse,
  HelloRequest,
  HelloResponse,
  PingRequest,
  PongResponse,
  ActionResultMessage,
  StateChangedMessage,
  ToolAction,
  DirectorCommandMessage,
  WorldSnapshot,
  ExecuteAdjustMessage,
  AdjustResultMessage,
  ReconnectSyncMessage,
} from "../src/types";

// ---------------------------------------------------------------------------
// T0: L1 契约 round-trip 测试
// ---------------------------------------------------------------------------
// 对每种活跃消息类型，构造一个完整实例 → JSON.stringify → JSON.parse → 断言所有字段一致。
// 特别关注新增字段：
//   - state_changed.reason（自由字符串，向后兼容）
//   - dialogue_response.friendshipDelta / friendshipReason（方案 B 好感度评估并回）
//   - action_result.reason（C# ActionResultReason 枚举的 camelCase 字符串）
// 断言 `type` 字段在 round-trip 后不变（防止 type 字段被序列化层吞掉）。
// 对应思路文档 §6 T0：round-trip 待补项。
// ---------------------------------------------------------------------------

function roundtrip<T>(obj: T): T {
  return JSON.parse(JSON.stringify(obj)) as T;
}

const fullWorldSnapshot: WorldSnapshot = {
  season: "summer",
  day: 28,
  time: "14:30",
  weather: "sunny",
  location: "Town",
  npcTile: { x: 32, y: 18 },
  nearbyObjects: "2 villagers, Pierre's shop entrance",
  friendship: 250,
  npcState: "IDLE",
  inventory: [{ name: "Amethyst", quantity: 2 }],
  farmerName: "新来的农夫",
};

// ─── hello（双向：C# 发 hello 请求，TS 返回 hello 响应） ───

test("hello request round-trip preserves all fields", () => {
  const req: HelloRequest = {
    type: "hello",
    requestId: "550e8400-e29b-41d4-a716-446655440000",
    modVersion: "1.2.3",
  };
  const rt = roundtrip(req);
  expect(rt.type).toBe("hello");
  expect(rt.requestId).toBe(req.requestId);
  expect(rt.modVersion).toBe("1.2.3");
});

test("hello response round-trip preserves all fields", () => {
  const resp: HelloResponse = {
    type: "hello",
    requestId: "req-abc",
    status: "ok",
    serverVersion: "0.1.0",
  };
  const rt = roundtrip(resp);
  expect(rt.type).toBe("hello");
  expect(rt.requestId).toBe("req-abc");
  expect(rt.status).toBe("ok");
  expect(rt.serverVersion).toBe("0.1.0");
});

// ─── ping / pong ───

test("ping request round-trip preserves type and requestId", () => {
  const req: PingRequest = { type: "ping", requestId: "ping-1" };
  const rt = roundtrip(req);
  expect(rt.type).toBe("ping");
  expect(rt.requestId).toBe("ping-1");
});

test("pong response round-trip preserves type and requestId", () => {
  const resp: PongResponse = { type: "pong", requestId: "ping-1" };
  const rt = roundtrip(resp);
  expect(rt.type).toBe("pong");
  expect(rt.requestId).toBe("ping-1");
});

// ─── ack ───

test("ack response round-trip preserves type and requestId", () => {
  const resp = { type: "ack" as const, requestId: "req-xyz" };
  const rt = roundtrip(resp);
  expect(rt.type).toBe("ack");
  expect(rt.requestId).toBe("req-xyz");
});

// ─── dialogue 请求 ───

test("dialogue request round-trip preserves all fields including worldSnapshot", () => {
  const req: DialogueRequest = {
    type: "dialogue",
    requestId: "req-dialogue-1",
    npcName: "Abigail",
    playerInput: "你好，今天天气不错。",
    worldSnapshot: fullWorldSnapshot,
  };
  const rt = roundtrip(req);
  expect(rt.type).toBe("dialogue");
  expect(rt.requestId).toBe("req-dialogue-1");
  expect(rt.npcName).toBe("Abigail");
  expect(rt.playerInput).toBe("你好，今天天气不错。");
  // worldSnapshot 嵌套对象字段
  expect(rt.worldSnapshot.season).toBe("summer");
  expect(rt.worldSnapshot.day).toBe(28);
  expect(rt.worldSnapshot.time).toBe("14:30");
  expect(rt.worldSnapshot.weather).toBe("sunny");
  expect(rt.worldSnapshot.location).toBe("Town");
  expect(rt.worldSnapshot.npcTile).toEqual({ x: 32, y: 18 });
  expect(rt.worldSnapshot.nearbyObjects).toBe("2 villagers, Pierre's shop entrance");
  expect(rt.worldSnapshot.friendship).toBe(250);
  expect(rt.worldSnapshot.npcState).toBe("IDLE");
  expect(rt.worldSnapshot.inventory).toEqual([{ name: "Amethyst", quantity: 2 }]);
  expect(rt.worldSnapshot.farmerName).toBe("新来的农夫");
});

// ─── dialogue_response（重点：方案 B friendshipDelta/friendshipReason 字段） ───

test("dialogue_response round-trip preserves friendshipDelta and friendshipReason when present", () => {
  const actions: ToolAction[] = [
    { tool: "speak", args: { text: "谢谢你的夸奖。" } },
    { tool: "set_state", args: { state: "TALK" }, callId: "tc-2" },
  ];
  const resp: DialogueResponse = {
    type: "dialogue_response",
    requestId: "req-dialogue-1",
    npcName: "Abigail",
    speech: "谢谢你的夸奖。",
    actions,
    emotion: "Happy",
    memorySideEffect: "recorded",
    friendshipDelta: 10,
    friendshipReason: "他夸了我的头发",
  };
  const rt = roundtrip(resp);
  expect(rt.type).toBe("dialogue_response");
  expect(rt.requestId).toBe("req-dialogue-1");
  expect(rt.npcName).toBe("Abigail");
  expect(rt.speech).toBe("谢谢你的夸奖。");
  expect(rt.actions).toEqual(actions);
  expect(rt.actions[1]!.callId).toBe("tc-2");
  expect(rt.emotion).toBe("Happy");
  expect(rt.memorySideEffect).toBe("recorded");
  // 方案 B 关键字段
  expect(rt.friendshipDelta).toBe(10);
  expect(rt.friendshipReason).toBe("他夸了我的头发");
});

test("dialogue_response round-trip omits friendshipDelta when not present (LLM 未输出)", () => {
  const resp: DialogueResponse = {
    type: "dialogue_response",
    requestId: "req-2",
    npcName: "Haley",
    speech: "你好。",
    actions: [],
    emotion: "Neutral",
  };
  const rt = roundtrip(resp);
  expect(rt.type).toBe("dialogue_response");
  // LLM 未输出 evaluate_friendship 时字段不携带，C# 端按 null/0 处理
  expect(rt.friendshipDelta).toBeUndefined();
  expect(rt.friendshipReason).toBeUndefined();
});

test("dialogue_response with fallback=true round-trips correctly", () => {
  const resp: DialogueResponse = {
    type: "dialogue_response",
    requestId: "req-fb",
    npcName: "Abigail",
    speech: "（我有点走神了，你刚说什么？）",
    actions: [{ tool: "emote", args: { emote_id: "question" } }],
    emotion: "Confused",
    fallback: true,
  };
  const rt = roundtrip(resp);
  expect(rt.type).toBe("dialogue_response");
  expect(rt.fallback).toBe(true);
  expect(rt.emotion).toBe("Confused");
});

test("dialogue_response friendshipDelta can be negative (减好感)", () => {
  const resp: DialogueResponse = {
    type: "dialogue_response",
    requestId: "req-neg",
    npcName: "Sebastian",
    speech: "嗯。",
    actions: [],
    emotion: "Annoyed",
    friendshipDelta: -5,
    friendshipReason: "他冒犯了我",
  };
  const rt = roundtrip(resp);
  expect(rt.friendshipDelta).toBe(-5);
  expect(rt.friendshipReason).toBe("他冒犯了我");
});

test("dialogue_response friendshipDelta of zero is preserved when explicitly set", () => {
  const resp: DialogueResponse = {
    type: "dialogue_response",
    requestId: "req-zero",
    npcName: "Sam",
    speech: "嗨。",
    actions: [],
    emotion: "Neutral",
    friendshipDelta: 0,
  };
  const rt = roundtrip(resp);
  expect(rt.friendshipDelta).toBe(0);
});

// ─── action_result（重点：reason 字段） ───

test("action_result round-trip preserves reason field when present (success=false)", () => {
  const msg: ActionResultMessage = {
    type: "action_result",
    requestId: "req-ar-1",
    callId: "call-1",
    npcName: "Haley",
    tool: "give_gift",
    success: false,
    result: "玩家背包已满",
    reason: "inventoryFull",
  };
  const rt = roundtrip(msg);
  expect(rt.type).toBe("action_result");
  expect(rt.requestId).toBe("req-ar-1");
  expect(rt.callId).toBe("call-1");
  expect(rt.npcName).toBe("Haley");
  expect(rt.tool).toBe("give_gift");
  expect(rt.success).toBe(false);
  expect(rt.result).toBe("玩家背包已满");
  // C# ActionResultReason 序列化为 camelCase 字符串
  expect(rt.reason).toBe("inventoryFull");
});

test("action_result round-trip preserves success=true with no reason", () => {
  const msg: ActionResultMessage = {
    type: "action_result",
    requestId: "req-ar-2",
    callId: "call-2",
    npcName: "Haley",
    tool: "give_gift",
    success: true,
    result: "礼物已送出",
  };
  const rt = roundtrip(msg);
  expect(rt.type).toBe("action_result");
  expect(rt.success).toBe(true);
  expect(rt.result).toBe("礼物已送出");
  expect(rt.reason).toBeUndefined();
});

test("action_result round-trip preserves all reason enum values", () => {
  // C# ActionResultReason 枚举 9 个值，camelCase 序列化
  const reasons = [
    "none",
    "agentMissing",
    "transitionBlocked",
    "invalidState",
    "targetUnreachable",
    "itemNotFound",
    "inventoryFull",
    "locationInvalid",
    "internalError",
  ];
  for (const reason of reasons) {
    const msg: ActionResultMessage = {
      type: "action_result",
      requestId: `req-${reason}`,
      callId: `call-${reason}`,
      success: false,
      reason,
    };
    const rt = roundtrip(msg);
    expect(rt.reason).toBe(reason);
    expect(rt.type).toBe("action_result");
  }
});

test("action_result without npcName round-trips (legacy C# client compat)", () => {
  const msg: ActionResultMessage = {
    type: "action_result",
    requestId: "req-legacy",
    callId: "call-legacy",
    success: true,
  };
  const rt = roundtrip(msg);
  expect(rt.type).toBe("action_result");
  expect(rt.npcName).toBeUndefined();
  expect(rt.tool).toBeUndefined();
});

// ─── state_changed（重点：reason 字段） ───

test("state_changed round-trip preserves all fields including reason", () => {
  const msg: StateChangedMessage = {
    type: "state_changed",
    npcName: "Haley",
    previousState: "FOLLOW",
    newState: "IDLE",
    wasForced: true,
    previousStateDurationMs: 12500,
    reason: "travel_failed",
  };
  const rt = roundtrip(msg);
  expect(rt.type).toBe("state_changed");
  expect(rt.npcName).toBe("Haley");
  expect(rt.previousState).toBe("FOLLOW");
  expect(rt.newState).toBe("IDLE");
  expect(rt.wasForced).toBe(true);
  expect(rt.previousStateDurationMs).toBe(12500);
  expect(rt.reason).toBe("travel_failed");
});

test("state_changed round-trip without reason (向后兼容旧 C# 客户端)", () => {
  const msg: StateChangedMessage = {
    type: "state_changed",
    npcName: "Abigail",
    previousState: "IDLE",
    newState: "FOLLOW",
    wasForced: false,
    previousStateDurationMs: 1000,
  };
  const rt = roundtrip(msg);
  expect(rt.type).toBe("state_changed");
  expect(rt.newState).toBe("FOLLOW");
  expect(rt.reason).toBeUndefined();
});

test("state_changed reason can be any free-form string (evicted/task_completed/llm_decision/manual)", () => {
  const reasons = ["travel_failed", "evicted", "task_completed", "llm_decision", "manual"];
  for (const reason of reasons) {
    const msg: StateChangedMessage = {
      type: "state_changed",
      npcName: "Sebastian",
      previousState: "FARM",
      newState: "IDLE",
      wasForced: true,
      previousStateDurationMs: 30000,
      reason,
    };
    const rt = roundtrip(msg);
    expect(rt.reason).toBe(reason);
    expect(rt.type).toBe("state_changed");
  }
});

// ─── type 字段在 round-trip 后不变（防序列化层吞字段） ───

test("all active message types preserve their type field through round-trip", () => {
  const messages: Array<{ type: string }> = [
    { type: "hello" },
    { type: "ping" },
    { type: "pong" },
    { type: "ack" },
    { type: "dialogue" },
    { type: "dialogue_response" },
    { type: "action_result" },
    { type: "state_changed" },
    { type: "director_command" },
    { type: "execute_adjust" },
    { type: "adjust_result" },
    { type: "reconnect_sync" },
  ];
  for (const m of messages) {
    const rt = roundtrip(m);
    expect(rt.type, `type "${m.type}" should survive round-trip`).toBe(m.type);
  }
});

// ─── director_command（Phase 3 Director 工具调用，TS→C#） ───

test("director_command round-trip preserves tool/args/requestId", () => {
  const cmd: DirectorCommandMessage = {
    type: "director_command",
    tool: "set_npc_mood",
    args: { npc: "Abigail", moodTag: "烦躁" },
    requestId: "req-director-1",
  };
  const rt = roundtrip(cmd);
  expect(rt.type).toBe("director_command");
  expect(rt.tool).toBe("set_npc_mood");
  expect(rt.args).toEqual({ npc: "Abigail", moodTag: "烦躁" });
  expect(rt.requestId).toBe("req-director-1");
});

test("director_command round-trip preserves empty args and all 9 tool names", () => {
  const tools = [
    "set_npc_position",
    "set_npc_inventory",
    "set_npc_money",
    "set_npc_mood",
    "set_npc_recent_events",
    "set_npc_working_on",
    "spawn_beat",
    "spawn_group_beat",
    "inject_memory",
  ];
  for (const tool of tools) {
    const cmd: DirectorCommandMessage = {
      type: "director_command",
      tool,
      args: {},
      requestId: `req-${tool}`,
    };
    const rt = roundtrip(cmd);
    expect(rt.type).toBe("director_command");
    expect(rt.tool).toBe(tool);
    expect(rt.args).toEqual({});
    expect(rt.requestId).toBe(`req-${tool}`);
  }
});

// ─── execute_adjust / adjust_result / reconnect_sync（2026-08-16 联机 playerId 契约） ───

test("execute_adjust round-trip preserves playerId when present", () => {
  const req: ExecuteAdjustMessage = {
    type: "execute_adjust",
    requestId: "req-adj-1",
    instructionId: "adj-req-adj-1",
    npcName: "Abigail",
    ops: [
      { kind: "money", target: "player", amount: 450, reason: "trade" },
      { kind: "item", target: "player", itemId: "(O)66", itemName: "Amethyst", quantity: -3, reason: "trade" },
    ],
    playerId: "12345678901234567",
  };
  const rt = roundtrip(req);
  expect(rt.type).toBe("execute_adjust");
  expect(rt.requestId).toBe(req.requestId);
  expect(rt.instructionId).toBe(req.instructionId);
  expect(rt.npcName).toBe("Abigail");
  expect(rt.ops).toEqual(req.ops);
  // 2026-08-16 联机关键字段：playerId 必须原样往返
  expect(rt.playerId).toBe("12345678901234567");
});

test("execute_adjust round-trip omits playerId when absent (旧客户端兼容)", () => {
  const req: ExecuteAdjustMessage = {
    type: "execute_adjust",
    requestId: "req-adj-2",
    instructionId: "adj-req-adj-2",
    npcName: "Haley",
    ops: [],
  };
  const rt = roundtrip(req);
  expect(rt.type).toBe("execute_adjust");
  expect(rt.playerId).toBeUndefined();
});

test("adjust_result round-trip preserves playerId echo when present", () => {
  const msg: AdjustResultMessage = {
    type: "adjust_result",
    requestId: "req-adj-1",
    instructionId: "adj-req-adj-1",
    npcName: "Abigail",
    success: true,
    steps: [{ index: 0, kind: "money", target: "player", success: true, failureCode: "none" }],
    playerMoney: 950,
    npcMoney: 550,
    playerId: "12345678901234567",
  };
  const rt = roundtrip(msg);
  expect(rt.type).toBe("adjust_result");
  expect(rt.instructionId).toBe(msg.instructionId);
  expect(rt.playerMoney).toBe(950);
  expect(rt.npcMoney).toBe(550);
  expect(rt.playerId).toBe("12345678901234567");
});

test("adjust_result round-trip omits playerId when absent (旧客户端)", () => {
  const msg: AdjustResultMessage = {
    type: "adjust_result",
    requestId: "req-adj-3",
    instructionId: "adj-req-adj-3",
    npcName: "Haley",
    success: false,
    steps: [{ index: 0, kind: "money", target: "npc", success: false, failureCode: "insufficientFunds" }],
    failureCode: "insufficientFunds",
  };
  const rt = roundtrip(msg);
  expect(rt.type).toBe("adjust_result");
  expect(rt.failureCode).toBe("insufficientFunds");
  expect(rt.playerId).toBeUndefined();
});

test("adjust_result failureCode playerNotFound round-trips (2026-08-16 联机)", () => {
  const msg: AdjustResultMessage = {
    type: "adjust_result",
    requestId: "req-adj-4",
    instructionId: "adj-req-adj-4",
    npcName: "Abigail",
    success: false,
    steps: [{ index: 0, kind: "batch", target: "", success: false, failureCode: "playerNotFound" }],
    failureCode: "playerNotFound",
    playerId: "999999999999",
  };
  const rt = roundtrip(msg);
  expect(rt.type).toBe("adjust_result");
  expect(rt.failureCode).toBe("playerNotFound");
  expect(rt.steps[0]!.failureCode).toBe("playerNotFound");
  expect(rt.playerId).toBe("999999999999");
});

test("reconnect_sync round-trip preserves replayedOutbox/agents/gameDate", () => {
  const msg: ReconnectSyncMessage = {
    type: "reconnect_sync",
    requestId: "req-rs-1",
    replayedOutbox: 3,
    agents: ["Abigail", "Haley"],
    gameDate: "Y1_summer_14",
  };
  const rt = roundtrip(msg);
  expect(rt.type).toBe("reconnect_sync");
  expect(rt.requestId).toBe("req-rs-1");
  expect(rt.replayedOutbox).toBe(3);
  expect(rt.agents).toEqual(["Abigail", "Haley"]);
  expect(rt.gameDate).toBe("Y1_summer_14");
});

test("reconnect_sync round-trip without gameDate (旧客户端)", () => {
  const msg: ReconnectSyncMessage = {
    type: "reconnect_sync",
    requestId: "req-rs-2",
    replayedOutbox: 0,
    agents: [],
  };
  const rt = roundtrip(msg);
  expect(rt.type).toBe("reconnect_sync");
  expect(rt.gameDate).toBeUndefined();
});

// ─── 综合场景：完整对话往返 ───

test("full dialogue exchange round-trip: dialogue → dialogue_response with friendshipDelta", () => {
  // 模拟一次完整对话的协议层往返：C# 发 dialogue 请求，TS 返回 dialogue_response
  const request: DialogueRequest = {
    type: "dialogue",
    requestId: "req-exchange-1",
    npcName: "Abigail",
    playerInput: "你的头发真好看。",
    worldSnapshot: fullWorldSnapshot,
  };
  const response: DialogueResponse = {
    type: "dialogue_response",
    requestId: "req-exchange-1",
    npcName: "Abigail",
    speech: "谢谢你的夸奖。",
    actions: [{ tool: "speak", args: { text: "谢谢你的夸奖。" } }],
    emotion: "Happy",
    memorySideEffect: "recorded",
    friendshipDelta: 10,
    friendshipReason: "他夸了我的头发",
  };

  // 序列化为 wire 格式（模拟 WebSocket 传输），再反序列化
  const wireReq = JSON.stringify(request);
  const wireResp = JSON.stringify(response);
  const parsedReq = JSON.parse(wireReq) as DialogueRequest;
  const parsedResp = JSON.parse(wireResp) as DialogueResponse;

  // 请求字段
  expect(parsedReq.type).toBe("dialogue");
  expect(parsedReq.npcName).toBe("Abigail");
  expect(parsedReq.playerInput).toBe("你的头发真好看。");
  expect(parsedReq.worldSnapshot.friendship).toBe(250);

  // 响应字段（含方案 B 好感度评估）
  expect(parsedResp.type).toBe("dialogue_response");
  expect(parsedResp.requestId).toBe(parsedReq.requestId); // requestId 配对
  expect(parsedResp.speech).toBe("谢谢你的夸奖。");
  expect(parsedResp.friendshipDelta).toBe(10);
  expect(parsedResp.friendshipReason).toBe("他夸了我的头发");
});
