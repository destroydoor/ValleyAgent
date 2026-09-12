# NPC 剧情演变系统 — Checklist

> **状态（2026-07-26 标注）：部分项已迁至 TS Agent Server**
>
> "C# → Python 同步"、"Python 服务器独立测试通过"等检查项已废弃。v4.3 中
> prompt 处理在 TS 服务器侧完成，C# 仅发送 `worldSnapshot`。
>
> **当前架构**：[`../../AGENTS.md`](../../AGENTS.md) 第 2.1.1 节。

---

## 数据模型

- [ ] FriendshipPhase 枚举定义正确（5个阶段）
- [ ] FromFriendship() 阈值与原版 heart event 对齐
- [ ] TraitPhaseMapper 关键词覆盖所有 33 个 NPC 的 trait
- [ ] Preoccupations 分类规则覆盖常见关注话题

## Bio 注入

- [ ] ValleyTalkBioData.PromptOverrides 属性已添加
- [ ] GetPhaseSummary() 各阶段输出符合预期
- [ ] GetVisibleTraits() Phase 1 不展示深层特质
- [ ] GetVisiblePreoccupations() Phase 1-2 不展示深层关注
- [ ] PromptOverrides 查找逻辑正确

## Prompt 构建

- [ ] AgentPromptBuilder.AppendBioSection 接受 friendshipPoints 参数
- [ ] Trait 列表按阶段筛选后注入
- [ ] Biography 摘要按阶段深度截取
- [ ] Preoccupations 按阶段筛选后注入
- [ ] 事件里程碑段落正确追加
- [ ] PromptOverrides 替换默认 friendship behavior

## C# → Python 同步

- [ ] ValleyAgentApi 传入筛选后的 bio 数据
- [ ] DialogueRequest 包含 friendship_phase 字段
- [ ] DecisionRequest 包含 friendship_phase 字段
- [ ] prompts.json 模板使用 friendship_phase 变量

## 角色验证

- [ ] Shane Phase 1: 冷漠、酗酒、自我毁灭
- [ ] Shane Phase 3+: 关心鸡、对 Jas 有责任感
- [ ] Shane Phase 5: 戒酒、温暖
- [ ] Haley Phase 1: 虚荣、嫌弃小镇
- [ ] Haley Phase 3+: 欣赏摄影和自然
- [ ] Sebastian Phase 1: 沉默回避
- [ ] Sebastian Phase 3+: 分享内心
- [ ] Abigail Phase 1: 叛逆冒险
- [ ] Abigail Phase 3+: 分享家庭压力

## 回归

- [ ] 无 bio 的 NPC 仍走 fallback 路径
- [ ] 现有测试不因新逻辑失败
- [ ] Python 服务器独立测试通过
