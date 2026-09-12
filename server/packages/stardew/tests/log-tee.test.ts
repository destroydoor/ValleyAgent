// Tests for log-tee (2026-09-11 观测补强：控制台输出自落盘).
// Run: bun test packages/stardew/tests/log-tee.test.ts
//
// Verifies:
//   1. attachConsoleTee mirrors console.log/warn/error to the file, preserving
//      original console behavior, with `[<ISO>] [level] <text>` line format and
//      space-joined multi-arg serialization (string as-is, objects JSON-stringified).
//   2. Rotation: with a small injected maxBytes, appending past the threshold
//      rotates the current file to `.old` (delete-old-rename strategy, same as
//      C# ModErrorLog).
//   3. Idempotency: re-attaching the same path is a no-op (console patched once).

import { test, expect } from "bun:test";
import { attachConsoleTee } from "../src/log-tee";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

/** 每行必须匹配 `[<ISO 时间戳>] [level] ` 前缀。 */
const LINE_PREFIX_RE = /^\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z\] \[(log|info|warn|error)\] /;

test("attachConsoleTee writes level-tagged lines with ISO timestamp prefix", () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-logtee-"));
  try {
    const logFile = join(dir, "server.log");
    attachConsoleTee(logFile);

    console.log("tee-log-line");
    console.info("tee-info-line");
    console.warn("tee-warn-line", { a: 1 });
    console.error("tee-error-line");

    expect(existsSync(logFile)).toBe(true);
    const content = readFileSync(logFile, "utf8");
    const lines = content.split("\n").filter((l) => l.length > 0);
    expect(lines.length).toBe(4);
    for (const line of lines) {
      expect(LINE_PREFIX_RE.test(line)).toBe(true);
    }
    expect(lines[0]).toContain("[log] tee-log-line");
    expect(lines[1]).toContain("[info] tee-info-line");
    // 多参数空格连接：string 原样，对象 JSON.stringify
    expect(lines[2]).toContain('[warn] tee-warn-line {"a":1}');
    expect(lines[3]).toContain("[error] tee-error-line");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("attachConsoleTee rotates to .old when file exceeds injected maxBytes", () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-logtee-rot-"));
  try {
    const logFile = join(dir, "server.log");
    // 注入小阈值（默认 5MB 无法在测试中触发）
    attachConsoleTee(logFile, 64);

    console.log("first rotation line — long enough to exceed the tiny threshold on its own");
    expect(existsSync(logFile + ".old")).toBe(false);

    // 第二条追加前发现文件 > 64 字节 → 轮转：当前文件改名 .old，新行写进新文件
    console.log("second rotation line");

    expect(existsSync(logFile + ".old")).toBe(true);
    expect(readFileSync(logFile + ".old", "utf8")).toContain("first rotation line");
    expect(readFileSync(logFile, "utf8")).toContain("second rotation line");
    expect(readFileSync(logFile, "utf8")).not.toContain("first rotation line");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("attachConsoleTee is idempotent for the same path (console patched once)", () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-logtee-idem-"));
  try {
    const logFile = join(dir, "server.log");
    attachConsoleTee(logFile);
    attachConsoleTee(logFile);
    console.log("idempotent-line");
    const content = readFileSync(logFile, "utf8");
    // 只出现一次（若重复 patch 会叠两层包装 → 双写）
    expect(content.match(/idempotent-line/g)?.length).toBe(1);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
