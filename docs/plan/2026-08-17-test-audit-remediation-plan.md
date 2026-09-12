# 测试审计整改执行计划（2026-08-17）

> **状态**：已批准，实施中
> **依据**：2026-08-17 测试审计报告（会话内产出；C# 执行器 + ValleyAI harness 覆盖度/正确性审计）
> **范围**：ValleyTalk（C# 执行器）与 ValleyAI（agent 框架）两仓库

## 审计结论修正

- P1#1（sendAdjust 超时回滚 vs C# 幂等缓存契约不一致）**已在 2026-08-17 修复**：
  `protocol-adapter.ts:377-424` 超时保留 pending + `scheduleReconcile` 30s 凭原 instructionId 重发，
  LLM 回执带 "do not repeat payment"；配套测试 3 条（`protocol-adapter-ledger.test.ts:163/466/536`）。
  **不纳入本计划**，仅更新记忆文件。
- P1#2（give_gift 账本键错位）、P1#3（AgentInventory.TryAdd / PlayerRemoveItem 非原子）：仍未修，批次 B。
- playerId 联机路由：C# 侧 IT14 覆盖完整（own-id/ghost-id/缺省三分支）；**TS 侧测试为零**，批次 A。
- TS 侧存在假测试（abort/followUp/types 自证）、teardown 泄漏、死代码，批次 D/E。

## 前置阻塞（用户）

C 盘 0MB 可用（507G/507G）。不清理则 dotnet build / bun test 随机失败。开工前先清理。

## 批次 A：playerId 契约测试（纯测试，零生产代码，最高优先）

- **A1 TS**：
  - `protocol-roundtrip.test.ts`：execute_adjust / adjust_result / reconnect_sync 往返（playerId 有/无）
  - `protocol-adapter-ledger.test.ts` 4 例：dialogue.playerId → execute_adjust 透传；adjust_result playerId echo；
    playerNotFound 失败码 → 账本 pending 回滚；reconnect_sync 重发保留原 playerId
- **A2 C#**（`AdjustExecutorTests.cs`）：指定不存在 playerId → PlayerNotFound 整批零副作用；
  若单测环境 `Game1.GetPlayer` 抛异常，该分支退回仅 IT14 覆盖并如实记录

## 批次 B：执行器正确性修复

| # | 改动 | 文件 |
|---|---|---|
| B1 | `PlayerRemoveItem` 原子化（先数足量再扣）+ 单测 | ValleyTalk `Economy/AdjustExecutor.cs` |
| B2 | `AgentInventory.TryAdd` 原子化（先找空槽再合并）+ 单测 | ValleyTalk `Inventory/AgentInventory.cs` |
| B3 | 环形缓存防重复入队 + `Execute` XML 注释标注"仅主线程调用"约定 | ValleyTalk `Economy/AdjustExecutor.cs` |
| B4 | 预算超限抛不可重试错误（现抛普通 Error 被重试→多打真实 API）+ 单测 | ValleyAI `packages/core/src/llm-provider.ts` |
| B5 | outbox 超 100 条丢弃时加 Warn 日志 + 调整测试断言 | ValleyTalk `WebSocket/WebSocketClient.cs` |
| B6 | 修过期/误导注释（:440「超时已回滚」、stardew-agent ReActGuard 声称未接线） | ValleyAI 两文件 |

## 批次 C：行为补全

`EmotionEngine.resetDay` 接入 `handleDayStarted`（迁移步骤 3 半成品：引擎枚举 day_started、resetDay
产出 source:"day_started"，但 handler 从未调用）。接线后情绪跨日重置回 baseline。+ 1 条测试。

## 批次 D：测试质量修复（ValleyAI）

| # | 问题 | 位置 |
|---|---|---|
| D1 | abort 假测试（agent_end 无条件发射） | `agent-loop.test.ts:156-173` |
| D2 | followUp 假测试（drainFollowUps 变 no-op 也过） | `agent.test.ts:67-77` |
| D3 | teardown 泄漏（unawaited pending 10s 后重建已删目录 + 活 timer） | `protocol-adapter-ledger.test.ts:518-559` |
| D4 | 时序脆弱（80ms/10ms sleep + 非空断言） | 同文件 :416/:363 |
| D5 | types.test.ts 自证测试 | `types.test.ts` 全文件 |

## 批次 E：死代码清理（ValleyAI）

- E1 删 `_llm-provider-new.ts`（0 字节）、`src/_test_write_check.txt`、`tests/_test_write_check.txt`
- E2 删 `AfterToolCallResult.skipFollowUp`（声明后无读取）
- E3 删 `LlmRouterConfig.global` 消费死路（有配置依赖则保留字段删消费注释）
- 每删一项跑 `tsc --noEmit` 确认零消费者
- **明确不删**：react-guard.ts（2026-08-01 文档结论"保留"，待 Director 接线）；`EmotionEngine.applyMood`
  （批次 C 后评估，仍无调用方则删）

## 批次 F：记忆更新

`known-unfixed-audit-bugs.md`：P1#1 标记已修（2026-08-17 对账闭环 + 测试），P1#2/P1#3 状态刷新。

## 执行顺序与提交

A → B → C → D → E → F。两仓库分开提交，前缀 `test(audit):` / `fix(executor):` / `refactor(cleanup):`。

## 验证门槛（每批次结束）

- ValleyAI：`bun test` + `tsc --noEmit` + `bun run check:protocol` 全绿
- ValleyTalk：dotnet 编译 0 警告 + `dotnet test` 全绿
- 游戏内回归（批次 B 后，用户自开）：IT14 + 一次真实交易对话
