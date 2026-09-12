# ValleyTalk 架构设计文档

本文档描述 ValleyTalk v2.0.0 的系统架构、核心设计决策和模块职责。

## 设计目标

1. **游戏无关核心**：所有业务逻辑不依赖 SMAPI 类型，便于单元测试和跨项目复用
2. **本地优先**：默认使用本地 LLM（LM Studio），零外部依赖，零网络延迟
3. **永不阻塞游戏线程**：所有 LLM 调用均为异步（`Task.Run` + `async/await`）
4. **可配置规模**：默认 1 个 Agent，最大支持 5 个，性能自适应
5. **向后兼容**：Content Patcher 数据包保持不变，存档自动迁移

## 系统架构图

```
┌─────────────────────────────────────────────────────────────────┐
│                         SMAPI / Game Layer                       │
│  (ModEntry, Game1, NPC, Location, Time, Events...)               │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      AgentService (编排器)                        │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌──────────┐  │
│  │ Agents  │ │  AI     │ │Controllers│ │ Dialogue│ │Performance│  │
│  │ Manager │ │ Engine  │ │ (5 types) │ │ System  │ │ Monitor   │  │
│  │ Manager │ │ Engine  │ │ (5 types) │ │ System  │ │ Monitor   │  │
│  └─────────┘ └─────────┘ └─────────┘ └─────────┘ └──────────┘  │
│  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌──────────┐  │
│  │Friendship│ │  Gifts  │ │ Circuit │ │   RAG   │ │   Save   │  │
│  │ System  │ │ System  │ │ Breaker │ │Knowledge│ │ Manager  │  │
│  └─────────┘ └─────────┘ └─────────┘ └─────────┘ └──────────┘  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      LLM Provider 抽象层                         │
│  ┌────────────┐ ┌────────┐ ┌──────────┐ ┌──────────┐            │
│  │ LM Studio  │ │  Kimi  │ │ DeepSeek │ │ OpenRouter│            │
│  │ (本地默认)  │ │        │ │          │ │          │            │
│  └────────────┘ └────────┘ └──────────┘ └──────────┘            │
└─────────────────────────────────────────────────────────────────┘
```

## 核心子系统

### 1. Agent 分配管理（AgentAllocationManager）

**职责**：决定哪些 NPC 成为 AI Agent。

**设计决策**：
- **优先级算法**：对话频率 × 0.4 + 礼物频率 × 0.3 + 友谊等级 × 0.3
- **手动覆盖**：玩家可通过控制台命令强制指定 Agent
- **优雅降级**：超过最大数量时，自动移除最低优先级的 Agent
- **线程安全**：`lock` + `ConcurrentDictionary` 快照

**配置**：`MaxAgentNpcs`（1-5，默认 1）

### 2. AI 决策引擎（AIDecisionEngine）

**职责**：每 5 分钟驱动一次 Agent 行为决策。

**工作流程**：
1. 构建上下文提示（RAG 知识 + 当前游戏状态 + Agent 历史 + **环境扫描结果**）
2. 发送 LLM 请求（全局 `SemaphoreSlim` 限制并发 = 1）
3. 解析决策 JSON（目标状态 + 理由）
4. 触发状态转换（受最短持续时间限制）

**环境感知**：
每个 Handler 提供 `ScanEnvironment()` 方法，将附近怪物/作物/矿石/采集物信息注入 Prompt：
```
nearby_objects: "Monster nearby: Green Slime at tile (12, 15); Mature crop ready to harvest at tile (8, 9)"
```

**紧急状态拦截**：
独立于 LLM 决策的实时检查：如果 NPC 附近有怪物且当前不在 FIGHT 状态，**强制切换**到 FIGHT。

**缓存策略**：
- 5 分钟冷却：同一 Agent 在 5 分钟内不会重复决策
- 无响应时回退：`IdleController` 接管

### 3. 行为控制器层（Controllers）

每个 Agent 拥有独立的控制器实例（状态隔离）。

| 控制器 | 职责 | 触发条件 |
|--------|------|----------|
| **FollowController** | 跟随玩家移动 | 距离 > FollowDistance 格时移动 |
| **FightController** | 协助战斗 | 玩家进入战斗且 Agent 有武器 |
| **FarmController** | 浇水、收获 | 农场任务队列非空，天气/季节感知 |
| **ForageController** | 地图采集 | 搜索 → 移动 → 拾取，上限 5 个物品 |
| **IdleController** | 空闲行为 | 默认状态：漫步 40% / 张望 20% / 表情 20% / 静止 20% |

### 4.1 任务 Handler 层（Handlers）

在 Controller 之上，任务状态（FIGHT/FARM/MINE/FORAGE）由专门的 Handler 处理：

| Handler | 职责 | 动画 |
|---------|------|------|
| **FightHandler** | 扫描怪物 → 寻路 → 攻击（伤害/暴击） | 身体挥剑动画 + 武器精灵绘制 |
| **FarmHandler** | 收割成熟作物 / 浇灌干旱土壤 | 浇水水花动画 |
| **MineHandler** | 砸可破坏石头/矿石节点 | 镐子挥动动画 + 工具精灵绘制 |
| **ForageHandler** | 拾取自然生成物品 | 弯腰拾取动画 + 叶子特效 |

**设计要点**：
- Handler 在每 tick 的 `UpdateTicked` 中直接操作 `NPC` 对象
- 使用 `PathFindController` 做同地图寻路，`AgentNavigator` 处理跨地图
- 攻击/动作有冷却计数器（~0.8s），防止无限连击
- 武器绘制通过 `RenderedWorld` 事件在每帧绘制 `MeleeWeapon` 精灵

**设计原则**：
- 每个控制器实现 `IAgentController`
- `AgentService` 通过工厂方法创建（`Func<T>`），避免状态共享
- 控制器内部使用 `CancellationToken` 支持中断

### 4. 状态机框架（StateMachine）

**接口**：`IAgentState`

**8 种状态**：
- `IDLE` — 空闲，可被任何状态打断
- `FOLLOW` — 跟随玩家
- `FIGHT` — 战斗模式
- `FARM` — 农场劳作
- `FORAGE` — 地图采集
- `MINE` — 矿洞探索
- `TALK` — 对话中（不可打断）
- `THINKING` — 决策思考中（LLM 调用中）

**转换规则**：
- `TALK` 状态不可被其他状态打断（防止对话中断）
- `THINKING` 状态可转换到任何状态（LLM 决策结果）
- 其他转换通过 `CanTransitionTo()` 显式控制

**最短持续时间**：每个状态有最小持续时间（如 FIGHT 15 秒），防止 LLM 决策抖动。

### 5. 智能对话系统（AIDialogueSystem）

**异步设计**：
```
玩家对话请求 → 异步 LLM 调用（不阻塞游戏线程）
                     ↓
              响应到达 → 更新 NPC 对话文本
                     ↓
              玩家看到 LLM 生成的回复
```

**熔断降级**：
- LLM 连续失败 3 次 → 断路器打开 → 使用本地预设对话
- 半开状态下尝试恢复，成功后关闭断路器

**内容过滤**：
- 黑名单关键词过滤
- 敏感内容检测（基于规则）
- 异常响应回退到预设对话

### 6. RAG 知识库（RAGKnowledgeBase）

**设计决策**：静态 JSON 文件而非向量数据库

**原因**：
- 零外部依赖（无需 Pinecone/Milvus/Chroma）
- 启动时加载，O(1) 查询
- 数据量小（< 1MB JSON），向量搜索无优势

**数据结构**：
```json
{
  "npcs": { "Abigail": { "personality": "adventurous", "likes": [...] } },
  "items": { "Amethyst": { "type": "gem", "gift_taste": "love" } },
  "locations": { "Farm": { "type": "player_farm", "owner": "player" } },
  "festivals": { "Egg Festival": { "date": "Spring 13", "activities": [...] } }
}
```

### 7. 友谊系统（FriendshipSystem）

**LLM 评估流程**：
1. 玩家与 Agent 互动（对话/送礼）
2. LLM 评估互动质量（1-10 分）
3. 计算友谊增量：`(quality - 5) * base_multiplier`
4. **防刷机制**：同一行为 100% → 递减至 10%（对数衰减）
5. **生日加成**：生日当天 3 倍

**线程安全**：`ConcurrentDictionary<string, FriendshipData>`

### 8. 礼物系统（GiftSystem）

**双向礼物**：
- **NPC → 玩家**：Agent 主动赠送物品（基于友谊等级 + LLM 决策）
- **玩家 → NPC**：标准 SMAPI 礼物系统 + 安全校验

**4 层安全校验**：
1. 白名单：仅允许配置列表中的物品
2. 距离检查：必须在 2 格以内
3. 冷却：同一 NPC 每天最多 1 次
4. 能力检查：Agent 必须有"给予"能力标记

### 9. 断路器（CircuitBreaker）

**状态机**：
```
CLOSED  → (连续失败 3 次) → OPEN
  ↑                            │
  └──── (半开成功) ← HALF_OPEN ←┘
```

**行为**：
- **CLOSED**：正常调用 LLM
- **OPEN**：拒绝 LLM 调用，使用本地预设
- **HALF_OPEN**：每 30 秒尝试一次，成功后关闭

### 10. 性能监控（PerformanceMonitor + TokenBudgetManager）

**指标**：
- LLM 调用次数/延迟/令牌消耗
- 决策引擎吞吐量
- 缓存命中率

**令牌预算**：
- 每日重置（6:00 AM）
- 达到预算上限后：禁用非必要 LLM 调用，保留对话
- 预算 = 0 表示无限制

## 线程安全策略

| 组件 | 机制 |
|------|------|
| Agent 映射 | `Dictionary` + `lock` |
| 友谊数据 | `ConcurrentDictionary` |
| 礼物历史 | `lock` + 不可变快照 |
| LLM 并发 | 全局 `SemaphoreSlim(2)` |
| 性能计数器 | `Interlocked` 原子操作 |
| 待处理决策 | `Queue` + `lock` |

## 存档数据格式

**版本**：v2.0.0（从 v1.0.2 自动迁移）

```json
{
  "Version": "2.0.0",
  "Agents": [
    {
      "NpcName": "Abigail",
      "CurrentState": "IDLE",
      "PriorityScore": 85.5,
      "IsManuallyOverridden": false
    }
  ],
  "FriendshipHistory": {
    "Abigail": [
      { "Date": "Spring 5 Year 1", "Change": 10, "Reason": "Gift_Loved" }
    ]
  },
  "GiftHistory": {
    "Abigail": [
      { "ItemId": "66", "ItemName": "Amethyst", "Date": "Spring 5 Year 1", "Direction": "PlayerToNpc" }
    ]
  }
}
```

**迁移逻辑**（`SaveDataManager`）：
- v1.0.2 → v2.0.0：添加 `Version` 字段，转换旧格式
- 状态清理：将 `THINKING` 替换为 `IDLE`（v2 中已移除）

## 事件驱动架构

`ModEntry` 订阅 11 个 SMAPI 事件：

| 事件 | 处理逻辑 |
|------|----------|
| `GameLaunched` | 初始化 GMCM、注册控制台命令 |
| `SaveLoaded` | 加载存档数据、分配 Agent |
| `Saving` | 保存 Agent 状态 |
| `DayStarted` | 每日重置（令牌预算、缓存、决策队列）+ 修复 NPC Schedule |
| `RenderedWorld` | 绘制 NPC 武器/工具精灵 |
| `DayEnding` | 归档当日友谊/礼物记录 |
| `TimeChanged` | 每小时检查一次决策（6:00 AM - 10:00 PM） |
| `UpdateTicked` | 每 10 ticks 更新控制器状态 |
| `ButtonPressed` | F9 切换调试 HUD |
| `Warped` | 玩家传送时重置跟随状态 |

## 扩展点

### 添加新的 LLM 提供商

实现 `ILLMProvider` 接口，在 `ModEntry` 中注册：

```csharp
// 在 AgentService 构造函数中添加
Provider.Custom => new CustomProvider(config),
```

### 添加新的行为状态

1. 创建 `IAgentState` 实现
2. 在 `AgentService` 中注册转换规则
3. 创建对应的 `IAgentController`
4. 在 `ActionValidator` 中添加校验逻辑

### 添加新的控制器

1. 实现 `IAgentController` 接口
2. 在 `AgentService` 中添加工厂方法
3. 在状态机中定义进入/退出逻辑

## 关键设计决策记录

### 1. 为什么不用向量数据库？

**背景**：传统 RAG 使用 Pinecone/Chroma 等向量数据库存储嵌入。

**决策**：使用静态 JSON 文件。

**理由**：
- 数据量极小（< 1MB），全量加载内存无压力
- 向量搜索的语义优势在此场景不明显（查询多为精确匹配）
- 消除外部依赖，降低安装复杂度
- 启动时间 < 50ms

**代价**：无法做语义模糊匹配（如"我喜欢紫色的东西"→"紫水晶"）。
**缓解**：LLM 本身具备语义理解能力，JSON 仅提供事实检索。

### 2. 为什么每个 Agent 独立控制器实例？

**背景**：考虑过共享控制器实例以节省内存。

**决策**：每个 Agent 拥有独立的控制器实例。

**理由**：
- 控制器内部有可变状态（目标位置、当前路径、冷却计时器）
- 共享实例会导致状态竞争和逻辑错误
- 5 个 Agent × 5 个控制器 = 25 个实例，内存占用 < 1MB

### 3. 为什么默认只支持 1 个 Agent？

**背景**：系统设计上支持最多 5 个 Agent。

**决策**：默认 1 个，玩家手动上调。

**理由**：
- 本地 LLM 推理资源有限（7B 模型单并发已占用 4-8GB VRAM）
- 多个 Agent 同时调用 LLM 会导致排队延迟
- 单个 Agent 已能提供显著的游戏体验提升

### 4. 为什么使用 `Task.Run` 而不是纯 `async/await`？

**背景**：SMAPI 游戏线程是单线程的，任何阻塞都会导致卡顿。

**决策**：LLM 调用使用 `Task.Run(() => ...)` 包装。

**理由**：
- `HttpClient` 的异步操作在 .NET 中实际上仍会占用线程池线程
- `Task.Run` 确保 LLM 逻辑在后台线程执行，彻底释放游戏线程
- 配合 `SemaphoreSlim` 限制并发，避免线程池耗尽

### 5. 为什么使用静态 JSON 而不是运行时生成？

**背景**：RAG 知识库需要包含所有 NPC、物品、地点等信息。

**决策**：预生成静态 JSON，打包到模组中。

**理由**：
- 游戏内容相对固定（1.6 版本后无大更新）
- 运行时生成需要反射遍历所有游戏对象，启动慢且易出错
- 静态文件可以被 Content Patcher 覆盖（模组兼容性）
- 便于版本控制和 diff

## 性能特征

| 指标 | 目标 | 实测 |
|------|------|------|
| 启动时间 | < 2s | ~800ms |
| LLM 调用延迟 | < 3s | 取决于模型 |
| 决策间隔 | 5 分钟 | 固定 |
| 缓存命中率 | > 80% | N/A（首次运行） |
| 内存占用 | < 50MB | ~35MB |
| 单帧耗时 | < 1ms | ~0.3ms |
| 单元测试 | 263 个 | 全部通过 |

## 故障模式与恢复

| 故障 | 检测 | 恢复 |
|------|------|------|
| LLM 无响应 | 超时（30s） | 重试 3 次 → 断路器打开 → 预设对话 |
| LLM 返回异常 | HTTP 5xx/解析失败 | 记录日志 → 使用预设 |
| 游戏线程阻塞 | UpdateTicked 耗时监控 | 强制中断异步任务 |
| 存档损坏 | JSON 解析失败 | 回退到默认状态，保留旧存档备份 |
| 内存泄漏 | 性能监控采样 | 定期清理缓存，限制最大条目数 |

---

*文档版本：v2.0.0 | 最后更新：2024*
