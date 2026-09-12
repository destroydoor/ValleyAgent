# ValleyAgent 完整运行时流程

> **历史文档（2026-07-26 标注）**
>
> 本文档描述的运行时流程基于 v4.1 Python 智能层的"Timer 决策驱动"模型
> （每 30 秒触发 `MakeDecisionsAsync`）。v4.3 已重构为 **dialogue 触发模型**：
> LLM 只在玩家点击 NPC 或主动对话时触发，TS Agent Server 接管所有决策与
> 工具调用。原"Timer → Python → 决策入队"流程不再适用。
>
> **当前架构权威文档**：[`../AGENTS.md`](../AGENTS.md) 第 2.2 节"关键数据流"
> 描述了 v4.3 的对话流、礼物流与工具调用反馈环。

---

> **更新**：2026-06-19 — THINKING 状态已移除，RuleBasedDecisionEngine 已集成到决策链。

## 一、NPC 分配

```
DayStarted 事件
  │
  ├── 遍历所有 villager NPC
  │    ├── 读存档：有手动分配的 NPC → ForceAllocate()
  │    └── 无手动分配 → 按好感度排序取 top-N → TryAllocate()
  │
  └── AgentService.CreateAgent(npcName)
       ├── 创建 7 个状态的 Controller 实例
       ├── 注册状态机（IDLE/FOLLOW/FIGHT/FARM/FORAGE/MINE/TALK）
       ├── followSchedule = false（禁用原版日程）
       └── 初始状态 = IDLE
```

---

## 二、TICK 循环（每帧 60fps）

```
OnUpdateTicked (每帧)
  │
  ├── _tickCounter++
  │
  ├── [每 1800 ticks / 30 秒] Timer 触发器
  │     └── Task.Run(MakeDecisionsAsync) → 进入后台线程
  │           │
  │           ├── CircuitBreaker 检查（v4.1 集成 RuleBasedDecisionEngine）
  │           │     ├── CanExecute == false（OPEN 状态）→ DecideByRules(agent)
  │           │     │     └── ApplyRuleDecision → 入队 _pendingDecisions → continue
  │           │     └── CanExecute == true → 正常 LLM 决策流程
  │           │
  │           ├── BuildDecisionContext(agent)
  │           │     ├── 基础信息：名称、状态、地点、季节、日期、时间
  │           │     ├── 情绪：agent.Brain.GetEmotionDescription()
  │           │     ├── 记忆：agent.Brain.GetMemorySummary()
  │           │     ├── 最近事件：agent.LastEvent（读后清空）
  │           │     ├── 好感度：Game1.player.friendshipData → 点数/爱心
  │           │     ├── 对话历史：AIDialogueSystem.GetDialogueHistory → 最近5轮
  │           │     ├── 上次决策：agent.LastDecisionState + LastDecisionReason
  │           │     ├── 生命：agent.Health（低于50%附加警告）
  │           │     ├── 天气、季节、日期
  │           │     ├── 世界知识：GameSummary.json 构建
  │           │     ├── 礼物历史：最近3次
  │           │     ├── 好感趋势：最近5次变化
  │           │     └── 环境扫描（按位置过滤）：
  │           │           ├── 怪物：FightHandler.ScanEnvironment → 始终注入
  │           │           ├── 作物：FarmHandler.ScanEnvironment → 仅 Farm/户外
  │           │           ├── 石头：MineHandler.ScanEnvironment → 仅矿洞
  │           │           └── 采集物：ForageHandler.ScanEnvironment → 仅户外非矿洞
  │           │
  │           ├── BuildDecisionRequest(agent, context) → DecisionRequest
  │           │     └── 序列化为 JSON，包含全部上下文字段
  │           │
  │           ├── WebSocket → ws://127.0.0.1:8765
  │           │     └── 发送 {"type":"decision", "npcName":"Haley", "currentState":"IDLE", ...}
  │           │
  │           └── 等待 Python 响应
  │
  ├── [每 tick] AgentTickLoop.ProcessAgent(agent)
  │     │
  │     ├── Vanilla-release（连续3次 IDLE 决策）？
  │     │    ├── 当前状态 == IDLE → Speed=0，停止移动，返回 Skip
  │     │    │                         → 本 tick 跳过 ApplyControllerToNpc
  │     │    └── 当前状态 != IDLE → 恢复 Speed，取消 Vanilla-release
  │     │
  │     ├── Anti-WanderingSpouses：强制 npc.followSchedule = false
  │     ├── 正在对话？→ 面朝玩家，Speed=0
  │     ├── NPC 死亡？→ 返回 Dead
  │     └── 正常 → 返回 Normal
  │
  ├── [每 tick 仅 Normal agent] ApplyControllerToNpc(agent, state, controller)
  │     │
  │     ├── FOLLOW → AgentNavigator.Update (跨地图跟随)
  │     ├── FIGHT  → FightHandler.Update(npc, agent, tick)
  │     ├── FARM   → FarmHandler.Update(npc, agent, tick)
  │     ├── MINE   → MineHandler.Update(npc, agent, tick)
  │     ├── FORAGE → ForageHandler.Update(npc, agent, tick)
  │     ├── TALK   → TalkHandler.Update(npc, agent, tick)
  │     └── IDLE   → npc.controller=null; npc.Halt()
  │
  ├── [每 10 tick] 主线程决策应用
  │     ├── 处理 _pendingDecisions 队列
  │     │     ├── 解析 targetState, reason, thought
  │     │     ├── 守卫检查：
  │     │     │     ├── 最小状态持续时间未到？→ 跳过
  │     │     │     └── 对话触发的 FOLLOW 被覆盖？→ 跳过
  │     │     ├── TrackIdleDecision / ResetIdleTracking
  │     │     ├── StateMachine.ForceTransition(targetState)
  │     │     ├── 保存 LastDecisionState + LastDecisionReason
  │     │     └── showTextAboveHead(thought) → 头顶显示想法
  │     │
  │     ├── 事件触发决策（对话结束/任务完成/礼物）
  │     │     └── Task.Run(MakeDecisionsAsync) → 同 Timer 流程
  │     │
  │     └── CheckEmergencyState（怪物靠近 → 紧急切换 FIGHT）
  │
  └── [每 60 tick] 状态同步 → WebSocket
        └── StateSyncSender.SendStateSync()
              └── 发送所有 Agent 的完整状态快照给 Python
```

---

## 三、Handler 内部 ReAct 循环（每帧调用 Update）

```
FarmHandler.Update(npc, agent, tick) ← 示例，其他 Handler 结构相同
  │
  ├── [OBSERVE 感知] ScanEnvironment(npc) → 扫描15格内成熟作物+干土
  │
  ├── [THINK 判断] 无目标？
  │     ├── 是 → 执行收尾仪式（emote伸懒腰）
  │     └──       → ForceTransition(IDLE) → OnStateChanged 事件触发
  │
  ├── [THINK 判断] 已到达目标旁边？
  │     ├── 是 → 动作冷却到了？
  │     │     ├── 是 → 执行收获/浇水
  │     │     └── 否 → 等待（本帧结束）
  │     └── 否 → MovementService.MoveTo(目标旁边格子)
  │              ├── PathFindController 不存在 → 创建新的
  │              ├── PathFindController 存在 → 让它继续寻路
  │              └── 寻路失败（多次重试）→ 传送回退到可行走位置
  │
  └── 返回（下一帧继续）
```

### 各 Handler 收尾仪式对比

| Handler | 退出触发 | 收尾动作 | 状态转换 |
|---------|---------|---------|---------|
| FightHandler | 附近无怪物 | doEmote(20) 生气 | ForceTransition(IDLE) |
| FarmHandler | 附近无可收作物 | doEmote(24) 伸懒腰 | ForceTransition(IDLE) |
| MineHandler | 附近无石头 | doEmote(36) 擦汗 | ForceTransition(IDLE) |
| ForageHandler | 附近无采集物 | doEmote(32) 拍背包 | ForceTransition(IDLE) |
| TalkHandler | 30秒超时 或 玩家走远 | 无 | ForceTransition(IDLE) |

---

## 四、OnStateChanged 事件（状态转换后自动触发）

```
ForceTransition(newState)
  └── StateMachine.PerformTransition
        ├── 旧状态.Exit()
        ├── 新状态.Entry()
        └── OnStateChanged 事件触发

OnStateChanged → ModEntry 事件处理器
  │
  ├── 新状态==IDLE && 旧状态是活跃任务(FARM/FIGHT/...)?
  │     ├── SetEmotion(TaskCompleted)
  │     ├── AddMemory("刚才做完了{任务}")
  │     └── LastEvent = "刚完成{任务}——可以接新任务了"
  │
  ├── 新状态==IDLE && 旧状态==FIGHT?
  │     ├── SetEmotion(FoughtMonsters)
  │     ├── AddMemory("刚才打了一架，累死了")
  │     └── LastEvent = "击败X只怪物，掉了Y点血(现在Z/100)"
  │
  └── 新状态==IDLE && 旧状态是活跃任务(FARM/FIGHT/...)?
        ├── 冷却检查（5秒内不重复触发）
        ├── 加入 _pendingTaskCompleteDecisions 队列
        └── 保存 LastDecisionState + Reason → 下次 LLM 决策的上下文
```

---

## 五、Python 服务器端（WebSocket → LLM → 响应）

```
ws_server.handle_connection
  │
  ├── [PERCEIVE 感知] 解析 DecisionRequest
  │     └── 提取：npc_name, current_state, location, time, weather,
  │               mood, health, max_health, friendship, nearby_objects, memory
  │
  ├── [PROMPT 提示词] build_decision_prompt(req)
  │     ├── System: prompts.json decision.system
  │     │     └── "你是 {npc_name}，星露谷鹈鹕镇的居民。你是一个真实的人..."
  │     │     └── "可选行动：IDLE, FOLLOW, FIGHT, FARM, FORAGE, MINE, TALK"
  │     │     └── "选择要反映性格和当前状况。不要总选IDLE..."
  │     │     └── + "把你的最终 JSON 答案放在 <answer></answer> 标签内"
  │     │
  │     └── User: prompts.json decision.user
  │           └── 当前状况：状态/地点/时间/天气/心情/生命/好感
  │           └── Nearby objects: {nearby_objects}
  │           └── Recent memories: {memory}
  │           └── "决定行动并将结果放在 <answer> 标签内"
  │
  ├── [THINK 思考] llm_client.chat_completion_json(prompt, _DECISION_SCHEMA)
  │     └── 调用 DeepSeek API (deepseek-chat)
  │     └── JSON Schema 约束: {"targetState": "enum7", "reason": "str", "thought": "str"}
  │     └── max_tokens=150, temperature=config.llm_temperature_json
  │
  ├── [DECIDE 决策] 解析 JSON 响应
  │     ├── 验证 targetState ∈ {IDLE,FOLLOW,FIGHT,FARM,FORAGE,MINE,TALK}
  │     ├── 提取 reason + thought
  │     └── 构建 DecisionResponse
  │
  └── [OUTPUT 输出] 通过 WebSocket 返回 DecisionResponse
        └── {"targetState":"FARM","reason":"...","thought":"...","parameters":{}}
```

---

## 六、决策结果应用（回到 C# 主线程）

```
WebSocket 响应到达 C# 后台线程
  │
  ├── 解析 targetState, reason, thought
  ├── lock(_pendingDecisionsLock)
  │     └── _pendingDecisions.Enqueue((agent, targetState, reason, thought))
  │
  └── 下一个 10-tick 处理周期：
        ├── 守卫检查通过
        ├── agent.StateMachine.ForceTransition(targetState)
        ├── 更新 LastDecisionState / LastDecisionReason
        ├── showTextAboveHead(thought)
        └── 下一帧 ApplyControllerToNpc → 进入新 Handler
```

---

## 七、对话系统（独立于决策流程）

```
玩家点击 NPC
  │
  ├── NPCDialoguePatch.Prefix 拦截 checkAction
  │     ├── 死亡 NPC → 显示 "需要休息" 阻止交互
  │     ├── Agent NPC → 直接打开 AgentChatMenu
  │     └── 非 Agent NPC → 显示 AI 问候语（不打开菜单）
  │
  └── AgentChatMenu 打开
        │
        ├── 显示 NPC 头像 + 当前消息（DialogueBox 风格底部栏）
        ├── 底部输入框（覆盖层）
        │
        └── 玩家输入文字 → Enter 发送
              │
              ├── ValleyAgentApi.TryGenerateDialogue(npcName, input, context)
              │     ├── 冷却检查（30秒间隔）
              │     ├── 重复请求检查（_pendingDialogueRequests）
              │     ├── 构建对话上下文（地点/时间/天气/好感/环境/称呼/历史）
              │     └── WebSocket → ws://127.0.0.1:8765
              │           └── {"type":"dialogue", "playerInput":"你还好吗？", ...}
              │
              ├── Python: build_dialogue_prompt(req)
              │     ├── System: prompts.json dialogue.system
              │     │     └── "{npc_name} 的性格：{personality}"
              │     │     └── "{npc_name} 的角色档案：{npc_biography}"
              │     │     └── "可用动作：FOLLOW/FIGHT/FARM/..."
              │     │
              │     └── User: prompts.json dialogue.user
              │           └── 地点/时间/天气/好感
              │           └── 环境对象
              │           └── 对话历史（最近5轮）
              │           └── "农场主说：{player_input}"
              │
              ├── Python: llm_client.chat_completion(prompt)
              │     ├── 调用 DeepSeek API
              │     ├── temperature=config.llm_temperature_dialogue
              │     ├── max_tokens=300
              │     └── 提取 [ACTION:NAME] 标签
              │
              └── 返回结果到 C#
                    ├── PushResponse → AgentChatMenu 显示
                    ├── FriendshipSystem 评估对话内容
                    └── 事件驱动：触发 LLM 决策（对话结束）
```

---

## 八、退出机制

```
NPC 从 Agent 活跃态退出：
  │
  ├── [正常退出] Handler 跑完目标 → 收尾仪式 → ForceTransition(IDLE)
  │     └── OnStateChanged → 排队决策 → LLM 决定下一步
  │
  ├── [Vanilla-release] 连续 3 次 LLM 决策返回 IDLE
  │     ├── AgentTickLoop 把 NPC 加入 _releasedToVanilla
  │     ├── 每帧：npc.controller=null, npc.Speed=0
  │     └── 原版 schedule 接管（回到普通 NPC 行为）
  │         └── 任何非 IDLE 决策自动退出 release → 恢复 Agent 控制
  │
  ├── [死亡] NPC 血量归零
  │     └── AgentTickLoop 返回 Dead → ModEntry 移动 NPC 出地图
  │         └── 次日 DayStarted 自动复活
  │
  └── [DayEnd] 保存存档
        └── 清零当天对话计数、连续 IDLE 计数、释放标记
```

---

## 九、完整时间线（一次典型决策周期）

```
T=0s    OnUpdateTicked → _tickCounter = 1800 → Timer 触发
        └── Task.Run(MakeDecisionsAsync)
              ├── BuildDecisionContext
              ├── BuildDecisionRequest
              └── WebSocket → Python

T=0-1s  Python 构建 prompt → DeepSeek API → 返回 JSON
        └── WebSocket → C# 后台线程
              └── Enqueue 到 _pendingDecisions

T=1s    下一个 10-tick 周期：
        ├── 出队 + 守卫检查
        ├── ForceTransition(FARM)
        └── showTextAboveHead("看到好多成熟的作物...")

T=1s    ApplyControllerToNpc → FarmHandler.Update
        ├── ScanEnvironment → 找到3个成熟作物
        ├── MovementService.MoveTo(最近的作物)
        └── PathFindController 开始寻路

T=1-5s  每帧 FarmHandler.Update：
        ├── 移动中 → PathFindController 工作
        ├── 到达 → 执行收获动作
        └── 找下一个目标 → 继续移动

T=5s    所有作物收完 → 无目标
        ├── doEmote(24) 伸懒腰 → "呼，都收完了"
        └── ForceTransition(IDLE) → OnStateChanged

T=5s    OnStateChanged
        ├── SetEmotion(TaskCompleted)
        ├── AddMemory("刚才做完了farm")
        ├── LastEvent = "刚完成farm——可以接新任务了"
        └── 加入 _pendingTaskCompleteDecisions

T=5s-30s 每30秒 Timer 或触发事件 → 新一轮决策 → LLM 看到 LastEvent → 选 FOLLOW
        └── ForceTransition(FOLLOW) → AgentNavigator 跟随玩家
```
