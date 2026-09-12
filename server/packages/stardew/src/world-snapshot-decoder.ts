import type { WorldSnapshot, SceneState } from "./types";

const REQUIRED_FIELDS = ["season", "day", "time", "weather", "location", "friendship", "farmerName"] as const;

export function decodeWorldSnapshot(snap: WorldSnapshot): SceneState {
  for (const field of REQUIRED_FIELDS) {
    if (snap[field] === undefined || snap[field] === null) {
      throw new Error(`WorldSnapshot missing required field: ${field}`);
    }
  }

  return {
    season: String(snap.season),
    day: Number(snap.day),
    timeStr: String(snap.time),
    weather: String(snap.weather),
    location: String(snap.location),
    npcTile: { x: Number(snap.npcTile?.x ?? 0), y: Number(snap.npcTile?.y ?? 0) },
    nearbyObjects: snap.nearbyObjects ?? "",
    friendship: Number(snap.friendship),
    npcState: snap.npcState ?? "IDLE",
    inventory: Array.isArray(snap.inventory) ? snap.inventory.map(i => ({ name: String(i.name), quantity: Number(i.quantity) })) : [],
    farmerName: String(snap.farmerName),
    // E4-1: 玩家钱包。旧 C# 客户端不携带时为 null（LLM 感知缺钱/有钱场景）。
    playerMoney: snap.playerMoney === undefined || snap.playerMoney === null ? null : Number(snap.playerMoney),
    // E0-6: NPC 自己所在的地图名。旧 C# 客户端不携带时为 null（回退玩家地图）。
    npcLocation: snap.npcLocation === undefined || snap.npcLocation === null ? null : String(snap.npcLocation),
    // E4-2: NPC 钱包余额。旧 C# 客户端不携带时为 null。
    npcMoney: snap.npcMoney === undefined || snap.npcMoney === null ? null : Number(snap.npcMoney),
    // E4-2: NPC 真背包。字段缺失 → null（未知）；空数组 → 真空包。
    npcInventory: Array.isArray(snap.npcInventory) ? snap.npcInventory.map(i => ({ name: String(i.name), quantity: Number(i.quantity) })) : null,
    // Phase 1: 玩家手持物。字段缺失/为 null → null（旧客户端兼容，LLM 呈现"没有手持物"）。
    playerHeldItem: snap.playerHeldItem
      ? {
          itemId: String(snap.playerHeldItem.itemId ?? ""),
          name: String(snap.playerHeldItem.name ?? ""),
          qty: Number(snap.playerHeldItem.qty ?? 1),
          marketPrice: Number(snap.playerHeldItem.marketPrice ?? 0),
        }
      : null,
    // Phase 2: NPC 当前执行目标。字段缺失/为 null → null（未在执行目标）。
    currentGoal: snap.currentGoal
      ? {
          type: String(snap.currentGoal.type ?? ""),
          params: snap.currentGoal.params && typeof snap.currentGoal.params === "object"
            ? snap.currentGoal.params
            : {},
          progress: String(snap.currentGoal.progress ?? ""),
          status: String(snap.currentGoal.status ?? ""),
        }
      : null,
    // Phase 3 L2: NPC 心情标签。字段缺失/为 null → null（旧客户端兼容，prompt 显示"平静"）。
    npcMood: snap.npcMood === undefined || snap.npcMood === null ? null : String(snap.npcMood),
    // Phase 3 L2: NPC 近期事件。字段缺失 → null（未知）；空数组 → 无近期事件。
    npcRecentEvents: Array.isArray(snap.npcRecentEvents) ? snap.npcRecentEvents.map(String) : null,
    // Phase 3 L2: NPC 工作标记。字段缺失/为 null → null（无工作标记）。
    npcWorkingOn: snap.npcWorkingOn === undefined || snap.npcWorkingOn === null ? null : String(snap.npcWorkingOn),
    // Phase 3 L2: NPC 欠款。字段缺失/为 null → null（无欠款）。
    npcOwedMoney: snap.npcOwedMoney === undefined || snap.npcOwedMoney === null ? null : Number(snap.npcOwedMoney),
    // Phase 3 L3: 当前活跃 beat 场景描述。字段缺失/为 null → null（无活跃 beat，
    // prompt 整段省略）。2026-09-12 修复：C# 自 2026-08-06 起就发送该字段，但 TS
    // 解码器没有它——L3 剧本接收即丢弃，NPC 从未见过任何 beat。
    currentBeat: snap.currentBeat === undefined || snap.currentBeat === null || String(snap.currentBeat).trim() === ""
      ? null
      : String(snap.currentBeat),
    // E3-5: NPC 当日求购单。字段缺失 → null（未知，旧客户端）；空数组 → 无求购。
    npcPurchaseOffers: Array.isArray(snap.npcPurchaseOffers)
      ? snap.npcPurchaseOffers.map((o) => ({
          itemId: String(o.itemId ?? ""),
          itemName: String(o.itemName ?? ""),
          quantity: Number(o.quantity ?? 1),
          unitPrice: Number(o.unitPrice ?? 0),
        }))
      : null,
  };
}

export function timeToPeriod(timeStr: string): string {
  const hour = parseInt(timeStr.split(":")[0] ?? "9", 10);
  if (isNaN(hour)) return "上午";
  if (hour < 6) return "凌晨";
  if (hour < 10) return "上午";
  if (hour < 14) return "下午";
  if (hour < 18) return "晚上";
  return "深夜";
}

export function coarsenLocation(location: string): string {
  const map: Record<string, string> = {
    town: "小镇", seedshop: "小镇", saloon: "小镇", blacksmith: "小镇",
    farm: "农场", farmhouse: "农场", forest: "森林", mountain: "山区",
    mine: "矿洞", beach: "海滩", desert: "沙漠",
  };
  const key = location.toLowerCase().replace(/\s/g, "");
  return map[key] ?? location;
}

export function summarizeNearby(nearby: string | undefined): string {
  if (!nearby || nearby.trim() === "") return "周围空无一人";
  return nearby;
}
