import { Agent, VercelAIProvider, ToolRegistry, LlmRouter } from "@valley/core";
import type { AgentLoopConfig, LlmCallResult, LlmRole } from "@valley/core";
import type { AgentContext, AgentEvent, AgentMessage, LlmMessage, Tool } from "@valley/core";
import type { AgentMemory } from "./agent-memory";
import { OutputValidator } from "./output-validator";
import type { PromptBuilder } from "./prompt-builder";
import { buildStardewTools } from "./stardew-tools";
import type { ToolContext } from "./stardew-tools";
import type { ToolResultRecord } from "./stardew-agent-registry";
import type { TranscriptStore } from "./transcript-store";
import { RunTranscriptRecorder } from "./transcript-recorder";
import { ConsoleLogSubscriber } from "./console-log-subscriber";
import type { AgentRunTokens } from "./transcript-types";
import type { SceneState, ToolAction, EconomyExecutor } from "./types";
import { DEFAULT_EMOTION } from "./types";

export interface StardewAgentConfig {
  name: string;
  memory: AgentMemory;
  promptBuilder: PromptBuilder;
  /** 多 provider 路由器（与 llmProvider 二选一） */
  llmRouter?: LlmRouter;
  /** NPC 角色（llmRouter 模式下必填） */
  role?: LlmRole;
  /** 单 provider 模式向后兼容（llmRouter 未传时使用） */
  llmProvider?: VercelAIProvider;
  maxTurns?: number;
  /** Phase 1 E1-1 全量留痕存储。未提供（默认）时不接线——零构造、零保存开销。 */
  transcriptStore?: TranscriptStore;
}

export interface DialogueResult {
  speech: string;
  actions: ToolAction[];
  emotion: string;
  rawText: string;
  toolCalls: Array<{ name: string; args: Record<string, unknown> }>;
  // §4.1.1 方案 B：LLM 通过 evaluate_friendship 工具输出的好感度评估。
  // runDialogue 的 extractResult 携带该字段；LLM 未评估时默认 0/""，存在无害。
  friendshipDelta: number;
  friendshipReason: string;
}

const SPEAK_TOOLS = new Set(["speak", "show_dialogue"]);
const FRIENDSHIP_EVAL_TOOL = "evaluate_friendship";
// 2026-08-15 步骤 2：经济工具在 ReAct 循环内同步执行（账本→execute_adjust→回执），
// 不再作为 action 发 C# 执行——extractResult 必须跳过（与 evaluate_friendship 同型）。
const SYNC_ECONOMY_TOOLS = new Set(["trade", "give_item", "give_gift", "receive_payment"]);
const TOOL_RESULT_FAILURE_IMPORTANCE = 5;
// 单次对话好感 delta 钳制上限（2026-08-23 审计修复）：LLM 幻觉 ±2500 一句话就能
// 打满/清空好感——单次变化封顶 ±100；累计 0..2500 的总钳制在 addFriendship 侧。
const FRIENDSHIP_DELTA_LIMIT = 100;
/**
 * C# ActionResultReason 枚举（camelCase 字符串值）→ 中文映射。
 * 用于在 prompt 反馈段中向 LLM 解释失败原因；protocol-adapter 的失败记忆
 * 文案也复用本映射（未覆盖的 reason 由调用方走"未知原因"兜底）。
 */
export const REASON_CN: Record<string, string> = {
  agentMissing: "agent 未分配",
  transitionBlocked: "状态机拒绝转换",
  invalidState: "非法状态名",
  targetUnreachable: "目标不可达",
  itemNotFound: "物品不存在",
  inventoryFull: "背包已满",
  locationInvalid: "地点无效",
  internalError: "内部异常",
};

/**
 * Format tool-result records into prompt text lines.
 * 成功："{tool}：成功 — {result}"
 * 失败无 reason："{tool}：失败 — {result}"
 * 失败有 reason："{tool}：失败（{reasonCn}） — {result}"
 * Example: "give_gift：失败（背包已满） — 玩家背包已满"
 */
function formatToolResultsText(records: ToolResultRecord[]): string {
  if (records.length === 0) return "";
  return records
    .map((r) => {
      const status = r.success ? "成功" : "失败";
      const detail = r.result ?? "";
      if (!r.success && r.reason) {
        const reasonCn = REASON_CN[r.reason] ?? "未知原因";
        return `${r.tool}：${status}（${reasonCn}）${detail.length > 0 ? ` — ${detail}` : ""}`;
      }
      return `${r.tool}：${status}${detail.length > 0 ? ` — ${detail}` : ""}`;
    })
    .join("\n");
}

export class StardewAgent {
  readonly name: string;
  private readonly memory: AgentMemory;
  private readonly promptBuilder: PromptBuilder;
  private readonly llmRouter: LlmRouter | null;
  private readonly role: LlmRole;
  private readonly llmProvider: VercelAIProvider | null;
  private readonly maxTurns: number;
  private readonly validator = new OutputValidator();
  // 2026-08-15 步骤 2：本轮 dialogue/beat 是否接线了经济同步执行器。
  // true 时 extractResult 跳过经济工具（已在 ReAct 内执行完，不再发 C#）；
  // false（旧路径/测试）时经济工具走意图式，仍需作为 action 发 C# 执行。
  private syncEconomyTools = false;
  private readonly transcriptStore: TranscriptStore | null;
  private coreAgent: Agent | null = null;
  // per-NPC 真实状态镜像，由 state_changed 消息更新，prompt 的 {actual_state_section} 消费
  actualState?: string;
  // 最近一次状态转换原因（如 "travel_failed"/"evicted"/"task_completed"），可选供 prompt 渲染
  lastTransitionReason?: string;
  // 当前对话玩家（runDialogue 设置）：extractResult 兜底台词与 speak 同权
  // 进该玩家的关系桶；无 playerId（旧调用）走世界桶。per-NPC 对话锁保证不串场。
  private dialoguePlayerId: string | undefined;
  private dialoguePlayerName: string | undefined;

  constructor(config: StardewAgentConfig) {
    this.name = config.name;
    this.memory = config.memory;
    this.promptBuilder = config.promptBuilder;
    if (config.llmRouter) {
      this.llmRouter = config.llmRouter;
      this.role = config.role ?? "npc";
      this.llmProvider = null;
    } else if (config.llmProvider) {
      this.llmRouter = null;
      this.role = "npc";
      this.llmProvider = config.llmProvider;
    } else {
      throw new Error("StardewAgent requires either llmRouter or llmProvider");
    }
    this.maxTurns = config.maxTurns ?? 5;
    this.transcriptStore = config.transcriptStore ?? null;
  }

  isIdle(): boolean {
    return this.coreAgent?.isIdle() ?? true;
  }

  async runDialogue(
    playerInput: string,
    scene: SceneState,
    toolResults: ToolResultRecord[] = [],
    economy?: EconomyExecutor,
    playerId?: string,
    playerName?: string,
  ): Promise<DialogueResult> {
    this.syncEconomyTools = economy !== undefined;
    // 当前对话玩家供 extractResult 兜底台词路由（speak 工具走 toolCtx.playerId 同源）。
    this.dialoguePlayerId = playerId;
    this.dialoguePlayerName = playerName;
    // Record player input immediately (spec 4.1 step 8). M2a：带 playerId 时写入该玩家的
    // 关系桶（conversationHistory per-player，玩家桶需已预加载——protocol-adapter 负责）。
    this.memory.addConversation("player", playerInput, playerId, playerName);

    // Build system prompt with current memory + scene + tool-result feedback
    const toolResultsText = formatToolResultsText(toolResults);
    const systemPrompt = this.promptBuilder.buildDialogueSystemPrompt(
      this.memory,
      scene,
      this.name,
      toolResultsText,
      this.actualState,
      playerId,
      playerName,
    );

    // Phase 1 E1-1 留痕：run 生命周期 + 逐轮 turn，仅启用 store 时才构造
    // recorder。对话协议不含游戏日期（worldSnapshot 无年月日），gameDate 留空。
    const recorder = this.transcriptStore
      ? new RunTranscriptRecorder(this.transcriptStore, {
          npcName: this.name,
          trigger: "dialogue",
          systemPrompt,
          userInput: playerInput,
        })
      : null;

    try {
      // Build tools with shared context
      const toolCtx: ToolContext = {
        memory: this.memory,
        scene,
        // E4-2: NPC 自己的背包（give_item/get_info inventory 的对象）；
        // scene.inventory 是玩家背包，不得作为 NPC 物品来源。
        inventory: (scene.npcInventory ?? []).map((i) => ({ ...i })),
        givenToPlayer: [],
        log: [],
        ...(economy ? { economy } : {}),
        // M2b：对话发起玩家（工具记忆路由 + 文案玩家化）。
        ...(playerId !== undefined ? { playerId } : {}),
        ...(playerName !== undefined ? { playerName } : {}),
      };
      const tools = buildStardewTools(toolCtx);
      const registry = new ToolRegistry();
      for (const t of tools) registry.register(t);

      // Build AgentContext. M2a：前缀用对话玩家名（多人时区分发起者）。
      const playerLabel = playerName && playerName.trim().length > 0 ? playerName.trim() : "农场主";
      const initialContext: AgentContext = {
        messages: [{ role: "user", content: `${playerLabel}说：${playerInput}` }],
        systemPrompt,
        metadata: {},
      };

      // LLM token 用量不在事件流里，只能在 llmCall 回传处累计供 run 终态落库。
      const tokenUsage: AgentRunTokens = {};

      // Build agentLoop config
      const loopConfig: AgentLoopConfig = {
        tools: registry,
        convertToLlm: this.makeConvertToLlm(systemPrompt),
        llmCall: async (messages, toolList) => {
          const result = await this.makeLlmCall(messages, toolList);
          if (result.usage?.promptTokens !== undefined) {
            tokenUsage.in = (tokenUsage.in ?? 0) + result.usage.promptTokens;
          }
          if (result.usage?.completionTokens !== undefined) {
            tokenUsage.out = (tokenUsage.out ?? 0) + result.usage.completionTokens;
          }
          return result;
        },
        toolExecution: "sequential",
        maxTurns: this.maxTurns,
        shouldStopAfterTurn: (ctx, _turn) => {
          // Stop only after a speak/show_dialogue tool was called (dialogue complete).
          // Otherwise let maxTurns bound the loop.
          for (const m of ctx.messages) {
            if (m.role === "assistant" && m.toolCalls) {
              const calledSpeak = m.toolCalls.some((tc) => SPEAK_TOOLS.has(tc.name));
              if (calledSpeak) return true;
            }
          }
          return false;
        },
      };

      // First attempt
      let result = await this.runOnce(initialContext, loopConfig, recorder);

      // OutputValidator retry (Task H): if the first attempt's speech/actions fail
      // CJK ratio / empty-text checks, append a retry prompt and run the loop
      // once more. If the retry also fails, throw so protocol-adapter runs fallback.
      const firstCheck = this.validator.validateSpeechAndActions(result.speech, result.actions);
      if (!firstCheck.valid) {
        const badText = result.speech || result.rawText || "";
        const retryMessage: AgentMessage = {
          role: "user",
          content: this.validator.buildRetryPrompt(badText),
        };
        const retryContext: AgentContext = {
          ...initialContext,
          messages: [...initialContext.messages, retryMessage],
        };

        result = await this.runOnce(retryContext, loopConfig, recorder);

        const retryCheck = this.validator.validateSpeechAndActions(result.speech, result.actions);
        if (!retryCheck.valid) {
          throw new Error(
            `Output validation failed after retry: ${retryCheck.reason ?? "unknown"}`,
          );
        }
      }

      // After validation: persist failed tool-result feedback into memory
      // so the NPC remembers what went wrong last time (spec C3 feedback loop).
      this.recordFailedToolResults(toolResults, scene);

      // 终态留痕：completed（含最终 speech / actions / tokens / 延迟）。
      recorder?.finalizeSuccess({
        ...(result.speech ? { finalSpeech: result.speech } : {}),
        actions: result.actions,
        toolCalls: result.toolCalls,
        tokens: tokenUsage,
      });

      return result;
    } catch (err) {
      // 失败也留痕：LLM 超时 / 校验重试失败 → status="error"（设计 §1.2）。
      recorder?.finalizeError(err);
      throw err;
    }
  }


  /**
   * Run the agent loop once with the given context + config, returning the
   * extracted dialogue result. Throws on agent-loop error events.
   * recorder 为 E1-1 留痕订阅器（可选）：每个 runOnce 都 new 一个 Agent，
   * 必须对新 Agent 重新 attach，否则 retry 那轮的事件就丢了。
   */
  private async runOnce(
    context: AgentContext,
    loopConfig: AgentLoopConfig,
    recorder: RunTranscriptRecorder | null = null,
  ): Promise<DialogueResult> {
    this.coreAgent = new Agent(`${this.name}-dialogue`, loopConfig);
    // 订阅事件流构建 agent_turns：Agent.prompt 在 run 结束后把全部事件重放
    // 给订阅者，attach 在 prompt 前注册即可覆盖 turn_start 等同步早发事件。
    recorder?.attach(this.coreAgent);
    // stdout 日志订阅：与 recorder 同机制，run 结束后 replay 事件批量打日志。
    const logSub = new ConsoleLogSubscriber(this.name);
    const unsubLog = logSub.attach(this.coreAgent);
    let events: AgentEvent[];
    try {
      const stream = this.coreAgent.prompt(context);
      events = await stream.awaitAll();
    } finally {
      unsubLog();
    }

    // Surface error events as thrown exceptions so callers can run fallback logic.
    // E5: preserve original error instance (when emitter attaches it) so
    // protocol-adapter's rule-engine.buildFallbackResponse can instanceof-match
    // LLMBillingError / LLMUnavailableError and pick the correct persona tier.
    // Falls back to generic Error for older emitters without `error` field.
    for (const ev of events) {
      if (ev.type === "error") {
        const original = (ev as { error?: unknown }).error;
        if (original instanceof Error) {
          throw original;
        }
        throw new Error(ev.message);
      }
    }

    return this.extractResult(events);
  }

  /**
   * Write each failed tool-result record into short-term memory.
   * Skipped on success — only failures are worth remembering.
   * Per spec: importance=5, entryType="event", tagged ["tool_result"].
   */
  private recordFailedToolResults(
    toolResults: ToolResultRecord[],
    scene: SceneState,
  ): void {
    for (const r of toolResults) {
      if (r.success) continue;
      const detail = r.result ?? "未知原因";
      const text = `我尝试${r.tool}但失败了：${detail}`;
      this.memory.addMemory(
        text,
        TOOL_RESULT_FAILURE_IMPORTANCE,
        "event",
        scene.location,
        ["tool_result"],
      );
    }
  }

  private makeConvertToLlm(systemPrompt: string) {
    return (ctx: AgentContext): { messages: LlmMessage[] } => {
      const out: LlmMessage[] = [{ role: "system", content: systemPrompt }];
      for (const m of ctx.messages) {
        if (m.role === "system") continue;
        if (m.role === "user") {
          out.push({ role: "user", content: m.content });
        } else if (m.role === "assistant") {
          if (m.toolCalls && m.toolCalls.length > 0) {
            out.push({
              role: "assistant",
              content: m.content,
              toolCalls: m.toolCalls.map((tc) => ({
                id: tc.id,
                type: "function" as const,
                functionName: tc.name,
                args: JSON.stringify(tc.args),
              })),
            });
          } else {
            out.push({ role: "assistant", content: m.content });
          }
        } else if (m.role === "tool") {
          out.push({
            role: "tool",
            content: m.content,
            toolCallId: m.toolCallId!,
            ...(m.toolName !== undefined ? { toolName: m.toolName } : {}),
          });
        }
      }
      return { messages: out };
    };
  }

  private async makeLlmCall(messages: LlmMessage[], tools?: Tool[]): Promise<LlmCallResult> {
    if (this.llmRouter) {
      return this.llmRouter.chatWithTools(this.role, messages, tools);
    }
    return this.llmProvider!.chatWithTools(messages, tools);
  }

  private extractResult(events: AgentEvent[]): DialogueResult {
    let speech = "";
    let rawText = "";
    let friendshipDelta = 0;
    let friendshipReason = "";
    const actions: ToolAction[] = [];
    const toolCalls: Array<{ name: string; args: Record<string, unknown> }> = [];

    for (const ev of events) {
      if (ev.type === "message_end") {
        rawText = ev.content;
      }
      if (ev.type === "tool_call_start") {
        const name = ev.toolName;
        const args = ev.args;
        toolCalls.push({ name, args });
        if (SPEAK_TOOLS.has(name)) {
          const text = String(args.text ?? "");
          if (text) speech = text;
        } else if (name === FRIENDSHIP_EVAL_TOOL) {
          // §4.1.1 方案 B：evaluate_friendship 是内部评估工具，捕获 delta/reason
          // 但不加入 actions（不发给 C# 执行）。delta 钳制 ±FRIENDSHIP_DELTA_LIMIT
          // （防幻觉泵值；NaN 经 || 0 归零后钳制无副作用）。
          const rawDelta = Number(args.delta) || 0;
          friendshipDelta = Math.max(-FRIENDSHIP_DELTA_LIMIT, Math.min(FRIENDSHIP_DELTA_LIMIT, rawDelta));
          friendshipReason = String(args.reason ?? "");
        } else if (this.syncEconomyTools && SYNC_ECONOMY_TOOLS.has(name)) {
          // 步骤 2：经济工具已同步执行（execute 内部完成账本→execute_adjust→回执），
          // 结果已通过工具返回注入 LLM 上下文，不重复发 C# 执行。
        } else {
          // Non-speak tool → action
          actions.push({ tool: name, args, callId: ev.toolCallId });
        }
      }
    }

    // Layer 2: if no speak tool was called, use raw LLM text
    if (!speech && rawText) {
      speech = rawText;
      // 兜底台词与 speak 同权（2026-08-23 修复）：带当前对话玩家进其玩家桶，
      // 否则玩家桶历史只有玩家半边。beat/无 playerId 走世界桶。
      this.memory.addConversation("npc", rawText, this.dialoguePlayerId, this.dialoguePlayerName);
    }

    return {
      speech,
      actions,
      emotion: DEFAULT_EMOTION,
      rawText,
      toolCalls,
      friendshipDelta,
      friendshipReason,
    };
  }
}
