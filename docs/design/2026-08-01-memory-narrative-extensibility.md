# 长期记忆、思考过程留痕、叙事引擎与扩展性评估

> 日期：2026-08-01
> 状态：设计稿（不含代码改动）
> 配套：`2026-08-01-npc-feedback-architecture.md`（主设计）、`2026-08-01-test-scoring-redesign.md`（测试/打分）
> 本文回答四个问题：
> 1. 思考过程（Director + NPC）从未落盘，但后期要做整体预览/历史回顾——怎么补。
> 2. NPC 长期记忆是否正确、能否长时间维持稳定人设。
> 3. Director 在程序上能否基于玩家行为和环境写出有趣且不崩坏的故事，同时保持 NPC 自主性。
> 4. 系统能否长期维护和扩展（雇佣 NPC、NPC 背包、NPC 间聊天、偷窃）。

---

## 0. 现状核实的硬事实

### 0.1 思考过程：什么都没留下

- **agentLoop 无轨迹持久化**：core 的 `EventStream`（`event-stream.ts:3-54`）只在内存收集事件，`awaitAll()` 返回即弃；`StardewAgent.runOnce`（`stardew-agent.ts:307-323`）提取 speech/actions 后事件流直接丢弃。全 `packages/core` grep 不到任何 transcript/journal/writeFile。
- **日志只到 stdout**：`protocol-adapter.ts` 的 console.log + server.ts 的 warn/error，经 `ServerProcessManager` 显示在 cmd 窗口或重定向进 SMAPI 日志——**关窗口即全部丢失，无文件落盘**。
- **Director 的思考**：原始 LLM 输出解析后丢弃（`director.ts:145-162`）；唯一留下的痕迹是 beat 的 `reasonGenerated`（存进 `beats.context_json`，`beat-store.ts:64`）。**被校验丢弃的 beat 和"今天没发 beat"的决定无任何记录**——事后无法回答"导演今天为什么沉默"。
- **讽刺的是 schema 已备好**：`ReActStep` 类型（thought/toolCall/toolResult/tokensUsed，`narrative-types.ts:7-14`）和 `beats.react_steps_json` 列存在，**没有任何生产代码写入它**。

### 0.2 长期记忆：三层都有硬伤

| 层 | 现状 | 硬伤 |
|---|---|---|
| conversationHistory | 50 条 FIFO（`agent-memory.ts:36-38`） | 旧对话**彻底丢弃，无归档**；持久化文件同样被截断重写 |
| shortTermMemories | 30 条上限，剪枝分 = `importance + (1 - age/7200s) * 2`（`:72-79`） | recency 以**现实 2 小时**为零点且无下界：老记忆分数无限变负，importance 封顶 10 的记忆约 7 小时后必被挤出。**一个周末没玩，NPC 上周的记忆全灭**；且时间轴用现实秒，与游戏内日期完全脱钩 |
| significantMemories | 精确去重、永不剪枝 | **无限增长**：`getSignificantMemoriesText()`（`:118-121`）每次全量逐行注入 prompt 前部——数十条后该段线性膨胀，且每加一条摧毁其后全部前缀缓存 |
| longTermMemories / LLM 压缩 | **不存在** | AGENTS.md §3.3 的表格与"已实现 LL 压缩"在 TS 端均无对应代码 |
| server 自存 friendship | `addFriendship` 生产代码无人调用 | 永远是 0，仅供 `get_info player` 返回假数据；prompt 实际用 C# 注入的 `scene.friendship`（只读） |

人设稳定性现状：人设 = `npc_prompts.json` 阶段 prompt + `base_memory` + `attitudeBrief`（按好感分档），这部分是**稳定的**；但"关系轨迹"（我们上周一起下过矿、他送过我花）完全依赖会被剪掉的短期记忆和会被截断的对话历史——**NPC 的角色设定不会崩，但"共同经历"会稳定地消失**。玩家感受到的人设崩坏大多属于后者。

### 0.3 Director：完成度高，但零实例化

`grep "new Director|new BeatStore|new ActivityLogStore|new PlayerProfileManager|new GameContextManager|new ReActGuard"` 排除 tests 后**零匹配**。`server.ts` 只装配五件套（PromptLoader/PromptBuilder/Provider/Registry/ProtocolAdapter）。且即使装配了，`morningPlan()` 第一步 `gameCtxMgr.getCurrent()` 也恒为 null（`game_context_sync` 消息未路由）→ **永远返回空计划**（`director.ts:100-102`）。

已具备的输入侧资产（全部休眠）：玩家每日行为流水（`daily_activities` 表：钓鱼/种地/挖矿/社交分钟数等）、五层玩家画像（静态/行为/偏好/关系/性格）、流派快照、里程碑去重、最近 14 个 beat 的节奏约束、NPC 冷却。Director prompt 有独立模板（`buildMorningPrompt`，`director.ts:274-329`），含 6 条设计原则（稀缺性/相关性/避免套路/符合人设/节日不发/只给方向）。

### 0.4 NPC 背包：双账本，无持久化

`ToolContext.inventory` 每次 `runDialogue` 从 C# worldSnapshot **浅拷贝现建**（`stardew-agent.ts:119-125`），run 结束即弃；server 端 memory.json 无 inventory 字段。库存真相源在 C# 侧 `AgentInventory`，server 只是当轮镜像——这正是主设计 §2.2 两阶段账本要解决的问题之一。

### 0.5 WorldSnapshot 撑不起叙事

7 个必填字段（season/day/time/weather/location/friendship/farmerName）是**以 NPC 为视角的单帧快照**。Director 需要的玩家位置历史、行为流水、游戏进度、金钱、其他 NPC 状态、游戏内时间调度概念，协议里全都没有（narrative-types.ts 已定义但未被推送）。

---

## 1. 思考过程与全量历史留痕（服务"整体预览/历史回顾"）

### 1.1 设计：双端 EventStore + 关联 ID

历史回顾功能需要的是**可回放的时间线**，不是日志文件。设计一个概念、两个落点：

**TS 端 TranscriptStore**（新建，SQLite，与 beat-store 同库不同表）：

| 表 | 内容 | 写入时机 |
|---|---|---|
| `agent_runs` | runId / npcName / trigger（dialogue·beat·director）/ 开始时间 / system prompt 全文 / user 输入 / 最终 speech / actions / 校验结果 / fallback 标记 / tokens / 延迟 | 每次 `runDialogue`/`runBeat` 结束 |
| `agent_turns` | runId / turn 序号 / LLM 原始输出 / tool calls + args / tool results | agentLoop 每轮（改 `runOnce` 不再丢弃 events） |
| `director_runs` | 日期 / 触发（morning·milestone）/ 完整 prompt / LLM 原始输出 / 产出 beats / **被丢弃 beats + 丢弃原因** / 空结果日标记（"今日无叙事"也是一条记录） | `morningPlan`/`milestoneReact` 每次调用 |

**C# 端事件流**（测试文档 L5 已设计的 JSONL）：ws 消息、state_changed、action_result、不变量违例。

**关联 ID**：`requestId`（对话）/ `callId`（动作）/ `runId`（agent run）/ `beatId` / 游戏内日期。历史回顾界面按 `日期 + npcName` 即可把"导演早上的意图 → NPC 的思考轨迹 → 说出口的话 → C# 执行结果"缝成一条完整故事线。

### 1.2 细则

- **体积控制**：按游戏日分表/分文件轮换；system prompt 静态段存 hash + 一份全文（段内容同日不变，只存动态段全文——顺带验证了 prompt 缓存命中情况）。SQLite WAL 模式沿用 beat-store 的先例。
- **失败也记录**：fallback 的 run、校验失败的 retry、LLM 超时——这些是历史回顾里"NPC 走神了"的真相，不能丢。
- **隐私边界**：全在本地，无外部上传。
- **直接收益**：测试文档的事件流回放、L5-B 体验指标计算、§3 的叙事质量评估全部从这一个设施取数。

---

## 2. 长期记忆重构：四层模型

### 2.1 结构

```
L1 工作记忆    conversationHistory（50 条）+ shortTermMemories（30 条）—— 现状保留
L2 情节归档    archive 表（SQLite，per-NPC）：被剪/被截的条目不删除，进归档
               字段：text / importance / entryType / 游戏日期 / 归档原因
L3 每日凝练    longTermMemories（有界，≤20 条）：每天 DayEnding 时由 LLM 把
               当天的对话+短期记忆+事件流凝练成 3-5 条第一人称摘要
L4 重要事项    significantMemories：存储无上限，prompt 注入有上限
```

### 2.2 各层规则

- **剪枝不再等于遗忘**：L1 挤出的条目写 L2 归档（追加，成本低）。归档默认不进 prompt，供 `get_info memory` 查询和未来检索（可选加 embedding）。
- **时间轴改用游戏日期**：剪枝的 recency 项以**游戏内天数**为单位（如 3 天为零点），不再用现实秒——玩家暂停/隔天再玩不应导致记忆加速死亡。
- **L3 每日凝练（新消息 `day_end`）**：C# DayEnding 推送 → TS 对每个当日活跃 NPC 跑一次凝练 run（prompt：今日对话+事件流+L1 记忆 → 3-5 条第一人称摘要，importance 由 LLM 标）→ 写入 longTermMemories，注入 prompt 的"最近记忆"段旁边（"─── 一直以来的经历 ───"）。**凝练失败保留原始条目不丢，次日重试**——压缩是增强不是唯一路径。这层就是 AGENTS.md 声称过但从未实现的"LLM 压缩"。
- **L4 注入有界**：prompt 只注入 top-K（建议 K=10，按 emotional_weight 权重 + 游戏日期 recency 排序），全量留在存储。remember 工具返回时告知"已记住（第 N 条重要记忆）"，LLM 可用 forget 之外的新工具 `unmark`（可选）降级不重要条目。
- **关系轨迹锚点**：L3 凝练 prompt 里固定要求一条"我和农场主的关系近况"摘要——即使细节被剪，NPC 始终知道"我们最近越走越近/他很久没来了"，这是玩家长期感知人设稳定的关键。
- **prompt 位置**（遵循要求二）：L4 top-K 放"准静态区"末尾，L3 放"中频区"，L1 保持高频区位置不变。
- **friendship 字段**：删掉 server 自存 friendship（死字段），`get_info player` 改从 scene 读取，消除假数据。

### 2.3 人设稳定性验收（对应测试文档增补）

- **人设漂移探针**：固定 10 个探针问题（"我们第一次见面是什么时候？""你还记得我送你的第一件礼物吗？""你喜欢我吗？"），在长跑存档的第 1/7/30 游戏日分别问同一 NPC，断言：① 事实类问题答案与 EventStore 记录一致（靠 L2/L3）；② 态度类问题与好感阶段一致（靠阶段 prompt）。这就是"长时间维持稳定人设"的可执行定义。

---

## 3. Director：能否写出有趣且不崩坏的故事

### 3.1 程序上的结论

**骨架是对的，输入是断的，留痕是没有的。** 三个前提按序成立后才能谈"有趣"：

| 前提 | 缺口 | 对应改动 |
|---|---|---|
| 导演看得见世界 | `game_context_sync`/`activity_report`/`player_state_update`/`day_started` 未路由，C# 无推送端 | 协议路由 + C# DayStarted/DayEnding/定时推送（主设计 §3.3 已列） |
| 导演的决定可追溯 | 原始输出、被丢弃 beat、空结果日都不记录 | §1.1 `director_runs` 表 |
| 导演的产出能落地 | beat 执行链路（runBeat → actions → C# 执行 → 回执）未接 | 主设计 §3.3 |

### 3.2 "不崩坏"靠确定性校验，不靠 LLM 自觉

- 已有的 `validateAndFilter`（结构校验）+ 节日熔断 + react-guard 软刹车 + LLM 失败返回空（**降级隐形**）都是正确方向，保留。
- 增补**确定性世界规则校验器**（代码而非 prompt）：beat 的地点可达性（LocationGraph）、时间不冲突（该 NPC 当时是否有原版强制日程/节日）、道具存在性。LLM 负责创意，代码负责物理。
- 崩坏的兜底永远是"今天没有故事"，而不是"今天有个坏故事"——这条已在 director.ts 注释里体现，是对的。

### 3.3 "有趣"的三个来源（程序上已具备或低成本）

1. **个性化**：玩家画像（流派/性格/日常规律）+ 最近 3 天活动流水已在 prompt 输入设计里——接上推送就有。
2. **连续性**：`recurringTropes`（玩家画像 story 层）+ 最近 14 beat 节奏约束 + NPC 冷却——防止"天天都是 Alex 找你打球"。
3. **长尾效应**：beat 结果（含失败）写入 NPC 记忆（主设计 §3.5），成为后续对话素材——故事不只是一次性事件。

### 3.4 自主性的四层编排（导演是 Token 节省设计的正确定位）

| 层 | 驱动 | Token 成本 | NPC 表现 |
|---|---|---|---|
| L0 生活层 | 原版日程 + IdleWander | 0 | 全镇 NPC 过日常，玩家撞见即为生活感 |
| L1 叙事层 | Director 每日 0–3 beat | 低（每 beat 一次 runBeat，maxTurns=8） | 被"点亮"的 NPC 主动找玩家/有自己的事 |
| L2 交互层 | 玩家对话（PromoteToAgent 临时升级） | 中（按对话轮次） | 任何 NPC 可聊，干活承诺触发升级 |
| L3 常驻层 | config 指定的核心 NPC（事件驱动决策） | 高 | 深度陪伴，当前默认形态 |

要点：**导演给方向（directive），NPC 自己演**——runBeat 的终止条件是"speak + 至少一个自选工具"，beat 内部的动作选择、台词、情绪全是 NPC agent 自主的。这恰好实现了用户的定位：自主性从"常驻 Agent 不断执行"收敛为"导演的合适编排 + beat 内自主"，Token 成本与叙事密度都由 Director 的预算参数（maxBeatsPerDay、冷却）显式控制。L2/L3 保留，因为导演不能替代玩家发起的即时交互。

---

## 4. 扩展性评估：四个规划中的功能

### 4.1 机制健康度

- **加工具成本低**：`buildStardewTools` 数组加一项（TypeBox schema + execute），registry 自动收录，`extractResult` 自动透传进 actions[]，纯 TS 侧 20–40 行；成本全在 C# 执行端。
- **协议扩展成本中等**：5 消息硬编码在 `routeMessage`，加消息类型是单点改动，但必须有契约测试（测试文档 L1）防两端漂移。
- **真正的盲区是 NPC 间交互**（见 4.4）。

### 4.2 雇佣 NPC

- 链路：对话中玩家出价 → LLM 调 `accept_job`（新工具：job 类型/报酬/时限）→ C# 扣钱 + 设置带过期的工作状态 → 执行干活 Handler → 到期/完成自动退出 + `state_changed` 上报。
- 协议可不动（复用 dialogue + actions + 回执），需要扩展的是上下文：worldSnapshot 加玩家金钱。
- 设计要点：雇佣本质上是**玩家付费的私人 beat**——可复用 beat 的时限/前提检查/失败解释框架，不必另起一套。

### 4.3 NPC 背包

- 维持 **C# 为唯一账本**（`AgentInventory` 已存在），server 端 inventory 明确降级为只读镜像（现状如此，把它写进设计而不是意外）。
- 所有改变背包的工具（give/steal/雇佣报酬物品）走主设计 §2.2 两阶段账本：server 记意图，C# 回执后才落现实。
- 初始物品与补货：由 C# 按 NPC 人设配置（如 Linus 永远没值钱东西可送——这也是人设）。

### 4.4 NPC 间聊天（当前架构最大盲区）

- 现状：`StardewAgentRegistry` 是 `Map<npcName, StardewAgent>`，user 消息只有"农场主说"和"导演指令"两种种子，speak 没有路由到另一个 agent 的通道。
- 设计：新消息 `npc_dialogue`；A 的 speak 经 registry 转成 B 的 user 消息（"Haley 对你说：…"）；per-NPC 锁需要协调——回合制 A→B→A，设最大回合数 + 超时防死锁；双方各写各的记忆；Director 可编排 NPC-NPC beat（节日八卦、传话）。
- prompt 区分说话人（"农场主说 / {NPC}说 / 导演指令"），第三人称呼规则进人设段。
- 改动量中等（registry + protocol-adapter + prompt 模板），建议排在雇佣/偷窃之后。

### 4.5 偷窃

- 机制便宜：`steal` 工具（item_id/target）+ C# 执行 + 两阶段账本 + 被发现时的记忆/情绪/好感惩罚。
- 真正的成本在**人设约束**：哪个 NPC 会偷、什么关系下会偷，应由阶段 prompt 承担（规则段只写"绝不做出违背你性格的行为"太弱，要在 npc_prompts.json 里给少数 NPC 明确写可能性）。这是数据工作不是代码工作。

### 4.6 结构性易踩点（扩展时必须顺手修）

| 点 | 现状 | 改进 |
|---|---|---|
| `SPEAK_TOOLS` 硬编码（`stardew-agent.ts:31`） | 新"说话类"工具不加进去就不会终止 loop、不会被提取为 speech | 改为工具定义的 `terminal: true` 标志 |
| `STATES`/`EMOTE_IDS` 字面量 union | 加状态要改 TS schema + C# 状态机 + prompt 规则三处 | 从单一源生成（schema 导出 → C# 代码生成或运行时校验） |
| 乐观记账 | server 工具 execute 直接改记忆/账本，无失败回滚 | 一切影响游戏世界的工具统一两阶段（主设计 §2.2） |
| 休眠代码腐化 | Director 全家桶测试全绿但协议已漂移 | "接线或删除"政策：任何超过一个版本未接线的功能模块，要么接要么删，不允许僵尸资产 |

---

## 5. 长期可维护性总结

**健康资产**：工具扩展机制（TypeBox + visibility）、SQLite 存储模式（WAL + UPSERT 幂等 + 单文档画像避免 schema 迁移）、Director 套件的设计完整度（含测试）、人设数据与代码分离（npc_prompts.json）。

**结构性风险**（按严重度）：

1. **零留痕**：没有思考过程记录，历史回顾功能无从谈起，且调试、测试回放、叙事评估全部缺基础设施——§1 的 TranscriptStore 是所有后期功能的公共地基，应最先做。
2. **记忆会丢**：对话截断即销毁、短期记忆 2 小时死亡线——§2 四层模型是"长期稳定人设"的必要条件。
3. **休眠代码与活代码漂移**：Director 套件再完好，不接线的每一天都在腐烂；`decision`/`friendship_eval` 死管道同理。契约测试 + 接线或删除政策。
4. **NPC 间交互盲区**：架构层面需要 registry 路由改造，越早设计越便宜。

**建议落地顺序**：TranscriptStore（§1）→ 协议路由补齐 + C# 推送端（§3.1）→ 记忆四层模型（§2）→ Director 接线跑通第一个真实 beat → 雇佣/偷窃（机制便宜）→ NPC 间聊天（架构改造）。
