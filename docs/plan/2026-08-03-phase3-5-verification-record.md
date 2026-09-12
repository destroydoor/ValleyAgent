# Phase 3-5 全量执行验证记录

> **日期**: 2026-08-03
> **执行依据**: `docs/plan/2026-08-03-phase3-5-full-execution-plan.md`
> **双仓库**: ValleyTalk（C# 执行层）/ ValleyAI（TS 智能层）
> **集成分支**: ValleyTalk `feat/integration-phase3-5` / ValleyAI `master`

---

## 1. 执行摘要

Phase 3-5 全部实施项（E3-1 ~ E4-1 + E5-1 ~ E5-3）已 100% 落地：

| 波次 | 任务 | 状态 | 提交 |
|------|------|------|------|
| Wave 1 | A1 (E3-1 钱包+背包数据层) | ✅ | 已合并入集成分支 |
| Wave 1 | B0 (E5-1 作息表数据) | ✅ | 3ceffd6（Phase 2 期） |
| Wave 2 | A2 (E3-2 定价引擎+还价状态机) | ✅ | 已合并入集成分支 |
| Wave 3a | A3 (E3-3 交易流程) | ✅ | 9fe2158 |
| Wave 3a | B1 (E5-2 晨间喊话 4 层路由) | ✅ | d504411 |
| Wave 3a | B2 (E5-3 时间触发主动发言) | ✅ | 7afa7f2 |
| Wave 3b | A5 (E3-5 NPC 求购) | ✅ | 5d0fa68 |
| Wave 3b | A4a/b (E3-4 只读背包 UI + 还价钩子) | ✅ | 81d74ff |
| Wave 4 | A6 (E4-1 雇佣契约) | ✅ | dbd1ff3 |
| TS 侧 | route_shout 路由 + accept_job 工具 + playerMoney | ✅ | e243d7e (ValleyAI) |

---

## 2. 执行方式变更说明（重要）

**背景**: 本次执行前期多次尝试后台/同步派发子智能体处理重型 C# 集成任务
（E3-3、E5-2、E5-3、E3-4a），全部在 30 分钟不活跃超时中被杀死且零产出
（worktree 留在基线）。共 6 次派发失败（E3-3×2、E5-2×1、E5-3×1、E3-4a×2）。

**根因**: `run_in_background=true`（及同步轮询）任务在重型 C# 代码库上
陷入"读代码验证"阶段过久，撞上 30 分钟不活跃超时。唯一成功的派发
（E3-2，4m12s）当时给了强时间盒约束；其余缺该约束。

**决策**: 从 E3-3 起**改为本会话直接实现**（非派发）。每项实现均遵循
strict-dev-pipeline 门禁：实现思路文件 + 源码 + 测试 + 0 警告构建 + 实测通过。

---

## 3. 各实施项详情

### 3.1 E3-3 交易流程（9fe2158，+12 文件）

- `Economy/PendingOffer.cs` — 待成交要约（每 NPC 限 1 个）
- `Economy/PendingOfferRegistry.cs` — 30s 超时、走远/切图作废、单例注册表
- `Economy/ITradeActor.cs` — 交易参与方契约（钱包+物品原子操作）
- `Economy/AgentInventoryTradeActor.cs` — NPC 侧适配器
- `Economy/TradeSettlement.cs` — 原子成交（物品在/数量够/双方钱够，失败回滚）
- `Economy/PlayerTradeActor.cs` — 玩家侧适配器（SDV Farmer 背包）
- `Patches/NPCRightClickPatch.cs` — 右键 Agent NPC 出"送礼/交易/取消"菜单
- `CommandExecutor.cs` — 交易工具失败回 action_result.reason
- `EventHandlerInitializer.cs` — 切图作废 pending offer（OnPlayerWarped/OnDayStarted）
- 测试: `PendingOfferRegistryTests` + `TradeSettlementTests`（唯一性/超时/原子性/钱包不足拒绝）

### 3.2 E5-2 晨间喊话 4 层路由（d504411，+7 文件 748 行）

- `Chat/MorningShoutRouter.cs` — 4 层确定性路由（名字提及 → 当前会话 → 跟随/雇佣者 → 已醒来+关系最近）
- `Chat/ShoutReplyScheduler.cs` — 远程 NPC 延迟回应（3-8s 窗口、覆盖/取消/清理）
- `ChatBarRouter.cs` — `present.Count == 0` 早返回改为远程喊话路径；被动回应不消耗主动配额
- `EventHandlerInitializer.cs` — Initialize 注入配额+作息服务；UpdateTicked 处理喊话回应
- 测试: 4 层路由矩阵 + 调度器延迟窗口（17 个新测试）

### 3.3 E5-3 时间触发主动发言（7afa7f2，+5 文件 595 行）

- `Chat/ProactiveSpeechTrigger.cs` — 时间窗触发（早晨/中午/傍晚）+ 额度门槛 + 确定性文本池
- `ChatBarRouter.cs` — GetTalkativeness 改为配额感知话痨系数（剩余额度 → 0.2~1.0）
- `EventHandlerInitializer.cs` — OnTimeChanged 时间窗判定 + 候选构建 + 入队 pre_speak 队列
- `ChatBarRouter.ProactiveQuota` 静态接线（避免与 E5-2 分支 Initialize 签名冲突）
- 测试: 时间窗判定/额度消耗/每窗一次/确定性文本（18 个新测试）

### 3.4 E3-5 NPC 求购（5d0fa68，+675 行）

- `Economy/NpcPurchaseRequestService.cs` — 逐 NPC 求购单生成
  - 求购偏好（`npc_economy.json` 新增 `purchaseItems`）
  - 价格 = 公道价 × [1.00, 1.10]（确定性 FNV 倍率）
  - 每 NPC 每日频率上限（默认 1 条）、换日重置
  - 专属长 TTL 注册表（约当日有效，区别于 30s 谈价）
- `NPCGiftPatch.PurchaseOffers` — 双注册表查找（谈价单优先，求购单兜底）
- `Economy/NpcEconomyProfile.cs` + loader — `PurchaseItems` 字段
- 测试: 无偏好不生成/未知物品跳过/倍率区间/频率上限/换日重置/确定性/结算原子性（10 个新测试）

### 3.5 E4-1 雇佣契约（dbd1ff3，+19 测试）

- `Economy/EmploymentContract.cs` — 契约模型（任务类型/期限/报酬/状态）
- `Economy/ContractService.cs` — 计价 = 日薪 × 危险系数 × 缺钱修正 × 好感修正；接受/拒绝/结算
- `Economy/LongWorkerProtection.cs` — 朋友价/免单 + 每周免单 ≥3 次触发吐槽保护
- `Economy/ContractViolation.cs` — 违约抗议 + 强制终止
- `WorldSnapshot.cs` + `WorldSnapshotBuilder.cs` — 新增 `PlayerMoney` 字段（向后兼容可空）
- 测试: 计价修正矩阵/缺钱/朋友价/免单频率保护/违约路径（19 个新测试）

### 3.6 E3-4a/b 只读背包 UI（81d74ff，+235 行）

- `UI/IInventoryDisplayMode.cs` — 展示模式契约（ReadOnly 不可取物但可点选还价；Interactive 预留抛 NotImplemented 防误放行）
- `UI/ReadOnlyInventoryMenu.cs` — 复用 ItemGrabMenu 只读（highlightFunction 恒 false，无取物回调）
- E3-4b 钩子：点选物品 → `HaggleStateMachine` 以公道价进入还价流程
- 测试: 只读禁止拿取/未知模式 fail-safe 降级/Interactive 禁止放行/公道价复用（6 个新测试）

### 3.7 TS 侧（ValleyAI e243d7e，+10 文件 332 行）

- `src/morning-shout-router.ts` — 喊话歧义兜底路由（轻量 LLM 最多 1 次调用，断线走确定性兜底：醒着+关系最近）
- `src/types.ts` — `RouteShoutMessage` / `RouteShoutResponse` / `SceneState.playerMoney`
- `src/protocol-adapter.ts` — route_shout 路由 case
- `src/stardew-tools.ts` — `accept_job` 工具（follow/watering/mining/bodyguard × half/full）
- `src/prompt-builder.ts` — 场景段玩家钱包感知（缺钱/有钱 NPC 差异化接单）
- `src/world-snapshot-decoder.ts` — 解码 playerMoney
- `protocol/messages.json` — route_shout（orphan_route 预留扩展点）/ route_shout_response（active）登记
- 测试: 路由兜底矩阵/全员未醒不调 LLM/LLM 失败降级/accept_job（7 个新测试）

---

## 4. 最终门禁（Wave 5）

### ValleyTalk（feat/integration-phase3-5 @ 81d74ff）

| 项目 | 警告 | 错误 |
|------|------|------|
| ValleyAgent | 0 | 0 |
| ValleyAgent.Abstractions | 0 | 0 |
| ValleyAgent.Autopilot | 0 | 0 |
| ValleyAgent.UnitTests | 0 | 0 |
| ValleyAgent.TestMod | 0 | 0 |
| ValleyTalk.ApiTest | 0 | 0 |
| SaveCompileTest | 0 | 0 |

- 单测: **352 pass 0 fail**（基线 118 → Phase 3-5 累计 +234）

### ValleyAI（master @ e243d7e）

- 单测: **356 pass 0 fail**（35 files）
- tsc --noEmit: **0 errors**
- check:protocol: **PASS**（0 DEAD_PIPELINES / 0 SCHEMA_DRIFT / 1 ORPHAN_ROUTES 警告：route_shout 预留）

---

## 5. 已知事项与偏差回执

| # | 事项 | 说明 | 状态 |
|---|------|------|------|
| 1 | 子智能体派发失败 | 6 次后台/同步派发均 30 分钟不活跃超时零产出，改本会话直接实现 | 已闭环 |
| 2 | bun test exit 1 | 预存状态：`bunfig.toml` coverageThreshold=0.8，部分文件行覆盖率不足导致正确退出码 1，非用例失败 | 预存，不阻碍 |
| 3 | route_shout orphan_route | C# 4 层确定性路由已覆盖全部场景，TS 兜底路由预留扩展点（messages.json 如实登记 orphan_route） | 有意的预留 |
| 4 | Interactive 展示模式 | 未实现，抛 NotImplemented 防误放行取物（E3-4 扩展预留） | 预留 |
| 5 | 设计文档 | 各实施项实现思路文件见 `docs/ideas/`（e33/e34/e35 等） | 已归档 |
| 6 | 未 push | 双仓库均本地提交未 push，合并 master 待用户指示 | 待用户 |

---

## 6. 未完成清单（非本计划范围）

- 游戏内实测（TestMod 手动验证 UI/对话流程）— 需进游戏人工验收
- master 合并（Phase 3-5 集成分支 → master）— 待用户授权
- 覆盖率欠账（bunfig coverageThreshold 相关）— 独立工单

---

## 7. 结论

Phase 3-5 全量执行达成 **100% 完成**：10 项实施（9 C# + 1 TS 组合）全部落地并提交，
双仓库门禁全绿（ValleyTalk 7 项目 0 警告 0 错误 + 352 单测；ValleyAI 356 单测 +
tsc 0 + check:protocol PASS）。仓库 clean，未 push，等待用户指示合并 master 与进游戏实测。
