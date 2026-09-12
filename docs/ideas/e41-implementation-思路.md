# E4-1 雇佣系统（契约 + 计价 + 朋友价）实现思路

> 分支：`feat/integration-phase3-5`（Phase 3 已全部合入）
> 日期：2026-08-03
> 依据：`docs/plan/2026-08-03-phase3-5-full-execution-plan.md` A6（E4-1）、
> `docs/design/2026-08-02-npc-economy-hire-chat-requirements.md` §2

## 1. 需求

雇佣 = 玩家花钱购买 NPC 的自主性配额（导演编排中的高优先级 beat）。契约期间 NPC 自主
决策让位于契约；契约结束归还自主权。

- **契约模型**：任务类型（跟随陪伴 / 收菜 / 挖矿 / 下矿保镖）+ 期限（半天 / 一天）+ 报酬
- **计价**：`报价 = 人设日薪基准 × 任务危险系数 × 缺钱修正 × 好感修正`
- **缺钱修正**：钱包低于阈值 → 更愿意接单甚至自降报价
- **好感修正**：朋友价折扣；close 以上可能免费
- **长工保护**：一周免费 ≥3 次触发"你总使唤我"吐槽（免费允许，但软性限白嫖）
- **违约**：指派契约外任务 → 抗议；强行 → 好感下降 + 可单方面中止契约
- **讨价还价**：复用 E3-2 HaggleStateMachine

## 2. 设计决策

### 2.1 纯逻辑核心（可单测）

四个新类全部为纯逻辑（无游戏依赖，与 E3-2/E3-3 同风格），测试用 xunit：

| 类 | 职责 |
|---|---|
| `EmploymentContract` | 契约不可变 record：任务类型/期限/报酬/状态/开始时间 |
| `ContractService` | 计价公式 + 缺钱修正 + 接受/拒绝 + 完成/中止结算 |
| `LongWorkerProtection` | 朋友价/免费判定 + 周免费次数保护（≥3 吐槽） |
| `ContractViolation` | 违约抗议判定 + 强行中止（含好感惩罚信号） |

### 2.2 计价公式

```
报价 = round(日薪基准 × 期限系数 × 危险系数 × 缺钱修正 × 好感修正)
期限系数: 半天 0.5 / 一天 1.0
危险系数: 跟随 1.0 / 收菜 1.2 / 挖矿 1.5 / 保镖 2.0
缺钱修正: 钱包 < 阈值(日薪×5) → 0.8（自降报价，"手头紧"）
好感修正: 好感心 ≥ 8 (close) → 免费(0)；≥ 4 → 0.7 朋友价；否则 1.0
```

- 免费（报价 0）允许，但触发 LongWorkerProtection 计数（周 ≥3 → 吐槽事件）。
- 有钱 NPC（钱包 ≥ 日薪×15）且好感 < close → 直接拒绝接单（不伤人好感）。

### 2.3 违约语义

- `EvaluateViolation(assignedTask, contractTask)`：任务类型不匹配 → Protested（抗议信号）。
- `ForceViolation(...)`：玩家强行 → 契约中止（Violated），返回好感惩罚信号（-500 点）。

### 2.4 WorldSnapshot 扩展

`WorldSnapshot` 增加 `PlayerMoney`（默认 null 向后兼容，WorldSnapshotBuilder 填充
`Game1.player.Money`）——LLM 感知玩家钱包，才能"缺钱 NPC vs 有钱 NPC"差异化反应。

## 3. 改动文件

| 文件 | 改动 |
|---|---|
| `src/ValleyAgent/Economy/EmploymentContract.cs` | **新增**：契约模型 + 任务/期限枚举 |
| `src/ValleyAgent/Economy/ContractService.cs` | **新增**：计价 + 接受/拒绝/结算 |
| `src/ValleyAgent/Economy/LongWorkerProtection.cs` | **新增**：朋友价 + 周免费保护 |
| `src/ValleyAgent/Economy/ContractViolation.cs` | **新增**：违约抗议 + 中止 |
| `src/ValleyAgent.Abstractions/WebSocket/IAgentServerProvider.cs` | WorldSnapshot + PlayerMoney |
| `src/ValleyAgent/AI/WorldSnapshotBuilder.cs` | 填充 PlayerMoney |
| `src/ValleyAgent.UnitTests/ContractServiceTests.cs` | **新增**：计价各修正因子/缺钱 vs 有钱/朋友价/长工吐槽/违约 |
| ValleyAI TS（后续） | accept_job 工具 + prompt 段 + playerMoney 解码 |

## 4. 验证

- 6 项目构建 0 警告 0 错误
- UnitTests 全绿（含新 ContractServiceTests）
- L4 不变量：契约优先于叙事（契约生效时导演不插入冲突 beat——C# 状态机侧由
  ForceTransition 约束，本模块输出契约状态供上层裁决）
