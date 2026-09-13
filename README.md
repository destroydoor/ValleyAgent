# ValleyAgent — 对ValleyTalk的全面改进，对把agent真正接入星露谷的尝试

ValleyAgent = 一个 Stardew Valley SMAPI mod + 一个本地 TS Agent Server。


它基于ValleyTlak项目的NPC设定和把NPC接入星露谷的理念继续开发，搞定了一些繁琐的程序工作

它想把原版的程序化木偶 NPC 变成**活的虚拟生命**：有真实性格、能记住玩家、有钱有物、能交易、能被雇佣、能和你一起冒险——并且每次对话都独一无二。
（太多agent框架都这么说，但是实际上会面临价格昂贵、效果不佳等等问题，本项目只是尝试了一种之前就有过的解法，也就是靠导演系统驱动，并不是很独创哈。我也不会吹嘘什么）

它100% 单机本地运行：游戏内 C# mod 通过 WebSocket 连接本机 TS 服务器，LLM 调用走你自己配置的 API key，不依赖任何第三方托管服务。

## 核心特性

- **AI 对话** — 与 NPC 的每段对话由 LLM 实时生成；对话内容影响好感度与 NPC 后续行为，NPC 会记住你说过的重要的话
- **真实性格** — 人设档案 + 三层记忆（长期记忆 RAG 检索 / 状态摘要强制注入 / 临时剧本）确保言行符合角色
- **NPC 经济系统** — NPC 有自己的钱包、背包、情绪；支持交易、送礼、雇佣收款。权威账本在 TS 端，C# 端做物理校验（真实余额/背包空间/物品存在性），每次资产变动都有指令 + 回执闭环
- **共同冒险** — NPC 可接 `set_goal` 和你一起砍树、挖矿、浇水、战斗、跟随（C# GoalExecutor 确定性执行，零 LLM 循环；另带低血量逃跑、卡住检测等生存反射）
- **导演编排** — Director 智能体基于全局视野编排事件、创造相遇机会、改写 NPC 状态；NPC Agent 永远不知道导演存在（职责隔离）
- **联机支持** — Host / ThinClient 双模式，房客也能与 AI NPC 对话、送礼、交易，经济账本主机权威
（基本的agent功能都有了，就是手动测试还是不算特别全面不保证完全没bug哈）

## 架构

```
┌─────────────────────────────────────────────────────┐
│ Director（导演智能体，TS）                          │
│ 编故事 / 编排事件 / 改 NPC 状态；不进 NPC 上下文    │
└───────────────┬─────────────────────────────────────┘
                │ 单向：调 C# 工具改状态数据
┌───────────────▼─────────────────────────────────────┐
│ C# 状态层（零 LLM，游戏进程内）                     │
│ AgentBrain 黑板 / GoalExecutor 执行 / 生存反射      │
│ adjust 执行器（物理校验+原子变更）/ 断线 Outbox     │
└───────────────┬─────────────────────────────────────┘
                │ 单向：worldSnapshot 拼装（WebSocket）
┌───────────────▼─────────────────────────────────────┐
│ NPC Agent（角色扮演智能体，TS）                     │
│ 人设 + L2 状态 + L1 记忆 + beat 场景 → 纯角色扮演   │
│ 工具：speak/emote/give_item/trade/set_goal/...      │
└─────────────────────────────────────────────────────┘
```

关键原则：NPC Agent 与 Director 之间**永远没有直接消息往来**，全部经由 C# 状态层单向流动；账本与叙事在 TS，反射与执行在 C#。

## 仓库结构

| 目录 | 内容 |
|---|---|
| `src/ValleyAgent/` | C# SMAPI mod 主体（游戏内状态层、执行器、联机中继） |
| `src/ValleyAgent.UnitTests/` | C# 单元测试（含联机虚拟环境测试） |
| `src/ValleyAgent.TestMod/` `src/ValleyAgent.Autopilot/` | 测试 mod / 自动驾驶 harness（游戏内自动化测试） |
| `server/` | TS Agent Server（bun workspace：`packages/core` 通用 Agent 框架 + `packages/stardew` 星露谷实现——NPC/Director LLM 编排、经济账本、情绪引擎、记忆） |
| `server/protocol/messages.json` | C# ↔ TS WebSocket 协议单一事实源 |
| `docs/` | 设计文档、执行计划、架构决策与排查记录 |
| `scripts/` | 构建 / 部署 / 测试脚本 |
| `docker/` | 游戏内集成测试与联机 E2E 的容器化环境 |
| `ValleyTalk for SVE/` | Stardew Valley Expanded 兼容 NPC 人设数据 |
| `tools/` | 自研辅助工具（PlayerInputDriver / XnbExtract / player_eval） |

## 环境要求

- Stardew Valley 1.6（Steam/GOG）+ [SMAPI](https://smapi.io/) 4.x
- .NET 8 SDK
- [Bun](https://bun.sh)（TS Agent Server）
- 任一 OpenAI 兼容 LLM 端点（开发期在 MiniMax / DeepSeek / 商汤 sensenova / 小米 mimo 上测试过）

## 构建与运行

```powershell
# 1. TS Agent Server（编译为单文件 exe）
cd server
bun install
cd packages/stardew
bun build --compile --target=bun-windows-x64 src/cli.ts --outfile bin/valley-ai-server.exe

# 2. C# mod
dotnet build src/ValleyAgent/ValleyAgent.csproj -c Debug

# 3. 部署到游戏 Mods 目录（DLL/exe/i18n/RAG/人设数据）
#    注意按需修改脚本内的游戏路径
pwsh scripts/build/deploy.ps1
```

**API key 配置**：复制 `scripts/secrets.local.ps1.template` 为 `scripts/secrets.local.ps1`，填入你的 key（该文件已被 gitignore，真实 key 绝不入库）。也可在游戏内 GMCM 菜单或 `Mods/ValleyAgent/config.json` 中配置。

## 测试

```powershell
# C# 单元测试
dotnet test src/ValleyAgent.UnitTests

# TS 单元测试 + 类型检查 + 协议契约检查
cd server
bun test
bun run typecheck
bun run check:protocol
```

联机 E2E（Docker 三容器：主机 + 两房客，真实 LLM）：见 `docker/docker-compose.e2e.yml` 与 `scripts/test/`。

## 设计文档

架构与决策记录大多为中文，见 `docs/`：

- `docs/design/2026-08-05-three-tier-architecture-redesign.md` — 三层架构总设计
- `docs/design/2026-08-15-ts-ledger-reflex-architecture.md` — 经济账本迁 TS + C# 反射执行
- `docs/design/2026-08-16-multiplayer-boundary-analysis.md` — 联机边界分析
- `AGENTS.md` — 项目全景指南（设计哲学、已知坑、开发规范）

## 已知限制

- 玩家名录（换日导演编排用）是内存观察式的：TS 服务器重启后需等玩家说过话才重新认识他，
  重启当天的 morningPlan 退化为单玩家编排（画像本身持久化，不丢数据）
- 玩家画像的行为层/活动日志仍是世界级（C# 未上报 per-player 活动；`activity_report` 协议未接线），
  联机下各玩家的日常统计暂不分离
- 导演的 game_context 是世界级快照（主机玩家状态），per-player 只做到画像与编排分层
- 房客不支持"喊不在场的 NPC"（远程喊话依赖主机侧 Agent 全员名单），只能对在场 NPC 说话

## 声明

本项目为爱好者作品，与 ConcernedApe / Chucklefish 无关，未分发任何游戏本体资源。Stardew Valley © ConcernedApe。使用任何 LLM 端点请遵守对应服务商条款。
