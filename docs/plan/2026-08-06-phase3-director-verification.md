# 阶段 3 验证记录 — Director 工具集 + L2 状态层 + 默认创建

> **Created:** 2026-08-06
> **依据:** `docs/plan/2026-08-05-three-tier-architecture-execution-plan.md` 阶段 3
> **状态:** 已合并至 master，验证门禁全绿（三阶段执行完成）

---

## 1. 实施内容

### TS 端（ValleyAI，commit `2869eca` + `dc4e367` 修复，17 文件 +368/-8）

| 条目 | 内容 |
|---|---|
| 3.4.2/3.4.3 L2 状态层 | types.ts WorldSnapshot/SceneState 加 npcMood/npcRecentEvents/npcWorkingOn/npcOwedMoney；decoder null-safe；prompt-builder DIALOGUE_SYSTEM_TEMPLATE 开头加「## 你的状态」段（心情/近期事件/正在做/欠款/当前目标），段序测试同步更新 |
| 3.3.2 Director 协议 | messages.json 加 `director_command`（TS→C#，9 工具统一入口）；types.ts IncomingMessage/OutgoingMessage 加 DirectorCommandMessage；protocol-adapter routeMessage 加 case + handleDirectorCommand（经 sendToCsharp 转发） |
| 测试 | +12 新增（L2 解码 ×4 / L2 prompt 段 ×4 / director_command 路由 ×2 / roundtrip ×2）→ **419 pass** |

**协议修复（dc4e367）**：director_command 初标 status=active + routed_by_ts=false → check:protocol FAIL（status_field_mismatch）；改 routed_by_ts=true → active_not_in_intersection（C# 从不发送）。最终标 **orphan_route + routed_by_ts=true**（与 route_shout 同语义：TS 路由、C# 从不发送），check:protocol PASS。

### C# 端（ValleyTalk，commit `b3a66c9` + `ef1b185`，23 文件 +3156/-69）

| 条目 | 内容 |
|---|---|
| 3.4.1 AgentBrain L2 | Abstractions/Brain/AgentBrain.cs 加 MoodTag/TodayEvents/WorkingOn/OwedMoney |
| 3.4.2 WorldSnapshot L2 | IAgentServerProvider.cs record 尾部追加 4 个 L2 参数（tail-default-null 兼容）；WorldSnapshotBuilder.Build 填充 |
| 3.1.1 默认创建 | EventHandlerInitializer OnSaveLoadedCore 新增遍历所有 NPC 建 AgentBrain 路径（不强制分配，尊重 MaxAgentNpcs） |
| 3.2.1 NpcConfigLoader | Config/NpcConfigLoader.cs（复制 NpcEconomyProfileLoader 骨架）+ npc-configs/{Shane,Abigail,Emily,Leah}.json |
| 3.3.1 DirectorTools | Commands/DirectorTools.cs 9 工具（position/inventory/money/mood/recent_events/working_on/spawn_beat/spawn_group_beat/inject_memory）；CommandExecutor director_command 特殊路由 |
| 3.5 BeatStore | Beats/BeatStore.cs（活跃 beat 场景描述，L3 注入源） |
| 3.6 PlayerActionTracker | Tracking/PlayerActionTracker.cs（10-tick 采样 + 行为分类 mine/farm/fish/forage/social/other + day_started 聚合） |
| 3.7 DirectorContextBuilder | AI/DirectorContextBuilder.cs（day_started 拼装 800-1500 token 结构化上下文） |
| 配置 | ModConfig 加 Director/Beat 子对象配置 + Validate clamp |
| 测试 | 4 新文件 83 测试：AgentBrainL2 / DirectorTools / NpcConfigLoader / PlayerActionTracker |

## 2. 验证结果

| 门禁 | 结果 |
|---|---|
| TS `bun test packages/stardew` | **419 pass / 0 fail**（407 基线 + 12 新增） |
| TS `tsc --noEmit` | **0 错误** |
| TS `check:protocol` | **PASS（exit 0）**（director_command 修复后） |
| C# 编译 | **0 警告 0 错误** |
| C# 单测（阶段 3 新增） | **83/83 通过** |

## 3. 关键设计裁决（思路文件 §1）

1. **director_command 独立消息通道**（不复用 action_result——它是 C#→TS per-NPC 反馈，语义不匹配）
2. **旧 director.ts/BeatStore 保留**作为 Director 叙事灵感来源；新工具型 Director 并存（additive）
3. **L2 注入在 prompt 开头**（「## 你的状态」在规则段前，静态段序不破坏段序测试）
4. **默认创建不强制分配**（AgentBrain 文件存在，活跃集仍由 spark/对话/Director beat 驱动）
5. **director_command 协议标 orphan_route**（TS 路由、C# 从不发送，与 route_shout 同语义）

## 4. 合并记录

- ValleyAI master ← `feat/phase3-director`（fast-forward，HEAD `2869eca`）+ 协议修复 `dc4e367`
- ValleyTalk master ← `feat/phase3-director-cs`（fast-forward，HEAD `ef1b185`）
- 无冲突

## 5. 三阶段执行总结

| 阶段 | 内容 | TS 测试 | C# 验证 |
|---|---|---|---|
| 1 | 交易 bug 修复（trade 工具 + playerHeldItem + 名称回落） | 397 pass | 0 警告 + 24 测试 |
| 2 | set_goal + GoalExecutor（五种 Goal + 寻路汇报） | 407 pass | 0 警告 + 66 测试 |
| 3 | Director 工具集 + L2 状态层 + 默认创建 | 419 pass | 0 警告 + 83 测试 |

执行计划全部三阶段完成。基线、设计文档、阶段 1/2/3 验证记录均已入库。

## 6. 遗留待办（范围外）

- 游戏内实测（交易方向/价格锚定、Shane set_goal 砍树汇报、Director spawn_beat 实际编排）待游戏环境运行
- 5 个 NpcEconomyProfileLoaderTests 预存失败（只读 Name 属性 / System.Text.Json）待修复
- ±30% 交易让步临时值迁逐 NPC 精明度模型
- Director 开放问题（token 预算/寻路细节/group beat 可见性）实施时细化
