# 测试与打分体系重设计：从"截图断言"到"不变量 + 故障注入"

> 日期：2026-08-01
> 状态：设计稿（不含代码改动）
> 配套：`docs/design/2026-08-01-npc-feedback-architecture.md`（下称"主设计"）。主设计中的失败模式编号（F1–F8 / G1–G7 / N1–N5）在本文有对应用例。
> 动机：现有测试体系测不出本项目**真实发生过的最严重 bug**——协议断链（`decision`/`friendship_eval` 被 TS 丢弃）、海莉事件（set_state 失败无反馈）、give_gift 永远报成功。这些 bug 的共性：**每个组件单测都是绿的，跨端语义是断的**。

---

## 1. 现有体系的诊断

| 现有机制 | 能测什么 | 测不到什么（盲区） |
|---|---|---|
| C# 单元测试（437 passed） | 状态机、工具函数 | 跨进程协议、真实 LLM、真实游戏状态 |
| TestMod 游戏内测试（Fuzzy/Edge/Functional/Experience/Pipeline） | 寻路、handler、UI 流程 | MockWebSocketServer（端口 8766）是**自己实现的假服务器**——它可能响应了真实 TS server 会丢弃的消息类型，协议漂移测不出 |
| MockLLMProvider 触发器响应 | 确定性对话流程 | 真实 LLM 的失败分布（超时/计费/校验失败/乱承诺）；故障路径 |
| 视觉断言（截图 + 离线 Kimi 分析） | 渲染是否正确 | 延迟判定、成本高；且"NPC 以为自己在跟随"这种状态-记忆不一致**截图根本看不出来** |
| ScoreAggregator S–F 评分 | 汇总已有断言 | 断言本身覆盖错了地方，评分是"绿放大器"而非质量信号 |

三个根因：

1. **没有契约层**：C# 发什么消息、TS 认什么消息，两端各自硬编码，没有任何东西校验它们的交集。
2. **没有故障注入**：所有测试都跑在"LLM 正常、执行成功"的晴天路径上，而生产环境的痛点全在雨天。
3. **没有不变量**：测试断言的是"某步操作返回了什么"，而不是"系统在任何时刻必须满足的性质"。状态-记忆不一致这类 bug 只有不变量抓得住。

---

## 2. 新体系五层

```
L1 契约测试（秒级，CI 必跑）        —— 防协议漂移
L2 组件测试（现有单测保留）          —— 防逻辑回归
L3 故障注入测试（分钟级，CI 必跑）   —— 防晴天偏见
L4 不变量监控（游戏内长跑）          —— 防状态-记忆分裂
L5 体验打分（自动指标 + 视觉辅助）   —— 面向玩家体验的质量信号
```

### L1 契约测试（新增，最高优先级）

**原则：wire 协议必须有单一事实源，两端从同一份定义生成/校验。**

- **Schema 单一源**：用 TS 侧已有的 TypeBox schema 导出 JSON Schema 作为协议定义文件（如 `protocol/schema.json`，可放 ValleyAI 仓库并同步到 ValleyTalk），覆盖全部消息类型：`hello/ping/dialogue/dialogue_response/tool_call_result/action_result/state_changed(新)/day_started(新)/beat_*(新)`。
- **静态交叉检查（CI 脚本，无需运行游戏或 LLM）**：
  - 从 C# 源码提取所有 `SendMessageAsync`/`SendRequestAsync` 的 `type` 字符串集合 S_send；
  - 从 TS `routeMessage` 提取所有 case 集合 S_route；
  - 断言 `S_send ⊆ S_route` 且每个 type 的字段名/类型与 schema 一致。
  - **这一条如果今天存在，`decision`/`friendship_eval` 死管道在提交当天就会红。**
- **Round-trip 测试**：对每种消息类型，起一个真实 TS server（bun run，无需游戏），C# 侧用轻量 test host 发消息，断言响应类型与 schema 匹配。覆盖"未知 type 不再静默 ack"的约定。
- **响应可达性断言**：任何带 `requestId` 的请求，server 必须回带**相同** requestId 的响应（禁止 `requestId:"unknown"` 落在已知请求上）——直接防 `PendingRequestTracker` 60s 超时链。

### L2 组件测试（保留 + 补强）

现有 437 个 C# 单测与 TS 单测保留。补强两处：

- **prompt 段序断言**（主设计 §4.4）：解析 `DIALOGUE_SYSTEM_TEMPLATE` 与各 builder 输出，断言静态段索引全部小于动态段索引；命中率用"连续两轮 prompt 的最长公共前缀 token 占比"做数值断言（同 NPC 同状态下应 ≥60%）。
- **记忆一致性单测**：fallback 路径必须 `memory.save()`（防 §0.5 的"玩家说了话没下文"）；`drainToolResults` 在 LLM 失败时批次不丢失（重入队或落记忆）。

### L3 故障注入测试（新增，核心）

**原则：对每一个已发生过的生产事故，固化一个故障注入用例。晴天路径的用例数不应超过雨天。**

在 MockWebSocketServer 之上加 **FaultProfile**（可组合的故障模式），并要求 Mock server 升级成"真实 TS server + LLM provider 打桩"（直接起真 server，把 `VercelAIProvider` 换成脚本化 fake），消灭"假服务器与真 server 行为不一致"这一整类假绿。

| 用例 | 注入故障 | 断言（对应主设计编号） |
|---|---|---|
| CH-01 | LLM 调 set_state(FOLLOW)，C# 侧 agent 未分配 | TS 收到 `action_result{success:false, reason:AGENT_MISSING}`；下轮 prompt 含"上次行动结果"失败；NPC 短期记忆含失败记录；**NPC 后续对话不承认自己在跟随**（F1，海莉事件回归） |
| CH-02 | FOLLOW 中旅行 warp 抛异常 | NPC 不停留在 (-1000,-1000)；状态转 IDLE；TS 收到 `state_changed(reason=travel_failed)`（F3） |
| CH-03 | 玩家 5 秒内连切 3 张图 | 同一 tick 不双发旅行；NPC 最终出现在玩家当前图或收到带原因的旅行失败上报（F2） |
| CH-04 | LLM 连续 2 次超时（送礼反应） | 第 3 次送礼 NPC 仍有**本地兜底反应**（台词或表情），不沉默（G1） |
| CH-05 | give_gift 时物品 id 不存在 | `action_result{success:false, reason:ITEM_NOT_FOUND}`；TS 库存**不扣减**；送礼记忆**不写入**（G4/G5，两阶段账本） |
| CH-06 | LLM 返回时 WS 断连 30s 后恢复 | 期间 `action_result`/`state_changed` 入 outbox，重连后按序补发，无丢失（§0.5 silently drop） |
| CH-07 | LLM 输出 CJK 比例不足 + retry 仍失败 | rule-engine 兜底台词写入对话历史且 `memory.save()` 被调用（§0.5 fallback 失忆） |
| CH-08 | 计费错误（402） | 走 billing 档兜底文案而非 unavailable 档（LLMBillingError 接线后） |
| CH-09 | beat 动作执行失败 | beat 标记 failed；NPC 记忆含失败；NPC 向在场玩家解释而非卡住（N3） |
| CH-10 | Agent 满员时叙事分配抢占 | 被淘汰 NPC 收到 `state_changed(reason=evicted)` + 玩家可见通知（F5/N2） |

打分规则：CH 用例**全红即构建失败**，不接受"已知问题"豁免——豁免机制正是这些 bug 活到今天的原因。

### L4 不变量监控（新增，游戏内长跑）

**原则：不变量是"任何时刻都必须为真"的系统性质，由游戏内探针每 tick/每秒采样，违例即记录事件，与具体测试用例解耦。**

| ID | 不变量 | 采样方式 | 抓到过的 bug |
|---|---|---|---|
| I1 跟随连续性 | NPC 处于 FOLLOW ⟹（与玩家同图 ∧ 距离 ≤ 阈值）∨ 处于合法 Travelling（旅行计时未超上限）。FOLLOW 退出必须存在 `state_changed` 记录且 reason ∈ 白名单 | 每秒 | F1/F2/F3/F7 |
| I2 状态镜像一致 | TS 端 `actualState` 镜像 == C# 状态机状态（允许 2s 同步延迟） | 状态变化时 + 每 10s 对账 | F1/F5 |
| I3 物资守恒 | give_gift `success` ⟺（玩家背包获得物品 ∧ TS 库存扣减 ∧ 记忆写入）；`failure` ⟺ 三者都不变 | 事件触发 | G4/G5 |
| I4 反应保证 | 玩家主动交互（送礼/对话消息提交）后 T 秒内必有 NPC 可见反应（台词/表情/动作之一） | 事件触发 + 计时 | G1 |
| I5 对话账本 | 每轮对话结束后：conversation_history 中玩家输入与 NPC 回复成对（fallback 也算）；server 记忆文件已落盘 | 事件触发 | §0.5 fallback 失忆 |
| I6 位置合法 | 任何 Agent NPC 的 Position 不得处于隐藏坐标超过旅行计时上限；不得处于不可行走瓦片超过 N tick | 每 5s | F3/F4 |
| I7 反馈不蒸发 | drain 的 tool result 批次 ⟹ 当轮 prompt 注入 ∨ 失败记忆写入 ∨ 重入队，三者必居其一 | 事件触发 | §0.3 |

实现形态：TestMod 加一个 `InvariantMonitor`（独立于现有 runner，常驻采样），违例写 `logs/invariants/{date}.jsonl`（含 tick、NPC、违例详情、最近 50 条事件流快照）。**长跑模式**：挂一个自动化存档跑 2–4 游戏日，事后统计违例分布——比单次用例更能暴露时序问题。

### L5 体验打分（改造）

打分信号来源从"断言通过率"改为三类指标加权，沿用 S/A/B/C/D/F 输出（ScoreAggregator 保留壳，换内核）：

**A. 正确性指标（权重 50%）——来自 L1/L3/L4**
- 契约违例数、CH 用例通过率、不变量违例率（按严重度加权：I2/I3 权重最高）。

**B. 玩家体验指标（权重 35%）——从事件流自动提取，无需人工**
- **幽灵承诺率**：LLM 表达行动意图（set_state/give/移动类 tool call）但 5s 内无对应现实状态变化的比例。这是"海莉事件"的量化形式，目标 0%。
- **跟随丢失率**：FOLLOW 总时长中 I1 违例时长占比。
- **跨图跟随成功率**：玩家切图时 NPC 处于 FOLLOW → N 秒内 NPC 出现在新图的比例。
- **反应延迟分布**：玩家交互 → NPC 可见反应的 P50/P95。
- **无反应率**：I4 违例率，目标 0%。
- **对话记忆命中率**：玩家提及过往事件时 NPC 回应体现该记忆的比例（抽样，由脚本构造固定"提及用例"测）。

**C. 表现力指标（权重 15%）——保留现有视觉断言**
- 截图/录像离线分析仅评"渲染是否出戏"（气泡位置、动画、HUD），不再承担行为正确性判定。

**事件流是打分的基础设施**：游戏内全量记录 `{tick, type: ws_msg|state_change|action|invariant, payload}` 到 JSONL。B 类指标全部由同一事件流离线计算，且支持**回放回归**——修复一个 bug 后重放事故当天的事件流，验证指标改善。

---

## 3. 与现有资产的映射

| 现有资产 | 处置 |
|---|---|
| V3TestRunner / Fuzzy·Edge·Functional 用例 | 保留，归入 L2/L3 晴天部分 |
| ExperienceTestRunner / Pipeline | 保留，逐步把断言迁到 L4 不变量 |
| MockWebSocketServer + MockLLMProvider | 升级为"真 server + fake provider"（L3 前提）；Mock server 退役 |
| VisualCaptureRunner / 视觉断言 | 降级为 L5-C，只做表现力 |
| ScoreAggregator | 保留壳，按 L5 权重换内核 |
| 437 个 C# 单测 / TS 单测 | 保留，归 L2 |

## 4. 落地顺序

| 阶段 | 内容 |
|---|---|
| T0 | L1 契约测试（schema 单一源 + S_send⊆S_route 静态检查 + round-trip）——**第一天就会红，这是好事**，红的清单就是主设计 §0.1 的死管道清单 |
| T1 | L3 故障注入框架 + CH-01/02/05（海莉事件三件套）；事件流 JSONL 记录 |
| T2 | L4 不变量 I1–I3 上线 + 长跑模式；L5-B 指标计算器（先做幽灵承诺率、跟随丢失率、无反应率） |
| T3 | 剩余 CH 用例与 I4–I7；回放回归；ScoreAggregator 换内核 |
