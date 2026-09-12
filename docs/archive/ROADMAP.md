# ValleyAgent 执行路线图

> **规则**：干一个打勾一个（`- [x]`），整组完成后整组删除。  
> **最后更新**：2026-04-27

---

## 🔴 P0 — 必须完成（阻断发布）

> 这组全做完才能进入 P1。做完后整段删除。

### UI 重构：回归原版 DialogueBox
- [ ] 修改 `NPCDialoguePatch`：移除 `AgentDialogueMenu` 弹出，改用原版 `DialogueBox` 显示 AI 问候
- [ ] 对话结束后显示选项分支（`Dialogue.answerChoices`）：继续聊天 / 查看背包 / 一起行动 / 再见
- [ ] 选择"继续聊天"→打开 `AgentChatMenu`（保留，作为独立输入界面）
- [ ] 选择"查看背包"→打开原版 `InventoryMenu` 显示 NPC 物品
- [ ] 删除 `AgentDialogueMenu.cs` 文件
- [ ] 验证：点击 NPC 后看到的是原版对话风格 + 立绘切换

### 事件驱动 LLM 决策
- [ ] 实现"对话结束触发决策"：`NPCDialoguePatch` 关闭 / `AgentChatMenu` 发送消息后调用 `MakeDecisionsAsync`
- [ ] 实现"任务完成触发决策"：所有 Handler 在 `target == null` 收尾后调用 `agent.StateMachine.ForceTransition(AgentState.IDLE)` 并触发决策
- [ ] 修改 `ModEntry.OnUpdateTicked`：移除旧的 30 秒定时触发，改为事件队列驱动
- [ ] 决策队列 `_pendingDecisions` 支持事件类型标记（Timer / Dialogue / TaskComplete / Emergency）

### 决策频率调整
- [ ] `ModConfig.DecisionIntervalMinutes` 默认值从 5 改为 0.5（30 秒）
- [ ] `ModEntry` 中定时器逻辑改为 `_tickCounter` 计数（1800 ticks ≈ 30s）
- [ ] 验证：30 秒内至少有一次保底决策

### 对话不冻结 NPC
- [ ] 确认 `AgentDialogueMenu` / `AgentChatMenu` 打开时不设置 `Game1.freezeControls = true`
- [ ] 确认 `NPCDialoguePatch` 返回 `false` 后，NPC 的 `UpdateTicked` 仍然执行
- [ ] 在 `ModEntry.OnUpdateTicked` 中增加检查：`if (Game1.activeClickableMenu is DialogueBox)` 时 NPC 仍然更新
- [ ] 测试：和 NPC 对话期间，NPC 仍然 farming / 战斗

### 消息控制：只有 TALK 才说话
- [ ] 修改 `FarmHandler`：移除 `NpcSpeechHelper.Speak("Harvested!")`，改用无声的 `showTextAboveHead` 或直接省略
- [ ] 修改 `MineHandler`：同上，移除物品获得消息
- [ ] 修改 `ForageHandler`：同上
- [ ] 修改 `FightHandler`：移除战斗台词，保留 `NpcSpeechHelper` 只在 TALK 使用
- [ ] 修改 `ModEntry`：决策 `thought` 字段只显示 `showTextAboveHead`，**不**发 `chatBox`
- [ ] 验证： farming / 挖矿 / 采集 / 战斗期间聊天框干净

### 任务收尾仪式（不发消息到 chatBox）
- [ ] `FarmHandler`：`target == null` 时播放伸懒腰 emote (24) + `showTextAboveHead("呼，都收完了。")`
- [ ] `MineHandler`：`target == null` 时播放擦汗 emote + `showTextAboveHead("清完了。")`
- [ ] `ForageHandler`：`target == null` 时播放拍背包 emote + `showTextAboveHead("收获不错。")`
- [ ] `FightHandler`：附近无怪物时播放收剑 emote + `showTextAboveHead("搞定！")`
- [ ] 所有收尾动作完成后触发 LLM 决策

---

## 🟠 P1 — 重要（影响活人感）

> P0 全删后再开始做这组。做完后整段删除。

### 送礼仪式感（7 步流程）
- [ ] 设计 `GiftingController` 或扩展现有流程
- [ ] 步骤 1：NPC 检查背包有无可送礼物
- [ ] 步骤 2：NPC 走到玩家面前（PathFindController）
- [ ] 步骤 3：面对玩家，做表情（害羞/开心/期待 emote）
- [ ] 步骤 4：说出送礼的话（LLM 生成，符合人设）
- [ ] 步骤 5：递出物品（原版送礼动画）
- [ ] 步骤 6：等待玩家反应（原版好感度变化）
- [ ] 步骤 7：根据玩家喜好做不同反应（开心/失落）

### 情绪系统
- [ ] 新增 `ValleyAgent/Emotion/NpcEmotion.cs`：Neutral/Happy/Sad/Angry/Worried/Excited/Tired/Grateful
- [ ] `AgentInstance` 增加 `CurrentEmotion` 属性
- [ ] `DecisionContext` 中注入 `CurrentEmotion`
- [ ] `AIDecisionEngine` 的 system prompt 中加入情绪说明
- [ ] `AIDialogueSystem` 的 system prompt 中加入情绪语气要求
- [ ] 情绪影响移动速度：`Tired` 时 `Speed = 1`

### 对话内容写入记忆
- [ ] `AIDialogueSystem.GenerateDialogueAsync` 返回后，将对话内容写入 `AgentInstance` 的短期记忆字段
- [ ] 短期记忆结构：`Queue<string> RecentMemories`（最多 10 条）
- [ ] `BuildDecisionContext` 中将 RecentMemories 注入 prompt
- [ ] 对话内容同步到 `FriendshipSystem` 作为 `InteractionContent`

### 健康恢复
- [ ] `NPCGiftPatch`：检测玩家送的食物，如果是 edible，调用 `AgentHealth.Heal(amount)`
- [ ] `ModEntry.OnDayStarted`：所有 Agent `Health.Respawn()`（回满血）
- [ ] `AgentHUD` 或 `AgentHealth` 中显示回血动画

### ReAct 强化
- [ ] 每个 Handler 内部实现真正的 observe-think-act 循环
- [ ] 例如 `FarmHandler`：观察环境 → 思考"先收最近的还是最大的" → 执行
- [ ] 当前已实现的是基础版本，ReAct 逻辑可以放在 Handler 内部，不需要 LLM 介入

---

## 🟡 P2 — 优化（提升品质）

> P1 全删后再做。做完后整段删除。

### 多 Agent 支持
- [ ] `ModConfig.MaxAgentNpcs` 默认值从 1 改为 2-3
- [ ] `AgentAllocationManager` 评估多个 NPC 的优先级
- [ ] 多个 Agent 之间避免冲突（不抢同一个目标）

### 更丰富的 idle 动画
- [ ] `IdleState` 中随机触发：四处张望、原地踏步、坐下休息
- [ ] 根据情绪切换 idle 动画：开心的 NPC 哼歌，难过的 NPC 低头

### 天气/季节影响行为
- [ ] `DecisionContext` 中加入天气和季节权重
- [ ] 下雨天 NPC 更倾向于室内活动
- [ ] 冬天 NPC 更倾向于矿洞或室内

### 记忆持久化到存档
- [ ] `SaveData` 模型中加入 `NpcEmotion`、`RecentMemories` 字段
- [ ] `SaveDataManager` 序列化/反序列化记忆
- [ ] 加载存档时恢复 NPC 情绪和记忆

---

## 🟢 P3 — 未来（想做但不做）

> 这组不删除，长期保留作为愿景。

- [ ] 自定义 NPC 性格编辑（游戏内 UI）
- [ ] 语音合成（TTS）
- [ ] 更复杂的社交关系网（NPC 之间的好感度）
- [ ] 任务系统（NPC 给玩家发布任务）
- [ ] 日程规划可视化
