import { LLMBillingError, LLMBudgetError, LLMUnavailableError } from "@valley/core";
import type { DialogueRequest, DialogueResponse, ToolAction } from "./types";
import { DEFAULT_EMOTION } from "./types";
import { SnapshotValidationError } from "./world-snapshot-decoder";

/**
 * fallback=true 时的机器可读降级原因（wire 约定见 server/protocol/messages.json 的
 * dialogue_response.fallbackReason 字段）。诊断分界：llm_error/billing/unavailable/config
 * 查 LLM 侧（key/配额/网络/预算）；bad_request/validation_failed/internal_error 查代码
 * 与协议侧，不要动 LLM 配置（issue #26 批⑤扩档，审计 §4.5）。
 */
export type FallbackReason =
  | "busy" // per-NPC 会话锁超时（正常并发拒绝，protocol-adapter buildBusyResponse）
  | "llm_error" // LLM 生成失败（输出异常/解析失败）
  | "billing" // LLM 计费失败（402/429；401/403 鉴权快速失败也归此类，见 llm-provider）
  | "unavailable" // LLM 重试耗尽不可用（网络/5xx）
  | "config" // LLM 配置问题（token 预算耗尽 LLMBudgetError）
  | "bad_request" // 请求缺关键载荷（如 worldSnapshot 整体缺失）
  | "validation_failed" // 请求结构不合规（worldSnapshot 缺必填字段）
  | "internal_error"; // TS 代码缺陷（TypeError/ReferenceError/RangeError 等）

export class RuleEngine {
  // 显式空构造：隐式构造函数会被 bun 的覆盖率计数判为未覆盖函数（funcs 50% 假红）
  constructor() {}

  /**
   * 构造规则引擎兜底响应。forcedReason 供调用方在异常分类无法自动判定时显式指定档位
   * （如 handleDialogue 缺 worldSnapshot 守卫直接定 bad_request，issue #26 批⑤）。
   */
  buildFallbackResponse(req: DialogueRequest, err: unknown, forcedReason?: FallbackReason): DialogueResponse {
    let speech: string;
    let emotion: string;
    let fallbackReason: FallbackReason;

    if (forcedReason) {
      // 显式指定档位（守卫路径）：不带情绪戏，直接中性兜底
      speech = "......";
      emotion = DEFAULT_EMOTION;
      fallbackReason = forcedReason;
    } else if (err instanceof LLMBillingError) {
      speech = "（我有点走神了，你刚说什么？）";
      emotion = "Confused";
      fallbackReason = "billing";
    } else if (err instanceof LLMBudgetError) {
      // issue #26 批⑤：预算耗尽是配置问题（tokenBudget 偏小），此前误标 llm_error
      speech = "......";
      emotion = DEFAULT_EMOTION;
      fallbackReason = "config";
    } else if (err instanceof LLMUnavailableError) {
      speech = "（话到嘴边说不出来...）";
      emotion = "Tired";
      fallbackReason = "unavailable";
    } else if (err instanceof SnapshotValidationError) {
      // 审计 §4.5：worldSnapshot 缺必填字段 = 坏请求（C# 侧序列化缺陷），不是 LLM 故障
      speech = "......";
      emotion = DEFAULT_EMOTION;
      fallbackReason = "validation_failed";
    } else if (err instanceof TypeError || err instanceof ReferenceError || err instanceof RangeError) {
      // 审计 §4.5 误诊修复：代码缺陷此前全落 llm_error，排障被误导去查 LLM key/配额
      speech = "......";
      emotion = DEFAULT_EMOTION;
      fallbackReason = "internal_error";
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
