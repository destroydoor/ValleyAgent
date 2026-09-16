import { test, expect } from "bun:test";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { tmpdir } from "node:os";
import { join, resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

/** 临时捕获 console.error（issue #25 验收：降级必须有明确原因行）。 */
function captureConsoleError<T>(fn: () => T): { result: T; errors: string[] } {
  const errors: string[] = [];
  const original = console.error;
  console.error = (...args: unknown[]) => {
    errors.push(args.map((a) => String(a)).join(" "));
  };
  try {
    return { result: fn(), errors };
  } finally {
    console.error = original;
  }
}

function tempFile(content: string): string {
  const dir = mkdtempSync(join(tmpdir(), "npc-prompts-"));
  const file = join(dir, "npc_prompts.json");
  writeFileSync(file, content, "utf-8");
  return file;
}

function cleanup(file: string): void {
  try {
    rmSync(file, { force: true });
  } catch {
    // 临时文件清理失败不影响测试结论
  }
}

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

// ── issue #25（异常处理审计 §4.6）：人设文件缺失/损坏 → 回退最小人设，进程不秒退 ──

test("missing persona file: no throw, minimal fallback per name, error logged with path", () => {
  const missingPath = join(tmpdir(), `npc-prompts-missing-${Date.now()}-${Math.random()}.json`);

  const { result: loader, errors } = captureConsoleError(() => new NpcPromptLoader(missingPath));

  // 进程不秒退：构造正常返回，人设按名字回退中性最小模板（个性化仅限名字本身）
  const npc = loader.getNpcData("Abigail");
  expect(npc).not.toBeNull();
  expect(npc!.base_memory).toContain("Abigail");
  expect(npc!.phases.stranger.prompt).toContain("Abigail");

  // 好感阶段走正常路径：不同档位模板不同
  const strangerPrompt = loader.getPhasePrompt("Abigail", 0);
  const partnerPrompt = loader.getPhasePrompt("Abigail", 2100);
  expect(strangerPrompt).toContain("Abigail");
  expect(partnerPrompt).not.toBe(strangerPrompt);

  // console.error 有明确原因（含文件路径）
  expect(errors.length).toBeGreaterThan(0);
  const logged = errors.join("\n");
  expect(logged).toContain(missingPath);
  expect(logged).toContain("npc-prompts");
});

test("corrupted JSON: falls back to minimal personas and logs parse failure reason", () => {
  const file = tempFile('{ "Abigail": { "base_memory": "oops", ');

  const { result: loader, errors } = captureConsoleError(() => new NpcPromptLoader(file));

  const npc = loader.getNpcData("Abigail");
  expect(npc).not.toBeNull();
  // 回退模板而非半解析数据
  expect(npc!.base_memory).not.toBe("oops");
  expect(npc!.phases.acquaintance.prompt).toContain("Abigail");

  expect(errors.length).toBeGreaterThan(0);
  const logged = errors.join("\n");
  expect(logged).toContain(file);
  // 解析错误原因（SyntaxError 带位置/原因描述）
  expect(logged).toMatch(/SyntaxError|JSON/i);
  cleanup(file);
});

test("non-object root JSON: falls back with explicit reason", () => {
  const file = tempFile("[1, 2, 3]");

  const { result: loader, errors } = captureConsoleError(() => new NpcPromptLoader(file));

  expect(loader.getNpcData("Alex")).not.toBeNull();
  expect(errors.join("\n")).toContain(file);
  expect(errors.join("\n")).toContain("not an object");
  cleanup(file);
});

test("valid persona file: loads normally, guard does not misfire (zero console.error)", () => {
  const { errors } = captureConsoleError(() => new NpcPromptLoader(DATA_PATH));

  expect(errors).toEqual([]);

  const loader = new NpcPromptLoader(DATA_PATH);
  expect(loader.listNpcs().length).toBeGreaterThan(0);
  const abigail = loader.getNpcData("Abigail");
  expect(abigail).not.toBeNull();
  // 真实人设（非最小模板）
  expect(abigail!.phases.stranger.prompt).not.toContain("人设文件缺失");
});

test("normal mode: unknown NPC keeps null-return contract (generic prompt line unchanged)", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  expect(loader.getNpcData("NonexistentNpc")).toBeNull();
  expect(loader.getPhasePrompt("NonexistentNpc", 100)).toBe("你是 NonexistentNpc，一个星露谷的居民。");
});
