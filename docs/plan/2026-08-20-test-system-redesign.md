# 测试系统大改：三层判定 + 双轨报告（2026-08-20）

> 状态：实施中（Phase 1-4 代码已完成，Phase 5 容器回归中）
> 依据：2026-08-19 测试执行五阶段复盘 + 2026-08-01-test-scoring-redesign.md（L1-L5 设计稿）
> 动机：测试跑通后发现三处结构性缺口——无防乐观判定、C# 无 wire 层契约、无 L3 复杂场景指标

---

## 1. 三层测试与判定权

| 层 | 内容 | 判定权 | 落地位置 |
|---|---|---|---|
| L1 | 功能模块（状态机/协议字段/账本/回执） | 全自动硬断言 | C# xUnit + TS `bun test`（**计数以现跑为准**：2026-09-15 实测 TS = 577 通过 / 0 失败；C# 侧最近记录 657 通过，见 2026-09-14 报告） |
| L2 | 集成流程（数据流发送/处理/上下文管理） | 全自动 | ValleyAI fault-injection/invariants/roundtrip + C# ProtocolWireContractTests + PIPE 组 |
| L3 | AI 行为（准确/稳定/可预期） | **参考指标 + 人评** | chat-cli.ts（简单场景）+ Complex01（复杂场景）+ 双轨报告 |

核心原则（用户 2026-08-19 讨论）：
- **L1 防大模型乐观判定/作弊**：断言必须可失败（反例约束），超时默认=失败，死断言可检测
- **L2 测"AI 编程易错的三形态"**：提示词发错（prompt 段序）、处理错（工具调用解析）、上下文管理错（记忆/历史流向）
- **L3 大模型只能给参考分，必须人来评判**：自动指标只做"违规检测"，不做"合格判定"；人评主导收尾

---

## 2. Phase 落地清单

### Phase 1：L1 防作弊/防乐观判定（已完成）
- `V3TestBase.AssertEx(label, condition, counterexample, detail)`：反例约束断言，随结果 JSON 落盘
- `TestConfig.TimeoutAsFailure`（默认 true）+ `timeoutExemptions` 白名单（条目须注明原因）：
  V3/Experience runner 超时分支未豁免时追加 `timeout_as_failure` 失败断言
- `scripts/check-dead-assertions.mjs`：扫描历史断言，分级标记候选死断言（无约束+从未失败=高风险）
- `scripts/check-test-anti-cheat.mjs`：静态扫描常量 true/false 断言（方法体直接层）、空 catch 块
- 修复测试资产：mock_llm_responses.json 协议漂移（text→speech）、24 个测试 API fallback 陷阱

### Phase 2：L2 wire 层契约（已完成）
- `ProtocolWireContractTests.cs`（13 测）：dialogue/dialogue_response/execute_adjust/adjust_result/
  action_result 的 JSON→C# 字段映射、可选字段缺省、requestId 配对、失败码枚举 camelCase、
  未知字段容错、类型错误拒绝路径、ToolAction args 行为
- 修复契约漂移：`DialogueResponse` record 缺 `npcName`/`memorySideEffect`（messages.json 有、C# 无）→ 补齐
- 上下文管理：熔断/异常 fallback 路径补 `AddMemory`（防"玩家说了话没下文"失忆）+ AgentBrain
  Conversation entryType 去重契约测试

### Phase 3：L3 复杂场景（代码已完成，容器验证中）
- `TestGroup.ComplexScenario` + `vat_run complex` + ExperienceTestRunner 支持
- `Complex01_MultiNpcRandomPlay`（mock LLM 先行）：3 NPC 同场、12 轮乱序操作脚本、
  矛盾指令（跟随→停止）、幻觉标记注入（[H:State]/[H:Location]/[H:Memory]/[H:Item]）、
  工具执行模拟（成功/失败 → action_result 回执）
- `Abstractions/Testing/ComplexInfrastructure.cs`（9 测）：
  - HallucinationDetector：7 类幻觉（状态/物品/记忆/承诺/位置/人物/时间），"跟着"启发式对照权威状态
  - ExecutionSuccessTracker：4 类成功率（tool call/set_goal/对话反应/账本一致）+ 硬门槛（≥95%/100%）
  - EventStreamRecorder：事件流 JSONL（设计稿 L5-B 基础设施）
- 产出：`logs/complex_scenario/{ts}/_complex_scenario_report.json` + `events.jsonl` + `replay.txt`
- 真 LLM 阶段：同一检测器对照权威状态工作（test_config 开关）

### Phase 4：双轨报告（进行中）
- `_auto_report.json`：代码断言通过率 + 执行成功率（硬门槛提示）+ 硬幻觉率（违规检测）
- `_human_report.json`：5 项量表（NPC 像本人/导演自洽/记住玩家的话/反应延迟/出戏瞬间）+ 回放链接
- 不合成 S/A/B/C/D/F 等级——人看两份报告自己下结论

### Phase 5：基础设施修复（待做）
- ExperienceTestRunner 测试间清理增强（对齐 V3：重置 NPC/玩家状态）——EXP 组失败教训
- EXP 对话时序修复（固定 tick → 轮询等待真实 LLM 回复）
- EXP012 stall 根因排查（gift 阶段后卡死）、EXP010/011 疑似真 bug 排查
- 容器 CI：Pipeline 26/26 保持 + ComplexScenario 组（mock LLM）跑通

---

## 3. 双轨报告 schema

### _auto_report.json（自动，无人工参与）
```json
{
  "run_timestamp": "...",
  "code_assertion_pass_rate": 0.98,
  "code_passed": 130, "code_failed": 3,
  "complex_scenario": {
    "llm_mode": "mock",
    "hallucination_rate": 0.25,
    "hallucination_count": 3,
    "execution_success": { "tool_call_success_rate": 0.92, "...": "..." },
    "hard_gates": { "tool_call_gate_95": false, "ledger_gate_95": true }
  },
  "verdicts": ["hard_gate_failed: tool_call_success_rate 0.92 < 0.95"]
}
```

### _human_report.json（人评模板，回放链接）
```json
{
  "run_timestamp": "...",
  "replay_links": ["logs/complex_scenario/{ts}/replay.txt"],
  "questionnaire": [
    { "id": "q1", "question": "这段对话里 NPC 像不像本人？", "scale": "1-5" },
    { "id": "q2", "question": "导演编排的剧情自不自洽？", "scale": "1-5" },
    { "id": "q3", "question": "玩家说过的事 NPC 有没有记住？", "scale": "1-5" },
    { "id": "q4", "question": "反应延迟是否合理？", "scale": "1-5" },
    { "id": "q5", "question": "有没有任何出戏瞬间（海莉事件类）？", "scale": "1-5" }
  ],
  "scores": {},   // 人填
  "notes": ""     // 人填
}
```

---

## 4. 验证门槛

- TS：`bun test` + `bun run typecheck`（**已覆盖 `packages/*/tests`，见 2026-09-15 偏移审查 C1**）+ `check:protocol` 全绿
- C#：`dotnet test` 全绿 + 编译 0 警告（计数不写死，以现跑为准）
- 容器：Pipeline 26/26 保持 + ComplexScenario（mock LLM）跑通 + `_complex_scenario_report.json` 产出
- 超时白名单：登记后 V3/Experience 全组超时失败数不回归

## 5. 与设计稿（2026-08-01）的关系

| 设计稿要求 | 现状 |
|---|---|
| L1 契约测试（schema 单一源） | ValleyAI check:protocol + roundtrip 已有；C# wire 层本次补齐 |
| L3 故障注入（CH-01~10） | ValleyAI fault-injection 已有 CH-01~06；C# 熔断/回滚路径由 IT 覆盖 |
| L4 不变量（I1-I7） | ValleyAI invariants（I1-I3）；C# 侧 I4 反应保证/承诺检测入 Complex01 |
| L5 体验打分（A50/B35/C15） | 改造为双轨报告（自动指标 + 人评），不做自动等级合成 |
