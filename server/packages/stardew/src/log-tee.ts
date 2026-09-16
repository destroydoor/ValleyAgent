// log-tee — 控制台输出自落盘（2026-09-11 主机卡死排查观测补强）。
//
// 用途：C# 端 ServerProcessManager 在"可见控制台窗口"模式（发行包默认，
// ServerConsoleWindow=true）下无法重定向 stdout——卡死时 TS 侧最后行为全部
// 丢失。本模块在 TS 进程内把 console.log/info/warn/error 逐条镜像追加到文件，
// 卡死后可事后取证。
//
// 接口：环境变量 VALLEY_SERVER_LOGFILE（C# 端在 ConsoleWindow=true 分支设置；
// false 分支 C# 自己捕获 stdout 重定向到同一文件，不设此变量，避免双写——
// 两路互斥使用同一文件）。见 server.ts 顶部的 attach 调用。
//
// 轮转策略与 C# 侧 ModErrorLog 一致：追加前若文件超过 maxBytes（默认 5MB），
// 先删旧 .old 再把当前文件改名 .old，然后重新开始写。
//
// 防御约束：一切 IO 包 try/catch 静默——日志器绝不能把服务器搞崩。原 console
// 行为完整保留（先调原函数再落盘）。

import { appendFileSync, renameSync, statSync, unlinkSync } from "node:fs";

/** 轮转阈值默认 5MB（与 C# ModErrorLog.MaxBytes 一致）。 */
const DEFAULT_MAX_BYTES = 5 * 1024 * 1024;

const LEVELS = ["log", "info", "warn", "error"] as const;
type ConsoleLevel = (typeof LEVELS)[number];

/** 当前落盘配置。单槽：重复 attach 同一路径是 no-op；换路径 = 重定向到新文件。 */
let activeConfig: { filePath: string; maxBytes: number } | null = null;
/** console 只 patch 一次（重复 attach 不叠加包装）。 */
let patched = false;

/** Error cause / AggregateError.errors 递归深度上限（防病态链/自引用把日志行撑爆）。 */
const MAX_ERROR_DEPTH = 4;

type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

/**
 * Error → 纯 JSON 结构（name/message/stack + 可枚举自定义属性 + cause 链 + AggregateError.errors）。
 * 深度与循环双保险：超过 MAX_ERROR_DEPTH 截断为 "[max depth]"，已访问对象再遇记 "[circular]"。
 * 背景（2026-09-15 审计 §3.6 / issue #21）：Error 的 message/stack 是不可枚举属性，
 * JSON.stringify(Error) === "{}"——tee 是 ServerConsoleWindow=true 模式下唯一持久化现场，
 * 此前所有 console.error(msg, err) 的错误详情在落盘层就被销毁。
 */
function errorToJson(err: Error, depth: number, seen: Set<object>): JsonValue {
  const out: Record<string, JsonValue> = { name: err.name, message: err.message };
  if (err.stack !== undefined) out.stack = err.stack;
  // 可枚举自定义属性（如 err.code）随结构保留。
  // cause/errors 跳过——下面有专门处理：否则同一子树会被序列化两次，
  // 第二次必然命中 seen 集合被错标成 "[circular]"。
  const isAgg = err instanceof AggregateError;
  for (const [k, v] of Object.entries(err)) {
    if (k === "cause" || (isAgg && k === "errors")) continue;
    if (v !== undefined) out[k] = toJsonValue(v, depth + 1, seen);
  }
  // ES2022 cause 链：cause 是 Error（或含 Error 的结构）时继续下钻
  const cause = err.cause;
  if (cause !== undefined) out.cause = toJsonValue(cause, depth + 1, seen);
  if (isAgg) {
    out.errors = err.errors.map((e) => toJsonValue(e, depth + 1, seen));
  }
  return out;
}

/** 任意值 → 纯 JSON 值（Error 结构化；bigint/function/symbol 降级；循环引用标记）。 */
function toJsonValue(v: unknown, depth: number, seen: Set<object>): JsonValue {
  // 直接对 v 做 typeof（不经中间变量）：TS 只对前者做类型收窄
  if (v === null || typeof v === "undefined") return null;
  if (typeof v === "string" || typeof v === "number" || typeof v === "boolean") return v;
  if (typeof v === "bigint" || typeof v === "symbol" || typeof v === "function") return null;
  if (depth >= MAX_ERROR_DEPTH) return "[max depth]";
  if (seen.has(v)) return "[circular]";
  seen.add(v);
  if (v instanceof Error) return errorToJson(v, depth, seen);
  if (Array.isArray(v)) return v.map((x) => toJsonValue(x, depth + 1, seen));
  const out: Record<string, JsonValue> = {};
  for (const [k, val] of Object.entries(v)) out[k] = toJsonValue(val, depth + 1, seen);
  return out;
}

/** 参数序列化：string 原样；Error 结构化（issue #21）；其余尝试 JSON.stringify（undefined/抛错退 String()）。 */
function serializeArg(arg: unknown): string {
  if (typeof arg === "string") return arg;
  if (arg instanceof Error) {
    try {
      // 经 toJsonValue 入口（而非直调 errorToJson）：顶层 Error 也进 seen 集合，
      // 否则自引用环 a↔b 会因根节点未登记而多绕一圈
      return JSON.stringify(toJsonValue(arg, 0, new Set<object>())) ?? `${arg.name}: ${arg.message}`;
    } catch {
      return `${arg.name}: ${arg.message}`; // 理论不可达（纯结构），保底与 String(Error) 等价
    }
  }
  try {
    const s = JSON.stringify(arg);
    if (s !== undefined) return s;
  } catch {
    // 循环引用等 stringify 失败 → String() 兜底
  }
  return String(arg);
}

function serializeArgs(args: unknown[]): string {
  return args.map(serializeArg).join(" ");
}

/** 追加一行 `[<ISO 时间戳>] [level] <文本>`；追加前超阈值则先轮转到 .old。 */
function writeTeeLine(level: ConsoleLevel, args: unknown[]): void {
  const cfg = activeConfig;
  if (!cfg) return;
  try {
    // 轮转检查（文件不存在等 stat 失败 → 跳过轮转直接追加）
    try {
      if (statSync(cfg.filePath).size > cfg.maxBytes) {
        const oldPath = cfg.filePath + ".old";
        try {
          unlinkSync(oldPath);
        } catch {
          // 旧 .old 不存在即无事
        }
        renameSync(cfg.filePath, oldPath);
      }
    } catch {
      // stat 失败（首条日志文件尚不存在）→ 落到下面直接追加
    }
    appendFileSync(cfg.filePath, `[${new Date().toISOString()}] [${level}] ${serializeArgs(args)}\n`, "utf8");
  } catch {
    // 日志 IO 失败绝不上抛、不打回 console（防递归/防崩溃）
  }
}

function patchConsoleOnce(): void {
  if (patched) return;
  for (const level of LEVELS) {
    const original = console[level];
    console[level] = (...args: unknown[]) => {
      original.apply(console, args);
      writeTeeLine(level, args);
    };
  }
  patched = true;
}

/**
 * 把 console.log/info/warn/error 镜像追加到 filePath（原行为保留：先调原函数再落盘）。
 * 幂等：对同一路径重复 attach 只生效一次（console 只 patch 一次，不叠加包装）。
 * maxBytes 可注入用于测试轮转（生产默认 5MB）。
 */
export function attachConsoleTee(filePath: string, maxBytes: number = DEFAULT_MAX_BYTES): void {
  if (activeConfig && activeConfig.filePath === filePath) return;
  activeConfig = { filePath, maxBytes };
  patchConsoleOnce();
}
