# 死断言清理计划（check:test-dead 281 条候选分流）

> **Created:** 2026-09-14
> **执行者:** subagent（自包含，无需会话上下文；有疑问按 §5 暂停点处理，不猜）
> **上游工具:** `scripts/check-dead-assertions.mjs`（扫 `logs/test_results/*_assertions.json` 历史断言日志，1787 次运行，min-runs=2）
> **当前基数:** 281 条"无反例约束 + 从未失败"高风险候选（**全部是 `src/ValleyAgent.TestMod` 游戏内 IT 测试的断言**，不是 xUnit 单测；xUnit 侧 0 条）

---

## 0. 一句话目标

对 281 条候选逐条分流：**删真死的、给能死的补反例约束、心跳型标注保留**，使 `check:test-dead` 在未来新鲜 IT 运行后归零（或只剩带人工确认记录的保留项）。**本批只动测试代码，零生产行为变化。**

## 1. 背景知识（执行前必读）

- 断言记录 API 在 `src/ValleyAgent.TestMod/V3TestBase.cs`：
  - `AssertEx(string label, bool condition, string counterexample, string detail = "")` —— **正确姿势**，带反例约束（counterexample 描述"什么情况这条断言应该失败"）
  - `RecordRunnerAssertion(label, condition, detail)` / `Record(...)` —— 无反例约束，checker 抓的就是这些
- 测试源码布局：`src/ValleyAgent.TestMod/Tests/{Edge,Integration,Functional,Visual,ComplexScenario,Pipeline,Focused,Fuzzy,Experience}/`，测试类名 = checker 输出里 `|` 前的名字（如 `E2_DoorLoop`、`IT11_Trade_Settlement`）。用 `grep -rn "class <名字>" src/ValleyAgent.TestMod` 定位。
- 281 条分布 TOP（全量清单执行时用 `node scripts/check-dead-assertions.mjs` 现跑现取）：
  `Func_MultiplayerSync`×30、`EmotionPipelineTests`×15、`IT11_Trade_Settlement`×14、`IT13_DirectorTools`×13、`Scene_PlayerGift`×12、`Integration_DecisionFlow`×12、`Scene_PlayerDialogue`×11、`Scene_MineExploration`×9、`PromptContractTests`×9、其余见工具输出。
- `logs/` 已 gitignore；历史日志只影响 checker 统计基数，不影响代码。
- 静态门 `scripts/check-test-anti-cheat.mjs`（常量真/Pass/tautology/Skip/注释断言/多行空 catch）在 CI 上，**必须保持 PASS**。

## 2. 分流判据（决策树，逐条断言走一遍）

```
Q1 这条断言测的对象还是活链路吗？
  ├─ 查证方法：AGENTS.md §3.3 协议表 + grep 生产代码（src/ValleyAgent，排除 Tests）
  │   注意已知事实：C# 旧结算链/TradeSettlement 已删（2026-08-15，结算走 TS execute_adjust）；
  │   旧叙事 Director 已砍（2026-09-14）但 C# DirectorTools 工具层保留（就绪层）。
  ├─ 是死管道（测试在测已删代码或永远走不到的分支）→ 处置 D：删断言；整条测试失去意义则删整个测试类
  └─ 是活链路 → Q2

Q2 条件是构造性恒真吗？
  （同方法内无条件赋值后立刻断言它；对 mock/桩断言桩本身的固定行为；断言刚 new 出来的对象非 null）
  ├─ 是 → 处置 D（删断言；若删完测试没有剩余断言 → 删测试）
  └─ 否 → Q3

Q3 这条断言可失败吗（存在真实机理使 condition 为 false）？
  ├─ 能写出具体反例机理（如 screenshot_saved：磁盘满/目录被占用时落盘失败）
  │   → 处置 S：把 Record/裸断言改为 AssertEx，counterexample 填该机理（一句话，具体到变量）
  ├─ 心跳/进度型（"NPC has not crashed"、"好感度变化: +20"、screenshot_saved 类良性守卫、
  │   编码脚本执行标记）：按 2026-08-20 测试系统设计的原定分流——**良性守卫走 allowlist 抑制**：
  │   在 `scripts/check-dead-assertions.mjs` 加 allowlist 机制（label 模式 + 理由注释），
  │   守卫类断言登记后 checker 不再列报；**真行为断言转 AssertEx 补具体反例**
  │   （反例机理同样存在：进程崩溃/冻结、好感回执丢失、落盘失败——能写具体机理的就转）；
  │   确实既无法转又不适入 allowlist 的 → 处置 K：保留原样记入《保留清单》注明理由
  └─ 断言从未失败是因为测试根本没走过失败分支（如 IT08 的 rollback 断言——回滚路径在测试里从未触发）
      → 处置 E：断言保留原样不动，记入《验证缺口 backlog》（补失败注入是另一个任务，本批不做）
```

**红线**：反例文案禁止空话（"出错时会失败"、"异常时"这类不带具体机理的）；不确定 Q1 归活归死的 → 一律处置 E 并记入《人工确认清单》，**宁可漏杀不可错杀**。

## 3. 硬约束

1. **只动 `src/ValleyAgent.TestMod/**`**（含 V3TestBase 的 API 若确有必要）+ **允许**给 `scripts/check-dead-assertions.mjs` 加 allowlist 机制（§2 K/S 处置需要；改动保持向后兼容，无 allowlist 时行为不变）。`src/ValleyAgent/` 生产代码一行不碰。
2. 每步之后：`DOTNET_ROOT="C:\Program Files\dotnet" dotnet build src/ValleyAgent.TestMod --nologo -v q` 必须 **0 警告 0 错误**；`DOTNET_ROOT="C:\Program Files\dotnet" dotnet test src/ValleyAgent.UnitTests --nologo` 必须**全绿**（守卫测试是源码文本扫描，会扫 TestMod 源码）。
3. `node scripts/check-test-anti-cheat.mjs` 保持 PASS（在仓库根跑）。
4. **不要 commit/push**——改完+门禁全绿即停，主会话亲自验收后提交。
5. 临时文件不落 C 盘；不删 `logs/`（历史数据，归用户决定）。
6. 猜想与实际分离（AGENTS §2 第 10 条）：每个 D 处置（删除）必须在改动清单里附一句证据（"结算链已删：grep TradeSettlement 生产代码仅剩 AdjustExecutor/PendingOffer 注释引用"这类）。

## 4. 产出物（写进仓库根 `docs/plan/` 下的执行报告）

1. 《改动清单》：文件 × 断言 label × 原状态 × 处置（D/S/K/E）× 一句证据/反例文案
2. 《删除清单》：删掉的断言/测试类及理由
3. 《人工确认清单》：Q1 拿不准的，附两边证据
4. 《验证缺口 backlog》：处置 E 的条目（测试缺失败注入路径）
5. 数字对账：处置后各类计数总和 = 281（以现跑 checker 输出为准，若数字与 281 有出入以现跑为准并在报告头部注明）

## 5. 暂停点（subagent 遇到即停，输出问题等主会话裁决）

- 某断言测的管道查不出死活（AGENTS §3.3 没覆盖、生产代码引用关系不清）
- 想改 V3TestBase 公共 API（影响所有测试的签名变更）
- 想删整个测试类而非单条断言（类级删除需主会话过目）
- 任何门禁（build/test/anti-cheat）变红且 10 分钟内修不回来

## 6. 验证与收尾（主会话做）

- 验收改动清单抽查 ≥10 条（重点抽查所有 D 处置的证据链）
- 提交（conventional commit，中文，`-F` UTF-8 文件）；push
- 游戏内/Docker IT 重跑（用户手动）→ `node scripts/check-dead-assertions.mjs` 复查 → 数字应大幅下降且剩余全部有《保留清单》或属新鲜运行的新候选
- 更新记忆 `test-fail-gate.md`（269/281 待分流 → 已分流，留报告路径）
