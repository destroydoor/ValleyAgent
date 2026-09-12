// ─── Agent Messages ───

export interface AgentMessage {
  role: "user" | "assistant" | "system" | "tool";
  content: string;
  toolCallId?: string;
  toolCalls?: AgentToolCall[];
  toolName?: string;
}

export interface AgentToolCall {
  id: string;
  name: string;
  args: Record<string, unknown>;
}

// ─── LLM Messages (Vercel AI SDK compatible) ───

export type LlmMessage =
  | { role: "system"; content: string }
  | { role: "user"; content: string }
  | { role: "assistant"; content: string; toolCalls?: LlmToolCall[] }
  | { role: "tool"; content: string; toolCallId: string; toolName?: string };

export interface LlmToolCall {
  id: string;
  type: "function";
  functionName: string;
  args: string;
}

// ─── Agent Events (discriminated union, 10 types) ───

export type AgentEventType =
  | "agent_start"
  | "agent_end"
  | "turn_start"
  | "turn_end"
  | "message_start"
  | "message_update"
  | "message_end"
  | "tool_call_start"
  | "tool_call_end"
  | "error";

export interface BaseEvent {
  type: AgentEventType;
  timestamp: number;
}

export interface AgentStartEvent extends BaseEvent {
  type: "agent_start";
}

export interface AgentEndEvent extends BaseEvent {
  type: "agent_end";
}

export interface TurnStartEvent extends BaseEvent {
  type: "turn_start";
  turnIndex: number;
}

export interface TurnEndEvent extends BaseEvent {
  type: "turn_end";
  turnIndex: number;
}

export interface MessageStartEvent extends BaseEvent {
  type: "message_start";
}

export interface MessageUpdateEvent extends BaseEvent {
  type: "message_update";
  delta: string;
}

export interface MessageEndEvent extends BaseEvent {
  type: "message_end";
  content: string;
}

export interface ToolCallStartEvent extends BaseEvent {
  type: "tool_call_start";
  toolName: string;
  toolCallId: string;
  args: Record<string, unknown>;
}

export interface ToolCallEndEvent extends BaseEvent {
  type: "tool_call_end";
  toolName: string;
  toolCallId: string;
  result: unknown;
  isError: boolean;
}

export interface ErrorEvent extends BaseEvent {
  type: "error";
  message: string;
  /**
   * E5: 原始错误对象保留，使下游（stardew-agent.runOnce → protocol-adapter catch
   * → rule-engine.buildFallbackResponse）能按 instanceof LLMBillingError/
   * LLMUnavailableError 分档命中人设兜底。可选：旧 emitter 不填，消费者回退
   * 到 message-only 旧路径。
   */
  error?: unknown;
}

export type AgentEvent =
  | AgentStartEvent
  | AgentEndEvent
  | TurnStartEvent
  | TurnEndEvent
  | MessageStartEvent
  | MessageUpdateEvent
  | MessageEndEvent
  | ToolCallStartEvent
  | ToolCallEndEvent
  | ErrorEvent;

// ─── Agent Context ───

export interface AgentContext {
  messages: AgentMessage[];
  systemPrompt: string;
  metadata: Record<string, unknown>;
}
