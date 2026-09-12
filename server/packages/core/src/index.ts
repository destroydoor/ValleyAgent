export const CORE_VERSION = "0.1.0";

// Agent + agentLoop
export { Agent } from "./agent";
export type { AgentState, DrainMode } from "./agent";
export { agentLoop } from "./agent-loop";
export type { AgentLoopConfig, AgentLoopContext, BeforeToolCallResult, AfterToolCallResult, LlmCallResult } from "./agent-loop";

// Types
export type {
  AgentMessage,
  AgentToolCall,
  LlmMessage,
  LlmToolCall,
  AgentEventType,
  AgentEvent,
  AgentStartEvent,
  AgentEndEvent,
  TurnStartEvent,
  TurnEndEvent,
  MessageStartEvent,
  MessageUpdateEvent,
  MessageEndEvent,
  ToolCallStartEvent,
  ToolCallEndEvent,
  ErrorEvent,
  AgentContext,
} from "./types";

// Tools
export { ToolRegistry } from "./tool-registry";
export type { Tool, ToolResult, ToolVisibility, ValidationResult } from "./tool";

// LLM
export { VercelAIProvider, LLMBillingError, LLMUnavailableError, LLMBudgetError } from "./llm-provider";
export type { ILLMProvider, LlmResponse, ProviderToolCallResult, LlmJsonResponse } from "./llm-provider";
export { resolveConfig } from "./llm-config";
export type { LLMConfig, LLMProviderType } from "./llm-config";
export * from "./llm-router";

// CircuitBreaker
export { CircuitBreaker, CircuitState } from "./circuit-breaker";
export type { CircuitBreakerConfig } from "./circuit-breaker";

// Transport
export { BunWebSocketTransport } from "./bun-transport";
export type { Transport, Connection, TransportConfig } from "./transport";

// EventStream
export { EventStream } from "./event-stream";

// MemoryBackend interface
export type { MemoryBackend, MemoryEntry, SignificantMemory, ConversationEntry } from "./memory-backend";

// Performance + Token Budget
export { PerformanceMonitor } from "./performance-monitor";
export { TokenBudgetManager } from "./token-budget";
