import { test, expect } from "bun:test";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

test("loads all 33 NPCs from npc_prompts.json", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const npcs = loader.listNpcs();
  expect(npcs.length).toBe(33);
  expect(npcs).toContain("Abigail");
  expect(npcs).toContain("Haley");
  expect(npcs).toContain("Wizard");
});

test("getNpcData returns base_memory + 5 phases for Abigail", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const data = loader.getNpcData("Abigail");
  expect(data).not.toBeNull();
  expect(data!.base_memory).toContain("Abigail");
  expect(data!.base_memory.length).toBeGreaterThan(50);
  expect(Object.keys(data!.phases)).toEqual([
    "stranger", "acquaintance", "friend", "close", "partner"
  ]);
  expect(data!.phases.stranger.friendship_range).toBe("0-250");
  expect(data!.phases.partner.friendship_range).toBe("2001-2500");
});

test("getNpcData returns null for unknown NPC", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  expect(loader.getNpcData("NonexistentNpc")).toBeNull();
});

test("getPhaseForFriendship returns correct phase", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  expect(loader.getPhaseForFriendship("Abigail", 0)).toBe("stranger");
  expect(loader.getPhaseForFriendship("Abigail", 250)).toBe("stranger");
  expect(loader.getPhaseForFriendship("Abigail", 251)).toBe("acquaintance");
  expect(loader.getPhaseForFriendship("Abigail", 500)).toBe("acquaintance");
  expect(loader.getPhaseForFriendship("Abigail", 501)).toBe("friend");
  expect(loader.getPhaseForFriendship("Abigail", 1000)).toBe("friend");
  expect(loader.getPhaseForFriendship("Abigail", 1001)).toBe("close");
  expect(loader.getPhaseForFriendship("Abigail", 2000)).toBe("close");
  expect(loader.getPhaseForFriendship("Abigail", 2001)).toBe("partner");
  expect(loader.getPhaseForFriendship("Abigail", 2500)).toBe("partner");
});

test("getPhasePrompt returns the phase prompt text", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const prompt = loader.getPhasePrompt("Abigail", 251);
  expect(prompt).toContain("Abigail");
  expect(prompt.length).toBeGreaterThan(50);
});

test("getAttitudeBrief returns correct brief for friendship level", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  expect(loader.getAttitudeBrief(0)).toContain("素不相识");
  expect(loader.getAttitudeBrief(300)).toContain("点头之交");
  expect(loader.getAttitudeBrief(800)).toContain("朋友");
  expect(loader.getAttitudeBrief(1500)).toContain("亲密好友");
  expect(loader.getAttitudeBrief(2200)).toContain("夫妻");
});
