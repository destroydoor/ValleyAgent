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

// ── Error 序列化（issue #21 / 审计 §3.6）──────────────────────────────
// 此前 JSON.stringify(new Error("x")) === "{}"（message/stack 不可枚举），
// tee 作为 ServerConsoleWindow=true 模式下唯一持久化现场会把错误详情整个销毁。

/** 读 logFile 中含 marker 的那一行（tee 每条日志一行）。 */
function readLineWith(logFile: string, marker: string): string {
  const line = readFileSync(logFile, "utf8").split("\n").find((l) => l.includes(marker));
  if (line === undefined) throw new Error(`marker "${marker}" not found in ${logFile}`);
  return line;
}

test("console.error(msg, Error) tee 行含 name/message/stack，不再是 {}", () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-logtee-err-"));
  try {
    const logFile = join(dir, "server.log");
    attachConsoleTee(logFile);

    console.error("tee-error-object:", new Error("boom"));

    const line = readLineWith(logFile, "tee-error-object:");
    expect(line).toContain("boom");            // message
    expect(line).toContain("Error");           // name（结构字段值）
    expect(line).toMatch(/at /);               // stack（bun 运行时有真实栈帧）
    expect(line).not.toBe('{}');               // 整行不再是空对象
    expect(line).not.toContain('tee-error-object: {}');
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("Error 的可枚举自定义属性（如 code）随结构保留", () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-logtee-props-"));
  try {
    const logFile = join(dir, "server.log");
    attachConsoleTee(logFile);

    const err = new Error("enoent");
    (err as { code?: string }).code = "ENOENT";
    console.error("tee-error-props:", err);

    const line = readLineWith(logFile, "tee-error-props:");
    expect(line).toContain("enoent");
    expect(line).toContain("ENOENT");
    expect(line).toContain("code");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("Error.cause 链递归下钻（多层）", () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-logtee-cause-"));
  try {
    const logFile = join(dir, "server.log");
    attachConsoleTee(logFile);

    const root = new Error("root-cause-msg");
    const mid = new Error("mid-cause-msg", { cause: root });
    const top = new Error("top-cause-msg", { cause: mid });
    console.error("tee-cause-chain:", top);

    const line = readLineWith(logFile, "tee-cause-chain:");
    expect(line).toContain("top-cause-msg");
    expect(line).toContain("mid-cause-msg");   // cause 第 1 层
    expect(line).toContain("root-cause-msg");  // cause 第 2 层（多于一层的递归）
    expect(line).toContain("cause");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("AggregateError.errors 数组逐项展开", () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-logtee-agg-"));
  try {
    const logFile = join(dir, "server.log");
    attachConsoleTee(logFile);

    const agg = new AggregateError([new Error("inner-a"), new Error("inner-b")], "aggregate-failed");
    console.error("tee-aggregate:", agg);

    const line = readLineWith(logFile, "tee-aggregate:");
    expect(line).toContain("aggregate-failed");
    expect(line).toContain("inner-a");
    expect(line).toContain("inner-b");
    expect(line).toContain("AggregateError");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("循环 cause 链被标记而非挂死/爆栈", () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-logtee-circ-"));
  try {
    const logFile = join(dir, "server.log");
    attachConsoleTee(logFile);

    const a = new Error("cyc-a");
    const b = new Error("cyc-b", { cause: a });
    (a as { cause?: unknown }).cause = b; // a ↔ b 互为 cause
    console.error("tee-circular:", a);

    const line = readLineWith(logFile, "tee-circular:");
    expect(line).toContain("cyc-a");
    expect(line).toContain("cyc-b");
    expect(line).toContain("[circular]");
    // 单行落盘（栈内换行被 JSON 转义，不得把日志文件撕成多行）
    expect(line).toMatch(LINE_PREFIX_RE);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
