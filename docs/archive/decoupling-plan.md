# ValleyAgent 架构解耦计划

## 目标
将C#模组中的所有AI智能逻辑迁移到Python Agent Server，C#模组仅作为游戏执行层。

## 当前状态
- ✅ C#决策核心已分析（AIDecisionEngine、DecisionOrchestrator、AIDialogueSystem）
- ✅ Python AgentCore已探索（agent_core.py、agent_memory.py、agent_state_machine.py）
- ✅ C#工具/动作系统已梳理（FarmHandler、FightHandler、MineHandler等）
- ❌ Python端缺少：决策限流、缓存、熔断器、工具调用基础设施
- ❌ 测试覆盖不足：缺少集成测试、模糊测试、功能测试

## 架构对比

### 当前架构
```
C# Mod (ValleyAgent)
├── AI决策 (AIDecisionEngine) → 调用Python WS
├── 对话系统 (AIDialogueSystem) → 调用Python WS  
├── 记忆系统 (AgentBrain) → C#本地
├── 状态机 (AgentStateMachine) → C#本地
├── 好友系统 (FriendshipSystem) → 调用Python WS
├── 礼物系统 (GiftSystem) → 调用Python WS
├── 处理器 (Handlers) → C#本地
└── 移动服务 (MovementService) → C#本地

Python Server
├── 7种消息处理器 → 无状态LLM代理
└── RAG引擎 → 知识查询
```

### 目标架构
```
C# Mod (ValleyAgent) - 纯执行层
├── 游戏状态读取 → 发送给Python
├── 命令执行 ← 接收Python命令
│   ├── 移动 (PathFindController)
│   ├── 攻击 (damageMonster)
│   ├── 收获/种植 (crop operations)
│   ├── 采集 (forage)
│   ├── 挖矿 (mine)
│   ├── 对话显示 (AgentChatMenu)
│   ├── 表情 (emote)
│   └── 状态切换 (StateMachine)
├── NPC点击拦截 → 打开UI，发送玩家输入给Python
└── 每tick同步 → 发送NPC状态快照

Python Server - Agent大脑
├── Agent核心
│   ├── 状态机 (StateMachine)
│   ├── 记忆系统 (Memory)
│   ├── 情绪系统 (Emotion)
│   ├── 决策引擎 (DecisionEngine) ← 新增：限流/缓存/熔断器
│   └── 对话系统 (DialogueSystem)
├── 工具调用 (Tool Calling) ← 新增：12+工具
│   ├── speak, emote, move_to
│   ├── follow_player, harvest, water
│   ├── attack, mine, forage
│   ├── give_item, set_state, wait
│   └── 观察环境、规划路径、执行动作、获取结果
├── RAG引擎 → 知识查询
└── 7种消息处理器 → 保留兼容
```
C# Mod (ValleyAgent)
├── AI决策 (AIDecisionEngine) → 调用Python WS
├── 对话系统 (AIDialogueSystem) → 调用Python WS  
├── 记忆系统 (AgentBrain) → C#本地
├── 状态机 (AgentStateMachine) → C#本地
├── 好友系统 (FriendshipSystem) → 调用Python WS
├── 礼物系统 (GiftSystem) → 调用Python WS
├── 处理器 (Handlers) → C#本地
└── 移动服务 (MovementService) → C#本地

Python Server
├── 7种消息处理器 → 无状态LLM代理
└── RAG引擎 → 知识查询
```

### 目标架构
```
C# Mod (ValleyAgent) - 纯执行层
├── 游戏状态读取 → 发送给Python
├── 命令执行 ← 接收Python命令
│   ├── 移动 (PathFindController)
│   ├── 攻击 (damageMonster)
│   ├── 收获/种植 (crop operations)
│   ├── 采集 (forage)
│   ├── 挖矿 (mine)
│   ├── 对话显示 (AgentChatMenu)
│   ├── 表情 (emote)
│   └── 状态切换 (StateMachine)
├── NPC点击拦截 → 打开UI，发送玩家输入给Python
└── 每tick同步 → 发送NPC状态快照

Python Server - Agent大脑
├── Agent核心
│   ├── 状态机 (StateMachine)
│   ├── 记忆系统 (Memory)
│   ├── 情绪系统 (Emotion)
│   ├── 决策引擎 (DecisionEngine)
│   └── 对话系统 (DialogueSystem)
├── 工具调用 (Tool Calling)
│   ├── 观察环境
│   ├── 规划路径
│   └── 执行动作
└── LLM客户端 (llm_client)
```

## 标准化通信协议

### 消息类型

#### 1. 状态同步 (state_sync)
**C# → Python** (每60 ticks / 1秒)
```json
{
  "type": "state_sync",
  "requestId": "uuid",
  "timestamp": 123456,
  "npcs": [
    {
      "name": "Abigail",
      "location": "Farm",
      "position": {"x": 64, "y": 15},
      "health": 100,
      "maxHealth": 100,
      "state": "IDLE",
      "inventory": ["Amethyst", null, null],
      "friendship": 1250,
      "emotion": "Happy",
      "nearbyObjects": ["crop at (65,15)", "monster at (70,20)"],
      "memory": ["Player said hi", "Helped farm yesterday"]
    }
  ],
  "gameState": {
    "time": "14:00",
    "season": "Spring",
    "day": 15,
    "year": 2,
    "weather": "Sunny",
    "playerPosition": {"x": 64, "y": 15},
    "playerLocation": "Farm"
  }
}
```

#### 2. 决策请求 (decision_request)
**Python内部触发** → 调用LLM → 返回命令
```json
{
  "type": "decision_request",
  "requestId": "uuid",
  "npcName": "Abigail",
  "context": {
    "currentState": "IDLE",
    "timeSinceLastDecision": 30,
    "recentEvents": ["player_clicked", "crop_matured"]
  }
}
```

#### 3. 命令下发 (command)
**Python → C#**
```json
{
  "type": "command",
  "requestId": "uuid",
  "npcName": "Abigail",
  "commands": [
    {"action": "set_state", "state": "FARM", "reason": "看到成熟作物"},
    {"action": "move_to", "target": {"x": 65, "y": 15}, "speed": 2},
    {"action": "harvest", "target": {"x": 65, "y": 15}},
    {"action": "speak", "text": "我来帮你收菜！"}
  ]
}
```

#### 4. 对话请求 (dialogue_request)
**C# → Python** (玩家发送消息时)
```json
{
  "type": "dialogue_request",
  "requestId": "uuid",
  "npcName": "Abigail",
  "playerInput": "你好！今天天气不错",
  "context": {
    "location": "Farm",
    "time": "14:00",
    "weather": "Sunny",
    "nearbyObjects": ["crop", "tree"]
  }
}
```

#### 5. 对话响应 (dialogue_response)
**Python → C#**
```json
{
  "type": "dialogue_response",
  "requestId": "uuid",
  "npcName": "Abigail",
  "text": "是啊！很适合出去冒险呢！",
  "emotion": "Happy",
  "action": "FOLLOW",
  "actionReason": "想跟着玩家"
}
```

#### 6. 玩家输入 (player_input)
**C# → Python** (NPC被点击时)
```json
{
  "type": "player_input",
  "requestId": "uuid",
  "npcName": "Abigail",
  "inputType": "click",
  "context": {
    "location": "Farm",
    "time": "14:00"
  }
}
```

#### 7. 事件通知 (event)
**C# → Python** (游戏事件)
```json
{
  "type": "event",
  "requestId": "uuid",
  "npcName": "Abigail",
  "eventType": "gift_received",
  "data": {
    "itemName": "Amethyst",
    "itemId": "66",
    "taste": "Love"
  }
}
```

#### 8. 动作结果 (action_result)
**C# → Python** (命令执行后)
```json
{
  "type": "action_result",
  "requestId": "uuid",
  "npcName": "Abigail",
  "action": "harvest",
  "success": true,
  "result": {
    "itemObtained": "Parsnip",
    "quantity": 1
  }
}
```

## 实施阶段

### Phase 1: 协议标准化 (当前)
- [x] 设计完整通信协议
- [ ] 在Python端实现协议处理器
- [ ] 在C#端实现协议客户端

### Phase 2: Python Agent核心
- [ ] 实现Agent状态机
- [ ] 实现记忆系统 (JSON/SQLite)
- [ ] 实现决策引擎
- [ ] 实现对话系统
- [ ] 实现工具调用框架

### Phase 3: C#执行层重构
- [ ] 移除AIDecisionEngine
- [ ] 移除AIDialogueSystem
- [ ] 简化AgentBrain (只保留游戏状态)
- [ ] 实现命令执行器
- [ ] 实现状态同步发送器

### Phase 4: 集成测试
- [ ] 单元测试协议序列化
- [ ] 集成测试端到端流程
- [ ] 性能测试延迟

### Phase 5: 审查和修复
- [ ] 审查Agent代码审查
- [ ] 修复问题
