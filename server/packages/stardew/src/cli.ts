import { startServer } from "./server";
import { resolve, dirname } from "path";
import { mkdirSync, existsSync } from "fs";

function defaultDataPath(): string {
  // In compiled exe, both process.execPath and import.meta.dir point to
  // Bun's virtual filesystem (B:\~BUN\), so we must use process.cwd()
  // (the working directory set by ServerProcessManager) to find data files.
  const cwdCandidate = resolve(process.cwd(), "data/npc_prompts.json");
  if (existsSync(cwdCandidate)) return cwdCandidate;

  // Fallback: try real exe path (works in non-compiled Bun runtime)
  const exeCandidate = resolve(dirname(process.execPath), "../data/npc_prompts.json");
  if (existsSync(exeCandidate)) return exeCandidate;

  // Last resort: dev mode (bun run src/cli.ts): cli.ts sits in src/, data sits in ../data/
  return resolve(import.meta.dir, "../data/npc_prompts.json");
}

interface CliArgs {
  port: number;
  hostname: string;
  dataPath: string;
  agentsDir: string;
  // 单 provider 模式
  llmApiKey: string;
  llmModel: string;
  llmBaseUrl: string;
  llmProvider: string;
  // 多 provider 模式
  llmConfigPath?: string;
  // 导演开关
  enableDirector?: boolean;
  // 导演每日触发概率（0-1），缺省用 server 默认 0.1
  directorTriggerProbability?: number;
  /** Phase 1 E1-1：提供该路径即启用全量留痕（transcript.sqlite 落于此目录）。 */
  transcriptDir?: string;
}

function parseArgs(argv: string[]): CliArgs {
  const args: CliArgs = {
    port: 8765,
    hostname: "127.0.0.1",
    dataPath: defaultDataPath(),
    agentsDir: resolve(process.cwd(), "agents"),
    llmApiKey: process.env.LLM_API_KEY ?? "",
    llmModel: process.env.LLM_MODEL ?? "MiniMax-M2",
    llmBaseUrl: process.env.LLM_BASE_URL ?? "https://api.minimax.chat/v1",
    llmProvider: process.env.LLM_PROVIDER ?? "minimax",
  };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]!;
    const next = argv[i + 1];
    switch (arg) {
      case "--port": if (next) { args.port = parseInt(next, 10); i++; } break;
      case "--hostname": if (next) { args.hostname = next; i++; } break;
      case "--data-path": if (next) { args.dataPath = next; i++; } break;
      case "--agents-dir": if (next) { args.agentsDir = next; i++; } break;
      case "--llm-api-key": if (next) { args.llmApiKey = next; i++; } break;
      case "--llm-model": if (next) { args.llmModel = next; i++; } break;
      case "--llm-base-url": if (next) { args.llmBaseUrl = next; i++; } break;
      case "--llm-provider": if (next) { args.llmProvider = next; i++; } break;
      case "--transcript-dir": if (next) { args.transcriptDir = next; i++; } break;
      case "--llm-config": if (next) { args.llmConfigPath = next; i++; } break;
      case "--enable-director": args.enableDirector = true; break;
      case "--disable-director": args.enableDirector = false; break;
      case "--director-probability": if (next) { args.directorTriggerProbability = parseFloat(next); i++; } break;
      case "--help":
        console.log(`Usage: valley-ai-server [options]

Options:
  --port <number>            WebSocket port (default: 8765)
  --hostname <string>        Bind hostname (default: 127.0.0.1)
  --data-path <path>         Path to npc_prompts.json
  --agents-dir <path>        Directory for agent memory files
  --llm-api-key <key>        LLM API key (or env LLM_API_KEY)
  --llm-model <name>         LLM model name (default: MiniMax-M2)
  --llm-base-url <url>       LLM API base URL
  --llm-provider <name>      LLM provider (minimax|openai|deepseek)
  --transcript-dir <path>    Enable full-trace transcript store in this dir
  --llm-config <path>      Path to multi-provider runtime JSON (enables multi-provider mode)
  --enable-director        Enable narrative director (default: enabled)
  --disable-director       Disable narrative director (NPCs fully autonomous)
  --director-probability <0-1>   Director daily trigger probability (default: 0.1)
  --help                     Show this help
`);
        process.exit(0);
    }
  }

  if (!args.llmApiKey && !args.llmConfigPath) {
    console.error("ERROR: --llm-api-key or LLM_API_KEY env var required (or use --llm-config for multi-provider mode)");
    process.exit(1);
  }

  return args;
}

// 全局异常处理：防止未捕获的 Promise rejection 导致 Bun 进程崩溃。
// 如果未被 try/catch 捕获会变成 unhandledRejection，Bun 默认会终止进程。
process.on("unhandledRejection", (reason) => {
  console.error("[valley-ai-server] unhandledRejection:", reason);
});
process.on("uncaughtException", (err) => {
  console.error("[valley-ai-server] uncaughtException:", err);
});

async function main() {
  const args = parseArgs(process.argv.slice(2));
  mkdirSync(args.agentsDir, { recursive: true });

  console.log(`[valley-ai-server] starting on ${args.hostname}:${args.port}`);
  console.log(`[valley-ai-server] data: ${args.dataPath}`);
  console.log(`[valley-ai-server] agents: ${args.agentsDir}`);
  if (args.llmConfigPath) {
    console.log(`[valley-ai-server] llm: multi-provider mode (${args.llmConfigPath})`);
  } else {
    console.log(`[valley-ai-server] llm: ${args.llmProvider}/${args.llmModel}`);
  }

  const handle = await startServer({
    port: args.port,
    hostname: args.hostname,
    dataPath: args.dataPath,
    agentsDir: args.agentsDir,
    ...(args.transcriptDir !== undefined
      ? { transcript: { enabled: true, dir: args.transcriptDir } }
      : {}),
    ...(args.enableDirector !== undefined ? { enableDirector: args.enableDirector } : {}),
    ...(args.directorTriggerProbability !== undefined ? { directorTriggerProbability: args.directorTriggerProbability } : {}),
    ...(args.llmConfigPath
      ? { llmRouterConfigPath: args.llmConfigPath }
      : {
          llmConfig: {
            provider: args.llmProvider,
            apiKey: args.llmApiKey,
            model: args.llmModel,
            baseUrl: args.llmBaseUrl,
          },
        }),
  });

  console.log(`[valley-ai-server] listening on port ${handle.port}`);

  process.on("SIGINT", async () => {
    console.log("[valley-ai-server] SIGINT received, shutting down...");
    await handle.stop();
    process.exit(0);
  });
  process.on("SIGTERM", async () => {
    console.log("[valley-ai-server] SIGTERM received, shutting down...");
    await handle.stop();
    process.exit(0);
  });
}

main().catch((err) => {
  console.error("[valley-ai-server] fatal:", err);
  process.exit(1);
});
