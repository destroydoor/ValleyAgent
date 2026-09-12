# 阶段 1 验证记录 — 交易 bug 修复

> **Created:** 2026-08-06
> **依据:** `docs/plan/2026-08-05-three-tier-architecture-execution-plan.md` 阶段 1
> **状态:** 已合并至 master，验证门禁全绿

---

## 1. 实施内容

### TS 端（ValleyAI，commit `ce8bbf2`，13 文件 +177/-6）

| 条目 | 内容 |
|---|---|
| 1.1.1 trade 工具 | `stardew-tools.ts` 新增意图式 trade 工具：NPC 是买家（direction 固定 `npc_buys`），参数 {item_id, quantity, price}；execute 只写意图日志，返回 `{action:"trade", itemId, quantity, price, direction:"npc_buys"}`，结算由 C# ExecuteTrade 执行 |
| 1.1.2 playerHeldItem | `types.ts` WorldSnapshot + SceneState 新增 `playerHeldItem: {itemId, name, qty, marketPrice}`；`world-snapshot-decoder.ts` null-safe 解码 |
| 1.1.3 prompt 交易规则 | `prompt-builder.ts` 规则段新增三条：你是买家 / ±30% 让步 / 必须调 trade 工具；当前场景段注入「农场主手持：{playerHeldItem_desc}」（有手持物显示名称×数量+公道价，无则提示） |
| 1.1.4 give_item 描述 | give_item 的 item_id 描述明确「传 QualifiedItemId（如 (O)388 for Wood），不要传 DisplayName」 |
| 协议 | messages.json worldSnapshot 字段描述加 playerHeldItem |
| 测试 | +9 新增（trade schema / trade execute / playerHeldItem 解码 ×4 / 手持物描述 ×2 / 交易规则） |

### C# 端（ValleyTalk，commit `e071a7c`，4 文件 +425/-15）

| 条目 | 内容 |
|---|---|
| 1.2.3 give_item 名称回落 | 新文件 `Utils/ItemNameResolver.cs`：注入式数据源（单测可传合成数据）+ 默认读 Game1.objectData（缓存）；匹配顺序英文内部名（OrdinalIgnoreCase）→ 本地化显示名（Ordinal）；失败输出 top-3 相近名建议（前缀命中优先 + Levenshtein ≤ 2）；`CommandExecutor.ExecuteGiveItem` 集成 |
| trade 校验一致 | `ExecuteTrade` 的物品校验（L310）加同样回落，保持一致 |
| 测试 | 新文件 `ItemNameResolverTests.cs`（143 行）+ 更新 `CommandExecutorTests.cs` |

### 已由 E 系列实现、验证不动（现状核对结论）

- 1.2.1 C# PlayerHeldItem 已由 E3-6 实现（WorldSnapshotBuilder.cs:84 + record IAgentServerProvider.cs:63，marketPrice 用 sellToStorePrice 公道价）
- 1.2.2/1.2.4 ExecuteTrade 已由 E3-3 实现（CommandExecutor.cs:279-341，TradeDirection.NpcBuysPlayerItem 方向正确 + PendingOffers 30s TTL + NPCGiftPatch.TrySettlePendingTrade 交接结算）

## 2. 验证结果

| 门禁 | 结果 |
|---|---|
| TS `bun test packages/stardew` | **397 pass / 0 fail**（基线 388 + 9 新增） |
| TS `tsc --noEmit` | **0 错误** |
| TS `check:protocol` | **PASS（exit 0，无死管道无 schema drift）** |
| C# 编译 | **0 警告 0 错误**（ValleyAgent.csproj） |
| C# 单测（改动相关） | **24/24 通过**（ItemNameResolver + ExecuteGiveItem + ExecuteTrade） |

## 3. 已知预存问题（非本阶段引入）

- `dotnet test` 全量跑有 **5 个 `NpcEconomyProfileLoaderTests` 失败**（Assert.Equal(3, loader.Count) → Expected 3 Actual 0；Assert.NotNull → Value is null）。经 `git stash` 基线验证为**预存失败**，根因：`NpcEconomyProfileData.Name { get; }` 是只读属性，System.Text.Json 无法反序列化 → 所有 profile 被跳过。与本阶段改动无关（未触碰该文件），留待后续修复。
- `bun test` 进程 exit 1 但 0 fail：预存环境怪癖（长跑/故障注入测试的未捕获异步错误），基线同样如此。

## 4. 合并记录

- ValleyAI master ← `feat/phase1-trade-wt`（fast-forward，HEAD `ce8bbf2`）
- ValleyTalk master ← `feat/phase1-giveitem-wt`（merge commit `c6b4e2e`）
- 无冲突；ValleyTalk 另含 `4c36bbb`（AGENTS.md v5→v6 + 阶段 1 思路文件）
- worktree `va-phase1` / `vt-phase1` 保留供阶段 2 复用或清理

## 5. 遗留待办（阶段 1 范围外）

- ±30% 让步为临时值，阶段 1 验证通过后迁逐 NPC 精明度模型（设计文档 §14.1 待决项 2）
- NpcEconomyProfileData.Name 只读属性导致的反序列化问题（5 个预存测试失败）
- 游戏内实测（Shane/Marnie/Haley 交易方向与价格锚定）待游戏环境运行
