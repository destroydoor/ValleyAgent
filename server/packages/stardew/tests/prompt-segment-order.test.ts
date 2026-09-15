import { test, expect } from "bun:test";
import { DIALOGUE_SYSTEM_TEMPLATE } from "../src/prompt-builder";

// ---------------------------------------------------------------------------
// 段序防回归断言：确保 prompt 段按变动频率从低到高排列（静态在前、动态在后）。
// 前缀缓存命中率依赖于此顺序——静态段在前，多数轮次不变，缓存前缀才能命中。
// 若有人把静态段又加到尾部，此测试会失败。
// ---------------------------------------------------------------------------

test("DIALOGUE_SYSTEM_TEMPLATE segments ordered by volatility (static before dynamic)", () => {
  // 各段标识，按变动频率从低到高排列：
  // 静态 → 静态 → 静态 → 准静态 → 低频 → 中频(条件注入占位符) → 高频 → 每轮必变 → 每轮必变
  const markers = [
    "你的状态",                // 0. 你的状态（Phase 3 L2 状态摘要，静态模板 + 动态注入，固定在最前）
    "规则",                    // 1. 规则（静态）
    "重要事项记忆规则",         // 2. 重要事项记忆规则（静态，原尾段前移）
    "我是谁",                  // 3. 我是谁（准静态，{phase_prompt}）
    "我永远不会忘记的事",       // 4. significant memories（低频）
    "{actual_state_section}",  // 5. 真实状态镜像（中频，条件注入占位符）
    "最近记忆",                // 6. 最近记忆（高频）
    "最近对话",                // 7. 最近对话（每轮必变）
    "当前场景",                // 8. 当前场景（每轮必变）
  ];

  const indices = markers.map((m) => DIALOGUE_SYSTEM_TEMPLATE.indexOf(m));

  // 所有标识都必须存在于模板中
  for (let i = 0; i < markers.length; i++) {
    expect(
      indices[i],
      `段标识 "${markers[i]}" 应存在于 DIALOGUE_SYSTEM_TEMPLATE`,
    ).toBeGreaterThan(-1);
  }

  // 索引必须严格单调递增（变动频率从低到高）
  for (let i = 1; i < indices.length; i++) {
    expect(
      indices[i],
      `段 "${markers[i]}" (idx=${indices[i]}) 应排在 "${markers[i - 1]}" (idx=${indices[i - 1]}) 之后`,
    ).toBeGreaterThan(indices[i - 1]!);
  }
});
