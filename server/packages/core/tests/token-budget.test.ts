import { test, expect } from "bun:test";
import { TokenBudgetManager } from "../src/token-budget";

test("starts with full budget", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  expect(mgr.getRemaining()).toBe(10000);
});

test("consume reduces remaining", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  mgr.consume(3000);
  expect(mgr.getRemaining()).toBe(7000);
});

test("consume returns true when within budget", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  expect(mgr.consume(5000)).toBe(true);
  expect(mgr.consume(5000)).toBe(true);
});

test("consume returns false when exceeding budget", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  mgr.consume(8000);
  expect(mgr.consume(3000)).toBe(false);
  expect(mgr.getRemaining()).toBe(2000);
});

test("budget resets after window", async () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 50 });
  mgr.consume(8000);
  expect(mgr.getRemaining()).toBe(2000);

  await new Promise((r) => setTimeout(r, 60));
  expect(mgr.getRemaining()).toBe(10000);
});

test("budget 0 means unlimited", () => {
  const mgr = new TokenBudgetManager({ budget: 0, windowMs: 60000 });
  expect(mgr.getRemaining()).toBe(Infinity);
  expect(mgr.consume(999999)).toBe(true);
  expect(mgr.getRemaining()).toBe(Infinity);
});

test("reset restores full budget", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  mgr.consume(5000);
  mgr.reset();
  expect(mgr.getRemaining()).toBe(10000);
});

test("getUsed returns consumed amount", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  mgr.consume(3000);
  mgr.consume(2000);
  expect(mgr.getUsed()).toBe(5000);
});
