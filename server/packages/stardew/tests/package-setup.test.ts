import { test, expect } from "bun:test";
import { existsSync, statSync } from "fs";
import { resolve } from "path";

test("npc_prompts.json data file exists in stardew package", () => {
  const p = resolve(import.meta.dir, "../data/npc_prompts.json");
  expect(existsSync(p)).toBe(true);
  const stats = statSync(p);
  expect(stats.size).toBeGreaterThan(50_000); // ~90KB expected
});

test("can import @valley/core from stardew package", async () => {
  const Core = await import("@valley/core");
  expect(Core.Agent).toBeDefined();
  expect(Core.CORE_VERSION).toBe("0.1.0");
});

test("stardew package index exports placeholder", async () => {
  const Stardew = await import("../src/index");
  expect(Stardew.STARDEW_VERSION).toBe("0.1.0");
});
