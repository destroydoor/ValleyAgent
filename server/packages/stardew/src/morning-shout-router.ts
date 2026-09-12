import type { RouteShoutMessage, RouteShoutResponse } from "./types";

/**
 * E5-2 喊话歧义兜底路由（TS 侧）。
 *
 * C# 的 4 层确定性路由（名字提及→当前会话→跟随/雇佣者→已醒来+关系最近）全空时，
 * 把候选列表发给本模块：用轻量路由 LLM 选目标（最多 1 次调用），断线/失败走确定性兜底。
 *
 * 性能纪律（AGENTS §2.6）：LLM 只花在歧义上——确定性兜底能解决时根本不调 LLM；
 * 全员未醒/空候选直接沉默（返回 null targetName，不调 LLM）。
 */
export interface ShoutCandidate {
  name: string;
  awake: boolean;
  friendship: number;
  location: string;
}

export interface RouteShoutDecision {
  targetName: string | null;
  reason: string;
}

export type ShoutLlm = (req: RouteShoutMessage) => Promise<RouteShoutDecision>;

/** 确定性兜底：醒着的候选里挑关系最近；无醒着候选 → null。 */
export function deterministicShoutFallback(req: RouteShoutMessage): RouteShoutDecision {
  const awake = req.candidates.filter((c) => c.awake);
  if (awake.length === 0) {
    return { targetName: null, reason: "no_awake_candidate" };
  }
  const best = awake.reduce((a, b) => (b.friendship > a.friendship ? b : a));
  return {
    targetName: best.name,
    reason: `deterministic_closest_friendship(${best.name})`,
  };
}

export class MorningShoutRouter {
  /** 轻量路由 LLM；缺省 → 纯确定性，永不调 LLM。 */
  constructor(private readonly llm?: ShoutLlm) {}

  async routeShout(req: RouteShoutMessage): Promise<RouteShoutResponse> {
    // 空候选 / 全员未醒 → 沉默，不调 LLM
    if (req.candidates.length === 0 || req.candidates.every((c) => !c.awake)) {
      return {
        type: "route_shout_response",
        requestId: req.requestId ?? "",
        targetName: null,
        reason: "no_awake_candidate",
      };
    }

    if (this.llm) {
      try {
        const decision = await this.llm(req);
        if (decision?.targetName) {
          return {
            type: "route_shout_response",
            requestId: req.requestId ?? "",
            targetName: decision.targetName,
            reason: decision.reason ?? "llm_pick",
          };
        }
      } catch (err) {
        // LLM 失败 → 确定性兜底，不吞异常细节但降级选择
      }
    }

    const fb = deterministicShoutFallback(req);
    return {
      type: "route_shout_response",
      requestId: req.requestId ?? "",
      targetName: fb.targetName,
      reason: fb.reason,
    };
  }
}
