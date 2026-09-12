# ValleyAI — TypeScript游戏AI框架设计文档

> **状态（2026-07-26 更新）：✅ 已实现**
>
> TS Agent Server（`valley-ai-server.exe`）已经实现并替换了原 Python 服务器。
> 本文档为设计阶段产物，保留作为架构决策依据。
>
> **当前架构权威文档**：[`../../../AGENTS.md`](../../../AGENTS.md) 第 2.1.1 节
> "TS Agent 服务器架构（v4.3 现状）"。
>
> **TS 服务器源码**：`D:\Source\ValleyAI`（bun workspace：`packages/core`
> 抽象框架 + `packages/stardew` 星露谷实现）。

---

> **创建时间**: 2026-07-08
> **状态**: 设计阶段（待用户审核）
> **项目位置**: `D:\Source\ValleyAI`（新建）
> **现有项目**: `D:\Source\ValleyTalk`（Python服务器 + C# Mod）

---

## 1. 概述

### 1.1 目标

将现有Python AI服务器（~12,000行，31模块）重写为TypeScript，做成一个通用游戏AI框架，参考pi-0.80.3的分层架构和极简内核哲学。框架分两层：

1. **抽象Agent执行框架和通用系统**（`@valley/core`）
2. **具体游戏实现**（`@valley/stardew`）

加上游戏内C# Mod作为纯执行层，Mod只接收服务器指令做实现。

### 1.2 已对齐的决策

| 决策 | 选择 | 理由 |
|------|------|------|
| NPC行为规划模型 | FSM + Handler（保持现有） | 最低风险，7状态FSM + Handler ReAct循环 |
| 通用框架范围 | 任何有NPC的游戏（最通用） | core层尽量薄，原语接口+默认实现 |
| 运行时分发 | Bun打包成exe | 零配置，启动快，Windows原生 |
| WebSocket协议 | 内部抽象+外部保留 | C# Mod几乎不用改 |
| 架构方案 | 方案A（pi风格极薄内核）+ 原语接口 | core极薄但提供游戏AI原语接口+默认实现 |

### 1.3 技术栈

| 层 | 技术 | 理由 |
|----|------|------|
| LLM抽象 | Vercel AI SDK v5+ | Bun兼容，多provider，避免LangChain.js的--compile问题 |
| WebSocket | Bun原生 Bun.serve().websocket | 5-8x快于ws，内置pub/sub，零依赖 |
| 持久化 | bun:sqlite（WAL模式） | 3-6x快于better-sqlite3，零native binding |
| 向量记忆 | Qdrant REST sidecar 或 SQLite余弦fallback | 纯fetch，无native依赖；LanceDB有Arrow IPC边界bug |
| 构建 | bun build --compile | 单文件exe，~40-50MB |

### 1.4 铁律约束

- 绝对不允许忽视警告，静态检测器必须0报错0警告才能交付
- 绝对不能依靠任何方式放宽检测规则绕过测试或检查器
- 绝对不能交付不经实际运行测试的代码
- 实现功能时绝对不能只实现占位符
- 工具执行必须考虑超时和失败的基础错误处理
- 不讨好用户，冷静客观回复

### 1.5 项目理念对齐（goap.md验证）

| goap.md条目 | 设计覆盖 |
|-------------|----------|
| NPC有情绪 | core EmotionSystem接口 + stardew 8情绪枚举+权重表 |
| NPC有记忆 | core MemoryBackend接口 + stardew 3层衰减+SignificantMemory |
| NPC有目标 | core GoalSystem/InternalWorld接口 + stardew 每日计划+人格 |
| NPC会受伤会死 | core HealthSystem接口 + stardew 血量/死亡/复活 |
| 决策不阻塞行动 | agentLoop双队列（steering+followUp），异步非阻塞 |
| 仪式感 | Handler收尾仪式（emote+说话），closingRitual抽象方法 |
| 真实性格/RAG约束 | PromptBuilder + RAGEngine + OutputValidator |
| 性能优先 | CircuitBreaker + TokenBudgetManager + PerformanceMonitor |

---

## 2. 包结构

### 2.1 目录布局

```
D:\Source\ValleyAI\
├── packages/
│   ├── core/                    @valley/core
│   └── stardew/                 @valley/stardew
├── package.json                 npm workspaces root
├── tsconfig.base.json           共享TS配置
├── bunfig.toml                  Bun配置
└── README.md
```

### 2.2 依赖方向（严格单向）

```
@valley/stardew  ->  @valley/core  ->  外部依赖(ai-sdk, bun:sqlite)
                      core不依赖stardew
                      core不依赖任何游戏特定类型
```

### 2.3 @valley/core 模块清单（~5000行预估）

| 模块 | 职责 |
|------|------|
| Agent | 有状态Agent包装器（订阅/双队列/生命周期） |
| agentLoop | 纯函数执行循环（流式->工具调用->hook），异步非阻塞 |
| EventStream | AgentEvent生命周期事件流 |
| ToolRegistry | 工具注册 + schema验证 + 可见性分级(llm_visible/tactical/local) |
| LLMProvider | Vercel AI SDK包装（多provider + 重试/熔断/semaphore并发/token预算） |
| Transport | WebSocket传输接口 + Bun原生实现 |
| CircuitBreaker | 3态熔断器(CLOSED/OPEN/HALF_OPEN) |
| TokenBudgetManager | Token预算追踪 |
| PerformanceMonitor | 操作耗时监控 |
| EmotionSystem | 情绪状态+事件+行为权重映射接口+默认实现 |
| MemoryBackend | 3层衰减+SignificantMemory+remember/forget接口+默认实现 |
| AffectionSystem | 阶段映射+评估接口+默认实现 |
| GoalSystem | InternalWorld(personality/plan/profile)接口 |
| HealthSystem | 血量/死亡/复活事件接口+默认实现 |
| VectorSearchBackend | 向量搜索后端接口（可选注入） |

### 2.4 @valley/stardew 模块清单（~8000行预估）

| 模块 | 职责 | Python对应 |
|------|------|-----------|
| StardewAgent | 组装core原语+星露谷默认值 | agent_core_v2.py |
| FSM | 7状态+转换表+最小持续时间+StateFeasibility | agent_state_machine.py + C# StateFeasibility.cs |
| Tools | 20个工具定义(12 llm_visible + 6 tactical + 2 local) | npc_tools.py |
| StardewEmotion | 8情绪枚举+事件映射+行为权重表 | EmotionAnalyzer (C#) |
| StardewMemory | 记忆类别(4类)+emotionalWeight(7种)+持久化 | vector_memory.py |
| StardewAffection | 5阶段定义+对话/礼物评估prompt | FriendshipSystem + Python评估 |
| StardewInternalWorld | 33NPC人格数据加载+每日计划+日终评估 | agent_internal_world.py |
| StardewHealth | 血量/伤害/复活（次日复活） | AgentHealth.cs |
| RAGEngine | JSON关键词检索(NPC/物品/地点/节日/机制) | rag_index.py |
| PromptBuilder | 双语模板+阶段注入+注入防护 | prompts.py + prompt_utils.py |
| RuleEngine | 5优先级fallback引擎 | agent_rule_engine.py |
| OutputValidator | 语言检测+设定检查+重试prompt | output_validator.py |
| CharacterArc | 5阶段+8里程碑+trust_level | character_arc.py |
| DailyPlanner | 每日计划生成+日终评估+话题生成 | agent_core_v2 + topic_generator.py |
| ProtocolAdapter | 18消息类型 <-> core通用接口 | ws_server.py + protocol_v2.py |
| Handlers | Farm/Fight/Forage/Mine/Talk + 收尾仪式 | C# Handlers |
| ContextBuilder | RAG+记忆+游戏状态 -> AgentContext | context_builder.py |
| Data | 7个JSON数据文件(直接复制) | data/*.json + prompts.json |

---

## 3. Core层抽象设计

### 3.1 Agent运行时——异步非阻塞双队列

Agent类持有状态和双队列。agentLoop是纯函数，不持有状态，接收AgentContext+AgentLoopConfig+AbortSignal，返回EventStream。

双队列实现goap.md第3条"决策不阻塞行动"：
- **steeringQueue**：当前turn结束后注入（如玩家突然说话）
- **followUpQueue**：Agent本应停止后追加（如新事件触发）

7个hook扩展点（参考pi AgentLoopConfig）：
- transformContext：注入游戏状态/记忆/RAG
- convertToLlm：AgentMessage转LLM Message（边界转换）
- beforeToolCall：权限/前置条件验证（可block）
- afterToolCall：同步状态回C# Mod（可覆盖结果）
- shouldStopAfterTurn：优雅停止
- prepareNextTurn：mid-run状态切换
- toolExecution：sequential/parallel

### 3.2 工具系统——可见性三级

- **llm_visible**（12个）：LLM可见可选（speak/emote/set_state/give_item等）
- **tactical**（6个）：LLM不可见（move_to/harvest/water/attack/mine/forage），Handler内部ReAct循环调用
- **local**（2个）：Agent内部吸收（remember/forget），不产出游戏命令

工具通过TypeBox/标准schema定义参数。错误语义：工具失败应抛异常，由loop捕获并以isError:true报告给LLM。terminate:true提示跳过后续LLM调用。

### 3.3 LLM抽象——Vercel AI SDK包装

LLMProvider接口提供chatCompletion和chatCompletionJson两个方法。内部使用Vercel AI SDK的generateText/streamText/generateObject。

支持provider：openai/anthropic/google/deepseek/openrouter/lmstudio/minimax。通过@ai-sdk/openai/@ai-sdk/anthropic/@ai-sdk/google切换。

重试退避（指数+jitter，max_delay=30s）+ semaphore并发控制(默认4) + token预算追踪。两个自定义异常：LLMBillingError(402/429)和LLMUnavailableError(重试耗尽)。

### 3.4 传输层——WebSocket抽象

Transport接口定义start/stop/broadcast/sendTo/onMessage/onConnect/onDisconnect。BunWebSocketTransport使用Bun.serve().websocket实现，利用原生pub/sub和ws.data零分配模式。

游戏层通过ProtocolAdapter桥接：Transport收到原始JSON -> ProtocolAdapter解析为具体消息类型 -> 分发给Agent或子系统。

### 3.5 游戏AI原语——接口+默认实现

每个原语都是"接口在core + 默认实现在core + 游戏层可覆盖"。

**EmotionSystem**：EmotionState(primary,intensity,source) + EmotionEvent + 行为权重映射。默认实现接受游戏层提供的emotionWeights Map和eventMap。

**MemoryBackend**：3层衰减(STRONG/MEDIUM/WEAK, decay=base*0.995^hours) + SignificantMemory(永不压缩) + remember/forget + 日终压缩。默认实现JSON文件持久化+关键词搜索fallback。VectorSearchBackend接口可选注入(Qdrant/LanceDB)。

**AffectionSystem**：AffectionPhase(key,minPoints,maxPoints,promptInjection) + 阶段映射 + 对话/礼物评估。默认实现接受游戏层提供的phases数组和LLM评估prompt builder。

**GoalSystem**：InternalWorld(personality/biography/dailyPlan/playerProfile/worldKnowledge) + generateDailyPlan + evaluatePlayerDay。游戏层提供具体prompt和数据。

**HealthSystem**：HealthState(current,max,isDead) + takeDamage/heal/respawn + onDeath事件。默认实现事件驱动（volatile bool + handler set）。

### 3.6 AgentEvent生命周期

9种事件：agent_start/end, turn_start/end, message_start/update/end, tool_call_start/end, error。游戏层订阅这些事件驱动UI更新。

---

## 4. 星露谷实现层设计

### 4.1 StardewAgent组装

StardewAgent继承Agent，在构造函数中组装所有星露谷具体实现：FSM、EmotionSystem(星露谷8情绪)、MemoryBackend(DefaultMemoryBackend)、AffectionSystem(5阶段)、GoalSystem(星露谷InternalWorld)、HealthSystem(DefaultHealthSystem)、RAGEngine、RuleEngine、OutputValidator、CharacterArc、DailyPlanner。

注册19个工具，配置3个核心hooks：transformContext(注入游戏状态+记忆+RAG)、beforeToolCall(StateFeasibility验证)、afterToolCall(同步状态回C# Mod)。

### 4.2 FSM——7状态

IDLE/FOLLOW/FIGHT/FARM/FORAGE/MINE/TALK。转换表与C# AgentStateMachine.AllowedTransitions完全镜像。最小持续时间：FIGHT=15s, FARM=8s, MINE=10s, FORAGE=8s, FOLLOW=3s, TALK=3s, IDLE=0s。

StateFeasibility前置条件：MINE需矿洞+附近石头，FARM需农场+成熟作物或干燥土壤，FORAGE需户外+附近采集物，FIGHT需附近怪物+血量>20%。FOLLOW/TALK/IDLE总是可行。

### 4.3 工具定义——20个

12个llm_visible(speak/emote/set_state/give_item/give_gift/show_dialogue/eat_food/drop_item/use_item/follow_player/wait/stop) + 6个tactical(move_to/harvest/water/attack/mine/forage) + 2个local(remember/forget)。对应C# CommandExecutor的19个游戏操作命令（remember/forget不产出C#命令，move_to是Handler内部移动请求）。

每个工具返回ToolResult(content+details+isError+terminate)。tactical工具由Handler调用，产出CommandAction通过state_sync返回C#执行。

### 4.4 好感系统——5阶段

stranger(0-250)/acquaintance(251-500)/friend(501-1000)/close(1001-2000)/partner(2001-2500)。promptInjection从npc_prompts.json动态加载对应阶段的第一人称内心独白。

### 4.5 情绪系统——8种情绪

neutral/happy/sad/angry/worried/excited/tired/grateful。11种事件映射(GiftLoved/GiftLiked/GiftHated/FoughtMonsters/HurtInBattle/PlayerInDanger/Complimented/Insulted/TaskCompleted/IdleTooLong/GiftGiven)。行为权重表影响决策（angry->FIGHT权重1.5, sad->IDLE权重1.5等）。

### 4.6 记忆系统

4类别(relationship/life_event/trauma/achievement) + 7种emotionalWeight(joy/sorrow/anger/fear/love/pride/surprise)。SignificantMemory永不压缩永不遗忘。3层衰减(STRONG=50/MEDIUM=200/WEAK=500, decay=base*0.995^hours)。remember/forget工具规则注入system prompt。

### 4.7 Handler——ReAct循环+收尾仪式

通用模式：findTarget -> isAdjacent?performAction : emitToolCall(move_to) -> 无目标:closingRitual+ForceTransition(IDLE)。

各Handler差异：
- FarmHandler：成熟作物/干燥土壤，收尾=伸懒腰emote+"呼，都收完了"
- FightHandler：最近怪物，收尾=收剑emote+战后感叹
- ForageHandler：可采集地面物品，收尾=拍背包emote+"收获不错"
- MineHandler：可破坏石头，收尾=擦汗emote+"清完了"
- TalkHandler：玩家位置，问候语冷却60s，TALK超时30s自动IDLE

### 4.8 规则引擎——5优先级fallback

1. EMERGENCY：附近怪物+血量>30% -> FIGHT
2. SURVIVAL：血量<35%+怪物 -> FOLLOW(好感高)/IDLE(好感低)
3. OPPORTUNITY：作物->FARM/采集物->FORAGE/矿石->MINE（疲惫/难过跳过，背包满忽略）
4. SOCIAL：下雨->IDLE/好感够->FOLLOW/随机->TALK
5. DEFAULT：IDLE + 情绪驱动thought

### 4.9 输出校验

语言层：CJK比例检测+首字符语言检查。设定层：专有名词白名单+不可能动作正则(飞/瞬移/隐身/变形/复活/永生/魔法等)+关系编造检测。校验失败构建补充prompt重试一次。

### 4.10 角色弧线

5阶段(基于好感度)+8里程碑(FIRST_MEETING/FIRST_GIFT/HEART_EVENT/CONFLICT/RECONCILIATION/DEEP_SECRET/TRUST_BREAKTHROUGH/ROUTINE_SHIFT)。每个里程碑更新trust_level(-0.1到+0.3, clamp 0-1)。

### 4.11 每日计划

on_day_started时LLM生成2-3句中文每日计划。日终LLM评估1-5分，调整好感度(1:-20, 2:-5, 3:+5, 4:+15, 5:+30)。话题生成器纯组合逻辑(季节+好感+天气+记忆)。

### 4.12 数据文件迁移

7个JSON文件从Python项目直接复制，格式不变：npc_prompts.json(92KB)/prompts.json/npcs.json(37KB)/items.json(45KB)/locations.json(13KB)/festivals.json(7KB)/mechanics.json(3KB)。

---

## 5. WebSocket协议

### 5.1 协议策略

外部保留现有18种消息类型（C# Mod不动），内部ProtocolAdapter将其转换为core通用接口。未来其他游戏实现自己的ProtocolAdapter。

### 5.2 18种消息类型

hello/ping/decision/dialogue/friendship_eval/gift_eval/emotion_event/rag_query/state_sync/player_input/event/topic_request/emotion_sync/memory_sync/tool_call_result/action_result/consolidate_day。

消息骨架：type + requestId(UUID 32位无连字符)。camelCase JSON，枚举转字符串，忽略null。请求-响应通过requestId匹配，超时60秒。错误：{type:"error", error:"message"}。

### 5.3 核心数据流

**决策流**：C#收集上下文->decision请求->ProtocolAdapter->agent.makeDecision()->熔断器检查->LLM或规则引擎->FSM验证->DecisionResponse->C#执行状态切换

**对话流**：玩家点击NPC->AI问候->AgentChatMenu->玩家输入->dialogue请求->冷却检查->PromptBuilder->LLM->输出校验->DialogueResponse->C#显示->好感评估->记忆写入->触发决策

**礼物流(NPC->玩家)**：LLM调用give_gift工具->ToolRegistry执行->CommandAction->state_sync返回C#->C#执行->记录SignificantMemory+情绪事件

**状态同步流**：每60tick->state_sync->更新agent状态->返回CommandMessage(commands+toolCalls)->C#入队执行

### 5.4 线程模型

网络收发在Bun I/O线程，游戏状态访问在C#主线程（队列桥接）。TS端单进程async，所有Agent共享event loop。LLM调用全部async不阻塞WebSocket。

### 5.5 连接管理

每个C# Mod实例一个WebSocket连接。hello握手注册NPC名->agent映射。多NPC共享连接（消息中npcName字段路由）。断开60秒重连窗口，超时优雅关闭保存记忆。

---

## 6. 生命周期绑定

### 6.1 Bun exe打包

`bun build src/server.ts --compile --target=bun --outfile valley-ai-server --compile-autoload-package-json --minify --sourcemap=linked`

Vercel AI SDK/Bun WebSocket/bun:sqlite都是纯JS或Bun内置，无native binding问题。体积~40-50MB。Pin Bun>=1.3.13。

### 6.2 C# Mod端进程管理

PythonProcessManager.cs改为ServerProcessManager.cs。逻辑：Mod SaveLoaded->spawn exe->端口检测->WebSocket连接->hello握手。看门狗每5s检查进程。崩溃重启最多3次(退避5/15/45s)。Mod卸载/返回标题->Kill进程树。

配置：exe路径通过config.json，默认Mod目录下valley-ai-server.exe。找不到exe则fallback到bun run（开发模式）。

### 6.3 生命周期事件

| SMAPI事件 | TS服务器行为 |
|---|---|
| SaveLoaded | spawn exe->等端口就绪->连接->hello |
| DayStarted | day_started事件->每日计划生成 |
| DayEnding | consolidate_day->日终记忆压缩 |
| UpdateTicked | state_sync每60tick |
| TimeChanged | 定时决策(30s/1800ticks) |
| PlayerWarped | FOLLOW跨地图跟随 |
| Mod卸载/返回标题 | Kill进程树 |

### 6.4 游戏崩溃处理

WebSocket断开后TS服务器等待60秒重连窗口。重连->恢复状态。超时->保存记忆->退出。C#端看门狗随游戏一起死，TS服务器靠WebSocket超时自保。

### 6.5 配置传递

命令行参数(--port/--llm-provider/--llm-api-key/--llm-model)优先级高于配置文件(server-config.json)。C# config.json的LLM配置字段由ServerProcessManager映射为命令行参数。

### 6.6 开发模式

bun run src/server.ts直接运行TS源码，或bun --watch自动重启。ServerProcessManager检测exe不存在时fallback到bun run。或config devMode:true强制bun run。

---

## 7. 测试策略

### 7.1 测试框架

Bun内置test runner(bun test)，零配置，TS原生支持。

### 7.2 三层测试

**单元测试(core)**：agentLoop纯函数(mock LLM)、ToolRegistry(schema/可见性/错误)、CircuitBreaker(3态)、原语默认实现(情绪/记忆/健康)、LLMProvider(mock SDK/重试/semaphore/token预算)

**单元测试(stardew)**：FSM(转换/持续时间/非法拒绝)、StateFeasibility、RuleEngine(5优先级)、OutputValidator(语言/设定)、PromptBuilder(阶段/注入防护)、RAGEngine(查询)、CharacterArc(阶段/里程碑)、MemorySystem(衰减/Significant/压缩)

**集成测试**：WebSocket协议兼容性(18消息类型黄金集对比)、端到端决策流、端到端对话流、多Agent并发(5个并行)、熔断器fallback

### 7.3 协议兼容性验证

录制Python服务器对每种消息类型的响应作为"黄金集"。TS服务器必须产出语义等价响应(字段名/值类型/嵌套结构相同)。逐消息类型对比确保C# Mod无感知切换。

### 7.4 静态检查

- tsc --noEmit严格模式，0 error 0 warning
- Biome或ESLint+Prettier代码风格统一
- dependency-cruiser强制core不依赖stardew

---

## 8. 迁移计划

### 8.1 分阶段

| Phase | 内容 | 预估 |
|-------|------|------|
| Phase 0 | 项目骨架(npm workspaces/tsconfig/CI) | 1-2天 |
| Phase 1 | Core层(Agent/agentLoop/工具/LLM/传输/原语/基础设施+单元测试) | 5-7天 |
| Phase 2 | 协议层(ProtocolAdapter+5核心消息+黄金集测试) | 3-5天 |
| Phase 3 | 星露谷完整实现(FSM/工具/PromptBuilder/RAG/规则/校验/好感/情绪/记忆/健康/弧线/计划/Handler+单元测试) | 7-10天 |
| Phase 4 | 集成测试(18消息端到端/黄金集全量/多Agent/熔断器) | 3-5天 |
| Phase 5 | Bun exe打包+C# ServerProcessManager+生命周期测试 | 2-3天 |
| Phase 6 | 游戏内测试(TestMod套件/行为一致性/性能) | 3-5天 |

总计24-37个工作日。Phase 1-3可部分并行。

### 8.2 废弃模块（不迁移）

- agent_core.py（v1 legacy）
- llm_client.py（v1 legacy）
- prompt_builder.py（v1 legacy，保留sanitize逻辑）
- agent_tools.py（v1 legacy，用npc_tools.py对应）
- agent_memory.py（v1 legacy，用vector_memory.py对应）
- mempalace_adapter.py（TS版用Qdrant或SQLite fallback替代）

### 8.3 验收标准

- tsc --noEmit：0 error 0 warning
- bun test：全部通过
- 协议黄金集：18种消息类型全部语义等价
- 游戏内测试：现有TestMod Fuzzy组22/22通过
- 性能：OnUpdateTicked每tick<1ms(C#端)，TS服务器内存<200MB(50 NPC)
- C# Mod改动：仅PythonProcessManager->ServerProcessManager+config字段调整

---

## 9. 开放问题

以下问题在设计阶段标记为"暂不实现，未来迭代"：

1. **向量记忆后端选择**：默认用SQLite余弦fallback（纯JS），Qdrant sidecar作为可选增强。LanceDB因Arrow IPC边界bug暂不采用。
2. **mempalace集成**：不迁移。TS版用Qdrant或SQLite fallback替代。
3. **多Agent协调**：基础架构支持（每个NPC独立Agent实例），但NPC间互动暂不实现。
4. **自适应决策间隔**：Python版有_get_adaptive_decision_interval（FIGHT 10s/HP<30% 5s等），TS版迁移此逻辑。
5. **任务驱动按需智能**：Python版规划中的TaskScheduler（Token节省80%），TS版暂不实现，保持30s定时+事件触发。
