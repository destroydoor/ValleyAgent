// TranscriptStore 记录类型 — Phase 1 E1-1 全量留痕的数据契约。
// 三张表 agent_runs / agent_turns / director_runs 各对应一个 Record。
// Spec: docs/design/2026-08-01-memory-narrative-extensibility.md §1
//
// 设计要点：
// - 数组字段（actions / toolCalls / producedBeats / droppedBeats）在 Record
//   层为必填且默认 []，避免下游消费方到处判空；写入时 JSON.stringify。
// - 可选字段用 `?` 标注，配合 exactOptionalPropertyTypes: true —— 赋值
//   undefined 会被 TS 拒绝，因此序列化时统一用 null 兜底再转回 undefined。
// - trigger / status 用字面量联合，落库为 TEXT，读回时 `as` 还原。

/** agent_runs.trigger：触发来源 */
export type AgentRunTrigger = "dialogue" | "beat" | "director";

/** agent_runs.status：运行终态 */
export type AgentRunStatus = "running" | "completed" | "error" | "fallback";

/** director_runs.status：导演运行终态 */
export type DirectorRunStatus = "completed" | "error" | "noop";

/** agent_runs.validation：输出校验结果（C# 侧规则 + LLM 侧结构） */
export interface AgentRunValidation {
  valid: boolean;
  /** 校验失败原因列表；valid=true 时省略 */
  issues?: string;
}

/** agent_runs.fallback：降级标记（熔断/超时/校验失败走兜底响应） */
export interface AgentRunFallback {
  flag: boolean;
  /** 降级原因；flag=false 时省略 */
  reason?: string;
}

/** agent_runs.tokens：LLM token 用量（provider 回传，可能缺失） */
export interface AgentRunTokens {
  in?: number;
  out?: number;
}

/**
 * agent_runs 一行 —— 一次 runDialogue / runBeat / 导演调用的完整留痕。
 * 数组字段默认 []；可选字段省略时落库为 NULL。
 */
export interface AgentRunRecord {
  runId: string;
  npcName: string;
  trigger: AgentRunTrigger;
  gameDate?: string;
  requestId?: string;
  beatId?: string;
  startedAt: string;          // ISO 8601 UTC
  finishedAt?: string;        // ISO 8601 UTC
  systemPromptHash: string;
  systemPromptFull: string;
  systemPromptDynamic: string;
  userInput?: string;
  finalSpeech?: string;
  actions: unknown[];          // ToolAction[]，宽松类型避免循环依赖
  toolCalls: unknown[];       // 原始 LLM tool_call 块
  validation: AgentRunValidation;
  fallback: AgentRunFallback;
  tokens: AgentRunTokens;
  latencyMs?: number;
  status: AgentRunStatus;
  /** error 时存放错误信息；status=error 才有值 */
  error?: string;
}

/**
 * agent_turns 一行 —— 一次 run 内部的单个 LLM 轮次（多轮 ReAct/工具调用）。
 * 通过 runId 关联 agent_runs；turn_index 从 0 起。
 */
export interface AgentTurnRecord {
  runId: string;
  turnIndex: number;
  llmRawOutput?: string;
  toolCalls: unknown[];
  toolResults: unknown[];
  gameDate?: string;
}

/**
 * director_runs 一行 —— 导演一次调度（每日/事件触发）的留痕。
 * empty_result=true 表示导演本轮没有产出任何 beat（正常 noop）。
 */
export interface DirectorRunRecord {
  runId: string;
  gameDate: string;
  trigger: string;
  promptFull: string;
  llmRawOutput?: string;
  producedBeats: unknown[];
  droppedBeats: unknown[];
  emptyResult: boolean;
  status: DirectorRunStatus;
  error?: string;
  /**
   * M3 多玩家化：本次导演编排面向的玩家（缺省 = 单玩家/未归属）。
   * 落在 director_runs.player_id 列（旧库增量加列，可空）。
   */
  playerId?: string;
}