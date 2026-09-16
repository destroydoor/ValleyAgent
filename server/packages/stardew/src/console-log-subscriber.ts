// ConsoleLogSubscriber — 订阅 Agent 事件流，把每轮决策/LLM输出/工具调用
// 实时打到 stdout（完整原始，不截断）。与 RunTranscriptRecorder 同机制：
// Agent.subscribe 在 run 结束后全量 replay 事件（agent.ts:88-94），所以日志
// 在 run 结束后按事件顺序批量输出，非逐事件实时流。
//
// 约束（仿 transcript-recorder 防御）：
//   - 所有输出 best-effort，try/catch 包裹，绝不向上抛、不阻塞事件流。
//   - 不修改 @valley/core：只消费 Agent.subscribe 的事件。

import type { Agent, AgentEvent } from "@valley/core";

/** 生成 [HH:MM:SS.mmm] 时间戳。与 protocol-adapter 的 timestamp() 同格式。 */
function timestamp(): string {
  const d = new Date();
  const pad = (n: number, l = 2) => String(n).padStart(l, "0");
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

/** safe-stringify：防循环引用导致 JSON.stringify 抛错。 */
function safeStringify(value: unknown): string {
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

/**
 * 订阅 Agent 事件流并打 stdout 日志。
 * 每个 runOnce（首次 + 校验重试）都要 attach —— 每次都 new 一个 Agent，
 * 订阅必须跟着新 Agent 走（与 RunTranscriptRecorder.attach 一致）。
 */
export class ConsoleLogSubscriber {
  /** turnIndex → turn_start.timestamp，用于算 turn 级耗时。 */
  private readonly turnStarts = new Map<number, number>();

  constructor(private readonly npcName: string) {}

  attach(agent: Agent): () => void {
    try {
      return agent.subscribe((ev) => this.onEvent(ev));
    } catch {
      // subscribe 失败绝不阻断 Agent.prompt
      return () => {};
    }
  }

  private onEvent(ev: AgentEvent): void {
    try {
      switch (ev.type) {
        case "turn_start":
          this.turnStarts.set(ev.turnIndex, ev.timestamp);
          console.log(`[${timestamp()}] [turn] ${this.npcName} #${ev.turnIndex} start`);
          break;
        case "message_end":
          console.log(`[${timestamp()}] [turn] ${this.npcName} llm输出:\n${ev.content}`);
          break;
        case "tool_call_start":
          console.log(`[${timestamp()}] [tool] ${this.npcName} → ${ev.toolName} args=${safeStringify(ev.args)}`);
          break;
        case "tool_call_end": {
          // tool 级耗时需配对 start 时间戳 Map，本轮 YAGNI；turn 级耗时足够定位慢调用。
          const ok = !ev.isError;
          const resultStr = safeStringify(ev.result);
          console.log(`[${timestamp()}] [tool] ${this.npcName} ← ${ev.toolName} ok=${ok} result=${resultStr}`);
          break;
        }
        case "turn_end": {
          const startTs = this.turnStarts.get(ev.turnIndex);
          const dur = startTs !== undefined ? ev.timestamp - startTs : undefined;
          console.log(
            `[${timestamp()}] [turn] ${this.npcName} #${ev.turnIndex} end${dur !== undefined ? ` ${dur}ms` : ""}`,
          );
          this.turnStarts.delete(ev.turnIndex);
          break;
        }
        case "error":
          console.error(`[${timestamp()}] [turn] ${this.npcName} error: ${ev.message}`);
          break;
      }
    } catch {
      // best-effort：日志失败绝不向上抛
    }
  }
}
