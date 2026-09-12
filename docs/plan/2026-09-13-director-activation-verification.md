# Director 有效化整改——执行验证记录（2026-09-13）

对应计划：`docs/plan/2026-09-12-director-activation-plan.md`（D1–D7 裁决、五阶段）。
执行环境说明：整改在无 bun / 无 .NET SDK 的沙箱中静态完成，动态验证命令见文末，**须由用户在本地复跑**。

---

## 一、执行摘要

| 阶段 | 内容 | 状态 |
|---|---|---|
| 1 | TS：DirectorAgent 工具脑 + adapter/server 接线 + prompt/decoder/types 扩展 + 旧 Director/runBeat/react-guard 管线删除 + 测试重写 | ✅ 完成 |
| 2 | C#：GoalExecutor/NPCGiftPatch L2 写入 + WorldSnapshotBuilder 求购注入 + 静态引用接线 | ✅ 完成 |
| 3 | C# 死代码删除（实际 ~6,000+ 行，超审计预估 5,600 行）+ 全部引用点清理 | ✅ 完成 |
| 4 | messages.json 死 schema 清除 + check:protocol 路径仓库相对化 + deploy.ps1 参数化 | ✅ 完成（契约交叉检查经 Python 移植模拟：0 违规） |
| 5 | AGENTS.md / README.md / 计划文档更正 / 本验证记录 | ✅ 完成 |

**净变更**（git diff --stat）：约 12,800+ 行删除、约 520 行新增（不含新文件 director-agent.ts / director-agent.test.ts 等 untracked）。

## 二、D1–D7 落实对照

### D1 Director 工具大脑端到端有效 ✅
- 新建 `server/packages/stardew/src/director-agent.ts`：`DirectorAgent.runDayPlan(directorContext?)`，chatWithTools 工具循环（默认 6 轮），9 工具全部经 `director_command` 下发（**不带 npcName**，防回执泄漏）。
- spawn 预检：NPC 在 gameContext 且可用 / 14 天同人冷却（BeatStore.listRecent）/ 每日上限（默认 3，`maxBeatsPerDay` 可配）/ sceneDesc ≤200 字 / duration 钳 30–600 分钟。
- `protocol-adapter.handleDayStarted`：概率触发 → `runDayPlan` → 逐 beat `allocate_agent`（`keepUntilIso` ISO 时间戳）→ C# 落库；`emotionEngine.resetAll()` 前置；`directorContext` 字段透传；未接线时静默 ack。
- `server.ts`：DirectorLlm 类型化直传（`router.chatWithTools("director", …)` 多供应商 / provider 单发）。
- 留痕：`director_runs` 表，`trigger="dayPlan"`，promptFull 含「叙事导演」。
- **旧管线退役**：`director.ts` / `react-guard.ts` / `runBeat`+`BEAT_SYSTEM_TEMPLATE` 删除；beat 唯一通路 = C# BeatStore → worldSnapshot.currentBeat → L3 场景注入（「─── 眼下正发生的事 ───」段）。

### D2 L2/L3 断链全部接通 ✅
- `npcWorkingOn`：GoalExecutor CreateGoal 写中文标签（伐木/采矿/浇灌农田/战斗/采集 + "（进行中）"）、终结分支清除、汇报寻路期「正在去找农场主汇报」。
- `npcRecentEvents`：GoalExecutor 成功/失败落事件（`AddTodayEvent`，此前零调用方）；NPCGiftPatch 送礼落「收到了玩家送的X（最爱/…）」。
- `currentBeat`：TS 解码（decoder）+ prompt BEAT_SECTION 段（此前 C# 已填、TS 接收即丢弃）。
- `npcOwedMoney`：prompt 欠款行仅非零渲染（诚实化）。
- 求购单：`NpcPurchaseRequestService.Current`（静态引用，BeatStore.Current 同模式，ServiceInitializer 注册）→ `WorldSnapshotBuilder.BuildPurchaseOffers`（过期滤除）→ `npcPurchaseOffers` 解码 → prompt「你想收购：X×n（出价 Pg/个）」行。

### D3 情绪引擎闭环 ✅
`handleDialogue` 在 runDialogue 前预填 `scene.npcMood`（引擎状态首次进入 NPC prompt）；C# 侧 SyncEmotion 保留镜像语义。

### D4 consolidate_day 实做 ✅
`handleConsolidateDay`：consolidatedProfileDates Set 每日一次，`refreshPreferences()` + `refreshPersonality()`（LLM×2/天），失败保旧值。逐 NPC 日结仍留 TODO（README 已列入已知限制）。

### D5 死代码清除 ✅
**TS 删除**：`director.ts`、`react-guard.ts`、`stardew-agent.ts` 内 runBeat/sceneStateFromGameContext/BEAT_MAX_TURNS、narrative-types 7 个死 wire 消息类型（activity_report/activity_milestone/beat_directive/beat_activate/beat_event/beat_state/player_state_update，GameContextSyncMessage 保留——生产在用）、6 个旧测试文件、messages.json 10 条 planned 死 schema。

**C# 删除（文件级）**：
| 簇 | 文件 | 行数（约） |
|---|---|---|
| Context/ | AgentContextBuilder + IContextProvider + 5 Providers | ~700 |
| Controllers/ | Farm/Fight/Forage/IdleController（全部，ApplyControllerToNpc 直驱状态机） | ~1,500 |
| Abstractions/Controllers/ | FollowController + IAgentController | 203 |
| Exceptions/ | GlobalExceptionHandler + BoundaryCaseChecker | ~900 |
| Recovery/ | RecoveryActions | ~400 |
| Validation/ | ActionValidator + IActionValidator + ValidationContext/Result | ~500 |
| RAG/ | RAGKnowledgeBase.cs + 5 个 json 数据 + Models/ 全目录 | ~1,800 |
| UI/Dialogue/Agents/Handlers | AgentInventoryMenu / Typewriter / InteractionTracker / WoodScanHelper | ~400 |
| ProtocolV2 | CommandMessage/PlayerInputMessage/EventMessage/ToolCallMessage 4 死消息类 + 3 常量 | ~120 |

**保留**（审计裁决）：ValleyTalkBioLoader（AllocateAgentHandler/ConsoleCommand 在用）、GameSummaryLoader（DecisionContextBuilder 在用）、Abstractions/RAG/Models/ValleyTalkBioData.cs（AgentBrain/TraitPhaseMapper 依赖）。
**引用清理**：ServiceInitializer 去 RAGKnowledgeBase 注册；csproj 去 RAG\*.json 拷贝规则；AgentNavigator 删 FollowController 兼容重载；全仓零残留（已 grep 复核）。

### D6/D7 门禁修缮 ✅
- `check-protocol-contract.ts`：默认路径改脚本位置相对（`server/` 与 `<repo>/src`），env 覆盖保留（VALLEYAI_ROOT/VALLEYTALK_SRC_ROOT），合并仓布局开箱即用。
- `deploy.ps1`：`-GamePath`/`-ValleyAIDir` 参数化（env 回落：VALLEY_GAME_PATH/VALLEY_AI_ROOT，默认回落本仓 server/）；补声明 `-StartGame`（原先 PSBoundParameters 引用了未声明参数，永假）；RAG 拷贝步骤转为清理旧部署残留。
- `morning-shout-router.ts`：注释与实现对齐（生产不注入 LLM、恒确定性兜底）。

## 三、审计更正（执行中发现）

1. **M-7 误判**：`ModConfig.DirectorConfig.TriggerProbability` 并非死旋钮——`ServerProcessManager.cs:299-301` 将其经 `--director-probability` 命令行参数传给 TS server。**保留**，AGENTS.md 已注明。
2. **C# 死代码规模超预估**：Controllers 四个全死（审计只列了 3 个）、Abstractions/Controllers/ 与 RAG/Models/ 整目录死（审计只列了 GameMechanics.cs）、WoodScanHelper.cs 确认删除。
3. **AgentNavigator.cs:225** 存在 FollowController 兼容重载（审计漏计的真实代码引用），随删除一并清理。

## 四、协议契约静态交叉检查（Python 移植模拟）

对 `check-protocol-contract.ts` 的提取与交叉逻辑做 Python 移植，在当前工作树上运行：

- S_send（C# 发送）= action_result / adjust_result / consolidate_day / day_started / dialogue / game_context_sync / hello / ping / reconnect_sync / state_changed（10 个）
- S_route（TS 路由）= 上述 10 + director_command / execute_adjust / route_shout（13 个）
- **DEAD_PIPELINES = 0，SCHEMA_DRIFTS = 0** → `bun run check:protocol` 预期 PASS（exit 0）
- messages.json 17 条剩余条目（10 active + 3 orphan_route + 4 ts_to_csharp 响应）标注与现实逐一相符。

## 五、复验命令（用户本地执行）

```powershell
# 1. TS：单元测试 + 类型检查 + 协议契约
cd server
bun install
bun test                      # 含新增 director-agent.test.ts（8 用例）等
bun run typecheck             # tsc --noEmit
bun run check:protocol        # 预期 PASS（见上）

# 2. C#：构建 + 单元测试
dotnet build src/ValleyAgent/ValleyAgent.csproj -c Debug
dotnet test src/ValleyAgent.UnitTests

# 3.（可选）部署冒烟
pwsh scripts/build/deploy.ps1 -GamePath "<你的游戏目录>"
```

**测试清单变化**：新增 `tests/director-agent.test.ts`（8 用例）；重写 `protocol-adapter-day-started` / `server-director-e2e` / `prompt-segment-order` / `transcript-wiring` / `server-ws-observability`；增补 `world-snapshot-decoder`（currentBeat/purchaseOffers）与 `prompt-builder`（L3 段 + 求购行 + 欠款条件化）用例；删除 6 个旧 Director/runBeat 测试。

## 六、遗留与后续

- **逐 NPC 日结 LLM**（consolidate_day 画像进化目前按日聚合触发一次）——TODO 保留，README 已列入已知限制。
- `route_shout` 仍为 orphan_route（C# 4 层确定性路由全空分支的预留通路，两端语义已对齐）。
- `morning-shout-router` LLM 钩子保留但生产不接线（性能纪律），待未来有歧义路由需求再评估。
- Director `spawn_group_beat` / `inject_memory` 等工具已在 DirectorAgent 暴露，行为回填节奏由后续实测调优。
