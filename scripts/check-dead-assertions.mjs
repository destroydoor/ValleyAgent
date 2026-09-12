#!/usr/bin/env node
/**
 * 死断言检测（2026-08-20 测试系统大改 Phase 1：防乐观判定）。
 *
 * 扫描 logs/test_results 下所有历史断言 JSON（文件名以 _assertions.json 结尾），
 * 统计每个 (测试, 断言label) 的通过/失败次数，标记：
 *   - 从未失败的断言（候选死断言——断言永远为真，测不到任何东西）
 *   - 有反例约束（AssertEx 的 counterexample）但从未失败的高危候选
 *
 * 用法：
 *   node scripts/check-dead-assertions.mjs [--min-runs N] [--dir <logs/test_results>]
 *
 * 退出码：0 = 无候选；1 = 发现候选死断言（CI 可接）。
 */
import { readdirSync, readFileSync, existsSync, statSync } from "node:fs";
import { join, resolve } from "node:path";

const root = process.cwd();
const defaultDir = resolve(root, "logs/test_results");
const args = process.argv.slice(2);
const minRuns = parseInt(args[args.indexOf("--min-runs") + 1] ?? "2", 10);
const dirIdx = args.indexOf("--dir");
const resultsDir = dirIdx >= 0 ? resolve(args[dirIdx + 1]) : defaultDir;

if (!existsSync(resultsDir)) {
  console.error(`结果目录不存在: ${resultsDir}`);
  process.exit(2);
}

// ── 收集所有断言 JSON ──
function collectAssertionFiles(dir) {
  const out = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...collectAssertionFiles(p));
    else if (entry.name.endsWith("_assertions.json")) out.push(p);
  }
  return out;
}

const files = collectAssertionFiles(resultsDir);
if (files.length === 0) {
  console.error(`未找到任何 *_assertions.json（目录: ${resultsDir}）`);
  process.exit(2);
}

// ── 聚合 (test, label) → {pass, fail, counterexamples} ──
const stats = new Map(); // key: `${test}|${label}`
let totalRuns = 0;

for (const f of files) {
  let data;
  try {
    data = JSON.parse(readFileSync(f, "utf-8"));
  } catch {
    continue; // 坏文件跳过
  }
  totalRuns++;
  const testName = data.test_name ?? data.testName ?? f;
  const assertions = data.assertions ?? [];
  for (const a of assertions) {
    const label = a.Label ?? a.label ?? "?";
    const key = `${testName}|${label}`;
    const s = stats.get(key) ?? { pass: 0, fail: 0, counterexamples: new Set() };
    if (a.Passed ?? a.passed) s.pass++;
    else s.fail++;
    const ce = a.Counterexample ?? a.counterexample;
    if (ce) s.counterexamples.add(ce);
    stats.set(key, s);
  }
}

// ── 输出 ──
console.log(`扫描 ${totalRuns} 次运行的断言结果 (min-runs=${minRuns})\n`);

// 分级：无反例约束且全通过 = 高风险（可能死断言）；有反例约束且全通过 = 有效但稳定（低风险）
const highRisk = [];
const lowRisk = [];

for (const [key, s] of stats) {
  const runs = s.pass + s.fail;
  if (runs < minRuns) continue;
  if (s.fail === 0) {
    const counterexample = [...s.counterexamples][0] ?? "";
    (counterexample ? lowRisk : highRisk).push({ key, runs, counterexample });
  }
}

highRisk.sort((a, b) => b.runs - a.runs);

console.log("=== 高风险候选死断言（无反例约束 + 从未失败，共 " + highRisk.length + "）===");
for (const c of highRisk) {
  console.log(`  ${c.key}  (${c.runs} 次全通过)`);
}

console.log(`\n=== 有效但稳定（有反例约束 + 从未失败，共 ${lowRisk.length}）===\n`);
for (const c of lowRisk) {
  console.log(`  ${c.key}  (${c.runs} 次全通过)\n      反例: ${c.counterexample}`);
}

if (highRisk.length > 0) {
  console.log(`\n结论: 发现 ${highRisk.length} 个高风险候选死断言（需补反例约束或人工确认）FAIL`);
  process.exit(1);
}

console.log("结论: 无高风险候选死断言。PASS");
process.exit(0);
