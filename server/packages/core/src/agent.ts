import { EventStream } from "./event-stream";
import { agentLoop } from "./agent-loop";
import type { AgentLoopConfig } from "./agent-loop";
import type { AgentContext, AgentMessage, AgentEvent } from "./types";

export type DrainMode = "one_at_a_time" | "all";

export interface AgentState {
  isStreaming: boolean;
  streamingMessage: string | null;
  pendingToolCalls: number;
  errorMessage: string | null;
}

interface ActiveRun {
  stream: EventStream<AgentEvent>;
  abortController: AbortController;
}

export class Agent {
  readonly name: string;
  private readonly config: AgentLoopConfig;
  private activeRun: ActiveRun | null = null;
  private steeringQueue: AgentMessage[] = [];
  private followUpQueue: Array<{ message: AgentMessage; mode: DrainMode }> = [];
  private subscribers: Set<(event: AgentEvent) => void> = new Set();
  private state: AgentState = {
    isStreaming: false,
    streamingMessage: null,
    pendingToolCalls: 0,
    errorMessage: null,
  };

  constructor(name: string, config: AgentLoopConfig) {
    this.name = name;
    this.config = config;
  }

  isIdle(): boolean {
    return this.activeRun === null;
  }

  getState(): AgentState {
    return { ...this.state };
  }

  prompt(context: AgentContext): EventStream<AgentEvent> {
    if (this.activeRun) {
      throw new Error(`Agent ${this.name} is already running`);
    }

    const abortController = new AbortController();
    const wrappedConfig: AgentLoopConfig = {
      ...this.config,
      transformContext: (ctx) => {
        let result = ctx;
        if (this.steeringQueue.length > 0) {
          const steered = [...this.steeringQueue];
          this.steeringQueue = [];
          result = { ...result, messages: [...result.messages, ...steered] };
        }
        if (this.config.transformContext) {
          result = this.config.transformContext(result);
        }
        return result;
      },
      shouldStopAfterTurn: (ctx, turn) => {
        if (this.steeringQueue.length > 0) return false;
        return this.config.shouldStopAfterTurn?.(ctx, turn) ?? (turn >= 0);
      },
    };

    const stream = agentLoop({ context, config: wrappedConfig, signal: abortController.signal });

    // Live state updates only. agentLoop emits agent_start/turn_start synchronously
    // during the agentLoop() call, before this subscriber is attached, so those
    // early events are not delivered live; the completed event log is replayed
    // to subscribers and the proxy stream below.
    stream.subscribe((event) => {
      this.updateState(event);
    });

    this.activeRun = { stream, abortController };
    this.state = { ...this.state, isStreaming: true, errorMessage: null };

    const proxyStream = new EventStream<AgentEvent>();

    // run 收尾的终态化步骤（成功/失败路径共用）：activeRun 清空 + 状态复位 + follow-up 排水。
    // issue #23（审计 §3.8）：此前只存在于 then 分支内联，任何异常路径都会跳过 → NPC 永久 BUSY。
    const finalizeRun = (): void => {
      this.activeRun = null;
      this.state = { ...this.state, isStreaming: false, streamingMessage: null };
      this.drainFollowUps();
    };

    stream.awaitAll().then((allEvents) => {
      // Replay the full event log (including synchronously-emitted agent_start
      // and turn_start) to the proxy stream and to subscribers.
      // 订阅者隔离（issue #23）：单个订阅者抛异常只记错 + 进状态，不得传播——
      // 否则回放中断，proxyStream 永不 done()、activeRun 永不清空。
      for (const event of allEvents) {
        for (const sub of this.subscribers) {
          try {
            sub(event);
          } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            console.error(
              `[agent] ${this.name}: subscriber ${sub.name || "(anonymous)"} threw during replay — isolated:`,
              err,
            );
            this.state = { ...this.state, errorMessage: message };
          }
        }
        if (!proxyStream.isDone()) proxyStream.emit(event);
      }
      if (!proxyStream.isDone()) proxyStream.done();
      finalizeRun();
    }).catch((err: unknown) => {
      // 兜底终态化（issue #23）：awaitAll 拒绝或任何未预期路径都必须收敛——
      // proxyStream 关闭（先补一个 error 事件）、activeRun 清空、错误进 AgentState，
      // 不再依赖 cli.ts 的全局 unhandledRejection 兜（那会留下永久 BUSY + 一行 {}）。
      const message = err instanceof Error ? err.message : String(err);
      console.error(`[agent] ${this.name}: run failed to finalize — closing stream with error:`, err);
      if (!proxyStream.isDone()) {
        proxyStream.emit({ type: "error", timestamp: Date.now(), message, error: err });
        proxyStream.done();
      }
      this.state = { ...this.state, errorMessage: message };
      finalizeRun();
    });

    return proxyStream;
  }

  steer(message: AgentMessage): void {
    this.steeringQueue.push(message);
  }

  followUp(message: AgentMessage, mode: DrainMode = "all"): void {
    this.followUpQueue.push({ message, mode });
    if (this.isIdle()) this.drainFollowUps();
  }

  abort(): void {
    if (this.activeRun) this.activeRun.abortController.abort();
    this.steeringQueue = [];
    this.followUpQueue = [];
  }

  reset(): void {
    this.abort();
    this.activeRun = null;
    this.state = {
      isStreaming: false,
      streamingMessage: null,
      pendingToolCalls: 0,
      errorMessage: null,
    };
  }

  subscribe(fn: (event: AgentEvent) => void): () => void {
    this.subscribers.add(fn);
    return () => { this.subscribers.delete(fn); };
  }

  async waitForIdle(): Promise<void> {
    if (!this.activeRun) return;
    await this.activeRun.stream.awaitAll();
    while (this.activeRun) {
      await this.activeRun.stream.awaitAll();
    }
  }

  private drainFollowUps(): void {
    if (this.followUpQueue.length === 0 || !this.isIdle()) return;

    const followUp = this.followUpQueue.shift()!;
    const context: AgentContext = {
      messages: [followUp.message],
      systemPrompt: "",
      metadata: {},
    };

    if (followUp.mode === "all") {
      while (this.followUpQueue.length > 0) {
        const next = this.followUpQueue.shift()!;
        context.messages.push(next.message);
      }
    }
    this.prompt(context);
  }

  private updateState(event: AgentEvent): void {
    switch (event.type) {
      case "message_start":
        this.state.streamingMessage = "";
        break;
      case "message_update":
        this.state.streamingMessage = (this.state.streamingMessage ?? "") + event.delta;
        break;
      case "message_end":
        this.state.streamingMessage = event.content;
        break;
      case "tool_call_start":
        this.state.pendingToolCalls++;
        break;
      case "tool_call_end":
        this.state.pendingToolCalls = Math.max(0, this.state.pendingToolCalls - 1);
        break;
      case "error":
        this.state.errorMessage = event.message;
        break;
    }
  }
}
