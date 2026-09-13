# ValleyTalk Scripts

Scripts for building, testing, and managing the ValleyTalk project.

> **Architecture note (v4.3+)**: The intelligence layer is the TypeScript
> Agent Server (`valley-ai-server.exe`, source at `<VALLEYAI_ROOT>`). The
> C# mod's `ServerProcessManager` auto-launches and supervises the exe — no
> manual server start is needed. See `AGENTS.md` for the full architecture.

## Directory Structure

```
scripts/
├── README.md              ← This file
├── secrets.local.ps1      # VALLEY_LLM_* env vars (gitignored — local only)
├── results/               ← ALL test output goes here (timestamped files)
├── build/
│   ├── build-all.ps1      # Build ValleyAgent + TestMod (+ valley-ai-server.exe via deploy.ps1)
│   ├── deploy.ps1         # Copy DLLs + valley-ai-server.exe + npc_prompts.json to Mods
│   └── build-deploy.ps1   # Combined build + deploy
├── test/
│   ├── test-game.ps1      # Build + deploy + launch game + capture results
│   ├── run-tests.ps1      # Unified runner (drives TestOrchestrator via marker file)
│   ├── run-test-with-env.ps1  # Wrapper that injects VALLEY_LLM_* for local LM Studio
│   ├── run-minimax-test.ps1
│   ├── run-game-runtime.ps1
│   ├── launch-test.ps1
│   ├── run_farmhand_e2e.ps1
│   └── analyze-multiplayer-logs.ps1
├── screenshot_window.ps1  # BMP screenshot (legacy — do not modify per project rules)
├── screenshot_desktop.ps1
├── resize_window.ps1     # BMP-related (legacy — do not modify)
└── utils/
    ├── clean-logs.ps1    # Archive old SMAPI logs
    └── status.ps1        # Show project status (build/game/Agent Server)
```

> Removed in v4.3: `scripts/server/start-server.ps1`, `scripts/test/test-python.ps1`,
> `run-python-tests.bat`, `run-all-tests.bat` (the Python server has been deleted;
> the TS Agent Server is launched by the mod itself).

## Quick Start

### One-line test run (game integration only)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\test\run-tests.ps1 -Group All
```

This will:
1. Build ValleyAgent + TestMod
2. Deploy DLLs + `valley-ai-server.exe` + `data/npc_prompts.json` to the Mods folder
3. Drop an orchestrator marker file (`auto_orchestrator_run.flag`) telling the
   TestMod which test group to run
4. Launch SMAPI — the mod's `ServerProcessManager` will auto-start
   `valley-ai-server.exe` and the test orchestrator will run the requested group

### Manual execution

```powershell
# Game tests (build + deploy + launch SMAPI)
powershell -ExecutionPolicy Bypass -File scripts\test\test-game.ps1

# Build only
powershell -ExecutionPolicy Bypass -File scripts\build\build-all.ps1

# Deploy only (no build)
powershell -ExecutionPolicy Bypass -File scripts\build\deploy.ps1

# Local LM Studio run (overrides LLM endpoint)
powershell -ExecutionPolicy Bypass -File scripts\test\run-test-with-env.ps1 -BaseUrl http://127.0.0.1:1234/v1 -ApiKey lm-studio -Model qwen-3.6-27b

# Project status (build state, game running?, Agent Server listening?)
powershell -ExecutionPolicy Bypass -File scripts\utils\status.ps1
```

## Test Output

All test results are saved to `scripts/results/` with timestamps:

```
scripts/results/
├── game-test-2026-07-26_143022.txt
├── deploy-2026-07-26_143015.txt
└── unified-test-2026-07-26_143022.txt
```

**Subagent access**: Reading results from `scripts/results/` is low-context-cost
since filenames encode timestamps and test type.

## Test Categories

### Game Tests (slow, 2-10min, requires game + LLM)
- TestMod in-game SMAPI console commands
- Full integration test of C# mod + TS Agent Server (`valley-ai-server.exe`)
- The TS server is auto-launched by `ServerProcessManager` based on `ModConfig`
- Groups: `Fuzzy` / `Edge` / `Functional` / `Real` / `Narrative` / `Visual` / `All`
- Commands: `vat_auto` (all phases), `vat_status`, `vat_abort`
- Run: `scripts\test\run-tests.ps1 -Group <Group>` or `scripts\test\test-game.ps1`

### TS Server Unit/Integration Tests (run inside <VALLEYAI_ROOT>)
- `packages/core/tests/*.test.ts` — Agent framework primitives
- `packages/stardew/tests/*.test.ts` — Stardew-specific (NPC, protocol, validator)
- Run via `bun test` in the `<VALLEYAI_ROOT>` workspace
- These tests are **not** invoked from ValleyTalk scripts — they belong to the
  ValleyAI repo

## Result File Format

### Game test result file
```
=== Test Report ===
[Phase 1] Cleanup         [PASS]
[Phase 2] Greeting         [PASS]
...
=== End of Report ===
Passed: 23/32
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `STARDW_PATH` | `<REPO_ROOT>\Stardew Valley` | Game installation path |
| `VALLEY_LLM_PROVIDER` | `minimax` | TS server LLM provider (`minimax`/`openai`/`anthropic`/`google`/`deepseek`/`openrouter`/`lmstudio`) |
| `VALLEY_LLM_BASE_URL` | `https://api.minimax.chat/v1` | OpenAI-compatible base URL |
| `VALLEY_LLM_API_KEY` | — | LLM API key (set in `secrets.local.ps1`) |
| `VALLEY_LLM_MODEL` | `minimax-M3` | Model ID |

> Note: The C# `ServerProcessManager` reads these from `ModConfig` and passes
> them as CLI args (`--llm-api-key` / `--llm-model` / `--llm-base-url` /
> `--llm-provider`) to `valley-ai-server.exe`. The TS `cli.ts` also reads
> `LLM_API_KEY` / `LLM_MODEL` / `LLM_BASE_URL` / `LLM_PROVIDER` env vars as
> fallbacks.

## For Subagents

Subagents should read results from `scripts/results/` using the timestamp in the filename:
- Latest game result: most recent `game-test-*.txt` in `scripts/results/`
- Latest combined report: `unified-test-*.txt` (generated by `run-tests.ps1`)
- Deployment log: `deploy-*.txt` (generated by `deploy.ps1`)
