#!/usr/bin/env node
/**
 * 异常处理 / 错误可观测性回归扫描（2026-09-15 异常处理审计的配套门禁）。
 *
 * 目的：把审计结论变成可重复测量的规则，防止"故障隔离缺口"和"错误信息降级"回潮。
 *       审计底稿见 docs/plan/2026-09-15-exception-handling-audit.md。
 *
 * 用法：
 *   node scripts/check-exception-hygiene.mjs                     # 报告 + 棘轮门禁（新增违规 → 退出码 1）
 *   node scripts/check-exception-hygiene.mjs --all               # 不看基线，所有违规都算失败
 *   node scripts/check-exception-hygiene.mjs --update-baseline   # 把当前计数写回基线（修完一批后收紧用）
 *   node scripts/check-exception-hygiene.mjs --json              # 机器可读输出
 *
 * 棘轮（ratchet）语义：基线记录每个 规则|文件 的既有违规数（存量待修，不阻塞 CI）；
 * 任何文件的计数**上升**即失败；下降则提示收紧基线（跑 --update-baseline）。
 *
 * 规则分档：
 *   P0 = 破坏"任一功能出错时模组仍能跑"或"错误信息进不了持久化日志"的缺口
 *   P1 = 错误信息价值被显著削弱（丢栈 / 级别过低 / 静默丢弃）
 *   P2 = 一致性/风格类，长期收敛
 */
import { readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { join, relative, sep } from "node:path";

const ROOT = join(import.meta.dirname, "..");
const BASELINE_PATH = join(ROOT, "scripts", "exception-hygiene-baseline.json");

const CS_DIRS = ["src/ValleyAgent", "src/ValleyAgent.Abstractions"];
const TS_DIRS = ["server/packages/core/src", "server/packages/stardew/src"];
const PATCH_DIR = "src/ValleyAgent/Patches";

/** 队列泵 / 串行调度点：这些方法一旦抛出，同一 tick 内后续子系统全部停摆。 */
const PUMP_METHODS = new Set([
  "OnUpdateTicked", "OnTimeChanged", "OnDayStarted", "OnDayEnding", "OnSaving",
  "OnPlayerWarped", "OnMenuChanged", "OnRenderedHud", "OnButtonPressed",
  "ProcessPendingReplies", "ProcessShoutReplies", "ProcessMainThreadActions",
  "ProcessPendingWsCommands", "ProcessPendingMainThreadCommands", "ProcessPendingPreSpeakActions",
  "ProcessAgent", "TickExecuting", "TickReporting",
]);

/** 长跑循环：捕获类型不全 = 循环静默死亡（重连/心跳停摆后无人再唤醒）。 */
const LOOP_METHODS = /^(ReadLoopAsync|ReconnectIndependentAsync|HeartbeatLoopAsync|WatchLoop|RunAsync)$/;

/** 已知"故意静默"的 catch（规则 CS-SILENT-CATCH 白名单）：日志器自身 / 标题屏 NRE 守卫等。 */
const SILENT_CATCH_ALLOW = [
  "src/ValleyAgent/Infrastructure/ModErrorLog.cs",      // 日志器自身故障必须静默
  "src/ValleyAgent/Infrastructure/MainThreadWatchdog.cs", // 取证仪器不得把进程带崩
];

/** TS：failure 语义关键词（用于识别"错误被打成 console.log"）。 */
const FAIL_KEYWORDS = /(fail(ed|ure)?|error|unavailable|timeout|timed out|dropped|drop\b|rollback|rolled_back|BUSY|FALLBACK|invalid|corrupt|no pending|not wired|未配置|异常|失败|丢弃)/i;

const RULES = {
  "CS-HARMONY-NO-GUARD": { sev: "P0", desc: "Harmony 补丁入口无外层 try/catch —— 异常注入游戏原生调用栈（draw/input/checkAction），SMAPI 事件级兜底覆盖不到" },
  "CS-PUMP-NO-GUARD": { sev: "P0", desc: "队列泵/串行调度方法体零 try —— 任一子系统抛出即让同 tick 后续全部子系统停摆" },
  "CS-PUMP-NARROW-CATCH": { sev: "P1", desc: "队列泵内只捕窄类型（如 InvalidOperationException）—— NRE 等常见缺陷直接穿透" },
  "CS-LOOP-CATCH-GAP": { sev: "P0", desc: "长跑循环（读/重连/心跳）未捕 Exception —— 意外异常让循环永久死亡且无重启者" },
  "CS-LOG-NO-STACK": { sev: "P1", desc: "catch 里只记 ex.Message —— 丢异常类型与栈，NRE 类缺陷日志无定位价值" },
  "CS-SILENT-CATCH": { sev: "P1", desc: "catch 既不记日志也不重抛 —— 降级不可观测（违反 AGENTS.md §3.6 可观测性铁律）" },
  "CS-LOG-DEFAULT-LEVEL": { sev: "P2", desc: "Monitor.Log 未显式给 level —— SMAPI 默认 Trace，控制台默认不可见" },
  // TS-ERR-OBJECT-TO-TEE 已于 2026-09-16 退役（issue #21 修复）：log-tee 的 serializeArg
  // 增加 Error 分支后，Error 直传 console.* 会以 {name, message, stack} 结构落盘，
  // `console.error(msg, err)` 成为**正确写法**（无需先 err.message/err.stack 手工展开）。
  // 该规则是纯名字启发（按 err/ex/e 等标识符名猜测运行时类型），无法区分"Error 直传"
  // 与其它场景，留着只会拦截正确代码。存量 13 处即-issue #21 列出的错误出口，保持原样。
  "TS-FAIL-AT-LOG-LEVEL": { sev: "P1", desc: "失败/丢弃/降级事件打在 console.log —— 落盘级别为 [log]，无法按 ERROR/WARN grep" },
  "TS-EMPTY-CATCH": { sev: "P2", desc: "空 catch 且无注释说明 —— 静默吞异常" },
  "TS-UNKNOWN-TYPE-SILENT": { sev: "P0", desc: "消息类型 switch 的 default 分支无日志 —— 协议漂移/畸形帧被静默 ack" },
  "TS-THEN-NO-CATCH": { sev: "P1", desc: ".then() 链无 .catch() —— 订阅器/回放抛出即成 unhandledRejection，状态永久卡住" },
};

/* ─────────────────────────── 工具函数 ─────────────────────────── */

function walk(dir, exts, out = []) {
  let entries;
  try { entries = readdirSync(dir); } catch { return out; }
  for (const entry of entries) {
    if (entry === "obj" || entry === "bin" || entry === "node_modules") continue;
    const full = join(dir, entry);
    let st;
    try { st = statSync(full); } catch { continue; }
    if (st.isDirectory()) walk(full, exts, out);
    else if (exts.some((e) => entry.endsWith(e))) out.push(full);
  }
  return out;
}

const rel = (p) => relative(ROOT, p).split(sep).join("/");

/** 从第 i 行（含方法签名）开始按大括号配平截取方法体，返回 { body, lines, startLine }。 */
function extractBlock(lines, i) {
  let depth = 0;
  let started = false;
  const body = [];
  for (let j = i; j < lines.length && j < i + 2000; j++) {
    depth += (lines[j].match(/\{/g) || []).length - (lines[j].match(/\}/g) || []).length;
    if (lines[j].includes("{")) started = true;
    body.push(lines[j]);
    if (started && depth <= 0) break;
  }
  return { body, text: body.join("\n"), count: body.length };
}

/** 从方法体文本里逐个提取 catch 块（含 header 行），返回 [{ header, text, lineOffset }]。 */
function extractCatches(bodyLines) {
  const out = [];
  for (let k = 0; k < bodyLines.length; k++) {
    if (!/\bcatch\b/.test(bodyLines[k])) continue;
    if (bodyLines[k].trim().startsWith("//") || bodyLines[k].trim().startsWith("*")) continue;
    const sub = extractBlock(bodyLines, k);
    if (sub.count < 2) continue; // 单行 catch (X) { } 也>=2行；表达式体不计
    out.push({ header: bodyLines[k].trim(), text: sub.text, inner: sub.body.slice(1).join("\n"), lineOffset: k });
  }
  return out;
}

const findings = [];
function add(rule, file, line, detail) {
  findings.push({ rule, sev: RULES[rule].sev, file, line, detail });
}

/* ─────────────────────────── C# 扫描 ─────────────────────────── */

for (const dir of CS_DIRS) {
  for (const file of walk(join(ROOT, dir), [".cs"])) {
    const r = rel(file);
    const lines = readFileSync(file, "utf8").split(/\r?\n/);
    const isPatchFile = r.startsWith(PATCH_DIR);

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];

      /* --- Monitor.Log 未显式给 level（默认 Trace） --- */
      if (/\.Log(Once)?\s*\(/.test(line) && !/LogCallback|LogError|LogServerLine|LogServerEvent|DebugLogger|_debugLogger/.test(line)) {
        let stmt = line;
        let j = i;
        while (!stmt.trimEnd().endsWith(";") && j - i < 6 && j + 1 < lines.length) { j++; stmt += " " + lines[j]; }
        if (!/LogLevel/.test(stmt) && !/\bsmapiLevel\b/.test(stmt)) {
          add("CS-LOG-DEFAULT-LEVEL", r, i + 1, stmt.trim().slice(0, 120));
        }
      }

      /* --- 方法级检查 --- */
      const sig = line.match(/\b(?:private|public|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>?,.\[\] ]+\s+(\w+)\s*\(/);
      if (!sig) continue;
      const name = sig[1];
      if (line.trim().endsWith(";") || /=>/.test(line)) continue; // 表达式体方法不在本规则范围

      const isHarmonyEntry =
        isPatchFile &&
        (/^(Prefix|Postfix|Transpiler|Finalizer)$/.test(name) || /(Prefix|Postfix)$/.test(name)) &&
        (/\[HarmonyPatch/.test(lines.slice(Math.max(0, i - 5), i + 1).join("\n")) ||
          /^(Prefix|Postfix|Transpiler|Finalizer)$/.test(name));
      const isPump = PUMP_METHODS.has(name);
      const isLoop = LOOP_METHODS.test(name);
      if (!isHarmonyEntry && !isPump && !isLoop) continue;

      const blk = extractBlock(lines, i);
      if (blk.count < 3) continue;
      const bodyLines = blk.body;
      const firstStmt = bodyLines
        .slice(1)
        .map((l) => l.trim())
        .find((l) => l && !l.startsWith("//") && !l.startsWith("///") && !l.startsWith("{")) ?? "";
      const catches = extractCatches(bodyLines);
      const hasCatchAll = catches.some((c) => /catch\s*(\{|$|\(\s*Exception|\(\s*AggregateException)/.test(c.header) || /catch\s*\(\s*(Exception|AggregateException)/.test(c.header));

      if (isHarmonyEntry && !firstStmt.startsWith("try")) {
        add("CS-HARMONY-NO-GUARD", r, i + 1, `${name}(...) 首条语句不是 try（方法 ${blk.count} 行，catch ${catches.length} 处）`);
      }
      if (isPump && catches.length === 0) {
        add("CS-PUMP-NO-GUARD", r, i + 1, `${name}(...) 方法体 ${blk.count} 行、零 try/catch`);
      }
      if (isPump && catches.length > 0 && !hasCatchAll) {
        add("CS-PUMP-NARROW-CATCH", r, i + 1, `${name}(...) 只捕 ${catches.map((c) => c.header.replace(/\s+/g, " ")).join(" / ").slice(0, 100)}`);
      }
      if (isLoop && !hasCatchAll) {
        add("CS-LOOP-CATCH-GAP", r, i + 1, `${name}(...) catch 覆盖：${catches.map((c) => c.header.replace(/\s+/g, " ")).join(" / ").slice(0, 120) || "（无）"}`);
      }
    }

    /* --- catch 块级检查（全文件，不限方法） --- */
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      if (!/\bcatch\b/.test(line)) continue;
      const t = line.trim();
      if (t.startsWith("//") || t.startsWith("*") || t.startsWith("///")) continue;
      if (!/catch\s*(\(|\{)/.test(line)) continue; // 跳过注释/字符串里的 "catch" 字样
      const sub = extractBlock(lines, i);
      if (sub.count < 2) continue;
      const inner = sub.body.slice(1).join("\n");
      const header = t;
      const logsSomething = /Monitor|\bLog\(|LogCallback|ModErrorLog|QueueTelemetry|DebugLogger/.test(inner);
      const rethrows = /\bthrow\b/.test(inner);
      if (!logsSomething && !rethrows && !SILENT_CATCH_ALLOW.includes(r)) {
        const snippet = inner.replace(/\s+/g, " ").trim().slice(0, 90);
        add("CS-SILENT-CATCH", r, i + 1, `${header.replace(/\s+/g, " ")} → ${snippet || "(空)"}`);
      }
      if (logsSomething && !/\{ex\}|\{e\}|\{exception\}|\{err\}|\.ToString\(\)|, ex\)|,ex\)|, exception\)/.test(inner)) {
        add("CS-LOG-NO-STACK", r, i + 1, inner.replace(/\s+/g, " ").trim().slice(0, 110));
      }
    }
  }
}

/* ─────────────────────────── TS 扫描 ─────────────────────────── */

for (const dir of TS_DIRS) {
  for (const file of walk(join(ROOT, dir), [".ts"])) {
    const r = rel(file);
    const lines = readFileSync(file, "utf8").split(/\r?\n/);

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const trimmed = line.trim();
      if (trimmed.startsWith("//") || trimmed.startsWith("*")) continue;

      /* --- console.* 语句级检查（TS-ERR-OBJECT-TO-TEE 已退役，见 RULES 注释） --- */
      const cm = line.match(/console\.(error|warn|log|info)\s*\((.*)/);
      if (cm) {
        // 拼完整语句（可能跨行）
        let stmt = line;
        let j = i;
        while (!/[);]\s*$/.test(stmt.trimEnd()) && j - i < 6 && j + 1 < lines.length) { j++; stmt += " " + lines[j]; }
        /* --- 失败语义打在 console.log --- */
        if (/console\.log\s*\(/.test(line) && FAIL_KEYWORDS.test(stmt)) {
          add("TS-FAIL-AT-LOG-LEVEL", r, i + 1, stmt.trim().slice(0, 130));
        }
      }

      /* --- 空 catch 无注释 --- */
      if (/^\s*(\}\s*)?catch\s*(\([^)]*\))?\s*\{\s*\}\s*$/.test(line)) {
        add("TS-EMPTY-CATCH", r, i + 1, trimmed);
      }

      /* --- switch(type) 的 default 分支无日志 --- */
      if (/^\s*default:\s*(return|break)/.test(trimmed) || /^\s*default:\s*\{\s*$/.test(trimmed)) {
        // 向上找最近的 switch 判定表达式
        let switchHead = "";
        for (let k = i; k >= Math.max(0, i - 120); k--) {
          const m = lines[k].match(/switch\s*\(([^)]*)\)/);
          if (m) { switchHead = m[1]; break; }
        }
        if (/\.type|\btype\b|\bkind\b/.test(switchHead)) {
          let caseBody = trimmed;
          if (/\{\s*$/.test(trimmed)) {
            const blk = extractBlock(lines, i);
            caseBody = blk.text;
          }
          if (!/console\./.test(caseBody)) {
            add("TS-UNKNOWN-TYPE-SILENT", r, i + 1, `switch(${switchHead}) 的 default 分支无日志：${caseBody.replace(/\s+/g, " ").slice(0, 90)}`);
          }
        }
      }

      /* --- .then( 无 .catch( --- */
      if (/\.then\s*\(/.test(line)) {
        let stmt = line;
        let j = i;
        let depth = 0;
        do {
          depth += (lines[j].match(/\(/g) || []).length - (lines[j].match(/\)/g) || []).length;
          stmt += "\n" + (lines[j + 1] ?? "");
          j++;
        } while (depth > 0 && j - i < 40 && j < lines.length);
        if (!/\.catch\s*\(/.test(stmt) && !/allSettled/.test(stmt)) {
          add("TS-THEN-NO-CATCH", r, i + 1, stmt.replace(/\s+/g, " ").trim().slice(0, 120));
        }
      }
    }
  }
}

/* ─────────────────────────── 基线棘轮 ─────────────────────────── */

const counts = new Map();
for (const f of findings) {
  const key = `${f.rule}|${f.file}`;
  counts.set(key, (counts.get(key) ?? 0) + 1);
}

let baseline = {};
try { baseline = JSON.parse(readFileSync(BASELINE_PATH, "utf8")); } catch { baseline = {}; }

const argv = process.argv.slice(2);
const asJson = argv.includes("--json");
const failAll = argv.includes("--all");

if (argv.includes("--update-baseline")) {
  const next = {};
  for (const [k, v] of [...counts.entries()].sort()) next[k] = v;
  writeFileSync(BASELINE_PATH, JSON.stringify(next, null, 2) + "\n", "utf8");
  console.log(`✅ 基线已更新：${relative(ROOT, BASELINE_PATH)}（${Object.keys(next).length} 条 规则|文件 计数）`);
  process.exit(0);
}

if (asJson) {
  console.log(JSON.stringify({ findings, counts: Object.fromEntries(counts) }, null, 2));
  process.exit(0);
}

/* ─────────────────────────── 报告 ─────────────────────────── */

const byRule = new Map();
for (const f of findings) {
  if (!byRule.has(f.rule)) byRule.set(f.rule, []);
  byRule.get(f.rule).push(f);
}

console.log("异常处理 / 错误可观测性扫描（scripts/check-exception-hygiene.mjs）\n");
const order = ["P0", "P1", "P2"];
for (const sev of order) {
  const rules = Object.keys(RULES).filter((k) => RULES[k].sev === sev && byRule.has(k));
  if (rules.length === 0) continue;
  console.log(`── ${sev} ${"─".repeat(66)}`);
  for (const rule of rules) {
    const items = byRule.get(rule);
    console.log(`\n[${rule}] ${items.length} 处 — ${RULES[rule].desc}`);
    const shown = items.slice(0, Number(process.env.HYGIENE_SHOW ?? 12));
    for (const it of shown) console.log(`    ${it.file}:${it.line}  ${it.detail}`);
    if (items.length > shown.length) console.log(`    ... 另 ${items.length - shown.length} 处（HYGIENE_SHOW=<n> 可展开）`);
  }
  console.log("");
}

/* 棘轮判定 */
const regressions = [];
const improvements = [];
for (const [key, n] of counts) {
  const base = baseline[key] ?? 0;
  if (n > base) regressions.push({ key, base, n });
  else if (n < base) improvements.push({ key, base, n });
}
const unbaselined = Object.keys(baseline).filter((k) => !counts.has(k));

const total = findings.length;
const p0 = findings.filter((f) => f.sev === "P0").length;
console.log(`合计 ${total} 处（P0 ${p0} / P1 ${findings.filter((f) => f.sev === "P1").length} / P2 ${findings.filter((f) => f.sev === "P2").length}）`);

if (failAll) {
  if (total > 0) {
    console.error(`\n❌ --all 模式：${total} 处违规全部计为失败。`);
    process.exit(1);
  }
  console.log("\n✅ --all 模式：零违规。");
  process.exit(0);
}

if (Object.keys(baseline).length === 0) {
  console.error("\n❌ 基线为空：先跑 `node scripts/check-exception-hygiene.mjs --update-baseline` 建立存量基线，再让 CI 只拦新增。");
  process.exit(1);
}
if (regressions.length > 0) {
  console.error(`\n❌ 异常处理门禁失败：${regressions.length} 处 规则|文件 计数高于基线（新增违规）：`);
  for (const g of regressions) console.error(`    ${g.key}: 基线 ${g.base} → 当前 ${g.n}`);
  process.exit(1);
}
if (improvements.length > 0 || unbaselined.length > 0) {
  console.log(`\n⚠️  基线可收紧：${improvements.length} 处计数下降、${unbaselined.length} 处已清零 —— 跑 --update-baseline 固化成果。`);
  for (const g of improvements.slice(0, 20)) console.log(`    ${g.key}: 基线 ${g.base} → 当前 ${g.n}`);
  for (const k of unbaselined.slice(0, 20)) console.log(`    ${k}: 基线 ${baseline[k]} → 当前 0`);
}
console.log("\n✅ 无新增违规（存量见基线 scripts/exception-hygiene-baseline.json，按审计文档优先级逐批收敛）。");
process.exit(0);
