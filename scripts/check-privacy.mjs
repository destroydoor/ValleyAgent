#!/usr/bin/env node
/**
 * 隐私 / 凭据回归扫描。
 *
 * 目的：防止本机绝对路径、真实存档名、API key 等个人信息被重新提交进公开仓库。
 * 用法：node scripts/check-privacy.mjs      （非 0 退出码表示发现疑似泄露）
 *
 * 扫描范围：git 仓库内只扫 git 跟踪的文件（含已暂存的新文件）——能进公开仓库
 * 的只有这些；未跟踪的本机工具状态（.mimosa/.claude、临时补丁脚本等）扫了只会
 * 让本地门禁常红。CI 干净检出下与全量扫描等价，不放过任何会落库的泄露。
 * 非 git 目录（如导出包）回退到全目录遍历。
 */
import { spawnSync } from "node:child_process";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";

const ROOT = join(import.meta.dirname, "..");

const SKIP_DIRS = new Set([
  ".git", "node_modules", "bin", "obj", "dist", "coverage",
  "Stardew Valley", "linux-game", "saves", "test-recordings",
]);

// 本文件自身含有示例模式，扫描时跳过。
// 审计底稿系 2026-09-16 自未合并分支 arena/01a09648-valleyagent 原样恢复（issue #12），
// 承诺正文一字不改以保历史证据可核验；其中取证命令里的开发者本机路径属当时记录，故整文件豁免。
const SKIP_FILES = new Set([
  "scripts/check-privacy.mjs",
  "docs/plan/2026-09-12-architecture-drift-audit.md",
]);

const RULES = [
  // X:\ 是文档中约定的占位盘符，不算泄露
  { name: "开发者本机绝对路径", re: /(?<![Xx]):(?<=[A-Za-z]:)[\\/](Source|SteamLibrary|Games|Tools|Logs)[\\/]/ },
  { name: "Windows 用户目录", re: /[A-Za-z]:[\\/]Users[\\/](?!<)[A-Za-z0-9._-]+/ },
  { name: "疑似真实存档名（含账号数字）", re: /\b[A-Za-z][A-Za-z0-9]*_\d{9,}\b/ },
  { name: "OpenAI 风格 API key", re: /\bsk-[A-Za-z0-9_-]{16,}/ },
  { name: "GitHub token", re: /\bgh[pousr]_[A-Za-z0-9]{20,}/ },
  { name: "AWS access key", re: /\bAKIA[0-9A-Z]{16}\b/ },
  { name: "Google API key", re: /\bAIza[0-9A-Za-z_-]{30,}/ },
  { name: "Slack token", re: /\bxox[baprs]-[A-Za-z0-9-]{10,}/ },
  { name: "私钥文件内容", re: /-----BEGIN [A-Z ]*PRIVATE KEY-----/ },
  { name: "JWT", re: /\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\./ },
  { name: "邮箱地址", re: /[A-Za-z0-9._%+-]+@(?!example\.com|valley)[A-Za-z0-9.-]+\.[A-Za-z]{2,}/ },
  { name: "中国大陆手机号", re: /(?<![0-9])1[3-9]\d{9}(?![0-9])/ },
  { name: "非官方 npm 镜像源（暴露地理位置）", re: /registry\.npmmirror\.com/ },
];

const BINARY_EXT = /\.(png|jpg|jpeg|gif|ico|zip|dll|pdb|exe|mp4|wav|ogg|xnb|bun-build)$/i;

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    if (SKIP_DIRS.has(entry)) continue;
    const full = join(dir, entry);
    let st;
    try { st = statSync(full); } catch { continue; }
    if (st.isDirectory()) yield* walk(full);
    else if (st.isFile() && !BINARY_EXT.test(entry) && st.size < 8 * 1024 * 1024) yield full;
  }
}

// 返回 git 跟踪的仓库相对路径（NUL 分隔，规避文件名里的特殊字符）；
// 不在 git 仓库内或 git 不可用时返回 null，调用方回退到全目录遍历。
function listTrackedFiles() {
  const git = spawnSync("git", ["ls-files", "-c", "-z"], { cwd: ROOT });
  if (git.status !== 0 || !git.stdout?.length) return null;
  return git.stdout.toString("utf8").split("\0").filter(Boolean);
}

const tracked = listTrackedFiles();
const files = tracked
  ? tracked
      .filter((f) => !SKIP_DIRS.has(f.split("/")[0]))
      .map((f) => join(ROOT, f))
  : walk(ROOT);

const findings = [];
for (const file of files) {
  const rel = relative(ROOT, file).split(sep).join("/");
  if (SKIP_FILES.has(rel)) continue;
  let text;
  try { text = readFileSync(file, "utf8"); } catch { continue; }
  if (text.includes("\u0000")) continue;

  text.split(/\r?\n/).forEach((line, i) => {
    for (const rule of RULES) {
      const m = line.match(rule.re);
      if (m) findings.push({ rel, line: i + 1, rule: rule.name, text: m[0].slice(0, 80) });
    }
  });
}

if (findings.length === 0) {
  console.log("✅ 隐私扫描通过：未发现本机路径、真实存档名或凭据。");
  process.exit(0);
}

console.error(`❌ 发现 ${findings.length} 处疑似隐私/凭据泄露：\n`);
for (const f of findings) {
  console.error(`  ${f.rel}:${f.line}  [${f.rule}]  ${f.text}`);
}
console.error("\n如为误报，请调整 scripts/check-privacy.mjs 中的规则或跳过列表。");
process.exit(1);
