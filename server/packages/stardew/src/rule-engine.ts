import { LLMBillingError, LLMUnavailableError } from "@valley/core";
import type { DialogueRequest, DialogueResponse, ToolAction } from "./types";
import { DEFAULT_EMOTION } from "./types";

export class RuleEngine {
  buildFallbackResponse(req: DialogueRequest, err: unknown): DialogueResponse {
    let speech: string;
    let emotion: string;

    if (err instanceof LLMBillingError) {
      speech = "（我有点走神了，你刚说什么？）";
      emotion = "Confused";
    } else if (err instanceof LLMUnavailableError) {
      speech = "（话到嘴边说不出来...）";
      emotion = "Tired";
    } else {
      speech = "......";
      emotion = DEFAULT_EMOTION;
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
    };
  }
}
