// ReActGuard — soft-brake controller for NPC ReAct loops (Task 5).
// Spec: docs/superpowers/specs/2026-07-21-narrative-director-design.md §3.6

import type { Beat } from "./types";

/**
 * Soft-brake conditions for the NPC ReAct loop.
 * Each threshold has a sensible default; pass a Partial<GuardConfig> to override.
 */
export interface GuardConfig {
  /** Trigger repeated_tool_calls when this many recent steps have the same tool+argsHash. */
  maxRepeatedToolCalls: number;   // default 3
  /** Trigger no_progress when this many recent steps have the same npcTileAfter. */
  maxNoProgressSteps: number;     // default 5
  /** Trigger token_budget_exceeded when accumulatedTokens exceeds this. */
  maxTokensPerBeat: number;       // default 8000
  /** Trigger llm_failures when consecutiveLlmFailures reaches this. */
  maxLlmFailures: number;         // default 3
  /** Trigger tool_failures when consecutiveToolFailures reaches this. */
  maxToolFailures: number;        // default 3
  /** Trigger player_left when playerLeftAt has been null for this many ms. */
  playerLeftThresholdMs: number;  // default 120_000
}

export const DEFAULT_GUARD_CONFIG: GuardConfig = {
  maxRepeatedToolCalls: 3,
  maxNoProgressSteps: 5,
  maxTokensPerBeat: 8000,
  maxLlmFailures: 3,
  maxToolFailures: 3,
  playerLeftThresholdMs: 120_000,
};

/**
 * A minimal per-step record the guard needs. The full ReActStep (with
 * thought, toolResult, etc.) lives in narrative-types.ts; this record
 * isolates only what the guard cares about so the caller (StardewAgent)
 * can build it cheaply without copying the full step.
 */
export interface ReActStepRecord {
  /** Tool name invoked this step. undefined if the LLM produced no tool_call. */
  toolName?: string;
  /** Stable hash of the tool args object. undefined if step had no tool_call. */
  argsHash?: string;
  /** NPC tile position observed after this step's tool executed. */
  npcTileAfter?: { x: number; y: number };
  /** Wall-clock ms when the step completed. Used for diagnostics only. */
  timestamp: number;
}

/**
 * Per-call guard context. The caller (Director / StardewAgent) assembles
 * this from its own state before each check().
 *
 * Design note: ReActGuard is stateless — all mutable state lives here,
 * passed per call. This makes the guard trivially testable and avoids
 * hidden cross-call coupling. reset() is therefore a no-op preserved
 * for API compatibility with the spec.
 */
export interface GuardContext {
  beat: Beat;
  /** Tokens consumed so far for this beat (cumulative). */
  accumulatedTokens: number;
  /** Consecutive LLM failures since the last success. */
  consecutiveLlmFailures: number;
  /** Consecutive tool failures since the last success. */
  consecutiveToolFailures: number;
  /** Current NPC tile (most recent observation). */
  npcTile: { x: number; y: number };
  /** Current player tile (most recent observation). */
  playerTile: { x: number; y: number };
  /** Wall-clock ms (Date.now()) when the player left the NPC's scene, or null when present. */
  playerLeftAt: number | null;
  /** Recent step records, oldest first. The guard inspects the tail. */
  recentSteps: ReActStepRecord[];
  /** Current in-game time as "HH:MM" — used for window_expired. */
  currentGameTime: string;
}

export interface GuardTrigger {
  reason:
    | "repeated_tool_calls"
    | "no_progress"
    | "token_budget_exceeded"
    | "llm_failures"
    | "tool_failures"
    | "player_left"
    | "window_expired";
  detail: string;
}

/**
 * Brake evaluation order. The first trigger encountered in this list wins.
 * Priority rationale (most-urgent first):
 *   1. window_expired — beat is contractually over, no point continuing
 *   2. player_left    — without a player there is no scene to react to
 *   3. token_budget_exceeded — runaway cost, must stop now
 *   4. llm_failures   — the LLM itself is misbehaving, can't recover
 *   5. tool_failures  — tools are breaking, likely a systemic issue
 *   6. repeated_tool_calls — the agent is stuck in a loop
 *   7. no_progress    — the agent isn't making spatial progress
 */
const BRAKE_ORDER: GuardTrigger["reason"][] = [
  "window_expired",
  "player_left",
  "token_budget_exceeded",
  "llm_failures",
  "tool_failures",
  "repeated_tool_calls",
  "no_progress",
];

/**
 * Stateless soft-brake controller. Returns the first triggered brake
 * (by priority) or null if everything is healthy.
 */
export class ReActGuard {
  private readonly config: GuardConfig;

  constructor(config?: Partial<GuardConfig>) {
    this.config = config ? { ...DEFAULT_GUARD_CONFIG, ...config } : DEFAULT_GUARD_CONFIG;
  }

  check(ctx: GuardContext): GuardTrigger | null {
    for (const reason of BRAKE_ORDER) {
      const trigger = this.checkOne(reason, ctx);
      if (trigger !== null) return trigger;
    }
    return null;
  }

  /**
   * No-op. Preserved for API compatibility with the spec — the guard is
   * stateless, so there is nothing to reset. If future versions accumulate
   * cross-call state (e.g., "already triggered for this beat"), they can
   * override this method.
   */
  reset(): void {
    // intentionally empty
  }

  private checkOne(reason: GuardTrigger["reason"], ctx: GuardContext): GuardTrigger | null {
    switch (reason) {
      case "window_expired":
        return this.checkWindowExpired(ctx);
      case "player_left":
        return this.checkPlayerLeft(ctx);
      case "token_budget_exceeded":
        return this.checkTokenBudget(ctx);
      case "llm_failures":
        return this.checkLlmFailures(ctx);
      case "tool_failures":
        return this.checkToolFailures(ctx);
      case "repeated_tool_calls":
        return this.checkRepeatedToolCalls(ctx);
      case "no_progress":
        return this.checkNoProgress(ctx);
      default:
        return null;
    }
  }

  private checkWindowExpired(ctx: GuardContext): GuardTrigger | null {
    const windowEnd = ctx.beat.windowEnd;
    // "HH:MM" string comparison works for same-day windows where
    // windowEnd > triggerTime (validated at beat-creation time).
    if (ctx.currentGameTime > windowEnd) {
      return {
        reason: "window_expired",
        detail: `current time ${ctx.currentGameTime} past window end ${windowEnd}`,
      };
    }
    return null;
  }

  private checkPlayerLeft(ctx: GuardContext): GuardTrigger | null {
    if (ctx.playerLeftAt === null) return null;
    const elapsed = Date.now() - ctx.playerLeftAt;
    if (elapsed > this.config.playerLeftThresholdMs) {
      return {
        reason: "player_left",
        detail: `player left ${elapsed}ms ago (threshold ${this.config.playerLeftThresholdMs}ms)`,
      };
    }
    return null;
  }

  private checkTokenBudget(ctx: GuardContext): GuardTrigger | null {
    if (ctx.accumulatedTokens > this.config.maxTokensPerBeat) {
      return {
        reason: "token_budget_exceeded",
        detail: `${ctx.accumulatedTokens} tokens used (budget ${this.config.maxTokensPerBeat})`,
      };
    }
    return null;
  }

  private checkLlmFailures(ctx: GuardContext): GuardTrigger | null {
    if (ctx.consecutiveLlmFailures >= this.config.maxLlmFailures) {
      return {
        reason: "llm_failures",
        detail: `${ctx.consecutiveLlmFailures} consecutive LLM failures (max ${this.config.maxLlmFailures})`,
      };
    }
    return null;
  }

  private checkToolFailures(ctx: GuardContext): GuardTrigger | null {
    if (ctx.consecutiveToolFailures >= this.config.maxToolFailures) {
      return {
        reason: "tool_failures",
        detail: `${ctx.consecutiveToolFailures} consecutive tool failures (max ${this.config.maxToolFailures})`,
      };
    }
    return null;
  }

  private checkRepeatedToolCalls(ctx: GuardContext): GuardTrigger | null {
    const n = this.config.maxRepeatedToolCalls;
    const steps = ctx.recentSteps;
    if (steps.length < n) return null;

    // Take the last n steps. Each must have BOTH a toolName and argsHash,
    // and all signatures must be identical. If any step is missing the
    // signature, we can't confidently declare a repeat — be lenient.
    const tail = steps.slice(steps.length - n);
    const signatures: string[] = [];
    for (const s of tail) {
      if (s.toolName === undefined || s.argsHash === undefined) return null;
      signatures.push(`${s.toolName}::${s.argsHash}`);
    }
    const first = signatures[0];
    if (first === undefined) return null;
    for (let i = 1; i < signatures.length; i++) {
      const sig = signatures[i];
      if (sig === undefined || sig !== first) return null;
    }
    return {
      reason: "repeated_tool_calls",
      detail: `last ${n} steps all called ${tail[0]?.toolName} with argsHash ${tail[0]?.argsHash}`,
    };
  }

  private checkNoProgress(ctx: GuardContext): GuardTrigger | null {
    const n = this.config.maxNoProgressSteps;
    const steps = ctx.recentSteps;
    if (steps.length < n) return null;

    // The last n steps must all have the SAME npcTileAfter. Steps with
    // missing npcTileAfter break the streak (we can't verify they didn't
    // move), so we return null — be lenient when data is missing.
    const tail = steps.slice(steps.length - n);
    const firstTile = tail[0]?.npcTileAfter;
    if (!firstTile) return null;
    for (let i = 1; i < tail.length; i++) {
      const tile = tail[i]?.npcTileAfter;
      if (!tile) return null;
      if (tile.x !== firstTile.x || tile.y !== firstTile.y) return null;
    }
    return {
      reason: "no_progress",
      detail: `NPC has not moved in ${n} steps (stuck at ${firstTile.x},${firstTile.y})`,
    };
  }
}
