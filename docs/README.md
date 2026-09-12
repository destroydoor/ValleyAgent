# ValleyTalk 项目文档

> **当前架构**：v4.3 — TS 智能层（`valley-ai-server.exe`）+ C# 执行层。
> 权威架构文档见根目录 [`AGENTS.md`](../AGENTS.md)。

## 核心文档（项目根目录）

| 文件 | 说明 |
|------|------|
| [../AGENTS.md](../AGENTS.md) | **AI 助手指南** — 项目愿景、架构、设计原则、模块职责矩阵、LLM 集成规范、TS Agent Server 架构（v4.3 现状） |
| [../README.md](../README.md) | **项目说明** — 安装、配置、运行方式（面向人类开发者） |
| [../TS服务器重写-上下文交接.md](../TS服务器重写-上下文交接.md) | TS 服务器重写的设计阶段交接文档（已完成，转为历史归档） |
| [../scripts/README.md](../scripts/README.md) | 脚本目录说明（构建/测试/工具，TS Agent Server 自动拉起） |
| [../scripts/TEST_README.md](../scripts/TEST_README.md) | 测试文档（TS Agent Server 单元测试 + 游戏集成测试） |

## 当前架构与运行时

| 文件 | 说明 |
|------|------|
| [agent-runtime-flow.md](agent-runtime-flow.md) | **Agent 运行时流程图** — 历史文档，描述 v4.1 的 NPC 分配/Tick/决策流程（v4.3 已重构为 dialogue 触发模型，参考 AGENTS.md 第 2.2 节） |

## 开发计划与历史进度

| 文件 | 说明 |
|------|------|
| [v4.1-refactor-progress.md](v4.1-refactor-progress.md) | v4.1 重构进度报告 — 历史文档，描述 P0/P1 重构（Python 时代，已被 v4.3 TS 重构取代） |
| [refactor-plan-2026-06-21.md](refactor-plan-2026-06-21.md) | 2026-06-21 系统性重构计划 — 历史文档，描述分阶段修复方案 |

## 规范文档

| 文件 | 说明 |
|------|------|
| [spec_npc_arc/](spec_npc_arc/) | NPC 弧线故事系统设计规范（spec.md / tasks.md / checklist.md） |
| [spec_autopilot/](spec_autopilot/) | Autopilot 自动驾驶模式设计规范（spec.md / plan.md） |
| [superpowers/specs/2026-07-08-valleyai-ts-server-design.md](superpowers/specs/2026-07-08-valleyai-ts-server-design.md) | **TS Agent Server 设计规范** — 设计阶段文档，已实现（参考 `<VALLEYAI_ROOT>`） |
| [superpowers/plans/2026-07-08-valleyai-core-plan.md](superpowers/plans/2026-07-08-valleyai-core-plan.md) | TS Agent Server 实现计划 — 已实现 |
| [superpowers/specs/2026-07-21-narrative-director-design.md](superpowers/specs/2026-07-21-narrative-director-design.md) | 叙事导演系统设计 |
| [superpowers/plans/2026-07-18-p0-dialogue-pipeline.md](superpowers/plans/2026-07-18-p0-dialogue-pipeline.md) | 对话管线 P0 实现计划（历史） |
| [superpowers/plans/2026-07-18-p1-scene-multiplayer-guards.md](superpowers/plans/2026-07-18-p1-scene-multiplayer-guards.md) | 多人游戏场景守卫计划（历史） |
| [superpowers/plans/2026-07-18-p2-memory-tool-loop.md](superpowers/plans/2026-07-18-p2-memory-tool-loop.md) | 记忆/工具循环计划（历史） |
| [superpowers/plans/2026-07-18-multiplayer-sync-host-authoritative.md](superpowers/plans/2026-07-18-multiplayer-sync-host-authoritative.md) | 多人游戏 Host 权威同步计划 |
| [superpowers/specs/2026-07-18-p3-multiplayer-architecture-options.md](superpowers/specs/2026-07-18-p3-multiplayer-architecture-options.md) | 多人游戏架构选项 |
| [superpowers/specs/2026-07-18-findings-fix-design.md](superpowers/specs/2026-07-18-findings-fix-design.md) | 调查结论与修复设计（历史） |

## 端到端日志

| 文件 | 说明 |
|------|------|
| [valley-core-e2e-log.md](valley-core-e2e-log.md) | `@valley/core` 端到端测试日志 |
| [valley-core-stardew-e2e-log.md](valley-core-stardew-e2e-log.md) | `@valley/stardew` 端到端测试日志 |

## 归档（历史参考，不再维护）

| 目录/文件 | 说明 |
|------|------|
| [archive/](archive/) | 历史文档归档目录 |
| [archive/ARCHITECTURE.md](archive/ARCHITECTURE.md) | 原始架构文档（Python 时代） |
| [archive/decoupling-plan.md](archive/decoupling-plan.md) | Python 服务器 + C# 模组解耦计划 |
| [archive/PLAN.md](archive/PLAN.md) | 历史开发计划 |
| [archive/ROADMAP.md](archive/ROADMAP.md) | 历史版本路线图 |
| [archive/API.md](archive/API.md) | 历史 API 接口文档 |
| [archive/TESTING.md](archive/TESTING.md) | 历史测试策略 |
| [archive/v3-test-fix-plan*.md](archive/) | v3 测试修复计划（已过时） |
| [archive/valleytalk-*.md](archive/) | 早期升级/反向工程讨论稿 |

> **更新提示**：本文档由 AI 在 v4.3 重构后整理。`archive/` 下的文档均为 Python
> 时代或更早期产物，仅供历史参考；任何架构性决策请以 [`AGENTS.md`](../AGENTS.md)
> 为准。
