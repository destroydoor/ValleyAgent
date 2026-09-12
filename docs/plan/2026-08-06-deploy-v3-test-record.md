# 部署 + 游戏内 V3 测试记录 — 三阶段合入后首次全链路验证

> **Created:** 2026-08-06 21:15-21:23
> **范围:** 三阶段（交易/GoalExecutor/Director）合入 master 后的首次真实游戏部署与运行
> **结果:** mod 加载 ✓、valley-ai-server 拉起 ✓、V3 自动化测试 67 pass / 15 fail（15 fail 全为环境时序问题）

---

## 1. 部署清单

| 步骤 | 内容 | 结果 |
|---|---|---|
| 1 | `bun build --compile --target=bun-windows-x64 src/cli.ts --outfile bin/valley-ai-server.exe`（ValleyAI packages/stardew） | ✓ 117,158,400 B（2026/8/6 21:14，含三阶段 TS 改动；此前 exe 是 8/4 的旧版） |
| 2 | `dotnet build src/ValleyAgent/ValleyAgent.csproj -c Debug` | ✓ 成功 0 警告（3.6s） |
| 3 | `deploy.ps1 -SkipBuild` | ✓ exe + npc_prompts.json + DLL + i18n/RAG/assets + Autopilot 全部部署 |
| 4 | LLM key 验证 | ✓ minimax `sk-cp-***`（已打码）有效（curl 返回内容）；✗ deepseek `sk-***`（已打码）**401**（opencode 网关令牌，非原生 key） |
| 5 | config.json 修正 | 全 provider 统一 minimax（Npc/Director/Protagonist primary+fallback 全部 minimax/MiniMax-M3） |
| 6 | 启动 | `test-game.ps1` 拉 SMAPI（PID 4584），运行 360s 后正常退出 |

## 2. V3 测试结果（logs/test_results/20260806_211535/_summary.json）

**67 pass / 15 fail / 0 skip，20 个测试**

### 三阶段相关正面证据

- `[F4] Registered states: IDLE, FOLLOW, FIGHT, FARM, MINE, FORAGE, TALK, EXECUTING_GOAL, TRAVELING_TO_REPORT` — **阶段 2 两个新状态在游戏内生效** ✓
- `[DirectorContext] built (717 tokens)` — **阶段 3 DirectorContextBuilder 真实运行** ✓（717 tokens，低于 800 目标但功能正常）
- IT06 送礼（tulip 进玩家背包）✓ / IT07 fallback 台词 ✓ / IT08 背包满掉地 ✓ / IT09 砍树（tree_removed_from_terrain）✓ / IT10 跟随无好感度 ✓
- F5 双 agent WebSocket 路由 ✓ / F1-F3 随机漫步/怪物/掉落 ✓

### 15 个失败的统一根因：**IdleEviction 时序问题**

- 证据：日志反复出现 `[IdleEviction] Haley evicted (no player interaction for 90s)` → `[Api] TrySetAgentState(Haley, XXX): agent not found` → 后续测试读 `agent.State` 得到**空字符串** → 断言失败
- 测试跑 360s，期间 Haley/Abigail 被 90s 无交互逐出多次，轮到它们的测试全部 `agent not found`
- **不是三阶段代码缺陷**：被逐出的 NPC 本来就不是 agent，读不到状态是预期行为；测试没有为长跑会话维持 agent 分配

| 失败测试 | fail 数 | 根因 |
|---|---|---|
| F4_RandomDecisions | 2 | 随机转换 7 次失败 + 状态读空（agent evicted） |
| F_FollowCrossMap | 4 | npc=Farm 卡农场（跨地图跟随期间 agent 被逐出） |
| F_MineRealCombat | 5 | TrySetAgentState(MINE)=False（agent not found）+ 石头/怪物未动 |
| IT02_StateChanged | 1 | state= 读空 |
| IT03_TravelFailureRecovery | 1 | state_transitions_to_idle state= 读空 |
| IT05_TravelCircuitBreaker | 1 | state_still_readable state= 读空 |
| IT09_ChopTree | 1 | state_still_readable state= 读空 |

## 3. 遗留问题

1. **V3 测试集不含三阶段专项测试**——trade 结算、set_goal 砍树汇报、Director 工具(spawn_beat/改状态) 没有游戏内自动化覆盖，需补 TestMod 用例或手动验证
2. **deepseek key 失效**（401）——若要用 deepseek 需换原生 key；当前全 minimax 可用
3. **IdleEviction 与长测试会话的冲突**——测试框架应保持测试目标 NPC 分配，或在测试期禁用 eviction
4. **DirectorContext 717 tokens 低于 800 目标**——Compress 有 min-800 不强凑说明，可接受；若需精确可调

## 4. 部署后工作区状态

- `git checkout -- "Stardew Valley/Mods"` 还原部署产物（构建产物不进 commit）
- `config.json` 不在 git（gitignore）——minimax 运行配置保留在磁盘，安全
- 两个仓库 master 工作区干净
