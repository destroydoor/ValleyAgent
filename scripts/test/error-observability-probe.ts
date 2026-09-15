/**
 * 错误可观测性实机探针（2026-09-15 异常处理审计的证据脚本）。
 *
 * 用途：用**真实 TS Agent Server**（不是 mock）验证四件事——
 *   ① 未知/畸形消息是否被静默 ack（协议漂移的可观测性）
 *   ② handler 内部抛错时，回给 C# 的 error 帧有没有 requestId（C# 能否快速失败）
 *   ③ ValleyAgent-server.log（发行包唯一持久化现场）里错误详情是否被 log-tee
 *      的 JSON.stringify(Error) === "{}" 销毁
 *   ④ TS 侧代码缺陷（worldSnapshot 解码抛 TypeError）是否被误报成 fallbackReason="llm_error"
 *
 * 跑法（需要 bun；不会调真实 LLM——llmCallOverride 顶替）：
 *   cd server && bun install
 *   bun ../scripts/test/error-observability-probe.ts
 *
 * 判据：控制台打印的 reply 与落盘日志逐条对照审计文档 §附录 A 的"实测结果"。
 * 修复后本脚本应显示：落盘日志含错误 message+stack、error 帧带 requestId、
 * 未知类型有 WARN 日志、代码缺陷不再标 llm_error。
 */
import { mkdirSync, readFileSync, rmSync } from "node:fs";
import { join } from "node:path";

const LOG_FILE = join(import.meta.dir, "..", "..", ".probe-server.log");
const AGENTS_DIR = join(import.meta.dir, "..", "..", ".probe-agents");
process.env.VALLEY_SERVER_LOGFILE = LOG_FILE; // 必须在 import server.ts 之前设置

rmSync(LOG_FILE, { force: true });
rmSync(AGENTS_DIR, { recursive: true, force: true });
mkdirSync(AGENTS_DIR, { recursive: true });

const { startServer } = await import("../../server/packages/stardew/src/server.ts");

const handle = await startServer({
  port: 18765,
  hostname: "127.0.0.1",
  dataPath: join(import.meta.dir, "..", "..", "server", "packages", "stardew", "data", "npc_prompts.json"),
  agentsDir: AGENTS_DIR,
  llmConfig: { provider: "minimax", apiKey: "probe-key", model: "MiniMax-M2", baseUrl: "https://example.invalid/v1" },
  llmCallOverride: async () => ({
    content: "（探针）",
    toolCalls: [{ id: "c1", name: "speak", args: { text: "你好呀" } }],
  }),
});

const ws = new WebSocket("ws://127.0.0.1:18765");
await new Promise<void>((resolve) => { ws.onopen = () => resolve(); });
const replies: unknown[] = [];
ws.onmessage = (ev) => replies.push(JSON.parse(String(ev.data)));
const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));
const last = () => JSON.stringify(replies.at(-1));

console.log("=== 探针 1：未知消息类型（模拟 C# 发了 TS 不认识的 type / 协议漂移）===");
ws.send(JSON.stringify({ type: "consolidate_day", requestId: "req-unknown-1" }));
await wait(150);
console.log("   reply =", last(), " ← 注意日志文件里有没有这条的记录");

console.log("=== 探针 2：payload 为 null（畸形帧）===");
ws.send("null");
await wait(150);
console.log("   reply =", last(), " ← 无 requestId：C# 侧只能等 120s 超时");

console.log("=== 探针 3：reconnect_sync 缺 agents 字段（handler 内部抛 TypeError）===");
ws.send(JSON.stringify({ type: "reconnect_sync", requestId: "req-3", replayedOutbox: 0 }));
await wait(200);
console.log("   reply =", last());

console.log("=== 探针 4：dialogue 缺 worldSnapshot（TS 代码缺陷 → 玩家侧降级）===");
ws.send(JSON.stringify({ type: "dialogue", requestId: "req-4", npcName: "Haley", playerInput: "你好", playerId: "1" }));
await wait(400);
console.log("   reply =", last(), " ← fallbackReason 是否把代码缺陷说成 llm_error");

console.log(`\n---- ${LOG_FILE}（发行包 ServerConsoleWindow=true 模式下唯一的持久化现场）----`);
console.log(readFileSync(LOG_FILE, "utf8"));

ws.close();
await handle.stop();
rmSync(AGENTS_DIR, { recursive: true, force: true });
rmSync(LOG_FILE, { force: true }); // 内容已打印到 stdout，不在仓库留残留
process.exit(0);
