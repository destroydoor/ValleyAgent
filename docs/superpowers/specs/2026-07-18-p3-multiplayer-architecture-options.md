# P3 联机架构方案选型 — 多方案对比设计文档

> **创建时间**: 2026-07-18
> **状态**: 方案选型（待用户决策）
> **前置**: FINDINGS.md（E1-E4 联机、D4 远端冻结、Q1/Q2 架构问答）、`2026-07-18-findings-fix-design.md`（旧 spec 第 7.2 节曾预定"双实体"路线）
> **本文目的**: 用 2026-07-18 最新代码审计 + 外部调研证据，给出 P3 的多套可选方案、工作量与风险对比，供用户拍板。**旧 spec 的"双实体"预设在证据更新后需要修正，见 §7 结论。**

---

## 0. 决策摘要（TL;DR）

| 方案 | 一句话 | 联机能力 | 工作量 | 风险 | 推荐度 |
|------|--------|---------|--------|------|--------|
| **A1 运行时影子 Farmer** | 可见 NPC + 隐形 Farmer 子类提供 Farmer-only API（工具/战斗/钓鱼） | ❌ 不解联机（影子非 net-backed，无免费同步） | 大 | 中（有生产先例） | 按需 |
| **A2 net-backed 假农场主** | 占 cabin 槽位的持久化假玩家 | ✅ 理论最优（免费同步+地图激活） | 很大 | **高（零先例，存档风险）** | ❌ 不建议现在做 |
| **A3 独立 bot 客户端** | 第二个游戏实例作 farmhand 跑 Agent | ✅ 真·vanilla 同步 | 大 + 运维成本 | 中 | 小众 |
| **B 主机权威 + ModMessage** | 接线已有死代码（AgentSyncBroadcaster/Renderer），主机跑全部逻辑，客机收快照+代理请求 | ✅ 实用解（Pathoschild 范式） | **中** | 低-中 | ✅ 联机首选 |
| **C 纯单机声明** | P1 的 E2 止血 + 官方声明不支持联机 | ❌（明确放弃） | 极小（P1 已含） | 极低 | ✅ 当前默认 |
| **D 分阶段混合** | C 现在 → B 联机需求出现时 → A1 仅在 Farmer-API 痛点致命时 | 渐进 | 渐进 | 低 | ✅ **推荐** |

**推荐：D（分阶段）**。理由见 §7。核心证据反转：FINDINGS Q1 假设"换 Farmer 后 vanilla 免费给跨图同步+远端地图激活"——调研证实该福利**只对 net-backed Farmer（cabin 槽/真实连接玩家）成立**；唯一有生产先例的影子 Farmer（StardewValley-MCP `BotFarmer`）刻意不 net-back，因而**没有**免费同步。隐藏 Farmer 路线解决的是"Farmer-only API"痛点，不是联机。

> **✅ 2026-07-18 用户拍板**：采纳 D 路线；**方案 B 立即立项**（实施计划：`docs\superpowers\plans\2026-07-18-multiplayer-sync-host-authoritative.md`）；IAgentEntity 不单独立项；A2 spike 验收三条通过；D4 写明为设计限制。

---

## 1. 问题重述

| 编号 | 问题 | 现状 |
|------|------|------|
| E1 | 联机同步基础设施全是死代码 | `AgentSyncBroadcaster`（5 方法）/ `AgentRemoteRenderer`（11 方法）/ 8 种消息 DTO 已写好，**生产代码零实例化**（仅 `Func_MultiplayerSync.cs` 测试引用） |
| E2 | 每个 farmhand 跑完整 mod | 无守卫；各自自启服务器（端口 8765 竞态互杀）、各自 LLM 决策烧 token、记忆分裂 → **P1 计划已设计止血**（`2026-07-18-p1-scene-multiplayer-guards.md` Phase 4） |
| E3 | 客机写 `npc.controller` 被主机覆盖 | NPC `controller`/`position` **不是 net field**（反编译确认），仅 `isMovingOnPathFindPath`（NetBool）广播 → 客机写入本地有效、主机不可见，视角橡皮筋 |
| E4 | 多玩家同时对话同一 Agent | 各自开框、各自 Python/TS 服务器、状态恢复互相覆盖 |
| D4 | NPC 不与玩家同图就不干活 | 换图强制 IDLE。单机下非激活地图不全帧模拟（PathFindController 不推进）——**这是引擎约束，单机任何方案都无法让远端地图活起来**（联机时"每个在线 Farmer 所在地图激活"是联机专属机制） |

---

## 2. 2026-07-18 调研新证据

### 2.1 外部事实（SDV 1.6 联机机制，一手来源）

| # | 事实 | 来源 |
|---|------|------|
| F1 | farmhand 只接收**激活位置**的同步（自己所在图 + 农场 + 农舍 + 温室）；其余位置是 shadow 副本，改动不同步 | stardewvalleywiki.com Modder Guide/Game Fundamentals |
| F2 | Farmer 的 position/facing/动画/背包/血条/坐骑/`hidden` 等走 net fields **自动同步**；`controller`（PathFindController）**不是** net field | Dannode36/StardewValleyDecompiled Farmer.cs |
| F3 | NPC 的 position/controller **不是** net field；只同步日程默认点、对话状态、`isMovingOnPathFindPath` 标志 → **客机永远驱动不了 NPC 寻路，只有主机能** | WeDias/StardewValley NPC.cs initNetFields |
| F4 | 主机权威范式成熟：`Context.IsMainPlayer` 门控 + `Helper.Multiplayer.SendMessage(..., playerIDs: [Game1.MasterPlayer.UniqueMultiplayerID])` 转发 + `ModMessageReceived` 接收（TractorMod/Automate/ChestsAnywhere 全系 Pathoschild 这么做） | Pathoschild/StardewMods |
| F5 | "主机 Farmer 当 bot 藏起"有三个独立生产 mod 家族（Always On Server / SMAPIDedicatedServerMod / AutoHideHost）；**运行时 new 第二个 Farmer 做持久化玩家则无先例** | 各 mod repo |
| F6 | **运行时影子 Farmer 有唯一直接先例**：StardewValley-MCP `BotFarmer : Farmer`（amarisaster）——`draw()` 置空隐形、驱动 `weapon.DoDamage`/`tool.DoFunction` 等真 API、**刻意不加入 `Game1.otherFarmers`（`Multiplayer.updateRoots()` 对非 net-backed farmer 空引用崩溃）**、`UniqueMultiplayerID = helper.Multiplayer.GetNewID()`、日始/2AM 必须 `WakeUp`/`SignalSleepReady` 防睡眠死锁 | github.com/amarisaster/StardewValley-MCP（BotFarmer.cs:14-89, CompanionFarmer.cs:35-67, BotManager.cs:171-207） |
| F7 | "joystick"驱动（每 tick 直接写速度向量）先例：`Utility.getVelocityTowardPoint(pos, target, speed)` + 逐轴 `isCollidingPosition` 碰撞检查（Floogen/CustomCompanions Companion.cs:561-584）——对 Farmer(Character) 子类同样适用 | github.com/Floogen/CustomCompanions |
| F8 | 存档结构：每个 cabin 一个 `<farmhand>` 元素；无 cabin 的 Farmer 无持久化位置；自定义 Farmer 子类需替换 save serializer（**不要子类化要持久化的 Farmer**） | Chucklefish 论坛 + save-editor 项目 |
| F9 | 现代 companion mod（The Stardew Squad, Nexus 35341, 2026 活跃）纯 NPC 路线，联机兼容是专门一个版本（v0.12.0）才补上的——佐证"NPC 路线联机不是免费的" | Nexus/Isalda |

### 2.2 内部事实（代码审计）

| # | 事实 | 出处 |
|---|------|------|
| I1 | 生产代码 **50 个文件**含 NPC 类型签名（ValleyAgent 35 + Abstractions 15）；FINDINGS"43+"确认且偏保守 | 全库审计 |
| I2 | 三个关键 seam：`IAgentState.Update(NPC,...)`（5 handler + IdleState 同签名）、`IMovementService`（11 方法中 8 个吃 NPC）、`IAgentCommand.Execute(NPC,...)`（19 命令类同签名） | IMovementService.cs / IAgentState.cs / IAgentCommand.cs |
| I3 | 热点：Handlers 层 133 处/6 文件；`EventHandlerInitializer.cs` 61 处；`AgentTickLoop.cs` 30 处；Commands 31 处/7 文件 | 审计 |
| I4 | **已解耦**：Brain 全模块（AgentBrain/Emotion/Memory）、AgentInventory、AgentHealth、AgentState 枚举、`IAgentController` 接口、WebSocket/Protocol/Config/Validation 全部、`IValleyAgentApi`（纯 string，零 NPC 泄漏） | 审计 |
| I5 | PathFindController 生产构造点**仅 1 处**（MovementService.cs:298，单一所有权已成立） | 审计 |
| I6 | 联机骨架已存在且设计完整：8 种消息 DTO（5 主机→客机 + 3 客机→主机）、Broadcaster 60 tick 节流、Renderer 缓存+队列；`MultiplayerHelper.ShouldRunAgentLogic` 就绪 | Multiplayer/*.cs |
| I7 | TS 服务器（ValleyAI）单实例在主机运行即可天然解决"一个 NPC 一个灵魂"；`StardewAgentRegistry` 同 NPC 对话锁已存在（E4 序列化基础） | ValleyAI 探查 |

---

## 3. 方案 A：隐藏 Farmer 实体（三个变体）

FINDINGS Q1 的原始设想。调研后必须拆成三个本质不同的变体。

### A1 运行时影子 Farmer（StardewValley-MCP 模式）

**形态**：保留可见 vanilla NPC（社交/好感/对话/checkAction 全保留），配一个隐形 `BotFarmer : Farmer` 实例（不入 `otherFarmers`、不持久化）驱动 Farmer-only API（`weapon.DoDamage` 不再借道 Game1.player、工具使用、钓鱼、吃食物回血）。

**解决什么**：Q1 阻碍点 4（Farmer-only API 借道）、战斗/工具动画原生、怪物仇恨/碰撞对 Farmer 天然生效。
**不解决什么**：**联机同步（影子非 net-backed，farmhand 看不到它）；远端地图激活（单机引擎约束，D4 依旧）**。

**前置依赖**：`IAgentEntity` 抽象（§8）把 50 文件的 NPC 耦合收敛到接口后，才能实现 `FarmerEntity` 而不重写整个执行层。

**先例**：StardewValley-MCP（F6）——坑位清单完整（draw 置空/otherFarmers 禁入/GetNewID/睡眠生命周期/eatObject 崩溃回退）。

**工作量**：大。IAgentEntity 抽象（50 文件，3 seam + 机械替换 + TestMod 52 文件适配）+ BotFarmer 实现（有先例代码可借鉴）+ 点击交互自建（Farmer 不触发 `NPC.checkAction`，需点击检测补丁）+ 双实体映射层（隐藏 NPC 保社交）。

**风险**：中。先例存在但规模小（单 companion）；多 Agent 并发放大睡眠/事件边界；`eatObject` 等 UI 耦合 API 需逐个回退处理。

### A2 net-backed 假农场主（占 cabin 槽）

**形态**：用真实 cabin 槽位创建持久化假玩家（`isUnclaimedFarmhand` 机制），Agent 驱动它。理论上拿到 F2 全部免费同步 + 联机地图激活。

**致命问题**：
- **零先例**（F5/F8）——没有任何公开 mod 做过；
- 占玩家槽位（默认上限 7）；
- 存档耦合（无 cabin 的 Farmer 无持久化；子类化需替换 serializer）；
- 联机事件/节日/睡眠/升级菜单会抓住这个"玩家"（F5 生产痛点：dedicated server  mods 大量代码在对抗这些）；
- `Game1.player`/事件流假设单主玩家的逻辑可能全面踩雷。

**结论**：理论最优、实操高风险探索项。**不建议作为当前路线**；若未来联机是硬需求且 B 方案体验不达标，以 2-3 天 spike 形式验证（验证标准：存档往返不损坏 + farmhand 看到位置同步 + 事件不卡死）。

### A3 独立 bot 客户端（真·第二个游戏实例）

**形态**：另一台/另一个进程跑 SDV 作为 farmhand 连接，bot 客户端上的 mod 驱动这个真 farmhand 执行 Agent 逻辑。免费获得一切 vanilla 同步与地图激活。

**代价**：每个 bot 一个游戏实例（内存/CPU/账号）；部署复杂度（JunimoServer/docker 模式）；本质上把问题转移成"bot 客户端的输入驱动"（Autopilot 已有技术储备）。

**适用**：dedicated-server 小众场景。**不作为主线路径**。

---

## 4. 方案 B：主机权威 NPC + ModMessage 同步（联机实用解）

**形态**：完全沿用现有 NPC 执行层。主机（`Context.IsMainPlayer`）跑全部 Agent 逻辑 + 唯一 TS 服务器（I7）；客机 mod 惰性执行层（P1 E2 止血已做），通过联机消息获得表现能力。

**数据流**：

```
主机: AgentTick/Handlers 驱动 NPC（同单机）
  → AgentSyncBroadcaster.Update（60 tick 节流，已有）
  → AgentStateMessage（位置/状态/情绪/血量快照）──ModMessage──▶ 客机
客机: AgentRemoteRenderer.HandleAgentStateMessage（已有）→ 插值渲染/气泡/血条

客机玩家点击 NPC: NPCDialoguePatch（客机薄补丁）
  → DialogueRequestMessage ──ModMessage──▶ 主机
主机: 转 TS 服务器 dialogue（Registry 锁天然序列化 E4）
  → DialogueResponseMessage ──ModMessage──▶ 该客机渲染
```

**现状基础（I6）**：8 种 DTO 已定义（AgentState/FullSync/DialogueResponse/GiftResponse/NpcAction + DialogueRequest/GiftRequest/InteractionRequest）；Broadcaster/Renderer 类完整未接线。需要新写的：
1. 实例化与事件接线（`ModMessageReceived`/`PeerConnected`/`PeerDisconnected`）；
2. 快照节流发送（Broadcaster.Update 已设计 60 tick）；
3. **客机渲染插值**（1s 快照间平滑移动——Renderer 当前只 apply emote/dialogue，位置插值需新写 ~100-200 行）；
4. 客机请求→主机执行→回包的 3 条代理链路（DTO 已有，处理器新写）；
5. `PeerConnected` 时 `SendFullSync` 暖缓存（方法已有）；
6. 客机薄交互补丁（点击检测 → 发请求，不本地开框）。

**E3 解法**：客机永不写 `npc.controller`（P1 守卫已保证）；移动表现全靠主机快照+客机插值。
**E4 解法**：所有对话走主机 TS 服务器，Registry 同 NPC 锁串行；对话状态恢复由主机广播。

**工作量**：中。骨架已存在，估计 ~1500-2500 行新代码 + 联机测试基建（双开 SDV 或分机）。最大不确定性是插值手感与双端测试成本。

**风险**：低-中。Pathoschild 全系验证过的范式（F4）；无引擎级未知。

---

## 5. 方案 C：纯单机声明

P1 的 E2 止血（farmhand 完全惰性 + GetApi null + 补丁 belt-and-braces）+ manifest/README 声明"联机时仅主机生效，客机 mod 自动禁用"。这就是 Pathoschild 多数 mod 的长期形态（Automate 等）。

**工作量**：极小——已含在 P1 计划 Phase 4。
**代价**：联机玩家中 farmhand 无 Agent 体验（但主机玩家正常；且不会像现在这样直接坏档/烧 token）。

---

## 6. 方案 D：分阶段混合（推荐）

| 阶段 | 内容 | 触发条件 | 交付 |
|------|------|---------|------|
| D-0（随 P1 落地） | 方案 C | 立即 | 联机不坏档，单机完整体验 |
| D-1 | 方案 B 联机同步 | 联机需求真实出现（用户要求/发布前） | farmhand 可见 Agent 表现 + 可对话 |
| D-2 | 方案 A1 影子 Farmer spike | Farmer-only API 痛点致命化（战斗借道 Game1.player 频繁出 bug、工具/钓鱼做不了）**且** IAgentEntity 抽象有独立收益（handler 可测试性）时 | Farmer 机制能力；不解联机 |
| D-3 | A2 spike | B 的联机体验被证实不达标（插值橡皮筋无法接受）且联机是核心卖点时 | 2-3 天可行性验证，不过则弃 |

**为什么这个顺序**：
1. C 几乎免费且 P1 已含；
2. B 站在已有 1500+ 行设计完成的骨架上，是联机性价比最高的真解；
3. A1 被证据重定位为"机制能力增强"而非"联机解"——只有当伤害/工具借道真的痛时才值回 50 文件重构的票价；
4. A2 保持为可触发的探索项，不预支高风险。

---

## 7. 与旧 spec 的偏差声明

`2026-07-18-findings-fix-design.md` §1/§7.2 曾预定"P3 实体策略：双实体（隐藏 NPC + Farmer 外壳）"、"实施顺序 P0 → P3 → P1/P2"。本文证据修正如下：

| 旧预设 | 新证据 | 修正 |
|--------|--------|------|
| 双实体 Farmer 外壳一次性解决 E1-E4 + D4 | F2/F6：免费同步只对 net-backed Farmer 成立；生产先例（BotFarmer）刻意不 net-back；A2 零先例高风险 | 双实体**不是**联机的免费解；联机走 B |
| 先做 P3 再做 P1/P2 | P1（D1/D2/E2/B2）不依赖实体抽象，在现有架构即可修复且用户价值即时 | 顺序改为 P0 → P1 → P2 →（按触发条件）B/A1 |
| IAgentEntity 是 P3 第一步（43 点收敛） | I1-I4：耦合面比预想小（3 seam + 机械替换），且 Brain/公 API 已解耦；抽象本身不产生用户价值 | IAgentEntity 降级为 A1 的前置依赖，不单独立项（§8） |

---

## 8. IAgentEntity 抽象 — 正交分析

**它是什么**：把 `NPC` 类型收敛到接口后（位置/朝向/说话/emote/动画/移动子系统），执行层对"实体"编程而非对"NPC"编程。

**成本（基于 I1-I3 审计）**：50 生产文件 + 52 TestMod 文件。三个高杠杆 seam（IAgentState.Update / IMovementService / IAgentCommand）改一处 cascading 一片；其余为机械参数替换。规模估计：3-5 天专注重构 + 全量回归。

**独立收益（不做 Farmer 也成立的）**：
- handler 可测试性（TestMod 可对 fake entity 断言，不依赖游戏实例）；
- 消灭 8 个 `npc.controller` 写入点的隐性契约（I5 已单一所有权，收益有限）；
- 代码卫生。

**结论**：**不单独立项**。收益是工程卫生级别，不值 3-5 天 + 全量回归风险；作为 A1 的组成部分实施时才做（那时它是必要前置）。若团队决定长期深耕本项目，可在 B 落地后的平稳期择机做。

---

## 9. 风险登记

| 风险 | 影响方案 | 缓解 |
|------|---------|------|
| 联机快照插值手感差（1s 节流下 NPC 滑行/瞬移） | B | 参考 F9（Stardew Squad v0.12 已趟过）；首版可 30 tick 节流 + 到达点吸附；实测调参 |
| 双端联机测试成本高（需双 SDV 实例） | B | TestMod 现有 `Func_MultiplayerSync` 骨架 + Autopilot 双开脚本；或局域网双机手动 checklist |
| BotFarmer 睡眠/事件死锁边界 | A1 | F6 坑位清单（WakeUp/SignalSleepReady/2AM）；spike 先验证 3 天生命周期 |
| A2 存档损坏 | A2 | 只在独立测试存档 spike；不过验收门即弃 |
| P1 E2 止血后 farmhand 体验缺失被社区差评 | C | README/manifest 明示"联机仅主机"；B 排期回应 |

---

## 10. 待用户决策 — ✅ 已于 2026-07-18 全部拍板

1. **总体路线**：✅ **采纳 D**（C→B→A1/A2 触发式），放弃旧 spec 双实体优先。
2. **B 的排期**：✅ **立即立项**（用户原话"联机立马就要做"）→ 实施计划已出：`docs\superpowers\plans\2026-07-18-multiplayer-sync-host-authoritative.md`。
3. **IAgentEntity**：✅ **不单独立项**（用户原话"不算"），作为 A1 的前置随 A1 启动。
4. **A2 spike 验收标准**：✅ 三条通过（存档往返无损 + farmhand 位置同步可见 + 节日不卡死）。
5. **D4 的产品口径**：✅ 维持现状，README 写明为设计限制；"离屏工作抽象结算"作为 P4 候选另立文档。
6. **附加输入**：到达观感要求（从入口走过来、方向与来路一致、传送仅卡死兜底）已落入 P1 计划 Task 6A，同样适用于 B 方案客机渲染侧的观感校准。

---

## 附录：关键引用

**外部**：
- StardewValley-MCP BotFarmer 模式：github.com/amarisaster/StardewValley-MCP（`smapi-mod/BotFarmer.cs:14-89`、`CompanionFarmer.cs:35-67,74-119,312-335`、`BotManager.cs:171-207,356-379`）
- Joystick 驱动：github.com/Floogen/CustomCompanions（`Companion.cs:561,571-584`）
- 主机权威范式：github.com/Pathoschild/StardewMods（TractorMod/ModEntry.cs:186-200,568-576；Automate/ModEntry.cs:37-42）
- 联机机制：stardewvalleywiki.com/Modding:Modder_Guide/APIs/Multiplayer + /Game_Fundamentals（net fields、active locations）
- 反编译：Dannode36/StardewValleyDecompiled（Farmer.cs/FarmerTeam.cs）、WeDias/StardewValley（NPC.cs initNetFields、`temporaryController [XmlIgnore]`）
- 现代 NPC companion 联机先例：Nexus 35341 The Stardew Squad（v0.12.0 联机兼容）

**内部**：
- 死代码骨架：`src\ValleyAgent\Multiplayer\AgentSyncBroadcaster.cs`、`src\ValleyAgent.Abstractions\Multiplayer\{AgentRemoteRenderer,AgentSyncMessages,MultiplayerHelper}.cs`
- 耦合审计：50 生产文件；3 seam（IAgentState/IMovementService/IAgentCommand）；热点 Handlers 133、EventHandlerInitializer 61、AgentTickLoop 30
- 已解耦：Brain 全模块、IValleyAgentApi（纯 string）、IAgentController、WebSocket 层
- P1 止血设计：`docs\superpowers\plans\2026-07-18-p1-scene-multiplayer-guards.md`
