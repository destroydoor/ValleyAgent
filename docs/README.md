# ValleyAgent 项目文档索引

> **当前架构**：C# SMAPI mod（游戏内执行层）+ TS Agent Server（智能层，**源码在本仓 `server/`**，
> 编译为 `valley-ai-server.exe` 由 mod 自动拉起）。权威架构文档见根目录 [`AGENTS.md`](../AGENTS.md)。
> **最近一次文档↔代码偏移审查**：[`plan/2026-09-15-doc-code-drift-audit.md`](plan/2026-09-15-doc-code-drift-audit.md)。

## 阅读约定（重要）

- **占位符**：2026-09 之前的文档按"双仓库（ValleyTalk + ValleyAI）"时代撰写，正文里的
  `<REPO_ROOT>` 指**本仓库根**、`<VALLEYAI_ROOT>` 指 **TS 工作区（今天的 `server/`）**。
  两个占位符均未展开成真实路径（全仓约 700 处），读到即按上述映射理解；脚本侧已自动适配
  （`scripts/lib/paths.ps1` 优先 `server/`，环境变量 `VALLEYAI_ROOT` 仅作 override）。
- **归档约定**：`archive/` 下是 Python 时代产物，只作历史参考，不代表现状。

## 核心文档（项目根目录）

| 文件 | 说明 |
|------|------|
| [../AGENTS.md](../AGENTS.md) | **AI 助手指南（权威）** — 愿景、三层架构、设计原则、协议、已知坑、开发规范与验证门槛 |
| [../README.md](../README.md) | **项目说明** — 安装、构建、运行、测试、已知限制（面向人类） |
| [../scripts/README.md](../scripts/README.md) | 脚本目录说明（构建 / 测试 / 工具） |
| [../scripts/TEST_README.md](../scripts/TEST_README.md) | 测试说明（TS 单元测试 + 游戏集成测试） |

## 设计文档（`design/`，13 篇）

| 文件 | 说明 |
|------|------|
| [2026-09-13-agent-body-refactor.md](design/2026-09-13-agent-body-refactor.md) | **最新** — Agent 语义从"身份"改为"身体"（issue #9 / PR2） |
| [2026-09-13-dialogue-continuity-fixes.md](design/2026-09-13-dialogue-continuity-fixes.md) | 对话连续性修复（issue #8 / PR1） |
| [2026-09-13-m3-multiplayer-director-emotion.md](design/2026-09-13-m3-multiplayer-director-emotion.md) | M3 多玩家化（情绪 per-player / 房客菜单 / 玩家画像）；**验收口径见其 §5** |
| [2026-08-17-ledger-fixes-and-m2-multiplayer-context.md](design/2026-08-17-ledger-fixes-and-m2-multiplayer-context.md) | 账本 P1 修复 + M2 多玩家上下文 |
| [2026-08-16-multiplayer-boundary-analysis.md](design/2026-08-16-multiplayer-boundary-analysis.md) | 联机异常边界与约束分析（M1/M2/M3 来源） |
| [2026-08-15-ts-ledger-reflex-architecture.md](design/2026-08-15-ts-ledger-reflex-architecture.md) | **账本权威修订设计** — 经济账本/情绪引擎迁 TS（四步全部落地） |
| [2026-08-05-three-tier-architecture-redesign.md](design/2026-08-05-three-tier-architecture-redesign.md) | 三层架构基础设计（**§2.1 账本归属已被 2026-08-15 文档推翻**，见文件顶栏警告） |
| [2026-08-03-ts-server-logging-design.md](design/2026-08-03-ts-server-logging-design.md) | TS 服务器日志可观测性设计 |
| [2026-08-03-multi-llm-provider-design.md](design/2026-08-03-multi-llm-provider-design.md) | 多 LLM Provider 支持设计 |
| [2026-08-02-npc-economy-hire-chat-requirements.md](design/2026-08-02-npc-economy-hire-chat-requirements.md) | NPC 经济 / 雇佣 / 聊天栏需求 |
| [2026-08-01-npc-feedback-architecture.md](design/2026-08-01-npc-feedback-architecture.md) | 行为反馈闭环 + 三场景架构 + 强制回执 |
| [2026-08-01-test-scoring-redesign.md](design/2026-08-01-test-scoring-redesign.md) | 测试与打分体系重设计（不变量 + 故障注入） |
| [2026-08-01-memory-narrative-extensibility.md](design/2026-08-01-memory-narrative-extensibility.md) | 长期记忆、留痕、叙事引擎与扩展性 |

## 执行计划与验证记录（`plan/`）

| 文件 | 说明 |
|------|------|
| [2026-09-15-doc-code-drift-audit.md](plan/2026-09-15-doc-code-drift-audit.md) | **文档↔代码偏移度审查报告（最新）** — 46 条声明取证 + 残留清单 + 待办 |
| [2026-09-14-dead-assertion-cleanup-report.md](plan/2026-09-14-dead-assertion-cleanup-report.md) | 死断言清理执行报告（281 条候选分流；配套 [plan](plan/2026-09-14-dead-assertion-cleanup-plan.md)） |
| [2026-09-10-host-freeze-root-cause.md](plan/2026-09-10-host-freeze-root-cause.md) | 主机卡死复刻与原因定位（结论文档；附录 B 猜想台账范式） |
| [2026-09-10-host-freeze-repro-plan.md](plan/2026-09-10-host-freeze-repro-plan.md) | 主机卡死复刻计划 |
| [2026-09-09-multiplayer-client-parity-and-freeze-hunt-plan.md](plan/2026-09-09-multiplayer-client-parity-and-freeze-hunt-plan.md) | 房客可用性修复 + 卡死猎捕计划 |
| [2026-08-20-test-system-redesign.md](plan/2026-08-20-test-system-redesign.md) | 测试系统大改：三层判定 + 双轨报告 |
| [2026-08-19-docker-multiplayer-e2e-plan.md](plan/2026-08-19-docker-multiplayer-e2e-plan.md) | Docker 联机 E2E 容器化计划 |
| [2026-08-17-test-audit-remediation-plan.md](plan/2026-08-17-test-audit-remediation-plan.md) | 测试审计整改计划 |
| [2026-08-17-docker-test-containerization-plan.md](plan/2026-08-17-docker-test-containerization-plan.md) | Docker 测试容器化计划 |
| [2026-08-09-phase1-3-v3-special-tests-verification.md](plan/2026-08-09-phase1-3-v3-special-tests-verification.md) | V3 三阶段专项测试验证记录 |
| [2026-08-06-phase3-director-verification.md](plan/2026-08-06-phase3-director-verification.md) | 阶段 3 验证（Director 工具集 + L2）（另见 [阶段 1](plan/2026-08-06-phase1-trade-bugfix-verification.md) / [阶段 2](plan/2026-08-06-phase2-setgoal-verification.md)） |
| [2026-08-06-execution-audit-report.md](plan/2026-08-06-execution-audit-report.md) | 三层架构实施 vs 计划逐条对照 |
| [2026-08-06-deploy-v3-test-record.md](plan/2026-08-06-deploy-v3-test-record.md) | 部署 + 游戏内 V3 测试记录 |
| [2026-08-05-three-tier-architecture-execution-plan.md](plan/2026-08-05-three-tier-architecture-execution-plan.md) | **三层架构执行计划**（三阶段：交易 bug / set_goal / 大重构） |
| [2026-08-03-ts-server-logging-plan.md](plan/2026-08-03-ts-server-logging-plan.md) | TS 服务器日志实施计划 |
| [2026-08-03-phase3-5-full-execution-plan.md](plan/2026-08-03-phase3-5-full-execution-plan.md) | Phase 3-5 执行计划（配套 [验证记录](plan/2026-08-03-phase3-5-verification-record.md)） |
| [2026-08-03-phase0-verification-record.md](plan/2026-08-03-phase0-verification-record.md) | Phase 0 架构债务收尾验证 |
| [2026-08-03-e1-1-transcript-store-verification-record.md](plan/2026-08-03-e1-1-transcript-store-verification-record.md) | E1-1 全量留痕（TranscriptStore）验证 |
| [2026-08-03-merge-e22-e23-e51-verification-record.md](plan/2026-08-03-merge-e22-e23-e51-verification-record.md) | E2-2/E2-3/E5-1 合并验证 |
| [2026-08-02-execution-plan.md](plan/2026-08-02-execution-plan.md) | 旧执行计划（部分被 2026-08-05 计划取代） |

## 思路与提案（`ideas/`、`思路/`）

| 文件 | 说明 |
|------|------|
| [ideas/e31](ideas/e31-implementation-思路.md) / [e32](ideas/e32-implementation-思路.md) / [e33](ideas/e33-implementation-思路.md) / [e34](ideas/e34-implementation-思路.md) / [e35](ideas/e35-implementation-思路.md) | E3 经济：NPC 钱包背包 / 定价与还价状态机 / 交易流程 / 只读背包 UI / 求购 |
| [ideas/e41](ideas/e41-implementation-思路.md) / [e52](ideas/e52-implementation-思路.md) / [e53](ideas/e53-implementation-思路.md) / [quota](ideas/quota-implementation-思路.md) | E4-1 雇佣 / E5-2 晨间喊话 / E5-3 主动发言 / 发言额度服务 |
| [ideas/phase1](ideas/phase1-trade-bugfix-思路.md) / [phase2](ideas/phase2-set-goal-executor-思路.md) / [phase3](ideas/phase3-director-l2-default-思路.md) | 三层架构三阶段的实现思路 |
| [ideas/2026-08-03-merge-plan-e22-e23-e51.md](ideas/2026-08-03-merge-plan-e22-e23-e51.md) | E2-2/E2-3/E5-1 合并计划 |
| [思路/2026-08-02-P0架构改进实施思路.md](思路/2026-08-02-P0架构改进实施思路.md) | P0 架构改进实施思路 |

## 规范（`spec_*`、`superpowers/`）

| 文件 | 说明 |
|------|------|
| [spec_npc_arc/](spec_npc_arc/) | NPC 弧线故事系统规范（spec / tasks / checklist） |
| [spec_autopilot/](spec_autopilot/) | Autopilot 自动驾驶模式规范（spec / plan） |
| [superpowers/specs/2026-07-08-valleyai-ts-server-design.md](superpowers/specs/2026-07-08-valleyai-ts-server-design.md) | TS Agent Server 设计规范（已实现，源码在 `server/`） |
| [superpowers/plans/2026-07-08-valleyai-core-plan.md](superpowers/plans/2026-07-08-valleyai-core-plan.md) | `@valley/core` 实现计划（已实现） |
| [superpowers/specs/2026-07-21-narrative-director-design.md](superpowers/specs/2026-07-21-narrative-director-design.md) · [plan](superpowers/plans/2026-07-21-narrative-director.md) | 叙事导演引擎设计/计划（**旧叙事线已于 2026-09-14 砍除**，仅存 C# 工具层就绪） |
| [superpowers/specs/2026-07-18-p3-multiplayer-architecture-options.md](superpowers/specs/2026-07-18-p3-multiplayer-architecture-options.md) · [plan](superpowers/plans/2026-07-18-multiplayer-sync-host-authoritative.md) | 联机架构选型与主机权威实施计划（已落地） |
| [superpowers/plans/2026-07-18-p0-dialogue-pipeline.md](superpowers/plans/2026-07-18-p0-dialogue-pipeline.md) · [p1](superpowers/plans/2026-07-18-p1-scene-multiplayer-guards.md) · [p2](superpowers/plans/2026-07-18-p2-memory-tool-loop.md) | 对话管线 / 场景联机守卫 / 记忆工具循环（历史计划） |
| [superpowers/specs/2026-07-18-findings-fix-design.md](superpowers/specs/2026-07-18-findings-fix-design.md) | 调查结论与修复设计（历史） |
| [superpowers/specs/2026-08-03-interaction-driven-agent-allocation-design.md](superpowers/specs/2026-08-03-interaction-driven-agent-allocation-design.md) · [plan](superpowers/plans/2026-08-03-interaction-driven-agent-allocation.md) | 交互式分配与三档容量（已被"身体池"语义取代，见 agent-body-refactor） |
| [superpowers/plans/2026-08-03-multi-llm-provider.md](superpowers/plans/2026-08-03-multi-llm-provider.md) | 多 LLM Provider 实施计划（已落地） |

## 端到端日志

| 文件 | 说明 |
|------|------|
| [valley-core-e2e-log.md](valley-core-e2e-log.md) | `@valley/core` E2E 日志 |
| [valley-core-stardew-e2e-log.md](valley-core-stardew-e2e-log.md) | `@valley/stardew` E2E 日志 |

## 归档（`archive/`，历史参考，不再维护）

| 文件 | 说明 |
|------|------|
| [archive/AGENTS-2026-08-02-v4.3-full.md](archive/AGENTS-2026-08-02-v4.3-full.md) | 旧版完整 AGENTS（模块矩阵、状态机细节、常量表） |
| [archive/ARCHITECTURE.md](archive/ARCHITECTURE.md) · [API.md](archive/API.md) · [TESTING.md](archive/TESTING.md) | Python 时代的架构 / 接口 / 测试文档 |
| [archive/PLAN.md](archive/PLAN.md) · [ROADMAP.md](archive/ROADMAP.md) · [decoupling-plan.md](archive/decoupling-plan.md) | 历史计划 / 路线图 / 解耦计划 |
| [archive/valleytalk-*.md](archive/) · [archive/v3-test-fix-plan*.md](archive/) | 早期升级、反向工程与 v3 测试修复稿 |
| [v4.1-refactor-progress.md](v4.1-refactor-progress.md) · [refactor-plan-2026-06-21.md](refactor-plan-2026-06-21.md) · [agent-runtime-flow.md](agent-runtime-flow.md) | 历史进度与运行时流程图（v4.1 时代） |

> **更新提示**：本索引由 2026-09-15 偏移审查后重建（此前只覆盖 14/74 篇非归档文档，
> 且含一条指向已删除文件的死链）。新增文档请同步登记到本文件对应分区。
