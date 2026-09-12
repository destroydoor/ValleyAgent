# NPC 剧情演变系统 — Tasks

> **状态（2026-07-26 标注）：实现路径已变**
>
> 本文档中的"C# → Python 请求构建"任务（Task 8 等）已废弃。v4.3 中所有
> prompt 处理在 TS Agent Server 侧完成（`packages/stardew/src/npc-prompt-loader.ts`
> + `prompt-builder.ts`），C# 端只通过 WebSocket 发送 `dialogue` 消息和
> `worldSnapshot`，不再做提示词切片。
>
> **当前架构**：[`../../AGENTS.md`](../../AGENTS.md) 第 2.1.1 节。

---

## Task 1: 实现 FriendshipPhase 枚举和计算

**文件**: 新建 `ValleyAgent/Brain/FriendshipPhase.cs`

- 定义枚举 `FriendshipPhase { Stranger, Acquaintance, Friend, Close, Partner }`
- 实现静态方法 `FromFriendship(int points): FriendshipPhase`
- 阈值: 0-499=Stranger, 500-1249=Acquaintance, 1250-1999=Friend, 2000-2499=Close, 2500+=Partner

## Task 2: 实现 Trait 阶段可见性映射

**文件**: 新建 `ValleyAgent/Brain/TraitPhaseMapper.cs`

- 定义静态字典 `_traitPhaseMin`，关键词 → 最低阶段
- 实现 `GetVisibleTraits(Dictionary<string, BioTrait> traits, FriendshipPhase phase): List<BioTrait>`
- 规则：
  - Phase 1 可见：含 `Depressed`, `SelfDestructive`, `Cynical`, `Guarded`, `Vain`, `Fashion`, `Introverted`, `Moody`, `Rebellious`, `Adventurous`, `Independent`, `Creative`
  - Phase 3+ 可见：含 `Caring`, `LovesChickens`, `Growing`, `Appreciation`, `Empathetic`, `Deep`, `Authenticity`, `Yearning`
  - Phase 4+ 可见：含 `Vulnerable`, `Grateful`, `Redemption`
  - 默认：Phase 2+ 可见

## Task 3: 实现 Preoccupations 阶段筛选

**文件**: 在 `TraitPhaseMapper.cs` 中追加

- 定义关键词分类：`_surfaceKeywords`（表层关注）和 `_deepKeywords`（深层关注）
- 实现 `GetVisiblePreoccupations(List<string> preoccupations, FriendshipPhase phase): List<string>`
- 规则：
  - Phase 1-2：只展示表层（含 `beer`, `wine`, `Jojamart`, `fashion`, `shopping` 等）
  - Phase 3+：展示全部
  - 不匹配任何关键词的 preoccupation 始终可见

## Task 4: 实现 Biography 阶段摘要

**文件**: 在 `ValleyTalkBioData.cs` 中添加方法

- 添加 `GetPhaseSummary(FriendshipPhase phase): string`
- Phase 1-2：前 2 句（现有 `PromptSummary` 逻辑）
- Phase 3-4：前 2 句 + 检测转折句（含 `However`, `Despite`, `But`, `Beneath`, `Underneath` 的句子）
- Phase 5：前 2 句 + 转折句 + `BiographyEnd`

## Task 5: 在 ValleyTalkBioData 中添加 PromptOverrides

**文件**: `ValleyAgent/RAG/Models/ValleyTalkBioData.cs`

- 添加属性 `Dictionary<string, string>? PromptOverrides { get; set; }`
- 添加方法 `GetPromptOverride(string key, FriendshipPhase phase): string?`
- key 映射：`nonSpouseFriendshipFirstConversation` → Phase 1, `nonSpouseFreindshipStrangers` → Phase 1, `nonSpouseFriendshipAcquaintances` → Phase 2, `nonSpouseFriendshipFriends` → Phase 3, `nonSpouseFriendshipCloseFriends` → Phase 4

## Task 6: 改造 AgentPromptBuilder.AppendBioSection

**文件**: `ValleyAgent/Brain/AgentPromptBuilder.cs`

- 方法签名改为 `AppendBioSection(StringBuilder sb, int friendshipPoints)`
- 内部计算 `FriendshipPhase phase = FriendshipPhase.FromFriendship(friendshipPoints)`
- Trait 注入改为 `TraitPhaseMapper.GetVisibleTraits(bio.Traits, phase)`
- Biography 注入改为 `bio.GetPhaseSummary(phase)`
- Preoccupations 注入改为 `TraitPhaseMapper.GetVisiblePreoccupations(bio.Preoccupations, phase)`
- PromptOverrides 查找并替换默认 friendship behavior 指导

## Task 7: 改造 AgentPromptBuilder.AppendGameContext — 事件里程碑

**文件**: `ValleyAgent/Brain/AgentPromptBuilder.cs`

- 在 `AppendGameContext` 中追加事件里程碑段落
- 从 `AgentInstance` 的记忆/状态中检查 `recentEvents`
- 匹配 `Prompts.json` 中的事件描述模板
- 格式：`[Recent Life Event]\n{事件描述}`

## Task 8: 更新 C# → Python 请求构建

**文件**: `ValleyAgent/Api/ValleyAgentApi.cs`

- 在构建 `DialogueRequest` 和 `DecisionRequest` 时，根据当前 friendship 阶段筛选 bio 数据
- `NpcBiography` → 使用 `bio.GetPhaseSummary(phase)` 替代 `bio.PromptSummary`
- `NpcTraits` → 使用阶段筛选后的 trait 替代 `bio.TraitNames`
- `NpcRelationships` → 保持不变（关系不随阶段变化）

## Task 9: 更新 prompts.json 对话模板

**文件**: `src/valley_agent_server/server/prompts.json`

- 在 dialogue system prompt 中追加阶段感知指导：
  ```
  当前关系阶段：{friendship_phase}
  {phase_behavior_guidance}
  ```
- 添加 `friendship_phase` 变量到 `DialogueRequest` 和 `DecisionRequest`

## Task 10: 测试验证

- 验证 Shane Phase 1 只展示 Depressed/Guarded/Cynical
- 验证 Shane Phase 3+ 展示 Caring/LovesChickens
- 验证 Haley Phase 1 展示 Fashion Conscious，Phase 3+ 展示 Growing Appreciation
- 验证 Biography 摘要随阶段变化
- 验证 PromptOverrides 对 Shane 的冷漠对话生效
- 验证 Python 服务器接收到筛选后的 bio 数据
