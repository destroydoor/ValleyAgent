/**
 * Agent 事件流卡死复现（2026-09-15 异常处理审计 §3.8 / 猜想 C5 的终验脚本）。
 *
 * 验证命题：`core/src/agent.ts` 的 `stream.awaitAll().then(...)` 没有 `.catch()`，
 * 因此**任一订阅者抛异常**（或回放循环内抛异常）会导致：
 *   ① proxy stream 永不 done() → `StardewAgent.runOnce` 的 await 永久挂起
 *      → `ProtocolAdapter.handleDialogue` 的 `finally { releaseLock() }` 永不执行
 *      → 该 NPC 之后所有对话都在 acquireLock(15s) 后回 BUSY，直到重启 TS 服务器；
 *   ② `activeRun` 永不清空、`getState().errorMessage` 仍为 null（错误连状态里都看不到）；
 *   ③ 该 rejection 只能由 cli.ts 的全局 unhandledRejection 兜住，而它写进
 *      ValleyAgent-server.log 的是 `{}`（见 error-observability-probe.ts / 审计 §3.6）。
 *
 * 跑法：cd server && bun install && bun ../scripts/test/agent-wedge-repro.ts
 * 判据：输出 `awaitAll-HUNG` + `isIdle(): false` = 缺陷复现；
 *       修复后应为 `awaitAll-RESOLVED`（或带 error 事件的正常终态）+ `isIdle(): true`。
 *
 * 说明：生产代码里现有两个订阅者（ConsoleLogSubscriber / RunTranscriptRecorder）都自带
 * try/catch，所以这是**已实测可达但当前未被触发**的卡死面（一次无心的订阅者改动即引爆）。
 */
import { Agent } from "../../server/packages/core/src/agent";
import { ToolRegistry } from "../../server/packages/core/src/tool-registry";
import type { AgentLoopConfig } from "../../server/packages/core/src/agent-loop";
import type { AgentContext } from "../../server/packages/core/src/types";

const loopConfig: AgentLoopConfig = {
  tools: new ToolRegistry(),
  convertToLlm: () => ({ messages: [{ role: "user", content: "hi" }] }),
  llmCall: async () => ({ content: "你好" }),
  maxTurns: 1,
};

const emptyContext: AgentContext = { messages: [], systemPrompt: "", metadata: {} };

const agent = new Agent("wedge-test", loopConfig);
// 模拟"未自防的订阅者"：留痕/日志/统计类订阅者一旦忘了 try/catch 就是这个形状
agent.subscribe(() => { throw new Error("subscriber boom"); });

const stream = agent.prompt(emptyContext);
const outcome = await Promise.race([
  stream.awaitAll().then(() => "awaitAll-RESOLVED"),
  new Promise<string>((resolve) => setTimeout(() => resolve("awaitAll-HUNG (>1s)"), 1000)),
]);

console.log("proxy stream  :", outcome);
console.log("agent.isIdle():", agent.isIdle(), " ← false = activeRun 永不清空");
console.log("errorMessage  :", agent.getState().errorMessage, " ← null = 错误连状态里都看不见");
try {
  agent.prompt(emptyContext);
  console.log("二次 prompt   : 成功");
} catch (err) {
  console.log("二次 prompt   : 抛错 →", (err as Error).message);
}
process.exit(outcome === "awaitAll-HUNG (>1s)" ? 1 : 0);
