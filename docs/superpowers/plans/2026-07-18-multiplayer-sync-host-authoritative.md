# 联机同步（方案 B：主机权威 + ModMessage）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 联机模式下 farmhand 玩家能看到 Agent NPC 的移动/表情/对话表现，并能与 Agent 对话、送礼；全部 Agent 逻辑与 LLM 只在主机运行（一个 NPC 一个灵魂）；farmhand 不启动服务器、不执行决策、不写 `npc.controller`。

**Architecture:** 主机权威（Pathoschild TractorMod 范式）。主机跑完整 mod + 唯一 TS 服务器；farmhand 跑"薄客户端"（渲染 + 消息收发 + 交互代理）。复用已存在但未接线的骨架：`AgentSyncBroadcaster`（主机侧广播）、`AgentRemoteRenderer`（客机侧渲染）、8 种 `AgentSyncMessages` DTO、`MultiplayerHelper`。

**Tech Stack:** C# (.NET 6 + SMAPI 1.6)、SMAPI Multiplayer API（SendMessage / ModMessageReceived / PeerConnected / PeerDisconnected）

**前置依赖：**
- P1 计划 Phase 4（E2 farmhand 惰性止血）已落地——本计划将其演进为三模式初始化（§"模式矩阵"），不冲突。
- 用户决策（2026-07-18）：联机立即立项；到达观感"从入口走过来"（P1 Task 6A）在主机侧生效后，经快照同步自然呈现给 farmhand。

**关键调研依据（一手来源）：**
- NPC `position`/`controller` **不是 net field**（反编译 NPC.cs initNetFields）→ farmhand 无法被 vanilla 同步 Agent 的自定义移动，必须由本计划广播快照。
- vanilla 同步给 farmhand 的 NPC 移动是"周期快照间插值"（被动、低频）→ 对 Agent 的快速移动/传送响应不足，自定义快照+插值是必须的。
- TractorMod 范式：`Context.IsMainPlayer` 门控 + `SendMessage(..., playerIDs: [Game1.MasterPlayer.UniqueMultiplayerID])` + 主机 mod 版本检查（host 没装 mod 时 farmhand 禁用并提示）。
- farmhand 只激活自己所在图 + 农场/农舍/温室（active locations）；非激活图的 NPC 是 shadow 副本 → **farmhand 只能看到与自己同图的 Agent 表现**（引擎约束，与 D4 同源，README 需写明）。

---

## 模式矩阵（ModEntry 三模式初始化）

| 模式 | 条件 | 行为 |
|------|------|------|
| **HostMode** | `Context.IsMainPlayer`（含单机） | 现状不变：完整服务 + TS 服务器 + 全部事件/补丁 + **新增广播层** |
| **ThinClientMode** | 联机 farmhand **且主机装有本 mod 且版本兼容** | 不启动服务器/Agent 逻辑；初始化：AgentRemoteRenderer + 联机消息接收 + 薄交互补丁（对话/送礼代理） |
| **InertMode** | 联机 farmhand 但主机**未装** mod / 版本不兼容 / 分屏副屏 | 完全惰性（P1 E2 现状）+ 日志告知原因 |

版本检查（TractorMod 范式）：
```csharp
var hostMod = helper.Multiplayer
    .GetConnectedPlayer(Game1.MasterPlayer.UniqueMultiplayerID)
    ?.GetMod("dandm1.ValleyAgent");
// hostMod == null → InertMode（"主机未安装 ValleyAgent"）
// hostMod.Version 与本地 Major 不一致 → InertMode（"版本不兼容"）
```

---

## 文件结构映射（全部在 `<REPO_ROOT>\src\`）

| 文件 | 操作 | 职责 |
|------|------|------|
| `ValleyAgent.Abstractions\Multiplayer\AgentSyncMessages.cs` | Modify | DTO 审计补齐：position/locationName/facing/isMoving（Phase 1） |
| `ValleyAgent.Abstractions\Multiplayer\AgentRemoteRenderer.cs` | Modify | 新增位置插值引擎（Phase 3） |
| `ValleyAgent\Multiplayer\AgentSyncBroadcaster.cs` | Modify | 快照内容生产（position/facing/isMoving）+ 触发点接线 |
| `ValleyAgent\Multiplayer\MultiplayerEventRouter.cs` | Create | 联机事件统一入口：ModMessageReceived/PeerConnected/PeerDisconnected 路由 |
| `ValleyAgent\Multiplayer\FarmhandDialogueTransport.cs` | Create | 客机对话传输：SubmitInput → ModMessage → 等 DialogueResponseMessage |
| `ValleyAgent\Multiplayer\HostRequestHandlers.cs` | Create | 主机侧 3 条代理链路处理器（Dialogue/Gift/Interaction Request） |
| `ValleyAgent\Initialization\ServiceInitializer.cs` | Modify | HostMode 下实例化 AgentSyncBroadcaster |
| `ValleyAgent\Initialization\EventHandlerInitializer.cs` | Modify | 注册联机事件；OnUpdateTicked 驱动 broadcaster.Update |
| `ValleyAgent\ModEntry.cs` | Modify | 三模式初始化分支（替换 P1 的单一惰性早退） |
| `ValleyAgent\Patches\NPCDialoguePatch.cs` | Modify | ThinClientMode 分支：点击走客机代理而非本地处理 |
| `ValleyAgent\Patches\NPCGiftPatch.cs` | Modify | ThinClientMode 分支：送礼走客机代理 |
| `ValleyAgent\Patches\DialogueBoxInputPatch.cs` | Modify | 对话传输抽象（Host 直连 / Farmhand ModMessage） |
| `ValleyAgent\StateMachine\AgentStateMachine.cs` | Modify | OnStateChanged 触发广播钩子 |
| `ValleyAgent\Core\AgentHealth.cs` | Modify | TakeDamage/Heal/Respawn 触发广播钩子 |
| `ValleyAgent\Core\CommandExecutor.cs` | Modify | speak/emote 命令执行后触发 BroadcastNpcAction |
| `ValleyAgent.TestMod\Tests\Functional\Func_MultiplayerSync.cs` | Modify | 扩展：DTO 往返、广播节流、插值行为 |
| `ValleyAgent\manifest.json` | Modify | 无需结构变更（确认即可） |
| `README.md` | Modify | 联机支持矩阵 + active-location 限制说明 |

---

## Phase 1: DTO 审计与协议版本

### Task 1: AgentSyncMessages 字段审计与补齐

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent.Abstractions\Multiplayer\AgentSyncMessages.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent.TestMod\Tests\Functional\Func_MultiplayerSync.cs`

**背景**：8 种 DTO 已定义（AgentState/FullSync/DialogueResponse/GiftResponse/NpcAction + DialogueRequest/GiftRequest/InteractionRequest，行 39-155），但字段清单未经验证。位置插值需要 `position`、`locationName`、`facingDirection`、`isMoving`；HUD 需要 `health/maxHealth/state/emotion`。

- [ ] **Step 1: 审计现有 DTO 字段并对照需求表补齐**

`AgentStateMessage` 必须字段：

| 字段 | 类型 | 用途 | 现状 |
|------|------|------|------|
| NpcName | string | 路由 | 待确认 |
| LocationName | string | 所在图（NameOrUniqueName） | 待确认/补 |
| PositionX / PositionY | float | 像素级位置（插值目标） | 待确认/补 |
| FacingDirection | int | 朝向 | 待确认/补 |
| IsMoving | bool | 插值/动画提示 | 补 |
| State | string | Agent 状态（IDLE/FOLLOW/...） | 待确认 |
| Emotion | string | 情绪 | 待确认 |
| Health / MaxHealth | int | 血条 | 待确认/补 |
| Friendship | int | 好感度 | 待确认 |

`DialogueRequestMessage` 必须字段：`NpcName`、`PlayerInput`、`FromPlayerId`、`WorldSnapshotJson`（客机本地采集 worldSnapshot 序列化——TS 服务器 prompt 需要场景上下文；采集代码复用 `DecisionContextBuilder` 的 worldSnapshot 段，抽公共方法）。

`DialogueResponseMessage` 必须字段：`NpcName`、`Speech`、`ActionsJson`（ToolAction[] 序列化）、`Emotion`、`TargetPlayerId`。

`GiftRequestMessage`：`NpcName`、`ItemId`、`Quantity`、`FromPlayerId`。
`GiftResponseMessage`：`NpcName`、`ResponseText`、`Emotion`、`FriendshipChange`、`TargetPlayerId`。

- [ ] **Step 2: 协议版本字段**

所有消息 DTO 增加 `int ProtocolVersion = 1`。接收端版本不符 → 丢弃 + 日志（防 mod 版本漂移产生静默错位）。

- [ ] **Step 3: DTO 序列化往返测试（TestMod）**

`Func_MultiplayerSync` 追加断言：每种 DTO `Helper.Multiplayer.SendMessage` 序列化 → 自收 → 字段全等（SMAPI 消息走 System.Text.Json，float/enum/中文需无损）。

- [ ] **Step 4: Commit**

```bash
cd <REPO_ROOT>
git add src/ValleyAgent.Abstractions/Multiplayer/AgentSyncMessages.cs src/ValleyAgent.TestMod/Tests/Functional/Func_MultiplayerSync.cs
git commit -m "feat(mp): audit + complete sync DTOs, add ProtocolVersion envelope

Position/facing/isMoving fields for interpolation; worldSnapshotJson on DialogueRequest;
version field guards against mod version drift."
```

---

## Phase 2: 主机广播接线

### Task 2: MultiplayerEventRouter（联机事件统一入口）

**Files:**
- Create: `<REPO_ROOT>\src\ValleyAgent\Multiplayer\MultiplayerEventRouter.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Initialization\EventHandlerInitializer.cs`

- [ ] **Step 1: 创建 Router**

```csharp
namespace ValleyAgent.Multiplayer
{
    /// <summary>联机事件统一入口。HostMode 处理客机请求 + Peer 生命周期；
    /// ThinClientMode 处理主机快照/响应。两模式互斥注册。</summary>
    public class MultiplayerEventRouter
    {
        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private AgentSyncBroadcaster? _broadcaster;   // HostMode only
        private AgentRemoteRenderer? _renderer;       // ThinClientMode only
        private HostRequestHandlers? _requestHandlers; // HostMode only

        public void InitializeHost(AgentSyncBroadcaster broadcaster, HostRequestHandlers handlers);
        public void InitializeThinClient(AgentRemoteRenderer renderer);

        public void Register()
        {
            _helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
            _helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
            _helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
        }
        public void Unregister() { /* 对称反注册 */ }

        private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != "dandm1.ValleyAgent") return;
            // 按 e.Type 分发：
            // Host: DialogueRequest/GiftRequest/InteractionRequest → _requestHandlers
            // ThinClient: AgentState/FullSync/DialogueResponse/GiftResponse/NpcAction → _renderer.Handle*
            // 协议版本不符 → 丢弃 + LogLevel.Trace
        }

        private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
        {
            // Host only: 新 farmhand 加入 → FullSync 暖缓存
            if (!Context.IsMainPlayer) return;
            _broadcaster?.SendFullSync(e.Peer.PlayerID);
        }

        private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
        {
            // 无 per-peer 状态需清理（broadcaster 是广播模式），预留扩展点
        }
    }
}
```

- [ ] **Step 2: EventHandlerInitializer 注册**

`RegisterEventHandlers` 末尾追加（注意：联机事件与游戏循环事件不同，通过 Router 注册而非逐个 +=）：

```csharp
_multiplayerRouter = new MultiplayerEventRouter(_helper, _monitor);
// Host/Thin 的 Initialize 在 ModEntry 三模式分支中完成后再调 Register()
```

`UnsubscribeEvents` 对称加 `_multiplayerRouter?.Unregister();`

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent/Multiplayer/MultiplayerEventRouter.cs src/ValleyAgent/Initialization/EventHandlerInitializer.cs
git commit -m "feat(mp): add MultiplayerEventRouter for message/peer lifecycle routing"
```

---

### Task 3: Broadcaster 实例化 + 快照生产 + 节流驱动

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Initialization\ServiceInitializer.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Multiplayer\AgentSyncBroadcaster.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Initialization\EventHandlerInitializer.cs`（OnUpdateTicked）

- [ ] **Step 1: ServiceInitializer 注册（仅 HostMode 会走到）**

```csharp
var broadcaster = new AgentSyncBroadcaster(agentService, _monitor, _helper, ModEntry.Instance.ModManifest.UniqueID);
_container.RegisterSingleton(broadcaster);
```

- [ ] **Step 2: OnUpdateTicked 驱动**

`OnUpdateTicked` 末尾追加：

```csharp
// 联机：60 tick 节流快照广播（broadcaster 内部自带 ShouldRunAgentLogic && IsMultiplayer 守卫）
_multiplayerBroadcaster?.Update(_tickCounter);
```

- [ ] **Step 3: 快照生产（broadcaster 内部）**

`Update` 每 60 tick 对每个 Agent 组装 `AgentStateMessage`：
- `LocationName = npc.currentLocation?.NameOrUniqueName`
- `PositionX/Y = npc.Position.X/Y`（像素级）
- `FacingDirection = npc.FacingDirection`
- `IsMoving = npc.isMoving()`
- `State/Emotion/Health/MaxHealth/Friendship` 从 AgentInstance/AgentBrain/AgentHealth 读取
- 变化检测：与上一帧快照字段全等则跳过该 NPC（省带宽）；`ProtocolVersion` 填充

- [ ] **Step 4: Commit**

```bash
git add src/ValleyAgent/Initialization/ src/ValleyAgent/Multiplayer/AgentSyncBroadcaster.cs
git commit -m "feat(mp): wire AgentSyncBroadcaster into host tick loop with delta-checked snapshots"
```

---

### Task 4: 事件触发广播（状态/表情/说话/血量）

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\StateMachine\AgentStateMachine.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Core\AgentHealth.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Core\CommandExecutor.cs`

- [ ] **Step 1: 状态转换广播**

`AgentStateMachine` 已有 `OnStateChanged` 事件。在 `EventHandlerInitializer` 订阅处追加：`broadcaster` 立即发送该 NPC 的 `AgentStateMessage`（不等 60 tick 节流——状态切换是重要事件）。

- [ ] **Step 2: speak/emote 广播**

`CommandExecutor` 的 speak/emote/show_dialogue 命令执行成功后调用 `_broadcaster.BroadcastNpcAction(npcName, actionType, emoteId, text, durationMs)`（方法已存在 :166-188）。通过 ServiceContainer 注入，避免静态引用。

- [ ] **Step 3: 血量变化广播**

`AgentHealth.TakeDamage/Heal/Respawn` 内触发回调（事件已有 `OnDeath`，扩展 `OnHealthChanged`），broadcaster 发送更新快照。

- [ ] **Step 4: Commit**

```bash
git add src/ValleyAgent/
git commit -m "feat(mp): event-driven broadcasts for state change, npc actions, health"
```

---

## Phase 3: 客机渲染链路

### Task 5: Renderer 实例化 + 消息接入

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\ModEntry.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent.Abstractions\Multiplayer\AgentRemoteRenderer.cs`

- [ ] **Step 1: ThinClientMode 初始化（ModEntry.Entry 三模式分支，Task 9 详述）**

```csharp
// ThinClientMode:
var renderer = new AgentRemoteRenderer(Monitor);
_multiplayerRouter = new MultiplayerEventRouter(Helper, Monitor);
_multiplayerRouter.InitializeThinClient(renderer);
_multiplayerRouter.Register();
```

- [ ] **Step 2: Router 分发接通**

`OnModMessageReceived` 的 ThinClient 分支：`AgentState → renderer.HandleAgentStateMessage(e.ReadAs<AgentStateMessage>())` 等 5 种（Handle 方法已存在 :80-138）。

- [ ] **Step 3: 每 tick 驱动**

ThinClientMode 需要**最小** UpdateTicked 订阅（只驱动渲染器，不跑任何 Agent 逻辑）：

```csharp
// ThinClientMode only:
helper.Events.GameLoop.UpdateTicked += (_, _) => renderer.Update(tickCounter++);
```

- [ ] **Step 4: Commit**

```bash
git add src/ValleyAgent/ModEntry.cs src/ValleyAgent.Abstractions/Multiplayer/AgentRemoteRenderer.cs
git commit -m "feat(mp): wire AgentRemoteRenderer on farmhand thin client"
```

---

### Task 6: 位置插值引擎（客机观感核心）

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent.Abstractions\Multiplayer\AgentRemoteRenderer.cs`

**设计**：快照 1s 一次（60 tick），两帧之间插值平滑：
1. `HandleAgentStateMessage` 更新缓存时记录 `(targetPosition, targetLocation, facing, isMoving)`；
2. `Update` 每 tick：若 NPC 与目标位置同图且距离 ≤ 8 tiles → 按 NPC Speed（2 格/s）向目标**步进**（`Utility.getVelocityTowardPoint` + `isCollidingPosition` 逐轴碰撞检查，CustomCompanions 已验证的模式）；距离 > 8 tiles 或跨图 → 直接 `setTileLocation` 吸附（主机侧传送/旅行到达的合理反映）；
3. 朝向：静止时应用快照 `FacingDirection`；
4. `IsMoving=true` 且本地已到目标 → 保持最后朝向即可（下一快照会修正）。
5. **激活图约束**：只对 `GameLocation.IsActiveLocation()` 内的 NPC 应用位置写；shadow 图 NPC 跳过（写了也不同步且无意义）。

**观感说明**：主机侧"从入口走过来"（P1 Task 6A）经快照序列自然呈现为 farmhand 视角的"NPC 从入口走向玩家"，无需客机特殊处理。

- [ ] **Step 1: 实现插值（~150 行）**

`AgentRemoteRenderer` 新增：

```csharp
private readonly Dictionary<string, RemotePositionTarget> _positionTargets = new(StringComparer.OrdinalIgnoreCase);

// HandleAgentStateMessage 内：
_positionTargets[msg.NpcName] = new RemotePositionTarget(
    msg.LocationName, new Vector2(msg.PositionX, msg.PositionY), msg.FacingDirection, msg.IsMoving);

// Update 内（在既有 action 队列处理之前）：
foreach (var (npcName, target) in _positionTargets)
{
    var npc = Game1.getCharacterFromName(npcName);
    if (npc?.currentLocation == null || !npc.currentLocation.IsActiveLocation()) continue;
    if (npc.currentLocation.NameOrUniqueName != target.LocationName) continue; // 跨图由 vanilla warp 同步处理
    var dist = Vector2.Distance(npc.Position, target.Position);
    if (dist > 8 * 64) { npc.setTileLocation(target.Position / 64f); continue; }  // 吸附阈值 8 格
    if (dist < 4) { if (!target.IsMoving) npc.faceDirection(target.FacingDirection); continue; }
    var velocity = Utility.getVelocityTowardPoint(npc.Position, target.Position, npc.Speed);
    // 逐轴碰撞检查后应用（CustomCompanions 模式）
    ApplyAxisMovement(npc, velocity);
}
```

- [ ] **Step 2: 测试（TestMod，单机模拟）**

`Func_MultiplayerSync` 扩展：构造 renderer → Handle 一条伪造 AgentStateMessage（目标位置距 NPC 3 格）→ 驱动 Update N tick → 断言 NPC 位置逼近目标、无穿墙；距离 10 格时断言吸附。

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Abstractions/Multiplayer/AgentRemoteRenderer.cs src/ValleyAgent.TestMod/Tests/Functional/Func_MultiplayerSync.cs
git commit -m "feat(mp): client-side position interpolation with collision-aware stepping

1s snapshots smoothed at NPC speed; snap beyond 8 tiles; active-location guard."
```

---

## Phase 4: 交互代理链路（对话/送礼）

### Task 7: 对话传输抽象 + 客机代理

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Patches\DialogueBoxInputPatch.cs`
- Create: `<REPO_ROOT>\src\ValleyAgent\Multiplayer\FarmhandDialogueTransport.cs`
- Create: `<REPO_ROOT>\src\ValleyAgent\Multiplayer\HostRequestHandlers.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Patches\NPCDialoguePatch.cs`

**设计**：`DialogueBoxInputPatch.SubmitInput` 当前直连 `IAgentServerProvider`。抽象出 `IDialogueTransport`：
- `HostDialogueTransport`：现状（WebSocket → TS 服务器）。
- `FarmhandDialogueTransport`：`SubmitInput` → 本地采集 worldSnapshot（复用 C# 既有采集方法，抽公共）→ `DialogueRequestMessage` 经 `SendMessage(playerIDs: [MasterPlayer])` 发主机 → 等 `DialogueResponseMessage`（超时 60s，与主机同文案降级）。

渲染两侧一致：`setNewDialogue + Game1.drawDialogue`（客机由 renderer 的 `DequeueDialogueResponse` 在 Update 中消费 → 已在 P0 实现的渲染路径上汇合，或客机薄层直接调同一渲染函数）。

主机侧 `HostRequestHandlers.HandleDialogueRequest`：
```
读 DialogueRequestMessage → 反序列化 worldSnapshotJson
→ 走主机既有 GenerateDialogueAsync（TS 服务器，Registry 锁天然串行化 E4）
→ SendDialogueResponse(npcName, speech, actionsJson, emotion, targetPlayerId: req.FromPlayerId)
→ Actions 在主机侧由 CommandExecutor 执行（实体在主机）；
  speech 文本回客机渲染
```

**E4 多玩家同一 NPC**：主机串行处理；每个响应带 `TargetPlayerId` 只回请求者。对话历史是 NPC 单灵魂共享（双方玩家的话都会进入同一记忆——设计如此，README 写明）。

- [ ] **Step 1: IDialogueTransport 抽象 + Host 实现搬移**
- [ ] **Step 2: FarmhandDialogueTransport（ModMessage 收发 + 超时降级）**
- [ ] **Step 3: HostRequestHandlers.HandleDialogueRequest**
- [ ] **Step 4: NPCDialoguePatch 薄分支**（IsFarmhand → 仅当点击的是 Agent NPC 才拦截并开框；非 Agent NPC 放行原版）
- [ ] **Step 5: worldSnapshot 采集抽公共方法**（从 DecisionContextBuilder/DialogueBoxInputPatch 提取 `BuildWorldSnapshot(npc)` 静态方法，主机客机共用）
- [ ] **Step 6: Commit**

```bash
git add src/ValleyAgent/
git commit -m "feat(mp): farmhand dialogue proxied to host via ModMessage

Single-soul NPC memory on host; responses routed by TargetPlayerId; E4 serialized by TS registry lock."
```

---

### Task 8: 送礼代理

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Patches\NPCGiftPatch.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Multiplayer\HostRequestHandlers.cs`

**设计**：
- 客机送礼：`NPCGiftPatch.Prefix` 的 ThinClient 分支 → 不放行原版、不本地评估；发 `GiftRequestMessage{npcName, itemId, quantity, fromPlayerId}` → 本地先消费物品（与原版一致）→ 等 `GiftResponseMessage` → 显示 NPC 反应文本/表情。
- 主机处理：`HandleGiftRequest` → 走主机既有礼物评估（FriendshipSystem/GiftSystem + LLM 反应）→ `SendGiftResponse(..., targetPlayerId)`。
- **好感度口径**：Agent 好感度是 NPC 单灵魂全局值（与 vanilla 每玩家独立 friendshipData 不同）；客机玩家看到的好感度变化是全局变化。README 写明；vanilla 好感不受影响。

- [ ] **Step 1-4: 实现 + Commit**

```bash
git commit -m "feat(mp): farmhand gifting proxied to host evaluation"
```

---

## Phase 5: ModEntry 三模式初始化与生命周期

### Task 9: ModEntry.Entry 三模式分支

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\ModEntry.cs`

**背景**：P1 Task 12 的单一惰性早退演进为三模式。

- [ ] **Step 1: 实现分支**

```csharp
public override void Entry(IModHelper helper)
{
    // 模式判定（联机相关判断必须在最早时机，此时 Context 可用）
    if (!Context.IsMultiplayer || Context.IsMainPlayer)
    {
        _mode = AgentRuntimeMode.Host;          // 单机或联机主机
    }
    else
    {
        var hostMod = helper.Multiplayer
            .GetConnectedPlayer(Game1.MasterPlayer.UniqueMultiplayerID)
            ?.GetMod(ModManifest.UniqueID);
        _mode = hostMod == null
            ? AgentRuntimeMode.Inert             // 主机没装 → 惰性
            : AgentRuntimeMode.ThinClient;       // 主机装了 → 薄客户端
        // Major 版本不一致 → Inert + 警告
        if (_mode == AgentRuntimeMode.ThinClient && hostMod!.Version.MajorVersion != ModManifest.Version.MajorVersion)
        {
            Monitor.Log($"ValleyAgent disabled: host version {hostMod.Version} incompatible with local {ModManifest.Version}.", LogLevel.Warn);
            _mode = AgentRuntimeMode.Inert;
        }
    }

    switch (_mode)
    {
        case AgentRuntimeMode.Host:
            // 现状全量初始化（ServiceContainer → ServiceInitializer → EventHandlerInitializer）
            // + Phase 2 的 broadcaster 接线
            break;
        case AgentRuntimeMode.ThinClient:
            // 最小初始化：AgentRemoteRenderer + MultiplayerEventRouter + 薄补丁 + 最小 UpdateTicked
            // 不创建 ServiceContainer/ServerProcessManager/AgentService/TS 连接
            break;
        case AgentRuntimeMode.Inert:
            // P1 现状：日志 + return（GetApi null）
            break;
    }
}
```

注意：联机时 `Context.IsMultiplayer` 在 `Entry` 时可用；但**主机身份判断在 save 加载前**对 split-screen 需复核——分屏副屏的 `IsMainPlayer=false` 会落 Inert，符合 P1 设计。

- [ ] **Step 2: 薄补丁注册**

ThinClientMode 的 Harmony 补丁只打需要的：NPCDialoguePatch（点击拦截→代理）、NPCGiftPatch（送礼→代理）、DialogueBoxInputPatch（输入 UI + 代理传输）。SocialPagePatch **不打**（记忆宫殿是主机数据，客机不可见——v1 决策）。
补丁方法内的 farmhand 守卫（P1 Task 13）改为模式判断：ThinClient 走代理分支、Inert 直接放行。

- [ ] **Step 3: 生命周期**

- `ReturnedToTitle`：ThinClient 清理（renderer.Clear() + Router.Unregister()）。
- `PeerDisconnected`：Router 内预留（当前无 per-peer 状态）。
- 客机 save：不写任何 SaveData（P1 已保证）。

- [ ] **Step 4: Commit**

```bash
git add src/ValleyAgent/ModEntry.cs src/ValleyAgent/Patches/
git commit -m "feat(mp): three-mode runtime init (Host/ThinClient/Inert) with host version check"
```

---

## 测试与验收

### TestMod（单机可模拟的部分）

| 测试 | 验证点 |
|------|--------|
| Func_MultiplayerSync（扩展） | DTO 序列化往返；Renderer 插值（3 格步进/10 格吸附）；Broadcaster 60 tick 节流与变化跳过 |
| 回归 | 单机全部既有测试（HostMode == 原行为）必须全绿 |

### 双端手动验收 checklist（双机或双实例）

- [ ] 主机开档：Agent 正常工作（同单机）
- [ ] farmhand（主机有 mod）：薄模式启动日志；与 Agent 同图时看到 NPC 走动平滑无橡皮筋；血条/表情/气泡可见
- [ ] farmhand 点击 Agent NPC：开框 → 输入 → 2-5s 收到回复（走主机 LLM）；同时主机玩家也能独立对话同一 NPC（E4 串行）
- [ ] farmhand 送礼：NPC 反应文本到达；主机的 SocialPage 记忆宫殿可见该次送礼记录（单灵魂）
- [ ] farmhand 与 Agent 不同图：看不到 Agent（active-location 约束，README 已写明）
- [ ] 新 farmhand 中途加入：收到 FullSync，血条/好感立即可见
- [ ] farmhand（主机无 mod）：惰性日志；完全原版体验；`ValleyAgent_status` 不存在
- [ ] 版本不一致 farmhand：惰性 + 警告日志
- [ ] 主机存档读写正常；farmhand 本地无 ValleyAgent save 数据

### 性能红线

- 快照广播：60 tick + 变化跳过 → 常态每 NPC 每分钟 ≤1 条消息；10 Agent × 4 farmhand 带宽可忽略
- 客机 UpdateTicked 增量 < 0.5ms（插值循环 ≤10 NPC）
- 对话代理延迟 = 主机 LLM 延迟 + 1 次 ModMessage 往返（局域网 <50ms）

---

## 风险与缓解

| 风险 | 缓解 |
|------|------|
| 1s 快照下快速移动滑行感 | Task 6 插值步进 + IsMoving 提示；实测后可降节流至 30 tick（broadcaster 参数化） |
| 双端测试成本高 | TestMod 单机模拟覆盖 DTO/插值/节流；双端 checklist 手动验收；Autopilot 双实例控制作为后续自动化候选 |
| 主机 mod 崩溃导致客机等待超时 | FarmhandDialogueTransport 60s 超时降级文案（与主机同款）；PeerDisconnected 时清理 pending |
| vanilla 与自定义位置同步打架（vanilla 也低频同步 NPC 位置） | 插值目标以自定义快照为准；vanilla 低频快照会被下一自定义快照修正；实测观察是否需 patch 屏蔽 |
| 客机 shadow 图 NPC 写位置无效 | IsActiveLocation() 守卫（Task 6） |

---

## 待用户决策的开放问题

1. **快照节流**：默认 60 tick（1s）。快速移动多的场景建议 30 tick——先按 60 交付，实测后调。如需直接 30 请指示。
2. **客机 SocialPage 记忆宫殿**：v1 不对客机开放（数据在主机）。如需客机可见（需再开一条记忆查询代理链路）请指示，将追加 Task。
3. **客机听到 Agent 全局聊天消息**（chatBox）：v1 仅对话/气泡/表情同步；Agent 主动 speak 的 chatBox 消息客机不可见。如需同步（NpcActionMessage 加 chat 类型）请指示。
