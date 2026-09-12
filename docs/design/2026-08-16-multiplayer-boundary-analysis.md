# 多人联机：异常边界与约束分析（需求与限制先行）

> **Created:** 2026-08-16
> **性质:** 需求与约束分析文档——**不含任何实施承诺**。用户指示"先弄明白需求和限制再行事"：本文档盘点多人联机的全部异常边界情况与硬约束，列出需要拍板的产品决策，给出建议分期。确认后再另立执行计划。
> **前置文档:**
> - `docs/superpowers/plans/2026-07-18-multiplayer-sync-host-authoritative.md`（主机权威 + SMAPI ModMessage 架构，已落地）
> - `docs/superpowers/specs/2026-07-18-p3-multiplayer-architecture-options.md`（当时的选型论证）
> - `docs/design/2026-08-15-ts-ledger-reflex-architecture.md`（账本迁移——新经济链路的来源，迁移时未做联机适配）
> **产生方式:** 双仓库联机现状探索（C# 侧 + TS 侧两个专项盘点）+ 新经济链路逐点核验，全部结论带文件:行证据。

---

## 0. 一句话结论

**C# 侧联机骨架 2026-07-18 已建成且基本可用；TS 端"玩家"维度物理上不存在（七类数据单份）；新经济链路有 4 处已证实的"错人"（全部硬编码主机玩家）。** 联机适配 = 把已经在 C# 中继协议里存在的 `PlayerId` 接进 WS 协议与 TS 端，并把 TS 端单份数据结构改为按玩家分维度。工程量主要在 TS 端上下文管理，不在网络层。

---

## 1. 现状盘点

### 1.1 C# 侧：骨架完备（已落地）

| 件 | 状态 | 证据 |
|---|---|---|
| 三模式运行时 Host / ThinClient / Inert | ✅ SaveLoaded 时判定：单机或主机→Host；分屏副屏→Inert；远程房客→ThinClient（主机 mod 版本 Major 不齐→Inert）；`VALLEY_TEST_INSTANCE` 环境变量可强制 | `ModEntry.cs:284-342` |
| ModMessage 中继协议（8 消息 + ProtocolVersion） | ✅ **房客请求已带 `PlayerId`（=UniqueMultiplayerID）、主机定向回包已带 `TargetPlayerId`** | `AgentSyncMessages.cs:103,117,128,140,149`；`FarmhandDialogueTransport.cs:57` |
| 对话/送礼两条请求-响应中继链 | ✅ C1（房客右键对话）/ C2（房客送礼走 transport）/ C3（主机代执行 actions）均已修 | `HostRequestHandlers.cs:46-118`、`MultiplayerEventRouter.cs:82-150` |
| 状态广播 / FullSync / 位置插值渲染 | ✅ 60 tick 节流广播；房客中途加入 FullSync | `AgentSyncBroadcaster.cs:19,82,277`；`MultiplayerEventRouter.cs:156-166` |
| 双实例 E2E harness | ✅ 同目录双开（SMAPI 自动分日志 `SMAPI-latest.player-2.txt`）+ `va_mp_host`/`va_mp_join` 程序化开服/入服 + C1/C2/C3 命令驱动 + ffmpeg 录像 | `scripts/test/run_farmhand_e2e.ps1`、`MultiplayerSetupCommands.cs`（497 行） |
| 房客不产生第二套 WS/brain | ✅ ThinClient 不创建 EventHandlerInitializer / WS 客户端 | `ModEntry.cs:411-463` |

**房客对话的完整现行调用链**：房客右键 Agent → 本地开 DialogueBox → 输入 → `WorldSnapshotBuilder.Build`（房客视角）→ `FarmhandDialogueTransport`（ModMessage 定向发主机，60s 超时）→ 主机 `HandleDialogueRequest`：反序列化房客快照（失败才主机重建）→ 主机 TS LLM → 主机 `CommandExecutor` 代执行全部 actions → broadcaster 定向回包 → 房客渲染。

### 1.2 TS 端：玩家维度物理不存在

`packages/stardew/src` + `protocol/messages.json` 全文 grep `playerId|farmerId|userId|multiplayer|farmhand` **零命中**。玩家是匿名单例（"the farmer"/"农场主"/`Game1.player`）：

| # | 数据 | 现状 | 证据 |
|---|---|---|---|
| 1 | WS 协议 | 无任何消息带玩家身份；`adjust_result.playerMoney` 注释明写"Game1.player.Money 镜像" | `messages.json:919,975-978` |
| 2 | 对话历史 | 单份 `conversationHistory`，`role:"player"` 统一渲染"农场主" | `agent-memory.ts:34,140` |
| 3 | 好感度 | 单标量 `friendship`，直接驱动人设阶段选择与态度简报 | `agent-memory.ts:23`；`npc-prompt-loader.ts:55-77` |
| 4 | L1 记忆 | 无玩家归属；`remember` 工具 `relatedNpcs` 硬编码 `["Farmer"]` | `stardew-tools.ts:360` |
| 5 | 玩家画像 | SQLite **单行表 `CHECK (id=1)`**，注释"one player, one row" | `player-profile-store.ts:25,37,50-57` |
| 6 | 活动日志/导演 | `DailyActivity` 无玩家归属；`PlayerActionTracker` 只采样 `Game1.player`（仅主机侧存在） | `narrative-types.ts:50-66`；`PlayerActionTracker.cs:99-101` |
| 7 | prompt 模板 | "农场主"单数假设遍布对话/beat 模板（称呼、钱包、手持物、画像摘要） | `prompt-builder.ts:28-62,81-126` |

NPC 侧的身份建模（npcName 键控的 registry/ledger/lock/emotion）是完备的，可直接复用——**缺的只是玩家这一整条维度**。

### 1.3 新经济链路的 4 处"错人"（已逐点核实）

| # | 链路 | 现状行为 | 证据 |
|---|---|---|---|
| 1 | **交易** | `execute_adjust` 无 playerId；AdjustExecutor 的 `player` target 一律 `Game1.player`（主机）⇒ **房客交易扣主机的钱/物，房客物品不动** | `AdjustExecutor.cs:383,419`；协议 `messages.json:919` |
| 2 | **跟随** | FOLLOW/GoalExecutor 全程 `Game1.player`（位置判定、移动、warp）⇒ **房客让 NPC 跟随，NPC 去跟主机** | `GoalExecutor.cs:290-353` |
| 3 | **好感度（对话）** | 主机对话 delta 应用到 `Game1.player.friendshipData`（主机玩家）；**房客对话的 LLM 好感度 delta 被整个丢弃**——`DialogueResponseMessage`（主机→房客回包）没有好感度字段，送礼路径有（`GiftResponseMessage` 带 FriendshipDelta+TargetPlayerId，房客本地应用）、对话路径没有 | `DialogueManagementApi.cs:144`；`AgentSyncMessages.cs:110-118` vs `:117,140` |
| 4 | **求购单** | 房客送礼命中 NPC 当日求购单不拦截（ThinClient 分支跳过检查 + 主机 `HandleGiftRequest` 走 HostGiftTransport 也不查 `PurchaseOffers`）⇒ **白送消耗物品** | `NPCGiftPatch.cs:95-101,119`；`HostRequestHandlers.cs:163-195` |

---

## 2. 异常边界情况清单

> 按"触发条件 → 现状行为 → 后果 → 严重度"组织。严重度：🔴 破坏体验/数据错乱；🟡 体验退化但可解释；🟢 可接受/已有兜底。

### A. 身份串味（TS 上下文管理，用户点名的核心）

| # | 边界 | 现状行为 | 后果 | 严重度 |
|---|---|---|---|---|
| A1 | 两玩家先后与同一 NPC 对话 | 对话进同一份 `conversationHistory`，统一渲染"农场主" | NPC 把 A 说的话"记成"是 B 说的；LLM 上下文混乱 | 🔴 |
| A2 | NPC 对 A 的记忆在 B 的对话中被检索注入 | L1 记忆无玩家归属 | 记忆串味（"你上次不是说要去矿洞吗"——那是跟另一个人说的） | 🔴 |
| A3 | prompt 注入"农场主钱包/手持物/称呼" | 无归属（房客对话时是房客的 ✓，主机对话时是主机的 ✓——各自快照救了场） | 对话内认知侥幸正确，但记忆/历史不正确 → 后续对话错乱 | 🟡 |
| A4 | `get_info(player)` 查好感 | 查 TS 单份 friendship | 与两个玩家的真实好感都不一致 | 🟡 |
| A5 | `remember`/`forget` 工具 | relatedNpcs 硬编码 `["Farmer"]` | 记忆条目归属错误 | 🟡 |

### B. 经济错位

| # | 边界 | 现状行为 | 后果 | 严重度 |
|---|---|---|---|---|
| B1 | 房客发起交易（对话议价 → LLM trade → execute_adjust） | player target = `Game1.player` | **主机钱包被扣、主机背包被加物，房客毫发无伤**；TS 账本只记 NPC 侧所以自洽，但玩家侧完全错 | 🔴 |
| B2 | 交易进行中房客断线/退出 | in-flight `execute_adjust` 的 playerId 解析不到 Farmer（联机适配后才会出现；现状只会错扣主机） | 需要新失败码 `player_not_found` + TS 回滚路径 | 🟡（适配后） |
| B3 | 两玩家并发与同一 NPC 交易 | `acquireLock(npcName)` 序列化同 NPC 对话，第二个玩家收 BUSY 回复；经济 ops 在对话流程内 → 已被锁覆盖 | 串行 ✓；但 BUSY 文案对第二玩家是"她现在很忙"，属可接受默认 | 🟢（需验证锁覆盖经济路径） |
| B4 | 主机改房客背包（execute_adjust 执行在主机）→ 房客立刻再对话 | net field 同步有延迟，房客快照可能是旧背包 | 双层校验设计本就容忍过时（C# 物理校验兜底） | 🟢 |
| B5 | 审计发现的 P0 线程 bug（execute_adjust 在 WS 线程改 `Game1.player`）在联机下 | `Money` 是 `NetIntDelta`（主机→房客同步字段），跨线程写 | 单机只是竞态；**联机下污染增量同步，可能踢人/状态错乱** | 🔴（联机下升级） |
| B6 | NPC 求购单 vs 房客送礼 | 见 §1.3-#4 | 白送消耗 | 🟡 |

### C. 关系错位

| # | 边界 | 现状行为 | 后果 | 严重度 |
|---|---|---|---|---|
| C1 | vanilla 好感度本来就是 per-player（每个 Farmer 独立 `friendshipData`） | TS `memory.friendship` 单标量，与 vanilla 模型不对齐 | mod 侧债：TS 好感与所有人脱节，且驱动人设阶段（素不相识→夫妻）必然错位 | 🔴 |
| C2 | 房客对话的好感度 delta | 丢弃（§1.3-#3） | 房客聊天永远不涨好感 | 🔴 |
| C3 | 房客送礼的好感度 delta | 房客本地应用 ✓（C2 修复），但 TS 不知道 | vanilla 侧 ✓；TS 侧 friendship 持续漂移 | 🟡 |
| C4 | 主机玩家对话的好感度 delta | 应用到主机自己 ✓ | 正确 | 🟢 |

### D. 行为错位

| # | 边界 | 现状行为 | 后果 | 严重度 |
|---|---|---|---|---|
| D1 | 房客对话让 NPC 跟随（FOLLOW） | GoalExecutor 跟 `Game1.player` | NPC 跟着主机跑，房客观感"它不听我的" | 🔴 |
| D2 | GoalExecutor 到达判定/完成汇报 | 用主机位置 | 跟随时判定错目标（与 D1 同根） | 🔴 |
| D3 | `set_state` 的 accompany 语义 | 工具无目标玩家参数 | 同上 | 🔴（与 D1 同修） |

### E. 认知混合视角

| # | 边界 | 现状行为 | 后果 | 严重度 |
|---|---|---|---|---|
| E1 | 房客对话的 worldSnapshot | 房客玩家字段 ✓（本地视角正确）+ NPC 侧字段全缺省（房客无 AgentService） | **NPC 经济字段（npcMoney/npcInventory）已被 TS 账本自填救了——账本迁移的意外红利** ✓；npcMood 由 TS 情绪引擎兜底 ✓；npcLocation/npcRecentEvents 仍缺省 | 🟡 |
| E2 | 快照反序列化失败的主机兜底重建 | `WorldSnapshotBuilder.Build`（主机视角） | LLM 看到的"玩家"瞬间变成主机玩家（钱/背包/名字全换人） | 🟡（仅降级路径） |
| E3 | 导演上下文 | DirectorContextBuilder 只读 `Game1.player`（钱）；PlayerActionTracker 只追主机 | 导演看不见房客的行为与钱包 | 🟡（M3 范围） |

### F. 生命周期

| # | 边界 | 现状行为 | 后果 | 严重度 |
|---|---|---|---|---|
| F1 | 房客中途加入 | FullSync 广播 ✓ | 正常 | 🟢 |
| F2 | 房客退出（Farmer 是否留在农场 = vanilla 设置） | Farmer 对象在主机侧仍存在（睡觉/消失取决于 vanilla） | in-flight 对账需处理（B2）；无其他 | 🟡 |
| F3 | 主机关服/退出 | vanilla 无主机迁移，游戏即散 | 无额外处理必要 | 🟢 |
| F4 | 未装 mod 的房客 / 版本不齐 | 前者纯原版双轨；后者 Major 版本协商 → Inert | 双轨制：该房客的交互走原版（好感/经济不经 mod 管线）| 🟡（记录在案即可） |
| F5 | 分屏副屏 | Inert（原版体验，2026-07-18 设计决定） | 维持 | 🟢 |

### G. 部署与测试环境

| # | 边界 | 现状行为 | 后果 | 严重度 |
|---|---|---|---|---|
| G1 | 同机双开测试：两实例 GameLaunched 都自动起 8765 服务器 | `ServerProcessManager` 端口占用即杀进程 → **互杀循环**，靠"房客 SaveLoaded 后快速停止"缓解 | harness 场景下服务器状态混乱；需 env var 提前跳过房客 autostart | 🔴（测试阻断） |
| G2 | 房客聊天栏路由 | 硬禁用（`ChatBarRouter` provider null 直接 return） | 房客不能用聊天栏对 NPC 喊话 | 🟡 |
| G3 | 房客交易菜单 | `GiftTradeMenuLogic` 对 ThinClient 永不弹菜单 | 房客无主动交易入口（只能对话议价） | 🟡 |
| G4 | ModMessage 中继无 requestId | 两个 Transport 按 `npcName_` 前缀 FIFO 匹配 | 同 NPC 并发请求理论上错配（per-NPC pending 去重缓解） | 🟡（中继层债） |

---

## 3. 硬约束与限制（改不动的物理事实）

1. **主机权威**：世界状态/net field 只能主机改。现有架构已对齐（ThinClient 只渲染+中继，经济执行在主机 AdjustExecutor，账本在 TS 主机连接）✓。
2. **TS 单 WS 连接拓扑**：所有玩家共用主机的 8765 连接。不需要多连接，但**对话/交易请求必须带发起玩家身份**，TS 上下文必须按玩家分维度。
3. **ModMessage 中继特性**：60s 超时、TCP 之上、无 requestId（G4）。做交易中继时必须补 requestId。
4. **Token 成本**：per-player 记忆**不会**翻倍对话 token（每次对话只注入当前对话玩家的记忆），但存储与 L1 检索库翻倍。
5. **存档兼容**：现有单份记忆文件（`<npc>_memory.json`）→ per-player 结构需要一次性数据迁移（旧数据归属主机玩家）。
6. **LLM 依赖路径无法离线确定性验证**：对话议价→trade 是 LLM 行为。确定性断言用主机侧注入（IT 风格直调 executor / 直发 WS 消息），真实链路走 E2E + 真实 API。
7. **测试环境上限**：同机双开 harness 已覆盖网络路径（Lidgren localhost）。真实多机/VM 不必要。
8. **审计遗留 P0/P1 与联机耦合**：P0 主线程 dispatch 是任何联机经济测试的前置（B5）；give_gift 键错位与超时对账缺口会在联机测试中直接污染账本（多人并发放大）。

---

## 4. 产品决策记录（2026-08-16 用户已拍板）

| # | 决策点 | 结论 |
|---|---|---|
| 1 | **好感度权威** | **TS 维护 per-player 好感（数据全存主机的 TS 服务器上）**。vanilla `friendshipData` 仍是游戏内显示值；delta 必须回流 TS（对话路径 LLM 评估、送礼路径主机代报），保证 TS 侧 per-player 数据与现实一致（认知不得脱离现实铁律） |
| 2 | **记忆分玩家方案** | **"NPC 对世界的事件记忆一份 + 对玩家的关系记忆分份"**（原建议 b）。L1 检索注入"世界记忆 + 当前对话玩家的关系记忆" |
| 3 | **同 NPC 并发对话** | **维持 BUSY**，提示改为**灰色系统提示词条**："他/她/它正在和别人交流"（区分 NPC 性别/非人设代词；不再用 NPC 第一人称口吻） |
| 4 | **房客交易入口** | **本期对话议价 only**。恢复 GiftTradeMenu 走中继**记入待办**（后续批次）：`GiftTradeMenuLogic.cs:25-26` 对 ThinClient 禁用是临时状态，中继方案 = 菜单确认后经 ModMessage `TradeRequest`（须带 requestId）走主机 execute_adjust |
| 5 | **导演/画像多玩家化** | **必须做（不推迟）**。M3 从"可选"升级为承诺范围：PlayerProfileStore 多行、活动日志分玩家、导演聚合/指定玩家视角、Beat 上下文玩家快照结构 |

---

## 5. 建议分期（待确认后另立执行计划）

### M1 经济正确性（1-2 个会话）——"钱物不能扣错人"
- **前置**：审计 P0（execute_adjust 主线程 dispatch，联机下是同步字段污染）
- WS 协议：`dialogue` 请求加 `playerId`（发起玩家）；`execute_adjust` 加 `playerId`（消息级，整批单一玩家）；`adjust_result` 回带 echo；`WorldSnapshot` 的 friendship 明确为"发起玩家的好感"。单机缺省回落 `Game1.player`（向后兼容，check:protocol 同步）
- C#：AdjustExecutor 按 `playerId` 从 `Game1.getAllFarmers()` 解析 Farmer（已有先例 `AgentSyncBroadcaster.cs:277`），找不到 → 新失败码 `player_not_found`；房客对话好感度 delta 回传（`DialogueResponseMessage` 补字段 + 房客本地应用，复用送礼路径模式）；`HandleGiftRequest` 补求购单检查；GameLaunched 时 `VALLEY_TEST_INSTANCE=farmhand` 跳过服务器 autostart（修 G1）
- FOLLOW 目标玩家（D1-D3）：set_goal/FOLLOW 的 GoalExecutor 按发起玩家解析（或本期仅记录限制）
- 测试：IT14 主机侧确定性测试（伪 Farmer 注入 otherFarmers → execute_adjust 带 playerId → 断言钱物落在目标 Farmer、主机不动）+ harness 扩 C4（房客对话议价 → 真实 LLM → 验证扣房客钱）+ C1/C2/C3 回归

### M2 多玩家上下文（多会话）——"记忆不串味"
- 记忆模型拆"世界记忆 + per-player 关系记忆"（决策 #2）；对话历史 per-player；好感度读 vanilla（决策 #1）；prompt 按当前对话玩家渲染（称呼/钱包/手持物/画像摘要）；旧记忆数据迁移（归属主机玩家）

### M3 导演与画像多玩家化（决策 #5：必须做）
- PlayerProfileStore 多行、活动日志分玩家（PlayerActionTracker per-farmer）、导演聚合或选定玩家视角、Beat 上下文玩家快照结构

---

## 6. 测试策略（对齐五层体系）

| 层 | 手段 |
|---|---|
| 契约 | messages.json 加 playerId 后 check:protocol 双端对齐；C# ModMessage 协议版本号提升 |
| 组件 | AdjustExecutor 按 ID 解析 Farmer 的单测（含 not found）；TS 端 playerId 透传单测 |
| 故障注入 | 房客断线时 in-flight adjust；playerId 指向已退出 Farmer；两玩家并发同 NPC 交易 |
| 不变量 | 交易守恒按"发起玩家 + NPC"封闭系统验证；TS 账本 ≡ C# 镜像（对账不变量扩展玩家侧） |
| 体验 | harness C1-C4 双实例 E2E + ffmpeg 录像人工评分 |

**验证门槛**（沿用项目标准）：`bun test packages/stardew` + `tsc --noEmit` + `check:protocol` 全绿；C# 编译 0 警告 + xUnit 全绿；双实例 E2E 实测。
