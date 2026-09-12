# 计划：3 人联机主机无报错卡死——复刻 + 原因定位

> 日期：2026-09-10
> 状态：**待执行**（本文件是换 agent 的任务交接文档；用户明确：**不要直接修复，要原因**）
> 实机证据：用户已提供两轮真实测试结果（见 §1）
> 前置提交：86399cc（房客可用性+requestId）/ 0a74e12（门禁加固）/ f755aa1（TestMod 看门狗）/ 7e41696（E2E 修复+打包脚本）
> 关联：`docs/plan/2026-09-09-multiplayer-client-parity-and-freeze-hunt-plan.md`（Phase 4 即本任务）；`docs/design/2026-08-16-multiplayer-boundary-analysis.md`

---

## 0. 任务一句话

在 Docker 虚拟环境复刻"1 主机 + 3 房客、三玩家跨地图、游玩过中午"的实机卡死场景，抓到主机卡死现场的主线程栈（dmp 或伴生日志），给出根因与证据链。**不改游戏代码**（看门狗/测试驱动除外）。

## 1. 实机证据（用户提供，两轮）

| 轮次 | 场景 | 现象 |
|---|---|---|
| 修复前 | 3 玩家正常进游戏，不同地图游玩一段时间 | 主机**无报错卡死**。客机无模组 → 无法与 NPC 交流（模组前端只在主机生效，对话 UI 修改客机不生效）；后端连通性未测但 log 显示连上了。用户从有限 log 推断"**系统给每个玩家单独分配了导演**"——应为错误，需验证 |
| 修复后 | 玩家一在家、玩家二矿洞、玩家三小镇 | 主机**崩溃**；**第 3 玩家加入时也崩** |
| 修复后 | 加入时不立即崩 | 一般**游玩过中午**后崩，无预警无报错，程序直接未响应 |
| 对照 | 原版（无模组）3 人同玩法 | 持续游玩无问题 |

**关键特征**：进程未响应、无异常、无 SMAPI 错误、无 dump。实机跑的是发行包 `release/ValleyTalk-dist-*.zip`——**不含 TestMod**，看门狗（f755aa1）没部署，所以现场零证据。

## 2. 已确认事实（避免重复劳动）

1. **上一轮修复从未声称修过主机卡死**：86399cc 修的是房客三队列排水、ModMessage 发送主线程化、回包 requestId 精确匹配；f755aa1 的主线程看门狗就是为抓本次卡死现场做的仪器（UpdateTicked 心跳 + 停滞>3s 落 MiniDump；Windows 直接 P/Invoke，Linux 落伴生日志提示外部跑 createdump；首跳门控防加载期误报）。计划原则是"**拿到栈之前不修**"。
2. **Docker E2E 已全绿但场景太短**：三容器（valley-ts + valley-host + valley-farmhand）C1-C5 + Alloc 6/6 ALL PASS（真实 LLM），但都是短时功能场景，**未覆盖 3 房客 + 跨地图 + 过中午的 soak**。
3. **架构事实（"每玩家一个导演"问题的背景）**：TS 端 Director 是**单例**、单玩家视角（AGENTS.md §3）；M3 多玩家导演**从未启动**；M2 多玩家上下文已完成（记忆分世界桶/玩家桶、per-player 好感/对话历史、prompt 玩家化）。用户"每玩家一个导演"的推断最可能是**日志误读**（M2 的 per-player 桶/日志行），但需在 TS 日志代码里验证，不能拍脑袋否定。
4. **原版同场景不崩** → 确系模组（或模组×多人交互）引入。
5. 已排除项（2026-09-09 结论）：主机中继链路 3 流并发压测全绿、泵零卡顿（xUnit 复现套件 `src/ValleyAgent.UnitTests/Multiplayer/` 守护）。
6. 既有候选假设（未定罪）：①决策排水在 `_pendingDecisionsLock` 内同步执行 OnStateChanged 订阅者链；②房客后台 SendMessage 污染网络流→主机收包卡死在原版网络代码。前者已被 86399cc 部分绕开（房客发送主线程化），实机仍崩 → 需新假设或重新审视。

## 3. 执行路线

### 3.1 代码侧三路探查（先行，纯读）

> 原计划派 3 个 Explore 代理，localrouter（Agent 工具后端）故障未执行；换 agent 后重派或直接 grep。

**探查 A：TS 侧"每玩家一个导演"真相**（源码在 `<VALLEYAI_ROOT>`，主代码 `packages/stardew/src`）
1. 连接/分配模型：C# 主机 hello + allocate_agent 后创建什么状态？是否存在**按 playerId 实例化**的 Director 或任何 per-player 对象（搜 playerId 作 key 的 map：记忆桶、好感、对话历史、导演上下文）？引用 M2 的世界桶/玩家桶代码位置。
2. Director 日志：所有含 "Director"/"director"/"beat" 的 logger 调用，**逐条引用格式串原文**。判定用户读这些行是否会合理得出"每玩家一个导演"。
3. 每玩家流量缩放：3 个不同 playerId 活跃时，是否有周期性/beat 驱动的回 C# 流量按玩家缩放（set_npc_* 命令、spawn_beat 节奏）？Director beat 的触发源是什么（定时？连接事件？对话？）——能否解释"过中午"或"玩家在不同地图"时流量变大？
4. 多连接：TS 能否同时接多个 C# 连接？若客机意外也连上会怎样（per-connection Director？显示双连接的日志行？）。

**探查 B：C# 主机主线程阻塞/死锁审计**（`<REPO_ROOT>\src\ValleyAgent` + `.Abstractions`）
1. 所有 `.Result`/`.Wait()`/`GetAwaiter().GetResult()`/`Thread.Sleep`/`lock(...)`，限可达路径：UpdateTicked 处理器、Harmony patches（DialogueBoxInputPatch/NPCGiftPatch/ChatBoxInputPatch/其他）、ModMessage 接收（HostRequestHandlers/AgentSyncBroadcaster）、PeerConnected/Disconnected、DayStarted/TimeChanged/Saving、WS 消息分发（WebSocketClient/DualPathAgentServerProvider）。每个给出 file:line + 3 玩家负载下无限阻塞的机理。
2. 主线程泵死锁对：EnqueueMainThread/ProcessMainThreadActions、NPCGiftPatch._mainThreadActions、DialogueBoxInputPatch._pendingReplies 里入队的动作，有没有**自己再等主线程**（自饥饿）或与入队线程取同一把锁。
3. 玩家数缩放：per-tick/per-snapshot 上遍历 farmers/getOnlineFarmers 的热路径（WorldSnapshotBuilder/DirectorContextBuilder），调用频率与开销。
4. 移动/warp/FOLLOW：IMovementService、set_npc_position 执行、跨图 warp；FOLLOW 目标玩家在**另一张图**（如矿洞）时，NPC 是否可能进入无上限的 warp/寻路重试循环（已知限制："travel 跨图跟随仍跟主机位置"）。
5. 午时触发：1200 前后被触发的额外工作（日程拦截、beat、GoalExecutor、spark、consolidate）。

**探查 C：C# 主机玩家加入路径**（同上目录）
1. 第 3 个房客加入时主机执行的**全部动作**：PeerConnected/PeerDisconnected 订阅、ModMessage 接收、Game1.multiplayer 事件、遍历 otherFarmers 处——每个列 file:line + 做什么 + 分配/发送什么。
2. 玩家名单上送 TS：worldSnapshot/DirectorContextBuilder 是否携带全部 farmer、是否有按玩家数缩放的内容。
3. 模式判定：Host/ThinClient/Inert 判定的代码位置与时序；**装了模组的客机有没有任何路径会自启 TS 服务器或自建 WS 连接**（ServerProcessManager 预启动、Context.IsMainPlayer 时序、VALLEY_TEST_INSTANCE）。无模组客机对主机的影响应为零——验证。
4. "Director"字样日志行原文（C# 侧 director_command 处理、DirectorContextBuilder 日志），判定是否可能被误读为"每玩家一个导演"。

### 3.2 虚拟环境复刻（soak）

- 拓扑：valley-ts + valley-host + **3× valley-farmhand**（现有 e2e 是 1 房客，需扩展 `docker/docker-compose.e2e.yml` 或新 compose；镜像 valleyagent-gameit 现成，TestMod+看门狗已在内）。
- 驱动：扩展 `scripts/docker/run-e2e.ps1` 或新写 soak 脚本；用 CommandFileWatcher 命令文件让三房客分赴不同地图（农场/矿洞/小镇——矿洞要真实矿井或先用小镇替代，先跑通用版），主机挂机；游戏内时间过 1200 后继续。
- 观察点：主机容器内看门狗日志（Armed 行、停滞>3s 的伴生日志）、SMAPI 日志尾部、容器进程存活/退出码。
- 取证：卡死/退出后按看门狗伴生日志提示在主机容器内外部执行 createdump 落 dmp（Linux 分支已在代码里写好命令模板），或宿主机 `dotnet-dump analyze`。
- 注意：3 房客对话消耗真实 LLM 额度（用户提醒过）；跑之前确认额度。

### 3.3 定罪与输出

- 拿到卡死栈 → 定位阻塞点 → 对照 3.1 的假设 → 给出**根因 + 证据链**（栈、日志行、代码 file:line）。
- 若 soak 2h 不复现 → 高置信排除，明说，并列出实机与虚拟环境的差异（Windows vs Linux、3 实体机 vs 容器、无模组客机）。
- 用户明确要求：**只要原因，不修**。产出 = 结论文档，落 `docs/design/` 或 `docs/plan/`。

## 4. 环境速查（本机已踩过的坑）

- TS 源码：`<VALLEYAI_ROOT>`（本仓库的兄弟目录）；C# 源码：`<REPO_ROOT>`。
- Docker 三容器配方：`docker/docker-compose.e2e.yml`；重跑前必须重建 valley-ts 镜像（构建期装依赖）+ 重新 build/deploy dll + `prep-mods.ps1 -Role host|farmhand` 双角色（现需扩 3 房客角色）。
- 游戏容器：Xvnc 必须；卡死判据退出码 124；SteamCMD CN 网络重试可解。
- `dotnet test` 需 `DOTNET_ROOT="C:\Program Files\dotnet"`；TMP/TEMP 指向 `<REPO_ROOT>\.tmp`（禁落 C 盘）。
- Mimosa git 门禁 2026-09-10 起已放行会话内 commit（7e41696 实测）。
- 流程规矩：先分析后动手、先复现后定罪、实机测试由用户自己跑（副屏）；偏好简单集中方案。
- 记忆参考：`multiplayer-freeze-investigation`、`docker-multiplayer-e2e`、`docker-unittest-environment`、`steamcmd-cn-network-gotcha`、`test-fail-gate`。

## 5. 验收

1. 代码侧三探查有 file:line 结论（尤其"每玩家一个导演"是真是误读）。
2. 虚拟环境复刻结果：复现或明确不复现（含差异说明）。
3. 若复现：主机卡死栈 + 根因 + 证据链；若不复现：排除结论 + 下一步取证方案（如给用户实机部署含 TestMod 的 build 抓 dmp）。
