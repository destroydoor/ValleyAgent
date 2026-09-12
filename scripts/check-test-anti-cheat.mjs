#!/usr/bin/env node
/**
 * 测试防作弊静态检查（2026-08-20 Phase 1；2026-08-31 加固：补齐 xUnit / 注释断言 /
 * 多行空 catch / 跳过测试 / tautology 盲区）。
 *
 * 宗旨：保证"测试能够失败"——抓"让测试容易过 / 无法失败"模式。
 *
 * 覆盖模式：
 *   A. 常量真断言（永远通过）：
 *      - TestMod 自定义签名 Assert("label", true) 于方法体直接层
 *        （控制流块内的常量属失败路径记录，不标记——见原 2026-08-20 注释）
 *      - xUnit Assert.True(true) / Assert.False(false)（任意位置）
 *      - 信息类（不计违规）：Assert.True(false)/Assert.False(true)——永远失败，
 *        属 broken test 而非"无法失败"
 *   B. 无条件通过：Assert.Pass()
 *   C. 同值 tautology：Assert.Equal(a, a) / StrictEqual / Same（两侧同一字面量/标识符）
 *   D. 跳过的测试：[Fact(Skip=...)] / [Theory(Skip=...)]——runner 标 skipped，不测任何东西
 *   E. 注释掉的断言：整行 // Assert.X(...) 或 // Assert("...——被悄悄绕过
 *   F. 空 catch 块（单行或多行）：异常被吞，应有断言或日志
 *
 * 已知未覆盖（后续单独设计）：[Fact]/[Theory] 方法体零断言——需方法边界解析，
 * 假阳性风险高，不在本版。
 *
 * 用法：
 *   node scripts/check-test-anti-cheat.mjs                # 扫 TestMod/Tests + UnitTests
 *   node scripts/check-test-anti-cheat.mjs --src <dir>    # 扫单个目录
 *   node scripts/check-test-anti-cheat.mjs --info-skip    # D(跳过)计为 info 不计违规
 *
 * 退出码：0 = 无违规；1 = 发现违规（CI 可接）；2 = 目录不存在/无文件。
 */
import { readdirSync, readFileSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";

const root = process.cwd();
const args = process.argv.slice(2);
const srcIdx = args.indexOf("--src");
const infoSkip = args.includes("--info-skip");
const DEFAULT_DIRS = ["src/ValleyAgent.TestMod/Tests", "src/ValleyAgent.UnitTests"];
const srcDirs = srcIdx >= 0 ? [resolve(args[srcIdx + 1])] : DEFAULT_DIRS.map((d) => resolve(root, d));

function collectCs(dir) {
  const out = [];
  if (!existsSync(dir)) return out;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...collectCs(p));
    else if (entry.name.endsWith(".cs")) out.push(p);
  }
  return out;
}

let files = [];
for (const d of srcDirs) files.push(...collectCs(d));
files = [...new Set(files)];

if (files.length === 0) {
  console.error(`未找到 .cs 测试文件（扫描: ${srcDirs.join(", ")}）`);
  process.exit(2);
}

// ── 注释剥离：返回每行 { code, lineComment }，跨行块注释状态正确 ──
// code = 去掉 // 和 /* */ 后的代码部分；lineComment = 当行 // 注释文本（块注释内不收）
function stripComments(lines) {
  let inBlock = false;
  return lines.map((raw) => {
    let code = "";
    let lineComment = "";
    let i = 0;
    while (i < raw.length) {
      if (inBlock) {
        const end = raw.indexOf("*/", i);
        if (end === -1) { i = raw.length; }
        else { inBlock = false; i = end + 2; }
      } else {
        const lc = raw.indexOf("//", i);
        const bc = raw.indexOf("/*", i);
        if (lc !== -1 && (bc === -1 || lc < bc)) { code += raw.slice(i, lc); lineComment = raw.slice(lc + 2); break; }
        if (bc !== -1) { code += raw.slice(i, bc); inBlock = true; i = bc + 2; }
        else { code += raw.slice(i); break; }
      }
    }
    return { code, lineComment };
  });
}

// ── 空 catch：从 catch 关键字起做括号深度匹配，块体无非空白字符即判空 ──
// 返回 { openLine, closeLine }（1-based）；未闭合（文件尾）返回 null
function emptyCatchBody(codes, lineIdx) {
  const m = codes[lineIdx].match(/\bcatch\b/);
  if (!m) return null;
  const start = m.index + m[0].length;
  let depth = 0, started = false, body = "", openLine = lineIdx + 1, closeLine = lineIdx + 1;
  for (let j = lineIdx; j < codes.length; j++) {
    const seg = j === lineIdx ? codes[lineIdx].slice(start) : codes[j];
    for (let k = 0; k < seg.length; k++) {
      const ch = seg[k];
      if (ch === "{") { if (!started) { started = true; openLine = j + 1; } depth++; }
      else if (ch === "}") {
        if (started) { depth--; if (depth === 0) { closeLine = j + 1; return /\S/.test(body) ? null : { openLine, closeLine }; } }
      } else if (started && depth >= 1) { body += ch; }
    }
  }
  return null;
}

const violations = [];
const infos = [];

for (const f of files) {
  const rawLines = readFileSync(f, "utf-8").split("\n");
  const parsed = stripComments(rawLines);
  const codes = parsed.map((p) => p.code);
  const rel = f.replace(root, "").replace(/\\/g, "/").replace(/^\//, "");

  for (let i = 0; i < rawLines.length; i++) {
    const raw = rawLines[i];
    const code = codes[i];
    const lc = parsed[i].lineComment;
    const lineNo = i + 1;

    // A. 常量真断言
    const tm = code.match(/Assert\(\s*"([^"]+)"\s*,\s*true\s*(?:,|\))/);
    if (tm) {
      const indent = raw.length - raw.trimStart().length;
      // 缩进 < 12 视为方法体直接层（2 级=8；3 级=12 起为控制流块内，属失败路径记录）
      if (indent < 12) violations.push({ file: rel, line: lineNo, code: raw.trim(), kind: `A 常量真断言（TestMod 方法体直接层 label="${tm[1]}"）——无条件通过` });
    }
    if (/Assert\.True\(\s*true\s*\)/.test(code) || /Assert\.False\(\s*false\s*\)/.test(code)) {
      violations.push({ file: rel, line: lineNo, code: raw.trim(), kind: "A 常量真断言（xUnit Assert.True(true)/Assert.False(false)）——永远通过" });
    }
    if (/Assert\.True\(\s*false\s*\)/.test(code) || /Assert\.False\(\s*true\s*\)/.test(code)) {
      infos.push({ file: rel, line: lineNo, code: raw.trim(), kind: "信息：常量失败断言（Assert.True(false)/Assert.False(true)）——永远失败，检查是否漏了条件" });
    }

    // B. 无条件通过
    if (/Assert\.Pass\(\)/.test(code)) {
      violations.push({ file: rel, line: lineNo, code: raw.trim(), kind: "B 无条件通过 Assert.Pass()——测不到任何东西" });
    }

    // C. 同值 tautology（两侧同一 token）
    const taut = code.match(/Assert\.(Equal|StrictEqual|Same)\(\s*([A-Za-z0-9_."]+)\s*,\s*\2\s*\)/);
    if (taut) {
      violations.push({ file: rel, line: lineNo, code: raw.trim(), kind: `C tautology Assert.${taut[1]}(${taut[2]}, ${taut[2]})——两侧同值，永真` });
    }

    // D. 跳过的测试
    if (/\[(Fact|Theory)\s*\([^)]*Skip\b/.test(code)) {
      const entry = { file: rel, line: lineNo, code: raw.trim(), kind: "D 跳过的测试 [Fact/Theory(Skip=...)]——runner 标 skipped，不测任何东西" };
      if (infoSkip) infos.push(entry); else violations.push(entry);
    }

    // E. 注释掉的断言（整行 //）——文档原列 Pattern C，此前未实现
    if (/\bAssert\.[A-Za-z]+\(/.test(lc) || /\bAssert\("/.test(lc)) {
      violations.push({ file: rel, line: lineNo, code: raw.trim(), kind: "E 注释掉的断言——被悄悄绕过，恢复为有效断言或删除" });
    }
  }

  // F. 空 catch（单行/多行统一）。有注释说明 → info；完全无内容 → violation
  for (let i = 0; i < codes.length; i++) {
    if (!/\bcatch\b/.test(codes[i])) continue;
    const r = emptyCatchBody(codes, i);
    if (!r) continue;
    let hasComment = false;
    for (let l = r.openLine - 1; l <= r.closeLine - 1 && l < rawLines.length; l++) {
      if (l < 0) continue;
      if (/\/\//.test(rawLines[l]) || /\/\*/.test(rawLines[l])) { hasComment = true; break; }
    }
    const entry = {
      file: rel, line: i + 1, code: rawLines[i].trim(),
      kind: `F 空 catch（${r.openLine}–${r.closeLine}，${hasComment ? "有注释说明" : "完全无内容"}）——异常被吞`,
    };
    if (hasComment) infos.push(entry); else violations.push(entry);
  }
}

console.log(`扫描 ${files.length} 个测试文件\n`);
if (infos.length > 0) {
  console.log(`=== 信息（不计违规）${infos.length} 处 ===`);
  for (const v of infos) console.log(`  [${v.kind}]\n    ${v.file}:${v.line}\n    ${v.code}`);
}
if (violations.length === 0) {
  console.log("无防作弊违规。PASS");
  process.exit(0);
}
console.log(`\n=== 发现 ${violations.length} 处违规 ===\n`);
for (const v of violations) console.log(`  [${v.kind}]\n    ${v.file}:${v.line}\n    ${v.code}\n`);
console.log("结论: 存在防作弊违规。FAIL");
process.exit(1);
