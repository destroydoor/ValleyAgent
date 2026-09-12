// DirectorAgent — 工具型导演大脑（2026-09-12 Director 有效化）。
// Spec: AGENTS.md §3.5 / docs/plan/2026-09-12-director-activation-plan.md 裁决 D1。
//
// LLM 工具循环（@valley/core agentLoop），9 个 Director 工具经 director_command
// 下发 C# DirectorTools.Execute。职责边界（治理设计钦定）：
//   - 只编排与改状态：set_npc_position/inventory/mood/money/recent_events/working_on、
//     spawn_beat/spawn_group_beat、inject_memory
//   - 永不调 NPC LLM、永不进 NPC 上下文、永不审批（NPC 不知道导演存在）
//
// beat 唯一通路 = L3 场景注入：spawn_beat → C# BeatStore → worldSnapshot.currentBeat
// → 对话 prompt「眼下正发生的事」段（第三人称，绝不出现"导演"字样）——NPC 在对话中
// 自然演绎场景，无 ReAct 接管。替代已退役的 director.ts（morningPlan beat-JSON 管线）
// 与 StardewAgent.runBeat（ReAct 接管，违反职责隔离 P0，2026-09-12 审计 P1-2）。
//
// 纪律（移植自旧 Director，测试口径不变）：
//   - 节日跳过 / 无 GameContext 跳过 / LLM 失败静默降级为"今日无编排"
//   - 每日 beat 上限（默认 3）+ 同 NPC 冷却（recentBeats 命中即拒）
//   - NPC 必须在 GameContext.npcStates 且 isAvailable
//   - 全量留痕：每次调度写 director_runs（prompt 原文 / 产出与被拒工具调用 / 状态）

import { Type } from "@sinclair/typebox";
import { randomUUID } from "node:crypto";
import { Agent, type AgentContext, type AgentLoopConfig, type LlmCallResult, type LlmMessage, type Tool, ToolRegistry } from "@valley/core";
import type { Beat, GameContext, DirectorCommandMessage } from "./types";
import type { BeatStore } from "./beat-store";
import type { PlayerProfileManager } from "./player-profile";
import type { GameContextManager } from "./game-context";
import type { TranscriptStore } from "./transcript-store";

/** 生成 [HH:MM:SS] 时间戳。 */
function logTimestamp(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

/** GameContext → 游戏日期 key（与 C# dateIso 同格式，如 Y2_summer_14）。 */
function gameDateOf(ctx: GameContext): string {
  return `Y${ctx.time.year}_${ctx.time.season}_${ctx.time.day}`;
}

/** LLM 调用器（chatWithTools 语义；server.ts 由 llmRouter/llmProvider 构造注入）。 */
export type DirectorLlm = (messages: LlmMessage[], tools?: Tool[]) => Promise<LlmCallResult>;

export interface DirectorAgentConfig {
  /** chatWithTools 语义的 LLM 调用器（director 角色）。 */
  llm: DirectorLlm;
  /** director_command 下发通道（server.ts 的 sendToCsharp）。 */
  sendToCsharp: (msg: unknown) => void;
  /** 每日 beat 上限（默认 3）。 */
  maxBeatsPerDay?: number;
  /** 冷却判定回看的近期 beat 数（默认 14）。 */
  maxRecentBeatsForBudget?: number;
  /** 工具循环轮次上限（默认 6——导演一次编排 0-3 个 beat + 少量状态微调足够）。 */
  maxTurns?: number;
}

/** 一次 spawn_beat 投递的记录（供 adapter 发 allocate_agent 保活）。 */
export interface SpawnedBeatInfo {
  npcName: string;
  /** beat 窗口时长（分钟）——allocate_agent keepUntil = now + duration。 */
  durationMinutes: number;
}

/** runDayPlan 结果摘要。 */
export interface DirectorDayPlanSummary {
  /** 产出并已投递的 beat（spawn_beat/spawn_group_beat 合并计数）。 */
  spawnedBeats: SpawnedBeatInfo[];
  /** 被工具层拒绝的调用（含原因，日志 + director_runs 共用同一份事实）。 */
  rejectedCalls: Array<{ tool: string; reason: string }>;
  /** 下发的全部 director_command 条数（含状态类工具）。 */
  commandsSent: number;
  /** noop/error 时的原因（留痕用）。 */
  status: "completed" | "noop" | "error";
  error?: string;
}

/** 单日运行期状态：当日已投递 beat 计数 + 去重（工具校验用）。 */
interface RunState {
  beatCount: number;
  spawnedNpcs: Set<string>;
  rejectedCalls: Array<{ tool: string; reason: string }>;
  commandsSent: number;
  spawnedBeats: SpawnedBeatInfo[];
}

const MAX_SCENE_DESC_LENGTH = 200;
const MIN_BEAT_DURATION_MINUTES = 30;
const MAX_BEAT_DURATION_MINUTES = 600;

/**
 * 工具型导演。构造一次、每日 runDayPlan 一次（protocol-adapter handleDayStarted
 * 概率门控后调用）。所有失败路径降级为"今日无编排"，绝不向上抛。
 */
export class DirectorAgent {
  private readonly llm: DirectorLlm;
  private readonly sendToCsharp: (msg: unknown) => void;
  private readonly maxBeatsPerDay: number;
  private readonly maxRecentBeatsForBudget: number;
  private readonly maxTurns: number;

  constructor(
    private readonly beatStore: BeatStore,
    private readonly profileMgr: PlayerProfileManager,
    private readonly gameCtxMgr: GameContextManager,
    config: DirectorAgentConfig,
    // E1-1 留痕接缝（可选注入；未启用时零开销 no-op）。
    private readonly transcriptStore?: TranscriptStore,
  ) {
    this.llm = config.llm;
    this.sendToCsharp = config.sendToCsharp;
    this.maxBeatsPerDay = config.maxBeatsPerDay ?? 3;
    this.maxRecentBeatsForBudget = config.maxRecentBeatsForBudget ?? 14;
    this.maxTurns = config.maxTurns ?? 6;
  }

  /**
   * 每日编排（day_started 概率命中后调用）。
   * directorContext = C# DirectorContextBuilder 拼装的全局视野文本（此前接收即丢弃，
   * 2026-09-12 起被消费）。返回摘要供 adapter 派发 allocate_agent。
   */
  async runDayPlan(directorContext?: string): Promise<DirectorDayPlanSummary> {
    console.log(`[${logTimestamp()}] [director] dayPlan start`);
    const runId = randomUUID();
    const ctx = this.gameCtxMgr.getCurrent();

    if (!ctx) {
      return this.recordNoop(runId, "", "no game context (game_context_sync 未收到，C# 应在 day_started 前发送)");
    }
    if (ctx.time.isFestivalDay) {
      return this.recordNoop(runId, gameDateOf(ctx), "festival day");
    }

    const recentBeats = this.beatStore.listRecent(this.maxRecentBeatsForBudget);
    const systemPrompt = this.buildSystemPrompt();
    const userPrompt = this.buildUserPrompt(ctx, recentBeats, directorContext);

    const runState: RunState = {
      beatCount: 0,
      spawnedNpcs: new Set<string>(),
      rejectedCalls: [],
      commandsSent: 0,
      spawnedBeats: [],
    };
    const tools = this.buildTools(ctx, recentBeats, runState);
    const registry = new ToolRegistry();
    for (const t of tools) registry.register(t);

    const initialContext: AgentContext = {
      messages: [{ role: "user", content: userPrompt }],
      systemPrompt,
      metadata: {},
    };

    const loopConfig: AgentLoopConfig = {
      tools: registry,
      convertToLlm: (c: AgentContext): { messages: LlmMessage[] } => {
        const out: LlmMessage[] = [{ role: "system", content: systemPrompt }];
        for (const m of c.messages) {
          if (m.role === "system") continue;
          if (m.role === "user") {
            out.push({ role: "user", content: m.content });
          } else if (m.role === "assistant") {
            out.push(m.toolCalls && m.toolCalls.length > 0
              ? {
                  role: "assistant",
                  content: m.content,
                  toolCalls: m.toolCalls.map((tc) => ({
                    id: tc.id,
                    type: "function" as const,
                    functionName: tc.name,
                    args: JSON.stringify(tc.args),
                  })),
                }
              : { role: "assistant", content: m.content });
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
      },
      llmCall: this.llm,
      toolExecution: "sequential",
      maxTurns: this.maxTurns,
      shouldStopAfterTurn: (c: AgentContext) => {
        // 最后一轮 assistant 无工具调用 = 编排自然结束（否则靠 maxTurns 兜底）。
        for (let i = c.messages.length - 1; i >= 0; i--) {
          const m = c.messages[i]!;
          if (m.role === "assistant") {
            return !(m.toolCalls && m.toolCalls.length > 0);
          }
        }
        return false;
      },
    };

    let llmRawOutput = "";
    try {
      const agent = new Agent("director-day-plan", loopConfig);
      const events = await agent.prompt(initialContext).awaitAll();
      for (const ev of events) {
        if (ev.type === "error") {
          throw new Error(ev.message);
        }
        if (ev.type === "message_end") {
          llmRawOutput = ev.content;
        }
      }
    } catch (err) {
      console.error(`[${logTimestamp()}] [director] LLM 调用失败:`, err);
      const summary: DirectorDayPlanSummary = {
        spawnedBeats: [],
        rejectedCalls: runState.rejectedCalls,
        commandsSent: runState.commandsSent,
        status: "error",
        error: String(err),
      };
      this.recordRun(runId, gameDateOf(ctx), systemPrompt + "\n\n" + userPrompt, llmRawOutput, summary);
      return summary;
    }

    const summary: DirectorDayPlanSummary = {
      spawnedBeats: runState.spawnedBeats,
      rejectedCalls: runState.rejectedCalls,
      commandsSent: runState.commandsSent,
      status: runState.beatCount > 0 ? "completed" : "noop",
    };
    console.log(
      `[${logTimestamp()}] [director] dayPlan end beats=${runState.beatCount} commands=${runState.commandsSent} rejected=${runState.rejectedCalls.length}`,
    );
    this.recordRun(runId, gameDateOf(ctx), systemPrompt + "\n\n" + userPrompt, llmRawOutput, summary);
    return summary;
  }

  // ------------------------------------------------------------------
  // Prompt
  // ------------------------------------------------------------------

  private buildSystemPrompt(): string {
    return `你是星露谷物语的叙事导演，负责让这个小镇的日常有故事感。

你的职责：
- 为 NPC 编排当天的"际遇"（beat）：用 spawn_beat 给某个 NPC 一段第三人称的场景描述，让玩家遇见他时撞见正在进行的生活
- 适度调整 NPC 状态烘托叙事：set_npc_mood（心情）、set_npc_working_on（在忙什么）、set_npc_recent_events（近期事件）
- 偶尔用 set_npc_position 创造相遇机会、set_npc_inventory/set_npc_money 铺垫情节合理性
- 用 inject_memory 为 NPC 植入"最近发生过的事"的长期记忆（谨慎、少量、符合人设）

硬性纪律（违反即浪费 token）：
- 每天最多 ${this.maxBeatsPerDay} 个 beat；无值得编排的日子就一个都不发（这也是好结果）
- 同一 NPC 短期不重复编排（冷却名单里有的 NPC 不再给 beat）
- spawn_beat 的 sceneDesc 必须是第三人称场景描述（如"她在码头修补渔网，看到玩家会自然打招呼"），绝不能出现"导演""指令""系统""任务"等词——NPC 和玩家永远不知道你的存在
- sceneDesc 是世界里的日常，不是戏剧化剧本：符合 NPC 人设、季节、天气与当前进度
- 你只编排，不代演：绝不假装 NPC 说话或行动，那些由 NPC 自己在对话中完成
- 没有合适的编排就只回复一段简短说明（不调用任何工具），不要硬凑`;
  }

  private buildUserPrompt(ctx: GameContext, recentBeats: Beat[], directorContext?: string): string {
    const lines: string[] = [];
    lines.push(this.gameCtxMgr.summarizeForDirector());
    lines.push("");
    lines.push(this.profileMgr.summarizeForDirector());
    lines.push("");
    if (recentBeats.length > 0) {
      lines.push("[近期已编排 beat（冷却名单——以下 NPC 今天不再编排）]");
      for (const b of recentBeats) {
        lines.push(`- ${b.npcName}: ${b.directive}`);
      }
    } else {
      lines.push("[近期已编排 beat] 无（无冷却约束）");
    }
    lines.push("");
    if (directorContext && directorContext.trim().length > 0) {
      lines.push("[今日全局视野（C# 侧拼装）]");
      lines.push(directorContext.trim());
    }
    lines.push("");
    lines.push("请决定今天的编排（调用工具，或什么都不做）。");
    return lines.join("\n");
  }

  // ------------------------------------------------------------------
  // Tools（9 个，参数契约与 C# DirectorTools.Execute 一一对应）
  // ------------------------------------------------------------------

  private buildTools(ctx: GameContext, recentBeats: Beat[], runState: RunState): Tool[] {
    const available = (npcName: string): boolean => {
      const s = ctx.npcStates.find((n) => n.name === npcName);
      return s !== undefined && s.isAvailable;
    };
    const onCooldown = (npcName: string): boolean =>
      recentBeats.some((b) => b.npcName === npcName) || runState.spawnedNpcs.has(npcName);

    /** 通用下发：director_command（注意：绝不带 npcName 字段——C# 回 action_result 时
     * NpcName 取消息 npcName，空值才会被 TS 安全丢弃，防导演元层结果回注 NPC 反馈队列）。 */
    const send = (tool: string, args: Record<string, unknown>): void => {
      const msg: DirectorCommandMessage = {
        type: "director_command",
        tool,
        args,
        requestId: randomUUID(),
      };
      this.sendToCsharp(msg);
      runState.commandsSent++;
    };

    /** beat 预检：可用性/冷却/每日上限/描述约束。 */
    const trySpawnBeat = (
      npcName: string,
      sceneDesc: string,
    ): { ok: true } | { ok: false; reason: string } => {
      const npc = String(npcName ?? "").trim();
      if (!npc) return { ok: false, reason: "缺少 npc" };
      if (!available(npc)) return { ok: false, reason: `${npc} 不在 gameContext 或不可用` };
      if (onCooldown(npc)) return { ok: false, reason: `${npc} 冷却中（近期已有 beat）` };
      if (runState.beatCount >= this.maxBeatsPerDay) {
        return { ok: false, reason: `超出每日上限 maxBeatsPerDay=${this.maxBeatsPerDay}` };
      }
      const desc = String(sceneDesc ?? "").trim();
      if (!desc) return { ok: false, reason: "sceneDesc 为空" };
      if (desc.length > MAX_SCENE_DESC_LENGTH) {
        return { ok: false, reason: `sceneDesc 过长 (${desc.length} > ${MAX_SCENE_DESC_LENGTH})` };
      }
      return { ok: true };
    };

    /** BeatStore 记录（冷却/预算历史；快照字段沿用旧 Director 的组装口径）。 */
    const recordBeat = (npcName: string, sceneDesc: string, durationMinutes: number, reason: string): void => {
      const now = new Date();
      const fmt = (d: Date) =>
        `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}`;
      const beat: Beat = {
        id: randomUUID(),
        npcName,
        triggerTime: fmt(now),
        windowEnd: fmt(new Date(now.getTime() + durationMinutes * 60_000)),
        directive: sceneDesc,
        context: {
          reasonGenerated: reason,
          playerProfileSnapshot: this.profileMgr.profileStore.load() ?? emptyProfile(),
          gameContextSnapshot: ctx,
          recentBeats: this.beatStore.listRecent(3),
        },
        status: "active",
      };
      this.beatStore.save(beat);
    };

    return [
      {
        name: "spawn_beat",
        description:
          "给一个 NPC 编排当天的际遇场景。sceneDesc 必须是第三人称场景描述（NPC 眼前正在发生的事），" +
          "绝不出现'导演/指令/系统'等词；durationMinutes 为场景持续时长（30-600）。",
        visibility: "llm_visible",
        parameters: Type.Object({
          npc: Type.String({ description: "NPC 名（必须在可用名单中）" }),
          sceneDesc: Type.String({ description: "第三人称场景描述，≤200 字，如：她在码头修补渔网，看见玩家会自然打招呼" }),
          durationMinutes: Type.Optional(Type.Integer({ description: "场景持续分钟数（默认 120）" })),
        }),
        async execute(args) {
          const npc = String(args.npc ?? "").trim();
          const desc = String(args.sceneDesc ?? "").trim();
          const duration = Math.max(MIN_BEAT_DURATION_MINUTES, Math.min(MAX_BEAT_DURATION_MINUTES, Math.round(Number(args.durationMinutes) || 120)));
          const check = trySpawnBeat(npc, desc);
          if (!check.ok) {
            runState.rejectedCalls.push({ tool: "spawn_beat", reason: check.reason });
            console.log(`[${logTimestamp()}] [director] 拒绝 spawn_beat ${npc}: ${check.reason}`);
            return { content: `spawn_beat 被拒绝：${check.reason}`, isError: true };
          }
          send("spawn_beat", { npc, sceneDesc: desc, durationMinutes: duration });
          recordBeat(npc, desc, duration, "dayPlan");
          runState.beatCount++;
          runState.spawnedNpcs.add(npc);
          runState.spawnedBeats.push({ npcName: npc, durationMinutes: duration });
          console.log(`[${logTimestamp()}] [director] spawn_beat ${npc} (${duration}min): ${desc}`);
          return { content: `已为 ${npc} 安排场景（${duration} 分钟）` };
        },
      },
      {
        name: "spawn_group_beat",
        description: "给多个 NPC 编排同一地点的群体场景（如酒馆聚会）。sceneScript 同样必须是第三人称场景描述。",
        visibility: "llm_visible",
        parameters: Type.Object({
          npcs: Type.Array(Type.String(), { description: "NPC 名列表（至少 1 个，全部须在可用名单中）" }),
          location: Type.String({ description: "场景地点（如 Saloon）" }),
          sceneScript: Type.String({ description: "第三人称群体场景描述，≤200 字" }),
          durationMinutes: Type.Optional(Type.Integer({ description: "场景持续分钟数（默认 120）" })),
        }),
        async execute(args) {
          const names = Array.isArray(args.npcs) ? args.npcs.map((n) => String(n ?? "").trim()).filter((n) => n.length > 0) : [];
          const sceneScript = String(args.sceneScript ?? "").trim();
          const location = String(args.location ?? "").trim() || "Town";
          const duration = Math.max(MIN_BEAT_DURATION_MINUTES, Math.min(MAX_BEAT_DURATION_MINUTES, Math.round(Number(args.durationMinutes) || 120)));
          if (names.length === 0) {
            runState.rejectedCalls.push({ tool: "spawn_group_beat", reason: "npcs 为空" });
            return { content: "spawn_group_beat 被拒绝：npcs 为空", isError: true };
          }
          if (!sceneScript || sceneScript.length > MAX_SCENE_DESC_LENGTH) {
            const reason = sceneScript ? `sceneScript 过长 (${sceneScript.length})` : "sceneScript 为空";
            runState.rejectedCalls.push({ tool: "spawn_group_beat", reason });
            return { content: `spawn_group_beat 被拒绝：${reason}`, isError: true };
          }
          // 逐 NPC 预检：可用/冷却/预算，剔除不可用者；全被剔 → 拒绝。
          const eligible: string[] = [];
          for (const n of names) {
            if (!available(n)) {
              runState.rejectedCalls.push({ tool: "spawn_group_beat", reason: `${n} 不可用` });
            } else if (onCooldown(n)) {
              runState.rejectedCalls.push({ tool: "spawn_group_beat", reason: `${n} 冷却中` });
            } else if (runState.beatCount >= this.maxBeatsPerDay) {
              runState.rejectedCalls.push({ tool: "spawn_group_beat", reason: "超出每日上限" });
            } else {
              eligible.push(n);
            }
          }
          if (eligible.length === 0) {
            return { content: "spawn_group_beat 被拒绝：无可用 NPC", isError: true };
          }
          send("spawn_group_beat", { npcs: eligible, location, sceneScript, durationMinutes: duration });
          for (const n of eligible) {
            recordBeat(n, `[${location}] ${sceneScript}`, duration, "dayPlan(group)");
            runState.beatCount++;
            runState.spawnedNpcs.add(n);
            runState.spawnedBeats.push({ npcName: n, durationMinutes: duration });
          }
          console.log(`[${logTimestamp()}] [director] spawn_group_beat ${eligible.join(",")} @${location} (${duration}min)`);
          return { content: `已为 ${eligible.join("、")} 安排群体场景` };
        },
      },
      {
        name: "set_npc_mood",
        description: "设置 NPC 的心情标签（覆盖情绪引擎推导，最高优先级）。如：烦躁、期待、若有所思。",
        visibility: "llm_visible",
        parameters: Type.Object({
          npc: Type.String({ description: "NPC 名" }),
          moodTag: Type.String({ description: "心情标签（中文短语）" }),
        }),
        async execute(args) {
          const npc = String(args.npc ?? "").trim();
          const moodTag = String(args.moodTag ?? args.mood ?? "").trim();
          if (!npc || !moodTag) {
            runState.rejectedCalls.push({ tool: "set_npc_mood", reason: "缺少 npc 或 moodTag" });
            return { content: "set_npc_mood 被拒绝：缺少参数", isError: true };
          }
          send("set_npc_mood", { npc, moodTag });
          return { content: `已下发 ${npc} 心情：${moodTag}` };
        },
      },
      {
        name: "set_npc_working_on",
        description: "设置 NPC 正在做的事（L2 工作标记，对话 prompt 注入）。传空字符串清除。",
        visibility: "llm_visible",
        parameters: Type.Object({
          npc: Type.String({ description: "NPC 名" }),
          workingOn: Type.String({ description: "在做什么（中文短语），空串清除" }),
        }),
        async execute(args) {
          const npc = String(args.npc ?? "").trim();
          if (!npc) {
            runState.rejectedCalls.push({ tool: "set_npc_working_on", reason: "缺少 npc" });
            return { content: "set_npc_working_on 被拒绝：缺少 npc", isError: true };
          }
          send("set_npc_working_on", { npc, workingOn: String(args.workingOn ?? "") });
          return { content: `已下发 ${npc} 工作标记` };
        },
      },
      {
        name: "set_npc_recent_events",
        description: "整体替换 NPC 的近期事件列表（L2）。用于铺垫'今天发生过的事'。",
        visibility: "llm_visible",
        parameters: Type.Object({
          npc: Type.String({ description: "NPC 名" }),
          events: Type.Array(Type.String(), { description: "事件列表（中文短句，1-5 条）" }),
        }),
        async execute(args) {
          const npc = String(args.npc ?? "").trim();
          const events = Array.isArray(args.events) ? args.events.map((e) => String(e ?? "").trim()).filter((e) => e.length > 0) : [];
          if (!npc || events.length === 0) {
            runState.rejectedCalls.push({ tool: "set_npc_recent_events", reason: "缺少 npc 或 events 为空" });
            return { content: "set_npc_recent_events 被拒绝：缺少参数", isError: true };
          }
          send("set_npc_recent_events", { npc, events: events.slice(0, 5) });
          return { content: `已下发 ${npc} 近期事件 ${events.length} 条` };
        },
      },
      {
        name: "set_npc_position",
        description: "移动 NPC 到指定地点（创造相遇机会）。tile 为 [x, y] 坐标。",
        visibility: "llm_visible",
        parameters: Type.Object({
          npc: Type.String({ description: "NPC 名" }),
          location: Type.String({ description: "目标地图名（如 Town、Beach、Forest）" }),
          tile: Type.Array(Type.Integer(), { description: "目标格子 [x, y]" }),
        }),
        async execute(args) {
          const npc = String(args.npc ?? "").trim();
          const location = String(args.location ?? "").trim();
          const tile = Array.isArray(args.tile) ? args.tile.map((t) => Math.round(Number(t) || 0)) : [];
          if (!npc || !location || tile.length !== 2) {
            runState.rejectedCalls.push({ tool: "set_npc_position", reason: "缺少 npc/location 或 tile 非法" });
            return { content: "set_npc_position 被拒绝：参数不完整", isError: true };
          }
          send("set_npc_position", { npc, location, tile });
          return { content: `已下发 ${npc} 移动到 ${location}` };
        },
      },
      {
        name: "set_npc_inventory",
        description: "增删 NPC 背包物品（铺垫情节合理性，如给渔夫添渔网）。itemId 用 qualified id（如 (O)388）。",
        visibility: "llm_visible",
        parameters: Type.Object({
          npc: Type.String({ description: "NPC 名" }),
          add: Type.Optional(Type.Array(Type.String(), { description: "要添加的 itemId 列表" })),
          remove: Type.Optional(Type.Array(Type.String(), { description: "要移除的 itemId 列表" })),
          quantity: Type.Optional(Type.Integer({ description: "每个物品数量（默认 1）" })),
        }),
        async execute(args) {
          const npc = String(args.npc ?? "").trim();
          const add = Array.isArray(args.add) ? args.add.map(String) : [];
          const remove = Array.isArray(args.remove) ? args.remove.map(String) : [];
          if (!npc || (add.length === 0 && remove.length === 0)) {
            runState.rejectedCalls.push({ tool: "set_npc_inventory", reason: "缺少 npc 或 add/remove 为空" });
            return { content: "set_npc_inventory 被拒绝：参数不完整", isError: true };
          }
          const payload: Record<string, unknown> = { npc };
          if (add.length > 0) payload["add"] = add;
          if (remove.length > 0) payload["remove"] = remove;
          const qty = Math.round(Number(args.quantity) || 1);
          if (qty > 1) payload["quantity"] = qty;
          send("set_npc_inventory", payload);
          return { content: `已下发 ${npc} 背包变更` };
        },
      },
      {
        name: "set_npc_money",
        description: "增减 NPC 钱包（delta 正负整数）。注意：C# 侧为执行镜像，权威账本在 TS ledger。",
        visibility: "llm_visible",
        parameters: Type.Object({
          npc: Type.String({ description: "NPC 名" }),
          delta: Type.Integer({ description: "金额变化（正=给钱，负=扣钱，非零）" }),
        }),
        async execute(args) {
          const npc = String(args.npc ?? "").trim();
          const delta = Math.round(Number(args.delta) || 0);
          if (!npc || delta === 0) {
            runState.rejectedCalls.push({ tool: "set_npc_money", reason: "缺少 npc 或 delta=0" });
            return { content: "set_npc_money 被拒绝：参数非法", isError: true };
          }
          send("set_npc_money", { npc, delta });
          return { content: `已下发 ${npc} 钱包 ${delta >= 0 ? "+" : ""}${delta}g` };
        },
      },
      {
        name: "inject_memory",
        description: "为 NPC 植入一条长期记忆（如'昨天在矿洞帮玩家挡过一次怪'）。谨慎少量，必须符合人设。",
        visibility: "llm_visible",
        parameters: Type.Object({
          npc: Type.String({ description: "NPC 名" }),
          text: Type.String({ description: "记忆内容（第一人称短句）" }),
          importance: Type.Optional(Type.Number({ description: "重要性 0-10（默认 3）" })),
        }),
        async execute(args) {
          const npc = String(args.npc ?? "").trim();
          const text = String(args.text ?? "").trim();
          if (!npc || !text) {
            runState.rejectedCalls.push({ tool: "inject_memory", reason: "缺少 npc 或 text" });
            return { content: "inject_memory 被拒绝：参数不完整", isError: true };
          }
          const importance = Math.min(10, Math.max(0, Number(args.importance) || 3));
          send("inject_memory", { npc, text, importance });
          return { content: `已下发 ${npc} 记忆注入` };
        },
      },
    ];
  }

  // ------------------------------------------------------------------
  // 留痕（director_runs，接缝与旧 Director 一致）
  // ------------------------------------------------------------------

  private recordNoop(runId: string, gameDate: string, reason: string): DirectorDayPlanSummary {
    console.log(`[${logTimestamp()}] [director] dayPlan end (noop: ${reason})`);
    const summary: DirectorDayPlanSummary = {
      spawnedBeats: [],
      rejectedCalls: [],
      commandsSent: 0,
      status: "noop",
      error: reason,
    };
    this.recordRun(runId, gameDate, "", "", summary);
    return summary;
  }

  private recordRun(
    runId: string,
    gameDate: string,
    promptFull: string,
    llmRawOutput: string,
    summary: DirectorDayPlanSummary,
  ): void {
    if (!this.transcriptStore) return;
    try {
      this.transcriptStore.recordDirectorRun({
        runId,
        gameDate,
        trigger: "dayPlan",
        promptFull,
        ...(llmRawOutput ? { llmRawOutput } : {}),
        producedBeats: summary.spawnedBeats,
        droppedBeats: summary.rejectedCalls,
        emptyResult: summary.spawnedBeats.length === 0,
        status: summary.status,
        ...(summary.error !== undefined ? { error: summary.error } : {}),
      });
    } catch {
      // 留痕 best-effort，绝不向上抛
    }
  }
}

/** 空玩家档案（BeatStore 记录快照兜底，与旧 Director.emptyProfile 同口径）。 */
function emptyProfile(): import("./types").PlayerProfile {
  return {
    static: {
      farmerName: "",
      gender: "unknown",
      farmName: "",
      farmType: "",
      startDate: "",
      lastUpdated: "",
    },
    behavior: {
      dailyActivities: [],
      totalStats: {
        fishCaught: 0,
        itemsShipped: 0,
        monstersKilled: 0,
        cropsHarvested: 0,
        itemsForaged: 0,
        giftsGiven: 0,
        dialoguesHad: 0,
        miningLevelsDescended: 0,
      },
    },
    preferences: {
      playStyle: [],
      topActivities: [],
      topLocations: [],
      routinePattern: "",
      lastUpdated: "",
    },
    relationships: {},
    personality: {
      traits: [],
      archetype: "",
      narrativeRole: "",
      lastUpdated: "",
    },
    story: {
      completedBeats: [],
      recurringTropes: [],
      lastUpdated: "",
    },
  };
}
