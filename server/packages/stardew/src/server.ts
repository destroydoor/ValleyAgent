import { VercelAIProvider, LlmRouter, loadRouterConfig, type LLMConfig, type LlmRouterConfig, type ProviderToolCallResult } from "@valley/core";
import { NpcPromptLoader } from "./npc-prompt-loader";
import { PromptBuilder } from "./prompt-builder";
import { StardewAgentRegistry } from "./stardew-agent-registry";
import type { TranscriptConfig, RegistryConfig } from "./stardew-agent-registry";
import { ProtocolAdapter, type ProtocolAdapterOptions } from "./protocol-adapter";
import { GameContextManager } from "./game-context";
import { AgentLedger } from "./agent-ledger";
import { EmotionEngine } from "./emotion-engine";
import { attachConsoleTee } from "./log-tee";

// 2026-09-11 观测补强：VALLEY_SERVER_LOGFILE 由 C# 端在"可见控制台窗口"模式
// （ServerConsoleWindow=true，发行包默认）下设置——该模式 stdout 无法重定向，
// 卡死时 TS 侧最后行为全部丢失，改由 log-tee 在进程内自落盘（false 分支 C#
// 自己捕获重定向，不设此变量，两路互斥不双写）。须在任何会打日志的逻辑之前执行。
const serverLogFile = process.env.VALLEY_SERVER_LOGFILE;
if (serverLogFile) attachConsoleTee(serverLogFile);

export interface ServerLLMConfig {
  provider: string;
  apiKey: string;
  model: string;
  baseUrl: string;
  temperature?: number;
  maxTokens?: number;
  timeout?: number;
  maxRetries?: number;
  maxConcurrency?: number;
}

export interface ServerConfig {
  port: number;
  hostname: string;
  dataPath: string;
  agentsDir: string;
  /** 单 provider 模式（与 llmRouterConfig 二选一） */
  llmConfig?: ServerLLMConfig;
  /** 多 provider 模式：直接传配置对象 */
  llmRouterConfig?: LlmRouterConfig;
  /** 多 provider 模式：传 runtime JSON 文件路径 */
  llmRouterConfigPath?: string;
  llmCallOverride?: (messages: unknown, tools?: unknown) => Promise<ProviderToolCallResult>;
  /** Phase 1 E1-1 全量留痕。缺省 = 不启用（prod 默认零开销）。 */
  transcript?: TranscriptConfig;
}

export interface ServerHandle {
  port: number;
  stop: () => Promise<void>;
}

export async function startServer(config: ServerConfig): Promise<ServerHandle> {
  const loader = new NpcPromptLoader(config.dataPath);
  const builder = new PromptBuilder(loader);

  // --- 构造 RegistryConfig ---
  let registryConfig: RegistryConfig;

  if (config.llmRouterConfigPath) {
    // 多 provider 模式（从文件加载）
    const routerConfig = loadRouterConfig(config.llmRouterConfigPath);
    const router = new LlmRouter(routerConfig);
    registryConfig = {
      promptBuilder: builder,
      llmRouter: router,
      agentsDir: config.agentsDir,
      ...(config.transcript ? { transcript: config.transcript } : {}),
    };
    // 注入 callOverride 到所有 provider（测试用）
    if (config.llmCallOverride) {
      for (const role of ["director", "protagonist", "npc"] as const) {
        router.getProvider(role)._setCallOverride(config.llmCallOverride);
        const fb = router.getProviderFallback(role);
        if (fb) fb._setCallOverride(config.llmCallOverride);
      }
    }
  } else if (config.llmRouterConfig) {
    // 多 provider 模式（直接传配置对象）
    const router = new LlmRouter(config.llmRouterConfig);
    registryConfig = {
      promptBuilder: builder,
      llmRouter: router,
      agentsDir: config.agentsDir,
      ...(config.transcript ? { transcript: config.transcript } : {}),
    };
    if (config.llmCallOverride) {
      for (const role of ["director", "protagonist", "npc"] as const) {
        router.getProvider(role)._setCallOverride(config.llmCallOverride);
        const fb = router.getProviderFallback(role);
        if (fb) fb._setCallOverride(config.llmCallOverride);
      }
    }
  } else if (config.llmConfig) {
    // 单 provider 模式（向后兼容）
    const llmConfig: LLMConfig = {
      provider: config.llmConfig.provider as LLMConfig["provider"],
      apiKey: config.llmConfig.apiKey,
      model: config.llmConfig.model,
      baseUrl: config.llmConfig.baseUrl,
      temperature: config.llmConfig.temperature ?? 0.7,
      maxTokens: config.llmConfig.maxTokens ?? 800,
      timeout: config.llmConfig.timeout ?? 30_000,
      maxRetries: config.llmConfig.maxRetries ?? 3,
      maxConcurrency: config.llmConfig.maxConcurrency ?? 4,
    };
    const provider = new VercelAIProvider(llmConfig);
    if (config.llmCallOverride) {
      provider._setCallOverride(config.llmCallOverride);
    }
    registryConfig = {
      promptBuilder: builder,
      llmProvider: provider,
      agentsDir: config.agentsDir,
      ...(config.transcript ? { transcript: config.transcript } : {}),
    };
  } else {
    throw new Error("ServerConfig requires either llmConfig, llmRouterConfig, or llmRouterConfigPath");
  }

  const registry = new StardewAgentRegistry(registryConfig);

  // 2026-09-14 砍除旧叙事 Director（morningPlan→beat→allocate_agent 产出无人消费，
  // runBeat 从未接入生产；见 docs/plan/2026-09-12-architecture-drift-audit.md 建议 #1
  // 的裁决）。GameContextManager 保留：game_context_sync 活链路在用。
  const gameCtxMgr = new GameContextManager();
  // activeWs 在 websocket open 回调中赋值，供 sendToCsharp 发送主动消息
  let activeWs: import("bun").ServerWebSocket<unknown> | null = null;

  // 2026-08-15 账本迁移（步骤 1）：权威经济账本（adjust_result 路由消费 + worldSnapshot 播种）。
  // 与 memory 同目录（agentsDir），文件 {npc}_ledger.json；步骤 1 不拦截任何工具，旧路径不动。
  const ledger = new AgentLedger(config.agentsDir);
  // 2026-08-15 步骤 3：确定性情绪引擎（零 LLM，事件驱动；人设参数走默认，可后续从数据注入）。
  const emotionEngine = new EmotionEngine();

  const adapterOptions: ProtocolAdapterOptions = {
    sendToCsharp: (msg: unknown) => {
      if (activeWs && activeWs.readyState === 1) {
        activeWs.send(JSON.stringify(msg));
      } else {
        // 2026-09-11 观测补强：断连/无连接期间的主动消息此前静默丢弃——只打 type
        // 不打全文（prompt 级大消息会刷爆日志）。
        console.warn("[sendToCsharp] dropped message (no active ws connection): type=" + ((msg as { type?: string }).type ?? "unknown"));
      }
    },
    ledger,
    emotionEngine,
    gameCtxMgr,
  };
  const adapter = new ProtocolAdapter(registry, adapterOptions);

  const server = Bun.serve({
    port: config.port,
    hostname: config.hostname,
    fetch(req, s) {
      if (s.upgrade(req)) return undefined;
      return new Response("WebSocket only", { status: 200 });
    },
    websocket: {
      open(ws) {
        // 2026-09-11 观测补强：第二连接顶替可见——"每玩家一个导演"误读的直接
        // 观测面（单槽 activeWs、last-writer-wins，老连接还开着说明可能是第二
        // 个游戏实例或重连竞态）。
        const previous = activeWs;
        if (previous && previous !== ws && previous.readyState === 1) {
          console.warn("[server] new connection REPLACING existing active connection (single-slot activeWs, last-writer-wins) — old connection still open, likely a second game instance or reconnect race");
        }
        console.log("[server] websocket connected");
        activeWs = ws;
      },
      async message(ws, message) {
        try {
          const text = typeof message === "string" ? message : new TextDecoder().decode(message);
          const parsed = JSON.parse(text);
          const response = await adapter.routeMessage(parsed);
          // ws 可能在 await routeMessage 期间关闭，send 前必须检查状态
          if (ws.readyState === 1) { // WebSocket.OPEN
            ws.send(JSON.stringify(response));
          } else {
            console.warn("[server] ws closed during request processing, dropping response");
          }
        } catch (err) {
          console.error("[server] message handler error:", err);
          // catch 块中也要检查 ws 状态，避免二次抛出导致进程崩溃
          try {
            if (ws.readyState === 1) {
              ws.send(JSON.stringify({ type: "error", message: String(err) }));
            }
          } catch (sendErr) {
            console.error("[server] failed to send error response:", sendErr);
          }
        }
      },
      close(ws, code, reason) {
        // 2026-09-11 观测补强：清 stale 引用（sendToCsharp 本就按 readyState 拒发，
        // 清 null 无行为差异）并标记是否是活跃连接断开。
        const wasActive = ws === activeWs;
        if (wasActive) activeWs = null;
        console.log(`[server] websocket closed: code=${code} reason=${reason}${wasActive ? " (was active connection)" : ""}`);
      },
    },
  });

  const boundPort = server.port;
  if (boundPort === undefined) {
    server.stop();
    throw new Error(`Server failed to bind port ${config.port}`);
  }

  return {
    port: boundPort,
    stop: async () => {
      await adapter.flushPendingSaves();
      // 关闭留痕 store（checkpoint 刷 WAL），未启用时 no-op。
      registry.closeTranscriptStore();
      server.stop();
    },
  };
}
