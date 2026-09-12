// RunTranscriptRecorder — 把 runDialogue / runBeat 的一次执行接线到
// TranscriptStore（Phase 1 E1-1）。职责：
//   1. run 生命周期：构造时写 status="running" 行，结束时按 runId UPSERT 终态
//      （success → "completed"，失败 → "error"/"fallback"）；
//   2. 逐轮 agent_turns：通过 Agent.subscribe（core 公开 API，不碰 @valley/core）
//      订阅事件流，从 turn_start / turn_end / tool_call_start / tool_call_end /
//      message_end 拼出每轮 LLM 原始输出 + tool calls + tool results，每轮一行；
//   3. 失败也留痕：LLM 超时/校验重试失败时把中断的轮次落盘，终态写 error——
//      "NPC 走神了"的真相不能丢（设计 §1.2）。
// 约束：
//   - 所有写操作 best-effort（TranscriptStore 内部已吞异常），本类在事件流侧
//     再做一层防御 try/catch，绝不向上抛。
//   - 不修改 @valley/core：只消费 Agent.subscribe 的事件重放。
// 注：PromptBuilder 不暴露静态/动态段切分，systemPromptDynamic 一律留空，
//     整段 prompt 作为 systemPromptFull + sha256（体积控制见设计 §1.2）。

import { createHash, randomUUID } from "node:crypto";
import type { Agent, AgentEvent } from "@valley/core";
import type { TranscriptStore } from "./transcript-store";
import type {
  AgentRunRecord,
  AgentRunStatus,
  AgentRunTokens,
  AgentTurnRecord,
} from "./transcript-types";

/** 创建 RunTranscriptRecorder 所需的 run 级元信息。 */
export interface RunTranscriptOptions {
  npcName: string;
  /** 触发来源：dialogue（runDialogue）或 beat（runBeat）。director 归 Task 4。 */
  trigger: "dialogue" | "beat";
  /** 完整 system prompt（静态+动态合一段，Builder 不暴露切分）。 */
  systemPrompt: string;
  /** 玩家输入 / 导演指令，作为 user_input 落库。 */
  userInput?: string;
  /** 游戏日期 YYYY-MM-DD；无来源时省略（列落 NULL）。 */
  gameDate?: string;
  /** C# 侧请求 ID（对话路径），用于跨端关联。 */
  requestId?: string;
}

/** finalizeSuccess 的入参：终态输出摘要。 */
export interface RunTranscriptOutcome {
  finalSpeech?: string;
  actions: unknown[];
  toolCalls: unknown[];
  tokens?: AgentRunTokens;
}

/** 单轮 agent_turns 的累积缓冲（turn_end 时落盘）。 */
interface TurnAccumulator {
  llmRawOutput?: string;
  toolCalls: unknown[];
  toolResults: unknown[];
}

export class RunTranscriptRecorder {
  readonly runId: string;
  private readonly store: TranscriptStore;
  private readonly startedAtMs = Date.now();
  private readonly startedAt = new Date().toISOString();
  // run 级不变字段；startedAt/status/终态字段在每次写入时按需填。
  private readonly base: {
    npcName: string;
    trigger: "dialogue" | "beat";
    systemPromptHash: string;
    systemPromptFull: string;
    systemPromptDynamic: string;
  };
  private readonly gameDate: string | undefined;
  private readonly userInput: string | undefined;
  private readonly requestId: string | undefined;
  private readonly turnBuffer = new Map<number, TurnAccumulator>();
  private currentTurn: number | null = null;
  private readonly unsubscribers: Array<() => void> = [];

  constructor(store: TranscriptStore, opts: RunTranscriptOptions) {
    this.store = store;
    this.runId = randomUUID();
    this.gameDate = opts.gameDate;
    this.userInput = opts.userInput;
    this.requestId = opts.requestId;
    this.base = {
      npcName: opts.npcName,
      trigger: opts.trigger,
      systemPromptHash: createHash("sha256").update(opts.systemPrompt).digest("hex"),
      systemPromptFull: opts.systemPrompt,
      systemPromptDynamic: "",
    };
    // 先落 running 行：让 run_id 在逐轮 turn 落盘前已存在，回放/审计顺序自洽。
    this.writeRun({ status: "running" });
  }

  /**
   * 订阅 Agent 事件流，把每轮拼成 agent_turns 落盘。
   * 每个 runOnce（首次 + 校验重试）都要 attach —— retry 是同一 run 的延续，
   * 但每个 runOnce 都 new 一个 Agent，订阅必须跟着新 Agent 走。
   */
  attach(agent: Agent): void {
    const unsub = agent.subscribe((ev) => this.onEvent(ev));
    this.unsubscribers.push(unsub);
  }

  /** 成功终态：按 run_id UPSERT，status="completed"。 */
  finalizeSuccess(outcome: RunTranscriptOutcome): void {
    try {
      this.writeRun({
        status: "completed",
        finishedAt: new Date().toISOString(),
        ...(outcome.finalSpeech !== undefined ? { finalSpeech: outcome.finalSpeech } : {}),
        actions: outcome.actions,
        toolCalls: outcome.toolCalls,
        tokens: outcome.tokens ?? {},
        latencyMs: Date.now() - this.startedAtMs,
      });
    } catch (err) {
      // 防御兜底：store 内部已吞异常，这里再包一层保证 finalize 永不外抛。
      console.warn(`[transcript] finalize failed: ${err}`);
    } finally {
      this.dispose();
    }
  }

  /**
   * 失败终态：LLM 超时 / 校验重试失败 → status="error"。
   * fallback=true 时写 "fallback"（当前 runDialogue/runBeat 不自带兜底，
   * 兜底在 protocol-adapter，此处预留该状态给未来自兜底路径）。
   */
  finalizeError(err: unknown, fallback = false): void {
    const message = err instanceof Error ? err.message : String(err);
    try {
      this.writeRun({
        status: fallback ? "fallback" : "error",
        finishedAt: new Date().toISOString(),
        actions: [],
        toolCalls: [],
        validation: { valid: false, issues: message },
        fallback: fallback ? { flag: true, reason: message } : { flag: false },
        tokens: {},
        latencyMs: Date.now() - this.startedAtMs,
        error: message,
      });
    } catch (err2) {
      console.warn(`[transcript] finalize failed: ${err2}`);
    } finally {
      this.dispose();
    }
  }

  private dispose(): void {
    for (const unsub of this.unsubscribers) unsub();
    this.unsubscribers.length = 0;
  }

  private writeRun(partial: Partial<AgentRunRecord> & { status: AgentRunStatus }): void {
    this.store.recordAgentRun(this.buildRecord(partial));
  }

  private buildRecord(
    partial: Partial<AgentRunRecord> & { status: AgentRunStatus },
  ): AgentRunRecord {
    const rec: AgentRunRecord = {
      runId: this.runId,
      npcName: this.base.npcName,
      trigger: this.base.trigger,
      startedAt: this.startedAt,
      systemPromptHash: this.base.systemPromptHash,
      systemPromptFull: this.base.systemPromptFull,
      systemPromptDynamic: this.base.systemPromptDynamic,
      actions: partial.actions ?? [],
      toolCalls: partial.toolCalls ?? [],
      validation: partial.validation ?? { valid: true },
      fallback: partial.fallback ?? { flag: false },
      tokens: partial.tokens ?? {},
      status: partial.status,
    };
    // exactOptionalPropertyTypes: 可选字段只在有值时挂载，避免 undefined 赋值。
    if (this.gameDate !== undefined) rec.gameDate = this.gameDate;
    if (this.requestId !== undefined) rec.requestId = this.requestId;
    if (this.userInput !== undefined) rec.userInput = this.userInput;
    if (partial.finishedAt !== undefined) rec.finishedAt = partial.finishedAt;
    if (partial.finalSpeech !== undefined) rec.finalSpeech = partial.finalSpeech;
    if (partial.latencyMs !== undefined) rec.latencyMs = partial.latencyMs;
    if (partial.error !== undefined) rec.error = partial.error;
    return rec;
  }

  private onEvent(ev: AgentEvent): void {
    switch (ev.type) {
      case "turn_start":
        this.currentTurn = ev.turnIndex;
        this.turnBuffer.set(ev.turnIndex, { toolCalls: [], toolResults: [] });
        break;
      case "message_end": {
        const acc = this.currentAccumulator();
        if (acc) acc.llmRawOutput = ev.content;
        break;
      }
      case "tool_call_start": {
        const acc = this.currentAccumulator();
        if (acc) {
          acc.toolCalls.push({ id: ev.toolCallId, name: ev.toolName, args: ev.args });
        }
        break;
      }
      case "tool_call_end": {
        const acc = this.currentAccumulator();
        if (acc) {
          acc.toolResults.push({
            toolName: ev.toolName,
            toolCallId: ev.toolCallId,
            result: ev.result,
            isError: ev.isError,
          });
        }
        break;
      }
      case "turn_end": {
        const acc = this.turnBuffer.get(ev.turnIndex);
        if (acc) this.writeTurn(ev.turnIndex, acc);
        this.turnBuffer.delete(ev.turnIndex);
        if (this.currentTurn === ev.turnIndex) this.currentTurn = null;
        break;
      }
      case "error":
      case "agent_end": {
        // 中断的轮次（llmCall 抛错时 agent-loop 不发 turn_end）也要落盘，
        // 否则"走神的那轮"就从 trace 里蒸发了。
        if (this.currentTurn !== null) {
          const acc = this.turnBuffer.get(this.currentTurn);
          if (acc) this.writeTurn(this.currentTurn, acc);
          this.turnBuffer.delete(this.currentTurn);
          this.currentTurn = null;
        }
        break;
      }
    }
  }

  private currentAccumulator(): TurnAccumulator | undefined {
    if (this.currentTurn === null) return undefined;
    return this.turnBuffer.get(this.currentTurn);
  }

  private writeTurn(turnIndex: number, acc: TurnAccumulator): void {
    const rec: AgentTurnRecord = {
      runId: this.runId,
      turnIndex,
      toolCalls: acc.toolCalls,
      toolResults: acc.toolResults,
    };
    if (acc.llmRawOutput !== undefined) rec.llmRawOutput = acc.llmRawOutput;
    this.store.recordAgentTurn(rec);
  }
}
