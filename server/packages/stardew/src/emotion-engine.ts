/**
 * 确定性情绪引擎（2026-08-15 账本迁移设计步骤 3，零 LLM）。
 * C# 侧 EmotionAnalyzer/DialogueMemoryAnalyzer（机械推导，属"假 AI"）删除后，
 * 情绪推导统一在 TS：事件（state_changed reason / action_result 成败）→ 情绪标签 + 强度。
 * - 人设参数化：每 NPC 可选 EmotionProfile（baseline/volatility），缺省默认参数；
 * - Director set_npc_mood 覆盖权：mood 覆盖引擎推导（对话响应直接采用）；
 * - 情绪字符串与 C# NpcEmotion 枚举一致（Neutral/Happy/Sad/Angry/Worried/Excited/Tired/Grateful），
 *   供 dialogue_response.emotion → C# 气泡消费。
 * 状态仅内存（确定性推导，服务器重启回基线，无需持久化）。
 */

export interface EmotionProfile {
  /** 默认情绪（C# NpcEmotion 枚举字符串）。 */
  baseline: string;
  /** 波动性 0-1：事件情绪强度缩放（低波动 NPC 情绪变化小）。 */
  volatility: number;
}

export interface EmotionState {
  emotion: string;
  intensity: number;
  /** 推导来源（"state_changed:task_completed" / "action_result:chop_tree" / "director"）。 */
  source: string;
}

export interface EmotionEvent {
  /** 事件类别：state_changed | action_result | day_started。 */
  kind: string;
  /** 具体事件（state reason 或工具名）。 */
  detail?: string;
  /** action_result 成败（kind=action_result 时）。 */
  success?: boolean;
}

const DEFAULT_PROFILE: EmotionProfile = { baseline: "Neutral", volatility: 0.4 };

/** state_changed reason → 情绪（未知 reason 保持 baseline）。 */
const STATE_REASON_EMOTION: Record<string, string> = {
  travel_failed: "Worried",
  evicted: "Sad",
  task_completed: "Happy",
  // llm_decision / manual / 空 reason → 中性（事件本身不携带情绪信息）
};

/** action_result 工具 → 情绪（未知工具按成败给默认情绪）。 */
const TOOL_SUCCESS_EMOTION: Record<string, string> = {
  chop_tree: "Happy",
  set_goal: "Excited",
};
const TOOL_FAILURE_EMOTION: Record<string, string> = {
  chop_tree: "Sad",
  set_goal: "Worried",
};

export class EmotionEngine {
  private readonly states = new Map<string, EmotionState>();
  private readonly profiles = new Map<string, EmotionProfile>();

  constructor(profiles?: Record<string, EmotionProfile>) {
    if (profiles) {
      for (const [npc, profile] of Object.entries(profiles)) {
        this.profiles.set(npc, { ...DEFAULT_PROFILE, ...profile });
      }
    }
  }

  private profile(npcName: string): EmotionProfile {
    return this.profiles.get(npcName) ?? DEFAULT_PROFILE;
  }

  /** 事件 → 情绪。当前情绪与目标情绪相同则强度 +0.1（封顶 1.0），否则直接切换。 */
  applyEvent(npcName: string, event: EmotionEvent): EmotionState {
    const profile = this.profile(npcName);
    const target = this.resolveEmotion(event, profile);
    const scale = profile.volatility;
    const prev = this.states.get(npcName);
    const intensity = prev && prev.emotion === target.emotion
      ? Math.min(1.0, prev.intensity + 0.1)
      : 0.5 * scale;

    const state: EmotionState = {
      emotion: target.emotion,
      intensity,
      source: target.source,
    };
    this.states.set(npcName, state);
    return state;
  }

  /** Director set_npc_mood 覆盖（mood 非空时优先于引擎推导）。 */
  applyMood(npcName: string, mood: string): EmotionState {
    const state: EmotionState = { emotion: mood, intensity: 1.0, source: "director" };
    this.states.set(npcName, state);
    return state;
  }

  /** 当前情绪；无事件记录时回 baseline。 */
  current(npcName: string): EmotionState {
    return this.states.get(npcName) ?? {
      emotion: this.profile(npcName).baseline,
      intensity: 0,
      source: "baseline",
    };
  }

  /** 换日重置为 baseline（情绪是短时状态，跨日自然消退）。 */
  resetDay(npcName: string): EmotionState {
    const state: EmotionState = {
      emotion: this.profile(npcName).baseline,
      intensity: 0,
      source: "day_started",
    };
    this.states.set(npcName, state);
    return state;
  }

  /**
   * 换日重置所有已跟踪 NPC（2026-08-17 接线：handleDayStarted 调用）。
   * 情绪是短时状态，跨日自然消退；未跟踪 NPC 的 current() 本就回 baseline，无需处理。
   */
  resetAll(): void {
    for (const npc of this.states.keys()) {
      this.resetDay(npc);
    }
  }

  private resolveEmotion(event: EmotionEvent, profile: EmotionProfile): { emotion: string; source: string } {
    if (event.kind === "state_changed") {
      const emotion = event.detail ? STATE_REASON_EMOTION[event.detail] : undefined;
      return {
        emotion: emotion ?? profile.baseline,
        source: `state_changed:${event.detail ?? "unknown"}`,
      };
    }
    if (event.kind === "action_result") {
      const tool = event.detail ?? "";
      const map = event.success ? TOOL_SUCCESS_EMOTION : TOOL_FAILURE_EMOTION;
      const emotion = map[tool] ?? (event.success ? "Happy" : "Sad");
      return {
        emotion,
        source: `action_result:${tool}:${event.success ? "ok" : "fail"}`,
      };
    }
    return { emotion: profile.baseline, source: `event:${event.kind}` };
  }
}
