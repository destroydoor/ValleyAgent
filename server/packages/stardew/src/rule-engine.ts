import { LLMBillingError, LLMUnavailableError } from "@valley/core";
import type { DialogueRequest, DialogueResponse, ToolAction } from "./types";
import { DEFAULT_EMOTION } from "./types";

export class RuleEngine {
  // 显式空构造：隐式构造函数会被 bun 的覆盖率计数判为未覆盖函数（funcs 50% 假红）
  constructor() {}

  buildFallbackResponse(req: DialogueRequest, err: unknown): DialogueResponse {
    let speech: string;
    let emotion: string;
    // 2026-09-13 R2：按异常类型带机器可读降级原因，wire 层透传 C#（取值约定见 messages.json）。
    let fallbackReason: "llm_error" | "billing" | "unavailable";

    if (err instanceof LLMBillingError) {
      speech = "（我有点走神了，你刚说什么？）";
      emotion = "Confused";
      fallbackReason = "billing";
    } else if (err instanceof LLMUnavailableError) {
      speech = "（话到嘴边说不出来...）";
      emotion = "Tired";
      fallbackReason = "unavailable";
    } else {
      speech = "......";
      emotion = DEFAULT_EMOTION;
      fallbackReason = "llm_error";
    }

    const actions: ToolAction[] = [
      { tool: "emote", args: { emote_id: "question" } },
    ];

    return {
      type: "dialogue_response",
      requestId: req.requestId,
      npcName: req.npcName,
      speech,
      actions,
      emotion,
      fallback: true,
      fallbackReason,
    };
  }
}
