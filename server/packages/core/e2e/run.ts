/**
 * E2E run entry: drives the player script against both providers,
 * records all dialogue and tool executions to a log file for review.
 *
 * Main test uses makeLlmCallDirect (direct fetch) for both providers so the
 * comparison is fair and avoids the v5 SDK endpoint issue. A VercelAIProvider
 * smoke probe is also run and recorded to document framework compatibility.
 *
 * Usage:
 *   $env:DEEPSEEK_API_KEY="sk-..."; $env:MINIMAX_API_KEY="sk-...";
 *   bun run packages/core/e2e/run.ts
 */
import { mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import {
  buildProvider,
  buildAgent,
  PROVIDER_CONFIGS,
  formatEvent,
  type ProviderKey,
  type ProviderRunConfig,
} from "./agent-setup";
import {
  createWorld,
  buildTools,
  PLAYER_SCRIPT,
  type WorldState,
} from "./world";
import type { AgentMessage, AgentEvent, AgentToolCall } from "../src/types";

interface TurnLog {
  turnIndex: number;
  intent: string;
  playerMessage: string;
  expectedTools: string[];
  notes: string;
  events: AgentEvent[];
  assistantFinal: string;
  assistantToolCalls: AgentToolCall[];
  toolCalls: Array<{
    name: string;
    args: Record<string, unknown>;
    result: unknown;
    isError: boolean;
  }>;
  durationMs: number;
  error?: string;
}

interface ProviderRunLog {
  provider: ProviderKey;
  label: string;
  turns: TurnLog[];
  worldFinal: WorldState;
  totalDurationMs: number;
  totalToolCalls: number;
  error?: string;
}

interface SmokeProbeResult {
  provider: ProviderKey;
  ok: boolean;
  error?: string;
  latencyMs: number;
  contentPreview: string;
}

const LOG_PATH = join(import.meta.dir, "..", "..", "..", "..", "docs", "valley-core-e2e-log.md");

function extractFinalAssistant(events: AgentEvent[]): {
  content: string;
  toolCalls: AgentToolCall[];
} {
  let content = "";
  for (const e of events) {
    if (e.type === "message_end") content = e.content;
  }
  const toolCalls: AgentToolCall[] = [];
  for (const e of events) {
    if (e.type === "tool_call_start") {
      toolCalls.push({ id: e.toolCallId, name: e.toolName, args: e.args });
    }
  }
  return { content, toolCalls };
}

function extractToolResults(events: AgentEvent[]): TurnLog["toolCalls"] {
  const argsById = new Map<string, Record<string, unknown>>();
  for (const e of events) {
    if (e.type === "tool_call_start") argsById.set(e.toolCallId, e.args);
  }
  const out: TurnLog["toolCalls"] = [];
  for (const e of events) {
    if (e.type === "tool_call_end") {
      out.push({
        name: e.toolName,
        args: argsById.get(e.toolCallId) ?? {},
        result: e.result,
        isError: e.isError,
      });
    }
  }
  return out;
}

async function runProvider(runCfg: ProviderRunConfig): Promise<ProviderRunLog> {
  const world = createWorld();
  const tools = buildTools(world);

  const turns: TurnLog[] = [];
  const runStart = Date.now();
  let totalToolCalls = 0;
  const history: AgentMessage[] = [];

  for (const script of PLAYER_SCRIPT) {
    const turnStart = Date.now();
    history.push({ role: "user", content: script.message });
    const context = {
      messages: [...history],
      systemPrompt: "",
      metadata: { turn: script.index, intent: script.intent },
    };

    const turnLog: TurnLog = {
      turnIndex: script.index,
      intent: script.intent,
      playerMessage: script.message,
      expectedTools: script.expectedTools,
      notes: script.notes ?? "",
      events: [],
      assistantFinal: "",
      assistantToolCalls: [],
      toolCalls: [],
      durationMs: 0,
    };

    try {
      const { agent } = buildAgent(world, tools, runCfg);
      const stream = agent.prompt(context);
      const allEvents = await stream.awaitAll();
      await new Promise((r) => setTimeout(r, 0));

      turnLog.events = allEvents;
      const { content, toolCalls } = extractFinalAssistant(allEvents);
      turnLog.assistantFinal = content;
      turnLog.assistantToolCalls = toolCalls;
      turnLog.toolCalls = extractToolResults(allEvents);
      totalToolCalls += turnLog.toolCalls.length;
      history.push({
        role: "assistant",
        content,
        ...(toolCalls.length > 0 ? { toolCalls } : {}),
      });
    } catch (err) {
      turnLog.error = err instanceof Error ? err.message : String(err);
      history.push({ role: "assistant", content: `[ERROR] ${turnLog.error}` });
    }

    turnLog.durationMs = Date.now() - turnStart;
    turns.push(turnLog);
    console.log(
      `  [${runCfg.provider}] turn ${script.index} done (${turnLog.durationMs}ms, ${turnLog.toolCalls.length} tools)${turnLog.error ? " ERR" : ""}`
    );
  }

  return {
    provider: runCfg.provider,
    label: runCfg.label,
    turns,
    worldFinal: world,
    totalDurationMs: Date.now() - runStart,
    totalToolCalls,
  };
}

async function probeVercelProvider(
  runCfg: ProviderRunConfig
): Promise<SmokeProbeResult> {
  const start = Date.now();
  try {
    const provider = buildProvider(runCfg);
    const resp = await provider.chatCompletion([
      { role: "user", content: "回复一个字：好" },
    ]);
    return {
      provider: runCfg.provider,
      ok: true,
      latencyMs: Date.now() - start,
      contentPreview: resp.content.slice(0, 60),
    };
  } catch (e) {
    return {
      provider: runCfg.provider,
      ok: false,
      error: e instanceof Error ? e.message.slice(0, 200) : String(e).slice(0, 200),
      latencyMs: Date.now() - start,
      contentPreview: "",
    };
  }
}

function renderLog(
  runs: ProviderRunLog[],
  smoke: SmokeProbeResult[]
): string {
  const lines: string[] = [];
  lines.push(`# @valley/core E2E 测试日志`);
  lines.push("");
  lines.push(`- 生成时间：${new Date().toISOString()}`);
  lines.push(`- 框架版本：@valley/core 0.1.0`);
  lines.push(`- 测试场景：山谷守谷老者 Elden 与旅行者（玩家）互动`);
  lines.push(`- 玩家由测试脚本扮演（6 轮固定剧本）`);
  lines.push(`- 主测试 llmCall 实现：直接 fetch（OpenAI 兼容端点）`);
  lines.push(`- 测试 Provider：${runs.map((r) => r.label).join(" / ")}`);
  lines.push("");
  lines.push("---");
  lines.push("");

  // VercelAIProvider smoke probe section
  lines.push(`## VercelAIProvider 冒烟测试（框架兼容性）`);
  lines.push("");
  lines.push(`用 VercelAIProvider.chatCompletion 发送一句最简请求，验证框架自带的 provider 能否调通各 LLM。主测试因 v5 SDK 端点问题改用直接 fetch。`);
  lines.push("");
  lines.push(`| Provider | 结果 | 耗时(ms) | 错误/预览 |`);
  lines.push(`|----------|------|---------|----------|`);
  for (const s of smoke) {
    const status = s.ok ? "成功" : "失败";
    const detail = s.ok ? `预览: ${s.contentPreview}` : `错误: ${s.error ?? ""}`;
    lines.push(`| ${s.provider} | ${status} | ${s.latencyMs} | ${detail} |`);
  }
  lines.push("");
  lines.push("---");
  lines.push("");

  for (const run of runs) {
    lines.push(`## ${run.label}`);
    lines.push("");
    if (run.error) {
      lines.push(`> 整体错误：${run.error}`);
      lines.push("");
      continue;
    }
    lines.push(`- 总耗时：${run.totalDurationMs} ms`);
    lines.push(`- 总工具调用：${run.totalToolCalls} 次`);
    lines.push("");
    for (const t of run.turns) {
      lines.push(`### 第 ${t.turnIndex} 轮 — ${t.intent}`);
      lines.push("");
      lines.push(`- 预期工具：${t.expectedTools.join(", ") || "无"}`);
      lines.push(`- 测试说明：${t.notes}`);
      lines.push(`- 耗时：${t.durationMs} ms`);
      if (t.error) lines.push(`- !! 错误：${t.error}`);
      lines.push("");
      lines.push(`**玩家：**`);
      lines.push(`> ${t.playerMessage}`);
      lines.push("");
      lines.push(`**NPC 最终回复：**`);
      lines.push(`> ${t.assistantFinal || "(无文本回复)"}`);
      lines.push("");
      if (t.toolCalls.length > 0) {
        lines.push(`**工具调用（${t.toolCalls.length} 次）：**`);
        for (const tc of t.toolCalls) {
          const flag = tc.isError ? " !ERROR" : "";
          lines.push(`- \`${tc.name}\`(${JSON.stringify(tc.args)})${flag}`);
          const resultText =
            typeof tc.result === "object" &&
            tc.result !== null &&
            "content" in tc.result
              ? String((tc.result as { content: unknown }).content)
              : String(tc.result);
          lines.push(`  - 结果：${resultText}`);
        }
        lines.push("");
      } else {
        lines.push(`**工具调用：** 无`);
        lines.push("");
      }
      lines.push(`<details><summary>事件流（${t.events.length} 个事件）</summary>`);
      lines.push("");
      lines.push("```");
      for (const e of t.events) lines.push(formatEvent(e));
      lines.push("```");
      lines.push("");
      lines.push(`</details>`);
      lines.push("");
    }
    lines.push(`### 最终世界状态`);
    lines.push("");
    lines.push(`- NPC 物品栏：${run.worldFinal.inventory.map((i) => `${i.name}x${i.quantity}`).join(", ") || "(空)"}`);
    lines.push(`- 赠予玩家：${run.worldFinal.givenToPlayer.map((i) => `${i.name}x${i.quantity}`).join(", ") || "(无)"}`);
    lines.push(`- 任务板：${run.worldFinal.questBoard.length} 个任务`);
    lines.push("");
    lines.push("---");
    lines.push("");
  }

  // Comparison summary
  lines.push(`## 对比摘要`);
  lines.push("");
  lines.push(`| Provider | 总耗时(ms) | 总工具调用 | 完成轮数 | 整体错误 |`);
  lines.push(`|----------|-----------|-----------|---------|---------|`);
  for (const r of runs) {
    lines.push(
      `| ${r.provider} | ${r.totalDurationMs} | ${r.totalToolCalls} | ${r.turns.length} | ${r.error ?? "无"} |`
    );
  }
  lines.push("");
  lines.push(`| Provider | 各轮工具命中(实际/预期) |`);
  lines.push(`|----------|------------|`);
  for (const r of runs) {
    const hit = r.turns
      .map((t) => {
        const expected = t.expectedTools;
        const actual = t.toolCalls.map((c) => c.name);
        const matched = expected.filter((e) => actual.includes(e)).length;
        return `T${t.turnIndex}:${matched}/${expected.length}`;
      })
      .join(" ");
    lines.push(`| ${r.provider} | ${hit} |`);
  }
  lines.push("");

  return lines.join("\n");
}

async function runOne(
  key: ProviderKey,
  apiKey: string
): Promise<ProviderRunLog> {
  const cfg: ProviderRunConfig = { ...PROVIDER_CONFIGS[key], apiKey };
  console.log(`Running ${cfg.label}...`);
  try {
    return await runProvider(cfg);
  } catch (err) {
    return {
      provider: key,
      label: cfg.label,
      turns: [],
      worldFinal: createWorld(),
      totalDurationMs: 0,
      totalToolCalls: 0,
      error: err instanceof Error ? err.message : String(err),
    };
  }
}

async function main() {
  const deepseekKey = process.env.DEEPSEEK_API_KEY;
  const minimaxKey = process.env.MINIMAX_API_KEY;
  if (!deepseekKey || !minimaxKey) {
    console.error(
      "Missing API keys. Set DEEPSEEK_API_KEY and MINIMAX_API_KEY env vars."
    );
    process.exit(1);
  }

  // 1. VercelAIProvider smoke probe (records framework compatibility).
  const smoke: SmokeProbeResult[] = [];
  for (const key of ["deepseek", "minimax"] as const) {
    const cfg: ProviderRunConfig = { ...PROVIDER_CONFIGS[key], apiKey: key === "deepseek" ? deepseekKey : minimaxKey };
    console.log(`Smoke probe ${cfg.label}...`);
    const s = await probeVercelProvider(cfg);
    console.log(`  -> ${s.ok ? "ok" : "FAIL"} (${s.latencyMs}ms)`);
    smoke.push(s);
  }

  // 2. Main test (direct fetch llmCall, both providers).
  const runs: ProviderRunLog[] = [];
  runs.push(await runOne("deepseek", deepseekKey));
  runs.push(await runOne("minimax", minimaxKey));

  const log = renderLog(runs, smoke);
  mkdirSync(dirname(LOG_PATH), { recursive: true });
  await Bun.write(LOG_PATH, log);
  console.log(`\nLog written to ${LOG_PATH}`);
  console.log("\n=== Summary ===");
  for (const r of runs) {
    console.log(
      `${r.label}: ${r.turns.length} turns, ${r.totalToolCalls} tool calls, ${r.totalDurationMs}ms${r.error ? " ERROR: " + r.error : ""}`
    );
  }
  console.log("Smoke probe:");
  for (const s of smoke) {
    console.log(`  ${s.provider}: ${s.ok ? "ok" : "FAIL"} (${s.latencyMs}ms)`);
  }
}

main().catch((err) => {
  console.error("Fatal:", err);
  process.exit(1);
});
