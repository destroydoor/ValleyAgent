import { test, expect } from "bun:test";
import { AgentMemory } from "../src/agent-memory";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { mkdtempSync, rmSync, writeFileSync } from "fs";
import { join, resolve } from "path";
import { tmpdir } from "os";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

function makeScene(friendship: number, farmerName: string) {
  return {
    season: "summer",
    day: 1,
    timeStr: "14:30",
    weather: "sunny",
    location: "Town",
    nearbyObjects: "",
    farmerName,
    friendship,
    npcState: "IDLE",
    inventory: [],
    npcTile: { x: 0, y: 0 },
    playerMoney: 500,
    npcLocation: "Town",
    npcMoney: 500,
    npcInventory: [],
    playerHeldItem: null,
    currentGoal: null,
    npcMood: null,
    npcRecentEvents: null,
    npcWorkingOn: null,
    npcOwedMoney: null,
  } as const;
}

test("M2a: two players' conversations stay in separate player buckets", async () => {
  const dir = mkdtempSync(join(tmpdir(), "mp-context-"));
  try {
    const mem = new AgentMemory("Haley", join(dir, "Haley_memory.json"));
    await mem.load();

    // 预加载两个玩家桶（对话前 protocol-adapter 的流程）
    await mem.getPlayerMemory("player-a", "阿明");
    await mem.getPlayerMemory("player-b", "小红");

    mem.addConversation("player", "阿明：明天一起去矿洞吗？", "player-a", "阿明");
    mem.addConversation("npc", "我怕灰弄脏头发。", "player-a", "阿明");
    mem.addConversation("player", "小红：帮我浇水吧！", "player-b", "小红");

    // 各玩家桶历史独立（不串味）
    const ctxA = mem.getConversationContext(10, "player-a");
    expect(ctxA).toContain("矿洞");
    expect(ctxA).not.toContain("浇水");
    expect(ctxA).toContain("阿明");
    const ctxB = mem.getConversationContext(10, "player-b");
    expect(ctxB).toContain("浇水");
    expect(ctxB).not.toContain("矿洞");
    expect(ctxB).toContain("小红");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("M2a: friendship is per-player and delta applies to the right bucket", async () => {
  const dir = mkdtempSync(join(tmpdir(), "mp-friendship-"));
  try {
    const mem = new AgentMemory("Haley", join(dir, "Haley_memory.json"));
    await mem.load();
    await mem.getPlayerMemory("player-a", "阿明");
    await mem.getPlayerMemory("player-b", "小红");

    mem.addFriendship(200, "player-a");
    expect(mem.getFriendship("player-a")).toBe(200);
    expect(mem.getFriendship("player-b")).toBe(0); // B 不受 A 影响

    mem.addFriendship(-50, "player-a");
    expect(mem.getFriendship("player-a")).toBe(150);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("M2a: prompt renders per-player attitude (subject + phase by that player's friendship)", async () => {
  const dir = mkdtempSync(join(tmpdir(), "mp-prompt-"));
  try {
    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const mem = new AgentMemory("Haley", join(dir, "Haley_memory.json"));
    await mem.load();
    await mem.getPlayerMemory("player-a", "阿明");
    await mem.getPlayerMemory("player-b", "小红");

    // A 好感 0（素不相识），B 好感 1300（亲密好友）
    mem.setFriendship(0, "player-a");
    mem.setFriendship(1300, "player-b");

    const promptA = builder.buildDialogueSystemPrompt(
      mem, makeScene(0, "阿明") as never, "Haley", "", undefined, "player-a", "阿明",
    );
    expect(promptA).toContain("你和阿明素不相识");
    expect(promptA).toContain("你称呼阿明为：阿明"); // 称呼玩家化

    const promptB = builder.buildDialogueSystemPrompt(
      mem, makeScene(1300, "小红") as never, "Haley", "", undefined, "player-b", "小红",
    );
    expect(promptB).toContain("你和小红是亲密好友");
    expect(promptB).toContain("你称呼小红为：小红");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("M2a: legacy single-file data migrates to _legacy and is claimed by first real player", async () => {
  const dir = mkdtempSync(join(tmpdir(), "mp-migrate-"));
  try {
    const filePath = join(dir, "Haley_memory.json");
    writeFileSync(
      filePath,
      JSON.stringify({
        npcName: "Haley",
        conversationHistory: [{ role: "player", text: "旧对话" }],
        shortTermMemories: [],
        significantMemories: [{ text: "和农场主结婚", timestamp: 1, category: "relationship", emotionalWeight: "joy", relatedNpcs: ["Farmer"], location: "Town" }],
        friendship: 2500,
        lastSavedAt: "2026-07-01T00:00:00Z",
      }),
    );

    const mem = new AgentMemory("Haley", filePath);
    await mem.load();
    // relationship significant 与对话/好感 → _legacy；世界桶干净
    expect(mem.significantMemories).toEqual([]);
    const legacy = mem.getLoadedPlayerMemory("_legacy");
    expect(legacy?.conversationHistory).toHaveLength(1);
    expect(legacy?.friendship).toBe(2500);
    expect(legacy?.significantMemories).toHaveLength(1);

    // 认领：真实玩家对话时数据转移
    await mem.getPlayerMemory("player-a", "阿明");
    const claimed = mem.getLoadedPlayerMemory("player-a");
    expect(claimed?.conversationHistory[0]!.text).toBe("旧对话");
    expect(claimed?.friendship).toBe(2500);
    expect(claimed?.significantMemories).toHaveLength(1);
    expect(mem.getLoadedPlayerMemory("_legacy")).toBeUndefined();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
