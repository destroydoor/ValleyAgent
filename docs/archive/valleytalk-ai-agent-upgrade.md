# ValleyTalk AI Agent NPC 升级计划

## TL;DR

> **核心目标**: 将ValleyTalk从简单AI对话模组升级为真正的AI Agent NPC系统，由LLM动态控制NPC行为状态机
> 
> **关键创新**: AI Agent替代硬编码规则，自主决策跟随/战斗/农场/采集/对话行为
> 
> **交付物**:
> - 多提供商LLM抽象层（LM Studio/Kimi/DeepSeek/OpenRouter）
> - 动态Agent分配系统（1-5个AI NPC）
> - AI状态机（LLM决策 + 异步执行）
> - 好感度/送礼系统（4层安全反幻觉）
> - RAG增强（静态JSON知识库）
> - 中度自主行动（跟随/战斗/浇水/采集）
> - 完整存档持久化
> 
> **Estimated Effort**: Large (6-8 weeks)
> **Parallel Execution**: YES - 5 waves
> **Critical Path**: 项目设置 → LLM Provider → 状态机框架 → AI决策引擎 → 行为控制器 → SMAPI集成 → 存档系统 → 最终验证

---

## Context

### Original Request
用户要求升级ValleyTalk模组，让AI Agent控制NPC状态机，实现真正的智能NPC行为。

### Interview Summary
**Key Discussions**:
- **决策频率**: 混合模式（空闲定时检查 + 事件即时触发）
- **LLM上下文**: 完整4K+ tokens（完整关系历史 + 长期记忆）
- **状态持久化**: 保存到存档（下次加载继续）
- **Agent数量**: v1.0默认1个，可配置上限5个
- **RAG**: v1.0预计算静态JSON，v1.1+考虑向量数据库

**Research Findings**:
- ValleyTalk只有编译后的DLL，无源代码 → 必须完全重写
- NPC Adventures v0.16.3架构可复用（StateMachine + Controller模式）
- SMAPI 4.x支持.NET 6+，但测试生态不成熟
- 社区mod几乎无单元测试，依赖手动游戏内测试

### Metis Review
**Identified Gaps** (addressed):
- **无源代码**: 决定完全重写，保留CP数据
- **RAG基础设施**: 决定v1.0用静态JSON，消除基础设施依赖
- **LLM延迟**: 强制异步模式，游戏线程永不阻塞
- **多Agent复杂性**: v1.0限制1个Agent上线
- **保存兼容**: 保持相同UniqueID，添加版本字段
- **NPC Adventures过时**: 仅参考架构模式，不引用DLL

---

## Work Objectives

### Core Objective
构建一个AI Agent驱动的NPC系统，LLM动态控制NPC行为状态，实现真正的智能交互。

### Concrete Deliverables
- `ValleyTalk.dll` - 主模组（.NET 6+，SMAPI 4.x）
- `[CP] ValleyTalk Base/` - 更新的Content Patcher数据包
- `ValleyTalk for SVE/` - 更新的SVE扩展包
- `RAG/` - 静态JSON知识库
- `docs/` - 开发文档和API参考

### Definition of Done
- [ ] 所有任务完成并通过QA验证
- [ ] SMAPI控制台无错误加载
- [ ] 1个Agent NPC可完整运行（对话/跟随/战斗/农场）
- [ ] 存档/读档正常，状态持久化
- [ ] 单元测试通过（xUnit）

### Must Have
- 多提供商LLM支持（至少LM Studio + 1个远程API）
- AI Agent状态机（LLM决策行为状态）
- 动态Agent分配（1-5个NPC）
- 好感度系统（LLM评估 + 安全限制）
- 4层安全礼物系统
- RAG静态知识库
- 中度自主行动（跟随/战斗/浇水/采集）
- 存档持久化
- 异步LLM调用（不阻塞游戏）

### Must NOT Have (Guardrails)
- **绝不实现**: 多人联机、完整Quest系统、自定义NPC创建、重度农场自动化
- **绝不迁移**: 现有CP prompts到C#代码（864+ keys保持CP格式）
- **绝不支持**: LLM直接修改游戏状态（必须通过验证层）
- **绝不引用**: NPC Adventures或PurrplingCore DLL
- **绝不使用**: 同步LLM调用（必须异步）

---

## Verification Strategy

### Test Decision
- **Infrastructure exists**: YES（.NET 6 + xUnit可用）
- **Automated tests**: YES (Tests-after) - 核心逻辑单元测试
- **Framework**: xUnit + Moq
- **SMAPI集成测试**: Agent QA（手动验证脚本）

### QA Policy
Every task MUST include agent-executed QA scenarios.
Evidence saved to `.sisyphus/evidence/task-{N}-{scenario-slug}.{ext}`.

- **Library/Module**: Use Bash (dotnet test) - 运行单元测试，验证输出
- **SMAPI Integration**: Agent手动验证 - 检查日志输出、配置文件

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Foundation - Start Immediately):
├── Task 1: 项目设置和SMAPI 4.x适配
├── Task 2: LLM Provider抽象层
├── Task 3: 配置系统（config.json + GMCM）
├── Task 4: RAG静态JSON知识库
└── Task 5: 基础状态机框架

Wave 2 (Core AI - After Wave 1):
├── Task 6: Agent分配管理器
├── Task 7: AI决策引擎（LLM异步调用）
├── Task 8: 动作验证层（IActionValidator）
├── Task 9: 好感度系统
├── Task 10: 礼物系统（4层安全）
└── Task 11: Circuit Breaker和降级系统

Wave 3 (Behaviors - After Wave 2):
├── Task 12: 跟随控制器（FollowController）
├── Task 13: 战斗控制器（FightController）
├── Task 14: 农场控制器（FarmController）
├── Task 15: 采集控制器（ForageController）
├── Task 16: 对话系统（AI生成 + CP降级）
└── Task 17: 空闲/思考状态

Wave 4 (Integration - After Wave 3):
├── Task 18: SMAPI事件集成
├── Task 19: 存档持久化
├── Task 20: HUD/UI显示
├── Task 21: 调试和日志系统
└── Task 22: 错误处理和边界情况

Wave 5 (Polish & SVE - After Wave 4):
├── Task 23: SVE add-on升级
├── Task 24: i18n支持（中文/英文）
├── Task 25: 单元测试（xUnit + Moq）
└── Task 26: 性能优化（缓存/token预算）

Wave FINAL (Verification):
├── Task F1: 整合测试和回归验证
├── Task F2: 代码质量审查
├── Task F3: 性能基准测试
└── Task F4: 文档和发布准备
```

### Dependency Matrix

| Task | Depends On | Blocks |
|------|-----------|--------|
| 1 (项目设置) | - | 2,3,4,5 |
| 2 (LLM Provider) | 1 | 7,11 |
| 3 (配置系统) | 1 | 6,7 |
| 4 (RAG) | 1 | 7,9,10 |
| 5 (状态机框架) | 1 | 6,12-17 |
| 6 (Agent分配) | 3,5 | 18 |
| 7 (AI决策引擎) | 2,3,4 | 12-17 |
| 8 (动作验证) | 5 | 12-17 |
| 9 (好感度) | 4 | 19 |
| 10 (礼物系统) | 4,8 | 16 |
| 11 (Circuit Breaker) | 2 | 7,16 |
| 12-17 (行为控制器) | 5,7,8 | 18 |
| 18 (SMAPI集成) | 6,12-17 | 19,20 |
| 19 (存档持久化) | 9,18 | F1 |
| 20 (HUD/UI) | 18 | F1 |
| 21 (调试日志) | 18 | F1 |
| 22 (错误处理) | 8,11 | F1 |
| 23 (SVE升级) | 1,4 | F1 |
| 24 (i18n) | 1 | F1 |
| 25 (单元测试) | 2,4,6,9 | F2 |
| 26 (性能优化) | 7,12-17 | F3 |

---

## TODOs

- [x] 1. **项目设置和SMAPI 4.x适配**

  **What to do**:
  - 创建新的.NET 6类库项目 `ValleyTalk.csproj`
  - 添加SMAPI 4.x NuGet包引用 (`Pathoschild.Stardew.ModBuildConfig` 最新版)
  - 设置`EnableHarmony=true`，配置x86平台目标
  - 创建基础Mod入口类 `ModEntry.cs` (继承 `StardewModdingAPI.Mod`)
  - 注册基本SMAPI事件处理器 (GameLoop.GameLaunched, SaveLoaded等)
  - 创建`manifest.json` (保持UniqueID `dandm1.ValleyTalk`，版本升至2.0.0)
  - 配置`.gitignore` (排除bin/obj/config.json含API key)

  **Must NOT do**:
  - 不要引用NPC Adventures或PurrplingCore DLL
  - 不要目标.NET Framework 4.5.2
  - 不要将API key硬编码到任何文件

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 纯项目脚手架，标准.NET/SMAPI设置
  - **Skills**: []
  - **Skills Evaluated but Omitted**: `git-master` (不需要复杂git操作)

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 2-5)
  - **Blocks**: Tasks 2,3,4,5
  - **Blocked By**: None

  **References**:
  - **Pattern**: SMAPI官方mod构建指南 - 标准项目结构
  - **Existing**: `NpcAdventures-master/NpcAdventure.csproj` - 参考项目结构（但升级到.NET 6）
  - **External**: https://stardewvalleywiki.com/Modding:Modder_Guide/Get_Started

  **Acceptance Criteria**:
  - [ ] `dotnet build` 成功编译
  - [ ] `manifest.json` 包含正确UniqueID和版本2.0.0
  - [ ] ModEntry.cs 成功注册GameLaunched事件
  - [ ] 无编译警告（TreatWarningsAsErrors=false，但0 warnings理想）

  **QA Scenarios**:
  ```
  Scenario: 项目编译成功
    Tool: Bash
    Preconditions: 空项目，仅安装了SMAPI NuGet包
    Steps:
      1. 运行 `dotnet build`
      2. 检查输出目录存在 `ValleyTalk.dll`
    Expected Result: Build succeeds with 0 errors
    Evidence: .sisyphus/evidence/task-1-build-success.log
  ```

  **Commit**: YES (单独commit)
  - Message: `chore: project setup and SMAPI 4.x configuration`

- [x] 2. **LLM Provider抽象层**

  **What to do**:
  - 创建 `ILLMProvider` 接口，定义统一LLM调用契约
  - 实现 `LMStudioProvider` (本地HTTP，OpenAI兼容API)
  - 实现 `KimiProvider` (Moonshot API)
  - 实现 `DeepSeekProvider` (保留原有支持)
  - 实现 `OpenRouterProvider` (保留原有支持)
  - 每个Provider支持：chat completions、stream选项、timeout、retry
  - 创建 `LLMProviderFactory` 根据配置实例化对应Provider
  - 所有Provider使用 `System.Net.Http.HttpClient` + async/await

  **Must NOT do**:
  - 不要同步调用HTTP (必须async)
  - 不要将API key存储在代码中
  - 不要依赖特定Provider的专属功能（保持接口通用）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要正确处理HTTP异步、错误处理、多提供商适配
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 3,4,5)
  - **Parallel Group**: Wave 1
  - **Blocks**: Task 7, 11
  - **Blocked By**: Task 1

  **References**:
  - **Existing**: 反向工程ValleyTalk.dll的LLM调用逻辑（DeepSeek/OpenRouter）
  - **External**: LM Studio API docs (localhost:1234/v1/chat/completions)
  - **External**: Kimi API docs (platform.moonshot.cn)

  **Acceptance Criteria**:
  - [ ] `ILLMProvider` 接口定义完成
  - [ ] 至少实现LMStudioProvider + 1个远程Provider
  - [ ] 所有Provider通过单元测试（mock HttpClient）
  - [ ] 超时和重试逻辑正确

  **QA Scenarios**:
  ```
  Scenario: LM Studio Provider调用成功
    Tool: Bash (dotnet test)
    Preconditions: mock HttpClient返回OpenAI格式JSON
    Steps:
      1. 调用 provider.ChatCompletionAsync(messages)
      2. 验证返回正确的AssistantMessage
      3. 验证HTTP请求格式正确
    Expected Result: 返回解析后的消息，无异常
    Evidence: .sisyphus/evidence/task-2-llm-provider-test.log

  Scenario: Provider超时处理
    Tool: Bash (dotnet test)
    Preconditions: mock HttpClient延迟10秒，timeout=1秒
    Steps:
      1. 调用 provider.ChatCompletionAsync
      2. 捕获TimeoutException
    Expected Result: 抛出TimeoutException，不阻塞
    Evidence: .sisyphus/evidence/task-2-timeout-test.log
  ```

  **Commit**: YES (groups with Task 1)
  - Message: `feat: multi-provider LLM abstraction layer`

- [x] 3. **配置系统（config.json + GMCM）**

  **What to do**:
  - 创建 `ModConfig` 类，包含所有配置项：
    - `Provider`: LLM提供商选择 (LMStudio/Kimi/DeepSeek/OpenRouter)
    - `ApiKey`: API密钥（远程API必填，本地可不填）
    - `ServerAddress`: 本地LLM服务器地址（默认http://localhost:1234）
    - `Model`: 模型名称
    - `MaxAgentNpcs`: 最大Agent NPC数量（默认1，范围1-5）
    - `EnableFriendshipChanges`: 启用好感度变化（默认true）
    - `EnableGifts`: 启用AI送礼（默认true）
    - `TokenBudget`: 每会话token预算（默认0=无限制）
    - `DebugMode`: 调试模式（默认false）
  - 集成Generic Mod Config Menu (GMCM) 提供游戏内配置UI
  - 配置热重载支持（无需重启游戏）
  - 默认值处理：旧存档缺少字段时自动填充

  **Must NOT do**:
  - 不要将默认API key写入代码
  - 不要要求玩家必须配置远程API（LM Studio为零配置）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 标准SMAPI配置模式
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 2,4,5)
  - **Parallel Group**: Wave 1
  - **Blocks**: Task 6, 7
  - **Blocked By**: Task 1

  **References**:
  - **Existing**: `ValleyTalk Base/config.json` - 参考现有配置格式
  - **External**: GMCM文档 (https://github.com/spacechase0/GenericModConfigMenu)

  **Acceptance Criteria**:
  - [ ] ModConfig类完整定义
  - [ ] GMCM集成完成，游戏内可配置
  - [ ] 配置读取/保存正常
  - [ ] 旧配置自动迁移

  **QA Scenarios**:
  ```
  Scenario: 配置读取和保存
    Tool: Bash (dotnet test)
    Preconditions: 测试配置JSON文件
    Steps:
      1. 读取config.json
      2. 验证所有字段有默认值
      3. 修改并保存
    Expected Result: 读写正确，无异常
    Evidence: .sisyphus/evidence/task-3-config-test.log
  ```

  **Commit**: YES (groups with Wave 1)

- [x] 4. **RAG静态JSON知识库**

  **What to do**:
  - 创建 `RAGKnowledgeBase` 类，管理静态游戏知识
  - 构建JSON数据结构，包含：
    - `npcs.json` - NPC基本信息（喜好、生日、性格）
    - `items.json` - 物品信息（ID、类型、季节、用途）
    - `locations.json` - 地点信息（居民、资源、事件）
    - `festivals.json` - 节日信息（日期、活动、参与NPC）
    - `mechanics.json` - 游戏机制（好感度规则、送礼规则、结婚条件）
  - 实现查询接口：按NPC名、物品名、地点名检索
  - 预计算关键数据（如每个NPC的最喜欢/喜欢/中立/不喜欢/讨厌礼物列表）
  - 知识库在Mod启动时加载，常驻内存

  **Must NOT do**:
  - 不要依赖外部向量数据库（v1.0）
  - 不要实时爬取wiki（数据静态打包）
  - 不要加载未使用的数据（按需加载）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要整理和结构化大量游戏数据
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 2,3,5)
  - **Parallel Group**: Wave 1
  - **Blocks**: Task 7, 9, 10
  - **Blocked By**: Task 1

  **References**:
  - **Existing**: `ValleyTalk/[CP] ValleyTalk Base/assets/GameSummary.json` - 现有游戏数据
  - **Existing**: `stardew-valley-data` npm包数据（1,900+ items）
  - **External**: Stardew Valley Wiki (stardewvalleywiki.com)

  **Acceptance Criteria**:
  - [ ] 至少包含所有原版NPC数据（33个）
  - [ ] 至少包含所有原版物品数据
  - [ ] 查询接口性能：<1ms单次查询
  - [ ] 内存占用：<10MB

  **QA Scenarios**:
  ```
  Scenario: 查询NPC喜好
    Tool: Bash (dotnet test)
    Preconditions: 知识库已加载
    Steps:
      1. 调用 knowledgeBase.GetNpcPreferences("Abigail")
      2. 验证返回Amethyst为最喜欢
    Expected Result: 返回正确的礼物偏好列表
    Evidence: .sisyphus/evidence/task-4-rag-query-test.log
  ```

  **Commit**: YES (groups with Wave 1)

- [x] 5. **基础状态机框架**

  **What to do**:
  - 创建 `IAgentState` 接口，定义状态契约：
    - `Entry()`, `Exit()`, `Update(ticks)`, `CanTransitionTo(state)`
  - 创建 `AgentStateMachine` 类，管理状态转换
  - 定义基础状态枚举：
    - `IDLE` - 空闲
    - `FOLLOW` - 跟随玩家
    - `FIGHT` - 战斗
    - `FARM` - 农场工作
    - `FORAGE` - 采集
    - `MINE` - 采矿
    - `TALK` - 对话中
    - `THINKING` - LLM思考中（显示表情）
  - 实现状态转换规则（某些状态间不能直接转换）
  - 状态持续时间跟踪（防止无限停留）
  - 与NPC Adventures的CompanionStateMachine类似但完全重写

  **Must NOT do**:
  - 不要硬编码特定NPC的行为逻辑（状态机是通用的）
  - 不要在Update中调用LLM（必须异步）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 状态机是核心架构，需要正确设计
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 2,3,4)
  - **Parallel Group**: Wave 1
  - **Blocks**: Tasks 6, 12-17
  - **Blocked By**: Task 1

  **References**:
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/StateMachine/CompanionStateMachine.cs` - 参考设计模式
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/AI/AI_StateMachine.cs` - 参考AI状态定义

  **Acceptance Criteria**:
  - [ ] 所有状态接口定义完成
  - [ ] 状态转换规则正确（非法转换被拒绝）
  - [ ] 单元测试覆盖所有状态转换路径
  - [ ] THINKING状态有最大超时（30秒）

  **QA Scenarios**:
  ```
  Scenario: 状态转换序列
    Tool: Bash (dotnet test)
    Preconditions: 新建StateMachine实例
    Steps:
      1. 从IDLE转换到FOLLOW
      2. 从FOLLOW转换到FIGHT
      3. 尝试从FIGHT直接转换到FARM（应失败）
    Expected Result: 前两次成功，第三次抛出InvalidTransitionException
    Evidence: .sisyphus/evidence/task-5-state-machine-test.log
  ```

  **Commit**: YES (groups with Wave 1)

- [x] 6. **Agent分配管理器**

  **What to do**:
  - 创建 `AgentAllocationManager` 类，管理哪些NPC获得Agent智能
  - 分配算法：
    - 基础配额：1个（玩家可配置任意NPC）
    - 动态加成：基于最近7天的对话次数、送礼次数、好感度hearts
    - 重新评估时机：每天开始时、玩家手动调整时
    - 降级策略：超出上限时，保留最活跃的Agent，其他降级为模板对话
  - 持久化Agent分配列表到存档
  - 提供API：GetAgentNpcs(), SetAgentNpc(npcName, enabled), ReevaluateAllocation()
  - 与现有Content Patcher数据兼容（非Agent NPC使用原有864+ prompts）

  **Must NOT do**:
  - 不要在游戏进行中频繁重新分配（只在每天开始时）
  - 不要强制分配特定NPC（玩家有选择权）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要设计分配算法和持久化逻辑
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 7-11)
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 18
  - **Blocked By**: Tasks 3, 5

  **Acceptance Criteria**:
  - [ ] 分配算法单元测试通过
  - [ ] 手动设置/取消Agent功能正常
  - [ ] 超出上限时正确降级
  - [ ] 存档持久化正确

  **QA Scenarios**:
  ```
  Scenario: Agent分配和降级
    Tool: Bash (dotnet test)
    Preconditions: 配置MaxAgentNpcs=2，已有2个Agent
    Steps:
      1. 尝试添加第3个Agent
      2. 验证最少互动的NPC被降级
    Expected Result: 第3个添加成功，最旧的Agent被降级
    Evidence: .sisyphus/evidence/task-6-allocation-test.log
  ```

  **Commit**: YES (groups with Wave 2)

- [x] 7. **AI决策引擎（LLM异步调用）**

  **What to do**:
  - 创建 `AIDecisionEngine` 类，核心LLM决策逻辑
  - 上下文构建器：
    - 系统提示：NPC人格、能力限制、行为规则
    - 游戏状态：时间、天气、地点、玩家状态
    - 关系数据：好感度、最近20条对话/事件记忆
    - RAG检索：相关wiki数据（当前地点、目标物品）
    - 其他Agent状态（如果同地点）
  - 决策触发器：
    - 定时：空闲时每5分钟游戏时间
    - 事件：进入新地点、战斗开始/结束、玩家送礼、玩家对话、受到攻击
  - LLM输出解析：JSON格式验证，映射到状态机动作
  - 异步执行：Task.Run启动LLM调用，回调更新状态机
  - "Thinking"状态：LLM调用期间NPC显示思考表情
  - 决策缓存：相同情境缓存结果（5分钟有效期）
  - 序列化：同时只允许1个LLM调用（全局锁）

  **Must NOT do**:
  - 不要同步调用LLM（必须async，不阻塞游戏线程）
  - 不要为每个tick调用LLM（使用触发器+缓存）
  - 不要在LLM提示中包含敏感信息（如API key）

  **Recommended Agent Profile**:
  - **Category**: `ultrabrain`
    - Reason: 这是最复杂的组件，需要正确处理异步、上下文构建、缓存、错误处理
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 6,8-11)
  - **Parallel Group**: Wave 2
  - **Blocks**: Tasks 12-17
  - **Blocked By**: Tasks 2, 3, 4

  **References**:
  - **Existing**: `ValleyTalk Base/Prompts.json` - 参考现有prompt模板
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/AI/AI_StateMachine.cs` - 参考决策触发模式
  - **External**: OpenAI Chat Completions API格式

  **Acceptance Criteria**:
  - [ ] LLM调用完全异步（游戏线程不阻塞）
  - [ ] 决策缓存命中时直接返回（不调用LLM）
  - [ ] 上下文构建完整（人格+游戏状态+记忆+RAG）
  - [ ] JSON解析失败时降级到IDLE状态
  - [ ] 全局序列化锁正确工作

  **QA Scenarios**:
  ```
  Scenario: 异步决策不阻塞游戏
    Tool: Bash (dotnet test)
    Preconditions: mock LLM延迟5秒
    Steps:
      1. 触发决策
      2. 立即检查游戏线程状态（应继续运行）
      3. 等待5秒后验证回调执行
    Expected Result: 决策启动后游戏线程不被阻塞，回调正确执行
    Evidence: .sisyphus/evidence/task-7-async-test.log

  Scenario: 决策缓存生效
    Tool: Bash (dotnet test)
    Preconditions: 相同情境连续触发2次
    Steps:
      1. 第一次触发 → 调用LLM
      2. 第二次触发（5分钟内） → 使用缓存
    Expected Result: 第二次不调用LLM，返回缓存结果
    Evidence: .sisyphus/evidence/task-7-cache-test.log
  ```

  **Commit**: YES (groups with Wave 2)

- [x] 8. **动作验证层（IActionValidator）**

  **What to do**:
  - 创建 `IActionValidator` 接口和 `ActionValidator` 实现
  - 验证规则：
    - 动作白名单：只允许预定义动作（follow/fight/farm/forage/mine/idle/talk/give_gift）
    - 目标验证：目标必须在当前location存在（或玩家）
    - 距离验证：目标必须在合理距离内（禁止瞬移）
    - 频率验证：同一动作有最小间隔（如give_gift每天最多1次）
    - 状态验证：当前状态允许转换到目标状态
    - 能力验证：NPC是否有对应技能（如warrior才能有效战斗）
  - 验证失败时：记录原因，拒绝执行，返回IDLE状态
  - 可扩展：新动作通过注册方式加入白名单

  **Must NOT do**:
  - 不要允许LLM直接修改游戏状态（所有动作必须经过验证）
  - 不要允许危险动作（如攻击友好NPC、删除物品）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 安全关键组件，需要全面考虑攻击面
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 6,7,9-11)
  - **Parallel Group**: Wave 2
  - **Blocks**: Tasks 12-17
  - **Blocked By**: Task 5

  **Acceptance Criteria**:
  - [ ] 所有非法动作被正确拒绝
  - [ ] 验证失败有明确日志
  - [ ] 单元测试覆盖所有验证规则
  - [ ] 性能：验证 < 0.1ms

  **QA Scenarios**:
  ```
  Scenario: 非法动作被拒绝
    Tool: Bash (dotnet test)
    Preconditions: NPC在农场
    Steps:
      1. LLM返回 action="attack", target="Pierre"
      2. 验证器检查目标是否为友好NPC
      3. 验证器拒绝执行
    Expected Result: 动作被拒绝，NPC保持IDLE
    Evidence: .sisyphus/evidence/task-8-validation-test.log
  ```

  **Commit**: YES (groups with Wave 2)

- [x] 9. **好感度系统**

  **What to do**:
  - 创建 `FriendshipSystem` 类，管理AI评估的好感度变化
  - 评估流程：
    - 玩家与Agent NPC对话结束后，LLM评估对话内容
    - LLM输出好感度变化值（-20到+30范围）
    - 系统验证变化值在允许范围内（软上限）
    - 应用变化到游戏内friendshipData
  - 上下文提供给LLM：
    - 当前好感度等级、最近对话内容、NPC人格、关系历史
    - 玩家行为（送礼、帮助、冒犯性语言检测）
  - 防刷机制：每天同类型互动有递减收益
  - 特殊事件：生日、节日、结婚/离婚处理
  - 持久化：好感度变化历史保存到存档

  **Must NOT do**:
  - 不要让LLM直接设置好感度（只能建议变化值）
  - 不要允许单次变化超过±30
  - 不要忽略游戏原有好感度上限（10 hearts）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要正确集成SMAPI Friendship API
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 6-8,10,11)
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 19
  - **Blocked By**: Task 4

  **References**:
  - **Existing**: SMAPI API - `Farmer.friendshipData`, `Friendship` class
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/StateMachine/State/RecruitedState.cs` - 参考UpdateFriendship

  **Acceptance Criteria**:
  - [ ] LLM评估好感度变化在允许范围内
  - [ ] 变化正确应用到游戏数据
  - [ ] 防刷机制生效
  - [ ] 特殊事件（生日等）正确处理

  **QA Scenarios**:
  ```
  Scenario: 好感度变化应用
    Tool: Bash (dotnet test)
    Preconditions: 模拟玩家与Abigail对话
    Steps:
      1. 当前好感度=500
      2. LLM评估变化=+20
      3. 应用变化
    Expected Result: 新好感度=520
    Evidence: .sisyphus/evidence/task-9-friendship-test.log
  ```

  **Commit**: YES (groups with Wave 2)

- [x] 10. **礼物系统（4层安全）**

  **What to do**:
  - 创建 `GiftSystem` 类，管理NPC→Player和Player→NPC的礼物交互
  - **NPC→Player礼物**（AI主动送礼）：
    - 触发条件：好感度达到一定阈值、特定节日、随机（低概率）
    - 礼物选择：从RAG知识库查询NPC会送的礼物（基于性格/季节/关系）
    - 安全检查：礼物必须在游戏内存在、玩家背包有空间
    - 动画：NPC走向玩家、播放给予动画、显示对话
  - **Player→NPC礼物评估**（AI反应）：
    - 触发：玩家赠送物品后
    - LLM评估：基于礼物适宜性、NPC人格、关系状态生成反应
    - 好感度变化：结合RAG数据（喜欢+80，讨厌-40等）+ LLM微调
  - **4层安全**：
    1. 受限礼物池（预定义每个NPC可能送的礼物）
    2. RAG验证（查询该礼物是否适合当前情境）
    3. 运行时检查（礼物ID存在、可获取）
    4. 频率限制（每个NPC每周最多1次送礼）

  **Must NOT do**:
  - 不要允许送不可能的物品（如传说武器、任务物品）
  - 不要允许一天内多次送礼
  - 不要让LLM决定具体物品ID（必须从受限池选择）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要正确集成游戏内物品系统和动画
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 6-9,11)
  - **Parallel Group**: Wave 2
  - **Blocks**: Task 16
  - **Blocked By**: Tasks 4, 8

  **References**:
  - **Existing**: SMAPI API - `NPC.getGiftTaste`, `Farmer.addItemToInventory`
  - **Existing**: `ValleyTalk/[CP] ValleyTalk Base/assets/bio/` - NPC喜好数据

  **Acceptance Criteria**:
  - [ ] NPC主动送礼动画正确
  - [ ] 礼物选择符合NPC性格
  - [ ] 频率限制生效
  - [ ] Player→NPC礼物评估正确

  **QA Scenarios**:
  ```
  Scenario: NPC送礼安全检查
    Tool: Bash (dotnet test)
    Preconditions: 模拟Abigail尝试送礼
    Steps:
      1. Abigail决定送礼
      2. 从受限池选择（如Amethyst）
      3. 验证RAG确认适合
      4. 检查本周未送过
    Expected Result: 通过所有检查，允许送礼
    Evidence: .sisyphus/evidence/task-10-gift-test.log
  ```

  **Commit**: YES (groups with Wave 2)

- [x] 11. **Circuit Breaker和降级系统**

  **What to do**:
  - 创建 `CircuitBreaker` 类，监控LLM调用健康状态
  - 状态：CLOSED（正常）→ OPEN（失败过多）→ HALF_OPEN（试探）
  - 触发条件：
    - 连续3次LLM调用失败（超时/错误/无效JSON）
    - 平均响应时间超过10秒（持续1分钟）
  - 降级策略（OPEN状态时）：
    - 使用Content Patcher模板对话（原有864+ prompts）
    - 使用预定义行为规则（NPC Adventures风格）
    - 禁用Agent功能，NPC恢复为普通村民
  - 自动恢复：HALF_OPEN状态下尝试1次LLM调用，成功则CLOSED
  - 手动重置：玩家可通过命令强制重置

  **Must NOT do**:
  - 不要无限重试失败的LLM调用
  - 不要在降级状态下仍尝试LLM调用
  - 不要让玩家完全无法与NPC交互（至少保留模板对话）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 可靠性工程，需要正确处理故障场景
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 6-10)
  - **Parallel Group**: Wave 2
  - **Blocks**: Tasks 7, 16
  - **Blocked By**: Task 2

  **Acceptance Criteria**:
  - [ ] 连续失败3次后触发OPEN
  - [ ] OPEN状态时降级到模板对话
  - [ ] HALF_OPEN试探成功恢复CLOSED
  - [ ] 手动重置命令有效

  **QA Scenarios**:
  ```
  Scenario: Circuit Breaker触发和恢复
    Tool: Bash (dotnet test)
    Preconditions: mock LLM连续失败
    Steps:
      1. 连续3次调用失败
      2. 验证状态变为OPEN
      3. 验证使用降级对话
      4. 恢复mock成功，验证HALF_OPEN → CLOSED
    Expected Result: 状态转换正确，降级生效
    Evidence: .sisyphus/evidence/task-11-circuit-breaker-test.log
  ```

  **Commit**: YES (groups with Wave 2)

- [x] 12. **跟随控制器（FollowController）**

  **What to do**:
  - 创建 `FollowController` 类，实现 `IAgentController` 接口
  - 功能：
    - NPC跟随玩家移动（跨场景传送）
    - 路径查找：使用SMAPI的PathFinder或自定义A*
    - 距离控制：过远时加速奔跑，接近时减速停止
    - 障碍物处理：绕开障碍物，卡住时重置路径
    - 动画：行走/奔跑动画，面向玩家方向
  - 参考NPC Adventures的FollowController但重写：
    - 适配.NET 6+和SMAPI 4.x API
    - 移除PurrplingCore依赖（自己实现PathFinder）
    - 添加LLM控制参数（跟随距离、速度偏好）
  - 事件集成：
    - `Player.Warped` → NPC传送到同一场景
    - `GameLoop.UpdateTicked` → 更新跟随逻辑

  **Must NOT do**:
  - 不要引用NPC Adventures DLL
  - 不要让NPC瞬移（必须走路径）
  - 不要在NPC卡死时崩溃（优雅降级到IDLE）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 路径查找和移动控制是复杂游戏逻辑
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 13-17)
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 18
  - **Blocked By**: Tasks 5, 7, 8

  **References**:
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/AI/Controller/FollowController.cs` - 参考算法（阈值、减速、路径重计算）
  - **Existing**: SMAPI API - `NPC.controller`, `PathFindController`

  **Acceptance Criteria**:
  - [ ] NPC跟随玩家跨场景
  - [ ] 路径查找正确（不撞墙）
  - [ ] 距离控制平滑（不抖动）
  - [ ] 卡住时自动恢复

  **QA Scenarios**:
  ```
  Scenario: NPC跟随跨场景
    Tool: Agent手动验证（SMAPI日志）
    Preconditions: 玩家在农场，NPC跟随中
    Steps:
      1. 玩家进入矿井
      2. 检查NPC是否传送到矿井入口
      3. 玩家移动，检查NPC跟随
    Expected Result: NPC正确传送并继续跟随
    Evidence: .sisyphus/evidence/task-12-follow-qa.log
  ```

  **Commit**: YES (groups with Wave 3)

- [x] 13. **战斗控制器（FightController）**

  **What to do**:
  - 创建 `FightController` 类，实现 `IAgentController` 接口
  - 功能：
    - 检测附近怪物（使用游戏内monster列表）
    - 选择目标：最近威胁、攻击玩家的怪物优先
    - 攻击动画：自定义swing动画（参考NPC Adventures）
    - 伤害计算：调用游戏内伤害API
    - 安全距离：低血量时后退，玩家逃跑时跟随
    - 武器支持：使用NPC预设武器或空手
  - 状态转换：
    - 进入FIGHT状态时激活
    - 无怪物时自动回到FOLLOW或IDLE
    - 玩家离开场景时退出战斗
  - 参考NPC Adventures的FightController但重写：
    - 移除PurrplingCore依赖
    - 适配Harmony 2.x patching
    - 简化动画系统（或使用游戏默认）

  **Must NOT do**:
  - 不要让NPC攻击友好生物（村民、动物）
  - 不要让NPC在0血量时继续战斗（应晕倒）
  - 不要无限追击（最大追击距离限制）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 战斗逻辑涉及游戏内伤害系统和动画
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 12,14-17)
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 18
  - **Blocked By**: Tasks 5, 7, 8

  **References**:
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/AI/Controller/FightController.cs` - 参考（怪物检测、伤害计算、动画）
  - **Existing**: SMAPI API - `GameLocation.damageMonster`, `Monster` class

  **Acceptance Criteria**:
  - [ ] 正确检测并攻击怪物
  - [ ] 伤害正确计算
  - [ ] 低血量时正确撤退
  - [ ] 玩家离开时退出战斗

  **QA Scenarios**:
  ```
  Scenario: NPC战斗行为
    Tool: Agent手动验证
    Preconditions: 玩家和NPC在矿井，有怪物
    Steps:
      1. 触发战斗状态
      2. 观察NPC攻击怪物
      3. 验证伤害数字出现
    Expected Result: NPC攻击怪物，不攻击玩家
    Evidence: .sisyphus/evidence/task-13-fight-qa.log
  ```

  **Commit**: YES (groups with Wave 3)

- [x] 14. **农场控制器（FarmController）**

  **What to do**:
  - 创建 `FarmController` 类，实现 `IAgentController` 接口
  - 功能：
    - 浇水作物：检测干涸作物，使用喷壶动画
    - 收获作物：检测成熟作物，收获到NPC背包
    - 范围限制：只在农场/温室工作，不在玩家视线内时不工作（避免突兀）
    - 工作时间：早上6点到晚上7点
    - 优先级：先浇水，再收获，再闲逛
  - 与游戏系统集成：
    - 使用 `HoeDirt` 状态检测是否需要浇水
    - 使用 `Crop` 状态检测是否成熟
    - 使用NPC背包（模拟Chest）存储收获物
  - LLM控制：决定今天是否工作、工作多长时间

  **Must NOT do**:
  - 不要收获玩家未允许的区域（尊重玩家私有空间）
  - 不要在雨天浇水（浪费）
  - 不要收获珍贵作物（如古代水果）除非玩家允许

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要正确集成农场地块系统
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 12,13,15-17)
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 18
  - **Blocked By**: Tasks 5, 7, 8

  **References**:
  - **Existing**: SMAPI API - `HoeDirt`, `Crop`, `Farm.terrainFeatures`

  **Acceptance Criteria**:
  - [ ] 正确检测需要浇水的作物
  - [ ] 浇水动画正确播放
  - [ ] 收获物进入NPC背包
  - [ ] 不在雨天浇水

  **QA Scenarios**:
  ```
  Scenario: NPC浇水作物
    Tool: Agent手动验证
    Preconditions: 农场有干涸作物
    Steps:
      1. NPC进入FARM状态
      2. 观察NPC走向作物
      3. 验证作物被浇水（土壤变深色）
    Expected Result: 作物正确浇水
    Evidence: .sisyphus/evidence/task-14-farm-qa.log
  ```

  **Commit**: YES (groups with Wave 3)

- [x] 15. **采集控制器（ForageController）**

  **What to do**:
  - 创建 `ForageController` 类，实现 `IAgentController` 接口
  - 功能：
    - 检测可采集物品（地上闪烁的物品、浆果丛、蘑菇树）
    - 路径查找：移动到最近可采集物品
    - 采集动画：弯腰拾取动画
    - 物品存储：采集物进入NPC背包
    - 范围限制：只在当前场景采集，不跨场景
    - 季节限制：只采集当季物品
  - 与游戏系统集成：
    - 遍历 `GameLocation.objects` 查找forageable
    - 检查 `obj.IsSpawnedObject` 或特定ID
  - LLM控制：决定采集时长、偏好类型

  **Must NOT do**:
  - 不要采集玩家种植/放置的物品
  - 不要采集任务物品
  - 不要采集博物馆捐献品

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要正确识别可采集物品
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 12-14,16,17)
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 18
  - **Blocked By**: Tasks 5, 7, 8

  **References**:
  - **Existing**: SMAPI API - `GameLocation.objects`, `Object.IsSpawnedObject`

  **Acceptance Criteria**:
  - [ ] 正确检测可采集物品
  - [ ] 采集动画正确
  - [ ] 不采集玩家物品

  **QA Scenarios**:
  ```
  Scenario: NPC采集物品
    Tool: Agent手动验证
    Preconditions: 森林有可采集物品
    Steps:
      1. NPC进入FORAGE状态
      2. 观察NPC走向物品
      3. 验证物品被拾取
    Expected Result: 物品进入NPC背包
    Evidence: .sisyphus/evidence/task-15-forage-qa.log
  ```

  **Commit**: YES (groups with Wave 3)

- [x] 16. **对话系统（AI生成 + CP降级）**

  **What to do**:
  - 创建 `AIDialogueSystem` 类，管理AI生成的对话
  - 功能：
    - 拦截NPC对话请求（Harmony patch `NPC.checkAction`）
    - 构建对话上下文：玩家输入 + NPC人格 + 关系 + 当前情境
    - LLM生成对话回复（异步，显示"thinking"）
    - 注入到游戏对话系统（`NPC.CurrentDialogue.Push`）
    - 对话历史：保存最近20条对话到存档
  - 降级策略：
    - LLM失败时 → 使用Content Patcher模板对话
    - 非Agent NPC → 使用原有864+ prompts
    - Circuit Breaker OPEN时 → 使用模板对话
  - 多语言：支持英文和中文（检测游戏语言）
  - 记忆引用：NPC能引用之前的对话（从历史中检索）

  **Must NOT do**:
  - 不要阻塞对话UI（LLM调用异步）
  - 不要生成不适当内容（过滤层）
  - 不要破坏原有CP对话（非Agent NPC不受影响）

  **Recommended Agent Profile**:
  - **Category**: `ultrabrain`
    - Reason: 对话是核心体验，需要正确处理异步、上下文、降级
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 12-15,17)
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 18
  - **Blocked By**: Tasks 5, 7, 8, 10

  **References**:
  - **Existing**: `ValleyTalk Base/Prompts.json` - 864+ prompt keys
  - **Existing**: SMAPI API - `NPC.CurrentDialogue`, `Dialogue` class
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/Dialogues/` - 参考对话注入方式

  **Acceptance Criteria**:
  - [ ] Agent NPC对话由LLM生成
  - [ ] 对话包含情境感知（地点、时间、关系）
  - [ ] LLM失败时降级到模板对话
  - [ ] 非Agent NPC不受影响

  **QA Scenarios**:
  ```
  Scenario: AI对话生成
    Tool: Agent手动验证
    Preconditions: Agent NPC激活，玩家点击对话
    Steps:
      1. 玩家点击NPC
      2. 观察"thinking"表情
      3. 验证对话内容相关且合理
    Expected Result: 生成情境相关的对话
    Evidence: .sisyphus/evidence/task-16-dialogue-qa.log
  ```

  **Commit**: YES (groups with Wave 3)

- [x] 17. **空闲/思考状态**

  **What to do**:
  - 创建 `IdleController` 类，实现 `IAgentController` 接口
  - 功能：
    - 随机空闲行为：四处走动、看风景、与其他NPC互动
    - 思考状态：LLM调用期间显示思考动画（emote 26或自定义）
    - 定时决策：每5分钟游戏时间触发LLM重新评估
    - 情境感知：根据地点选择合适空闲行为（图书馆看书、海滩望海）
  - 与现有Schedule系统兼容：
    - Agent NPC在IDLE时遵循修改后的schedule
    - 被招募时完全接管schedule（忽略原schedule）
  - 动画：emote表情、原地转身、短距离漫步

  **Must NOT do**:
  - 不要让NPC在空闲时卡住
  - 不要让NPC在危险区域空闲（矿井深层）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 相对简单，基于随机和情境
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 12-16)
  - **Parallel Group**: Wave 3
  - **Blocks**: Task 18
  - **Blocked By**: Tasks 5, 7, 8

  **References**:
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/AI/Controller/IdleController.cs` - 参考空闲行为

  **Acceptance Criteria**:
  - [ ] NPC空闲时自然移动
  - [ ] 思考状态有视觉反馈
  - [ ] 定时决策触发正确

  **QA Scenarios**:
  ```
  Scenario: NPC空闲行为
    Tool: Agent手动验证
    Preconditions: NPC在IDLE状态
    Steps:
      1. 观察NPC 1分钟
      2. 验证NPC有移动或表情
    Expected Result: NPC不静止不动
    Evidence: .sisyphus/evidence/task-17-idle-qa.log
  ```

  **Commit**: YES (groups with Wave 3)

- [x] 18. **SMAPI事件集成**

  **What to do**:
  - 在 `ModEntry.cs` 中注册所有需要的SMAPI事件：
    - `GameLoop.GameLaunched` → 初始化系统（LLM Provider、Agent分配、RAG）
    - `GameLoop.SaveLoaded` → 加载存档数据（Agent状态、记忆、好感度历史）
    - `GameLoop.Saving` → 保存Agent数据到存档
    - `GameLoop.DayStarted` → 每日重置（Agent重新评估、schedule更新）
    - `GameLoop.DayEnding` → 日终处理（保存当日统计、清理缓存）
    - `GameLoop.UpdateTicked` → 更新Agent状态机（每tick检查是否需要决策）
    - `GameLoop.TimeChanged` → 时间变化时触发特定行为（如定时决策）
    - `Player.Warped` → 玩家传送时通知跟随的Agent
    - `Input.ButtonPressed` → 检测玩家按键（如调试命令）
  - 事件处理注意事项：
    - 所有事件处理必须轻量（ heavy work放到异步任务）
    - 保存/加载时处理版本迁移
    - 多人游戏检测（不支持的警告）

  **Must NOT do**:
  - 不要在事件处理中调用LLM（必须异步）
  - 不要在保存时做耗时的序列化（预处理数据）
  - 不要在UpdateTicked中每帧做大量计算（使用冷却）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: SMAPI事件是模组生命线，需要正确处理
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 19-22)
  - **Parallel Group**: Wave 4
  - **Blocks**: Tasks 19, 20
  - **Blocked By**: Tasks 6, 12-17

  **References**:
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/NpcAdventureMod.cs` - 参考事件注册模式
  - **External**: SMAPI事件文档 (https://stardewvalleywiki.com/Modding:Modder_Guide/APIs/Events)

  **Acceptance Criteria**:
  - [ ] 所有事件正确注册和触发
  - [ ] SaveLoaded正确恢复Agent状态
  - [ ] Saving正确持久化数据
  - [ ] DayStarted触发Agent重新评估

  **QA Scenarios**:
  ```
  Scenario: 存档加载和保存
    Tool: Agent手动验证
    Preconditions: 有Agent激活的存档
    Steps:
      1. 加载存档
      2. 验证Agent状态恢复（位置、行为、好感度）
      3. 玩游戏5分钟
      4. 保存并重新加载
      5. 验证状态一致
    Expected Result: 状态正确持久化
    Evidence: .sisyphus/evidence/task-18-save-load-qa.log
  ```

  **Commit**: YES (groups with Wave 4)

- [x] 19. **存档持久化**

  **What to do**:
  - 创建 `SaveDataManager` 类，管理所有存档数据
  - 数据结构：
    - `AgentStateData`: 每个Agent的当前状态、位置、目标
    - `MemoryData`: 对话历史（最近20条）、事件记录
    - `FriendshipHistory`: 好感度变化日志（用于LLM上下文）
    - `AllocationData`: Agent分配列表和配置
    - `StatisticsData`: 会话统计（token使用、LLM调用次数）
  - 使用SMAPI的 `helper.Data.WriteSaveData` / `ReadSaveData`
  - 版本控制：保存数据包含版本号，加载时进行迁移
  - 迁移策略：
    - v1.0.2 → v2.0.0: 无Agent数据，默认初始化
    - v2.0.0 → v2.x: 字段缺失时填充默认值
  - 数据压缩：大记忆数据可考虑压缩（但先不实现）

  **Must NOT do**:
  - 不要保存敏感信息（API key、个人数据）
  - 不要在存档中保存临时状态（如THINKING）
  - 不要让旧存档崩溃（优雅处理缺失字段）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 数据持久化是可靠性关键
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 18,20-22)
  - **Parallel Group**: Wave 4
  - **Blocks**: Task F1
  - **Blocked By**: Tasks 9, 18

  **References**:
  - **Existing**: SMAPI API - `helper.Data.WriteSaveData`, `ReadSaveData`

  **Acceptance Criteria**:
  - [ ] 数据正确保存和加载
  - [ ] 版本迁移工作正常
  - [ ] 旧存档能正常加载（默认初始化）
  - [ ] 数据完整性验证

  **QA Scenarios**:
  ```
  Scenario: 版本迁移
    Tool: Bash (dotnet test)
    Preconditions: 模拟v1.0.2存档数据
    Steps:
      1. 加载旧数据
      2. 验证新字段有默认值
      3. 保存并重新加载
    Expected Result: 无异常，数据完整
    Evidence: .sisyphus/evidence/task-19-migration-test.log
  ```

  **Commit**: YES (groups with Wave 4)

- [x] 20. **HUD/UI显示**

  **What to do**:
  - 创建 `AgentHUD` 类，显示Agent NPC状态
  - UI元素：
    - Agent指示器：显示当前Agent NPC名称和状态（FOLLOW/FIGHT等）
    - 思考指示器：LLM调用时显示旋转图标
    - 好感度快速查看：鼠标悬停时显示 hearts
    - Token使用统计：调试模式显示会话token数
  - 使用SMAPI的 `Display.RenderedHud` 事件绘制
  - 使用游戏内字体和颜色风格（保持一致性）
  - 配置选项：玩家可调整HUD位置、大小、透明度
  - 与GMCM集成：HUD设置页面

  **Must NOT do**:
  - 不要遮挡重要游戏UI
  - 不要在高强度战斗时显示复杂UI（简化模式）
  - 不要使用非游戏内字体

  **Recommended Agent Profile**:
  - **Category**: `visual-engineering`
    - Reason: UI/UX设计需要视觉敏感性
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 18,19,21,22)
  - **Parallel Group**: Wave 4
  - **Blocks**: Task F1
  - **Blocked By**: Task 18

  **References**:
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/HUD/CompanionDisplay.cs` - 参考HUD绘制
  - **Existing**: SMAPI API - `Display.RenderedHud`, `SpriteBatch`

  **Acceptance Criteria**:
  - [ ] HUD正确显示Agent状态
  - [ ] 思考指示器在LLM调用时显示
  - [ ] HUD位置可配置
  - [ ] 不遮挡游戏UI

  **QA Scenarios**:
  ```
  Scenario: HUD显示
    Tool: Agent手动验证
    Preconditions: 有Agent激活
    Steps:
      1. 观察屏幕角落
      2. 验证显示Agent名称和状态
      3. 触发LLM决策，验证思考图标
    Expected Result: HUD信息正确且清晰可见
    Evidence: .sisyphus/evidence/task-20-hud-qa.log
  ```

  **Commit**: YES (groups with Wave 4)

- [x] 21. **调试和日志系统**

  **What to do**:
  - 创建 `DebugLogger` 类，统一日志管理
  - 日志级别：Verbose（LLM调用详情）、Info（状态变化）、Warn（降级）、Error（异常）
  - 调试命令（SMAPI控制台）：
    - `valleytalk_status` → 显示所有Agent状态
    - `valleytalk_force_decision [npc]` → 强制触发决策
    - `valleytalk_reset_circuit` → 重置Circuit Breaker
    - `valleytalk_token_usage` → 显示token统计
  - 日志内容：
    - LLM调用：prompt长度、响应时间、token使用
    - 决策结果：旧状态→新状态、原因
    - 错误：异常堆栈、降级原因
  - 调试模式：详细记录LLM prompt和response（可配置）

  **Must NOT do**:
  - 不要在发布版中记录敏感信息（如完整prompt中的玩家数据）
  - 不要频繁日志（避免性能影响）

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 标准日志系统
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 18-20,22)
  - **Parallel Group**: Wave 4
  - **Blocks**: Task F1
  - **Blocked By**: Task 18

  **References**:
  - **Existing**: SMAPI API - `IMonitor.Log`, `LogLevel`

  **Acceptance Criteria**:
  - [ ] 日志级别正确过滤
  - [ ] 调试命令可用
  - [ ] 日志包含关键信息（决策、错误）

  **QA Scenarios**:
  ```
  Scenario: 调试命令
    Tool: Agent手动验证
    Preconditions: 游戏运行中
    Steps:
      1. 在SMAPI控制台输入 `valleytalk_status`
      2. 验证显示Agent列表和状态
    Expected Result: 命令执行，输出正确
    Evidence: .sisyphus/evidence/task-21-debug-qa.log
  ```

  **Commit**: YES (groups with Wave 4)

- [x] 22. **错误处理和边界情况**

  **What to do**:
  - 全局异常处理：捕获未处理异常，记录日志，降级到安全状态
  - 边界情况处理：
    - 玩家睡觉时NPC正在行动 → 取消当前行动，保存状态
    - 节日当天 → Agent NPC遵循节日schedule，暂停自主行动
    - NPC在矿井晕倒 → 传送回农场，状态设为UNAVAILABLE（当天）
    - LLM返回无效JSON → 记录错误，使用上次有效决策或IDLE
    - LLM返回越界动作 → 验证层拒绝，记录警告
    - 多个Agent同地点 → 优先级系统（高好感度优先）
    - Agent NPC被离婚 → 移除Agent状态，降级为普通NPC
    - 存档损坏 → 尝试修复，失败则重置Agent数据
  - 防御性编程：所有外部输入（LLM输出、存档数据、配置）验证

  **Must NOT do**:
  - 不要让异常导致游戏崩溃
  - 不要静默吞掉错误（至少记录）
  - 不要在边界情况时卡住（必须有退出路径）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要全面考虑边界情况
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 18-21)
  - **Parallel Group**: Wave 4
  - **Blocks**: Task F1
  - **Blocked By**: Tasks 8, 11

  **References**:
  - **Existing**: `NpcAdventures-master/NpcAdventures-master/src/StateMachine/State/RecruitedState.cs` - 参考边界处理

  **Acceptance Criteria**:
  - [ ] 所有边界情况有处理逻辑
  - [ ] 异常不导致游戏崩溃
  - [ ] 错误有明确日志

  **QA Scenarios**:
  ```
  Scenario: NPC晕倒处理
    Tool: Agent手动验证
    Preconditions: Agent NPC在矿井，血量0
    Steps:
      1. 让NPC受到致命伤害
      2. 验证NPC传送回农场
      3. 验证当天不可用
    Expected Result:  graceful degradation
    Evidence: .sisyphus/evidence/task-22-boundary-qa.log
  ```

  **Commit**: YES (groups with Wave 4)

- [x] 23. **SVE add-on升级**

  **What to do**:
  - 更新 `[CP] ValleyTalk for SVE` 数据包到v2.0.0
  - 添加SVE NPC的Agent支持：
    - 扩展AgentAllocationManager识别SVE NPC
    - 添加SVE NPC的bios到RAG知识库
    - 添加SVE地点到locations.json
  - 更新manifest.json：
    - 版本2.0.0
    - 依赖ValleyTalk Base 2.0.0
    - 依赖Stardew Valley Expanded
  - 保留现有SVE对话数据，添加AI增强支持
  - 测试：确保SVE NPC能正确成为Agent

  **Must NOT do**:
  - 不要修改SVE原始文件
  - 不要添加SVE没有的角色

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 主要是数据更新和配置
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 24-26)
  - **Parallel Group**: Wave 5
  - **Blocks**: Task F1
  - **Blocked By**: Tasks 1, 4

  **References**:
  - **Existing**: `[CP] ValleyTalk for SVE/manifest.json` - 现有配置
  - **Existing**: `ValleyTalk for SVE/assets/` - SVE数据

  **Acceptance Criteria**:
  - [ ] SVE NPC可被分配为Agent
  - [ ] SVE地点数据正确
  - [ ] 与SVE模组兼容

  **QA Scenarios**:
  ```
  Scenario: SVE NPC成为Agent
    Tool: Agent手动验证
    Preconditions: SVE安装
    Steps:
      1. 尝试将Sophie设为Agent
      2. 验证成功
      3. 与Sophie对话
    Expected Result: SVE NPC正常工作
    Evidence: .sisyphus/evidence/task-23-sve-qa.log
  ```

  **Commit**: YES (groups with Wave 5)

- [x] 24. **i18n支持（中文/英文）**

  **What to do**:
  - 更新i18n系统：
    - 保留现有 `i18n/default.json` 和 `i18n/zh.json`
    - 添加新功能的翻译键：
      - Agent状态名称（"FOLLOWING", "FIGHTING"等）
      - HUD文本
      - 调试命令输出
      - 错误消息
  - 语言检测：自动检测游戏语言设置
  - 回退策略：缺少翻译时回退到英文
  - 测试：验证中英文显示正确

  **Must NOT do**:
  - 不要硬编码任何用户可见字符串
  - 不要破坏现有翻译

  **Recommended Agent Profile**:
  - **Category**: `quick`
    - Reason: 主要是翻译工作
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 23,25,26)
  - **Parallel Group**: Wave 5
  - **Blocks**: Task F1
  - **Blocked By**: Task 1

  **References**:
  - **Existing**: `ValleyTalk Base/i18n/` - 现有翻译文件
  - **External**: SMAPI i18n文档

  **Acceptance Criteria**:
  - [ ] 所有用户可见字符串可翻译
  - [ ] 中英文显示正确
  - [ ] 缺少翻译时回退英文

  **QA Scenarios**:
  ```
  Scenario: 中文显示
    Tool: Agent手动验证
    Preconditions: 游戏语言设为中文
    Steps:
      1. 观察HUD文本
      2. 验证为中文
    Expected Result: 显示正确中文
    Evidence: .sisyphus/evidence/task-24-i18n-qa.log
  ```

  **Commit**: YES (groups with Wave 5)

- [x] 25. **单元测试（xUnit + Moq）**

  **What to do**:
  - 创建测试项目 `ValleyTalk.Tests.csproj`
  - 配置：`.NET 6`, `xUnit`, `Moq`, `FluentAssertions`
  - 测试覆盖：
    - **LLM Provider**: 所有Provider的mock测试（超时、重试、解析）
    - **Agent分配**: 分配算法、降级逻辑
    - **状态机**: 所有状态转换路径
    - **动作验证**: 白名单、目标验证、频率限制
    - **好感度**: 变化计算、防刷、特殊事件
    - **礼物系统**: 安全检查、频率限制
    - **RAG**: 查询正确性、性能
    - **Circuit Breaker**: 状态转换、降级
    - **配置**: 读写、迁移
  - 使用Moq模拟SMAPI API（IMonitor, IModHelper等）
  - 代码覆盖率目标：核心业务逻辑 > 80%

  **Must NOT do**:
  - 不要测试SMAPI集成（需要游戏运行）
  - 不要测试UI（超出单元测试范围）

  **Recommended Agent Profile**:
  - **Category**: `unspecified-high`
    - Reason: 需要编写全面测试
  - **Skills**: [`test-driven-development`]
    - TDD skill: 帮助设计可测试的代码和完整测试用例

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 23,24,26)
  - **Parallel Group**: Wave 5
  - **Blocks**: Task F2
  - **Blocked By**: Tasks 2, 4, 6, 9

  **References**:
  - **External**: xUnit文档, Moq文档
  - **Existing**: SMAPI测试模式（参考NUnit用法，转为xUnit）

  **Acceptance Criteria**:
  - [ ] 所有核心业务逻辑有单元测试
  - [ ] `dotnet test` 通过
  - [ ] 代码覆盖率 > 80%

  **QA Scenarios**:
  ```
  Scenario: 单元测试通过
    Tool: Bash
    Preconditions: 测试项目构建成功
    Steps:
      1. 运行 `dotnet test`
      2. 验证所有测试通过
    Expected Result: 100% tests passed
    Evidence: .sisyphus/evidence/task-25-unit-test.log
  ```

  **Commit**: YES (groups with Wave 5)

- [x] 26. **性能优化（缓存/token预算）**

  **What to do**:
  - 实现缓存层：
    - LLM响应缓存（相同prompt缓存5分钟）
    - RAG查询缓存（地点/NPC数据缓存）
    - 路径查找缓存（最近路径缓存）
  - Token预算系统：
    - 每会话跟踪token使用量
    - 接近预算时警告玩家
    - 超出预算时强制使用本地LLM
  - 性能监控：
    - 记录LLM调用p50/p95/p99延迟
    - 记录帧率影响（对比无模组）
    - 记录内存使用
  - 优化：
    - 上下文压缩：老旧记忆摘要化
    - 异步预加载：预测性加载RAG数据
    - 对象池：减少GC压力（频繁创建的小对象）

  **Must NOT do**:
  - 不要过早优化（先测量再优化）
  - 不要牺牲正确性换取性能

  **Recommended Agent Profile**:
  - **Category**: `deep`
    - Reason: 需要深度分析和优化
  - **Skills**: []

  **Parallelization**:
  - **Can Run In Parallel**: YES (with Tasks 23-25)
  - **Parallel Group**: Wave 5
  - **Blocks**: Task F3
  - **Blocked By**: Tasks 7, 12-17

  **References**:
  - **Existing**: `AIDecisionEngine` 缓存实现

  **Acceptance Criteria**:
  - [ ] 缓存命中可测量
  - [ ] Token预算生效
  - [ ] 帧率影响 < 5%

  **QA Scenarios**:
  ```
  Scenario: 性能基准
    Tool: Agent手动验证
    Preconditions: 游戏运行30分钟
    Steps:
      1. 记录平均帧率
      2. 对比无模组帧率
      3. 验证差异 < 5%
    Expected Result: 性能影响可接受
    Evidence: .sisyphus/evidence/task-26-performance-qa.log
  ```

  **Commit**: YES (groups with Wave 5)

---

## Final Verification Wave

- [x] F1. **整合测试和回归验证** — `unspecified-high`
  运行完整游戏流程：创建存档 → 加载模组 → 与Agent NPC对话 → 触发跟随 → 进入矿井战斗 → 返回农场浇水 → 存档 → 读档验证状态恢复。检查SMAPI控制台无错误。
  Output: `Integration [PASS/FAIL] | Scenarios [N/N] | Errors [0/N] | VERDICT`

- [x] F2. **代码质量审查** — `unspecified-high`
  运行 `dotnet build` + `dotnet test`。检查：无`as any`、无空catch、无console.log、无死代码。检查AI slop模式。
  Output: `Build [PASS/FAIL] | Tests [N/N] | Issues [N] | VERDICT`

- [x] F3. **性能基准测试** — `unspecified-high`
  测量：LLM调用延迟（p50/p95/p99）、内存使用、帧率影响、token使用量。对比基准：无模组时的帧率。
  Output: `Latency [Xms] | Memory [XMB] | FPS [X] | Tokens [N] | VERDICT`

- [x] F4. **文档和发布准备** — `deep`
  验证：README完整、配置文档、API文档、更新日志。检查：版本号一致、依赖清单、安装说明。
  Output: `Docs [COMPLETE/INCOMPLETE] | Version [OK] | Dependencies [OK] | VERDICT`

---

## Commit Strategy

- **Wave 1完成**: `feat: foundation - project setup, LLM provider, config, RAG, state machine`
- **Wave 2完成**: `feat: core AI - agent allocation, decision engine, validation, friendship, gifts`
- **Wave 3完成**: `feat: behaviors - follow, fight, farm, forage, dialogue controllers`
- **Wave 4完成**: `feat: integration - SMAPI events, save/load, HUD, debugging`
- **Wave 5完成**: `feat: polish - SVE, i18n, tests, performance`
- **Final**: `release: v2.0.0 - AI Agent NPC system`

---

## Success Criteria

### Verification Commands
```bash
# Build
 dotnet build

# Unit tests
dotnet test

# SMAPI console check (manual)
# Load mod, check no errors in SMAPI console
```

### Final Checklist
- [ ] All "Must Have" present
- [ ] All "Must NOT Have" absent
- [ ] All tests pass
- [ ] SMAPI console error-free
- [ ] Save/load works correctly
- [ ] 1 Agent NPC complete gameplay loop verified
- [ ] Documentation complete
- [ ] Version bumped to 2.0.0
