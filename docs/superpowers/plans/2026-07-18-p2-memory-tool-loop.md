# P2 记忆闭环 + 工具结果回注 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 补完 TS 服务器（`<VALLEYAI_ROOT>`）的记忆闭环：`forget` 工具注册（B3）、工具执行结果回注 LLM（C3）、短期记忆计数合并去重（B4）、输出校验器接线（C1 补强）。全部工作在 TS 侧，C# 侧仅一处验证/补发 `action_result`。

**Architecture:** 沿用 P0 建立的三层架构。关键结构性约束（探查确认）：`StardewAgent` 每次对话重建 core `Agent` 实例（`stardew-agent.ts:94`），跨对话状态只能挂在 `StardewAgentRegistry` 或 `AgentMemory` 上——C3 的反馈队列因此设计在 Registry 层。

**Tech Stack:** TypeScript (Bun)、bun test（单测+集成，bunfig 强制 80% 行覆盖）

**前置事实（2026-07-18 探查确认）：**
- `remember` 工具**已注册**（`stardew-tools.ts:144-173`），调用 `memory.addSignificantMemory`。B3 只剩 `forget`。
- `tool_call_result` / `action_result` 处理器是 ack-only no-op（`protocol-adapter.ts:39-46`，注释自认 "P1+"）；全代码库无 `_last_tool_result` 等价物。
- `AgentMemory` 已有 60 秒文本去重窗（`agent-memory.ts:48-52`）与 30 条 importance+recency 淘汰上限（:63-71）；无计数合并；`getRecentMemories` 按时间序返回尾部。
- `OutputValidator`（CJK 比例 ≥30% + 空回复拒绝）已实现但**零调用点**。
- B5（`entryType` vs `entry_type`）：TS 全链路 camelCase，且新架构下 C# 不向 TS 发送结构化记忆 → **无需修复，仅文档确认关闭**。
- C# 侧 `MemoryCommands.cs` 存在 remember/forget 命令（旧协议残留）；新架构下 remember/forget 由 TS 吸收，不下发 C#。本计划不删 C# 命令（无害死代码，P3 统一清理）。

---

## 文件结构映射（全部在 `<VALLEYAI_ROOT>\packages\stardew\`，除注明外）

| 文件 | 操作 | 职责 |
|------|------|------|
| `src\agent-memory.ts` | Modify | 新增 `removeMemory()`（模糊匹配）+ 计数合并去重 + `MemoryEntry.count` 持久化 |
| `src\stardew-tools.ts` | Modify | 注册第 9 个工具 `forget` |
| `src\stardew-agent-registry.ts` | Modify | 新增 per-NPC `pendingToolResults` 队列（C3 反馈暂存） |
| `src\protocol-adapter.ts` | Modify | `handleToolCallResult`/`handleActionResult` 路由到 Registry 队列（替换 ack-only） |
| `src\stardew-agent.ts` | Modify | 对话启动时取出并注入工具结果反馈；重要失败写记忆 |
| `src\prompt-builder.ts` | Modify | 模板新增"上次行动结果"段（有反馈时才出现） |
| `src\dialogue-handler.ts`（或 protocol-adapter 内 dialogue 路径） | Modify | 接线 `OutputValidator`：校验失败重试 1 次，再失败走 fallback |
| `tests\agent-memory.test.ts` | Modify | removeMemory + 计数合并 + count 持久化往返 |
| `tests\stardew-tools.test.ts` | Modify | 工具数 8→9；forget 行为（删除/拒绝删 SignificantMemory/无匹配） |
| `tests\tool-result-feedback.test.ts` | Create | C3 集成：action_result 入队 → 下次对话注入 → 队列清空 |
| `tests\output-validator-wired.test.ts` | Create | 校验器触发重试/fallback |
| `<REPO_ROOT>\src\ValleyAgent\Core\CommandExecutor.cs` | Verify/Modify | 确认执行 action 后回发 `action_result`；缺失则补 |

---

## Phase 1: B3 — forget 工具（TS）

### Task 1: AgentMemory.removeMemory（模糊匹配删除）

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\agent-memory.ts`
- Modify: `<VALLEYAI_ROOT>\packages\stardew\tests\agent-memory.test.ts`

- [ ] **Step 1: Write the failing test**

追加到 `agent-memory.test.ts`：

```typescript
test("removeMemory deletes matching short-term memory by substring", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("在矿洞捡了一块石头", 3, "event", "Mine", ["item"]);
  mem.addMemory("在农场收获了防风草", 3, "event", "Farm", ["crop"]);
  const removed = mem.removeMemory("石头");
  expect(removed).toBe(1);
  expect(mem.shortTermMemories.length).toBe(1);
  expect(mem.shortTermMemories[0]!.text).toContain("防风草");
});

test("removeMemory returns 0 when no match", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("在矿洞捡了一块石头", 3, "event", "Mine", []);
  expect(mem.removeMemory("不存在的事情")).toBe(0);
  expect(mem.shortTermMemories.length).toBe(1);
});

test("removeMemory never touches significantMemories", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addSignificantMemory("我和农场主第一次一起战斗了", "life_event", "joy", ["Farmer"], "Mine");
  const removed = mem.removeMemory("战斗");
  expect(removed).toBe(0);
  expect(mem.significantMemories.length).toBe(1);
});

test("removeMemory deletes all matches and returns count", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("捡了石头 A", 3, "event", "Mine", []);
  mem.addMemory("捡了石头 B", 3, "event", "Mine", []);
  mem.addMemory("收获了作物", 3, "event", "Farm", []);
  expect(mem.removeMemory("石头")).toBe(2);
  expect(mem.shortTermMemories.length).toBe(1);
});

test("removeMemory persists through save/load roundtrip", async () => {
  // 复用现有 makeTempDir 模式：add 3 条 → removeMemory 删 1 条 → save → load → 断言剩 2 条
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/agent-memory.test.ts`
Expected: FAIL with "mem.removeMemory is not a function"

- [ ] **Step 3: Write minimal implementation**

在 `AgentMemory` 类追加（**不**加入 `MemoryBackend` core 接口——forget 是 stardew 特化能力，core 接口保持稳定）：

```typescript
/**
 * 按子串模糊匹配删除短期记忆，返回删除条数。
 * 永不删除 significantMemories（AGENTS.md §3.3：forget 工具只能删普通记忆）。
 */
removeMemory(matchText: string): number {
  if (!matchText || matchText.trim() === "") return 0;
  const needle = matchText.trim();
  const before = this.shortTermMemories.length;
  this.shortTermMemories = this.shortTermMemories.filter(
    (m) => !m.text.includes(needle)
  );
  return before - this.shortTermMemories.length;
}
```

- [ ] **Step 4: Run test to verify it passes + typecheck**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/agent-memory.test.ts && cd packages/stardew && bun run typecheck`
Expected: PASS + 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/agent-memory.ts packages/stardew/tests/agent-memory.test.ts
git commit -m "feat(stardew): add AgentMemory.removeMemory with fuzzy substring match

B3 foundation. Never touches significantMemories per AGENTS.md 3.3 contract."
```

---

### Task 2: 注册 forget 工具

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\stardew-tools.ts`
- Modify: `<VALLEYAI_ROOT>\packages\stardew\tests\stardew-tools.test.ts`

- [ ] **Step 1: Write the failing test**

`stardew-tools.test.ts:33-41` 的工具数断言 8→9，追加：

```typescript
test("forget tool removes matching memory and reports count", async () => {
  const { tools, memory } = makeToolContext(); // 复用现有 helper
  memory.addMemory("捡了一块没用的石头", 2, "event", "Mine", []);
  const forget = tools.find(t => t.name === "forget")!;
  const result = await forget.execute({ memory_text: "石头", reason: "不重要的小事" });
  expect(result.success).toBe(true);
  expect(memory.shortTermMemories.length).toBe(0);
});

test("forget tool refuses to delete significant memories", async () => {
  const { tools, memory } = makeToolContext();
  memory.addSignificantMemory("农场主送了我最爱的紫水晶", "relationship", "joy", ["Farmer"], "Town");
  const forget = tools.find(t => t.name === "forget")!;
  const result = await forget.execute({ memory_text: "紫水晶", reason: "测试" });
  expect(memory.significantMemories.length).toBe(1);
  expect(result.message).toContain("重要");
});

test("forget tool reports when nothing matches", async () => {
  const { tools } = makeToolContext();
  const forget = tools.find(t => t.name === "forget")!;
  const result = await forget.execute({ memory_text: "不存在", reason: "测试" });
  expect(result.success).toBe(true);
  expect(result.message).toContain("没有找到");
});
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL — 工具数断言（8≠9）+ "forget is undefined"

- [ ] **Step 3: Write minimal implementation**

在 `buildStardewTools` 返回数组追加（参照 remember 的注册模式）：

```typescript
{
  name: "forget",
  visibility: "llm_visible",
  description: "删除一条不重要的短期记忆。只能删除普通记忆，重要的里程碑记忆无法删除。用于清理过时或错误的记忆。",
  parameters: Type.Object({
    memory_text: Type.String({ description: "要删除的记忆内容（部分匹配即可）" }),
    reason: Type.String({ description: "为什么要删除这条记忆" }),
  }),
  execute: async (args) => {
    const removed = ctx.memory.removeMemory(args.memory_text);
    if (removed === 0) {
      return { success: true, message: `没有找到包含"${args.memory_text}"的普通记忆（重要记忆无法删除）` };
    }
    return { success: true, message: `已删除 ${removed} 条记忆` };
  },
},
```

- [ ] **Step 4: Run tests + typecheck**

Expected: PASS（含工具数=9）+ 0 errors

- [ ] **Step 5: Commit**

```bash
git add packages/stardew/src/stardew-tools.ts packages/stardew/tests/stardew-tools.test.ts
git commit -m "feat(stardew): register forget tool (9th LLM-visible tool)

B3 closed. Agent can now proactively prune unimportant short-term memories;
SignificantMemory remains undeletable."
```

---

## Phase 2: C3 — 工具结果回注 LLM（TS + C# 验证）

**设计**：C# 执行 actions 后回发的 `action_result`/`tool_call_result` 携带 `{callId, success, result}`。TS 收到后存入 Registry 的 per-NPC 队列；该 NPC 下次对话时：
1. 队列内容格式化为"─── 上次行动结果 ───"段注入 system prompt（注入后清空队列）；
2. `success=false` 的结果同时写入 `AgentMemory` 短期记忆（importance 5，entryType="event"），保证跨会话不丢。
LLM 由此知道"上次送礼失败了，因为玩家背包满了"，不再盲目重复。

### Task 3: C# 侧 action_result 回发验证（先行确认项）

**Files:**
- Verify: `<REPO_ROOT>\src\ValleyAgent\Patches\DialogueBoxInputPatch.cs`（SubmitInput 消费 `response.Actions` 处）
- Verify: `<REPO_ROOT>\src\ValleyAgent\Core\CommandExecutor.cs`（ExecuteAction）
- Verify: `<REPO_ROOT>\src\ValleyAgent\Network\WebSocketClient.cs` 或 `StateSyncSender.cs`

- [ ] **Step 1: 确认 C# 是否在执行 action 后回发 `action_result` 消息**

搜索 `action_result` / `ActionResult` 在 C# 侧的发送点。
- **若已发送**：记录 file:line，本 Task 关闭，仅确保消息含 `npcName` 字段（TS 路由需要）。若缺 `npcName`，在 C# 消息 DTO 补上（TS `types.ts ActionResultMessage` 同步加字段）。
- **若未发送**：在 `CommandExecutor.ExecuteAction` 调用点（DialogueBoxInputPatch.SubmitInput 的 foreach 循环）收集每个 action 的 `{callId, success, result}`，全部执行完后一次性回发（每 action 一条消息）。`callId` 需从 TS 下发的 ToolAction 透传——检查 `ToolAction` record 是否含 `callId` 字段，没有则 C#/TS 双侧同步加（TS 在 stardew-agent.ts 汇总 actions 时生成 `callId = crypto.randomUUID()`）。

- [ ] **Step 2: Commit（如有改动）**

```bash
cd <REPO_ROOT>
git add src/ValleyAgent/
git commit -m "fix(mod): C3 send action_result to server after ExecuteAction

Each executed action reports {callId, success, result, npcName} so the TS server
can close the tool-result feedback loop."
```

---

### Task 4: Registry per-NPC 反馈队列

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\stardew-agent-registry.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\tool-result-feedback.test.ts`（新建）

- [ ] **Step 1: Write the failing test**

```typescript
import { test, expect } from "bun:test";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";

test("enqueueToolResult stores results per NPC and drainToolResults returns+clears", () => {
  const registry = makeRegistry(); // 复用现有测试构造
  registry.enqueueToolResult("Haley", { callId: "c1", tool: "give_gift", success: true, result: "已送出" });
  registry.enqueueToolResult("Haley", { callId: "c2", tool: "give_item", success: false, result: "玩家背包已满" });
  registry.enqueueToolResult("Abigail", { callId: "c3", tool: "emote", success: true, result: "ok" });

  const haleyResults = registry.drainToolResults("Haley");
  expect(haleyResults.length).toBe(2);
  expect(haleyResults[1]!.result).toContain("背包已满");
  // 第二次取应为空（已清空）
  expect(registry.drainToolResults("Haley").length).toBe(0);
  // Abigail 的不受 Haley drain 影响
  expect(registry.drainToolResults("Abigail").length).toBe(1);
});

test("queue is capped at 10 entries per NPC (oldest dropped)", () => {
  const registry = makeRegistry();
  for (let i = 0; i < 15; i++) {
    registry.enqueueToolResult("Haley", { callId: `c${i}`, tool: "emote", success: true, result: `r${i}` });
  }
  const results = registry.drainToolResults("Haley");
  expect(results.length).toBe(10);
  expect(results[0]!.callId).toBe("c5"); // 最老的 5 条被丢弃
});
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL with "enqueueToolResult is not a function"

- [ ] **Step 3: Write minimal implementation**

在 `StardewAgentRegistry` 追加：

```typescript
export interface ToolResultRecord {
  callId: string;
  tool: string;
  success: boolean;
  result?: string;
}

private readonly toolResultQueues = new Map<string, ToolResultRecord[]>();
private static readonly MAX_TOOL_RESULT_QUEUE = 10;

enqueueToolResult(npcName: string, record: ToolResultRecord): void {
  let queue = this.toolResultQueues.get(npcName);
  if (!queue) {
    queue = [];
    this.toolResultQueues.set(npcName, queue);
  }
  queue.push(record);
  if (queue.length > StardewAgentRegistry.MAX_TOOL_RESULT_QUEUE) {
    queue.splice(0, queue.length - StardewAgentRegistry.MAX_TOOL_RESULT_QUEUE);
  }
}

drainToolResults(npcName: string): ToolResultRecord[] {
  const queue = this.toolResultQueues.get(npcName);
  if (!queue || queue.length === 0) return [];
  this.toolResultQueues.delete(npcName);
  return queue;
}
```

- [ ] **Step 4: Run tests + typecheck**

- [ ] **Step 5: Commit**

```bash
git add packages/stardew/src/stardew-agent-registry.ts packages/stardew/tests/tool-result-feedback.test.ts
git commit -m "feat(stardew): per-NPC tool-result queue in StardewAgentRegistry

C3 foundation. Holds at most 10 pending results per NPC until next dialogue drains them."
```

---

### Task 5: ProtocolAdapter 路由结果到队列

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\protocol-adapter.ts`
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\types.ts`（消息加 `npcName`/`tool` 字段，若 C# 侧未发则标记 optional）
- Modify: `<VALLEYAI_ROOT>\packages\stardew\tests\tool-result-feedback.test.ts`

- [ ] **Step 1: Write the failing test**

```typescript
test("action_result message is routed to NPC tool-result queue", async () => {
  const { adapter, registry } = makeAdapterWithRegistry();
  await adapter.routeMessage({
    type: "action_result",
    requestId: "r1",
    callId: "c1",
    npcName: "Haley",
    success: false,
    result: "玩家背包已满",
  } as any);
  const results = registry.drainToolResults("Haley");
  expect(results.length).toBe(1);
  expect(results[0]!.success).toBe(false);
});

test("tool_call_result message is routed to NPC tool-result queue", async () => {
  // 同上，type: "tool_call_result"
});
```

- [ ] **Step 2: Run test to verify it fails**

Expected: 队列长度为 0（现状 ack-only）

- [ ] **Step 3: Write minimal implementation**

`protocol-adapter.ts:39-46` 替换为：

```typescript
async handleToolCallResult(req: ToolCallResultMessage): Promise<{ type: "ack"; requestId: string }> {
  this.routeToolResult(req);
  return { type: "ack", requestId: req.requestId };
}

async handleActionResult(req: ActionResultMessage): Promise<{ type: "ack"; requestId: string }> {
  this.routeToolResult(req);
  return { type: "ack", requestId: req.requestId };
}

private routeToolResult(req: { npcName?: string; callId: string; tool?: string; success: boolean; result?: string }): void {
  if (!req.npcName) return; // 旧 C# 版本未带 npcName：丢弃并记日志（升级后必带）
  this.registry.enqueueToolResult(req.npcName, {
    callId: req.callId,
    tool: req.tool ?? "unknown",
    success: req.success,
    result: req.result,
  });
}
```

`types.ts` 的 `ToolCallResultMessage`/`ActionResultMessage` 增加 `npcName?: string`、`tool?: string`。

- [ ] **Step 4: Run tests + typecheck**

- [ ] **Step 5: Commit**

```bash
git add packages/stardew/src/protocol-adapter.ts packages/stardew/src/types.ts packages/stardew/tests/tool-result-feedback.test.ts
git commit -m "feat(stardew): route tool_call_result/action_result into NPC feedback queue

Replaces P0 ack-only no-ops. C3 loop closed at ingress."
```

---

### Task 6: 对话时注入反馈 + 失败落记忆

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\stardew-agent.ts`
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\prompt-builder.ts`
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\protocol-adapter.ts`（把 drain 结果传入 runDialogue）
- Modify: `<VALLEYAI_ROOT>\packages\stardew\tests\tool-result-feedback.test.ts`

- [ ] **Step 1: Write the failing test**

```typescript
test("dialogue prompt includes pending tool results section and drains queue", async () => {
  const { adapter, registry, promptSpy } = makeFullStack();
  registry.enqueueToolResult("Haley", { callId: "c1", tool: "give_gift", success: false, result: "玩家背包已满" });
  await adapter.routeMessage(makeDialogueRequest("Haley", "再送我一次"));
  const systemPrompt = promptSpy.lastSystemPrompt;
  expect(systemPrompt).toContain("上次行动结果");
  expect(systemPrompt).toContain("玩家背包已满");
  expect(registry.drainToolResults("Haley").length).toBe(0);
});

test("failed tool results are written to short-term memory", async () => {
  const { adapter, memoryOf } = makeFullStack();
  await adapter.routeMessage({
    type: "action_result", requestId: "r1", callId: "c1", npcName: "Haley",
    tool: "give_gift", success: false, result: "玩家背包已满",
  } as any);
  await adapter.routeMessage(makeDialogueRequest("Haley", "你好"));
  const mem = memoryOf("Haley");
  expect(mem.shortTermMemories.some(m => m.text.includes("背包已满") && m.importance >= 4)).toBe(true);
});

test("no tool results → prompt has no action-result section", async () => {
  // 断言 lastSystemPrompt 不含 "上次行动结果"
});
```

- [ ] **Step 2: Run test to verify it fails**

- [ ] **Step 3: Write minimal implementation**

数据流改动：
1. `protocol-adapter.handleDialogue`：`const toolResults = this.registry.drainToolResults(req.npcName);` 传给 `agent.runDialogue(..., toolResults)`。
2. `stardew-agent.runDialogue`：
   - 成功/失败结果格式化为文本行：`"give_gift：失败 — 玩家背包已满"`；
   - 失败项写记忆：`memory.addMemory(`我尝试${r.tool}但失败了：${r.result}`, 5, "event", scene.location, ["tool_result"])`；
   - 文本段传给 `promptBuilder.buildDialogueSystemPrompt(memory, scene, npcName, toolResultsText)`。
3. `prompt-builder.ts`：模板在"最近对话"段后加可选段：
   ```
   ─── 上次行动结果 ───
   {tool_results}
   ```
   无结果时整段省略（不留空标题）。

- [ ] **Step 4: Run tests + typecheck + 全量 bun test（覆盖率门）**

- [ ] **Step 5: Commit**

```bash
git add packages/stardew/src/
git commit -m "feat(stardew): inject tool results into next dialogue prompt + persist failures

C3 closed. LLM now sees outcomes of its previous actions ('give_gift failed: inventory full')
and failures are persisted to short-term memory across sessions."
```

---

## Phase 3: B4 — 短期记忆计数合并（TS）

**设计**：相同文本的记忆不再按"60 秒窗外重复添加"，而是**永远合并**：`count++`、刷新 timestamp、importance 微升（+0.5，封顶 10）。展示时 `count>1` 显示为"……（×17）"。45 条记忆 3 种文本的灌水场景（FINDINGS B4）收敛为 3 条带计数的记忆。`MemoryEntry` 增加 `count: number`（默认 1），持久化 schema 同步（旧文件无 count 字段 → load 时补 1，向后兼容）。

### Task 7: MemoryEntry.count + addMemory 合并逻辑

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\core\src\memory-backend.ts`（`MemoryEntry` 加 `count`）
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\agent-memory.ts`
- Modify: `<VALLEYAI_ROOT>\packages\stardew\tests\agent-memory.test.ts`

- [ ] **Step 1: Write the failing test**

```typescript
test("addMemory merges exact-text duplicates into count regardless of age", () => {
  const mem = new AgentMemory("Haley", "/tmp/x");
  mem.addMemory("收到了农场主的礼物", 4, "gift", "Town", []);
  // 模拟 61 秒前（超出旧 60s 去重窗）
  mem.shortTermMemories[0]!.timestamp = (Date.now() / 1000) - 61;
  mem.addMemory("收到了农场主的礼物", 4, "gift", "Town", []);
  expect(mem.shortTermMemories.length).toBe(1);
  expect(mem.shortTermMemories[0]!.count).toBe(2);
});

test("merge refreshes timestamp and slightly bumps importance", () => {
  const mem = new AgentMemory("Haley", "/tmp/x");
  mem.addMemory("收到了礼物", 4, "gift", "Town", []);
  const oldTs = mem.shortTermMemories[0]!.timestamp = (Date.now() / 1000) - 3600;
  mem.addMemory("收到了礼物", 4, "gift", "Town", []);
  const entry = mem.shortTermMemories[0]!;
  expect(entry.timestamp).toBeGreaterThan(oldTs);
  expect(entry.importance).toBeCloseTo(4.5, 1);
});

test("importance bump is capped at 10", () => {
  const mem = new AgentMemory("Haley", "/tmp/x");
  mem.addMemory("重大事件", 9.8, "event", "Town", []);
  mem.addMemory("重大事件", 9.8, "event", "Town", []);
  expect(mem.shortTermMemories[0]!.importance).toBe(10);
});

test("different text still creates separate entries", () => {
  const mem = new AgentMemory("Haley", "/tmp/x");
  mem.addMemory("收到了礼物", 4, "gift", "Town", []);
  mem.addMemory("捡了石头", 4, "event", "Mine", []);
  expect(mem.shortTermMemories.length).toBe(2);
});

test("count persists through save/load roundtrip", async () => {
  // add ×3 同文 → save → load → count===3
});

test("old memory file without count field loads with count=1", async () => {
  // 手写无 count 的旧 schema JSON → load → count===1
});

test("getRecentMemories renders count suffix for merged entries", () => {
  const mem = new AgentMemory("Haley", "/tmp/x");
  mem.addMemory("收到了礼物", 4, "gift", "Town", []);
  mem.addMemory("收到了礼物", 4, "gift", "Town", []);
  expect(mem.getRecentMemories(5)).toContain("（×2）");
});
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL（`count` 属性不存在 / 合并行为不存在）

- [ ] **Step 3: Write minimal implementation**

`memory-backend.ts`：`MemoryEntry` 加 `count: number; // 合并计数，默认 1`。

`agent-memory.ts addMemory` 重写去重段：

```typescript
addMemory(text, importance = 1.0, entryType = "generic", location = "", tags = []): void {
  const now = Date.now() / 1000;
  // B4：同文记忆永远合并（不再限 60s 窗口）
  const existing = this.shortTermMemories.find((m) => m.text === text);
  if (existing) {
    existing.count += 1;
    existing.timestamp = now;
    existing.importance = Math.min(10, existing.importance + 0.5);
    return;
  }
  this.shortTermMemories.push({ text, timestamp: now, importance: clamp(importance), entryType, location, tags, count: 1 });
  // MAX_SHORT_TERM 淘汰逻辑不变
}

// getRecentMemories / 展示处：
// const suffix = m.count > 1 ? `（×${m.count}）` : "";
// line = `${m.text}${suffix}`

// load()：parsed.shortTermMemories.map(m => ({ count: 1, ...m }))  // 旧文件兼容
```

注意 `DEDUP_WINDOW_SECONDS` 常量删除；`addMemory` 内联注释更新。

- [ ] **Step 4: Run tests + typecheck + core 包测试回归**

Run: `cd <VALLEYAI_ROOT> && bun test packages/core/tests/memory-backend.test.ts packages/stardew/tests/agent-memory.test.ts`
Expected: PASS（core 接口测试需同步加 `count` 字段——修改 `memory-backend.test.ts` 的 fakeMemory 构造）

- [ ] **Step 5: Commit**

```bash
git add packages/core/src/memory-backend.ts packages/core/tests/memory-backend.test.ts packages/stardew/src/agent-memory.ts packages/stardew/tests/agent-memory.test.ts
git commit -m "feat(stardew): B4 count-merge duplicate short-term memories

Same-text memories now always merge (count++, timestamp refresh, +0.5 importance capped at 10)
instead of re-adding after a 60s window. '收到礼物×17' flooding collapses to one entry.
Old memory files load with count=1 (backward compatible)."
```

---

## Phase 4: C1 补强 — OutputValidator 接线（TS）

**背景**：C1（残缺 `[ACTION:]` 乱码）在 P0 已通过"TS 结构化工具调用、C# 不再解析文本标签"根除。`OutputValidator`（CJK≥30%、空回复拒绝）已实现但零调用点——LLM 输出仍可能语言跑偏或空 speech 直达玩家。本 Task 接线：speech 校验失败 → 带原因重试 1 次 → 再失败走 RuleEngine fallback。

### Task 8: 对话路径接线 OutputValidator

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\protocol-adapter.ts`（或 `dialogue-handler.ts`，以 P0 实际结构为准）
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\output-validator-wired.test.ts`（新建）

- [ ] **Step 1: Write the failing test**

```typescript
test("speech failing CJK validation triggers one retry then succeeds", async () => {
  const { adapter, llmCallCount } = makeStackWithLlmSequence([
    englishOnlyResponse,   // 第一次：英文，CJK 比例不足
    validChineseResponse,  // 第二次：合格
  ]);
  const resp = await adapter.routeMessage(makeDialogueRequest("Haley", "你好"));
  expect(llmCallCount()).toBe(2);
  expect(resp.speech).toContain("你");  // 中文回复
});

test("two consecutive validation failures fall back to RuleEngine response", async () => {
  const { adapter } = makeStackWithLlmSequence([englishOnlyResponse, englishOnlyResponse]);
  const resp = await adapter.routeMessage(makeDialogueRequest("Haley", "你好"));
  expect(resp.fallback).toBe(true);
});

test("empty speech is rejected and retried", async () => {
  const { adapter, llmCallCount } = makeStackWithLlmSequence([emptySpeechResponse, validChineseResponse]);
  const resp = await adapter.routeMessage(makeDialogueRequest("Haley", "你好"));
  expect(llmCallCount()).toBe(2);
});
```

- [ ] **Step 2: Run test to verify it fails**

Expected: llmCallCount===1（校验器未接线，英文/空 speech 直接通过）

- [ ] **Step 3: Write minimal implementation**

在 dialogue 响应构建路径（`handleDialogueWithFallback` 或等价处）：

```typescript
const validation = this.outputValidator.validateSpeech(extracted.speech, scene);
if (!validation.ok) {
  // 重试 1 次：把失败原因告知 LLM
  const retryPrompt = this.outputValidator.buildRetryPrompt(validation.reason);
  extracted = await runAgentLoop(agent, memory, scene, req.playerInput, retryPrompt);
  const revalidation = this.outputValidator.validateSpeech(extracted.speech, scene);
  if (!revalidation.ok) {
    return buildFallbackResponse(req, memory, new Error(`validation: ${revalidation.reason}`));
  }
}
```

`OutputValidator` 现有 API 以实际签名为准（`validateSpeech`/`buildRetryPrompt` 若不存在则按现有方法适配，缺失则补最小方法）。

- [ ] **Step 4: Run tests + typecheck + 全量 bun test**

- [ ] **Step 5: Commit**

```bash
git add packages/stardew/src/ packages/stardew/tests/output-validator-wired.test.ts
git commit -m "feat(stardew): wire OutputValidator into dialogue path with one retry

C1 hardening. CJK-ratio/empty-speech failures retry once, then RuleEngine fallback."
```

---

## 测试与验收

```bash
# TS 全量（bunfig.toml 强制 80% 行覆盖）
cd <VALLEYAI_ROOT>
bun test
bun run typecheck
```

| 验收项 | 验证 |
|--------|------|
| B3 | forget 工具注册（9 工具）；删普通记忆/拒删 SignificantMemory/无匹配三路径测试通过 |
| C3 | 集成测试：action_result 入队 → 下次对话 prompt 含"上次行动结果"→ 队列清空；失败结果写短期记忆（importance≥4） |
| B4 | 同文记忆合并（×N）、timestamp 刷新、importance 微升封顶 10、count 持久化往返、旧文件兼容 |
| C1 补强 | 校验失败重试 1 次→成功；连续失败→fallback；空 speech 拒绝 |
| B5 | **文档关闭**：TS 全链路 camelCase `entryType`，新架构 C# 不发送结构化记忆，无失配面 |
| C# | `action_result` 回发确认/补发（Task 3） |

### 游戏内 E2E（TestMod，可选但推荐）

| 用例 | 验证点 |
|------|-------|
| 对话遗忘 | 玩家让 NPC"忘掉刚才说的"→ NPC 调 forget → 短期记忆减少 |
| 行动反馈 | 玩家背包满时让 NPC 送礼 → 失败后追问 → NPC 回答提及"背包满了"（记忆闭环实测） |

---

## 风险与缓解

| 风险 | 缓解 |
|------|------|
| 同文合并让 NPC"记不住两次不同的送礼"（同文本不同语境） | 合并刷新 timestamp + importance 微升，等价于"这件事反复发生所以记得更牢"——符合直觉；语境差异大的事件文本通常不同 |
| C# 旧版本不带 npcName 的 action_result 被丢弃 | routeToolResult 记日志；Task 3 保证 C# 同步升级；协议版本在 hello 握手可后续加 |
| 反馈队列注入占 prompt 长度 | 队列上限 10 条/格式化单行；失败落记忆后即使队列溢出也有记忆兜底 |
| OutputValidator 重试增加延迟 | 仅校验失败时重试（实测 MiniMax-M2 中文输出稳定，触发率低）；重试共享 30s LLM 超时 |

---

## 待用户决策的开放问题 — ✅ 已于 2026-07-18 全部拍板

1. **B4 合并强度**：**同文永远合并**（Task 7 按此设计）。
2. **forget 边界**：**只允许删除短期不重要的普通记忆**（Task 1/2 按此设计）；SignificantMemory 与对话历史不可删。
3. **工具结果注入位置**：**system prompt"上次行动结果"段**（Task 6 按此设计）。
4. **已批准的后续方向（P4 候选，本计划不实现）**：用户提出双 Agent 记忆管理——主 Agent 运行，第二个"记忆管理员"Agent 专门为主 Agent 裁剪/压缩记忆（可挂 `consolidate_day` 日终流程，输入短期记忆→输出压缩摘要+删除清单）。立项时另出设计文档。
