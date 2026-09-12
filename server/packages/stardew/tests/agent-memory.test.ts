import { test, expect } from "bun:test";
import { AgentMemory } from "../src/agent-memory";
import { mkdtempSync, rmSync, existsSync, readFileSync, writeFileSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";

function makeTempDir(): string {
  return mkdtempSync(join(tmpdir(), "valley-memory-test-"));
}

test("implements MemoryBackend interface", () => {
  const mem = new AgentMemory("Abigail", "/tmp/nonexistent");
  // Structural check: all methods exist
  expect(typeof mem.addConversation).toBe("function");
  expect(typeof mem.addMemory).toBe("function");
  expect(typeof mem.addSignificantMemory).toBe("function");
  expect(typeof mem.getConversationContext).toBe("function");
  expect(typeof mem.getRecentMemories).toBe("function");
  expect(typeof mem.getSignificantMemoriesText).toBe("function");
  expect(typeof mem.load).toBe("function");
  expect(typeof mem.save).toBe("function");
});

test("addConversation appends and trims to 50 entries", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  for (let i = 0; i < 60; i++) {
    mem.addConversation("player", `msg ${i}`);
  }
  expect(mem.conversationHistory.length).toBe(50);
  expect(mem.conversationHistory[0]!.text).toBe("msg 10");
});

test("addMemory merges duplicate text by incrementing count (no time window)", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("picked up a rock", 5, "event", "Mine", ["item"]);
  mem.addMemory("picked up a rock", 5, "event", "Mine", ["item"]);
  expect(mem.shortTermMemories.length).toBe(1);
  expect(mem.shortTermMemories[0]!.count).toBe(2);
});

test("addMemory merge bumps importance by 0.5 capped at 10", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("saw a rock", 5, "event", "Mine", []);
  mem.addMemory("saw a rock", 5, "event", "Mine", []);
  expect(mem.shortTermMemories[0]!.importance).toBeCloseTo(5.5, 5);
  // Add many times to test cap at 10
  for (let i = 0; i < 20; i++) {
    mem.addMemory("saw a rock", 5, "event", "Mine", []);
  }
  expect(mem.shortTermMemories[0]!.importance).toBe(10);
  expect(mem.shortTermMemories.length).toBe(1);
});

test("addMemory merge refreshes timestamp to now", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("saw a rock", 5, "event", "Mine", []);
  const firstTs = mem.shortTermMemories[0]!.timestamp;
  // Wait a tiny bit then add again to ensure timestamp moves forward
  const start = Date.now() / 1000;
  while (Date.now() / 1000 - start < 0.05) { /* spin briefly */ }
  mem.addMemory("saw a rock", 5, "event", "Mine", []);
  expect(mem.shortTermMemories[0]!.timestamp).toBeGreaterThan(firstTs);
});

test("addMemory does not merge different texts", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("picked up a rock", 5, "event", "Mine", []);
  mem.addMemory("picked up a gem", 5, "event", "Mine", []);
  expect(mem.shortTermMemories.length).toBe(2);
  expect(mem.shortTermMemories[0]!.count).toBe(1);
  expect(mem.shortTermMemories[1]!.count).toBe(1);
});

test("addMemory new entry starts with count=1", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("first memory", 3, "event", "Town", []);
  expect(mem.shortTermMemories[0]!.count).toBe(1);
});

test("getRecentMemories shows (xN) suffix when count > 1", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("picked up a rock", 5, "event", "Mine", []);
  mem.addMemory("picked up a rock", 5, "event", "Mine", []);
  mem.addMemory("picked up a rock", 5, "event", "Mine", []);
  const text = mem.getRecentMemories(5);
  expect(text).toContain("picked up a rock");
  expect(text).toContain("×3");
});

test("getRecentMemories omits suffix when count is 1", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("picked up a rock", 5, "event", "Mine", []);
  const text = mem.getRecentMemories(5);
  expect(text).toContain("picked up a rock");
  expect(text).not.toContain("×");
});

test("addMemory caps shortTermMemories at 30 entries", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  for (let i = 0; i < 40; i++) {
    mem.addMemory(`unique memory ${i}`, 1 + (i % 5), "event", "Town", [`tag${i}`]);
  }
  expect(mem.shortTermMemories.length).toBe(30);
});

test("addSignificantMemory deduplicates by text", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  const added1 = mem.addSignificantMemory("我和农场主第一次见面了", "relationship", "joy", ["Farmer"], "Town");
  const added2 = mem.addSignificantMemory("我和农场主第一次见面了", "relationship", "joy", ["Farmer"], "Town");
  expect(added1).toBe(true);
  expect(added2).toBe(false);
  expect(mem.significantMemories.length).toBe(1);
});

test("save writes JSON file with expected schema", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Abigail_memory.json");
    const mem = new AgentMemory("Abigail", filePath);
    mem.addConversation("player", "hello");
    mem.addConversation("npc", "hi");
    mem.addMemory("saw a rock", 3, "event", "Mine", ["rock"]);
    mem.addSignificantMemory("first meeting", "relationship", "joy", ["Farmer"], "Town");
    mem.addFriendship(50);

    await mem.save();
    expect(existsSync(filePath)).toBe(true);

    const raw = readFileSync(filePath, "utf-8");
    const parsed = JSON.parse(raw);
    expect(parsed.npcName).toBe("Abigail");
    expect(parsed.conversationHistory).toHaveLength(2);
    expect(parsed.shortTermMemories).toHaveLength(1);
    expect(parsed.shortTermMemories[0].count).toBe(1);
    expect(parsed.significantMemories).toHaveLength(1);
    expect(parsed.friendship).toBe(50);
    expect(parsed.lastSavedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("load reads JSON file and restores state", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Abigail_memory.json");
    // Pre-populate file
    const seed = {
      npcName: "Abigail",
      conversationHistory: [{ role: "player", text: "previous msg" }],
      shortTermMemories: [{ text: "old memory", timestamp: 1000, importance: 5, entryType: "event", location: "Town", tags: [] }],
      significantMemories: [{ text: "past event", timestamp: 1000, category: "life_event", emotionalWeight: "joy", relatedNpcs: [], location: "Town" }],
      friendship: 200,
      lastSavedAt: "2026-07-18T00:00:00Z",
    };
    writeFileSync(filePath, JSON.stringify(seed));

    const mem = new AgentMemory("Abigail", filePath);
    await mem.load();
    // M2a 迁移：旧单文件格式的对话/好感搬入 _legacy 玩家桶（首个真实玩家对话时认领）；
    // 世界桶保留任务类记忆与非 relationship significant。
    expect(mem.conversationHistory).toHaveLength(0);
    expect(mem.shortTermMemories).toHaveLength(1);
    expect(mem.significantMemories).toHaveLength(1);
    const legacy = mem.getLoadedPlayerMemory("_legacy");
    expect(legacy?.conversationHistory).toHaveLength(1);
    expect(legacy?.conversationHistory[0]!.text).toBe("previous msg");
    expect(legacy?.friendship).toBe(200);
    // 认领：真实玩家访问时 _legacy 数据转移
    await mem.getPlayerMemory("player-1", "新农夫");
    const claimed = mem.getLoadedPlayerMemory("player-1");
    expect(claimed?.conversationHistory).toHaveLength(1);
    expect(claimed?.friendship).toBe(200);
    expect(mem.getLoadedPlayerMemory("_legacy")).toBeUndefined();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("load defaults count to 1 for legacy entries without count field", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Abigail_memory.json");
    // Seed a legacy file without the count field
    const seed = {
      npcName: "Abigail",
      conversationHistory: [],
      shortTermMemories: [
        { text: "old memory", timestamp: 1000, importance: 5, entryType: "event", location: "Town", tags: [] },
      ],
      significantMemories: [],
      friendship: 0,
      lastSavedAt: "2026-07-18T00:00:00Z",
    };
    writeFileSync(filePath, JSON.stringify(seed));

    const mem = new AgentMemory("Abigail", filePath);
    await mem.load();
    expect(mem.shortTermMemories).toHaveLength(1);
    expect(mem.shortTermMemories[0]!.count).toBe(1);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("load restores count field from saved file", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Abigail_memory.json");
    const mem = new AgentMemory("Abigail", filePath);
    mem.addMemory("saw a rock", 5, "event", "Mine", []);
    mem.addMemory("saw a rock", 5, "event", "Mine", []);
    mem.addMemory("saw a rock", 5, "event", "Mine", []);
    await mem.save();

    const mem2 = new AgentMemory("Abigail", filePath);
    await mem2.load();
    expect(mem2.shortTermMemories).toHaveLength(1);
    expect(mem2.shortTermMemories[0]!.count).toBe(3);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("load with missing file is a no-op (starts fresh)", async () => {
  const mem = new AgentMemory("Abigail", "/tmp/nonexistent_memory.json");
  await mem.load();
  expect(mem.conversationHistory).toEqual([]);
  expect(mem.shortTermMemories).toEqual([]);
  expect(mem.significantMemories).toEqual([]);
  expect(mem.friendship).toBe(0);
});

test("load + save roundtrip preserves state", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Haley_memory.json");
    const mem1 = new AgentMemory("Haley", filePath);
    // 旧调用（无 playerId）：对话/好感走世界桶（兼容）；load 时迁移到 _legacy。
    mem1.addConversation("player", "test");
    mem1.addMemory("event", 5, "event", "Town", []);
    mem1.addSignificantMemory("milestone", "relationship", "joy", ["Farmer"], "Town");
    mem1.addFriendship(100);
    await mem1.save();

    const mem2 = new AgentMemory("Haley", filePath);
    await mem2.load();
    // 世界桶：任务类记忆保留；relationship significant 与对话/好感迁入 _legacy。
    expect(mem2.conversationHistory).toEqual([]);
    expect(mem2.shortTermMemories).toEqual(mem1.shortTermMemories);
    expect(mem2.significantMemories).toEqual([]);
    const legacy = mem2.getLoadedPlayerMemory("_legacy");
    expect(legacy?.conversationHistory).toEqual(mem1.conversationHistory);
    expect(legacy?.significantMemories).toEqual(mem1.significantMemories);
    expect(legacy?.friendship).toBe(mem1.friendship);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("addFriendship clamps to 0-2500 range", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addFriendship(3000);
  expect(mem.friendship).toBe(2500);
  mem.addFriendship(-5000);
  expect(mem.friendship).toBe(0);
});

test("getConversationContext formats recent conversation", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addConversation("player", "你好");
  mem.addConversation("npc", "嗨");
  const ctx = mem.getConversationContext(10);
  expect(ctx).toContain("农场主: 你好");
  expect(ctx).toContain("Abigail: 嗨");
});

test("getSignificantMemoriesText returns default when empty", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  expect(mem.getSignificantMemoriesText()).toBe("（暂无特别记忆）");
});

test("removeMemory deletes matching short-term memory by substring", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("在矿洞捡了一块石头", 3, "event", "Mine", ["item"]);
  mem.addMemory("在农场收获了防风草", 3, "event", "Farm", ["crop"]);
  const removed = mem.removeMemory("石头");
  expect(removed).toBe(1);
  expect(mem.shortTermMemories.length).toBe(1);
  expect(mem.shortTermMemories[0]!.text).toContain("防风草");
});

test("removeMemory returns 0 when no match", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("在矿洞捡了一块石头", 3, "event", "Mine", []);
  expect(mem.removeMemory("不存在的事情")).toBe(0);
  expect(mem.shortTermMemories.length).toBe(1);
});

test("removeMemory never touches significantMemories", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addSignificantMemory("我和农场主第一次一起战斗了", "life_event", "joy", ["Farmer"], "Mine");
  const removed = mem.removeMemory("战斗");
  expect(removed).toBe(0);
  expect(mem.significantMemories.length).toBe(1);
});

test("removeMemory deletes all matches and returns count", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("捡了石头 A", 3, "event", "Mine", []);
  mem.addMemory("捡了石头 B", 3, "event", "Mine", []);
  mem.addMemory("收获了作物", 3, "event", "Farm", []);
  expect(mem.removeMemory("石头")).toBe(2);
  expect(mem.shortTermMemories.length).toBe(1);
});

test("removeMemory persists through save/load roundtrip", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Abigail_memory.json");
    const mem = new AgentMemory("Abigail", filePath);
    mem.addMemory("捡了石头 A", 3, "event", "Mine", []);
    mem.addMemory("捡了石头 B", 3, "event", "Mine", []);
    mem.addMemory("收获了作物", 3, "event", "Farm", []);
    expect(mem.removeMemory("石头")).toBe(2);
    expect(mem.shortTermMemories.length).toBe(1);
    await mem.save();

    const mem2 = new AgentMemory("Abigail", filePath);
    await mem2.load();
    expect(mem2.shortTermMemories.length).toBe(1);
    expect(mem2.shortTermMemories[0]!.text).toContain("作物");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("removeMemory returns 0 for empty or whitespace-only input", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("在矿洞捡了一块石头", 3, "event", "Mine", []);
  expect(mem.removeMemory("")).toBe(0);
  expect(mem.removeMemory("   ")).toBe(0);
  expect(mem.shortTermMemories.length).toBe(1);
});

// ── M2a 迁移守卫 + 认领清理（2026-08-23 审计修复）─────────

test("legacyMigrated flag skips migration even if world bucket has conversations", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Haley_memory.json");
    // 带守卫标记的世界文件：对话/好感即使存在（beat 路径合法回写）也不得二次打包 _legacy
    writeFileSync(
      filePath,
      JSON.stringify({
        npcName: "Haley",
        conversationHistory: [{ role: "npc", text: "beat 台词" }],
        shortTermMemories: [],
        significantMemories: [],
        friendship: 300,
        legacyMigrated: true,
        lastSavedAt: "2026-08-20T00:00:00Z",
      }),
    );

    const mem = new AgentMemory("Haley", filePath);
    await mem.load();
    expect(mem.conversationHistory).toHaveLength(1); // 留在世界桶，不迁移
    expect(mem.getLoadedPlayerMemory("_legacy")).toBeUndefined();

    // 标记随 save 持久化（roundtrip 后依旧跳过）
    await mem.save();
    const onDisk = JSON.parse(readFileSync(filePath, "utf-8"));
    expect(onDisk.legacyMigrated).toBe(true);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("first successful load persists the legacyMigrated guard on save", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Haley_memory.json");
    writeFileSync(
      filePath,
      JSON.stringify({
        npcName: "Haley",
        conversationHistory: [{ role: "player", text: "旧对话" }],
        shortTermMemories: [],
        significantMemories: [],
        friendship: 100,
        lastSavedAt: "2026-07-01T00:00:00Z",
      }),
    );

    const mem = new AgentMemory("Haley", filePath);
    await mem.load(); // 本次发生迁移 → 置位守卫
    expect(mem.getLoadedPlayerMemory("_legacy")).toBeDefined();
    await mem.save();

    const onDisk = JSON.parse(readFileSync(filePath, "utf-8"));
    expect(onDisk.legacyMigrated).toBe(true);
    // 下一次 load 见标记直接跳过：世界桶新积累的对话不再被打包成 _legacy
    const mem2 = new AgentMemory("Haley", filePath);
    await mem2.load();
    mem2.addConversation("npc", "beat 台词"); // 模拟其他路径回写世界桶
    await mem2.save();
    const mem3 = new AgentMemory("Haley", filePath);
    await mem3.load();
    expect(mem3.conversationHistory).toHaveLength(1);
    expect(mem3.getLoadedPlayerMemory("_legacy")).toBeUndefined();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("claiming _legacy deletes the legacy rel file from disk", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Haley_memory.json");
    writeFileSync(
      filePath,
      JSON.stringify({
        npcName: "Haley",
        conversationHistory: [{ role: "player", text: "旧对话" }],
        shortTermMemories: [],
        significantMemories: [],
        friendship: 100,
        lastSavedAt: "2026-07-01T00:00:00Z",
      }),
    );
    const legacyPath = join(dir, "Haley_players", "_legacy_rel.json");

    const mem = new AgentMemory("Haley", filePath);
    await mem.load();
    await mem.save(); // _legacy 桶落盘（迁移后首次保存）
    expect(existsSync(legacyPath)).toBe(true);

    await mem.getPlayerMemory("player-a", "阿明"); // 认领
    expect(mem.getLoadedPlayerMemory("_legacy")).toBeUndefined();
    // 磁盘残留文件一并删除——否则重启后会被再次当迁移数据认领（复制而非转移）
    expect(existsSync(legacyPath)).toBe(false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("removeMemory with playerId rejects empty match without wiping the bucket", async () => {
  const dir = makeTempDir();
  try {
    const mem = new AgentMemory("Abigail", join(dir, "Abigail_memory.json"));
    await mem.getPlayerMemory("player-a", "阿明");
    mem.addMemory("想买农场主的木头但没买成", 3, "event", "Town", ["trade"], "player-a", "阿明");

    // includes("") 恒真——空串若放行会清空整个玩家桶
    expect(mem.removeMemory("", "player-a")).toBe(0);
    expect(mem.removeMemory("   ", "player-a")).toBe(0);
    expect(mem.getRecentMemories(5, "player-a")).toContain("木头");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
