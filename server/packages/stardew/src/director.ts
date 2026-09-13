// Director — narrative director agent (Task 8).
// Generates high-level "beats" (story directives) for NPC agents to execute
// via ReAct. Two entry points:
//   - morningPlan(): daily 6am plan (0-3 beats)
//   - milestoneReact(milestone): triggered by player activity milestones (0-1 beat)
// Spec: docs/superpowers/specs/2026-07-21-narrative-director-design.md §6
//
// 全量留痕（E1-1 活化）：morningPlan / milestoneReact 每次调度自动经
// recordPlanRun 写 director_runs（注入 TranscriptStore 时）；无 store 时零开销
// no-op。prompt 原文 / LLM 原始输出 / 产出与丢弃 beat（含原因）全部落库。

import type { Beat, BeatStatus, PlayerProfile, GameContext } from "./types";
import type { BeatStore } from "./beat-store";
import type { PlayerProfileManager } from "./player-profile";
import { LEGACY_PLAYER_ID } from "./player-profile-store";
import type { ActivityLogStore } from "./activity-log-store";
import type { GameContextManager } from "./game-context";
import type { TranscriptStore } from "./transcript-store";
import type { DirectorRunRecord, DirectorRunStatus } from "./transcript-types";

/** 生成 [HH:MM:SS.mmm] 时间戳。 */
function logTimestamp(): string {
  const d = new Date();
  const pad = (n: number, l = 2) => String(n).padStart(l, "0");
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

/** GameContext → 游戏日期 key（与 C# dateIso 同格式，如 Y2_summer_14）。 */
function gameDateOf(ctx: GameContext): string {
  return `Y${ctx.time.year}_${ctx.time.season}_${ctx.time.day}`;
}

/**
 * LLM caller signature. Implementations MUST NOT throw on transient errors
 * — wrap provider failures and return an empty string. (The Director treats
 * empty/whitespace text as "no plan" and returns an empty array.)
 */
export interface DirectorLlmConfig {
  callLlm: (prompt: string) => Promise<{
    text: string;
    usage: { promptTokens: number; completionTokens: number };
  }>;
}

export interface DirectorConfig {
  /** LLM caller. */
  callLlm: DirectorLlmConfig["callLlm"];
  /** Max beats per day from morningPlan (default 3). */
  maxBeatsPerDay?: number;
  /** Number of recent beats to consider for rhythm budget (default 14). */
  maxRecentBeatsForBudget?: number;
  /** Cooldown days for the same NPC (default 5). */
  npcCooldownDays?: number;
  /**
   * M3 多玩家化：一次 morningPlan 最多为多少个玩家分别编排（默认 4，星露谷联机上限）。
   * 每玩家一次 LLM 调用 + 各自画像，故必须有硬上限把 token 成本钉住。
   */
  maxPlayersPerPlan?: number;
}

/**
 * M3 多玩家化：morningPlan 的编排目标。
 * playerIds 省略/为空 → 单玩家（legacy）行为，与 M3 前完全一致。
 */
export interface DirectorPlanOptions {
  playerIds?: string[];
}

/** M3：milestoneReact 的编排目标（里程碑天然归属单个玩家）。 */
export interface DirectorMilestoneOptions {
  playerId?: string;
}

/**
 * recordPlanRun 入参：导演一次调度（每日计划 / 里程碑反应）的留痕摘要。
 * producedBeats / droppedBeats 为宽松快照（Beat 或含丢弃原因的包装对象），
 * 由调用方按需组装 —— 接缝只负责原样落库，不绑定具体形状。
 */
export interface DirectorPlanRunOptions {
  runId: string;
  gameDate: string;
  /** 触发来源：morningPlan / milestoneReact。 */
  trigger: string;
  /** 完整 prompt（导演调用 LLM 的原文）；未提供时落空串（列 NOT NULL）。 */
  promptFull?: string;
  /** LLM 原始输出（beat JSON 原文）。 */
  llmRawOutput?: string;
  /** 产出并持久化的 beat。 */
  producedBeats: unknown[];
  /** 被丢弃的 beat（含丢弃原因，如"NPC 冷却中"）。 */
  droppedBeats: unknown[];
  /** true = 本轮无产出（"今日无叙事"也是一个记录）。 */
  emptyResult: boolean;
  status: DirectorRunStatus;
  /** 调度出错时的错误信息。 */
  error?: string;
  /**
   * M3 多玩家化：本次编排面向的玩家（省略 = 单玩家/legacy 编排）。
   * 落在 director_runs.player_id 列（旧库增量加列，可空）。
   */
  playerId?: string;
}

/** Maximum directive length (spec §6.3). */
const MAX_DIRECTIVE_LENGTH = 200;
/** Minimum window duration: windowEnd must be > triggerTime by at least 1 hour. */
const MIN_WINDOW_MINUTES = 60;

interface RawBeatOutput {
  npcName: string;
  triggerTime: string;
  windowEnd: string;
  directive: string;
  reasonGenerated: string;
}

/** 校验阶段被丢弃的 beat 快照（落 director_runs.droppedBeats）。 */
interface DroppedBeat {
  npcName: string;
  directive: string;
  reasonDropped: string;
}

/** validateAndFilter 结果：保留 beat + 丢弃明细（含原因）。 */
interface FilterResult {
  valid: RawBeatOutput[];
  dropped: DroppedBeat[];
}

/** callLlmForBeats 结果：prompt/原文/解析出的 beat/LLM 错误。 */
interface LlmAttempt {
  prompt: string;
  responseText: string;
  beats: RawBeatOutput[];
  llmError?: string;
}

/**
 * The narrative Director. Generates "beats" — high-level natural-language
 * directives that an NPC Agent then executes via ReAct.
 *
 * Failure-mode policy: ALL LLM failures degrade gracefully.
 *   - On exception: return [] / null
 *   - On unparseable JSON: return [] / null
 *   - On validation failure of an individual beat: drop that beat, keep others
 *   - Empty array output is a legitimate "no plan today" result
 *
 * The Director NEVER retries LLM calls — a single bad LLM response simply
 * means no beats that day. This keeps token costs bounded and prevents
 * runaway retry loops.
 */
export class Director {
  private readonly callLlm: DirectorLlmConfig["callLlm"];
  private readonly maxBeatsPerDay: number;
  private readonly maxRecentBeatsForBudget: number;
  /** M3：一次 morningPlan 最多编排的玩家数（钉住联机下的 LLM 调用次数）。 */
  private readonly maxPlayersPerPlan: number;

  constructor(
    private readonly beatStore: BeatStore,
    private readonly profileMgr: PlayerProfileManager,
    private readonly gameCtxMgr: GameContextManager,
    private readonly activityStore: ActivityLogStore,
    config: DirectorConfig,
    // E1-1 全量留痕接缝：可选注入（server 经 registry.getTranscriptStore() 提供，
    // 留痕未启用时为 undefined）。morningPlan / milestoneReact 每次调度自动落
    // director_runs；缺省 no-op，零构造零保存开销。
    private readonly transcriptStore?: TranscriptStore,
  ) {
    this.callLlm = config.callLlm;
    this.maxBeatsPerDay = config.maxBeatsPerDay ?? 3;
    this.maxRecentBeatsForBudget = config.maxRecentBeatsForBudget ?? 14;
    this.maxPlayersPerPlan = config.maxPlayersPerPlan ?? 4;
    // npcCooldownDays is accepted by the config interface for API stability
    // but not used directly — NPC cooldown is enforced implicitly via the
    // recentBeats list bounded by maxRecentBeatsForBudget (a beat for the
    // same NPC in the last N beats means cooldown active).
  }

  /**
   * Daily morning planning. Returns 0 to maxBeatsPerDay beats based on
   * PlayerProfile + GameContext + recent beat history.
   *
   * Steps (spec §6.1):
   *   1. Load GameContext — bail on festival day or no context
   *   2. Load profile summary + game context summary
   *   3. Load recent N beats from BeatStore
   *   4. Build prompt
   *   5. Call LLM, parse JSON array (tolerant extraction)
   *   6. Validate each beat
   *   7. Truncate to maxBeatsPerDay
   *   8. Persist + return
   *
   * 每条退出路径（无上下文/节日/LLM 失败/空产出/正常产出）都写一条
   * director_runs —— 降级是行为，留痕是可观测性，两者都必须。
   */
  async morningPlan(options: DirectorPlanOptions = {}): Promise<Beat[]> {
    console.log(`[${logTimestamp()}] [director] morningPlan start`);
    const runId = crypto.randomUUID();

    const ctx = this.gameCtxMgr.getCurrent();
    if (!ctx) {
      console.log(`[${logTimestamp()}] [director] morningPlan end (no game context)`);
      this.recordPlanRun({
        runId,
        gameDate: "",
        trigger: "morningPlan",
        producedBeats: [],
        droppedBeats: [],
        emptyResult: true,
        status: "noop",
        error: "no game context (game_context_sync 未收到，C# 应在 day_started 前发送)",
      });
      return [];
    }
    if (ctx.time.isFestivalDay) {
      console.log(`[${logTimestamp()}] [director] morningPlan end (festival day)`);
      this.recordPlanRun({
        runId,
        gameDate: gameDateOf(ctx),
        trigger: "morningPlan",
        producedBeats: [],
        droppedBeats: [],
        emptyResult: true,
        status: "noop",
        error: "festival day",
      });
      return [];
    }

    // M3：无玩家名单（单机 / 服务器重启后尚无人开口）→ 单玩家编排，行为等价 M3 前。
    const targets = this.resolveTargetPlayers(options.playerIds);
    if (targets.length === 0) {
      return this.morningPlanForPlayer(ctx, undefined, new Set(), this.maxBeatsPerDay);
    }

    console.log(
      `[${logTimestamp()}] [director] morningPlan multiplayer: ${targets.length} 个玩家 (${targets.join(", ")})，`
      + `每日 beat 预算 ${this.maxBeatsPerDay} 全局共享（联机不放大总量）`,
    );
    const produced: Beat[] = [];
    // 跨玩家保留表：同一 NPC 当天只为第一个玩家编排（否则两个玩家各拿一个同 NPC beat，
    // 玩家侧表现为"同一个 NPC 同一天被安排两次"，破坏节奏预算）。
    const reserved = new Set<string>();
    for (const playerId of targets) {
      const budget = this.maxBeatsPerDay - produced.length;
      if (budget <= 0) {
        console.log(`[${logTimestamp()}] [director] 每日 beat 预算已用尽 (${this.maxBeatsPerDay})，跳过剩余玩家 ${playerId}`);
        break;
      }
      const beats = await this.morningPlanForPlayer(ctx, playerId, reserved, budget);
      produced.push(...beats);
    }

    console.log(`[${logTimestamp()}] [director] morningPlan end (multiplayer) players=${targets.length} produced=${produced.length}`);
    return produced;
  }

  /**
   * 单个玩家的一次 morningPlan 编排（LLM 调用 → 校验 → 落库 → 留痕）。
   * playerId 为 undefined 时是单玩家/legacy 路径（prompt 与快照取缺省玩家画像）。
   *
   * @param reserved 已被前面玩家占用的 NPC（跨玩家去重，函数内会把本次占用的 NPC 加进去）
   * @param budget   剩余每日 beat 预算（多玩家共享同一个 maxBeatsPerDay）
   */
  private async morningPlanForPlayer(
    ctx: GameContext,
    playerId: string | undefined,
    reserved: Set<string>,
    budget: number,
  ): Promise<Beat[]> {
    const runId = crypto.randomUUID();
    const who = playerId ?? "(单玩家)";
    const playerOpt = playerId !== undefined ? { playerId } : {};

    const attempt = await this.callLlmForBeats(ctx, /* isMilestone */ false, undefined, playerId);
    if (attempt.llmError !== undefined) {
      console.log(`[${logTimestamp()}] [director] morningPlan[${who}] end (no beats from LLM)`);
      this.recordPlanRun({
        runId,
        gameDate: gameDateOf(ctx),
        trigger: "morningPlan",
        promptFull: attempt.prompt,
        producedBeats: [],
        droppedBeats: [],
        emptyResult: true,
        status: "error",
        error: attempt.llmError,
        ...playerOpt,
      });
      return [];
    }
    if (attempt.beats.length === 0) {
      console.log(`[${logTimestamp()}] [director] morningPlan[${who}] end (no beats from LLM)`);
      this.recordPlanRun({
        runId,
        gameDate: gameDateOf(ctx),
        trigger: "morningPlan",
        promptFull: attempt.prompt,
        llmRawOutput: attempt.responseText,
        producedBeats: [],
        droppedBeats: [],
        emptyResult: true,
        status: "noop",
        ...playerOpt,
      });
      return [];
    }

    const { valid, dropped } = this.validateAndFilter(attempt.beats, ctx, reserved);
    const truncated = valid.slice(0, budget);
    // 超出每日上限的截断也是"丢弃"，同样留痕 + 留日志。
    const overflow = valid.slice(budget);
    for (const raw of overflow) {
      console.log(`[${logTimestamp()}] [director] 丢弃 beat npc=${raw.npcName}: 超出每日上限 budget=${budget}`);
      dropped.push({ npcName: raw.npcName, directive: raw.directive, reasonDropped: `超出每日上限 budget=${budget}` });
    }
    const produced = this.persistAll(truncated, ctx, playerId);
    for (const beat of produced) {
      reserved.add(beat.npcName);
    }
    console.log(`[${logTimestamp()}] [director] morningPlan[${who}] end produced=${produced.length} dropped=${attempt.beats.length - valid.length + overflow.length}`);

    this.recordPlanRun({
      runId,
      gameDate: gameDateOf(ctx),
      trigger: "morningPlan",
      promptFull: attempt.prompt,
      llmRawOutput: attempt.responseText,
      producedBeats: produced,
      droppedBeats: dropped,
      emptyResult: produced.length === 0,
      status: "completed",
      ...playerOpt,
    });
    return produced;
  }

  /**
   * M3：从候选名单解析出真正要编排的玩家（去空、去重、剔除 `_legacy` 占位键、限流）。
   * 空结果 = 走单玩家路径。
   */
  private resolveTargetPlayers(playerIds?: string[]): string[] {
    if (!playerIds || playerIds.length === 0) return [];
    const out: string[] = [];
    for (const id of playerIds) {
      // `_legacy` 是旧单玩家画像的归属标记，不是活人：不参与 per-player 编排。
      if (!id || id === LEGACY_PLAYER_ID) continue;
      if (!out.includes(id)) out.push(id);
    }
    return out.slice(0, this.maxPlayersPerPlan);
  }

  /**
   * React to a player activity milestone. Returns 0 or 1 beat.
   *
   * Steps (spec §6.4): same as morningPlan but capped at 1 beat.
   * 与 morningPlan 同样全路径落 director_runs。
   */
  async milestoneReact(milestone: {
    type: string;
    description: string;
    detectedAt: string;
  }, options: DirectorMilestoneOptions = {}): Promise<Beat | null> {
    const runId = crypto.randomUUID();
    // M3：里程碑天然归属单个玩家（谁的里程碑就按谁的画像编排）。
    const playerId = options.playerId;
    const playerOpt = playerId !== undefined ? { playerId } : {};

    const ctx = this.gameCtxMgr.getCurrent();
    if (!ctx) {
      this.recordPlanRun({
        runId,
        gameDate: "",
        trigger: "milestoneReact",
        producedBeats: [],
        droppedBeats: [],
        emptyResult: true,
        status: "noop",
        error: "no game context",
        ...playerOpt,
      });
      return null;
    }
    if (ctx.time.isFestivalDay) {
      this.recordPlanRun({
        runId,
        gameDate: gameDateOf(ctx),
        trigger: "milestoneReact",
        producedBeats: [],
        droppedBeats: [],
        emptyResult: true,
        status: "noop",
        error: "festival day",
        ...playerOpt,
      });
      return null;
    }

    const attempt = await this.callLlmForBeats(ctx, /* isMilestone */ true, milestone, playerId);
    if (attempt.llmError !== undefined) {
      this.recordPlanRun({
        runId,
        gameDate: gameDateOf(ctx),
        trigger: "milestoneReact",
        promptFull: attempt.prompt,
        producedBeats: [],
        droppedBeats: [],
        emptyResult: true,
        status: "error",
        error: attempt.llmError,
        ...playerOpt,
      });
      return null;
    }
    if (attempt.beats.length === 0) {
      this.recordPlanRun({
        runId,
        gameDate: gameDateOf(ctx),
        trigger: "milestoneReact",
        promptFull: attempt.prompt,
        llmRawOutput: attempt.responseText,
        producedBeats: [],
        droppedBeats: [],
        emptyResult: true,
        status: "noop",
        ...playerOpt,
      });
      return null;
    }

    const { valid, dropped } = this.validateAndFilter(attempt.beats, ctx);
    if (valid.length === 0) {
      this.recordPlanRun({
        runId,
        gameDate: gameDateOf(ctx),
        trigger: "milestoneReact",
        promptFull: attempt.prompt,
        llmRawOutput: attempt.responseText,
        producedBeats: [],
        droppedBeats: dropped,
        emptyResult: true,
        status: "completed",
        ...playerOpt,
      });
      return null;
    }
    // Milestone react always returns at most 1 beat.
    const beats = this.persistAll([valid[0]!], ctx, playerId);
    const produced = beats[0] ?? null;
    this.recordPlanRun({
      runId,
      gameDate: gameDateOf(ctx),
      trigger: "milestoneReact",
      promptFull: attempt.prompt,
      llmRawOutput: attempt.responseText,
      producedBeats: produced ? [produced] : [],
      droppedBeats: dropped,
      emptyResult: produced === null,
      status: "completed",
      ...playerOpt,
    });
    return produced;
  }

  // ------------------------------------------------------------------
  // 全量留痕（E1-1）：director_runs 的写接缝
  // ------------------------------------------------------------------

  /**
   * 记录一次导演调度到 director_runs —— 由 morningPlan / milestoneReact 在
   * 每条退出路径自动调用（全量留痕铁律：prompt 原文 + LLM 原始输出 + 产出/
   * 丢弃 beat 含原因）。也保留 public 供未来入口点（如工具型 Director 循环）
   * 复用。无 store 注入时直接返回（不写不抛，零开销接缝）；写失败也不外抛
   * （recordDirectorRun 内部已吞异常）。
   */
  recordPlanRun(opts: DirectorPlanRunOptions): void {
    if (this.transcriptStore === undefined) return;
    const rec: DirectorRunRecord = {
      runId: opts.runId,
      gameDate: opts.gameDate,
      trigger: opts.trigger,
      promptFull: opts.promptFull ?? "",
      producedBeats: opts.producedBeats,
      droppedBeats: opts.droppedBeats,
      emptyResult: opts.emptyResult,
      status: opts.status,
    };
    // exactOptionalPropertyTypes：可选字段只在有值时挂载。
    if (opts.llmRawOutput !== undefined) rec.llmRawOutput = opts.llmRawOutput;
    if (opts.error !== undefined) rec.error = opts.error;
    if (opts.playerId !== undefined) rec.playerId = opts.playerId;
    this.transcriptStore.recordDirectorRun(rec);
  }

  // ------------------------------------------------------------------
  // LLM call + parsing
  // ------------------------------------------------------------------

  /**
   * Calls the LLM with the appropriate prompt (morning vs milestone)
   * and parses the JSON array response. Returns the attempt (prompt, raw
   * response, parsed beats); llmError is set when the call threw — the
   * caller treats that as "no beats today".
   */
  private async callLlmForBeats(
    ctx: GameContext,
    isMilestone: boolean,
    milestone?: { type: string; description: string; detectedAt: string },
    playerId?: string,
  ): Promise<LlmAttempt> {
    const prompt = isMilestone && milestone
      ? this.buildMilestonePrompt(ctx, milestone, playerId)
      : this.buildMorningPrompt(ctx, playerId);

    console.log(`[${logTimestamp()}] [director] prompt输入 (${isMilestone ? "milestone" : "morning"}, ${prompt.length} chars):\n${prompt}`);

    let responseText: string;
    try {
      const result = await this.callLlm(prompt);
      responseText = result.text;
      console.log(`[${logTimestamp()}] [director] llm输出 (tokens: prompt=${result.usage.promptTokens} completion=${result.usage.completionTokens}):\n${responseText}`);
    } catch (err) {
      console.error(`[${logTimestamp()}] [director] LLM 调用失败:`, err);
      return { prompt, responseText: "", beats: [], llmError: String(err) };
    }
    return { prompt, responseText, beats: this.parseBeatsArray(responseText) };
  }

  /**
   * Extracts and parses a JSON array from the LLM response text.
   * Tolerant of surrounding prose (spec §6.3 — "尝试提取 [...] 子串").
   * Returns an empty array if no valid array can be extracted.
   */
  private parseBeatsArray(text: string): RawBeatOutput[] {
    const trimmed = text.trim();
    if (!trimmed) {
      console.log(`[${logTimestamp()}] [director] parseBeatsArray: 空响应，返回空数组`);
      return [];
    }

    // Direct parse — most LLMs return clean JSON when prompted correctly.
    const direct = tryParseArray(trimmed);
    if (direct !== null) {
      console.log(`[${logTimestamp()}] [director] parseBeatsArray: 直接解析成功，${direct.length} 个 beat`);
      return direct;
    }

    // Extract the first [...] substring (tolerant mode).
    const start = trimmed.indexOf("[");
    const end = trimmed.lastIndexOf("]");
    if (start === -1 || end === -1 || end <= start) {
      console.log(`[${logTimestamp()}] [director] parseBeatsArray: 未找到 JSON 数组边界，返回空数组。原文前200字符: ${trimmed.slice(0, 200)}`);
      return [];
    }
    const slice = trimmed.slice(start, end + 1);
    const extracted = tryParseArray(slice);
    if (extracted === null) {
      console.log(`[${logTimestamp()}] [director] parseBeatsArray: 提取子串后仍解析失败，返回空数组。子串前200字符: ${slice.slice(0, 200)}`);
      return [];
    }
    console.log(`[${logTimestamp()}] [director] parseBeatsArray: 容错提取解析成功，${extracted.length} 个 beat`);
    return extracted;
  }

  // ------------------------------------------------------------------
  // Validation
  // ------------------------------------------------------------------

  /**
   * Apply spec §6.3 validation rules:
   *   - npcName is in GameContext.npcStates and isAvailable
   *   - triggerTime and windowEnd match HH:MM
   *   - windowEnd > triggerTime (by at least MIN_WINDOW_MINUTES)
   *   - directive non-empty and ≤ MAX_DIRECTIVE_LENGTH
   *   - same NPC not already in this batch (dedupe)
   *   - NPC not on cooldown (no beat in last npcCooldownDays days)
   *   - rhythm budget not exhausted (spec §6.1 step 4)
   *
   * 返回 {valid, dropped}：dropped 携带每条丢弃原因（日志 + director_runs
   * 留痕共用同一份事实）。
   */
  private validateAndFilter(
    rawBeats: RawBeatOutput[],
    ctx: GameContext,
    /** M3：已被同轮其他玩家占用的 NPC（跨玩家去重）。 */
    reservedNpcs?: Set<string>,
  ): FilterResult {
    const recentBeats = this.beatStore.listRecent(this.maxRecentBeatsForBudget);
    const seenNpcs = new Set<string>();
    const valid: RawBeatOutput[] = [];
    const dropped: DroppedBeat[] = [];
    console.log(`[${logTimestamp()}] [director] validateAndFilter: 输入 ${rawBeats.length} 个 beat，recentBeats=${recentBeats.length}`);

    for (const raw of rawBeats) {
      // Dedupe within this batch.
      if (seenNpcs.has(raw.npcName)) {
        console.log(`[${logTimestamp()}] [director] 丢弃 beat npc=${raw.npcName}: 本批次重复`);
        dropped.push({ npcName: raw.npcName, directive: raw.directive, reasonDropped: "本批次重复" });
        continue;
      }

      // M3：同一天已被前面的玩家编排过的 NPC 不再重复安排（节奏预算按世界算，不按玩家算）。
      if (reservedNpcs?.has(raw.npcName)) {
        console.log(`[${logTimestamp()}] [director] 丢弃 beat npc=${raw.npcName}: 本轮已为其他玩家编排过该 NPC`);
        dropped.push({ npcName: raw.npcName, directive: raw.directive, reasonDropped: "本轮已为其他玩家编排过该 NPC" });
        continue;
      }

      // NPC availability.
      const npcState = ctx.npcStates.find((n) => n.name === raw.npcName);
      if (!npcState) {
        console.log(`[${logTimestamp()}] [director] 丢弃 beat npc=${raw.npcName}: NPC 不在 gameContext 中`);
        dropped.push({ npcName: raw.npcName, directive: raw.directive, reasonDropped: "NPC 不在 gameContext 中" });
        continue;
      }
      if (!npcState.isAvailable) {
        console.log(`[${logTimestamp()}] [director] 丢弃 beat npc=${raw.npcName}: NPC 不可用 (isAvailable=false)`);
        dropped.push({ npcName: raw.npcName, directive: raw.directive, reasonDropped: "NPC 不可用 (isAvailable=false)" });
        continue;
      }

      // Time format.
      if (!isValidTime(raw.triggerTime) || !isValidTime(raw.windowEnd)) {
        console.log(`[${logTimestamp()}] [director] 丢弃 beat npc=${raw.npcName}: 时间格式非法 triggerTime="${raw.triggerTime}" windowEnd="${raw.windowEnd}"`);
        dropped.push({ npcName: raw.npcName, directive: raw.directive, reasonDropped: `时间格式非法 triggerTime="${raw.triggerTime}" windowEnd="${raw.windowEnd}"` });
        continue;
      }

      // Window ordering.
      if (!windowIsAfter(raw.triggerTime, raw.windowEnd, MIN_WINDOW_MINUTES)) {
        console.log(`[${logTimestamp()}] [director] 丢弃 beat npc=${raw.npcName}: 窗口时长不足 ${MIN_WINDOW_MINUTES} 分钟 triggerTime="${raw.triggerTime}" windowEnd="${raw.windowEnd}"`);
        dropped.push({ npcName: raw.npcName, directive: raw.directive, reasonDropped: `窗口时长不足 ${MIN_WINDOW_MINUTES} 分钟 triggerTime="${raw.triggerTime}" windowEnd="${raw.windowEnd}"` });
        continue;
      }

      // Directive constraints.
      if (!raw.directive || raw.directive.length === 0) {
        console.log(`[${logTimestamp()}] [director] 丢弃 beat npc=${raw.npcName}: directive 为空`);
        dropped.push({ npcName: raw.npcName, directive: raw.directive, reasonDropped: "directive 为空" });
        continue;
      }
      if (raw.directive.length > MAX_DIRECTIVE_LENGTH) {
        console.log(`[${logTimestamp()}] [director] 丢弃 beat npc=${raw.npcName}: directive 过长 (${raw.directive.length} > ${MAX_DIRECTIVE_LENGTH})`);
        dropped.push({ npcName: raw.npcName, directive: raw.directive, reasonDropped: `directive 过长 (${raw.directive.length} > ${MAX_DIRECTIVE_LENGTH})` });
        continue;
      }

      // NPC cooldown — drop if any beat for this NPC exists in recent history.
      if (recentBeats.some((b) => b.npcName === raw.npcName)) {
        console.log(`[${logTimestamp()}] [director] 丢弃 beat npc=${raw.npcName}: NPC 冷却中 (recent history 命中)`);
        dropped.push({ npcName: raw.npcName, directive: raw.directive, reasonDropped: "NPC 冷却中 (recent history 命中)" });
        continue;
      }

      seenNpcs.add(raw.npcName);
      valid.push(raw);
      console.log(`[${logTimestamp()}] [director] 保留 beat npc=${raw.npcName} triggerTime="${raw.triggerTime}" directive="${raw.directive}"`);
    }
    console.log(`[${logTimestamp()}] [director] validateAndFilter: 输出 ${valid.length} 个 beat (丢弃 ${dropped.length})`);
    return { valid, dropped };
  }

  // ------------------------------------------------------------------
  // Persistence
  // ------------------------------------------------------------------

  private persistAll(rawBeats: RawBeatOutput[], ctx: GameContext, playerId?: string): Beat[] {
    // M3：画像快照取该玩家的画像（无 playerId → 缺省/legacy 玩家，等价现状）。
    const profileSnapshot = this.profileMgr.profileStore.loadOrClaim(playerId) ?? emptyProfile();
    const recentBeats = this.beatStore.listRecent(3);
    const beats: Beat[] = [];
    for (const raw of rawBeats) {
      const beat = this.buildBeat(raw, profileSnapshot, ctx, recentBeats, playerId);
      this.beatStore.save(beat);
      beats.push(beat);
    }
    return beats;
  }

  private buildBeat(
    raw: RawBeatOutput,
    profile: PlayerProfile,
    ctx: GameContext,
    recentBeats: Beat[],
    playerId?: string,
  ): Beat {
    return {
      id: crypto.randomUUID(),
      npcName: raw.npcName,
      triggerTime: raw.triggerTime,
      windowEnd: raw.windowEnd,
      directive: raw.directive,
      context: {
        reasonGenerated: raw.reasonGenerated,
        playerProfileSnapshot: profile,
        gameContextSnapshot: ctx,
        recentBeats,
        // M3：beat 归属玩家（旧数据无此字段，读取方按可选处理）。
        ...(playerId !== undefined ? { playerId } : {}),
      },
      status: "scheduled" as BeatStatus,
    };
  }

  // ------------------------------------------------------------------
  // Prompt construction
  // ------------------------------------------------------------------

  private buildMorningPrompt(ctx: GameContext, playerId?: string): string {
    const profileSummary = this.profileMgr.summarizeForDirector(playerId);
    const gameCtxSummary = this.gameCtxMgr.summarizeForDirector();
    const recentBeats = this.beatStore.listRecent(this.maxRecentBeatsForBudget);
    const recentBeatsSummary = recentBeats.length > 0
      ? recentBeats.map((b) => `${b.npcName}(${b.status},${b.triggerTime}): ${b.directive}`).join("; ")
      : "(无)";

    // Enrich the prompt with the player's last 3 days of activity stats so
    // the Director can ground its beats in what the player actually did.
    const recentActivities = this.activityStore.listRecent(3);
    const activitySummary = recentActivities.length > 0
      ? recentActivities
          .map((a) => `${a.date}: 钓鱼${a.fishingMinutes}m/种地${a.farmingMinutes}m/挖矿${a.miningMinutes}m/战斗${a.combatMinutes}m/社交${a.socialMinutes}m`)
          .join("\n")
      : "(无)";

    const availableNpcs = ctx.npcStates
      .filter((n) => n.isAvailable)
      .map((n) => n.name)
      .join(", ");

    return [
      "你是星露谷的叙事导演。你的职责是设计 NPC 与玩家之间有意义的互动故事，",
      "让玩家感觉到这个世界是活的、NPC 是关心他的。",
      "",
      profileSummary.trim(),
      gameCtxSummary.trim(),
      "",
      "[最近 3 天活动]",
      activitySummary,
      "",
      "[节奏约束]",
      `最近 ${this.maxRecentBeatsForBudget} 天已发的 beat: ${recentBeatsSummary}`,
      `可用 NPC: ${availableNpcs || "(无)"}`,
      "",
      "[设计原则]",
      "1. 不要每天都安排特殊故事——新鲜感来自稀缺，隔几天一次最好",
      "2. beat 要与玩家当前的行为和偏好相关",
      "3. 避免重复套路",
      "4. beat 要符合 NPC 人设",
      "5. 尊重游戏节奏——节日/婚礼等特殊场景不发 beat",
      "6. 高层指令只给方向，不写具体台词和动作",
      "",
      "[输出格式]",
      "输出 JSON 数组，0-3 个 beat。每个 beat 包含:",
      "- npcName: NPC 名（必须从可用 NPC 列表中选）",
      "- triggerTime: 当天触发时间 \"HH:MM\"",
      "- windowEnd: 窗口结束 \"HH:MM\"（至少比 triggerTime 晚 1 小时）",
      "- directive: 高层指令（1-2 句自然语言，给方向不给细节）",
      "- reasonGenerated: 为什么选这个 NPC 和这个 beat",
      "",
      "如果没有合适的 beat，输出空数组 []。",
      "直接输出 JSON 数组，不要包裹在 markdown 代码块中，不要任何前后缀文字。",
    ].join("\n");
  }

  private buildMilestonePrompt(
    ctx: GameContext,
    milestone: { type: string; description: string; detectedAt: string },
    playerId?: string,
  ): string {
    const profileSummary = this.profileMgr.summarizeForDirector(playerId);
    const gameCtxSummary = this.gameCtxMgr.summarizeForDirector();
    const recentBeats = this.beatStore.listRecent(this.maxRecentBeatsForBudget);
    const recentBeatsSummary = recentBeats.length > 0
      ? recentBeats.map((b) => `${b.npcName}(${b.status}): ${b.directive}`).join("; ")
      : "(无)";

    const availableNpcs = ctx.npcStates
      .filter((n) => n.isAvailable)
      .map((n) => n.name)
      .join(", ");

    return [
      "玩家刚刚达成了一个行为里程碑，你需要判断是否值得为此生成一个即兴 NPC 互动。",
      "",
      profileSummary.trim(),
      gameCtxSummary.trim(),
      "",
      "[里程碑事件]",
      milestone.description,
      "",
      "[节奏约束]",
      `最近 ${this.maxRecentBeatsForBudget} 天已发的 beat: ${recentBeatsSummary}`,
      `可用 NPC: ${availableNpcs || "(无)"}`,
      "",
      "[判断原则]",
      "1. 里程碑要与 NPC 有关联——钓鱼里程碑适合 Willy/Abigail，不适合 Sebastian",
      "2. 节奏预算未耗尽才发",
      "3. 即兴 beat 要比计划 beat 更轻量——一个简短的问候/帮助/好奇就够",
      "4. 如果里程碑不值得即兴反应，输出空数组 []",
      "",
      "[输出格式]",
      "输出 JSON 数组，0 或 1 个 beat。每个 beat 包含:",
      "- npcName: NPC 名（必须从可用 NPC 列表中选）",
      "- triggerTime: 当天触发时间 \"HH:MM\"",
      "- windowEnd: 窗口结束 \"HH:MM\"（至少比 triggerTime 晚 1 小时）",
      "- directive: 高层指令（1-2 句自然语言）",
      "- reasonGenerated: 为什么选这个 NPC 和这个 beat",
      "",
      "直接输出 JSON 数组，不要包裹在 markdown 代码块中，不要任何前后缀文字。",
    ].join("\n");
  }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function tryParseArray(text: string): RawBeatOutput[] | null {
  try {
    const parsed = JSON.parse(text);
    if (!Array.isArray(parsed)) return null;
    // Filter to objects with required fields.
    return parsed.filter(isRawBeatOutput);
  } catch {
    return null;
  }
}

function isRawBeatOutput(value: unknown): value is RawBeatOutput {
  if (typeof value !== "object" || value === null) return false;
  const v = value as Record<string, unknown>;
  return (
    typeof v.npcName === "string" &&
    typeof v.triggerTime === "string" &&
    typeof v.windowEnd === "string" &&
    typeof v.directive === "string" &&
    typeof v.reasonGenerated === "string"
  );
}

/** Validates "HH:MM" format with 0-23 hours and 0-59 minutes. */
function isValidTime(t: string): boolean {
  const match = /^(\d{2}):(\d{2})$/.exec(t);
  if (!match) return false;
  const hh = parseInt(match[1]!, 10);
  const mm = parseInt(match[2]!, 10);
  return hh >= 0 && hh <= 23 && mm >= 0 && mm <= 59;
}

/**
 * Returns true if `windowEnd` is at least `minMinutes` later than `triggerTime`.
 * Both inputs must be valid "HH:MM" strings.
 */
function windowIsAfter(triggerTime: string, windowEnd: string, minMinutes: number): boolean {
  const t = parseTimeToMinutes(triggerTime);
  const w = parseTimeToMinutes(windowEnd);
  if (t === null || w === null) return false;
  return w - t >= minMinutes;
}

function parseTimeToMinutes(t: string): number | null {
  const match = /^(\d{2}):(\d{2})$/.exec(t);
  if (!match) return null;
  const hh = parseInt(match[1]!, 10);
  const mm = parseInt(match[2]!, 10);
  if (hh < 0 || hh > 23 || mm < 0 || mm > 59) return null;
  return hh * 60 + mm;
}

function emptyProfile(): PlayerProfile {
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
