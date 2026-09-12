import { readFile, mkdir } from "fs/promises";
import { dirname, join } from "path";
import { writeFileAtomic } from "./atomic-fs";
import type { AdjustOp, AdjustResultMessage, AdjustStepResult, SceneState } from "./types";

/**
 * 单笔 pending 账目（TS 账本状态机：pending → committed | rolled_back）。
 * pending 记账不修改 money/items——余额只在实际入账（committed）或回滚时变化。
 */
export interface PendingAdjust {
  instructionId: string;
  requestId: string;
  npcName: string;
  ops: AdjustOp[];
  status: "pending" | "committed" | "rolled_back";
  createdAt: string;
  /** 2026-08-16 联机：指令发起玩家（execute_adjust.playerId 原样存，断线重发保持）。可选。 */
  playerId?: string;
  /** committed/rolled_back 终态条目即时删除（2026-08-23 无界增长修复）——本字段
   * 只承载回执到达前的 in-flight 账目；result/rollbackReason 仅在终态化返回值上
   * 短暂存在，供当次调用方消费，不再留存在账本里。 */
  result?: AdjustResultMessage;
  rollbackReason?: string;
}

/** 账本中一件物品的存量（key 为 qualified item id，如 "(O)128"）。 */
export interface LedgerItemState {
  /** 显示名（worldSnapshot 播种时只有 name；execute_adjust 携带 itemName 时更新）。 */
  name: string;
  quantity: number;
}

/** 单个 NPC 的权威账本状态（持久化到 agents/{npc}_ledger.json）。 */
export interface LedgerState {
  npcName: string;
  money: number;
  items: Record<string, LedgerItemState>;
  /** 已从 worldSnapshot 播种过（只播一次；档案初始资金/读档恢复优先于播种）。 */
  seeded: boolean;
  /** 最近一次 adjust_result 确认的 C# 侧余额镜像（对账用，不参与业务计算）。 */
  lastConfirmedNpcMoney?: number;
  lastSavedAt: string;
  pending: Record<string, PendingAdjust>;
}

/** beginPending 业务校验失败原因（设计 §6：LLM 非法交易 → TS 业务校验拒绝）。 */
export type BeginPendingResult =
  | { ok: true }
  | { ok: false; reason: string; failureCode: "insufficientFunds" | "itemNotFound" | "invalidOp" };

/**
 * 权威经济账本（2026-08-15 账本迁移设计 §4.1，步骤 1 骨架）。
 * TS 是经济权威：业务校验（NPC 余额/库存）在这里做；C# 只做物理校验 + 原语执行。
 * 流程：beginPending（业务校验 + 记 pending）→ execute_adjust 下发 → adjust_result 回执
 * → commit（pending→committed，应用余额/物品）或 rollback（pending→rolled_back，零变更）。
 * 持久化：agents/{npc}_ledger.json（镜像 AgentMemory 文件风格）；首次见 NPC 从 worldSnapshot 播种。
 * 步骤 1 不拦截任何工具——旧路径完全不动，账本只被 adjust_result 路由消费与播种。
 */
export class AgentLedger {
  private readonly states = new Map<string, LedgerState>();
  private readonly loaded = new Set<string>();

  constructor(private readonly agentsDir: string) {}

  /** 账本文件路径（与 memory 文件同目录，`{npc}_ledger.json`）。 */
  ledgerFilePath(npcName: string): string {
    return join(this.agentsDir, `${npcName}_ledger.json`);
  }

  getOrCreate(npcName: string): LedgerState {
    let state = this.states.get(npcName);
    if (!state) {
      state = {
        npcName,
        money: 0,
        items: {},
        seeded: false,
        lastSavedAt: "",
        pending: {},
      };
      this.states.set(npcName, state);
    }
    return state;
  }

  /**
   * 从 worldSnapshot 首次播种（设计 §5 步骤 1：首次见 NPC 从 worldSnapshot 播种）。
   * 只播一次（seeded 标记）；旧客户端不携带经济字段（null）时不播种——"未知"不等于"零"。
   * 播种数据来自 C# 镜像（AgentInventory），步骤 2 后权威性移交本账本。
   * @returns 本次调用是否执行了播种（true 表示首次播种，调用方应保存）。
   */
  seedFromSnapshot(npcName: string, scene: SceneState): boolean {
    const state = this.getOrCreate(npcName);
    if (state.seeded) return false;
    // npcMoney 与 npcInventory 都缺失 → 无数据可播，保持未播种（后续快照再来时补播）。
    if (scene.npcMoney === null && scene.npcInventory === null) return false;

    if (scene.npcMoney !== null) {
      state.money = scene.npcMoney;
    }
    if (scene.npcInventory !== null) {
      // 播种只做基线抄录（镜像语义）；不覆盖已存在的条目（防御重复播种竞态）。
      for (const inv of scene.npcInventory) {
        const key = inv.name; // worldSnapshot 只携带 name/quantity，无 item id——步骤 2 前以名为键
        if (state.items[key] === undefined && inv.quantity > 0) {
          state.items[key] = { name: key, quantity: inv.quantity };
        }
      }
    }
    state.seeded = true;
    return true;
  }

  /**
   * 业务校验 + 记 pending 账（设计 §4.1：TS 账本业务校验 → 记 pending 账 → execute_adjust）。
   * 校验规则（步骤 2 语义，2026-08-15）：
   * - 账本未播种（seeded=false）→ 全部放行（未知≠0，交给 C# 物理校验兜底）；
   * - 已播种：npc 钱包支出必须有余额；npc 物品扣除按 itemId/物品名双键查存量——
   *   账本**已知**该物品且数量不足才拒绝（未知条目放行，C# TryRemove 兜底）；
   * - player 目标不做业务校验（玩家真实余额/背包由 C# 物理校验兜底）。
   * 校验失败返回原因且不记 pending（LLM 自然改口）。
   * 同 instructionId 已存在 pending（TS 重发/重复调用）时直接返回 ok（幂等）。
   */
  beginPending(npcName: string, instructionId: string, requestId: string, ops: AdjustOp[], playerId?: string): BeginPendingResult {
    if (!instructionId || ops.length === 0) {
      return { ok: false, reason: "missing instructionId or empty ops", failureCode: "invalidOp" };
    }

    const state = this.getOrCreate(npcName);
    if (state.pending[instructionId]) {
      return { ok: true }; // 幂等：同一 instructionId 的 pending 已存在，等待回执即可
    }

    if (state.seeded) {
      for (const op of ops) {
        if (op.target !== "npc") continue;
        if (op.kind === "money" && typeof op.amount === "number" && op.amount < 0) {
          if (state.money < -op.amount) {
            return {
              ok: false,
              reason: `insufficient funds (${npcName} ledger ${state.money}g, need ${-op.amount}g)`,
              failureCode: "insufficientFunds",
            };
          }
        }
        if (op.kind === "item" && typeof op.quantity === "number" && op.quantity < 0) {
          // 双键查找：execute_adjust 的 itemId 是 QualifiedItemId，而播种来源
          // worldSnapshot 只带物品名——两键任一命中即视为"账本已知该物品"；
          // 名称形态的 itemId 先归一为播种键（2026-08-17 键归一）。
          const normalizedId = this.resolveItemKey(state, op.itemId ?? "");
          const have = this.getItemQuantity(state, normalizedId, op.itemName ?? "");
          if (have !== undefined && have < -op.quantity) {
            return {
              ok: false,
              reason: `item not found (${npcName} ledger has ${have} x ${op.itemId ?? op.itemName}, need ${-op.quantity})`,
              failureCode: "itemNotFound",
            };
          }
        }
      }
    }

    state.pending[instructionId] = {
      instructionId,
      requestId,
      npcName,
      ops,
      status: "pending",
      createdAt: new Date().toISOString(),
      ...(playerId !== undefined ? { playerId } : {}),
    };
    return { ok: true };
  }

  /** 按 itemId 或物品名取存量；账本无此条目返回 undefined（未知，交由 C# 物理校验兜底）。 */
  private getItemQuantity(state: LedgerState, itemId: string, itemName: string): number | undefined {
    if (itemId && state.items[itemId] !== undefined) return state.items[itemId]!.quantity;
    if (itemName && state.items[itemName] !== undefined) return state.items[itemName]!.quantity;
    // 名称键也可能是 "name" 字段匹配（播种键即名称，条目 name 字段与键一致）——兜底扫描。
    for (const entry of Object.values(state.items)) {
      if (entry.name === itemName || entry.name === itemId) return entry.quantity;
    }
    return undefined;
  }

  /**
   * 回执到达：pending → committed，按 ops 应用余额/物品变更（npc 目标）。
   * 余额取 C# 回执镜像（result.npcMoney）——C# 已物理执行，账本必须与现实一致
   * （"认知不得脱离现实"，设计 §2 第 9 条）。
   * 物品键（2026-08-17 键归一）：优先用回执步骤的 itemId（C# 已按 ID/名称回落解析出的
   * 权威 QualifiedItemId）——LLM 传显示名/旧客户端无回执键时回退 op.itemId 并做名称反查归一。
   * 返回该笔 pending，无则 undefined。
   */
  commit(npcName: string, instructionId: string, result: AdjustResultMessage): PendingAdjust | undefined {
    const state = this.getOrCreate(npcName);
    const pending = state.pending[instructionId];
    if (!pending || pending.status !== "pending") return undefined;

    // 回执步骤按 index 对齐 ops（步骤数组与 ops 顺序一致）。
    const stepByIndex = new Map<number, AdjustStepResult>();
    for (const step of result.steps ?? []) stepByIndex.set(step.index, step);

    pending.ops.forEach((op, i) => {
      if (op.target !== "npc") return;
      if (op.kind === "money" && typeof op.amount === "number") {
        state.money += op.amount;
      } else if (op.kind === "item" && typeof op.quantity === "number") {
        // 权威键：回执 step.itemId > op.itemId（名称形态时反查播种键归一）。
        const receiptId = stepByIndex.get(i)?.itemId;
        const rawId = op.itemId ?? op.itemName ?? "";
        const key = receiptId ?? this.resolveItemKey(state, rawId);
        if (!key) return; // 无法解析键（理论上不会：C# 已物理执行成功），跳过入账保一致性
        const entry = (state.items[key] ??= { name: op.itemName ?? key, quantity: 0 });
        entry.quantity += op.quantity;
        if (entry.quantity <= 0) delete state.items[key];
        else if (op.itemName) entry.name = op.itemName;
      }
    });

    if (typeof result.npcMoney === "number") {
      state.lastConfirmedNpcMoney = result.npcMoney;
    }
    pending.status = "committed";
    pending.result = result;
    // 终态即时清理（2026-08-23 无界增长修复）：committed 条目无人再消费
    // （幂等靠"非 pending 跳过"+ C# 缓存回执），留着只会让内存/磁盘无限膨胀。
    delete state.pending[instructionId];
    return pending;
  }

  /**
   * 物品键归一（2026-08-17）：rawId 为名称形态（非 `(X)` 前缀的 qualified id）时，
   * 按账本条目 name 字段反查播种键（如 "Wood" → "(O)388"）；查不到原样返回（未知物品，
   * 由 C# 物理校验兜底）。beginPending 校验与 commit 入账前调用，消除显示名/ID 双键漂移。
   */
  private resolveItemKey(state: LedgerState, rawId: string): string {
    if (!rawId) return rawId;
    if (/^\([A-Z]\)/.test(rawId)) return rawId; // 已是 qualified id 形态
    for (const [key, entry] of Object.entries(state.items)) {
      if (entry.name === rawId || key === rawId) return key;
    }
    return rawId;
  }

  /** 回执失败/超时：pending → rolled_back。pending 记账未动余额/物品，无需逆向操作。 */
  rollback(npcName: string, instructionId: string, reason: string, result?: AdjustResultMessage): PendingAdjust | undefined {
    const state = this.getOrCreate(npcName);
    const pending = state.pending[instructionId];
    if (!pending || pending.status !== "pending") return undefined;

    pending.status = "rolled_back";
    pending.rollbackReason = reason;
    if (result) pending.result = result;
    // 终态即时清理（同 commit）：rolled_back 条目零副作用，无保留价值。
    delete state.pending[instructionId];
    return pending;
  }

  getPending(npcName: string, instructionId: string): PendingAdjust | undefined {
    return this.getOrCreate(npcName).pending[instructionId];
  }

  /**
   * 遍历全部 NPC 的 pending 账目（步骤 4 断线对账用：重连后凭 instructionId 重发，
   * C# 幂等返回缓存回执，pending → committed/rolled_back 闭环）。
   */
  listPending(): Array<{ npcName: string; pending: PendingAdjust }> {
    const out: Array<{ npcName: string; pending: PendingAdjust }> = [];
    for (const [npcName, state] of this.states) {
      for (const pending of Object.values(state.pending)) {
        if (pending.status === "pending") {
          out.push({ npcName, pending });
        }
      }
    }
    return out;
  }

  /** 已入账的余额（步骤 2 get_info / 业务校验读取；未加载时调用方负责先 load）。 */
  getMoney(npcName: string): number {
    return this.getOrCreate(npcName).money;
  }

  async load(npcName: string): Promise<void> {
    if (this.loaded.has(npcName)) return;
    const filePath = this.ledgerFilePath(npcName);
    try {
      const raw = await readFile(filePath, "utf-8");
      const data = JSON.parse(raw) as Partial<LedgerState>;
      this.states.set(npcName, {
        npcName,
        money: data.money ?? 0,
        items: data.items ?? {},
        seeded: data.seeded ?? false,
        ...(data.lastConfirmedNpcMoney !== undefined ? { lastConfirmedNpcMoney: data.lastConfirmedNpcMoney } : {}),
        lastSavedAt: data.lastSavedAt ?? "",
        pending: data.pending ?? {},
      });
    } catch (err) {
      // 文件缺失（ENOENT）→ 全新账本；其他解析错误 → 降级重建（设计 §6：账本损坏从镜像重建）。
      if ((err as NodeJS.ErrnoException).code !== "ENOENT") {
        console.warn(`[AgentLedger] Failed to load ${filePath}, starting fresh: ${(err as Error).message}`);
      }
      this.getOrCreate(npcName); // 确保状态存在（保留默认值）
    }
    this.loaded.add(npcName);
  }

  async save(npcName: string): Promise<void> {
    const state = this.getOrCreate(npcName);
    state.lastSavedAt = new Date().toISOString();
    const data: LedgerState = {
      npcName: state.npcName,
      money: state.money,
      items: state.items,
      seeded: state.seeded,
      ...(state.lastConfirmedNpcMoney !== undefined ? { lastConfirmedNpcMoney: state.lastConfirmedNpcMoney } : {}),
      lastSavedAt: state.lastSavedAt,
      pending: state.pending,
    };
    await mkdir(dirname(this.ledgerFilePath(npcName)), { recursive: true });
    // 原子写（temp+rename）：账本 JSON 半截截断会被 load 降级重建，真金白银的余额不能赌。
    await writeFileAtomic(this.ledgerFilePath(npcName), JSON.stringify(data, null, 2));
  }
}
