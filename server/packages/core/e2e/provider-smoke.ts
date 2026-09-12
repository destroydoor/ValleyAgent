// 冒烟测试：sensenova / mimo 第三方兼容端点走 VercelAIProvider 真实链路（含工具调用）。
// 用法：SENSENSOVA_KEY=... MIMO_KEY=... bun packages/core/e2e/provider-smoke.ts
import { VercelAIProvider } from "../src/llm-provider";
import type { LlmMessage } from "../src/types";

const speakTool = {
  name: "speak",
  description: "说一句话",
  parameters: {
    type: "object",
    properties: { text: { type: "string", description: "要说的话" } },
    required: ["text"],
  },
};

const messages: LlmMessage[] = [
  { role: "system", content: "你是星露谷的 NPC 阿比盖尔。必须用工具回复。" },
  { role: "user", content: "你好呀，今天天气真好" },
];

async function smoke(name: string, provider: string, apiKey: string, model: string, baseUrl: string) {
  const p = new VercelAIProvider({ provider: provider as any, apiKey, model, baseUrl, timeout: 60_000, maxRetries: 1 });
  const r = await p.chatWithTools(messages, [speakTool]);
  const call = r.toolCalls?.[0];
  console.log(`[${name}] ok — toolCall=${call?.name ?? "NONE"} args=${JSON.stringify(call?.args ?? {})} out=${r.usage?.completionTokens}tok`);
}

const sensenovaKey = process.env.SENSENSOVA_KEY;
const mimoKey = process.env.MIMO_KEY;
if (!sensenovaKey || !mimoKey) {
  console.error("usage: SENSENSOVA_KEY=... MIMO_KEY=... bun packages/core/e2e/provider-smoke.ts");
  process.exit(1);
}

await smoke("sensenova-deepseek-v4-flash", "sensenova", sensenovaKey, "deepseek-v4-flash", "https://token.sensenova.cn/v1");
await smoke("mimo-v2.5", "mimo", mimoKey, "mimo-v2.5", "https://token-plan-cn.xiaomimimo.com/v1");
