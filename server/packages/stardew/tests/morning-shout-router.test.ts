import { test, expect } from "bun:test";
import { MorningShoutRouter, deterministicShoutFallback } from "../src/morning-shout-router";
import type { RouteShoutMessage } from "../src/types";

function makeReq(overrides: Partial<RouteShoutMessage> = {}): RouteShoutMessage {
  return {
    type: "route_shout",
    npcName: "Sebastian",
    playerShout: "有人在家吗？",
    candidates: [
      { name: "Abigail", awake: true, friendship: 1800, location: "Town" },
      { name: "Alex", awake: false, friendship: 400, location: "Beach" },
      { name: "Gus", awake: true, friendship: 250, location: "Saloon" },
    ],
    requestId: "req-1",
    ...overrides,
  };
}

test("deterministic fallback picks awake + closest friendship", () => {
  const result = deterministicShoutFallback(makeReq());
  expect(result.targetName).toBe("Abigail"); // awake(1800) > Gus(250)
  expect(result.reason).toContain("deterministic_closest_friendship");
});

test("deterministic fallback ignores sleeping candidates", () => {
  const result = deterministicShoutFallback(
    makeReq({ candidates: [{ name: "Alex", awake: false, friendship: 9000, location: "Beach" }] }),
  );
  expect(result.targetName).toBeNull();
  expect(result.reason).toBe("no_awake_candidate");
});

test("empty candidates → null (silence)", async () => {
  const router = new MorningShoutRouter();
  const resp = await router.routeShout(makeReq({ candidates: [] }));
  expect(resp.targetName).toBeNull();
  expect(resp.reason).toBe("no_awake_candidate");
  expect(resp.type).toBe("route_shout_response");
});

test("all asleep → null without LLM call", async () => {
  let llmCalls = 0;
  const router = new MorningShoutRouter(async () => {
    llmCalls++;
    return { targetName: "Alex", reason: "llm_pick" };
  });
  const resp = await router.routeShout(
    makeReq({ candidates: [{ name: "Alex", awake: false, friendship: 500, location: "Beach" }] }),
  );
  expect(resp.targetName).toBeNull();
  expect(llmCalls).toBe(0); // 全员未醒 → 沉默，不调 LLM
});

test("LLM path used when available (max 1 call)", async () => {
  let llmCalls = 0;
  const router = new MorningShoutRouter(async (req) => {
    llmCalls++;
    expect(req.npcName).toBe("Sebastian");
    return { targetName: "Gus", reason: "llm_context" };
  });
  const resp = await router.routeShout(makeReq());
  expect(resp.targetName).toBe("Gus");
  expect(resp.reason).toBe("llm_context");
  expect(llmCalls).toBe(1);
});

test("LLM failure → deterministic fallback", async () => {
  const router = new MorningShoutRouter(async () => {
    throw new Error("LLM timeout");
  });
  const resp = await router.routeShout(makeReq());
  expect(resp.targetName).toBe("Abigail"); // 兜底：醒着 + 关系最近
  expect(resp.reason).toContain("deterministic");
});

test("no LLM router → deterministic only", async () => {
  const router = new MorningShoutRouter();
  const resp = await router.routeShout(makeReq());
  expect(resp.targetName).toBe("Abigail");
  expect(resp.requestId).toBe("req-1");
});
