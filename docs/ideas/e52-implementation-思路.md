# E5-2 晨间喊话（远程喊话路由）实现思路

> 分支：`feat/exec-e52`（worktree: vt-e52）
> 日期：2026-08-03
> 依据：`docs/plan/2026-08-03-phase3-5-full-execution-plan.md` B1（E5-2）

## 1. 需求

玩家在聊天栏喊话（`@所有人` 或普通文本），NPC 按 4 层确定性路由选择**唯一回应者**：

1. **点名命中** — 文本中出现某 NPC 名字（大小写不敏感，支持显示名）
2. **当前会话** — 与该 NPC 存在活跃聊天会话（ChatSessionRegistry）
3. **跟随/雇佣** — 该 NPC 当前 FOLLOW（E4-1 后并入 HIRED）
4. **已醒 + 好友最高** — 未点名、无会话、无跟随 → 已醒来且好感度最高者

约束：
- 全员未醒/无目标 → **沉默**（不调 LLM，不渲染）
- 4 层全空 → 沉默（确定性路由不调 LLM；LLM 兜底由合并阶段按需接线）
- 远程 NPC（不在场）听到喊话后 **3~8 秒延迟回应**（非即时，模拟"听到后走过来再开口"）
- 被动回应**不消耗主动发言配额**（ProactiveSpeechQuota.RecordPassiveResponse）
- 每 NPC 同一时间最多 1 条待发回应（后到覆盖，防刷屏）

## 2. 改动文件

| 文件 | 改动 |
|---|---|
| `src/ValleyAgent/Chat/MorningShoutRouter.cs` | **新增**：4 层确定性路由 |
| `src/ValleyAgent/Chat/ShoutReplyScheduler.cs` | **新增**：延迟回应调度器 |
| `src/ValleyAgent/Chat/ChatBarRouter.cs` | 接线：`Initialize` 增参（quota/scheduleService）；`ProcessShoutReplies`；远程喊话路径 |
| `src/ValleyAgent/Initialization/EventHandlerInitializer.cs` | 接线：`ChatBarRouter.Initialize` 调用点增参；`OnUpdateTicked` 驱动 `ProcessShoutReplies` |
| `src/ValleyAgent.UnitTests/MorningShoutRouterTests.cs` | **新增**：9 个路由单测 |
| `src/ValleyAgent.UnitTests/ShoutReplySchedulerTests.cs` | **新增**：8 个调度器单测 |
| `docs/ideas/e52-implementation-思路.md` | **本文档** |

## 3. MorningShoutRouter（4 层确定性路由）

纯静态、无游戏依赖，可单测：

```csharp
public static string? Resolve(string shoutText, IReadOnlyList<ShoutCandidate> candidates)
```

- `ShoutCandidate(NpcName, DisplayName, IsAwake, IsInActiveSession, IsFollowingOrHired, FriendshipPoints)`
- 层 1：shoutText 含 candidate.NpcName 或 DisplayName（OrdinalIgnoreCase）→ 返回该 NPC
- 层 2：IsInActiveSession → 返回（多会话时取会话最近者——当前实现取第一个，合并阶段可细化）
- 层 3：IsFollowingOrHired → 返回
- 层 4：IsAwake 且 FriendshipPoints 最高 → 返回
- 全部未命中 → null（沉默）
- 空文本/空候选 → null

## 4. ShoutReplyScheduler（延迟回应）

- `ScheduleReply(npcName, playerShout, nowUtc)`：FNV-1a 确定性哈希 → 3~8s 延迟；同 NPC 覆盖
- `ProcessDueReplies(nowUtc)`：主线程每 tick 调用；到期 → `RenderReply`
- `RenderReply`：NPC 存在且在场 → `RecordPassiveResponse`（不耗额度）→ `ActiveSpeechRouter.Route`
  （气泡/聊天栏，不强制开对话框）→ 渲染失败防堆积（消费即删）
- `Cancel(npcName)` / `Clear()` / `PendingCount` / `PendingNpcs`

设计决策：
- **确定性哈希替代 Random**：规避 CA5394（Random 不安全警告，交付铁律 0 警告），且同 NPC+内容延迟一致可断言
- **被动额度**：`RecordPassiveResponse` 在渲染前调用，保证"回应不算主动"语义
- **render 失败也消费**：防 `_pending` 堆积造成内存泄漏

## 5. ChatBarRouter 接线

- `Initialize` 新增参数：`ProactiveSpeechQuota? quota`、`NpcScheduleService? scheduleService`
- 原 `present.Count == 0` 早返回改为：先本地在场处理，不在场 NPC 走远程喊话路径
- `ProcessShoutReplies()`：每 tick 转发到 `ShoutReplyScheduler.ProcessDueReplies`
- 远程路径：`TryRouteRemoteShout(text)` → 构建全 Agent 候选 → `MorningShoutRouter.Resolve`
  → 命中且已醒 → `ScheduleReply`；全员未醒/无目标 → 沉默

## 6. 验证

- `dotnet build src/ValleyAgent` → 0 警告 0 错误
- `dotnet test src/ValleyAgent.UnitTests` → 277 通过 0 失败（含 17 个 E5-2 新测）
- 渲染路径（Game1 依赖）由游戏内实测覆盖，不强行单测
