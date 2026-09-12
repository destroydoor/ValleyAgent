# NPC 剧情演变系统 — Spec

> **状态（2026-07-26 标注）：设计意图仍有效，但实现路径已变**
>
> 本文档设计的"好感度阶段 + 事件里程碑"提示词切片机制已经在 v4.3 TS Agent
> Server 中以 `npc-prompt-loader.ts` + `prompt-builder.ts` 实现：
>
> - **好感度阶段映射**：`packages/stardew/src/npc-prompt-loader.ts` 按 5 阶段
>   （stranger/acquaintance/friend/close/partner）选择 NPC 提示词
> - **提示词数据**：`packages/stardew/data/npc_prompts.json`（33 个 NPC）
> - **Prompt 构建**：`packages/stardew/src/prompt-builder.ts` 注入角色规则、
>   记忆、场景、tool-result 反馈
>
> 文档中"C# → Python 请求构建"等任务（基于 `src/valley_agent_server/`）已废弃，
> 新架构中所有 prompt 处理在 TS 服务器侧完成，C# 仅作为执行层。

---

## 1. 问题

当前 NPC 的性格在所有好感度阶段完全一致。Shane 无论 0 心还是 10 心都同样冷漠，Haley 永远爱慕虚荣。ValleyTalk bio 数据中已包含丰富的角色弧线描述（如 Shane 的 `SelfDestructive` → `CaringUnderneath`、Haley 的 `Yearning for Something More` → `Growing Appreciation`），但这些信息被一次性全部注入 prompt，LLM 无法区分"当前阶段应该展现哪一面"。

**核心矛盾**：bio 数据描述的是角色的**完整弧线**，而 prompt 需要的是**当前阶段的切片**。

## 2. 目标

让 NPC 的言行随好感度阶段和关键事件自然演变，仅通过提示词层实现，不改状态机/Handler 逻辑。

## 3. 设计方案：好感度阶段 + 事件里程碑 混合驱动

### 3.1 好感度阶段定义

基于原版 heart event 节点，定义 5 个阶段：

| 阶段 | 好感度范围 | 心数 | 语义 |
|------|-----------|------|------|
| Phase 1: Stranger | 0-499 | 0-1 | 初次接触，保持距离 |
| Phase 2: Acquaintance | 500-1249 | 2-4 | 开始认识，有基本了解 |
| Phase 3: Friend | 1250-1999 | 5-7 | 朋友关系，分享个人想法 |
| Phase 4: Close | 2000-2499 | 8-9 | 亲密好友/恋人，深度信任 |
| Phase 5: Partner | 2500 | 10+ | 伴侣/至交，完全敞开 |

### 3.2 事件里程碑

从 ValleyTalk Prompts.json 的 `recentEvents` 和原版事件中提取关键转折点：

| 事件 | 影响范围 | 说明 |
|------|---------|------|
| `cc_Complete` | 全镇 | 社区中心修复，镇民士气提升 |
| `cc_Bus_Repaired` | 全镇 | 公交修复，可去沙漠 |
| `recentEventsGreenRain` | 全镇 | 绿雨异象，引发不安 |
| `recentEventsMarried` | 当事NPC | 结婚，关系质变 |
| `recentEventsBabyBoy/Girl` | 当事NPC | 生育，家庭角色转变 |
| `recentEventsJojaLightning` | 全镇 | Joja 被雷劈，商业格局变化 |
| `wonEggHunt/wonGrange/wonIceFishing` | 当事NPC | 玩家赢得比赛，NPC 印象改变 |

### 3.3 核心机制：阶段感知的 Trait 筛选 + PromptOverrides 注入

#### 3.3.1 Trait 阶段可见性

为每个 Trait 添加阶段可见性标注。**不修改 bio JSON 文件**，而是在代码层根据 trait 名称/关键词自动映射阶段：

**自动映射规则**（基于 trait 名称关键词）：

| 关键词模式 | 最低阶段 | 示例 |
|-----------|---------|------|
| `Depressed`, `SelfDestructive`, `Cynical`, `Guarded` | Phase 1（始终可见） | Shane 的 Depressed |
| `Yearning`, `Vulnerable`, `Struggles` | Phase 2+ | Haley 的 Vulnerable Beneath the Surface |
| `Creative`, `Adventurous`, `Independent` | Phase 2+（始终可见但描述随阶段变化） | Abigail 的 Adventurous |
| `Caring`, `LovesChickens`, `Growing` | Phase 3+ | Shane 的 Caring Underneath |
| `Deep Conversations`, `Authenticity` | Phase 3+ | Sebastian 的 Deep Conversations |
| `Empathetic`, `Appreciation` | Phase 4+ | Haley 的 Growing Appreciation |

**实现方式**：在 `AgentPromptBuilder.AppendBioSection()` 中，根据当前好感度阶段过滤 trait 列表。Phase 1 只展示"表层/防御性"特质，Phase 3+ 才展示"深层/成长性"特质。

#### 3.3.2 PromptOverrides 注入

Shane 的 bio 已有 `PromptOverrides` 字段（如 `nonSpouseFriendshipFirstConversation`），但 C# 端 `ValleyTalkBioData` 类未实现此字段。

**需要**：
1. 在 `ValleyTalkBioData` 中添加 `PromptOverrides` 属性
2. 在 `AgentPromptBuilder` 中根据当前阶段和场景查找 override
3. override 文本替换默认的 friendship behavior 指导

#### 3.3.3 Biography 阶段摘要

当前 `PromptSummary` 取 Biography 前 2 句，不区分阶段。改为：

- Phase 1-2：取 Biography 前 2 句（表层描述）
- Phase 3-4：取前 2 句 + 关键转折句（如 "Despite her adventurous spirit, Abigail grapples with..."）
- Phase 5：取前 2 句 + 转折句 + BiographyEnd（完整弧线）

#### 3.3.4 Preoccupations 阶段筛选

当前 `Preoccupations` 完全未注入 prompt。按阶段筛选：

- Phase 1-2：展示"表层关注"（如 Shane 的 `beer`, `Jojamart`）
- Phase 3+：展示"深层关注"（如 Shane 的 `helping Marnie more`, `chickens`）

筛选规则：基于 preoccupation 内容关键词自动分类。

### 3.4 事件里程碑注入

当关键事件发生时，在 prompt 中追加事件影响描述：

```
[Recent Life Event]
社区中心修复了！整个小镇都焕发了新的活力。你感到前所未有的希望。
```

**实现**：在 `AgentPromptBuilder.AppendGameContext()` 中，检查 `recentEvents` 列表，将匹配的事件描述追加到 prompt。

### 3.5 Python 服务器端同步

当前 Python 端通过 `DialogueRequest.npc_biography` / `npc_traits` / `npc_relationships` 接收的是扁平字符串。需要：

1. C# 端在构建请求时，根据阶段筛选后再传入
2. Python 端无需修改（它只接收已筛选的文本）

## 4. 数据流

```
ValleyTalkBioData (bio JSON)
    ↓ ValleyTalkBioLoader
    ↓ brain.Bio = bio
    ↓
AgentPromptBuilder.AppendBioSection(agent)
    ↓ 读取 agent.FriendshipPoints → 计算当前 Phase
    ↓ 读取 agent.RecentEvents → 检查里程碑
    ↓
    ├─ Trait 筛选：只注入当前阶段可见的 trait
    ├─ PromptOverrides：查找当前阶段的 override
    ├─ Biography 摘要：根据阶段深度截取
    ├─ Preoccupations：根据阶段筛选
    └─ 事件里程碑：追加 recentEvent 影响
    ↓
构建最终 prompt → LLM
```

## 5. 不做的事

- 不修改状态机逻辑（IDLE/FOLLOW/FIGHT 等状态转换规则不变）
- 不修改 Handler 行为（FarmHandler/FightHandler 等执行逻辑不变）
- 不修改 bio JSON 文件（所有阶段逻辑在代码层实现）
- 不新增数据库/存档字段（阶段从现有 friendship 值实时计算）

## 6. 预期效果

| NPC | Phase 1 (0-1心) | Phase 3 (5-7心) | Phase 5 (10心) |
|-----|-----------------|-----------------|----------------|
| Shane | 酗酒、冷漠、自我毁灭 | 开始关心鸡、对 Jas 有责任感 | 戒酒、温暖、主动帮助 |
| Haley | 虚荣、嫌弃小镇 | 开始欣赏摄影和自然 | 拥抱小镇生活、真诚关心他人 |
| Sebastian | 沉默、回避 | 偶尔分享内心想法 | 敞开心扉、幽默、深情 |
| Abigail | 叛逆、冒险 | 分享家庭压力 | 独立自信、与父母和解 |
