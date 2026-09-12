import { EventStream } from "./event-stream";
import type { AgentContext, AgentMessage, AgentEvent, LlmMessage, AgentToolCall } from "./types";
import type { ToolRegistry } from "./tool-registry";
import type { Tool, ToolResult } from "./tool";
import type { LlmResponse } from "./llm-provider";

export interface LlmCallResult extends LlmResponse {
  toolCalls?: AgentToolCall[];
}

export interface AgentLoopContext {
  context: AgentContext;
  config: AgentLoopConfig;
  signal: AbortSignal;
}

export interface BeforeToolCallResult {
  allow: boolean;
  reason?: string;
  overrideArgs?: Record<string, unknown>;
}

export interface AfterToolCallResult {
  result: ToolResult;
}

export interface AgentLoopConfig {
  tools: ToolRegistry;
  convertToLlm: (ctx: AgentContext) => { messages: LlmMessage[] };
  llmCall: (messages: LlmMessage[], tools?: Tool[]) => Promise<LlmCallResult>;
  transformContext?: (ctx: AgentContext) => AgentContext;
  beforeToolCall?: (
    name: string,
    args: Record<string, unknown>,
    ctx: AgentContext
  ) => Promise<BeforeToolCallResult>;
  afterToolCall?: (
    name: string,
    args: Record<string, unknown>,
    result: ToolResult,
    ctx: AgentContext
  ) => Promise<AfterToolCallResult>;
  shouldStopAfterTurn?: (ctx: AgentContext, turnIndex: number) => boolean;
  prepareNextTurn?: (ctx: AgentContext, turnIndex: number) => AgentContext;
  toolExecution?: "sequential" | "parallel";
  maxTurns?: number;
}

export function agentLoop(params: AgentLoopContext): EventStream<AgentEvent> {
  const stream = new EventStream<AgentEvent>();
  const { context, config, signal } = params;
  const maxTurns = config.maxTurns ?? 10;
  const execMode = config.toolExecution ?? "sequential";

  (async () => {
    stream.emit({ type: "agent_start", timestamp: Date.now() });

    let currentContext = context;
    let aborted = false;

    signal.addEventListener("abort", () => { aborted = true; });

    try {
      for (let turn = 0; turn < maxTurns && !aborted; turn++) {
        stream.emit({ type: "turn_start", timestamp: Date.now(), turnIndex: turn });

        if (config.transformContext) {
          currentContext = config.transformContext(currentContext);
        }

        const { messages } = config.convertToLlm(currentContext);
        const llmVisibleTools = config.tools.getLlmVisibleTools();

        let llmResult: LlmCallResult;
        try {
          llmResult = await config.llmCall(messages, llmVisibleTools);
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err);
          // E5: preserve original error so downstream instanceof checks survive.
          stream.emit({ type: "error", timestamp: Date.now(), message, error: err });
          break;
        }

        if (aborted) break;

        stream.emit({ type: "message_start", timestamp: Date.now() });
        stream.emit({ type: "message_update", timestamp: Date.now(), delta: llmResult.content });
        stream.emit({ type: "message_end", timestamp: Date.now(), content: llmResult.content });

        const assistantMsg: AgentMessage = {
          role: "assistant",
          content: llmResult.content,
          ...(llmResult.toolCalls ? { toolCalls: llmResult.toolCalls } : {}),
        };
        currentContext = {
          ...currentContext,
          messages: [...currentContext.messages, assistantMsg],
        };

        if (llmResult.toolCalls && llmResult.toolCalls.length > 0) {
          const toolResults: AgentMessage[] = [];
          if (execMode === "parallel") {
            const settled = await Promise.all(
              llmResult.toolCalls.map(async (tc) => {
                const result = await processToolCall(stream, config, currentContext, tc);
                return { tc, result };
              })
            );
            for (const { tc, result } of settled) {
              toolResults.push({
                role: "tool",
                content: result.content,
                toolCallId: tc.id,
                toolName: tc.name,
              });
            }
          } else {
            for (const tc of llmResult.toolCalls) {
              if (aborted) break;
              const result = await processToolCall(stream, config, currentContext, tc);
              toolResults.push({
                role: "tool",
                content: result.content,
                toolCallId: tc.id,
                toolName: tc.name,
              });
            }
          }

          if (toolResults.length > 0) {
            currentContext = {
              ...currentContext,
              messages: [...currentContext.messages, ...toolResults],
            };
          }
        }

        stream.emit({ type: "turn_end", timestamp: Date.now(), turnIndex: turn });

        if (config.shouldStopAfterTurn?.(currentContext, turn)) break;

        if (config.prepareNextTurn) {
          currentContext = config.prepareNextTurn(currentContext, turn + 1);
        }
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      // E5: preserve original error so downstream instanceof checks survive.
      stream.emit({ type: "error", timestamp: Date.now(), message, error: err });
    }

    stream.emit({ type: "agent_end", timestamp: Date.now() });
    stream.done();
  })();

  return stream;
}

async function processToolCall(
  stream: EventStream<AgentEvent>,
  config: AgentLoopConfig,
  ctx: AgentContext,
  toolCall: AgentToolCall
): Promise<ToolResult> {
  stream.emit({
    type: "tool_call_start",
    timestamp: Date.now(),
    toolName: toolCall.name,
    toolCallId: toolCall.id,
    args: toolCall.args,
  });

  let result: ToolResult;

  if (config.beforeToolCall) {
    const gateResult = await config.beforeToolCall(toolCall.name, toolCall.args, ctx);
    if (!gateResult.allow) {
      result = { content: gateResult.reason ?? "Tool call blocked", isError: true };
      stream.emit({
        type: "tool_call_end",
        timestamp: Date.now(),
        toolName: toolCall.name,
        toolCallId: toolCall.id,
        result,
        isError: true,
      });
      return result;
    }
    if (gateResult.overrideArgs) {
      toolCall = { ...toolCall, args: gateResult.overrideArgs };
    }
  }

  result = await config.tools.execute(toolCall.name, toolCall.args);

  if (config.afterToolCall) {
    const hookResult = await config.afterToolCall(toolCall.name, toolCall.args, result, ctx);
    result = hookResult.result;
  }

  stream.emit({
    type: "tool_call_end",
    timestamp: Date.now(),
    toolName: toolCall.name,
    toolCallId: toolCall.id,
    result,
    isError: result.isError ?? false,
  });

  return result;
}
