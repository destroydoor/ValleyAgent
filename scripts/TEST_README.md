# ValleyAgent 测试文档

> **架构现状**：智能层是 TypeScript Agent Server（`valley-ai-server.exe`），
> **源码在本仓 `server/` 工作区**（`packages/core` + `packages/stardew`）。
> C# Mod 通过 `ServerProcessManager` 自动拉起并监护该 exe，无需手动启动服务器。
> 完整架构见根目录 `AGENTS.md`。

## 测试架构

测试分为两个独立层面：

1. **TS Agent Server 单元/集成测试**（位于本仓 `server/`）
   - `packages/core/tests/*.test.ts` — Agent 框架原语（agentLoop、CircuitBreaker、
     LLMProvider、TokenBudget、ToolRegistry、MemoryBackend 等）
   - `packages/stardew/tests/*.test.ts` — Stardew 实现（NPC prompt loader、
     output-validator、protocol-adapter、stardew-tools、stardew-agent、
     world-snapshot-decoder、rule-engine、agent-memory 等）
   - 通过 `bun test` 在 `server/` 工作区运行；`bun run typecheck` 同时检查 src 与 tests

2. **游戏集成测试**（位于 `src/ValleyAgent.TestMod/`）
   - 模拟玩家点击 NPC、送礼、对话等场景，验证 C# 执行层 + TS Agent Server
     全链路（WebSocket 协议、tool_calling、记忆持久化、状态同步）
   - 由 `V3TestRunner` 驱动，分组：`Fuzzy` / `Edge` / `Functional` / `Pipeline` /
     `Experience` / `ComplexScenario` / `Visual`（`Narrative` 随旧叙事 Director 砍除）
   - 通过 SMAPI 加载 TestMod，由标记文件 `auto_orchestrator_run.flag` 触发

## 测试方式

### TS Agent Server 测试（在本仓 `server/` 目录）

```bash
cd server
bun install
bun test                       # 运行所有 core + stardew 单元测试
bun run typecheck              # 类型检查：src + 两个包的 tests
bun run check:protocol         # C# ↔ TS 协议契约静态检查
```

### 游戏集成测试（在本仓库）

```powershell
# 一键运行（构建 + 部署 + 启动 SMAPI + 拉起 valley-ai-server.exe + 跑测试）
powershell -ExecutionPolicy Bypass -File scripts\test\run-tests.ps1 -Group All

# 跳过构建（已构建过）
powershell -ExecutionPolicy Bypass -File scripts\test\test-game.ps1 -NoBuild

# 使用本地 LM Studio 而非 MiniMax 云端
powershell -ExecutionPolicy Bypass -File scripts\test\run-test-with-env.ps1 `
    -BaseUrl http://127.0.0.1:1234/v1 `
    -ApiKey lm-studio `
    -Model qwen-3.6-27b
```

## 游戏测试场景（v4.3 简化）

| 场景 | 触发工具 | 验证点 |
|------|---------|--------|
| 玩家点击 NPC 对话 | `dialogue` WS 消息 | C# → TS server LLM → tool_calling → speak/emote → 显示对话 |
| NPC 主动说话 | `show_dialogue` 工具 | NPC 主动发起对话气泡/对话框 |
| 玩家送礼给 NPC | `NPCGiftPatch` 拦截 | C# 端直接计算 giftTaste，记录记忆，TS 后续看到更新后的 friendship |
| NPC 送礼给玩家 | `give_gift` 工具 | NPC 从背包扣物品给玩家，记录 GiftGiven 情绪 |
| 工具调用反馈 | `tool_call_result` WS 消息 | C# 执行工具后回传结果给 TS server（C3 反馈环） |
| 多 NPC 隔离 | per-NPC Agent + Memory | 不同 NPC 上下文独立、记忆文件独立（`agents/{npc}_memory.json`） |

## 四维评估标准

### 1. 提示词系统
- [x] NPC prompt 包含角色性格/传记/特质（`data/npc_prompts.json`，33 个 NPC）
- [x] 好感度分层态度（stranger/acquaintance/friend/close/partner 五阶段）
- [x] 第一人称视角指令（`prompt-builder.ts`）
- [x] 工具调用反馈注入系统 prompt（`toolResultQueues`）

### 2. 记忆/上下文系统
- [x] 短期记忆 + 长期记忆 + 重要事项记忆（`AgentMemory` 实现 `MemoryBackend`）
- [x] 每次对话后持久化（`agents/{npcName}_memory.json`）
- [x] 对话历史影响决策（注入 system prompt）
- [x] 记忆按 NPC 名隔离

### 3. 运行时系统
- [x] ReAct 循环（Vercel AI SDK v5 原生 tool_calling，THINKING 状态已移除）
- [x] 熔断保护（`CircuitBreaker`，5 次/30 秒阈值）
- [x] 输出验证（`output-validator.ts`，CJK 比例 ≥ 0.3）
- [x] 兜底机制（`rule-engine.ts`，LLM 失败时生成中文人设化兜底 speech）
- [x] 进程级容错（`ServerProcessManager` watchdog + 指数退避重启，上限 3 次）

### 4. 工具链
- [x] speak / emote / give_item / give_gift / set_state / show_dialogue /
      remember / forget / get_info — 9 个 TypeBox schema 工具
- [x] 工具结果反馈环（`tool_call_result` 消息，上限 10 队列）
- [x] per-NPC 对话锁（同 NPC 串行）

## 已知问题

### MiniMax 推理模型兼容性
- MiniMax M2.7 是推理模型，输出内部推理 token
- 启用 `enable_tool_calling=True` 时额外 prompt 会干扰 JSON 输出
- 当前默认使用 `MiniMax-M3`（非推理模型），可避免该问题
- 修复方向：精简 tool prompt，不干扰 JSON format

### WebSocket 测试超时
- MiniMax API 响应慢（5-15s）
- 端到端 WS 测试需要本地 LLM（如 LM Studio）或云端 MiniMax
- `ServerProcessManager` 默认 `StartupTimeoutSeconds=60` 应足够

### TS Agent Server 调试
- 配置 `ServerConsoleWindow=true`（默认）可看到 valley-ai-server.exe 的 stdout/stderr
- 配置 `ServerConsoleWindow=false` 则输出重定向到 SMAPI 日志
- 服务器崩溃日志在 SMAPI 日志中可见（`OnCrashed` 事件）
