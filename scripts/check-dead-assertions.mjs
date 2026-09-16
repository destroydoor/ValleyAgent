#!/usr/bin/env node
/**
 * 死断言检测（2026-08-20 测试系统大改 Phase 1：防乐观判定）。
 *
 * 扫描 logs/test_results 下所有历史断言 JSON（文件名以 _assertions.json 结尾），
 * 统计每个 (测试, 断言label) 的通过/失败次数，标记：
 *   - 从未失败的断言（候选死断言——断言永远为真，测不到任何东西）
 *   - 有反例约束（AssertEx 的 counterexample）但从未失败的高危候选
 *
 * 前置条件（post-run 本地工具）：logs/test_results/ 由游戏内测试（TestMod）运行时
 * 写出，且被 .gitignore 忽略不入库——干净 clone 上没有，必须先跑一轮游戏内测试。
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
  console.error(`结果目录不存在: ${resultsDir}

本工具是"游戏跑完之后"的本地校验（post-run），无法在干净环境单独运行：
  缺什么  : 结果目录（默认 logs/test_results/，可用 --dir 覆盖）及其下的 *_assertions.json
  怎么产出: 先在游戏内跑一轮测试（TestMod，由 scripts/test/run-tests.ps1 或
            scripts/test/test-game.ps1 驱动），每轮会在 logs/test_results/<时间戳>/
            下写出断言 JSON
  参考文档: scripts/TEST_README.md
注意：该目录已被 .gitignore 忽略，不会随仓库分发，也无法进 CI。`);
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
  console.error(`未找到任何 *_assertions.json（目录: ${resultsDir}）
这些文件由游戏内测试（TestMod）运行时写出——先在游戏里跑一轮测试（scripts/test/run-tests.ps1），再重试本工具；详见 scripts/TEST_README.md。`);
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

// ── 良性守卫 allowlist（2026-09-14 死断言清理计划 §2 K 处置）──
// 心跳/诊断型 Assert(label, true)：断言内容是"测试跑到此点"，条件即字面量 true，
// 既无法失败也无法改写为可失败断言。登记后 checker 不再列报；删除条目即恢复列报。
// 匹配规则：test 精确相等 + label 精确相等（或正则）。无条目时行为与旧版完全一致。
const ALLOWLIST = [
  // { test: "测试类名", label: "断言label 或 /正则/", reason: "为什么是良性守卫" },
  {
    test: "E10_IllegalTransitionPermitted",
    label: "No_crash_during_transition_tests",
    reason: "心跳型 Assert(true)：标记非法转换矩阵测试全程跑到收尾；真崩溃时结果文件根本不会写出，断言自身无失败语义（真行为断言是 Illegal_transitions_are_blocked_by_state_machine）",
  },
  {
    test: "E11_FightRadiusUnenforced",
    label: "Scan_correctly_identifies_monster_count",
    reason: "诊断信息型 Assert(true)：记录远/近两次 ScanEnvironment 原文供人工对照；真行为断言是 SearchRadius_enforced_far_monsters_filtered / Near_monster_detected_as_baseline",
  },
  {
    test: "E12_ForageScanBlindIndoor",
    label: "Scan_divergence_documented",
    reason: "诊断信息型 Assert(true)：记录室内/室外扫描差异；E12 的核心漏洞断言是 ForceForageLocation_flag_affects_ScanEnvironment",
  },
  {
    test: "E14_MineScanLocationCheck",
    label: "Scan_divergence_documented",
    reason: "诊断信息型 Assert(true)：记录 Farm/Mountain 扫描差异；E14 的核心漏洞断言是 ForceMiningLocation_flag_affects_ScanEnvironment",
  },
  {
    test: "F7_ScanConsistencyStress",
    label: "Scan_mismatches_documented_not_failures",
    reason: "诊断信息型 Assert(true)：扫描不匹配是 Handler location guard 的设计行为（E12/E14 单独验证），此断言只记录计数供审计",
  },
  {
    test: "F8_StateDurationBypass",
    label: "All_transitions_completed_without_crash",
    reason: "心跳型 Assert(true)：标记 10 轮状态持续时间压力测试跑到收尾；真行为断言是 ForceTransition_duration_bypass_blocked",
  },
  {
    test: "Func_IllegalTransitionAudit",
    label: "Audit_complete_without_crash",
    reason: "心跳型 Assert(true)：标记全转换矩阵审计跑到收尾（统计已移入日志）；真行为断言是 No_illegal_transition_succeeds / All_legal_transitions_succeed / Full_transition_matrix_covered",
  },
  {
    test: "Func_NpcInventoryFidelity",
    label: "Inventory_roundtrip_completed",
    reason: "心跳型 Assert(true)：标记背包填充/覆写/读取往返跑到收尾；真行为断言是 All_12_slots_populated / No_empty_or_null_slot_names / Filled_item_appears_in_inventory",
  },
];

function isAllowlisted(testName, label) {
  return ALLOWLIST.some(
    (e) =>
      e.test === testName &&
      (e.label instanceof RegExp ? e.label.test(label) : e.label === label)
  );
}

// ── 输出 ──
console.log(`扫描 ${totalRuns} 次运行的断言结果 (min-runs=${minRuns})\n`);

// 分级：无反例约束且全通过 = 高风险（可能死断言）；有反例约束且全通过 = 有效但稳定（低风险）
const highRisk = [];
const lowRisk = [];
let allowlisted = 0;

for (const [key, s] of stats) {
  const runs = s.pass + s.fail;
  if (runs < minRuns) continue;
  if (s.fail === 0) {
    const sep = key.indexOf("|");
    const testName = key.slice(0, sep);
    const label = key.slice(sep + 1);
    const counterexample = [...s.counterexamples][0] ?? "";
    if (!counterexample && isAllowlisted(testName, label)) {
      allowlisted++;
      continue;
    }
    (counterexample ? lowRisk : highRisk).push({ key, runs, counterexample });
  }
}

highRisk.sort((a, b) => b.runs - a.runs);

if (allowlisted > 0) {
  console.log(
    `已按 allowlist 抑制 ${allowlisted} 条良性守卫（登记见本脚本 ALLOWLIST 段，含理由）\n`
  );
}

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
