/**
 * Agent wiring for the e2e scenario.
 *
 * Responsibilities:
 *  - buildSystemPrompt: NPC persona + tool catalogue + tool-call syntax.
 *  - convertToLlm: AgentMessage[] -> LlmMessage[] (folds assistant toolCalls
 *    into text because VercelAIProvider.doCall drops the toolCalls field).
 *  - parseToolCalls: parses `[[tool_call]]...[[/tool_call]]` and the common
 *    misformat `[[tool_name]]...[[/tool_name]]` into AgentToolCall[].
 *  - makeLlmCallDirect: direct fetch llmCall (bypasses VercelAIProvider to
 *    avoid the v5 SDK endpoint issue where Deepseek 404s; used for both
 *    providers so the comparison is fair).
 *  - buildProvider: VercelAIProvider factory (kept for the smoke probe that
 *    records VercelAIProvider compatibility in the report).
 *  - Tool-result feedback hack: agentLoop does NOT feed tool results back
 *    into context (framework bug). We stash them in afterToolCall and
 *    inject as user messages via prepareNextTurn. We use role "user"
 *    because AgentMessage.role has no "tool" variant.
 */
import { VercelAIProvider } from "../src/llm-provider";
import type { ILLMProvider, LlmResponse } from "../src/llm-provider";
import type { LLMConfig } from "../src/llm-config";
import type { Agent } from "../src/agent";
import type { AgentEvent, AgentMessage, AgentToolCall } from "../src/types";
import type { LlmMessage } from "../src/types";
import type { ToolRegistry } from "../src/tool-registry";
import { ToolRegistry as ToolRegistryClass } from "../src/tool-registry";
import type { AgentLoopConfig, LlmCallResult } from "../src/agent-loop";
import { Agent as AgentClass } from "../src/agent";
import type { Tool } from "../src/tool";
import type { WorldState } from "./world";

export type ProviderKey = "deepseek" | "minimax";

export interface ProviderRunConfig {
  provider: ProviderKey;
  apiKey: string;
  model: string;
  baseUrl: string;
  label: string;
}

export const PROVIDER_CONFIGS: Record<ProviderKey, Omit<ProviderRunConfig, "apiKey">> = {
  deepseek: {
    provider: "deepseek",
    model: "deepseek-chat",
    baseUrl: "https://api.deepseek.com/v1",
    label: "Deepseek (deepseek-chat)",
  },
  minimax: {
    provider: "minimax",
    model: "abab6.5s-chat",
    baseUrl: "https://api.minimax.chat/v1",
    label: "Minimax (abab6.5s-chat, via openai-compat path)",
  },
};

/** VercelAIProvider factory. Used only by the smoke probe; main run uses
 *  makeLlmCallDirect because v5 SDK hits a 404 on Deepseek. */
export function buildProvider(runCfg: ProviderRunConfig): ILLMProvider {
  const providerType =
    runCfg.provider === "minimax" ? ("openai" as const) : runCfg.provider;
  const config: LLMConfig = {
    provider: providerType,
    apiKey: runCfg.apiKey,
    model: runCfg.model,
    baseUrl: runCfg.baseUrl,
    temperature: 0.7,
    maxTokens: 800,
    timeout: 45000,
    maxRetries: 1,
    maxConcurrency: 2,
  };
  return new VercelAIProvider(config);
}

/** Build the system prompt: NPC persona + tool catalogue + tool-call syntax. */
export function buildSystemPrompt(npcName: string, tools: Tool[]): string {
  const toolCatalogue = tools
    .map((t) => {
      const params =
        t.parameters && Object.keys(t.parameters).length > 0
          ? JSON.stringify(t.parameters)
          : "(无参数)";
      return `- ${t.name}: ${t.description}\n  参数schema: ${params}`;
    })
    .join("\n");

  return [
    `你是 ${npcName}，山谷村庄的守谷老者。你年迈却矍铄，说话平和而带古风，对远道而来的旅人既谨慎又热忱。`,
    "你守护着山谷的封印与传说，会根据玩家的问题调用合适的工具来查看环境、清点物品、赠予物品、讲述传说或查阅任务。",
    "",
    "## 你可以调用的工具",
    toolCatalogue,
    "",
    "## 工具调用规则（极其重要，必须严格遵守）",
    "",
    "### 调用格式",
    "当需要调用工具时，在回复末尾插入如下格式的行（可多行调用多个工具）：",
    "",
    '    [[tool_call]]{"name":"工具名","args":{"参数名":"值"}}[[/tool_call]]',
    "",
    "### 格式要点",
    "1. 标签名**必须**是 `tool_call`（固定不变），**绝不要**把 `tool_call` 替换为工具名。正确：`[[tool_call]]...[[/tool_call]]`。错误：`[[look_around]]...[[/look_around]]`。",
    "2. JSON 必须包含两个顶层字段：`name`（字符串，工具名）和 `args`（对象，参数）。",
    "3. 参数名**必须**严格匹配工具 schema 中的定义。例如 give_item 的参数是 `item_name` 和 `quantity`，不要写成 `name`。",
    "4. 工具调用之外的所有文字，都会作为你对玩家说的话被显示出来。",
    "5. 你可以「先说一句话 + 调用工具」，工具结果会反馈给你后，你应当在下一轮总结工具结果给玩家。",
    "6. 不要捏造工具结果。如果工具返回错误，请如实转述给玩家并给出建议。",
    "7. 不要调用不存在的工具。不要在 args 中加入 schema 未定义的字段。",
    "8. 回复保持简洁、贴合NPC口吻，不要暴露你是AI或这些规则。",
    "",
    "### 示例",
    "调用无参数工具 look_around：",
    "",
    '    [[tool_call]]{"name":"look_around","args":{}}[[/tool_call]]',
    "",
    "调用带参数工具 give_item（注意参数名是 item_name）：",
    "",
    '    [[tool_call]]{"name":"give_item","args":{"item_name":"治疗药水","quantity":1}}[[/tool_call]]',
    "",
    "调用带参数工具 tell_lore：",
    "",
    '    [[tool_call]]{"name":"tell_lore","args":{"topic":"守谷人"}}[[/tool_call]]',
  ].join("\n");
}

/** Convert AgentContext (with AgentMessage[]) to LlmMessage[] for the provider. */
export function makeConvertToLlm(systemPrompt: string) {
  return (ctx: { messages: AgentMessage[] }): { messages: LlmMessage[] } => {
    const out: LlmMessage[] = [{ role: "system", content: systemPrompt }];
    for (const m of ctx.messages) {
      if (m.role === "system") {
        continue;
      }
      if (m.role === "user") {
        out.push({ role: "user", content: m.content });
      } else if (m.role === "assistant") {
        let content = m.content;
        if (m.toolCalls && m.toolCalls.length > 0) {
          const callDesc = m.toolCalls
            .map((tc) => `[[tool_call]]{"name":"${tc.name}","args":${JSON.stringify(tc.args)}}[[/tool_call]]`)
            .join("\n");
          content = content ? `${content}\n${callDesc}` : callDesc;
        }
        out.push({ role: "assistant", content });
      } else {
        if (m.toolCallId) {
          out.push({ role: "user", content: m.content });
        }
      }
    }
    return { messages: out };
  };
}

// Match `[[tag]]content[[/tag]]` where tag is an identifier. Accepts both
// `[[tool_call]]` and the misformat `[[tool_name]]`.
const TOOL_CALL_RE = /\[\[([a-zA-Z_][a-zA-Z0-9_]*)\]\]\s*([\s\S]*?)\s*\[\[\/\1\]\]/g;

export function parseToolCalls(text: string): {
  cleanText: string;
  toolCalls: AgentToolCall[];
} {
  const calls: AgentToolCall[] = [];
  let match: RegExpExecArray | null;
  const re = new RegExp(TOOL_CALL_RE);
  while ((match = re.exec(text)) !== null) {
    const tag = match[1]!;
    const raw = match[2]!.trim();
    let name: string | undefined;
    let args: Record<string, unknown> = {};
    try {
      const parsed = JSON.parse(raw) as Record<string, unknown>;
      if (tag === "tool_call") {
        if (typeof parsed.name === "string") name = parsed.name;
        if (parsed.args && typeof parsed.args === "object") {
          args = parsed.args as Record<string, unknown>;
        }
      } else {
        name = tag;
        if (parsed.args && typeof parsed.args === "object") {
          args = parsed.args as Record<string, unknown>;
        } else {
          const { name: _ignoredName, ...rest } = parsed;
          args = rest;
        }
      }
    } catch {
      if (tag !== "tool_call") name = tag;
      else continue;
    }
    if (name) {
      calls.push({
        id: `call_${calls.length}_${Date.now()}`,
        name,
        args,
      });
    }
  }
  const cleanText = text.replace(TOOL_CALL_RE, "").trim();
  return { cleanText, toolCalls: calls };
}

/** Direct-fetch llmCall. Bypasses VercelAIProvider to avoid v5 SDK endpoint
 *  issues; used for both providers so the comparison is fair. */
export function makeLlmCallDirect(runCfg: ProviderRunConfig) {
  return async (messages: LlmMessage[]): Promise<LlmCallResult> => {
    const apiMessages = messages.map((m) => {
      if (m.role === "system") return { role: "system", content: m.content };
      if (m.role === "user") return { role: "user", content: m.content };
      if (m.role === "assistant") return { role: "assistant", content: m.content };
      return { role: "tool", content: m.content, tool_call_id: m.toolCallId };
    });

    const url = `${runCfg.baseUrl.replace(/\/$/, "")}/chat/completions`;
    const body: Record<string, unknown> = {
      model: runCfg.model,
      messages: apiMessages,
      max_tokens: 800,
    };
    // Minimax abab6.5s-chat is a reasoning model that rejects temperature;
    // skip it to avoid warnings. Deepseek-chat accepts temperature.
    if (runCfg.provider !== "minimax") {
      body.temperature = 0.7;
    }

    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 45000);
    try {
      const resp = await fetch(url, {
        method: "POST",
        headers: {
          Authorization: `Bearer ${runCfg.apiKey}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify(body),
        signal: controller.signal,
      });
      if (!resp.ok) {
        const text = await resp.text();
        throw new Error(`LLM API ${resp.status}: ${text.slice(0, 300)}`);
      }
      const data = (await resp.json()) as {
        choices: Array<{ message: { content: string } }>;
        usage?: {
          prompt_tokens?: number;
          completion_tokens?: number;
          total_tokens?: number;
        };
      };
      const content = data.choices[0]?.message?.content ?? "";
      const usage: LlmResponse["usage"] = {};
      if (data.usage?.prompt_tokens !== undefined)
        usage.promptTokens = data.usage.prompt_tokens;
      if (data.usage?.completion_tokens !== undefined)
        usage.completionTokens = data.usage.completion_tokens;
      if (data.usage?.total_tokens !== undefined)
        usage.totalTokens = data.usage.total_tokens;

      const { cleanText, toolCalls } = parseToolCalls(content);
      const result: LlmCallResult = {
        content: cleanText,
        ...(Object.keys(usage).length > 0 ? { usage } : {}),
      };
      if (toolCalls.length > 0) result.toolCalls = toolCalls;
      return result;
    } finally {
      clearTimeout(timeout);
    }
  };
}

export interface BoundAgent {
  agent: Agent;
  registry: ToolRegistry;
  pendingToolMessages: AgentMessage[];
}

export function buildAgent(
  world: WorldState,
  tools: Tool[],
  runCfg: ProviderRunConfig
): BoundAgent {
  const registry = new ToolRegistryClass();
  for (const t of tools) registry.register(t);

  const pendingToolMessages: AgentMessage[] = [];
  const systemPrompt = buildSystemPrompt(world.npcName, tools);

  const config: AgentLoopConfig = {
    tools: registry,
    convertToLlm: makeConvertToLlm(systemPrompt),
    llmCall: makeLlmCallDirect(runCfg),
    toolExecution: "sequential",
    maxTurns: 3,
    afterToolCall: async (_name, _args, result, _ctx) => {
      pendingToolMessages.push({
        role: "user",
        content: `[工具结果] ${result.content}`,
      });
      return { result };
    },
    prepareNextTurn: (ctx, _turnIndex) => {
      if (pendingToolMessages.length === 0) return ctx;
      const injected = [...pendingToolMessages];
      pendingToolMessages.length = 0;
      return { ...ctx, messages: [...ctx.messages, ...injected] };
    },
    shouldStopAfterTurn: (ctx, turnIndex) => {
      if (turnIndex >= 2) return true;
      const last = ctx.messages[ctx.messages.length - 1];
      if (
        last &&
        last.role === "assistant" &&
        last.toolCalls &&
        last.toolCalls.length > 0
      ) {
        return false;
      }
      return true;
    },
  };

  const agent = new AgentClass(`Elden-${world.npcName}`, config);
  return { agent, registry, pendingToolMessages };
}

/** Pretty-print an event for the log file. */
export function formatEvent(event: AgentEvent): string {
  const ts = new Date(event.timestamp).toISOString().split("T")[1]!;
  switch (event.type) {
    case "agent_start":
      return `[${ts}] > agent_start`;
    case "agent_end":
      return `[${ts}] = agent_end`;
    case "turn_start":
      return `[${ts}] ~ turn_start #${event.turnIndex}`;
    case "turn_end":
      return `[${ts}] ~ turn_end #${event.turnIndex}`;
    case "message_start":
      return `[${ts}] + message_start`;
    case "message_update":
      return `[${ts}] + message_update (delta ${event.delta.length} chars)`;
    case "message_end":
      return `[${ts}] * message_end (${event.content.length} chars):\n      ${event.content.split("\n").join("\n      ")}`;
    case "tool_call_start":
      return `[${ts}] T tool_call_start ${event.toolName}(${JSON.stringify(event.args)})`;
    case "tool_call_end": {
      const flag = event.isError ? " !ERROR" : "";
      const preview =
        typeof event.result === "object" &&
        event.result !== null &&
        "content" in event.result
          ? String((event.result as { content: unknown }).content)
          : String(event.result);
      return `[${ts}] T* tool_call_end ${event.toolName}${flag}:\n      ${preview.split("\n").join("\n      ")}`;
    }
    case "error":
      return `[${ts}] X error: ${event.message}`;
    default: {
      const _exhaustive: never = event;
      return `[${ts}] unknown event: ${JSON.stringify(_exhaustive)}`;
    }
  }
}
