import { test, expect } from "bun:test";
import { decodeWorldSnapshot, timeToPeriod, coarsenLocation, summarizeNearby } from "../src/world-snapshot-decoder";
import type { WorldSnapshot } from "../src/types";

const validSnapshot: WorldSnapshot = {
  season: "summer",
  day: 28,
  time: "14:30",
  weather: "sunny",
  location: "Town",
  npcTile: { x: 32, y: 18 },
  nearbyObjects: "2 villagers, Pierre's shop entrance",
  friendship: 250,
  npcState: "IDLE",
  inventory: [{ name: "Amethyst", quantity: 2 }],
  farmerName: "新来的农夫",
};

test("decodes valid WorldSnapshot to SceneState", () => {
  const scene = decodeWorldSnapshot(validSnapshot);
  expect(scene.season).toBe("summer");
  expect(scene.day).toBe(28);
  expect(scene.timeStr).toBe("14:30");
  expect(scene.weather).toBe("sunny");
  expect(scene.location).toBe("Town");
  expect(scene.npcTile).toEqual({ x: 32, y: 18 });
  expect(scene.nearbyObjects).toBe("2 villagers, Pierre's shop entrance");
  expect(scene.friendship).toBe(250);
  expect(scene.npcState).toBe("IDLE");
  expect(scene.inventory).toEqual([{ name: "Amethyst", quantity: 2 }]);
  expect(scene.farmerName).toBe("新来的农夫");
});

test("decodes snapshot with missing optional fields using defaults", () => {
  const partial = { ...validSnapshot, nearbyObjects: "", inventory: [] } as WorldSnapshot;
  const scene = decodeWorldSnapshot(partial);
  expect(scene.nearbyObjects).toBe("");
  expect(scene.inventory).toEqual([]);
});

test("decodes E0-6/E4-2 npc fields when present", () => {
  const snap = {
    ...validSnapshot,
    npcLocation: "Mine",
    npcMoney: 320,
    npcInventory: [{ name: "Stone", quantity: 13 }],
  } as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.npcLocation).toBe("Mine");
  expect(scene.npcMoney).toBe(320);
  expect(scene.npcInventory).toEqual([{ name: "Stone", quantity: 13 }]);
});

test("missing npc fields degrade to null（旧客户端兼容）", () => {
  const scene = decodeWorldSnapshot(validSnapshot);
  expect(scene.npcLocation).toBeNull();
  expect(scene.npcMoney).toBeNull();
  // 字段缺失 → null（未知），不是空数组——与"真空包"区分
  expect(scene.npcInventory).toBeNull();
});

test("explicit empty npcInventory decodes to empty array（真空包 ≠ 未知）", () => {
  const snap = { ...validSnapshot, npcInventory: [] } as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.npcInventory).toEqual([]);
});

test("playerHeldItem 缺失降级为 null（旧客户端兼容）", () => {
  const scene = decodeWorldSnapshot(validSnapshot);
  expect(scene.playerHeldItem).toBeNull();
});

test("playerHeldItem 显式 null 降级为 null", () => {
  const snap = { ...validSnapshot, playerHeldItem: null } as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.playerHeldItem).toBeNull();
});

test("playerHeldItem 存在时解码为 PlayerHeldItemInfo", () => {
  const snap = {
    ...validSnapshot,
    playerHeldItem: { itemId: "(O)388", name: "木头", qty: 4, marketPrice: 8 },
  } as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.playerHeldItem).toEqual({ itemId: "(O)388", name: "木头", qty: 4, marketPrice: 8 });
});

test("playerHeldItem 字段缺失时安全兜底默认值", () => {
  const snap = {
    ...validSnapshot,
    playerHeldItem: { itemId: "", name: "", qty: undefined, marketPrice: undefined },
  } as unknown as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.playerHeldItem).toEqual({ itemId: "", name: "", qty: 1, marketPrice: 0 });
});

test("currentGoal 缺失时降级为 null（旧客户端兼容）", () => {
  const scene = decodeWorldSnapshot(validSnapshot);
  expect(scene.currentGoal).toBeNull();
});

test("currentGoal 显式 null 降级为 null", () => {
  const snap = { ...validSnapshot, currentGoal: null } as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.currentGoal).toBeNull();
});

test("currentGoal 存在时解码为 CurrentGoalInfo", () => {
  const snap = {
    ...validSnapshot,
    currentGoal: { type: "chop_tree", params: { quantity: 10 }, progress: "已砍 3/10 棵", status: "Executing" },
  } as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.currentGoal).toEqual({ type: "chop_tree", params: { quantity: 10 }, progress: "已砍 3/10 棵", status: "Executing" });
});

test("currentGoal 字段缺失时安全兜底默认值", () => {
  const snap = {
    ...validSnapshot,
    currentGoal: { type: "mine", params: undefined, progress: undefined, status: undefined },
  } as unknown as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.currentGoal).toEqual({ type: "mine", params: {}, progress: "", status: "" });
});

// === Phase 3 L2：npcMood / npcRecentEvents / npcWorkingOn / npcOwedMoney ===

test("L2 字段缺失时全部降级为 null（旧客户端兼容）", () => {
  const scene = decodeWorldSnapshot(validSnapshot);
  expect(scene.npcMood).toBeNull();
  expect(scene.npcRecentEvents).toBeNull();
  expect(scene.npcWorkingOn).toBeNull();
  expect(scene.npcOwedMoney).toBeNull();
});

test("L2 字段显式 null 降级为 null", () => {
  const snap = { ...validSnapshot, npcMood: null, npcWorkingOn: null, npcOwedMoney: null } as unknown as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.npcMood).toBeNull();
  expect(scene.npcWorkingOn).toBeNull();
  expect(scene.npcOwedMoney).toBeNull();
  expect(scene.npcRecentEvents).toBeNull(); // 未携带 → null（未知）
});

test("L2 字段存在时解码为原始值", () => {
  const snap = {
    ...validSnapshot,
    npcMood: "烦躁",
    npcRecentEvents: ["上午在酒吧打工", "被农场主送了向日葵"],
    npcWorkingOn: "chop_tree",
    npcOwedMoney: 250,
  } as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.npcMood).toBe("烦躁");
  expect(scene.npcRecentEvents).toEqual(["上午在酒吧打工", "被农场主送了向日葵"]);
  expect(scene.npcWorkingOn).toBe("chop_tree");
  expect(scene.npcOwedMoney).toBe(250);
});

test("L2 npcRecentEvents 显式空数组解码为空数组（无近期事件 ≠ 未知）", () => {
  const snap = { ...validSnapshot, npcRecentEvents: [] } as WorldSnapshot;
  const scene = decodeWorldSnapshot(snap);
  expect(scene.npcRecentEvents).toEqual([]);
});

// === Phase 3 L3 / E3-5：currentBeat / npcPurchaseOffers（2026-09-12 接线） ===

test("L3 currentBeat 缺失/空串解码为 null，非空字符串原样解码", () => {
  const missing = decodeWorldSnapshot(validSnapshot);
  expect(missing.currentBeat).toBeNull();
  const empty = decodeWorldSnapshot({ ...validSnapshot, currentBeat: "" } as WorldSnapshot);
  expect(empty.currentBeat).toBeNull();
  const active = decodeWorldSnapshot({ ...validSnapshot, currentBeat: "她在湖边画画" } as WorldSnapshot);
  expect(active.currentBeat).toBe("她在湖边画画");
});

test("E3-5 npcPurchaseOffers 缺失解码为 null，数组逐项解码", () => {
  const missing = decodeWorldSnapshot(validSnapshot);
  expect(missing.npcPurchaseOffers).toBeNull();
  const withOffers = decodeWorldSnapshot({
    ...validSnapshot,
    npcPurchaseOffers: [{ itemId: "(O)388", itemName: "木材", quantity: 1, unitPrice: 12 }],
  } as WorldSnapshot);
  expect(withOffers.npcPurchaseOffers).toEqual([
    { itemId: "(O)388", itemName: "木材", quantity: 1, unitPrice: 12 },
  ]);
  const emptyArr = decodeWorldSnapshot({ ...validSnapshot, npcPurchaseOffers: [] } as WorldSnapshot);
  expect(emptyArr.npcPurchaseOffers).toEqual([]);
});

test("decodes snapshot with missing required fields throws", () => {
  // Use unknown cast to bypass TS check at call site
  const broken = { season: "summer" } as unknown as WorldSnapshot;
  expect(() => decodeWorldSnapshot(broken)).toThrow(/missing required field/);
});

test("timeToPeriod maps hours to time-of-day labels", () => {
  expect(timeToPeriod("03:00")).toBe("凌晨");
  expect(timeToPeriod("09:00")).toBe("上午");
  expect(timeToPeriod("12:00")).toBe("下午");
  expect(timeToPeriod("17:00")).toBe("晚上");
  expect(timeToPeriod("22:00")).toBe("深夜");
});

test("coarsenLocation maps known locations to area names", () => {
  expect(coarsenLocation("Town")).toBe("小镇");
  expect(coarsenLocation("SeedShop")).toBe("小镇");
  expect(coarsenLocation("Farm")).toBe("农场");
  expect(coarsenLocation("FarmHouse")).toBe("农场");
  expect(coarsenLocation("Forest")).toBe("森林");
  expect(coarsenLocation("Mine")).toBe("矿洞");
  expect(coarsenLocation("Beach")).toBe("海滩");
  expect(coarsenLocation("Unknown")).toBe("Unknown");
});

test("summarizeNearby returns input or default when empty", () => {
  expect(summarizeNearby("2 villagers")).toBe("2 villagers");
  expect(summarizeNearby("")).toBe("周围空无一人");
  expect(summarizeNearby(undefined as unknown as string)).toBe("周围空无一人");
});
