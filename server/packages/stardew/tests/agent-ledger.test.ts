import { test, expect } from "bun:test";
import { AgentLedger } from "../src/agent-ledger";
import { mkdtempSync, rmSync, existsSync, readFileSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import type { AdjustOp, AdjustResultMessage, SceneState } from "../src/types";

function makeTempDir(): string {
  return mkdtempSync(join(tmpdir(), "valley-ledger-test-"));
}

function makeScene(overrides: Partial<SceneState> = {}): SceneState {
  return {
    season: "summer", day: 28, timeStr: "14:30", weather: "sunny",
    location: "Town", nearbyObjects: "", farmerName: "农夫",
    friendship: 250, npcState: "IDLE", inventory: [],
    npcTile: { x: 0, y: 0 },
    playerMoney: 500,
    npcLocation: "Town",
    npcMoney: 1000,
    npcInventory: [],
    playerHeldItem: null,
    currentGoal: null,
    npcMood: null,
    npcRecentEvents: null,
    npcWorkingOn: null,
    npcOwedMoney: null,
    ...overrides,
  };
}

function moneyOp(target: "player" | "npc", amount: number): AdjustOp {
  return { kind: "money", target, amount, reason: "test" };
}

function itemOp(target: "player" | "npc", itemId: string, quantity: number): AdjustOp {
  return { kind: "item", target, itemId, quantity, reason: "test" };
}

function resultOf(instructionId: string, npcName: string, success: boolean, npcMoney?: number): AdjustResultMessage {
  return {
    type: "adjust_result",
    requestId: "req",
    instructionId,
    npcName,
    success,
    steps: [],
    ...(npcMoney !== undefined ? { npcMoney } : {}),
  };
}

// ── 播种（worldSnapshot 首次见 NPC）───────────────────

test("seedFromSnapshot seeds money and items once from worldSnapshot", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    const scene = makeScene({ npcMoney: 750, npcInventory: [{ name: "Amethyst", quantity: 2 }] });

    expect(ledger.seedFromSnapshot("Abigail", scene)).toBe(true);
    expect(ledger.getMoney("Abigail")).toBe(750);
    expect(ledger.getOrCreate("Abigail").items["Amethyst"]).toEqual({ name: "Amethyst", quantity: 2 });

    // 只播一次：第二次快照（即使数值变化）不再覆盖
    const scene2 = makeScene({ npcMoney: 9999, npcInventory: [] });
    expect(ledger.seedFromSnapshot("Abigail", scene2)).toBe(false);
    expect(ledger.getMoney("Abigail")).toBe(750);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("seedFromSnapshot skips when snapshot carries no economy fields (未知≠零)", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    const scene = makeScene({ npcMoney: null, npcInventory: null });

    expect(ledger.seedFromSnapshot("Abigail", scene)).toBe(false);
    expect(ledger.getOrCreate("Abigail").seeded).toBe(false); // 保持未播种，后续快照补播
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ── beginPending 业务校验（设计 §4.1：TS 账本业务校验 → 记 pending）──

test("beginPending rejects npc spend beyond ledger balance (insufficientFunds)", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", makeScene({ npcMoney: 100, npcInventory: [] }));

    const r = ledger.beginPending("Abigail", "i1", "req", [moneyOp("npc", -150)]);
    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.failureCode).toBe("insufficientFunds");
      expect(r.reason).toContain("100");
    }
    expect(ledger.getPending("Abigail", "i1")).toBeUndefined(); // 未记 pending
    expect(ledger.getMoney("Abigail")).toBe(100); // 余额未动
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("beginPending rejects npc item removal beyond stock (itemNotFound)", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", makeScene({ npcMoney: 0, npcInventory: [{ name: "Wood", quantity: 3 }] }));

    const r = ledger.beginPending("Abigail", "i2", "req", [itemOp("npc", "Wood", -5)]);
    expect(r.ok).toBe(false);
    if (!r.ok) expect(r.failureCode).toBe("itemNotFound");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("beginPending passes player-target ops through (C# 物理校验兜底)", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", makeScene({ npcMoney: 100, npcInventory: [] }));

    const r = ledger.beginPending("Abigail", "i3", "req", [moneyOp("player", -99999), itemOp("player", "(O)388", -99)]);
    expect(r.ok).toBe(true); // 玩家侧不做业务校验
    expect(ledger.getPending("Abigail", "i3")?.status).toBe("pending");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("beginPending rejects missing instructionId / empty ops (invalidOp)", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");

    expect(ledger.beginPending("Abigail", "", "req", [moneyOp("npc", 10)]).ok).toBe(false);
    expect(ledger.beginPending("Abigail", "i4", "req", []).ok).toBe(false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("beginPending is idempotent for the same instructionId", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", makeScene({ npcMoney: 100, npcInventory: [] }));

    expect(ledger.beginPending("Abigail", "dup", "req", [moneyOp("npc", -10)]).ok).toBe(true);
    expect(ledger.beginPending("Abigail", "dup", "req", [moneyOp("npc", -10)]).ok).toBe(true); // 幂等复用
    expect(Object.keys(ledger.getOrCreate("Abigail").pending)).toHaveLength(1);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ── pending 状态机：commit / rollback ────────────────

test("commit applies npc money and item deltas, records C# mirror balance", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", makeScene({ npcMoney: 500, npcInventory: [{ name: "Wood", quantity: 10 }] }));
    ledger.beginPending("Abigail", "c1", "req", [moneyOp("npc", -100), itemOp("npc", "Wood", -4), moneyOp("npc", 50)]);

    const done = ledger.commit("Abigail", "c1", resultOf("c1", "Abigail", true, 450));
    expect(done?.status).toBe("committed");
    expect(ledger.getMoney("Abigail")).toBe(450); // 500 - 100 + 50 = 450
    expect(ledger.getOrCreate("Abigail").items["Wood"]?.quantity).toBe(6);
    expect(ledger.getOrCreate("Abigail").lastConfirmedNpcMoney).toBe(450);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("commit is idempotent — second commit on same instruction is a no-op", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", makeScene({ npcMoney: 100, npcInventory: [] }));
    ledger.beginPending("Abigail", "c2", "req", [moneyOp("npc", -10)]);

    ledger.commit("Abigail", "c2", resultOf("c2", "Abigail", true, 90));
    const second = ledger.commit("Abigail", "c2", resultOf("c2", "Abigail", true, 90));
    expect(second).toBeUndefined(); // 非 pending 状态 → 跳过
    expect(ledger.getMoney("Abigail")).toBe(90); // 未重复入账
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("rollback marks pending rolled_back without touching balances", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", makeScene({ npcMoney: 100, npcInventory: [] }));
    ledger.beginPending("Abigail", "r1", "req", [moneyOp("npc", -10)]);

    const done = ledger.rollback("Abigail", "r1", "adjust failed: insufficientFunds", resultOf("r1", "Abigail", false));
    expect(done?.status).toBe("rolled_back");
    expect(done?.rollbackReason).toBe("adjust failed: insufficientFunds");
    expect(ledger.getMoney("Abigail")).toBe(100); // pending 记账不动余额
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ── 持久化 ──────────────────────────────────────────

test("save/load round-trips ledger state including pending entries", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", makeScene({ npcMoney: 300, npcInventory: [{ name: "Wood", quantity: 5 }] }));
    ledger.beginPending("Abigail", "p1", "req", [moneyOp("npc", -50)]);
    await ledger.save("Abigail");

    const filePath = join(dir, "Abigail_ledger.json");
    expect(existsSync(filePath)).toBe(true);
    const onDisk = JSON.parse(readFileSync(filePath, "utf-8"));
    expect(onDisk.money).toBe(300);
    expect(onDisk.items.Wood.quantity).toBe(5);
    expect(onDisk.pending.p1.status).toBe("pending");

    // 新实例重新加载：状态完整恢复
    const reloaded = new AgentLedger(dir);
    await reloaded.load("Abigail");
    expect(reloaded.getMoney("Abigail")).toBe(300);
    expect(reloaded.getPending("Abigail", "p1")?.status).toBe("pending");
    expect(reloaded.getOrCreate("Abigail").seeded).toBe(true);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("load tolerates missing file (fresh ledger) and corrupt JSON (rebuild)", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail"); // 文件不存在 → 全新账本
    expect(ledger.getMoney("Abigail")).toBe(0);

    const { writeFileSync } = await import("fs");
    writeFileSync(join(dir, "Haley_ledger.json"), "{corrupt json!!", "utf-8");
    await ledger.load("Haley"); // 损坏 → 降级重建（设计 §6）
    expect(ledger.getMoney("Haley")).toBe(0);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ── 终态清理（2026-08-23 无界增长修复）────────────────

test("commit/rollback remove terminal entries from pending (memory and disk)", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", makeScene({ npcMoney: 100, npcInventory: [] }));

    // committed → 条目删除
    ledger.beginPending("Abigail", "t1", "req", [moneyOp("npc", -10)]);
    const committed = ledger.commit("Abigail", "t1", resultOf("t1", "Abigail", true, 90));
    expect(committed?.status).toBe("committed"); // 返回值仍携带终态信息供当次消费
    expect(ledger.getPending("Abigail", "t1")).toBeUndefined();
    expect(Object.keys(ledger.getOrCreate("Abigail").pending)).toHaveLength(0);

    // rolled_back → 条目删除
    ledger.beginPending("Abigail", "t2", "req", [moneyOp("npc", -10)]);
    ledger.rollback("Abigail", "t2", "adjust failed: insufficientFunds");
    expect(Object.keys(ledger.getOrCreate("Abigail").pending)).toHaveLength(0);

    // 持久化侧同样不残留终态条目
    await ledger.save("Abigail");
    const reloaded = new AgentLedger(dir);
    await reloaded.load("Abigail");
    expect(Object.keys(reloaded.getOrCreate("Abigail").pending)).toHaveLength(0);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("item quantity dropping to zero removes the ledger entry", async () => {
  const dir = makeTempDir();
  try {
    const ledger = new AgentLedger(dir);
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", makeScene({ npcMoney: 0, npcInventory: [{ name: "Wood", quantity: 2 }] }));
    ledger.beginPending("Abigail", "z1", "req", [itemOp("npc", "Wood", -2)]);

    ledger.commit("Abigail", "z1", resultOf("z1", "Abigail", true));
    expect(ledger.getOrCreate("Abigail").items["Wood"]).toBeUndefined();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
