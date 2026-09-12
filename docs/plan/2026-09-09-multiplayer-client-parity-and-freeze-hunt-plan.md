# 计划：联机房客可用性修复 + 房主随机卡死猎捕

> 日期：2026-09-09
> 状态：**待放行**（未动任何 src 代码）
> 证据链：`src/ValleyAgent.UnitTests/Multiplayer/` 13 测试（Windows/Docker 双平台 587 过 / 6 设计内红）
> 关联：AGENTS.md §3.7 已知限制；`docs/design/2026-08-16-multiplayer-boundary-analysis.md`

---

## 0. 问题与证据

| # | 问题 | 复现测试（当前红） | 根因定位 |
|---|------|--------------------|----------|
| ① | 房客 AI 回复不渲染、送礼反应与好感不落账 | `ThinClient_UpdateTicked_MustDrainMainThreadQueues` | 房客 UpdateTicked 只驱动 renderer（ModEntry.cs:475-485），三个主线程队列无人排水 |
| ② | 迟到回包被重试请求窃取；同 NPC 并发回包交错；好感 delta 记错礼物 | `Dialogue/Gift_LateResponseAfterTimeout*`、`Dialogue_SameNpc50ConcurrentPairs*` | ModMessage 不携带 requestId，HandleResponse 按 npcName 前缀 FIFO |
| ③ | 后台线程直发 ModMessage（底层消息队列非线程安全） | `DialogueRequest_MustNotBeSentFromBackgroundThread` | SubmitInput/NPCGiftPatch 用 Task.Run 包 SendAsync，SendMessage 落 ThreadPool 线程 |
| ④ | 房客聊天栏输入静默丢弃 | `ThinClient_MustWireChatBarRouter` | ChatBarRouter 仅主机初始化，依赖 6 个主机端组件 |

**已排除**：主机中继链路（HostRequestHandlers→EnqueueMainThread→主线程泵）3 流并发压测全绿（`RelayStress_*` 等 7 个绿测试守卫）。
**未定罪**：房主 3-4 人随机整机卡死（无日志）→ Phase 4 用游戏内看门狗抓现场，**拿到栈之前不修**。

---

## Phase 1：房客可用性（① + ③ + ③b）——本轮主体

### 1.1 房客主线程泵（修①）

**改动**：`ModEntry.InitializeThinClientMode` 第 6 步的 UpdateTicked 订阅，在 `_remoteRenderer?.Update(tickCounter++)` 之后追加：

```csharp
DialogueBoxInputPatch.ProcessPendingReplies();          // AI 回复渲染
NPCGiftPatch.ProcessMainThreadActions();                // 送礼反应 + fd.Points 落账
Multiplayer.HostRequestHandlers.ProcessMainThreadActions(); // 防御性：房客侧未来入队路径
```

**说明**：三个 drain 均为非阻塞 TryDequeue，追加进同一 lambda 无帧预算风险；Inert 模式不初始化 patch，无需处理。

### 1.2 ModMessage 发送主线程化（修③）

**改动**：`FarmhandDialogueTransport.SendAsync` / `FarmhandGiftTransport.SendAsync` 第 4 步——

- 现状：后台线程直接 `_helper.Multiplayer.SendMessage(...)`。
- 改为：`HostRequestHandlers.EnqueueMainThread(() => { try { SendMessage(...) } catch (ex) { _pending.TryRemove; tcs.TrySetResult(BuildFallbackResponse(...)); } })`。
- TCS 生命周期不变（await tcs.Task + 超时兜底原样保留）；发送失败不再走 return 路径而是经 TCS 完成，调用方语义不变。

### 1.3 WorldSnapshot 主线程采集（③b，同类纪律问题）

**改动**：`DialogueBoxInputPatch.SubmitInput`（DialogueBoxInputPatch.cs:304-308）与 `NPCGiftPatch` 房客分支：把 `WorldSnapshotBuilder.Build(...)` / `GetNpcState(...)` 移到 `Task.Run` **之前**（调用点本身在 Harmony Prefix=主线程），后台 lambda 只做网络等待。

### 测试与验收

- `ThinClient_UpdateTicked_MustDrainMainThreadQueues`、`DialogueRequest_MustNotBeSentFromBackgroundThread` → **转绿**；源码审计测试保留为接线回归守卫。
- 全量 `dotnet test`（Windows + Docker）≥587 绿，且 6 红中只剩 Phase 2 的 3 个匹配类。
- 游戏内：双开 e2e 重跑 C1-C5（重点 C2/C3 房客侧**可见**回复与好感变化）；手动双开体验房客对话/送礼。

---

## Phase 2：回包 requestId（修②）

### 2.1 协议

`AgentSyncMessages.cs`：`DialogueRequestMessage`/`DialogueResponseMessage`/`GiftRequestMessage`/`GiftResponseMessage` 各加 `public string? RequestId`（可选，向后兼容旧端）。

### 2.2 链路

1. 房客 SendAsync：已生成的 `requestId`（`{npc}_{guid:N}`）填进请求消息。
2. 主机 `HostRequestHandlers.HandleDialogueRequest/HandleGiftRequest`：原样把 `msg.RequestId` 传给 `ApplyDialogueResponse` / 回包构造。
3. `AgentSyncBroadcaster.SendDialogueResponse/SendGiftResponse`：加 `string? requestId = null` 尾参（默认值保持既有调用点不动）。
4. 房客 `HandleResponse`：`msg.RequestId` 非空 → 精确 `_pending.TryRemove(msg.RequestId)`；为空（旧主机）→ 退回现 FIFO 前缀策略。**版本内不破坏兼容**。

### 测试与验收

- 3 个匹配类红测试 → **转绿**；`Dialogue_ConcurrentDifferentNpcs_RoutesByPrefixCorrectly` 保持绿（旧主机回退路径）。
- 新增：`HandleResponse_OldHostWithoutRequestId_FallsBackToFifo`（绿基线）。
- 文档：AGENTS.md §3.7 已知限制中"ModMessage 中继无 requestId"条目更新为已修 + 回退策略说明。

---

## Phase 3（可选，需拍板）：房客聊天栏 parity（修④）

`ChatBarRouter.Initialize` 依赖 AgentService/CommandExecutor/ShoutReplyScheduler/NpcScheduleService 等主机端组件，thin client 全部没有。两个方案：

- **方案 A（轻量路由，建议）**：新增 thin-client 分支——聊天栏消息直接路由给最近/指定 NPC，经 `FarmhandDialogueTransport` 走对话链路，回包进房客聊天栏（灰色 NPC 名前缀）。不搬 ShoutReplyScheduler/主动发言额度（主机概念）。
- **方案 B（完整改造）**：ChatBarRouter 抽象出 IChatDialogueSink，房客注入 transport 版。工作量大，多数主机语义（喊话/额度/日程）在房客侧无意义。

**建议**：本轮做 A；B 记入 backlog。

---

## Phase 4：房主随机卡死猎捕（不改主代码，纯仪器）

1. **TestMod 主线程看门狗**：`UpdateTicked` 心跳 + 专用 watchdog 线程；停滞 >3s → P/Invoke `MiniDumpWriteDump` 落 `.dmp`（Mods 目录）+ 记录最后 N 条 SMAPI 日志。host/farmhand 都挂（验证"房客污染网络流→主机卡死"假设②时房客侧心跳同样关键）。
2. **加压 soak**：现 docker e2e 增至 3-4 个 farmhand 容器 + Autopilot 高频对话/送礼脚本，复跑至卡死或 2h 无复现。
3. **分析**：`.dmp` 用 `dotnet-dump` 看卡死线程栈 → 定罪后单独立项修复。

**验收**：拿到卡死现场栈，或高置信排除现有两假设。

---

## 执行顺序与风险

1 → 2 → 3（可选）与 4 并行。Phase 1/2 均为小步、每步测试映射明确；主要风险是 Phase 1.2 改变发送时序（消息经主线程泵发送，泵停则发送延迟——与游戏同帧排水，风险可忽略）。回滚单元=单 commit。

**验证门**：每 Phase 结束跑 Windows + Docker 全量 `dotnet test`（期望只减红不加红）+ 游戏内双开实测（按惯例你自己开，我给命令清单）。
