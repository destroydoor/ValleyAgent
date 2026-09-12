# ValleyAgent 系统性重构计划

> **历史文档（2026-07-26 标注）**
>
> 本文档是 2026-06-21 的 v4.1 重构计划，针对 Python 智能层时代的系统性问题。
> 部分修复（如 Friendship 死代码、THINKING 状态移除、CircuitBreaker 集成）
> 仍然有效并已合入 v4.3 主线；但所有"修改 Python 端"任务均不再适用——
> Python 服务器已被 TS Agent Server（`valley-ai-server.exe`）完全替换。
>
> **当前架构权威文档**：[`../AGENTS.md`](../AGENTS.md)。

---

> **目标**：修复"功能做完了、编译过了、测试通过了，但用户视角看根本没实现"的系统性问题，让 LLM 真正接管指挥，让礼物/对话/决策系统按设计意图工作。

**架构**：分 6 个阶段，每个阶段独立可编译、可测试、可提交。阶段 0 修复致命 bug，阶段 1-3 修复核心系统，阶段 4-5 修复配置和测试系统。

**技术栈**：C# (.NET 6+)、SMAPI、Harmony、xUnit、星露谷物语

**铁律**：
- 每个阶段必须编译通过 0 警告 0 错误
- 每个阶段必须通过静态分析
- 每个阶段必须通过单元测试
- 每个阶段必须实际运行验证
- 不允许抑制任何警告或错误

---

## 阶段 0：致命 bug 修复（最高优先级）

**目标**：修复让功能"完全不可用"的致命 bug。

### 任务 0.1：修复 Friendship 永远为 0 的致命 bug

**问题**：`EventHandlerInitializer.cs:1692-1696` 读取 `RelationshipMemory["FriendshipPoints"]`，但这个 key 从未写入（`RelationshipContextProvider` 是死代码）。导致 LLM 永远认为友谊点是 0，FriendshipPhase 永远是"陌生人"。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:1692-1696`

**修改内容**：直接从 `Game1.player.friendshipData` 读取真实友谊点数，不依赖死代码 Provider。

```csharp
// 修改前
var friendship = 0;
if (context.RelationshipMemory.TryGetValue("FriendshipPoints", out var fpObj) && fpObj is int fp)
{
    friendship = fp;
}

// 修改后
var friendship = 0;
if (Game1.player?.friendshipData.TryGetValue(agent.NpcName, out var friendshipData) == true)
{
    friendship = friendshipData.Points;
}
```

**验证**：编译通过，启动游戏后 LLM 决策请求中 Friendship 字段为真实值。

### 任务 0.2：修复 i18n 键缺失导致 NPC 说话只有 "..."

**问题**：`TalkHandler.cs:43` 引用 `DIALOG_Talk_0` 到 `DIALOG_Talk_6`，但 i18n 文件里这些键全部缺失，导致 NPC 主动说话永远只有 "..."。

**文件**：
- 修改：`src/ValleyAgent/i18n/default.json`
- 修改：`src/ValleyAgent/i18n/zh.json`

**修改内容**：在两个 i18n 文件中添加 7 个问候语键。

`default.json` 添加：
```json
"DIALOG_Talk_0": "Hi there!",
"DIALOG_Talk_1": "Beautiful day, isn't it?",
"DIALOG_Talk_2": "How are you doing?",
"DIALOG_Talk_3": "Nice to see you!",
"DIALOG_Talk_4": "What's on your mind?",
"DIALOG_Talk_5": "Hey, got a moment?",
"DIALOG_Talk_6": "Good to talk to you!"
```

`zh.json` 添加：
```json
"DIALOG_Talk_0": "嗨，你好呀！",
"DIALOG_Talk_1": "今天天气真不错呢。",
"DIALOG_Talk_2": "你最近过得怎么样？",
"DIALOG_Talk_3": "见到你真高兴！",
"DIALOG_Talk_4": "你在想什么呢？",
"DIALOG_Talk_5": "嘿，有空聊聊天吗？",
"DIALOG_Talk_6": "和你说话真开心。"
```

**验证**：编译通过，启动游戏后 NPC 进入 TALK 状态时头顶气泡显示真实问候语而非 "..."。

### 任务 0.3：修复 TalkHandler 注释撒谎

**问题**：`TalkHandler.cs:36-37` 注释声称"Tries AI text pool first (50% probability)"，但代码实际只查 i18n。

**文件**：
- 修改：`src/ValleyAgent/Handlers/TalkHandler.cs:34-37`

**修改内容**：删除虚假注释，让注释与代码一致。

```csharp
// 修改前
/// <summary>
/// Gets a random greeting line for the NPC to speak in TALK state.
/// Tries AI text pool first (50% probability), then falls back to i18n.
/// </summary>

// 修改后
/// <summary>
/// Gets a random greeting line for the NPC to speak in TALK state.
/// Picks from i18n greeting pool (DIALOG_Talk_0..6).
/// </summary>
```

**验证**：编译通过，注释与代码一致。

### 任务 0.4：修复 DIALOG_Greet_Final 值为 "..."

**问题**：`i18n/default.json:55` 和 `zh.json:55` 的 `DIALOG_Greet_Final` 值是 "..." / "…"，这会导致 NPC 对话问候语也是省略号。

**文件**：
- 修改：`src/ValleyAgent/i18n/default.json:55`
- 修改：`src/ValleyAgent/i18n/zh.json:55`

**修改内容**：
```json
// default.json
"DIALOG_Greet_Final": "Oh, hello! What brings you here today?",

// zh.json
"DIALOG_Greet_Final": "哦，你好呀！今天怎么想到来找我？"
```

**验证**：编译通过，启动游戏后玩家点击 NPC 时显示真实问候语。

---

## 阶段 1：LLM 上下文完整性

**目标**：让 LLM 收到完整的决策上下文，不再基于错误数据决策。

### 任务 1.1：发送完整 Inventory 内容

**问题**：`EventHandlerInitializer.cs:1749-1757` 只发送"N件物品"，LLM 不知道背包里有什么。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:1749-1757`

**修改内容**：发送实际物品名称列表。

```csharp
// 修改后
private static string GetInventorySummary(AgentInstance agent)
{
    if (agent.Inventory == null) return "";
    var count = agent.Inventory.Count;
    var isFull = agent.Inventory.IsFull;
    var items = agent.Inventory.GetAllItems()
        .Where(i => i != null)
        .Select(i => i.DisplayName)
        .Take(20);
    var itemList = string.Join("、", items);
    var header = isFull ? $"背包已满({count}件)" : $"{count}件物品";
    return string.IsNullOrEmpty(itemList) ? header : $"{header}: {itemList}";
}
```

**验证**：编译通过，LLM 决策请求中 Inventory 字段包含物品名称。

### 任务 1.2：发送 EmotionState 完整信息

**问题**：LLM 只收到情绪描述字符串，不知道原因（Source）和强度（Intensity）。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:1661-1724`

**修改内容**：在 Mood 字段中附加情绪来源和强度。

```csharp
// 在 BuildDecisionRequest 中修改 mood 构造
var mood = agent.Brain?.CurrentEmotionState != null
    ? $"{agent.Brain.GetEmotionDescription()} (强度:{agent.Brain.CurrentEmotionState.Intensity:F1}, 原因:{agent.Brain.CurrentEmotionState.Source ?? "未知"})"
    : "未知";
```

**验证**：编译通过，LLM 决策请求中 Mood 字段包含强度和原因。

### 任务 1.3：发送 CurrentGoal

**问题**：`AgentBrain.CurrentGoal` 存了但从未发送给 LLM。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:1700-1723`

**修改内容**：在 DecisionRequest 中添加 CurrentGoal 字段。需要先修改 `IAgentServerProvider.cs` 的 DecisionRequest record 定义。

**注意**：此任务需要修改 WebSocket 协议，需同步修改 Python 端。暂列为后续任务，本阶段先在 Mood 或 Memory 字段中附加 CurrentGoal 信息。

```csharp
// 在 memory 字段中附加 CurrentGoal
var currentGoal = agent.Brain?.CurrentGoal;
if (!string.IsNullOrEmpty(currentGoal))
{
    memory = string.IsNullOrEmpty(memory) ? $"当前目标: {currentGoal}" : $"{memory}\n当前目标: {currentGoal}";
}
```

### 任务 1.4：发送 world_knowledge

**问题**：`DecisionContextBuilder.cs:96-106` 构建了 world_knowledge 但 `BuildDecisionRequest` 未读取。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:1700-1723`

**修改内容**：在 BuildDecisionRequest 中读取 world_knowledge 并附加到 Memory 字段。

```csharp
// 在 BuildDecisionRequest 中添加
var worldKnowledge = "";
if (context.GameState.TryGetValue("world_knowledge", out var wkObj) && wkObj is string wkStr)
{
    worldKnowledge = wkStr;
}

// 附加到 memory
if (!string.IsNullOrEmpty(worldKnowledge))
{
    memory = string.IsNullOrEmpty(memory) ? worldKnowledge : $"{memory}\n{worldKnowledge}";
}
```

### 任务 1.5：发送 BlockedStates

**问题**：`InjectBlockedStates` 注入的不可行状态列表未被读取，LLM 不知道哪些状态不可行。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:1700-1723`

**修改内容**：读取 BlockedStates 并附加到 Memory 字段。

```csharp
// 在 BuildDecisionRequest 中添加
var blockedStates = context.BlockedStates != null && context.BlockedStates.Count > 0
    ? $"不可行状态: {string.Join(", ", context.BlockedStates)}"
    : "";

if (!string.IsNullOrEmpty(blockedStates))
{
    memory = string.IsNullOrEmpty(memory) ? blockedStates : $"{memory}\n{blockedStates}";
}
```

### 任务 1.6：决策路径接入 TraitPhaseMapper

**问题**：`EventHandlerInitializer.cs:1717-1731` 的 `GetVisibleTraits` 不过滤友谊阶段，陌生人也能看到深层特质。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:1727-1731`

**修改内容**：使用 TraitPhaseMapper 根据友谊阶段过滤特质。

```csharp
// 修改后
private static string GetVisibleTraits(AgentInstance agent)
{
    if (agent.Brain?.Bio?.Traits == null || agent.Brain.Bio.Traits.Count == 0) return "";
    var phase = FriendshipPhaseHelper.FromFriendship(GetCurrentFriendshipPoints(agent.NpcName));
    var visibleTraits = TraitPhaseMapper.GetVisibleTraitNames(agent.Brain.Bio.Traits, phase);
    return string.Join("、", visibleTraits);
}

private static int GetCurrentFriendshipPoints(string npcName)
{
    return Game1.player?.friendshipData.TryGetValue(npcName, out var fd) == true ? fd.Points : 0;
}
```

### 任务 1.7：决策路径使用 Bio.GetPhaseSummary

**问题**：决策路径用 `Bio.PromptSummary`（固定前2句），不用 `GetPhaseSummary(phase)`（按友谊阶段分层）。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:1716`

**修改内容**：

```csharp
// 修改前
NpcBiography: agent.Brain?.Bio != null ? (agent.Brain.Bio.PromptSummary ?? "") : "",

// 修改后
NpcBiography: GetPhaseAwareBiography(agent),
```

添加辅助方法：
```csharp
private static string GetPhaseAwareBiography(AgentInstance agent)
{
    if (agent.Brain?.Bio == null) return "";
    var phase = FriendshipPhaseHelper.FromFriendship(GetCurrentFriendshipPoints(agent.NpcName));
    return agent.Brain.Bio.GetPhaseSummary(phase) ?? "";
}
```

---

## 阶段 2：决策系统重构

**目标**：让 LLM 决策真正生效，不被程序路径抢占或丢弃。

### 任务 2.1：修复 ParseAgentState 静默降级

**问题**：`EventHandlerInitializer.cs:1907` 的 `ParseAgentState` 失败时静默返回 IDLE，无日志。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:1907`

**修改内容**：添加日志记录。

```csharp
// 修改后
private static AgentState ParseAgentState(string state, IMonitor? monitor = null, string? npcName = null)
{
    if (Enum.TryParse<AgentState>(state, true, out var result))
    {
        return result;
    }
    monitor?.Log($"[Decision] {npcName ?? "unknown"}: LLM returned unparseable state '{state}', defaulting to IDLE", LogLevel.Warn);
    return AgentState.IDLE;
}
```

### 任务 2.2：修复决策被 continue 丢弃

**问题**：`EventHandlerInitializer.cs:927-932` 在状态太年轻时 `continue` 丢弃决策，日志撒谎说"deferred"。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:927-932`

**修改内容**：将决策重新入队而非丢弃。

```csharp
// 修改后
var minDuration = GetMinimumStateDuration(safeAgent.StateMachine.CurrentStateFlag);
if (safeAgent.StateMachine.StateDuration.TotalSeconds < minDuration)
{
    _monitor.Log($"Decision re-enqueued for {safeAgent.NpcName}: state too young ({safeAgent.StateMachine.StateDuration.TotalSeconds:F1}s < {minDuration}s)", LogLevel.Trace);
    // 重新入队，延后处理
    lock (_pendingDecisionsLock)
    {
        _pendingDecisions.Enqueue((safeAgent, targetState, reason, thought));
    }
    continue;
}
```

### 任务 2.3：用 TryTransition 替换 ForceTransition（LLM 决策路径）

**问题**：`EventHandlerInitializer.cs:985` 用 `ForceTransition` 绕过验证。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:985`

**修改内容**：

```csharp
// 修改前
safeAgent.StateMachine.ForceTransition(targetState);

// 修改后
if (!safeAgent.StateMachine.TryTransition(targetState))
{
    _monitor.Log($"[Decision] {safeAgent.NpcName}: transition to {targetState} rejected by state machine", LogLevel.Warn);
}
```

### 任务 2.4：修复 Brain 记录与实际状态不一致

**问题**：`EventHandlerInitializer.cs:985-987` 无论 `ForceTransition` 是否成功都设置 `LastDecisionState`。

**文件**：
- 修改：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs:985-987`

**修改内容**：

```csharp
// 修改后（与任务 2.3 合并）
if (safeAgent.StateMachine.TryTransition(targetState))
{
    safeAgent.LastDecisionState = targetState.ToString();
    safeAgent.LastDecisionReason = reason;
}
else
{
    _monitor.Log($"[Decision] {safeAgent.NpcName}: transition to {targetState} rejected by state machine", LogLevel.Warn);
    safeAgent.LastDecisionState = safeAgent.StateMachine.CurrentStateFlag.ToString();
    safeAgent.LastDecisionReason = $"转换被拒绝: {reason}";
}
```

---

## 阶段 3：礼物对话系统对齐

**目标**：让礼物和对话系统在关键节点对齐，不再"一直出错"。

### 任务 3.1：修复对话历史 entryType 错配

**问题**：`DialogueManagementApi.cs:137-138` 写入记忆时未指定 `entryType`（默认 Generic），但查询时过滤 `Conversation`，导致 LLM 永远收不到对话历史。

**文件**：
- 修改：`src/ValleyAgent/Api/DialogueManagementApi.cs:137-138`

**修改内容**：

```csharp
// 修改前
agent.Brain?.AddMemory(memPlayer);
agent.Brain?.AddMemory(memResponse);

// 修改后
agent.Brain?.AddMemory(memPlayer, entryType: MemoryEntryType.Conversation);
agent.Brain?.AddMemory(memResponse, entryType: MemoryEntryType.Conversation);
```

### 任务 3.2：礼物路径接入熔断器检查

**问题**：`NPCGiftPatch.cs:204` 直接调用 LLM，无熔断器检查，Python 宕机时礼物反应消失。

**文件**：
- 修改：`src/ValleyAgent/Patches/NPCGiftPatch.cs:172-253`

**修改内容**：在 LLM 调用前添加熔断器检查和本地回退。

```csharp
// 在 LLM 调用前添加
var circuitBreaker = AgentService?.CircuitBreaker;
if (circuitBreaker != null && !circuitBreaker.CanExecute)
{
    Monitor?.Log($"[Gift] {__instance.Name}: circuit breaker OPEN, using local fallback", LogLevel.Warn);
    var fallbackText = GetLocalGiftFallback(giftTaste, itemName);
    agent.Brain?.AddMemory($"我对礼物的反应：{fallbackText}", importance: 2.0, entryType: MemoryEntryType.Event);
    // 显示回退文本
    _ = Task.Run(() =>
    {
        System.Threading.Thread.Sleep(500);
        Utils.NpcSpeechHelper.Speak(__instance, fallbackText, durationMs: 3000);
    });
    return;
}
```

添加辅助方法：
```csharp
private static string GetLocalGiftFallback(int giftTaste, string itemName)
{
    return giftTaste switch
    {
        0 => $"哇，{itemName}！这是我最大的惊喜，谢谢你！",
        2 => $"谢谢你送我{itemName}，我很喜欢。",
        4 => $"嗯，{itemName}，谢谢你的心意。",
        6 => $"呃...{itemName}啊，谢谢，但我不是很喜欢。",
        8 => $"抱歉，{itemName}...我真的无法接受。",
        _ => $"谢谢你送我{itemName}。",
    };
}
```

### 任务 3.3：礼物路径接入 SpecialEvents 倍率

**问题**：`NPCGiftPatch.cs:92-100` 构造 `FriendshipChangeContext` 时未设置 `SpecialEvents`，丢失生日/节日倍率。

**文件**：
- 修改：`src/ValleyAgent/Patches/NPCGiftPatch.cs:92-100`

**修改内容**：

```csharp
// 修改后
var specialEvents = SpecialEventFlags.None;
if (Game1.player?.Spouse == __instance.Name) specialEvents |= SpecialEventFlags.Spouse;
// 检查生日
var npcData = Game1.player?.friendshipData.TryGetValue(__instance.Name, out var fd) == true ? fd : null;
// SDV 原版生日检查
if (Utility.isPlayerHeardOfCharacter(__instance.Name) && __instance.isBirthday())
{
    specialEvents |= SpecialEventFlags.Birthday;
}

var context = new FriendshipChangeContext
{
    NpcName = __instance.Name,
    CurrentFriendshipPoints = fd.Points,
    MaxFriendshipPoints = 2500,
    InteractionContent = $"Gift: {item.DisplayName} ({tasteLabel})",
    InteractionType = InteractionType.Gift,
    CurrentDateKey = dateKey,
    SpecialEvents = specialEvents
};
```

**注意**：需要确认 `SpecialEventFlags` 枚举和 `FriendshipChangeContext.SpecialEvents` 字段是否存在。如果不存在，需要先创建。

### 任务 3.4：礼物路径使用 EvaluateAndApplyChangeAsync

**问题**：礼物路径用 `ApplyDirectChange`（无递减、无倍率），注释声称有递减。

**文件**：
- 修改：`src/ValleyAgent/Patches/NPCGiftPatch.cs:89-104`

**修改内容**：改用 `EvaluateAndApplyChangeAsync`，但需要注意这是异步方法，而 Prefix 是同步的。需要重构为：Prefix 只记录礼物信息，Postfix 异步应用好感度变更。

**注意**：此任务较复杂，涉及异步重构。暂列为后续任务，本阶段先用任务 3.3 的 SpecialEvents 倍率在 `ApplyDirectChange` 中手动应用。

```csharp
// 临时方案：在 ApplyDirectChange 后手动应用生日倍率
var actualDelta = delta;
if (specialEvents.HasFlag(SpecialEventFlags.Birthday))
{
    actualDelta = (int)(delta * 3.0); // 生日 3 倍
    Monitor?.Log($"[Gift] {__instance.Name}: birthday gift, 3x multiplier applied ({delta} → {actualDelta})", LogLevel.Info);
}
var result = FriendshipSystem.ApplyDirectChange(context, actualDelta, ...);
```

---

## 阶段 4：配置项和 UI 对齐

**目标**：让配置项真正生效，或移除虚假配置项。

### 任务 4.1：实现 EnableGifts 开关

**问题**：`EnableGifts` 配置项 UI 暴露但 `NPCGiftPatch.Prefix` 不检查。

**文件**：
- 修改：`src/ValleyAgent/Patches/NPCGiftPatch.cs:44`

**修改内容**：

```csharp
// 在 Prefix 开头添加
if (AgentService?.Config?.EnableGifts == false)
{
    return true; // 走原版逻辑
}
```

### 任务 4.2：实现 EnableFriendshipChanges 开关

**问题**：`EnableFriendshipChanges` 配置项 UI 暴露但代码不检查。

**文件**：
- 修改：`src/ValleyAgent/Patches/NPCGiftPatch.cs:89-104`

**修改内容**：

```csharp
// 在好感度变更前添加
if (AgentService?.Config?.EnableFriendshipChanges == false)
{
    // 只消耗物品，不变更好感度
    who.ActiveObject = null;
    return false;
}
```

### 任务 4.3：移除未实现的配置项 UI

**问题**：多个配置项 UI 暴露但代码不消费，误导用户。

**文件**：
- 修改：`src/ValleyAgent/Config/GMCMIntegration.cs`

**修改内容**：移除以下配置项的 UI 注册（保留 config 字段但不在 UI 暴露）：
- `ServerAddress`、`Provider`、`ApiKey`、`Model`（LLM 走 Python 端，这些无效）
- `LLMTimeoutSeconds`、`MaxRetries`（C# 侧不消费）
- `FallbackAIMixProbability`、`FallbackLiveGenerationProbability`（未实现）
- `AIDailyTopicCount`、`EnableFirstClickVanilla`、`CustomSystemPrompt`、`Language`（未实现）

**注意**：移除 UI 注册时需保持 config 字段不变，避免破坏存档兼容性。

---

## 阶段 5：测试系统修复

**目标**：让测试系统能发现"LLM 回退到程序回复"等问题。

### 任务 5.1：API 暴露响应来源

**问题**：`agent.LastDialogueResponse` 不区分 LLM/回退/错误来源。

**文件**：
- 修改：`src/ValleyAgent/Brain/AgentBrain.cs`（添加 `LastDialogueSource` 字段）
- 修改：`src/ValleyAgent/Api/DialogueManagementApi.cs`（写入来源）
- 修改：`src/ValleyAgent/Api/IValleyAgentApi.cs`（暴露查询方法）

**修改内容**：

在 `AgentBrain.cs` 添加：
```csharp
public DialogueResponseSource LastDialogueSource { get; set; } = DialogueResponseSource.None;
```

添加枚举：
```csharp
public enum DialogueResponseSource { None, LLM, Fallback, Error }
```

在 `DialogueManagementApi.cs` 的三个写入点分别设置来源：
```csharp
// LLM 成功时
agent.LastDialogueSource = DialogueResponseSource.LLM;

// 熔断器 OPEN 时
agent.LastDialogueSource = DialogueResponseSource.Fallback;

// 异常时
agent.LastDialogueSource = DialogueResponseSource.Error;
```

### 任务 5.2：修复 TestResult.Warn 当 Pass

**问题**：`TestResult.Warn` 的 `Passed=true`，功能缺失算"通过"。

**文件**：
- 修改：`src/ValleyAgent.TestMod/TestAssertions.cs:500-515`

**修改内容**：将关键断言的 Warn 改为 Fail，或添加严格模式。

```csharp
// 修改前
return afterPoints != beforePoints
    ? TestResult.Pass(...)
    : TestResult.Warn($"... may be expected if LLM is offline.");

// 修改后
return afterPoints != beforePoints
    ? TestResult.Pass(...)
    : TestResult.Fail($"Friendship unchanged ({beforePoints}→{afterPoints}). LLM may be offline or not affecting friendship.");
```

### 任务 5.3：添加响应来源断言

**问题**：测试无法检查"LLM 真的被调用了"。

**文件**：
- 修改：`src/ValleyAgent.TestMod/TestAssertions.cs`

**修改内容**：添加新断言方法。

```csharp
public static TestResult DialogueFromLLM(DialogueResponseSource source)
{
    return source == DialogueResponseSource.LLM
        ? TestResult.Pass($"Dialogue from LLM")
        : TestResult.Fail($"Dialogue source is {source}, expected LLM");
}
```

---

## 执行顺序

1. **阶段 0**（致命 bug）→ 编译 → 测试 → 提交
2. **阶段 1**（LLM 上下文）→ 编译 → 测试 → 提交
3. **阶段 2**（决策系统）→ 编译 → 测试 → 提交
4. **阶段 3**（礼物对话）→ 编译 → 测试 → 提交
5. **阶段 4**（配置 UI）→ 编译 → 测试 → 提交
6. **阶段 5**（测试系统）→ 编译 → 测试 → 提交
7. **全量验证**：编译 0 警告 + 静态分析 + 单元测试 + 实际运行

## 验证标准

每个阶段完成后必须满足：
- [ ] `dotnet build` 0 警告 0 错误
- [ ] 静态分析（CA 分析器）0 警告
- [ ] 单元测试全部通过
- [ ] 实际启动游戏验证功能生效
- [ ] 提交 git commit
