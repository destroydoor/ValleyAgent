# ValleyAgent Phase 3-5 完整执行计划

> **生成时间**：2026-08-03
> **基线分支**：`feat/exec-2026-08-02-phase0` HEAD=`8608ebd`（CA2024 修复后）
> **执行依据**：`docs/plan/2026-08-02-execution-plan.md` Phase 3-5 条目
> **双仓库**：ValleyTalk (C# SMAPI mod) + ValleyAI (TS/Bun server)

---

## 0. 前置状态确认

| 已完成 | 提交 |
|---|---|
| Phase 0 (E0-1..E0-5) | `b348c42` |
| Phase 1 (E1-1 TranscriptStore) | `985bc67` |
| Phase 2 (E2-1 动态速度) | `591f924` |
| Phase 2 (E2-2 聊天栏路由 + 会话) | `5b91780` → merge `af8d187` |
| Phase 2 (E2-3 长文本规则) | `b0d3133` → merge `62f0e54` |
| Phase 5 (E5-1 作息表数据) | `9422c96` → merge `3ceffd6` |
| CA2024 修复 (ApiTest) | `8608ebd` |

**全量门禁**：6 个 dotnet 项目 0 警告 0 错误；UnitTests 118 pass；ApiTest 0 警告；ValleyAI bun test 349 pass 0 fail；tsc 0；check:protocol PASS。

---

## 1. 决策门（实施前拍板）

### Q4 — E3-2 还价状态机归属

**决策**：数值判定放 C# 规则侧，LLM 只做话术。

**理由**：
- AGENTS.md 设计哲学 §5："数值判定优先落 C# 规则侧，LLM 只出话术"
- 需求 §1.8："LLM 在聊天里答应的价格只是意向"
- 执行计划 E3-2："LLM 断线时交易可降级不卡死"
- 防坑是底线：价格公式必须确定性防骗，不能靠 LLM 即兴
- 可追溯：数值判定进 TranscriptStore 可复核

**落地**：C# 新建 `PricingEngine` + `HaggleStateMachine`（纯逻辑、可单测）；TS 侧只生成话术，从 C# 发来的结构化 offer/response intent 渲染自然语言。

### Q8 — E5-2 歧义兜底路由

**决策**：独立轻量路由 LLM（最多 1 次调用），不复用 Director。

**理由**：
- 需求 §3.4："完全无指向且规则判不出时才调一次 LLM 决策"
- Director 未接线（思路 §4.8 明确"未来扩展，本次不做"）
- 路由是查表决策（哪个 NPC 醒着 + 关系最近），Director 是叙事编排——职责不同
- 独立路由省 token、低延迟、可降级（LLM 断线时确定性路由兜底）

**落地**：C# `MorningShoutRouter` 4 层确定性路由（名字提及 → 当前会话 → 跟随/雇佣者 → 已醒来+关系最近）；仅当 4 层全空时调一次 TS 轻量 `route_shout` LLM。

---

## 2. 双轨并行架构

```
Track A (Phase 3 + Phase 4)          Track B (Phase 5)
══════════════════════════            ═════════════════
E3-1 数据层 ──┐                       共享 QuotaService ──┐
              ├─→ E3-2 定价                 (C# 新建)     │
              │        └─→ E3-3 交易        E5-2 晨间喊话 ┤
              │              └─→ E3-5 求购   E5-3 主动发言 ┘
              └─→ E3-4 只读UI (并行侧支)
                    ↓
              E4-1 雇佣 (Phase 3 完成后)
```

**两轨完全独立**：Track A 依赖 Phase 0（已完成）；Track B 依赖 Phase 2 + E5-1（已完成）。无跨轨依赖。

---

## 3. Wave 详细计划

### Wave 0 — 前置 master 合并（顺序，不并行）

**1a. Master 合并门禁验证**
- 验证 `feat/exec-2026-08-02-phase0` HEAD=`8608ebd` 全量门禁
- 命令：
  ```
  dotnet build src\ValleyAgent\ValleyAgent.csproj --no-incremental --verbosity minimal
  dotnet build src\ValleyTalk.ApiTest\ValleyTalk.ApiTest.csproj --no-incremental --verbosity minimal
  dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --no-build --verbosity minimal
  cd D:\Source\ValleyAI; bun test packages/stardew; tsc --noEmit; bun run check:protocol
  ```
- 验收：6 项目 0 警告 0 错误；118 pass 0 fail；349 pass 0 fail；check:protocol PASS

**1b. Phase0 → master 合并**
- `git checkout master; git merge --no-ff feat/exec-2026-08-02-phase0 -m "merge: Phase 0-2 + E5-1 + CA2024 fix into master"`
- ValleyAI: `git checkout master; git merge --no-ff feat/exec-2026-08-02-phase0-ts -m "merge: Phase 0-2 TS side into master"`
- 不 push（用户决定何时 push）
- 合并后 master = 稳定检查点，Phase 3-5 从 master 分支

**1c. 创建集成分支**
- ValleyTalk: `git checkout -b feat/integration-phase3-5 master`
- ValleyAI: `git checkout -b feat/integration-phase3-5-ts master`

---

### Wave 1 — Track A 根 + Track B 共享件（3 项并行）

**A1 — E3-1 数据层：钱包 + 背包**
- Worktree: `vt-e31` → branch `feat/exec-e31`
- 依赖: 无（Phase 1 TranscriptSink + `OnItemChanged` hook 已预留）
- 阻塞: A2, A3, A4, A5, A6（全部 Phase 3+4 依赖钱包数据）
- 派发: `category="deep"`, `load_skills=["strict-dev-pipeline"]`
- 改动文件:
  - `src/ValleyAgent.Abstractions/Inventory/AgentInventory.cs` — 添加 `Money` 属性 + `TrySpend/Spend/Add` 原子操作 + `OnWalletChanged` 事件
  - `src/ValleyAgent/Save/AgentSaveData.cs` + `Save/Models/SaveData.cs` — 钱包持久化字段
  - `src/ValleyAgent/Protocol/TranscriptSink.cs` — 接线 `OnWalletChanged` → 写入 JSONL
  - 新建 `src/ValleyAgent/Data/npc_economy.json` — 逐 NPC 初始资金/物品/精明度/预算分档/日薪基准/话�厅度
  - 新建 `src/ValleyAgent/Economy/NpcEconomyProfile.cs` + `NpcEconomyProfileLoader.cs` — 数据加载
  - `src/ValleyAgent/Initialization/ServiceInitializer.cs` — 注册 NpcEconomyProfileLoader
  - `src/ValleyAgent/Config/ModConfig.cs` — 添加 Economy 配置段
- 写测试:
  - `NpcEconomyProfileLoaderTests` — 加载 + 数据完整性
  - `AgentInventoryWalletTests` — 钱包原子操作 + 事件触发
  - `SaveDataPersistenceTests` — 存档/读档一致
- 验收: 存档/读档后钱包背包一致；每次变动有日志；check:protocol PASS
- 实现思路文件: `vt-e31/docs/ideas/e31-implementation-思路.md`（134 行+，含数据格式定义 + 接线图）

**B0 — 共享 QuotaService (Track B 前置)**
- Worktree: `vt-e53` → branch `feat/exec-quota`（B0/B1/B2 可以共用 e53 worktree）
- 依赖: 无
- 阻塞: B1, B2
- 派发: `category="quick"`, `load_skills=["strict-dev-pipeline"]`
- 改动文件:
  - 新建 `src/ValleyAgent/Chat/ProactiveSpeechQuota.cs` — 封装 `ChatSessionRegistry` 已有的 `RecordNpcSpeech`/`GetDailyProactiveCount`/`ClearDailyCounts` + 超时 + 会话豁免逻辑
  - `src/ValleyAgent/Chat/ChatSessionRegistry.cs` — 委托给 ProactiveSpeechQuota（或 ProactiveSpeechQuota 直接包装 ChatSessionRegistry 计数器）
  - `src/ValleyAgent/Initialization/EventHandlerInitializer.cs:840` OnDayStarted — 调用 `ProactiveSpeechQuota.ResetDaily()`
  - `src/ValleyAgent/Config/ModConfig.cs` — 新增 `ProactiveSpeechDailyLimit`（默认 2）, `ProactiveSpeechCooldownMinutes`（默认 30）
- 写测试:
  - `ProactiveSpeechQuotaTests` — 限额消耗/重置/会话豁免/被动不计
- 验收: 单测全绿；ChatSessionRegistry 现有测试不回归
- 实现思路文件: `vt-e53/docs/ideas/quota-implementation-思路.md`

**TS1 — ValleyAI 集成分支就绪**
- 在主 ValleyAI checkout 上 `feat/integration-phase3-5-ts` 分支就绪
- 验证 check:protocol 从主 checkout 运行 exit 0

---

### Wave 2 — E3-2 定价 + E3-4 UI 壳（并行，依赖 A1）

**A2 — E3-2 定价引擎（Q4 决策已拍板）**
- Worktree: `vt-e32` → branch `feat/exec-e32`
- 依赖: A1（钱包数据）
- 阻塞: A3, A4b
- 派发: `category="ultrabrain"`, `load_skills=["strict-dev-pipeline"]`
- 改动文件:
  - 新建 `src/ValleyAgent/Economy/PricingEngine.cs` — 公道价 = 游戏卖价；精明度浮动率表
  - 新建 `src/ValleyAgent/Economy/HaggleStateMachine.cs` — 阶梯还价（3 轮上限、让步逐轮减半、恶意低价警觉）、购买力约束（出价 = min(心理价, 钱包 × 预算比例)）
  - `src/ValleyAgent/Data/npc_economy.json` — 精明度/预算分档数据（谨慎 0.3 / 普通 0.5 / 豪爽 0.8）
  - `src/ValleyAgent/Config/ModConfig.cs` — Haggle 配置段
  - ValleyAI TS: `packages/stardew/src/stardew-tools.ts` — 新增 `haggle_reply` 工具（只做话术，数值由 C# 返回的结构化 intent 驱动）
  - ValleyAI TS: `packages/stardew/src/prompt-builder.ts` — 交易话术 prompt 段
- 写测试:
  - `PricingEngineTests` — 公道价计算、精明度浮动、购买力上限
  - `HaggleStateMachineTests` — 第 4 轮必拒、让步减半、恶意低价警觉、连崩两次关闭、预算比例三人三反应
- 验收: 需求 §1.9 验收标准全部通过
- 实现思路文件: `vt-e32/docs/ideas/e32-implementation-思路.md`

**A4a — E3-4 只读背包 UI 壳**
- Worktree: `vt-e34` → branch `feat/exec-e34`
- 依赖: A1（背包数据）
- 阻塞: A4b
- 派发: `category="visual-engineering"`, `load_skills=["game-ui-design", "strict-dev-pipeline"]`
- 改动文件:
  - 新建 `src/ValleyAgent/UI/ReadOnlyInventoryMenu.cs` — 复用 ItemGrabMenu 设只读
  - 新建 `src/ValleyAgent/UI/IInventoryDisplayMode.cs` — 展示模式抽象（预留偷窃复用）
- 写测试:
  - `ReadOnlyInventoryMenuTests` — 无法拖走任何物品
- 验收: 无法从界面直接拿走物品；展示正确
- 实现思路文件: `vt-e34/docs/ideas/e34-implementation-思路.md`

---

### Wave 3 — Phase 3 收尾 + Phase 5 主体（5 项并行）

**A3 — E3-3 交易流程**
- Worktree: `vt-e33` → branch `feat/exec-e33`
- 依赖: A1, A2
- 阻塞: A5
- 派发: `category="ultrabrain"`, `load_skills=["strict-dev-pipeline"]`
- 改动文件:
  - 新建 `src/ValleyAgent/Economy/PendingOfferRegistry.cs` — 每 NPC 限 1 个、30 秒超时、走远/切图作废
  - 新建 `src/ValleyAgent/Economy/TradeSettlement.cs` — 原子成交校验（物品在/数量够/双方钱够）
  - 新建 `src/ValleyAgent/Patches/NPCRightClickPatch.cs` — 右键 Agent NPC 出"送礼/交易/取消"菜单
  - `src/ValleyAgent/Handlers/CommandExecutor.cs` — 失败回 action_result.reason
  - `src/ValleyAgent/Initialization/EventHandlerInitializer.cs` — 切图作废 pending offer + OnWarped 事件
- 写测试:
  - `TradeSettlementTests` — 每种失败原因（物品消失/数量不够/钱不够）+ 原子性
  - `PendingOfferRegistryTests` — 唯一性、超时、走远、切图作废
  - L3 故障注入: 成交时物品消失 → 成交失败 + NPC 知情
- 验收: 需求 §1.8 验收；谈妥后丢弃物品 → 成交失败且 NPC 知情；超时作废有提示
- 实现思路文件: `vt-e33/docs/ideas/e33-implementation-思路.md`

**A4b — E3-4 谈价入口 hook**
- 同 `vt-e34` worktree
- 依赖: A2（haggle 状态机）
- 派发: `category="visual-engineering"`, `load_skills=["game-ui-design", "strict-dev-pipeline"]`
- 改动: ReadOnlyInventoryMenu 点选物品 → 调用 HaggleStateMachine 进入还价流程
- 验收: 点选物品正确进入还价流程
- 实现思路文件: 追加到 `e34-implementation-思路.md`

**A5 — E3-5 NPC 求购**
- Worktree: `vt-e35` → branch `feat/exec-e35`
- 依赖: A1, A3
- 派发: `category="deep"`, `load_skills=["strict-dev-pipeline"]`
- 改动文件:
  - 新建 `src/ValleyAgent/Economy/NpcPurchaseRequestService.cs` — 逐人设求购生成（格斯收食材、克林特收矿石等）、求购价 1.0~1.1× 公道价、频率受限
  - `src/ValleyAgent/Data/npc_economy.json` — 求购偏好数据
  - `src/ValleyAgent/Chat/ChatBarRouter.cs` — 求购发布到聊天栏（复用 Phase 2 路由）
- 写测试:
  - `NpcPurchaseRequestServiceTests` — 频率上限、倍率区间、交付后钱包扣减
  - L4 不变量: NPC 钱包扣减后不为负
- 验收: 求购频率合理不刷屏；交付成交后 NPC 钱包正确扣减
- 实现思路文件: `vt-e35/docs/ideas/e35-implementation-思路.md`

**B1 — E5-2 晨间喊话（Q8 决策已拍板）**
- Worktree: `vt-e52` → branch `feat/exec-e52`
- 依赖: B0（QuotaService）, E5-1（NpcScheduleService 已完成）
- 派发: `category="ultrabrain"`, `load_skills=["strict-dev-pipeline"]`
- 改动文件:
  - 新建 `src/ValleyAgent/Chat/MorningShoutRouter.cs` — 4 层确定性路由（名字提及 → 当前会话 → 跟随/雇佣者 → 已醒来+关系最近）
  - `src/ValleyAgent/Chat/ChatBarRouter.cs:101-106` — 替换 `present.Count == 0` 早返回为喊话路由
  - `src/ValleyAgent/Services/Schedule/NpcScheduleService.cs` — 复用 `IsAwake()` 做醒着过滤
  - 新建 `src/ValleyAgent/Chat/ShoutReplyScheduler.cs` — 几秒延迟回应（复用 SpeechDisplayRouter pending 机制）
  - `src/ValleyAgent/Initialization/EventHandlerInitializer.cs:840` OnDayStarted — 连续清晨骚扰计数 + 记忆升级
  - ValleyAI TS: 新建 `packages/stardew/src/morning-shout-router.ts` — 轻量路由 LLM（最多 1 次调用）
  - ValleyAI TS: `packages/stardew/src/protocol/types.ts` + `messages.json` — `route_shout` 消息类型
  - ValleyAI TS: `packages/stardew/src/protocol-adapter.ts` — route_shout 路由
- 写测试:
  - `MorningShoutRouterTests` — 4 层路由优先级、全员未醒沉默、名字提及 100%、LLM 调用 ≤1 次
  - `ShoutReplySchedulerTests` — 延迟执行
  - L4 不变量: 被动回应不消耗主动额度
- 验收: 需求 §3.4 验收标准全部通过
- 实现思路文件: `vt-e52/docs/ideas/e52-implementation-思路.md`

**B2 — E5-3 主动发言**
- Worktree: `vt-e53` → branch `feat/exec-e53`（与 B0 同 worktree）
- 依赖: B0（QuotaService）
- 派发: `category="deep"`, `load_skills=["strict-dev-pipeline"]`
- 改动文件:
  - `src/ValleyAgent/Initialization/EventHandlerInitializer.cs:1510` OnTimeChanged — 空桩 → 场景事件触发钩
  - `src/ValleyAgent/Initialization/EventHandlerInitializer.cs:103` `_pendingPreSpeakActions` — 添加生产者（场景事件入队）
  - `src/ValleyAgent/Chat/ChatBarRouter.cs:207` GetTalkativeness — 替换为逐人设话痨度配置驱动
  - `src/ValleyAgent/Config/ModConfig.cs` — talkativeness per NPC 配置
  - 新建 `src/ValleyAgent/Chat/ProactiveSpeechTrigger.cs` — 场景事件检测（大面积收获、暴雨、残血、节日前夕、经过 NPC）+ 硬冷却
  - ValleyAI TS: `packages/stardew/src/prompt-builder.ts` — 主动发言 prompt 段
- 写测试:
  - `ProactiveSpeechTriggerTests` — 触发条件、硬冷却、每日 ≤2 条、会话模式豁免
  - L4 不变量: 无触发不刷屏
- 验收: 上限生效；无触发不刷屏；触发时机贴切
- 实现思路文件: `vt-e53/docs/ideas/e53-implementation-思路.md`

---

### Wave 4 — Phase 4 雇佣系统

**A6 — E4-1 契约 + 计价 + 朋友价**
- Worktree: `vt-e41` → branch `feat/exec-e41`
- 依赖: A1-A5（Phase 3 全部完成）、FriendshipSystem（已存在）
- 派发: `category="ultrabrain"`, `load_skills=["strict-dev-pipeline"]`
- 改动文件:
  - 新建 `src/ValleyAgent/Economy/EmploymentContract.cs` — 契约模型（任务类型/期限/报酬）
  - 新建 `src/ValleyAgent/Economy/ContractService.cs` — 计价 = 日薪基准 × 危险系数 × 缺钱修正 × 好感修正；还价复用 E3-2 HaggleStateMachine
  - 新建 `src/ValleyAgent/Economy/LongWorkerProtection.cs` — 朋友价/免费 + 长工频率保护（一周免费 ≥3 次触发吐槽）
  - 新建 `src/ValleyAgent/Economy/ContractViolation.cs` — 违约抗议与中止
  - `src/ValleyAgent/AI/WorldSnapshotBuilder.cs` — 添加 playerMoney 字段
  - ValleyAI TS: `packages/stardew/src/types.ts` — WorldSnapshot 增加 playerMoney
  - ValleyAI TS: `packages/stardew/src/protocol/world-snapshot-decoder.ts` — 解码 playerMoney
  - ValleyAI TS: `packages/stardew/src/stardew-tools.ts` — 新增 `accept_job` 工具
  - ValleyAI TS: `packages/stardew/src/prompt-builder.ts` — 雇佣 prompt 段
- 写测试:
  - `ContractServiceTests` — 计价各修正因子、缺钱 vs 有钱反应、朋友价、长工吐槽（3 次/周触发）、违约+settlement
  - L4 不变量: 契约优先于叙事
- 验收: 需求 §2.6 验收标准全部通过
- 实现思路文件: `vt-e41/docs/ideas/e41-implementation-思路.md`

---

### Wave 5 — 最终合并

**9. GLM 5.2 Plan Agent 合并编排**
- 派发: GLM 5.2（oracle/plan 只读）审查累积 diff vs master
- 合并顺序（Track A 内依赖序 + Track B 独立）：
  1. A1 (E3-1) → 集成分支
  2. A2 (E3-2) → 集成分支
  3. A3 (E3-3) → 集成分支
  4. A4a+A4b (E3-4) → 集成分支
  5. A5 (E3-5) → 集成分支
  6. B0 (QuotaService) → 集成分支
  7. B1 (E5-2) → 集成分支
  8. B2 (E5-3) → 集成分支
  9. A6 (E4-1) → 集成分支
  10. 集成分支 → master（--no-ff）
- ValleyAI TS: 同序合并 TS 侧变更到 TS 集成分支 → master
- 每步合并后：
  - `git checkout -- "Stardew Valley/Mods"`（清理 mod deploy 覆盖）
  - 重建 + 全量测试
  - check:protocol（从主 ValleyAI checkout 运行）

**最终门禁**：
```
# ValleyTalk
dotnet build (6 项目) --no-incremental → 0 警告 0 错误
dotnet test → 全 pass 0 fail
# ValleyAI
bun test packages/stardew → 全 pass 0 fail (exit 1 仅覆盖率阈值)
tsc --noEmit → 0
bun run check:protocol → PASS (0 DEAD_PIPELINES / 0 SCHEMA_DRIFT)
```

---

## 4. 并行时间线

```
时间 →  Wave0   Wave1      Wave2       Wave3            Wave4     Wave5
       ─────────────────────────────────────────────────────────────────
Track A  [merge] E3-1 ──→ E3-2 ──→ E3-3 ──→ E3-5 ──→ E4-1 ──→ [final merge]
                         E3-4(并行)──┘
Track B          B0(Quota)          B1(喊话) ┘
                                   B2(主动) ┘ ──────────────→ [final merge]
```

- Wave 0: 顺序（master 合并检查点）
- Wave 1: A1 ∥ B0 ∥ TS 就绪（3 并行）
- Wave 2: A2 ∥ A4a（2 并行）
- Wave 3: A3 ∥ A5 ∥ A4b ∥ B1 ∥ B2（5 并行，跨双轨）
- Wave 4: A6（独占）
- Wave 5: GLM 5.2 合并

---

## 5. 测试策略（L1-L5 per 需求 §测试）

### L1 契约
- 每项合并后 `bun run check:protocol` 必须 exit 0
- 新增消息类型（route_shout, accept_job, haggle_reply）必须登记到 messages.json
- 检查项: DEAD_PIPELINES / ORPHAN_ROUTES / SCHEMA_DRIFT

### L2 组件
- C# xUnit: 每项配套测试文件（见 Wave 细节）
- TS bun test: 每项 TS 改动配套测试
- 新增不变量测试: 钱包不为负、背包物品数守恒、pending offer 唯一

### L3 故障注入（每流程 ≥1 条）
- E3-2: LLM 断线 → 交易降级到纯规则话术
- E3-3: 成交时物品消失 → 成交失败 + NPC 知情
- E3-3: pending offer 超时 → 自动作废有提示
- E5-2: 连续清晨骚扰 → 记忆升级
- E4-1: 契约违约 → 抗议 + 中止

### L4 不变量
- NPC 钱包 ≥ 0（交易扣减后）
- 背包物品数守恒（交易双方）
- pending offer 每 NPC 唯一
- 主动发言每日 ≤ 配置上限
- 被动回应不消耗主动额度

### L5 体验打分
- 对照需求各章验收标准
- 游戏内实测（TestMod 或手动）

---

## 6. 风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| check:protocol 硬编码 D:\Source\ValleyAI 路径 | TS worktree 中无法直接运行 | 合并到主 checkout 后运行 check:protocol |
| ValleyTalk "Stardew Valley/Mods" git 跟踪被 mod deploy 覆盖 | 误提交部署产物 | 每次构建后 `git checkout -- "Stardew Valley/Mods"` |
| 隔离 worktree 的 Stardew Valley 文件夹缺 exe | ModBuildConfig 拒绝 | `-p:GamePath="D:\Source\ValleyTalk\Stardew Valley"` |
| TS 多项并行同分支提交竞争 | git add/commit 冲突 | 每个 TS 项独立 worktree 分支，顺序合并到 TS 集成分支 |
| E3-2 Q4 拍板后仍可能有边界争议 | 定价实现偏差 | 实现前以需求 §1.2-1.4 为权威 |
| E4-1 "导演高优先级 beat" 语义 | Director 未接线 → 需 C# 状态机实现 | 按 记忆 §4.2 复用 beat 模式（时限/前提检查/失败解释），在 C# 直接实现 |
| bun test exit 1（覆盖率阈值） | 误判为失败 | exit 1 仅因 9 文件行覆盖率 <0.8，无用例失败；独立覆盖率工单，不阻碍 |

---

## 7. 派发约束（铁律）

1. **每个 subagent 动手前必须写实现思路文件**（设计思路 md，放 worktree/分支里）
2. **能并行的工作必须并行**（用 git worktree 隔离到独立分支/目录）
3. **同分支并行提交会竞争** → 必须各自 `git add` 只含自己的文件
4. **最终合并必须派发 GLM 5.2 Plan Agent** 写合并计划并执行
5. **GLM 5.2 只做设计/只读咨询**，绝不编写逻辑代码
6. **执行层代码一律用 deepseek-v4-flash**（opencode-go/deepseek-v4-flash）
7. **OMO 派发只认 `opencode-go/*` provider 前缀**
8. **每个 subagent mandate 必须包含 commit 阶段**（防 checkpoint 丢失）
9. **构建后 `git checkout -- "Stardew Valley/Mods"`** 再 commit
10. **不 push**（用户决定何时 push）

---

## 8. 派发模型路由

| 用途 | 模型 | OMO 前缀 |
|---|---|---|
| 执行（代码编写） | deepseek-v4-flash | `opencode-go/deepseek-v4-flash` |
| 视觉/UI | kimi-k3 | `opencode-go/kimi-k3` |
| 设计/架构（oracle 只读） | glm-5.2 | `opencode-go/glm-5.2` |
| 合并编排 | glm-5.2 (Plan agent) | `opencode-go/glm-5.2` |
| ~~minimax~~ | ~~M3/M2.7~~ | **额度耗尽，禁止路由** |

---

## 附录 A — 触点速查

### E5-2/E5-3 触点（from explore）

| 触点 | 文件 | 行号 | 说明 |
|---|---|---|---|
| 喊话插入点 | `Chat\ChatBarRouter.cs` | 101-106 | `present.Count == 0` 早返回 + "远程喊话是 E5"注释 |
| 配额已实现 | `Chat\ChatSessionRegistry.cs` | 163/182/192 | `RecordNpcSpeech`/`GetDailyProactiveCount`/`ClearDailyCounts` + `ChatSessionInitiator.ProactiveSpeech` |
| Day-start hook | `EventHandlerInitializer.cs` | 840 | `OnDayStarted` |
| Time 空桩 | `EventHandlerInitializer.cs` | 1510 | `OnTimeChanged` 空实现 — E5-3 时间触发钩 |
| pre_speak 队列 | `EventHandlerInitializer.cs` | 103 | `_pendingPreSpeakActions` 有消费者无生产者 |
| 话�厅度注入点 | `Chat\ChatBarRouter.cs` | 207 | `GetTalkativeness` + "E5-3 逐人设话痨度配置就位后替换本函数" |
| 作息查询 | `Services\Schedule\NpcScheduleService.cs` | 82-112 | `IsAwake(NPC, int\|StardewTime)` 已注册为 singleton |
| 语音输出 | `Services\ActiveSpeechRouter.cs` | 20 | `Route(NPC, Farmer, string)` 单一出口 |
| LLM 请求模式 | `Chat\ChatBarRouter.cs` | 264 | `SendDialogueAsync` 参考模式 |

### Phase 3 触点

| 触点 | 文件 | 说明 |
|---|---|---|
| 钱包+背包 | `src\ValleyAgent.Abstractions\Inventory\AgentInventory.cs` | 扩展 Money + 原子操作 + 事件 |
| TranscriptSink | `src\ValleyAgent\Protocol\TranscriptSink.cs` | 接线钱包/背包变动写入 |
| 存档 | `src\ValleyAgent\Save\AgentSaveData.cs` + `Save\Models\SaveData.cs` | 钱包持久化 |
| 世界快照 | `src\ValleyAgent\AI\WorldSnapshotBuilder.cs` | E4-1 添加 playerMoney |
| 友谊 | `src\ValleyAgent\Systems\FriendshipSystem.cs` | E4-1 好感修正读取 |
| 右键 | `src\ValleyAgent\Patches\NPCGiftPatch.cs` | 参考 — 新建 NPCRightClickPatch |
| TS 协议 | `D:\Source\ValleyAI\packages\stardew\src\protocol\types.ts` | WorldSnapshot + 消息类型 |
| TS 工具 | `D:\Source\ValleyAI\packages\stardew\src\stardew-tools.ts` | 新增 haggle_reply / accept_job |
| TS Prompt | `D:\Source\ValleyAI\packages\stardew\src\prompt-builder.ts` | 交易/雇佣 prompt 段 |

---

## 附录 B — 门禁命令

### C# 项目（worktree 中）
```powershell
# 构建（需 GamePath 指向主仓库游戏目录）
dotnet build src\ValleyAgent\ValleyAgent.csproj -c Debug -p:GamePath="D:\Source\ValleyTalk\Stardew Valley" --no-incremental --verbosity minimal
dotnet build src\ValleyAgent.Abstractions\ValleyAgent.Abstractions.csproj -c Debug --no-incremental --verbosity minimal
dotnet build src\ValleyTalk.ApiTest\ValleyTalk.ApiTest.csproj -c Debug --no-incremental --verbosity minimal

# 测试
dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj -c Debug --no-build --verbosity minimal

# 构建后清理 mod deploy 覆盖
git checkout -- "Stardew Valley/Mods"
```

### ValleyAI TS
```powershell
cd D:\Source\ValleyAI
bun test packages/stardew
tsc --noEmit
bun run check:protocol
```

### 主 checkout 合并后全量门禁
```powershell
# ValleyTalk 主 checkout
dotnet build src\ValleyAgent\ValleyAgent.csproj --no-incremental --verbosity minimal  # 确认 0 警告 0 错误
dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --no-build --verbosity minimal

# ValleyAI 主 checkout（合并后）
cd D:\Source\ValleyAI
bun test packages/stardew
tsc --noEmit
bun run check:protocol  # 验证 0 DEAD_PIPELINES / 0 SCHEMA_DRIFT
```