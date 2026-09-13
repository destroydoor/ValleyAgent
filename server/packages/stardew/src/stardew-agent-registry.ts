import { LlmRouter } from "@valley/core";
import type { VercelAIProvider } from "@valley/core";
import type { PromptBuilder } from "./prompt-builder";
import { StardewAgent, type StardewAgentConfig } from "./stardew-agent";
import { AgentMemory } from "./agent-memory";
import { TranscriptStore } from "./transcript-store";
import { join } from "path";

/** HH:MM:SS 时间戳，用于日志前缀（与 protocol-adapter 同款）。 */
function timestamp(): string {
  return new Date().toISOString().slice(11, 19);
}

/** 限时等待模式的轮询间隔 ms（2026-09-13 R1：BUSY 前等锁）。 */
const LOCK_POLL_INTERVAL_MS = 250;

/** Phase 1 E1-1 全量留痕配置。默认不启用——零构造、零保存开销。 */
export interface TranscriptConfig {
  enabled: boolean;
  /** transcript.sqlite 所在目录；缺省时落到 agentsDir。 */
  dir?: string;
}

export interface RegistryConfig {
  promptBuilder: PromptBuilder;
  /** 多 provider 路由器（与 llmProvider 二选一） */
  llmRouter?: LlmRouter;
  /** 单 provider 向后兼容 */
  llmProvider?: VercelAIProvider;
  agentsDir: string;
  maxTurns?: number;
  /** Phase 1 E1-1 全量留痕。缺省/disabled → 不构造 SQLite store，无保存路径。 */
  transcript?: TranscriptConfig;
}

/**
 * Record of a tool call / action result reported by C# after execution.
 * Stored per-NPC until the next dialogue drains the queue.
 */
export interface ToolResultRecord {
  callId: string;
  tool: string;
  success: boolean;
  result?: string;
  // C# ActionResultReason 枚举的 camelCase 字符串值（如 "inventoryFull"）。
  // 可选，向后兼容旧 C# 客户端（未携带时按原格式渲染）。
  reason?: string;
}

export class StardewAgentRegistry {
  private readonly agents = new Map<string, StardewAgent>();
  private readonly lockedNpcs = new Set<string>();
  private readonly config: RegistryConfig;
  private readonly transcriptStore: TranscriptStore | null;
  private readonly toolResultQueues = new Map<string, ToolResultRecord[]>();
  private static readonly MAX_TOOL_RESULT_QUEUE = 10;

  constructor(config: RegistryConfig) {
    this.config = config;
    this.transcriptStore = config.transcript?.enabled
      ? this.initTranscriptStore(config.transcript.dir ?? config.agentsDir)
      : null;
  }

  /** 启用时构造 SQLite store；初始化失败降级为不启用（留痕绝不拖垮服务器）。 */
  private initTranscriptStore(dir: string): TranscriptStore | null {
    try {
      const store = new TranscriptStore(join(dir, "transcript.sqlite"));
      store.init();
      return store;
    } catch (err) {
      console.warn(`[transcript] store init failed, transcript disabled: ${err}`);
      return null;
    }
  }

  /**
   * 关闭留痕 store（server 关闭时调用，close 内部 checkpoint 刷 WAL）。
   * 未启用时为 no-op。
   */
  closeTranscriptStore(): void {
    this.transcriptStore?.close();
  }

  /**
   * 暴露留痕 store 引用 —— 导演接线（Director.transcriptStore）与
   * StardewAgent 共用同一个 funnel；未启用时返回 null。
   */
  getTranscriptStore(): TranscriptStore | null {
    return this.transcriptStore;
  }

  getOrCreate(npcName: string): StardewAgent {
    let agent = this.agents.get(npcName);
    if (!agent) {
      const memoryPath = this.getMemoryFilePath(npcName);
      const memory = new AgentMemory(npcName, memoryPath);
      const agentConfig: StardewAgentConfig = {
        name: npcName,
        memory,
        promptBuilder: this.config.promptBuilder,
        ...(this.config.llmRouter
          ? { llmRouter: this.config.llmRouter, role: this.config.llmRouter.resolveRole(npcName) }
          : { llmProvider: this.config.llmProvider! }),
        ...(this.config.maxTurns !== undefined
          ? { maxTurns: this.config.maxTurns }
          : {}),
        ...(this.transcriptStore !== null
          ? { transcriptStore: this.transcriptStore }
          : {}),
      };
      agent = new StardewAgent(agentConfig);
      this.agents.set(npcName, agent);
    }
    return agent;
  }

  hasAgent(npcName: string): boolean {
    return this.agents.has(npcName);
  }

  listAgents(): StardewAgent[] {
    return Array.from(this.agents.values());
  }

  /**
   * Acquire dialogue lock for an NPC.
   * Returns true if acquired, false if NPC is already in dialogue.
   * Spec 5.5: 同一 NPC 同时只有一个 dialogue 请求
   *
   * 2026-09-13 R1（design §3）：持锁期=整个 ReAct 循环+工具执行+adjust 回执等待，
   * 可达数十秒——BUSY 前允许限时等待（timeoutMs>0 时每 250ms 轮询，锁一释放立即占锁）。
   * timeoutMs=0（缺省）保持历史语义"被占立即 false"，既有调用方/测试不受影响。
   */
  async acquireLock(npcName: string, timeoutMs = 0): Promise<boolean> {
    if (!this.lockedNpcs.has(npcName)) {
      this.lockedNpcs.add(npcName);
      return true;
    }
    if (timeoutMs <= 0) return false;
    console.log(`[${timestamp()}] [lock] ${npcName} busy, waiting up to ${timeoutMs}ms`);
    // 单调算 deadline：先查后睡、醒来先查——锁一释放立即占锁返回；
    // 末轮睡剩余时间，不会睡过 deadline，且 deadline 边界仍做最后一次锁检查。
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      if (!this.lockedNpcs.has(npcName)) {
        this.lockedNpcs.add(npcName);
        return true;
      }
      const remaining = deadline - Date.now();
      if (remaining <= 0) break;
      await new Promise((r) => setTimeout(r, Math.min(LOCK_POLL_INTERVAL_MS, remaining)));
    }
    console.log(`[${timestamp()}] [lock] ${npcName} still busy after ${timeoutMs}ms wait, giving up`);
    return false;
  }

  releaseLock(npcName: string): void {
    this.lockedNpcs.delete(npcName);
  }

  getMemoryFilePath(npcName: string): string {
    return join(this.config.agentsDir, `${npcName}_memory.json`);
  }

  /**
   * Append a tool-result record to the per-NPC feedback queue.
   * Queue is capped at MAX_TOOL_RESULT_QUEUE; oldest entries are dropped.
   */
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

  /**
   * Peek (return a copy of) all pending tool-result records for an NPC.
   * Does NOT clear the queue — caller must call clearToolResults once the
   * feedback has been successfully consumed (§4.4 步骤 3). On LLM failure
   * the queue is preserved so the feedback can be re-injected next round.
   * Returns an empty array if no records exist (or queue never created).
   */
  drainToolResults(npcName: string): ToolResultRecord[] {
    const queue = this.toolResultQueues.get(npcName);
    if (!queue || queue.length === 0) return [];
    return queue.slice();
  }

  /**
   * Clear the per-NPC tool-result queue.
   * Only called by ProtocolAdapter.handleDialogue on the success path
   * (§4.4 步骤 3) so that feedback is consumed exactly once. The failure
   * path leaves the queue intact for re-injection on the next round.
   */
  clearToolResults(npcName: string): void {
    this.toolResultQueues.delete(npcName);
  }

  /**
   * 更新指定 NPC 的 actualState 镜像（由 state_changed 消息驱动）。
   * 如果 agent 尚未创建，getOrCreate 会惰性创建——后续 dialogue 时复用。
   * reason 可选透传：状态转换原因（如 "travel_failed"/"evicted"），存入
   * per-NPC 状态供 prompt 渲染。
   */
  updateActualState(npcName: string, newState: string, reason?: string): void {
    const agent = this.getOrCreate(npcName);
    agent.actualState = newState;
    if (reason && reason !== "") {
      agent.lastTransitionReason = reason;
    }
  }

  /**
   * 读取指定 NPC 的 actualState 镜像。
   * 返回 undefined 表示尚未收到 state_changed 消息（prompt 中省略该段）。
   */
  getActualState(npcName: string): string | undefined {
    return this.agents.get(npcName)?.actualState;
  }
}
