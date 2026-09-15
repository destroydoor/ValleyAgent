# 文档 ↔ 实际 偏移度审查报告（2026-09-15）

> **性质**：只读审查。审查过程中**未修改**任何被审文档或代码（`git status` 干净；仅本地安装了 bun 并在 `server/` 跑了 `bun install`，未改动受版本控制的文件）。
> **审查范围**：`README.md`、`AGENTS.md`（权威指南）、`docs/**` 90 篇 md、`scripts/README.md`、`scripts/TEST_README.md`、`docs/README.md` ↔ 实际代码（C# 35,641 行 / TS 6,563 行 / 协议 `server/protocol/messages.json` / 构建脚本 / 配置 / 测试）。
> **方法**：把文档里的可证伪声明逐条抽出来，用「grep 符号存在性 + 构建/测试实跑 + 协议静态检查」取证；不可证伪或本环境不可达的项**显式标注为未核实**，不与结论混写。

---

## 0. 偏移度总览

| 面 | 核对项 | 偏移项 | 偏移率 | 定性 |
|---|---:|---:|---:|---|
| **A. 权威文档 `AGENTS.md`** | 46 | 9 | ~20% | **方向准确、状态标注滞后**：无一处架构方向性错误，9 条全是「已完成被写成规划中 / 已删除仍写成现状 / 清单遗漏」 |
| **B. 其他文档（design/plan/README）** | 9 条专项 | 9 | — | **文档间互相打架**：同一事实在不同文档里状态不同（实施中 vs 已完成）；测试数字过期 |
| **C. 代码侧残留（文档说已清理）** | 7 类 | 7 | — | **清理未收尾**：删了模块没删引用，删了协议没删测试，删了子系统没删配置 |
| **D. 机械性（链接 / 占位符 / 索引）** | 2 类 | 2 | — | **索引层严重滞后**：`docs/README.md` 只覆盖 14/74 篇非归档文档，另有 2 条真死链、697 处未替换占位符 |
| **E. 质量门禁可信度** | 3 门 | 1 门失效 | — | `check:protocol` / `typecheck` 真通过；**`bun test` 全绿是假象**（测试目录根本不参与类型检查，见 C1） |

一句话结论：**AGENTS.md 的架构叙事与代码主体是对得上的（80% 命中），但它的"状态标注"层停留在 2026-08-15 之前，同时仓库里还留着 8 类"文档说已删/未接线、代码里仍在"的残留。**

---

## 1. 表 A：`AGENTS.md` 偏移（9 条）

| # | 位置 | 文档说法 | 实际 | 证据 |
|---|---|---|---|---|
| A1 | `AGENTS.md:36`（§2 第 6 条） | 账本迁移「（2026-08-15 修订，**迁移未开始**）」 | 四步迁移**已全部落地** | 同文件 `:6` 自述"四步全部完成"；`src/ValleyAgent/Economy/AdjustExecutor.cs`（33 KB）、`server/packages/stardew/src/agent-ledger.ts`、`emotion-engine.ts`、`protocol-adapter.ts` `adjustEconomy()/handleReconnectSync()` 均在生产路径 |
| A2 | `AGENTS.md:71`（§3 架构图） | `adjust 执行器: …（规划）` | 已实现并被单测/IT 覆盖 | `Economy/AdjustExecutor.cs`；`ValleyAgent.UnitTests/AdjustExecutorTests.cs`；IT06/IT08/IT11/IT14 |
| A3 | `AGENTS.md:72` | `断线 Outbox: 事件缓存+重连补发（规划）` | 已实现 | `ValleyAgent.Abstractions/WebSocket/WebSocketClient.cs`（outbox 补发）、`ValleyAgent.UnitTests/WebSocketOutboxTests.cs`、`EventHandlerInitializer.cs:1257 OnReconnected` → `reconnect_sync`（协议 active） |
| A4 | `AGENTS.md:150`（§4 索引） | 账本设计「最新，**步骤 1 已落地**」 | 四步全落地 | 与 `AGENTS.md:6` 直接冲突（同一文件两处口径） |
| A5 | `AGENTS.md:141`（§3.7 M3 条）③ | 「新增 `PlayerDirectory`」「`Director.morningPlan({ playerIds })` 按玩家各取画像各调一次 LLM…」写成**现状** | 两者已于 2026-09-14 砍除，**模块文件不存在** | `server/packages/stardew/src/player-directory.ts`、`director.ts` 均不存在；`player-directory` 全仓唯一引用是测试 `tests/m3-multiplayer.test.ts:17`（悬空 import） |
| A6 | `AGENTS.md:143`（§3.7 已知限制） | 「玩家名录是内存观察式的…**当天 morningPlan 退化为单玩家编排**」 | 该机制已不存在（名录/导演已删），此限制**描述的是已删除功能** | 同 A5；`docs/plan/…` 无对应条目 |
| A7 | `AGENTS.md:120`（§3.4 工具集） | 列出 13 个 NPC 工具 + evaluate_friendship | 实注册 **15 个** LLM 可见工具，**漏了 `chop_tree`** | `server/packages/stardew/src/stardew-tools.ts:316` `name: "chop_tree"`（运行日志可见 `tools=15`） |
| A8 | `AGENTS.md:110`（§3.2 L3 行） | L3 行划删除线「随旧叙事 Director 砍除」 | C# 侧 **L3 通路仍在**：`WorldSnapshotBuilder.cs:100` 仍读 `BeatStore.Current?.GetActiveBeat(npcName)?.SceneDesc` 注入 worldSnapshot；`spawn_beat/spawn_group_beat` 仍注册 | 与同文件 `:126`「工具层保留为就绪层」口径不一致（§3.2 说没了，§3.5 说保留） |
| A9 | `AGENTS.md:3`（banner） | 「审计底稿见 arena 分支 `docs/plan/2026-09-12-architecture-drift-audit.md`」 | **该文件在任何分支都不存在**（"arena 分支"亦未随仓交付） | 三处引用同一缺失文件：`AGENTS.md:3`、`narrative-types.ts:3`、`server.ts:123` → 支撑"删了 5,600 行"的证据链断档 |

> 附注（不计入偏移）：`AGENTS.md:118-126` 关于"旧叙事 Director 砍除、C# 9 个 DirectorTools + `director_command` 通道保留"的说法**与代码完全一致**（`Commands/DirectorTools.cs`、`ServiceInitializer.cs:443`、`EventHandlerInitializer.cs:1412`）——即 A5/A6 是"忘了改 M3 那一段"，不是架构理解错误。

---

## 2. 表 B：其他文档偏移（9 条）

| # | 位置 | 偏移 |
|---|---|---|
| B1 | `docs/design/2026-08-05-three-tier-architecture-redesign.md:85` | 仍写着「**C# 是唯一的账本与状态持有者**：钱包/背包/状态机在 C#」，**没有"已被 2026-08-15 设计推翻"的标注**。而 `AGENTS.md:149` 把它列为"基础架构依据"——读者沿 AGENTS 索引进来会拿到已被推翻的账本模型。（2026-08-15 文档单向引用它，反向没有回链） |
| B2 | `docs/design/2026-08-17-ledger-fixes-and-m2-multiplayer-context.md:4` | 状态写「计划已批准，**实施中**」，而 AGENTS 与代码均表明 M2 已完成（`player_profile` 分键、记忆双桶、`legacyMigrated` 均在） |
| B3 | `docs/design/2026-09-13-m3-multiplayer-director-emotion.md:5` | 状态写「已落地（TS 全绿；**C# 仅语法级校验，实机联机验证待做**）」，AGENTS 转述时丢掉该 caveat，直接写成「M3 已完成…三项遗留限制全清」——**验收口径被放大** |
| B4 | `docs/plan/2026-08-20-test-system-redesign.md:13,108` | 测试计数过期：「C# xUnit（580 测）+ ValleyAI bun test（**628** 测）」。今日实测 TS = **580 pass**；C# 源码为 560 `[Fact]` + 21 `[Theory]`（97 组 InlineData），最近报告称 657 通过 |
| B5 | `docs/README.md` | ①`:12` 死链 `../TS服务器重写-上下文交接.md`（文件不存在）②`:3` 仍标"当前架构 v4.3" ③**索引只覆盖 14/74 篇非归档文档**（design/plan/ideas/superpowers/spec_*/思路 几乎全缺，含全部 2026-08/09 新设计）④把 TS 描述为外部仓库 `<VALLEYAI_ROOT>` |
| B6 | `scripts/TEST_README.md:3,17-30,45` | 同 B5④：TS 测试"位于 `<VALLEYAI_ROOT>`"（实际在本仓 `server/packages/*/tests`）；测试分组仍列 `Narrative`（已随导演砍除）；用例示例仍是 LM Studio 时代产物 |
| B7 | `scripts/README.md` | 目录树列出 `screenshot_window.ps1` / `screenshot_desktop.ps1` / `resize_window.ps1`，**三个文件在仓库中都不存在**（且没有像 `start-server.ps1` 那样标注"v4.3 已移除"） |
| B8 | `docs/plan/2026-09-14-dead-assertion-cleanup-plan.md:1,12` + `…report.md:1` + 提交 `aed4995` 标题 | 全部把检查器称作 `check:test-dead`，但**仓库里没有任何地方定义这个命令**（真实调用是 `node scripts/check-dead-assertions.mjs`，无 npm/bun 脚本别名） |
| B9 | `README.md` 仓库结构表 | 漏了真实存在且被大量引用的 `src/ValleyAgent.Abstractions/`（协议 DTO、WebSocketClient、outbox 都在这）；也漏了 `src/ValleyTalk.ApiTest/` 与根级前端目录（见 C5/C6） |

---

## 3. 表 C：代码侧残留（文档说已清理 / 已接线，实际还在）

### C1（最高价值）TS 测试目录长期不参与类型检查，26 个类型错误常驻，"全绿"是假象

- 根 `server/tsconfig.json` 只 `include: ["packages/*/src/**/*.ts"]` → `bun run typecheck` **永远不查测试**。
- 用包级配置实跑（`bunx tsc -p packages/stardew/tsconfig.json`，该配置 *含* tests）：**26 个错误 / 4 个测试文件**

| 文件 | 错误 | 内容 |
|---|---:|---|
| `tests/types-extended.test.ts` | 10 | 引用已删类型 `Beat` `BeatStatus` `ReActStep` `ActivityReportMessage` `ActivityMilestoneMessage` `BeatDirectiveMessage` `BeatActivateMessage` `BeatEventMessage` `BeatStateMessage` `PlayerStateUpdateMessage` → **这 10 个测试断言的是不存在的类型，等于空跑**（如 `expect([...]).toHaveLength(5)`） |
| `tests/m3-multiplayer.test.ts` | 10 | 3 个悬空 import（`../src/player-directory`、`../src/director`、`../src/beat-store`，模块已删）+ 7 个未使用变量 |
| `tests/transcript-wiring.test.ts` | 4 | `Beat` 已不在 `types.ts` 导出 + 3 个未使用类型 |
| `tests/dialogue-e2e.test.ts` | 2 | `ServerConfig.enableDirector` 已随导演删除（`server.ts:31-45` 无此字段） |

- **为什么 `bun test` 仍然是 580 pass / 0 fail**：bun 转译时丢弃未使用的 import（这些 import 恰好都没被使用），所以悬空模块不会在运行时炸。
- 结论：文档 §5「验证门槛：`bun test` + `tsc --noEmit` + `check:protocol` 全绿」在字面上成立，但**其"全绿"无法证明测试代码与代码现实一致**——这正是本次审查里最值得修的偏移（也是 `docs/plan/2026-09-14-dead-assertion-cleanup-report.md` 声称"281 条候选全部结案"时漏掉的一类死断言：TS 侧的类型编译型死测试）。

### C2 C# 侧仍在驱动已被砍除的导演

- `src/ValleyAgent.TestMod/Tests/Integration/DIR_DirectorBehaviorRecord.cs` 仍注册在 `V3TestRunner.cs:724`：Setup 写 `config.Director.TriggerProbability = 1.0`，Update 轮询等待 `"[director] … morningPlan end"` / `"[send] allocate_agent"` 日志——**这些日志永远不会再出现**（TS 侧 sender 已删），该测试每轮必然跑到 `MaxWaitTicks` 才收尾并产出空报告。
- 它还是 `ModConfig.Director.TriggerProbability` 的**唯一读者**：即一个死配置项被一个死测试保活。

### C3 C# 单测仍在序列化"未接线"的协议

- `src/ValleyAgent.UnitTests/Tracking/ActivityTypesSerializationTests.cs:245` 构造 `type = "activity_report"`。该协议**不在 `messages.json`**（AGENTS 也自述"未接线"）——测试在为不存在的链路背书。
- TS 侧同一现象：`tests/types-extended.test.ts:263` 也构造 `activity_report`（见 C1）。

### C4 20 个配置项零读者（修复期复核后修正为 A/B 两类，见下方口径说明）

`src/ValleyAgent/Config/ModConfig.cs` 共 **150** 个属性；脚本扫描"除 `ModConfig.cs` / `GMCMIntegration.cs` / `LanguageValidation.cs` 外无任何 C# 引用"的属性，命中 **20** 个：

```
Python 时代遗留（5）：AutoStartPythonServer、PythonExecutablePath、PythonServerDirectory、
                      PythonServerStartupTimeoutSeconds、PythonServerMaxRestartAttempts
其余（15）：ServerAddress、DevMode、DialogueTemperature、FallbackAIMixProbability、
            FallbackLiveGenerationProbability、CustomSystemPrompt、AIDailyTopicCount、
            StateRejectionCooldownTicks、MaxStateRejections、
            Haggle / MaxRounds / HostileThreshold（还价子系统，结算链已删）、
            ModelName、DebugLogEnabled、TodayEventsMaxCount
```

> 口径说明：脚本按"属性声明名在除 `ModConfig.cs`/`GMCMIntegration.cs`/`LanguageValidation.cs` 外的所有 C# 文件中零命中"判定，嵌套配置类（`Haggle.MaxRounds`/`Haggle.HostileThreshold`）按属性名独立计数，故此 20 个名字里 `Haggle/MaxRounds/HostileThreshold` 属同一子系统（真实"死子系统"数为 18 个）。
> 另：`Director.TriggerProbability` 因 C2 成为"只被死测试读"的第 21 个。GMCM 面板仍在渲染其中多项，例如 `GMCMIntegration.cs:786,954,1042,1138,1276`。
>
> **修复期复核（2026-09-15 同日）**：这 20 个名字**不是同一性质**，动手前必须分类——
> - **A 类「刻意的兼容垫片，勿删」**：`AutoStartPythonServer` / `PythonExecutablePath` / `PythonServerDirectory` / `PythonServerStartupTimeoutSeconds` / `PythonServerMaxRestartAttempts`（均已 `[Obsolete]`，只在 `ModConfig.MigrateLegacyFields()` 内被读，包在 `#pragma warning disable CS0618` 中）+ `ModelName`（迁移进 `LlmModel`）+ `DialogueTemperature`（存档兼容注释在案）。删除它们等于放弃旧 `config.json` 迁移，且需同步 `scripts/docker/prep-mods.ps1:97-98`（该脚本会写 `AutoStartPythonServer=false`）。
> - **B 类「真孤儿，待摘除」**：`ServerAddress` / `DevMode` / `DebugLogEnabled` / `CustomSystemPrompt` / `AIDailyTopicCount` / `StateRejectionCooldownTicks` / `MaxStateRejections` / `FallbackAIMixProbability` / `FallbackLiveGenerationProbability` / `TodayEventsMaxCount` / `Haggle.{Enabled,MaxRounds,HostileThreshold}`。除 `ServerAddress`、`TodayEventsMaxCount` 外**都还在 GMCM 面板上可改可存**，改了什么也不会发生。
> - 摘除属**需编译验证的 C# 改动**（本环境无 dotnet，未执行）；已在 `ModConfig.cs` 顶部与 `AGENTS.md §3.6` 登记名单与摘除顺序，避免后续误删 A 类 / 误信 B 类。

### C5 `src/ValleyTalk.ApiTest/` 是 Python/本地 LLM 时代的独立 provider 测试台

`Program.cs` 默认 `providerType = "lmstudio"`、`http://localhost:1234`；README 结构表、AGENTS 文档索引均未收录；仅历史文档（`docs/archive/TESTING.md:316`、2026-08-03 的记录）提到它。

### C6 根级前端是"孤儿工程"

`src/App.tsx`、`main.tsx`、`store.ts`、`index.css`、`vite-env.d.ts`、`components/{Header,Timeline,StagePanel,Legend,CommitModal,Empty}.tsx`、`pages/Home.tsx`、`hooks/`、`lib/`、`data/commits.ts`：

- 仓库**没有**根 `package.json` / `vite.config.*` / `tsconfig.json` / `index.html`（全仓 `find -name index.html` 零命中）→ 无法构建；
- `src/data/commits.ts:19` `import rawCommits from "../../git_history.json"`，该文件不存在且被 `.gitignore:71` 忽略；
- 全仓 md/json/csproj **零引用**这套组件；git 历史只有 `aed4995` 一个提交，看不到来历。

### C7 `CompatibilityStubs.cs`：为已删子系统保留的兼容壳

`src/ValleyAgent.TestMod/CompatibilityStubs.cs` 保留 `GiftSystem`、`AIDecisionEngine`、`GiftEvaluationContext/Result`、`GiftItem` 等桩类（`EmotionAnalyzer`/`DialogueMemoryAnalyzer` 的注释也在此），文档从未描述这层"兼容壳"的存在与去留期限。

---

## 4. 表 D：机械性问题

| # | 问题 | 数字 | 分布 |
|---|---|---:|---|
| D1 | md 内本地链接失效 | **73 / 115**：其中 **71 条是未替换模板占位符** `file:///<REPO_ROOT>/…`、`file:///<VALLEYAI_ROOT>/…`，**2 条是真死链** | 占位符集中在 `docs/v4.1-refactor-progress.md`(51)、`docs/design/2026-08-03-ts-server-logging-design.md`(12)、`docs/superpowers/specs/2026-08-03-interaction-driven-agent-allocation-design.md`(8)；真死链：`docs/README.md:12`，以及 `docs/superpowers/plans/2026-07-21-narrative-director.md`（目标文件其实存在，只是用占位符形式指向） |
| D2 | 未替换占位符 | `<VALLEYAI_ROOT>` **431 处 / 25 个文件**；`<REPO_ROOT>` **266 处 / 22 个文件** | 根因：文档按"ValleyTalk + ValleyAI 双仓库"时代撰写；而今 TS 已并入本仓 `server/`。**脚本侧已经改对了**（`scripts/lib/paths.ps1:57-69` 优先用 `<repo>/server`，`<env:VALLEYAI_ROOT>` 仅作 override），只有文档没跟上 |

---

## 5. 核对通过项（文档可信的部分，审 46 条，命中 37）

**协议层（与 `messages.json` / 两端代码一致）**
1. 协议 17 条消息的 status 与代码实测一致：13 active / 3 orphan_route / 1 planned。
2. `check:protocol` 实跑 **PASS（exit 0）**：`DEAD_PIPELINES 0`、`SCHEMA_DRIFT 0`、`ORPHAN_ROUTES 3`（`route_shout` / `director_command` / `execute_adjust`，均为"TS 路由、C# 从不发"告警不失败）。
3. `route_shout` 仍为 `orphan_route` 预留（AGENTS §3.3 说法成立）。
4. `consolidate_day` 已降级 `planned`，C# 发送端确已删除（`protocol-adapter.ts:742` 注释 + 无 sender）。
5. 声明已清理的 10 条死 schema（beat_plan/beat_start/beat_end/emotion_sync/memory_sync/decision/gift_eval/rag_query/state_sync/day_end）**确实不在** `messages.json` 中。
6. `execute_adjust` / `adjust_result` / `reconnect_sync` 三件套在两端都有实现（含 `instructionId`、`playerNotFound`、幂等缓存）。

**保留层（AGENTS §3.5 的"就绪层"说法成立）**
7. 9 个 Director 工具齐全：`set_npc_position/inventory/money/mood/recent_events/working_on` + `spawn_beat/spawn_group_beat/inject_memory`（`Commands/DirectorTools.cs`）。
8. `director_command` 通道完整：`ServiceInitializer.cs:443` 注册 → `EventHandlerInitializer.cs:1412` 路由 → `CommandExecutor`。
9. `DirectorContextBuilder` 每日推送保留（`AI/DirectorContextBuilder.cs`）。
10. `BeatStore` 与其消费方（`WorldSnapshotBuilder.cs:100`）在容器中注册、被 IT13 / `DirectorToolsTests` 覆盖。
11. `allocate_agent` 的 C# 接收入口与主线程队列健在（`EventHandlerInitializer.cs:84,1689`）。

**联机 M1/M2/M3/审计修复**
12. `AdjustExecutor` 按 `playerId` 解析目标 Farmer + `playerNotFound` 失败码（IT14 存在）。
13. 主线程纪律符号齐备：`HostRequestHandlers.ProcessMainThreadActions`、`EnqueueMainThread`（14 处引用）。
14. 房客 transport `requestId` 精确配对（`RequestId` 112 处引用）。
15. 主体池语义：`AgentAllocationManager` + `KeepUntil`（66 处）+ 空闲回收 + `OnAgentDeallocated`。
16. `EmotionEngine` 确为 `(npcName, playerId)` 双键 + 单调 `seq` "最近写入胜出"（`emotion-engine.ts:11-78`）。
17. `ChatBarRouter.InitializeFarmhand` 存在，房客 tick 排水补齐。
18. `GiftTradeMenuLogic.ShouldOfferGiftTradeMenu` 存在且单测覆盖（`GiftTradeMenuLogicTests.cs:18`）。
19. `player_profile` 分键 + `LEGACY_PLAYER_ID` 惰性认领 + `legacyMigrated` 迁移守卫均存在。
20. 房客限制"不支持喊不在场的 NPC"在代码里确为显式约束（`ChatBarRouter.cs:158`）。

**已删项的删除事实成立**
21. `EmotionAnalyzer` / `DialogueMemoryAnalyzer` 仅存于注释与桩说明（无实现类）。
22. `runBeat` 从未接入生产（仅注释与测试名），`director.ts` / `beat-store.ts` / `player-directory.ts` 在 TS 侧确已不存在。
23. `RuleBasedDecisionEngine` 仍在（AGENTS 说的是"瘦身为生存反射"而非删除）——描述准确。

**C# 行为细节**
24. `ClientWebSocket.Options.Proxy = null` 带"必须显式置空"注释（`WebSocketClient.cs:101,108`）。
25. `config.json` 未作为 csproj 部署项（`grep config.json src/ValleyAgent/ValleyAgent.csproj` → 0 命中），与已知坑一致。
26. L2 `todayEvents` 保留近 3 天并在 `day_started` 清理（`EventHandlerInitializer.cs:1086-1097` + `L2.TodayEventsRetentionDays`）。
27. 服务器生命周期绑定 Mod、返回标题不杀进程（`ModEntry.cs:544` 仅做 ThinClient 清理）。
28. spark 基础概率 5%、低于 Min 双倍（`SparkAllocator.cs:107`）。
29. `evaluate_friendship` 确为 no-op 记录型（`stardew-tools.ts:521-534`）。
30. L1 记忆无向量嵌入（`agent-memory.ts` 无 embedding/vector 相关代码）。
31. `CompatibilityStubs` 之外的"假 AI"删除叙事与代码一致（`NPCGiftPatch.cs:312,545` 只余注释）。

**构建 / 脚本**
32. `AGENTS.md §5` 引用的 10 个脚本与 compose 文件**全部存在**（`package-distribution.ps1`、`build-deploy.ps1`、`deploy.ps1`、`verify-all.ps1`、`check-*.mjs`、`run_farmhand_e2e.ps1`、`docker-compose.{e2e,soak}.yml`）。
33. `scripts/lib/paths.ps1` 已适配"TS 并入本仓"（优先 `server/`，同级 `ValleyAI/` 兜底）。
34. `scripts/README.md` 对 v4.3 移除项（`start-server.ps1` 等）的说明正确（这些文件确实不存在）。
35. `README.md` 的构建/测试命令可执行：`bun run typecheck`、`bun test`、`bun run check:protocol` 三条实跑均按文档工作。
36. docker 镜像假设正确：`Dockerfile.unittests` 用 .NET 8 SDK + 装 .NET 6 runtime，与 `ValleyAgent.csproj` 的 `net6.0`、`UnitTests` 的 `net8.0` 自洽。
37. `docker-compose.e2e.yml` 里 TS CLI 参数（`--hostname/--port/--data-path/--agents-dir/--llm-*`）与 `cli.ts:52-59` 完全对得上，且 confirm 了 `--enable-director` 已不存在（导演砍除一致）。

---

## 6. 修复建议（按性价比排序）

**P0（会误导后续开发者/agent，改动都很小）**
1. 修 `AGENTS.md` 三处自相矛盾：`:36` 删「迁移未开始」；`:71-72` 去掉「（规划）」；`:150` 改「四步全部落地」。
2. 修 `AGENTS.md:141` 的 M3 段：把 ③ 里 `PlayerDirectory` / `Director.morningPlan` 改为"已于 2026-09-14 随旧叙事 Director 砍除"，并同步删掉 `:143` 已知限制中"玩家名录/morningPlan 退化"两条（该限制已随功能消失）。
3. 给 `docs/design/2026-08-05-three-tier-architecture-redesign.md` 顶部加**被推翻提示**（一行 banner 指向 2026-08-15 文档），这是"按 AGENTS §4 索引阅读"的入口文档。
4. 修 `docs/README.md:12` 死链（指向已不存在的 `../TS服务器重写-上下文交接.md`），并把"v4.3"与 `<VALLEYAI_ROOT>` 外部仓库表述改为"TS 位于本仓 `server/`"。
5. 补 `AGENTS.md` §3.4 工具清单缺的 `chop_tree`；§3.2 的 L3 行改为"叙事线已删，但 C# L3 通路随工具层保留（当前无生产者）"。
6. 让测试参与类型检查：把 `packages/*/tests/**` 纳入一个 `typecheck:tests` 脚本（或把 `server/tsconfig.json` 的 include 扩到 tests），并在 §5 验证门槛里写明——否则 C1 会持续复发。

**P1（清残留）**
7. 清 C1 的 26 个错误：删 `types-extended.test.ts` 中已删类型的 10 个空测试（或整文件归档）、删 `m3-multiplayer.test.ts:16-21` 悬空 import、修 `transcript-wiring.test.ts:22`、删 `dialogue-e2e.test.ts` 的 `enableDirector`。
8. 处置 C2：删除 `DIR_DirectorBehaviorRecord` 或降级为明确 Skip（同时它是 `Director.TriggerProbability` 的唯一读者）。
9. 处置 C3：`ActivityTypesSerializationTests` 若要留，注明"协议未接线、仅序列化形态守护"；TS `activity_report` 类型已删，同步清测试。
10. 处置 C4：20 个死配置（尤其 5 个 Python 项）从 `ModConfig` + GMCM 一并摘除，或加 `[Obsolete]`/注释说明保留理由。
11. 更新 B2/B3/B4：`2026-08-17` 文档状态改"已完成"；M3 文档与 AGENTS 的验收口径统一（保留"C# 仅语法级校验/实机待验"这句）；测试计数改为"现跑现取"或加日期戳。
12. 更新 `B9` README 结构表：补 `src/ValleyAgent.Abstractions/`（重要）、`ValleyTalk.ApiTest`、根级前端与 `autopilot_mcp`。

**P2（整理）**
13. 决策 C5（ApiTest）与 C6（孤儿前端）去留——两者都不在任何文档里，属"幽灵资产"。
14. D2 占位符：统一替换为仓内相对路径（或加一节"占位符约定"说明），优先处理 `docs/v4.1-refactor-progress.md` 这类高密度文件。
15. B7/B8：删 `scripts/README.md` 里三个不存在的截图脚本条目；给 `check-dead-assertions.mjs` 加 `check:test-dead` 别名（让文档里的命令名成真）。
16. A9：把 `2026-09-12-architecture-drift-audit.md` 补进仓库（或在三处引用改成"未入库"），否则"砍 5,600 行"的决策依据无法追溯。

---

## 6bis. 修复记录（2026-09-15 同日，分支 `arena/01a0a53d-valleyagent`）

> 本节记录**已经落地**的修复与**明确未做**的部分；未做项均注明理由，避免"看着像做完了"。

### 已修复

| 项 | 改动 | 验证 |
|---|---|---|
| A1/A2/A3/A4 | `AGENTS.md`：§2 第 6 条删「迁移未开始」；架构图 adjust 执行器/断线 Outbox 去「（规划）」；`AgentInventory` 改「2026-08-15 落地」；§4 索引改「四步全部落地」 | 人工复核三处口径一致 |
| A5/A6 | `AGENTS.md` §3.7：M3 ③ 移除已删的 `PlayerDirectory`/`Director.morningPlan` 现状描述并加删除说明；已知限制删掉"玩家名录/morningPlan 退化"两条 | 与 TS 源码树一致（无 `director.ts`/`player-directory.ts`） |
| A7 | `AGENTS.md` §3.4 工具清单补 `chop_tree` 并标注"共 15 个 LLM 可见工具" | `stardew-tools.ts:316` 注册项 |
| A8 | `AGENTS.md` §3.2 L3 行改为「C# 通路保留、当前无生产者」；状态光谱的「Director beat」改为 `allocate_agent` 建身体（同口径） | `WorldSnapshotBuilder.cs:100` 仍在读 beat |
| A9 | `AGENTS.md` banner 明示审计底稿**未随仓交付** | 全仓仅 3 处引用该缺失文件 |
| B1 | `2026-08-05-three-tier-architecture-redesign.md` 顶部加「部分被推翻」banner（指向 2026-08-15 文档） | — |
| B2/B4 | `2026-08-17-ledger-fixes…` 状态改**已完成**；`2026-08-20-test-system-redesign.md` 测试计数改「以现跑为准」并写入 2026-09-15 实测值 | — |
| B3 | `AGENTS.md` M3 行改「已落地」+ 保留"TS 全绿；C# 仅语法级校验"验收口径 | 与 M3 文档 §5 一致 |
| B5/B6/B7/B8 | `docs/README.md` 重建索引（覆盖 13 设计 + 22 计划 + ideas/superpowers/归档 + 占位符约定），死链已消失；`scripts/README.md` / `scripts/TEST_README.md` 改「TS 源码在本仓 `server/`」、分组去掉 `Narrative`、删三个不存在的截图脚本条目；`server/package.json` 增 `check:test-dead` / `check:anti-cheat` / `check:privacy` 别名（真正可用：内部先 `cd` 到仓库根） | 链接检查：非占位符死链 **0**（修复前 2）；`bun run check:anti-cheat` → PASS |
| B9 | `README.md` 结构表补 `src/ValleyAgent.Abstractions/`，并登记 ApiTest 与根级前端两个"幽灵资产" | — |
| C1 | **TS 测试纳入类型检查**：`typecheck` = `tsc --noEmit` + `tsc -p packages/stardew/tsconfig.json` + `tsc -p packages/core/tsconfig.tests.json`（新增配置）。修完 41 个错误：stardew 26（删 `types-extended.test.ts` 3 个断言已删类型的空测试 + 改 1 个为活类型 + 清悬空/未用 import + 删 `enableDirector`）+ core 15（补类型标注、去未用 import/参数、闭包赋值改为数组收集）；`AGENTS.md §5` 验证门槛同步写死这条 | `bun run typecheck` → **exit 0**；`bun test` → **577 pass / 0 fail**（删 3 个空测试后的正确计数） |
| C2 | `DIR_DirectorBehaviorRecord` 加 `DirectorFlowEnabled` 开关（默认跳过、`VALLEY_DIRECTOR_TEST=1` 可强制跑），类注释与 AGENTS 登记复活条件；省掉每轮 180s 空等 | 逻辑自洽（`Skip()` 走 `WasSkipped` 分支，runner 不再调 Update；该类注册在列表末尾，不触发"3 连跳中止"） |
| C3 | `ActivityTypesSerializationTests` 补注：`activity_report` **协议未接线**（无 messages.json 条目、无 C# 发送端、TS 侧 envelope 类型已删），本测试只守护 payload 字段名契约 | — |
| C4 | `ModConfig.cs` 顶部加配置健康度登记块（A 类垫片勿删 / B 类孤儿待摘除 + 摘除顺序）；`AGENTS.md §3.6` 新增"配置项『可改但无效』陷阱"条目；本报告 §3 C4 补 A/B 分类 | 分类依据：`MigrateLegacyFields()`/`GMCMIntegration` 引用点逐条核对 |

**顺带发现（本轮新增，原报告未含）**：`packages/core/tests` 也有 15 个类型错误（原报告只报了 stardew 的 26 个——当时只跑了 stardew 的配置）→ 已一并修完，并把 core 测试纳入 `typecheck`。

### 明确未做（附理由）

| 项 | 状态 | 理由 |
|---|---|---|
| C4 的 B 类孤儿配置摘除 | **未做** | 属 C# 源码改动，本环境**无 dotnet**，`TestMod`/`UnitTests` 均 `TreatWarningsAsErrors`，无法验证编译；已在代码与文档登记名单+顺序，等有编译环境一次做完 |
| C5/C6 幽灵资产去留（`ValleyTalk.ApiTest`、根级 React 原型） | **未做** | 需用户决策（删/移/保留）；本轮只把它们**登记进 README 与报告**（从"无人知晓"变为"有据可查"） |
| P2-14 697 处占位符逐个替换 | **部分做** | 已在 `docs/README.md` 写明占位符映射约定（`<REPO_ROOT>`=本仓根、`<VALLEYAI_ROOT>`=`server/`），并把两个**活文档**（`scripts/*README.md`）改成真实路径；历史计划/设计文档里的占位符**保留原样**（它们是当时的记录，批量改写会污染历史） |
| A9 补回 `2026-09-12-architecture-drift-audit.md` | **未做** | 底稿内容不在本仓，无法凭空补写；已改为在 AGENTS 中明示"未随仓交付" |

### 复验命令（本轮跑过的）

```bash
cd server
bun run typecheck                      # exit 0（src + stardew tests + core tests）
bun test                               # 577 pass / 0 fail
bun run check:protocol                 # PASS（0 dead pipeline / 0 schema drift）
bun run check:anti-cheat               # PASS（0 违规）
cd .. && python3 md-link-check         # 非占位符死链 0
```

---

## 7. 本环境**未核实**项（勿当作结论）

| 项 | 原因 |
|---|---|
| C# 门禁（`dotnet build` 0 警告 / `dotnet test` 657 通过） | 沙箱无 dotnet SDK；且 `Pathoschild.Stardew.ModBuildConfig` 需要真实游戏目录（`STARDREW_VALLEY_GAME_PATH`），Docker 才有 |
| 游戏内 IT / Docker 三容器 E2E / soak | 需要游戏本体 + 真实 LLM key，本环境不具备 |
| `check-dead-assertions.mjs` 重跑数字（273 条残留等） | 依赖 gitignored 的 `logs/test_results/` 历史断言日志，仓库内不存在 |
| AGENTS 中"性能/体感/行为"类描述（卡死排查结论、体验打分） | 不可证伪，需实机 |
| `Check: test-anti-cheat`、`check-privacy.mjs` | 未纳入本轮（不在文档声明的验证门槛里）；如需可单独跑 |

---

## 8. 复现命令（审查用的全部取证）

```bash
# 环境
node --version                 # v22.22.3
npm install -g bun             # 沙箱原本无 bun；bun 1.4.2
cd server && bun install       # 71 packages

# 文档声明的三道 TS 门禁
bun run check:protocol         # PASS：0 dead pipeline / 0 schema drift / 3 orphan 警告
bun run typecheck              # 干净（只覆盖 packages/*/src）
bun test                       # 580 pass / 0 fail / 57 文件（覆盖率门禁 0.8 逐文件通过）

# 揭示 C1 的那一条（tests 参与类型检查）
bunx tsc -p packages/stardew/tsconfig.json    # 26 errors / 4 test files

# 死链与索引
python3 - <<'EOF'  # 简化版：遍历 md，校验非 http 相对链接
# 结果：97 篇 md、115 条本地链接、73 条失效（71 条为 file:///<REPO_ROOT> 占位）
EOF

# 符号存在性（样例）
grep -rn "PlayerDirectory\|morningPlan" server/packages/stardew/src   # 无 src 命中
grep -rn "chop_tree" server/packages/stardew/src/stardew-tools.ts     # :316 命中，文档未列
grep -rn "DIR_DirectorBehaviorRecord" src/ValleyAgent.TestMod/V3TestRunner.cs  # :724 仍注册
```

> 审查者备注：本轮结论全部基于**可复现命令的输出**；凡未跑到的（C#、实机、历史日志）都在 §7 单列，未混入偏移计数。
