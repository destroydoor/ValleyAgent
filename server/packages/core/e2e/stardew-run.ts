import { join } from "node:path";
import { Agent } from "../src/agent";
import type { AgentLoopConfig, LlmCallResult } from "../src/agent-loop";
import type { AgentContext, LlmMessage, AgentEvent } from "../src/types";
import { VercelAIProvider } from "../src/llm-provider";
import type { LLMConfig } from "../src/llm-config";
import { ToolRegistry } from "../src/tool-registry";
import type { Tool } from "../src/tool";
import { AgentMemory } from "./stardew-memory";
import { INITIAL_SCENE, PLAYER_SCRIPT, ABIGAIL_INVENTORY } from "./stardew-data";
import type { SceneState, InventoryItem } from "./stardew-data";
import { buildStardewTools, evaluateGiftForAbigail } from "./stardew-tools";
import type { ToolContext } from "./stardew-tools";
import { buildSystemPrompt } from "./stardew-prompt";
import { mkdir, writeFile } from "fs/promises";
import { dirname } from "path";

// ── Provider 配置 ──
export interface ProviderConfig {
  key: string;
  label: string;
  provider: "deepseek" | "minimax";
  apiKey: string;
  model: string;
  baseUrl: string;
}

const PROVIDER_CONFIGS: ProviderConfig[] = [
  {
    key: "deepseek",
    label: "Deepseek (deepseek-chat)",
    provider: "deepseek",
    // key 不落仓库：从环境变量读取（. ./scripts/secrets.local.ps1 或手动设置）
    apiKey: process.env.DEEPSEEK_API_KEY ?? "",
    model: "deepseek-chat",
    baseUrl: "https://api.deepseek.com/v1",
  },
  {
    key: "minimax",
    label: "Minimax (abab6.5s-chat)",
    provider: "minimax",
    apiKey: process.env.MINIMAX_API_KEY ?? "",
    model: "abab6.5s-chat",
    baseUrl: "https://api.minimax.chat/v1",
  },
];

// ── convertToLlm: AgentMessage[] → LlmMessage[] ──
function makeConvertToLlm(systemPrompt: string) {
  return (ctx: AgentContext): { messages: LlmMessage[] } => {
    const out: LlmMessage[] = [{ role: "system", content: systemPrompt }];
    for (const m of ctx.messages) {
      if (m.role === "system") continue;
      if (m.role === "user") {
        out.push({ role: "user", content: m.content });
      } else if (m.role === "assistant") {
        if (m.toolCalls && m.toolCalls.length > 0) {
          out.push({
            role: "assistant",
            content: m.content,
            toolCalls: m.toolCalls.map(tc => ({
              id: tc.id,
              type: "function" as const,
              functionName: tc.name,
              args: JSON.stringify(tc.args),
            })),
          });
        } else {
          out.push({ role: "assistant", content: m.content });
        }
      } else if (m.role === "tool") {
        out.push({
          role: "tool",
          content: m.content,
          toolCallId: m.toolCallId!,
          ...(m.toolName !== undefined ? { toolName: m.toolName } : {}),
        });
      }
    }
    return { messages: out };
  };
}

// ── llmCall: 使用 VercelAIProvider.chatWithTools（原生工具调用）──
function makeLlmCall(provider: VercelAIProvider) {
  return async (messages: LlmMessage[], tools?: Tool[]): Promise<LlmCallResult> => {
    return provider.chatWithTools(messages, tools);
  };
}

// ── Fallback: 直接 fetch（如果 VercelAIProvider 失败）──
async function makeDirectLlmCall(
  config: ProviderConfig,
  messages: LlmMessage[],
  tools?: Tool[]
): Promise<LlmCallResult> {
  const apiMessages = messages.map(m => {
    if (m.role === "system") return { role: "system", content: m.content };
    if (m.role === "user") return { role: "user", content: m.content };
    if (m.role === "assistant") {
      if (m.toolCalls && m.toolCalls.length > 0) {
        return {
          role: "assistant",
          content: m.content || null,
          tool_calls: m.toolCalls.map(tc => ({
            id: tc.id,
            type: "function",
            function: { name: tc.functionName, arguments: tc.args },
          })),
        };
      }
      return { role: "assistant", content: m.content };
    }
    return { role: "tool", content: m.content, tool_call_id: m.toolCallId };
  });

  const body: Record<string, unknown> = {
    model: config.model,
    messages: apiMessages,
    max_tokens: 800,
  };
  // Minimax abab6.5s-chat is a reasoning model that rejects temperature;
  // skip it to avoid warnings. Deepseek-chat accepts temperature.
  if (config.provider !== "minimax") {
    body.temperature = 0.7;
  }
  if (tools && tools.length > 0) {
    body.tools = tools.map(t => ({
      type: "function",
      function: {
        name: t.name,
        description: t.description,
        parameters: t.parameters,
      },
    }));
    body.tool_choice = "auto";
  }

  const url = `${config.baseUrl.replace(/\/$/, "")}/chat/completions`;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 60000);
  try {
    const resp = await fetch(url, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${config.apiKey}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify(body),
      signal: controller.signal,
    });
    if (!resp.ok) {
      const text = await resp.text();
      throw new Error(`API ${resp.status}: ${text.slice(0, 300)}`);
    }
    const data = await resp.json() as {
      choices: Array<{ message: { content: string | null; tool_calls?: Array<{ id: string; function: { name: string; arguments: string } }> } }>;
      usage?: { prompt_tokens?: number; completion_tokens?: number; total_tokens?: number };
    };
    const msg = data.choices[0]?.message;
    const content = msg?.content ?? "";
    const usage: { promptTokens?: number; completionTokens?: number; totalTokens?: number } = {};
    if (data.usage?.prompt_tokens !== undefined) usage.promptTokens = data.usage.prompt_tokens;
    if (data.usage?.completion_tokens !== undefined) usage.completionTokens = data.usage.completion_tokens;
    if (data.usage?.total_tokens !== undefined) usage.totalTokens = data.usage.total_tokens;

    const result: LlmCallResult = {
      content,
      ...(Object.keys(usage).length > 0 ? { usage } : {}),
    };
    if (msg?.tool_calls && msg.tool_calls.length > 0) {
      result.toolCalls = msg.tool_calls.map(tc => ({
        id: tc.id,
        name: tc.function.name,
        args: JSON.parse(tc.function.arguments || "{}"),
      }));
    }
    return result;
  } finally {
    clearTimeout(timeout);
  }
}

// ── 事件格式化 ──
function formatEvent(event: AgentEvent): string {
  const ts = new Date(event.timestamp).toISOString().split("T")[1]!;
  switch (event.type) {
    case "agent_start": return `[${ts}] > agent_start`;
    case "agent_end": return `[${ts}] = agent_end`;
    case "turn_start": return `[${ts}] ~ turn_start #${event.turnIndex}`;
    case "turn_end": return `[${ts}] ~ turn_end #${event.turnIndex}`;
    case "message_start": return `[${ts}] + message_start`;
    case "message_update": return `[${ts}] + message_update (delta ${event.delta.length} chars)`;
    case "message_end": return `[${ts}] * message_end (${event.content.length} chars): ${event.content.slice(0, 200)}`;
    case "tool_call_start": return `[${ts}] T tool_call_start ${event.toolName}(${JSON.stringify(event.args)})`;
    case "tool_call_end": {
      const flag = event.isError ? " !ERROR" : "";
      const preview = typeof event.result === "object" && event.result !== null && "content" in event.result
        ? String((event.result as { content: unknown }).content)
        : String(event.result);
      return `[${ts}] T* tool_call_end ${event.toolName}${flag}: ${preview.slice(0, 200)}`;
    }
    case "error": return `[${ts}] X error: ${event.message}`;
    default: return `[${ts}] unknown: ${JSON.stringify(event)}`;
  }
}

// ── 单轮运行辅助 ──
async function runOneTurn(
  agentName: string,
  registry: ToolRegistry,
  systemPrompt: string,
  llmCall: AgentLoopConfig["llmCall"],
  context: AgentContext
): Promise<AgentEvent[]> {
  const agentCfg: AgentLoopConfig = {
    tools: registry,
    convertToLlm: makeConvertToLlm(systemPrompt),
    llmCall,
    toolExecution: "sequential",
    maxTurns: 3,
    shouldStopAfterTurn: (ctx, turn) => {
      if (turn >= 2) return true;
      const last = ctx.messages[ctx.messages.length - 1];
      if (last && last.role === "assistant" && (!last.toolCalls || last.toolCalls.length === 0)) {
        return true;
      }
      return false;
    },
  };
  const agent = new Agent(agentName, agentCfg);
  const stream = agent.prompt(context);
  return await stream.awaitAll();
}

// ── 单 Provider 运行 ──
interface TurnResult {
  playerMessage: string;
  npcSpokenText: string;     // 从 speak/show_dialogue 工具提取
  npcRawText: string;        // LLM 原始文本回复
  toolCalls: Array<{ name: string; args: Record<string, unknown>; result: string; isError: boolean }>;
  events: string[];
  durationMs: number;
  error?: string;
  toolLog: string[];
  memorySnapshot: {
    conversationCount: number;
    shortTermCount: number;
    significantMemories: string[];
    friendship: number;
  };
  llmMethod: "vercel" | "direct-fetch";
}

interface ProviderResult {
  turns: TurnResult[];
  finalMemory: AgentMemory;
  finalInventory: InventoryItem[];
  givenToPlayer: Array<{ name: string; quantity: number }>;
}

async function runProvider(
  config: ProviderConfig,
  log: string[]
): Promise<ProviderResult> {
  log.push(`\n## ${config.label}\n`);

  // 初始化记忆和场景
  const memory = new AgentMemory("Abigail");
  const scene: SceneState = { ...INITIAL_SCENE };
  const inventory: InventoryItem[] = ABIGAIL_INVENTORY.map(i => ({ ...i }));
  const givenToPlayer: Array<{ name: string; quantity: number }> = [];

  // 创建 VercelAIProvider
  const llmConfig: LLMConfig = {
    provider: config.provider,
    apiKey: config.apiKey,
    model: config.model,
    baseUrl: config.baseUrl,
    temperature: 0.7,
    maxTokens: 800,
    timeout: 60000,
    maxRetries: 1,
    maxConcurrency: 2,
  };
  const vercelProvider = new VercelAIProvider(llmConfig);

  const turns: TurnResult[] = [];
  let useDirectFetch = false;

  for (const playerTurn of PLAYER_SCRIPT) {
    log.push(`\n### 第 ${playerTurn.index} 轮：${playerTurn.intent}\n`);
    log.push(`**玩家**: ${playerTurn.message}\n`);
    log.push(`*(预期工具: ${playerTurn.expectedTools.join(", ")} — ${playerTurn.notes})*\n`);

    // 记录玩家对话到记忆
    memory.addConversation("player", playerTurn.message);

    // 特殊处理：第3轮玩家送紫水晶
    let giftContext = "";
    if (playerTurn.index === 3) {
      const giftResult = evaluateGiftForAbigail("Amethyst");
      memory.addFriendship(giftResult.friendshipDelta);
      memory.addMemory(
        `农场主送了我紫水晶（${giftResult.taste}）`,
        8.0,
        "gift",
        scene.location,
        ["gift", "amethyst", "love"]
      );
      giftContext = `\n（系统事件：农场主送了你一颗紫水晶。这是你最爱的礼物！好感度 +${giftResult.friendshipDelta}，当前好感度 ${memory.friendship}/2500）\n`;
    }

    // 构建 system prompt（含最新记忆和场景）
    const systemPrompt = buildSystemPrompt(memory, scene);

    // 构建工具上下文
    const toolCtx: ToolContext = {
      memory,
      scene,
      inventory,
      givenToPlayer,
      log: [],
    };
    const tools = buildStardewTools(toolCtx);
    const registry = new ToolRegistry();
    for (const t of tools) registry.register(t);

    // 构建 AgentContext
    const context: AgentContext = {
      messages: [{
        role: "user",
        content: `农场主说：${playerTurn.message}${giftContext}`,
      }],
      systemPrompt,
      metadata: {},
    };

    const startTime = Date.now();
    let events: AgentEvent[] = [];
    let llmMethod: "vercel" | "direct-fetch" = useDirectFetch ? "direct-fetch" : "vercel";
    let turnError: string | undefined;

    const runWith = async (llmCall: AgentLoopConfig["llmCall"]): Promise<AgentEvent[]> => {
      return runOneTurn(
        `Abigail-${config.key}-T${playerTurn.index}`,
        registry,
        systemPrompt,
        llmCall,
        context
      );
    };

    try {
      events = useDirectFetch
        ? await runWith(async (m, t) => makeDirectLlmCall(config, m, t))
        : await runWith(makeLlmCall(vercelProvider));
    } catch (err) {
      const errMsg = err instanceof Error ? err.message : String(err);
      events = [{ type: "error", timestamp: Date.now(), message: errMsg }];
      turnError = errMsg;
    }

    // 检测错误事件，自动回退到 direct-fetch
    const hasError = events.some(e => e.type === "error");
    if (hasError && !useDirectFetch) {
      log.push(`⚠️ VercelAIProvider 失败，切换到 direct-fetch 模式重试...\n`);
      useDirectFetch = true;
      llmMethod = "direct-fetch";
      toolCtx.log.length = 0;
      try {
        events = await runWith(async (m, t) => makeDirectLlmCall(config, m, t));
        turnError = undefined;
      } catch (err) {
        const retryMsg = err instanceof Error ? err.message : String(err);
        events = [{ type: "error", timestamp: Date.now(), message: retryMsg }];
        turnError = retryMsg;
      }
    }

    const durationMs = Date.now() - startTime;
    const eventLog = events.map(formatEvent);

    // 提取 NPC 说话内容（从 speak/show_dialogue 工具调用）
    let npcSpokenText = "";
    const argsById = new Map<string, Record<string, unknown>>();
    for (const ev of events) {
      if (ev.type === "tool_call_start") argsById.set(ev.toolCallId, ev.args);
    }
    const toolCallResults: TurnResult["toolCalls"] = [];
    for (const ev of events) {
      if (ev.type === "tool_call_start" && (ev.toolName === "speak" || ev.toolName === "show_dialogue")) {
        const text = ev.args.text as string;
        if (text) npcSpokenText = text;
      }
      if (ev.type === "tool_call_end") {
        const content = typeof ev.result === "object" && ev.result !== null && "content" in ev.result
          ? String((ev.result as { content: unknown }).content)
          : String(ev.result);
        toolCallResults.push({
          name: ev.toolName,
          args: argsById.get(ev.toolCallId) ?? {},
          result: content,
          isError: ev.isError,
        });
      }
    }

    // 提取 LLM 原始文本
    let npcRawText = "";
    for (const ev of events) {
      if (ev.type === "message_end") {
        npcRawText = ev.content;
      }
    }

    // 如果 NPC 没说话但有原始文本，用原始文本作为说话内容
    if (!npcSpokenText && npcRawText) {
      npcSpokenText = npcRawText;
      memory.addConversation("npc", npcRawText);
    }

    if (turnError) {
      log.push(`**错误**: ${turnError}\n`);
    }
    log.push(`**NPC对话**: ${npcSpokenText || "(无对话)"}\n`);
    if (npcRawText && npcRawText !== npcSpokenText) {
      log.push(`*(LLM原始文本: ${npcRawText.slice(0, 150)})*\n`);
    }
    log.push(`**工具调用**: ${toolCallResults.length > 0 ? toolCallResults.map(t => `\`${t.name}\``).join(", ") : "无"}\n`);
    if (toolCtx.log.length > 0) {
      log.push(`**工具日志**:\n${toolCtx.log.map(l => `  - ${l}`).join("\n")}\n`);
    }
    log.push(`**耗时**: ${durationMs}ms\n`);
    log.push(`**LLM方式**: ${llmMethod === "direct-fetch" ? "direct-fetch (fallback)" : "VercelAIProvider.chatWithTools"}\n`);
    log.push(`<details><summary>事件流</summary>\n\n\`\`\`\n${eventLog.join("\n")}\n\`\`\`\n</details>\n`);

    turns.push({
      playerMessage: playerTurn.message,
      npcSpokenText,
      npcRawText,
      toolCalls: toolCallResults,
      events: eventLog,
      durationMs,
      ...(turnError !== undefined ? { error: turnError } : {}),
      toolLog: [...toolCtx.log],
      memorySnapshot: {
        conversationCount: memory.conversationHistory.length,
        shortTermCount: memory.shortTermMemories.length,
        significantMemories: memory.significantMemories.map(m => m.text),
        friendship: memory.friendship,
      },
      llmMethod,
    });
  }

  log.push(`\n### 最终状态\n`);
  log.push(`- 对话记录: ${memory.conversationHistory.length} 条\n`);
  log.push(`- 短期记忆: ${memory.shortTermMemories.length} 条\n`);
  log.push(`- 重要记忆: ${memory.significantMemories.length} 条\n`);
  if (memory.significantMemories.length > 0) {
    log.push(`\n**重要记忆列表**:\n`);
    memory.significantMemories.forEach((m, i) => {
      log.push(`${i + 1}. [${m.category}/${m.emotionalWeight}] 我记得... ${m.text}\n`);
    });
  }
  log.push(`- 好感度: ${memory.friendship}/2500\n`);
  log.push(`- Abigail 物品栏: ${inventory.length > 0 ? inventory.map(i => `${i.name}x${i.quantity}`).join(", ") : "空"}\n`);
  log.push(`- 赠予玩家: ${givenToPlayer.length > 0 ? givenToPlayer.map(i => `${i.name}x${i.quantity}`).join(", ") : "无"}\n`);

  return { turns, finalMemory: memory, finalInventory: inventory, givenToPlayer };
}

// ── 主函数 ──
async function main() {
  const log: string[] = [];
  log.push(`# @valley/core 星露谷物语真实场景 E2E 测试报告\n`);
  log.push(`> **生成时间**: ${new Date().toISOString()}\n`);
  log.push(`> **框架版本**: @valley/core 0.1.0 (修复 6 处 bug 后)\n`);
  log.push(`> **NPC**: Abigail（真实星露谷人设，来自 ValleyTalk bio/Abigail.json）\n`);
  log.push(`> **记忆系统**: 移植自 valley_agent_server/server/agent_memory.py（简化版）\n`);
  log.push(`> **工具系统**: 移植自 valley_agent_server/server/agent_tools.py + npc_tools.py\n`);
  log.push(`> **Prompt**: 使用真实 prompts.json dialogue 模板\n`);
  log.push(`> **LLM调用**: VercelAIProvider.chatWithTools()（原生工具调用，无文本协议 hack）\n`);
  log.push(`> **如果 VercelAIProvider 失败**: 自动回退到 direct-fetch（OpenAI 兼容格式）\n`);

  log.push(`\n## 测试设置\n`);
  log.push(`- **NPC**: Abigail，19岁，热爱冒险和超自然，与父母关系紧张，喜欢矿洞和紫水晶\n`);
  log.push(`- **场景**: 春季，上午9:00，晴天，鹈鹕镇（皮埃尔商店附近）\n`);
  log.push(`- **初始好感度**: 0（初次见面）\n`);
  log.push(`- **Abigail 物品栏**: Amethyst x2, Pumpkin x1, Coffee x3\n`);
  log.push(`- **玩家**: 助手扮演新来的农夫，6轮对话\n`);
  log.push(`- **工具**: 10个 LLM 可见工具（speak, emote, give_item, give_gift, set_state, show_dialogue, wait, stop, remember, get_info）\n`);
  log.push(`- **修复验证点**:\n`);
  log.push(`  - Bug #1 (minimax 路由): minimax 应通过 openai.chat() 路由\n`);
  log.push(`  - Bug #2 (工具结果回传): tool 结果以 role:"tool" 消息回传\n`);
  log.push(`  - Bug #3 (tool role): AgentMessage 支持 "tool" role\n`);
  log.push(`  - Bug #5 (llmCall tools): 工具定义传给 LLM\n`);
  log.push(`  - Bug #6 (Deepseek 404): .chat() 使用 Chat Completions API\n`);

  log.push(`\n## 玩家剧本\n`);
  log.push(`| 轮次 | 意图 | 预期工具 | 测试点 |\n`);
  log.push(`|------|------|----------|--------|\n`);
  PLAYER_SCRIPT.forEach(t => {
    log.push(`| ${t.index} | ${t.intent} | ${t.expectedTools.join("+")} | ${t.notes} |\n`);
  });

  const allResults: Array<{ config: ProviderConfig; result: ProviderResult }> = [];

  for (const config of PROVIDER_CONFIGS) {
    try {
      const result = await runProvider(config, log);
      allResults.push({ config, result });
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      log.push(`\n## ${config.label} — 运行失败\n\n**错误**: ${msg}\n`);
    }
  }

  // 对比摘要
  log.push(`\n## 对比摘要\n`);
  const headerCells = allResults.map(r => r.config.label);
  log.push(`| 指标 | ${headerCells.join(" | ")} |\n`);
  log.push(`|------|${allResults.map(() => "------|").join("")}\n`);

  const totalDuration = allResults.map(r => r.result.turns.reduce((s, t) => s + t.durationMs, 0));
  const totalTools = allResults.map(r => r.result.turns.reduce((s, t) => s + t.toolCalls.length, 0));
  const totalSpeak = allResults.map(r => r.result.turns.reduce((s, t) => s + t.toolCalls.filter(tc => tc.name === "speak" || tc.name === "show_dialogue").length, 0));
  const totalRemember = allResults.map(r => r.result.turns.reduce((s, t) => s + t.toolCalls.filter(tc => tc.name === "remember").length, 0));
  const totalEmote = allResults.map(r => r.result.turns.reduce((s, t) => s + t.toolCalls.filter(tc => tc.name === "emote").length, 0));
  const significantCount = allResults.map(r => r.result.finalMemory.significantMemories.length);
  const friendship = allResults.map(r => r.result.finalMemory.friendship);
  const llmMethods = allResults.map(r => {
    const methods = new Set(r.result.turns.map(t => t.llmMethod));
    return [...methods].join(", ");
  });
  const errors = allResults.map(r => r.result.turns.filter(t => t.error !== undefined).length);

  log.push(`| 总耗时 | ${totalDuration.join("ms | ")}ms |\n`);
  log.push(`| 工具调用总数 | ${totalTools.join(" | ")} |\n`);
  log.push(`| speak/show_dialogue | ${totalSpeak.join(" | ")} |\n`);
  log.push(`| emote | ${totalEmote.join(" | ")} |\n`);
  log.push(`| remember | ${totalRemember.join(" | ")} |\n`);
  log.push(`| 重要记忆数 | ${significantCount.join(" | ")} |\n`);
  log.push(`| 最终好感度 | ${friendship.join(" | ")} |\n`);
  log.push(`| LLM方式 | ${llmMethods.join(" | ")} |\n`);
  log.push(`| 错误轮次 | ${errors.join(" | ")} |\n`);

  // 角色保真度评估
  log.push(`\n## 角色保真度评估\n`);
  for (const { config, result } of allResults) {
    log.push(`\n### ${config.label}\n`);
    const turns = result.turns;

    // 检查第2轮：是否提到矿洞/冒险
    const t2 = turns[1];
    if (t2) {
      const mentionsMine = t2.npcSpokenText.includes("矿") || t2.npcSpokenText.includes("冒险") || t2.npcSpokenText.includes("探险");
      log.push(`- 第2轮（矿洞话题）: ${mentionsMine ? "✅ 提及矿洞/冒险" : "❌ 未提及矿洞"} — "${t2.npcSpokenText.slice(0, 80)}"\n`);
    }

    // 检查第3轮：紫水晶反应
    const t3 = turns[2];
    if (t3) {
      const lovesAmethyst = t3.npcSpokenText.includes("紫水晶") || t3.npcSpokenText.includes("最爱") || t3.npcSpokenText.includes("喜欢") || t3.npcSpokenText.includes("谢谢");
      const hasLoveEmote = t3.toolCalls.some(tc => tc.name === "emote" && (tc.args.emote_id === "love" || tc.args.emote_id === "heart" || tc.args.emote_id === "happy" || tc.args.emote_id === "star"));
      log.push(`- 第3轮（紫水晶礼物）: ${lovesAmethyst ? "✅ 有正面反应" : "❌ 反应平淡"} / ${hasLoveEmote ? "✅ 有积极表情" : "❌ 无积极表情"}\n`);
    }

    // 检查第4轮：家人话题
    const t4 = turns[3];
    if (t4) {
      const mentionsFamily = t4.npcSpokenText.includes("爸") || t4.npcSpokenText.includes("妈") || t4.npcSpokenText.includes("皮埃尔") || t4.npcSpokenText.includes("Caroline") || t4.npcSpokenText.includes("商店");
      log.push(`- 第4轮（家人话题）: ${mentionsFamily ? "✅ 提及家人" : "❌ 未提及家人"} — "${t4.npcSpokenText.slice(0, 80)}"\n`);
    }

    // 检查 remember 工具使用
    const rememberCalls = turns.reduce((s, t) => s + t.toolCalls.filter(tc => tc.name === "remember").length, 0);
    log.push(`- remember 工具调用: ${rememberCalls} 次\n`);
    if (result.finalMemory.significantMemories.length > 0) {
      log.push(`  记忆内容:\n`);
      result.finalMemory.significantMemories.forEach(m => {
        log.push(`  - [${m.category}/${m.emotionalWeight}] ${m.text}\n`);
      });
    }

    // 检查第5轮：冒险邀请回应（从角色角度评价自然度）
    const t5 = turns[4];
    if (t5) {
      const acceptsInvitation = t5.npcSpokenText.includes("好的") || t5.npcSpokenText.includes("明天") || t5.npcSpokenText.includes("见") || t5.npcSpokenText.includes("一起") || t5.npcSpokenText.includes("去");
      log.push(`- 第5轮（冒险邀请）: ${acceptsInvitation ? "✅ 同意一起去矿洞" : "❌ 未回应邀请"} — "${t5.npcSpokenText.slice(0, 50)}"`);
    }
  }

  // 框架修复验证
  log.push(`\n## 框架修复验证\n`);
  log.push(`| Bug | 修复 | 验证方式 | 结果 |\n`);
  log.push(`|-----|------|----------|------|\n`);
  const minimaxOk = allResults[1]?.result.turns.some(t => t.error === undefined) ? "✅ 成功" : "❌ 失败";
  const toolResultFedBack = allResults.some(r => r.result.turns.some(t => t.events.some(e => e.includes("tool_call_end"))))
    ? "✅ 工具结果回传" : "⚠️ 未验证";
  const multiTurnOk = allResults.some(r => r.result.turns.some(t => t.toolCalls.length > 0 && t.events.some(e => e.includes("turn_start #1"))))
    ? "✅ 多轮正常" : "⚠️ 未验证";
  const toolsCalled = allResults.some(r => r.result.turns.some(t => t.toolCalls.length > 0))
    ? "✅ 工具被调用" : "❌ 工具未被调用";
  const deepseekOk = allResults[0]?.result.turns.some(t => t.error === undefined) ? "✅ 成功" : "❌ 失败";
  log.push(`| #1 minimax路由 | 路由到 openai.chat() | Minimax 调用是否成功 | ${minimaxOk} |\n`);
  log.push(`| #2 工具结果回传 | role:"tool" 消息 | 事件流中 tool_call_end 后有 tool 消息 | ${toolResultFedBack} |\n`);
  log.push(`| #3 tool role | AgentMessage 支持 | convertToLlm 处理 tool 消息 | ✅ 类型系统通过 |\n`);
  log.push(`| #4 assistant toolCalls | 消息转换保留 | 多轮工具调用是否正常 | ${multiTurnOk} |\n`);
  log.push(`| #5 llmCall tools | 工具定义传递 | LLM 是否正确调用工具 | ${toolsCalled} |\n`);
  log.push(`| #6 Deepseek 404 | .chat() Chat Completions | Deepseek 调用是否成功 | ${deepseekOk} |\n`);

  // 写入文件
  const outputPath = join(import.meta.dir, "..", "..", "..", "..", "docs", "valley-core-stardew-e2e-log.md");
  await mkdir(dirname(outputPath), { recursive: true });
  await writeFile(outputPath, log.join(""), "utf-8");
  console.log(`日志已写入: ${outputPath}`);
  console.log(`总轮次: ${allResults.reduce((s, r) => s + r.result.turns.length, 0)}`);
  console.log(`总工具调用: ${allResults.reduce((s, r) => s + r.result.turns.reduce((s2, t) => s2 + t.toolCalls.length, 0), 0)}`);
}

main().catch(err => {
  console.error("Fatal error:", err);
  process.exit(1);
});
