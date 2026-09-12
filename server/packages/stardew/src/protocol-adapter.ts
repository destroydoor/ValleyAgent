import type { StardewAgentRegistry, ToolResultRecord } from "./stardew-agent-registry";
import { RuleEngine } from "./rule-engine";
import { decodeWorldSnapshot } from "./world-snapshot-decoder";

import type {
  DialogueRequest,
  DialogueResponse,
  HelloRequest,
  HelloResponse,
  PingRequest,
  PongResponse,
  ActionResultMessage,
  StateChangedMessage,
  ConsolidateDayMessage,
  RouteShoutMessage,
  RouteShoutResponse,
  OutgoingMessage,
  DayStartedMessage,
  GameContextSyncMessage,
  AllocateAgentMessage,
  DirectorCommandMessage,
  ExecuteAdjustMessage,
  AdjustResultMessage,
  AdjustOp,
  EconomyExecutor,
  SceneState,
  ReconnectSyncMessage,
} from "./types";
import { AgentLedger } from "./agent-ledger";
import type { AgentMemory } from "./agent-memory";
import { EmotionEngine } from "./emotion-engine";
import { MEMORY_SIDE_EFFECT_RECORDED, DEFAULT_EMOTION } from "./types";
import { MorningShoutRouter } from "./morning-shout-router";
import type { DirectorAgent } from "./director-agent";
import type { GameContextManager } from "./game-context";
import type { PlayerProfileManager } from "./player-profile";

const SERVER_VERSION = "0.1.0";

/** HH:MM:SS 时间戳，用于 cmd 窗口日志前缀。 */
function timestamp(): string {
  return new Date().toISOString().slice(11, 19);
}

/** 截断字符串用于日志输出，超长部分以 "..." 表示。 */
function truncate(s: string, max: number): string {
  return s.length <= max ? s : s.slice(0, max) + "...";
}

export interface ProtocolAdapterOptions {
  sendToCsharp?: (msg: unknown) => void;
  /** 2026-09-12 Director 有效化：工具型导演大脑（原 Director beat-JSON 管线退役）。 */
  directorAgent?: DirectorAgent;
  directorTriggerProbability?: number;
  gameCtxMgr?: GameContextManager;
  /** 玩家画像管理器（consolidate_day 日刷新用；Director 启用时由 server.ts 注入）。 */
  profileMgr?: PlayerProfileManager;
  /** 2026-08-15 账本迁移（步骤 1）：权威经济账本（adjust_result 路由 + worldSnapshot 播种）。 */
  ledger?: AgentLedger;
  /** adjust_result 回执等待超时 ms（默认 10s；测试可调小）。 */
  adjustTimeoutMs?: number;
  /** 超时对账重发间隔 ms（2026-08-17；默认 30s；测试可调小）。 */
  adjustReconcileRetryMs?: number;
  /** 2026-08-15 步骤 3：确定性情绪引擎（事件驱动，Director mood 覆盖权）。 */
  emotionEngine?: EmotionEngine;
}

/** adjust_result 回执等待超时（设计 §6：回执丢失 → 超时回滚 pending，TS 重发靠 instructionId 幂等兜底）。 */
const ADJUST_TIMEOUT_MS = 10_000;
/** 超时对账重发间隔（2026-08-17）：超时后凭原 instructionId 重发，C# 幂等缓存返回原回执。 */
const ADJUST_RECONCILE_RETRY_MS = 30_000;

interface PendingAdjustWaiter {
  promise: Promise<AdjustResultMessage>;
  resolve: (r: AdjustResultMessage) => void;
  timer: ReturnType<typeof setTimeout>;
  npcName: string;
}
export class ProtocolAdapter {
  private readonly ruleEngine = new RuleEngine();
  private readonly seenConsolidations = new Set<string>();
  // 2026-09-11 观测补强：同一游戏日多次 day_started 的重复计数（联机下已知现象，
  // 此前重复跑导演编排无任何日志标记——"每玩家一个导演"误读的直接来源）。
  // adapter 是长生命周期单例（server.ts 只 new 一次），Map 每天仅增 1 key，无增长忧。
  private readonly seenDayStartedDates = new Map<string, number>();
  // 2026-09-12 consolidate_day：当日玩家画像 LLM 刷新是否已触发（每日期一次，
  // C# 会逐 NPC 发送 consolidate_day，画像刷新只需一次）。
  private readonly consolidatedProfileDates = new Set<string>();
  private pendingSaves: Promise<void>[] = [];
  private readonly shoutRouter = new MorningShoutRouter();
  // Phase 2: set_goal 意图暂存（callId → 目标类型）。action_result 不带工具参数，
  // 目标记忆需要 type，故在 dialogue_response 发出时按 callId 暂存，回执到达后消费。
  private readonly pendingGoals = new Map<string, string>();
  // 2026-08-15 账本迁移（步骤 1）：execute_adjust 回执等待表（instructionId → waiter）。
  // sendAdjust 挂等待；adjust_result 到达时按 instructionId 解析（超时 10s 回滚 pending）。
  private readonly pendingAdjusts = new Map<string, PendingAdjustWaiter>();
  // 2026-08-17 对账闭环：超时后待重发的指令（instructionId → msg）。超时**不进终态**
  // （pending 保留、账本不回滚）——C# 可能已真实执行，回滚会让 LLM 重试导致双倍扣钱；
  // 30s 后凭原 instructionId 重发，C# 幂等缓存命中返回原回执 → 正常 commit/rollback 闭环。
  private readonly pendingReconciles = new Map<string, ExecuteAdjustMessage>();

  constructor(
    private readonly registry: StardewAgentRegistry,
    private readonly options?: ProtocolAdapterOptions,
  ) {}

  async handleHello(req: HelloRequest): Promise<HelloResponse> {
    console.log(`[${timestamp()}] [recv] hello modVersion=${(req as { modVersion?: string }).modVersion ?? "?"}`);
    return {
      type: "hello",
      requestId: req.requestId,
      status: "ok",
      serverVersion: SERVER_VERSION,
    };
  }

  async handlePing(req: PingRequest): Promise<PongResponse> {
    return {
      type: "pong",
      requestId: req.requestId,
    };
  }

  async handleActionResult(req: ActionResultMessage): Promise<{ type: "ack"; requestId: string }> {
    // 2026-08-15 步骤 3：工具执行结果 → 情绪推导（chop_tree/set_goal 成败）。
    if (req.npcName) {
      this.options?.emotionEngine?.applyEvent(req.npcName, { kind: "action_result", ...(req.tool !== undefined ? { detail: req.tool } : {}), success: req.success });
    }
    const reason = (req as { reason?: string }).reason;
    console.log(`[${timestamp()}] [recv] action_result callId=${req.callId} ok=${req.success}${reason ? ` reason=${reason}` : ""}${req.result ? ` result="${req.result}"` : ""}`);
    this.routeToolResult(req);
    return { type: "ack", requestId: req.requestId };
  }

  /**
   * Handle state_changed message (fire-and-forget) from C# StateChangedSender.
   * Updates the per-NPC actualState mirror in the registry so the next
   * dialogue prompt includes the {actual_state_section}.
   * Returns ack - C# does not register a pending request for this type.
   */
  async handleStateChanged(req: StateChangedMessage): Promise<{ type: "ack"; requestId: string }> {
    this.registry.updateActualState(req.npcName, req.newState, req.reason);
    // 2026-08-15 步骤 3：状态转换事件 → 情绪推导（travel_failed/evicted/task_completed 等 reason 映射）。
    this.options?.emotionEngine?.applyEvent(req.npcName, { kind: "state_changed", ...(req.reason !== undefined ? { detail: req.reason } : {}) });
    const forced = req.wasForced ? " (forced)" : "";
    const reason = req.reason ? ` reason=${req.reason}` : "";
    console.log(`[${timestamp()}] [recv] state_changed npc=${req.npcName} ${req.previousState}->${req.newState}${forced}${reason}`);
    return { type: "ack", requestId: (req as { requestId?: string }).requestId ?? "unknown" };
  }

  /**
   * Handle day_started (fire-and-forget) from C# DayStarted event.
   * Rolls against directorTriggerProbability (default 0.1). If triggered,
   * runs DirectorAgent.runDayPlan() (tool brain: 9 director tools via
   * director_command) and sends one allocate_agent per spawned beat so the
   * beat NPC stays allocated for the beat window. Returns ack — C# does not
   * register a pending request for this type.
   */
  async handleDayStarted(req: DayStartedMessage): Promise<{ type: "ack"; requestId: string }> {
    // 2026-09-11 观测补强：重复 day_started 只告警不去重（去重是行为修复，本批
    // 仅观测——已知联机现象：情绪重置 + 导演编排会对该日期重复执行）。
    const seen = (this.seenDayStartedDates.get(req.dateIso) ?? 0) + 1;
    this.seenDayStartedDates.set(req.dateIso, seen);
    if (seen > 1) {
      console.warn(`[protocol] duplicate day_started date=${req.dateIso} (count=${seen}) — known multiplayer artifact: emotion reset + director dayPlan will run again for this date`);
    }
    // 2026-08-17 步骤 3 补全：换日重置所有 NPC 情绪回 baseline（引擎枚举 day_started
    // 事件类别、resetDay 产出 source:"day_started"，但此前从未接线——昨天的情绪
    // 会一直挂进今天的 prompt。重置与 Director 是否接线无关，放最前无条件执行）。
    this.options?.emotionEngine?.resetAll();
    // directorContext 由 C# DirectorContextBuilder 拼装：2026-09-12 起被
    // DirectorAgent 消费（此前接收即丢弃）。
    const dctxLen = req.directorContext?.length;
    console.log(`[${timestamp()}] [recv] day_started date=${req.dateIso} directorContext=${dctxLen !== undefined ? `${dctxLen} chars` : "(none)"}`);
    const directorAgent = this.options?.directorAgent;
    const sendToCsharp = this.options?.sendToCsharp;
    if (!directorAgent || !sendToCsharp) {
      console.log(`[${timestamp()}] [director] skipped: directorAgent=${directorAgent ? "yes" : "no"} sendToCsharp=${sendToCsharp ? "yes" : "no"}`);
      return { type: "ack", requestId: req.requestId };
    }
    const probability = this.options?.directorTriggerProbability ?? 0.1;
    const roll = Math.random();
    if (roll >= probability) {
      console.log(`[${timestamp()}] [director] skipped: probability=${probability} roll=${roll.toFixed(4)} (未命中，跳过 dayPlan)`);
      return { type: "ack", requestId: req.requestId };
    }
    console.log(`[${timestamp()}] [director] triggered: probability=${probability} roll=${roll.toFixed(4)} (命中，调用 DirectorAgent.runDayPlan)`);
    try {
      const summary = await directorAgent.runDayPlan(req.directorContext);
      console.log(`[${timestamp()}] [director] dayPlan 完成: beats=${summary.spawnedBeats.length} commands=${summary.commandsSent} rejected=${summary.rejectedCalls.length}`);
      // 每个 spawned beat 发 allocate_agent 保活（KeepUntil 豁免互动空闲淘汰）。
      // keepUntilIso 必须是 ISO 8601——旧实现传 "16:00" 非 ISO，C# 解析 fail-open
      // 等于没有豁免（2026-09-12 修复）。
      for (const beat of summary.spawnedBeats) {
        const keepUntilIso = new Date(Date.now() + beat.durationMinutes * 60_000).toISOString();
        const msg: AllocateAgentMessage = {
          type: "allocate_agent",
          requestId: crypto.randomUUID(),
          npcName: beat.npcName,
          keepUntilIso,
        };
        console.log(`[${timestamp()}] [send] allocate_agent npc=${beat.npcName} keepUntil=${keepUntilIso} (${beat.durationMinutes}min)`);
        sendToCsharp(msg);
      }
    } catch (err) {
      console.error(`[${timestamp()}] [director] dayPlan 失败:`, err);
    }
    return { type: "ack", requestId: req.requestId };
  }

  /**
   * Handle game_context_sync (fire-and-forget) from C# GameContextSender.
   * Pushes the game-world snapshot into GameContextManager so that
   * DirectorAgent.runDayPlan can read real game state instead
   * of falling into the no-game-context branch. Returns ack — C# does not
   * register a pending request for this type.
   */
  async handleGameContextSync(req: GameContextSyncMessage): Promise<{ type: "ack"; requestId: string }> {
    const ctx = this.options?.gameCtxMgr;
    if (!ctx) {
      console.log(`[${timestamp()}] [game_context_sync] dropped: gameCtxMgr not wired`);
      return { type: "ack", requestId: req.requestId };
    }
    ctx.update(req.context);
    console.log(`[${timestamp()}] [game_context_sync] updated: Y${req.context.time.year} ${req.context.time.season} ${req.context.time.day} weather=${req.context.time.weather} npcStates=${req.context.npcStates.length} playerMoney=${req.context.playerState.money}`);
    return { type: "ack", requestId: req.requestId };
  }

  /**
   * Phase 3 Director 工具调用（TS→C#，orphan_route：C# 从不发送，本 case 仅为
   * check:protocol 的路由面完整性保留——收到同名消息时原样转发 C#）。
   * 生产路径：DirectorAgent（2026-09-12 工具大脑）→ sendToCsharp 直接下发
   * director_command → C# DirectorTools.Execute（CommandExecutor 特殊路由，
   * 不走 NPC Agent switch）。sendToCsharp 未配置时丢弃并记日志（降级不静默）。
   * 返回 ack — C# 侧为 fire-and-forget，执行结果仅落 SMAPI 日志（回执
   * action_result 的 NpcName 为空，被本端 routeToolResult 安全丢弃——见
   * types.ts DirectorCommandMessage 的 npcName 禁填说明）。
   */
  async handleDirectorCommand(req: DirectorCommandMessage): Promise<{ type: "ack"; requestId: string }> {
    const sendToCsharp = this.options?.sendToCsharp;
    if (!sendToCsharp) {
      console.log(`[${timestamp()}] [director_command] dropped: sendToCsharp 未配置 tool=${req.tool}`);
      return { type: "ack", requestId: req.requestId };
    }
    console.log(`[${timestamp()}] [send] director_command tool=${req.tool} requestId=${req.requestId} args=${truncate(JSON.stringify(req.args ?? {}), 120)}`);
    sendToCsharp(req);
    return { type: "ack", requestId: req.requestId };
  }

  /**
   * 断线重连对账（C#→TS，2026-08-15 步骤 4）。C# 重连成功补发 outbox 后发送；
   * TS 侧把 in-flight（status=pending）的 adjust 凭原 instructionId 重发——
   * C# 幂等缓存返回原回执（已执行则缓存，未执行则新执行），pending → committed/rolled_back 闭环。
   * 断线期间不会有新经济指令（TS 不可达），账本天然无漂移，只处理 in-flight 批次。
   */
  async handleReconnectSync(req: ReconnectSyncMessage): Promise<{ type: "ack"; requestId: string }> {
    console.log(`[${timestamp()}] [recv] reconnect_sync replayed=${req.replayedOutbox} agents=${req.agents.length} date=${req.gameDate ?? "?"}`);
    const ledger = this.options?.ledger;
    if (!ledger) {
      console.log(`[${timestamp()}] [reconnect_sync] dropped: ledger not wired`);
      return { type: "ack", requestId: req.requestId };
    }

    // 对账范围：只重发消息里列出的 agent 的 pending（其余 NPC 无 in-flight 指令）。
    const scope = new Set(req.agents.map((n) => n.toLowerCase()));
    let replayed = 0;
    for (const { npcName, pending } of ledger.listPending()) {
      if (!scope.has(npcName.toLowerCase())) continue;
      console.log(`[${timestamp()}] [reconnect_sync] reconciling ${npcName}: instruction=${pending.instructionId} ops=${pending.ops.length}`);
      const msg: ExecuteAdjustMessage = {
        type: "execute_adjust",
        requestId: pending.requestId,
        instructionId: pending.instructionId,
        npcName,
        ops: pending.ops,
        ...(pending.playerId !== undefined ? { playerId: pending.playerId } : {}),
      };
      const existing = this.pendingAdjusts.get(pending.instructionId);
      if (existing) {
        // waiter 还在（未超时）：sendAdjust 的幂等去重会挡住重发——这里绕过去重直接重投，
        // 回执到达时由原 waiter 解析（同一 promise）。
        this.options?.sendToCsharp?.(msg);
      } else {
        // waiter 已超时清除：重新挂等待 + 重发（C# 幂等返回缓存回执）。
        void this.sendAdjust(msg);
      }
      replayed++;
    }
    console.log(`[${timestamp()}] [reconnect_sync] reconciled ${replayed} pending adjust(s)`);
    return { type: "ack", requestId: req.requestId };
  }

  /**
   * execute_adjust 原子批指令（TS→C#，2026-08-15 账本迁移设计 §4.1）。
   * routeMessage 统一入口：实际发送经 sendToCsharp 通道；回执等待/超时回滚由
   * {@link sendAdjust} 管理（本 handler 只负责投递，供未来工具/路由复用）。
   * 返回 ack — C# 侧回执走 adjust_result 消息。
   */
  async handleExecuteAdjust(req: ExecuteAdjustMessage): Promise<{ type: "ack"; requestId: string }> {
    const sendToCsharp = this.options?.sendToCsharp;
    if (!sendToCsharp) {
      console.log(`[${timestamp()}] [execute_adjust] dropped: sendToCsharp 未配置 instruction=${req.instructionId} ops=${req.ops.length}`);
      return { type: "ack", requestId: req.requestId };
    }
    console.log(`[${timestamp()}] [send] execute_adjust instruction=${req.instructionId} npc=${req.npcName} ops=${req.ops.length}`);
    sendToCsharp(req);
    return { type: "ack", requestId: req.requestId };
  }

  /**
   * 同步编排经济原子批（2026-08-15 账本迁移步骤 2，设计 §4.1 交易闭环）。
   * 流程：账本业务校验（beginPending，失败 → 合成失败回执，不记 pending）→ execute_adjust
   * 下发 → await 回执（10s 超时回滚）。账本入账/回滚由 handleAdjustResult 在回执到达时驱动。
   * 经济工具（trade/give_item/give_gift/receive_payment）的同步执行入口。
   */
  async adjustEconomy(npcName: string, ops: AdjustOp[], playerId?: string): Promise<AdjustResultMessage> {
    const ledger = this.options?.ledger;
    const requestId = crypto.randomUUID();
    const instructionId = `adj-${requestId}`;

    if (ledger) {
      await ledger.load(npcName);
      const begin = ledger.beginPending(npcName, instructionId, requestId, ops, playerId);
      if (!begin.ok) {
        console.log(`[${timestamp()}] [execute_adjust] ${instructionId} business-check failed: ${begin.reason}`);
        return {
          type: "adjust_result",
          requestId,
          instructionId,
          npcName,
          success: false,
          steps: [],
          failureCode: begin.failureCode,
        };
      }
    }

    return this.sendAdjust({
      type: "execute_adjust",
      requestId,
      instructionId,
      npcName,
      ops,
      ...(playerId !== undefined ? { playerId } : {}),
    });
  }

  /**
   * 账本自填（2026-08-15 步骤 2）：对话场景的 npcMoney/npcInventory 以 TS 账本为准
   * （权威），覆盖 worldSnapshot 的 C# 镜像值。账本未播种（未知）时保留镜像值。
   * 播种本身仍来自 worldSnapshot（镜像 = 现实基线），见 handleDialogue。
   */
  private applyLedgerToScene(npcName: string, scene: SceneState): void {
    const ledger = this.options?.ledger;
    if (!ledger) return;
    const state = ledger.getOrCreate(npcName);
    if (!state.seeded) return;

    scene.npcMoney = state.money;
    scene.npcInventory = Object.entries(state.items).map(([key, item]) => ({
      name: item.name || key,
      quantity: item.quantity,
    }));
  }

  /**
   * 下发原子批经济指令并等待 adjust_result 回执（设计 §4.1 交易闭环）。
   * 挂 pending 回执 Promise：超时（10s）→ 账本 rollback + 合成失败回执（LLM 自然改口）；
   * 无 sendToCsharp 通道 → 立即 rollback（降级不静默）。幂等：同 instructionId 复用已挂等待
   * （TS 重发场景，设计 §6：C# 缓存已执行结果，重发直接返回缓存回执）。
   */
  sendAdjust(msg: ExecuteAdjustMessage): Promise<AdjustResultMessage> {
    const existing = this.pendingAdjusts.get(msg.instructionId);
    if (existing) return existing.promise;

    const ledger = this.options?.ledger;
    const fail = (reason: string): AdjustResultMessage => {
      console.log(`[${timestamp()}] [execute_adjust] ${msg.instructionId} failed: ${reason}`);
      if (ledger) {
        ledger.rollback(msg.npcName, msg.instructionId, reason);
        this.pendingSaves.push(
          ledger.save(msg.npcName).catch((err) => {
            console.error(`[execute_adjust] ledger.save failed after rollback for ${msg.npcName}:`, err);
          }),
        );
      }
      return {
        type: "adjust_result",
        requestId: msg.requestId,
        instructionId: msg.instructionId,
        npcName: msg.npcName,
        success: false,
        steps: [],
        failureCode: "internalError",
      };
    };

    const sendToCsharp = this.options?.sendToCsharp;
    if (!sendToCsharp) {
      return Promise.resolve(fail("no sendToCsharp channel"));
    }

    let resolveFn!: (r: AdjustResultMessage) => void;
    const promise = new Promise<AdjustResultMessage>((resolve) => {
      resolveFn = resolve;
    });
    const timeoutMs = this.options?.adjustTimeoutMs ?? ADJUST_TIMEOUT_MS;
    const timer = setTimeout(() => {
      this.pendingAdjusts.delete(msg.instructionId);
      // 2026-08-17 对账闭环：超时**不进终态**（pending 保留、账本不 rollback）——C# 可能已
      // 真实执行（回执发送失败只降级日志/主线程卡顿 >10s），直接回滚 + LLM 收"失败"会自然
      // 重试（新 instructionId）→ C# 二次执行 → 玩家双倍扣钱。改挂 30s 后重发对账。
      this.scheduleReconcile(msg);
      resolveFn({
        type: "adjust_result",
        requestId: msg.requestId,
        instructionId: msg.instructionId,
        npcName: msg.npcName,
        success: false,
        steps: [
          {
            index: 0,
            kind: "batch",
            target: "",
            success: false,
            failureCode: "internalError",
            detail: `receipt timeout after ${timeoutMs}ms; result unconfirmed, do not repeat payment`,
          },
        ],
        failureCode: "internalError",
      });
    }, timeoutMs);
    this.pendingAdjusts.set(msg.instructionId, { promise, resolve: resolveFn, timer, npcName: msg.npcName });
    sendToCsharp(msg);
    return promise;
  }

  /**
   * 超时对账重发（2026-08-17）：超时后凭原 instructionId 重发 execute_adjust。
   * C# 幂等缓存（256 条环形）命中返回原回执 → handleAdjustResult 正常驱动 pending
   * → committed/rolled_back 闭环；未执行过则 C# 新执行（回执再次超时则 pending 保留
   * 等 reconnect_sync 对账）。重发幂等：pendingReconciles 去重 + sendAdjust 内部去重。
   */
  private scheduleReconcile(msg: ExecuteAdjustMessage): void {
    if (this.pendingReconciles.has(msg.instructionId)) return;
    this.pendingReconciles.set(msg.instructionId, msg);
    const retryMs = this.options?.adjustReconcileRetryMs ?? ADJUST_RECONCILE_RETRY_MS;
    setTimeout(() => {
      this.pendingReconciles.delete(msg.instructionId);
      console.log(
        `[${timestamp()}] [execute_adjust] ${msg.instructionId} reconciling after timeout (C# idempotent cache should return original receipt)`,
      );
      void this.sendAdjust(msg);
    }, retryMs);
  }

  /**
   * adjust_result 回执（C#→TS，2026-08-15 账本迁移设计 §4.1）。
   * 解析 sendAdjust 挂的等待 Promise（超时/重复回执时无等待者，仅记日志——幂等由
   * C# 指令结果缓存保证，断线对账留到步骤 4）。同时驱动账本 pending → committed/rolled_back
   * （幂等：非 pending 状态的账目跳过，不重复入账）。
   */
  async handleAdjustResult(req: AdjustResultMessage): Promise<{ type: "ack"; requestId: string }> {
    const pending = this.pendingAdjusts.get(req.instructionId);
    console.log(`[${timestamp()}] [recv] adjust_result instruction=${req.instructionId} ok=${req.success}${req.failureCode ? ` code=${req.failureCode}` : ""} steps=${req.steps.length}`);
    if (pending) {
      clearTimeout(pending.timer);
      this.pendingAdjusts.delete(req.instructionId);
      pending.resolve(req);
    } else {
      // 2026-08-17 对账闭环：超时不再回滚（pending 保留待 reconcile 重发）——
      // 此处无 waiter 只可能是"重复回执"或"对账重发后回执到达但 waiter 已超时清除"，
      // 幂等由 C# 指令结果缓存保证，账本由 handleAdjustResult 的 pending 状态判断驱动。
      console.log(`[${timestamp()}] [adjust_result] no pending waiter for ${req.instructionId} (重复回执/waiter 已超时，幂等跳过)`);
    }

    const ledger = this.options?.ledger;
    const npcName = req.npcName || pending?.npcName;
    if (ledger && npcName) {
      const done = req.success
        ? ledger.commit(npcName, req.instructionId, req)
        : ledger.rollback(npcName, req.instructionId, `adjust failed: ${req.failureCode ?? "unknown"}`, req);
      if (done) {
        this.pendingSaves.push(
          ledger.save(npcName).catch((err) => {
            console.error(`[adjust_result] ledger.save failed for ${npcName}:`, err);
          }),
        );
      }
    }
    return { type: "ack", requestId: req.requestId };
  }

  /** Phase 1 将在此接入逐 NPC 的 LLM 日结器。 */
  async handleConsolidateDay(req: ConsolidateDayMessage): Promise<{ type: "ack"; requestId: string }> {
    const key = `${req.npcName}|${req.dateIso}`;
    const duplicate = this.seenConsolidations.has(key);
    this.seenConsolidations.add(key);
    const id = (req as { requestId?: string }).requestId ?? "unknown";
    console.log(`[${timestamp()}] [consolidate] ${req.npcName} date=${req.dateIso}${duplicate ? " (duplicate, skipped)" : ""}`);
    // 2026-09-12 从存根变实做（审计 M-3/M-6）：当日首次 consolidate 触发玩家画像
    // LLM 日刷新（refreshPreferences/refreshPersonality——player-profile.ts 既有
    // 实现此前无生产调用方）。失败静默保旧值（PlayerProfileManager 内部已吞错）。
    // 逐 NPC 的 LLM 记忆日结仍留待后续（记忆写入已在对话路径即时持久化）。
    if (!duplicate && !this.consolidatedProfileDates.has(req.dateIso)) {
      this.consolidatedProfileDates.add(req.dateIso);
      const profileMgr = this.options?.profileMgr;
      if (profileMgr) {
        console.log(`[${timestamp()}] [consolidate] refreshing player profile for ${req.dateIso}`);
        void (async () => {
          try {
            await profileMgr.refreshPreferences();
            await profileMgr.refreshPersonality();
          } catch (err) {
            console.error(`[${timestamp()}] [consolidate] profile refresh failed (kept stale):`, err);
          }
        })();
      }
    }
    return { type: "ack", requestId: id };
  }

  /**
   * E5-2 喊话歧义兜底路由。C# 4 层确定性路由全空时发送；本方法用轻量路由
   * （MorningShoutRouter，最多 1 次 LLM 调用，断线走确定性兜底）选目标。
   * 返回 route_shout_response（目标名或 null=无人可听）。
   */
  async handleRouteShout(req: RouteShoutMessage): Promise<RouteShoutResponse> {
    const ts = timestamp();
    console.log(`[${ts}] [route_shout] ${req.npcName} ← "${truncate(req.playerShout, 60)}" candidates=${req.candidates.map(c => `${c.name}(${c.awake ? "awake" : "sleep"}${c.location ? "," + c.location : ""})`).join(",")}`);
    return this.shoutRouter.routeShout(req);
  }

  /**
   * Route an action_result into the per-NPC feedback queue.
   * Silently drops messages without npcName (older C# clients without the field).
   */
  private routeToolResult(req: {
    npcName?: string;
    callId: string;
    tool?: string;
    success: boolean;
    result?: string;
    reason?: string;
  }): void {
    if (!req.npcName) {
      // Legacy C# client without npcName — cannot route, drop silently.
      return;
    }
    console.log(`[${timestamp()}] [tool] ${req.npcName} ← ${req.tool ?? "unknown"} success=${req.success}${req.result ? ` result="${truncate(req.result, 80)}"` : ""}${req.reason ? ` reason=${req.reason}` : ""}`);
    const record: ToolResultRecord = {
      callId: req.callId,
      tool: req.tool ?? "unknown",
      success: req.success,
      ...(req.result !== undefined ? { result: req.result } : {}),
      ...(req.reason !== undefined ? { reason: req.reason } : {}),
    };
    this.registry.enqueueToolResult(req.npcName, record);

    // C2/D4 两阶段落地（2026-08-15 步骤 2 修订）：chop_tree / set_goal 的 action_result
    // 回来后写记忆。give_gift / receive_payment 已改为 TS 同步执行（工具内直接写记忆），
    // 不再产生 action_result，此处不再消费。
    // chop_tree: success=true 写砍树成功记忆；success=false 写失败记忆
    // set_goal: success=true 写目标成立记忆（类型取暂存意图）；success=false 写失败记忆
    // 库存不主动扣，依赖下一轮 dialogue 的 worldSnapshot 从 C# 同步
    const toolName = req.tool ?? "unknown";
    if (toolName === "chop_tree" || toolName === "set_goal") {
      const agent = this.registry.getOrCreate(req.npcName);
      const memory = agent["memory"];
      if (toolName === "chop_tree") {
        if (req.success) {
          memory.addMemory("砍树获取了一些木材", 4.0, "event", "", ["chop", "wood"]);
        } else {
          const reasonText = req.reason ? `(${req.reason})` : "";
          memory.addMemory(`想砍树但没砍成${reasonText}`, 2.0, "event", "", ["chop", "failure"]);
        }
      } else if (toolName === "set_goal") {
        // Phase 2: 目标类型从 callId 暂存的意图取（action_result 不带工具参数）；
        // 无暂存（旧客户端无 callId 或服务重启丢失）时落"未知"。
        const goalType = this.pendingGoals.get(req.callId);
        this.pendingGoals.delete(req.callId);
        if (req.success) {
          memory.addMemory(`我给自己设定了目标：${goalType ?? "未知"}`, 4.0, "decision", "", ["goal"]);
        } else {
          const reasonText = req.reason ? `(${req.reason})` : "";
          memory.addMemory(`我设定的目标没成立${reasonText}`, 2.0, "decision", "", ["goal", "failure"]);
        }
      }
      // 异步保存记忆（不阻塞 action_result 路由）
      this.pendingSaves.push(
        memory.save().catch((err) => {
          console.error(`[tool] memory.save failed for ${req.npcName} after ${toolName}:`, err);
        }),
      );
    }
  }

  async handleDialogue(req: DialogueRequest): Promise<DialogueResponse> {
    // Check NPC lock (spec 5.5: same NPC serial dialogue)
    const locked = await this.registry.acquireLock(req.npcName);
    if (!locked) {
      console.log(`[${timestamp()}] [dialogue] ${req.npcName} BUSY (locked)`);
      return this.buildBusyResponse(req);
    }

    console.log(`[${timestamp()}] [recv] dialogue npc=${req.npcName} player="${truncate(req.playerInput, 80)}"`);

    try {
      const agent = this.registry.getOrCreate(req.npcName);

      // Load memory if not yet loaded (lazy load on first dialogue)
      await agent["memory"].load();

      // Peek pending tool-result feedback for this NPC (C3 feedback loop).
      // drainToolResults now returns a copy without clearing (§4.4): the queue
      // is cleared only after runDialogue succeeds (see clearToolResults below),
      // so an LLM failure preserves the feedback for re-injection next round.
      const toolResults: ToolResultRecord[] = this.registry.drainToolResults(req.npcName);

      const scene = decodeWorldSnapshot(req.worldSnapshot);

      // 2026-08-15 账本迁移（步骤 1）：首次见 NPC 从 worldSnapshot 播种权威账本
      // （npcMoney/npcInventory 镜像基线；步骤 2 后权威性移交账本本身）。播种后保存。
      const ledger = this.options?.ledger;
      if (ledger) {
        await ledger.load(req.npcName);
        if (ledger.seedFromSnapshot(req.npcName, scene)) {
          this.pendingSaves.push(
            ledger.save(req.npcName).catch((err) => {
              console.error(`[dialogue] ledger.save failed for ${req.npcName}:`, err);
            }),
          );
        }
      }
      // 步骤 2：账本已播种 → 场景经济字段以账本为准（TS 自填，覆盖 C# 镜像）。
      this.applyLedgerToScene(req.npcName, scene);

      // 2026-09-12 情绪引擎接线（审计 P1-1 修复）：scene.npcMood 为空（Director 未覆盖）时
      // 用 TS 情绪引擎当前状态预填——引擎从 action_result/state_changed 事件累积的情绪
      // 首次进入 NPC prompt（此前只进 dialogue_response.emotion，NPC 认知恒"平静"）。
      // 覆盖权次序不变：Director set_npc_mood（snapshot）> 情绪引擎 > 默认。
      if ((scene.npcMood === null || scene.npcMood === undefined || scene.npcMood.trim() === "") && this.options?.emotionEngine) {
        scene.npcMood = this.options.emotionEngine.current(req.npcName).emotion;
      }

      // M2a：预加载对话发起玩家的关系桶（玩家桶的后续读写全部同步依赖此步），
      // 并做快照自愈——快照好感（C# 镜像，含送礼等 TS 不知情的变动）与 TS 值差 >50
      // 时以快照为准校准（覆盖送礼路径漂移；单机 playerId 恒存在，行为等价现状）。
      const playerId = req.playerId;
      const playerName = scene.farmerName;
      if (playerId) {
        const mem = agent["memory"] as AgentMemory;
        await mem.getPlayerMemory(playerId, playerName);
        const snapshot = scene.friendship;
        const tsFriendship = mem.getFriendship(playerId);
        if (Math.abs(snapshot - tsFriendship) > 50) {
          console.log(
            `[${timestamp()}] [dialogue] ${req.npcName}: friendship self-heal snapshot=${snapshot} ts=${tsFriendship} (diff>50, adopting snapshot)`,
          );
          mem.setFriendship(snapshot, playerId);
        }
      }

      // 步骤 2：经济工具同步执行器（trade/give_item/give_gift/receive_payment 拦截入口）。
      // 账本校验 → pending → execute_adjust → await 回执，工具在 ReAct 循环内同步完成。
      // 2026-08-16 联机：发起对话玩家 ID 透传进 execute_adjust（C# 按此解析目标 Farmer）。
      const economy: EconomyExecutor = {
        npcName: req.npcName,
        adjust: async (ops) => this.adjustEconomy(req.npcName, ops, req.playerId),
        getMoney: () => ledger?.getMoney(req.npcName) ?? null,
        getInventory: () => {
          if (!ledger) return null;
          const state = ledger.getOrCreate(req.npcName);
          if (!state.seeded) return null;
          return Object.entries(state.items).map(([key, item]) => ({ name: item.name || key, quantity: item.quantity }));
        },
      };

      const result = await agent.runDialogue(req.playerInput, scene, toolResults, economy, playerId, playerName);

      // M2a：对话好感 delta 记入 TS 权威账本（玩家桶；同时 dialogue_response 回传 C#
      // 加游戏内点——两套各自用途：vanilla 管游戏事件，TS 管人设/阶段）。
      if (playerId && result.friendshipDelta) {
        agent["memory"].addFriendship(result.friendshipDelta, playerId);
      }

      // Phase 2: 暂存 set_goal 意图（callId → 目标类型），回执到达后写目标记忆。
      // 2026-08-15 步骤 2: receive_payment 已改 TS 同步执行（工具内写记忆），
      // 不再产生 action，无需暂存（pendingPayments 已删除）。
      for (const action of result.actions) {
        if (action.tool === "set_goal" && action.callId) {
          this.pendingGoals.set(action.callId, String(action.args.type ?? ""));
        }
      }

      const fd = result.friendshipDelta ?? 0;
      console.log(`[${timestamp()}] [send] dialogue npc=${req.npcName} speech="${truncate(result.speech, 120)}" actions=${result.actions.length} emotion=${result.emotion} friendship=${fd >= 0 ? "+" : ""}${fd}`);

      // §4.4 步骤 3: dialogue succeeded — feedback has been consumed (injected
      // into prompt + persisted to memory). Now safe to clear the queue.
      // On failure the catch branch does NOT clear, leaving feedback for retry.
      this.registry.clearToolResults(req.npcName);

      // Async save (non-blocking response). Tracked in pendingSaves so that
      // server shutdown / test teardown can flush before deleting the agentsDir.
      this.pendingSaves.push(
        agent["memory"].save().catch((err) => {
          console.error(`[dialogue] memory.save failed for ${req.npcName}:`, err);
        })
      );

      // 2026-08-15 步骤 3：响应情绪 = Director set_npc_mood 覆盖权 > 情绪引擎推导 > 默认。
      // scene.npcMood 由 C# worldSnapshot 携带（Director 写过则非 null）。
      const responseEmotion = scene.npcMood && scene.npcMood.trim() !== ""
        ? scene.npcMood
        : (this.options?.emotionEngine?.current(req.npcName).emotion ?? result.emotion);

      return {
        type: "dialogue_response",
        requestId: req.requestId,
        npcName: req.npcName,
        speech: result.speech || "...",
        actions: result.actions,
        emotion: responseEmotion,
        memorySideEffect: MEMORY_SIDE_EFFECT_RECORDED,
        // 2026-08-16 联机：发起玩家 ID 原样 echo（C# 端记录 LastDialoguePlayerId，FOLLOW 目标解析）。
        ...(req.playerId !== undefined ? { playerId: req.playerId } : {}),
        // §4.1.1 方案 B：好感度评估并回 dialogue 主路径。仅在非零/非空时携带，
        // 避免冗余字段（LLM 未输出时默认 0，不阻塞对话）。
        ...(result.friendshipDelta !== 0 ? { friendshipDelta: result.friendshipDelta } : {}),
        ...(result.friendshipReason ? { friendshipReason: result.friendshipReason } : {}),
      };
    } catch (err) {
      console.error(`[dialogue] LLM failed for ${req.npcName}:`, err);
      // §4.4: do NOT clear the tool-result queue on failure — the feedback
      // was never successfully injected, so preserve it for the next round.
      const fallback = this.ruleEngine.buildFallbackResponse(req, err);
      // E4: fallback 路径也持久化记忆（玩家输入已在 runDialogue 开头记录到 memory）
      try {
        const agent = this.registry.getOrCreate(req.npcName);
        // M2 修复（2026-08-23）：fallback 台词同样进当前对话玩家的桶（玩家桶已在
        // 本请求预加载；无 playerId/桶未加载时 AgentMemory 内部落世界桶或丢弃，与
        // 玩家输入记录同语义）。playerName 不传——桶内已存有该玩家显示名。
        agent["memory"].addConversation("npc", fallback.speech, req.playerId);
        this.pendingSaves.push(
          agent["memory"].save().catch((saveErr) => {
            console.error(`[dialogue] memory.save failed (fallback) for ${req.npcName}:`, saveErr);
          })
        );
      } catch (saveErr) {
        console.error(`[dialogue] fallback memory save skipped for ${req.npcName}:`, saveErr);
      }
      console.log(`[${timestamp()}] [send] dialogue npc=${req.npcName} FALLBACK`);
      return fallback;
    } finally {
      this.registry.releaseLock(req.npcName);
    }
  }

  private buildBusyResponse(req: DialogueRequest): DialogueResponse {
    return {
      type: "dialogue_response",
      requestId: req.requestId,
      npcName: req.npcName,
      // 2026-08-16 决策 #3：BUSY 改灰色系统提示（C# 端按 NPC 性别渲染"他/她/它正在和别人交流"，
      // 这里只留占位文案；fallback=true 是 C# 走灰色 chatBox 路径的开关）。
      speech: `（${req.npcName} 正在和别人交流）`,
      actions: [],
      emotion: DEFAULT_EMOTION,
      fallback: true,
    };
  }

  /**
   * Await all in-flight memory saves. Called by server shutdown to prevent
   * test teardown from deleting the agentsDir while saves are still pending.
   */
  async flushPendingSaves(): Promise<void> {
    const saves = this.pendingSaves;
    this.pendingSaves = [];
    await Promise.allSettled(saves);
  }

  async routeMessage(msg: unknown): Promise<OutgoingMessage> {
    const m = msg as { type: string };
    switch (m.type) {
      case "hello": return this.handleHello(m as HelloRequest);
      case "ping": return this.handlePing(m as PingRequest);
      case "dialogue": return this.handleDialogue(m as DialogueRequest);
      case "action_result": return this.handleActionResult(m as ActionResultMessage);
      case "state_changed": return this.handleStateChanged(m as StateChangedMessage);
      case "consolidate_day": return this.handleConsolidateDay(m as ConsolidateDayMessage);
      case "day_started": return this.handleDayStarted(m as DayStartedMessage);
      case "game_context_sync": return this.handleGameContextSync(m as GameContextSyncMessage);
      case "route_shout": return this.handleRouteShout(m as RouteShoutMessage);
      case "director_command": return this.handleDirectorCommand(m as DirectorCommandMessage);
      case "execute_adjust": return this.handleExecuteAdjust(m as ExecuteAdjustMessage);
      case "adjust_result": return this.handleAdjustResult(m as AdjustResultMessage);
      case "reconnect_sync": return this.handleReconnectSync(m as ReconnectSyncMessage);
      default: return { type: "ack", requestId: "unknown" };
    }
  }
}
