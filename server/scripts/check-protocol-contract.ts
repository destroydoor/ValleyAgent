#!/usr/bin/env bun
/**
 * L1 协议契约静态交叉检查脚本
 *
 * 静态提取 C# 发送端（ValleyAgent / ValleyAgent.Abstractions）和 TS 路由端
 * （protocol-adapter.ts routeMessage）的消息 type 字符串，对照
 * protocol/messages.json 单一事实源，报告：
 *   - DEAD_PIPELINES：C# 发送但 TS 不路由（exit 1，"第一天就会红，这是好事"）
 *   - ORPHAN_ROUTES：TS 路由但 C# 从不发（警告，不失败）
 *   - SCHEMA_DRIFT：messages.json 与代码实际不一致（exit 1）
 *
 * 运行：bun run scripts/check-protocol-contract.ts
 *
 * 退出码：
 *   0 - 无 DEAD_PIPELINES 违规且无 SCHEMA_DRIFT（ORPHAN_ROUTES 警告不影响）
 *   1 - 存在 DEAD_PIPELINES 违规 或 SCHEMA_DRIFT
 *   2 - 脚本自身错误（文件不存在、解析失败、JSON 非法等）
 */

import { readFileSync, existsSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";

// ============================================================================
// 常量
// ============================================================================

// 2026-09-12 D7 门禁修缮：默认路径改为脚本位置相对（合并仓布局开箱即用）——
// 本脚本位于 <repo>/server/scripts/，TS 包在 <repo>/server/packages，C# 源在 <repo>/src。
// 环境变量覆盖保留：VALLEYAI_ROOT=server 根（含 packages/ 与 protocol/ 的目录，
// 兼容旧独立仓语义）、VALLEYTALK_SRC_ROOT=C# src 根（外部 C# 检出）。
const SERVER_ROOT = resolve(process.env["VALLEYAI_ROOT"] ?? resolve(import.meta.dir, ".."));
const REPO_ROOT = resolve(SERVER_ROOT, "..");
const CSHARP_ROOT = resolve(process.env["VALLEYTALK_SRC_ROOT"] ?? join(REPO_ROOT, "src"));

/** C# 发送端源码扫描根目录（递归） */
const CSHARP_SCAN_DIRS: readonly string[] = [
  join(CSHARP_ROOT, "ValleyAgent"),
  join(CSHARP_ROOT, "ValleyAgent.Abstractions"),
];

/** ProtocolV2.cs 常量与消息类定义路径 */
const PROTOCOL_V2_CS = join(CSHARP_ROOT, "ValleyAgent", "Protocol", "ProtocolV2.cs");

/** TS 路由端文件 */
const PROTOCOL_ADAPTER_TS = join(SERVER_ROOT, "packages", "stardew", "src", "protocol-adapter.ts");

/** 协议单一事实源 */
const MESSAGES_JSON = join(SERVER_ROOT, "protocol", "messages.json");

// ============================================================================
// 类型定义
// ============================================================================

type SendSource =
  | "literal_first_arg"
  | "constant_first_arg"
  | "anonymous_type_literal"
  | "anonymous_type_constant"
  | "message_class_default";

interface SendEvidence {
  readonly type: string;
  readonly file: string;
  readonly line: number;
  readonly snippet: string;
  readonly source: SendSource;
}

interface RouteEvidence {
  readonly type: string;
  readonly file: string;
  readonly line: number;
  readonly snippet: string;
}

interface MessageField {
  readonly name: string;
  readonly type: string;
  readonly required: boolean;
  readonly value?: string;
  readonly description?: string;
  readonly sent_by?: string;
}

type MessageStatus = "active" | "dead_pipeline" | "planned" | "orphan_route";

interface MessageEntry {
  readonly type: string;
  readonly direction: string;
  readonly transport: string;
  readonly status: MessageStatus;
  readonly routed_by_ts: boolean;
  readonly sent_by_csharp: boolean;
  readonly description: string;
  readonly fields: readonly MessageField[];
}

interface MessagesSchema {
  readonly version: string;
  readonly messages: readonly MessageEntry[];
}

interface Violation {
  readonly type: string;
  readonly evidence: string;
}

interface SchemaDrift {
  readonly kind:
    | "active_not_in_intersection"
    | "dead_pipeline_not_in_diff"
    | "orphan_route_not_in_route_diff"
    | "unregistered_in_send"
    | "unregistered_in_route"
    | "status_field_mismatch";
  readonly type: string;
  readonly expected: string;
  readonly actual: string;
}

interface CheckReport {
  readonly sendEvidence: readonly SendEvidence[];
  readonly routeEvidence: readonly RouteEvidence[];
  readonly sendTypes: ReadonlySet<string>;
  readonly routeTypes: ReadonlySet<string>;
  readonly deadPipelines: readonly Violation[];
  readonly orphanRoutes: readonly Violation[];
  readonly schemaDrifts: readonly SchemaDrift[];
}

// ============================================================================
// 工具函数
// ============================================================================

/** 计算字符串中指定 index 所在的行号（从 1 开始）。 */
function lineOf(content: string, index: number): number {
  let line = 1;
  const end = Math.min(index, content.length);
  for (let i = 0; i < end; i++) {
    if (content.charAt(i) === "\n") line++;
  }
  return line;
}

/** 提取匹配位置所在的源码行（去掉前后空白）。 */
function snippetAt(content: string, index: number): string {
  let start = index;
  while (start > 0 && content.charAt(start - 1) !== "\n") start--;
  let end = index;
  while (end < content.length && content.charAt(end) !== "\n") end++;
  return content.slice(start, end).trim();
}

/** 短化文件路径用于报告输出。 */
function shortPath(absolutePath: string): string {
  return absolutePath.replace(/\\/g, "/");
}

/** 递归列出目录下所有 .cs 文件。 */
function listCsFiles(dirs: readonly string[]): readonly string[] {
  const out: string[] = [];
  for (const dir of dirs) {
    if (!existsSync(dir)) {
      throw new Error(`C# 扫描目录不存在: ${dir}`);
    }
    const stack: string[] = [dir];
    while (stack.length > 0) {
      const current = stack.pop() as string;
      let entries: readonly string[];
      try {
        entries = readdirSync(current);
      } catch (err) {
        throw new Error(`读取目录失败 ${current}: ${(err as Error).message}`);
      }
      for (const name of entries) {
        const full = join(current, name);
        let st: { isDirectory(): boolean; isFile(): boolean };
        try {
          st = statSync(full);
        } catch (err) {
          throw new Error(`stat 失败 ${full}: ${(err as Error).message}`);
        }
        if (st.isDirectory()) {
          stack.push(full);
        } else if (st.isFile() && name.endsWith(".cs")) {
          out.push(full);
        }
      }
    }
  }
  return out;
}

/** 读取文件内容，剥离 BOM。 */
function readFileText(path: string): string {
  if (!existsSync(path)) {
    throw new Error(`文件不存在: ${path}`);
  }
  let content: string;
  try {
    content = readFileSync(path, "utf-8");
  } catch (err) {
    throw new Error(`读取文件失败 ${path}: ${(err as Error).message}`);
  }
  if (content.charCodeAt(0) === 0xfeff) {
    content = content.slice(1);
  }
  return content;
}

// ============================================================================
// ProtocolV2.cs 解析
// ============================================================================

interface ProtocolV2Info {
  /** 常量名 → 字符串值，如 MessageTypeStateSync → "state_sync" */
  readonly constants: ReadonlyMap<string, string>;
  /** 消息类名 → 默认 Type 字符串值，如 ActionResultMessage → "action_result" */
  readonly messageDefaultTypes: ReadonlyMap<string, string>;
}

/** 解析 ProtocolV2.cs，提取常量定义和消息类默认 Type。 */
function parseProtocolV2(filePath: string): ProtocolV2Info {
  const content = readFileText(filePath);

  const constants = new Map<string, string>();
  // 匹配: public const string MessageTypeXxx = "value";
  const constRegex = /public\s+const\s+string\s+(\w+)\s*=\s*"([^"]+)"/g;
  let m: RegExpExecArray | null;
  while ((m = constRegex.exec(content)) !== null) {
    const name = m[1];
    const value = m[2];
    if (name !== undefined && value !== undefined) {
      constants.set(name, value);
    }
  }
  if (constants.size === 0) {
    throw new Error(`ProtocolV2.cs 未匹配到任何 const string 常量: ${filePath}`);
  }

  const messageDefaultTypes = new Map<string, string>();
  // 匹配: public class XxxMessage { ... [JsonPropertyName("type")] public string Type { get; set; } = <expr>;
  // 使用 tempered greedy token 限制不跨类边界。
  const classRegex =
    /public\s+class\s+(\w+Message)\s*\{(?:(?!public\s+class)[\s\S])*?\[JsonPropertyName\("type"\)\]\s*public\s+string\s+Type\s*\{\s*get;\s*set;\s*\}\s*=\s*([^;]+);/g;
  while ((m = classRegex.exec(content)) !== null) {
    const className = m[1];
    const defaultExpr = m[2];
    if (className === undefined || defaultExpr === undefined) continue;
    const trimmed = defaultExpr.trim();
    let resolved: string | undefined;
    // 情况 1: 字面量 "value"
    const litMatch = /^"([^"]+)"$/.exec(trimmed);
    if (litMatch && litMatch[1] !== undefined) {
      resolved = litMatch[1];
    } else {
      // 情况 2: 常量引用 MessageTypeXxx（可能带 ProtocolV2. 前缀）
      const constName = trimmed.replace(/^ProtocolV2\./, "");
      resolved = constants.get(constName);
      if (resolved === undefined) {
        // 表达式无法解析，跳过（不报错，可能是个未支持的复杂表达式）
        continue;
      }
    }
    messageDefaultTypes.set(className, resolved);
  }

  return { constants, messageDefaultTypes };
}

// ============================================================================
// C# 发送 type 提取
// ============================================================================

/** 解析常量引用为字符串值，失败返回 undefined。 */
function resolveConstantRef(ref: string, constants: ReadonlyMap<string, string>): string | undefined {
  const name = ref.replace(/^ProtocolV2\./, "").trim();
  return constants.get(name);
}

/** 扫描所有 .cs 文件，提取 C# 发送的所有消息 type 字符串。 */
function extractSentTypes(
  csFiles: readonly string[],
  proto: ProtocolV2Info,
): readonly SendEvidence[] {
  const out: SendEvidence[] = [];

  for (const file of csFiles) {
    const content = readFileText(file);

    // 模式 A/B: SendRequestAsync / SendTestMessageAsync 第一参数
    // 字面量: SendXxxAsync("type", ...)
    // 常量: SendXxxAsync(ProtocolV2.MessageTypeXxx, ...)
    const firstArgRegex = /Send(?:Request|TestMessage)Async\s*\(\s*(?:"([^"]+)"|ProtocolV2\.(\w+))/g;
    let m: RegExpExecArray | null;
    while ((m = firstArgRegex.exec(content)) !== null) {
      const literal = m[1];
      const constRef = m[2];
      const idx = m.index;
      if (idx === undefined) continue;
      if (literal !== undefined) {
        out.push({
          type: literal,
          file,
          line: lineOf(content, idx),
          snippet: snippetAt(content, idx),
          source: "literal_first_arg",
        });
      } else if (constRef !== undefined) {
        const resolved = resolveConstantRef(constRef, proto.constants);
        if (resolved === undefined) {
          // 未知常量引用 - 报告但不崩溃
          out.push({
            type: `<<unresolved:ProtocolV2.${constRef}>>`,
            file,
            line: lineOf(content, idx),
            snippet: snippetAt(content, idx),
            source: "constant_first_arg",
          });
        } else {
          out.push({
            type: resolved,
            file,
            line: lineOf(content, idx),
            snippet: snippetAt(content, idx),
            source: "constant_first_arg",
          });
        }
      }
    }

    // 模式 C/D: 匿名对象 type 字段（在 JsonSerializer.Serialize(new { type = "xxx" }) 或类似上下文）
    // 排除 XML 注释 <list type="..."> 模式
    // 后行断言排除 `<word type=` 模式（XML doc 注释）
    const anonTypeRegex = /(?<!<\w+\s+)type\s*=\s*(?:"([^"]+)"|ProtocolV2\.(\w+))/g;
    while ((m = anonTypeRegex.exec(content)) !== null) {
      const literal = m[1];
      const constRef = m[2];
      const idx = m.index;
      if (idx === undefined) continue;
      if (literal !== undefined) {
        out.push({
          type: literal,
          file,
          line: lineOf(content, idx),
          snippet: snippetAt(content, idx),
          source: "anonymous_type_literal",
        });
      } else if (constRef !== undefined) {
        const resolved = resolveConstantRef(constRef, proto.constants);
        if (resolved !== undefined) {
          out.push({
            type: resolved,
            file,
            line: lineOf(content, idx),
            snippet: snippetAt(content, idx),
            source: "anonymous_type_constant",
          });
        }
      }
    }

    // 模式 E: new ProtocolV2.XxxMessage { ... } 实例化 → 用消息类默认 Type
    const newMsgRegex = /new\s+ProtocolV2\.(\w+Message)\b/g;
    while ((m = newMsgRegex.exec(content)) !== null) {
      const className = m[1];
      const idx = m.index;
      if (idx === undefined || className === undefined) continue;
      const defaultType = proto.messageDefaultTypes.get(className);
      if (defaultType === undefined) {
        // 消息类未在 ProtocolV2.cs 中识别出默认 Type - 跳过（可能是非消息类的实例化）
        continue;
      }
      out.push({
        type: defaultType,
        file,
        line: lineOf(content, idx),
        snippet: snippetAt(content, idx),
        source: "message_class_default",
      });
    }
  }

  return out;
}

// ============================================================================
// TS 路由 type 提取
// ============================================================================

/** 扫描 protocol-adapter.ts，提取 routeMessage 函数体内的所有 case 字符串。 */
function extractRoutedTypes(adapterPath: string): readonly RouteEvidence[] {
  const content = readFileText(adapterPath);

  // 定位 routeMessage 函数体（兼容 async/Promise<OutgoingMessage> 与 OutgoingMessage 两种签名）
  const fnRegex = /\brouteMessage\s*\(\s*msg\s*:\s*unknown\s*\)\s*:\s*\S+\s*\{/;
  const fnMatch = fnRegex.exec(content);
  if (fnMatch === null) {
    throw new Error(
      `protocol-adapter.ts 中未找到 routeMessage(msg: unknown): OutgoingMessage 函数签名: ${adapterPath}`,
    );
  }
  const fnStart = fnMatch.index + fnMatch[0].length;

  // 从函数体开括号开始扫描匹配的闭括号（考虑嵌套）
  let depth = 1;
  let i = fnStart;
  while (i < content.length && depth > 0) {
    const ch = content.charAt(i);
    if (ch === "{") depth++;
    else if (ch === "}") depth--;
    i++;
    if (depth === 0) break;
  }
  if (depth !== 0) {
    throw new Error(`routeMessage 函数体大括号不匹配: ${adapterPath}`);
  }
  const fnBody = content.slice(fnStart, i - 1);
  const fnBodyOffset = fnStart;

  const out: RouteEvidence[] = [];
  const caseRegex = /case\s+"([^"]+)"/g;
  let m: RegExpExecArray | null;
  while ((m = caseRegex.exec(fnBody)) !== null) {
    const type = m[1];
    const idx = m.index;
    if (idx === undefined || type === undefined) continue;
    out.push({
      type,
      file: adapterPath,
      line: lineOf(content, fnBodyOffset + idx),
      snippet: snippetAt(content, fnBodyOffset + idx),
    });
  }

  if (out.length === 0) {
    throw new Error(`routeMessage 函数体内未匹配到任何 case "type" 语句: ${adapterPath}`);
  }

  return out;
}

// ============================================================================
// messages.json 加载与验证
// ============================================================================

function isMessageStatus(x: unknown): x is MessageStatus {
  return x === "active" || x === "dead_pipeline" || x === "planned" || x === "orphan_route";
}

function assertValidSchema(raw: unknown, source: string): MessagesSchema {
  if (typeof raw !== "object" || raw === null) {
    throw new Error(`messages.json 顶层不是对象: ${source}`);
  }
  const obj = raw as Record<string, unknown>;
  const version = obj["version"];
  if (typeof version !== "string") {
    throw new Error(`messages.json 缺少 string 类型的 version 字段: ${source}`);
  }
  const messages = obj["messages"];
  if (!Array.isArray(messages)) {
    throw new Error(`messages.json 缺少数组类型的 messages 字段: ${source}`);
  }
  const entries: MessageEntry[] = [];
  for (let i = 0; i < messages.length; i++) {
    const entry = messages[i] as unknown;
    if (typeof entry !== "object" || entry === null) {
      throw new Error(`messages.json messages[${i}] 不是对象: ${source}`);
    }
    const e = entry as Record<string, unknown>;
    const type = e["type"];
    const direction = e["direction"];
    const transport = e["transport"];
    const status = e["status"];
    const routed = e["routed_by_ts"];
    const sent = e["sent_by_csharp"];
    const description = e["description"];
    const fields = e["fields"];
    if (typeof type !== "string") throw new Error(`messages.json messages[${i}].type 不是 string: ${source}`);
    if (typeof direction !== "string") throw new Error(`messages.json messages[${i}].direction 不是 string: ${source}`);
    if (typeof transport !== "string") throw new Error(`messages.json messages[${i}].transport 不是 string: ${source}`);
    if (!isMessageStatus(status)) {
      throw new Error(`messages.json messages[${i}].status "${String(status)}" 不是合法枚举: ${source}`);
    }
    if (typeof routed !== "boolean") throw new Error(`messages.json messages[${i}].routed_by_ts 不是 boolean: ${source}`);
    if (typeof sent !== "boolean") throw new Error(`messages.json messages[${i}].sent_by_csharp 不是 boolean: ${source}`);
    if (typeof description !== "string") throw new Error(`messages.json messages[${i}].description 不是 string: ${source}`);
    if (!Array.isArray(fields)) throw new Error(`messages.json messages[${i}].fields 不是数组: ${source}`);
    entries.push({
      type,
      direction,
      transport,
      status,
      routed_by_ts: routed,
      sent_by_csharp: sent,
      description,
      fields: fields as readonly MessageField[],
    });
  }
  return { version, messages: entries };
}

function loadMessagesSchema(filePath: string): MessagesSchema {
  const content = readFileText(filePath);
  let raw: unknown;
  try {
    raw = JSON.parse(content);
  } catch (err) {
    throw new Error(`messages.json JSON 解析失败 ${filePath}: ${(err as Error).message}`);
  }
  return assertValidSchema(raw, filePath);
}

// ============================================================================
// 交叉检查
// ============================================================================

function crossCheck(
  sendEvidence: readonly SendEvidence[],
  routeEvidence: readonly RouteEvidence[],
  schema: MessagesSchema,
): CheckReport {
  const sendTypes = new Set<string>();
  const sendEvidenceMap = new Map<string, SendEvidence[]>();
  for (const ev of sendEvidence) {
    if (ev.type.startsWith("<<unresolved")) continue; // 跳过未解析的常量引用
    sendTypes.add(ev.type);
    const arr = sendEvidenceMap.get(ev.type) ?? [];
    arr.push(ev);
    sendEvidenceMap.set(ev.type, arr);
  }

  const routeTypes = new Set<string>();
  const routeEvidenceMap = new Map<string, RouteEvidence[]>();
  for (const ev of routeEvidence) {
    routeTypes.add(ev.type);
    const arr = routeEvidenceMap.get(ev.type) ?? [];
    arr.push(ev);
    routeEvidenceMap.set(ev.type, arr);
  }

  // DEAD_PIPELINES: S_send - S_route
  const deadPipelines: Violation[] = [];
  for (const type of sendTypes) {
    if (!routeTypes.has(type)) {
      const evs = sendEvidenceMap.get(type) ?? [];
      const evidence = evs
        .map((e) => `  - ${shortPath(e.file)}:${e.line} [${e.source}] ${e.snippet}`)
        .join("\n");
      deadPipelines.push({ type, evidence });
    }
  }

  // ORPHAN_ROUTES: S_route - S_send
  const orphanRoutes: Violation[] = [];
  for (const type of routeTypes) {
    if (!sendTypes.has(type)) {
      const evs = routeEvidenceMap.get(type) ?? [];
      const evidence = evs
        .map((e) => `  - ${shortPath(e.file)}:${e.line} ${e.snippet}`)
        .join("\n");
      orphanRoutes.push({ type, evidence });
    }
  }

  // SCHEMA_DRIFT
  const schemaDrifts: SchemaDrift[] = [];
  const schemaTypes = new Set<string>();
  for (const entry of schema.messages) {
    schemaTypes.add(entry.type);
    const inSend = sendTypes.has(entry.type);
    const inRoute = routeTypes.has(entry.type);

    // active 类型必须在 S_send ∩ S_route（两端都接线）
    // 例外：TS→C# 响应消息（如 pong/dialogue_response）由 TS 发送 C# 接收，
    // 不经过 routeMessage 路由也不由 C# 发送，其"两端接线"体现在 TS 发 + C# 收，
    // 用 direction=ts_to_csharp 且 sent_by_csharp=false 且 routed_by_ts=false 识别。
    if (entry.status === "active") {
      const isTsOnlyResponse =
        entry.direction === "ts_to_csharp" && !entry.sent_by_csharp && !entry.routed_by_ts;
      if (!isTsOnlyResponse && (!inSend || !inRoute)) {
        schemaDrifts.push({
          kind: "active_not_in_intersection",
          type: entry.type,
          expected: "S_send ∩ S_route (active 类型应两端都接线)",
          actual: `in_send=${inSend}, in_route=${inRoute}`,
        });
      }
    }

    // dead_pipeline 类型必须在 S_send - S_route（C# 发但 TS 不路由）
    if (entry.status === "dead_pipeline") {
      if (!inSend || inRoute) {
        schemaDrifts.push({
          kind: "dead_pipeline_not_in_diff",
          type: entry.type,
          expected: "S_send - S_route (dead_pipeline 应 C# 发但 TS 不路由)",
          actual: `in_send=${inSend}, in_route=${inRoute}`,
        });
      }
    }

    // orphan_route 类型必须在 S_route - S_send（TS 路由但 C# 不发）
    if (entry.status === "orphan_route") {
      if (inSend || !inRoute) {
        schemaDrifts.push({
          kind: "orphan_route_not_in_route_diff",
          type: entry.type,
          expected: "S_route - S_send (orphan_route 应 TS 路由但 C# 不发)",
          actual: `in_send=${inSend}, in_route=${inRoute}`,
        });
      }
    }

    // 字段一致性校验（仅对非 planned 类型，planned 两端都不实现）
    if (entry.status !== "planned") {
      if (entry.sent_by_csharp && !inSend) {
        schemaDrifts.push({
          kind: "status_field_mismatch",
          type: entry.type,
          expected: "sent_by_csharp=true 应在 S_send 中",
          actual: "in_send=false",
        });
      }
      if (!entry.sent_by_csharp && inSend) {
        schemaDrifts.push({
          kind: "status_field_mismatch",
          type: entry.type,
          expected: "sent_by_csharp=false 应不在 S_send 中",
          actual: "in_send=true",
        });
      }
      if (entry.routed_by_ts && !inRoute) {
        schemaDrifts.push({
          kind: "status_field_mismatch",
          type: entry.type,
          expected: "routed_by_ts=true 应在 S_route 中",
          actual: "in_route=false",
        });
      }
      if (!entry.routed_by_ts && inRoute) {
        schemaDrifts.push({
          kind: "status_field_mismatch",
          type: entry.type,
          expected: "routed_by_ts=false 应不在 S_route 中",
          actual: "in_route=true",
        });
      }
    }
  }

  // 反向校验：S_send 和 S_route 中的每个 type 必须在 messages.json 里注册
  for (const type of sendTypes) {
    if (!schemaTypes.has(type)) {
      schemaDrifts.push({
        kind: "unregistered_in_send",
        type,
        expected: "S_send 中的 type 应在 messages.json 注册",
        actual: "未注册",
      });
    }
  }
  for (const type of routeTypes) {
    if (!schemaTypes.has(type)) {
      schemaDrifts.push({
        kind: "unregistered_in_route",
        type,
        expected: "S_route 中的 type 应在 messages.json 注册",
        actual: "未注册",
      });
    }
  }

  return {
    sendEvidence,
    routeEvidence,
    sendTypes,
    routeTypes,
    deadPipelines,
    orphanRoutes,
    schemaDrifts,
  };
}

// ============================================================================
// 报告输出
// ============================================================================

function formatReport(report: CheckReport, schema: MessagesSchema): string {
  const lines: string[] = [];
  lines.push("=".repeat(78));
  lines.push("L1 协议契约检查报告");
  lines.push(`messages.json 版本: ${schema.version}`);
  lines.push(`扫描 C# 目录: ${CSHARP_SCAN_DIRS.map(shortPath).join(", ")}`);
  lines.push(`TS 路由文件: ${shortPath(PROTOCOL_ADAPTER_TS)}`);
  lines.push("=".repeat(78));

  lines.push("");
  lines.push("【统计】");
  lines.push(`  S_send (C# 发送的 type 集合): ${report.sendTypes.size} 个`);
  lines.push(`  S_route (TS 路由的 type 集合): ${report.routeTypes.size} 个`);
  lines.push(`  messages.json 注册的 type: ${schema.messages.length} 个`);
  lines.push(`  DEAD_PIPELINES 违规: ${report.deadPipelines.length} 个 (exit 1)`);
  lines.push(`  ORPHAN_ROUTES 警告: ${report.orphanRoutes.length} 个 (警告)`);
  lines.push(`  SCHEMA_DRIFT 违规: ${report.schemaDrifts.length} 个 (exit 1)`);

  lines.push("");
  lines.push("【S_send 完整清单 (C# 发送的所有 type)】");
  const sortedSend = [...report.sendTypes].sort();
  for (const type of sortedSend) {
    const evs = report.sendEvidence.filter((e) => e.type === type);
    const sources = [...new Set(evs.map((e) => e.source))].join(", ");
    lines.push(`  - ${type}  [${sources}]`);
    for (const ev of evs) {
      lines.push(`      ${shortPath(ev.file)}:${ev.line}`);
    }
  }

  lines.push("");
  lines.push("【S_route 完整清单 (TS routeMessage 处理的所有 case)】");
  const sortedRoute = [...report.routeTypes].sort();
  for (const type of sortedRoute) {
    const evs = report.routeEvidence.filter((e) => e.type === type);
    lines.push(`  - ${type}`);
    for (const ev of evs) {
      lines.push(`      ${shortPath(ev.file)}:${ev.line}  ${ev.snippet}`);
    }
  }

  lines.push("");
  lines.push("-".repeat(78));
  lines.push(`【DEAD_PIPELINES 违规】(${report.deadPipelines.length} 个) — C# 发送但 TS 不路由 = 死管道`);
  lines.push("-".repeat(78));
  if (report.deadPipelines.length === 0) {
    lines.push("  (无)");
  } else {
    for (const v of report.deadPipelines) {
      lines.push(`  X ${v.type}`);
      lines.push(v.evidence);
    }
  }

  lines.push("");
  lines.push("-".repeat(78));
  lines.push(`【ORPHAN_ROUTES 警告】(${report.orphanRoutes.length} 个) — TS 路由但 C# 从不发 (不失败)`);
  lines.push("-".repeat(78));
  if (report.orphanRoutes.length === 0) {
    lines.push("  (无)");
  } else {
    for (const v of report.orphanRoutes) {
      lines.push(`  ! ${v.type}`);
      lines.push(v.evidence);
    }
  }

  lines.push("");
  lines.push("-".repeat(78));
  lines.push(`【SCHEMA_DRIFT 违规】(${report.schemaDrifts.length} 个) — messages.json 与代码不一致`);
  lines.push("-".repeat(78));
  if (report.schemaDrifts.length === 0) {
    lines.push("  (无)");
  } else {
    for (const d of report.schemaDrifts) {
      lines.push(`  X [${d.kind}] ${d.type}`);
      lines.push(`      期望: ${d.expected}`);
      lines.push(`      实际: ${d.actual}`);
    }
  }

  lines.push("");
  lines.push("=".repeat(78));
  const hasFailures = report.deadPipelines.length > 0 || report.schemaDrifts.length > 0;
  if (hasFailures) {
    lines.push("结论: FAIL (exit 1) — 存在死管道或 schema drift");
    lines.push('说明: "第一天就会红，这是好事" - 红的清单就是待修复的死管道清单');
  } else {
    lines.push("结论: PASS (exit 0) — 无死管道，无 schema drift");
  }
  lines.push("=".repeat(78));

  return lines.join("\n");
}

// ============================================================================
// 主入口
// ============================================================================

function main(): number {
  // 1. 解析 ProtocolV2.cs
  const proto = parseProtocolV2(PROTOCOL_V2_CS);
  console.error(`[info] ProtocolV2.cs: 解析到 ${proto.constants.size} 个常量, ${proto.messageDefaultTypes.size} 个消息类默认 Type`);

  // 2. 扫描 C# 发送端
  const csFiles = listCsFiles(CSHARP_SCAN_DIRS);
  console.error(`[info] 扫描 ${csFiles.length} 个 .cs 文件`);
  const sendEvidence = extractSentTypes(csFiles, proto);
  console.error(`[info] 提取到 ${sendEvidence.length} 条 C# 发送证据`);

  // 3. 扫描 TS 路由端
  const routeEvidence = extractRoutedTypes(PROTOCOL_ADAPTER_TS);
  console.error(`[info] 提取到 ${routeEvidence.length} 条 TS 路由证据`);

  // 4. 加载 messages.json
  const schema = loadMessagesSchema(MESSAGES_JSON);
  console.error(`[info] messages.json: ${schema.messages.length} 个消息定义, 版本 ${schema.version}`);

  // 5. 交叉检查
  const report = crossCheck(sendEvidence, routeEvidence, schema);

  // 6. 输出报告
  const output = formatReport(report, schema);
  console.log(output);

  // 7. 退出码
  const hasFailures = report.deadPipelines.length > 0 || report.schemaDrifts.length > 0;
  return hasFailures ? 1 : 0;
}

try {
  const code = main();
  process.exit(code);
} catch (err) {
  console.error(`[fatal] ${(err as Error).message}`);
  console.error((err as Error).stack ?? "");
  process.exit(2);
}
