/**
 * 确定性情绪引擎（2026-08-15 账本迁移设计步骤 3，零 LLM）。
 * C# 侧 EmotionAnalyzer/DialogueMemoryAnalyzer（机械推导，属"假 AI"）删除后，
 * 情绪推导统一在 TS：事件（state_changed reason / action_result 成败）→ 情绪标签 + 强度。
 * - 人设参数化：每 NPC 可选 EmotionProfile（baseline/volatility），缺省默认参数；
 * - Director set_npc_mood 覆盖权：mood 覆盖引擎推导（对话响应直接采用）；
 * - 情绪字符串与 C# NpcEmotion 枚举一致（Neutral/Happy/Sad/Angry/Worried/Excited/Tired/Grateful），
 *   供 dialogue_response.emotion → C# 气泡消费。
 * 状态仅内存（确定性推导，服务器重启回基线，无需持久化）。
 *
 * M3 多玩家化（2026-09-13）：状态键由 `npcName` 拆为 `(npcName, playerId)` 双键，
 * 与 M2 记忆拆桶（世界记忆一份 / 玩家关系分份）同范式：
 * - **世界桶**（playerId 缺省）：无玩家归属的事件（state_changed、Director 世界级 mood）
 *   —— 对所有玩家可见，即"NPC 当下的整体心情"；
 * - **玩家桶**（带 playerId）：可归属到发起玩家的事件（action_result 经 callId 归属）
 *   或 Director 的 per-player mood —— 只对被对话的该玩家可见
 *   （A 把 NPC 惹毛，B 来对话不会承接那份情绪）。
 * 解析规则：**最近一次写入胜出**（内部单调 seq），而非"玩家桶恒优先"——
 * 否则某玩家的一次性情绪会永久遮蔽后续的世界级情绪，直到换日。
 * 单机行为完全等价现状（playerId 缺省 → 世界桶）。
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

/** 世界桶占位 playerId（内部键，不出现在任何对外 API 里）。 */
const WORLD_BUCKET = "";

/** 状态键分隔符（NPC 名与 playerId 都可能含可见字符，用 NUL 保证不碰撞）。 */
const KEY_SEP = "\u0000";

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

/** 桶内条目：state 对外，seq 仅用于"最近写入胜出"的解析。 */
interface EmotionEntry {
  npcName: string;
  /** 玩家桶的 playerId；世界桶为空串。 */
  playerId: string;
  state: EmotionState;
  seq: number;
}

export class EmotionEngine {
  /** key = `${playerId}\u0000${npcName}`；世界桶 playerId 为空串。 */
  private readonly states = new Map<string, EmotionEntry>();
  private readonly profiles = new Map<string, EmotionProfile>();
  /** 单调写入序号（M3：跨桶解析"谁更晚"）。 */
  private seq = 0;

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

  private static key(npcName: string, playerId?: string): string {
    return `${playerId ?? WORLD_BUCKET}${KEY_SEP}${npcName}`;
  }

  /**
   * 事件 → 情绪。当前情绪与目标情绪相同则强度 +0.1（封顶 1.0），否则直接切换。
   * @param playerId 事件归属玩家（缺省 → 世界桶，对所有玩家可见）。
   */
  applyEvent(npcName: string, event: EmotionEvent, playerId?: string): EmotionState {
    const profile = this.profile(npcName);
    const target = this.resolveEmotion(event, profile);
    const scale = profile.volatility;
    const prev = this.states.get(EmotionEngine.key(npcName, playerId));
    const intensity = prev && prev.state.emotion === target.emotion
      ? Math.min(1.0, prev.state.intensity + 0.1)
      : 0.5 * scale;

    const state: EmotionState = {
      emotion: target.emotion,
      intensity,
      source: target.source,
    };
    this.states.set(EmotionEngine.key(npcName, playerId), {
      npcName,
      playerId: playerId ?? WORLD_BUCKET,
      state,
      seq: ++this.seq,
    });
    return state;
  }

  /**
   * Director set_npc_mood 覆盖（mood 非空时优先于引擎推导）。
   * @param playerId 定向到某玩家（联机导演 per-player 编排）；缺省 → 世界级，对所有玩家生效。
   */
  applyMood(npcName: string, mood: string, playerId?: string): EmotionState {
    const state: EmotionState = { emotion: mood, intensity: 1.0, source: "director" };
    this.states.set(EmotionEngine.key(npcName, playerId), {
      npcName,
      playerId: playerId ?? WORLD_BUCKET,
      state,
      seq: ++this.seq,
    });
    return state;
  }

  /**
   * 当前情绪（某玩家视角）：取该玩家桶与世界桶中**更晚写入**的那份；
   * 两者都无记录时回 baseline。
   */
  current(npcName: string, playerId?: string): EmotionState {
    const fallback: EmotionState = {
      emotion: this.profile(npcName).baseline,
      intensity: 0,
      source: "baseline",
    };
    const world = this.states.get(EmotionEngine.key(npcName));
    const entry = playerId
      ? this.states.get(EmotionEngine.key(npcName, playerId))
      : undefined;

    if (entry && world) return entry.seq >= world.seq ? entry.state : world.state;
    if (entry) return entry.state;
    if (world) return world.state;
    return fallback;
  }

  /**
   * 单个桶换日重置为 baseline（情绪是短时状态，跨日自然消退）。
   * playerId 缺省 → 只重置世界桶。换日全量清场请用 {@link resetAll}。
   */
  resetDay(npcName: string, playerId?: string): EmotionState {
    const state: EmotionState = {
      emotion: this.profile(npcName).baseline,
      intensity: 0,
      source: "day_started",
    };
    this.states.set(EmotionEngine.key(npcName, playerId), {
      npcName,
      playerId: playerId ?? WORLD_BUCKET,
      state,
      seq: ++this.seq,
    });
    return state;
  }

  /**
   * 换日重置所有已跟踪桶（2026-08-17 接线：handleDayStarted 调用）。
   * 情绪是短时状态，跨日自然消退；未跟踪 NPC 的 current() 本就回 baseline，无需处理。
   * M3：玩家桶与世界桶一并重置（换日不该留下任何昨日情绪）。
   */
  resetAll(): void {
    for (const entry of this.states.values()) {
      this.states.set(EmotionEngine.key(entry.npcName, entry.playerId || undefined), {
        npcName: entry.npcName,
        playerId: entry.playerId,
        state: {
          emotion: this.profile(entry.npcName).baseline,
          intensity: 0,
          source: "day_started",
        },
        seq: ++this.seq,
      });
    }
  }

  /** 已跟踪的 NPC 名（去重，含仅存在于玩家桶的 NPC）。测试/诊断用。 */
  trackedNpcs(): string[] {
    return [...new Set([...this.states.values()].map((e) => e.npcName))];
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
